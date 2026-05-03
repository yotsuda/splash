namespace Ripple.Services.Adapters;

/// <summary>
/// In-memory representation of a ripple adapter YAML (schema v1).
/// Mirrors adapters/SCHEMA.md. Optional sections are nullable.
///
/// This is the data model only — loading is in AdapterLoader,
/// lookup is in AdapterRegistry, and consumption is in ConsoleWorker
/// (once phase B replaces hardcoded shell branches).
/// </summary>
public class Adapter
{
    public int Schema { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Homepage { get; set; }
    public string? License { get; set; }
    public string Family { get; set; } = "";          // shell | repl | debugger
    public List<string>? Aliases { get; set; }

    public ProcessSpec Process { get; set; } = new();
    public ReadySpec Ready { get; set; } = new();
    public InitSpec Init { get; set; } = new();
    public PromptSpec Prompt { get; set; } = new();
    public OutputSpec Output { get; set; } = new();
    public InputSpec Input { get; set; } = new();
    public List<ModeSpec>? Modes { get; set; }
    public CommandsSpec? Commands { get; set; }
    public SignalsSpec Signals { get; set; } = new();
    public LifecycleSpec Lifecycle { get; set; } = new();
    public CapabilitiesSpec Capabilities { get; set; } = new();
    public ProbeSpec? Probe { get; set; }
    public List<AdapterTest>? Tests { get; set; }

    /// <summary>
    /// Inline integration script body. When present, takes precedence over
    /// process.script_resource. For pwsh/bash/zsh in the v1 draft this is
    /// the full content of ShellIntegration/integration.{ps1,bash,zsh}.
    /// </summary>
    public string? IntegrationScript { get; set; }

    /// <summary>
    /// Load provenance, set by <see cref="AdapterRegistry"/> at registration
    /// time. "embedded" for adapters compiled into the ripple binary,
    /// "external" for YAMLs loaded from the user's override directory
    /// (see <see cref="AdapterRegistry.DefaultExternalDirectory"/>).
    /// Exposed via the list_adapters MCP tool so AI consumers can tell
    /// whether a given adapter came from the binary itself or from a
    /// user-dropped override file.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// Resolve the executable that <c>start_console</c> / <c>execute_command</c>
    /// would actually launch when the adapter is selected via its
    /// <see cref="Name"/>. Precedence mirrors
    /// <see cref="ConsoleWorker"/>'s launch path:
    ///   1. Each entry in <see cref="ProcessSpec.ExecutableCandidates"/>
    ///      is expanded (<c>%VAR%</c>) and resolved against the registry
    ///      PATH; the first existing file wins.
    ///   2. <see cref="ProcessSpec.Executable"/> is expanded and resolved.
    ///   3. The adapter <see cref="Name"/> itself is resolved via PATH +
    ///      PATHEXT.
    /// Returned <see cref="LaunchResolution.ResolvedPath"/> is a rooted,
    /// existing file or null. <see cref="LaunchResolution.Attempted"/>
    /// lists every raw candidate tried so list_adapters can show "all
    /// these were checked, none exist" when resolution fails.
    /// </summary>
    public LaunchResolution ResolveLaunchExecutable()
    {
        var attempted = new List<string>();

        if (Process.ExecutableCandidates is { Count: > 0 } candidates)
        {
            foreach (var raw in candidates)
            {
                attempted.Add(raw);
                var expanded = Environment.ExpandEnvironmentVariables(raw);
                var resolved = ShellPathResolver.Resolve(expanded);
                if (File.Exists(resolved))
                    return new LaunchResolution(resolved, raw, attempted, Path.IsPathRooted(raw) ? "executable_candidates_rooted" : "executable_candidates_path");
            }
            // fall through — no candidate existed
        }

        if (!string.IsNullOrEmpty(Process.Executable))
        {
            attempted.Add(Process.Executable);
            var expanded = Environment.ExpandEnvironmentVariables(Process.Executable);
            var resolved = ShellPathResolver.Resolve(expanded);
            if (File.Exists(resolved))
                return new LaunchResolution(resolved, Process.Executable, attempted, "executable");
        }

        // Final fallback: the adapter name itself, matching the default
        // code path when neither Executable nor ExecutableCandidates are
        // set.
        attempted.Add(Name);
        var nameResolved = ShellPathResolver.Resolve(Name);
        if (Path.IsPathRooted(nameResolved) && File.Exists(nameResolved))
            return new LaunchResolution(nameResolved, Name, attempted, "name");

        return new LaunchResolution(null, null, attempted, "unresolved");
    }
}

/// <summary>
/// Result of <see cref="Adapter.ResolveLaunchExecutable"/>. When
/// <see cref="ResolvedPath"/> is null the adapter name is not on PATH
/// and no <c>executable_candidates</c> / <c>executable</c> override
/// produced an existing file — <c>start_console</c> with that shell
/// would fail with ERROR_FILE_NOT_FOUND.
/// </summary>
/// <param name="ResolvedPath">Absolute path to the launchable binary, or null if resolution failed.</param>
/// <param name="PickedRaw">The raw candidate string (before env-var expansion) that resolved, or null.</param>
/// <param name="Attempted">Every candidate tried, in order, up to and including the one that resolved.</param>
/// <param name="Strategy">Which precedence branch produced the result: <c>executable_candidates_*</c> / <c>executable</c> / <c>name</c> / <c>unresolved</c>.</param>
public record LaunchResolution(
    string? ResolvedPath,
    string? PickedRaw,
    IReadOnlyList<string> Attempted,
    string Strategy);

public class ProcessSpec
{
    public string CommandTemplate { get; set; } = "";
    public string? PromptTemplate { get; set; }       // cmd's /k PROMPT payload
    public bool InheritEnvironment { get; set; } = true;
    public Dictionary<string, string>? Env { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public string LineEnding { get; set; } = "\n";

    /// <summary>
    /// Optional override: the actual binary to launch when this adapter
    /// is selected, distinct from the adapter's <c>name</c>. Used by
    /// adapters where the user-facing REPL name does not match an
    /// executable on PATH — e.g. <c>fsi</c> (the F# Interactive REPL)
    /// is launched via <c>dotnet fsi</c>, not a standalone fsi.exe. The
    /// worker re-resolves <c>{shell_path}</c> against this override
    /// before expanding <see cref="CommandTemplate"/>. When null, the
    /// default behaviour applies: the shell name is resolved verbatim.
    ///
    /// Prefer <see cref="ExecutableCandidates"/> when the binary lives
    /// at multiple well-known install locations across distributions
    /// (Perl, JDK, Python, etc.).
    /// </summary>
    public string? Executable { get; set; }

    /// <summary>
    /// Ordered list of launcher candidates tried left-to-right; the
    /// first one that resolves to an existing file wins. Each entry
    /// goes through <c>Environment.ExpandEnvironmentVariables</c> so
    /// Windows-style <c>%VAR%</c> references (e.g. <c>%JAVA_HOME%\bin\jdb.exe</c>)
    /// resolve against the process environment before path search.
    /// Bare names (like <c>perl</c>) are resolved via
    /// <see cref="ShellPathResolver.Resolve"/>, which searches
    /// the registry PATH + PATHEXT on Windows. Rooted paths are used
    /// as-is after env-var expansion.
    ///
    /// This field solves the "single absolute path doesn't port across
    /// distributions" problem: adapters for interpreters with multiple
    /// common install locations (Strawberry Perl, ActivePerl, Git-
    /// bundled perl, system perl; Temurin / Corretto / Zulu / OpenJDK
    /// for Java; python.org / Windows Store / Anaconda for Python) can
    /// declare every plausible location once and let the worker pick
    /// whichever is installed on the host. Falls back to
    /// <see cref="Executable"/> and then to the adapter name when the
    /// list is null, empty, or fully unresolvable.
    /// </summary>
    public List<string>? ExecutableCandidates { get; set; }
}

public class ReadySpec
{
    public string WaitForEvent { get; set; } = "prompt_start";
    public string? WaitFor { get; set; }              // regex fallback
    public int TimeoutMs { get; set; }
    public int SettleBeforeInjectMs { get; set; }
    public bool SuppressMirrorDuringInject { get; set; }
    public bool KickEnterAfterReady { get; set; }
    public int DelayAfterInjectMs { get; set; }

