# RefreshRateOverlay.WPF

Per-app display-settings overlay: refresh rate, HDR, DSX controller profile, and
NVIDIA G-SYNC, all switched automatically on foreground-app change and editable
from a settings popup (hotkey or tray icon).

Before a non-trivial change — real back-and-forth on approach, anything worth
benchmarking, anything touching enough of the app that the plan is worth
pinning down before starting — write it up in `plans/` first; see
`plans/README.md` for the shape. Sections below are the *shipped* decisions
this file exists to carry forward; `plans/` is where those decisions get
worked out beforehand, and the historical record of how they were reached.

## Per-app setting shapes

All four settings (rate, HDR, DSX controller profile, G-SYNC mode) use the same
shape: app-agnostic default + per-app override, both backed by our own
`RefreshSettings.ini` via `ProfileSetting<T>` (`Services/ProfileSetting.cs`,
owned by `Services/ProfileService.cs`). The INI is the single source of truth
for all of them, including G-SYNC — NVIDIA's own DRS database is never read to
decide what the overlay shows; it's a **push target**, exactly the relationship
`DisplayService`/`HdrService` have with the physical display.

## G-SYNC: base-profile-only, on purpose

`Services/NvidiaGsyncService.cs` writes `VRR_MODE` (setting ID `0x1194F158`,
NVCP's "Enable G-SYNC" toggle) via the `NvAPIWrapper.Net` NuGet package (the
only non-Win32 dependency in this project — `nvapi64.dll` exports just
`nvapi_QueryInterface`, so raw `[DllImport]` the way every other service here
does it isn't viable).

**It only ever writes NVIDIA's base profile — never a per-app DRS profile.**
This was tested both ways: an earlier version wrote `VRR_MODE` onto the
running game's own application profile as a per-app override, on the
assumption (backed by NVIDIA Profile Inspector's UI, which does expose
"G-SYNC – Application Mode" separately from "G-SYNC – Global Mode") that the
driver would prefer the more specific profile. In practice it didn't: the
game only ever got G-SYNC when the *base* profile said so, regardless of what
sat on its own profile. Whatever Profile Inspector's UI implies, this driver
doesn't honor a per-app `VRR_MODE` override the way it does for other DRS
settings — so don't reintroduce that path without re-testing it for real.

Per-app *behavior* still exists, the same way it does for HDR — which has no
per-app concept at the OS level either. `TrayApp.ApplyGsyncMode` resolves
(this app's INI override ?? INI default) on every foreground-app change and
reasserts that single resolved value into NVIDIA's one base-profile setting —
mirroring `ApplyProfile` reasserting rate/HDR into hardware. `VRR_APP_OVERRIDE`
(the setting that actually is documented as per-application-only) isn't used
by this app at all — it was tried and dropped because every push landed on
whatever app happened to be in the foreground when the overlay's non-profile
(default-editing) mode was applied, which is not a coherent target for a
setting with no default/global scope of its own. If per-app G-SYNC exclusion
is wanted later, it needs its own control that's *only* ever writable in
per-app (ticked) mode, never touched from default-editing mode.

`TryGetGlobalMode` seeds the INI default from NVIDIA's live base-profile
state at startup — same treatment `HdrService.GetState()` gets for
`_defaultHdr` — but is not something the overlay reads to decide what to show
mid-session; see "G-SYNC reads are a write-through cache, not a poll" below
for the other two places it's called and why nowhere else calls it.

NVIDIA's own DRS docs are explicit that **sessions don't merge** — save is a
last-write-wins snapshot, so if the NVIDIA app or NVCP has a session open
concurrently, whichever side saves last silently drops the other's edit. This
is why every `NvidiaGsyncService` method opens a session, does one thing,
saves, and disposes immediately rather than holding one open across the
overlay's lifetime — minimizes, doesn't eliminate, that window.

## Refresh rate must be persisted, not just set live

`DisplayService.SetRate` calls `ChangeDisplaySettingsW` with `CDS_UPDATEREGISTRY`
— **not optional**. Without that flag, `ChangeDisplaySettingsW` only changes
the live/dynamic mode for the current session; it never touches Windows' own
persisted "current settings" for the display (what Settings > Display shows,
what survives logoff/reboot). Found and fixed after a real, reproducible bug:
with the rate left un-persisted, the display would visibly flash black on
transitions that had nothing to do with this app's own reconciliation logic —
alt-tabbing away from and back to a game, switching virtual desktops. Root
cause, confirmed by hand: Windows' persisted display config still said 60Hz
(this app had only ever changed the live mode), so some of those transitions
made Windows/the driver transiently fall back toward that still-60Hz
persisted value, which this app's own settle/reconcile loop then dutifully
detected as drift and corrected back — a second, avoidable mode-set stacked
on top of whatever caused the first one, both visible as a flash. Confirmed
by manually setting Windows' own display settings to match this app's target
rate: the transitions stopped immediately, with this app's code completely
unchanged. The two were never disagreeing on the *value* — they were reading
from two different sources of truth for what "the" refresh rate even is.

## Startup seeding is a reconciliation decision too — it must respect ReconciliationEnabled

`TrayApp`'s constructor seeds each default+profile setting's shared default
(rate, HDR, G-SYNC) from live hardware/driver state at startup — needed for a
genuine first run, where the INI has nothing to go on yet. The pre-fix code
did this unconditionally on *every* startup: rate only reseeded when the
stored default happened to equal a hardcoded `60` sentinel, but HDR and
G-SYNC reseeded from live state on every single restart with no gate at all.

Real, reproducible bug: restarting the app (crash, dev reload, Windows
Update — anything) while a per-app profile was still driving the display at
a non-default rate/HDR/G-SYNC state (a game running, or its rate just never
settled back) silently overwrote the *permanent* default and persisted it to
the INI — indistinguishable from a deliberate profile change. Confirmed via
`RefreshRateOverlay.WPF.log`: a restart while a 100Hz-profiled game was
running rewrote `DefaultRefreshRate` 60 → 100, and every unprofiled app
(Explorer, terminal, other tools) ran at 100Hz from then on, for the rest of
that session and every session after — surfacing, a day later, as "I left
[game] and its profile became the global settings," even though no profile
save was ever involved.

This is the exact same question `ReconcileAll` already answers for every
other drift during a session — "is an externally-observed value allowed to
overwrite our own persisted truth?" — and `ReconciliationEnabled` is
supposed to be the one switch that controls that answer everywhere. Startup
seeding was answering it differently (always yes) instead of asking the same
question the same way. Fixed by routing all three settings' startup seed
through `ReconcilePlanner.PlanSeed` (see its remarks): adopt live state
unconditionally only on true first run (`ProfileSetting<T>.HasDefault()` is
false — the INI key was never written, not just equal to the fallback),
otherwise only when `ReconciliationEnabled` is on, exactly like every other
drift. With reconciliation off, the stored default now survives a restart
unchanged, the same way it already survives every mid-session
`HardwareChange`.

## Routine re-assertion must no-op when nothing's actually changing

`ApplyAllProfiles` re-resolves and re-pushes rate/HDR/G-SYNC on every single
foreground-app change — including Sticky Profiles resolving back to the SAME
app when focus briefly passes through something unrelated (see
`StickyProfileTracker`/`ResolveStickyApp`). That means "nothing changed" is
the *common* case for these pushes, not the exception, so every push path
needs its own honest answer to "do we actually need to do anything" — a gap
that shipped as a real bug for two of the three settings this pushes:

- **Rate** (`TrayApp.ApplyRate`) always had this: `if (_currentRate == rate)
  return;` before ever touching `DisplayService.SetRate`.
- **HDR** (`HdrService.SetState`) didn't — it unconditionally called
  `DisplayConfigSetDeviceInfo` + `SetDisplayConfig(..., SDC_APPLY)` on every
  call. `SDC_APPLY` reprograms the display path — enough on its own to cause
  a visible flash even when the value being written was already active. Now
  reads `GetState().Enabled` first and no-ops if it already matches.
- **G-SYNC** (`NvidiaGsyncService.SetGlobalMode`) didn't either — it
  unconditionally opens a DRS session and `Save()`s. Re-saving the base
  profile is enough to make the driver retrain/blank the link, identical
  value or not.

G-SYNC's guard is shaped differently from HDR's on purpose: it does **not**
read NVIDIA's live state first to decide whether to push (unlike HDR's
`GetState()` check) — it compares against `_gsyncCache`, a write-through
cache that's never refreshed from a live read on the routine push path. See
"G-SYNC reads are a write-through cache, not a poll" below for why, and for
how external drift still gets caught without polling.

## G-SYNC reads are a write-through cache, not a poll

`NvidiaGsyncService.TryGetGlobalMode` used to be polled every 2 seconds
(`TrayApp.PollGsyncMode`), forever, whenever G-SYNC is available — plus a
second, fully redundant read inside `ReconcileAll` every time it ran for
*any* reason (including `WM_DISPLAYCHANGE`, unrelated to G-SYNC). Measured
directly rather than assumed (see
`RefreshRateOverlay.WPF/plans/2026-09-10-gsync-cache.md` for the full
numbers): a single `TryGetGlobalMode` call costs **~130ms** (min 102.7ms,
p95 161.1ms across 50 calls), roughly 20,000-150,000x a `DisplayService`/
`HdrService` Win32 call (both sub-0.02ms). Confirmed why: NVIDIA's DRS API
has no entry point narrower than `DriverSettingsSession.CreateAndLoad()`,
which loads its *entire* database every call — 7,960 profiles, 30,205
settings, 12,972 app associations on this machine, to read one 24-setting
base profile. Every DRS tool (nvidiaProfileInspector included) pays this the
same way; it isn't specific to how this app uses the wrapper.

`TrayApp._gsyncCache` is a write-through cache instead: the sole source of
truth `_gsyncSetting`'s `tryReadLive` and `ApplyGsyncMode`'s push guard both
read, and the only thing `NvidiaGsyncService.SetGlobalMode`/
`EnforceGsyncAsync` update alongside the real NVIDIA push. `TryGetGlobalMode`
itself is now called from exactly three places: once at startup (seeding),
once inside `EnforceGsyncAsync`'s settle-confirm loop (has to be live — it's
answering "did my own write from a moment ago land," which a cache can't
answer), and once whenever focus leaves the NVIDIA App
(`TrayApp.RefreshGsyncCacheIfLeavingNvidiaApp`) — event-driven invalidation
in place of the old poll's time-driven invalidation, refreshing exactly at
the moment someone is most likely to have just changed it by hand. Confirmed
this isn't hypothetical: the log already had a real instance of a G-SYNC
change made this exact way, caught (at the time) by the poll this replaces
(`2026-08-31 23:22:09`, `app='NVIDIA App.exe'`).

