using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Ripple.Services;

namespace Ripple.Tools;

[McpServerToolType]
public class ShellTools
{
    // SGR escape sequence: ESC [ {params} m. Used for color / bold /
    // reverse video, etc. Stripping this — via the opt-in strip_ansi
    // parameter on execute_command / wait_for_completion — trades
    // visual fidelity for token savings when an AI caller has no use
    // for color and just wants the text content. Deliberately NARROW:
    // other CSI sequences (cursor movement, line erase) should already
    // have been consumed by the renderer grid upstream; anything that
    // survives in command output is the SGR the finalizer attached as
    // cell prefixes. Compile-once + RegexOptions.Compiled keeps the
    // matcher fast on repeated calls; the pattern has no alternation
    // or backrefs so AOT is happy without [GeneratedRegex] + partial.
    private static readonly Regex s_sgrPattern = new(
        @"\x1b\[[0-9;]*m",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string StripAnsi(string s) =>
        string.IsNullOrEmpty(s) ? s : s_sgrPattern.Replace(s, "");

    /// <summary>
    /// Strip well-known fixed startup banners from a peek snapshot. Today
    /// the only one is PowerShell's "screen reader detected → PSReadLine
    /// disabled" warning, which appears at the very top of every pwsh
    /// console launched under non-interactive ConPTY (= every ripple-spawned
    /// pwsh) and persists in the rolling window for a long time because
    /// nothing scrolls it out. The AI seeing this on every peek_console
    /// call is pure noise — same text every time, no actionable signal —
    /// so we drop it before building the response. Anything we can't
    /// confidently match stays put.
    /// </summary>
    private static string FilterStartupBanners(string snapshot)
    {
        if (string.IsNullOrEmpty(snapshot)) return snapshot;
        if (!snapshot.Contains("Warning: PowerShell detected")) return snapshot;

        var lines = snapshot.Split('\n');
        var keep = new List<string>(lines.Length);
        int dropping = 0; // 0 = not in banner; >0 = nth line being dropped

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimEnd('\r');

            if (dropping == 0)
            {
                if (trimmed.StartsWith("Warning: PowerShell detected"))
                {
                    dropping = 1;
                    continue;
                }
                keep.Add(line);
            }
            else
            {
                // Inside the banner; this line is its continuation. End on
                // the closing marker, a blank terminator, or a paranoid cap
                // to avoid eating the entire snapshot if something unusual
                // happened to the buffer.
                bool isClosingMarker = trimmed.Contains("Import-Module PSReadLine'.");
                bool isBlank = trimmed.Length == 0;
                if (isClosingMarker || isBlank || dropping > 5)
                {
                    dropping = 0;
                    // If we ended on the closing marker and the next line
                    // is blank, swallow that blank too — the banner visually
                    // includes the trailing gap, and leaving it produces
                    // an awkward leading newline in the trimmed output.
                    if (isClosingMarker && i + 1 < lines.Length
                        && lines[i + 1].TrimEnd('\r').Length == 0)
                    {
                        i++;
                    }
                    continue;
                }
                dropping++;
            }
        }

        return string.Join('\n', keep);
    }

    // Render the structured error-message list as a trailing section
    // in the tool response. Leading "\n\n" so it detaches from the main
    // output body; empty string when there's nothing to render so
    // callers can append it unconditionally. Numbered when there's more
    // than one message, bare when there's exactly one — the count badge
    // already appears in the status line ("Errors: N") so duplicating
    // it here is noise on the single-error path.
    //
    // When the integration script truncated some error records (because
    // its per-command cap was hit), the section header shows
    // `(N of total)` where N is what we got and total is N + dropped,
    // and a trailing line restates how many newer records were dropped.
    // The marker is rendered OUTSIDE the numbered entry list so it
    // can't be misread as error #N+1 — that was a design smell the
    // initial OSC 633;R-only encoding had.
    //
    // This is complementary to the SGR-coloured inline error text in
    // the main output: the inline path preserves the visible-console
    // fidelity the user sees, and this section gives the AI a clean
    // parseable list (especially valuable when strip_ansi=true removed
    // the SGR span cues the AI would otherwise use to pick error lines
    // out of stdout).
    private static string FormatErrorsSection(IReadOnlyList<string>? messages, int truncatedCount = 0)
    {
        if ((messages is null || messages.Count == 0) && truncatedCount == 0) return "";
        var sb = new StringBuilder();
        var shown = messages?.Count ?? 0;
        sb.Append("\n\n--- errors (");
        if (truncatedCount > 0)
        {
            sb.Append(shown).Append(" of ").Append(shown + truncatedCount);
        }
        else
        {
            sb.Append(shown);
        }
        sb.Append(") ---\n");
        if (shown == 1 && truncatedCount == 0)
        {
            sb.Append(messages![0]);
        }
        else if (shown > 0)
        {
            for (int i = 0; i < shown; i++)
            {
                sb.Append('[').Append(i + 1).Append("] ").Append(messages![i]);
                if (i < shown - 1) sb.Append('\n');
            }
        }
        if (truncatedCount > 0)
        {
            if (shown > 0) sb.Append('\n');
            sb.Append("... ").Append(truncatedCount)
              .Append(" newer error record(s) truncated.");
        }
        return sb.ToString();
    }

