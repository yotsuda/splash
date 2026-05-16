# ripple shell integration for PowerShell (pwsh.exe / powershell.exe)
# Injects OSC 633 escape sequences for command lifecycle tracking.
# Uses [char] escapes for compatibility with Windows PowerShell 5.1.
#
# Event sequence (for both AI-initiated and user-typed commands):
#   OSC B (Enter pressed) → PSReadLine AcceptLine finalize →
#   OSC C (command about to execute) → command output →
#   OSC D;{exitCode} (command finished) → OSC P;Cwd=... → OSC A (prompt ready) →
#   prompt text rendered → PSReadLine input loop (prediction, etc.)
#
# ripple captures just the region between OSC C and OSC D as the command
# output. Bytes outside that window (typing animation, AcceptLine finalize,
# prompt repaint, PSReadLine prediction) are ignored, so no heuristic
# "strip up to first newline" logic is needed.

if ($global:__RippleInjected) { return }
$global:__RippleInjected = $true

$global:__rp_ESC = [char]0x1B
$global:__rp_BEL = [char]7

function global:__rp_osc_str([string]$code) {
    return "$($global:__rp_ESC)]633;$code$($global:__rp_BEL)"
}

# Override prompt function — emits OSC D (command finished with exit code),
# OSC P (cwd), OSC A (prompt ready) so ripple can detect the end of the
# command's output and drain its capture window. OSC C is NOT emitted here;
# it now lives on PreCommandLookupAction below so the "execution started"
# marker fires BEFORE the command writes anything to the console.
$global:__rp_original_prompt = $function:prompt
$global:__rp_last_history_id = 0
# Snapshot of $LASTEXITCODE at the moment PreCommandLookupAction fires
# (i.e. just before the user / AI pipeline actually runs). The prompt fn
# compares the post-command value against this to decide whether
# $LASTEXITCODE reflects THIS pipeline's native exit or is residue from
# a prior native invocation. Without this, a pure-PowerShell pipeline
# (no native exe) would inherit whatever $LASTEXITCODE happened to be
# left from an earlier `cmd /c "exit 7"` and ripple would report the
# innocent pipeline as Failed (exit 7) — observed and reproduced.
$global:__rp_lec_at_cmd_start = $null
# Multi-line AI pipelines run inside a dot-sourced tempfile wrapped by
# `Import-Module PSReadLine; . 'tempfile'; Remove-Item '...'`. The prompt
# fn's naive $? would reflect Remove-Item (always true), not the user
# pipeline inside the tempfile. ConsoleWorker.BuildMultiLineTempfileBody
# now stashes the tempfile's own $? / $LASTEXITCODE into these globals
# as its final two statements; the prompt fn reads them with priority,
# then clears, so subsequent single-line commands fall through to plain
# $?-based detection.
$global:__rp_ai_pipeline_ok = $null
$global:__rp_ai_pipeline_lec = $null
# Snapshot of $Error.Count at PreCommandLookupAction. The prompt fn
# computes ($Error.Count - this) to report the number of error records
# the pipeline added — written to OSC 633;E;{N} so the proxy can
# surface it as `Errors: N` in the status line. Errors are PowerShell's
# canonical "something failed" signal: cmdlet non-terminating errors,
# `Write-Error`, thrown exceptions all populate $Error. Native exe
# non-zero exits do NOT — those are covered by OSC D's $? path. Warning
# / Information streams don't have an analogous reliable counter (no
# global `$Warning.Count`; cmdlets emit warnings via the engine pipe,
# bypassing any Write-Warning proxy), so only errors are counted.
$global:__rp_err_count_at_cmd_start = 0

