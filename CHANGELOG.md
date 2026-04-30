# Changelog

All notable changes to ripple are documented here. Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.13.0] - 2026-04-30

**Structured PowerShell error visibility.** AI can now read PowerShell errors as typed fields instead of parsing SGR-coloured text. Three new OSC 633 extensions surface error messages, truncation count, and silent native exit codes; an opt-in `strip_ansi` flag drops SGR bytes when callers don't need them.

**Refuse and rescue paths show what the AI needs.** First-execute refuse messages include Live cwd and Resolved shell so the AI can confirm the target before re-sending. Busy-timeout hint reordered to peek → send → wait. CLI gains `--version` / `--help`. PAGER / GIT_PAGER / MANPAGER pinned so external CLIs don't freeze the console waiting for `q`.

### Added

- **Structured error messages from PowerShell.** OSC 633;R surfaces each `$Error` entry as a separate field, base64-utf8 encoded. Proxy renders an `--- errors (N of total) ---` section after the main output, with cmdlet provenance prefixes (`Get-Item: ...`). Caps at 20 records, oldest first (root causes are usually the first errors, later cascades drop on overflow). Each message capped at 1000 chars.

- **Truncation count as its own field.** OSC 633;T reports how many error records were dropped, separate from the R event stream. Header reads `--- errors (20 of 25) ---` matching the status badge `Errors: 25`. Under-cap cases keep the bare `(N)` header.

- **Native non-zero exit code surfaced when pipeline succeeds.** OSC 633;L emits `LastExit: N` on the status line for `cmd /c exit 7; "after"`-style pipelines where `$?` is True but a native exe exited non-zero. The green ✓ badge no longer hides silent native failures.

- **`strip_ansi` flag on `execute_command` / `wait_for_completion`.** Default `false`. When `true`, response output runs through a narrow SGR-only regex (`\x1b\[[0-9;]*m`) — saves tokens for callers that don't reference colour cues. Visible console output is never touched.

- **First-execute refuse shows Live cwd and Resolved shell.** AI can confirm the target without an extra `pwd` round-trip. Sub-agent isolation boundaries probe the target console's cwd directly so the line is never blank.

- **CLI: `--version`, `--help`, unknown-arg rejection.** No more silent hangs in MCP server mode when a flag is mistyped. `--version` carries the git commit hash via `SourceRevisionId`.

- **Pager suppression in every console.** `PAGER` / `GIT_PAGER` / `MANPAGER=cat` injected at `PtyFactory.Start`. `git log` (~8700 lines) returns in 243 ms instead of waiting on `q`.

### Changed

- **Busy-timeout hint reordered: peek → send → wait.** Matches the order an AI should reach for the rescue tools when a command looks stuck. `peek_console` was previously missing from the hint entirely.

- **GHA actions on actually-Node-24 runtimes.** `upload-artifact@v5` / `download-artifact@v5` carried preliminary Node 24 but defaulted to Node 20; the default moved to Node 24 in v6+. `azure/login@v2 → v3` (also Node 20 → 24).

- **README and npm metadata refreshed.** README leads with descriptor `# Ripple — REPL-sharing MCP for AI Co-Driving`. "Why ripple?" promotes Sensitive operations as the lead sub-section (the most novel differentiator). 19-adapter list bullet-formatted by family. Rename history moved to `## Migration` near the bottom. npm `description` rewritten to lead with co-driving + secret-safety; adapter count corrected (18 → 19, sbcl was missing); keywords expanded 17 → 31 to cover discovery vectors.

- **`list_shells` documented in README** and tool descriptions in `AdapterTools.cs` / `ShellTools.cs` synced with the actual shipping set.

### Fixed

- **DECAWM duplicate-char wrap continuation.** ConPTY re-emits the wrapping char on auto-margin, so on long lines (~100+ chars, e.g. git CRLF warnings) the continuation's first byte duplicated the prefix's last — visible as `CRLLF the next time...`. The cursor redirect now lands at `_lastLfPreCol - 1` so the duplicate overwrites itself harmlessly. See `HANDOFF_GARBLING.md` for the dogfood report.

- **Drift detection switched to direct cwd comparison.** The previous OSC-C-event-counter heuristic miscounted under wrapped scripts, command composition, and during AI command execution. Now asks the actual question: does the current cwd match what the AI thinks it should be? `_userCmdsSinceLastAi` and its `get_status` field are removed.

### Internal

- **`ShellPathResolver` extracted** to fix a layer-violating reverse dependency from `Adapter` (data model) into `ConsoleManager` (runtime). PATH resolution now lives in its own utility.
- **`StatusLineFormatter` consolidates two near-identical implementations** (`ConsoleWorker.BuildStatusLine` + `ShellTools.FormatStatusLine`). Status-line extensions now have one source of truth instead of two sites that had to be updated in lockstep.
- **OSC 633 extension field (de)serialization centralized** in `WriteOscExtensionFields` / `ReadOscExtensionFields`. Adding the next extension is one line per call site instead of four.
- **pwsh exit-code resolution centralized** in `__rp_resolve_exit_code`. OSC D and OSC L can no longer disagree on pipeline success.
- **`AssertOscExt` test helper.** OSC R / T payload tests collapsed: 90 lines removed, 19 added, same coverage.
- Inline comments on two intentionally-bare catches in `ConsoleWorker` (no behavior change).
- `docs/ARCHITECTURE.md` Cache / drain row corrected (file attributions and stale symbol name).
- `.gitignore` adds `.stash-next-release/` for work-in-progress adapters parked between releases.

## [0.12.0] - 2026-04-23

**pwsh output fidelity.** PowerShell error visibility upgraded across the board: cmdlet failures show on the status line as `⚠ Completed with errors | Errors: N` instead of hiding behind a green ✓ on exit 0. Multi-line bodies now ship as base64-encoded scriptblock by default — faster than tempfile, no disk I/O, same dot-source scope semantics. Stale `$LASTEXITCODE` from a preceding native pipeline no longer leaks into the next pure-PS command's reported exit.

**Output rendering hardened at the MCP boundary.** OSC 8 hyperlinks preserved as `<URI>` cells instead of dropped. Dangling SGR at output edges cleaned up (leading no-op reset stripped, trailing reset added when output ends mid-colour). Stale SGR no longer bleeds into cells overwritten with different characters. `ripple-exec-<pid>-<guid>.ps1` tempfile paths stripped from PowerShell `ConciseView` error summaries.

Plus: a queryable `list_shells` MCP tool, two-tier startup stderr (silent modes only surface user-actionable load issues), AI-initiated `cd` no longer misattributed as user drift, PTY width tracks the visible ConHost width so `\r`-based progress bars repaint in place, and a try/finally safety net guarantees `HandleExecuteAsync` releases the user-input hold gate on every exception path.

### Acknowledgments

- @doraemonkeys — PR #7 (JSON-native escape recommendations in `send_input` docs) and PR #8 (surface `send_input` in busy-timeout hint).

### Added

- **`Errors: N` on the status line for PowerShell.** OSC 633;E carries `$Error.Count` delta from the integration script; status line prepends `| Errors: N` when N > 0. Other shells emit no E and stay at zero. `Write-Warning` / `Write-Information` are not tracked — cmdlets bypass any user proxy via `$PSCmdlet.WriteWarning`, so they can't be reliably counted from an integration script.

- **OSC 8 hyperlinks preserved as `<URI>`.** Build tools (`dotnet`, linters), `Write-Information`, IDE integrations emit `\e]8;;<URI>\a link-text \e]8;;\a`. Previously dropped with every other OSC; now `CommandOutputRenderer` injects the captured URI as `<URI>` cells right after the link-text (`"click here<https://example.com/>"`). Both BEL and ST terminators handled.

- **MCP tool hint when output spills to disk.** When output exceeds the 15 KB threshold the spill-file preview now points at ripple's own `search_files` / `read_file` tools — instead of leaving the AI to reach for shell `grep` / `cat` (which round-trips through the same PTY). Bare tool names so the hint stays valid no matter which client prefix wraps ripple.

- **`encoded_scriptblock` multi-line delivery for pwsh.** Base64-encoded `. ([ScriptBlock]::Create(...))` invocation. Same dot-source scope as the tempfile path but no disk I/O and no history-filter bookkeeping. ~0.3–0.5 s faster on the warm path. Shares `BuildMultiLineTempfileBody` so both deliveries produce identical echo + OSC-C semantics — only the wrapper differs.