    // WaitForOutputSettled tuning — see ConsoleWorker.WaitForOutputSettled.
    // Defaults match the pre-schema hardcoded values so omitting these in
    // existing adapters is a no-op.
    public int OutputSettledMinMs { get; set; } = 2000;
    public int OutputSettledStableMs { get; set; } = 1000;
    public int OutputSettledMaxMs { get; set; } = 30000;
}

public class InitSpec
{
    public string Strategy { get; set; } = "none";    // shell_integration | marker | prompt_variable | regex | none
    public string HookType { get; set; } = "none";    // prompt_function | preexec | ps0 | precommand_lookup_action | debug_trap | custom | none
    public string Delivery { get; set; } = "none";    // launch_command | pty_inject | rc_file | none
    public string? ScriptResource { get; set; }
    public string? Script { get; set; }
    public string? InitInvocationTemplate { get; set; }

    public TempfileSpec? Tempfile { get; set; }
    public BannerInjectionSpec? BannerInjection { get; set; }
    public RcFileSpec? RcFile { get; set; }
    public MarkerSpec? Marker { get; set; }
}

/// <summary>
/// Integration delivered by staging the script as a shell-specific
/// startup file that the interpreter sources automatically on its own
/// initialisation. The worker writes the script to a per-worker
/// temporary directory and sets the environment variable the shell
/// consults to locate that directory before <c>CreateProcess</c>, so
/// the OSC-emitting hooks are already installed by the time the first
/// prompt is drawn. Bypasses the PTY entirely, avoiding the ZLE /
/// readline submission-ambiguity that breaks <c>delivery: pty_inject</c>
/// for zsh under ConPTY.
/// </summary>
public class RcFileSpec
{
    /// <summary>
    /// Name of the environment variable the shell reads to locate its
    /// rc-file directory. For zsh this is <c>ZDOTDIR</c>; other shells
    /// with equivalent hooks would use their own (e.g. future fish
    /// adapter: <c>XDG_CONFIG_HOME</c>).
    /// </summary>
    public string? DirEnvVar { get; set; }

