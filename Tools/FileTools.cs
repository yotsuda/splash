using ModelContextProtocol.Server;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace Ripple.Tools;

/// <summary>
/// File operation tools — compatible with Claude Code's built-in tools.
/// Single-pass streaming for large files, binary detection, shared read access.
/// Encoding detection (BOM + Ude heuristic) and newline preservation ported
/// from PowerShell.MCP so edits round-trip Shift-JIS / CRLF files intact.
/// </summary>
[McpServerToolType]
public class FileTools
{
    private const int BinaryCheckBytes = 8192;
    private const int MaxLineLength = 10000;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".hg", ".svn", "__pycache__",
        "dist", "build", ".next", ".nuxt", "coverage",
        ".tox", ".venv", "venv", ".mypy_cache", ".pytest_cache",
        "target", "bin", "obj",
    };

    [McpServerTool]
    [Description("Read a file with line numbers. Supports offset/limit for large files. Auto-detects encoding (UTF-8/16/32 BOM, Shift-JIS, EUC-JP, GBK, Big5, windows-125x, etc.).")]
    public static async Task<string> ReadFile(
        [Description("Absolute path to the file")] string path,
        [Description("Line number to start from (0-based)")] int offset = 0,
        [Description("Maximum number of lines to read")] int limit = 2000,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0) return $"Error: offset must be >= 0 (got {offset})";
        if (limit < 0) return $"Error: limit must be >= 0 (got {limit})";
        if (!File.Exists(path)) return $"Error: File not found: {path}";
        if (Directory.Exists(path)) return $"Error: Path is a directory: {path}";
        if (IsBinaryFile(path)) return $"Error: Binary file, cannot display: {path}";

        var lines = new List<string>();
        int lineNum = 0, totalLines = 0;
        var encoding = EncodingHelper.DetectEncoding(path);

        using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true,
            new FileStreamOptions { Access = FileAccess.Read, Share = FileShare.ReadWrite });

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            totalLines++;
            if (lineNum >= offset && lines.Count < limit)
            {
                var display = line.Length > MaxLineLength ? line[..MaxLineLength] + "..." : line;
                lines.Add($"{lineNum + 1,4}: {display}");
            }
            lineNum++;
        }

        var output = string.Join('\n', lines);
        if (totalLines > offset + limit)
            output += $"\n\n[Showing lines {offset + 1}-{offset + lines.Count} of {totalLines}]";

        return output;
    }

    [McpServerTool]
    [Description("Write content to a file. Creates the file if it does not exist, overwrites if it does. Creates parent directories as needed. When overwriting, preserves the original file's encoding and newline sequence (CRLF/LF/CR) by default. Specify `encoding` only when converting between encodings (e.g., Shift-JIS → UTF-8).")]
    public static Task<string> WriteFile(
        [Description("Absolute path to the file")] string path,
        [Description("Content to write")] string content,
        [Description("Optional encoding override for conversion. Usually leave unset to auto-preserve. Accepts: utf-8, utf-8-bom, utf-16, utf-16be, utf-32, shift_jis/sjis/cp932, euc-jp, iso-2022-jp, big5, gb2312/gbk/gb18030, euc-kr, windows-125x, iso-8859-x, ascii.")] string? encoding = null,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        Encoding enc;
        string output;
        if (File.Exists(path))
        {
            var meta = FileMetadataHelper.DetectFileMetadata(path);
            enc = string.IsNullOrEmpty(encoding)
                ? meta.Encoding
                : EncodingHelper.GetEncoding(path, encoding);
            // Preserve the file's existing newline sequence on overwrite,
            // even under encoding conversion. Changing both in one step
            // is ambiguous — convert first, adjust line endings after.
            output = NormalizeNewlines(content, meta.NewlineSequence);
        }
        else
        {
            enc = string.IsNullOrEmpty(encoding)
                ? Utf8NoBom
                : EncodingHelper.GetEncoding(path, encoding);
            output = content;
        }

        WithIORetry(() => File.WriteAllText(path, output, enc));
        var lines = content.Count(c => c == '\n') + 1;
        return Task.FromResult($"Written {lines} lines to {path}");
    }

    [McpServerTool]
    [Description("Edit a file by replacing an exact string with a new string. By default old_string must be unique. Use replace_all to replace all occurrences. Preserves the file's original encoding and newline sequence. Response includes ±2 lines of surrounding context for each replacement so the AI can verify the change without re-reading the file.")]
    public static Task<string> EditFile(
        [Description("Absolute path to the file")] string path,
        [Description("Exact string to find and replace")] string old_string,
        [Description("Replacement string")] string new_string,
        [Description("Replace all occurrences (default: false, requires unique match)")] bool replace_all = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return Task.FromResult($"Error: File not found: {path}");

        var meta = FileMetadataHelper.DetectFileMetadata(path);

        // Multi-line old_string still uses the whole-file path: a match that
        // spans lines can't be detected line-by-line, and a character-level
        // sliding window adds significant complexity for a rarer case.
        // Single-line old_string takes the streaming path with a RotateBuffer
        // for ±2 lines of context — that covers the common edit shape (one
        // identifier or one call-site at a time) without ever loading the
        // whole file into memory.
        bool isMultiline = old_string.Contains('\n') || old_string.Contains('\r');
        return Task.FromResult(isMultiline
            ? EditFileMultiline(path, old_string, new_string, replace_all, meta)
            : EditFileSingleLine(path, old_string, new_string, replace_all, meta));
    }

    /// <summary>
    /// Streaming edit for single-line old_string. Reads the file once with a
    /// StreamReader, writes to a same-volume temp file with a StreamWriter,
    /// and atomically renames into place. A 2-slot RotateBuffer holds the
    /// most recent non-match lines so we can emit ±2 lines of context around
    /// each replacement without buffering the whole file.
    ///
    /// On non-replace_all mode, replacements are written optimistically: if a
    /// second match is later discovered, we keep streaming (without applying
    /// further replacements) so we can report the total occurrence count to
    /// the caller, then delete the temp instead of renaming. The same
    /// behavior shape the whole-file version had — error message wording is
    /// unchanged so existing tests still pass.
    /// </summary>
    private static string EditFileSingleLine(string path, string oldText, string newText, bool replaceAll, FileMetadata meta)
    {
        // new_string is allowed to span lines even when old_string doesn't.
        // Normalize any newline flavour the caller sent to the file's own
        // sequence so the post-replace line carries native line endings.
        var normalizedNew = newText.Contains('\n') || newText.Contains('\r')
            ? newText.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", meta.NewlineSequence)
            : newText;

        var preContext = new RotateBuffer<(string line, int num)>(2);
        var contextOut = new StringBuilder();
        int afterCounter = 0;
        int lastEmittedLine = 0;
        int matchCount = 0;
        bool aborted = false;

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        var tempFile = Path.Combine(dir, $".{Path.GetFileName(path)}.ripple-{Guid.NewGuid():N}.tmp");

        try
        {
            using (var reader = new StreamReader(path, meta.Encoding, detectEncodingFromByteOrderMarks: true,
                new FileStreamOptions { Access = FileAccess.Read, Share = FileShare.ReadWrite }))
            using (var writer = new StreamWriter(tempFile, append: false, meta.Encoding, bufferSize: 65536) { NewLine = meta.NewlineSequence })
            {
                int lineNum = 0;
                string? line;
                bool firstWritten = false;

                while ((line = reader.ReadLine()) != null)
                {
                    lineNum++;

                    int occInLine = CountOccurrences(line, oldText);
                    string outLine;

                    if (occInLine > 0 && !aborted)
                    {
                        bool acceptHere;
                        if (replaceAll)
                        {
                            acceptHere = true;
                        }
                        else
                        {
                            // Unique mode: accept iff this is the first match
                            // anywhere AND there's only one occurrence on this
                            // line. Anything else is non-unique → abort.
                            acceptHere = matchCount == 0 && occInLine == 1;
                        }

                        if (acceptHere)
                        {
                            outLine = line.Replace(oldText, normalizedNew);

                            // Emit a blank gap separator if this match is far
                            // enough away from the last emitted line to leave
                            // a gap in the context view.
                            if (lastEmittedLine > 0 && lineNum - 2 > lastEmittedLine + 1)
                                contextOut.AppendLine();

                            // Pre-context: lines from RotateBuffer that haven't
                            // been emitted as post-context for an earlier match.
                            foreach (var (ctxLine, ctxNum) in preContext)
                            {
                                if (ctxNum > lastEmittedLine)
                                {
                                    contextOut.Append($"{ctxNum,4}- ").AppendLine(ctxLine);
                                    lastEmittedLine = ctxNum;
                                }
                            }

                            contextOut.Append($"{lineNum,4}: ").AppendLine(outLine);
                            lastEmittedLine = lineNum;
                            afterCounter = 2;
                            matchCount += occInLine;
                        }
                        else
                        {
                            // Second+ match in non-replace_all mode (or two
                            // occurrences on the first match line). Stop
                            // replacing further but keep streaming + counting
                            // so the error message can report the total.
                            aborted = true;
                            outLine = line;
                            matchCount += occInLine;
                        }
                    }
                    else if (occInLine > 0 && aborted)
                    {
                        outLine = line;
                        matchCount += occInLine;
                    }
                    else
                    {
                        outLine = line;
                        if (afterCounter > 0)
                        {
                            contextOut.Append($"{lineNum,4}- ").AppendLine(line);
                            lastEmittedLine = lineNum;
                            afterCounter--;
                        }
                        preContext.Add((line, lineNum));
                    }

                    // Write to temp. Newline goes BEFORE the next line, not
                    // after this one — that way we only emit a trailing
                    // newline at EOF if the source file had one.
                    if (firstWritten)
                        writer.Write(meta.NewlineSequence);
                    writer.Write(outLine);
                    firstWritten = true;
                }

                if (firstWritten && meta.HasTrailingNewline)
                    writer.Write(meta.NewlineSequence);
            }

            if (matchCount == 0)
            {
                File.Delete(tempFile);
                return "Error: old_string not found in file.";
            }
            if (!replaceAll && matchCount > 1)
            {
                File.Delete(tempFile);
                return $"Error: old_string found {matchCount} times. It must be unique. Add more context or use replace_all.";
            }

            WithIORetry(() => File.Move(tempFile, path, overwrite: true));

            var summary = $"Replaced {matchCount} occurrence{(matchCount != 1 ? "s" : "")} in {path}";
            return contextOut.Length == 0
                ? summary
                : summary + "\n" + contextOut.ToString().TrimEnd('\r', '\n');
        }
        catch
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Whole-file edit for multi-line old_string. Same matching semantics as
    /// before (LF-normalize both sides for matching, write back with the
    /// file's original newline sequence), with the response now including
    /// ±2 lines of context around the first replacement so the AI can
    /// verify the change. Streaming for multi-line spans would need a
    /// character-level sliding window — out of scope here; the common case
    /// (single-line) is the one we optimize.
    /// </summary>
    private static string EditFileMultiline(string path, string oldText, string newText, bool replaceAll, FileMetadata meta)
    {
        var rawContent = File.ReadAllText(path, meta.Encoding);
        var content = ToLf(rawContent, meta.NewlineSequence);
        var oldNorm = ToLf(oldText, meta.NewlineSequence);
        var newNorm = ToLf(newText, meta.NewlineSequence);

        var firstIdx = content.IndexOf(oldNorm, StringComparison.Ordinal);
        if (firstIdx == -1) return "Error: old_string not found in file.";

        string resultLf;
        int replacedCount;
        if (replaceAll)
        {
            var sb = new StringBuilder();
            int lastEnd = 0, idx = firstIdx, count = 0;
            while (idx != -1)
            {
                sb.Append(content, lastEnd, idx - lastEnd);
                sb.Append(newNorm);
                lastEnd = idx + oldNorm.Length;
                count++;
                idx = content.IndexOf(oldNorm, lastEnd, StringComparison.Ordinal);
            }
            sb.Append(content, lastEnd, content.Length - lastEnd);
            resultLf = sb.ToString();
            replacedCount = count;
        }
        else
        {
            var secondIdx = content.IndexOf(oldNorm, firstIdx + 1, StringComparison.Ordinal);
            if (secondIdx != -1)
            {
                int count = 0;
                int idx = -1;
                while ((idx = content.IndexOf(oldNorm, idx + 1, StringComparison.Ordinal)) != -1) count++;
                return $"Error: old_string found {count} times. It must be unique. Add more context or use replace_all.";
            }
            resultLf = string.Concat(content.AsSpan(0, firstIdx), newNorm, content.AsSpan(firstIdx + oldNorm.Length));
            replacedCount = 1;
        }

        var finalContent = FromLf(resultLf, meta.NewlineSequence);
        WithIORetry(() => File.WriteAllText(path, finalContent, meta.Encoding));

        var ctx = BuildMultilineContext(resultLf, firstIdx, newNorm.Length);
        var summary = $"Replaced {replacedCount} occurrence{(replacedCount != 1 ? "s" : "")} in {path}";
        return string.IsNullOrEmpty(ctx) ? summary : summary + "\n" + ctx;
    }

    /// <summary>
    /// Build a ±2 line context view around the multi-line replacement that
    /// landed at <paramref name="matchStartLfIdx"/> in the LF-normalized
    /// post-replace content. Replacement lines are marked with `:`, context
    /// lines with `-`, matching the streaming path's format.
    /// </summary>
    private static string BuildMultilineContext(string lfContent, int matchStartLfIdx, int newLength)
    {
        var lines = lfContent.Split('\n');

        int startLine = 0;
        int charsSeen = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            int lineLen = lines[i].Length + 1; // +1 for the '\n'
            if (charsSeen + lineLen > matchStartLfIdx)
            {
                startLine = i;
                break;
            }
            charsSeen += lineLen;
        }

        int endLine = startLine;
        int matchEnd = matchStartLfIdx + newLength;
        int sweep = charsSeen;
        for (int i = startLine; i < lines.Length; i++)
        {
            int lineLen = lines[i].Length + 1;
            if (sweep >= matchEnd) { endLine = i - 1; break; }
            sweep += lineLen;
            endLine = i;
        }

        int ctxStart = Math.Max(0, startLine - 2);
        int ctxEnd = Math.Min(lines.Length - 1, endLine + 2);

        // The Split('\n') above produces a trailing empty entry when the file
        // ends with '\n'; drop that from the visible context tail.
        if (ctxEnd == lines.Length - 1 && lines[ctxEnd].Length == 0 && ctxEnd > 0)
            ctxEnd--;

        var sb = new StringBuilder();
        for (int i = ctxStart; i <= ctxEnd; i++)
        {
            bool isMatch = i >= startLine && i <= endLine;
            sb.Append($"{i + 1,4}{(isMatch ? ':' : '-')} ").AppendLine(lines[i]);
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Count non-overlapping occurrences of <paramref name="needle"/> in
    /// <paramref name="haystack"/>. Equivalent to
    /// (haystack.Length - haystack.Replace(needle, "").Length) / needle.Length
    /// but without the intermediate string allocation, which matters when the
    /// streaming path scans every line.
    /// </summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    [McpServerTool]
    [Description("Search file contents using a regular expression. Returns matching lines with file paths and line numbers.")]
    public static async Task<string> SearchFiles(
        [Description("Regular expression pattern to search for")] string pattern,
        [Description("Directory or file to search in (default: current directory)")] string? path = null,
        [Description("Glob pattern to filter files (e.g., \"*.js\", \"*.ts\")")] string? glob = null,
        [Description("Maximum number of matching lines to return")] int max_results = 50,
        CancellationToken cancellationToken = default)
    {
        var regex = GetSearchRegex(pattern);
        var basePath = path ?? Directory.GetCurrentDirectory();
        var results = new List<string>();

        if (File.Exists(basePath))
        {
            await SearchInFileAsync(basePath, regex, results, max_results, cancellationToken);
        }
        else if (Directory.Exists(basePath))
        {
            await WalkAndSearchAsync(basePath, regex, results, max_results, glob, NewVisitedSet(), cancellationToken);
        }
        else
        {
            return $"Error: Path not found: {basePath}";
        }

        if (results.Count == 0) return "No matches found.";
        var output = string.Join('\n', results);
        if (results.Count >= max_results) output += $"\n\n[Results limited to {max_results}]";
        return output;
    }

    [McpServerTool]
    [Description("Find files by glob pattern. Returns matching file paths.")]
    public static Task<string> FindFiles(
        [Description("Glob pattern (e.g., \"*.js\", \"src/**/*.ts\")")] string pattern,
        [Description("Base directory to search in (default: current directory)")] string? path = null,
        [Description("Maximum number of files to return")] int max_results = 200,
        CancellationToken cancellationToken = default)
    {
        var dir = path ?? Directory.GetCurrentDirectory();
        var results = new List<string>();
        FindFilesRecursive(dir, pattern, results, max_results, NewVisitedSet());

        if (results.Count == 0) return Task.FromResult("No files found.");
        return Task.FromResult(string.Join('\n', results));
    }

    // --- Helpers ---

    // Filesystem walk cycle detection. Symlinks / Windows junctions can
    // form loops (`ln -s . self`, junction pointing at an ancestor); a
    // naive recursive walk would spin until max_results or stack overflow.
    // We track each visited directory by its resolved real path and bail
    // before entering one we've already seen this walk.
    private static HashSet<string> NewVisitedSet() =>
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static string ResolveRealPath(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return Path.GetFullPath(target?.FullName ?? info.FullName);
        }
        catch
        {
            // Inaccessible / malformed entry — fall back to the literal
            // path so the visited-set still de-dupes by string identity.
            return Path.GetFullPath(dir);
        }
    }

    private static async Task SearchInFileAsync(string filePath, Regex regex, List<string> results, int maxResults, CancellationToken ct)
    {
        if (IsBinaryFile(filePath)) return;

        var encoding = EncodingHelper.DetectEncoding(filePath);
        using var reader = new StreamReader(filePath, encoding, true,
            new FileStreamOptions { Access = FileAccess.Read, Share = FileShare.ReadWrite });

        int lineNum = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            lineNum++;
            if (results.Count >= maxResults) return;
            if (regex.IsMatch(line))
            {
                var display = line.Length > MaxLineLength ? line[..MaxLineLength] + "..." : line;
                results.Add($"{filePath}:{lineNum}: {display}");
            }
        }
    }

    private static async Task WalkAndSearchAsync(string dir, Regex regex, List<string> results, int maxResults, string? globPattern, HashSet<string> visited, CancellationToken ct)
    {
        if (!visited.Add(ResolveRealPath(dir))) return;

        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            if (results.Count >= maxResults) return;
            ct.ThrowIfCancellationRequested();

            if (Directory.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (SkipDirs.Contains(name)) continue;
                await WalkAndSearchAsync(entry, regex, results, maxResults, globPattern, visited, ct);
            }
            else if (File.Exists(entry))
            {
                if (globPattern != null && !MatchGlob(Path.GetFileName(entry), globPattern)) continue;
                await SearchInFileAsync(entry, regex, results, maxResults, ct);
            }
        }
    }

    private static void FindFilesRecursive(string dir, string pattern, List<string> results, int maxResults, HashSet<string> visited)
    {
        if (!visited.Add(ResolveRealPath(dir))) return;

        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            if (results.Count >= maxResults) return;

            if (Directory.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (SkipDirs.Contains(name)) continue;
                FindFilesRecursive(entry, pattern, results, maxResults, visited);
            }
            else if (File.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (MatchGlob(name, pattern) || MatchGlob(entry, pattern))
                    results.Add(entry);
            }
        }
    }

    // Normalize any incoming newlines (\r\n, \r, \n) to the target sequence.
    // Used by WriteFile on overwrite so the file keeps its original line endings.
    private static string NormalizeNewlines(string content, string target)
    {
        var lf = content.Replace("\r\n", "\n").Replace("\r", "\n");
        return target == "\n" ? lf : lf.Replace("\n", target);
    }

    // ToLf/FromLf: round-trip through \n for matching in EditFile,
    // then re-emit with the file's original newline sequence on write.
    //
    // ToLf always normalises every newline flavour (\r\n + orphan \r) to
    // \n regardless of the file's own line ending — the AI-supplied
    // old_string / new_string can arrive with whatever newlines the
    // client happened to send (Windows clipboard CRLF, VS Code LF, etc.),
    // and pre-2026-04-18 this branched on the file's newline so an AI
    // editing a LF file with CRLF in old_string silently missed. Normal
    // files (pure LF / pure CRLF / pure CR) are unchanged by the double-
    // replace; only mixed-newline files see any difference, and for those
    // "match in LF-space" is still the correct behaviour.
    private static string ToLf(string s, string originalNewline)
        => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string FromLf(string s, string originalNewline) => originalNewline switch
    {
        "\r\n" => s.Replace("\n", "\r\n"),
        "\r" => s.Replace("\n", "\r"),
        _ => s,
    };

    private static bool IsBinaryFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[BinaryCheckBytes];
            int read = fs.Read(buf, 0, buf.Length);
            // UTF-16/32 text legitimately contains 0x00 bytes for ASCII chars,
            // so a BOM presence takes precedence over the null-byte heuristic.
            if (read >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF)))
                return false;
            if (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF)
                return false;
            for (int i = 0; i < read; i++)
                if (buf[i] == 0) return true;
            return false;
        }
        catch { return false; }
    }

    private static bool MatchGlob(string str, string pattern)
    {
        return GetGlobRegex(pattern).IsMatch(str);
    }

    // Compile-once-reuse caches for the two regex hot paths in this file.
    // - `MatchGlob` runs per file entry inside the recursive walk; with N
    //   files in a tree, an AI doing a repo-wide find_files / search_files
    //   round-trip would otherwise call `Regex.Escape` + chained Replace +
    //   `Regex.IsMatch` (whose static cache caps at 15 patterns) once per
    //   entry. Caching here turns N compilations into 1 per glob.
    // - `SearchFiles`'s `pattern` parameter is constant for the duration of
    //   one tool call but is often reused across calls in the same session
    //   (an AI that greps for the same symbol while iterating on an edit).
    // The caches are unbounded by design: in practice the cardinality of
    // distinct patterns in one MCP session is small (tens at most), and
    // each compiled Regex is on the order of KB. If memory ever becomes a
    // concern, swap for an LRU bound.
    private static readonly ConcurrentDictionary<string, Regex> _globRegexCache = new();
    private static readonly ConcurrentDictionary<string, Regex> _searchRegexCache = new();

    // Retry shim for the moments right after we close our own handle
    // on a file: on Windows, antivirus / Defender / indexing services
    // routinely open the file for an on-access scan in that window, and
    // any concurrent File.Move / File.WriteAllText against the same path
    // can land while the scanner still holds the handle and surface as
    // either UnauthorizedAccessException ("Access to the path is denied")
    // or IOException with ERROR_SHARING_VIOLATION (HRESULT 0x80070020).
    // Real permission errors raise the same exception types, but they
    // don't go away on retry — the bounded backoff burns at most ~750ms
    // on a genuinely-locked file before re-throwing.
    private static void WithIORetry(Action action)
    {
        const int maxAttempts = 5;
        int delayMs = 50;
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (IOException) when (attempt < maxAttempts) { }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts) { }
            Thread.Sleep(delayMs);
            delayMs *= 2;
        }
    }

    // Per-match runtime cap for both caches. SearchFiles' `pattern` is
    // user-supplied and could be pathological ((a+)+b -shape on a long
    // line); a timeout converts that into a clean RegexMatchTimeoutException
    // surfaced as an MCP error rather than a hung tool call. Glob patterns
    // are structurally unable to trigger catastrophic backtracking after
    // the Escape + canned-replacement transform, but applying the same cap
    // there keeps every cached regex in this file on a single policy.
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(1);

    private static Regex GetGlobRegex(string globPattern) =>
        _globRegexCache.GetOrAdd(globPattern, p => new Regex(
            "^" + Regex.Escape(p)
                // `/**/` collapses zero or more path segments — `src/**/test.js`
                // must match `src/test.js` AND `src/a/test.js` AND
                // `src/a/b/test.js`, matching Bash globstar / npm minimatch
                // semantics. Without this special case the post-Escape
                // pattern `src/.*/test\.js` requires at least the slashes
                // around `.*` and rejects the top-level `src/test.js`.
                // NOTE: MatchGlob is wired up by FindFiles to match either
                // the basename or the full absolute path of each entry,
                // and uses literal `/` as the separator. That means glob
                // patterns containing path segments (e.g. `src/**/*.cs`)
                // don't engage usefully on Windows full paths (which use
                // `\\`) and never engage on the basename. Fixing those
                // requires matching against root-relative paths with a
                // platform-aware separator class, which is outside the
                // scope of this regex-semantics fix.
                .Replace(@"/\*\*/", "/(?:.*/)?")
                .Replace(@"\*\*", ".*")
                .Replace(@"\*", @"[^/\\]*")
                .Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            RegexMatchTimeout));

    private static Regex GetSearchRegex(string pattern) =>
        _searchRegexCache.GetOrAdd(pattern, p => new Regex(
            p, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexMatchTimeout));
}