# Heuristic: did the last pipeline actually run a native exe? Used by
# the resolver to suppress phantom `LastExit: N` reports from manual
# `$LASTEXITCODE = N` assignments in pure-PowerShell pipelines (which
# updated the variable without any process actually exiting). Walks
# the command line's AST, looks for CommandAst nodes, and resolves each
# command name through the session's command discovery filtered to
# Application — only native exes return a hit. Aliases pointing at
# natives resolve as Alias here, so e.g. an alias to `git` would NOT
# flip the flag; that's the conservative direction (fewer false
# positives), matching the spirit of the fix. Functions that internally
# invoke a native are also missed for the same reason — accepted as a
# known limitation of static analysis.
#
# Done in the prompt fn (not in PreCommandLookupAction) to avoid any
# recursion question: the parser + GetCommand calls are pure helpers
# at this point and don't trigger further command lookups that the
# action would observe.
function global:__rp_pipeline_uses_native([string]$commandLine) {
    if ([string]::IsNullOrWhiteSpace($commandLine)) { return $false }
    try {
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput(
            $commandLine, [ref]$tokens, [ref]$errors)
        if ($null -eq $ast) { return $false }
        $cmds = $ast.FindAll(
            { param($n) $n -is [System.Management.Automation.Language.CommandAst] },
            $true)
        foreach ($c in $cmds) {
            $name = $c.GetCommandName()
            if ([string]::IsNullOrEmpty($name)) { continue }
            try {
                $resolved = $ExecutionContext.SessionState.InvokeCommand.GetCommand(
                    $name, [System.Management.Automation.CommandTypes]::Application)
                if ($resolved) { return $true }
            } catch { }
        }
    } catch { }
    return $false
}

# Resolve the OSC D exit code and the OSC L payload in one pass, from
# the five signals the prompt fn has in hand at command finish:
#
#   $ok         — $? captured on the prompt fn's very first line
#                 (single-line AI commands + user-typed commands).
#   $lec        — $global:LASTEXITCODE at prompt fn time.
#   $lecAtStart — snapshot from PreCommandLookupAction (before the
#                 pipeline ran). Comparing the two tells us whether
#                 $LASTEXITCODE was UPDATED BY this pipeline or is
#                 stale from an earlier native exe.
#   $aiOk       — $? stashed INSIDE a multi-line AI tempfile (set by
#                 BuildMultiLineTempfileBody). $null for single-line
#                 AI commands and all user-typed commands, because
#                 only the multi-line wrapper needs the stash (its
#                 outer `Remove-Item` would otherwise clobber $? before
#                 the prompt fn ran).
#   $aiLec      — matching $LASTEXITCODE stash for the multi-line
#                 tempfile.
#
# Returns a hashtable with:
#   ExitCode       — value for OSC D
#   LastExitReport — value for OSC L; 0 when L should be silent
#                    (either the pipeline failed — D already carries
#                    the non-zero value — or no native ran, or the
#                    native returned 0).
#
# Collapsing both outputs into one function removes the duplicate
# branching the prompt fn used to have (the $overallOk / $lecChanged
# calc for L was a second pass over the same signals the D calc had
# already considered) and gives the resolver a single obvious place
# to evolve.
function global:__rp_resolve_exit_code([bool]$ok, $lec, $lecAtStart, $aiOk, $aiLec, [bool]$nativeSeen) {
    $lecChanged = $lec -ne $lecAtStart
    # Treat $LASTEXITCODE as authoritative only when (a) it changed this
    # pipeline AND (b) AST analysis saw at least one native invocation.
    # Without the native gate, a bare `$LASTEXITCODE = 7` (a plain
    # variable assignment with no process exit behind it) was
    # indistinguishable from `cmd /c exit 7`, so the manual assignment
    # leaked into the status line as `LastExit: 7` and into the D code
    # as exit 7.
    $lecAuthoritative = $lecChanged -and $nativeSeen -and $null -ne $lec -and $lec -ne 0
    if ($null -ne $aiOk) {
        # Multi-line AI path: the tempfile stash captured the user's
        # body outcome before the outer `. 'tmp'; Remove-Item '...'`
        # wrapper reset $?. Use $aiOk / $aiLec verbatim. AST gating
        # also applies here.
        $ec = if ($aiOk) { 0 }
              elseif ($nativeSeen -and $null -ne $aiLec -and $aiLec -ne 0) { $aiLec }
              else { 1 }
        $overallOk = $aiOk
    } else {
        # Single-line + user-typed path: $? and $LASTEXITCODE are
        # authoritative. $LASTEXITCODE only consulted when $? is false
        # AND the value was written by this pipeline AND a native
        # actually ran (otherwise a stale value from an earlier
        # `cmd /c exit 7`, or a manual `$LASTEXITCODE = 7` assignment
        # in pure-PS code, would both leak through).
        $ec = if ($ok) { 0 }
              elseif ($lecAuthoritative) { $lec }
              else { 1 }
        $overallOk = $ok
    }

    # OSC L is emitted ONLY when the pipeline overall succeeded (so D
    # is 0) AND a native exe returned non-zero mid-pipeline. In every
    # other case either D already carries the non-zero exit (pipeline
    # failed) or there is nothing to report (no native ran, no native
    # returned non-zero, or no LEC delta). Encoding "do not emit" as 0
    # lets the prompt fn call site use a plain `-gt 0` gate with no
    # extra null checks.
    $emitL = $overallOk -and $lecAuthoritative
    $lastExitReport = if ($emitL) { $lec } else { 0 }

    return @{
        ExitCode       = $ec
        LastExitReport = $lastExitReport
    }
}

