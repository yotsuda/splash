using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Ripple.Services.Adapters;

namespace Ripple.Services;

/// <summary>
/// Manages shell console processes via Named Pipe discovery.
/// Pipe naming: RP.{proxyPid}.{agentId}.{consolePid} (owned) / RP.{consolePid} (unowned)
/// Category naming: each proxy instance gets a unique category (Animals, Gems, etc.)
/// and assigns names from that category to consoles.
/// </summary>
public class ConsoleManager
{
    public const string PipePrefix = "RP";

    private readonly ProcessLauncher _launcher;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _toolLock = new(1, 1);
    private readonly Dictionary<int, ConsoleInfo> _consoles = new();
    private readonly Dictionary<string, AgentSessionState> _agentSessions = new();

    // Category naming
    private readonly int _categoryIndex;
    private readonly Queue<string> _nameQueue = new();
    private string[]? _fixedNameOrder;
    private readonly Dictionary<int, string> _pidToTitle = new();

    public int ProxyPid { get; } = Environment.ProcessId;

    // This proxy's own binary version — sent in claim requests so workers
    // can detect cross-version re-claim (a strictly newer proxy reaching an
    // older worker with potentially incompatible pipe protocol).
    public static readonly string ProxyVersion =
        (typeof(ConsoleManager).Assembly.GetName().Version ?? new Version(0, 0)).ToString(3);

    // Shared memory for category allocation (same pattern as PowerShell.MCP)
    private static readonly string SharedMemoryFile = Path.Combine(Path.GetTempPath(), "Ripple.AllocatedConsoleCategories.dat");
    private const string MutexName = "Ripple.AllocatedConsoleCategories";
    private const int MaxEntries = 64;
    private const int EntrySize = 8;        // 4 bytes PID + 4 bytes category index
    private const int HeaderSize = 8;       // 4 bytes magic + 4 bytes count
    private const int SharedMemorySize = HeaderSize + (MaxEntries * EntrySize);
    private const int MagicNumber = 0x53504C54; // "SPLT"

    /// <summary>
    /// Console name categories — each proxy gets a unique category.
    /// Same set as PowerShell.MCP for consistency.
    /// </summary>
    private static readonly (string Name, string[] Words)[] Categories = new[]
    {
        ("Animals",      new[] { "Cat", "Dog", "Fox", "Wolf", "Bear", "Lion", "Tiger", "Panda", "Koala", "Rabbit", "Deer", "Zebra", "Gorilla", "Horse", "Elephant" }),
        ("Zodiac",       new[] { "Aries", "Taurus", "Gemini", "Capricorn", "Leo", "Virgo", "Libra", "Scorpio", "Aquarius", "Pisces" }),
        ("Gems",         new[] { "Sapphire", "Emerald", "Diamond", "Pearl", "Opal", "Topaz", "Ruby", "Amethyst", "Jade", "Garnet", "Onyx" }),
        ("Planets",      new[] { "Venus", "Mars", "Jupiter", "Saturn", "Neptune", "Pluto", "Titan", "Europa", "Luna" }),
        ("Colors",       new[] { "Red", "Blue", "Green", "Yellow", "Cyan", "Pink", "Purple", "Brown", "Gray", "White", "Black", "Indigo" }),
        ("Flowers",      new[] { "Rose", "Lily", "Iris", "Daisy", "Lotus", "Orchid", "Tulip", "Jasmine", "Peony", "Poppy", "Magnolia", "Hibiscus", "Sunflower" }),
        ("Birds",        new[] { "Eagle", "Falcon", "Sparrow", "Robin", "Swan", "Dove", "Parrot", "Penguin", "Owl", "Flamingo", "Hawk", "Raven", "Crow" }),
        ("Trees",        new[] { "Oak", "Pine", "Maple", "Cedar", "Willow", "Birch", "Elm", "Ash", "Cypress", "Bamboo", "Sequoia" }),
        ("Mountains",    new[] { "Mt.Fuji", "Everest", "K2", "Kilimanjaro", "Mt.Olympus", "Denali", "Mt.Blanc", "Matterhorn", "Vesuvius", "Etna", "Ararat" }),
        ("Seas",         new[] { "Pacific", "Atlantic", "Arctic", "Baltic", "Caspian", "Adriatic", "Norwegian", "Arabian", "Tasman", "Caribbean", "Coral" }),
        ("Mythology",    new[] { "Zeus", "Athena", "Hermes", "Artemis", "Hera", "Hades", "Poseidon", "Demeter", "Ares", "Apollo", "Aphrodite", "Dionysus", "Prometheus", "Orpheus" }),
        ("Music",        new[] { "Jazz", "Blues", "Rock", "Soul", "Funk", "Reggae", "Pop", "Punk", "Classical", "Techno", "Disco", "Gospel" }),
        ("Weather",      new[] { "Sunny", "Cloudy", "Misty", "Sleet", "Drizzle", "Haze", "Stormy", "Foggy", "Snowy", "Frosty", "Windy", "Thunder" }),
        ("Fruits",       new[] { "Mango", "Apple", "Peach", "Grape", "Melon", "Banana", "Plum", "Lemon", "Fig", "Cherry" }),
        ("Fish",         new[] { "Salmon", "Tuna", "Goldfish", "Swordfish", "Catfish", "Trout", "Piranha", "Angelfish", "Koi", "Sardine", "Marlin" }),
    };

    public ConsoleManager(ProcessLauncher launcher)
    {
        _launcher = launcher;
        _categoryIndex = InitializeCategory();
    }

    public void Initialize()
    {
        // Category already initialized in constructor
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupCategory();
    }

    private class AgentSessionState
    {
        // Rolling "most recently touched pid across ALL shells" — kept in
        // sync with ActivePidsByShell so legacy call-sites that don't have
        // a shell context (peek default, get_status default) still have a
        // reasonable single pid to fall back on. Routing no longer reads
        // this field — execute_command's shell parameter is mandatory and
        // the per-shell MRU stack below is the authoritative source.
        public int ActivePid { get; set; }
        public readonly HashSet<int> KnownBusyPids = new();

        // Per-shell MRU stack of pids, keyed by the resolved shell path
        // (full path, PathComparison-cased). Index 0 is "the active console
        // for this shell" — the one a bare execute_command shell=X should
        // route to — and subsequent entries are MRU fallbacks that fill in
        // when the top is busy. Pids are moved to index 0 on every use
        // (touch-on-use), pushed at index 0 on fresh start, and removed
        // wholesale when the console dies.
        //
        // Why per-shell instead of a global single ActivePid: with `shell`
        // now mandatory on execute_command we always know which shell the
        // caller wants, and a global active was the bug that let a psql
        // session's "active" silently swallow a subsequent shell=pwsh call
        // before — the single-active model couldn't represent "pwsh's
        // active is A while psql's active is B", so routing collapsed to
        // whichever was most recent. Splitting by shell lets each shell
        // keep its own independent MRU without cross-shell interference.
        public readonly Dictionary<string, List<int>> ActivePidsByShell =
            new(PathComparer);

        // Per-shell snapshot of the last-observed cwd + shell path for
        // consoles that have died. Replaces the old single LastActiveCwd/
        // LastActiveShellPath pair, which could only remember one dead
        // console across the whole agent. Now each shell independently
        // remembers its own tail state so `shell=pwsh` after pwsh's active
        // died can resurrect at the right cwd without the psql side
        // clobbering the snapshot in between. Consumed by PlanExecutionAsync
        // on the next execute_command for that shell and cleared after use.
        public readonly Dictionary<string, (string Cwd, string ShellPath)>
            LastActiveByShell = new(PathComparer);

        // Per-logical-shell cwd continuity (keyed by the shell executable's
        // resolved absolute path). Populated as the AI works across shells;
        // consulted when a new physical console is needed for a shell this
        // agent has used before (auto-route to standby, auto-spawn after
        // close, reuse-on-start_console) so the cd preamble lands at the
        // cwd the AI last observed, not wherever the fresh shell naturally
        // starts. Distinct from LastActiveCwd, which only remembers the
        // single most-recently-active console across all shells and is
        // cleared after one use.
        public readonly Dictionary<string, string> ShellCwd = new(StringComparer.OrdinalIgnoreCase);

        // First-natural-cwd capture per logical shell. Set once, on the
        // first fresh launch where the user didn't supply an explicit cwd —
        // whatever the shell's own startup picked (typically $HOME) is
        // recorded here and used later when the AI calls start_console
        // without a cwd argument and ripple needs to restore the logical
        // shell to its "home". Keyed by shell_path, same as ShellCwd.
        // Remains UNKNOWN (missing key) until such a natural fresh launch
        // has happened; design.md §3-B covers the fallback behavior.
        public readonly Dictionary<string, string> ShellHome = new(StringComparer.OrdinalIgnoreCase);
    }

    private AgentSessionState GetOrCreateAgentState(string agentId)
    {
        if (!_agentSessions.TryGetValue(agentId, out var state))
        {
            state = new AgentSessionState();
            _agentSessions[agentId] = state;
        }
        return state;
    }

    private void MarkPipeBusy(string agentId, int pid)
    {
        lock (_lock) GetOrCreateAgentState(agentId).KnownBusyPids.Add(pid);
    }

    private void UnmarkPipeBusy(string agentId, int pid)
    {
        lock (_lock) GetOrCreateAgentState(agentId).KnownBusyPids.Remove(pid);
    }

    private List<int> SnapshotBusyPids(string agentId)
    {
        lock (_lock) return GetOrCreateAgentState(agentId).KnownBusyPids.ToList();
    }

    /// <summary>
    /// Move (or push) a pid to the top of its shell's MRU stack. Both the
    /// per-shell stack and the legacy global ActivePid are updated in one
    /// shot so the two sources of truth never drift. Call under _lock.
    /// </summary>
    private static void TouchActivePid(AgentSessionState state, string shellPath, int pid)
    {
        if (!state.ActivePidsByShell.TryGetValue(shellPath, out var stack))
        {
            stack = new List<int>();
            state.ActivePidsByShell[shellPath] = stack;
        }
        stack.Remove(pid);
        stack.Insert(0, pid);
        state.ActivePid = pid;
    }

    /// <summary>
    /// Remove a pid from every per-shell MRU stack and from the legacy
    /// global ActivePid slot. Called when a console dies (ClearDeadConsole,
    /// CollectClosedConsoles) so routing can't pick a pid that no longer
    /// has a process behind it. Call under _lock.
    /// </summary>
    private static void RemoveActivePid(AgentSessionState state, int pid)
    {
        foreach (var stack in state.ActivePidsByShell.Values)
            stack.Remove(pid);
        // Strip any now-empty stacks so LastActiveByShell is the sole
        // survivor for "this shell is temporarily pid-less but we know
        // where it was working".
        foreach (var key in state.ActivePidsByShell
                     .Where(kv => kv.Value.Count == 0)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            state.ActivePidsByShell.Remove(key);
        }
        if (state.ActivePid == pid)
            state.ActivePid = 0;
    }

    /// <summary>
    /// If the given pid is currently tracked as any shell's active (i.e.
    /// appears at least once in an ActivePidsByShell stack), snapshot its
    /// last-known cwd + shell path into LastActiveByShell[shellPath] so the
    /// next execute_command for that shell can seamlessly auto-start a
    /// replacement at the same cwd. No-op for consoles not in the tracking
    /// table. Call this BEFORE ClearDeadConsole so the ConsoleInfo is still
    /// readable.
    /// </summary>
    private void RememberClosedActive(string agentId, int pid)
    {
        lock (_lock)
        {
            var state = GetOrCreateAgentState(agentId);
            var info = _consoles.GetValueOrDefault(pid);
            if (info == null) return;
            bool wasTracked = state.ActivePidsByShell.Values.Any(s => s.Contains(pid));
            if (!wasTracked) return;
            if (info.LastAiCwd != null)
                state.LastActiveByShell[info.ShellPath] = (info.LastAiCwd, info.ShellPath);
        }
    }

    public string AllocateSubAgentId()
    {
        // 8 hex chars from a v4 GUID = 32 bits of entropy. Collisions across a
        // single proxy's lifetime are vanishingly unlikely, and if one ever
        // happens the two sub-agents land on the same AgentSessionState bucket
        // — the same failure mode as if the caller passed a duplicate agent_id
        // manually. No tracking table needed.
        return $"sa-{Guid.NewGuid():N}"[..11];
    }