Accepted gap: a third-party DRS editor (nvidiaProfileInspector, legacy NVCP)
changing G-SYNC wouldn't be caught until the next startup or next
NVIDIA-App-focus event — the common real case (the first-party NVIDIA App)
is covered; broader coverage wasn't worth reintroducing a poll for.

## Core parking is a default + per-app setting, like HDR

"Disable core parking" pins "Processor performance core parking min cores"
(both efficiency classes, AC and DC) to 100% on the *active* power plan via
`powrprof.dll`. It uses the same shape as HDR: a `ProfileSetting<bool>`
(`ProfileService.CoreParking`, true = parking disabled) wrapped in a
`SyncableSetting<bool>` in `TrayApp`, a sync-dot row in the overlay
(`OverlayWindow` CoreParkingRow), saved by Apply / "Save for <app>", resolved
per app by `ApplyAllProfiles`, seeded via `ReconcilePlanner.PlanSeed`, and
absorbed or reverted by `ReconcileAll` depending on `ReconciliationEnabled`.
`Services/CoreParkingService.cs` is only the hardware half.

- **No-op when nothing differs.** `CoreParkingEnforcer.Apply` fetches the active
  plan once, reads first, writes only values that differ, and re-activates the
  plan only if it wrote (also after a partial write failure, so written values
  never sit unapplied). Re-activating fires the power-setting notifications that
  make `TrayApp` re-check, so the pass that finds nothing to do ends that loop.
