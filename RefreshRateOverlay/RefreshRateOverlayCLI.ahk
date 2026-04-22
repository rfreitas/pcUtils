#Requires AutoHotkey v2.0
#SingleInstance Off

#Include "%A_ScriptDir%\..\Modules\HDRControl.ahk"

global INI_FILE := A_ScriptDir "\RefreshSettings.ini"

CLI_GetCurrentRefreshRate() {
    devMode := Buffer(220, 0)
    NumPut("UShort", 220, devMode, 68)
    if DllCall("EnumDisplaySettingsW", "Ptr", 0, "Int", -1, "Ptr", devMode)
        return NumGet(devMode, 184, "UInt")
    return 0
}

CLI_GetActiveApp() {
    try {
        procName := WinGetProcessName("A")
        winClass := WinGetClass("A")
        if (procName == "" || winClass == "Shell_TrayWnd" || winClass == "Shell_SecondaryTrayWnd" || winClass == "NotifyIconOverflowWindow")
            return "Desktop"
        return procName
    } catch {
        return "Desktop"
    }
}

; Mirrors ToggleOverlay() state computation without showing UI.
ComputeOverlayState(activeApp) {
    local state, hdrSupp, hdrEnabled, defaultHDR, defaultRate, hasProfile
    state := Map()
    state["ActiveApp"] := activeApp

    GetPrimaryHDRState(&hdrSupp, &hdrEnabled)
    state["HDRSupported"] := hdrSupp ? 1 : 0
    state["SystemHDR"]    := hdrEnabled ? 1 : 0

    defaultHDR  := 0
    defaultRate := 60
    try {
        defaultHDR := Integer(IniRead(INI_FILE, "Settings", "DefaultHDR"))
    } catch {
        defaultHDR := 0
    }
    try {
        defaultRate := Integer(IniRead(INI_FILE, "Settings", "DefaultRefreshRate"))
    } catch {
        defaultRate := 60
    }
    state["DefaultHDR"]  := defaultHDR
    state["DefaultRate"] := defaultRate

    hasProfile := false
    try {
        IniRead(INI_FILE, "Profiles", activeApp)
        hasProfile := true
    } catch {
        hasProfile := false
    }
    state["HasProfile"] := hasProfile ? 1 : 0

    if (hasProfile) {
        try {
            state["ProfileRate"] := Integer(IniRead(INI_FILE, "Profiles", activeApp))
        } catch {
            state["ProfileRate"] := "none"
        }
        try {
            state["ProfileHDR"] := Integer(IniRead(INI_FILE, "HDRProfiles", activeApp))
        } catch {
            state["ProfileHDR"] := "none"
        }
    }

    state["CurrentRate"] := CLI_GetCurrentRefreshRate()
    state["OverlayHDR"]  := state["SystemHDR"]
    state["OverlayRate"] := state["CurrentRate"]
    state["HDRSynced"]   := (state["OverlayHDR"] == state["SystemHDR"]) ? 1 : 0

    return state
}

PrintState(state) {
    local key, val
    for key, val in state
        FileAppend(key ": " val "`n", "*")
}

; --- CLI entry point ---

if (A_Args.Length == 0) {
    FileAppend("Usage: RefreshRateOverlayCLI.ahk <command> [args]`n", "*")
    FileAppend("  status                     Current system + INI state`n", "*")
    FileAppend("  overlay-state [appname]    What the overlay would show`n", "*")
    FileAppend("  set-default-hdr <0|1>      Write DefaultHDR to INI`n", "*")
    FileAppend("  test-hdr-sync              Run HDR sync test`n", "*")
    ExitApp(0)
}

cmd := StrLower(A_Args[1])

if (cmd == "status") {
    GetPrimaryHDRState(&hdrSupp, &hdrEnabled)
    FileAppend("SystemHDR: "    (hdrEnabled ? 1 : 0) "`n", "*")
    FileAppend("HDRSupported: " (hdrSupp ? 1 : 0) "`n", "*")
    defaultHDR  := 0
    defaultRate := 60
    try {
        defaultHDR := IniRead(INI_FILE, "Settings", "DefaultHDR")
    } catch {
        defaultHDR := 0
    }
    try {
        defaultRate := IniRead(INI_FILE, "Settings", "DefaultRefreshRate")
    } catch {
        defaultRate := 60
    }
    FileAppend("DefaultHDR: "  defaultHDR "`n", "*")
    FileAppend("DefaultRate: " defaultRate "`n", "*")
    FileAppend("CurrentRate: " CLI_GetCurrentRefreshRate() "`n", "*")
    ExitApp(0)
}

if (cmd == "overlay-state") {
    activeApp := (A_Args.Length >= 2) ? A_Args[2] : CLI_GetActiveApp()
    state := ComputeOverlayState(activeApp)
    PrintState(state)
    ExitApp(0)
}

if (cmd == "set-default-hdr") {
    if (A_Args.Length < 2) {
        FileAppend("Usage: set-default-hdr <0|1>`n", "*")
        ExitApp(1)
    }
    val := A_Args[2]
    IniWrite(val, INI_FILE, "Settings", "DefaultHDR")
    FileAppend("DefaultHDR set to " val "`n", "*")
    ExitApp(0)
}

if (cmd == "test-hdr-sync") {
    FileAppend("=== HDR sync test ===`n", "*")

    GetPrimaryHDRState(&hdrSupp, &ignored)
    if (!hdrSupp) {
        FileAppend("SKIP: HDR not supported`n", "*")
        ExitApp(0)
    }

    GetPrimaryHDRState(&s, &originalHDR)
    FileAppend("OriginalHDR: " (originalHDR ? 1 : 0) "`n", "*")

    SetHDRState(1)
    IniWrite(1, INI_FILE, "Settings", "DefaultHDR")
    FileAppend("SetHDR: 1`n", "*")

    SetHDRState(0)
    FileAppend("SetHDR: 0`n", "*")

    state := ComputeOverlayState("Desktop")
    FileAppend("SystemHDR: "  state["SystemHDR"] "`n", "*")
    FileAppend("OverlayHDR: " state["OverlayHDR"] "`n", "*")

    if (state["OverlayHDR"] == state["SystemHDR"]) {
        FileAppend("PASS: overlay matches system HDR state`n", "*")
    } else {
        FileAppend("FAIL: overlay=" state["OverlayHDR"] " system=" state["SystemHDR"] "`n", "*")
    }

    SetHDRState(originalHDR)
    IniWrite(originalHDR ? 1 : 0, INI_FILE, "Settings", "DefaultHDR")
    FileAppend("Restored DefaultHDR: " (originalHDR ? 1 : 0) "`n", "*")
    ExitApp(0)
}

FileAppend("Unknown command: " A_Args[1] "`n", "*")
ExitApp(1)
