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
