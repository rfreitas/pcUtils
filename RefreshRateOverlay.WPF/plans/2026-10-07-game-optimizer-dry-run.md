# Game optimizer, dry-run first

Status: implemented 2026-10-07 (dry run only). Date: 2026-10-07.
Touches: `Services/ProfileService.cs`, `Tray/TrayApp.cs`, `OverlayWindow.xaml(.cs)`,
new `Services/GameOptimizerPlanner.cs`, new `Services/ProcessSnapshotService.cs`,
new `OptimizerPanelWindow.xaml(.cs)`, new tests.

## Problem

The user wants to stop other processes from stealing CPU from a focused game
and causing stutter, without relying on Process Lasso. Getting the
protection rules wrong is the main risk: throttling something the game
depends on (the VR streamer, compositor, audio) makes stutter worse. So the
first slice only *classifies* processes and shows the result. It changes
nothing.

## Measured findings (2026-10-07, this machine)

| Fact | Value |
| --- | --- |
| CPU | i7-14700KF, 8 P-cores (logical 0-15) + 12 E-cores (16-27) |
| Active OpenXR runtime (HKLM\SOFTWARE\Khronos\OpenXR\1 ActiveRuntime) | `C:\Program Files\Virtual Desktop Streamer\OpenXR\virtualdesktop-openxr.json` |
| VR processes running | VirtualDesktop.Streamer, VirtualDesktop.Service, OVRServer_x64, OVRRedir, OVRServiceLauncher |
| Already non-Normal priority | dwm High, NVIDIA Overlay AboveNormal, one steamwebhelper Idle |
| Process Lasso | running; ProBalance on (70% total / 10% per process / 2.7 s); its log shows it restraining rustc and boardvr test exes |

Note the Virtual Desktop runtime JSON sits in an `OpenXR` subfolder while
its exes sit one level up. SteamVR's and Oculus's JSONs sit next to their
exes.

## Design

Same shapes the app already uses.

**Setting.** `ProfileSetting<OptimizerMode>` (`DefaultOptimizerMode` /
`OptimizerProfiles`, fallback `Off`), enum `{ Off, DryRun }`. Default +
per-app override like G-SYNC Mode. No live readback, so it follows the DSX
pattern (no SyncableSetting, `SaveOrClear` helper, dot baseline = stored).
A `Live` value is added only when enforcement is built.

**Planner (pure, unit-tested).** Input: process snapshots (name, pid, path or
null, session id, priority class or null, CPU % of total capacity) plus
context (game exe name, own pid, protected runtime roots). Output: one
decision per process, rules first-match-wins:

1. Own process: Protected, "Overlay app".
2. Name equals the focused app: Game, would boost "High priority, P-cores".
   If its path or priority can't be read (anti-cheat strips handle rights),
   the detail says "no access: a live boost would fail". Knowing this per
   game is one of the main things the dry run is for.
3. Session 0, or no access (path or priority unreadable): Protected, "Windows / service".
   This includes Defender, the indexer and Windows Update. They are real
   stutter sources but are not ours to touch; the panel still shows their CPU.
4. Path under an OpenXR runtime root: Protected, "OpenXR runtime".
5. Built-in name lists: Protected with reason "VR runtime", "GPU / audio / input helper",
   "Anti-cheat", "Launcher", "Capture". (SteamVR, Oculus, Virtual Desktop,
   nvcontainer, NVIDIA Overlay, audiodg, DSX, EasyAntiCheat, BEService,
   steam, obs64, ...)
6. Priority class not Normal: Left alone, "Priority already set (X)".
   While Process Lasso runs, this also catches its temporary ProBalance
   restraints, so the detail says the priority may come from another tool.
7. Built-in dev-tool list (rustc, cargo, link, lld, cl, msbuild):
   Would throttle, "Below normal, E-cores, low I/O".
8. CPU above threshold: Would throttle, same action, reason "High CPU".
   Threshold is in cores, not % of total: default 0.5 core averaged over the
   sample. A % of total on 28 threads (5% = 1.4 cores) would never flag a
   single busy thread, which is exactly what preempts a game thread.
   Known limit: a 2.5 s average misses millisecond bursts; the dev-tool list
   covers the known bursty offenders.
