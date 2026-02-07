#Requires AutoHotkey v2.0
#SingleInstance Force
#Include VerticalSlider.ahk

; =======================
; ERROR LOGGING
; =======================
LogFile := A_ScriptDir "\AggressiveScreensaver.log"
LogMsg(msg) {
    try FileAppend(FormatTime(, "yyyy-MM-dd HH:mm:ss") ": " msg "`n", LogFile)
}

; Set up error handler
OnError(HandleError)
HandleError(exception, mode) {
    LogMsg("UNHANDLED ERROR: " exception.Message "`n    File: " exception.File "`n    Line: " exception.Line "`n    Extra: " exception.Extra "`n    Stack: " exception.Stack)
    return 0 ; Show default error message as well
}

LogMsg("Script starting... (Admin: " A_IsAdmin ")")

; Set custom tray icon
if FileExist(A_ScriptDir "\AggressiveScreensaver.png")
    TraySetIcon(A_ScriptDir "\AggressiveScreensaver.png")


; Request admin elevation for powercfg access
if (!A_IsAdmin) {
    try {
        Run('*RunAs "' A_AhkPath '" /restart "' A_ScriptFullPath '"')
        ExitApp()
    }
}

; ==============================================================================
; Power & Idle Monitor (Agent Idle)
; ==============================================================================
; LOGIC OVERVIEW:
;
; 1. IDLE COUNTERS (The three timers):
;    - Soft Idle (A_TimeIdle): 
;        Resets on ANY input (Physical OR Simulated). If this resets but 
;        Phys/Agent don't, an app is likely using a "jiggler" to trick Windows.
;    - Phys Idle (A_TimeIdlePhysical): 
;        Resets only on HARDWARE Keyboard/Mouse input. Ignores game controllers.
;    - True Idle (Agent Idle): 
;        The smart timer. It combines Keyboard, Mouse, and Controller (XInput/Joy).
;        - Filters out mouse vibrations/jiggles (< 5 pixels).
;        - Filters out controller stick drift (deadzones).
;        - This reflects the user's actual physical presence.
;
; 2. POWER REQUESTS (Detection):
;    - Uses 'powercfg /requests' to find apps blocking Windows sleep.
;    - SCREEN (DISPLAY): Apps saying "Don't turn off the monitor" (e.g. VLC).
;    - SLEEP (SYSTEM/AWAY): Apps saying "Don't let the PC sleep" (e.g. Audio).
;
; 3. SMART BLANKING (Force Blanking):
;    - When 'True Idle' exceeds 'blankThresholdSec' AND no apps are blocking
;      the SCREEN, a black multi-monitor overlay is shown.
;    - This protects TVs/monitors from burn-in without cutting the HDMI signal.
;    - Overlay dismisses instantly on any REAL input or if an app starts 
;      blocking the screen.
; ==============================================================================

; =======================
; CONFIGURATION & PERSISTENCE
; =======================
IniFile := A_ScriptDir "\AggressiveScreensaver.ini"
powerCheckMs := 5000         ; Check power requests every 5 seconds

; Load settings with fallback to default (30s)
blankThresholdSec := Number(IniRead(IniFile, "Settings", "BlankThreshold", 30))

/**
 * Persists current settings to INI file with 500ms debounce
 */
SaveSettings() {
    SetTimer(DoSave, -500)
    DoSave() {
        global blankThresholdSec, IniFile
        try {
            IniWrite(blankThresholdSec, IniFile, "Settings", "BlankThreshold")
            LogMsg("Settings saved: " blankThresholdSec "s")
        } catch as e {
            LogMsg("SaveSettings Error: " e.Message)
        }
    }
}

; =======================
; GLOBAL STATE
; =======================
blockingScreenApps := ""     ; Apps preventing screensaver (DISPLAY)
blockingSleepApps := ""      ; Apps preventing sleep (SYSTEM/AWAYMODE)
blacklistedApps := Map()     ; Set of apps to ignore for blocking
blacklistedApps.CaseSense := "Off"
historyApps := Map()         ; History of all blocking apps
historyApps.CaseSense := "Off"
lastActivityTime := A_TickCount
lastControllerState := Map() ; Track XInput controller states
lastJoyState := Map()        ; Track DirectInput joystick states
agentIdleSec := 0           ; Our own idle counter
lastMouseX := 0             ; Track mouse position for delta check
lastMouseY := 0
blackGuis := []              ; Storage for multi-monitor black overlays
isBlanked := false
lastDisplayChange := 0       ; Debounce for display change events
displayChangeDebounce := 500 ; ms - prevents rapid rebuilds during HDR/mode switches