- **"Allowed" is whatever the live read says.** `IsDisabled` is true only when
  every readable value is 100. `Apply(allow)` therefore does nothing unless the
  plan is fully pinned, so a mixed state (values at 100 from another tool or a
  vendor plan) is never rewritten. An unreadable plan makes the setting
  unavailable instead of reading as "allowed".
- **"Off" needs a defined value, per plan.** Disabling remembers each value it
  replaces, keyed by plan GUID + setting + AC/DC (`[CoreParkingRestore]` in the
  INI), so a plan switch can never restore one plan's numbers into another.
  Allowing parking again from a fully pinned plan puts those back, or falls
  back to 0 (Windows decides) when nothing was remembered.
- **Its own reconcile path.** Parking is deliberately not in `_syncables`.
  Power-setting notifications (`SystemMessageSink`, on the active-plan GUID and
  both min-cores GUIDs) call `TrayApp.OnPowerSettingChange`, which reconciles
  parking only. Going through `HardwareChange`/`ReconcileAll` would re-reconcile
  rate/HDR/G-SYNC in the middle of the same focus change's display settle, where
  our own parking write lands, and mistake a transient clamped value for an
  external change. `ReconcileAll` still calls `ReconcileCoreParking` too.
- **ParkControl.** Its Dynamic Boost swaps in an Idle plan/profile after a
  timeout. With reconciliation on, that is absorbed as an external change (the
  stored default follows it); with reconciliation off it is reverted through
  `_parkingBackoff`, which gives up after repeated reverts like the other
  enforcement buckets and shows "Core parking" in the overlay warning.
  Enforcement edits whichever plan is active and nothing restores it on exit.
