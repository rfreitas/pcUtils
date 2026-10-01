# Tray app UX guideline

How every tray app in this repo looks, behaves and is built. Follow it when creating or changing one
(AggressiveScreensaver.NET, LGTV_brightness.NET, RefreshRateOverlay.WPF, TaskbarReveal.NET, PortWatch.NET).
**PortWatch.NET is the reference implementation** — it uses all of the shared building blocks below.

## 1. The model in one paragraph

A tray app is a **WPF `Application` host** (message loop, dispatcher, windows) with a **WinForms `NotifyIcon`**
for the icon and right-click menu (WPF has no native tray control). **Left-click opens a flyout** above the
taskbar; **right-click opens a dark menu** ending in *Start at Login* and *Exit*; **hover only shows a tooltip**.
One copy runs at a time, it logs to `%LOCALAPPDATA%`, and it can be rendered and exercised with no mouse.

| Gesture        | Does                                                                 |
| -------------- | -------------------------------------------------------------------- |
| Left-click     | Toggle the flyout (open → click-away / Escape / icon click closes it) |
| Right-click    | Dark context menu (`TrayMenu`)                                        |
| Hover          | Tooltip text only (`NotifyIcon.Text`, **≤ 127 chars**)                |
| Double-click   | Nothing special (treat as two left-clicks)                            |

**Do not build hover popups.** It was tried (PortWatch) and rejected: `NotifyIcon` has no mouse-leave event
(you need a polling timer to know when to hide), a hover is a burst of ~90 `MouseMove`/s that re-enter
`Window.Show()` and corrupt the window, and an icon parked in the overflow flyout is a separate hover target.
Click is deterministic.

## 2. Shared building blocks (`Shared.NET/`)

Link them into your csproj (`<Compile Include="..\Shared.NET\X.cs" Link="..." />`). They are `internal` and
compiled into each app. **Use them; do not copy their logic.**

| File                        | Gives you                                                                                                   |
| --------------------------- | ----------------------------------------------------------------------------------------------------------- |
| `SingleInstanceGuard.cs`    | One copy only; newest launch kills the old copy (same exe path). `TryAcquire` / `Dispose`.                   |
| `TrayMenu.cs`               | Dark menu factory, `CheckItem` (✓ prefix, revert-on-failure), `StartAtLoginItem`, `Header`, `Action`, `ExitItem`. |
| `DarkMenuRenderer.cs`       | The renderer `TrayMenu.Create()` installs.                                                                   |
| `StartupTaskService.cs`     | Task Scheduler logon task (`IsInstalled` / `Install` / `Uninstall`). Used by `StartAtLoginItem`.             |
| `TrayFlyoutWindow.cs`       | Base class for the flyout: borderless/topmost, Deactivated + Escape dismiss, `ToggleShow` click-away debounce. |
| `TrayPopupPlacement.cs`     | `ShowAbove(window, cursor)`: taskbar-aware, per-monitor-DPI placement from the window's real pixel rect.     |

> Status: PortWatch uses all of them. AggressiveScreensaver and LGTV_brightness still carry older copies of the
> single-instance code, the checkbox-menu code and the slider placement code. When you touch those areas, migrate
> them to the shared versions instead of editing the copies.

## 3. Project setup checklist

- `OutputType` `WinExe`, `TargetFramework` `net8.0-windows10.0.19041.0`, `UseWPF` **and** `UseWindowsForms` true,
  `Nullable` + `ImplicitUsings` enabled. Note: with WPF/WinForms, `System.IO` is **not** an implicit using.
- `app.manifest`: `dpiAwareness` **PerMonitorV2**. Execution level **`asInvoker`** unless the app genuinely needs
  elevation. Current reasons: LGTV/Aggressive draw over elevated fullscreen apps; PortWatch starts a kernel network ETW
  trace for live per-port traffic. The startup task's `requireElevation` argument **must match** the manifest.
  An elevated app can only be stopped/replaced by an elevated process (`SingleInstanceGuard`, `Directory.Build.targets`'
  taskkill and your own shell all need to be elevated), and its headless flags need an elevated shell too.
- `App.xaml`: `ShutdownMode="OnExplicitShutdown"` (the app has no main window). Exit only via
  `Application.Current.Shutdown()` from the menu.
