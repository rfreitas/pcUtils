# ClaudeFleet.NET — tray view of running Claude Code sessions, agents and processes

- **Status:** approved, mock UI in progress (real data not yet implemented)
- **Date:** 2026-10-16
- **Touches:** new `ClaudeFleet.NET/` + `ClaudeFleet.NET.Tests/`, `AutoHotkey.sln`, `.gitignore`.
  Links `Shared.NET` (SingleInstanceGuard, TrayMenu, DarkMenuRenderer, StartupTaskService,
  TrayFlyoutWindow, TrayPopupPlacement). Follows `Shared.NET/TRAY_APP_UX.md`.

## Problem

Several Claude Code sessions run at once (terminals, worktrees, headless `claude -p` runs started by
other sessions), each spawning subagents and long shell commands (cargo builds, test runs, viewers).
There is no single place to see which sessions are busy, which agents they are running, what
processes those agents started and how much CPU all of it uses. Claude Code has no documented API
for this; `ListAgents` shows peers but only from inside a session.

## Measured findings (2026-10-16, Claude Code 2.1.294–2.1.301)

### Live session registry — `%USERPROFILE%\.claude\sessions\<pid>.json`

One file per running session process, rewritten on status change:

```json
{"pid":49752,"sessionId":"b77a76cc-…","cwd":"C:\\Users\\ricfr\\Repos","startedAt":1792176972707,
 "procStart":"134366505681072514","version":"2.1.301","kind":"interactive","entrypoint":"cli",
 "messagingSocketPath":"\\\\.\\pipe\\LOCAL\\cc-msg-…","name":"repos-5e","nameSource":"derived",
 "status":"busy","updatedAt":…,"statusUpdatedAt":…}
```

| Observation | Value |
| --- | --- |
| Files vs live PIDs | 8 files, 8 live `claude.exe` PIDs; matches `ListAgents` (7 peers + self) |
| `status` values seen | `busy`, `idle` |
| `procStart` | FILETIME of process start — use with PID to reject a reused PID |
| `<pid>.<hash>.key` files | secrets for the messaging pipe — **never read** |
| Headless runs | `claude -p` children register their own file (e.g. `agent-a81aa9b9ea982d6dc-08`, `eval-baseline-e3`); parent PID chain leads to the session that started them |

### Subagents — `%USERPROFILE%\.claude\projects\<cwd-slug>\<sessionId>\subagents\`

- `agent-<id>.meta.json`: `{"agentType":"general-purpose","description":"…","toolUseId":"toolu_…",
  "spawnDepth":1,"requestShape":"background","requestNonInteractive":true,"model":"opus"}`
- `agent-<id>.jsonl`: transcript; every line has `sessionId`, `agentId`, `timestamp`, `cwd`.
- No explicit running/finished flag. Finished = parent `<sessionId>.jsonl` holds a `tool_result` for
  `toolUseId` (foreground) or a task notification for the agent (background). Fallback: transcript
  mtime older than N s.