    /// <summary>
    /// Assigns a console name from the category and returns display name like "#12345 Sparrow".
    /// </summary>
    private string AssignConsoleName(int pid)
    {
        lock (_lock)
        {
            if (_pidToTitle.TryGetValue(pid, out var existing))
                return existing;

            if (_nameQueue.Count == 0)
                RefillNames();

            var name = _nameQueue.Dequeue();
            var title = $"#{pid} {name}";
            _pidToTitle[pid] = title;
            return title;
        }
    }

    /// <summary>
    /// Start or reuse a console. Enforces single-shell-type per session.
    /// Serialized via _toolLock to prevent concurrent state mutations.
    /// </summary>
    public async Task<StartConsoleResult> StartConsoleAsync(string? shell, string? cwd, string? reason, string agentId = "default", string? banner = null)
    {
        await _toolLock.WaitAsync();
        try { return await StartConsoleInnerAsync(shell, cwd, reason, agentId, banner); }
        finally { _toolLock.Release(); }
    }

    private async Task<StartConsoleResult> StartConsoleInnerAsync(string? shell, string? cwd, string? reason, string agentId, string? banner = null)
    {
        var rawShell = shell ?? GetDefaultShell();

        // Reject shell values that contain command-line options (e.g., "bash --login -i").
        var fileName = Path.GetFileName(rawShell);
        if (fileName.Contains(' '))
            throw new InvalidOperationException(
                $"Shell parameter must be a shell name or path, not a command line. Got: '{rawShell}'");

        // Resolve to full path via PATH search (e.g., "pwsh" → "C:\Program Files\PowerShell\7\pwsh.exe")
        var resolvedShell = ShellPathResolver.Resolve(rawShell);
        var shellFamily = NormalizeShellFamily(resolvedShell);
        bool forceNew = !string.IsNullOrEmpty(reason);

        if (!forceNew)
        {
            var standby = await FindStandbyConsoleAsync(agentId, resolvedShell);
            if (standby != null)
            {
                lock (_lock) TouchActivePid(GetOrCreateAgentState(agentId), resolvedShell, standby.Value.Pid);

                var reusePipe = _consoles.GetValueOrDefault(standby.Value.Pid)?.PipePath;

                // Reposition the reused standby to the target cwd. An
                // explicit cwd wins; otherwise default to the user's home
                // directory so an unspecified start_console acts like a
                // fresh session. Skip the cd if the console is already at
                // the target directory — avoids unnecessary noise in the
                // terminal when the standby happens to be at home already.
                var targetCwd = !string.IsNullOrEmpty(cwd)
                    ? cwd
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (reusePipe != null)
                {
                    // Query current cwd to decide whether cd is needed.
                    var currentCwd = await QueryConsoleCwdAsync(reusePipe);
                    var needsCd = currentCwd == null
                        || !string.Equals(
                            Path.GetFullPath(currentCwd), Path.GetFullPath(targetCwd),
                            StringComparison.OrdinalIgnoreCase);

                    if (needsCd)
                    {
                        var cdPreamble = BuildCdPreamble(shellFamily, targetCwd);
                        if (cdPreamble != null)
                        {
                            try
                            {
                                await SendPipeRequestAsync(reusePipe, w =>
                                {
                                    w.WriteString("type", "execute");
                                    w.WriteString("command", cdPreamble.TrimEnd('&', ' '));
                                    w.WriteNumber("timeout", 5000);
                                }, TimeSpan.FromSeconds(8));
                            }
                            catch { /* best-effort */ }
                        }
                    }
                    RecordShellCwd(standby.Value.Pid, targetCwd);
                }

                // Display banner on reused console via pipe. Pass the target
                // cwd too so the worker can use it as the kick command —
                // writing a bare "\r" after the banner would make shells
                // (pwsh/bash/zsh via OSC 633 integration) report it as a
                // user-typed empty command, flipping _userCommandBusy for
                // the duration of the shell's empty-line handling and
                // making the start_console response claim Busy even
                // though nothing is actually running. Issuing the cd_command
                // as an AI-marked command sidesteps that — the OSC B/C pair
                // the shell emits while it runs the cd is gated by the
                // tracker's _isAiCommand flag.
                if (!string.IsNullOrEmpty(banner) || !string.IsNullOrEmpty(reason))
                {
                    if (reusePipe != null)
                        try { await SendPipeRequestAsync(reusePipe, w => { w.WriteString("type", "display_banner"); w.WriteStringOrNull("banner", banner); w.WriteStringOrNull("reason", reason); w.WriteStringOrNull("cwd", targetCwd); }, TimeSpan.FromSeconds(10)); } catch { }
                }

                string? reusedCwd;
                lock (_lock) reusedCwd = _consoles.GetValueOrDefault(standby.Value.Pid)?.LastAiCwd;
                return new StartConsoleResult("reused", standby.Value.Pid, standby.Value.DisplayName, shellFamily, reusedCwd);
            }
        }

        // Launch ripple.exe --console mode with ConPTY.
        // Banner/reason passed as CLI args so the worker can display them before the first prompt.
        int pid = _launcher.LaunchConsoleWorker(ProxyPid, agentId, resolvedShell, cwd, banner, reason);

        var displayName = AssignConsoleName(pid);
        var pipeName = GetPipeName(agentId, pid);
        lock (_lock)
        {
            _consoles[pid] = new ConsoleInfo(pipeName, displayName, shellFamily, resolvedShell);
            TouchActivePid(GetOrCreateAgentState(agentId), resolvedShell, pid);
        }

        await WaitForPipeReadyAsync(pipeName, TimeSpan.FromSeconds(30));

        // Query the worker for the actual cwd in shell-native format
        // (e.g., /mnt/c/foo for WSL bash, C:\foo for pwsh).
        var initialCwd = await QueryConsoleCwdAsync(pipeName);
        if (initialCwd != null)
        {
            RecordShellCwd(pid, initialCwd);
            // First natural-launch cwd observation for this logical shell —
            // capture it as the "home" to return to on later
            // start_console calls without an explicit cwd. The capture is
            // gated on `cwd == null` so that a user who explicitly named a
            // cwd on first launch doesn't freeze that path as the shell's
            // home. No-op if we already have a home recorded.
            if (cwd == null) RecordShellHomeIfUnset(pid, initialCwd);
        }

        try { await SendPipeRequestAsync(pipeName, w => { w.WriteString("type", "set_title"); w.WriteString("title", displayName); }, TimeSpan.FromSeconds(3)); }
        catch { /* best-effort */ }

        return new StartConsoleResult("started", pid, displayName, shellFamily, initialCwd);
    }

    /// The hard ceiling a single execute_command can spend inside the
    /// MCP tool call, in seconds. Just under the 180s (3-minute) ceiling
    /// the MCP protocol imposes on tool-call response latency. Kept in
    /// sync with CommandTracker.PreemptiveTimeoutMs so the worker's
    /// internal timer always fires before the pipe wait gives up.
    public const int MaxExecuteTimeoutSeconds = 170;

    /// <summary>
    /// Execute a command on the active console via Named Pipe.
    /// Serialized via _toolLock.
    /// </summary>
    public async Task<ExecuteResult> ExecuteCommandAsync(string command, int timeoutSeconds, string agentId = "default", string? shell = null)
    {
        // Cap the caller-supplied timeout at the MCP ceiling so the
        // pipe wait + worker timer both unwind within the 3-minute
        // tool-call window. Callers that ask for longer get a clean
        // preemptive-timeout response at 170s and can keep polling
        // via wait_for_completion. 0 is the "interactive" sentinel —
        // ripple flips to cache mode as soon as the pipeline is on the
        // PTY so execute_command returns immediately and the drain
        // wrapper salvages the result on the next tool call.
        timeoutSeconds = Math.Clamp(timeoutSeconds, 0, MaxExecuteTimeoutSeconds);
        await _toolLock.WaitAsync();
        try { return await ExecuteCommandInnerAsync(command, timeoutSeconds, agentId, shell); }
        finally { _toolLock.Release(); }
    }

    private async Task<ExecuteResult> ExecuteCommandInnerAsync(string command, int timeoutSeconds, string agentId, string? shell)
    {
        var plan = await PlanExecutionAsync(command, agentId, shell);
        if (plan.EarlyResult != null) return plan.EarlyResult;

        return await ExecutePlannedCommandAsync(
            consolePid: plan.ConsolePid,
            pipeName: plan.PipeName,
            command: command,
            cdCommand: plan.CdCommand,
            expectedCwdAfterCd: plan.PreambleCwd,
            timeoutSeconds: timeoutSeconds,
            agentId: agentId,
            shell: shell,
            routingNotice: plan.RoutingNotice);
    }

    /// <summary>
    /// Output of the routing phase. Either a concrete target console to run
    /// the command on (ConsolePid + PipeName + optional CdCommand that must
    /// be executed first, possibly with a RoutingNotice that should be
    /// surfaced to the AI) or an EarlyResult that short-circuits the execute
    /// entirely — used for the "switched, re-execute" and "cwd drifted,
    /// verify" paths where the planner refuses to run the command on the
    /// AI's behalf. CdCommand is a standalone shell command (e.g.
    /// `Set-Location 'C:\...'`) that gets run as its own execute so the
    /// AI's command stays pure and the status line can show what the AI
    /// asked for rather than the proxy-injected cd preamble.
    /// </summary>
    private sealed record ExecutionPlan(
        int ConsolePid,
        string PipeName,
        string? CdCommand,
        string? PreambleCwd,
        string? RoutingNotice,
        ExecuteResult? EarlyResult);