- **`⚠ Completed with errors` status badge.** Three outcomes instead of two: `exit != 0` → ✗ Failed, `exit == 0, errors > 0` → ⚠ Completed with errors, `exit == 0, errors == 0` → ✓ Completed. PowerShell commands hitting `Write-Error` or non-terminating cmdlet failures exit 0 but bump `$Error` — the plain green ✓ understated what the AI needed to look at. Pairs with the new `Errors: N` tag.

- **`list_shells` MCP tool.** Discovery endpoint for the valid `shell` argument values. Returns name, aliases, family (`shell` / `repl` / `debugger`), source (`embedded` / `external`), and the resolved executable path `start_console` would launch (or `null` + `executable_note` when not on PATH). Any absolute path can also be passed as `shell` to launch an unregistered REPL — with the caveat that without a matching adapter ripple runs it in minimal mode (no prompt detection / exit code / cwd tracking). A `load_issues` object surfaces every parse error / collision / override at startup with a `user_actionable` flag.

### Changed

- **Startup stderr two-tier.** CLI modes (`--test`, `--list-adapters`, ...) still print full `[ripple adapters info]` summary — a human is reading the terminal. Silent modes (MCP stdio, ConPTY worker) now suppress the info-level roll-up and print only user-actionable issues (parse errors / collisions on YAMLs in `~/.ripple/adapters/`). Embedded-only failures are ripple bugs the user can't fix; firing them every prompt was noise. AI consumers needing the full picture call `list_shells`.

- **pwsh multi-line delivery defaults to `encoded_scriptblock`.** The baseline-bleed race that previously tripped encoded delivery is fixed (see Fixed). 20+ consecutive burn-in runs of the original repro produce clean output. `tempfile` remains selectable for bodies exceeding PSReadLine's input line cap.

- **Busy-timeout hint mentions `send_input`.** Previously pointed only at `wait_for_completion`, which never returns when the command is stuck on an interactive prompt (pager, `Read-Host`, y/n). Hint now lists both with role labels. PR #8 by @doraemonkeys.

- **`send_input` docs use JSON-native escapes.** Recommended form is now `\u0003` / `\u001b[A` instead of `\x03` / `\x1b[A`. JSON does not accept `\xNN` — strict parsers rejected the input, lenient ones passed through four literal chars, so the advertised "send Ctrl+C" behavior didn't reach the PTY. Runtime accepts both forms; docs show the JSON-native one. PR #7 by @doraemonkeys.

- **GHA actions bumped to v5** ahead of GitHub's forced Node 24 switch on 2026-06-02 and Node 20 removal on 2026-09-16.

### Fixed

- **Dangling SGR at output boundaries.** Two boundary cases: (a) leading `\e[m` reset that attached as the SgrPrefix of the first non-bar character (`Write-Progress` cleanup → `[mafter` garbage); (b) missing trailing reset when output ends mid-colour, leaving the next prompt stuck red. `CleanString` now strips leading reset-only SGR and appends `\e[0m` when end-of-output state is non-default.

- **Tempfile path leakage in multi-line errors.** PowerShell's `ConciseView` prefixed error summaries with `{Cmdlet}: {TempfilePath}:{Line}`, exposing ripple's `.ripple-exec-<pid>-<guid>.ps1` wrapper. Now stripped; the `Line | N | <source>` diagnostic block below is preserved.

- **Stale SGR bleeding into overwriting characters.** `Write-Progress`'s reverse-video bar cells leaked `\e[7m` onto subsequent normal text — `Write-Progress ... ; "after"` produced garbled `[mafter [7mprogress`. `WriteChar` now treats same-char repaint differently from genuine content change: same-char keeps SGR (ConPTY idempotence), different-char drops stale SGR.

- **Stale `$LASTEXITCODE` leaking into pure-PS pipelines.** `$LASTEXITCODE` is only updated by native exes; after `cmd /c "exit 7"` the value stayed 7 and innocent PS pipelines (`Get-Date`) reported `Failed (exit 7)`. Fixed by capturing `$?` first in the prompt fn, snapshotting `$LASTEXITCODE` at `PreCommandLookupAction`, and resolving via priority: `$?` true → 0; `$?` false + LEC changed non-zero → use it; `$?` false otherwise → 1.

- **AI's own `cd` no longer misattributed as user drift.** Cwd-snapshot drift detection produced spurious "moved by user" notices when standby rotation or `RecordShellCwd` race lagged the snapshot. Replaced with a provenance counter (`UserCmdsSinceLastAi`) that increments at OSC A close-of-user-busy and resets on `RegisterCommand`.

- **Explicit LineFeed clears stale soft-wrap flag on baseline rows.** Pre-command rows that were wrap continuations kept their `ContinuedFromAbove` flag, chaining command-emitted lines onto erased predecessors (`abiter 1` instead of `a\nb\niter 1`). Now cleared when LineFeed targets a row in the baseline range.

- **PTY width tracks the visible ConHost width.** Old floor-at-200 broke `Write-Progress` self-repaint — bars padded to the declared width physically wrapped past visible cols, so `\r` returned only to the wrapped row and updates stacked vertically. Matching visible width fixes in-place repainting; mid-word splits on narrow terminals remain a theoretical edge the AI can read around (matches what the user is looking at).

- **`_vtState` fed in OSC-aligned slices.** A single PTY read straddling OSC C and command output had the renderer baseline include cells from the very output it was about to replay (writing `"a"` onto a row whose stale tail was `"ter 3"` produced `"ater 3"`, or dropped the line when the cursor landed on an already-written row). Slice-by-offset feed so the snapshot at `CommandExecuted` reflects state at the OSC C byte exactly. Unblocks `encoded_scriptblock` multi-line as the new default.

- **`HandleExecuteAsync` user-input hold gate guaranteed to release.** Previously leaked on any unhandled exception path — flag stayed true, queued keystrokes never drained, next `execute_command` started with a poisoned shell. try/finally added; release is idempotent so duplicate calls on normal paths are no-ops.

## [0.11.0] - 2026-04-20

**Cross-platform shipping.** Native binaries for Linux x64 and macOS arm64 ship alongside Windows. `npm i -g @ytsuda/ripple` resolves the right platform automatically via `optionalDependencies`; a small Node launcher (`bin/cli.mjs`) spawns the matching binary with stdio inherited and SIGTERM / SIGINT / SIGHUP / SIGQUIT forwarded.

The release workflow is now a three-runner build matrix (windows / ubuntu / macos) followed by a single Linux publish job that Authenticode-signs the Windows binary via AzureSignTool, publishes three subpackages + a meta package with SLSA provenance, and attaches all three binaries to the GitHub Release.

### Added

- **Linux x64 and macOS arm64 native binaries.** NativeAOT-compiled from the same source as the Windows binary; identical `--test` suite gates publish. macOS Intel (`osx-x64`) deferred — GHA's macos-13 runner pool capacity made it unshippable.

- **`@ytsuda/ripple-<platform>` subpackages.** Each carries `os` + `cpu` filters; npm installs only the matching one and skips the rest silently via `optionalDependencies`.

- **`npm/bin/cli.mjs` dispatcher.** Resolves `<platform>-<arch>` against an allow-list, locates the subpackage binary, spawns with `stdio: 'inherit'`, forwards SIGTERM / SIGINT / SIGHUP / SIGQUIT, and re-raises the child's exit signal so `$?` / `%ERRORLEVEL%` / `$status` match a direct invocation.

### Changed

- **Release workflow split into matrix build + single-runner publish.** `build` runs over `{win-x64, linux-x64, osx-arm64}` and uploads artifacts; `publish` (Linux, `environment: release`) downloads all three, signs the Windows binary, then publishes subpackages sequentially (win32-x64 → linux-x64 → darwin-arm64) before the meta package — a mid-sequence failure halts before the meta points at a missing subpackage.

- **`npm/package.json` is now a meta package.** Ships only `bin/cli.mjs`, README, LICENSE; `optionalDependencies` pin the three subpackages to the exact current version. The `os` restriction is lifted — Node installs everywhere; the native binary requirement is enforced by the subpackage filter.