- `cwd-slug` = cwd with `:` `\` `/` `.` replaced by `-` (e.g. `C--Users-ricfr-Repos-boardvr-bevy--claude-worktrees-board-yaw-view`).

### Process trees (Win32_Process dump of the same moment)

- Every shell command is a child of the session's `claude.exe`: `cmd /c chcp 65001 & pwsh … -Command "$__claud…"`
  or `bash.exe -c "source …\shell-snapshots\snapshot-bash-…"`, then the real program (cargo → rustc ×5, python, …).
- Every session has an MCP child (`cmd /d /c whiteboard mcp` → `Whiteboard.exe`); some add more (`bevy_brp_mcp.exe`).
- In-process subagents (Agent tool) have **no process of their own**; their commands are children of
  the session PID, indistinguishable by tree alone.

## Design

### Data sources (read-only; the app never writes under `~/.claude`)

1. **Sessions:** `FileSystemWatcher` on `sessions\*.json`. Liveness by PID + `procStart`. Exit noticed
   without polling: `OpenProcess(SYNCHRONIZE)` + `RegisterWaitForSingleObject` per session.
2. **Agents:** read `subagents\*.meta.json` + tail of transcripts **only while the flyout is open**.
   Running/finished as above; finished agents hidden after 10 min.
3. **Processes + CPU:** one `NtQuerySystemInformation(SystemProcessInformation)` snapshot (PID, PPID,
   name, kernel+user time, working set) — ~1 ms, the Task Manager source. Command lines (needed for
   labels) via `NtQueryInformationProcess(ProcessCommandLineInformation)` only for PIDs inside Claude
   trees, cached per PID+start time. Not WMI — too heavy for a loop.
4. **Process → agent attribution:** headless sessions are exact (own registry file). In-process
   subagents: best-effort match of the child's command line against `Bash`/`PowerShell` tool_use
   commands in each subagent transcript; otherwise "session" (unattributed).

### Cost model (agreed: no cost when nothing to show)

| State | Work done |
| --- | --- |
| All sessions idle, aggregate CPU < 1 % for 2 samples | Nothing. Watcher + exit waits only (kernel-signalled, no timers). |
| Any session busy, or Claude trees still using CPU | Snapshot every 5 s → tray meter + tooltip. ~1 ms per sample. |
| Flyout open | Snapshot every 2 s, agent folders + transcripts read, full tree. Stops on hide. |

Rejected: Job Objects for exact aggregate CPU (must inject the app into Claude's processes, misses
pre-existing children); WMI polling (cost); hover popups (UX guide §1).

### Tray icon

16×16 (scaled for DPI), drawn at runtime: small Claude-orange spark on the left, vertical 5-step
meter on the right (grey idle → accent → amber → red by aggregate CPU of all Claude trees, % of the
whole machine). Redrawn only when the step changes. Tooltip (≤127 chars):
`Claude: 42% CPU · 3 busy / 8 · 2 agents · top boardvr-bevy-f8`.

### Flyout (left-click)

Fixed width (no reflow when values change), max height then scroll. One line per item:

```
Claude · 8 sessions · 3 busy                                     ▮▮▮▯▯
▾ ● boardvr-bevy-f8      boardvr-bevy                 40m   ▮▮▮▯▯
     ▶ Explore   find grip clamp refs        sonnet    2m
     ✓ general   review drive_arms           opus      done
     ⚙ cargo build --release --bin fixture_viewer       ▮▮▮▮▯
        rustc ×5
     ◆ agent-a81a…-08  (headless, sonnet)                ▮▯▯▯▯
        cargo run fixture_viewer · python scan.py
▸ ○ repos-5e             Repos                         2m   ▯▯▯▯▯
```

- Busy/idle: glyph **and** colour (● amber busy, ○ grey idle). Agent ▶ running / ✓ done.
- CPU shown as a fixed-width bar (colour/length only) — no per-second changing numbers in rows
  (UX guide §7). Exact % and memory in the row's tooltip.
- Rows that disappear linger 2 s greyed before removal; new rows append within their session.
- Busy sessions expanded by default; click ▸/▾ to toggle. MCP server processes hidden by default.
- Row right-click: Focus terminal · Copy `claude --resume <id>` · Open folder · Open transcript ·
  Kill process tree… (confirm).
- Footer legend: ● busy ○ idle ▶ running ✓ done ◆ headless ⚙ process.

### Menu (right-click)

Header → "Notify when a session finishes" ✓ · "Show MCP servers" → Start at Login → Exit.

### Toasts

Busy → idle on a session ("boardvr-bevy-f8 finished"), background agent done. Default on.

## Implementation plan

1. **Mock UI (this step):** project + tests + sln + gitignore; `Model.cs` records; `MockFleet.cs`
   sample data from the 2026-10-16 dump with animated CPU; `FleetPopup` flyout; `TrayIconRenderer`;
   `TrayApp`; `--mock` (live tray on mock data), `--render out.png`, `--render-icon out.png`,
   `--selftest`, `--placement`. Tests: formatting, icon steps, popup does not move on CPU update.
2. `SessionRegistry` (watcher + PID/procStart liveness + exit waits) → real sessions in the flyout.
3. `ProcessSnapshot` (NtQuerySystemInformation) + `CpuSampler` (delta per PID, tree sums) + tray meter.
4. `AgentReader` (meta + running state from parent transcript) + attribution.
5. Row actions, toasts, settings (`%LOCALAPPDATA%\ClaudeFleet\ClaudeFleet.ini`).

## What does not change

- Nothing under `~/.claude` is written; no hooks installed; no use of the messaging pipes or `.key` files.
- Shared.NET is linked, not modified.
- Cloud/remote sessions are out of scope.