    /// <summary>
    /// Basename the script is written as inside the staged directory
    /// (e.g. <c>.zshrc</c>). Must match what the shell reads from the
    /// directory pointed to by <see cref="DirEnvVar"/>.
    /// </summary>
    public string? FileName { get; set; }
}

public class TempfileSpec
{
    public string? Prefix { get; set; }
    public string? Extension { get; set; }
    public string? PathTemplate { get; set; }
    public string? InvocationTemplate { get; set; }
    public string? HistoryFilter { get; set; }
    public bool CleanupOnStart { get; set; }
    public int StaleTtlHours { get; set; }
}

public class BannerInjectionSpec
{
    public string Mode { get; set; } = "none";        // prepend_to_tempfile | write_before_pty | none
    public string? BannerTemplate { get; set; }
    public string? ReasonTemplate { get; set; }
}

public class MarkerSpec
{
    public string Primary { get; set; } = "";
    public string? Continuation { get; set; }
}

public class PromptSpec
{
    public string Strategy { get; set; } = "shell_integration";  // shell_integration | marker | regex
    public ShellIntegrationSpec? ShellIntegration { get; set; }
    public string? Primary { get; set; }                         // regex for marker / regex strategies
    public string? PrimaryRegex { get; set; }
    public string? Continuation { get; set; }
    // Bytes written to the PTY when `Continuation` matches during an AI command.
    // Forces the REPL out of its incomplete-statement state back to the primary
    // prompt so the existing primary-regex path can resolve the execute_command.
    // Without this, the continuation prompt is an absorbing state — no primary
    // match ever arrives, the tracker stays busy forever, and send_input's
    // Busy-gate lets raw bytes pile up on the continuation line.
    public string? ContinuationEscape { get; set; }
    public string Anchor { get; set; } = "line_start";
    public List<GroupCapture>? GroupCaptures { get; set; }
}

public class ShellIntegrationSpec
{
    public string Protocol { get; set; } = "osc633";
    public Osc633Markers? Markers { get; set; }
    public PropertyUpdates? PropertyUpdates { get; set; }
}

public class Osc633Markers
{
    public string? PromptStart { get; set; }
    public string? CommandInputStart { get; set; }
    public string? CommandExecuted { get; set; }
    public string? CommandFinished { get; set; }
    public string? PropertyUpdate { get; set; }
}

public class PropertyUpdates
{
    public string CwdKey { get; set; } = "Cwd";
}

public class GroupCapture
{
    public string Name { get; set; } = "";
    public int Group { get; set; }
    public string Type { get; set; } = "string";     // int | string | bool
    public string? Role { get; set; }                // monotonic_counter | nesting_level | node_name | mode_indicator
}

public class OutputSpec
{
    public int PostPromptSettleMs { get; set; } = 150;
    public bool StripAnsi { get; set; }
    public bool StripInputEcho { get; set; } = true;
    public string InputEchoStrategy { get; set; } = "osc_boundaries"; // osc_boundaries | deterministic_byte_match | fuzzy_byte_match | none
    public string LineEnding { get; set; } = "\n";
    public AsyncInterleaveSpec? AsyncInterleave { get; set; }
}

public class AsyncInterleaveSpec
{
    public string Strategy { get; set; } = "none";    // redraw_detect | quiesce | accept | none
    public string CaptureAs { get; set; } = "merge";  // out_of_band | merge | discard
}

public class InputSpec
{
    public string LineEnding { get; set; } = "\n";
    public string MultilineDetect { get; set; } = "none";      // prompt_based | wrapper | balanced_parens | indent_based | none
    public string MultilineDelivery { get; set; } = "direct";  // direct | tempfile | heredoc | wrapper | encoded_scriptblock
    public MultilineWrapperSpec? MultilineWrapper { get; set; }
    public BalancedParensSpec? BalancedParens { get; set; }
    public TempfileSpec? Tempfile { get; set; }
    public int ChunkDelayMs { get; set; }

