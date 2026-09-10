# Plan: G-SYNC as a write-through cache, event-invalidated

**Status:** implemented
**Date:** 2026-09-10
**Touches:** `TrayApp.cs`, `ReconcilePlanner.cs`, `ReconcilePlannerTests.cs`, `NvidiaGsyncService.cs` (docs), `EnforcementBackoffTracker.cs` (docs), `CLAUDE.md`

## Problem

`TrayApp` currently treats NVIDIA's G-SYNC global mode the same way it treats
Rate/HDR: poll for external drift, reconcile on detection, gate routine
pushes with an in-memory cache (`_lastAppliedGsyncMode` +
`ReconcilePlanner.ShouldPush`). That gate was added to stop a redundant NVIDIA
write on every focus change from causing a visible black flash — reasonable
at the time, but **never actually confirmed to fix a real observed flash**.
The one flash we did confirm and fix (see `CLAUDE.md`, "Refresh rate must be
persisted, not just set live") turned out to be caused by something
unrelated (`CDS_UPDATEREGISTRY`), not this.

Separately, and independent of whether the flash-prevention theory holds up,
we measured what the routine G-SYNC path actually costs today and found a
real, worth-fixing problem: NVIDIA reads are far more expensive than we'd
assumed, and we're doing more of them than necessary — including a fully
redundant one on every externally-detected change.

## Measured findings

### Read latency (this machine, 50 iterations each, warmed up)

| Call | min | mean | median | p95 | max |
|---|---|---|---|---|---|
| `NvidiaGsyncService.TryGetGlobalMode` | 102.7ms | 133.7ms | 147.5ms | 161.1ms | 163.4ms |
| `DisplayService.GetCurrentRate` | 0.001ms | 0.001ms | 0.001ms | 0.001ms | 0.003ms |
| `HdrService.GetState` | 0.006ms | 0.006ms | 0.006ms | 0.006ms | 0.017ms |

NVIDIA's read is **~20,000–150,000x** slower than the Win32 calls it sits
next to, and it runs synchronously on the UI thread every time it's called
(WinForms timer ticks and dispatcher-marshaled focus events both run there).
100ms+ is above the threshold where a UI action reads as laggy if this were
ever in a hot path a user notices directly.

### Why it's that slow: DRS database size (this machine)

| | |
|---|---|
| `CreateAndLoad()` time | 106.7ms |
| Profiles loaded | 7,960 |
| Total settings across all profiles | 30,205 |
| Total app associations | 12,972 |
| Predefined (NVIDIA-shipped) profiles | 7,958 |
| Custom/user profiles (ours) | 2 |
| Base profile itself | 24 settings, 0 app associations |

Confirmed by reflecting on the installed `NvAPIWrapper.dll`
(`NvAPIWrapper.DRS.DriverSettingsSession`/`DriverSettingsProfile`): there is
no narrower entry point than `CreateAndLoad()` — no `Load(profileName)`, no
per-setting fetch that skips the full load. `FindProfileByName`/`GetSetting`
only ever operate on an already-loaded session; they don't trigger their own
driver round-trip. To read the one `VRR_MODE` setting (1 of 24 on the base
profile, itself 1 of 7,960 profiles), NVIDIA's driver deserializes the whole
database every single time. This is a hard constraint of NVIDIA's own DRS
API surface, not a limitation of how this app uses it — every DRS tool
(nvidiaProfileInspector included) pays the same cost the same way.

### Existing redundant-read bug found while tracing this

`HardwareChange` (the shared "something might have changed externally, go
check" signal) is raised by two independent sources: `PollGsyncMode` (every
2s) and `OnDisplayChange` (`WM_DISPLAYCHANGE`, for Rate/HDR reasons — which
fires often during actual play: every rate change, every alt-tab). Every time
`ReconcileAll` runs, for *either* reason, it walks `_gsyncSetting`, whose
`tryReadLive` delegate calls the real `TryGetGlobalMode` again — a full
~130ms read, even when the poll already just fetched the same value
microseconds earlier, and even when the trigger had nothing to do with
G-SYNC at all. Actual read volume today is *(every 2s) + (every
WM_DISPLAYCHANGE)*, not just "every 2s" as the poll interval implies.

## Design

Three named, standard patterns, combined:

- **Write-through cache** — one in-memory field is the sole source of truth
  everyone reads. Every write updates NVIDIA *and* the field together, so the
  cache can never go stale from our own actions — only from something
  external touching NVIDIA behind our back.
- **Dirty checking** (same term ORMs use — Entity Framework's
  `ChangeTracker`, Hibernate's dirty checking) — compare the target against
  the cached value before writing; skip the NVIDIA call entirely if they
  already match. This is what `ReconcilePlanner.ShouldPush` already does,
  just not labeled as such; folding it back to a plain inline check once the
  cache is the obvious, singular source of truth (see Implementation below).
- **Event-driven invalidation**, replacing TTL/poll-based invalidation — the
  cache refreshes on a specific signal (leaving the NVIDIA App) instead of a
  fixed interval.

Read-only in-memory forever, except:
1. One real read at startup, to seed the cache (unchanged from today).
2. One real read whenever focus leaves `NVIDIA App.exe` — confirmed exact
   process name via `Get-Process` and this app's own log
   (`ForegroundTracker: app changed -> 'NVIDIA App.exe'`, with the space).
   This is the point someone is most likely to have just changed G-SYNC by
   hand; refreshing exactly there catches the real-world case (confirmed
   already happened once — see `2026-08-31 23:22:09` in the log, where the
   poll caught a change made this exact way) without paying for continuous
   polling.
3. `EnforceGsyncAsync`'s settle-confirm loop keeps reading live, unchanged —
   it's answering "did my own write from a moment ago land," which a cache
   (even one refreshed a second ago) can't answer; same treatment
   Rate/HDR's `SettleAsync` already gives its own settle loop.

**Accepted limitation:** a third-party DRS editor (nvidiaProfileInspector, or
NVCP's legacy `nvcplui.exe` if still reachable on this system) changing
G-SYNC would not be caught until the next startup or next NVIDIA-App-focus
event. Explicitly accepted for now — the common real case (the first-party
NVIDIA App) is covered.

## Implementation plan

- [x] `TrayApp`: merge `_lastAppliedGsyncMode` + `_lastPolledGsyncMode` into
      one field — the write-through cache. Seed it at startup exactly as
      today (no change to that part).
- [x] Remove `_gsyncPollTimer` / `PollGsyncMode` entirely.
- [x] `_gsyncSetting`'s `tryReadLive` delegate returns the cached field
      instead of calling `TryGetGlobalMode` — this is what actually kills
      the redundant double-read inside `ReconcileAll`.
- [x] Add "previously focused app" tracking to `TrayApp` (a plain field,
      updated at the end of `OnAppChanged`, checked at the start of the next
      call — `ForegroundTracker` itself only exposes the *new* app, not the
      one just left, so this can't be read off it directly).
- [x] In `OnAppChanged`: when the app just left was `'NVIDIA App.exe'`
      (`OrdinalIgnoreCase`) and the new one isn't, do one real
      `TryGetGlobalMode`, update the cache, and raise `HardwareChange` if it
      disagrees with what reconciliation currently believes — routes through
      the *existing* `ReconcileAll`/`EnforceGsyncAsync`/backoff machinery
      unchanged, just with a smarter trigger.
- [x] `ApplyGsyncMode`: fold the `ReconcilePlanner.ShouldPush` call back into
      a plain inline comparison against the cache — remove `ShouldPush` and
      its dedicated tests from `ReconcilePlanner`/`ReconcilePlannerTests`,
      since it's no longer a distinct, separately-worth-naming concept once
      the cache is the singular, obvious source of truth for the whole
      component rather than a bespoke guard for one call site.
- [x] `NvidiaGsyncService.cs` doc comment: note `TryGetGlobalMode` is now
      called from exactly two places (startup seed, NVIDIA-App-focus-loss
      refresh) plus the settle-confirm loop, not on any recurring cadence.
- [x] `CLAUDE.md`: new section once shipped, matching the existing style —
      what changed, why, and the measured numbers above as the justification
      (mirroring how "Refresh rate must be persisted" and "Routine
      re-assertion must no-op" are already written).
- [x] Build + full test suite must stay green (137/137 at the time of
      writing) before this is considered done.

## What does *not* change

- Rate/HDR reconciliation — untouched; their `tryReadLive` delegates stay on
  cheap, real Win32 reads, since there's no cost problem to solve there.
- `EnforceGsyncAsync`, `_gsyncBackoff`, `_gsyncEnforceInFlight`, and
  `ReconcileAll`'s G-SYNC branch (both reconciliation-on and -off) — all stay
  exactly as they are; only what *triggers* them changes.