- Tests use fake `IProcessorPowerSettings` / `IParkingRestoreStore`; never read
  or write real power plans from tests.

## Game optimizer

`Services/GameOptimizerPlanner.cs` classifies every process for the focused
app as Game, Would throttle, Protected or Left alone (first-match-wins rules,
see the plan `plans/2026-10-07-game-optimizer-dry-run.md`).
`Services/ProcessSnapshotService.cs` samples processes read-only
(`PROCESS_QUERY_LIMITED_INFORMATION`, about 10 ms for ~350 processes on the
dev machine). Nothing ever writes to a process yet.

- **Setting.** `ProfileService.Optimizer` (Off / DryRun), default + per-app like
  DSX: no live readback, so no SyncableSetting. A Live value is added only
  with enforcement, together with a restore journal.
- **Panel.** The overlay's optimizer row has a ▸ button that opens
  `OptimizerPanelWindow` beside the overlay. It works in any mode (Off shows
  "Preview only"), refreshes every 2.5 s while open, closes with the overlay
  and follows it on an app switch.
- **Throttle by default, protect by general signals.** Every ordinary user
  process at Normal priority is throttled up front (groups Busy now / Build
  tool / Background), so a spike lands on E-cores at Below normal instead of
  waiting up to a sample to be caught. New processes are classified at birth
  via the existing `ProcessStartWatcher` (ETW process-start trace), not the
  next 2.5 s pass. Because unknown processes get throttled, protection can't
  depend on name lists; the lists are only a backstop. The general signals:
  - **Registered VR runtimes and tools** (`ReadVrRuntimeFolders`): every
    OpenXR runtime (active + `AvailableRuntimes`), every OpenXR API layer
    (HKLM/HKCU, Implicit/Explicit, enabled or not), and the OpenVR runtime and
    `external_drivers` from `%LOCALAPPDATA%\openvr\openvrpaths.vrpath`. Each
    folder plus its parent, unless the parent is a container folder (Program
    Files, `steamapps\common`, AppData\Local, ...): SteamVR's parent would
    protect every game.
  - **Started by the VR pipeline**: descendants of a VR pipeline process are
    pipeline too, whatever their name; the walk stops at the game.
  - **Real-time threads**: a thread base priority of 16+ only exists via MMCSS
    (audio, VR compositors/streamers) or a Realtime class, so it marks
    latency-critical work. 15 is common UI noise (Explorer, PowerToys) and does
    not count. Sticky per process instance: MMCSS raises threads only while
    active. Read from `Process.Threads`, which `GetProcesses` already loaded
    (a Toolhelp thread snapshot tripled the scan time).