    /// <summary>
    /// Byte sequence to write to the PTY immediately before the AI
    /// command payload, to clear whatever the user may have typed
    /// into the current prompt's line-editor buffer. Without this,
    /// user keystrokes accumulated while the console window had
    /// focus (e.g. the user accidentally typed into ripple's window
    /// after a fresh spawn stole focus from their editor) would be
    /// prepended to the AI command and both would submit together
    /// as one garbled line.
    ///
    /// **Default null** — opt-in per adapter. The obvious-looking
    /// choice <c>"\u0001\u000b"</c> (Ctrl-A + Ctrl-K) works against
    /// any <i>emacs-mode readline / PSReadLine / libedit / JLine</i>
    /// line editor, but several shipped REPLs deliberately run
    /// without a line editor at all — Python with
    /// <c>PYTHON_BASIC_REPL=1</c>, F# Interactive with
    /// <c>--readline-</c>, Racket <c>-i</c>, ABCL, CCL — and pass
    /// raw bytes straight to the parser, which rejects
    /// <c>U+0001</c> as an invalid non-printable character. Empirical
    /// verification per adapter is the only way to know what's
    /// safe: walk the adapter in ripple, type into its console
    /// window, run execute_command, and confirm the clear bytes
    /// wipe the buffer without syntax errors.
    ///
    /// The Groovy pattern applies: <c>clear_line</c> is declared
    /// alongside the YAML by whoever ships the adapter after running
    /// the smoke test. No default means "nothing written" — the
    /// user-typed bytes still corrupt the command, but no new
    /// corruption is introduced. Adapters that haven't verified yet
    /// should leave it null.
    ///
    /// Written only when direct PTY write is used — multi-line
    /// tempfile delivery replaces the whole line buffer with the
    /// dot-source invocation anyway, so clear_line is unnecessary
    /// there.
    /// </summary>
    public string? ClearLine { get; set; }
}

public class MultilineWrapperSpec
{
    public string Open { get; set; } = "";
    public string Close { get; set; } = "";
    public string Trigger { get; set; } = "auto";    // auto | always | never
}

public class BalancedParensSpec
{
    public List<string>? Open { get; set; }
    public List<string>? Close { get; set; }
    public List<string>? StringDelims { get; set; }
    public string? Escape { get; set; }
    public string? LineComment { get; set; }
    public List<string>? BlockComment { get; set; }

