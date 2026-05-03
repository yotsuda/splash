using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ripple.Services.Adapters;

namespace Ripple.Services;

/// <summary>
/// Console worker process: runs in --console mode.
/// Creates a PTY, launches the shell, injects shell integration (OSC 633),
/// and serves commands via Named Pipe.
///
/// This is the "console side" — all PTY I/O, OSC parsing, and command tracking
/// happen here. The proxy side only communicates via Named Pipe protocol.
/// </summary>
public class ConsoleWorker
{
    // Worker logs go to a file, NOT to Console.Error.
    // The worker's visible console (Console.Out) is reserved for mirroring PTY output.
    // Anything to stderr would also appear there, mixed with PTY data.
    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), $"ripple-worker-{Environment.ProcessId}.log");
    private static void Log(string msg) { try { File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { } }

    /// <summary>
    /// Delete worker log files older than 24 hours so long-running sessions
    /// don't accumulate files in %TEMP%. Best-effort — failures are silent.
    /// </summary>
    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), "ripple-worker-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                        File.Delete(path);
                }
                catch { /* in use by another live worker, or locked — skip */ }
            }
            // Multi-line command tempfiles — HandleExecuteAsync writes the
            // command body to `.ripple-exec-{pid}-{guid}.ps1` and deletes
            // it inline via `Remove-Item` after dot-sourcing. If the
            // worker crashes or the shell dies mid-dot-source the delete
            // never runs, so sweep stale ones older than 24 hours here
            // on startup just like we do for the logs.
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), ".ripple-exec-*.ps1"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                        File.Delete(path);
                }
                catch { /* in use / locked — skip */ }
            }
        }
        catch { /* TEMP not readable — skip */ }
    }

    private string _pipeName;
    private readonly string _unownedPipeName;
    private int _proxyPid;
    private TaskCompletionSource<string>? _claimTcs;
    private readonly string _shell;
    private readonly string _cwd;
    /// <summary>
    /// Adapter for the shell this worker is hosting, looked up from
    /// AdapterRegistry.Default during construction. Null when no YAML
    /// adapter matches the shell name — in that case the worker falls back
    /// to the original hardcoded shell-family branches. Phase B milestones
    /// progressively replace those branches with adapter-driven reads.
    /// </summary>
    private readonly Adapter? _adapter;

    /// <summary>
    /// Milestone 2i: precomputed shell-family info so in-worker code never
    /// has to call the ConsoleManager helpers that phase B is tearing out.
    ///
    /// _shellFamily is the normalized family key (e.g. "pwsh", "bash");
    /// _isPwshFamily is true for pwsh / powershell;
    /// _defaultEnter is the PTY line-ending to use for this shell —
    /// adapter.input.line_ending when loaded, else the legacy
    /// "\r for pwsh/cmd, \n for everything else" rule.
    /// </summary>
    private readonly string _shellFamily;
    private readonly bool _isPwshFamily;
    private readonly string _defaultEnter;
    private IPtySession? _pty;

    /// <summary>
    /// Current mode for adapters that declare a <c>modes:</c> block
    /// (schema §9). Re-evaluated after each command resolves by
    /// running every auto_enter mode's detect regex against the tail
    /// of the captured output — see <see cref="ModeDetector"/>. Null
    /// when the adapter has no modes block. Surfaced on execute /
    /// get_status responses as <c>currentMode</c> so MCP clients
    /// (and AdapterDeclaredTestsRunner's expect_mode assertion) can
    /// observe mode transitions across commands.
    /// </summary>
    private string? _currentMode;
    private int? _currentModeLevel;
    private Stream? _writer;
    private readonly OscParser _parser = new();

    /// <summary>
    /// Regex-based prompt detector for adapters that declare
    /// <c>prompt.strategy: regex</c> (REPLs whose prompt cannot be replaced
    /// to emit OSC 633 markers — F# Interactive, ghci without integration,
    /// etc.). Null for adapters using <c>shell_integration</c> or
    /// <c>marker</c> strategies, in which case <see cref="OscParser"/> alone
    /// drives prompt boundary detection. Fed by the same read loop that
    /// feeds <see cref="_parser"/>; the synthetic events it produces are
    /// merged with real OSC events in TextOffset order so
    /// <see cref="CommandTracker"/> sees one coherent stream.
    /// </summary>
    private readonly RegexPromptDetector? _regexPromptDetector;
    // Continuation-prompt detector for regex-strategy REPLs that stall in an
    // incomplete-statement state (`>> ` in Lua, etc). Scanned alongside the
    // primary detector; on match during an AI command we write the adapter's
    // continuation_escape bytes to the PTY so the REPL errors back to its
    // primary prompt and the existing primary path resolves the command. Null
    // for adapters that don't declare a continuation pattern.
    private readonly RegexPromptDetector? _regexContinuationDetector;
    private readonly string? _regexContinuationEscape;
    private bool _regexContinuationEscapeSent;
    private bool _regexFirstPromptSeen;
    private readonly CommandTracker _tracker = new();

    // Live VT-100 interpreter fed from ReadOutputLoop — the authoritative
    // cursor that AnswerAndStripTerminalQueries reads on Unix when replying
    // to DSR (\x1b[6n). Replaces the static `Console.WindowHeight` row +
    // adapter-specific EstimateCursorCol approximation that caused PSReadLine
    // history recall to paint over the active prompt after ~5-10 AI commands
    // (see scratch/unix-vt-parity.md). On Windows ConPTY intercepts DSR
    // before ripple sees it, so this field is maintained but its cursor is
    // not consulted — the cost is per-byte CSI parsing overhead on the read
    // loop, expected <5% on large outputs.
    private VtLiteState _vtState = new(30, 200);

    // Partial DSR prefix held across PTY chunks: up to 3 bytes ("\x1b",
    // "\x1b[", or "\x1b[6") when the 4-byte DSR query "\x1b[6n" straddles
    // two reads. Flushed by AnswerAndStripDsr on the call that completes
    // or invalidates it. See the helper's docstring for why this is
    // needed even though a substring Contains check handled most DSRs.
    private string _pendingDsrPrefix = "";

    // Worker-owned finalize-once state. Issue #1 moved truncation / spill /
    // cache ownership out of the tracker and into the worker so the inline
    // execute_command path and the deferred wait_for_completion path
    // always read from the same finalized CommandResult. The tracker now
    // only emits a CompletedCommandSnapshot; the worker runs cleaning,
    // echo-stripping, truncation, and cache insertion in one place.
    // Non-readonly so TestReplaceTruncationHelper can swap in an
    // instance backed by a fake filesystem / clock for unit tests that
    // need to observe the lease / cleanup transitions without touching
    // real %TEMP%. Production code never writes to this field after
    // construction.
    private OutputTruncationHelper _truncationHelper = new();
    private readonly object _cacheLock = new();
    private readonly List<CachedCommandResult> _cachedResults = new();
    // Live spill-file lease set. Each cache entry that references a spill
    // file adds its path here; CollectCachedOutputs drains release the
    // lease. OutputTruncationHelper.CleanupOldSpillFiles is passed a
    // predicate backed by this set so age-based deletion never removes
    // a spill file that an undrained cache entry still points at.
    private readonly HashSet<string> _liveSpillPaths = new(StringComparer.OrdinalIgnoreCase);
    // Per-registration inline delivery routing. Replaces the old single-
    // slot _inlineDelivery field so two concurrent execute requests
    // landing on different pipe server instances can never overwrite
    // each other's TCS and cross-deliver results. HandleExecuteAsync
    // allocates a monotonic id via _commandIdSeq, parks the inline TCS
    // in this dictionary under that id, and threads the id through
    // CommandRegistration → CompletedCommandSnapshot.InlineDeliveryId.
    // FinalizeSnapshotAsync looks the matching TCS up by the snapshot's
    // id (not by reading a shared field) and delivers there; a missing
    // id falls through to the cache branch. Guarded by _cacheLock so
    // the snapshot handler and a concurrent FlipToCacheMode / detach
    // can never race on the same TaskCompletionSource.
    private long _commandIdSeq;
    private readonly Dictionary<long, TaskCompletionSource<CommandResult>> _inlineDeliveriesById = new();
    // Display identity supplied by the proxy at claim / set_title time.
    // Baked into every finalized CommandResult's statusLine so cache
    // entries stay self-describing — §7 of the ripple issue #1 plan.
    private string? _displayName;
    private bool _ready;
    private volatile int _outputLength;
    // Controls whether PTY output is mirrored to the worker's visible console.
    // Disabled during shell integration injection to hide the source echo.
    private volatile bool _mirrorVisible = true;
    // User-input hold gate: when true, InputForwardLoop buffers
    // keystrokes instead of forwarding them to the PTY. Set before
    // an AI command is written and cleared after the command
    // completes. Ctrl+C (0x03) passes through even when held so the
    // user can interrupt a stuck command. Held bytes are replayed
    // to the PTY on release so the user's typing isn't lost.
    private volatile bool _holdUserInput;
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _heldUserInput = new();
    // Direct stdout stream — bypasses Console.Out's TextWriter buffering.
    private Stream? _stdoutStream;

    /// <summary>
    /// Current console status for get_status requests. Cache ownership
    /// moved from the tracker to the worker in ripple issue #1, so
    /// "completed" is derived from the worker's cached-result list.
    /// </summary>
    private string Status
    {
        get
        {
            if (_tracker.Busy) return "busy";
            lock (_cacheLock) return _cachedResults.Count > 0 ? "completed" : "standby";
        }
    }

    /// <summary>
    /// Finalized command result held in the worker's cache. Carries the
    /// same wire fields the old tracker-side cache exposed
    /// (<c>output</c>, <c>exitCode</c>, <c>cwd</c>, <c>command</c>,
    /// <c>duration</c>, <c>statusLine</c>) plus the public spill path
    /// so cache-draining callers can retrieve it without reparsing the
    /// truncation preview.
    /// </summary>
    private sealed record CachedCommandResult(CommandResult Result, string? SpillFilePath);

    /// <summary>
    /// Finalized command result returned from the shared finalize-once
    /// path. Inline execute_command responses and cached entries drained
    /// by wait_for_completion both read from this shape, so the two
    /// paths can never diverge on output content or status line.
    /// </summary>
    internal sealed record CommandResult(
        string Output,
        int ExitCode,
        string? Cwd,
        string Command,
        string Duration,
        string StatusLine,
        string? SpillFilePath,
        // Number of error records the pipeline added to $Error during
        // execution. Surfaced in the worker's execute response so the
        // proxy can render "Errors: N" in the inline-path status line.
        // PowerShell-only (other adapters leave this at 0).
        int ErrorCount = 0,
        // Raw $LASTEXITCODE at command end — only populated when a
        // native exe returned non-zero mid-pipeline AND the overall
        // pipeline succeeded (ExitCode already 0). Surfaced as
        // "LastExit: N" in the status line. Zero means "no report"
        // (no native, native returned 0, or non-pwsh adapter).
        int LastExitCode = 0,
        // Structured error-message list — one entry per new $Error
        // record the PowerShell integration script emitted during the
        // pipeline (decoded from OSC 633;R payloads by the tracker).
        // Empty / null for non-pwsh adapters and for pwsh pipelines
        // that produced no errors. Surfaced in the worker's execute
        // response as an "errorMessages" JSON array so the proxy can
        // render a structured "--- errors ---" section.
        IReadOnlyList<string>? ErrorMessages = null,
        // How many $Error records were dropped beyond the integration
        // script's cap (OSC 633;T). Surfaced in the JSON response as
        // "truncatedErrorCount" so the proxy header can show
        // `(N of total)` instead of just `(N)`. 0 = no truncation.
        int TruncatedErrorCount = 0);

    private readonly string? _banner;
    private readonly string? _reason;
    // Rolling buffer for partial OSC title sequences that straddle
    // a PTY read-chunk boundary. ReplaceOscTitle owns the scan logic
    // and pushes any unterminated opener here so the next chunk can
    // re-scan with the terminator visible. Single owner: the read
    // loop's MirrorToVisible call path.
    private string _oscTitlePending = "";

    // Title set by proxy via set_title. Used to override OSC 0 title sequences
    // emitted by shells (e.g., bash's PROMPT_COMMAND sets "user@host: cwd").
    private volatile string? _desiredTitle;
    // Set to true when a strictly newer proxy tries to claim this worker.
    // Signals the main loop to stop serving pipes while keeping the PTY alive
    // so the user can continue working in the terminal.
    private volatile bool _obsolete;
    // Fires when ReadOutputLoop sees EOF on the PTY output stream, i.e. the
    // child shell process has exited (e.g. user typed `exit`). The main loop
    // watches this so the worker process shuts down cleanly instead of
    // hanging forever on a dead PTY while pending execute requests sit
    // stuck waiting for OSC markers that will never come.
    private readonly TaskCompletionSource _shellExitedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // This worker's own binary version — compared against the proxy_version
    // field in incoming claim requests to detect cross-version re-claim.
    private static readonly Version _myVersion =
        typeof(ConsoleWorker).Assembly.GetName().Version ?? new Version(0, 0);

    public ConsoleWorker(string pipeName, int proxyPid, string shell, string cwd, string? banner = null, string? reason = null)
    {
        _pipeName = pipeName;
        _proxyPid = proxyPid;
        _shell = shell;
        _cwd = cwd;
        _banner = banner;
        _reason = reason;
        _unownedPipeName = $"{ConsoleManager.PipePrefix}.{Environment.ProcessId}";

        // Adapter shadow lookup (phase B, Milestone 2a). Normalize the
        // shell path to a family name and look it up in the registry.
        // Null result means we fall through to the hardcoded paths.
        _shellFamily = ConsoleManager.NormalizeShellFamily(shell);
        _adapter = AdapterRegistry.Default?.Find(_shellFamily);
        Log(_adapter != null
            ? $"Adapter matched: name={_adapter.Name} family={_adapter.Family} version={_adapter.Version}"
            : $"No adapter matched for shell family '{_shellFamily}' — using hardcoded fallback");

        // process.executable{_candidates} override: for adapters whose
        // REPL name does not match an executable on PATH (fsi → dotnet,
        // jshell → java, perldb → perl, etc.), the adapter declares
        // which binary to launch. Re-resolve _shell against that name
        // so {shell_path} expansion in BuildCommandLine produces the
        // right launcher.
        //
        // Two override forms exist:
        //  - `executable_candidates: [list]` — walked left-to-right,
        //    each entry env-var-expanded and PATH-resolved. First entry
        //    that exists on disk wins. Use this when the binary lives
        //    at multiple plausible install locations across
        //    distributions (Perl / JDK / Python / etc.).
        //  - `executable: <string>` — single override, kept for
        //    backwards compatibility and for cases where there's
        //    exactly one canonical launcher (`dotnet` for fsi).
        // Candidates take precedence when present; the single-string
        // form is the fallback. When neither resolves, the adapter
        // name is left as-is and CreateProcess will report
        // ERROR_FILE_NOT_FOUND with a hopefully-actionable message.
        if (_adapter?.Process.ExecutableCandidates is { Count: > 0 } candidates)
        {
            string? pickedResolved = null;
            string? pickedRaw = null;
            foreach (var raw in candidates)
            {
                var expanded = Environment.ExpandEnvironmentVariables(raw);
                var resolved = ShellPathResolver.Resolve(expanded);
                if (File.Exists(resolved))
                {
                    pickedRaw = raw;
                    pickedResolved = resolved;
                    break;
                }
                Log($"Executable candidate miss: '{raw}' (expanded='{expanded}', resolved='{resolved}')");
            }
            if (pickedResolved != null)
            {
                Log($"Executable candidate picked: '{pickedRaw}' → {pickedResolved}");
                _shell = pickedResolved;
            }
            else
            {
                Log($"WARNING: all {candidates.Count} executable_candidates failed for adapter '{_adapter.Name}'; launch will likely fail");
            }
        }
        else if (!string.IsNullOrEmpty(_adapter?.Process.Executable))
        {
            var expanded = Environment.ExpandEnvironmentVariables(_adapter.Process.Executable);
            var resolved = ShellPathResolver.Resolve(expanded);
            Log($"Executable override: '{_adapter.Process.Executable}' → {resolved}");
            _shell = resolved;
        }

        // Adapters with prompt.strategy == "regex" don't speak OSC 633 at
        // all; the prompt is a literal visible string like "> " (F# Interactive)
        // or "irb(main):001:0> " (irb). Construct a RegexPromptDetector now
        // so the read loop can scan the cleaned PTY output for prompt
        // boundaries and synthesize PromptStart events for the tracker.
        if (_adapter?.Prompt.Strategy == "regex")
        {
            var pattern = _adapter.Prompt.Primary ?? _adapter.Prompt.PrimaryRegex;
            if (!string.IsNullOrEmpty(pattern))
            {
                _regexPromptDetector = new RegexPromptDetector(pattern);
                Log($"Regex prompt detector active: pattern={pattern}");
            }
            else
            {
                Log("WARNING: prompt.strategy == regex but no prompt.primary / prompt.primary_regex set");
            }

            // Optional continuation-prompt detector. Paired with
            // continuation_escape so we can force the REPL out of an
            // absorbing `>> ` state back to its primary prompt and let
            // the primary path resolve the AI command cleanly.
            var contPattern = _adapter.Prompt.Continuation;
            var contEscape = _adapter.Prompt.ContinuationEscape;
            if (!string.IsNullOrEmpty(contPattern) && !string.IsNullOrEmpty(contEscape))
            {
                _regexContinuationDetector = new RegexPromptDetector(contPattern);
                _regexContinuationEscape = contEscape;
                Log($"Regex continuation detector active: pattern={contPattern}, escape={EscapeForLog(contEscape)}");
            }
            else if (!string.IsNullOrEmpty(contPattern) || !string.IsNullOrEmpty(contEscape))
            {
                Log("WARNING: prompt.continuation and prompt.continuation_escape must both be set; ignoring partial config");
            }
        }

        // Milestone 2i: bake shell-family booleans into readonly fields so
        // no in-worker code needs ConsoleManager.IsPowerShellFamily /
        // EnterKeyFor after this constructor returns. Those helpers are
        // removed from ConsoleManager as part of this milestone.
        _isPwshFamily = _shellFamily is "pwsh" or "powershell";
        _defaultEnter = _adapter?.Input.LineEnding
            ?? (_isPwshFamily || _shellFamily == "cmd" ? "\r" : "\n");

        // Subscribe once to the tracker's completion event. Every primary
        // completion — whether an inline caller is still listening or the
        // response channel has been flipped to cache mode — routes through
        // FinalizeSnapshotAsync. Having one subscriber, one handler, and
        // one finalize path is how the inline and wait_for_completion
        // branches stay guaranteed equivalent (plan §2, exec-order step 2).
        _tracker.SnapshotProduced += snapshot =>
            _ = Task.Run(() => FinalizeSnapshotAsync(snapshot));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Ensure the worker's visible console uses UTF-8 for both input and output.
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        // Set initial window title (unowned format — proxy will update after claiming)
        Console.Title = $"#{Environment.ProcessId} ~~~~";

        // Enable Virtual Terminal Processing on stdout so the visible console
        // interprets ANSI/VT escape sequences (cursor movement, clear-to-EOL,
        // colors) emitted by the shell instead of writing them as literal chars.
        EnableVirtualTerminalOutput();

        // Display banner/reason. For pwsh/powershell, banner emission is
        // delegated to the generated integration tempfile so it survives
        // ConPTY's startup `\e[2J\e[H` wipe; see BuildCommandLine. For
        // other shells, write directly to the worker's stdout here. (TODO:
        // bash/zsh/cmd have the same ConPTY-wipe issue and would also
        // benefit from shell-side emission.)
        if (!_isPwshFamily)
            WriteBanner();

        // Prepare shell integration script BEFORE launching the shell.
        // For pwsh, we pass it via -NoExit -Command so it doesn't echo in the console.
        var commandLine = BuildCommandLine();

        // Launch shell via platform PTY (ConPTY on Windows, forkpty on Linux/macOS)
        // Use the visible console's actual dimensions instead of hardcoded 120x30.
        // MSYS2/Git Bash needs the parent's environment (MSYSTEM, HOME, PATH with Git paths).
        // pwsh uses a clean environment to avoid inheriting MCP server variables.
        var shellName = _shellFamily;
        // Milestone 2b: inherit_environment comes from the adapter when
        // one is loaded for this shell, else falls back to the hardcoded
        // "pwsh family = clean env, everyone else = inherit" rule.
        bool inheritEnv = _adapter?.Process.InheritEnvironment
            ?? !_isPwshFamily;
        // PTY cols track the visible ConHost width. A previous version
        // floored at 200 to dodge mid-word soft-wraps in AI output, but
        // that broke self-repainting lines: pwsh's Write-Progress pads
        // its bar to the declared PTY width, and when that exceeds the
        // visible console's width the bar wraps in ConHost and the
        // trailing \r returns to the wrapped row only, leaving each
        // update stacked vertically. Matching the visible width makes
        // \r overwrite semantics work; mid-word splitting on very narrow
        // terminals is a rare edge case the AI can still read through.
        int cols = Console.WindowWidth > 0 ? Console.WindowWidth : 120;
        int rows = Console.WindowHeight > 0 ? Console.WindowHeight : 30;
        var envOverrides = _adapter?.Process.Env;

        // init.delivery: rc_file — stage the integration script as a
        // shell-specific rc file that the interpreter sources on its own
        // startup, before any PTY interaction. The hooks are registered
        // by the time the first prompt is drawn, so the ready-phase
        // inject cycle is bypassed entirely. zsh is the motivating case:
        // MSYS2 zsh under ConPTY runs ZLE in a mode that swallows
        // PTY-written `source <tmpfile>` bytes without submitting them
        // (neither \n nor \r\n is a reliable Enter from our write path),
        // so pty_inject hangs on WaitForReady forever. ZDOTDIR sidesteps
        // ZLE entirely. The same mechanism extends to any shell with an
        // rc-directory env var (future fish / bash --init-file use cases).
        if (_adapter?.Init.Delivery == "rc_file"
            && _adapter.Init.RcFile is { DirEnvVar: string envVar, FileName: string fileName }
            && !string.IsNullOrEmpty(envVar) && !string.IsNullOrEmpty(fileName)
            && _adapter.IntegrationScript is string rcScript)
        {
            var rcDir = Path.Combine(Path.GetTempPath(), $".ripple-{_shellFamily}-{Environment.ProcessId}");
            Directory.CreateDirectory(rcDir);
            // Line endings are normalised to LF before write: the shells
            // that read rc files under this delivery mode (zsh on MSYS2,
            // future fish, etc.) all come from the POSIX lineage and
            // parse CRLF as part of the command text.
            await File.WriteAllTextAsync(Path.Combine(rcDir, fileName), rcScript.Replace("\r\n", "\n"), ct);
            envOverrides = new Dictionary<string, string>(envOverrides ?? new Dictionary<string, string>())
            {
                [envVar] = rcDir
            };
            Log($"rc_file delivery: staged {rcDir}\\{fileName}; {envVar} set in child env");
        }

        _pty = PtyFactory.Start(commandLine, _cwd, cols, rows,
            inheritEnvironment: inheritEnv,
            envOverrides: envOverrides);
        _tracker.SetTerminalSize(cols, rows);
        _vtState = new VtLiteState(rows, cols);
        _writer = _pty.InputStream;

        // Start reading PTY output on dedicated thread (feeds OscParser + CommandTracker)
        var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var readTask = ReadOutputLoop(readCts.Token);

        // Start user-input forwarding immediately — before shell integration
        // loading, before WaitForReady. Early start is critical on Unix:
        // PSReadLine (pwsh) fires DSR during its own startup sync, BEFORE the
        // first OSC A that WaitForReady gates on. The outer terminal emulator
        // answers DSR with the real cursor position, and that reply has to
        // reach the shell for PSReadLine to continue. If input forwarding
        // starts after WaitForReady the reply sits in ripple's stdin buffer
        // long enough that PSReadLine either times out on DSR wait
        // (degraded-mode rendering — wrong cursor row) or consumes the
        // buffered reply as typed characters once forwarding catches up
        // (garbage in the command line). Forwarding from t=0 keeps the
        // byte relay bidirectional for every sequence the shell and the
        // outer terminal exchange during startup handshake. Windows is
        // unaffected: ConPTY is its own terminal emulator so the outer /
        // inner distinction doesn't exist there.
        var inputTask = InputForwardLoop(ct);

        // Watch the child shell process. ConPTY does NOT close the output
        // pipe when the child exits, so ReadOutputLoop's blocking Read
        // would sit waiting forever if we relied on EOF. Instead wait on
        // the process handle directly and, when it fires, signal the main
        // loop via _shellExitedTcs so the worker can tear itself down.
        _ = WaitForShellExitAsync(ct);

        // Milestone 2c: PTY line-ending sequence for submitting input.
        // Comes from the adapter's input.line_ending when loaded (pwsh/cmd
        // = "\r", bash/zsh = "\n"), else falls back to the hardcoded
        // family-based rule.
        var enter = _adapter?.Input.LineEnding
            ?? _defaultEnter;

        // Milestone 2f: ready-phase orchestration is driven by adapter.Ready
        // fields. Three paths emerge from the four shells:
        //
        //   pwsh  - integration was loaded via -Command at launch; the first
        //           OSC A fires automatically. No settle, no inject, no kick.
        //   cmd   - /k prompt doesn't paint until it reads input. Settle,
        //           then kick Enter to render the OSC-aware prompt — that
        //           kick IS the ready signal, so it happens BEFORE
        //           WaitForReady.
        //   bash/zsh - settle, suppress mirror, inject the integration
        //           script via PTY stdin, settle again. The kick happens
        //           AFTER WaitForReady so the suppressed prompt is redrawn.
        //
        // suppress_mirror_during_inject is the discriminator between the
        // cmd path (kick-before-ready) and the bash/zsh path (inject +
        // kick-after-ready), since both have kick_enter_after_ready=true.
        var settleMs = _adapter?.Ready.SettleBeforeInjectMs
            ?? (_isPwshFamily ? 0 : 2000);
        var suppressMirror = _adapter?.Ready.SuppressMirrorDuringInject
            ?? (!_isPwshFamily && shellName is not "cmd");
        var kickEnter = _adapter?.Ready.KickEnterAfterReady
            ?? !_isPwshFamily;
        var delayAfterInject = _adapter?.Ready.DelayAfterInjectMs
            ?? (suppressMirror ? 500 : 0);

        if (settleMs > 0)
            await WaitForOutputSettled(ct);

        if (suppressMirror)
        {
            _mirrorVisible = false;
            await InjectShellIntegration(ct);
            if (delayAfterInject > 0)
                await Task.Delay(delayAfterInject, ct);
        }
        else if (kickEnter)
        {
            await WriteToPty(enter, ct);
        }

        // Wait for PromptStart marker from shell integration (confirms OSC pipeline is working)
        // Wait until the shell reports its first OSC A (PromptStart).
        // No wall-clock timeout: a cold pwsh startup + Defender first-
        // scan of ripple.exe + Import-Module PSReadLine + sourcing
        // integration.ps1 can take arbitrary real time, and any fallback
        // "proceed without OSC markers" path lets the worker accept AI
        // commands before the shell is actually interactive — the next
        // stray first-prompt OSC A then resolves them against a stale
        // pre-command buffer, returning reason banner / PSReadLine
        // prediction rendering as if it were command output. Instead we
        // patiently wait; if the shell process dies during startup
        // WaitForShellExitAsync fires _shellExitedTcs and bails us out.
        await WaitForReady(ct);
        _ready = true;
        _mirrorVisible = true;

        // For regex-strategy adapters, the FIRST visible prompt may
        // appear before the REPL's eval loop has finished wiring up
        // (fsi --use:script.fsx is the canonical case: the post-script
        // -load prompt fires ~200ms before stdin input is actually
        // accepted by the eval loop). The detector has no way to
        // distinguish "true REPL ready" from "post-script-load
        // intermediate prompt", so honor an adapter-declared settle
        // window before letting the worker accept commands. Reuses
        // ready.delay_after_inject_ms because the semantics are the
        // same: "wait this long after the ready signal before
        // declaring the worker open for business".
        if (_regexPromptDetector != null && _adapter?.Ready.DelayAfterInjectMs is int dms && dms > 0)
        {
            Log($"regex strategy: settling {dms} ms after first prompt before pipe ready");
            await Task.Delay(dms, ct);
        }

        // For shells with PTY-injected integration (bash/zsh), the prompt drawn
        // during injection was suppressed. Send a kick to draw a fresh prompt.
        // Milestone 2f: gated on suppressMirror (the inject path) so cmd's
        // pre-ready kick isn't duplicated here.
        if (suppressMirror && kickEnter)
        {
            await WriteToPty(enter, ct);
        }

        Log($"Shell ready, pipe={_pipeName}");

        // Monitor visible console window resizes and propagate to ConPTY
        var resizeTask = ResizeMonitorLoop(ct);

        // cmd has no preexec hook (no PROMPT-time access to %ERRORLEVEL%, no
        // way to fire OSC 633 C when the user starts a command), so the
        // OSC-driven user-busy tracking that pwsh and bash rely on is silent
        // for cmd. Run a side-channel polling loop that watches the cmd
        // process's CPU usage and child-process count to derive a busy hint:
        // CPU > 0 → cmd is running an internal builtin, child present → cmd
        // launched an external command. Either signal flips the tracker to
        // busy so execute_command auto-routes around the user.
        //
        // Milestone 2h: gated on adapter.capabilities.user_busy_detection
        // (falls back to the old shellName == "cmd" check for unknown shells).
        Task? userBusyTask = null;
        var userBusyMethod = _adapter?.Capabilities.UserBusyDetection
            ?? (shellName == "cmd" ? "process_polling" : null);
        if (OperatingSystem.IsWindows() && userBusyMethod == "process_polling")
            userBusyTask = UserBusyDetectorLoop(ct);

        // Run owned + unowned pipe servers. Two owned listeners share the
        // pipe name via NamedPipeServerStream.MaxAllowedServerInstances — a
        // long-running execute occupies one instance, the other stays free
        // to handle get_status / get_cached_output without stalling. When
        // the proxy dies, both owned instances are cancelled and the
        // unowned pipe keeps running for re-claim by another proxy.
        const int OwnedListenerCount = 2;
        while (!ct.IsCancellationRequested && !_obsolete)
        {
            using var ownedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var ownedTasks = new Task[OwnedListenerCount];
            for (int i = 0; i < OwnedListenerCount; i++)
                ownedTasks[i] = RunPipeServerAsync(_pipeName, ownedCts.Token);
            var unownedTask = RunPipeServerAsync(_unownedPipeName, ct);
            var monitorTask = MonitorParentProxyAsync(ct);

            // Wait for proxy death, shell exit, or external cancellation.
            // Shell exit (user typed `exit`, or the shell crashed) wins and
            // shuts the whole worker down so the proxy's next pipe request
            // gets a clean IOException and reports "Previous console died"
            // to the AI. Otherwise proxy death triggers re-claim flow.
            var mainWaitWinner = await Task.WhenAny(monitorTask, _shellExitedTcs.Task);
            if (mainWaitWinner == _shellExitedTcs.Task)
            {
                Log("Shell process exited; worker shutting down");
                ownedCts.Cancel();
                await Task.WhenAll(ownedTasks).ContinueWith(_ => { });
                break;
            }

            // Proxy died — stop owned listeners, keep unowned running
            ownedCts.Cancel();
            await Task.WhenAll(ownedTasks).ContinueWith(_ => { });

            // Wait for re-claim via unowned pipe (blocks until _claimTcs is set)
            _claimTcs = new TaskCompletionSource<string>();
            Console.Title = $"#{Environment.ProcessId} ~~~~";
            Log("Proxy died, waiting for re-claim on unowned pipe...");

            string newPipeName;
            try
            {
                newPipeName = await _claimTcs.Task;
            }
            catch (InvalidOperationException) when (_obsolete)
            {
                Log("Claim refused (obsolete); exiting pipe service loop. Shell remains available for user.");
                break;
            }
            _pipeName = newPipeName;
            _proxyPid = GetProxyPidFromPipeName(newPipeName);
            _claimTcs = null;
            Log($"Re-claimed by proxy {_proxyPid}, new pipe={_pipeName}");
        }

        // Obsolete mode: the pipe service has stopped, but the shell is still
        // alive and the user may still be working in it. readTask/inputTask/
        // resizeTask are running on `ct`; wait here until the user closes the
        // console window (which cancels `ct`) or the shell process exits,
        // then fall through to cleanup.
        if (_obsolete)
        {
            try { await Task.WhenAny(Task.Delay(Timeout.Infinite, ct), _shellExitedTcs.Task); }
            catch (OperationCanceledException) { }
        }

        // Cleanup — guarded so any late-stage race (ReadOutputLoop still
        // blocked on the now-dead PTY, double-dispose of handles, etc.)
        // can't escape and turn an otherwise-clean shutdown into a non-
        // zero process exit. Windows Terminal's "close on exit" default
        // only fires on exit code 0, so a thrown exception here would
        // leave the visible window stuck after the shell died.
        try { readCts.Cancel(); } catch { }
        try { _pty?.Dispose(); } catch (Exception ex) { Log($"PTY dispose: {ex.GetType().Name}: {ex.Message}"); }
        Log("RunAsync completed cleanly");
    }

    /// <summary>
    /// Watch the parent proxy process. When it dies, the worker continues
    /// running on the unowned pipe so another proxy can claim it.
    /// </summary>
    private async Task MonitorParentProxyAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct);
            try
            {
                using var _ = System.Diagnostics.Process.GetProcessById(_proxyPid);
            }
            catch
            {
                // Parent died — main loop handles title revert and re-claim.
                return;
            }
        }
    }

    // --- Shell integration injection ---

    /// <summary>
    /// Build the command line for launching the shell.
    /// For pwsh: injects shell integration via -NoExit -Command (no echo in console).
    /// For bash/zsh: plain shell, injection happens later via PTY input.
    /// </summary>
    private string BuildCommandLine()
    {
        var shellName = _shellFamily;

        if (_isPwshFamily)
        {
            // Milestone 2d: integration script comes from the adapter
            // (AdapterLoader resolves YAML's script_resource to the
            // embedded ShellIntegration/integration.ps1 content at
            // startup). Milestone 2j: the pre-phase-B LoadEmbeddedScript
            // fallback was removed after every in-tree shell got a YAML
            // adapter; unknown pwsh-family shells without an adapter now
            // fall through to the generic launch path below.
            if (_adapter?.IntegrationScript is { } script)
            {
                // Milestone 2e-3: assemble the tempfile body, init invocation,
                // and outer command line by expanding adapter templates. The
                // fallbacks below match the pre-adapter hardcoded strings so
                // unknown pwsh-family shells still boot.
                //
                // Prepend Write-Host banner/reason lines so they're emitted by
                // pwsh itself AFTER ConPTY's initial `\e[?9001h...\e[2J\e[H`
                // screen-clear payload. If we wrote them to the worker's
                // stdout before the PTY started (the old WriteBanner path),
                // ConPTY wipes them almost immediately, which the user saw
                // as banner text flashing on screen for ~0.5s.
                var bannerTpl = _adapter?.Init.BannerInjection?.BannerTemplate
                    ?? "Write-Host '{banner}' -ForegroundColor Green\n";
                var reasonTpl = _adapter?.Init.BannerInjection?.ReasonTemplate
                    ?? "Write-Host 'Reason: {reason}' -ForegroundColor DarkYellow\n";

                var prefix = new StringBuilder();
                if (!string.IsNullOrEmpty(_banner))
                    prefix.Append(ExpandTemplate(bannerTpl, ("banner", _banner.Replace("'", "''"))));
                if (!string.IsNullOrEmpty(_banner) && !string.IsNullOrEmpty(_reason))
                    prefix.AppendLine("Write-Host");
                if (!string.IsNullOrEmpty(_reason))
                    prefix.Append(ExpandTemplate(reasonTpl, ("reason", _reason.Replace("'", "''"))));
                if (prefix.Length > 0) prefix.AppendLine("Write-Host");

                var tmpPrefix = _adapter?.Init.Tempfile?.Prefix ?? ".ripple-integration-";
                var tmpExt = _adapter?.Init.Tempfile?.Extension ?? ".ps1";
                var tmpFile = Path.Combine(
                    Path.GetTempPath(),
                    $"{tmpPrefix}{Environment.ProcessId}{tmpExt}");
                File.WriteAllText(tmpFile, prefix.ToString() + script);

                var initInvocationTpl = _adapter?.Init.InitInvocationTemplate
                    ?? "Import-Module PSReadLine -ErrorAction SilentlyContinue; . '{tempfile_path}'; Remove-Item '{tempfile_path}' -ErrorAction SilentlyContinue";
                var initInvocation = ExpandTemplate(initInvocationTpl,
                    ("tempfile_path", tmpFile));

                var commandTpl = _adapter?.Process.CommandTemplate
                    ?? "\"{shell_path}\" -NoExit -Command \"{init_invocation}\"";
                return ExpandTemplate(commandTpl,
                    ("shell_path", _shell),
                    ("init_invocation", initInvocation));
            }
        }

        // Phase C: REPL-style adapters whose integration lives in a
        // tempfile the interpreter sources at startup (Python's `-i
        // script.py`, and in principle any future REPL with a similar
        // launch convention). Same shape as the pwsh branch above but
        // without banner_injection — the worker's pre-PTY WriteBanner
        // handles banner/reason rendering for non-pwsh adapters, so
        // the tempfile body stays pure integration code.
        //
        // The script body may itself reference {tempfile_path} (e.g. to
        // hardcode the absolute path for self-deletion), so substitute
        // it once before writing the file. Guarded on launch_command +
        // script_resource + non-pwsh + non-cmd so pwsh and cmd keep
        // their existing branches.
        if (!_isPwshFamily && shellName != "cmd" &&
            _adapter is { Init: { Delivery: "launch_command", ScriptResource: not null } } replAdapter &&
            replAdapter.IntegrationScript is { } replScript &&
            !string.IsNullOrEmpty(replAdapter.Process.CommandTemplate))
        {
            var tmpPrefix = replAdapter.Init.Tempfile?.Prefix ?? ".ripple-integration-";
            var tmpExt = replAdapter.Init.Tempfile?.Extension ?? "";
            var tmpFile = Path.Combine(
                Path.GetTempPath(),
                $"{tmpPrefix}{Environment.ProcessId}{tmpExt}");

            File.WriteAllText(tmpFile, replScript.Replace("{tempfile_path}", tmpFile));

            var initInvocationTpl = replAdapter.Init.InitInvocationTemplate
                ?? "\"{tempfile_path}\"";
            var initInvocation = ExpandTemplate(initInvocationTpl,
                ("tempfile_path", tmpFile));

            return ExpandTemplate(replAdapter.Process.CommandTemplate,
                ("shell_path", _shell),
                ("init_invocation", initInvocation));
        }

        // cmd.exe: set PROMPT with OSC 633 markers via /k at startup.
        // The D;0 between P and A is a fake CommandFinished marker so the AI
        // command tracker resolves. cmd has no way to expand %ERRORLEVEL% at
        // PROMPT-display time, so the reported exit code is always 0 — a
        // documented limitation. Without this marker, AI commands hang
        // forever because Resolve() requires _commandEnd >= 0.
        //
        // Milestone 2e-2: command_template + prompt_template come from the
        // adapter. The prompt_template payload is substituted into the outer
        // command_template first (so any literal braces in it don't collide
        // with subsequent {shell_path} expansion).
        if (shellName is "cmd")
        {
            if (_adapter is { } a2e2 &&
                !string.IsNullOrEmpty(a2e2.Process.CommandTemplate) &&
                !string.IsNullOrEmpty(a2e2.Process.PromptTemplate))
            {
                return ExpandTemplate(
                    ExpandTemplate(a2e2.Process.CommandTemplate,
                        ("prompt_template", a2e2.Process.PromptTemplate)),
                    ("shell_path", _shell));
            }

            var prompt = "$E]633;P;Cwd=$P$E\\$E]633;D;0$E\\$E]633;A$E\\$P$G$S";
            return $"\"{_shell}\" /q /k \"prompt {prompt}\"";
        }

        // Milestone 2e-1: bash / zsh (and any future simple shell) can
        // be driven entirely from adapter.process.command_template with
        // just {shell_path} substitution. The integration script is
        // injected later via PTY input by InjectShellIntegration().
        if (_adapter is { } a2e1 &&
            !string.IsNullOrEmpty(a2e1.Process.CommandTemplate) &&
            shellName is "bash" or "sh" or "zsh")
        {
            return ExpandTemplate(a2e1.Process.CommandTemplate,
                ("shell_path", _shell));
        }

        // Generic REPL adapter: command_template without an integration
        // script. Used by adapters whose REPL has no script-load
        // mechanism (or doesn't need one), e.g. groovy (regex strategy,
        // direct java invocation with a jar classpath) and jshell
        // (regex strategy, bare `jshell` invocation). Unlike the
        // "REPL-style with script" branch above (which requires
        // Init.Delivery == launch_command + script_resource), this
        // path fires whenever an adapter declares a command_template
        // but no integration script. `{init_invocation}` is NOT
        // substituted here — adapters on this path must not reference
        // it. ExpandTemplate additionally applies %ENVVAR% expansion
        // so paths like `%LOCALAPPDATA%\ripple-deps\...` resolve.
        if (!_isPwshFamily && shellName != "cmd" &&
            _adapter is { } cmdTplAdapter &&
            !string.IsNullOrEmpty(cmdTplAdapter.Process.CommandTemplate))
        {
            return ExpandTemplate(cmdTplAdapter.Process.CommandTemplate,
                ("shell_path", _shell));
        }

        // Fallback: no YAML adapter for this shell family — keep the
        // hardcoded launch strings so unknown shells still boot.
        if (shellName is "bash" or "sh")
            return $"\"{_shell}\" --login -i";

        if (shellName is "zsh")
            return $"\"{_shell}\" -l -i";

        return $"\"{_shell}\"";
    }

    /// <summary>
    /// Minimal {name} → value substitution for adapter templates.
    /// Deliberately non-recursive: placeholder values are inserted as-is so
    /// they can't reference other placeholders. Callers that need layered
    /// expansion (pwsh's init_invocation inside command_template) call this
    /// multiple times from innermost to outermost.
    /// </summary>
    private static string ExpandTemplate(string template, params (string Name, string Value)[] vars)
    {
        var result = template;
        foreach (var (name, value) in vars)
            result = result.Replace("{" + name + "}", value);
        // After the named-placeholder substitution, also expand Windows
        // `%ENVVAR%` references so adapter authors can reference user-
        // specific paths like `%LOCALAPPDATA%\ripple-deps\...` without
        // ripple having to mint a new named placeholder for every
        // possible env var. No-op on non-Windows (the method just
        // returns the input unchanged there).
        if (OperatingSystem.IsWindows())
            result = Environment.ExpandEnvironmentVariables(result);
        return result;
    }

    private async Task WaitForOutputSettled(CancellationToken ct)
    {
        // Timings come from adapter.ready.output_settled_* (schema v1), with
        // the pre-schema hardcoded 2s/1s/30s as the ReadySpec defaults.
        var ready = _adapter?.Ready;
        int minMs    = ready?.OutputSettledMinMs    ?? 2000;
        int stableMs = ready?.OutputSettledStableMs ?? 1000;
        int maxMs    = ready?.OutputSettledMaxMs    ?? 30000;

        int pollMs = Math.Max(50, stableMs / 2);
        int requiredConsecutive = Math.Max(1, (int)Math.Ceiling(stableMs / (double)pollMs));

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(maxMs);
        var lastLength = 0;
        var settledCount = 0;
        await Task.Delay(minMs, ct);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollMs, ct);

            var currentLength = _outputLength;
            if (currentLength == lastLength && currentLength > 0)
            {
                settledCount++;
                if (settledCount >= requiredConsecutive) break;
            }
            else
            {
                settledCount = 0;
            }
            lastLength = currentLength;
        }
    }

    private async Task InjectShellIntegration(CancellationToken ct)
    {
        // This path is only reached for POSIX-style shells (bash / sh / zsh
        // and whatever else falls through the RunAsync shell dispatch into
        // the "inject via PTY" branch). pwsh/powershell integration is
        // injected via BuildCommandLine's -Command argument, and cmd sets
        // its PROMPT at /k startup. So the old dead pwsh branch here used
        // to never fire — it's gone now.
        //
        // Milestone 2d: adapter.IntegrationScript is resolved from YAML's
        // script_resource at startup. Milestone 2j: the embedded-resource
        // fallback was removed once every in-tree POSIX shell had a YAML
        // adapter; shells without an adapter fall through to no-OSC mode.
        var shellName = _shellFamily;
        string? script = _adapter?.IntegrationScript;

        if (script == null)
        {
            Log($"WARNING: No shell integration script found, falling back to no-OSC mode");
            return;
        }

        // init.delivery: rc_file — the script was staged before
        // CreateProcess (see the env setup in RunAsync) and the shell
        // sources it on its own startup, so pty_inject is a no-op.
        // InjectShellIntegration is still called from the ready path
        // because the settle/suppress/kick orchestration around it
        // applies identically; the delivery-mode check short-circuits
        // only the PTY write.
        if (_adapter?.Init.Delivery == "rc_file")
        {
            Log("rc_file delivery: integration already staged before spawn; skipping pty_inject");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows bash (WSL, MSYS2, Git Bash) — write the script to a
            // Windows temp path directly since the worker and child share
            // the filesystem, then teach the shell how to find it in its
            // own namespace (/mnt/c/... for WSL, /c/... for MSYS2).
            var windowsPath = Path.Combine(Path.GetTempPath(), $".ripple-integration-{Environment.ProcessId}.sh");
            var scriptContent = script.Replace("\r\n", "\n");
            await File.WriteAllTextAsync(windowsPath, scriptContent, ct);

            var unixPath = IsWslBash(_shell)
                ? "/mnt/" + char.ToLower(windowsPath[0]) + windowsPath[2..].Replace('\\', '/')
                : "/" + char.ToLower(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');

            Log($"Integration script: {windowsPath} → {unixPath} (exists={File.Exists(windowsPath)}, wsl={IsWslBash(_shell)})");

            await WriteToPty($"source '{unixPath}'; rm -f '{unixPath}'\n", ct);
        }
        else
        {
            // Linux/macOS: heredoc the script into the child's own /tmp so
            // we don't need the worker to see the child's filesystem.
            var tmpFile = $"/tmp/.ripple-integration-{Environment.ProcessId}.sh";
            var injection = new StringBuilder();
            injection.AppendLine($"cat > {tmpFile} << 'RIPPLE_EOF'");
            injection.AppendLine(script.TrimEnd());
            injection.AppendLine("RIPPLE_EOF");
            injection.AppendLine($"source {tmpFile}; rm -f {tmpFile}");
            await WriteToPty(injection.ToString(), ct);
        }
    }

    // --- cmd user-busy detector (CPU + child polling) ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint INVALID_HANDLE_VALUE_UINT = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Walk the live process snapshot and return true if any process has
    /// the given PID as its direct parent. Used by the cmd polling loop to
    /// detect external commands the user has launched (notepad, git, etc).
    /// </summary>
    private static bool HasChildProcess(int parentPid)
    {
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || (ulong)snap.ToInt64() == INVALID_HANDLE_VALUE_UINT)
            return false;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32FirstW(snap, ref entry)) return false;
            do
            {
                if (entry.th32ParentProcessID == (uint)parentPid)
                    return true;
            } while (Process32NextW(snap, ref entry));
            return false;
        }
        finally
        {
            CloseHandle(snap);
        }
    }

    /// <summary>
    /// Sample the cmd process's CPU time and child-process count every
    /// 500 ms and forward the OR'd result to the tracker as a user-busy
    /// hint. The threshold of 50 ms over 500 ms is well above Windows's
    /// 15.625 ms timer-tick noise floor (idle cmd shows 0–1 ticks per
    /// window) and well below any real workload (`dir /s C:\Windows`
    /// measured at 200–340 ms per window).
    /// </summary>
    private async Task UserBusyDetectorLoop(CancellationToken ct)
    {
        // Milestone 2h: tuning params come from
        // adapter.capabilities.user_busy_detection_params when available,
        // else fall back to the values cmd.yaml documents (500ms / 50ms /
        // children=true).
        var tuning = _adapter?.Capabilities.UserBusyDetectionParams;
        int pollIntervalMs = tuning?.PollIntervalMs > 0 ? tuning.PollIntervalMs : 500;
        var cpuBusyThreshold = TimeSpan.FromMilliseconds(
            tuning?.CpuBusyThresholdMs > 0 ? tuning.CpuBusyThresholdMs : 50);
        bool includeChildren = tuning?.IncludeChildren ?? true;

        Process? proc;
        try { proc = Process.GetProcessById(_pty!.ProcessId); }
        catch { return; }

        long lastCpuTicks = 0;
        bool firstSample = true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(pollIntervalMs, ct); }
                catch (OperationCanceledException) { break; }

                try
                {
                    proc.Refresh();
                    if (proc.HasExited) break;

                    long currentTicks = proc.TotalProcessorTime.Ticks;
                    bool cpuBusy = false;
                    if (!firstSample)
                    {
                        var delta = currentTicks - lastCpuTicks;
                        cpuBusy = delta > cpuBusyThreshold.Ticks;
                    }
                    lastCpuTicks = currentTicks;
                    firstSample = false;

                    bool hasChild = includeChildren && HasChildProcess(_pty.ProcessId);
                    _tracker.SetUserBusyHint(cpuBusy || hasChild);
                }
                catch (Exception ex)
                {
                    Log($"UserBusyDetector tick failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            try { _tracker.SetUserBusyHint(false); } catch { }
            proc.Dispose();
        }
    }

    /// <summary>
    /// ANSI / DEC escape sequences that appear inside the PTY echo but were
    /// never part of the bytes the worker typed to stdin. Unlike
    /// CommandTracker's AnsiRegex (which deliberately keeps SGR so colors
    /// reach the user), this matcher covers SGR too — the walker below needs
    /// to skip every escape sequence to line up with the sentInput bytes.
    /// Matches CSI, OSC (BEL- or ST-terminated), DEC G0/G1 charset selectors,
    /// and DEC cursor-key mode toggles.
    /// </summary>
    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1b\[[0-9;?]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[()][0-9A-B]|\x1b[=>]",
        RegexOptions.Compiled);

    /// <summary>
    /// Derive a \G-anchored prefix matcher from an adapter's prompt regex,
    /// for fuzzy_byte_match's one-shot "swallow the redrawn prompt" step.
    /// Prompt regexes are written for "this IS the current prompt line"
    /// matching (e.g. <c>^\S+ D $</c>) and carry line-anchors at either
    /// end; we strip those anchors and re-anchor the pattern at the current
    /// scan position. Returns null when the adapter has no regex-strategy
    /// prompt or the pattern is empty.
    /// </summary>
    private static Regex? BuildPromptRedrawMatcher(string? promptPattern)
    {
        if (string.IsNullOrEmpty(promptPattern)) return null;
        var p = promptPattern;
        if (p.StartsWith('^')) p = p[1..];
        if (p.EndsWith('$')) p = p[..^1];
        if (p.Length == 0) return null;
        try { return new Regex(@"\G" + p, RegexOptions.Compiled); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Strip the leading bytes that ConPTY echoed back when an AI command
    /// was written to a REPL's PTY input. REPLs without a preexec hook to
    /// fire OSC 633 C can't mark the echo/output boundary, so the worker
    /// recovers it by walking the output and matching the exact bytes that
    /// were written.
    ///
    /// The walker tolerates PTY / REPL artifacts that were never part of
    /// sentInput:
    ///   - CR/LF injected by ConPTY's terminal-width line wrap.
    ///   - ANSI escape sequences injected by the REPL's own syntax
    ///     highlighter (duckdb's linenoise, node's repl, etc.) around the
    ///     echoed input. These appear in the echo but not in sentInput.
    ///
    /// When <paramref name="promptRedrawMatcher"/> is supplied (fuzzy mode),
    /// the walker additionally consumes a leading prompt re-emission once:
    /// linenoise-style REPLs rewrite the prompt on each keystroke, so the
    /// captured window opens with "&lt;prompt&gt;&lt;input&gt;" rather than just
    /// "&lt;input&gt;". The matcher (derived from the adapter's prompt regex)
    /// swallows that prompt prefix so byte-matching can start on sentInput's
    /// first real byte.
    ///
    /// On any unresolvable mismatch the original output is returned unchanged
    /// so the AI gets at-worst the pre-fix ugliness, never lost data.
    /// </summary>
    internal static string StripCmdInputEcho(string output, string sentInput, Regex? promptRedrawMatcher = null)
    {
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(sentInput))
            return output;

        int oi = 0;

        // Fuzzy mode: consume a leading prompt redraw once. Skip the CR and
        // ANSI noise that precedes the prompt bytes, then let the adapter's
        // prompt regex swallow the prompt text itself. Subsequent prompt
        // redraws (if any) are handled by the regular byte-matching loop
        // because the sentInput bytes also re-appear alongside them.
        if (promptRedrawMatcher != null)
        {
            while (oi < output.Length)
            {
                var oc = output[oi];
                if (oc is '\r' or '\n') { oi++; continue; }
                if (oc == '\x1b')
                {
                    var m = AnsiEscapeRegex.Match(output, oi);
                    if (m.Success && m.Index == oi) { oi += m.Length; continue; }
                }
                break;
            }
            var pm = promptRedrawMatcher.Match(output, oi);
            if (pm.Success && pm.Index == oi)
                oi += pm.Length;
        }

        int ci = 0;
        while (ci < sentInput.Length && oi < output.Length)
        {
            var oc = output[oi];
            // ConPTY wraps long input echo at terminal width by injecting
            // CR/LF into the output stream — those bytes were never in the
            // typed command, so skip them while continuing to match.
            if (oc is '\r' or '\n')
            {
                oi++;
                continue;
            }

            // ANSI escape sequences injected by the REPL's syntax
            // highlighter appear in the echo but were never in sentInput
            // (assuming sentInput is plain text, which it always is for
            // AI-driven commands). Skip the full sequence and keep matching.
            // The sentInput[ci] != '\x1b' guard preserves the (vanishingly
            // rare) case of a literal ESC in the typed command — falls
            // through to the byte compare so both sides advance together.
            if (oc == '\x1b' && sentInput[ci] != '\x1b')
            {
                var m = AnsiEscapeRegex.Match(output, oi);
                if (m.Success && m.Index == oi)
                {
                    oi += m.Length;
                    continue;
                }
                return output;
            }

            if (oc != sentInput[ci])
                return output;

            oi++;
            ci++;
        }

        if (ci < sentInput.Length)
            return output;

        while (oi < output.Length && output[oi] is '\r' or '\n')
            oi++;

        return output[oi..];
    }

    private async Task WriteToPty(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _writer!.WriteAsync(bytes, ct);
        await _writer.FlushAsync(ct);
    }

    private readonly ManualResetEventSlim _readyEvent = new(false);

    private async Task WaitForReady(CancellationToken ct)
    {
        // Wait for the first PromptStart signal from ReadOutputLoop, or
        // the shell process dying. No timeout — see the call site in
        // RunAsync for the reasoning.
        var readyTask = Task.Run(() => _readyEvent.Wait(ct), ct);
        var winner = await Task.WhenAny(readyTask, _shellExitedTcs.Task).ConfigureAwait(false);
        if (winner == _shellExitedTcs.Task)
        {
            Log("Shell process exited before first prompt; aborting startup");
            throw new InvalidOperationException("Shell process exited during startup");
        }
        // Surface any exception from readyTask (e.g. OperationCanceledException
        // if the worker ct cancelled while we were waiting).
        await readyTask.ConfigureAwait(false);
    }

    // --- User input forwarding ---

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleW(IntPtr hConsoleInput, char[] lpBuffer, uint nNumberOfCharsToRead, out uint lpNumberOfCharsRead, IntPtr pInputControl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleOutputCharacterW(IntPtr hConsoleOutput, char[] lpCharacter, uint nLength, SCREEN_COORD dwReadCoord, out uint lpNumberOfCharsRead);

    [StructLayout(LayoutKind.Sequential)]
    private struct SCREEN_COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public SCREEN_COORD dwSize;
        public SCREEN_COORD dwCursorPosition;
        public short wAttributes;
        public SMALL_RECT srWindow;
        public SCREEN_COORD dwMaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left, Top, Right, Bottom;
    }

    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    // VT input: arrow keys become \x1b[A etc., no line buffering, no echo
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

    /// <summary>
    /// Read the visible portion of the worker's console screen buffer
    /// via Win32 API. Returns the screen content as a multi-line string
    /// with trailing whitespace trimmed per row and trailing empty rows
    /// dropped. Returns null if the API call fails or on non-Windows
    /// platforms (caller should fall back to VT-lite ring snapshot).
    ///
    /// This is the primary peek mechanism on Windows: it reads exactly
    /// what the user sees in the terminal window, with no VT parsing
    /// needed. PSReadLine prediction artifacts, cursor-dance noise,
    /// and ConPTY redraw patterns are all invisible because the
    /// console host has already rendered them into the final cell grid.
    /// </summary>
    private static string? ReadConsoleScreenText()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var hOut = GetStdHandle(STD_OUTPUT_HANDLE);
            if (hOut == IntPtr.Zero || hOut == (IntPtr)(-1)) return null;
            if (!GetConsoleScreenBufferInfo(hOut, out var info)) return null;

            // Read the visible window portion of the buffer.
            int width = info.srWindow.Right - info.srWindow.Left + 1;
            int height = info.srWindow.Bottom - info.srWindow.Top + 1;
            if (width <= 0 || height <= 0) return null;

            var sb = new StringBuilder();
            var rowBuf = new char[width];

            // Track last non-blank row to trim trailing empties.
            int lastNonBlankRow = -1;
            var rows = new List<string>(height);

            for (int row = 0; row < height; row++)
            {
                var coord = new SCREEN_COORD
                {
                    X = (short)(info.srWindow.Left),
                    Y = (short)(info.srWindow.Top + row)
                };
                if (!ReadConsoleOutputCharacterW(hOut, rowBuf, (uint)width, coord, out var charsRead))
                    return null;

                // Trim trailing spaces for this row.
                int end = (int)charsRead - 1;
                while (end >= 0 && rowBuf[end] == ' ') end--;

                var line = end >= 0 ? new string(rowBuf, 0, end + 1) : "";
                rows.Add(line);
                if (line.Length > 0) lastNonBlankRow = row;
            }

            if (lastNonBlankRow < 0) return "";

            for (int r = 0; r <= lastNonBlankRow; r++)
            {
                if (r > 0) sb.Append('\n');
                sb.Append(rows[r]);
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Enable VT escape sequence processing on the worker's stdout console
    /// so cursor movement, clear-to-EOL, and color codes from the shell
    /// are interpreted instead of written as literal characters.
    /// </summary>
    private static void EnableVirtualTerminalOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var hStdOut = GetStdHandle(STD_OUTPUT_HANDLE);
        if (hStdOut == IntPtr.Zero || hStdOut == (IntPtr)(-1)) return;
        if (!GetConsoleMode(hStdOut, out var mode)) return;
        SetConsoleMode(hStdOut, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }

    /// <summary>
    /// Detect whether the bash executable is WSL bash (vs Git Bash / MSYS2).
    /// WSL bash: C:\Windows\System32\bash.exe or WindowsApps\bash.exe
    /// MSYS2/Git Bash: C:\Program Files\Git\usr\bin\bash.exe etc.
    /// </summary>
    private static bool IsWslBash(string shellPath)
    {
        // If user specified a full path, check it directly
        if (Path.IsPathRooted(shellPath))
        {
            return shellPath.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase) ||
                   shellPath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
        }

        // Resolve "bash" via PATH — check which one we'd get
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "where",
                Arguments = shellPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var firstLine = proc?.StandardOutput.ReadLine();
            proc?.WaitForExit();
            if (firstLine != null)
            {
                return firstLine.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase) ||
                       firstLine.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) { Log($"IsWslBash detection failed: {ex.Message}"); }
        return false;
    }

    /// <summary>
    /// Poll the visible console window size and notify ConPTY when it changes.
    /// This keeps the shell's COLUMNS/LINES in sync with the actual window.
    /// </summary>
    private async Task ResizeMonitorLoop(CancellationToken ct)
    {
        int lastCols = 0, lastRows = 0;
        try { lastCols = Console.WindowWidth; lastRows = Console.WindowHeight; } catch { }

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);
            try
            {
                int rawCols = Console.WindowWidth;
                int rows = Console.WindowHeight;
                // Match the visible ConHost width so Write-Progress and
                // other \r-based in-place repainting behave correctly.
                // Matches the spawn-time policy at the top of this file —
                // keep the two branches in sync.
                int cols = rawCols;
                if (rawCols > 0 && rows > 0 && (cols != lastCols || rows != lastRows))
                {
                    lastCols = cols;
                    lastRows = rows;
                    _pty?.Resize(cols, rows);
                    _tracker.SetTerminalSize(cols, rows);
                    // Reset VtLiteState on resize — its grid is fixed-size
                    // and existing cursor coordinates are invalid in the new
                    // geometry. Shells typically repaint after resize anyway,
                    // so the next chunk re-establishes correct state.
                    _vtState = new VtLiteState(rows, cols);
                    Log($"Resized PTY to {cols}x{rows}");
                }
            }
            catch { /* best-effort: Console.WindowWidth/Height throw on
                       redirected streams, _pty.Resize can fail if the
                       handle closed mid-tick. Next poll re-tries with
                       fresh state. */ }
        }
    }

    /// <summary>
    /// Forward user keyboard input from the worker's visible console to the PTY input pipe.
    /// When AI is executing a command (CommandTracker.Busy), input is held until the command completes.
    /// Dispatches to the Windows or Unix implementation — stdin acquisition and raw-mode handling
    /// differ enough between ConPTY + Win32 console and forkpty + termios that a single code path
    /// would be unreadable.
    /// </summary>
    private Task InputForwardLoop(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return InputForwardLoopWindows(ct);
        return InputForwardLoopUnix(ct);
    }

    private Task InputForwardLoopWindows(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;

        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                var hStdIn = GetStdHandle(STD_INPUT_HANDLE);
                if (hStdIn == IntPtr.Zero || hStdIn == (IntPtr)(-1)) return;

                // Switch stdin to VT input mode:
                //   - ENABLE_VIRTUAL_TERMINAL_INPUT: special keys → VT sequences
                //   - No ENABLE_LINE_INPUT: character-at-a-time (not line-buffered)
                //   - No ENABLE_ECHO_INPUT: ConPTY handles echo via its output pipe
                //   - No ENABLE_PROCESSED_INPUT: Ctrl+C → \x03 (not signal)
                SetConsoleMode(hStdIn, ENABLE_VIRTUAL_TERMINAL_INPUT);

                var shellName = _shellFamily;
                // pwsh and cmd.exe understand win32-input-mode natively; only Unix shells need translation
                bool needsTranslation = !_isPwshFamily && shellName is not "cmd";

                var charBuf = new char[256];
                var pending = needsTranslation ? new StringBuilder() : null;
                while (!ct.IsCancellationRequested)
                {
                    // ReadConsoleW reads Unicode (UTF-16) — handles CJK characters correctly
                    if (!ReadConsoleW(hStdIn, charBuf, (uint)charBuf.Length, out var charsRead, IntPtr.Zero) || charsRead == 0)
                        break;

                    try
                    {
                        byte[] utf8;
                        if (needsTranslation)
                        {
                            // Decode win32-input-mode rich key events into plain text/VT sequences.
                            // Bash/zsh readline does not understand `ESC [ Vk;Sc;Uc;Kd;Cs;Rc _` sequences.
                            pending!.Append(charBuf, 0, (int)charsRead);
                            var translated = TranslateWin32InputMode(pending);
                            if (translated.Length == 0) continue;
                            utf8 = Encoding.UTF8.GetBytes(translated);
                        }
                        else
                        {
                            // pwsh/powershell: PSReadLine understands win32-input-mode natively.
                            // Forward the raw sequences without translation.
                            utf8 = Encoding.UTF8.GetBytes(charBuf, 0, (int)charsRead);
                        }

                        if (_holdUserInput)
                        {
                            // AI command in flight — hold user keystrokes for
                            // replay after the command completes. Ctrl+C (0x03)
                            // passes through immediately so the user can still
                            // interrupt a stuck command.
                            bool isCtrlC = utf8.Length == 1 && utf8[0] == 0x03;
                            if (isCtrlC)
                            {
                                _pty!.InputStream.Write(utf8, 0, utf8.Length);
                                _pty.InputStream.Flush();
                            }
                            else
                            {
                                _heldUserInput.Enqueue((byte[])utf8.Clone());
                            }
                        }
                        else
                        {
                            _pty!.InputStream.Write(utf8, 0, utf8.Length);
                            _pty.InputStream.Flush();
                        }
                    }
                    catch (IOException) { break; }
                }
            }
            catch (Exception ex) { Log($"InputForwardLoop error: {ex.Message}"); }
            finally { tcs.TrySetResult(); }
        });
        thread.IsBackground = true;
        thread.Name = "Console-Input";
        thread.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Unix equivalent of InputForwardLoopWindows: read keystrokes the human typed
    /// into the worker's visible terminal (our stdin fd 0), and forward them to the
    /// forkpty master so the hosted shell receives them.
    ///
    /// Raw mode is critical — without it the kernel tty driver cooks input
    /// (line-buffered until Enter, local echo, ^C → SIGINT), which would prevent
    /// bash's readline from seeing arrow keys / tab / Ctrl-shortcuts and would
    /// double-echo every keystroke (once locally, once from readline). We
    /// snapshot the original termios, cfmakeraw() a copy, install it, and
    /// restore the snapshot on worker exit.
    ///
    /// If stdin isn't a tty (proxy mode with a piped stdin, --test harness,
    /// smoke tests), we skip raw mode and the loop falls through immediately;
    /// the loop is harmless to start even when no human will ever type.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private Task InputForwardLoopUnix(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            IntPtr saved = IntPtr.Zero;
            IntPtr modified = IntPtr.Zero;
            bool rawInstalled = false;
            try
            {
                if (UnixPty.IsATty(0) != 1)
                {
                    // Proxy mode / tests: stdin is a pipe, no user typing to forward.
                    return;
                }

                saved = Marshal.AllocHGlobal(UnixPty.TermiosBufSize);
                modified = Marshal.AllocHGlobal(UnixPty.TermiosBufSize);
                if (UnixPty.TcGetAttr(0, saved) != 0)
                {
                    Log($"InputForwardLoopUnix: tcgetattr failed errno={Marshal.GetLastPInvokeError()}");
                    return;
                }
                unsafe
                {
                    System.Buffer.MemoryCopy((void*)saved, (void*)modified, UnixPty.TermiosBufSize, UnixPty.TermiosBufSize);
                }
                UnixPty.CfMakeRaw(modified);
                if (UnixPty.TcSetAttr(0, UnixPty.TCSANOW, modified) != 0)
                {
                    Log($"InputForwardLoopUnix: tcsetattr raw failed errno={Marshal.GetLastPInvokeError()}");
                    return;
                }
                rawInstalled = true;

                // Belt-and-braces termios restore: the finally block handles the
                // normal path, this catches an out-of-band process exit (signal,
                // crash) that skips managed cleanup.
                var savedSnapshot = saved;
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    try { UnixPty.TcSetAttr(0, UnixPty.TCSANOW, savedSnapshot); } catch { }
                };

                var buf = new byte[256];
                while (!ct.IsCancellationRequested)
                {
                    int n = UnixPty.ReadFd(0, buf, buf.Length);
                    if (n <= 0) break;

                    try
                    {
                        if (_holdUserInput)
                        {
                            // Mirror the Windows hold-gate behaviour: Ctrl+C
                            // (0x03) always passes through so the human can
                            // interrupt a stuck AI command; other keystrokes
                            // are queued for replay once the AI command
                            // finishes and the gate is released.
                            bool isCtrlC = n == 1 && buf[0] == 0x03;
                            if (isCtrlC)
                            {
                                _pty!.InputStream.Write(buf, 0, n);
                                _pty.InputStream.Flush();
                            }
                            else
                            {
                                var slice = new byte[n];
                                Array.Copy(buf, slice, n);
                                _heldUserInput.Enqueue(slice);
                            }
                        }
                        else
                        {
                            _pty!.InputStream.Write(buf, 0, n);
                            _pty.InputStream.Flush();
                        }
                    }
                    catch (IOException) { break; }
                }
            }
            catch (Exception ex) { Log($"InputForwardLoopUnix error: {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                if (rawInstalled && saved != IntPtr.Zero)
                {
                    try { UnixPty.TcSetAttr(0, UnixPty.TCSANOW, saved); } catch { }
                }
                if (saved != IntPtr.Zero) Marshal.FreeHGlobal(saved);
                if (modified != IntPtr.Zero) Marshal.FreeHGlobal(modified);
                tcs.TrySetResult();
            }
        });
        thread.IsBackground = true;
        thread.Name = "Unix-Input";
        thread.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Translate win32-input-mode escape sequences (`ESC [ Vk;Sc;Uc;Kd;Cs;Rc _`) into
    /// plain text or standard VT escape sequences that bash/zsh readline can understand.
    ///
    /// The pending buffer is consumed up to the last complete sequence; any partial
    /// trailing sequence is preserved for the next read.
    /// </summary>
    private static string TranslateWin32InputMode(StringBuilder pending)
    {
        var output = new StringBuilder();
        int i = 0;
        var input = pending.ToString();

        while (i < input.Length)
        {
            char c = input[i];

            // Detect ESC [ ... _  (win32-input-mode sequence)
            if (c == '\x1b' && i + 1 < input.Length && input[i + 1] == '[')
            {
                // Find terminator '_'
                int end = input.IndexOf('_', i + 2);
                if (end < 0)
                {
                    // Incomplete sequence — keep in pending
                    break;
                }

                // Parse fields between ESC [ and _
                var payload = input.Substring(i + 2, end - i - 2);
                var fields = payload.Split(';');
                if (fields.Length == 6 &&
                    int.TryParse(fields[0], out var vk) &&
                    int.TryParse(fields[2], out var uc) &&
                    int.TryParse(fields[3], out var kd) &&
                    int.TryParse(fields[4], out var cs) &&
                    int.TryParse(fields[5], out var rc))
                {
                    // Skip key-up events
                    if (kd == 1)
                    {
                        var seq = MapKeyToVt(vk, uc, cs);
                        for (int r = 0; r < Math.Max(1, rc); r++)
                            output.Append(seq);
                    }
                    i = end + 1;
                    continue;
                }

                // Not a recognized win32-input-mode sequence — pass through as-is
                output.Append(input, i, end - i + 1);
                i = end + 1;
                continue;
            }

            // Plain character — pass through
            output.Append(c);
            i++;
        }

        // Consume processed portion from pending
        pending.Remove(0, i);
        return output.ToString();
    }

    /// <summary>
    /// Map a Windows virtual key + Unicode char + control state to the bytes/sequence
    /// that bash/zsh readline expects.
    /// </summary>
    private static string MapKeyToVt(int vk, int uc, int controlState)
    {
        const int LEFT_CTRL = 0x0008;
        const int RIGHT_CTRL = 0x0004;
        const int LEFT_ALT = 0x0002;
        const int RIGHT_ALT = 0x0001;
        bool ctrl = (controlState & (LEFT_CTRL | RIGHT_CTRL)) != 0;
        bool alt = (controlState & (LEFT_ALT | RIGHT_ALT)) != 0;

        // Special keys (Uc == 0)
        if (uc == 0)
        {
            return vk switch
            {
                0x25 => "\x1b[D", // Left
                0x26 => "\x1b[A", // Up
                0x27 => "\x1b[C", // Right
                0x28 => "\x1b[B", // Down
                0x24 => "\x1b[H", // Home
                0x23 => "\x1b[F", // End
                0x21 => "\x1b[5~", // PgUp
                0x22 => "\x1b[6~", // PgDn
                0x2D => "\x1b[2~", // Insert
                0x2E => "\x1b[3~", // Delete
                0x70 => "\x1bOP",  // F1
                0x71 => "\x1bOQ",  // F2
                0x72 => "\x1bOR",  // F3
                0x73 => "\x1bOS",  // F4
                0x74 => "\x1b[15~", // F5
                0x75 => "\x1b[17~", // F6
                0x76 => "\x1b[18~", // F7
                0x77 => "\x1b[19~", // F8
                0x78 => "\x1b[20~", // F9
                0x79 => "\x1b[21~", // F10
                0x7A => "\x1b[23~", // F11
                0x7B => "\x1b[24~", // F12
                _ => "",
            };
        }

        // Backspace (BS or DEL): bash readline expects \x7f (DEL) for the Backspace key
        if (vk == 0x08)
            return "\x7f";

        // Tab
        if (vk == 0x09)
            return "\t";

        // Enter
        if (vk == 0x0D)
            return "\r";

        // Escape
        if (vk == 0x1B)
            return "\x1b";

        // Alt + char → ESC + char
        if (alt && uc >= 0x20 && uc < 0x7f)
            return "\x1b" + (char)uc;

        // Plain Unicode character
        return char.ConvertFromUtf32(uc);
    }

    // --- PTY output reading ---

    /// <summary>
    /// Forward a slice of OSC-stripped PTY output to the worker's visible
    /// console so the human user sees what the AI is doing. Rewrites any
    /// OSC 0 (set window title) sequences on the way so the title stays as
    /// ripple's "#PID Name" tag instead of being overwritten by whatever
    /// the shell's prompt decided to set.
    /// </summary>
    private void MirrorToVisible(string text)
    {
        if (!_mirrorVisible || text.Length == 0) return;
        try
        {
            var cleanedOutput = ReplaceOscTitle(text, _desiredTitle, ref _oscTitlePending);
            var outBytes = Encoding.UTF8.GetBytes(cleanedOutput);
            _stdoutStream ??= Console.OpenStandardOutput();
            _stdoutStream.Write(outBytes, 0, outBytes.Length);
            _stdoutStream.Flush();
        }
        catch { /* best-effort: visible-console mirroring must never
                   take down the worker. Redirected stdout, closed
                   console window, encoding surrogates — any failure
                   here degrades the human view but the MCP stdio
                   channel (which the AI reads) is untouched. */ }
    }

    /// <summary>
    /// Scan recent PTY output for in-band terminal queries from the hosted
    /// shell and inject synthetic responses back into the PTY input. On Unix
    /// the inner forkpty has no real terminal emulator behind it — ripple is
    /// the middleman relaying bytes between the outer xfce4-terminal /
    /// Terminal.app and the shell — so line-editor libraries that sync their
    /// internal state by querying the terminal (PSReadLine fires DSR on
    /// startup, various REPLs probe DA1 / DA2) never see a reply and can
    /// hang indefinitely waiting for one. Answering here keeps ripple a
    /// pure relay on Windows (the real ConPTY handles these queries itself,
    /// so the loop below no-ops unless the specific sequence actually
    /// appears) while unblocking the Unix path.
    ///
    /// Returns the text with any handled query sequences stripped so the
    /// downstream mirror (to the outer Terminal / xfce4-terminal) and
    /// OSC/CSI parser never see them. Stripping is critical: if a query
    /// leaks into the mirror, the outer terminal ALSO answers it, and
    /// the reply races back through the stdin relay into the shell — the
    /// shell's line editor then treats the duplicate reply as typed
    /// characters and prints garbage like "R24" or "21R" into the command
    /// buffer. Answering on ripple's side and swallowing the query is the
    /// single-authoritative path.
    ///
    /// Currently handled:
    ///   • CSI 6 n (DSR — Device Status Report: cursor position). Answered
    ///     from the live <see cref="VtLiteState"/> that ReadOutputLoop
    ///     advances on every PTY chunk, so the reply reflects the real
    ///     cursor position after everything the shell has printed up to
    ///     the query. During the pre-first-chunk window (cursor still at
    ///     origin) falls back to a static "near the bottom" row plus
    ///     EstimateCursorCol so PSReadLine's startup-sync DSR round-trip
    ///     lands somewhere plausible. This path is Unix-only in practice:
    ///     ConPTY on Windows intercepts DSR before ripple ever sees it.
    ///
    /// Future queries to consider: CSI c / CSI &gt; c (DA1 / DA2 device
    /// attribute probes), CSI 14 t / CSI 18 t (window pixel / cell size),
    /// OSC 10 / 11 ? (foreground / background colour query). Added on
    /// demand when a new adapter needs them.
    /// </summary>
    private string AnswerAndStripTerminalQueries(string text)
        => AnswerAndStripDsr(text, ref _pendingDsrPrefix, WriteDsrReply);

    private void WriteDsrReply()
    {
        if (_writer is null) return;
        try
        {
            // Live virtual-terminal tracking: ReadOutputLoop feeds every
            // PTY chunk into `_vtState` before AnswerAndStripDsr is
            // called, so its (Row, Col) is authoritative at DSR-reply
            // time. DSR is 1-indexed; VtLiteState is 0-indexed
            // internally. Fallback to the legacy static-row +
            // EstimateCursorCol heuristic only during the brief window
            // before the first chunk arrives (cursor still at origin,
            // fallback is closer to what the shell expects during its
            // own startup sync).
            int row, col;
            if (_vtState.Row == 0 && _vtState.Col == 0)
            {
                row = 24;
                try { if (Console.WindowHeight > 1) row = Console.WindowHeight; } catch { }
                col = EstimateCursorCol();
            }
            else
            {
                row = _vtState.Row + 1;
                col = _vtState.Col + 1;
            }
            var reply = System.Text.Encoding.UTF8.GetBytes($"\x1b[{row};{col}R");
            _writer.Write(reply, 0, reply.Length);
            _writer.Flush();
        }
        catch (IOException) { /* PTY closed — worker is tearing down */ }
    }

    /// <summary>
    /// Pure DSR (\x1b[6n) scan + strip, boundary-safe across PTY chunks.
    /// Each complete DSR fires <paramref name="onDsrDetected"/> exactly
    /// once and is stripped from the returned text. A trailing partial
    /// DSR prefix (ESC, ESC[, or ESC[6) is moved into
    /// <paramref name="pendingPrefix"/> so the next call completes it —
    /// without this, a 4-byte DSR split across two PTY reads would
    /// leak downstream (into OscParser, the mirror, and the shell's
    /// output stream a consumer sees) and never produce a reply.
    /// The partial prefix is also stripped from the returned text so
    /// the same downstream consumers never see the half-formed query.
    /// Mirrors the shape of <see cref="ReplaceOscTitle"/> for the same
    /// kind of chunk-boundary problem.
    /// </summary>
    internal static string AnswerAndStripDsr(
        string input,
        ref string pendingPrefix,
        Action? onDsrDetected)
    {
        const string Dsr = "\x1b[6n";
        const int DsrLen = 4;

        string combined;
        if (pendingPrefix.Length > 0)
        {
            combined = pendingPrefix + input;
            pendingPrefix = "";
        }
        else
        {
            // Hot path: no pending partial and no ESC in input — zero
            // allocation, forward untouched. PTY chunks without escape
            // bytes are the common case on bulk stdout.
            if (input.IndexOf('\x1b') < 0) return input;
            combined = input;
        }

        var sb = new StringBuilder(combined.Length);
        int i = 0;
        while (true)
        {
            int next = combined.IndexOf(Dsr, i, StringComparison.Ordinal);
            if (next >= 0)
            {
                sb.Append(combined, i, next - i);
                onDsrDetected?.Invoke();
                i = next + DsrLen;
                continue;
            }

            // No more complete DSRs. Check whether the tail is a
            // partial DSR prefix ("\x1b", "\x1b[", or "\x1b[6") — in
            // which case buffer it for the next chunk and don't emit
            // it downstream.
            int partialStart = -1;
            int tailMin = combined.Length - (DsrLen - 1);
            if (tailMin < i) tailMin = i;
            for (int j = tailMin; j < combined.Length; j++)
            {
                if (combined[j] != '\x1b') continue;
                int remaining = combined.Length - j;
                bool matches = true;
                for (int k = 0; k < remaining; k++)
                {
                    if (combined[j + k] != Dsr[k]) { matches = false; break; }
                }
                if (matches) { partialStart = j; break; }
            }
            if (partialStart >= 0)
            {
                sb.Append(combined, i, partialStart - i);
                pendingPrefix = combined.Substring(partialStart);
            }
            else
            {
                sb.Append(combined, i, combined.Length - i);
            }
            break;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Best-effort cursor column for DSR replies when the outer terminal's
    /// real reply isn't available through the relay. Used only by
    /// <see cref="AnswerAndStripTerminalQueries"/> on Unix.
    ///
    /// PSReadLine fires DSR right after the shell writes its prompt, so the
    /// honest answer is "prompt end + 1". We compute that from
    /// adapter-specific knowledge of the prompt format rather than parsing
    /// the output stream (which would require a mini terminal emulator
    /// that's out of scope for the Phase 2 relay):
    ///
    ///   • pwsh / powershell — default prompt is "PS &lt;cwd&gt;&gt; ",
    ///     so column = 3 ("PS ") + cwd.Length + 2 ("&gt; ") + 1 (1-indexed
    ///     cursor position after the space). Custom $PROFILE prompts
    ///     break this approximation; ok as the common case.
    ///
    ///   • everyone else — we don't have a portable way to guess the
    ///     prompt length (bash PS1 can be anything), so fall back to
    ///     column 1. New keystrokes still land at the correct on-screen
    ///     column because readline emits a `\r` + re-render cycle on
    ///     input. History recall via up-arrow picks the wrong column
    ///     in this fallback and renders into the prompt area — a known
    ///     cosmetic limitation until proper virtual terminal tracking
    ///     is added.
    /// </summary>
    /// <summary>
    /// Best-effort cursor column for DSR replies when the outer terminal's
    /// real reply isn't available through the relay. Used only by
    /// <see cref="AnswerAndStripTerminalQueries"/> on Unix.
    ///
    /// A byte-level virtual-terminal tracker was tried and regressed worse
    /// than a static guess: PSReadLine renders prompts with SGR colour
    /// escapes whose widths don't match the counted byte advance, and
    /// PSReadLine carries its own cursor model — when our estimate and
    /// its model diverge even slightly, the two compound into bigger
    /// visual drift than either alone. The current compromise:
    ///
    ///   • pwsh / powershell — compute column from the default prompt
    ///     format "PS &lt;cwd&gt;&gt; ". Column = 3 ("PS ") + cwd.Length
    ///     + 2 ("&gt; ") + 1 (1-indexed cursor after the space). Accurate
    ///     for the out-of-the-box prompt, which matches what PSReadLine
    ///     expects since it built its own model from the same string.
    ///     Custom $PROFILE prompts defeat this approximation.
    ///
    ///   • everyone else — fall back to column 1. New keystrokes still
    ///     land at the correct on-screen column because readline emits
    ///     a \r + re-render cycle on input, so the initial column
    ///     estimate drops out after the first render. History recall
    ///     (up-arrow) uses absolute positioning tied to the DSR reply,
    ///     so the column-1 fallback draws the recalled command into
    ///     the prompt area — a known cosmetic gap until a full virtual
    ///     terminal layer lands.
    /// </summary>
    private int EstimateCursorCol()
    {
        if (_isPwshFamily)
        {
            var cwd = _tracker.LastKnownCwd ?? _cwd ?? "/";
            // "PS " + cwd + "> " + cursor-after-space = 3 + len + 2 + 1
            return 3 + cwd.Length + 2 + 1;
        }
        if (_shellFamily == "bash" || _shellFamily == "zsh")
        {
            // Debian / Ubuntu / Fedora / Arch default PS1 is
            //   "\u@\h:\w\$ "  →  "user@host:path$ ". Home directory and
            // prefixes of home get "~"-substituted (the \w expansion).
            // Length = user + "@" + host + ":" + displayPath + "$ "
            //        = user + 1 + host + 1 + displayPath + 2
            // Plus 1 for 1-indexed cursor position after the space.
            var user = Environment.GetEnvironmentVariable("USER") ?? Environment.UserName;
            var host = Environment.MachineName;
            var home = Environment.GetEnvironmentVariable("HOME") ?? "";
            var cwd = _tracker.LastKnownCwd ?? _cwd ?? "/";
            string displayCwd = cwd;
            if (!string.IsNullOrEmpty(home))
            {
                if (cwd == home) displayCwd = "~";
                else if (cwd.StartsWith(home + "/")) displayCwd = "~" + cwd.Substring(home.Length);
            }
            return user.Length + 1 + host.Length + 1 + displayCwd.Length + 2 + 1;
        }
        return 1;
    }

    /// <summary>
    /// Wait for the child shell process to exit, then signal the main loop.
    /// Needed because ConPTY keeps the output pipe open indefinitely even
    /// after the child process dies, so ReadOutputLoop's blocking Read is
    /// not a reliable shell-death signal. We watch the Windows process
    /// handle directly via Process.WaitForExitAsync.
    /// </summary>
    private async Task WaitForShellExitAsync(CancellationToken ct)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(_pty!.ProcessId);
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        catch (ArgumentException) { /* already gone */ }
        catch (Exception ex) { Log($"WaitForShellExit: {ex.GetType().Name}: {ex.Message}"); }

        Log("Shell process exited");
        _tracker.AbortPending();
        _shellExitedTcs.TrySetResult();
    }

    private Task ReadOutputLoop(CancellationToken ct)
    {
        // Dedicated thread with synchronous ReadFile in a tight loop.
        // This pattern matches the ConPTY minimal test where ReadFile worked correctly.
        // Stream.ReadAsync/Task.Run wrappers don't reliably work with ConPTY pipe handles.
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            var stream = _pty!.OutputStream;
            var buffer = new byte[4096];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;

                    var text = Encoding.UTF8.GetString(buffer, 0, read);
                    text = AnswerAndStripTerminalQueries(text);
                    if (_tracker.Busy) Log($"RAW: {EscapeForLog(text)}");
                    var result = _parser.Parse(text);

                    // For regex-strategy adapters: scan the cleaned chunk
                    // for prompt boundaries and inject synthetic OSC events
                    // (CommandFinished + PromptStart, mirroring what the
                    // python adapter emits for real on each prompt). The
                    // synthetic events are merged with any real OSC events
                    // in TextOffset order so the tracker downstream sees
                    // one coherent stream regardless of which strategy
                    // fired.
                    // Re-arm the continuation-escape flag between AI
                    // commands. Using RunningCommand (which mirrors
                    // the tracker's _isAiCommand) matches the guard
                    // condition below, so reset and escape share one
                    // semantic of "there is an AI command in flight".
                    // If a primary match resolves the current command
                    // later in this same chunk, the event is processed
                    // downstream and RunningCommand only goes null
                    // from the next chunk — soon enough, since no
                    // legitimate continuation match can arrive between
                    // resolve and the next RegisterCommand.
                    if (_tracker.RunningCommand == null) _regexContinuationEscapeSent = false;

                    // Continuation scan runs BEFORE the primary scan so
                    // the escape is issued as early in the chunk as
                    // possible. The continuation detector's output is
                    // intentionally *not* merged into the event stream:
                    // Lua's syntax error + return to `> ` that follows
                    // the escape produces a real primary match on a
                    // later chunk, which is what we want the tracker
                    // to see.
                    if (_regexContinuationDetector != null
                        && _regexContinuationEscape != null
                        && _tracker.RunningCommand != null
                        && !_regexContinuationEscapeSent)
                    {
                        var contMatches = _regexContinuationDetector.Scan(result.Cleaned);
                        if (contMatches.Count > 0)
                        {
                            Log($"Continuation prompt detected during AI command; writing escape {EscapeForLog(_regexContinuationEscape)}");
                            _regexContinuationEscapeSent = true;
                            // Sync write — we're on the dedicated read
                            // thread, not an async context, so reuse the
                            // same pattern other sync writes in this
                            // file use (InputStream.Write + Flush).
                            try
                            {
                                var escBytes = Encoding.UTF8.GetBytes(_regexContinuationEscape);
                                _pty!.InputStream.Write(escBytes, 0, escBytes.Length);
                                _pty.InputStream.Flush();
                            }
                            catch (Exception ex)
                            {
                                Log($"Failed to write continuation escape: {ex.Message}");
                            }
                        }
                    }

                    if (_regexPromptDetector != null)
                    {
                        // RegexPromptDetector is CSI-aware: it strips
                        // CSI escapes from result.Cleaned internally
                        // before running the adapter's prompt regex,
                        // so the offsets come back in original-byte
                        // coordinates that line up with the same
                        // cleaned text we feed to the tracker below.
                        //
                        // Use Start (the first visible char of the
                        // prompt) for both synthetic events: that puts
                        // the prompt cap RIGHT BEFORE the prompt, so
                        // the regex-prompt REPLs (node, pdb, perldb,
                        // python) don't leak the next prompt's chars
                        // into the AI-facing output.
                        var promptMatches = _regexPromptDetector.Scan(result.Cleaned);
                        if (promptMatches.Count > 0)
                        {
                            var merged = new List<OscParser.OscEvent>(result.Events);
                            foreach (var match in promptMatches)
                            {
                                if (_regexFirstPromptSeen)
                                {
                                    merged.Add(new OscParser.OscEvent(
                                        OscParser.OscEventType.CommandFinished,
                                        ExitCode: 0, TextOffset: match.Start));
                                }
                                merged.Add(new OscParser.OscEvent(
                                    OscParser.OscEventType.PromptStart,
                                    TextOffset: match.Start));
                                _regexFirstPromptSeen = true;
                            }
                            merged.Sort((a, b) => a.TextOffset.CompareTo(b.TextOffset));
                            result = result with { Events = merged };
                        }
                    }

                    // Interleave _vtState.Feed, FeedOutput, and HandleEvent
                    // in source order using each event's TextOffset (position
                    // in Cleaned where the event fired). Feeding the live
                    // VT-100 interpreter in slices aligned with OSC event
                    // offsets — instead of the whole chunk up front — means
                    // Snapshot() at CommandExecuted reflects exactly the
                    // screen state as of the OSC C byte, not end-of-chunk
                    // state that would include downstream command-output
                    // bytes. Without the alignment, a single chunk that
                    // straddled OSC C (encoded_scriptblock multi-line
                    // delivery with the payload + first output lines in one
                    // read) would snapshot a baseline containing cells from
                    // the very command output about to be replayed into
                    // CommandOutputRenderer, and the renderer's cursor would
                    // start at the post-output position instead of the
                    // blank row the AI expects command output to land on.
                    // Feeding cleaned text (OSC 633 stripped) to _vtState is
                    // equivalent to feeding raw for VT interpretation — OSC
                    // sequences are dropped by ApplyOsc — and DSR queries
                    // are already stripped upstream. The tracker knows
                    // "_output was N bytes long when OSC C arrived" so it
                    // can still slice out just the region between OSC C and
                    // OSC D when producing the command result.
                    int lastOffset = 0;
                    foreach (var evt in result.Events)
                    {
                        if (evt.TextOffset > lastOffset)
                        {
                            var slice = result.Cleaned.Substring(lastOffset, evt.TextOffset - lastOffset);
                            _vtState.Feed(slice.AsSpan());
                            _tracker.FeedOutput(slice);
                            _outputLength += slice.Length;
                            MirrorToVisible(slice);
                        }
                        lastOffset = evt.TextOffset;

                        if (!_ready && evt.Type == OscParser.OscEventType.PromptStart)
                        {
                            _ready = true;
                            _readyEvent.Set();
                        }
                        // Snapshot the session-wide _vtState BEFORE the
                        // tracker handles OSC C — the tracker's own
                        // VtLiteState resets at OSC C, which would erase
                        // the screen state we want to ship to the
                        // CommandOutputRenderer as its baseline. The
                        // worker's _vtState accumulates across commands
                        // and matches what ConPTY's screen buffer
                        // contains, so its snapshot is what makes the
                        // post-alt-screen repaint detector work.
                        if (evt.Type == OscParser.OscEventType.CommandExecuted)
                            _tracker.SetCapturedBaseline(_vtState.Snapshot());
                        _tracker.HandleEvent(evt);
                        if (_ready && evt.Type == OscParser.OscEventType.CommandInputStart)
                            _mirrorVisible = true;
                    }

                    // Any text after the last event in this chunk.
                    if (lastOffset < result.Cleaned.Length)
                    {
                        var tail = result.Cleaned.Substring(lastOffset);
                        _vtState.Feed(tail.AsSpan());
                        _tracker.FeedOutput(tail);
                        _outputLength += tail.Length;
                        MirrorToVisible(tail);
                    }
                }
            }
            catch (IOException) { }
            catch (Exception ex) { Log($"ReadOutputLoop error: {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                // Signal the main loop that the child shell process is gone
                // (Read returned 0 or threw). The main loop wakes up and
                // tears the worker down so the pipe closes promptly and the
                // proxy can surface a "Previous console died" to the AI.
                Log("ReadOutputLoop exited; shell process has gone");
                _tracker.AbortPending();
                _shellExitedTcs.TrySetResult();
                tcs.TrySetResult();
            }
        });
        thread.IsBackground = true;
        thread.Name = "ConPTY-Reader";
        thread.Start();
        return tcs.Task;
    }

    // --- Named Pipe server ---

    private async Task RunPipeServerAsync(string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                // Multiple server instances share the same pipe name so a
                // long-running execute on one instance doesn't starve
                // get_status / get_cached_output arriving on another. The
                // instance count caps at NamedPipeServerStream.MaxAllowedServerInstances
                // (~256 on Windows) but the worker only spawns a fixed few
                // listening loops (see RunAsync), so the cap is never hit.
                server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (IOException ex)
            {
                Log($"Pipe create error: {ex.Message}");
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            using var _server = server;

            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var request = await ReadMessageAsync(server, ct);
                var response = await HandleRequestAsync(request, ct);
                await WriteMessageAsync(server, response, ct);
            }
            catch (IOException)
            {
                // Pipe closed mid-handshake (proxy disconnected before the
                // worker's response fully drained). Benign; the proxy already
                // has whatever response it was going to get.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"Pipe error: {ex.Message}");
            }
            finally
            {
                if (server.IsConnected)
                    server.Disconnect();
            }
        }
    }

    private async Task<JsonElement> HandleRequestAsync(JsonElement request, CancellationToken ct)
    {
        var type = request.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
        if (type == null)
            return SerializeResponse(w => w.WriteString("error", "Missing 'type' field in request"));

        return type switch
        {
            "execute" => await HandleExecuteAsync(request, ct),
            "get_status" => SerializeResponse(w =>
            {
                w.WriteString("status", Status);
                w.WriteBoolean("hasCachedOutput", HasCachedOutput);
                w.WriteString("shellFamily", _shellFamily);
                w.WriteString("shellPath", _shell);
                w.WriteStringOrNull("cwd", _tracker.LastKnownCwd);
                w.WriteStringOrNull("runningCommand", _tracker.RunningCommand);
                var elapsed = _tracker.RunningElapsedSeconds;
                if (elapsed.HasValue) w.WriteNumber("runningElapsedSeconds", elapsed.Value);
                else w.WriteNull("runningElapsedSeconds");
                if (_adapter?.Modes is { Count: > 0 })
                {
                    w.WriteStringOrNull("currentMode", _currentMode);
                    if (_currentModeLevel.HasValue) w.WriteNumber("currentModeLevel", _currentModeLevel.Value);
                }
            }),
            "get_cached_output" => HandleGetCachedOutput(),
            "peek" => SerializeResponse(w =>
            {
                // On Windows, prefer reading the console screen buffer
                // directly — this gives us exactly what the user sees,
                // with no VT-lite parsing artifacts. On other platforms
                // fall back to the ring buffer + VT-lite interpreter.
                var snapshot = ReadConsoleScreenText() ?? _tracker.GetRecentOutputSnapshot();
                w.WriteString("status", Status);
                w.WriteBoolean("busy", _tracker.Busy);
                w.WriteStringOrNull("runningCommand", _tracker.RunningCommand);
                var elapsed = _tracker.RunningElapsedSeconds;
                if (elapsed.HasValue) w.WriteNumber("runningElapsedSeconds", elapsed.Value);
                else w.WriteNull("runningElapsedSeconds");
                w.WriteString("recentOutput", snapshot);
                var wantRaw = request.TryGetProperty("raw", out var rawProp) && rawProp.ValueKind == JsonValueKind.True;
                if (wantRaw)
                {
                    var raw = _tracker.GetRawRecentBytes();
                    var rawBytes = Encoding.UTF8.GetBytes(raw);
                    w.WriteString("rawBase64", Convert.ToBase64String(rawBytes));
                }
            }),
            "send_input" => await HandleSendInputAsync(request, ct),
            "set_title" => HandleSetTitle(request),
            "display_banner" => await HandleDisplayBannerAsync(request, ct),
            "claim" => HandleClaim(request),
            "ping" => SerializeResponse(w => w.WriteString("status", "ok")),
            _ => SerializeResponse(w => w.WriteString("error", $"Unknown request type: {type}")),
        };
    }

    /// <summary>
    /// Write a colorized echo of the AI command to the visible console for
    /// pwsh / powershell.exe, matching PSReadLine's own rendering style.
    /// We bypass the usual PTY mirror path because PSReadLine is idle in the
    /// input loop between commands and writes prediction ghost text /
    /// cursor moves that would clash with the echo. Instead we suppress
    /// the mirror (flipped back on by OSC B), reset the current line via
    /// \r + \e[2K, paint a synthetic prompt, then write the colorized
    /// command. Crucially the echo does NOT end with \r\n — PSReadLine's
    /// own AcceptLine finalize emits the newline once OSC B re-enables
    /// the mirror, so adding our own here would leave a blank line
    /// between the echo and the first line of real command output.
    /// </summary>
    private void RenderPwshCommandEcho(string command)
    {
        _mirrorVisible = false;
        _stdoutStream ??= Console.OpenStandardOutput();

        var echoText = PwshColorizer.Colorize(command);
        var cwd = _tracker.LastKnownCwd ?? _cwd;
        var synthPrompt = $"PS {cwd}> ";

        // Multi-line commands read much better when the body starts on its
        // own line instead of being glued to the prompt. Insert a newline
        // right after the prompt when the command contains an embedded
        // newline. Strip any trailing newline from echoText so the last
        // line of the echo sits on its own line without an extra blank
        // row before PSReadLine's AcceptLine finalizes.
        string payload;
        if (command.Contains('\n'))
        {
            var trimmed = echoText.TrimEnd('\n', '\r');
            payload = $"\r\x1b[2K{synthPrompt}\r\n{trimmed}";
        }
        else
        {
            payload = $"\r\x1b[2K{synthPrompt}{echoText}";
        }

        var cmdDisplay = Encoding.UTF8.GetBytes(payload);
        _stdoutStream.Write(cmdDisplay, 0, cmdDisplay.Length);
        _stdoutStream.Flush();
    }

    /// <summary>
    /// Build the body of a tempfile that runs a multi-line AI command. The
    /// wrapper does three things in order:
    ///   1. Move the cursor up one row and clear the line. PSReadLine
    ///      displays `. 'path/to/tempfile.ps1'` as the "command being run"
    ///      on the previous prompt row when the user presses Enter; we
    ///      overwrite that line with a synthesized prompt + the real AI
    ///      command text so the visible console ends up looking like the
    ///      user just typed the multi-line command at a fresh prompt.
    ///   2. Write the colorized echo (PS prompt + newline + colorized
    ///      multi-line body) via a single [Console]::Write call. The
    ///      payload is base64-encoded so the raw ESC sequences inside the
    ///      colorizer output don't have to be escaped for a PowerShell
    ///      string literal. Crucially, this is emitted by the child shell
    ///      itself — NOT by the worker writing to _stdoutStream — so the
    ///      child's virtual buffer (and therefore PSReadLine's _initialY
    ///      for future input loops) stays in sync with the rows the
    ///      visible console actually shows. Rendering the echo from the
    ///      worker side bypassed ConPTY and left PSReadLine's history
    ///      display N rows above where the user expected it.
    ///   3. Emit an OSC 633;C marker. The PreCommandLookupAction that
    ///      fires OSC C naturally was already triggered at the first
    ///      real cmdlet invocation, but its _commandStart was captured
    ///      BEFORE the echo was written. We re-emit OSC C right after
    ///      the echo so _commandStart gets reset to the end of the echo,
    ///      and the captured output window the AI sees excludes the
    ///      echo lines.
    /// Finally the AI's actual multi-line command body follows.
    /// </summary>
    private string BuildMultiLineTempfileBody(string command, int wrapRowCount)
    {
        var colorizedBody = PwshColorizer.Colorize(command).TrimEnd('\r', '\n');
        var cwd = _tracker.LastKnownCwd ?? _cwd;
        var synthPrompt = $"PS {cwd}> ";

        // Build the full payload as a single blob:
        //   \e[<N>F          — cursor previous line (CPL) — move up N
        //                      rows to col 0 of the start of the
        //                      dot-source input. N is sized by the
        //                      caller based on terminal width so we
        //                      cover the entire wrapped input area,
        //                      not just the last row.
        //   \e[0J            — erase display from the cursor to end
        //                      (wipes the dot-source input + any
        //                      trailing rows, leaves scrollback above
        //                      the prompt untouched).
        //   PS cwd> \r\n     — synthetic prompt + newline so the
        //                      command body reads as a clean block
        //                      below it.
        //   <colorized body> — the AI command, ANSI-colored.
        //   \r\n             — terminator.
        //   \e]633;C\a       — manual OSC C so CommandTracker rewinds
        //                      _commandStart past the echo and the AI
        //                      doesn't see this noise in its captured
        //                      output.
        // Pack it base64 and decode at runtime. Decoded bytes go straight
        // through OpenStandardOutput, bypassing pwsh's host TextWriter
        // layer (which was transforming our cursor-control escapes).
        var payload = new StringBuilder();
        payload.Append('\x1b').Append($"[{Math.Max(1, wrapRowCount)}F"); // CPL N
        payload.Append('\x1b').Append("[0J");                            // ED 0
        payload.Append(synthPrompt);
        payload.Append("\r\n");
        payload.Append(colorizedBody);
        payload.Append("\r\n");
        payload.Append('\x1b').Append("]633;C").Append('\x07');          // OSC C
        var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToString()));

        var sb = new StringBuilder();
        // Bypass pwsh's host Console wrapper by grabbing the raw stdout
        // stream and writing the payload as bytes directly.
        sb.AppendLine("$__rp_out = [System.Console]::OpenStandardOutput()");
        sb.AppendLine($"$__rp_bytes = [System.Convert]::FromBase64String('{payloadBase64}')");
        sb.AppendLine("$__rp_out.Write($__rp_bytes, 0, $__rp_bytes.Length)");
        sb.AppendLine("$__rp_out.Flush()");
        // and the real command body, normalized to LF line endings
        sb.AppendLine(command.Replace("\r\n", "\n"));
        // Stash $? and $LASTEXITCODE into globals the prompt fn in
        // integration.ps1 reads with priority. Necessary because the
        // outer wrapper is `Import-Module PSReadLine; . '<tmp>';
        // Remove-Item '<tmp>'` — by the time the prompt fn runs,
        // Remove-Item has already reset $? to its own (successful)
        // result, destroying the signal the user's pipeline left behind.
        // Capturing here, inside the tempfile's scope, preserves the
        // pipeline's real outcome for the exit-code resolver.
        sb.AppendLine("$global:__rp_ai_pipeline_ok = $?");
        sb.AppendLine("$global:__rp_ai_pipeline_lec = $global:LASTEXITCODE");
        return sb.ToString();
    }

    private async Task<JsonElement> HandleExecuteAsync(JsonElement request, CancellationToken ct)
    {
        var command = request.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(command))
            return SerializeResponse(w => w.WriteString("error", "Missing 'command' field in request"));
        var timeoutMs = request.TryGetProperty("timeout", out var tp) ? tp.GetInt32() : CommandTracker.PreemptiveTimeoutMs;

        // Fast-path reject if another command is still running (e.g., timed-out command in background).
        // The arrival of this execute_command is definitive proof that the prior
        // command's response channel has been lost — the caller cannot both be
        // awaiting the prior result and issuing a new execute at the same time
        // (ripple has no concept of "target console" in the tool shape — each
        // new execute replaces the previous expectation). Flip the in-flight
        // command to cache mode so its result gets appended to _cachedResults
        // and surfaces on the next drain instead of being silently discarded.
        //
        // This is only a fast-path — two concurrent requests can still both
        // observe !Busy here and race through the tempfile I/O below. The
        // authoritative registration-atomic re-check happens under
        // _cacheLock immediately before RegisterCommand.
        if (_tracker.Busy)
        {
            _tracker.FlipToCacheMode();
            return SerializeResponse(w => { w.WriteString("status", "busy"); w.WriteString("command", command); });
        }

        // Pre-send syntactic gate for adapters that declare
        // multiline_detect: balanced_parens (Racket today, future Lisp
        // family). Catches AI mistakes like a half-finished
        // (define (f x) before they deadlock the REPL waiting for the
        // closing paren. The counter understands string literals, line
        // and block comments, and the schema §18 Q1 reader-macro
        // extensions (char_literal_prefix, datum_comment_prefix).
        //
        // Only gates when the adapter asks for it — every other
        // adapter's execute path is untouched.
        if (_adapter?.Input.MultilineDetect == "balanced_parens"
            && _adapter.Input.BalancedParens is { } bpSpec)
        {
            var check = BalancedParensCounter.Evaluate(bpSpec, command);
            if (!check.IsComplete)
            {
                var diag = check.Diagnostic ?? "syntactically incomplete input";
                return SerializeResponse(w =>
                {
                    w.WriteString("status", "error");
                    w.WriteString("error", "incomplete_input");
                    w.WriteString("message", diag);
                    w.WriteString("command", command);
                });
            }
        }

        var shellName = _shellFamily;
        var enter = _adapter?.Input.LineEnding
            ?? _defaultEnter;

        // Multi-line pwsh commands have their echo emitted from inside the
        // tempfile itself via [Console]::Write so the child's virtual
        // buffer's cursor tracking stays consistent with what the visible
        // console shows — see BuildMultiLineTempfileBody. Only render the
        // echo directly here for single-line commands.
        bool isMultiLinePwsh = _isPwshFamily && command.Contains('\n');
        bool isMultiLineCmd = shellName is "cmd" && command.Contains('\n');
        bool isMultiLinePosix = shellName is "bash" or "sh" or "zsh" && command.Contains('\n');
        if (_isPwshFamily && !isMultiLinePwsh)
            RenderPwshCommandEcho(command);

        // Multi-line commands can't be written straight to the PTY: pwsh
        // (and bash) would treat each embedded \n as "submit line now",
        // push subsequent lines into the continuation-prompt input, and
        // fragment the OSC markers so the capture window is meaningless.
        // Bracketed paste and raw passthrough both proved unreliable
        // under ConPTY, so we fall back to the robust approach: write
        // the full multi-line body to a temp file and dot-source it.
        // The shell parses the file as-is — heredocs, comments, nested
        // scriptblocks and multi-line pipelines all survive — and the
        // `. 'file'` form runs in the caller's scope so variables and
        // functions defined by the command persist for later calls.
        // Single-line commands skip the temp file and go straight to the
        // PTY as before so echo / history quality stays highest for the
        // common case.
        string ptyPayload;
        if (isMultiLinePwsh)
        {
            // Two delivery strategies, picked per adapter:
            //   encoded_scriptblock — base64-encode the multi-line body and
            //     send a single-line `. ([ScriptBlock]::Create(...))`
            //     invocation. No disk I/O, no tempfile cleanup, no history
            //     filter needed; measured ~0.3-0.5s faster than tempfile.
            //   tempfile (default)  — write the body to a temp .ps1 and
            //     dot-source it. Slower but works even when the decoded
            //     body length would exceed PSReadLine's input line cap.
            int termWidth = 120;
            try { if (Console.WindowWidth > 0) termWidth = Console.WindowWidth; } catch { }
            var cwdForPrompt = _tracker.LastKnownCwd ?? _cwd ?? "";
            var promptLen = 3 /* "PS " */ + cwdForPrompt.Length + 2 /* "> " */;

            if (_adapter?.Input.MultilineDelivery == "encoded_scriptblock")
            {
                // Two-pass sizing: wrapRowCount appears inside the body as
                // the digit count in the \e[NF wipe sequence, and the body
                // is what we base64-encode into the ptyInput whose length
                // determines wrapRowCount. One pass converges whenever the
                // row count stays within a single digit-width bucket (the
                // practical case); a second pass is enough when it flips
                // from 1- to 2-digit.
                int wrapRowCount = 1;
                string ptyInput = "";
                for (int pass = 0; pass < 3; pass++)
                {
                    var body = BuildMultiLineTempfileBody(command, wrapRowCount);
                    var bodyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                    ptyInput = $". ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{bodyB64}'))))";
                    var totalCols = promptLen + ptyInput.Length;
                    var next = Math.Max(1, (totalCols + termWidth - 1) / termWidth);
                    if (next == wrapRowCount)
                        break;
                    wrapRowCount = next;
                }
                ptyPayload = ptyInput + enter;
            }
            else
            {
                var tmpFile = Path.Combine(Path.GetTempPath(), $".ripple-exec-{Environment.ProcessId}-{Guid.NewGuid():N}.ps1");
                var ptyInput = $". '{tmpFile}'; Remove-Item '{tmpFile}' -ErrorAction SilentlyContinue";

                // Work out how many terminal rows the dot-source + Remove-Item
                // input occupies once it wraps at the PTY's current width, so
                // BuildMultiLineTempfileBody can emit `\e[<N>F\e[0J` and wipe
                // every wrapped row rather than just the last one. Prompt is
                // "PS <cwd>> " followed by the ptyInput itself.
                var totalCols = promptLen + ptyInput.Length;
                var wrapRowCount = Math.Max(1, (totalCols + termWidth - 1) / termWidth);

                await File.WriteAllTextAsync(tmpFile, BuildMultiLineTempfileBody(command, wrapRowCount), ct);
                ptyPayload = ptyInput + enter;
            }
        }
        else if (isMultiLineCmd)
        {
            // cmd can't parse embedded newlines from a PTY keystroke stream —
            // each \n is treated as Enter and the second line drops into a
            // fresh prompt, fragmenting command parsing and the OSC markers.
            // Mirror the pwsh tempfile strategy: write the body to a .cmd
            // batch file, `call` it from the PTY as a single-line input, then
            // `del` it. `@echo off` up front suppresses re-echo of each line
            // inside the batch so the output mirrors single-line cmd usage.
            var tmpFile = Path.Combine(Path.GetTempPath(), $"ripple-exec-{Environment.ProcessId}-{Guid.NewGuid():N}.cmd");
            var body = "@echo off\r\n" + command.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
            await File.WriteAllTextAsync(tmpFile, body, ct);
            ptyPayload = $"call \"{tmpFile}\" & del \"{tmpFile}\"" + enter;
        }
        else if (isMultiLinePosix)
        {
            // bash/zsh/sh share the same problem cmd does: writing newlines
            // into the PTY makes the shell submit each line as Enter, so
            // multi-line constructs (for/done, if/fi, function definitions)
            // either capture wrong output (the tracker resolves on the first
            // OSC A) or drop iterations entirely. Drop the body into a temp
            // .sh file and dot-source it so the shell parses the whole block
            // as a single source file. State (variables, functions, cwd)
            // persists because dot-sourcing runs in the caller's scope.
            var windowsPath = Path.Combine(Path.GetTempPath(), $"ripple-exec-{Environment.ProcessId}-{Guid.NewGuid():N}.sh");
            var body = command.Replace("\r\n", "\n");
            if (!body.EndsWith('\n')) body += "\n";
            await File.WriteAllTextAsync(windowsPath, body, ct);

            // bash sees the Windows temp dir under its own mount namespace —
            // /mnt/c/... for WSL, /c/... for MSYS2 / Git Bash. On Linux/macOS
            // the worker and shell share a real POSIX filesystem so the
            // Windows-path translation is skipped.
            string unixPath;
            if (OperatingSystem.IsWindows())
            {
                unixPath = IsWslBash(_shell)
                    ? "/mnt/" + char.ToLower(windowsPath[0]) + windowsPath[2..].Replace('\\', '/')
                    : "/" + char.ToLower(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');
            }
            else
            {
                unixPath = windowsPath;
            }
            ptyPayload = $". '{unixPath}'; rm -f '{unixPath}'" + enter;
        }
        else if (command.Contains('\n') &&
                 _adapter?.Input.MultilineDelivery == "tempfile" &&
                 _adapter.Input.Tempfile?.InvocationTemplate is { } invocationTpl)
        {
            // Generic adapter-driven tempfile delivery for multi-line REPL
            // commands. Used today by Python (whose parser-based REPL needs
            // a trailing blank line to close def/class/if blocks and would
            // otherwise capture output from a half-submitted block). A
            // future Node or Ruby adapter that declares multiline_delivery:
            // tempfile picks this path up for free.
            //
            // The body is written to adapter.input.tempfile.{prefix,extension}
            // and the adapter-supplied invocation_template (e.g.
            // _ripple_exec_file(r"{path}") for python) is sent to the PTY
            // as a single line. The helper referenced by the template is
            // expected to have been registered by the adapter's
            // init.script_resource at REPL startup and is responsible for
            // cleanup so interrupted commands still delete their tempfile.
            var tmpPrefix = _adapter.Input.Tempfile.Prefix ?? ".ripple-exec-";
            var tmpExt = _adapter.Input.Tempfile.Extension ?? "";
            var tmpFile = Path.Combine(
                Path.GetTempPath(),
                $"{tmpPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}{tmpExt}");
            var body = command.Replace("\r\n", "\n");
            if (!body.EndsWith('\n')) body += "\n";
            await File.WriteAllTextAsync(tmpFile, body, ct);
            ptyPayload = ExpandTemplate(invocationTpl, ("path", tmpFile)) + enter;
        }
        else
        {
            ptyPayload = command + enter;
        }

        // Atomic registration block. Allocating the inline-delivery id,
        // parking the TCS in _inlineDeliveriesById, AND calling
        // RegisterCommand all happen under _cacheLock so a concurrent
        // execute request can never observe a partial state where the
        // tracker has a live command but the worker has no dictionary
        // entry to route its snapshot through (which was the old TOCTOU
        // root cause — a shared _inlineDelivery field could be
        // overwritten between the second caller's Busy check and the
        // first caller's registration, mis-delivering results across
        // commands).
        //
        // RegisterCommand does nest into the tracker's internal _lock,
        // but the tracker never reaches back into _cacheLock, so the
        // lock order (_cacheLock → tracker._lock) is consistent with
        // every other site in this file (FinalizeSnapshotAsync exits
        // _cacheLock before calling DetachSettlingCapture /
        // ReleaseAiCommand).
        //
        // Setting the TCS up BEFORE RegisterCommand also closes the
        // original preemptive-timeout race: when timeoutMs == 0 the
        // tracker's FlipToCacheMode callback can fire synchronously
        // from RegisterCommand and hand a snapshot to
        // FinalizeSnapshotAsync before control returns here. With the
        // id-keyed dictionary write already persisted, the finalize
        // path still finds the correct TCS.
        var inlineDelivery = new TaskCompletionSource<CommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        long inlineDeliveryId = 0;
        Task<CompletedCommandSnapshot>? snapshotTask = null;
        bool busyRace = false;
        lock (_cacheLock)
        {
            // Authoritative Busy re-check INSIDE the lock. The outer
            // fast-path check can race with another execute that is
            // between its RegisterCommand and FinalizeSnapshotAsync —
            // bridging Busy and registration under the same lock is
            // what makes the two checks equivalent.
            if (_tracker.Busy)
            {
                busyRace = true;
            }
            else
            {
                inlineDeliveryId = ++_commandIdSeq;
                _inlineDeliveriesById[inlineDeliveryId] = inlineDelivery;
                try
                {
                    snapshotTask = _tracker.RegisterCommand(new CommandTracker.CommandRegistration(
                        CommandText: command,
                        PtyPayload: ptyPayload,
                        InputEchoLineEnding: enter,
                        InputEchoStrategy: _adapter?.Output.InputEchoStrategy,
                        ShellFamily: _shellFamily,
                        DisplayName: _displayName,
                        PostPromptSettleMs: _adapter?.Output.PostPromptSettleMs ?? 150,
                        TimeoutMs: timeoutMs,
                        InlineDeliveryId: inlineDeliveryId));
                }
                catch (InvalidOperationException)
                {
                    // Concurrent race at the tracker level — somebody else
                    // registered a command between the Busy re-check above
                    // and here (shouldn't happen with the atomic re-check,
                    // but kept as belt-and-braces because the tracker's
                    // own gate is what enforces "at most one live _tcs").
                    // Remove the dictionary entry we just inserted so it
                    // doesn't linger as a phantom slot.
                    _inlineDeliveriesById.Remove(inlineDeliveryId);
                    busyRace = true;
                }
            }
        }

        if (busyRace)
        {
            // FlipToCacheMode takes the tracker's lock. Drop _cacheLock
            // before calling in — FinalizeSnapshotAsync never holds
            // _cacheLock while touching the tracker, and this keeps
            // that invariant intact.
            _tracker.FlipToCacheMode();
            return SerializeResponse(w => { w.WriteString("status", "busy"); w.WriteString("command", command); });
        }

        // Adapters whose input echo strategy is deterministic_byte_match
        // or fuzzy_byte_match (cmd, python, linenoise-style REPLs — any
        // REPL without a stdlib pre-input hook that can emit OSC B/C)
        // have no way to fire OSC C at the moment the command starts,
        // so the tracker would wait forever for a marker that never
        // arrives. Paper over the gap: the capture's command start is
        // forced to 0 so the finalizer slice begins at the true start
        // of the captured window.
        if (_adapter?.Output.InputEchoStrategy is "deterministic_byte_match" or "fuzzy_byte_match")
            _tracker.SkipCommandStartMarker();

        // Hold user input while the AI command is in flight. Any
        // keystrokes the user types into the visible console window
        // are buffered by InputForwardLoop instead of being forwarded
        // to the PTY — this prevents stray characters from being
        // prepended to the AI command. Held bytes are replayed
        // automatically after the command completes so the user's
        // typing isn't lost. Ctrl+C passes through even while held
        // so the user can always interrupt a stuck command.
        //
        // This replaces the old adapter-level clear_line approach
        // (which depended on the shell's readline supporting Ctrl-A +
        // Ctrl-K and didn't work at all for shells without a line
        // editor). The hold gate operates at ripple's own forwarding
        // layer, above the shell, so it works universally.
        _holdUserInput = true;
        // From here to the end of the method, any early return calls
        // ReleaseHeldUserInput explicitly (before building the response,
        // so the shell gets a head start on replaying held keystrokes).
        // The outer try/finally is purely a safety net: if WriteToPty,
        // the race await, or any other code below throws without
        // reaching an explicit release, the finally un-sticks the hold
        // gate so the next user keystroke is not silently buffered into
        // a queue that nobody will ever drain. ReleaseHeldUserInput is
        // idempotent (flag→false, drain-queue-until-empty), so the
        // duplicate call on the happy path is a no-op.
        try
        {

        // Legacy clear_line: still useful as a belt-and-suspenders
        // defense for characters that slipped into the line editor
        // buffer before the hold gate was set. Eventually removable
        // once the hold approach proves sufficient in the field.
        var clearLine = _adapter?.Input.ClearLine;
        if (!string.IsNullOrEmpty(clearLine))
        {
            try { await WriteToPty(clearLine, ct); }
            catch { /* best-effort */ }
        }

        await WriteToPty(ptyPayload, ct);

        // Await either:
        //   - the inline delivery (fully finalized CommandResult the
        //     shared finalize-once path built from the snapshot), OR
        //   - the tracker's snapshot task completing with a
        //     TimeoutException when the preemptive timer fires /
        //     FlipToCacheMode detaches.
        // Whichever completes first drives the exit path. snapshotTask
        // surfaces TimeoutException and shell-exit errors; inline
        // delivery surfaces the happy-path CommandResult.
        //
        // snapshotTask is guaranteed non-null past the busyRace guard
        // above — it was assigned inside the same lock-protected
        // critical section that allocated inlineDeliveryId.
        var snapshotTaskLocal = snapshotTask!;
        var raceTask = Task.WhenAny(inlineDelivery.Task, snapshotTaskLocal);
        await raceTask;

        if (snapshotTaskLocal.IsFaulted || snapshotTaskLocal.IsCanceled)
        {
            ReleaseHeldUserInput();
            try { await snapshotTaskLocal; }
            catch (TimeoutException)
            {
                // Timeout = command is still running in the background.
                // The real snapshot will flow through FinalizeSnapshotAsync
                // into _cachedResults when the shell eventually resolves;
                // wait_for_completion drains it from there. Return the
                // tracker's bounded in-flight partial (NOT a spill
                // preview) as diagnostic context.
                //
                // Race window (CodeRabbit finding #3): Task.WhenAny above
                // only proves SOME task completed first — between that
                // and here, FinalizeSnapshotAsync can still run its
                // Step 6 and TrySetResult on our inline TCS. If we
                // unconditionally remove the slot and ignore the inline
                // TCS, that successfully-delivered CommandResult becomes
                // orphaned on a TCS nobody awaits AND is never appended
                // to _cachedResults, so wait_for_completion later reports
                // `no_cache`. Close the window atomically:
                //
                //   1. Under _cacheLock, TrySetCanceled the inline TCS.
                //      - Returns true  → we won the race; finalize has
                //        not yet reached its Step 6 (or will observe
                //        the cancellation and fall through to the
                //        _cachedResults branch because its guard
                //        `!pending.Task.IsCompleted` is false for a
                //        cancelled task). Remove the entry and return
                //        the timed-out payload.
                //      - Returns false → finalize beat us and already
                //        called TrySetResult (IsCompletedSuccessfully
                //        on the inline task). The dictionary entry was
                //        already removed by finalize's Step 6. The
                //        command actually completed inside the timeout
                //        window; surface the real result instead of a
                //        synthetic timeout.
                //   2. Removal lives inside the same lock as the
                //      TrySetCanceled call so no concurrent finalize
                //      can squeeze in after we cancel but before we
                //      remove (which would leave a cancelled entry
                //      stranded in _inlineDeliveriesById).
                bool weWonRace;
                lock (_cacheLock)
                {
                    weWonRace = inlineDelivery.TrySetCanceled();
                    if (weWonRace) _inlineDeliveriesById.Remove(inlineDeliveryId);
                }
                if (!weWonRace)
                {
                    var raceResponse = await TryResolveTimeoutRaceLossAsync(
                        inlineDelivery, ct);
                    if (raceResponse is JsonElement r) return r;
                }
                var partial = _tracker.GetCurrentCommandSnapshot();
                return SerializeResponse(w =>
                {
                    w.WriteString("output", "");
                    w.WriteNumber("exitCode", 0);
                    w.WriteNull("cwd");
                    w.WriteString("duration", (timeoutMs / 1000.0).ToString("F1"));
                    w.WriteBoolean("timedOut", true);
                    if (!string.IsNullOrEmpty(partial))
                        w.WriteString("partialOutput", partial);
                });
            }
            catch (InvalidOperationException ex)
            {
                // Shell exited mid-command — no snapshot will ever
                // arrive, but still detach this command's inline TCS
                // so it doesn't linger in the routing dictionary (an
                // orphaned entry would never be reaped because no
                // finalize is ever coming).
                lock (_cacheLock) _inlineDeliveriesById.Remove(inlineDeliveryId);
                return SerializeResponse(w =>
                {
                    w.WriteString("output", ex.Message);
                    w.WriteNumber("exitCode", -1);
                    w.WriteNull("cwd");
                    w.WriteString("duration", "0.0");
                    w.WriteBoolean("timedOut", false);
                    w.WriteBoolean("shellExited", true);
                });
            }
            // Any other exception — surface as-is so the proxy sees the
            // same shape of error it did before the refactor.
        }

        // Happy path: inline delivery completed with a fully finalized
        // CommandResult. Release held user input BEFORE serializing so
        // the held keystrokes replay into the shell's fresh prompt
        // while the proxy is still formatting the JSON. This gives the
        // shell a head start on processing any user-typed partial
        // command so it's visible in the console window by the time
        // the AI's next tool call arrives.
        //
        // FinalizeSnapshotAsync routes catastrophic finalize failures
        // through the inline TCS via TrySetException so the awaiter
        // unwinds instead of hanging forever on an uncompleted task.
        // Surface the failure with the same wire shape as the shell-
        // exit branch (minus the shellExited flag) so the proxy can
        // render a useful message.
        CommandResult result;
        try
        {
            result = await inlineDelivery.Task;
        }
        catch (Exception ex)
        {
            ReleaseHeldUserInput();
            return SerializeResponse(w =>
            {
                w.WriteString("output", $"finalize failed: {ex.GetType().Name}: {ex.Message}");
                w.WriteNumber("exitCode", -1);
                w.WriteNull("cwd");
                w.WriteString("duration", "0.0");
                w.WriteBoolean("timedOut", false);
            });
        }
        ReleaseHeldUserInput();
        return await BuildExecuteSuccessResponseAsync(result, ct);

        }
        finally
        {
            // Safety net — see the comment at _holdUserInput = true
            // above. Explicit release is called on every happy-path /
            // expected-failure branch before this runs; the finally
            // only does real work if an unexpected exception bypassed
            // those paths. ReleaseHeldUserInput is idempotent so the
            // second call on normal paths is harmless.
            ReleaseHeldUserInput();
        }
    }

    /// <summary>
    /// Shared happy-path response builder used by both the normal
    /// inline-await success arm and the TimeoutException race branch's
    /// Case 1 (finalize beat us with a successful result). Keeps the
    /// two paths serializing through a single point so the wire shape —
    /// including `_currentMode` / `_currentModeLevel` re-evaluation and
    /// field emission — is guaranteed identical.
    ///
    /// Re-evaluates which mode the REPL is now in. The detector scans
    /// the tail of recent terminal output against every auto_enter
    /// mode's detect regex; falls back to the adapter's default mode
    /// otherwise. Result is cached on the worker so get_status /
    /// cached_output can report the same value without re-scanning.
    ///
    /// Scans the recent-output ring rather than the OSC-C..D slice
    /// because the mode transition is visible in the NEXT prompt
    /// (e.g. CCL drops to `1 > ` after an error), which arrives AFTER
    /// the OSC A that fires Resolve — so it's never inside
    /// cleanedOutput. The ring is updated unconditionally by FeedOutput
    /// and captures the post-A prompt as soon as its bytes land.
    /// Because ConPTY typically delivers OSC A and the following prompt
    /// bytes in the same chunk, the ring has the new prompt by the
    /// time we read it; a short poll with a fresh ring snapshot on
    /// each tick covers the rare case where the prompt trails by a
    /// few milliseconds.
    /// </summary>
    private async Task<JsonElement> BuildExecuteSuccessResponseAsync(
        CommandResult result,
        CancellationToken ct)
    {
        if (_adapter?.Modes is { Count: > 0 } modes)
        {
            // Start with an explicit "no match yet" ModeMatch rather
            // than `default` — records are reference types, so `default`
            // is null and the compiler (rightly) can't prove the while
            // loop reassigns it before the post-loop `match.Name`
            // access. The loop body always runs at least once and
            // overwrites this value, but flowing through a non-null
            // sentinel makes the safety invariant explicit.
            var match = new ModeMatch(Name: null, Level: null);
            string? defaultModeName = null;
            foreach (var m in modes)
            {
                if (m.Default) { defaultModeName = m.Name; break; }
            }
            defaultModeName ??= modes[0].Name;

            var deadline = DateTime.UtcNow.AddMilliseconds(150);
            while (true)
            {
                var snap = _tracker.GetRawRecentBytes();
                match = ModeDetector.Detect(modes, snap);
                if (match.Name != null && match.Name != defaultModeName) break;
                if (DateTime.UtcNow >= deadline) break;
                try { await Task.Delay(15, ct); }
                catch (OperationCanceledException) { break; }
            }
            _currentMode = match.Name;
            _currentModeLevel = match.Level;
        }
        return SerializeResponse(w =>
        {
            w.WriteStringOrNull("output", result.Output);
            w.WriteNumber("exitCode", result.ExitCode);
            WriteOscExtensionFields(w, result);
            w.WriteStringOrNull("cwd", result.Cwd);
            w.WriteStringOrNull("duration", result.Duration);
            w.WriteBoolean("timedOut", false);
            // Inline parity with the cached-drain path: when truncation
            // spilled the full output to disk, surface the path as a
            // structured field so the proxy / MCP client never has to
            // parse the preview header to recover it.
            if (result.SpillFilePath != null)
                w.WriteString("spillFilePath", result.SpillFilePath);
            if (_adapter?.Modes is { Count: > 0 })
            {
                w.WriteStringOrNull("currentMode", _currentMode);
                if (_currentModeLevel.HasValue) w.WriteNumber("currentModeLevel", _currentModeLevel.Value);
            }
        });
    }

    /// <summary>
    /// Post-<c>TrySetCanceled</c> dispatch for the
    /// <c>TimeoutException</c> catch in <see cref="HandleExecuteAsync"/>
    /// when we LOST the cancel race (finalize beat us into the inline
    /// TCS). Returns a non-null response if the TCS's terminal state
    /// tells us the client should observe finalize's actual outcome,
    /// or null to let the caller fall through to the synthetic
    /// <c>timedOut:true</c> payload.
    ///
    /// Cases:
    ///   - <c>IsCompletedSuccessfully</c>: finalize delivered a real
    ///     <see cref="CommandResult"/>. Route through the shared
    ///     happy-path response builder so the wire shape (including
    ///     mode re-evaluation and mode fields) is identical to the
    ///     normal success arm. Finalize's Step 6 removed our dict
    ///     entry and did NOT append to <c>_cachedResults</c>, so
    ///     cache invariants hold.
    ///   - <c>IsFaulted</c>: finalize threw and its catch routed the
    ///     exception through <c>TrySetException</c> on the still-
    ///     attached inline TCS. Return the same
    ///     <c>"finalize failed: &lt;Type&gt;: &lt;message&gt;"</c> shape
    ///     the normal inline-await catch emits, so the client observes
    ///     the finalize failure instead of a misleading
    ///     <c>timedOut:true</c>.
    ///   - Default: TCS is still not terminal (shouldn't happen past
    ///     <c>TrySetCanceled</c> returning false — it would imply a
    ///     pre-existing cancel) — return null and let the caller
    ///     synthesize the timeout payload.
    /// </summary>
    private async Task<JsonElement?> TryResolveTimeoutRaceLossAsync(
        TaskCompletionSource<CommandResult> inlineDelivery,
        CancellationToken ct)
    {
        if (inlineDelivery.Task.IsCompletedSuccessfully)
        {
            return await BuildExecuteSuccessResponseAsync(
                inlineDelivery.Task.Result, ct);
        }
        if (inlineDelivery.Task.IsFaulted)
        {
            // Task.Exception is an AggregateException wrapper; peel the
            // inner exception so the emitted "finalize failed:" string
            // matches what finalize's own catch at Step 6 produces
            // against the underlying throw.
            var aggregate = inlineDelivery.Task.Exception!;
            var inner = aggregate.InnerException ?? aggregate;
            return SerializeResponse(w =>
            {
                w.WriteString("output", $"finalize failed: {inner.GetType().Name}: {inner.Message}");
                w.WriteNumber("exitCode", -1);
                w.WriteNull("cwd");
                w.WriteString("duration", "0.0");
                w.WriteBoolean("timedOut", false);
            });
        }
        return null;
    }

    /// <summary>
    /// Shared finalize-once handler. Every primary command completion
    /// (inline and timed-out / flipped-to-cache) flows through this
    /// method exactly once per command, so the inline execute_command
    /// response and the cached wait_for_completion payload can never
    /// diverge.
    ///
    /// Steps (plan §2, §3 steps 3-4, §7):
    ///   1. Optionally wait shell-specific post-prompt settle time
    ///      (adapter.output.post_prompt_settle_ms) so trailing bytes
    ///      that still belong to the command result land in the
    ///      capture before we slice it. pwsh/powershell emit OSC A
    ///      from their prompt function after all output has been
    ///      captured, so we skip the settle for that family.
    ///   2. Clean the capture's [CommandStart, CommandEnd) window
    ///      through the slice-reader finalizer (ANSI strip, CRLF
    ///      normalize, pwsh continuation-prompt drop).
    ///   3. Strip deterministic input echo against the persisted
    ///      ptyPayload baseline, for adapters that need it.
    ///   4. Run truncation / spill creation on the cleaned stream.
    ///   5. Bake the statusLine using the worker's display context.
    ///   6. Deliver to the inline caller (if the original awaiting
    ///      task is still attached) or append to the cache entry
    ///      list for the next wait_for_completion drain.
    ///   7. Trigger opportunistic spill-file cleanup, honouring the
    ///      live-lease set so still-referenced entries survive.
    ///   8. Dispose the capture so its scratch file is released.
    /// </summary>
    private async Task FinalizeSnapshotAsync(CompletedCommandSnapshot snapshot)
    {
        try
        {
            // Step 1: settle window. pwsh emits OSC A as part of its
            // prompt function RETURN value, so by the time the worker
            // sees OSC A all command output has already been captured.
            // Non-pwsh shells may stream a few trailing bytes (cmd's
            // PROMPT repaint timing, Format-Table rows still
            // finishing). Poll the capture's length until it has been
            // stable for the adapter-declared post_prompt_settle_ms.
            // The capture accepts FeedOutput appends throughout this
            // window because the tracker keeps it as _settlingCapture
            // until DetachSettlingCapture fires below — that's what
            // lets the poll observe real growth instead of a frozen
            // length.
            if (!_isPwshFamily && snapshot.PostPromptSettleMs > 0)
                await WaitCaptureStable(snapshot.Capture, snapshot.PostPromptSettleMs);

            // Cut the tracker's write path to the capture before we
            // read its final slice. After this call FeedOutput routes
            // post-settle bytes to nothing (they're no longer part of
            // this command's window), so the slice the finalizer
            // reads next is stable and corresponds exactly to what
            // the user saw when the prompt returned.
            _tracker.DetachSettlingCapture(snapshot.Capture);

            // Step 2: clean the window via the slice-reader finalizer
            // so arbitrarily large captures never need to be assembled
            // as one string here. Extend the end offset to include any
            // trailing bytes that arrived between OSC D (CommandEnd)
            // and OSC A (snapshot emission) — those are still part of
            // the command's output window (Format-Table trailing rows,
            // cmd PROMPT repaint tail, etc.) but the tracker only
            // marked CommandEnd at OSC D. Using the capture's total
            // length picks them up without mutating the snapshot.
            //
            // Cap the extended end at PromptStartOffset (the capture
            // position where OSC A fired): anything arriving after
            // OSC A is prompt text (bash `$ `, cmd PROMPT repaint,
            // zsh `%`) that never belongs in the command's cleaned
            // output. Without this cap bash's post-prompt settle
            // would pull the next prompt line into the finalized
            // result — pwsh is immune because PwshColorizer handles
            // its prompt separately, but non-pwsh shells stream
            // prompt chars immediately after OSC A. Fall back to the
            // capture's total length when PromptStartOffset is
            // absent (early-exit paths that bypass OSC A).
            var cutoff = snapshot.PromptStartOffset ?? snapshot.Capture.Length;
            var effectiveEnd = Math.Min(
                Math.Max(snapshot.CommandEnd, snapshot.Capture.Length),
                cutoff);
            var cleaned = CommandOutputFinalizer.Clean(
                snapshot.Capture,
                snapshot.CommandStart,
                effectiveEnd,
                snapshot.VtBaseline);

            // Step 3: deterministic echo stripping for adapters that
            // have no OSC-C marker to anchor the output window start.
            // fuzzy_byte_match additionally swallows a leading prompt
            // redraw for linenoise-style REPLs (duckdb etc.) whose echo
            // opens with "<prompt><input>" rather than just "<input>";
            // it routes through StripCmdInputEcho so the ANSI-skip and
            // adapter-prompt matcher apply.
            if (snapshot.InputEchoStrategy == "deterministic_byte_match")
            {
                cleaned = EchoStripper.Strip(
                    cleaned,
                    snapshot.PtyPayloadBaseline,
                    snapshot.InputEchoLineEnding);
            }
            else if (snapshot.InputEchoStrategy == "fuzzy_byte_match")
            {
                var echoExpected = snapshot.PtyPayloadBaseline;
                var lineEnding = snapshot.InputEchoLineEnding;
                if (lineEnding.Length > 0 && echoExpected.EndsWith(lineEnding))
                    echoExpected = echoExpected[..^lineEnding.Length];
                var promptRedrawMatcher = BuildPromptRedrawMatcher(_adapter?.Prompt?.Primary);
                cleaned = StripCmdInputEcho(cleaned, echoExpected, promptRedrawMatcher);
            }

            // Step 4: truncation / spill creation. Under threshold the
            // DisplayOutput is `cleaned` verbatim; over threshold the
            // helper wrote the full cleaned content to the public
            // spill directory and returned a head+tail preview.
            var truncation = _truncationHelper.Process(cleaned);

            // Step 5: bake status line using the display context the
            // tracker captured at registration time, so the cached
            // entry doesn't need a proxy-side metadata re-join.
            var statusLine = BuildStatusLine(
                snapshot.Command,
                snapshot.ExitCode,
                snapshot.Duration,
                snapshot.Cwd,
                snapshot.ShellFamily,
                snapshot.DisplayName,
                snapshot.ErrorCount,
                snapshot.LastExitCode);

            var result = new CommandResult(
                Output: truncation.DisplayOutput,
                ExitCode: snapshot.ExitCode,
                Cwd: snapshot.Cwd,
                Command: snapshot.Command,
                Duration: snapshot.Duration,
                StatusLine: statusLine,
                SpillFilePath: truncation.SpillFilePath,
                ErrorCount: snapshot.ErrorCount,
                LastExitCode: snapshot.LastExitCode,
                ErrorMessages: snapshot.ErrorMessages,
                TruncatedErrorCount: snapshot.TruncatedErrorCount);

            // Step 6: deliver inline OR cache. Route by the snapshot's
            // InlineDeliveryId so each command hits its own TCS — a
            // shared field would let a concurrent execute overwrite
            // this command's routing slot between registration and
            // finalize and mis-deliver the result to a different
            // caller. HandleExecuteAsync inserts the TCS under the id
            // before calling RegisterCommand and removes it on the
            // timeout / shell-exit branches; we remove it here on
            // successful hand-off. A missing id (null on the snapshot,
            // or a routing entry already removed by the timeout path)
            // falls through to the cache branch so the next
            // wait_for_completion drain picks the result up.
            bool delivered = false;
            lock (_cacheLock)
            {
                if (snapshot.InlineDeliveryId is long inlineId
                    && _inlineDeliveriesById.TryGetValue(inlineId, out var pending)
                    && !pending.Task.IsCompleted)
                {
                    _inlineDeliveriesById.Remove(inlineId);
                    delivered = pending.TrySetResult(result);
                }

                if (!delivered)
                {
                    _cachedResults.Add(new CachedCommandResult(result, truncation.SpillFilePath));
                    if (truncation.SpillFilePath != null)
                        _liveSpillPaths.Add(truncation.SpillFilePath);
                }
            }

            // Step 7: opportunistic age-based cleanup of old spill
            // files, protecting any path currently referenced by an
            // undrained cache entry.
            TriggerSpillCleanup();
        }
        catch (Exception ex)
        {
            Log($"FinalizeSnapshotAsync error: {ex.GetType().Name}: {ex.Message}");

            // Route the failure through whichever delivery channel is
            // still attached so the command is not silently lost.
            // Without this, an inline awaiter would block forever on
            // its inline-delivery Task (the TCS was never completed)
            // and a flip-to-cache caller would get `no_cache` from
            // wait_for_completion because _cachedResults.Add was
            // inside the try. Both branches converge on the same
            // finalize-failed payload shape so the proxy sees a
            // structured error regardless of which path observed the
            // throw.
            lock (_cacheLock)
            {
                TaskCompletionSource<CommandResult>? pending = null;
                if (snapshot.InlineDeliveryId is long inlineId
                    && _inlineDeliveriesById.TryGetValue(inlineId, out var found)
                    && !found.Task.IsCompleted)
                {
                    _inlineDeliveriesById.Remove(inlineId);
                    pending = found;
                }

                if (pending != null)
                {
                    pending.TrySetException(ex);
                }
                else
                {
                    var statusLine = BuildStatusLine(
                        snapshot.Command,
                        snapshot.ExitCode,
                        snapshot.Duration,
                        snapshot.Cwd,
                        snapshot.ShellFamily,
                        snapshot.DisplayName,
                        snapshot.ErrorCount,
                        snapshot.LastExitCode);
                    var fallback = new CommandResult(
                        Output: $"finalize failed: {ex.GetType().Name}: {ex.Message}",
                        ExitCode: snapshot.ExitCode,
                        Cwd: snapshot.Cwd,
                        Command: snapshot.Command,
                        Duration: snapshot.Duration,
                        StatusLine: statusLine,
                        SpillFilePath: null);
                    _cachedResults.Add(new CachedCommandResult(fallback, null));
                }
            }
        }
        finally
        {
            // Step 8: release the capture's scratch file. Safe to do
            // here because every slice we needed has already been read
            // above.
            try { snapshot.Capture.Complete(); } catch { /* best effort */ }

            // Step 9: clear the tracker's AI-busy flag AFTER the
            // result has been delivered inline / appended to cache.
            // This closes the settle-window race where a fast polling
            // client could otherwise see {status: standby,
            // hasCachedOutput: false} between tracker emission and
            // cache insertion and treat the command as lost. The
            // generation token guards against clobbering a newer
            // command that registered while this finalize was still
            // running.
            _tracker.ReleaseAiCommand(snapshot.Generation);
        }
    }

    /// <summary>
    /// Wait until the capture's Length has stopped growing for at
    /// least <paramref name="stableMs"/>, or until a 2×stable ceiling
    /// elapses. This is ripple's post-prompt settle — moved here from
    /// the old proxy-side drain_post_output assembly — so the
    /// finalizer can include cmd / bash trailing bytes that arrive
    /// just after OSC A but still belong to the command result.
    /// </summary>
    private static async Task WaitCaptureStable(CommandOutputCapture capture, int stableMs)
    {
        stableMs = Math.Max(1, stableMs);
        int maxMs = Math.Max(stableMs * 2, stableMs + 200);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(maxMs);

        long lastLen = capture.Length;
        var lastChange = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            int pollMs = Math.Clamp(stableMs / 2, 5, 30);
            try { await Task.Delay(pollMs); }
            catch { break; }

            long current = capture.Length;
            if (current != lastLen)
            {
                lastLen = current;
                lastChange = DateTime.UtcNow;
                continue;
            }

            if ((DateTime.UtcNow - lastChange).TotalMilliseconds >= stableMs)
                break;
        }
    }

    /// <summary>
    /// True when the worker has at least one finalized cache entry that
    /// a drain has not yet consumed. Routed through <see cref="Status"/>
    /// and the <c>hasCachedOutput</c> field on get_status responses so
    /// the manager's polling contract stays identical to the pre-refactor
    /// tracker-owned cache.
    /// </summary>
    private bool HasCachedOutput
    {
        get { lock (_cacheLock) return _cachedResults.Count > 0; }
    }

    private void TriggerSpillCleanup()
    {
        try
        {
            _truncationHelper.CleanupOldSpillFiles(path =>
            {
                lock (_cacheLock) return _liveSpillPaths.Contains(path);
            });
        }
        catch { /* cleanup is opportunistic */ }
    }

    /// <summary>
    /// Build a self-describing status line for a command result, using
    /// what the worker knows at finalize time: the proxy-supplied
    /// display name, the adapter's shell family, the command text,
    /// exit code, duration and resolved cwd. Baking this into the
    /// CommandResult (rather than formatting at drain time on the
    /// proxy side) keeps cached results self-contained: a cache
    /// drain just reads the status line out and prints it, without
    /// having to re-join metadata from the proxy's ConsoleInfo
    /// which may have drifted since the command was registered.
    /// </summary>
    /// <summary>
    /// Thin passthrough to the shared <see cref="StatusLineFormatter"/>.
    /// See that class for the rendering rules — keeping this call site
    /// so the worker-internal callers don't depend on the Services-
    /// layer helper type name directly.
    /// </summary>
    private static string BuildStatusLine(
        string? command, int exitCode, string duration, string? cwd,
        string? shellFamily, string? displayName, int errorCount, int lastExitCode)
        => StatusLineFormatter.Format(
            command, exitCode, duration, cwd,
            shellFamily, displayName, errorCount, lastExitCode);

    /// <summary>
    /// Release the user-input hold gate and replay any keystrokes that
    /// were buffered while the AI command was in flight. Called from
    /// every exit path of HandleExecuteAsync (success, timeout, error)
    /// so the hold is never accidentally left on.
    /// </summary>
    private void ReleaseHeldUserInput()
    {
        _holdUserInput = false;
        while (_heldUserInput.TryDequeue(out var bytes))
        {
            try { _pty!.InputStream.Write(bytes, 0, bytes.Length); }
            catch (IOException) { break; }
        }
        try { _pty?.InputStream.Flush(); } catch { }
    }

    /// <summary>
    /// Serialize the OSC 633 extension fields that ride on every
    /// finalized <see cref="CommandResult"/>: <c>errorCount</c> (always
    /// written, 0 for non-pwsh adapters that emit no OSC E),
    /// <c>lastExitCode</c> / <c>errorMessages</c> / <c>truncatedErrorCount</c>
    /// (conditional — omitted when zero / empty so the wire stays tight
    /// on the happy path). Both the inline execute response path and
    /// the cached-drain response path call this so a new extension
    /// field is added in one place instead of two.
    /// </summary>
    private static void WriteOscExtensionFields(System.Text.Json.Utf8JsonWriter w, CommandResult r)
    {
        w.WriteNumber("errorCount", r.ErrorCount);
        if (r.LastExitCode > 0) w.WriteNumber("lastExitCode", r.LastExitCode);
        if (r.ErrorMessages is { Count: > 0 } msgs)
        {
            w.WriteStartArray("errorMessages");
            foreach (var m in msgs) w.WriteStringValue(m);
            w.WriteEndArray();
        }
        if (r.TruncatedErrorCount > 0)
            w.WriteNumber("truncatedErrorCount", r.TruncatedErrorCount);
    }

    private JsonElement HandleGetCachedOutput()
    {
        // Drain snapshot under _cacheLock, then release spill leases so the
        // next TriggerSpillCleanup pass can reclaim files for entries that
        // have now been handed to the proxy. We do the lease release AFTER
        // serialization so a crash between drain and serialize doesn't
        // leak the files (they stay in _liveSpillPaths and the cleanup
        // predicate still honours them).
        List<CachedCommandResult> snapshot;
        lock (_cacheLock)
        {
            if (_cachedResults.Count == 0)
                return SerializeResponse(w => w.WriteString("status", "no_cache"));
            snapshot = new List<CachedCommandResult>(_cachedResults);
            _cachedResults.Clear();
        }

        var response = SerializeResponse(w =>
        {
            w.WriteString("status", "ok");
            w.WriteStartArray("results");
            foreach (var entry in snapshot)
            {
                var r = entry.Result;
                w.WriteStartObject();
                w.WriteStringOrNull("output", r.Output);
                w.WriteNumber("exitCode", r.ExitCode);
                WriteOscExtensionFields(w, r);
                w.WriteStringOrNull("cwd", r.Cwd);
                w.WriteStringOrNull("command", r.Command);
                w.WriteStringOrNull("duration", r.Duration);
                w.WriteString("statusLine", r.StatusLine);
                if (r.SpillFilePath != null)
                    w.WriteString("spillFilePath", r.SpillFilePath);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

        // Release spill leases — the proxy now owns these paths (the
        // consumer either reads them inline or deletes them explicitly).
        //
        // Deliberately NOT calling TriggerSpillCleanup here: the drain
        // response we just serialized exposes spillFilePath values that
        // the caller has not yet had a chance to open, and the age-
        // based cleanup would happily delete any path older than
        // MaxFileAgeMinutes (120 min today) the moment its lease is
        // released. Cached entries that waited more than two hours
        // before being drained — wait_for_completion on a long-parked
        // result, proxy reconnecting after a sleep — would surface a
        // path that no longer exists on disk by the time the caller
        // tries to read it. Age-based cleanup still runs opportun-
        // istically on the next FinalizeSnapshotAsync, which is the
        // natural cadence; by then the caller has had its response and
        // either read the file or moved on.
        lock (_cacheLock)
        {
            foreach (var entry in snapshot)
                if (entry.SpillFilePath != null)
                    _liveSpillPaths.Remove(entry.SpillFilePath);
        }

        return response;
    }

    // --- Test-only seams ---
    //
    // These hooks let platform-agnostic unit tests exercise the cache /
    // drain / spill-lease plumbing without spinning up a real PTY. They
    // are intentionally `internal` so production callers outside the
    // assembly cannot reach them; the test assembly lives in the same
    // project and sees them directly. Each hook is a thin wrapper over
    // the same private state that FinalizeSnapshotAsync and
    // HandleGetCachedOutput mutate at runtime, so a regression in the
    // wire contract or lease lifecycle surfaces through the test
    // alongside the production path.

    /// <summary>
    /// Seeds a finalized <see cref="CommandResult"/> into the worker's
    /// cache as if <see cref="FinalizeSnapshotAsync"/> had produced it.
    /// The spill path (if any) is registered in <c>_liveSpillPaths</c>
    /// so cleanup-lease tests can observe the drain-releases-lease
    /// transition. Test-only — the runtime only ever appends to this
    /// list via the finalize-snapshot path.
    /// </summary>
    internal void TestSeedCachedResult(CommandResult result)
    {
        lock (_cacheLock)
        {
            _cachedResults.Add(new CachedCommandResult(result, result.SpillFilePath));
            if (result.SpillFilePath != null)
                _liveSpillPaths.Add(result.SpillFilePath);
        }
    }

    /// <summary>
    /// Invokes the same drain path <see cref="HandleGetCachedOutput"/>
    /// serves over the pipe, returning the serialized wire JSON for
    /// cache-contract assertions. Test-only.
    /// </summary>
    internal JsonElement TestDrainCachedOutput() => HandleGetCachedOutput();

    /// <summary>
    /// Snapshot of the live-lease set for cleanup-protection tests.
    /// Returned as a copy under <c>_cacheLock</c> so the caller can
    /// observe the set without racing the drain path. Test-only.
    /// </summary>
    internal IReadOnlyCollection<string> TestGetLiveSpillPaths()
    {
        lock (_cacheLock) return _liveSpillPaths.ToArray();
    }

    /// <summary>
    /// Runs the same opportunistic age-based cleanup
    /// <see cref="FinalizeSnapshotAsync"/> triggers, so tests that
    /// want to observe deletion after drain can force the sweep
    /// deterministically instead of waiting for the next finalize.
    /// Test-only.
    /// </summary>
    internal void TestTriggerSpillCleanup() => TriggerSpillCleanup();

    /// <summary>
    /// Swaps in a custom <see cref="OutputTruncationHelper"/> so tests
    /// can back the worker with an in-memory filesystem / fake clock.
    /// Test-only — the production constructor wires the default helper
    /// and no runtime path touches this setter.
    /// </summary>
    internal void TestReplaceTruncationHelper(OutputTruncationHelper replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        _truncationHelper = replacement;
    }

    /// <summary>
    /// Drives <see cref="FinalizeSnapshotAsync"/> directly with a caller-
    /// supplied snapshot so catastrophic-failure-path tests can observe
    /// how finalize exceptions propagate to the inline TCS or the
    /// cached-results list. When <paramref name="inlineDelivery"/> is
    /// non-null the seam allocates an inline-delivery id, registers
    /// the TCS under it, and rewrites the supplied snapshot to carry
    /// that id so the finalize-once path's id-based routing resolves
    /// the TCS exactly the way the production execute path does.
    /// When <paramref name="inlineDelivery"/> is null the snapshot is
    /// passed through verbatim and finalize falls through to the
    /// cache branch. Test-only.
    /// </summary>
    internal Task TestRunFinalizeSnapshotAsync(
        CompletedCommandSnapshot snapshot,
        TaskCompletionSource<CommandResult>? inlineDelivery)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        CompletedCommandSnapshot snapshotToFinalize = snapshot;
        if (inlineDelivery != null)
        {
            long id;
            lock (_cacheLock)
            {
                id = ++_commandIdSeq;
                _inlineDeliveriesById[id] = inlineDelivery;
            }
            snapshotToFinalize = snapshot with { InlineDeliveryId = id };
        }
        return FinalizeSnapshotAsync(snapshotToFinalize);
    }

    /// <summary>
    /// Returns the count of cached results under <c>_cacheLock</c> so
    /// fallback-path tests can assert "finalize exception appended a
    /// cache entry" without draining the cache (which would clear it).
    /// Test-only.
    /// </summary>
    internal int TestGetCachedResultCount()
    {
        lock (_cacheLock) return _cachedResults.Count;
    }

    /// <summary>
    /// Allocates a fresh inline-delivery id, parks the caller-supplied
    /// TCS under it, and returns the id. Used by concurrency tests to
    /// simulate multiple in-flight execute registrations without
    /// driving the full pipe / PTY path, so a test can prove that a
    /// finalize on command A never completes command B's TCS even
    /// when both entries live in <c>_inlineDeliveriesById</c>
    /// simultaneously. Test-only.
    /// </summary>
    internal long TestRegisterInlineDelivery(TaskCompletionSource<CommandResult> inlineDelivery)
    {
        ArgumentNullException.ThrowIfNull(inlineDelivery);
        lock (_cacheLock)
        {
            var id = ++_commandIdSeq;
            _inlineDeliveriesById[id] = inlineDelivery;
            return id;
        }
    }

    /// <summary>
    /// Returns the count of inline-delivery entries still parked in
    /// <c>_inlineDeliveriesById</c>. Concurrency-routing tests assert
    /// on this to verify that a successful finalize removes exactly
    /// its own entry and leaves concurrent entries untouched.
    /// Test-only.
    /// </summary>
    internal int TestGetInlineDeliveryCount()
    {
        lock (_cacheLock) return _inlineDeliveriesById.Count;
    }

    /// <summary>
    /// Drives <see cref="TryResolveTimeoutRaceLossAsync"/> with a caller-
    /// supplied inline TCS so the <c>TimeoutException</c> catch's
    /// post-cancel-race dispatch can be unit-tested without standing
    /// up a full pipe/PTY. Callers set the TCS to Faulted /
    /// CompletedSuccessfully BEFORE invoking this seam, mirroring the
    /// state finalize's Step 6 leaves the TCS in when finalize wins
    /// the <c>Task.WhenAny</c> race with the timeout branch.
    /// Test-only.
    /// </summary>
    internal Task<JsonElement?> TestResolveTimeoutRaceLossAsync(
        TaskCompletionSource<CommandResult> inlineDelivery,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inlineDelivery);
        return TryResolveTimeoutRaceLossAsync(inlineDelivery, ct);
    }

    /// <summary>
    /// Send raw input to the PTY while a command is running. Rejects
    /// if the console is idle — use execute_command for that. The
    /// input is written as-is: the caller is responsible for including
    /// \r for Enter, \x03 for Ctrl+C, escape sequences for arrow keys,
    /// etc. Capped at 256 chars to prevent accidental bulk injection.
    /// </summary>
    private async Task<JsonElement> HandleSendInputAsync(JsonElement request, CancellationToken ct)
    {
        if (!_tracker.Busy)
            return SerializeResponse(w => { w.WriteString("status", "rejected"); w.WriteString("error", "Console is not busy. Use execute_command to run commands."); });

        var input = request.TryGetProperty("input", out var inputProp) ? inputProp.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(input))
            return SerializeResponse(w => { w.WriteString("status", "error"); w.WriteString("error", "Missing 'input' field"); });

        if (input.Length > 256)
            return SerializeResponse(w => { w.WriteString("status", "error"); w.WriteString("error", $"Input too long ({input.Length} chars, max 256)"); });

        // Interpret C-style escape sequences so the AI can express
        // control characters naturally: \r for Enter, \n for LF,
        // \t for Tab, \x03 for Ctrl+C, \x1b for ESC, \\ for literal \.
        var unescaped = UnescapeInput(input);
        await WriteToPty(unescaped, ct);
        Log($"SendInput: {EscapeForLog(unescaped)}");

        return SerializeResponse(w =>
        {
            w.WriteString("status", "ok");
            w.WriteNumber("bytesSent", unescaped.Length);
        });
    }

    internal static string UnescapeInput(string input)
    {
        var sb = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '\\' && i + 1 < input.Length)
            {
                switch (input[i + 1])
                {
                    case 'r': sb.Append('\r'); i++; break;
                    case 'n': sb.Append('\n'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case 'a': sb.Append('\a'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    case 'x' when i + 3 < input.Length:
                        var hex = input.Substring(i + 2, 2);
                        if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var b))
                        { sb.Append((char)b); i += 3; }
                        else sb.Append(input[i]);
                        break;
                    default: sb.Append(input[i]); break;
                }
            }
            else
            {
                sb.Append(input[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Replace OSC 0/1/2 (set window title) sequences in shell output with our
    /// desired title. Prevents shells like bash from overriding the title set
    /// by the proxy via set_title pipe command.
    /// Format: \x1b]N;text\x07 (BEL terminator) or \x1b]N;text\x1b\\ (ST terminator)
    /// where N is 0, 1, or 2.
    ///
    /// <para><b>Cross-chunk buffering.</b> The PTY read loop calls this
    /// function on every chunk it produces, and a shell emitting a title
    /// OSC near a read-buffer boundary can easily split the sequence
    /// (opener in chunk N, body or terminator in chunk N+1). Without
    /// state, a split opener leaks into the visible stream and the
    /// terminal interprets it as an open-ended title write — the shell's
    /// title ends up displayed instead of ripple's desired one. The
    /// <paramref name="pendingTail"/> ref parameter carries a
    /// not-yet-classified or not-yet-terminated OSC fragment forward
    /// between calls: on entry it's prepended to the current chunk, on
    /// exit it contains any unterminated opener that belongs to a title
    /// sequence (or a partial ESC at chunk end that could begin one).
    /// Callers are expected to keep one tail buffer per stream.</para>
    /// </summary>
    internal static string ReplaceOscTitle(string input, string? desiredTitle, ref string pendingTail)
    {
        // Prepend any fragment carried over from the previous chunk.
        // The carried fragment is always either `\x1b`, `\x1b]`, or
        // a partial `\x1b]N;...` opener with no terminator — i.e.
        // something we deliberately refused to emit last time because
        // we couldn't tell whether it was a title OSC that needed
        // rewriting. Clear it now; it's re-set below if still partial.
        var combined = pendingTail.Length == 0 ? input : pendingTail + input;
        pendingTail = "";
        if (desiredTitle == null) return combined;

        var sb = new StringBuilder(combined.Length);
        int i = 0;
        while (i < combined.Length)
        {
            if (combined[i] == '\x1b')
            {
                int remain = combined.Length - i;

                // Lone ESC at end of chunk — we can't classify what
                // kind of escape sequence this starts, and emitting it
                // bare would leave the visible terminal in "waiting for
                // sequence" state. Buffer until the next call.
                if (remain < 2)
                {
                    pendingTail = combined[i..];
                    break;
                }

                if (combined[i + 1] == ']')
                {
                    // OSC. Need at least \x1b + ] + type byte + ; = 4 bytes
                    // to decide whether this is a title sequence we care about.
                    if (remain < 4)
                    {
                        pendingTail = combined[i..];
                        break;
                    }

                    var typeByte = combined[i + 2];
                    var isTitleOsc = (typeByte == '0' || typeByte == '1' || typeByte == '2')
                                     && combined[i + 3] == ';';

                    if (isTitleOsc)
                    {
                        // Look for the terminator: BEL (\x07) or ST (\x1b\).
                        int end = -1;
                        int termLen = 0;
                        for (int j = i + 4; j < combined.Length; j++)
                        {
                            if (combined[j] == '\x07') { end = j; termLen = 1; break; }
                            if (combined[j] == '\x1b' && j + 1 < combined.Length && combined[j + 1] == '\\')
                            { end = j; termLen = 2; break; }
                        }

                        if (end >= 0)
                        {
                            // Fully terminated — rewrite with desired title,
                            // preserving OSC type and terminator style.
                            sb.Append('\x1b').Append(']').Append(typeByte).Append(';').Append(desiredTitle);
                            if (termLen == 1) sb.Append('\x07');
                            else { sb.Append('\x1b').Append('\\'); }
                            i = end + termLen;
                            continue;
                        }

                        // Terminator hasn't arrived yet — buffer the whole
                        // opener + partial body so the next chunk can
                        // re-scan with the terminator visible. Crucially
                        // do NOT emit these bytes, or the visible terminal
                        // would interpret them as an open-ended title and
                        // display the shell's title once the terminator
                        // eventually shows up.
                        pendingTail = combined[i..];
                        break;
                    }
                    // Non-title OSC (OSC 4, 7, 112, 633, ...) — fall
                    // through and pass the `\x1b` through byte-by-byte.
                    // The rest of the sequence flows through the plain
                    // copy path below without being rewritten.
                }
                // Non-OSC escape (CSI `\x1b[`, charset `\x1b(`, ...) —
                // pass through unchanged.
            }

            sb.Append(combined[i]);
            i++;
        }
        return sb.ToString();
    }

    private JsonElement HandleSetTitle(JsonElement request)
    {
        var title = request.TryGetProperty("title", out var tp) ? tp.GetString() : null;
        if (title != null)
        {
            _desiredTitle = title;
            Console.Title = title;
            // Also write OSC 0 (Set Window Title) directly to stdout.
            // Some shells (cmd.exe) override Console.Title via ConPTY;
            // the OSC 0 sequence ensures the visible console title is set.
            _stdoutStream ??= Console.OpenStandardOutput();
            var osc = Encoding.UTF8.GetBytes($"\x1b]0;{title}\x07");
            _stdoutStream.Write(osc, 0, osc.Length);
            _stdoutStream.Flush();

            // Keep the worker's display-name field in sync with the
            // proxy's current name. set_title is the earliest point at
            // which a freshly launched worker learns its name, so
            // without this, commands registered before the claim path
            // runs would bake the wrong identity into their cached
            // status lines. BuildStatusLine reads _displayName on
            // every finalize, so no further propagation is needed.
            _displayName = title;
        }
        return SerializeResponse(w => w.WriteString("status", "ok"));
    }

    /// <summary>
    /// Write banner/reason text directly to the visible console at startup.
    /// Called from RunAsync before the first prompt is drawn.
    /// </summary>
    private void WriteBanner()
    {
        WriteBannerText(_banner, _reason, isReuse: false);
    }

    /// <summary>
    /// Write banner and/or reason text directly to the visible console.
    /// Uses ANSI colors: banner in green, reason in dark yellow.
    /// Shell-agnostic — writes to worker's stdout, not through the shell.
    /// </summary>
    private void WriteBannerText(string? banner, string? reason, bool isReuse = true)
    {
        var sb = new StringBuilder();
        if (isReuse)
            sb.Append("\r\n\r\n"); // blank line separating previous prompt from banner

        if (!string.IsNullOrEmpty(banner))
            sb.Append($"\x1b[32m{banner}\x1b[0m\r\n");

        if (!string.IsNullOrEmpty(reason))
        {
            if (sb.Length > 0) sb.Append("\r\n");
            sb.Append($"\x1b[33mReason: {reason}\x1b[0m\r\n");
        }

        if (sb.Length > 0 && (!string.IsNullOrEmpty(banner) || !string.IsNullOrEmpty(reason)))
        {
            sb.Append("\r\n"); // blank line after banner before shell output/prompt
            _stdoutStream ??= Console.OpenStandardOutput();
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            _stdoutStream.Write(bytes, 0, bytes.Length);
            _stdoutStream.Flush();
        }
    }

    private async Task<JsonElement> HandleDisplayBannerAsync(JsonElement request, CancellationToken ct)
    {
        var banner = request.TryGetProperty("banner", out var bp) ? bp.GetString() : null;
        var reason = request.TryGetProperty("reason", out var rp) ? rp.GetString() : null;
        var cwd = request.TryGetProperty("cwd", out var cp) ? cp.GetString() : null;
        WriteBannerText(banner, reason);

        // Preferred kick: run the adapter's cd_command as an AI-tracked
        // command. That flags _isAiCommand in the tracker, which gates the
        // OSC B/C pair the shell emits — so the shell's "I'm about to run
        // something" markers don't flip the user-typed-command view that
        // drives the proxy's Busy status. Side effect: the prompt is
        // redrawn at the end of the cd, fulfilling the kick's original
        // UX purpose (visible prompt below the banner).
        var cdCmd = !string.IsNullOrEmpty(cwd) && _adapter != null
            ? PathEscape.RenderCdCommand(_adapter, cwd!)
            : null;
        if (cdCmd != null && !_tracker.Busy)
        {
            try
            {
                // 5s is enough for a cd on any sane shell; if the shell is
                // somehow stuck we prefer giving up and returning rather
                // than holding the start_console response hostage.
                //
                // The snapshot from this registration flows through the
                // normal FinalizeSnapshotAsync handler like every other AI
                // command — no inline delivery is wired up, so the cd's
                // cleaned result lands in _cachedResults and the next
                // wait_for_completion / get_cached_output drains it. That
                // matches legacy behaviour where the banner's cd kick was
                // visible to the proxy as a standard completed command.
                var enter = _adapter?.Input.LineEnding ?? _defaultEnter;
                var resultTask = _tracker.RegisterCommand(new CommandTracker.CommandRegistration(
                    CommandText: cdCmd,
                    PtyPayload: cdCmd + enter,
                    InputEchoLineEnding: enter,
                    InputEchoStrategy: _adapter?.Output.InputEchoStrategy,
                    ShellFamily: _shellFamily,
                    DisplayName: _displayName,
                    PostPromptSettleMs: _adapter?.Output.PostPromptSettleMs ?? 150,
                    TimeoutMs: 5000));
                if (_adapter?.Output.InputEchoStrategy is "deterministic_byte_match" or "fuzzy_byte_match")
                    _tracker.SkipCommandStartMarker();
                var payload = Encoding.UTF8.GetBytes(cdCmd + enter);
                _pty?.InputStream.Write(payload, 0, payload.Length);
                _pty?.InputStream.Flush();
                await resultTask.WaitAsync(ct);
                return SerializeResponse(w => w.WriteString("status", "ok"));
            }
            catch (InvalidOperationException)
            {
                // Tracker was already mid-command — very unlikely on a
                // standby we just reused, but fall through to the raw kick
                // so the banner at least gets its prompt redraw.
            }
            catch (Exception ex)
            {
                Log($"DisplayBanner cd kick failed: {ex.Message}");
            }
        }

        // Fallback for adapters that don't declare cd_command (REPLs),
        // or when the AI-tracked cd path bailed out: the original raw
        // enter kick. Phantom-busy risk only materialises on adapters
        // with an Enter-triggered OSC B hook, and those are exactly the
        // adapters that have cd_command set, so this fallback is safe.
        var fallbackEnter = _adapter?.Input.LineEnding ?? _defaultEnter;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(fallbackEnter);
            _pty?.InputStream.Write(bytes, 0, bytes.Length);
            _pty?.InputStream.Flush();
        }
        catch (Exception ex) { Log($"DisplayBanner kick failed: {ex.Message}"); }

        return SerializeResponse(w => w.WriteString("status", "ok"));
    }

    /// <summary>
    /// Handle claim request from a new proxy. Constructs a new owned pipe name
    /// and signals the main loop to start serving it.
    /// </summary>
    private JsonElement HandleClaim(JsonElement request)
    {
        var proxyPid = request.TryGetProperty("proxy_pid", out var pp) ? pp.GetInt32() : 0;
        var agentId = request.TryGetProperty("agent_id", out var ai) ? ai.GetString() : "default";
        var title = request.TryGetProperty("title", out var tp) ? tp.GetString() : null;
        var proxyVersionStr = request.TryGetProperty("proxy_version", out var vp) ? vp.GetString() : null;

        if (proxyPid == 0)
            return SerializeResponse(w => { w.WriteString("status", "error"); w.WriteString("error", "proxy_pid required"); });

        // Version check: if the calling proxy is strictly newer than this worker,
        // the pipe protocol may have changed in incompatible ways. Refuse the claim,
        // mark the console as obsolete, and stop serving pipes. The shell itself
        // keeps running so the user can continue working in the terminal.
        if (proxyVersionStr != null
            && Version.TryParse(proxyVersionStr, out var proxyVer)
            && proxyVer > _myVersion)
        {
            _obsolete = true;
            _desiredTitle = $"#{Environment.ProcessId} (obsolete v{_myVersion.ToString(3)})";
            Console.Title = _desiredTitle;
            // Show a prominent banner so the human user understands what happened:
            // AI/MCP control has been detached, but the shell itself is still usable.
            WriteBannerText(
                $"This console is no longer managed by ripple (worker v{_myVersion.ToString(3)}).",
                $"A newer proxy (v{proxyVer}) tried to re-claim this console. The shell is still available for you to use directly, but AI commands via MCP will no longer route here. Close the window when you're done.",
                isReuse: true);
            Log($"Claim refused: proxy v{proxyVer} > worker v{_myVersion}. Marking obsolete.");
            _claimTcs?.TrySetException(new InvalidOperationException("obsolete"));
            return SerializeResponse(w =>
            {
                w.WriteString("status", "obsolete");
                w.WriteString("worker_version", _myVersion.ToString(3));
            });
        }

        var newPipeName = $"{ConsoleManager.PipePrefix}.{proxyPid}.{agentId}.{Environment.ProcessId}";
        if (title != null) Console.Title = title;

        // Propagate the proxy-supplied display name (sent in the claim's
        // `title` field, already in "Fox" / "Reggae" form) to the
        // worker's own _displayName field. BuildStatusLine reads it
        // on every finalize, so any command that ends up cached on
        // this console carries a self-contained status line matching
        // how inline results are rendered.
        _displayName = title;

        // New proxy taking ownership — drop whatever is in the
        // recent-output ring. Anything captured before this moment
        // belonged to a previous MCP session (the shell was already
        // running when the human restarted Claude Code or launched a
        // new proxy), and exposing those bytes via peek_console would
        // show content that isn't part of the current session's
        // terminal view. The ring will refill with current-session
        // bytes as the new proxy issues commands.
        _tracker.ClearRecentOutput();

        // Signal the main loop to start the new owned pipe
        _claimTcs?.TrySetResult(newPipeName);

        return SerializeResponse(w => { w.WriteString("status", "ok"); w.WriteString("pipe", newPipeName); });
    }

    private static int GetProxyPidFromPipeName(string pipeName)
    {
        // RP.{proxyPid}.{agentId}.{consolePid}
        var parts = pipeName.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[1], out var pid) ? pid : 0;
    }

    // --- Pipe protocol (length-prefixed JSON) ---

    // Sanity ceiling for an inbound pipe frame. Real messages are
    // request/response JSON capped well under this; a length prefix
    // outside [0, MaxPipeFrameBytes] is either a torn frame or a buggy
    // proxy, and unconditionally allocating `new byte[len]` from a
    // negative or multi-GiB length would just OOM the worker.
    private const int MaxPipeFrameBytes = 64 * 1024 * 1024;

    private static async Task<JsonElement> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, ct);
        var len = BitConverter.ToInt32(lenBuf);
        if ((uint)len > MaxPipeFrameBytes)
            throw new IOException($"Pipe frame length {len} out of range (0..{MaxPipeFrameBytes})");
        var msgBuf = new byte[len];
        await ReadExactAsync(stream, msgBuf, ct);
        return PipeJson.ParseElement(msgBuf);
    }

    private static async Task WriteMessageAsync(Stream stream, JsonElement message, CancellationToken ct)
    {
        var msgBytes = PipeJson.ElementToBytes(message);
        var lenBytes = BitConverter.GetBytes(msgBytes.Length);
        await stream.WriteAsync(lenBytes, ct);
        await stream.WriteAsync(msgBytes, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) throw new IOException("Pipe closed");
            offset += read;
        }
    }

    private static string EscapeForLog(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c < 0x20 || c == 0x7f) sb.Append($"\\x{(int)c:x2}");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static JsonElement SerializeResponse(Action<Utf8JsonWriter> writeFields)
        => PipeJson.BuildObjectElement(writeFields);

    // --- Entry point for --console mode ---

    public static async Task<int> RunConsoleMode(string[] args)
    {
        CleanupOldLogs();
        string? proxyPid = null, agentId = null, shell = null, cwd = null, banner = null, reason = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--proxy-pid" when i + 1 < args.Length: proxyPid = args[++i]; break;
                case "--agent-id" when i + 1 < args.Length: agentId = args[++i]; break;
                case "--shell" when i + 1 < args.Length: shell = args[++i]; break;
                case "--cwd" when i + 1 < args.Length: cwd = args[++i]; break;
                case "--banner" when i + 1 < args.Length: banner = args[++i]; break;
                case "--reason" when i + 1 < args.Length: reason = args[++i]; break;
                case "--no-user-input": break; // parsed below
            }
        }
        bool noUserInput = args.Contains("--no-user-input");

        if (proxyPid == null || agentId == null || shell == null)
        {
            Console.Error.WriteLine("Usage: ripple --console --proxy-pid <pid> --agent-id <id> --shell <shell> [--cwd <dir>]");
            return 1;
        }

        cwd ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Pipe name: RP.{proxyPid}.{agentId}.{ownPid}
        var ownPid = Environment.ProcessId;
        var pipeName = $"{ConsoleManager.PipePrefix}.{proxyPid}.{agentId}.{ownPid}";

        Log($"PID={ownPid} Pipe={pipeName} Shell={shell} Cwd={cwd}");

        var worker = new ConsoleWorker(pipeName, int.Parse(proxyPid), shell, cwd, banner, reason)
        {
            _holdUserInput = noUserInput  // --no-user-input: permanently hold (suppress) input forwarding
        };
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            await worker.RunAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Swallow any late-stage exception so Program.cs still sees
            // exit code 0. Windows Terminal's "close on exit" default is
            // graceful, which means exit != 0 leaves the window open with
            // "[process exited with code ...]" instead of closing. A
            // clean exit 0 after shell death lets the console window
            // close automatically alongside the dead shell.
            Log($"RunConsoleMode exception: {ex.GetType().Name}: {ex.Message}");
        }

        return 0;
    }
}