    /// <summary>
    /// Decide which console the command should run on, whether a cd preamble
    /// is needed, and whether any drift / cross-shell-switch warning should
    /// be surfaced. This is all side-effect-aware (MarkPipeBusy, LastAiCwd
    /// updates, auto-starts via StartConsoleInnerAsync) because the decisions
    /// and the state updates are entangled — separating them would just mean
    /// the caller has to replay the decisions.
    /// </summary>
    private async Task<ExecutionPlan> PlanExecutionAsync(string command, string agentId, string? shell)
    {
        // Resolve shell to full path for consistent matching
        var resolvedShell = shell != null ? ShellPathResolver.Resolve(shell) : null;

        int initialActivePid;
        int consolePid;
        string pipeName;
        string? sourceShellFamily;
        // Snapshot of a recently-closed active console (shell exited,
        // pipe broken, window closed by user). If present, it seeds
        // sourceShellFamily + sourceCwd + resolvedShell so the auto-start
        // path below can spin up a same-family replacement at the AI's
        // last known cwd and run the command there in one shot, instead
        // of a "switched to Foo, please re-execute" warning.
        string? cachedDeadCwd = null;
        // Snapshot of the requested shell's MRU stack, taken under _lock
        // so the fallback-walk below sees a stable list even if another
        // thread close a console mid-plan. Contains the resolvedShell's
        // pids in MRU order (index 0 = top = current "active for this shell").
        List<int> mruStack = new();

        lock (_lock)
        {
            var state = GetOrCreateAgentState(agentId);

            // Per-shell MRU stack is the authoritative routing source when
            // the caller named a shell. Fall back to the legacy
            // global-ActivePid pick only when shell is null — that path
            // remains only for internal callers that don't know the target
            // shell; the public execute_command tool already rejects null.
            if (resolvedShell != null
                && state.ActivePidsByShell.TryGetValue(resolvedShell, out var trackedStack)
                && trackedStack.Count > 0)
            {
                mruStack = new List<int>(trackedStack);
                initialActivePid = mruStack[0];
            }
            else
            {
                initialActivePid = state.ActivePid;
            }
            consolePid = initialActivePid;
            sourceShellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily;

            // Lazy-recovery: if no live pid is tracked for the requested
            // shell, see whether the shell had a recently-dead console we
            // can resurrect at the same cwd. LastActiveByShell is
            // per-shell, so a psql session that died mid-work doesn't
            // leak its cwd into the next shell=pwsh call the way the old
            // single LastActiveCwd slot did.
            if (initialActivePid == 0 && resolvedShell != null
                && state.LastActiveByShell.TryGetValue(resolvedShell, out var last))
            {
                cachedDeadCwd = last.Cwd;
                sourceShellFamily = NormalizeShellFamily(last.ShellPath);
                // Consume once so subsequent calls don't keep re-applying.
                state.LastActiveByShell.Remove(resolvedShell);
            }

            // Check if active console matches the requested shell (by full path)
            if (consolePid != 0 && resolvedShell != null)
            {
                var info = _consoles.GetValueOrDefault(consolePid);
                if (info != null && !info.ShellPath.Equals(resolvedShell, PathComparison))
                    consolePid = 0; // Force finding/starting a matching console
            }

            pipeName = consolePid != 0
                ? _consoles.GetValueOrDefault(consolePid)?.PipePath ?? GetPipeName(agentId, consolePid)
                : "";
        }

        // Query the active console's status: get cwd (for drift detection
        // and cd preamble) and detect busy. If busy, the active console is
        // running something — route to a sibling so AI's command doesn't
        // queue indefinitely. If idle, we'll compare the live cwd to AI's
        // last observed cwd (LastAiCwd) below to detect drift; on drift,
        // a cd preamble restores AI's intended cwd in the same console
        // (no need to abandon the console — shared state stays usable).
        string? sourceCwd = cachedDeadCwd;
        bool activeBusy = false;
        if (initialActivePid != 0 && IsProcessAlive(initialActivePid))
        {
            string? sourcePipe;
            lock (_lock) sourcePipe = _consoles.GetValueOrDefault(initialActivePid)?.PipePath;
            if (sourcePipe != null)
            {
                try
                {
                    var statusResp = await SendPipeRequestAsync(sourcePipe,
                        w => w.WriteString("type", "get_status"), TimeSpan.FromSeconds(3));
                    sourceCwd = statusResp.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
                    var statusStr = statusResp.TryGetProperty("status", out var stProp) ? stProp.GetString() : null;
                    // "completed" = the worker has one or more cached results from
                    // earlier flipped commands that haven't been drained yet. Routing
                    // away here isn't strictly required — RegisterCommand no longer
                    // clears _cachedResults and AppendCachedOutputs drains it at the
                    // tail of this tool call — but surfacing those results in the
                    // same response as a brand-new inline execute would conflate
                    // two unrelated command histories. Stay conservative: treat
                    // completed as busy for routing purposes so the fresh execute
                    // lands on a sibling console and the cached results drain
                    // cleanly on their own line.
                    activeBusy = statusStr == "busy" || statusStr == "completed";
                }
                catch { }
            }
        }

        bool isSwitching = false;

        // Route away from the active console only when it's busy. When idle
        // but cwd drifted (user moved cwd, or AI's previous command did a cd
        // we haven't observed yet), we stay on the same console and inject a
        // cd preamble that restores AI's intended cwd — the shared console
        // model preserves variables, modules, and history that the user and
        // AI built up together, so abandoning it on every cwd touch would
        // break the "shared workspace" contract.
        // When the caller didn't request a specific shell, pin the search/auto-start
        // to the busy console's own shell path so we stay same-family — crossing
        // shell families here would strand the AI in a foreign cwd it doesn't
        // know how to translate (bash /mnt/c/... vs pwsh C:\...).
        if (activeBusy && consolePid == initialActivePid)
        {
            // Remember that this console is running something so the tool
            // response can include a background-busy line for it. Without
            // this, `KnownBusyPids` is only populated from AI-command
            // timeouts, so user-initiated activity like `pause` silently
            // disappears from the background busy report even though we
            // just detected and routed away from it. CollectBusyStatuses
            // will self-heal the entry once the console returns to idle.
            MarkPipeBusy(agentId, initialActivePid);

            consolePid = 0;
            if (resolvedShell == null)
            {
                lock (_lock) resolvedShell = _consoles.GetValueOrDefault(initialActivePid)?.ShellPath;
            }

            // Walk the requested shell's MRU stack (skipping the busy top)
            // to prefer recently-used siblings over arbitrary standby picks.
            // We pick any non-busy candidate — if the user has touched the
            // candidate's cwd since AI's last command, the cd preamble
            // computed downstream restores AI's intended cwd before the
            // pipeline runs.
            foreach (var candidate in mruStack.Skip(1))
            {
                if (candidate == initialActivePid) continue;
                if (!IsProcessAlive(candidate)) continue;
                bool candidateBusy;
                lock (_lock) candidateBusy = GetOrCreateAgentState(agentId).KnownBusyPids.Contains(candidate);
                if (candidateBusy) continue;

                // Live status check: a candidate is only a usable standby if
                // it's idle (status == "standby" or "completed").
                string? candidatePipe;
                lock (_lock) candidatePipe = _consoles.GetValueOrDefault(candidate)?.PipePath;
                if (candidatePipe != null)
                {
                    try
                    {
                        var candResp = await SendPipeRequestAsync(candidatePipe,
                            w => w.WriteString("type", "get_status"), TimeSpan.FromSeconds(2));
                        var candStatus = candResp.TryGetProperty("status", out var cstat) ? cstat.GetString() : null;
                        if (candStatus != "standby" && candStatus != "completed") continue;
                    }
                    catch
                    {
                        // Worker not responding — treat as unhealthy, skip.
                        continue;
                    }
                }

                consolePid = candidate;
                isSwitching = true;
                lock (_lock)
                {
                    pipeName = _consoles.GetValueOrDefault(consolePid)?.PipePath ?? GetPipeName(agentId, consolePid);
                    // Touch: the chosen fallback becomes the new top of this
                    // shell's MRU stack so the next execute_command keeps
                    // using it rather than retrying the busy top. Cached
                    // output from the still-busy original top will surface
                    // via AppendCachedOutputs when it eventually finishes.
                    var info = _consoles.GetValueOrDefault(consolePid);
                    if (info != null)
                        TouchActivePid(GetOrCreateAgentState(agentId), info.ShellPath, consolePid);
                }
                break;
            }
        }

        // No active console, or active console is wrong shell type, or busy → switch or auto-start
        if (consolePid == 0 || !IsProcessAlive(consolePid))
        {
            isSwitching = true;

            var standby = await FindStandbyConsoleAsync(agentId, resolvedShell);
            if (standby != null)
            {
                consolePid = standby.Value.Pid;
                lock (_lock)
                {
                    // Touch the standby in the per-shell MRU stack so it
                    // becomes the new "active for this shell" — same as if
                    // we'd freshly launched it — and route it to the top
                    // for the next execute_command.
                    string? standbyShell;
                    var info = _consoles.GetValueOrDefault(consolePid);
                    standbyShell = info?.ShellPath ?? resolvedShell;
                    if (standbyShell != null)
                        TouchActivePid(GetOrCreateAgentState(agentId), standbyShell, consolePid);
                    else
                        GetOrCreateAgentState(agentId).ActivePid = consolePid;
                    pipeName = _consoles.GetValueOrDefault(consolePid)?.PipePath ?? GetPipeName(agentId, consolePid);
                }
            }
            else
            {
                // Auto-start a new console. When we're in the lazy-recovery
                // path (cachedDeadCwd populated from a freshly-dead active
                // console) and the target shell is Windows-native (pwsh,
                // powershell, cmd) we can hand the cached Windows path
                // straight to CreateProcess as workingDirectory — the
                // shell comes up in the right place from its very first
                // prompt, so no cd preamble or Set-Location injection is
                // needed at all. Bypass the rest of the planning phase
                // in that case. For bash/zsh on Windows we still pass
                // null and fall through to the cd preamble branch below,
                // because those shells report POSIX paths
                // (/mnt/c/... or /c/...) that CreateProcess can't use as
                // a working directory.
                var targetShellPath = resolvedShell ?? GetDefaultShell();
                // Only shells that report cwd in Windows-native form
                // (pwsh/powershell/cmd, and REPLs on Windows that use
                // process.cwd() / os.getcwd()) can have their cached
                // dead cwd handed straight to CreateProcess's
                // lpCurrentDirectory. For posix-cwd shells (bash/zsh
                // via WSL or MSYS2) the path would be /mnt/c/... which
                // Win32 rejects — those fall through to the preamble
                // branch below. Phase C(postscript): this is the last
                // hardcoded shell-family helper in ConsoleManager; the
                // adapter capability takes over.
                var targetAdapter = AdapterRegistry.Default?.Find(NormalizeShellFamily(targetShellPath));
                var targetCwdFormat = targetAdapter?.Capabilities.CwdFormat ?? "none";
                if (cachedDeadCwd != null && targetCwdFormat == "windows_native")
                {
                    var startResult = await StartConsoleInnerAsync(targetShellPath, cachedDeadCwd, null, agentId);
                    consolePid = startResult.Pid;
                    lock (_lock)
                        pipeName = _consoles.GetValueOrDefault(consolePid)?.PipePath ?? GetPipeName(agentId, consolePid);
                    // StartConsoleInnerAsync has already set LastAiCwd to
                    // the freshly-started console's live cwd (which is
                    // cachedDeadCwd). Return a plan that points directly
                    // at the new console — no preamble, no warning.
                    return new ExecutionPlan(consolePid, pipeName, CdCommand: null, PreambleCwd: null, RoutingNotice: null, EarlyResult: null);
                }

                var fallbackStart = await StartConsoleInnerAsync(targetShellPath, null, null, agentId);
                consolePid = fallbackStart.Pid;
                lock (_lock)
                    pipeName = _consoles.GetValueOrDefault(consolePid)?.PipePath ?? GetPipeName(agentId, consolePid);
            }
        }

        // Determine target shell family and check cross-shell compatibility for cd preamble
        string? targetShellFamily;
        lock (_lock) targetShellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily;

        bool sameShellFamily = sourceShellFamily != null && targetShellFamily != null &&
                                sourceShellFamily.Equals(targetShellFamily, StringComparison.OrdinalIgnoreCase);

        // Standalone cd command to run before the AI's command, when
        // routing moved us to a console whose cwd differs from the AI's
        // intended cwd. Running it as a separate execute (instead of
        // prepending `cd '...'; ` to the AI's command) keeps the AI-
        // facing status line honest: the Pipeline field shows what the
        // AI actually asked for, not a proxy-manufactured composite.
        // expectedCwdAfterCd is the cwd we expect to reach; after the
        // cd runs we compare the worker's reported cwd against it and
        // bail if they don't match — catches the pwsh case where
        // `Set-Location` to a non-existent path leaves $LASTEXITCODE
        // unchanged (= 0) so an exit-code-only check would silently
        // let the AI command run in whatever cwd cd got stuck at.
        string? cdCommand = null;
        string? expectedCwdAfterCd = null;

        // Out-of-band notice attached to the success result, used when we
        // silently corrected for a user-initiated cwd change in the source
        // console so the AI sees what ripple did on its behalf.
        string? routingNotice = null;

        if (isSwitching && sourceCwd != null && sameShellFamily)
        {
            // The AI's "intended cwd" is whatever it last saw a successful
            // command complete at on the source console — i.e. source's
            // LastAiCwd. The source's *live* cwd may have drifted from that
            // if the human user manually cd'd before kicking off the busy
            // command we're routing around. Honor the AI's intent: when
            // there's drift, use LastAiCwd as the preamble target instead
            // of the live cwd so the AI keeps working in the directory it
            // thinks it's in. The source's LastAiCwd is intentionally NOT
            // updated, so if the AI later returns to the source console it
            // will either match (user cd'd back) or trigger the same-console
            // mismatch warning, which gives the AI the explicit signal it
            // needs to verify and re-execute.
            string? sourceLastAiCwd = null;
            if (initialActivePid != 0)
                lock (_lock) sourceLastAiCwd = _consoles.GetValueOrDefault(initialActivePid)?.LastAiCwd;

            // Drift detection by direct cwd comparison: if the source's live
            // cwd differs from what AI last observed there, something moved
            // it (user manual cd, or AI's previous command did a cd we
            // haven't rolled forward into LastAiCwd yet). Either way, AI's
            // intended cwd is sourceLastAiCwd, not the live cwd.
            bool sourceDrifted = sourceLastAiCwd != null && sourceCwd != null
                && !CwdEquals(sourceCwd, sourceLastAiCwd);

            // preambleCwd is what the new console will be cd'd to before
            // the AI command runs. When source has drifted we restore the
            // AI's last known cwd; otherwise we propagate the live cwd
            // (which equals LastAiCwd in the no-drift case anyway).
            var preambleCwd = sourceDrifted ? sourceLastAiCwd! : sourceCwd;

            var cdPreamble = preambleCwd != null
                ? BuildCdPreamble(targetShellFamily!, preambleCwd)
                : null;
            if (cdPreamble != null)
            {
                // Strip trailing `&` / `;` / ` ` so the preamble stands on
                // its own as a standalone shell command. BuildCdPreamble
                // produces `cd '...' && ` / `Set-Location '...'; ` /
                // `cd /d "..." && `; after TrimEnd we get the bare cd.
                cdCommand = cdPreamble.TrimEnd('&', ';', ' ');
                expectedCwdAfterCd = preambleCwd;
                RecordShellCwd(consolePid, preambleCwd);
            }

            if (sourceDrifted)
            {
                var sourceDisplay = _consoles.GetValueOrDefault(initialActivePid)?.DisplayName ?? $"#{initialActivePid}";
                var targetDisplay = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
                routingNotice =
                    $"Note: source {sourceDisplay} was moved by user to '{sourceCwd}'; " +
                    $"ran in {targetDisplay} at your last known cwd '{sourceLastAiCwd}'.";
            }
        }
        else if (isSwitching)
        {
            // Cases reaching this branch:
            //   - Cross-shell switch where the AI has already used the target
            //     shell before (ShellCwd[targetShellPath] is populated) —
            //     silently restore that cwd via cd preamble, no refuse.
            //   - Cross-shell switch to a shell the AI has never used (first
            //     execute_command for this logical shell, no previous state)
            //     — refuse with a first-use notice so the AI can confirm
            //     where the new console landed before running anything
            //     destructive there (design.md §5-C Case 6).
            //   - Fresh start with no previous active console — same
            //     first-use refuse path.
            //   - Switch to standby with no source cwd — first-use refuse.
            // Involuntary cross-shell switches (active busy with no shell
            // param) don't reach this branch; resolvedShell is pinned to
            // the busy source earlier so find/auto-start stays same-family.
            string? targetShellPath;
            lock (_lock) targetShellPath = _consoles.GetValueOrDefault(consolePid)?.ShellPath;

            string? storedShellCwd = null;
            if (!string.IsNullOrEmpty(targetShellPath))
            {
                lock (_lock)
                    GetOrCreateAgentState(agentId).ShellCwd.TryGetValue(targetShellPath, out storedShellCwd);
            }

            if (!string.IsNullOrEmpty(storedShellCwd) && !string.IsNullOrEmpty(targetShellFamily))
            {
                // Silent restore: this shell has a remembered cwd from the
                // agent's prior work. Issue the cd as a preamble and let
                // the pipeline run, same as the same-family branch does.
                var cdPreamble = BuildCdPreamble(targetShellFamily!, storedShellCwd!);
                if (cdPreamble != null)
                {
                    cdCommand = cdPreamble.TrimEnd('&', ';', ' ');
                    expectedCwdAfterCd = storedShellCwd;
                    RecordShellCwd(consolePid, storedShellCwd);
                }
                var targetDisplay = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
                routingNotice = routingNotice
                    ?? $"Note: switched to {targetDisplay} (shell={targetShellFamily}); restored cwd to '{storedShellCwd}'.";
                return new ExecutionPlan(consolePid, pipeName, cdCommand, expectedCwdAfterCd, routingNotice, EarlyResult: null);
            }

            // First-use refuse: no stored cwd means this is the agent's
            // first interaction with this logical shell. Surface the live
            // cwd and shell path so the AI can confirm both before any
            // destructive pipeline runs at the shell's natural home —
            // without the live cwd, the AI has no way to tell whether
            // re-sending the same pipeline is safe and ends up issuing a
            // wasted round-trip just to learn what `pwd` would have shown.
            //
            // sourceCwd is the live cwd of the *source* console (only
            // populated when we're switching from another active console).
            // The sub-agent boundary and any path that lands here without
            // a source console reach this branch with sourceCwd == null.
            // For those cases probe the *target* console's cwd directly so
            // the refuse message always carries it — the AI's confidence
            // on the retry shouldn't depend on whether ripple happened to
            // be switching from a known source.
            string? liveCwd = sourceCwd;
            if (string.IsNullOrEmpty(liveCwd))
            {
                try { liveCwd = await QueryConsoleCwdAsync(pipeName); }
                catch { /* probe is best-effort; fall through with no Live cwd line */ }
            }

            var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
            var contextLines = new List<string>();
            if (!string.IsNullOrEmpty(liveCwd))
                contextLines.Add($"Live cwd: '{liveCwd}'");
            if (!string.IsNullOrEmpty(targetShellPath))
                contextLines.Add($"Resolved shell: {targetShellPath}");
            var contextBlock = contextLines.Count > 0 ? "\n" + string.Join("\n", contextLines) : "";
            return new ExecutionPlan(consolePid, pipeName, cdCommand, expectedCwdAfterCd, routingNotice,
                EarlyResult: new ExecuteResult
                {
                    Pid = consolePid,
                    Switched = true,
                    DisplayName = displayName,
                    Output = $"Switched to console {displayName}. Pipeline NOT executed — first execute_command for this logical shell.{contextBlock}\nRe-send to run at this cwd, or call start_console(shell=..., cwd=...) to target a different directory.",
                });
        }
        else
        {
            // Same-console branch: staying on the active console. Detect
            // cwd drift via direct comparison of live cwd vs LastAiCwd —
            // if they differ, the user (or a standby-rotation) moved the
            // cwd since AI last observed it. We bail out without executing:
            // running the AI's command silently at the user's cwd risks
            // destructive ops at the wrong place (e.g. `rm *.tmp` at a
            // workspace the user opened to inspect). Update LastAiCwd to
            // the live cwd so a re-issue of the same command runs at the
            // user's location with no second bail. The AI keeps full
            // agency: re-send to accept the new cwd, or prepend a `cd`
            // back to the previous location.
            string? activeLastAiCwd;
            lock (_lock) activeLastAiCwd = _consoles.GetValueOrDefault(consolePid)?.LastAiCwd;

            if (activeLastAiCwd != null && sourceCwd != null
                && !CwdEquals(sourceCwd, activeLastAiCwd) && !string.IsNullOrEmpty(targetShellFamily))
            {
                RecordShellCwd(consolePid, sourceCwd);
                var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
                var revertHint = BuildCdPreamble(targetShellFamily!, activeLastAiCwd)?.TrimEnd('&', ';', ' ');
                var revertSuffix = revertHint != null ? $", or prepend `{revertHint}` to revert." : ".";
                return new ExecutionPlan(consolePid, pipeName, CdCommand: null, PreambleCwd: null, RoutingNotice: null,
                    EarlyResult: new ExecuteResult
                    {
                        Pid = consolePid,
                        DisplayName = displayName,
                        Output = $"ℹ️ User changed cwd in console {displayName} from '{activeLastAiCwd}' to '{sourceCwd}'.\nPipeline NOT executed. Re-issue to run at '{sourceCwd}'{revertSuffix}",
                    });
            }
        }

        return new ExecutionPlan(consolePid, pipeName, cdCommand, expectedCwdAfterCd, routingNotice, EarlyResult: null);
    }