    /// <summary>
    /// Reader-macro char literal prefix (e.g. Racket's <c>#\</c>).
    /// The character immediately after this prefix is consumed
    /// verbatim and never contributes to bracket depth, so
    /// <c>#\(</c> is treated as a literal open paren token rather
    /// than an unclosed bracket. Schema §18 Q1 extension.
    /// </summary>
    public string? CharLiteralPrefix { get; set; }

    /// <summary>
    /// Reader-macro datum comment prefix (e.g. Racket's <c>#;</c>).
    /// The next balanced expression after this prefix is skipped
    /// entirely — brackets inside are still balanced, but the
    /// whole sub-expression does not contribute to the outer
    /// depth count. Schema §18 Q1 extension.
    /// </summary>
    public string? DatumCommentPrefix { get; set; }
}

public class ModeSpec
{
    public string Name { get; set; } = "";
    public string? Primary { get; set; }
    public bool Default { get; set; }
    public string? EnterKey { get; set; }
    public string? ExitKey { get; set; }
    public bool AutoEnter { get; set; }
    public string? Detect { get; set; }
    public bool Nested { get; set; }
    public int? LevelCapture { get; set; }
    /// <summary>
    /// Commands that advance execution position within this mode
    /// (step-in, step-over, step-out). Unlike exit_commands which
    /// leave the mode entirely (continue/resume), advance commands
    /// keep the debugger in the same paused mode but at a different
    /// source location. AI agents use this to distinguish "I stepped
    /// one line but I'm still paused" from "I resumed and left the
    /// breakpoint".
    /// </summary>
    public List<AdvanceCommandSpec>? AdvanceCommands { get; set; }
    public List<ExitCommandSpec>? ExitCommands { get; set; }
    public string? ExitDetect { get; set; }
    public List<GroupCapture>? GroupCaptures { get; set; }
}

public class ExitCommandSpec
{
    public string Command { get; set; } = "";
    public string Effect { get; set; } = "";          // return_to_toplevel | invoke_restart | resume | unwind_one_level
}

/// <summary>
/// A command that advances execution position within a paused mode
/// (debugger step operations). Same shape as ExitCommandSpec but with
/// a different effect vocabulary: step_in, step_over, step_out.
/// </summary>
public class AdvanceCommandSpec
{
    public string Command { get; set; } = "";
    public string Effect { get; set; } = "";          // step_in | step_over | step_out
}

public class CommandsSpec
{
    public string Prefix { get; set; } = "";
    public List<string>? Scope { get; set; }
    public string? Discovery { get; set; }
    public List<BuiltinCommand>? Builtin { get; set; }

    /// <summary>
    /// Structured command vocabulary for <c>family: debugger</c> adapters.
    /// Each field is a command template string with <c>{expr}</c>,
    /// <c>{target}</c>, <c>{line}</c>, <c>{file}</c> placeholders, or
    /// null when the operation is not supported by this debugger. AI
    /// agents read this section to discover "how do I step / print /
    /// set a breakpoint in this particular debugger" without parsing
    /// help text or guessing from the adapter name.
    /// </summary>
    public DebuggerCommandsSpec? Debugger { get; set; }
}

/// <summary>
/// Command templates for debugger operations. Every field is a string
/// template or null. Templates use <c>{expr}</c> for expressions,
/// <c>{target}</c> for breakpoint targets (function name, class.method),
/// <c>{line}</c> for line numbers, <c>{file}</c> for file paths.
/// </summary>
public class DebuggerCommandsSpec
{
    // --- Navigation (change execution position) ---