; =======================
; POWER REQUEST DETECTION
; =======================

/**
 * Runs a command and captures output
 */
RunWaitOutput(cmd) {
    tmpFile := A_Temp "\ahk_power_monitor.txt"
    try {
        ; Use RunWait with 'Hide' to prevent window flashing
        if (FileExist(tmpFile))
            FileDelete(tmpFile)
            
        RunWait(A_ComSpec ' /c ' cmd ' > "' tmpFile '"', , "Hide")
        
        output := ""
        if FileExist(tmpFile) {
            output := FileRead(tmpFile)
            FileDelete(tmpFile)
        }
        return Trim(output)
    } catch as e {
        LogMsg("RunWaitOutput Error: " e.Message " (Cmd: " cmd ")")
        return ""
    }
}

/**
 * Gets list of apps preventing Windows screensaver via Power Request API
 * Requires admin privileges
 */
GetPowerRequests() {
    global blockingScreenApps, blockingSleepApps, historyApps, blacklistedApps, IniFile
    
    if (!A_IsAdmin) {
        blockingScreenApps := "(needs admin)"
        blockingSleepApps := ""
        return
    }
    
    output := RunWaitOutput("powercfg /requests")
    
    ; Parse the output to find apps with active requests
    screenApps := []
    sleepApps := []
    currentSection := ""
    
    for line in StrSplit(output, "`n", "`r") {
        ; Detect section headers: must start at column 1 and end with :
        ; This prevents indented description lines like "  Reason:" from being caught as headers
        if (RegExMatch(line, "^([A-Z]+):$", &match)) {
            currentSection := match[1]
            continue
        }

        line := Trim(line)
        if (line = "" || line = "None.") {
            continue
        }
        
        ; Check entries starting with [
        if (SubStr(line, 1, 1) = "[") {
            if (RegExMatch(line, "^\[([^\]]+)\]\s*(.*)$", &match)) {
                tag := match[1]
                rest := Trim(match[2])
                
                ; Extract filename from path
                ; Clean up entry name based on type
                entry := rest
                filename := rest
                
                if (tag = "DRIVER") {
                    ; For drivers, take the name before the Hardware ID (parentheses)
                    if (pos := InStr(rest, "(")) {
                        entry := Trim(SubStr(rest, 1, pos - 1))
                        filename := entry
                    }
                } else if (InStr(rest, "\")) {
                    ; For processes/files, take the filename
                    parts := StrSplit(rest, "\")
                    entry := parts[parts.Length]
                    filename := entry
                }
                
                ; Remove extension (.exe) for cleaner look in display
                displayEntry := RegExReplace(entry, "i)\.exe$", "")
                
                if (StrLen(displayEntry) > 15) {
                    displayEntry := SubStr(displayEntry, 1, 12) "..."
                }
                
                entryText := displayEntry " [" tag "]"
                
                if (currentSection = "DISPLAY") {
                    ; Only track history and blacklist for DISPLAY
                    if (!historyApps.Has(filename)) {
                        historyApps[filename] := true
                        try IniWrite("1", IniFile, "History", filename)
                    }
                    
                    if (!blacklistedApps.Has(filename)) {
                        screenApps.Push(entryText)
                    }
                } else if (currentSection = "SYSTEM" || currentSection = "AWAYMODE") {
                    sleepApps.Push(entryText)
                }
            }
        }
    }
    
    ; helper function to build string
    BuildList(arr) {
        if (arr.Length = 0) {
            return ""
        }
        str := ""
        for i, app in arr {
            if (i > 2) {
                str .= " +" (arr.Length - 2) " more"
                break
            }
            str .= app (i < arr.Length && i < 2 ? ", " : "")
        }
        return str
    }
    
    blockingScreenApps := BuildList(screenApps)
    blockingSleepApps := BuildList(sleepApps)
    
    UpdateTrayTip()
}
/**
 * Gets XInput controller state
 */
GetXInputState(controllerIndex) {
    static xinputDll := ""
    static funcPtr := 0
    
    if (xinputDll = "") {
        for dllName in ["xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll"] {
            try {
                xinputDll := DllCall("LoadLibrary", "Str", dllName, "Ptr")
                if (xinputDll) {
                    funcPtr := DllCall("GetProcAddress", "Ptr", xinputDll, "AStr", "XInputGetState", "Ptr")
                    if (funcPtr) {
                        break
                    }
                }
            }
        }
    }
    
    if (!funcPtr) {
        return Map()
    }
    
    stateBuffer := Buffer(16, 0)
    if (DllCall(funcPtr, "UInt", controllerIndex, "Ptr", stateBuffer, "UInt") != 0) {
        return Map()
    }
    
    state := Map()
    state["packet"] := NumGet(stateBuffer, 0, "UInt")
    state["buttons"] := NumGet(stateBuffer, 4, "UShort")
    state["leftTrigger"] := NumGet(stateBuffer, 6, "UChar")
    state["rightTrigger"] := NumGet(stateBuffer, 7, "UChar")
    state["thumbLX"] := NumGet(stateBuffer, 8, "Short")
    state["thumbLY"] := NumGet(stateBuffer, 10, "Short")
    state["thumbRX"] := NumGet(stateBuffer, 12, "Short")
    state["thumbRY"] := NumGet(stateBuffer, 14, "Short")
    return state
}

/**
 * Checks for meaningful controller activity
 */
HasControllerActivity() {
    global lastControllerState
    deadzone := 8000
    triggerThreshold := 30
    
    Loop 4 {
        idx := A_Index - 1
        state := GetXInputState(idx)
        
        if (state.Count = 0) {
            continue
        }
        
        keyName := "controller" idx
        if (!lastControllerState.Has(keyName)) {
            lastControllerState[keyName] := state
            continue
        }
        
        oldState := lastControllerState[keyName]
        if (state["packet"] != oldState["packet"]) {
            hasInput := false
            
            if (state["buttons"] != 0) {
                hasInput := true
            }
            if (state["leftTrigger"] > triggerThreshold || state["rightTrigger"] > triggerThreshold) {
                hasInput := true
            }
            if (Abs(state["thumbLX"]) > deadzone || Abs(state["thumbLY"]) > deadzone) {
                hasInput := true
            }
            if (Abs(state["thumbRX"]) > deadzone || Abs(state["thumbRY"]) > deadzone) {
                hasInput := true
            }
            
            lastControllerState[keyName] := state
            
            if (hasInput) {
                return true
            }
        }
    }
    return false
}

/**
 * Checks for generic Joystick activity (DirectInput)
 * e.g. DualSense, older gamepads
 */
HasJoystickActivity() {
    global lastJoyState
    joyDeadzone := 10 ; 0-100 scale for axes
    
    Loop 4 { ; Check first 4 joysticks
        joyID := A_Index
        joyName := joyID "Joy"
        
        ; Verify connection by checking name/info
        if (GetKeyState(joyName "Name") == "")
            continue
            
        ; Build current state map
        currentState := Map()
        
        ; Check Axes (X, Y, Z, R, U, V)
        hasInput := false
        axisList := ["X", "Y", "Z", "R", "U", "V"]
        for axis in axisList {
            val := GetKeyState(joyName axis)
            if (!IsNumber(val)) {
                val := 50
            }
            currentState[axis] := val
            
            ; Check simple deviation from center (approx 50)
            ; This is a rough activity check
            if (Abs(val - 50) > joyDeadzone) {
                 ; We don't mark 'hasInput' just for being off-center (drift)
                 ; We only check for CHANGE below
            }
        }
        
        ; Check POV (Hat switch)
        currentState["POV"] := GetKeyState(joyName "POV")
        
        ; Check Buttons 1-32 (Bitmap would be faster but AHK native is simple loop)
        ; To save perf, we'll just check if the state matches previous
        buttonMask := 0
        Loop 32 {
            if (GetKeyState(joyName A_Index))
                buttonMask |= (1 << (A_Index - 1))
        }
        currentState["Buttons"] := buttonMask
        
        ; Compare with valid last state
        if (!lastJoyState.Has(joyID)) {
             lastJoyState[joyID] := currentState
             continue
        }
        
        oldJoy := lastJoyState[joyID]
        
        ; Detect Changes
        if (currentState["Buttons"] != oldJoy["Buttons"]) 
            hasInput := true
        if (currentState["POV"] != oldJoy["POV"])
            hasInput := true
            
        ; Detect Axis Movement (Change > 2)
        for axis in axisList {
            if (Abs(currentState[axis] - oldJoy[axis]) > 2)
                hasInput := true
        }
        
        lastJoyState[joyID] := currentState
        
        if (hasInput)
            return true
    }
    return false
}

/**
 * Custom idle check that filters out 'jiggles' and small mouse moves
 */
UpdateAgentIdle() {
    global lastActivityTime, agentIdleSec, lastMouseX, lastMouseY, blockingScreenApps
    
    ; Setup hooks on first run to ensure A_TimeIdlePhysical works
    static hooksInstalled := false
    if (!hooksInstalled) {
        InstallKeybdHook()
        InstallMouseHook()
        MouseGetPos(&x, &y)
        lastMouseX := x
        lastMouseY := y
        hooksInstalled := true
    }
    
    ; Check Mouse Delta
    MouseGetPos(&currX, &currY)
    dist := Sqrt((currX - lastMouseX)**2 + (currY - lastMouseY)**2)
    
    ; Only reset if moved more than 5 pixels (ignore jiggles/vibration)
    if (dist > 5) {
        lastActivityTime := A_TickCount
        lastMouseX := currX
        lastMouseY := currY
    }
    
    ; Check Keyboard (using idle timer but only if it's very low, implies keypress)
    ; (A_TimeIdlePhysical resets on mouse too, so we rely on delta for mouse)
    if (A_TimeIdlePhysical < 50) {
        ; If mouse didn't move much but idle is low, it must be a keypress or click
        if (dist <= 5) {
             lastActivityTime := A_TickCount
        }
    }
    
    ; Reset timer on controller activity (XInput or Joystick)
    if (HasControllerActivity() || HasJoystickActivity()) {
        lastActivityTime := A_TickCount
    }
    
    agentIdleSec := Round((A_TickCount - lastActivityTime) / 1000)
    
    ; --- BLANKING LOGIC ---
    if (agentIdleSec >= blankThresholdSec && blockingScreenApps == "") {
        if (!isBlanked)
            ShowBlackOverlay()
    } else {
        if (isBlanked)
            RemoveBlackOverlay()
    }
}

/**
 * Internal helper to destroy all active black overlay windows
 */
DestroyBlackGuis() {
    global blackGuis
    for g in blackGuis {
        try {
            if (IsObject(g))
                g.Destroy()
        }
    }
    blackGuis := []
}

/**
 * Shows a black full-screen window on all monitors
 */
ShowBlackOverlay() {
    global blackGuis, isBlanked
    if (isBlanked)
        return
        
    LogMsg("Entering blanking mode...")
    
    ; Clean up any orphaned GUIs before starting to prevent leaks
    DestroyBlackGuis()

    try {
        count := MonitorGetCount()
        Loop count {
            try {
                MonitorGet(A_Index, &L, &T, &R, &B)
                
                ; Create a black window for each monitor
                g := Gui("+AlwaysOnTop -Caption +ToolWindow") ; Removed E0x20 (click-through) to block mouse
                g.BackColor := "Black"
                g.Show("x" L " y" T " w" (R-L) " h" (B-T) " NoActivate")
                blackGuis.Push(g)
            } catch as e {
                LogMsg("Error creating overlay for monitor " A_Index ": " e.Message)
            }
        }
        
        ; Only enter blanked state if we actually managed to create windows
        if (blackGuis.Length > 0) {
            DllCall("ShowCursor", "Int", 0)
            isBlanked := true
        }
    } catch as e {
        LogMsg("ShowBlackOverlay Critical Error: " e.Message)
        RemoveBlackOverlay() ; Defensive cleanup
    }
}

/**
 * Removes the black full-screen windows
 */
RemoveBlackOverlay() {
    global blackGuis, isBlanked, lastActivityTime
    
    ; Reset activity timer whenever we force cleanup
    ; This prevents immediate re-blanking after wake/resolution change
    lastActivityTime := A_TickCount

    ; If we have windows OR the flag is set, we must clean up
    if (blackGuis.Length == 0 && !isBlanked)
        return
        
    LogMsg("Leaving blanking mode. (Count: " blackGuis.Length ")")
    
    DestroyBlackGuis()
    
    ; Restore cursor
    DllCall("ShowCursor", "Int", 1)
    isBlanked := false
}

/**
 * Handles display change events (resolution, HDR toggle, refresh rate, monitor connect/disconnect)
 * SAFETY: Always unblank on display change to prevent "stuck" overlays or race conditions.
 */
OnDisplayChange(wParam, lParam, msg, hwnd) {
    LogMsg("Display change detected. Forcing cleanup.")
    RemoveBlackOverlay()
}

/**
 * Handles power events (Sleep, Wake, Hibernation)
 */
OnPowerMessage(wParam, lParam, msg, hwnd) {
    ; PBT_APMRESUMESUSPEND = 7, PBT_APMRESUMEAUTOMATIC = 18
    if (wParam = 7 || wParam = 18) {
        LogMsg("System wake detected. Forcing cleanup.")
        
        ; Repeatedly attempt cleanup to catch any delayed GUI creations during wake
        RemoveBlackOverlay()
        SetTimer(RemoveBlackOverlay, -500)  ; Try again in 500ms
        SetTimer(RemoveBlackOverlay, -2000) ; Try again in 2s
    }
}

; =======================
; TRAY MENU
; =======================
A_TrayMenu.Delete()
A_TrayMenu.Add("Power Request Monitor", (*) => {})
A_TrayMenu.Add()
A_TrayMenu.Add("Blacklist Apps...", ShowBlacklistGui)
A_TrayMenu.Add("Show Details (Debug)", ShowPowerRequests)
A_TrayMenu.Add()
A_TrayMenu.Add("Exit", (*) => ExitApp())

/**
 * Updates the tray icon tooltip with current power requests
 */
UpdateTrayTip() {
    global blockingScreenApps, blockingSleepApps
    
    ; Get idle timers
    idleSec := Round(A_TimeIdle / 1000)
    physIdleSec := Round(A_TimeIdlePhysical / 1000)
    
    tip := "True Idle: " agentIdleSec "s (Goal: " blankThresholdSec "s)`n"
    tip .= "Phys Idle: " physIdleSec "s`n"
    tip .= "Soft Idle: " idleSec "s`n`n"
    
    FormatBlocking(label, apps) {
        if (apps != "")
            return label ": " apps
        return label ": None"
    }
    
    tip .= FormatBlocking("SCREEN", blockingScreenApps) "`n"
    tip .= FormatBlocking("SLEEP", blockingSleepApps) "`n"
        
    A_IconTip := tip
}

ShowPowerRequests(*) {
    global blockingScreenApps, blockingSleepApps
    
    adminStatus := A_IsAdmin ? "YES" : "NO"
    output := RunWaitOutput("powercfg /requests")
    
    text := "Running as Admin: " adminStatus "`r`n"
         . "Screen Blocked: " (blockingScreenApps != "" ? blockingScreenApps : "None") "`r`n"
         . "Sleep Blocked: " (blockingSleepApps != "" ? blockingSleepApps : "None") "`r`n`r`n"
         . "Raw powercfg output:`r`n" (output != "" ? output : "(empty)")

    g := Gui(, "Power Requests Debug")
    g.SetFont("s9", "Consolas") ; Use monospace for better readability
    g.Add("Edit", "r20 w600 ReadOnly", text)
    g.Add("Button", "w80 Default", "Close").OnEvent("Click", (*) => g.Destroy())
    g.Show()
}

; =======================
; TIMEOUT SLIDER (Using VerticalSlider Module)
; =======================

; Define 10 fixed steps (Common timeouts in seconds)
timeoutSteps := [15, 30, 60, 120, 180, 300, 600, 900, 1200, 1800]

; Create the slider using the dumb view module
; Range 1-10 for our 10 steps. We invert: raw 1 (top) = highest step, raw 10 (bottom) = lowest step
timeoutSlider := VerticalSlider({
    title: "Timeout",
    min: 1,
    max: 10,
    onChange: OnTimeoutSliderChange
})

; Initialize slider position
timeoutSlider.SetRawValue(FindStepIndex(blankThresholdSec))
timeoutSlider.SetLabel(FormatTimeout(blankThresholdSec))

/**
 * Find the step index for a given timeout value (inverted for display)
 */
FindStepIndex(sec) {
    global timeoutSteps
    for i, sVal in timeoutSteps {
        if (sec <= sVal)
            return 11 - i  ; Invert: step 1 -> raw 10 (bottom), step 10 -> raw 1 (top)
    }
    return 1  ; Default to top (longest timeout)
}

/**
 * Format seconds to human readable time
 */
FormatTimeout(s) {
    if (s < 60)
        return s . "s"
    m := Floor(s / 60)
    rem := Mod(s, 60)
    return m . "m" . (rem > 0 ? " " . rem . "s" : "")
}

/**
 * Called when the timeout slider value changes (receives raw 1-10)
 */
OnTimeoutSliderChange(rawVal, *) {
    global blankThresholdSec, timeoutSteps, timeoutSlider
    
    ; Invert: raw 1 (top) -> step 10, raw 10 (bottom) -> step 1
    stepIdx := 11 - rawVal
    blankThresholdSec := timeoutSteps[stepIdx]
    
    ; Update label with formatted timeout
    timeoutSlider.SetLabel(FormatTimeout(blankThresholdSec))
    UpdateTrayTip()

    ; Save the new setting (debounced)
    SaveSettings()
}

/**
 * Show the timeout slider
 */
ShowTimeoutSlider(*) {
    global timeoutSlider, blankThresholdSec
    ; Update slider to match current value before showing
    timeoutSlider.SetRawValue(FindStepIndex(blankThresholdSec))
    timeoutSlider.SetLabel(FormatTimeout(blankThresholdSec))
    timeoutSlider.Show()
}

/**
 * Handle Tray Icon Messages
 */
TrayIconClick(wParam, lParam, msg, hwnd) {
    ; 0x202 = WM_LBUTTONUP
    if (lParam == 0x202) {
        ShowTimeoutSlider()
    }
}

; =======================
; START TIMERS
; =======================

; Set update timers
SetTimer(GetPowerRequests, powerCheckMs)  ; Check powercfg requests
SetTimer(UpdateTrayTip, 1000)            ; Update idle timers every second
SetTimer(UpdateAgentIdle, 100)           ; Check for input frequently

; Detect Tray Icon Clicks (Left click for slider)
OnMessage(0x404, TrayIconClick)
OnMessage(0x007E, OnDisplayChange)  ; WM_DISPLAYCHANGE - monitor/resolution/HDR changes
OnMessage(0x0218, OnPowerMessage)   ; WM_POWERBROADCAST - sleep/wake events

; Initial calls
GetPowerRequests()
UpdateTrayTip()

; Initial notification
TrayTip("Power Monitor Debug", "Hover tray icon to see idle timers and blocking apps.", 1)

; =======================
; BLACKLIST & HISTORY LOADING
; =======================

; Load blacklist and history from INI
LoadHistoryAndBlacklist() {
    global blacklistedApps, historyApps, IniFile
    try {
        ; Load History
        histSection := IniRead(IniFile, "History", , "")
        Loop Parse, histSection, "`n", "`r" {
            if (pos := InStr(A_LoopField, "=")) {
                key := SubStr(A_LoopField, 1, pos - 1)
                historyApps[key] := true
            }
        }
        
        ; Load Blacklist
        blSection := IniRead(IniFile, "Blacklist", , "")
        Loop Parse, blSection, "`n", "`r" {
            if (pos := InStr(A_LoopField, "=")) {
                key := SubStr(A_LoopField, 1, pos - 1)
                blacklistedApps[key] := true
            }
        }
    }
}
LoadHistoryAndBlacklist()

/**
 * Shows the blacklist configuration GUI
 */
ShowBlacklistGui(*) {
    global historyApps, blacklistedApps, IniFile
    
    g := Gui(, "Blacklist Apps (Check to Ignore)")
    g.SetFont("s9", "Segoe UI")
    g.Add("Text", "w400", "Checked apps will be IGNORED by this script.`n(They won't stop the black screen overlay)")
    
    ; Sort apps alphabetically
    sortedApps := []
    for appName, _ in historyApps {
        sortedApps.Push(appName)
    }
    
    str := ""
    for appName in sortedApps
        str .= appName "`n"
    str := Sort(str)
    sortedApps := StrSplit(Trim(str, "`n"), "`n")
    
    if (sortedApps.Length == 0 || (sortedApps.Length == 1 && sortedApps[1] == "")) {
        g.Add("Text",, "No blocking apps detected yet.")
    } else {
        for appName in sortedApps {
            if (appName == "")
                continue
            isBlacklisted := blacklistedApps.Has(appName) ? 1 : 0
            cb := g.Add("Checkbox", "w400 Checked" isBlacklisted, appName)
            cb.OnEvent("Click", ToggleBlacklist)
        }
    }
    
    g.Add("Button", "w80 Default", "Close").OnEvent("Click", (*) => g.Destroy())
    g.Show()
}

/**
 * Toggles the blacklist state of an app
 */
ToggleBlacklist(ctrl, *) {
    global blacklistedApps, IniFile
    appName := ctrl.Text
    isChecked := ctrl.Value
    
    if (isChecked) {
        blacklistedApps[appName] := true
        try IniWrite("1", IniFile, "Blacklist", appName)
    } else {
        if (blacklistedApps.Has(appName)) {
            blacklistedApps.Delete(appName)
            try IniDelete(IniFile, "Blacklist", appName)
        }
    }
    
    ; Force a refresh of power requests so the status updates immediately
    SetTimer(GetPowerRequests, -10)
}