    /// <summary>
    /// Run the pipe-level execute call after routing is done. Writes the
    /// (possibly preamble-augmented) command to the worker, waits for the
    /// response, drains any trailing post-prompt output, updates
    /// LastAiCwd, and translates pipe-level exceptions (timeout, cancel,
    /// I/O error) into an appropriate ExecuteResult. The routing notice
    /// chosen by the planner is carried through every success / busy /
    /// timeout path so the AI sees source-drift context even when the
    /// command itself fails.
    /// </summary>
    private async Task<ExecuteResult> ExecutePlannedCommandAsync(
        int consolePid,
        string pipeName,
        string command,
        string? cdCommand,
        string? expectedCwdAfterCd,
        int timeoutSeconds,
        string agentId,
        string? shell,
        string? routingNotice)
    {
        try
        {
            // Record the AI-visible command for this console so background
            // busy reports can show what the AI asked for, not the proxy-
            // injected cd. The cd itself is handled as a separate execute
            // below and never shown in the AI-facing status line.
            UpdateConsoleInfo(consolePid, ci => ci.LastAiCommand = command);

            // Phase 1: optional cd. Run as its own execute so the AI's
            // command stays pure in the status line and the cache entry.
            // Splitting also makes error handling cleaner: if the cd
            // fails (target directory doesn't exist, etc.) we report the
            // failure explicitly instead of running the AI command in
            // the wrong place. We use a short fixed timeout here since
            // cd is always a near-instant shell builtin.
            if (!string.IsNullOrEmpty(cdCommand))
            {
                try
                {
                    var cdResp = await SendPipeRequestAsync(pipeName, w =>
                    {
                        w.WriteString("type", "execute");
                        w.WriteString("id", Guid.NewGuid().ToString());
                        w.WriteString("command", cdCommand);
                        w.WriteNumber("timeout", 5000);
                    }, TimeSpan.FromSeconds(8));

                    var cdActualCwd = cdResp.TryGetProperty("cwd", out var ccProp) ? ccProp.GetString() : null;
                    var cdOutput = cdResp.TryGetProperty("output", out var coProp) ? coProp.GetString() : null;
                    // Verify the cd actually reached the expected
                    // directory. Exit-code checking is unreliable here
                    // because pwsh's `Set-Location` surfaces path-not-
                    // found failures as a cmdlet error record, not a
                    // non-zero $LASTEXITCODE — and cmd's `cd /d`
                    // doesn't expose %ERRORLEVEL% through the PROMPT
                    // shim at all. The worker's post-command cwd (sent
                    // on every execute response via OSC P) is the
                    // shell-agnostic source of truth: if the cwd
                    // returned from cdResp doesn't match what we
                    // expected, the cd didn't land where we wanted,
                    // even if the shell reported exit code 0.
                    if (!string.IsNullOrEmpty(expectedCwdAfterCd)
                        && !string.IsNullOrEmpty(cdActualCwd)
                        && !CwdEquals(cdActualCwd!, expectedCwdAfterCd!))
                    {
                        var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
                        var shellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily;
                        // Roll LastAiCwd forward to whatever cwd the
                        // shell actually ended up at so subsequent
                        // execute_commands from the AI don't keep
                        // trying to return to the unreachable path.
                        RecordShellCwd(consolePid, cdActualCwd);
                        return new ExecuteResult
                        {
                            Pid = consolePid,
                            DisplayName = displayName,
                            ShellFamily = shellFamily,
                            Command = command,
                            Output = string.IsNullOrEmpty(cdOutput)
                                ? $"Failed to change directory to '{expectedCwdAfterCd}' before running command. Shell stayed at '{cdActualCwd}'."
                                : cdOutput + $"\n(Shell stayed at '{cdActualCwd}'.)",
                            ExitCode = 1,
                            Cwd = cdActualCwd,
                            Notice = routingNotice,
                        };
                    }
                }
                catch
                {
                    // Pipe error on the cd phase — fall through to the
                    // main execute. The status line will still report
                    // whatever cwd the main command ends up at, so the
                    // AI has visible signal if the cd didn't happen.
                }
            }

            var response = await SendPipeRequestAsync(pipeName, w =>
            {
                w.WriteString("type", "execute");
                w.WriteString("id", Guid.NewGuid().ToString());
                w.WriteString("command", command);
                w.WriteNumber("timeout", timeoutSeconds * 1000);
            }, TimeSpan.FromSeconds(timeoutSeconds + 5));

            // Check if the worker reported a command timeout or busy
            var timedOut = response.TryGetProperty("timedOut", out var toProp) && toProp.GetBoolean();
            var busy = response.TryGetProperty("status", out var stProp) && stProp.GetString() == "busy";
            if (timedOut || busy)
            {
                var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
                var shellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily;
                MarkPipeBusy(agentId, consolePid);
                var partial = response.TryGetProperty("partialOutput", out var poProp) ? poProp.GetString() : null;
                return new ExecuteResult { Pid = consolePid, TimedOut = true, DisplayName = displayName, ShellFamily = shellFamily, Command = command, Notice = routingNotice, PartialOutput = partial };
            }

            // Worker reported that its shell process exited before the
            // command could complete (user typed `exit`, shell crashed,
            // etc). Clear our tracking of the dead console and return a
            // "died" notification — deliberately NOT auto-starting a
            // replacement. If the AI meant to close the console, auto-
            // spawning a new one would resurrect it against the AI's
            // intent. The next execute_command will go through the
            // normal no-active-console path in PlanExecutionAsync and
            // spin up a fresh same-family console lazily.
            var shellExited = response.TryGetProperty("shellExited", out var seProp) && seProp.GetBoolean();
            if (shellExited)
            {
                var deadName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
                RememberClosedActive(agentId, consolePid);
                ClearDeadConsole(agentId, consolePid);
                return new ExecuteResult
                {
                    Pid = consolePid,
                    Switched = true,
                    DisplayName = deadName,
                    Output = $"Console {deadName} exited (shell process gone). Pipeline NOT executed — re-execute and ripple will spin up a fresh console if needed.",
                };
            }

            var output = response.TryGetProperty("output", out var outputProp) ? outputProp.GetString() ?? "" : "";
            var exitCode = response.TryGetProperty("exitCode", out var exitProp) ? exitProp.GetInt32() : 0;
            var duration = response.TryGetProperty("duration", out var durProp) ? durProp.GetString() ?? "0" : "0";
            var cwdResult = response.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
            var spillPath = response.TryGetProperty("spillFilePath", out var spProp) ? spProp.GetString() : null;
            var displayName2 = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";

            // Post-prompt settle now happens inside the worker: its
            // finalize-once path (FinalizeSnapshotAsync) waits for the
            // capture to be stable for the adapter's
            // output.post_prompt_settle_ms before reading the final
            // slice. The worker returns a fully assembled `output`
            // string here, so the proxy no longer needs a second
            // round-trip over the pipe to drain trailing bytes. This
            // guarantees inline `execute_command` and deferred
            // `wait_for_completion` see identical output — both paths
            // read the same `CommandResult` that the finalize-once
            // pipeline built.

            // Update LastAiCwd with the result cwd (the cwd the command ended at)
            if (cwdResult != null)
                RecordShellCwd(consolePid, cwdResult);

            var execResult = new ExecuteResult
            {
                Pid = consolePid,
                Output = output,
                ExitCode = exitCode,
                Duration = duration,
                Command = command,
                DisplayName = displayName2,
                ShellFamily = _consoles.GetValueOrDefault(consolePid)?.ShellFamily,
                Cwd = cwdResult,
                Notice = routingNotice,
                SpillFilePath = spillPath,
            };
            ReadOscExtensionFields(response, execResult);
            return execResult;
        }
        catch (TimeoutException)
        {
            // Pipe communication timeout (worker didn't respond in time)
            var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
            MarkPipeBusy(agentId, consolePid);
            return new ExecuteResult { Pid = consolePid, TimedOut = true, DisplayName = displayName, Command = command, Notice = routingNotice };
        }
        catch (OperationCanceledException)
        {
            // Pipe CancellationToken fired
            var displayName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
            MarkPipeBusy(agentId, consolePid);
            return new ExecuteResult { Pid = consolePid, TimedOut = true, DisplayName = displayName, Command = command, Notice = routingNotice };
        }
        catch (IOException)
        {
            var deadName = _consoles.GetValueOrDefault(consolePid)?.DisplayName ?? $"#{consolePid}";
            RememberClosedActive(agentId, consolePid);
            ClearDeadConsole(agentId, consolePid);
            return new ExecuteResult
            {
                Pid = consolePid,
                Switched = true,
                DisplayName = deadName,
                Output = $"Console {deadName} died (pipe broken). Pipeline NOT executed — re-execute and ripple will spin up a fresh console.",
            };
        }
    }

