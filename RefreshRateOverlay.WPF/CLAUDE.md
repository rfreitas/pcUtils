# RefreshRateOverlay.WPF

Per-app display-settings overlay: refresh rate, HDR, DSX controller profile, and
NVIDIA G-SYNC, all switched automatically on foreground-app change and editable
from a settings popup (hotkey or tray icon).

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

`TryGetGlobalMode` (the only read method) exists purely to seed the INI
default from NVIDIA's live base-profile state at startup — same treatment
`HdrService.GetState()` gets for `_defaultHdr`, not something the overlay
reads to decide what to show mid-session.

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
`GetState()` check). A live DRS read is comparatively expensive (a full
session open/load, not a cheap Win32 call) and reading-then-conditionally-
writing opens a real time-of-check-to-time-of-use gap — if something external
changes G-SYNC in between our read and our (skipped) write, we'd wrongly
believe hardware still matches. Instead `TrayApp` tracks
`_lastAppliedGsyncMode` — what *this app itself* last successfully pushed —
and `ReconcilePlanner.ShouldPush` (`Services/ReconcilePlanner.cs`) is the
pure, unit-tested comparison behind that gate. Real external drift is still
caught independently by `PollGsyncMode`/`ReconcileAll`, which always re-read
NVIDIA's live state and never consult this cache — so the cache can only ever
elide a redundant re-push, never mask a genuine external change.
