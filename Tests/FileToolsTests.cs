using System.Text;
using Ripple.Tools;

namespace Ripple.Tests;

/// <summary>
/// Tests for FileTools encoding detection + newline preservation. Covers the
/// PowerShell.MCP port (EncodingHelper + FileMetadataHelper) and the Shift-JIS /
/// UTF-16 / CRLF round-trip scenarios that used to silently corrupt edits.
/// </summary>
public static class FileToolsTests
{
    public static void Run()
    {
        Console.WriteLine("=== FileTools Tests ===");
        var pass = 0;
        var fail = 0;
        void Assert(bool condition, string name)
        {
            if (condition) { pass++; Console.WriteLine($"  PASS: {name}"); }
            else { fail++; Console.Error.WriteLine($"  FAIL: {name}"); }
        }

        // Ensure the legacy codepages provider is live — normal MCP startup
        // registers it in Main, but --test mode reaches us before that path
        // on some arg orderings. Registering twice is a no-op.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var tmpRoot = Path.Combine(Path.GetTempPath(), $"ripple-filetools-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpRoot);

        try
        {
            // --- Encoding detection ---

            {
                // Pure ASCII would be detected as ASCII (codepage 20127) by Ude;
                // include non-ASCII so detection resolves to UTF-8 without BOM.
                var p = Path.Combine(tmpRoot, "utf8-plain.txt");
                File.WriteAllBytes(p, Encoding.UTF8.GetBytes("hello — τέλος — 終わり\n"));
                var enc = EncodingHelper.DetectEncoding(p);
                Assert(enc is UTF8Encoding u && u.GetPreamble().Length == 0,
                    "detect UTF-8 (no BOM) on non-ASCII content");
            }

            {
                var p = Path.Combine(tmpRoot, "utf8-bom.txt");
                var enc = new UTF8Encoding(true);
                File.WriteAllBytes(p, enc.GetPreamble().Concat(enc.GetBytes("hello")).ToArray());
                var detected = EncodingHelper.DetectEncoding(p);
                Assert(detected is UTF8Encoding u && u.GetPreamble().Length == 3,
                    "detect UTF-8 BOM");
            }

            {
                var p = Path.Combine(tmpRoot, "utf16-le.txt");
                var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                File.WriteAllBytes(p, enc.GetPreamble().Concat(enc.GetBytes("hello")).ToArray());
                var detected = EncodingHelper.DetectEncoding(p);
                Assert(detected.CodePage == Encoding.Unicode.CodePage, "detect UTF-16 LE BOM");
            }

            {
                var p = Path.Combine(tmpRoot, "utf16-be.txt");
                var enc = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
                File.WriteAllBytes(p, enc.GetPreamble().Concat(enc.GetBytes("hello")).ToArray());
                var detected = EncodingHelper.DetectEncoding(p);
                Assert(detected.CodePage == Encoding.BigEndianUnicode.CodePage, "detect UTF-16 BE BOM");
            }

            {
                var p = Path.Combine(tmpRoot, "sjis.txt");
                var sjis = Encoding.GetEncoding("shift_jis");
                File.WriteAllBytes(p, sjis.GetBytes("先頭行\r\n日本語のテスト\r\n末尾行\r\n"));
                var detected = EncodingHelper.DetectEncoding(p);
                Assert(detected.CodePage == sjis.CodePage, "detect Shift-JIS via Ude heuristic");
            }

            {
                var p = Path.Combine(tmpRoot, "eucjp.txt");
                var euc = Encoding.GetEncoding("euc-jp");
                File.WriteAllBytes(p, euc.GetBytes("日本語のサンプルをもう少し長めに書いてみる\n"));
                var detected = EncodingHelper.DetectEncoding(p);
                Assert(detected.CodePage == euc.CodePage, "detect EUC-JP via Ude heuristic");
            }

            // --- Newline + trailing newline detection ---

            {
                var p = Path.Combine(tmpRoot, "crlf.txt");
                File.WriteAllText(p, "a\r\nb\r\nc\r\n", new UTF8Encoding(false));
                var m = FileMetadataHelper.DetectFileMetadata(p);
                Assert(m.NewlineSequence == "\r\n" && m.HasTrailingNewline, "detect CRLF + trailing newline");
            }

            {
                var p = Path.Combine(tmpRoot, "lf-no-trailing.txt");
                File.WriteAllText(p, "a\nb\nc", new UTF8Encoding(false));
                var m = FileMetadataHelper.DetectFileMetadata(p);
                Assert(m.NewlineSequence == "\n" && !m.HasTrailingNewline, "detect LF with no trailing newline");
            }

            {
                var p = Path.Combine(tmpRoot, "cr-only.txt");
                File.WriteAllText(p, "a\rb\rc\r", new UTF8Encoding(false));
                var m = FileMetadataHelper.DetectFileMetadata(p);
                Assert(m.NewlineSequence == "\r" && m.HasTrailingNewline, "detect bare CR newline");
            }

            // --- EditFile round-trip: Shift-JIS CRLF with LF old_string ---

            {
                var p = Path.Combine(tmpRoot, "sjis-crlf-edit.txt");
                var sjis = Encoding.GetEncoding("shift_jis");
                File.WriteAllBytes(p, sjis.GetBytes("先頭行\r\n日本語のテスト\r\n末尾行\r\n"));

                // old_string uses \n even though file is CRLF
                var result = FileTools.EditFile(p, "日本語のテスト", "NIHONGO").GetAwaiter().GetResult();
                Assert(result.StartsWith("Replaced 1"), $"EditFile on SJIS CRLF succeeds ({result})");
                // Streaming path emits ±2 lines of context; on this 3-line
                // file that's the full content, with the matched line marked
                // by `:` and the surrounding lines by `-`. Verifies the
                // RotateBuffer context emission survives Shift-JIS decoding.
                Assert(result.Contains("   2: NIHONGO"),
                    $"SJIS CRLF context: replaced line shown with ':' marker ({result})");
                Assert(result.Contains("   1- 先頭行") && result.Contains("   3- 末尾行"),
                    $"SJIS CRLF context: surrounding lines shown with '-' marker ({result})");

                var after = File.ReadAllBytes(p);
                var decoded = sjis.GetString(after);
                Assert(decoded == "先頭行\r\nNIHONGO\r\n末尾行\r\n",
                    "SJIS CRLF round-trip: encoding + CRLF preserved, content replaced");
                Assert(!(after.Length >= 3 && after[0] == 0xEF && after[1] == 0xBB && after[2] == 0xBF),
                    "SJIS CRLF round-trip: no stray UTF-8 BOM introduced");
                int crlfCount = 0;
                for (int i = 0; i < after.Length - 1; i++)
                    if (after[i] == 0x0D && after[i + 1] == 0x0A) crlfCount++;
                Assert(crlfCount == 3, $"SJIS CRLF round-trip: 3 CRLF kept (got {crlfCount})");
            }

            // --- EditFile with old_string spanning a newline ---

            {
                var p = Path.Combine(tmpRoot, "multiline-edit.txt");
                File.WriteAllText(p, "line1\r\nline2\r\nline3\r\n", new UTF8Encoding(false));
                // Pass old_string with \n; file has \r\n. Must still match.
                var result = FileTools.EditFile(p, "line1\nline2", "LINE1\nLINE2").GetAwaiter().GetResult();
                Assert(result.StartsWith("Replaced 1"), $"multi-line edit with LF old_string ({result})");
                var after = File.ReadAllText(p, new UTF8Encoding(false));
                Assert(after == "LINE1\r\nLINE2\r\nline3\r\n", $"multi-line edit preserves CRLF (got [{after.Replace("\r", "\\r").Replace("\n", "\\n")}])");
            }

            // --- EditFile: LF file + CRLF old_string (the opposite direction).
            // Pre-2026-04-18 this silently missed — ToLf only normalised when
            // the file's own newline was the one being replaced, so a LF file's
            // content stayed LF while a CRLF-carrying old_string kept its \r\n
            // and IndexOf failed. AI clients pasting Windows-clipboard snippets
            // into an LF-native file (the common case for any repo with a
            // mixed team) hit this silently. Regression test.

            {
                var p = Path.Combine(tmpRoot, "lf-file-crlf-oldstring.txt");
                File.WriteAllText(p, "alpha\nbeta\ngamma\n", new UTF8Encoding(false));
                var result = FileTools.EditFile(p, "alpha\r\nbeta", "A\r\nB").GetAwaiter().GetResult();
                Assert(result.StartsWith("Replaced 1"), $"LF file + CRLF old_string matches ({result})");
                var after = File.ReadAllText(p, new UTF8Encoding(false));
                Assert(after == "A\nB\ngamma\n", $"LF file preserves LF on write (got [{after.Replace("\r", "\\r").Replace("\n", "\\n")}])");
            }

            // --- EditFile: mixed-newline old_string (\n and \r\n together)
            // against a LF file. Normalisation has to flatten every flavour,
            // not just the file's own newline.

            {
                var p = Path.Combine(tmpRoot, "mixed-newline-oldstring.txt");
                File.WriteAllText(p, "x\ny\nz\n", new UTF8Encoding(false));
                var result = FileTools.EditFile(p, "x\r\ny\nz", "P\nQ\nR").GetAwaiter().GetResult();
                Assert(result.StartsWith("Replaced 1"), $"LF file + mixed-newline old_string ({result})");
                var after = File.ReadAllText(p, new UTF8Encoding(false));
                Assert(after == "P\nQ\nR\n", $"mixed-newline old_string resolves on LF file (got [{after.Replace("\r", "\\r").Replace("\n", "\\n")}])");
            }

            // --- WriteFile overwrite preserves encoding + newline ---

            {
                var p = Path.Combine(tmpRoot, "overwrite.txt");
                var sjis = Encoding.GetEncoding("shift_jis");
                // Ude needs enough content to reliably detect SJIS; use a longer sample.
                File.WriteAllBytes(p, sjis.GetBytes("初期のサンプル内容をある程度長く書く\r\nもう一行追加しておく\r\n"));

                // Claude passes LF content; WriteFile must re-emit as SJIS + CRLF
                FileTools.WriteFile(p, "新しい内容\n2行目\n").GetAwaiter().GetResult();

                var after = File.ReadAllBytes(p);
                var decoded = sjis.GetString(after);
                Assert(decoded == "新しい内容\r\n2行目\r\n",
                    $"WriteFile overwrite: SJIS + CRLF preserved from existing file (got [{decoded.Replace("\r", "\\r").Replace("\n", "\\n")}])");
            }

            // --- WriteFile on new file uses UTF-8 no BOM ---

            {
                var p = Path.Combine(tmpRoot, "new-file.txt");
                FileTools.WriteFile(p, "hello\nworld\n").GetAwaiter().GetResult();
                var bytes = File.ReadAllBytes(p);
                Assert(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF),
                    "WriteFile on new file has no UTF-8 BOM");
                Assert(Encoding.UTF8.GetString(bytes) == "hello\nworld\n",
                    "WriteFile on new file keeps Claude's newline choice (LF)");
            }

            // --- WriteFile with explicit encoding: convert existing SJIS → UTF-8 ---

            {
                var p = Path.Combine(tmpRoot, "convert-to-utf8.txt");
                var sjis = Encoding.GetEncoding("shift_jis");
                File.WriteAllBytes(p, sjis.GetBytes("変換元のサンプル内容を十分な長さで書いておく\r\n二行目も書いておく\r\n"));

                FileTools.WriteFile(p, "変換後\n", encoding: "utf-8").GetAwaiter().GetResult();

                var after = File.ReadAllBytes(p);
                Assert(!(after.Length >= 3 && after[0] == 0xEF && after[1] == 0xBB && after[2] == 0xBF),
                    "SJIS → UTF-8 conversion: no BOM");
                Assert(Encoding.UTF8.GetString(after) == "変換後\r\n",
                    $"SJIS → UTF-8 conversion: content correct, CRLF preserved from original");
            }

            // --- WriteFile with explicit encoding: new file as UTF-16 LE BOM ---

            {
                var p = Path.Combine(tmpRoot, "new-utf16.txt");
                FileTools.WriteFile(p, "hello\nworld\n", encoding: "utf-16").GetAwaiter().GetResult();
                var after = File.ReadAllBytes(p);
                Assert(after.Length >= 2 && after[0] == 0xFF && after[1] == 0xFE,
                    "new file with encoding=utf-16: LE BOM present");
                var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                Assert(enc.GetString(after, 2, after.Length - 2) == "hello\nworld\n",
                    "new file with encoding=utf-16: content correct");
            }

            // --- WriteFile with explicit encoding: convert UTF-8 → SJIS ---

            {
                var p = Path.Combine(tmpRoot, "convert-to-sjis.txt");
                File.WriteAllText(p, "元データ\n", new UTF8Encoding(false));

                FileTools.WriteFile(p, "SJISに変換\n", encoding: "sjis").GetAwaiter().GetResult();

                var after = File.ReadAllBytes(p);
                var sjis = Encoding.GetEncoding("shift_jis");
                Assert(sjis.GetString(after) == "SJISに変換\n",
                    $"UTF-8 → SJIS conversion: content correct, LF preserved");
            }

            // --- ReadFile on UTF-16 file (regression: was flagged as binary) ---

            {
                var p = Path.Combine(tmpRoot, "utf16-read.txt");
                var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                File.WriteAllBytes(p, enc.GetPreamble().Concat(enc.GetBytes("alpha\nbeta\ngamma\n")).ToArray());
                var output = FileTools.ReadFile(p).GetAwaiter().GetResult();
                Assert(!output.StartsWith("Error:"), $"UTF-16 BOM file not flagged as binary ({output.Split('\n')[0]})");
                Assert(output.Contains("alpha") && output.Contains("beta") && output.Contains("gamma"),
                    "UTF-16 ReadFile decodes content");
            }

            // --- EditFile on UTF-16 preserves BOM ---

            {
                var p = Path.Combine(tmpRoot, "utf16-edit.txt");
                var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                File.WriteAllBytes(p, enc.GetPreamble().Concat(enc.GetBytes("alpha\nbeta\ngamma\n")).ToArray());
                FileTools.EditFile(p, "beta", "BETA").GetAwaiter().GetResult();
                var after = File.ReadAllBytes(p);
                Assert(after.Length >= 2 && after[0] == 0xFF && after[1] == 0xFE, "UTF-16 LE BOM preserved after edit");
                Assert(enc.GetString(after, 2, after.Length - 2) == "alpha\nBETA\ngamma\n",
                    "UTF-16 edit content correct");
            }

            // --- EditFile: non-existent old_string returns error ---

            {
                var p = Path.Combine(tmpRoot, "notfound.txt");
                File.WriteAllText(p, "some content", new UTF8Encoding(false));
                var result = FileTools.EditFile(p, "missing", "X").GetAwaiter().GetResult();
                Assert(result.StartsWith("Error: old_string not found"), $"missing old_string → error ({result})");
            }

            // --- EditFile: non-unique old_string without replace_all returns error ---

            {
                var p = Path.Combine(tmpRoot, "duplicate.txt");
                File.WriteAllText(p, "a\na\na\n", new UTF8Encoding(false));
                var result = FileTools.EditFile(p, "a", "b").GetAwaiter().GetResult();
                Assert(result.Contains("found 3 times"), $"duplicate old_string → count error ({result})");
            }

            // --- EditFile: replace_all replaces every occurrence ---

            {
                var p = Path.Combine(tmpRoot, "replace-all.txt");
                File.WriteAllText(p, "a\na\na\n", new UTF8Encoding(false));
                var result = FileTools.EditFile(p, "a", "b", replace_all: true).GetAwaiter().GetResult();
                Assert(result.StartsWith("Replaced 3"), $"replace_all count ({result})");
                var after = File.ReadAllText(p);
                Assert(after == "b\nb\nb\n", $"replace_all content ({after.Replace("\n", "\\n")})");
                // Each replaced line should appear with the `:` marker. The
                // file is 3 lines long so all are matches; ±2 context just
                // means each match gets shown once on its own row.
                Assert(result.Contains("   1: b") && result.Contains("   2: b") && result.Contains("   3: b"),
                    $"replace_all context: every replaced line shown ({result})");
            }

            // --- EditFile streaming context: ±2 surrounding lines ---
            // Larger file so the context window is non-trivial: 7 lines, the
            // match on line 4. Pre-context buffer should yield lines 2-3,
            // afterCounter should yield lines 5-6. Lines 1 and 7 must NOT
            // appear because they fall outside the ±2 window.

            {
                var p = Path.Combine(tmpRoot, "context-window.txt");
                File.WriteAllText(p,
                    "L1\nL2\nL3\nMATCH\nL5\nL6\nL7\n",
                    new UTF8Encoding(false));
                var result = FileTools.EditFile(p, "MATCH", "REPLACED").GetAwaiter().GetResult();
                Assert(result.StartsWith("Replaced 1"), $"streaming match found ({result})");
                Assert(result.Contains("   4: REPLACED"),
                    $"streaming context: replaced line shown ({result})");
                Assert(result.Contains("   2- L2") && result.Contains("   3- L3"),
                    $"streaming context: 2 lines before shown ({result})");
                Assert(result.Contains("   5- L5") && result.Contains("   6- L6"),
                    $"streaming context: 2 lines after shown ({result})");
                Assert(!result.Contains("L1") && !result.Contains("L7"),
                    $"streaming context: lines outside ±2 window suppressed ({result})");
            }

            // --- EditFile streaming gap separator between distant matches ---
            // Two matches with > 4 lines between them: each gets its own
            // ±2 window and a blank line separates the windows so the AI
            // can tell adjacent matches apart from one merged span.

            {
                var p = Path.Combine(tmpRoot, "gap-window.txt");
                File.WriteAllText(p,
                    "MATCH\nA\nB\nC\nD\nE\nF\nMATCH\n",
                    new UTF8Encoding(false));
                var result = FileTools.EditFile(p, "MATCH", "X", replace_all: true).GetAwaiter().GetResult();
                Assert(result.StartsWith("Replaced 2"), $"streaming replace_all count ({result})");
                Assert(result.Contains("   1: X") && result.Contains("   8: X"),
                    $"streaming gap: both replaced lines shown ({result})");
                // The two windows are line 1 + lines 2-3 (after) for match
                // #1, and lines 6-7 (before) + line 8 for match #2. The
                // middle lines (4-5: MID4, MID5) fall outside both ±2
                // windows and must NOT appear. Use unique tokens so the
                // assertion can't be fooled by an incidental letter in
                // the path / prefix message.
                File.WriteAllText(p,
                    "MATCH\nA\nB\nMID4\nMID5\nE\nF\nMATCH\n",
                    new UTF8Encoding(false));
                var result2 = FileTools.EditFile(p, "MATCH", "X", replace_all: true).GetAwaiter().GetResult();
                Assert(result2.StartsWith("Replaced 2"), $"streaming gap (with unique tokens) count ({result2})");
                Assert(!result2.Contains("MID4") && !result2.Contains("MID5"),
                    $"streaming gap: middle lines (outside both ±2 windows) suppressed ({result2})");
            }

            // --- ReadFile parameter validation ---
            {
                var p = Path.Combine(tmpRoot, "read-validation.txt");
                File.WriteAllText(p, "line1\nline2\nline3\n", new UTF8Encoding(false));

                var negOffset = FileTools.ReadFile(p, offset: -1).GetAwaiter().GetResult();
                Assert(negOffset.StartsWith("Error: offset"),
                    $"ReadFile rejects negative offset ({negOffset})");

                var negLimit = FileTools.ReadFile(p, limit: -5).GetAwaiter().GetResult();
                Assert(negLimit.StartsWith("Error: limit"),
                    $"ReadFile rejects negative limit ({negLimit})");

                // Sanity: zero limit is legal (read nothing) and shouldn't error.
                var zeroLimit = FileTools.ReadFile(p, limit: 0).GetAwaiter().GetResult();
                Assert(!zeroLimit.StartsWith("Error:"),
                    $"ReadFile accepts limit=0 ({zeroLimit})");
            }

        }
        finally
        {
            try { Directory.Delete(tmpRoot, recursive: true); } catch { }
        }

        Console.WriteLine($"  Total: {pass} passed, {fail} failed");
        if (fail > 0) Environment.Exit(1);
    }
}