    /// <summary>Step into the next function call.</summary>
    public string? StepIn { get; set; }

    /// <summary>Execute one source line, stepping over calls.</summary>
    public string? StepOver { get; set; }

    /// <summary>Run until the current function returns to its caller.</summary>
    public string? StepOut { get; set; }

    /// <summary>Resume execution until the next breakpoint (or program end).</summary>
    public string? Continue { get; set; }

    /// <summary>Start (or restart) the target program.</summary>
    public string? Run { get; set; }

    // --- Inspection (read state without side effects) ---

    /// <summary>Evaluate and print an expression. Template: <c>p {expr}</c>.</summary>
    public string? Print { get; set; }

    /// <summary>Structured dump (arrays, objects). Template: <c>x {expr}</c>.</summary>
    public string? Dump { get; set; }

    /// <summary>Print the call stack / backtrace.</summary>
    public string? Backtrace { get; set; }

    /// <summary>List source lines around the current position.</summary>
    public string? SourceList { get; set; }

    /// <summary>Print all local variables in the current frame.</summary>
    public string? Locals { get; set; }

    /// <summary>Show the current file, line, and function.</summary>
    public string? Where { get; set; }

    /// <summary>Print function arguments (e.g. <c>p @_</c> in perl, <c>info args</c> in gdb).</summary>
    public string? Args { get; set; }

    // --- Breakpoints ---

    /// <summary>Set a breakpoint on a target (function/method name). Template: <c>b {target}</c>.</summary>
    public string? BreakpointSet { get; set; }

    /// <summary>Set a breakpoint at a specific source line. Template: <c>b {line}</c>.</summary>
    public string? BreakpointSetLine { get; set; }

    /// <summary>List all currently set breakpoints.</summary>
    public string? BreakpointList { get; set; }

    /// <summary>Delete all breakpoints at once.</summary>
    public string? BreakpointClearAll { get; set; }
}

public class BuiltinCommand
{
    public string Name { get; set; } = "";
    public string Syntax { get; set; } = "";
    public string Description { get; set; } = "";
}

public class SignalsSpec
{
    // Nullable: groovy and any future adapter whose host has a
    // destructive Ctrl-C handler (kills the process instead of
    // interrupting the running command) declares this as null so
    // AI clients and the runtime know there is no safe byte to
    // send for interrupt. The common case is still "\x03" — the
    // YAML default `interrupt: "\x03"` keeps shell/REPL adapters
    // that support cooperative interrupt concise.
    public string? Interrupt { get; set; } = "\x03";
    public string? Eof { get; set; } = "\x04";
    public string? Suspend { get; set; }
    public string? InterruptConfirm { get; set; }
}

public class LifecycleSpec
{
    public int ReadyTimeoutMs { get; set; }
    public ShutdownSpec Shutdown { get; set; } = new();
    public List<string>? RestartOn { get; set; }
}

public class ShutdownSpec
{
    public string Command { get; set; } = "exit";
    public int GraceMs { get; set; } = 1000;
    public string ForceSignal { get; set; } = "kill";
}

public class CapabilitiesSpec
{
    public bool Stateful { get; set; } = true;
    public bool Interrupt { get; set; }
    public bool MetaCommands { get; set; }
    public bool AutoModes { get; set; }
    public bool AsyncOutput { get; set; }

    /// <summary>
    /// true | false | unreliable. "unreliable" means always reports 0
    /// regardless of actual exit status (cmd.exe's PROMPT limitation).
    /// </summary>
    public string ExitCode { get; set; } = "false";

    public bool CwdTracking { get; set; }