    [McpServerTool]
    [Description("Open a visible terminal window. The user can see and type in this terminal; AI commands sent via execute_command will also appear here in real time. If a standby console of the requested shell already exists it is reused unless `reason` is provided. Multiple shell types can be active simultaneously. Every response also reports the busy / finished / closed state of any other consoles you have open so background work stays visible.")]
    public static async Task<string> StartConsole(
        ConsoleManager consoleManager,
        [Description("Shell, REPL, or debugger to use. Name of any registered adapter (e.g. bash, pwsh, zsh, cmd, python, node, sqlite3, pdb, perldb, ...) or a full executable path. Call list_shells to see everything this ripple build accepts. Default: platform default shell.")]
        string? shell = null,
        [Description("Working directory. Default: home directory.")]
        string? cwd = null,
        [Description("Banner message displayed in the console (green text). Shown on both new and reused consoles.")]
        string? banner = null,
        [Description("Do NOT specify this parameter unless explicitly needed. Forces a new console launch instead of reusing an existing standby. The reason text is displayed in the console as yellow text.")]
        string? reason = null,
        [Description("Set true for sub-agent isolation. Returns an agent_id for subsequent calls.")]
        bool is_subagent = false,
        [Description("Agent ID for sub-agent console isolation.")]
        string? agent_id = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = agent_id ?? "default";

        if (is_subagent && string.IsNullOrEmpty(agent_id))
        {
            var newId = consoleManager.AllocateSubAgentId();
            return $"Sub-agent allocated: {newId}. Use this agent_id in subsequent calls.";
        }

        var result = await consoleManager.StartConsoleAsync(shell, cwd, reason, agentId, banner);
        var shellInfo = result.ShellFamily != null ? $" ({result.ShellFamily})" : "";
        var cwdInfo = !string.IsNullOrEmpty(result.Cwd) ? $" at {result.Cwd}" : "";
        var response = result.Status == "reused"
            ? $"Reusing standby {result.DisplayName}{shellInfo}{cwdInfo}."
            : $"Console {result.DisplayName}{shellInfo} opened{cwdInfo}.";

        return await AppendCachedOutputs(consoleManager, agentId, response);
    }

