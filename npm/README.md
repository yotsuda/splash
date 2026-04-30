# Ripple — REPL-sharing MCP for AI Co-Driving

<div align="center">
  <img src="https://github.com/user-attachments/assets/1343f694-1c05-4899-9faa-d2b1138aa3ba" alt="social-image" width="640" />
</div>

**A REPL-sharing MCP server for AI that actually holds a session.** Shell, Python, Node, a language debugger — whatever you'd open a REPL window for, ripple keeps it live between tool calls. Load `Import-Module Az` once and let AI run 50 follow-up cmdlets in milliseconds each. Watch every command happen in a real terminal window — the same one you can type into yourself.

## Install

No runtime prerequisite — ripple ships as a self-contained NativeAOT binary (~13 MB, Windows x64). `npx` fetches it on first run.

```bash
claude mcp add-json ripple -s user '{"command":"npx","args":["-y","@ytsuda/ripple@latest"]}'
```

<details>
<summary>Claude Desktop</summary>

Add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "ripple": {
      "command": "npx",
      "args": ["-y", "@ytsuda/ripple@latest"]
    }
  }
}
```

The `@latest` tag is important: without it, npx will happily keep reusing a stale cached copy even after a new version ships.

</details>

## Why ripple?

ripple gives AI a **stateful and visible** REPL session — the same window you can read along with and type into yourself. That combination unlocks workflows other MCP servers can't support: secrets the AI never sees, full PowerShell module ecosystems, real debugger sessions.

### Sensitive operations stay sensitive

When a command needs a passphrase, MFA code, or other secret — `Read-Host -AsSecureString`, `gpg --sign`, `ssh-add`, `sudo`, cloud CLI MFA prompts — you type it directly into the visible window. The keystrokes go to the running program, not to the terminal output stream the AI sees. The AI orchestrates the workflow ("run the publish build, sign with my key, then push the tag") but never sees the secret itself.

This is impossible with stdin-piped MCP shells, where the AI must somehow supply the secret. Interactive build pipelines that involve code-signing keys, hardware token PINs, or two-factor codes work naturally on ripple — the human stays in the loop only for the moment that requires them, and the AI handles everything around it.

### PowerShell becomes a first-class AI environment

Session persistence helps every shell, but for **PowerShell it's transformative**. Most MCP shell servers spin up a fresh subshell per command — which makes real PowerShell workflows impractical:

- **10,000+ modules on [PowerShell Gallery](https://www.powershellgallery.com/).** Az (Azure), AWS.Tools, Microsoft.Graph (Entra ID / M365), ExchangeOnlineManagement, PnP.PowerShell, SqlServer, ActiveDirectory — plus every CLI in PATH (git, docker, kubectl, terraform, gh, az, aws, gcloud) and full access to .NET types.
- **30–70 second cold imports, paid once.** `Import-Module Az.Compute, Az.Storage, Az.Network` can take over a minute on the first call. A subshell-per-command MCP server pays that cost on *every* command and the AI gives up on Azure workflows entirely. With ripple, the AI imports once and every subsequent cmdlet runs in milliseconds.
- **Live .NET object graphs.** PowerShell pipes rich objects, not text. After `$vms = Get-AzVM -Status`, the AI can chain arbitrary follow-ups against the live object — filter, group, drill into nested properties — without re-hitting Azure. In a one-shot MCP server, that object vanishes the moment the command returns.
- **Interactive build-up of complex work.** Set a variable, inspect it, reshape it, feed it back into the next cmdlet. Build a multi-step workflow one command at a time with every previous step's result still in scope.

```powershell
# Command 1 — cold import, paid once for the whole session
Import-Module Az.Compute, Az.Storage

# Command 2 — instant; capture into a variable
$vms = Get-AzVM -Status

# Command 3 — instant; same session, $vms still in scope
$vms | Where-Object PowerState -eq "VM running" |
    Group-Object Location | Sort-Object Count -Descending