    /// <summary>
    /// Shape of the cwd strings this adapter's shell reports.
    ///
    /// <c>windows_native</c> — Windows-style absolute paths
    /// (<c>C:\foo</c>). These can be handed to CreateProcess's
    /// <c>lpCurrentDirectory</c> parameter directly, so ripple can
    /// spawn a replacement console straight into a cached dead-cwd.
    /// pwsh / powershell / cmd / python-on-Windows / node-on-Windows.
    ///
    /// <c>posix</c> — POSIX paths (<c>/mnt/c/foo</c>, <c>/home/u</c>).
    /// Not valid as a Win32 working directory. ripple injects a
    /// <c>cd</c> preamble at the command level instead. bash / zsh
    /// under WSL, MSYS2, Git Bash.
    ///
    /// <c>none</c> — adapter does not track cwd at all.
    /// </summary>
    public string CwdFormat { get; set; } = "none";

    public bool JobControl { get; set; }
    public string? ShellIntegration { get; set; }
    public string? UserBusyDetection { get; set; }   // osc_b | process_polling | none
    public UserBusyDetectionParams? UserBusyDetectionParams { get; set; }

    /// <summary>
    /// Template for a runtime cd command that the adapter accepts on its
    /// already-running shell/REPL. <c>{path}</c> is substituted with the
    /// target cwd (after applying <see cref="CdCommandQuote"/> escape
    /// rules). Used by ConsoleManager to inject a cd preamble during
    /// auto-route / auto-spawn / reuse on start_console. Omit for
    /// adapters that don't participate in cwd management (or whose
    /// cwd concept doesn't map to a shell-like cd command).
    /// </summary>
    public string? CdCommand { get; set; }

    /// <summary>
    /// Quote context for the <c>{path}</c> substitution in
    /// <see cref="CdCommand"/>. Enum string: <c>single_quote_posix</c>
    /// (bash/zsh/sh — escape <c>'</c> as <c>'\''</c>), <c>single_quote_pwsh</c>
    /// (pwsh/powershell — escape <c>'</c> as <c>''</c>),
    /// <c>double_quote_cmd</c> (cmd — escape <c>"</c> as <c>""</c>).
    /// Must be set whenever <see cref="CdCommand"/> is set; null otherwise.
    /// </summary>
    public string? CdCommandQuote { get; set; }
}

public class UserBusyDetectionParams
{
    public int PollIntervalMs { get; set; }
    public int CpuBusyThresholdMs { get; set; }
    public bool IncludeChildren { get; set; }
}

public class ProbeSpec
{
    public string Eval { get; set; } = "";
    public string Expect { get; set; } = "";
}

public class AdapterTest
{
    public string Name { get; set; } = "";
    public string? Setup { get; set; }
    public string? Eval { get; set; }
    public string? Expect { get; set; }
    public bool ExpectError { get; set; }
    public int? ExpectExitCode { get; set; }
    /// <summary>
    /// Assert that the worker did NOT emit a non-zero `lastExitCode`
    /// field on this eval. Used to verify pwsh integration
    /// suppresses phantom LastExit reports — e.g. a manual
    /// `$LASTEXITCODE = 7` assignment must not surface as
    /// `LastExit: 7` in the AI-facing status line. Distinct from
    /// expect_exit_code (= the OSC D code, the overall pipeline
    /// outcome): a manual-assignment leak shows D=0 (pipeline
    /// succeeded) but L=7 (phantom native exit), and only the new
    /// assertion catches the L side.
    /// </summary>
    public bool ExpectNoLastExit { get; set; }
    public bool ExpectCwdUpdate { get; set; }
    public string? ExpectMode { get; set; }
    public int? ExpectLevel { get; set; }
    public int? ExpectCounter { get; set; }
    public string? ExpectOutOfBand { get; set; }
    public bool ExitCodeIsUnreliable { get; set; }
    public List<AdapterTest>? SetupSequence { get; set; }
    public string? ThenEval { get; set; }
    public int WaitMs { get; set; }
}