function global:prompt {
    # CRITICAL: $? must be captured on the very first line — any statement
    # below (including simple assignments) can reset it. $? is the
    # canonical "did the last pipeline succeed" indicator in PowerShell:
    # true for successful cmdlets / successful natives (exit 0) /
    # successful statements, false for cmdlet errors / native non-zero
    # exit / thrown exceptions. Using $? as the prime signal — with
    # $LASTEXITCODE only consulted when $? is false AND the value was
    # updated by this pipeline — eliminates the "stale $LASTEXITCODE
    # from a prior native bleeds into every subsequent innocent
    # pipeline" bug.
    $ok = $?
    $lec = $global:LASTEXITCODE
    $lecAtStart = $global:__rp_lec_at_cmd_start
    $aiOk = $global:__rp_ai_pipeline_ok
    $aiLec = $global:__rp_ai_pipeline_lec

    # Consume the multi-line AI stash immediately so the next command
    # (which may be user-typed or single-line AI) sees clean slots.
    $global:__rp_ai_pipeline_ok = $null
    $global:__rp_ai_pipeline_lec = $null

    $prefix = ""

    # Detect if a command was executed since last prompt
    $lastCmd = Get-History -Count 1 -ErrorAction SilentlyContinue
    if ($lastCmd -and $lastCmd.Id -ne $global:__rp_last_history_id) {
        $global:__rp_last_history_id = $lastCmd.Id

        # CommandFinished with exit code. OSC C is emitted from
        # PreCommandLookupAction before the command runs, so by the time
        # we're in the prompt function it has already fired.
        # Exit-code + OSC L payload are resolved in one pass by
        # __rp_resolve_exit_code so D and L never disagree on what
        # "this pipeline succeeded" means (see that function for the
        # resolution rules).
        # AST scan of the command line that just finished, used to gate
        # $LASTEXITCODE-based reporting on "a native exe actually ran".
        # Done here (not in PreCommandLookupAction) so the GetCommand
        # call inside the helper can never recursively trigger the
        # action mid-resolution.
        $nativeSeen = __rp_pipeline_uses_native $lastCmd.CommandLine
        $__rp_res = __rp_resolve_exit_code $ok $lec $lecAtStart $aiOk $aiLec $nativeSeen
        $prefix += (__rp_osc_str "D;$($__rp_res.ExitCode)")

        # Errors-this-pipeline count via $Error.Count delta. Floor at 0
        # so a user `$Error.Clear()` mid-command can't produce a negative
        # delta that breaks the int parser on the proxy side. The proxy
        # surfaces this as `Errors: N` in the status line when N > 0.
        $errDelta = $Error.Count - $global:__rp_err_count_at_cmd_start
        if ($errDelta -lt 0) { $errDelta = 0 }
        $prefix += (__rp_osc_str "E;$errDelta")

        # OSC R: structured error messages. Emit each new $Error entry
        # as a base64-encoded UTF-8 string so the proxy can surface a
        # clean `--- errors ---` list alongside (or instead of, when the
        # caller passes strip_ansi=true) the inline SGR-coloured error
        # text. $Error[0] is most recent; $Error[$errDelta-1] is the
        # earliest one this pipeline added.
        #
        # Each message is prefixed with the raising cmdlet's name (from
        # $err.InvocationInfo.InvocationName), matching the "Get-Item:
        # ..." format PowerShell's ConciseView uses for inline errors.
        # Naked `throw "..."` statements have no InvocationName, so the
        # prefix is omitted in that case — the message text stands alone,
        # same as the inline render.
        #
        # Cap kept tight so a command that fills $Error with a thousand
        # records (pathological loops) can't stuff megabytes into the
        # OSC stream. When capped, we emit the OLDEST $emitLimit entries
        # (root cause side) and drop the newer cascade errors; the
        # truncation marker makes that ordering explicit so the AI does
        # not misread which records are missing. Per-entry base64
        # encoding tolerates newlines and non-ASCII without needing
        # payload escaping.
        if ($errDelta -gt 0) {
            $__rp_emitLimit = [Math]::Min($errDelta, 20)
            $__rp_firstIdx = $errDelta - 1                   # oldest new error ($Error[N] = older)
            $__rp_lastIdx  = $errDelta - $__rp_emitLimit     # stop here (inclusive)
            for ($__rp_i = $__rp_firstIdx; $__rp_i -ge $__rp_lastIdx; $__rp_i--) {
                try {
                    $__rp_err = $Error[$__rp_i]
                    $__rp_name = if ($__rp_err.InvocationInfo -and
                                     $__rp_err.InvocationInfo.InvocationName) {
                        "$($__rp_err.InvocationInfo.InvocationName): "
                    } else { "" }
                    $__rp_msg = $__rp_name + "$__rp_err"
                    if ($__rp_msg.Length -gt 1000) {
                        $__rp_msg = $__rp_msg.Substring(0, 997) + '...'
                    }
                    $__rp_b64 = [Convert]::ToBase64String(
                        [Text.Encoding]::UTF8.GetBytes($__rp_msg))
                    $prefix += (__rp_osc_str "R;$__rp_b64")
                } catch {
                    # Malformed error record — skip rather than aborting
                    # the OSC stream. The E count has already given the
                    # proxy a reliable indicator; losing one R payload
                    # only loses the text of this record, not the count.
                }
            }
            if ($errDelta -gt $__rp_emitLimit) {
                # OSC T: how many newer error records were dropped from
                # the cap. Distinct from R so the proxy can render this
                # as list metadata (header "20 of 25" + trailing "5
                # truncated" note) instead of forcing the marker into
                # the numbered entry list as if it were error #21.
                $prefix += (__rp_osc_str "T;$($errDelta - $__rp_emitLimit)")
            }
        }

        # OSC L: __rp_resolve_exit_code encoded "do not emit" as 0, so
        # the prompt fn just gates on > 0 — all the pipeline-overall-ok
        # / lecChanged / native-actually-non-zero logic lives in the
        # resolver with the D decision it is paired with.
        if ($__rp_res.LastExitReport -gt 0) {
            $prefix += (__rp_osc_str "L;$($__rp_res.LastExitReport)")
        }
    }

    # Clear the pre-command snapshot so the next command starts fresh.
    # PreCommandLookupAction sets it again when the next pipeline begins.
    $global:__rp_lec_at_cmd_start = $null

    # Report cwd
    $prefix += (__rp_osc_str "P;Cwd=$($PWD.Path)")

    # PromptStart (A) — triggers command completion detection
    $prefix += (__rp_osc_str "A")

    # Call original prompt
    $originalOutput = if ($global:__rp_original_prompt) {
        & $global:__rp_original_prompt
    } else {
        "PS $($PWD.Path)> "
    }

    # Return: OSC prefix + original prompt text
    return $prefix + $originalOutput
}

