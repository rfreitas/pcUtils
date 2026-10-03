# SteamDxvk.NET

Scans installed Steam games, shows which graphics APIs each one uses, and swaps D3D8/9/10/11 for Vulkan by
installing [DXVK](https://github.com/doitsujin/dxvk) next to the game exe, optionally with async shader compilation
([dxvk-gplasync](https://gitlab.com/Ph42oN/dxvk-gplasync)). A normal window app (not a tray app), but it follows
`Shared.NET/TRAY_APP_UX.md` for palette, font, logging, single-instance and headless flags.

## Verify without a mouse

```
SteamDxvk.exe --render out.png [game]   render the window to a PNG (game = name fragment to select). LOOK AT THE PNG.
SteamDxvk.exe --scan out.txt            library scan table
SteamDxvk.exe --fetch                   real download + extraction of both DXVK builds
SteamDxvk.exe --selftest                open the window, scan, open every dropdown; exit 0/1
SteamDxvk.exe --launch <game> [mode]    the real Launch button path (a mode is saved for the game); starts the game
SteamDxvk.exe --check <game>            what DXVK's logs say about the last run
SteamDxvk.exe --detect <game>           one poll of real processes: is it running, on which API, and why we think so
SteamDxvk.exe --assess <game> <from> <to>   judge a past run (local times) with the real crash detector; records nothing
```
Results of the flags that don't write a file go to the log (`%LOCALAPPDATA%\SteamDxvk\SteamDxvk.log`).

Build and test with `& .\CommonScripts\BuildDotNetApp.ps1 -ProjectDir SteamDxvk.NET` from **pwsh**. Under Windows PowerShell 5.1 that
script fails to parse (an em dash in a string, no BOM), which is a repo problem, not this app.

State lives in `%LOCALAPPDATA%\SteamDxvk\`: `SteamDxvk.log`, `scan-cache.json`, `builds\{official|async}-<tag>\`.

## Structure

`Services/` is all logic, UI-free and unit-tested with mock data (synthetic PE files built in memory, temp folders):
`PeInspector` (PE headers/imports/strings), `Vdf` + `SteamLibrary` (Steam files), `GameScanner` (+`ScanCache`),
`DxvkInstaller` (file operations only; the build dir is passed in), `DxvkBuilds` (download/extract/cache),
`InstallPlanner` (what the UI may offer and warn about), `LibraryScanner` (shared by the window and `--scan`/`--render`).
`MainWindow` is XAML + code-behind; `Theme.xaml` is merged by the window itself so tests need no `Application`.

## Decisions worth keeping

**Detection says what a game can use, not what it will use.** Evidence is the PE import + delay-import tables ("linked",
shown plain) and a string scan for DLL names ("mentioned", shown as `(+...)`). Unreal and Unity ship several renderers and
pick one at runtime, and a D3D9 import is often just PIX markers, so most games show several APIs. Packed exes can
under-report. The user fixes that with *Force API*. Getting the real answer needs a running game's loaded modules
(not implemented).

**The game exe is the biggest candidate, with engine exes first.** Ranking by "mentions D3D" put launchers on top
(EAC's `start_protected_game.exe` made ELDEN RING look like D3D11; `idTechLauncher.exe` made DOOM look like D3D11).
Unity stubs are ~600 KB, so the size floor is lower beside a `UnityPlayer.dll`. Bundled tools (`StreamingAssets`, ffmpeg) are skipped.

**The app chooses the API; it doesn't just guess it.** *Run as* (Default / D3D11 / D3D12 / Vulkan) is saved per game in
`settings.json` and applied at launch by passing the engine's own flag (`EngineFlags`: Unreal `-dx11`/`-dx12`/`-vulkan`,
Unity `-force-d3d11`/`-force-d3d12`/`-force-vulkan`). Only engines in that table offer modes; for others the dropdown has just
Default. A flag is passed with `steam.exe -applaunch <id> <flag>`, with no flag `steam://rungameid/<id>`. No Steam config is edited and
Steam isn't restarted. **Not `steam://run/<id>//<flag>/`:** Steam asks the user to confirm any launch with arguments that way
(`console_log.txt`: `LaunchApp waiting for user response to ShowGameArgs "-dx11"`, 9 to 22 s of waiting), while `-applaunch` with the same
flag went straight to `CreatingProcess` (checked with Session). The URL form is only the fallback if steam.exe isn't found. The flag
goes on a real command line, so `SteamLaunch.Command` accepts only one `-[A-Za-z0-9-]+` token. Not verified: how args combine with
launch options saved in Steam's UI.
**DXVK is one tick, and the DLLs are an implementation detail.** The panel has *Run as* (only for engines with launch flags) and a
*Use DXVK (translate to Vulkan)* checkbox. There are no Install/Uninstall/Apply buttons and no DLL picker. `InstallPlanner` picks the DLLs
from Run as and the detected APIs (D3D11 set for D3D10/D3D11, d3d9, d3d8+d3d9; when nothing at all was detected it installs both the D3D9 and
D3D11 sets, and the one the game uses takes effect).
**The tick is the user's remembered choice, not a mirror of the folder** (`GameSettings.UseDxvk`, null = never decided: follow what is
installed). `ReconcileAsync` makes the folder follow it, after a tick, an option change, a Run as change, and at Launch: DXVK in when it is
wanted *and* usable (installing, or re-applying when the options or needed DLLs differ), out otherwise. Tick installs (anti-cheat games
ask first), untick restores the originals and is a remembered "no" that nothing overrides. When DXVK can't be used (Run as D3D12 or
Vulkan, a D3D12-only, Vulkan-only or OpenGL-only game, no exe) the tick is **greyed but stays ticked**, the options stay visible but greyed
with their values, the folder is cleaned, and DXVK comes back by itself, with the same options, when it can be used again. Keeping the
tick and options on screen means the layout doesn't jump when Run as changes. An install from before choices were remembered is adopted as
"wanted". Ticking DXVK on a game that could start in D3D12 sets Run as D3D11 for it (where the engine has a flag). A DXVK installed by hand
is shown ticked and disabled: the app never touches what it didn't install. The diagnostic flags run `ReadOnly`, so selecting a game
there can't clean up its folder or write settings. DXVK never converts one API into another: a D3D9/D3D11 game keeps using that API and
DXVK translates it to Vulkan. A one-line sentence under the title says so in plain words (`PlanSummary`).
**An install that fails part-way (a DLL locked by the running game) rolls back**, restoring backed-up originals. Without that the leftover
DLLs, having no manifest, were mistaken for a hand-made DXVK install and never cleaned up.
This came from a real crash: Session (UE 4.27) defaulted to D3D12 and died with `DXGI_ERROR_UNSUPPORTED` because DXVK's
`dxgi.dll` can't create a D3D12 swap chain. Launched with Run as D3D11 the same game ran with DXVK on the Vulkan device
(its command line was `SessionGame -dx11`). `LaunchPlanner` refuses Run as D3D12 while DXVK is installed and warns when a
D3D12-capable game is left on Default. `Check last run` reads DXVK's `<exe>_d3d11.log`/`_dxgi.log` (only the last process's
section, only files newer than the launch) to say which API the game actually used. That is the ground truth the static scan can't give.