9. Otherwise: Left alone, "Low CPU".

**OpenXR roots (pure).** From the ActiveRuntime JSON path: its folder, plus
the folder's parent when the folder is named `OpenXR`. Never the parent
otherwise (SteamVR's parent is `steamapps\common`, which would protect every
game).

**Snapshot service.** `Process.GetProcesses()`, path via
`QueryFullProcessImageName`, session id, priority class, CPU % from
`TotalProcessorTime` deltas between samples (first sample: CPU unknown, so
rule 8 can't fire yet). Runs on the thread pool, result marshalled to the UI
thread. Read-only: opens processes with
`PROCESS_QUERY_LIMITED_INFORMATION` only.

**When it runs.** On every `ApplyAllProfiles(app)` when the resolved mode for
that app is DryRun, and every 2.5 s while the panel is open. The panel works
for any app regardless of mode, as a preview: it is read-only either way, and
seeing the classification before turning the mode on is the point.
Snapshot cost must be measured before wiring it to focus changes; target
under 50 ms on this machine. Result is kept
as `TrayApp._lastOptimizerReport` and logged once per focus change
(counts only).

**Overlay row.** After Core parking: sync dot, "Optimizer:" combo
(Off / Dry run), and a `▸` button that raises `OptimizerPanelRequested`.

**Side panel.** New borderless dark window, anchored to the overlay's right
edge (left edge if no room on that monitor), `ShowActivated=False`. Header
"Dry run: nothing is changed" plus app and time. Sections: Game, Would
throttle, Protected (grouped by reason), Left alone (collapsed to a count).
Same-name processes collapse into one row with a count. Closes with the
overlay; re-anchors when the overlay reopens on an app switch. Placement
uses the overlay's own monitor work area (`Screen.FromHandle`, converted to
DIPs), not the primary screen's.

Rejected: a tray-menu toggle (user asked for an overlay option with a side
panel); allowlist-everything-else-throttled (too risky before seeing real
classifications).

## Implementation plan

- [ ] `Services/GameOptimizerPlanner.cs`: `OptimizerMode`, `ProcessSnapshot`,
      `OptimizerRole`, `OptimizerDecision`, `OptimizerReport`, `Plan(...)`,
      `OpenXrRoots(...)`, built-in lists, `Summarize(report)` for the panel.
- [ ] `Services/ProcessSnapshotService.cs`: sampling + CPU deltas + OpenXR registry read.
- [ ] `ProfileService.Optimizer`.
- [ ] `OverlayWindow`: row, `SelectedOptimizerMode`, `OptimizerSync`, `OptimizerPanelRequested`, `ReflectOptimizerApplied`.
- [ ] `OptimizerPanelWindow`: render sections, `Update(report)`, `AnchorTo(window)`.
- [ ] `TrayApp`: run on focus change, panel lifetime + timer, Apply/Delete handlers, `HasAnyProfile`.
- [ ] Tests: planner rules (one per rule + ordering), OpenXR roots for VD / SteamVR / Oculus paths, summary grouping, overlay row, panel renders.
- [ ] Visual check: render overlay + panel to PNG and look at it.
- [ ] Live check: run the planner against this machine's real processes and confirm VD Streamer, Oculus, dwm, audiodg are Protected.

## Known gaps (accepted for the dry run)

- The game is identified by the focused exe name only. Games that run their
  real work in a second process (a launcher plus a game exe) show the
  launcher as Game until the real exe takes focus.
- Name lists are built in, not configurable yet. They move to the INI if
  the dry run shows they need per-user edits.
- No P/E core detection: action text says "P-cores" / "E-cores" without
  ranges. Topology (GetSystemCpuSetInformation) is needed only for Live.

## What does not change

Nothing is written to any process: no priority, affinity, CPU set, EcoQoS,
I/O or GPU priority changes. Process Lasso keeps running untouched. No
enforcement, no restore journal (needed only once a Live mode exists).