- Name things `<App>.NET` (+ `<App>.NET.Tests`), add both to `AutoHotkey.sln`, add `bin/` `obj/` ignores to `.gitignore`.
- The root `Directory.Build.targets` kills a running copy before each build, so `dotnet build` never hits a locked exe.

## 4. Startup sequence (`App.xaml.cs`)

In `OnStartup`, in this order:

1. `WinForms.Application.SetUnhandledExceptionMode(CatchException)` and a `ThreadException` handler that **logs**.
   Without it, an exception inside a `NotifyIcon` event shows a modal "Unhandled exception" dialog and never reaches the log.
2. `DispatcherUnhandledException` → log, `Handled = true`. `AppDomain.UnhandledException` → log.
3. `SingleInstanceGuard.TryAcquire(@"Global\<App>.NET", "<exe name>", replaceExisting: true, Logger.Log)`;
   `Shutdown()` and return if null. Dispose it in `OnExit`.
4. Construct `TrayApp` (owns the `NotifyIcon`, the menu, the flyout and the services). Dispose it in `OnExit`.

**Logging:** `%LOCALAPPDATA%\<App>\<App>.log`, never `AppContext.BaseDirectory` (`dotnet clean` wipes it). Every
failure path logs. Logging must never throw.

## 5. The flyout (left-click)

- Derive from `TrayFlyoutWindow`. Borderless, `Topmost`, `ShowInTaskbar=false`; it dismisses on click-away
  (Deactivated) and Escape.
- Open with `flyout.ToggleShow(() => { refresh(); TrayPopupPlacement.ShowAbove(flyout, Cursor.Position); flyout.Activate(); })`.
  `ToggleShow` closes an open flyout and swallows the reopen that a click-away on the icon would otherwise cause
  (250 ms debounce). `Activate()` is required or Deactivated never fires.
- **Never position by hand.** `ShowAbove` anchors to the taskbar's top edge (works with auto-hide taskbars), or the
  cursor if that is higher (icon in the overflow flyout), clamps to the screen, sizes from the real pixel rect and
  re-places after layout settles and on DPI change. Hand-rolled placement from WPF `ActualHeight` put PortWatch
  under the taskbar on a 240 % display.
- Construct with `Left = Top = -10000` and `WindowInteropHelper.EnsureHandle()` so the first show neither flashes at
  WPF's default position nor pays window-creation cost mid-click.
- Refresh data on open (scan when shown), not on a timer, unless the content is live. Live data (PortWatch traffic) runs a
  1 s timer and any collector **only while the flyout is open** — start on show, stop on hide — so a closed tray app costs nothing.
- Cap height (PortWatch: 420 px) and scroll; never let a flyout exceed the work area.

## 6. The menu (right-click)

```csharp
var menu = TrayMenu.Create();
menu.Items.Add(TrayMenu.Header("App title"));              // optional, disabled
menu.Items.Add(TrayMenu.Action("Do a thing…", OnThing));
menu.Items.Add(TrayMenu.CheckItem("Some option", value, on => { Save(on); return true; }));   // false => reverts
foreach (var item in TrayMenu.RadioGroup(["Ports", "Processes"], selectedIndex, i => Save(i)))   // pick one of several
    menu.Items.Add(item);
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add(TrayMenu.StartAtLoginItem(TaskName, ExePath, Description, requireElevation: false, Logger.Log, "App"));
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add(TrayMenu.ExitItem(() => System.Windows.Application.Current.Shutdown()));
```

- Order: header → actions → options → separator → **Start at Login** → separator → **Exit**.
- Checkbox state is text (`✓ ` / three spaces), never the WinForms check margin (it breaks at high DPI).
- A checkbox callback that cannot persist the change returns `false`; the item reverts itself.
- A choice between exclusive options is a `RadioGroup` under a disabled `Header`, not two checkboxes. Apply it the next
  time the flyout opens (never rearrange an open flyout) and persist it in `%LOCALAPPDATA%\<App>\<App>.ini`.
- Labels that open a dialog end in `…`. Sentence case.

## 7. Visual design

Dark only; there is no light theme. One palette across apps:

| Use                     | Value                |
| ----------------------- | -------------------- |
| Background              | `#2d2d2d`            |
| Border                  | `#555555`, 1 px      |
| Text                    | `#e6e6e6` (primary), `#888888` (secondary), `#cccccc` (menu) |
| Accent / values         | `#7fc8ff`            |
| Warning                 | `#ffb454`            |
| Row highlight           | `#3b4a5e`, `#4f6680` (source) |
| Category (e.g. system)  | `#c39bff`            |
| Data in / data out      | `#6fcf97` (↓) / `#ff8fa3` (↑); both = white |

- Font **Segoe UI**: 12 px in WPF flyouts, 9 pt in WinForms.
- **A live flyout must not move.** Show changing state with colour only, never by adding or removing text: reserve a
  fixed slot for every state indicator (PortWatch lays out a `↓↑` pair for every process and port and just recolours it,
  transparent when idle) so widths, wrapping and scroll positions stay put. Don't show numbers that change every second.
  Transient indicators should **linger** (PortWatch holds an arrow 2 s after its traffic stops, per direction) so brief
  gaps don't strobe; keep the hold in a small pure class with an injectable clock so it is unit-testable.
  Cover it with a test that compares window size and full text before/after a state change.
- Never convey meaning by colour alone — pair it with a glyph/label (PortWatch: `+N` badges, a legend footer).
- Secondary information is dimmed, not smaller than 10 px. Padding 6–10 px. Keep dense lists to one line per item.
- Tray icon: a real `.ico` embedded as a resource with `SystemIcons.Application` as the fallback (AggressiveScreensaver does
  this; PortWatch still uses a stock system icon and is the one gap in the reference).

## 8. Make it verifiable without a mouse

Hover and tray clicks cannot be driven by an agent, so every app must expose a **headless path** that runs the real
exe (real manifest, DPI, dispatcher) and exits with 0/1, logging the detail. PortWatch.NET implements these flags;
copy them:

| Flag                     | Does                                                                                   |
| ------------------------ | -------------------------------------------------------------------------------------- |
| `--render out.png [arg]` | Render the flyout content to a PNG without showing it (`arg` selects a state, e.g. `UDP:5353`). Then **view the PNG**. |
| `--capture out.png`      | Post a genuine tray left-click message, then `PrintWindow` the live window over time.    |
| `--placement`            | Open at several cursor positions; fail if the window overlaps the taskbar.              |
| `--selftest`             | Run the real open path; fail on any exception.                                          |

- Build menus/flyouts from factories that take **data + callbacks** (null callbacks = render-only) so tests can
  construct them without a running `TrayApp`.
- Keep logic in **pure functions** (grouping, formatting, placement maths) with unit tests; mock data, never system calls.
- Tests that touch WPF/WinForms run on an **STA thread** and pump with `Application.DoEvents()`.
- The shared pieces have tests in `PortWatch.NET.Tests/SharedTrayPatternTests.cs` and `TrayPopupPlacementTests.cs`.
- Always **look at the rendered PNG** after a UI change; "it builds" is not verification.

## 9. Pitfalls that already cost time

- `Window.Show()` pumps messages: any event that can fire during it (tray mouse moves) must not re-enter it.
- WPF `ActualWidth/ActualHeight` are DIPs and are wrong until the window has been placed on its target monitor.
- After a click-away the tray click arrives *after* `Deactivated`; without `ToggleShow`'s debounce the flyout reopens.
- `Activate()` from a non-foreground process is ignored, so a headless flyout may close at once; that is OS
  behaviour, not a bug.
- `System.Drawing` vs `System.Windows.Media` (`Brush`, `Color`, `Point`, `Size`) are ambiguous when both are imported; qualify.
- `RenderTargetBitmap` ignores a `Window.Background`; put the background on the root element you render.
- Exceptions thrown inside `NotifyIcon` events bypass the WPF dispatcher handler; only the WinForms `ThreadException`
  handler (section 4) sees them.
- Driving edits through an inline shell heredoc with non-ASCII characters on Windows corrupts or truncates files;
  write a script file, open files as UTF-8, or use the edit tools.

## 10. New app checklist

1. Project + tests + sln + gitignore (section 3). 2. `App.xaml.cs` (section 4). 3. `TrayApp` with `NotifyIcon`,
`TrayMenu` (section 6) and a `TrayFlyoutWindow` (section 5). 4. Tooltip text. 5. Logger under `%LOCALAPPDATA%`.
6. Headless flags (section 8) and tests. 7. Render the PNG, run `--placement`, run the tests. 8. Only then commit.