**Running games are watched (`GameMonitor`, polled every 2 s on a worker thread).** Processes whose exe is under a scanned game folder are a
session. The real API comes from `ApiDetector`, which treats a DLL being *loaded* as weak evidence (Session on `-vulkan` still loads DXVK's
`d3d11.dll`, because the exe links it statically) and a device being *created* as proof: `D3D12Core.dll` loaded, or DXVK writing a fresh
`<exe>_d3d11.log`. Without that proof it says "(likely)". A run is compared with what the app asked for (`Mismatch`): runs started from the
app's Launch button carry the Run as that was requested, anything else was started without a flag and counts as Default. Rows get a green
dot (amber `⚠` on a mismatch) and the panel shows "Running · <API>".

**Crashes are remembered per setup (`RunHistory`, `history.json`).** A setup is Run as + DXVK state (`ConfigKey`: e.g. `D3D12|dxvk-async-gplAuto`), because
D3D12 without DXVK is a different thing from D3D12 with it. When a session ends `CrashDetector` judges it: an Unreal `CrashContext.runtime-xml`
written during the run, then a Windows Application Error event for the exe, then DXVK's log showing a D3D12 device, then a run shorter than 45 s
with none of those = `ExitedEarly` (Session on `-vulkan`: quit after 20 s with no crash report at all). Verified on three real Session runs.
The Run as dropdown colours entries whose latest run was bad (pink `✖ crashed`, amber `⚠ exited early`, always with a glyph and words), the
panel warns, and Launch becomes "Launch anyway". A later good run of the same setup clears it. Not done: runs from before the monitor existed
aren't backfilled, and a Steam-launched run is judged as the Default setup.

**DXVK replaces `dxgi.dll`, so it displaces any mod loader that uses that name** (Session had UE4 Plugin Loader there; it is
backed up and restored on uninstall, but its mods don't load while DXVK is installed). Not yet warned about in the UI.

**D3D12 is never installable.** DXVK can't translate it. If a game has D3D11 *and* D3D12 it may default to D3D12 and fail to
start once DXVK's `dxgi.dll` is in place, so the UI warns and the button says "anyway". Anti-cheat (EAC, BattlEye, ...) is
flagged the same way; the gplasync author says async may trip it. Warnings are inline (visible in a render), not modal.

**Install is reversible and never destroys data.** Existing same-named DLLs go to `.dxvk-manager-backup\` and come back on
uninstall; `dxvk-manager.json` records what was installed with SHA-256s; a file whose hash changed since install (Steam verify,
another tool) is left alone. `dxvk.conf` edits live in a marked block, so the user's lines survive and uninstall restores the
file byte-for-byte (including CRLF). The config is written next to the exe **and** in the game root, because DXVK reads it
from the process working directory and which one that is depends on how the game launches.

**Builds come from release APIs, not hard-coded URLs:** GitHub `doitsujin/dxvk` latest, GitLab `Ph42oN/dxvk-gplasync` newest
release. Extraction only writes members shaped exactly `<top>/<x32|x64>/<known dll>`; anything else in the archive is ignored.
Official DXVK has no true async (it uses Vulkan's graphics-pipeline-library, `dxvk.enableGraphicsPipelineLibrary`);
`dxvk.enableAsync` exists only in gplasync.

**Layout:** every list column but the last is fixed-width, and the last flexes, so header and row columns line up whatever
width the scrollbar takes (a star column in the middle shifted everything after it). Covered by a test.