# PreCommandLookupAction fires inside the PowerShell engine right before it
# resolves a command name to an invocation target — i.e. AFTER PSReadLine
# AcceptLine has finalized the input line and BEFORE the command produces
# any output. Emit OSC C here so ripple knows that whatever follows is real
# command output, not PSReadLine rendering. The action fires once per
# command lookup, including nested lookups inside a pipeline, so a flag set
# by the Enter key handler ensures we emit exactly once per user command.
$global:__rp_pending_user_command = $false

$ExecutionContext.InvokeCommand.PreCommandLookupAction = {
    param($commandName, $eventArgs)
    if ($global:__rp_pending_user_command) {
        $global:__rp_pending_user_command = $false
        # Snapshot $LASTEXITCODE so the prompt fn can distinguish "this
        # pipeline ran a native exe and that's where the value came from"
        # from "this pipeline was pure PowerShell and $LASTEXITCODE is
        # residue from an earlier native run". Without the snapshot, a
        # cmd.exe exit 7 followed by a pure-PS pipeline would be reported
        # as exit 7.
        $global:__rp_lec_at_cmd_start = $global:LASTEXITCODE
        # Same idea for $Error.Count: snapshot here so the prompt fn can
        # report the delta as the number of error records this pipeline
        # added. $Error is per-runspace and persists across commands; only
        # the delta is meaningful as "this pipeline's errors".
        $global:__rp_err_count_at_cmd_start = $Error.Count
        [Console]::Write((__rp_osc_str "C"))
    }
}

