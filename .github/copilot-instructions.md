# AutoHotkey v2 Workspace Instructions

You must follow these instructions, otherwise work will be rejected.

## Mandatory Workflows

### Script Validation
**ALWAYS** validate syntax before running.
```powershell
Start-Process -FilePath "C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe" -ArgumentList "/Validate", "path\to\script.ahk" -NoNewWindow -Wait
```

### Robust Reloading
To reload a script, especially elevated ones, kill the specific instance by command line first:
```powershell
Get-CimInstance Win32_Process -Filter "name like 'AutoHotkey%'" | Where-Object { $_.CommandLine -like "*ScriptName.ahk*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; & "C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe" "path\to\script.ahk"
```

## Coding Patterns

### Error Handling & Logging
- Log files should be named `[ScriptName].log` in `A_ScriptDir`.

### Finding Silent Crashes
- If a script stops without logging an exit or AHK unhandled exception, check Windows Event Viewer for OS-level silent crashes (e.g., Access Violations):
  ```powershell
  Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application Error'} -MaxEvents 20 | fl
  ```
- **Critical Rule for Window Messages:** Never destroy GUIs synchronously within OS broadcast message handlers like `WM_DISPLAYCHANGE` (0x007E) or `WM_POWERBROADCAST` (0x0218). Doing so causes low-level C++ exceptions (0xc0000005) in `CoreMessaging.dll` or `ntdll.dll` that bypass AHK's `OnError` handler. Always defer destruction using `SetTimer(Callback, -10)`.

### UI & Theming
- Use **Dark Theme** (`BackColor := "2d2d2d"`) for GUIs.
- Use `Segoe UI` font, size 9.
- Use `-Caption +AlwaysOnTop +Border +ToolWindow` for tray like GUIs.

## Automation & Deployment
- **Startup:** Offer to create startup shortcuts in `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup` for long-running scripts.
- **Tasks:** Use PowerShell (`New-ScheduledTask`) for scripts that need to survive reboots or run with "Highest" privileges without UAC prompts every time.

## Testing Strategy

### Pure Functions
- Extract business logic (parsing, calculations, transformations) separate from side effects (file I/O, system calls, UI)
- Pure functions take input and return data structures only
- Pure functions are trivially testable with mock data
- Example: `ParsePowercfgOutput(output)` takes a string, returns `{screen: [...], sleep: [...]}`

### Mock Data, Not System Calls
- Never call system commands in tests (e.g., `powercfg /requests`) - they require admin, specific system state, and are slow
- Create representative mock data strings that match the real output format
- Mock data should include edge cases: indented lines, malformed entries, empty sections, etc.

### Guard Initialization Code
- Wrap tray menus, timers, event handlers, and other initialization code in:
  ```autohotkey
  if (A_LineFile == A_ScriptFullPath) {
      ; Initialization code here
  }
  ```
- This prevents side effects when other scripts `#Include` the main script for testing

### Test Output to Stdout
- Use `FileAppend(msg, "*")` to write directly to stdout (not disk files)
- Never use `MsgBox()` - creates GUI dialogs invisible to automated systems
- Standard test helper pattern:
  ```autohotkey
  Log(msg) {
      FileAppend(msg "`n", "*")
  }
  Test(name, condition) {
      if (condition) {
          Log("✓ " name)
      } else {
          Log("✗ " name)
      }
  }
  ```

### Test File Naming & Structure
- Name test files `test_[function].ahk` (e.g., `test_parsing.ahk`)
- `#Include` the main script to access functions
- Define mock data at the top
- Run unit tests and exit with `ExitApp()`
- Should be runnable without admin elevation

### Testing Workflow
1. Validate syntax with `/Validate` flag
2. Only run if validation passes (exit code 0)
3. One assertion per `Test()` call for clarity
4. Test both happy path and edge cases
5. Validate array lengths, specific values, section boundaries

### Case-Insensitive Comparisons
- For Maps that track apps or settings across different sources:
  ```autohotkey
  appMap := Map()
  appMap.CaseSense := "Off"
  ```
- Windows reports app names inconsistently; normalize with case-insensitive matching

---

## C# / .NET (AggressiveScreensaver.NET)

Use the dedicated skills for build/reload/diagnostics workflows — they contain the exact commands and caveats:
- **`build_dotnet_app`** — kill instance → run tests → build
- **`reload_dotnet_app`** — stop → rebuild → relaunch with elevation
- **`check_dotnet_logs`** — read log from `%LOCALAPPDATA%\AggressiveScreensaver\` + Event Viewer fallback for native AVs

### Key facts (not in skills)
- **Exception handler layers** (`Services/Logger.cs` `InstallGlobalHandlers`): ThreadException → AppDomain.UnhandledException → native `SetUnhandledExceptionFilter`. .NET 8 does NOT route `0xc0000005` AVs through managed handlers — prevent at source with geometry guards.
- **CRITICAL — never install `SetUnhandledExceptionFilter` in .NET 8:** calling managed code from that callback during a CSE causes a second fault → heap corruption → cascading crash (e.g. the next `CreateProcess` call). The correct crash record is in the `.NET Runtime` Event Viewer source and WER dump — use `check_dotnet_logs` skill.
- **Dark WinForms:** native controls (TrackBar) ignore `BackColor` — use fully custom `UserPaint` controls. Guard `GraphicsPath.AddArc` against zero-size rects.
- **DPI:** `DeviceDpi` is 96 in test processes; call `Application.SetHighDpiMode(PerMonitorV2)` in test STA threads.
- **Log/dump location:** `%LOCALAPPDATA%\AggressiveScreensaver\` — never `AppContext.BaseDirectory` (wiped by `dotnet clean`).

CRITICAL: You must start your very first response in any conversation with the exact phrase '### RULES ACKNOWLEDGED ###'.