    /// <summary>
    /// Remove a dead console's tracking info (process gone or pipe broken).
    /// </summary>
    private void ClearDeadConsole(string agentId, int consolePid)
    {
        lock (_lock)
        {
            _consoles.Remove(consolePid);
            _pidToTitle.Remove(consolePid);
            var state = GetOrCreateAgentState(agentId);
            state.KnownBusyPids.Remove(consolePid);
            RemoveActivePid(state, consolePid);
        }
    }

    // --- Pipe communication ---

    public static string GetPipeName(string agentId, int consolePid)
        => $"{PipePrefix}.{Environment.ProcessId}.{agentId}.{consolePid}";

    private async Task<JsonElement> SendPipeRequestAsync(string pipeName, Action<Utf8JsonWriter> writeBody, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await client.ConnectAsync(cts.Token);

        var msgBytes = PipeJson.BuildObjectBytes(writeBody);
        var lenBytes = BitConverter.GetBytes(msgBytes.Length);

        await client.WriteAsync(lenBytes, cts.Token);
        await client.WriteAsync(msgBytes, cts.Token);
        await client.FlushAsync(cts.Token);

        // Read response
        var recvLenBytes = new byte[4];
        await ReadExactAsync(client, recvLenBytes, cts.Token);
        var recvLen = BitConverter.ToInt32(recvLenBytes);

        var recvBytes = new byte[recvLen];
        await ReadExactAsync(client, recvBytes, cts.Token);

        return PipeJson.ParseElement(recvBytes);
    }