    [McpServerTool]
    [Description("Execute a command in the shared terminal. The command and its output are visible to the user as they stream. Session state (variables, modules, cwd) persists across calls. `shell` is REQUIRED — every call must state which shell family / REPL the pipeline targets. If the active console is busy with a user-typed command, ripple auto-routes to a same-family standby (or auto-starts a fresh one) and preserves your last known cwd via a cd preamble — if the source console was moved by the user since your last command, you'll see a one-line routing notice explaining what ripple did. If the active console is idle but the user manually cd'd in it since your last command, the call returns a verify-and-retry warning instead of running. Every response also reports any other consoles' busy / finished / closed state so you stay aware of background activity. IMPORTANT: do NOT invoke powershell/pwsh from the built-in Bash tool (the Dev-env / VS tools / user profile won't be loaded and many commands will fail). If you need PowerShell, start it via start_console (shell=\"pwsh\") and run it through this tool.")]
    public static async Task<string> ExecuteCommand(
        ConsoleManager consoleManager,
        [Description("The pipeline to execute (supports pipes, e.g. 'ls | grep foo')")]
        string pipeline,
        [Description("Shell type to execute in (bash, pwsh, zsh, cmd, python, duckdb, psql, or any registered adapter name / full path). REQUIRED — must be stated explicitly on every call. This closes a silent-failure hole where an AI that just talked to a REPL (psql, duckdb, python) could accidentally send a shell pipeline to that REPL because the active console defaulted there. If no matching console exists for the requested shell, one is auto-started.")]
        string shell,
        [Description("Timeout in seconds (0-170, default: 30). On timeout, execution continues in the background and output is cached for wait_for_completion. The timeout response includes a partialOutput snapshot so you can diagnose immediately. Increase for known long-running commands (builds, module imports). Use 0 for commands that block on user interaction (pause, Read-Host, read -p) — ripple flips to cache mode as soon as the pipeline is on the PTY so execute_command returns without blocking on the human key press, and the result is drained on the next tool call.")]
        int timeout_seconds = 30,
        [Description("Strip ANSI SGR (color / bold / reverse-video) escape sequences from the command output before returning. Default true — AI callers almost never want raw color codes mixed into the text they reason about, so we strip by default. Set false when you specifically need the color cues, e.g. when you'll be guiding the user through the output and want to point at a red error span or a green diff line by quoting it back with its highlighting intact, or when you're inspecting colored compiler diagnostics and the color carries semantic information you don't want to lose. The visible console keeps colors regardless of this flag — ripple's shared-console model deliberately preserves them for human viewing. Narrow strip: other control sequences (cursor movement, OSC hyperlinks) are already consumed by the renderer and not affected. Only acts on this call's response; cached results drained via wait_for_completion carry their own strip_ansi flag.")]
        bool strip_ansi = true,
        [Description("Agent ID for sub-agent console isolation.")]
        string? agent_id = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shell))
            return "✗ `shell` is required. Pass shell=\"pwsh\" / \"bash\" / \"zsh\" / \"cmd\" / or a registered REPL adapter name (python, duckdb, psql, node, ...). Stating the target shell on every execute_command call prevents silent routing into whatever console happened to be active — a REPL you were just talking to could otherwise interpret your next shell pipeline as input.";
        var agentId = agent_id ?? "default";
        var result = await consoleManager.ExecuteCommandAsync(pipeline, timeout_seconds, agentId, shell);

        string response;
        if (result.TimedOut)
        {
            var shellInfo = result.ShellFamily != null ? $" ({result.ShellFamily})" : "";
            var cmd = CommandTracker.TruncateForStatusLine(result.Command);
            var header = $"⧗ {result.DisplayName}{shellInfo} | Status: Busy | Pipeline: {cmd}\nRelated tools (try in this order): peek_console (see what the console is showing right now), send_input (if peek reveals an interactive prompt / pager / stuck TUI), wait_for_completion (if the command is just long-running)";
            if (!string.IsNullOrEmpty(result.PartialOutput))
            {
                var partial = strip_ansi ? StripAnsi(result.PartialOutput) : result.PartialOutput;
                response = $"{header}\n\n--- partial output (recent window, not the final result) ---\n{partial}";
            }
            else
                response = header;
        }
        else if (result.Switched)
            response = result.Output ?? "";
        else
        {
            var statusLine = FormatStatusLine(result);
            var body = string.IsNullOrEmpty(result.Output) ? "(no output)"
                     : strip_ansi ? StripAnsi(result.Output)
                     : result.Output;
            response = $"{statusLine}\n\n{body}{FormatErrorsSection(result.ErrorMessages, result.TruncatedErrorCount)}";
        }

        // A routing notice (e.g. "source console was moved by user, your
        // last known cwd was preserved by routing to a different console")
        // belongs above the status line so the AI sees the context before
        // reading the command's own output.
        if (!string.IsNullOrEmpty(result.Notice))
            response = $"{result.Notice}\n\n{response}";

        return await AppendCachedOutputs(consoleManager, agentId, response, excludePid: result.Pid);
    }

    [McpServerTool]
    [Description("Wait for AI-initiated commands that previously timed out to finish and retrieve their cached output. Returns one of three states: 'no commands pending' (nothing to wait for, stop calling), 'completed' (one or more drained results included in the response), or 'still busy' (call again to keep waiting). Use this after execute_command returned a Busy/timed-out result.")]
    public static async Task<string> WaitForCompletion(
        ConsoleManager consoleManager,
        [Description("Maximum seconds to wait (default: 30)")]
        int timeout_seconds = 30,
        [Description("Strip ANSI SGR escape sequences from each drained result's output. Default true — same rationale as execute_command's strip_ansi (AI callers rarely want raw color bytes). Set false when you need the color cues, e.g. you'll be quoting the output back to the user to walk them through a colored error span or diff and want the highlighting preserved. Same semantics otherwise: text content only, no cursor/OSC impact, visible console keeps colors regardless.")]
        bool strip_ansi = true,
        [Description("Agent ID for sub-agent console isolation.")]
        string? agent_id = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = agent_id ?? "default";
        var result = await consoleManager.WaitForCompletionAsync(timeout_seconds, agentId);

        if (result.HadNoBusyPids)
            return await AppendCachedOutputs(consoleManager, agentId,
                "No AI-initiated commands are currently running. Nothing to wait for.");

        var sb = new StringBuilder();
        foreach (var r in result.Completed)
        {
            sb.AppendLine(!string.IsNullOrEmpty(r.StatusLine) ? r.StatusLine : FormatStatusLine(r));
            sb.AppendLine();
            var body = string.IsNullOrEmpty(r.Output) ? "(no output)"
                     : strip_ansi ? StripAnsi(r.Output)
                     : r.Output;
            sb.AppendLine(body);
            var errSection = FormatErrorsSection(r.ErrorMessages, r.TruncatedErrorCount);
            if (errSection.Length > 0) sb.Append(errSection);
            sb.AppendLine();
        }

        if (result.StillBusy.Count > 0)
        {
            // Don't claim "after Ns" here — wait_for_completion returns
            // early on the first drained result (see ConsoleManager.cs
            // first-drain-wins loop), so the actual elapsed time is
            // usually much shorter than timeout_seconds.
            sb.AppendLine("Still busy. Call wait_for_completion again to keep waiting:");
            foreach (var b in result.StillBusy)
                sb.AppendLine(FormatBusyLine(b));
        }

        return await AppendCachedOutputs(consoleManager, agentId, sb.ToString().TrimEnd());
    }

    [McpServerTool]
    [Description("Send raw keystrokes to a busy console's PTY input. ONLY works when the target console is busy (idle consoles are rejected — use execute_command instead). Use this to: respond to an interactive prompt (Read-Host, password, y/n confirmation); send Ctrl+C (\\u0003) to interrupt a stuck or runaway command; exit a watch-mode TUI (q, Ctrl+C); send Enter (\\r) to dismiss a 'Press Enter to continue' pause; send arrow keys (\\u001b[A/B/C/D) to navigate a TUI menu. Always peek_console first to verify what the console is waiting for, then send_input with the appropriate response. Input is sent as-is — include \\r for Enter, \\u0003 for Ctrl+C, \\u001b[A for arrow-up, etc. JSON does not support \\xNN escapes; use \\uNNNN. Max 256 chars per call.")]
    public static async Task<string> SendInput(
        ConsoleManager consoleManager,
        [Description("Which console to send input to. Accepts a PID number or a display-name substring (e.g. \"Poseidon\" matches \"#10612 Poseidon\"). Required — you must specify the target.")]
        string console,
        [Description("The raw input to send to the PTY. Sent as-is. Use \\r for Enter, \\u0003 for Ctrl+C, \\u001b[A for arrow up, etc. JSON does not support \\xNN escapes — use \\uNNNN. Max 256 chars.")]
        string input,
        [Description("Agent ID for sub-agent console isolation.")]
        string? agent_id = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = agent_id ?? "default";

        if (string.IsNullOrEmpty(console))
            return "Error: 'console' parameter is required. Specify the target console by display name or PID.";
        if (string.IsNullOrEmpty(input))
            return "Error: 'input' parameter is required.";

        var result = await consoleManager.SendInputAsync(agentId, console, input);
        if (result == null)
            return await AppendCachedOutputs(consoleManager, agentId,
                $"No console matches \"{console}\". Use the display name (e.g. \"Poseidon\") or PID shown in previous tool responses.");

        string response;
        if (result.Status == "ok")
            response = $"✓ Sent to {result.DisplayName}.";
        else if (result.Status == "rejected")
            response = $"✗ {result.DisplayName} is not busy. Use execute_command to run commands on idle consoles.";
        else
            response = $"✗ {result.DisplayName}: {result.Error}";

        return await AppendCachedOutputs(consoleManager, agentId, response, excludePid: result.Pid);
    }

    [McpServerTool]
    [Description("Snapshot what a console has been printing recently — the output from the currently running command (since it started executing), plus the next prompt once it finishes. Use this to: check a busy console's progress without waiting for wait_for_completion (the peek pipe works during a running command); diagnose a timed-out execute_command (watch mode, interactive prompt, stalled progress); look in on a console that's busy with a user-typed command the AI can see is blocked. Read-only — does not interrupt or change anything. Returns the live rolling window (ANSI-stripped) plus busy / running-command / elapsed metadata. Works on any console — if you need to observe one other than your active one, pass its display name or PID in `console`.")]
    public static async Task<string> PeekConsole(
        ConsoleManager consoleManager,
        [Description("Which console to peek at. Accepts a PID number or a display-name substring (e.g. \"Reggae\" matches \"#43060 Reggae\"). Omit to peek at your current active console. Crucial when you need to observe a different console than the one you'd normally send commands to — for example, a console that just returned busy for execute_command.")]
        string? console = null,
        [Description("Agent ID for sub-agent console isolation.")]
        string? agent_id = null,
        [Description("Debug: include raw ring buffer bytes as an escaped hex preview. Off by default.")]
        bool raw = false,
        CancellationToken cancellationToken = default)
    {
        var agentId = agent_id ?? "default";
        var peek = await consoleManager.PeekConsoleAsync(agentId, console, raw);
        if (peek == null)
        {
            var msg = !string.IsNullOrEmpty(console)
                ? $"No console matches \"{console}\". Use the display name (e.g. \"Reggae\") or PID shown in previous tool responses."
                : "No console to peek at. Start one with start_console first.";
            return await AppendCachedOutputs(consoleManager, agentId, msg);
        }

        var shellInfo = peek.ShellFamily != null ? $" ({peek.ShellFamily})" : "";
        var busyMark = peek.Busy ? "⧗ Busy" : "✓ Idle";
        var sb = new StringBuilder();
        sb.AppendLine($"{busyMark} {peek.DisplayName}{shellInfo} | Status: {peek.Status}");
        if (peek.Busy && !string.IsNullOrEmpty(peek.RunningCommand))
        {
            var elapsedPart = peek.RunningElapsedSeconds.HasValue
                ? $" ({peek.RunningElapsedSeconds.Value:F1}s elapsed)"
                : "";
            var peekCmd = CommandTracker.TruncateForStatusLine(peek.RunningCommand);
            sb.AppendLine($"Running: {peekCmd}{elapsedPart}");
        }
        else if (peek.Busy)
        {
            var elapsedPart = peek.RunningElapsedSeconds.HasValue
                ? $" ({peek.RunningElapsedSeconds.Value:F1}s elapsed)"
                : "";
            sb.AppendLine($"Running: (user-typed command, unknown){elapsedPart}");
        }
        sb.AppendLine();
        sb.AppendLine("--- recent output ---");
        var recentFiltered = FilterStartupBanners(peek.RecentOutput);
        sb.Append(string.IsNullOrEmpty(recentFiltered) ? "(empty)" : recentFiltered);

        if (raw && !string.IsNullOrEmpty(peek.RawBase64))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("--- raw ring bytes (hex escaped) ---");
            var rawBytes = Convert.FromBase64String(peek.RawBase64!);
            var rawText = System.Text.Encoding.UTF8.GetString(rawBytes);
            var hex = new StringBuilder();
            foreach (var c in rawText)
            {
                if (c == '\x1b') hex.Append("\\e");
                else if (c == '\r') hex.Append("\\r");
                else if (c == '\n') hex.Append("\\n");
                else if (c == '\t') hex.Append("\\t");
                else if (c == '\a') hex.Append("\\a");
                else if (c < 0x20) hex.Append($"\\x{(int)c:x2}");
                else if (c == '\\') hex.Append("\\\\");
                else hex.Append(c);
            }
            sb.Append(hex.ToString());
        }

        return await AppendCachedOutputs(consoleManager, agentId, sb.ToString(), excludePid: peek.Pid);
    }

    /// <summary>
    /// Format a status line for a completed/failed command result.
    /// Includes console name, shell type, status, pipeline, duration, and location.
    /// </summary>
    /// <summary>
    /// Thin unpack of <see cref="ConsoleManager.ExecuteResult"/> into
    /// the shared <see cref="StatusLineFormatter.Format"/> — see that
    /// helper for the full rendering rules. Keeping the unpack here
    /// (instead of teaching the formatter about the ExecuteResult
    /// record) avoids a dependency from Services onto Tools.
    /// </summary>
    private static string FormatStatusLine(ConsoleManager.ExecuteResult r)
        => StatusLineFormatter.Format(
            r.Command, r.ExitCode, r.Duration, r.Cwd,
            r.ShellFamily, r.DisplayName,
            r.ErrorCount, r.LastExitCode);

    /// <summary>
    /// Detect closed consoles, collect cached outputs, and report busy
    /// consoles other than the one this tool just used. Everything gets
    /// prepended to the response so the AI stays aware of background work
    /// on consoles it isn't currently acting on. Pass excludePid to keep
    /// the response free of a duplicate busy line for the current console.
    /// </summary>
    private static async Task<string> AppendCachedOutputs(ConsoleManager consoleManager, string agentId, string response, int excludePid = 0)
    {
        var closed = consoleManager.DetectClosedConsoles(agentId);
        var cached = await consoleManager.CollectCachedOutputsAsync(agentId);
        var report = await consoleManager.CollectBusyStatusesAsync(agentId, excludePid);

        if (closed.Count == 0 && cached.Count == 0 && report.Busy.Count == 0 && report.Finished.Count == 0)
            return response;

        var sb = new StringBuilder();

        // Closed console notifications
        foreach (var (displayName, shellFamily) in closed)
        {
            var shellInfo = !string.IsNullOrEmpty(shellFamily) ? $" ({shellFamily})" : "";
            sb.AppendLine($"Console {displayName}{shellInfo} closed.");
            sb.AppendLine();
        }

        // Other consoles still running a previously-started command
        foreach (var b in report.Busy)
        {
            sb.AppendLine(FormatBusyLine(b));
        }
        if (report.Busy.Count > 0) sb.AppendLine();

        // Consoles whose previously-reported busy command has now finished.
        // Currently only fires for user-typed commands; AI commands with
        // cached output are drained above and never reach this branch.
        foreach (var f in report.Finished)
        {
            sb.AppendLine(FormatFinishedLine(f));
        }
        if (report.Finished.Count > 0) sb.AppendLine();

        // Cached command results (timed-out AI commands that have since completed).
        // Prefer the worker-baked StatusLine so the drained entry reads
        // exactly the way its inline counterpart would have — the worker
        // captured the display name / shell family / duration at Resolve
        // time, which may be more accurate than anything we can reconstruct
        // from current ConsoleInfo if the console has since been reused.
        foreach (var r in cached)
        {
            sb.AppendLine(!string.IsNullOrEmpty(r.StatusLine) ? r.StatusLine : FormatStatusLine(r));
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(r.Output) ? "(no output)" : r.Output);
            sb.AppendLine();
        }

        sb.Append(response);
        return sb.ToString();
    }

    /// <summary>
    /// One-line summary of a background busy console. Shown at the top of
    /// unrelated tool responses so the AI doesn't forget long-running work.
    /// </summary>
    private static string FormatBusyLine(ConsoleManager.BusyStatus b)
    {
        var shell = b.ShellFamily != null ? $" ({b.ShellFamily})" : "";
        var elapsed = b.ElapsedSeconds.HasValue ? $" ({b.ElapsedSeconds.Value:F0}s)" : "";
        var cmd = string.IsNullOrEmpty(b.RunningCommand)
            ? "(user command)"
            : CommandTracker.TruncateForStatusLine(b.RunningCommand);
        var cwdInfo = !string.IsNullOrEmpty(b.Cwd) ? $" | Location: {b.Cwd}" : "";
        return $"⧗ {b.DisplayName}{shell} | Status: Busy{elapsed} | Pipeline: {cmd}{cwdInfo}";
    }

    /// <summary>
    /// One-line summary of a console whose previously-busy command has just
    /// finished. Mirrors FormatBusyLine's shape so the two lines read
    /// consistently when they appear together.
    /// </summary>
    private static string FormatFinishedLine(ConsoleManager.FinishedStatus f)
    {
        var shell = f.ShellFamily != null ? $" ({f.ShellFamily})" : "";
        var cwdInfo = !string.IsNullOrEmpty(f.Cwd) ? $" | Location: {f.Cwd}" : "";
        return $"✓ {f.DisplayName}{shell} | Status: User command finished{cwdInfo}";
    }
}
