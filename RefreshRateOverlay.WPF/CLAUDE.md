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
