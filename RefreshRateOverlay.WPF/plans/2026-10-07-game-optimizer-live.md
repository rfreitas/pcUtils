# Game optimizer, Live mode

Status: implemented 2026-10-07. Builds on
`2026-10-07-game-optimizer-dry-run.md`.
Touches: `Services/GameOptimizerPlanner.cs`, new `Services/CpuTopology.cs`,
new `Services/ProcessControl.cs`, new `Services/GameOptimizerEnforcer.cs`,
`Tray/TrayApp.cs`, `OverlayWindow.xaml.cs`, `OptimizerPanelWindow.xaml.cs`, tests.

## Problem

The dry run classifies processes; the user approved actually applying it.
The hard requirements are reversibility and crash safety: every change must be
undone when the optimizer stops, and a crash of this app must not leave other
processes throttled.

## Facts the design relies on

- Priority class, CPU sets and I/O priority are runtime attributes of a
  process. Windows persists none of them: after a reboot every process starts
  at its defaults. Only this app crashing while the PC keeps running can leave
  changes behind.
- A child process starts at its parent's priority class when that is Idle or
  Below normal, and inherits affinity (why the launch chain is never touched).
- 14700KF: CPU sets with the highest EfficiencyClass are P-cores (logical 0-15),
  the rest E-cores (16-27). Non-hybrid CPU: no CPU-set changes at all.

## Design

**Actions.** Game: default CPU sets = P-cores, priority unchanged (changed
after implementation: High would outrank the Normal-priority VR streamer and
runtime threads; see CLAUDE.md). Throttled:
priority Below normal, default CPU sets = E-cores, I/O priority Very low.
Never Realtime, never Idle, never kill or suspend. CPU sets (soft) rather than
affinity (hard): an explicit affinity, e.g. from Process Lasso, still wins.

**Journal (write-ahead).** Before changing a process, record pid, creation
time, name, the original values and the values applied, and save the journal
to `%LOCALAPPDATA%\RefreshRateOverlay.WPF\optimizer-journal.json`. Restore
per attribute, and only when the current value still equals what we applied
(someone else changed it later: leave it), and only when the creation time
still matches (pid reuse: never touch). Entries of exited processes are dropped.

**Session.** A session is one resolved app in Live mode. It starts on
`ApplyAllProfiles(app)` with Live, re-evaluates every 2.5 s while active, and
ends (restore everything) when the resolved app changes or its mode is not
Live, on app exit, and at startup (crash recovery: restore whatever the journal
still holds).

**Stable classification.** Before planning, journaled processes are fed to the
planner with their original priority, so our own Below normal doesn't make
them "Priority already set". A process throttled for high CPU stays throttled
until the session ends (no flapping as its CPU drops); it is released early
only if it becomes Game, launch chain or Protected.

**UI.** Mode combo gains Live. Panel banner and section title follow the mode
("Throttled" instead of "Would throttle"); rows show per-process failures
(for example anti-cheat denying access to the game).

## What does not change

Protection rules, thresholds and lists. No GPU priority or power-saving
(EcoQoS) changes yet. Process Lasso is not touched.

## Update 2026-10-07: throttle by default

Reactive throttling (only processes over 0.5 core at a 2.5 s sample) misses
short spikes. Now every ordinary user process at Normal priority is throttled
up front, and new processes are classified at birth through the existing
`ProcessStartWatcher`. The CPU threshold only picks the panel group (Busy now
vs Background). Since unknown processes are throttled, protection moved to
general signals: every registered OpenXR runtime and API layer, the OpenVR
runtime and drivers, anything started by the VR pipeline, and real-time
threads (base priority 16+). Children of throttled processes are journaled
with the parent's original values so restore doesn't leave them throttled.
Left alone is only processes with a priority someone else set.