# Emit initial CommandInputStart marker via Write-Host (goes to console screen
# buffer). ripple uses this to mark the shell as "ready" (first PromptStart
# hasn't fired yet at integration-load time, but the subsequent prompt render
# will).
Write-Host -NoNewline (__rp_osc_str "B")

# PSReadLine integrations — best-effort. PSReadLine is normally loaded in
# interactive pwsh (ripple's launch line also calls Import-Module on it),
# but a screen-reader fallback or an explicit Remove-Module would leave the
# cmdlets undefined. Guard with Get-Module so the integration script stops
# cleanly instead of throwing CommandNotFoundException, which would crash
# the worker mid-startup. Without these handlers the AI command tracker
# stops getting OSC B (user-busy detection) and the tempfile dot-source
# lines leak into history — both observability concerns, not correctness.
if (Get-Module PSReadLine) {
    # Disable inline prediction (Predictive IntelliSense) in the worker
    # pwsh. The worker is an automation shell: ripple writes AI command
    # payloads into it as if typed, so PSReadLine's predictor renders
    # history-based ghost text (default SGR 38;2;68;68;68) over the line
    # while a long encoded_scriptblock payload streams in. That redraw
    # interleaves with the input echo and the osc_boundaries stripper
    # can't cleanly separate it, so the captured command output gets
    # corrupted — intermittently, depending on the user's history and
    # timing (ripple#9: flaky pwsh adapter tests on a box with populated
    # history; never reproduces with empty history). No human reads the
    # prediction in an AI-driven worker, so this loses nothing here.
    # PredictionSource is the ONLY thing turned off — history recall
    # (Up/Down, Ctrl-R), Tab completion, and syntax highlighting all
    # keep working — and it touches only ripple's worker shell, never
    # the user's normal pwsh session.
    try { Set-PSReadLineOption -PredictionSource None } catch { }

    # Override Enter to emit OSC B and arm the "next command lookup is a
    # user command" flag. The actual OSC C fires from PreCommandLookupAction
    # a moment later, right before the command runs — by then PSReadLine's
    # AcceptLine has finalized the visible line and we're out of the input-
    # rendering noise.
    try {
        Set-PSReadLineKeyHandler -Key Enter -ScriptBlock {
            Write-Host -NoNewline (__rp_osc_str "B")
            $global:__rp_pending_user_command = $true
            [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
        }
    } catch { }

    # Skip the internal `. 'C:\...\.ripple-exec-*.ps1'; Remove-Item ...`
    # dot-source lines that ripple uses to run multi-line AI commands.
    # Those are an implementation detail — the user pressing Up to recall
    # history wants to see the real previous commands they typed, not the
    # transient tempfile path, and the scrollback already shows the
    # colorized echo of the actual command body via the tempfile's own
    # output.
    try {
        Set-PSReadLineOption -AddToHistoryHandler {
            param([string]$line)
            if ($line -match "\.ripple-exec-.*\.ps1") {
                return [Microsoft.PowerShell.AddToHistoryOption]::SkipAdding
            }
            return [Microsoft.PowerShell.AddToHistoryOption]::MemoryAndFile
        }
    } catch { }
}