- **Version cross-check verifies five fields:** csproj `<Version>`, meta `npm/package.json`, and each of the three platform `package.json` must match the pushed tag; publish aborts fast on disagreement.

## [0.10.0] - 2026-04-20

Three parallel rounds land in one release.

**(1) Live VT cursor tracking.** ripple now keeps an authoritative VT-100 interpreter advanced from every PTY chunk and answers DSR (`\x1b[6n`) cursor-position queries from real state instead of the static "near the bottom of the screen" heuristic it shipped with. Closes the Unix drift where PSReadLine's up-arrow history recall painted over the active prompt and bash readline wrapped long input into the wrong column after a few AI commands had scrolled by.

**(2) Oversized output spilled to a temp file** (closes #1, PR #5). Outputs over 15,000 chars are written to a worker-owned spill file (`%TEMP%\ripple.output\` on Windows, `${TMPDIR:-/tmp}/ripple.output/` on Unix) and the MCP response returns a head + tail preview embedding the spill path. Inline `execute_command` and deferred `wait_for_completion` flow through a shared finalize-once boundary so both delivery modes return the same `CommandResult` shape.

**(3) Command-output extraction rebuilt as a per-command renderer fork** (closes #4). At OSC C the worker snapshots the session-wide `_vtState` and hands the snapshot to a new `CommandOutputRenderer` initialised from it. ConPTY's post-prompt redraw bursts target cells whose baseline values match what's being rewritten, so a per-cell change detector recognises them as idempotent overwrites and they stay out of the AI-facing response. Alt-screen entry / exit collapses to an `[interactive screen session]` placeholder; soft-wrapped logical lines re-join at render time so a narrow PTY can't fragment a single `git log --oneline` entry.

### Acknowledgments

- @doraemonkeys — reported the oversized-output overflow as #1 and contributed PR #5 (round 2's spill-to-tempfile fix).
- @luchezarno — reported #4 with a detailed Git Bash log that pinpointed the ConPTY post-prompt redraw burst as the cause of the dropped grep matches.

### Added

- **`Services/VtLiteState.cs`** — VT-100 interpreter formerly embedded in `CommandTracker`, now a public class with `Feed(ReadOnlySpan<char>)`. A 16 KB pending-escape buffer stitches CSI / OSC sequences split across PTY reads. The static `VtLite(...)` one-shot helper is preserved for compatibility.

- **CSI catalog growth.** `ECH`, `DCH`, `ICH`, `IL`, `DL` handlers added — readline / PSReadLine emit these for in-line editing; previously dropped to the silent default branch.

- **`ConsoleWorker.AnswerAndStripDsr`** — pure helper carrying up to 3 partial DSR prefix bytes across PTY reads via `ref string pendingPrefix`. Fires the reply callback once per detected DSR (was once per chunk regardless of count) and strips the partial prefix from output so it never leaks downstream.

- **`Services/CommandOutputCapture.cs`** — bounded raw-capture store (small hot char buffer + scratch-file spill, offset-based slice readers + bounded snapshot for timeout `partialOutput`). Worker-private; distinct from the public `ripple.output` spill directory.

- **`Services/CompletedCommandSnapshot.cs`** — lightweight record the tracker emits on primary completion: capture handle, command-window offsets, exit metadata, cwd, shell family, settle policy, and the exact `ptyPayload` baseline.

- **`Services/CommandOutputFinalizer.cs`** — slice-reader-driven cleaner + `EchoStripper` for `deterministic_byte_match` adapters. Reads from offset-based capture slices instead of rebuilding tracker state from one monolithic in-memory output buffer.

- **`Services/OutputTruncationHelper.cs`** — preview + spill-file creation, DI-friendly (`IOutputSpillFileSystem`, `IClock`). Threshold 15,000 chars, head ~1,000, tail ~2,000, newline scan ±200, retention 120 min. Files referenced by undrained cached results are never cleaned.

- **`Services/CommandOutputRenderer.cs`** — cell-based per-command fork of `VtLiteState`. Optional `VtLiteSnapshot` baseline; rows pre-populated from the snapshot grid, cursor / saved cursor / scroll region / SGR / alt-screen state carry over, baseline rows stash a `BaselineCells` copy for the per-cell change detector. Implements CUU/CUD/CUF/CUB/CNL/CPL/CHA/VPA, CUP/HVP, EL/ED, ECH, DCH/ICH, IL/DL, save/restore, alt-screen save/restore, viewport-top tracking.

- **`VtLiteState.Snapshot()` + per-row soft-wrap + active-SGR tracking.** Snapshot deep-copies primary + alternate grids, soft-wrap flags, cursor, saved cursor, scroll region, alt-screen flag, and accumulated active SGR. `WriteChar`'s auto-wrap flips a per-row `ContinuedFromAbove` flag (set on wrap, cleared on hard LF / EraseLine mode 2 / EraseDisplay; shifted with grid rows on scroll). `RecordSgr` accumulates non-reset SGR since the last `\e[m` so the snapshot's `ActiveSgr` can seed the renderer's first-cell prefix.

- **`CompletedCommandSnapshot.VtBaseline`** — optional snapshot field threaded through `CommandTracker` ↔ `ConsoleWorker` ↔ `CommandOutputFinalizer.Clean`. The worker snapshots `_vtState` (session-wide, not the tracker's per-OSC-C-reset one) right before forwarding the OSC C event so the renderer receives the screen state ConPTY has at command start.

- **Soft-wrap re-joining at render time.** Rows with the `ContinuedFromAbove` flag append to the previous emitted line without an inter-row newline, so a long `git log --oneline` entry reaches the AI as one logical line.

- **Alt-screen as placeholder.** Entry switches to a separate row list for alt-buffer writes; exit restores the main-buffer cursor and inserts a single `[interactive screen session]` line. ConPTY's post-exit redraw of the saved main buffer is naturally absorbed by the per-cell baseline diff.

- **Regex-prompt cap.** `RegexPromptDetector.Scan` returns `(Start, End)` per match. Worker fires synthetic `CommandFinished` + `PromptStart` at `Start` so visible prompt characters are excluded from the `[commandStart, OSC A]` window — fixes trailing `(Pdb)` / `DB<N>` / `>` leak for pdb / perldb / python / any regex-prompt REPL. `Start` also backs past contiguous non-reset SGR immediately preceding the prompt match (REPL prompt decoration).

- **Test coverage:** 34 `VtLiteStateTests` asserts (split-at-every-byte property test, alt-screen save/restore, SGR no-shift, bracketed paste, pending-buffer overflow safety); 29 new `ConsoleWorker Unit Tests` for DSR splits; spill / finalize coverage in `OutputTruncationHelper Tests` (32), `CommandOutputCapture Tests` (20), `CommandOutputFinalizer Tests` (22), `ConsoleWorker Cache Unit Tests` (59); end-to-end `SpillIntegrationTests` (41 under `--test --e2e`); 23 new renderer tests. 728 tests pass.

- **1 MiB Feed throughput bench** (informational, not pass/fail). Baseline on Win11 AOT release: 174 MiB/s — the `<5%` overhead bar is cleared with three orders of magnitude of headroom.

### Changed

- **DSR reply uses live cursor state.** Reads `_vtState.Row+1` / `Col+1` instead of static row + heuristic column. Heuristic retained only as fallback during the pre-first-chunk shell-startup window. Windows path is dormant in practice — ConPTY intercepts DSR before ripple sees it.

- **`peek_console` snapshot routes through live state.** `GetRecentOutputSnapshot` returns `_vtState.Render()` directly instead of re-parsing the recent-output ring through a fresh `VtLite()` on every call. Tracker keeps its own `VtLiteState` fed from `FeedOutput`, reset on the same triggers as the ring. Raw ring buffer is retained for `GetRawRecentBytes()` — `ModeDetector` reads bytes pre-VT-reshape.

- **Allocation-minimal `Feed` hot path.** `Feed` / `ParseEscape` / `ApplyCsi` operate on `ReadOnlySpan<char>` directly. `paramsStr.Split(';')` replaced with a zero-alloc `GetParam` helper; pending merges use a 512-char `stackalloc` buffer with `ArrayPool<char>` fallback. With allocation pressure removed, live tracking runs on every platform — the earlier `!OperatingSystem.IsWindows()` gate (added when GC pressure caused deno adapter-test flakes) is gone.

- **Finalize ownership moved from `CommandTracker` to `ConsoleWorker`.** Tracker now only emits a `CompletedCommandSnapshot` on primary completion; worker runs cleaning, echo-stripping, truncation, and cache insertion in one place. `ConsoleManager` no longer reassembles output via `drain_post_output` — it forwards the worker's finalized `CommandResult` directly. Inline and deferred always read from the same finalized result shape.

- **Bare `\r` is now cursor-reset only (no row clear).** Matches what the human sees in the live terminal. Spec semantics produce identical results for properly-formed progress bars (`\r\x1b[K` or full rewrites); slightly truthier results for short rewrites that match the live-terminal residue.

- **Node REPL integration script emits OSC bytes BEFORE the visible prompt** instead of after — same pattern pwsh's prompt fn has always used. Eliminates the trailing `> ` on every node REPL command.

- **`Build.ps1` gains `-Sign`.** Optional Authenticode signing of `dist/ripple.exe` before npm/dist deploy. Defaults preserve unsigned dev workflow; pass `-Sign` for publish builds. PFX password read interactively via `Read-Host -AsSecureString`.

### Fixed

- **#4 — `echo … | grep` on Git Bash via ConPTY no longer drops trailing match lines.** ConPTY's screen-redraw burst around prompts contains real grep output (`cherry`, `elderberry`) plus absolute cursor positioning that the legacy `StripAnsi` silently dropped — leaving only the first match (`banana`). The renderer processes cursor positioning correctly and per-cell baseline diff treats matching repaint as idempotent — all matches survive.

- **Cross-chunk DSR queries no longer leak downstream.** Old `text.Contains("\x1b[6n")` substring check missed DSR queries straddling two PTY reads. Partial ESC bytes flowed into parser / mirror / output; shell sat indefinitely waiting for a reply that never fired. New `AnswerAndStripDsr` buffers the partial prefix, completes on next chunk, replies once.

- **Orphaned inline `TaskCompletionSource` on `HandleExecuteAsync` timeout / shell-exit.** Previously the inline TCS was not detached on those branches, breaking `wait_for_completion`'s ability to drain timed-out commands. Per-id routing through `_inlineDeliveriesById` closes the race.

- **OSC stripping no longer swallows past a prior ST terminator.** OSC alternative `\x1b\][^\x07]*\x07` previously matched across an earlier `ESC \\` (ST) terminator to a later bare BEL when input mixed ST-terminated title OSCs with subsequent BELs. Tightened so each OSC stops at its own terminator.

- **Unix spill file permissions.** Spill directory is `0700`, files `0600` via `UnixCreateMode` on .NET 9. Command output (which can contain secrets) is no longer world-readable on multi-user hosts.

- **Progress-bar redraws no longer flood AI-visible output.** `StripAnsi` rewritten as a line + cursor-row tracker: bare `\r` overwrites in place, CSI cursor-up rewinds row index, EL/ED clears, `\b` undoes one char. Previously stripped as raw bytes; a `./Build.ps1` invocation filled output with dozens of stale frames. Visible mirror is untouched — human still sees a live progress bar.

- **Trailing `\e[m` reset SGR is no longer lost** when output ends `text\r\n\e[m`. `Render` flushes pending SGR to the last non-empty row's `TrailingSgr` so end-of-output color resets reach downstream consumers instead of leaving color stuck on.

### Known limitation

- **First alt-screen run on a fresh console after non-alt commands** may include ConPTY's post-exit redraw of the visible session history (typically 3–6 prior prompts). Subsequent alt-screen runs in the same console produce clean output. Cause: subtle divergence between `ConsoleWorker._vtState` incremental session state and ConPTY's screen view; the first ConPTY full redraw syncs them. Mitigation: ignore the first noisy response or discard one warm-up command. Affects only alt-screen workflows (vim, less, htop) on the very first such command of a session.

### Release infrastructure

`.github/workflows/release.yml` triggers on `v*` tag pushes. NativeAOT publish, unit-test gate, tag/version cross-check, then Azure OIDC login → Authenticode sign via Azure Key Vault (`kv-yotsuda-sign`) → `npm publish --access public --provenance` → `gh release create` with the per-version CHANGELOG section as release body and `dist\ripple.exe` attached. The `release` environment requires reviewer approval and locks deploys to `v*` tags. Federated credential is repo+environment scoped; no client secret stored in the repo. The npm publish carries an [SLSA build provenance attestation](https://docs.npmjs.com/generating-provenance-statements) verifiable back to the workflow run.

## [0.8.0] - 2026-04-16

**Debuggers as a first-class adapter type.** The schema gains three additive fields — `process.executable_candidates`, `modes.advance_commands`, `commands.debugger` — so a single YAML can describe any debugger's step / print / breakpoint vocabulary in a vendor-agnostic way. AI agents drive perldb and jdb using the same operation names, no per-debugger knowledge required. perldb / jdb / pdb are the first `family: debugger` adapters.

Three new debugger adapters plus three new REPLs (sqlite3, lua, deno) bring the embedded set to **18 adapters**. Plus two root-cause fixes that close out the user-input-contamination class of flakes: a hold-gate buffers user keystrokes during AI command execution, and `--adapter-tests` worker windows launch fully hidden (SW_HIDE) so a long test run no longer disrupts the user's other windows. 100 adapter assertions green.

### Added

- **`family: debugger` adapter framework with three instances.**
  - **`perldb`** — Perl `perl -d -e 0` scriptless debugger. Prompt `  DB<N> `, nested `  DB<<N>> ` on breakpoint pause. 8 tests including end-to-end breakpoint-hit / inspect / continue / verify-return chain.
  - **`jdb`** — Java Debugger detached mode (`jdb` with no target class). Prompt `> `. 5 tests covering deferred breakpoint registration, meta-command dispatch, detached-mode restrictions.
  - **`pdb`** — Python's built-in debugger via `python -c "import pdb; pdb.Pdb().set_trace()"`. Prompt `(Pdb) `. 6 tests.

- **`process.executable_candidates`** (schema §3). Ordered launcher list tried left-to-right with `%VAR%` env expansion; first existing file wins. Solves "single absolute path doesn't port across distributions" for interpreters with multiple install locations (Strawberry / ActivePerl / Git-bundled perl; Microsoft OpenJDK / Temurin / Corretto / Zulu; python.org / Windows Store / Anaconda). Falls back to `executable`, then the adapter name.

- **`commands.debugger`** (schema §10). Structured debugger vocabulary: navigation (`step_in`, `step_over`, `step_out`, `continue`, `run`), inspection (`print`, `dump`, `backtrace`, `source_list`, `locals`, `where`, `args`), breakpoints (`breakpoint_set`, `breakpoint_set_line`, `breakpoint_list`, `breakpoint_clear_all`). Each field is a template with `{expr}` / `{target}` / `{line}` / `{file}` placeholders, or `null` when unsupported. AI agents discover the syntax from the adapter instead of parsing help text.

- **`modes.advance_commands`** (schema §9). Distinct from `exit_commands`: advance commands (step_in / step_over / step_out) change position within the same paused mode without leaving it. Lets AI agents distinguish "stepped one line, still paused" from "resumed and left the breakpoint".

- **`sqlite3` REPL adapter.** `sqlite3 :memory:` with `.mode list` + `.headers off` forced at startup so query output is pipe-separated and regex-friendly. 6 tests.

- **`lua` REPL adapter.** Lua 5.4 interactive interpreter with classic `> ` prompt. Probes use the `=` prefix shortcut (Lua 5.3+) to return values without `print()`. 6 tests.

- **`deno` REPL adapter.** Deno 2.x for JavaScript / TypeScript. Distinct from node — Deno evaluates TypeScript directly (no ts-node), supports top-level await, has built-in Web Platform APIs. `NO_COLOR=1` for clean regex matching. 6 tests.

- **`--no-user-input` worker flag.** Test workers permanently hold user keystrokes via `InputForwardLoop`. Prevents typing in unrelated windows from leaking into test PTYs — the root cause of the intermittent jshell / node / fsi / jdb-hello flakes on the 0.7 release train.

- **OSC sequence stripping in `RegexPromptDetector`.** `StripCsiWithMap` now consumes `ESC ] ... BEL` and `ESC ] ... ESC \` in addition to CSI. Fixes failures where ConPTY's window-title setter (`ESC ] 0 ; <path> BEL`) sat between banner and prompt, preventing `^` anchoring.

### Changed

- **User input held during AI command execution.** New `_holdUserInput` gate buffers keystrokes into `_heldUserInput` instead of forwarding to the PTY while `HandleExecuteAsync` is in flight. Held bytes replay after the command completes (success / timeout / error). Ctrl+C passes through even when held so the user can always interrupt. Operates at ripple's forwarding layer (above the shell), universal across adapters regardless of line-editor presence.

  The hold gate complements the pre-existing `input.clear_line` field — they cover different windows. Hold gate protects *during-command* keystrokes (between AI command arrival and output drain). `clear_line` protects *between-command* keystrokes (user-typed bytes already in the shell's line editor before the next AI command), by issuing line-editor-kill bytes (Ctrl-A + Ctrl-K for readline) right before submitting. Both remain in use.

- **`--adapter-tests` worker windows hidden (SW_HIDE).** Normal usage keeps `SW_SHOWNOACTIVATE` (visible-but-inactive). Test runs gate on `noUserInput` to switch to fully invisible windows. Rapid window creation during a full test suite (15+ workers) previously caused focus churn that disrupted the user's other windows — now the entire test run is silent.

### Fixed

- **`RegexPromptDetector` missed prompts behind OSC window-title sequences.** Pre-0.8 the stripper handled CSI only, leaving OSC sequences intact. When ConPTY emitted `ESC ] 0 ; <path> BEL` right before the first prompt, the title bytes sat between banner newline and prompt and a regex like `^> $` couldn't anchor. The jdb adapter's initial-prompt detection blocked on this exact pattern. `StripCsiWithMap` now also consumes OSC via BEL or ST terminator.

- **`CS8600` warning in `HandleExecuteAsync`.** `ModeMatch` is a record (reference type), so `ModeMatch match = default` gave null and triggered flow-analysis warnings. Replaced `default` with an explicit `new ModeMatch(Name: null, Level: null)` sentinel so the variable is never null.

- **jdb `next`-stepping test was state-dependent.** Relied on a previous test leaving the VM paused at a breakpoint. The fragile design and the `cont` async race (jdb returns the post-resume prompt while the VM is still running) are documented in the adapter YAML for future `async_interleave` work.

- **CCL / ABCL `execute_command` responses leaked the next `? ` prompt.** `CleanDelta`'s trailing-prompt suppressor recognised `$ # % > ❯ λ` but not `?`. Fix: add `line.EndsWith('?')` to `IsShellPrompt`. Nested break-loop prompts (`1 > ` / `2 > `) were already matched via `>`.

### Known limitations

- **`dotnet-dump analyze` is post-mortem only.** Adapter shipping deferred — has the same fixture-dependency problem as `jdb-hello` (a dump file must exist at adapter-launch time).
- **IPython under ConPTY.** `--simple-prompt` still emits absolute cursor positioning (`\x1b[5;1H`) when it detects a TTY-like environment. `TERM=dumb` and `PROMPT_TOOLKIT_NO_CPR=1` don't override — it's a ConPTY-specific codepath inside IPython. The stdlib `python` adapter remains the recommended Python REPL.
- **perldb `b subname` on `do`-loaded subs.** Setting a function-name breakpoint on a sub loaded via `do 'file.pl'` may silently not fire — address resolution doesn't always match. Workaround in `perldb.yaml`: use line-number breakpoints (`b {line}`) after `l subname` finds the target lines.

## [0.7.0] - 2026-04-15

**Polish round + Clozure Common Lisp ships embedded.** Seven bug fixes from adversarial testing of the v0.6.0 surface — window title split-chunk leak, nested datum comments, node / groovy `signals.interrupt` mis-declaration, console focus theft, line-editor buffer flush, mode detection against the wrong input source. Each fix started with a complaint or a suspected weakness, pinned the broken behaviour with a test, then replaced the implementation.

**12 embedded adapters** for the first time: CCL moves out of the local gitignore after empirical confirmation that the corporate AppLocker block which motivated the exclusion has been relaxed. 528 / 528 assertions pass — `--test` (458 unit) + `--adapter-tests` (70 declared, 12 adapters). The two pre-existing `ConsoleWorkerTests.Run` flakes (Ctrl+C standby, obsolete PTY alive) predate 0.6 and are tracked separately — invisible to release binaries.

### Fixed

- **Owned console window titles got clobbered by split-chunk OSC.** `ReplaceOscTitle` was stateless and couldn't handle an OSC 0/1/2 title sequence straddling a PTY read-chunk boundary — the partial opener leaked to the visible terminal and the terminal interpreted following bytes as the shell's title until whatever terminator arrived. Now uses a `ref string pendingTail` to buffer unterminated openers across chunks. 37 new unit asserts cover splits at every byte boundary plus non-title OSCs (633, 7, 112) flowing through untouched.

- **`#;#;(a)` reported submit-ready when it needed a second datum.** `BalancedParensCounter`'s atom-run-consume branch decremented `pendingDatumComments` on atoms inside a datum-commented list — atoms already being skipped by the list's own bracket accounting. Stacked datum-comment prefixes with only one following datum therefore silently resolved. Fix: gate the atom-run branch on `datumCommentAnchorDepths.Count == 0`. 32 new stress asserts cover reader-macro pathologies: char literals of quote / semicolon / pipe / backslash, multi-line strings, 200-deep nesting, quasi-quote, CL `#+nil` passthrough.

- **Node / groovy mis-declared `signals.interrupt`.** Live verification of all 10 adapters found two lies: Node's REPL can't handle Ctrl-C while its event loop is blocked (signal handler runs on the same thread), and groovysh's Ctrl-C terminates the JVM outright. Both flip `capabilities.interrupt` to `false`. Groovy's `signals.interrupt` is now `null` so MCP clients don't even try the send. SCHEMA §11 extended with nullable-interrupt semantics; `SignalsSpec.Interrupt` is now `string?`.

- **New console windows stole keyboard focus.** `CreateProcessW` with only `CREATE_NEW_CONSOLE` activates the new window by default — starting a ripple shell while the user types in their editor dropped keystrokes into ripple's buffer. Now sets `STARTF_USESHOWWINDOW | wShowWindow = SW_SHOWNOACTIVATE`: window displays but isn't activated, editor keeps focus.

- **User-typed bytes prepended to next AI command.** Even with the focus fix, users occasionally click into a ripple console and type before noticing. Those bytes sit in the line editor and get submitted together with the next AI command as one garbled line. New `input.clear_line` schema field carries bytes to write before each execute to wipe the current line. Default `null` (opt-in per adapter) because Python basic REPL / fsi `--readline-` / Racket `-i` / CCL / ABCL run without a line editor and parse `\x01\x0b` (Ctrl-A + Ctrl-K) as literal input. Opted in for bash and zsh where readline / ZLE emacs defaults treat the bytes as no-ops on an empty buffer.

- **`ModeDetector` never saw the post-OSC-A prompt.** Auto_enter + nested + level_capture scanned `cleanedOutput` (the OSC-C..D slice), but the mode-transition signal lives in the NEXT prompt (`1 > ` for CCL's break loop, `(Pdb) ` for Python, `N] ` for SBCL) which arrives AFTER OSC A fires Resolve. `GetRecentOutputSnapshot()` was the obvious alternative but routes the ring through VtLite, which reshapes the trailing prompt into cell-addressed coordinates that break `^<prompt>$` anchors. Fix: scan `GetRawRecentBytes()` in a short 150 ms poll loop, breaking out as soon as a non-default auto_enter mode matches. Verified against a 4-test chain walking CCL's break loop. Schema §18 Q2 is now backed by runtime evidence.

### Added

- **Clozure Common Lisp (CCL) ships embedded.** Through v0.6.0 `adapters/ccl.yaml` + `ShellIntegration/integration.lisp` lived locally-only because corporate AppLocker on the dev box blocked user-dir PE files under ConPTY spawn (`CreateProcessW failed: 5`). On 2026-04-15 that block is empirically gone: `--adapter-tests --only ccl` runs 10 / 10 green covering probe, five expression-level tests (arithmetic, `defparameter` persistence, block-comment / char-literal reader macros, default mode), and a four-test debugger-mode chain verifying §18 Q2's auto_enter + nested + level_capture path. CCL is the first native-binary Lisp in the embedded set alongside the JVM-hosted ABCL. On boxes where AppLocker persists the probe still soft-fails — same class as a missing zsh on Windows, not a regression.

- **`--adapter-tests [--only <name>]` CLI flag** — shipped in 0.6.0; now documented as the canonical way to exercise adapter-declared tests without the pre-existing `ConsoleWorkerTests.Run` flakes hard-exiting the process. Useful for verifying a new adapter in isolation.

- **`input.clear_line` schema field** documented in SCHEMA.md §8 alongside the empirical-verification requirement ("walk the adapter in ripple, type into its console, confirm the clear bytes wipe the buffer without syntax errors, then add the field"). Bash and zsh opt-in; everything else null by default.

- **ABCL 1.9.2 gotcha in HANDOFF.md** — `--add-opens java.base/java.lang=ALL-UNNAMED` on the Groovy-pattern command template silences the JDK 21 virtual-threading warning ABCL 1.9.2 prints on every cold start.

- **Pre-existing E2E flake documentation in HANDOFF.md** — the two `ConsoleWorkerTests.Run` tests (Ctrl+C post-interrupt standby, obsolete PTY alive) and the `--adapter-tests` standalone workaround. Invisible to release binaries; only affects `--test --e2e` during development.

### Changed

- **`SignalsSpec.Interrupt` is now `string?`.** YAML default stays `"\x03"`; adapters with destructive Ctrl-C handlers (groovy today, future hosts) can set `interrupt: null` to signal "no safe interrupt byte available".

- **Mode detection poll window.** `HandleExecuteAsync` now waits up to 150 ms for a non-default auto_enter mode to appear in the raw ring after a command resolves. Happy path (default mode) returns immediately; transitions get up to 150 ms of headroom.

- **`capabilities.interrupt` flipped to `false`** for node and groovy. MCP clients querying the flag now see the honest story: sending Ctrl-C will NOT rescue a runaway command.

## [0.6.0] - 2026-04-15

**Cache-on-busy-receive salvage layer.** When a command is in flight and the MCP client silently drops the response channel — ESC cancel, the MCP protocol's 3-minute ceiling, or a fresh tool call sneaking in on the same console — the worker flips the in-flight command to cache-on-complete mode so its eventual result lands in a per-console list instead of being silently discarded. The next tool call — **any** tool call, not just `execute_command` — drains the list and surfaces the result to the AI. Mirrors the PowerShell.MCP pattern, then closes three implementation holes observed in its reference.

**Armed Bear Common Lisp (ABCL) joins the embedded adapter set**, giving ripple a JVM-hosted Lisp reference for the `balanced_parens` counter and proving the **Groovy pattern** (java.exe from a whitelisted Program Files path loading a jar payload from `%LocalAppData%`) works for any future JVM-hosted REPL. 536 / 536 assertions pass (408 unit + 79 pre-existing E2E + 49 adapter-declared).

### Added

- **`CommandTracker.FlipToCacheMode()`** detaches the in-flight TCS with a `TimeoutException` and marks `_shouldCacheOnComplete` so the eventual OSC-driven `Resolve()` appends to `_cachedResults` instead of delivering to the original caller. Invoked by two paths: the 170 s preemptive deadline firing, and `HandleExecuteAsync` catching a fresh `execute_command` on a busy console (proof the prior caller stopped listening).

- **Multi-entry cached results per console.** `_cachedResults: List<CommandResult>` replaces the old single-slot so sequential flipped commands accumulate without racing to overwrite. `ConsumeCachedOutputs()` drains the whole list atomically; `get_cached_output` returns a `results` array.

- **170 s preemptive timeout cap.** `CommandTracker.PreemptiveTimeoutMs = 170_000` and `ConsoleManager.MaxExecuteTimeoutSeconds = 170` clamp both worker-side timer and proxy-side pipe wait, so `execute_command` always returns within the MCP 3-minute window even when the command keeps running in the background.

- **Worker-baked status line on cached results.** `SetDisplayContext(displayName, shellFamily)` lets the worker compute a self-describing status line at `Resolve` time, threaded through `CommandResult.StatusLine` → `ExecuteResult.StatusLine`. `AppendCachedOutputs` prefers it over proxy-side reformatting so drained output reads identically to inline results — even if the console has since been reused.

- **84 new cache / drain test assertions** — 76 `CommandTrackerTests` (flip semantics, list accumulation, atomic drain, status-line formatting per shell, cache survival across `RegisterCommand`, 170 s cap, wall-clock preemptive-timer path) + 8 `ConsoleWorkerTests` E2E covering the multi-entry wire protocol (back-to-back short-timeout commands stack and drain in one RPC).

- **Armed Bear Common Lisp (ABCL) adapter** — `adapters/abcl.yaml` + `ShellIntegration/integration.abcl.lisp`. Second CL adapter, first JVM-hosted Lisp embedded. Runs ABCL 1.9.2 from `%LocalAppData%\ripple-deps\abcl-bin-1.9.2\` via `java.exe` from Program Files — the **Groovy pattern** that bypasses AppLocker's user-dir PE block. Integration script `setf`s `top-level::*repl-prompt-fun*` to an OSC-emitting wrapper — simpler than CCL's `ccl::print-listener-prompt` override. Prompt overridden from `CL-USER(N): ` to literal `? ` so both CL adapters share mode regexes. Probe + 5 tests pass on `--adapter-tests --only abcl`. Debugger mode deferred (ABCL's `system::debug-loop` uses a separate prompt mechanism).

- **`--adapter-tests [--only <name>]` CLI flag** — runs each adapter's declared `tests:` block standalone, without the `ConsoleWorkerTests.Run` harness whose pre-existing flakes hard-exit on failure. Useful for verifying a new adapter in isolation.

### Changed
- **`execute_command` timeout cap** — the tool's `timeout_seconds` still defaults to 30 but now hard-caps at 170 s; larger values are silently clamped. Worker-side `RegisterCommand` applies the same cap internally so the pipe wait and worker timer both unwind inside the MCP 3-minute window.
- **`CollectCachedOutputsAsync` / `WaitForCompletionAsync` drain loops** — both consume the new `results` array from `get_cached_output` and emit one `ExecuteResult` per cached entry. A console that accumulated three flipped results surfaces as three entries in the next tool response.
- **`AppendCachedOutputs` in every MCP tool** — `send_input` is now wrapped like the rest, so its response also drains any cached results on its target console and reports other consoles' busy / finished / closed state. Closes the last gap where a tool response could omit freshly-ready cached output.

### Fixed
- **Drain hole: read-only MCP tools didn't surface stale cache** — PowerShell.MCP's `CollectAllCachedOutputsAsync` is only called from execute / wait_for_completion handlers, so tools like `get_current_location` leave other consoles' caches sitting until the next execute. ripple now drains from every tool response (execute, wait_for_completion, start_console, peek_console, send_input), matching the user's "any MCP tool response" requirement.
- **Drain hole: older cache hidden behind timeout / flipped branches** — PS.MCP's `invoke_expression` timeout / `shouldCache` branches don't consume older cache entries, so they sit until the next normal completion. ripple's atomic `ConsumeCachedOutputs` picks up everything in one call regardless of which branch fires.
- **`CancellationTokenRegistration` self-disposal deadlock** — `FlipToCacheMode` originally disposed `_timeoutReg` inline, but when called FROM the token's own callback via `Register(FlipToCacheMode)`, `CTR.Dispose` blocks until the callback finishes — which was the same thread currently inside the callback. Disposal now happens in `Resolve`'s cache branch and `AbortPending`, where the command has already finished running.

### Known limitations
- **Silent fast-completing ESC cancel** — if `execute_command` completes normally in well under 170 s but the client already stopped listening (ESC fired before anything triggered a flip), the result is delivered via `_tcs.TrySetResult` and never enters the cache. ripple has no way to detect client-side cancel without a protocol extension. Commands taking more than a few hundred milliseconds are covered via the flip-on-busy-receive trigger as soon as the next tool call arrives.
- **Cross-agent salvage not attempted** — the drain walks only consoles owned by the current agent. A sub-agent's flipped cache is not visible to the parent agent's tool calls, by design (agent isolation is a first-class ripple concept).

## [0.5.0] - 2026-04-13

**cmd.exe and bash polish driven by systematic shell-by-shell testing.** cmd is now usable for AI commands instead of hanging indefinitely. bash subshells and command substitutions resolve correctly. pwsh integration tolerates a missing PSReadLine module without crashing the worker. 240 / 240 tests pass.

### Added
- **cmd.exe AI command support** — multi-line cmd commands (heredoc-equivalent batch blocks, `if/else`, `for /l`, `setlocal enabledelayedexpansion` with `!VAR!`) now work via a tempfile `.cmd` wrapper called from a single-line PTY input. ConPTY's input echo is stripped from the captured slice via `StripCmdInputEcho` so AI output mirrors what pwsh and bash produce.
- **cmd.exe user-busy detection** — a side-channel polling loop (cmd worker only, Windows only) samples the cmd process's CPU time delta and child-process count every 500 ms. CPU > 50 ms / 500 ms catches builtins like `dir /s`; child detection via `CreateToolhelp32Snapshot` catches external commands like `notepad`, `git`, `xcopy`, `timeout /t`. Either signal flips the tracker to busy so `execute_command` auto-routes around the user. Suppressed during AI command execution.
- **Multi-shell E2E test suite** — `RunMultiShell` exercises pwsh / Windows PowerShell 5.1 / cmd / bash through the worker pipe protocol with shell-specific `ShellProfile`s. Covers ready/standby state, simple echo with input-echo strip assertion, session variable persistence, multi-line block syntax, and (bash) subshell capture + exit-code propagation. `RunIntegrationScriptGuardTest` verifies `integration.ps1` doesn't crash when PSReadLine is unloaded.
- **Additional E2E tests for pwsh** — session variable persistence across execute calls, multi-line foreach, slow-command timeout + busy-state probe, cached output retrieval after timeout, `send_input` rejection on idle consoles, `send_input` Ctrl+C interrupt with standby recovery.

### Changed
- **bash integration rewritten from DEBUG trap to PS0** — `PS0=$'\\e]633;C\\a'` fires OSC 633 C exactly once per command-line submit in the parent shell, working for subshells (`(echo foo)`), command substitutions (`$(date)`), pipelines, brace groups, and multi-statement lines. The old DEBUG trap approach couldn't fire for compound commands without `set -T`, and even with functrace had recursive emission issues inside `__rp_precmd`. The `__rp_in_command` flag and DEBUG trap are deleted entirely; `__rp_precmd` now emits OSC D unconditionally.
- **bash multi-line / multi-statement command capture** — multi-line bodies route through a tempfile `.sh` dot-source with WSL/MSYS path translation. Multi-statement single-line commands (`cmd1; cmd2; cmd3`) capture all output in order — previously only the last statement was reported because each sub-command's DEBUG firing reset the OSC C marker.
- **cmd.exe status line** — now renders as `○ Finished (exit code unavailable)` instead of a misleading `✓ Completed`. cmd's PROMPT can't expand `%ERRORLEVEL%` at display time, so the worker reports a fake exit 0 for every command; the new status text makes that limitation visible to the AI instead of silently lying.
- **Long status-line commands truncated** — the pipeline column in status lines now caps at 60 characters with `...` to keep multi-line responses readable.

### Fixed
- **cmd.exe AI commands hung forever** — cmd has no preexec hook to fire OSC 633 C, so the proxy tracker waited on a `_commandStart` that never advanced. The worker now calls `SkipCommandStartMarker()` after `RegisterCommand` for cmd, and cmd's PROMPT emits a fake `OSC 633 D;0` so the resolve path completes.
- **bash subshell commands hung forever** — `(echo foo)`, `(exit N)`, command substitutions all blocked the AI tracker indefinitely (and the multi-statement gate before that fix only captured the last sub-command's output). The PS0 rewrite resolves both issues.
- **bash multi-line newlines lost in PTY echo** — embedded `\n` in execute payloads got submitted as Enter and dropped subsequent lines into the continuation prompt. The tempfile-dot-source path preserves the body as a single source file.
- **pwsh integration crash when PSReadLine missing** — `Set-PSReadLineKeyHandler` and `Set-PSReadLineOption` calls are now guarded by `Get-Module PSReadLine` plus per-call `try/catch` so a screen-reader fallback or `Remove-Module` doesn't throw `CommandNotFoundException` at integration-load time.

### Known limitations
- **cmd.exe exit codes are always reported as 0**. cmd's PROMPT can't expand `%ERRORLEVEL%` at display time, so the worker emits a fake `OSC 633 D;0` after every command. AI commands show as `Finished (exit code unavailable)` to make the limitation visible. Use pwsh or bash if you need exit-code-aware execution. (The visible terminal still has the real `%ERRORLEVEL%`; only the AI-side capture is affected.)
- **`Remove-Module PSReadLine` mid-session breaks the pwsh worker.** PSReadLine spawns persistent reader threads that survive module unload (.NET can't fully unload binary modules), so the orphaned threads keep consuming console input bytes and the next AI command hangs forever. ripple can't recover from this state. Documented in README.
- **cmd.exe builtin interactive prompts are not detected as user-busy** (`pause`, `set /p`). Zero CPU + zero children leave both polling signals silent. Uncommon enough to leave undetected.

## [0.4.0] - 2026-04-12

**Visible-console rescue tools.** New `peek_console` (read-only snapshot of what a console is displaying) and `send_input` (raw keystrokes to a busy console's PTY) let the AI diagnose and respond to stuck commands without waiting for completion. `execute_command` timeouts now include a `partialOutput` snapshot for immediate diagnosis. Plus multi-line PowerShell support via tempfile dot-sourcing, and a 4 KB recent-output ring fed through a multi-row VT interpreter so the snapshot reads as the human sees the screen.

### Added
- **`peek_console` tool** — read-only snapshot of what a console is currently displaying. On Windows, reads the console screen buffer directly via `ReadConsoleOutputCharacterW` for an exact match with the visible terminal. On Linux/macOS, falls back to a built-in VT-medium terminal interpreter with fixed viewport, scrolling, alternate screen buffer, and save/restore cursor. Accepts a console selector (PID or display-name substring) or defaults to the active console.
- **`send_input` tool** — send raw keystrokes to a busy console's PTY input. Supports C-style escape sequences (`\r` for Enter, `\x03` for Ctrl+C, `\x1b[A` for arrow up, `\\` for literal backslash). Rejected when the console is idle (use `execute_command` instead). Console must be specified explicitly for safety. Max 256 chars per call.
- **`partialOutput` on execute timeout** — when `execute_command` times out, the response now includes a snapshot of what the console has been printing so far, so the AI can immediately diagnose stuck commands (watch mode, interactive prompts, stalled progress) without calling `wait_for_completion`.
- **Multi-line pwsh command support** — multi-line PowerShell commands (heredocs, foreach, try/catch, nested scriptblocks, comments) are handled via tempfile dot-sourcing. The command body is written to a temp `.ps1` file and dot-sourced, so session state (variables, functions) persists. A synthetic colorized echo replaces the dot-source line in the visible console, and PSReadLine history skips the internal tempfile path via `AddToHistoryHandler`.
- **Console selector for peek/send_input** — both tools accept a PID number or display-name substring (e.g. "Reggae" matches "#43060 Reggae"). Ambiguous matches are rejected. `peek_console` allows omitting the selector (defaults to active console); `send_input` requires it for safety.
- **Busy console workflow** — `execute_command` timeout responses now include a `partialOutput` snapshot for immediate diagnosis. From there the AI can `send_input` (respond or Ctrl+C), `wait_for_completion` (wait), or `peek_console` (get a fresher snapshot later).

### Changed
- **Recent-output ring buffer** — `CommandTracker` now maintains a 4 KB circular buffer fed from every PTY byte unconditionally (AI and user commands alike), with OSC C clearing to drop PSReadLine typing noise and claim-handshake clearing to drop prior-session residue.
- **VT-medium terminal interpreter** — the ring buffer snapshot is processed through a multi-row VT state machine handling CR/LF/BS/HT, CSI cursor positioning (CUU/CUD/CUF/CUB/CHA/CUP/HVP/VPA/CNL/CPL), EL/ED erasure, scroll regions (DECSTBM), alternate screen buffer (`\e[?1049h/l`), save/restore cursor (`\e7`/`\e8`, `\e[s`/`\e[u`), reverse index (`\eM`), SGR/OSC as no-ops, and DEC window manipulation (`\e[<params>t`) as a full-grid clear trigger. Fixed viewport with soft line wrap and vertical scrolling.
- **`_output` renamed to `_aiOutput`** to disambiguate from the new ring buffer. AI command result slicing via OSC C/D markers is unchanged.
- **Cache drain in `peek_console`** — every `peek_console` call now also drains cached outputs and detects closed consoles, matching `execute_command` and `wait_for_completion` behavior.

### Fixed
- **Dot-source line visible in console** — the `\e[<N>F\e[0J` erase sequence now dynamically calculates wrap row count based on terminal width, so the full dot-source input (which can exceed 200 chars and wrap to 2-3 rows) is erased completely.
- **Multi-line command cursor position** — the colorized echo is now emitted from inside the tempfile via `[Console]::OpenStandardOutput()`, bypassing pwsh's host TextWriter layer that was rewriting cursor-control escapes into absolute positioning. This keeps the child's virtual buffer cursor in sync with the visible terminal.
- **PSReadLine history pollution** — `.ripple-exec-*.ps1` dot-source lines are excluded from PSReadLine history via `AddToHistoryHandler` in `integration.ps1`.

### Known limitations
- `peek_console` on Linux/macOS uses the VT-medium interpreter which may not perfectly match the real terminal for complex TUI applications. Windows uses native screen buffer reads for exact fidelity.
- `send_input` escape sequences are interpreted by the worker; if the MCP client pre-processes backslashes (e.g. JSON `\r` → CR), the worker passes them through unchanged — both paths produce correct results.

## [0.3.0] - 2026-04-11

**Quality-focused polish on the v0.2.0 foundation.** pwsh is now stable and polished. bash / zsh / cmd are functional but lag on a few items. Drop-in upgrade from v0.2.0. Headline additions: PSReadLine-equivalent syntax-highlighted echo of AI commands, background busy / finished / closed reports on every tool response, source-cwd drift handling when auto-routing around a busy console, and a NativeAOT publish path that drops cold start from ~1 s to ~130 ms.

### Added
- **Syntax-highlighted AI command echo** — pwsh and Windows PowerShell 5.1 both render the echoed command with PSReadLine-equivalent colors: cmdlets, keywords (`foreach`, `in`, `if`, `else`, ...), scriptblock bodies (`Write-Host` inside `{ ... }`), double-quoted string interpolation (`"- $i"`), parameters, variables, numbers and comments. Hand-rolled state machine in `Services/PwshColorizer.cs` with unit tests.
- **Background busy / finished / closed reports** — every tool response now prepends a one-line summary of any other console's state, discovered on demand via a get_status pass. Includes a `✓ #N Name | Status: User command finished` line fired exactly once when a user-typed command like `pause` completes.
- **Source-cwd drift handling when auto-routing** — if the human user manually `cd`'d in the busy source console since your last command, ripple preserves your last known cwd by using it as the cd preamble target on the routed-to console and attaches a one-line `Note: source #N was moved by user to '...'; ran in #M at your last known cwd '...'` to the response. Source's `LastAiCwd` is intentionally not updated, so later returns to that console still prompt a verify-and-retry warning.
- **Same-console drift warning** — if the user manually `cd`'d in the *idle* active console, the next `execute_command` returns a "verify cwd and re-execute" warning instead of running in the wrong place.
- **`wait_for_completion` three-state contract** — distinguishes "no commands pending" (nothing to wait for, stop calling), "completed" (one or more drained results included), and "still busy" (call again to keep waiting).

### Changed
- **NativeAOT publish** — `ripple.exe` cold start dropped from ~1 s (R2R) to ~130 ms, eliminating the race between Claude Code's first MCP call and ripple warm-up.
- **Two concurrent owned pipe listeners** — a long-running `execute_command` no longer stalls `get_status` / `get_cached_output`; the second instance stays free for status queries.
- **500 ms fixed settle removed** — fast commands return without the old delay; trailing output is drained adaptively via the new `drain_post_output` pipe command.
- **Stream capture rewritten** as OSC C/D position slicing (`_commandStart` / `_commandEnd`), replacing the layered AcceptLine noise filters and first-newline heuristics.
- **start_console banner / reason survive ConPTY startup** — they used to flash for ~0.5 s before ConPTY's initial `\e[?25l\e[2J\e[m\e[H` wiped them. For pwsh / powershell.exe the banner is now emitted from inside the shell via the generated integration tempfile, so it sticks.
- **Unowned window title** changed from `#PID ____` to `#PID ~~~~` so ripple's idle state visually differentiates from PowerShell.MCP's identical `____`.
- **AI command echo blank line removed** — `cmdDisplay` no longer ends with `\r\n`, so it no longer doubles up with PSReadLine's AcceptLine newline.
- **Same-shell-family pinning when auto-switching** away from a busy console, so bash users don't get silently bounced into pwsh.
- **`powershell.exe` fallback** when `pwsh.exe` is absent on the host.
- **Tool descriptions refreshed** to reflect routing, cwd preservation, busy reports and `wait_for_completion` states — AI clients now see the new behavior in their tool list.

### Fixed
- **PSReadLine prediction and AcceptLine noise** leaking into pwsh command capture — pre-existing in v0.2.0, now cleanly avoided by moving OSC C emission to `PreCommandLookupAction` and slicing captured output between OSC C and OSC D.
- **First-OSC-B emission race** that stripped the first line of output on certain pwsh commands — the Enter handler now emits OSC B before delegating to `AcceptLine`.

### Known limitations (resolved in 0.4.0)
- ~~Multi-line commands break in ConPTY~~ — fixed in 0.4.0 via tempfile dot-sourcing.
- bash / zsh / cmd still use the pre-banner-fix `start_console` path, so banners flash briefly there. Colorization is pwsh-only.
- Routing / drift logic has no automated end-to-end test coverage yet.
- Worker re-claim across proxy restarts loses `LastAiCwd` / `LastAiCommand` state (expected).

## [0.2.0] - 2026-04-10

**Claim-handshake version check + npx install path.** A strictly newer proxy attaching to an older worker is refused; the old worker marks itself obsolete and stops serving pipes while keeping the PTY alive for the human user, so the MCP session disconnects cleanly without killing the shell.

### Added

- **Claim-handshake version check.** Old worker refuses newer proxy, marks itself obsolete, stops serving pipes, keeps the PTY alive for the human.
- **npx-based install docs.** README documents `npx splashshell` as the primary install path.

## [0.1.0] - 2026-04-10

**First published release**, rebranded from the internal `shellpilot` codename. ConPTY backend with OSC 633 shell integration, multi-shell support (bash / pwsh / powershell.exe / cmd.exe in parallel workers), console re-claim across proxy restarts, per-console cwd tracking, and the core MCP tool set (`start_console`, `execute_command`, `wait_for_completion`, plus file primitives).

### Added

- **ConPTY backend** with shell integration via OSC 633 sequences (PromptStart / CommandInputStart / CommandExecuted / CommandFinished / Cwd).
- **Multi-shell support** — bash, pwsh, powershell.exe, cmd.exe run simultaneously, each in its own worker process with its own Named Pipe.
- **Console re-claim** — worker survives proxy death; a new proxy can pick it up on the unowned pipe so the human never loses their shell when Claude Code restarts.
- **Per-console window titles** — `#PID Name` when owned, `#PID ____` when unowned.
- **Per-console cwd tracking** — `LastAiCwd`, auto cd on switch, detection of busy active console.
- **Banner and reason** display on `start_console` with format-aware layout.
- **MCP tools** — `start_console`, `execute_command`, `wait_for_completion`, plus file primitives (`read_file`, `write_file`, `edit_file`, `find_files`, `search_files`).
- **Cached output drain** on every MCP tool call so timed-out AI commands surface their result automatically.
- **Closed-console notifications** so the AI learns when a console has been closed since the last call.
- **User input forwarding** from the visible console to ConPTY, so the human can still type in the shared terminal (Ctrl+C, interactive prompts).
- **OSC 0 window title preservation** against shell overrides.
- **Shell type + cwd** in `start_console` response and status lines.