- **Left alone** is only what someone else set a priority for (the user, the
  app, Process Lasso), grouped as Already High / Above normal / Below normal /
  Idle.
- **The game's parent chain is never changed** (role `GameChain`, shown under the game in the panel as the launch chain, nearest parent first). A child starts at its parent's
  priority class when that is Idle or Below normal, and inherits its CPU
  affinity, so throttling a launcher (or Explorer, a terminal, the anti-cheat
  bootstrapper) just before it starts the game would start the game
  throttled. Parent pids come from a Toolhelp snapshot, dropped when the
  parent's creation time is after the child's (pid reuse).
- **The VR pipeline sits with the game.** Processes under a registered VR
  runtime folder, started by one, or with a known VR runtime name get role
  `VrPipeline`: never
  changed, shown under the game next to the launch chain, because in VR the
  game's frame still has to pass through them.
- **Hook holders are protected.** A starved process with a global keyboard or
  mouse hook delays input system-wide: AutoHotkey, AggressiveScreensaver,
  LGTV_brightness.
- **CPU threshold is in cores** (0.5), not % of total: one busy thread is
  3.6% of 28 threads and is exactly what preempts a game thread. It now only
  decides which throttle group (Busy now vs Background) a process shows in.
- **Live mode** (`Services/GameOptimizerEnforcer.cs`, plan
  `plans/2026-10-07-game-optimizer-live.md`): the game only gets the P-core
  CPU sets, never a higher priority. A High game outranks most threads of the
  VR streamer/runtime, which run Normal (measured: 25 of Virtual Desktop
  Streamer's 27 threads and 93 of OVRServer's 101 at base 8; its critical ones
  are realtime-range), and in VR the frame still has to be encoded and sent.
  Throttling the background is what removes contention; throttled processes get Below normal, the E-core CPU sets
  and Very low I/O. Never Realtime, Idle, kill or suspend. CPU sets come from
  `CpuTopology` (highest EfficiencyClass = P); a non-hybrid CPU gets priority
  changes only. All writes go through `IProcessControl`, which re-checks the
  creation time on the opened handle (pid reuse is never touched).
- **Undo is the core of it.** Write-ahead JSON journal in `%LOCALAPPDATA%`;
  restore per attribute only when it still holds our value (a later change by
  the user or Process Lasso wins). A session is one resolved app: switching
  app, a mode other than Live, exit, and startup (crash recovery) restore
  everything. These attributes don't survive a reboot, so only an app crash
  with the PC still running needs the journal.
- **Stable classification.** Journaled processes are fed to the planner at
  their original priority, and a throttle is sticky for the session, so
  nothing flaps.
- **Inherited throttling is undone too.** A child started by a throttled
  process inherits Below normal/Idle. It is fed to the planner at the parent's
  original priority, and its journal entry records the parent's original for
  every attribute that equals the parent's applied value, so restore puts it
  back to Normal instead of leaving it throttled after the session. Only the Live app is ever enforced; a panel
  preview of another app never changes anything.
- **Panel choices** (`Services/OptimizerActions.cs`): the Game and Would
  throttle sections each have a Priority and a Cores picker, stored globally in
  `[Settings]` (`OptimizerGame*`, `OptimizerThrottle*`). The game can only be
  raised (Unchanged / Above normal / High), a throttled process only lowered
  (Unchanged / Below normal / Idle); never Realtime. Defaults: game Unchanged +
  P-cores, throttle Below normal + E-cores. A change restores everything, then
  re-applies from the true originals. The pickers are built once and
  re-parented on each 2.5 s rebuild; a rebuild is skipped while a dropdown is
  open. Throttle, Protected and Left alone are collapsible groups by reason;
  Busy now and Build tool start open, the rest collapsed. Protected, Left alone, the launch chain and the VR pipeline have no
  pickers on purpose: they are never changed.