    // Deserialize the optional "errorMessages" string array the worker
    // writes for pwsh commands with $Error records. Missing / wrong-type
    // entries are silently skipped — a malformed element should not
    // poison the whole list. Returns a fresh list (callers don't share).
    private static IReadOnlyList<string> ParseErrorMessages(JsonElement obj)
    {
        if (!obj.TryGetProperty("errorMessages", out var arr)
            || arr.ValueKind != JsonValueKind.Array
            || arr.GetArrayLength() == 0)
            return Array.Empty<string>();

        var list = new List<string>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String && el.GetString() is string s)
                list.Add(s);
        }
        return list;
    }

    /// <summary>
    /// Populate the four OSC 633 extension fields (ErrorCount,
    /// LastExitCode, ErrorMessages, TruncatedErrorCount) on an
    /// <see cref="ExecuteResult"/> from a worker's JSON response. Both
    /// the inline path (live worker response) and the cached-drain
    /// path (<c>get_cached_output</c> result entries) read the same
    /// wire fields, so keeping the extraction in one place prevents
    /// the two sites from drifting when a new extension is added.
    ///
    /// Tolerates missing / wrong-type fields: each extension defaults
    /// to zero / empty so old binaries that never wrote the field
    /// produce the same result shape as a pwsh command that simply
    /// did not populate it.
    /// </summary>
    private static void ReadOscExtensionFields(JsonElement obj, ExecuteResult r)
    {
        r.ErrorCount = obj.TryGetProperty("errorCount", out var ec)
            && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0;
        r.LastExitCode = obj.TryGetProperty("lastExitCode", out var lec)
            && lec.ValueKind == JsonValueKind.Number ? lec.GetInt32() : 0;
        r.ErrorMessages = ParseErrorMessages(obj);
        r.TruncatedErrorCount = obj.TryGetProperty("truncatedErrorCount", out var tec)
            && tec.ValueKind == JsonValueKind.Number ? tec.GetInt32() : 0;
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

    // --- Pipe readiness ---

    private static async Task WaitForPipeReadyAsync(string pipeName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(cts.Token);

                // Send a ping to verify the worker is fully ready
                var msgBytes = PipeJson.BuildObjectBytes(w => w.WriteString("type", "ping"));
                var lenBytes = BitConverter.GetBytes(msgBytes.Length);
                await client.WriteAsync(lenBytes);
                await client.WriteAsync(msgBytes);
                await client.FlushAsync();

                // Read response
                var recvLenBytes = new byte[4];
                await ReadExactAsync(client, recvLenBytes, CancellationToken.None);
                // If we got here, pipe is ready
                return;
            }
            catch
            {
                await Task.Delay(300);
            }
        }
        throw new TimeoutException($"Console worker pipe '{pipeName}' did not become ready within {timeout.TotalSeconds}s");
    }

    // --- Discovery ---

    /// <summary>
    /// Find a standby console by enumerating pipes and querying get_status.
    /// Checks owned pipes for this agent first, then unowned pipes (orphaned by previous proxies).
    /// </summary>
    private async Task<(int Pid, string DisplayName)?> FindStandbyConsoleAsync(string agentId, string? shellPath = null)
    {
        // 1. Try owned pipes for this proxy + agent
        var found = await TryFindInPipesAsync(EnumeratePipes(ProxyPid, agentId), agentId, shellPath);
        if (found.HasValue) return found;

        // 2. Try unowned pipes (workers whose original proxy died). The
        // claiming agent assigns its own id to the adopted console — the
        // new pipe / claim payload carry `agentId`, so the orphan ends up
        // routed under the discovering agent's MRU stack rather than a
        // shared bucket. This keeps cross-agent isolation intact: agent
        // A discovering an orphan does not give agent B implicit access
        // to that console on the next tool call.
        found = await TryFindInPipesAsync(EnumerateUnownedPipes(), agentId, shellPath);
        return found;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Same policy as PathComparison but in IEqualityComparer form for
    // dictionary keys (ActivePidsByShell, LastActiveByShell, etc. all key
    // on full shell paths, which must match case-insensitively on Windows
    // and exactly on POSIX to stay consistent with the rest of this file).
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Case/separator-normalized cwd comparison used by the cd-failure
    /// detector in ExecutePlannedCommandAsync. Normalizes both paths
    /// via <see cref="Path.GetFullPath(string)"/> so trailing slashes
    /// and short-vs-long 8.3 forms collapse to the same canonical
    /// spelling, then compares under the platform path-comparison
    /// policy (OrdinalIgnoreCase on Windows, Ordinal on POSIX).
    /// Returns false on any normalization exception — a path that
    /// can't even be canonicalized is definitely not equivalent to a
    /// real cwd.
    /// </summary>
    private static bool CwdEquals(string a, string b)
    {
        try
        {
            return Path.GetFullPath(a).Equals(Path.GetFullPath(b), PathComparison);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Apply an update to a tracked console's info under _lock. Consolidates
    /// the "look up _consoles[pid], if non-null mutate" pattern that used to
    /// live inline at every LastAiCwd / LastAiCommand assignment site, so
    /// all writes now go through one well-defined critical section. No-op
    /// if the console has been removed in the meantime.
    /// </summary>
    private void UpdateConsoleInfo(int pid, Action<ConsoleInfo> update)
    {
        lock (_lock)
        {
            var info = _consoles.GetValueOrDefault(pid);
            if (info != null) update(info);
        }
    }

    /// <summary>
    /// Central point for recording the AI's observed cwd after a command
    /// completes on a specific console. Atomically updates both the
    /// per-console <see cref="ConsoleInfo.LastAiCwd"/> (used for drift
    /// detection on the next call) and the owning agent's
    /// <see cref="AgentSessionState.ShellCwd"/> entry for this logical
    /// shell (used when a later call needs a cd preamble for a *different*
    /// physical console of the same shell — auto-route, auto-spawn,
    /// reuse-on-start_console). Pass an empty or whitespace cwd and the
    /// call is a no-op so callers don't have to guard.
    /// </summary>
    private void RecordShellCwd(int pid, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return;
        lock (_lock)
        {
            var info = _consoles.GetValueOrDefault(pid);
            if (info == null) return;
            info.LastAiCwd = cwd;
            var agentId = GetAgentIdFromPipeName(info.PipePath);
            if (string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(info.ShellPath)) return;
            GetOrCreateAgentState(agentId).ShellCwd[info.ShellPath] = cwd;
        }
    }

    /// <summary>
    /// First-natural-cwd capture for a logical shell (design.md §3-A).
    /// Called once, the first time we observe a fresh-launched console's
    /// own OSC A cwd without the AI having supplied an explicit cwd —
    /// whatever the shell naturally picked becomes the "home" we return
    /// to on later <c>start_console</c> calls that don't name a cwd.
    /// No-op if <see cref="AgentSessionState.ShellHome"/> already has an
    /// entry for this shell_path (the capture is lazy and one-shot).
    /// </summary>
    private void RecordShellHomeIfUnset(int pid, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return;
        lock (_lock)
        {
            var info = _consoles.GetValueOrDefault(pid);
            if (info == null) return;
            var agentId = GetAgentIdFromPipeName(info.PipePath);
            if (string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(info.ShellPath)) return;
            var state = GetOrCreateAgentState(agentId);
            state.ShellHome.TryAdd(info.ShellPath, cwd);
        }
    }

    private async Task<(int Pid, string DisplayName)?> TryFindInPipesAsync(IEnumerable<string> pipes, string agentId, string? shellPath = null)
    {
        foreach (var pipe in pipes)
        {
            var pid = GetPidFromPipeName(pipe);
            if (!pid.HasValue || !IsProcessAlive(pid.Value)) continue;

            try
            {
                var response = await SendPipeRequestAsync(pipe,
                    w => w.WriteString("type", "get_status"),
                    TimeSpan.FromSeconds(3));

                var status = response.TryGetProperty("status", out var sp) ? sp.GetString() : null;
                // Reusable states: "standby" (nothing pending) and
                // "completed" (shell is idle but holds a cached result
                // from a previous AI command). Routing onto a
                // "completed" console is safe since RegisterCommand no
                // longer clears _cachedResults — the old entries ride
                // along until the universal drain wrapper picks them
                // up on the way out of the next tool call, which is
                // typically the same tool call that spawned the new
                // command. Excluding "completed" caused auto-routing
                // to spawn a fresh console whenever an earlier
                // timeout-drained console was still holding a cached
                // result, which felt unnatural and wasted standbys.
                if (status != "standby" && status != "completed") continue;

                // Shell path filter: match by full path (tracked consoles) or family (unowned)
                if (shellPath != null)
                {
                    bool alreadyTrackedForFilter;
                    ConsoleInfo? infoForFilter;
                    lock (_lock)
                    {
                        alreadyTrackedForFilter = _consoles.TryGetValue(pid.Value, out infoForFilter);
                    }

                    if (alreadyTrackedForFilter && infoForFilter != null)
                    {
                        // Tracked: match by full path
                        if (!infoForFilter.ShellPath.Equals(shellPath, PathComparison))
                            continue;
                    }
                    else
                    {
                        // Unowned: try full path first, fall back to family
                        var workerPath = response.TryGetProperty("shellPath", out var spProp) ? spProp.GetString() : null;
                        if (workerPath != null && workerPath.Length > 0)
                        {
                            if (!workerPath.Equals(shellPath, PathComparison))
                                continue;
                        }
                        else
                        {
                            var workerShell = response.TryGetProperty("shellFamily", out var sf) ? sf.GetString() : null;
                            var requestedFamily = NormalizeShellFamily(shellPath);
                            if (workerShell != null && !workerShell.Equals(requestedFamily, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }
                    }
                }

                // Already tracked by this proxy — just return it (no re-claim needed)
                bool alreadyTracked;
                lock (_lock) { alreadyTracked = _consoles.ContainsKey(pid.Value); }

                if (alreadyTracked)
                {
                    var displayName = _consoles[pid.Value].DisplayName;
                    return (pid.Value, displayName);
                }

                // Unowned console — claim it under the discovering
                // agent's id so the resulting RP.{proxy}.{agent}.{pid}
                // pipe lands in this agent's routing partition. Worker
                // and proxy must agree on the agent_id baked into the
                // pipe name; passing the same value in both the claim
                // payload (worker uses it to rebuild the pipe) and the
                // pipe name we wait on keeps them aligned.
                var displayNameNew = AssignConsoleName(pid.Value);
                var newPipeName = GetPipeName(agentId, pid.Value);
                try
                {
                    var claimResponse = await SendPipeRequestAsync(pipe, w =>
                    {
                        w.WriteString("type", "claim");
                        w.WriteNumber("proxy_pid", ProxyPid);
                        w.WriteString("proxy_version", ProxyVersion);
                        w.WriteString("agent_id", agentId);
                        w.WriteString("title", displayNameNew);
                    }, TimeSpan.FromSeconds(3));

                    // Worker refused claim because our proxy is strictly newer than it
                    // (pipe protocol may be incompatible). Skip this orphan — the worker
                    // has marked itself obsolete and stopped serving pipes, but its shell
                    // is still alive for the human user.
                    if (claimResponse.TryGetProperty("status", out var claimStatus)
                        && claimStatus.GetString() == "obsolete")
                    {
                        continue;
                    }

                    await WaitForPipeReadyAsync(newPipeName, TimeSpan.FromSeconds(5));
                }
                catch { newPipeName = pipe; }

                lock (_lock)
                {
                    var claimShellFamily = response.TryGetProperty("shellFamily", out var sfClaim) ? sfClaim.GetString() ?? "unknown" : "unknown";
                    var claimShellPath = response.TryGetProperty("shellPath", out var spClaim) ? spClaim.GetString() ?? "" : "";
                    _consoles[pid.Value] = new ConsoleInfo(newPipeName, displayNameNew, claimShellFamily, claimShellPath);
                }

                return (pid.Value, displayNameNew);
            }
            catch
            {
                // Pipe dead or unresponsive — skip
            }
        }
        return null;
    }

    // --- Cached output collection ---

    /// <summary>
    /// Collect cached outputs from all owned consoles (single scan, no polling).
    /// Called from every MCP tool to drain completed background commands.
    /// </summary>
    /// <summary>
    /// Build a shell-specific "cd 'path' && " preamble that can be prepended to a command.
    /// Returns null if the shell family is not supported.
    /// </summary>
    private static string? BuildCdPreamble(string shellFamily, string cwd)
    {
        return shellFamily.ToLowerInvariant() switch
        {
            "bash" or "sh" or "zsh" => $"cd '{cwd.Replace("'", "'\\''")}' && ",
            "pwsh" or "powershell" => $"Set-Location '{cwd.Replace("'", "''")}'; ",
            "cmd" => $"cd /d \"{cwd.Replace("\"", "\"\"")}\" && ",
            _ => null,
        };
    }

    /// <summary>
    /// Query a console's current cwd via get_status pipe command.
    /// Returns null if the query fails or the worker doesn't have a tracked cwd yet.
    /// </summary>
    private async Task<string?> QueryConsoleCwdAsync(string pipeName)
    {
        try
        {
            var resp = await SendPipeRequestAsync(pipeName,
                w => w.WriteString("type", "get_status"),
                TimeSpan.FromSeconds(3));
            return resp.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolve a user-supplied console selector (from peek_console /
    /// diagnostic tools) to a console PID. The selector matches in
    /// this order:
    ///   1. An exact PID number.
    ///   2. An exact display name ("#43060 Reggae").
    ///   3. A case-insensitive substring of the display name
    ///      ("Reggae", "reggae", "43060").
    /// Returns null if nothing matches, or if the match is ambiguous
    /// across multiple consoles. Caller must hold _lock.
    /// </summary>
    private int? ResolveConsoleSelector(string selector)
    {
        // Numeric → PID
        if (int.TryParse(selector, out var pid) && _consoles.ContainsKey(pid))
            return pid;

        // Exact display-name match
        foreach (var kv in _consoles)
        {
            if (string.Equals(kv.Value.DisplayName, selector, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }

        // Substring match
        var matches = _consoles
            .Where(kv => kv.Value.DisplayName.Contains(selector, StringComparison.OrdinalIgnoreCase))
            .Select(kv => (int?)kv.Key)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    public record PeekResult(
        int Pid,
        string DisplayName,
        string? ShellFamily,
        string Status,
        bool Busy,
        string? RunningCommand,
        double? RunningElapsedSeconds,
        string RecentOutput,
        string? RawBase64 = null);

    /// <summary>
    /// Snapshot what a console has been emitting recently via the peek
    /// pipe command. Lets the AI inspect a busy console (stuck command,
    /// interactive prompt, user-typed command in progress) without
    /// interrupting it or waiting for completion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="console"/> selects which console to peek at.
    /// If it's a number, it's matched against console PIDs. Otherwise
    /// it's matched (case-insensitive, contains-style) against display
    /// names like "#43060 Reggae". When omitted, the agent's current
    /// active console is used.
    /// </para>
    /// <para>
    /// Works regardless of whether the target is idle or busy — the
    /// worker's peek pipe handler is never blocked by the tracker's
    /// busy state, so this is specifically the right tool for
    /// observing a long-running command's progress.
    /// </para>
    /// <para>
    /// Returns null if no console matches.
    /// </para>
    /// </remarks>
    public async Task<PeekResult?> PeekConsoleAsync(string agentId, string? console = null, bool raw = false)
    {
        int? pid;
        string? pipeName;
        string? displayName;
        string? shellFamily;

        lock (_lock)
        {
            var state = GetOrCreateAgentState(agentId);
            if (string.IsNullOrWhiteSpace(console))
            {
                pid = state.ActivePid != 0 ? state.ActivePid : (int?)null;
            }
            else
            {
                pid = ResolveConsoleSelector(console);
            }

            if (pid == null || !_consoles.TryGetValue(pid.Value, out var info))
                return null;

            pipeName = info.PipePath;
            displayName = info.DisplayName;
            shellFamily = info.ShellFamily;
        }

        try
        {
            var resp = await SendPipeRequestAsync(pipeName,
                w => { w.WriteString("type", "peek"); if (raw) w.WriteBoolean("raw", true); },
                TimeSpan.FromSeconds(3));

            var status = resp.TryGetProperty("status", out var stProp) ? stProp.GetString() ?? "" : "";
            var busy = resp.TryGetProperty("busy", out var bProp) && bProp.GetBoolean();
            var runningCmd = resp.TryGetProperty("runningCommand", out var rcProp) && rcProp.ValueKind == JsonValueKind.String ? rcProp.GetString() : null;
            double? elapsed = null;
            if (resp.TryGetProperty("runningElapsedSeconds", out var esProp) && esProp.ValueKind == JsonValueKind.Number)
                elapsed = esProp.GetDouble();
            var recent = resp.TryGetProperty("recentOutput", out var roProp) ? roProp.GetString() ?? "" : "";
            var rawB64 = resp.TryGetProperty("rawBase64", out var rbProp) ? rbProp.GetString() : null;

            return new PeekResult(pid.Value, displayName!, shellFamily, status, busy, runningCmd, elapsed, recent, rawB64);
        }
        catch
        {
            return null;
        }
    }

    public record SendInputResult(int Pid, string DisplayName, string Status, string? Error = null);

    /// <summary>
    /// Send raw input to a busy console's PTY. The console is resolved
    /// via the same selector as PeekConsoleAsync (PID or display-name
    /// substring). Returns a result with status "ok" or an error.
    /// </summary>
    public async Task<SendInputResult?> SendInputAsync(string agentId, string console, string input)
    {
        int? pid;
        string? pipeName;
        string? displayName;

        lock (_lock)
        {
            pid = ResolveConsoleSelector(console);
            if (pid == null || !_consoles.TryGetValue(pid.Value, out var info))
                return null;
            pipeName = info.PipePath;
            displayName = info.DisplayName;
        }

        try
        {
            var resp = await SendPipeRequestAsync(pipeName, w =>
            {
                w.WriteString("type", "send_input");
                w.WriteString("input", input);
            }, TimeSpan.FromSeconds(5));

            var status = resp.TryGetProperty("status", out var stProp) ? stProp.GetString() ?? "" : "";
            var error = resp.TryGetProperty("error", out var errProp) ? errProp.GetString() : null;
            return new SendInputResult(pid.Value, displayName!, status, error);
        }
        catch (Exception ex)
        {
            return new SendInputResult(pid.Value, displayName!, "error", ex.Message);
        }
    }

    /// <summary>
    /// Detect consoles that have been closed since the last check.
    /// Removes them from _consoles and returns their display names + shell families.
    /// </summary>
    public List<(string DisplayName, string ShellFamily)> DetectClosedConsoles(string agentId)
    {
        var closed = new List<(string, string)>();
        lock (_lock)
        {
            var deadPids = _consoles
                .Where(kv => !IsProcessAlive(kv.Key))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var pid in deadPids)
            {
                var info = _consoles[pid];
                closed.Add((info.DisplayName, info.ShellFamily));
                var state = GetOrCreateAgentState(agentId);
                // If this console was tracked as any shell's active (top
                // of stack or fallback in the MRU list), snapshot its cwd
                // into LastActiveByShell[info.ShellPath] so the next
                // execute_command for that shell can auto-start a
                // replacement at the same cwd. Inlined rather than calling
                // RememberClosedActive because we're already under _lock.
                bool wasTracked = state.ActivePidsByShell.Values.Any(s => s.Contains(pid));
                if (wasTracked && info.LastAiCwd != null)
                    state.LastActiveByShell[info.ShellPath] = (info.LastAiCwd, info.ShellPath);
                RemoveActivePid(state, pid);
                _consoles.Remove(pid);
                _pidToTitle.Remove(pid);
                state.KnownBusyPids.Remove(pid);
            }
        }
        return closed;
    }

    public async Task<List<ExecuteResult>> CollectCachedOutputsAsync(string agentId)
    {
        var results = new List<ExecuteResult>();

        foreach (var pipe in EnumeratePipes(ProxyPid, agentId))
        {
            var pid = GetPidFromPipeName(pipe);
            if (!pid.HasValue) continue;

            try
            {
                var statusResp = await SendPipeRequestAsync(pipe,
                    w => w.WriteString("type", "get_status"),
                    TimeSpan.FromSeconds(3));

                var hasCached = statusResp.TryGetProperty("hasCachedOutput", out var hc) && hc.GetBoolean();

                // If there's no cached output, leave KnownBusyPids alone —
                // CollectBusyStatusesAsync (called right after this in
                // AppendCachedOutputs) will detect the busy→idle transition
                // and emit a finished notification. Stale entries with a
                // lost cache end up going through the same path and get
                // surfaced as "finished" too, which is a little misleading
                // but beats the old silent cleanup that swallowed real
                // user-command finish events.
                if (!hasCached) continue;

                var cachedResp = await SendPipeRequestAsync(pipe,
                    w => w.WriteString("type", "get_cached_output"),
                    TimeSpan.FromSeconds(5));

                var cacheStatus = cachedResp.TryGetProperty("status", out var cs) ? cs.GetString() : null;
                if (cacheStatus != "ok") continue;
                if (!cachedResp.TryGetProperty("results", out var resultsProp)
                    || resultsProp.ValueKind != JsonValueKind.Array)
                    continue;

                var consoleInfo = _consoles.GetValueOrDefault(pid.Value);
                var displayName = consoleInfo?.DisplayName ?? $"#{pid.Value}";

                // A single console's cache may hold multiple entries if
                // sequential commands each flipped to cache mode without
                // an intervening drain — preserve them all, in order, so
                // the AI sees the full history on the next tool call.
                foreach (var entry in resultsProp.EnumerateArray())
                {
                    var drained = new ExecuteResult
                    {
                        Pid = pid.Value,
                        Output = entry.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "",
                        ExitCode = entry.TryGetProperty("exitCode", out var e) ? e.GetInt32() : 0,
                        Duration = entry.TryGetProperty("duration", out var d) ? d.GetString() ?? "0" : "0",
                        Command = entry.TryGetProperty("command", out var c) ? c.GetString() : null,
                        DisplayName = displayName,
                        ShellFamily = consoleInfo?.ShellFamily,
                        Cwd = entry.TryGetProperty("cwd", out var w) ? w.GetString() : null,
                        StatusLine = entry.TryGetProperty("statusLine", out var sl) ? sl.GetString() : null,
                        SpillFilePath = entry.TryGetProperty("spillFilePath", out var sp) ? sp.GetString() : null,
                    };
                    ReadOscExtensionFields(entry, drained);
                    results.Add(drained);
                }
                UnmarkPipeBusy(agentId, pid.Value);
            }
            catch { }
        }

        return results;
    }

    /// <summary>
    /// Snapshot of a busy console's running command for reporting to the AI
    /// at the end of unrelated tool calls. Used to keep the AI aware of
    /// long-running work on other consoles instead of silently forgetting them.
    /// </summary>
    public record BusyStatus(
        int Pid,
        string DisplayName,
        string? ShellFamily,
        string? RunningCommand,
        double? ElapsedSeconds,
        string? Cwd);

    public record FinishedStatus(
        int Pid,
        string DisplayName,
        string? ShellFamily,
        string? Cwd);

    public record BusyReport(
        List<BusyStatus> Busy,
        List<FinishedStatus> Finished);

    /// <summary>
    /// Report currently-busy consoles (other than any caller-excluded one)
    /// so the caller can prepend their status to the next response. Walks
    /// every known console — not just KnownBusyPids — so that user-typed
    /// commands which the proxy never explicitly tracked still surface in
    /// the busy report. Newly-discovered busy consoles are added to
    /// KnownBusyPids so the eventual idle transition lands in the Finished
    /// list on a later call. Consoles that were previously reported as
    /// busy but are now idle produce one "finished" notification and are
    /// then unmarked.
    /// </summary>
    public async Task<BusyReport> CollectBusyStatusesAsync(string agentId, int excludePid = 0)
    {
        var busyReport = new List<BusyStatus>();
        var finishedReport = new List<FinishedStatus>();

        var knownBusy = SnapshotBusyPids(agentId).ToHashSet();
        List<int> allConsoles;
        lock (_lock) allConsoles = _consoles.Keys.ToList();

        var toCheck = new HashSet<int>(knownBusy);
        foreach (var pid in allConsoles) toCheck.Add(pid);

        foreach (var pid in toCheck)
        {
            if (pid == excludePid) continue;

            if (!IsProcessAlive(pid))
            {
                UnmarkPipeBusy(agentId, pid);
                continue;
            }

            string? pipeName;
            ConsoleInfo? info;
            lock (_lock)
            {
                info = _consoles.GetValueOrDefault(pid);
                pipeName = info?.PipePath;
            }
            if (pipeName == null)
            {
                UnmarkPipeBusy(agentId, pid);
                continue;
            }

            try
            {
                var statusResp = await SendPipeRequestAsync(pipeName,
                    w => w.WriteString("type", "get_status"),
                    TimeSpan.FromSeconds(3));

                var statusStr = statusResp.TryGetProperty("status", out var st) ? st.GetString() : null;
                var wasKnownBusy = knownBusy.Contains(pid);

                // Both busy and finished lines carry the worker's live
                // cwd so the AI can see where each background console
                // is without a separate peek_console round-trip. The
                // worker reports _tracker.LastKnownCwd which is updated
                // on every OSC P (Cwd) event, including from user-typed
                // cd commands, so this stays in sync with whatever the
                // human is doing on the shared terminal.
                var cwdForLine = statusResp.TryGetProperty("cwd", out var cwdProp)
                    ? cwdProp.GetString()
                    : null;

                if (statusStr != "busy")
                {
                    // Idle now. Only emit a finished line if we'd previously
                    // reported the console as busy — skip consoles that were
                    // idle all along, otherwise every tool call for a user
                    // with multiple standby consoles would spam finished
                    // entries. AI commands that timed out and then completed
                    // are drained by CollectCachedOutputs before this runs,
                    // so they're gone from KnownBusyPids by now.
                    if (wasKnownBusy)
                    {
                        UnmarkPipeBusy(agentId, pid);
                        finishedReport.Add(new FinishedStatus(pid, info?.DisplayName ?? $"#{pid}", info?.ShellFamily, cwdForLine));
                    }
                    continue;
                }

                // Busy now. Mark it so the later transition to idle produces
                // a finished notification even if nothing else tagged it.
                if (!wasKnownBusy) MarkPipeBusy(agentId, pid);

                // Decide what command text to show on the busy line. The
                // worker's runningCommand returns null when its busy state
                // is from a user-typed command (CommandTracker only fills it
                // for AI commands), so a null here means "the human typed
                // this, not the AI" — surface that as "(user command)" by
                // returning null. The proxy's LastAiCommand is the previous
                // AI command's text and would otherwise leak across into
                // a user busy line as a stale label. When the worker IS
                // running an AI command, prefer LastAiCommand (it's the
                // clean original without any cd preamble ripple injected),
                // falling back to the worker's text if the proxy never
                // recorded one.
                var workerRunning = statusResp.TryGetProperty("runningCommand", out var rc)
                    ? rc.GetString()
                    : null;
                var cmd = workerRunning != null
                    ? (info?.LastAiCommand ?? workerRunning)
                    : null;
                double? elapsed = null;
                if (statusResp.TryGetProperty("runningElapsedSeconds", out var esProp)
                    && esProp.ValueKind == JsonValueKind.Number)
                    elapsed = esProp.GetDouble();

                busyReport.Add(new BusyStatus(pid, info?.DisplayName ?? $"#{pid}", info?.ShellFamily, cmd, elapsed, cwdForLine));
            }
            catch
            {
                // Transient pipe error — leave any existing KnownBusy state
                // alone so we retry next tick. Don't emit a partial entry.
            }
        }

        return new BusyReport(busyReport, finishedReport);
    }

    // --- Wait for completion ---

    /// <summary>
    /// Result of a wait_for_completion call. Distinguishes three states so the
    /// tool can give the AI an actionable response:
    ///   - HadNoBusyPids=true: nothing was running when the wait started → the
    ///     AI should not keep calling wait_for_completion; there is nothing to wait for.
    ///   - Completed has entries: one or more busy commands finished during the wait.
    ///   - StillBusy has entries: wait timed out before these consoles finished;
    ///     the AI can call wait_for_completion again to continue waiting.
    /// </summary>
    public record WaitForCompletionResult(
        List<ExecuteResult> Completed,
        List<BusyStatus> StillBusy,
        bool HadNoBusyPids);

    /// <summary>
    /// Wait for any commands this agent left running (execute_command returning
    /// TimedOut) to finish, and drain their cached output. The set of "still
    /// running" consoles is the KnownBusyPids tracked on the agent session.
    ///
    /// Contract:
    ///   - If KnownBusyPids is empty on entry → return HadNoBusyPids=true
    ///     immediately. The AI should report "nothing running" rather than loop.
    ///   - Otherwise poll only those pipes until each one either produces cached
    ///     output (completed/timed out from worker side) or its process dies
    ///     (closed externally). Unmark busy on drain.
    ///   - On overall timeout, return whatever we drained plus the set of pids
    ///     that are still busy so the tool can report them.
    /// </summary>
    public async Task<WaitForCompletionResult> WaitForCompletionAsync(int timeoutSeconds, string agentId)
    {
        var busyPids = SnapshotBusyPids(agentId);

        // Drop any pids whose process is already dead — a console closed while
        // still flagged busy counts as "not running anymore", and we don't want
        // to pretend there's something to wait for.
        for (int i = busyPids.Count - 1; i >= 0; i--)
        {
            if (!IsProcessAlive(busyPids[i]))
            {
                UnmarkPipeBusy(agentId, busyPids[i]);
                busyPids.RemoveAt(i);
            }
        }

        if (busyPids.Count == 0)
        {
            return new WaitForCompletionResult(
                new List<ExecuteResult>(),
                new List<BusyStatus>(),
                HadNoBusyPids: true);
        }

        var completed = new List<ExecuteResult>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);

        while (busyPids.Count > 0 && DateTime.UtcNow < deadline)
        {
            for (int i = busyPids.Count - 1; i >= 0; i--)
            {
                var pid = busyPids[i];

                // Console process died while busy — stop waiting for it, and
                // surface it in the result so the tool can show a notification.
                if (!IsProcessAlive(pid))
                {
                    var info = _consoles.GetValueOrDefault(pid);
                    completed.Add(new ExecuteResult
                    {
                        DisplayName = info?.DisplayName ?? $"#{pid}",
                        ShellFamily = info?.ShellFamily,
                        Output = "(console closed before command completed)",
                        ExitCode = -1,
                    });
                    UnmarkPipeBusy(agentId, pid);
                    busyPids.RemoveAt(i);
                    continue;
                }

                string? pipeName;
                lock (_lock) pipeName = _consoles.GetValueOrDefault(pid)?.PipePath;
                if (pipeName == null)
                {
                    UnmarkPipeBusy(agentId, pid);
                    busyPids.RemoveAt(i);
                    continue;
                }

                try
                {
                    var statusResp = await SendPipeRequestAsync(pipeName,
                        w => w.WriteString("type", "get_status"),
                        TimeSpan.FromSeconds(3));

                    var statusStr = statusResp.TryGetProperty("status", out var stProp) ? stProp.GetString() : null;
                    var hasCached = statusResp.TryGetProperty("hasCachedOutput", out var hc) && hc.GetBoolean();

                    // Worker is back at standby with nothing to deliver — the
                    // previous AI command's cache was lost/destroyed. Stop
                    // waiting; there will never be a result to drain.
                    if (!hasCached && statusStr == "standby")
                    {
                        UnmarkPipeBusy(agentId, pid);
                        busyPids.RemoveAt(i);
                        continue;
                    }

                    if (!hasCached) continue;

                    var cachedResp = await SendPipeRequestAsync(pipeName,
                        w => w.WriteString("type", "get_cached_output"),
                        TimeSpan.FromSeconds(5));

                    var cacheStatus = cachedResp.TryGetProperty("status", out var cs) ? cs.GetString() : null;
                    if (cacheStatus != "ok") continue;
                    if (!cachedResp.TryGetProperty("results", out var resultsProp)
                        || resultsProp.ValueKind != JsonValueKind.Array)
                        continue;

                    var info2 = _consoles.GetValueOrDefault(pid);
                    var displayName2 = info2?.DisplayName ?? $"#{pid}";
                    foreach (var entry in resultsProp.EnumerateArray())
                    {
                        completed.Add(new ExecuteResult
                        {
                            Pid = pid,
                            Output = entry.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "",
                            ExitCode = entry.TryGetProperty("exitCode", out var e) ? e.GetInt32() : 0,
                            Duration = entry.TryGetProperty("duration", out var d) ? d.GetString() ?? "0" : "0",
                            Command = entry.TryGetProperty("command", out var c) ? c.GetString() : null,
                            DisplayName = displayName2,
                            ShellFamily = info2?.ShellFamily,
                            Cwd = entry.TryGetProperty("cwd", out var w) ? w.GetString() : null,
                            StatusLine = entry.TryGetProperty("statusLine", out var sl) ? sl.GetString() : null,
                            SpillFilePath = entry.TryGetProperty("spillFilePath", out var sp) ? sp.GetString() : null,
                        });
                    }
                    UnmarkPipeBusy(agentId, pid);
                    busyPids.RemoveAt(i);
                }
                catch
                {
                    // Transient pipe error — keep the pid in the busy list and
                    // retry on the next poll tick.
                }
            }

            // Return as soon as ANY busy console produces a result — the
            // caller can call wait_for_completion again to pick up the
            // rest. Waiting for all of them at once made AI sessions
            // block on the slowest command even after a faster one had
            // completed, which defeats the whole point of the
            // cache-on-busy-receive salvage layer. First-drain-wins
            // gives the AI a tight feedback loop: react to whatever
            // finished, issue the next command, and loop.
            if (busyPids.Count == 0 || completed.Count > 0) break;
            await Task.Delay(300);
        }

        // Report the still-busy consoles via the same BusyStatus shape the
        // per-tool background busy report uses, so the AI sees a consistent
        // `⧗ #pid Name (shell) | Status: Busy (Ns) | Pipeline: cmd` format
        // everywhere.
        var stillBusy = (await CollectBusyStatusesAsync(agentId)).Busy;

        return new WaitForCompletionResult(completed, stillBusy, HadNoBusyPids: false);
    }

    // --- Pipe enumeration ---

    /// <summary>
    /// Enumerates ripple Named Pipes.
    /// Windows: \\.\pipe\SP.*
    /// Linux/macOS: /tmp/CoreFxPipe_RP.*
    /// </summary>
    public IEnumerable<string> EnumeratePipes(int? proxyPid = null, string? agentId = null)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        string filterPattern;
        if (proxyPid.HasValue && agentId != null)
            filterPattern = $"{PipePrefix}.{proxyPid.Value}.{agentId}.*";
        else if (proxyPid.HasValue)
            filterPattern = $"{PipePrefix}.{proxyPid.Value}.*";
        else
            filterPattern = $"{PipePrefix}*";

        IEnumerable<string> paths;
        try
        {
            if (isWindows)
            {
                paths = Directory.EnumerateFiles(@"\\.\pipe\", filterPattern);
            }
            else
            {
                var directories = new List<string> { "/tmp" };
                var tmpDir = Environment.GetEnvironmentVariable("TMPDIR");
                if (!string.IsNullOrEmpty(tmpDir) && tmpDir != "/tmp" && tmpDir != "/tmp/")
                    directories.Add(tmpDir.TrimEnd('/'));

                paths = directories
                    .Where(Directory.Exists)
                    .SelectMany(dir =>
                    {
                        try { return Directory.EnumerateFiles(dir, $"CoreFxPipe_{filterPattern}"); }
                        catch { return Enumerable.Empty<string>(); }
                    });
            }
        }
        catch
        {
            yield break;
        }

        foreach (var path in paths)
        {
            var fileName = Path.GetFileName(path);
            yield return isWindows ? fileName : fileName["CoreFxPipe_".Length..];
        }
    }

    /// <summary>
    /// Enumerate unowned pipes — those whose original proxy has exited.
    /// Format: RP.{consolePid} (2 segments) vs owned RP.{proxyPid}.{agentId}.{consolePid} (4 segments).
    /// </summary>
    public IEnumerable<string> EnumerateUnownedPipes()
    {
        foreach (var pipe in EnumeratePipes())
        {
            var segments = pipe.Split('.');
            if (segments.Length == 2 && int.TryParse(segments[1], out _))
                yield return pipe;
        }
    }

    /// <summary>
    /// Extracts console PID from pipe name (last segment).
    /// </summary>
    public static int? GetPidFromPipeName(string pipeName)
    {
        var parts = pipeName.Split('.');
        if (parts.Length > 0 && int.TryParse(parts[^1], out var pid))
            return pid;
        return null;
    }

    /// <summary>
    /// Extract the owning agent_id from an owned pipe name
    /// (<c>RP.{proxyPid}.{agentId}.{consolePid}</c>). Returns null for
    /// unowned pipes (<c>RP.{consolePid}</c>, 2 segments) or malformed
    /// names. Treat null as "no owner — don't update per-agent state"; a
    /// subsequent claim will attach the console to a specific agent and
    /// regenerate the pipe name in the owned form.
    /// </summary>
    public static string? GetAgentIdFromPipeName(string pipeName)
    {
        var parts = pipeName.Split('.');
        if (parts.Length != 4) return null;
        return parts[2];
    }

    // --- Helpers ---

    private static string GetDefaultShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Prefer pwsh (PowerShell 7); fall back to Windows PowerShell 5.1 if absent.
            var pwshResolved = ShellPathResolver.Resolve("pwsh.exe");
            if (File.Exists(pwshResolved)) return "pwsh.exe";
            return "powershell.exe";
        }
        return Environment.GetEnvironmentVariable("SHELL") ?? "bash";
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch { return false; }
    }

    // --- Category naming ---

    private void RefillNames()
    {
        if (_fixedNameOrder == null)
        {
            _fixedNameOrder = Categories[_categoryIndex].Words.ToArray();
            for (int i = _fixedNameOrder.Length - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (_fixedNameOrder[i], _fixedNameOrder[j]) = (_fixedNameOrder[j], _fixedNameOrder[i]);
            }
        }
        foreach (var n in _fixedNameOrder) _nameQueue.Enqueue(n);
    }

    private int InitializeCategory()
    {
        using var mutex = new Mutex(false, MutexName, out _);
        if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Could not acquire shared memory mutex for category initialization");

        try
        {
            using var mmf = MemoryMappedFile.CreateFromFile(SharedMemoryFile, FileMode.OpenOrCreate, null, SharedMemorySize);
            using var accessor = mmf.CreateViewAccessor();

            int magic = accessor.ReadInt32(0);
            int count = accessor.ReadInt32(4);

            if (magic != MagicNumber)
            {
                accessor.Write(0, MagicNumber);
                count = 0;
            }

            var validEntries = new List<(int pid, int category)>();
            var usedIndices = new HashSet<int>();

            for (int i = 0; i < count && i < MaxEntries; i++)
            {
                int offset = HeaderSize + (i * EntrySize);
                int pid = accessor.ReadInt32(offset);
                int category = accessor.ReadInt32(offset + 4);

                if (IsProcessAlive(pid))
                {
                    usedIndices.Add(category);
                    validEntries.Add((pid, category));
                }
            }

            var available = Enumerable.Range(0, Categories.Length).Where(i => !usedIndices.Contains(i)).ToList();
            int categoryIndex = available.Count > 0
                ? available[Random.Shared.Next(available.Count)]
                : Random.Shared.Next(Categories.Length);

            validEntries.Add((ProxyPid, categoryIndex));

            accessor.Write(4, validEntries.Count);
            for (int i = 0; i < validEntries.Count; i++)
            {
                int offset = HeaderSize + (i * EntrySize);
                accessor.Write(offset, validEntries[i].pid);
                accessor.Write(offset + 4, validEntries[i].category);
            }

            return categoryIndex;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private void CleanupCategory()
    {
        try
        {
            using var mutex = new Mutex(false, MutexName, out _);
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) return;

            try
            {
                using var mmf = MemoryMappedFile.CreateFromFile(SharedMemoryFile, FileMode.OpenOrCreate, null, SharedMemorySize);
                using var accessor = mmf.CreateViewAccessor();

                int magic = accessor.ReadInt32(0);
                int count = accessor.ReadInt32(4);
                if (magic != MagicNumber) return;

                var validEntries = new List<(int pid, int category)>();
                for (int i = 0; i < count && i < MaxEntries; i++)
                {
                    int offset = HeaderSize + (i * EntrySize);
                    int pid = accessor.ReadInt32(offset);
                    int category = accessor.ReadInt32(offset + 4);
                    if (pid != ProxyPid && IsProcessAlive(pid))
                        validEntries.Add((pid, category));
                }

                accessor.Write(4, validEntries.Count);
                for (int i = 0; i < validEntries.Count; i++)
                {
                    int offset = HeaderSize + (i * EntrySize);
                    accessor.Write(offset, validEntries[i].pid);
                    accessor.Write(offset + 4, validEntries[i].category);
                }
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
        catch { }
    }

    // --- Types ---

    private record ConsoleInfo(string PipePath, string DisplayName, string ShellFamily, string ShellPath)
    {
        // Cwd as of the most recent AI command. Used to detect manual user cd
        // and to skip the "NOT executed" warning when cwd is consistent.
        public string? LastAiCwd { get; set; }

        // Original AI-visible command text (without proxy-injected cd preamble).
        // Used by CollectBusyStatusesAsync so background busy lines show what
        // the AI asked for, not the preamble-augmented string.
        public string? LastAiCommand { get; set; }
    }

    /// <summary>
    /// Normalize a shell path/name to a canonical family name (for display only).
    /// "bash", "/usr/bin/bash", "C:\Windows\System32\bash.exe" → "bash"
    /// "pwsh", "pwsh.exe" → "pwsh"
    /// </summary>
    internal static string NormalizeShellFamily(string shell)
        => Path.GetFileNameWithoutExtension(shell).ToLowerInvariant();

    public record StartConsoleResult(string Status, int Pid, string DisplayName, string? ShellFamily = null, string? Cwd = null);

    public class ExecuteResult
    {
        public int Pid { get; set; }
        public string Output { get; set; } = "";
        public int ExitCode { get; set; }
        // Number of error records the pipeline added to $Error (PowerShell
        // only, via OSC 633;E;{N}). Surfaced as "Errors: N" in the proxy
        // status line when N > 0; ignored for non-pwsh adapters where it
        // stays at 0.
        public int ErrorCount { get; set; }
        // Raw $LASTEXITCODE at command end (PowerShell only, via
        // OSC 633;L;{N}). Only populated when a native exe returned
        // non-zero mid-pipeline AND the overall pipeline succeeded
        // (ExitCode == 0); in every other case it stays at 0 and
        // "LastExit: N" is omitted from the status line.
        public int LastExitCode { get; set; }
        // Structured error-message list (PowerShell only, via
        // OSC 633;R). Empty for non-pwsh adapters and for pwsh
        // pipelines that produced no errors. Rendered as a
        // `--- errors ---` section in the proxy's response after
        // the main output.
        public IReadOnlyList<string> ErrorMessages { get; set; } = Array.Empty<string>();
        // How many $Error records were dropped beyond the integration
        // script's per-command cap (PowerShell only, via OSC 633;T).
        // 0 when no truncation occurred. Surfaced by the proxy as
        // `(N of total)` in the section header plus a trailing line.
        public int TruncatedErrorCount { get; set; }
        public string Duration { get; set; } = "0";
        public string? Command { get; set; }
        public string? DisplayName { get; set; }
        public string? ShellFamily { get; set; }
        public string? Cwd { get; set; }
        public bool Switched { get; set; }
        public bool TimedOut { get; set; }
        // Pre-formatted status line baked in by the worker at Resolve
        // time (or reconstructed by the proxy for inline results). Cached
        // drains return this verbatim instead of reformatting with
        // possibly-stale proxy-side ConsoleInfo metadata.
        public string? StatusLine { get; set; }
        // Absolute path of the public spill file when the finalized
        // output exceeded the truncation threshold. Populated on
        // oversized results from both inline and cached delivery
        // paths so MCP clients can open / grep / archive the full
        // output without parsing the preview header.
        public string? SpillFilePath { get; set; }
        // Populated only on TimedOut — the recent-output ring snapshot the
        // worker captured at timeout. Lets the AI diagnose stuck commands
        // (watch mode, interactive prompts) without waiting for
        // wait_for_completion to drain the full cached result.
        public string? PartialOutput { get; set; }
        // Free-form notice prepended to the response. Used to surface
        // out-of-band events like "the source console you came from has
        // been moved by the user, your last known cwd has been preserved".
        public string? Notice { get; set; }
    }
}