```

PowerShell on ripple is the difference between **"AI can answer one-off questions"** and **"AI can do real infrastructure work."** bash and cmd are fully supported too, but pwsh is where ripple shines.

### Full transparency, in both directions

ripple opens a **real, visible terminal window**. You see every AI command as it runs — same characters, same output, same prompt — and you can type into the same window yourself at any time. When a command hangs on an interactive prompt, stalls in watch mode, or just needs a Ctrl+C, the AI can read what's currently on the screen and send keystrokes (Enter, y/n, arrow keys, Ctrl+C) back to the running command — diagnosing and responding without human intervention.

### 19 adapters: shells, language REPLs, and debuggers

ripple ships **19 adapters** — 4 shells, 12 language REPLs, 3 debuggers — each with the same persistence, OSC 633 lifecycle tracking, and shared-terminal transparency:

- **Shells (4)**: pwsh / powershell, bash, zsh, cmd
- **REPLs (12)**: Python, Node.js, Deno (TypeScript), Lua, Racket, CCL / ABCL / SBCL (Common Lisp), jshell (Java), groovysh (Groovy), F# Interactive, SQLite3
- **Debuggers (3)**: pdb (Python), perldb (Perl), jdb (Java) — driven via a unified `commands.debugger` vocabulary (step_in / step_over / continue / print / backtrace / breakpoint_set / ...) so AI agents drive any debugger with the same operation names regardless of underlying syntax

Tell the AI to drive a `groovysh` REPL for Spring Boot exploration, a `sqlite3` session for ad-hoc data shaping, or a `perldb` breakpoint loop to chase a bug in a live Perl program — all via the same `execute_command`.

## Tools

| Tool | Description |
|------|-------------|
| `list_shells` | Enumerate every adapter this ripple build accepts as the `shell` argument — shells, REPLs, and debuggers — with their resolved executable paths and any startup load issues. Use before `start_console` to see what's available, or to diagnose an unrecognized shell name. |
| `start_console` | Open a visible terminal. Pick a shell (bash, pwsh, powershell, cmd) or any registered REPL / debugger adapter. Reuses an existing standby of the same adapter unless `reason` is provided. |
| `execute_command` | Run a pipeline. Optionally target a specific `shell`. Times out cleanly with output cached for `wait_for_completion`; timeout responses include a `partialOutput` snapshot for immediate diagnosis. |
| `wait_for_completion` | Block until busy consoles finish and retrieve cached output. |
| `peek_console` | Read-only snapshot of what a console is displaying. Windows reads the screen buffer directly; Linux/macOS uses a VT interpreter. Reports busy/idle state, running command, and elapsed time. |
| `send_input` | Send raw keystrokes to a **busy** console's PTY input. `\r` for Enter, `\x03` for Ctrl+C, `\x1b[A` for arrow up, etc. Max 256 chars. |

Plus Claude Code–compatible file primitives: `read_file`, `write_file`, `edit_file`, `search_files`, `find_files`.

Status lines include console name, shell family, exit code, duration, and cwd:

```
✓ #12345 Sapphire (bash) | Status: Completed | Pipeline: ls /tmp | Duration: 0.6s | Location: /tmp
```

## Reliability features

- **Auto-routing when busy**: each console tracks its own cwd; when the active one is busy, the AI is routed to a sibling at the same cwd automatically
- **Console re-claim**: consoles outlive their parent MCP process — AI client restarts don't kill loaded modules or variables
- **Cwd drift detection**: a manual `cd` in the terminal triggers a verification warning before the AI runs in the wrong place
- **Sub-agent isolation**: parallel AI agents get their own consoles, no cross-contamination
- **Multi-line PowerShell**: heredocs, `foreach`, `try`/`catch`, nested scriptblocks all work via tempfile dot-sourcing

> **Architecture diagram, full routing matrix, and source**: see [yotsuda/ripple](https://github.com/yotsuda/ripple#readme).

## Platform support

**Windows** is the primary target (ConPTY + Named Pipe, fully tested). Unix PTY fallback for Linux/macOS is experimental.

## Known limitations

- **cmd.exe exit codes always read as 0** — cmd's `PROMPT` can't expand `%ERRORLEVEL%` at display time, so AI commands show as `Finished (exit code unavailable)`. Use `pwsh` or `bash` for exit-code-aware work.
- **Don't `Remove-Module PSReadLine -Force` inside a pwsh session** — PSReadLine's background reader threads survive module unload and steal console input, hanging the next AI command. Not recoverable.

## Migration

Renamed from `splash` at v0.8.0. Shipped as `splashshell` for v0.1.0–v0.5.0, then `@ytsuda/splash` for v0.7.0, and is now `@ytsuda/ripple` starting with v0.8.0. Both `@ytsuda/splash` and `splashshell` are deprecated; uninstall them and install `@ytsuda/ripple` to keep receiving updates.

## License

MIT. Full release notes and source at [yotsuda/ripple](https://github.com/yotsuda/ripple).
