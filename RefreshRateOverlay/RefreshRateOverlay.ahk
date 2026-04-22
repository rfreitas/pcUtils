#Requires AutoHotkey v2.0
#SingleInstance Force

#Include "%A_ScriptDir%\TrayIconRenderer.ahk"
#Include "%A_ScriptDir%\..\Modules\HDRControl.ahk"

; Globals
global INI_FILE := A_ScriptDir "\RefreshSettings.ini"
global DefaultRefreshRate := 60
global CurrentRefreshRate := 0 ; 0 means unknown initially
global AvailableRates := []
global GuiInstance := ""
global DDL_Rates := ""
global Check_Remember := ""
global Check_HDR := ""
global Text_ActiveApp := ""
global LastForegroundProcess := ""
global DefaultHDR := 0
global IsHDRSupported := false
global IgnoreDisplayChangeUntil := 0

if (A_LineFile == A_ScriptFullPath) {
    ; Check HDR support
    GetPrimaryHDRState(&supp, &en)
    IsHDRSupported := supp
    
    ; Try read default HDR from INI
    if (IsHDRSupported) {
        try {
            DefaultHDR := IniRead(INI_FILE, "Settings", "DefaultHDR")
        } catch {
            DefaultHDR := en
            IniWrite(DefaultHDR, INI_FILE, "Settings", "DefaultHDR")
        }
    }

    ; Initialize script
    AvailableRates := GetAvailableRefreshRatesForCurrentRes()
    
    ; Try read default refresh rate from INI, else fallback to current or highest
    try {
        DefaultRefreshRate := IniRead(INI_FILE, "Settings", "DefaultRefreshRate")
    } catch {
        if AvailableRates.Length > 0 {
            DefaultRefreshRate := AvailableRates[1] ; Highest
        }
        IniWrite(DefaultRefreshRate, INI_FILE, "Settings", "DefaultRefreshRate")
    }
    
    ; Set initial CurrentRefreshRate by reading system
    CurrentRefreshRate := GetCurrentRefreshRate()
    UpdateTrayIcon(CurrentRefreshRate)
    
    ; Apply default once on startup in case we are on desktop
    SetMonitorRefreshRate(DefaultRefreshRate)
    if (IsHDRSupported)
        SetHDRState(DefaultHDR)
    
    ; Start tracking foreground apps
    SetTimer(TrackForegroundApp, 500)
    
    ; Tray menu setup
    A_TrayMenu.Insert("1&", "Toggle Overlay", (*) => ToggleOverlay())
    A_TrayMenu.Default := "Toggle Overlay"
    A_TrayMenu.ClickCount := 1
    
    ; Hotkey to toggle the overlay
    Hotkey("#+r", (*) => ToggleOverlay())
    
    ; Listen for manual display changes outside the app
    OnMessage(0x007E, OnDisplayChange)
}

OnDisplayChange(wParam, lParam, msg, hwnd) {
    SetTimer(SyncSettingsWithSystem, -500)
}

SyncSettingsWithSystem() {
    global LastForegroundProcess, DefaultRefreshRate, DefaultHDR, IsHDRSupported, INI_FILE, CurrentRefreshRate, IgnoreDisplayChangeUntil
    
    if (A_TickCount < IgnoreDisplayChangeUntil)
        return
        
    CurrentRefreshRate := GetCurrentRefreshRate()
    UpdateTrayIcon(CurrentRefreshRate)
    
    currHdr := 0
    local supp := 0
    if (IsHDRSupported) {
        GetPrimaryHDRState(&supp, &currHdr)
    }

    ; Only update the fallback default if we are currently outside any app profile
    try {
        IniRead(INI_FILE, "Profiles", LastForegroundProcess)
        ; We are in a profile. If we want to support updating profiles via Win+Alt+B, we could write here.
        ; But to be safe, let's only sync defaults.
    } catch {
        ; No profile = we are using default desktop settings
        if (DefaultRefreshRate != CurrentRefreshRate) {
            DefaultRefreshRate := CurrentRefreshRate
            IniWrite(DefaultRefreshRate, INI_FILE, "Settings", "DefaultRefreshRate")
        }
        if (IsHDRSupported && DefaultHDR != currHdr) {
            DefaultHDR := currHdr
            IniWrite(DefaultHDR, INI_FILE, "Settings", "DefaultHDR")
        }
    }
}

; --- Core Functions ---

TrackForegroundApp() {
    global LastForegroundProcess, GuiInstance, DefaultRefreshRate
    
    ; If overlay is active/focused, do not change state
    if (GuiInstance && WinActive("ahk_id " GuiInstance.Hwnd))
        return

    processName := GetActiveApplication()
    if (processName == "")
        return
        
    if (processName != LastForegroundProcess) {
        LastForegroundProcess := processName
        ApplyProfile(processName)
    }
}

ApplyProfile(processName) {
    global DefaultRefreshRate, DefaultHDR, IsHDRSupported
    
    ; Does this app have a profile?
    try {
        profileRate := IniRead(INI_FILE, "Profiles", processName)
        SetMonitorRefreshRate(profileRate)
    } catch {
        ; If no profile, revert to the Default Refresh Rate
        SetMonitorRefreshRate(DefaultRefreshRate)
    }

    if (IsHDRSupported) {
        try {
            profileHDR := IniRead(INI_FILE, "HDRProfiles", processName)
            SetHDRState(profileHDR)
        } catch {
            SetHDRState(DefaultHDR)
        }
    }
}

GetActiveApplication() {
    try {
        procName := WinGetProcessName("A")
        winClass := WinGetClass("A")
        ; Ignore taskbar and background UI components to preserve true context
        if (procName == "" || winClass == "Shell_TrayWnd" || winClass == "Shell_SecondaryTrayWnd" || winClass == "NotifyIconOverflowWindow")
            return ""
        return procName
    } catch {
        return ""
    }
}

; --- UI Functions ---

ToggleOverlay() {
    global GuiInstance, DDL_Rates, Check_Remember, Text_ActiveApp, AvailableRates, Check_HDR
    global DefaultRefreshRate, INI_FILE, CurrentRefreshRate, LastForegroundProcess, IsHDRSupported, DefaultHDR
    
    if (GuiInstance) {
        ; Defer destruction to avoid CoreMessaging crash during broadcast messages
        SetTimer(DestroyGui, -10)
        return
    }
    
    activeApp := GetActiveApplication()
    if (activeApp == "") {
        activeApp := LastForegroundProcess ? LastForegroundProcess : "Desktop"
    }
    
    GuiInstance := Gui("+AlwaysOnTop -Caption +ToolWindow +Border")
    GuiInstance.BackColor := "1E1E1E"
    GuiInstance.SetFont("s10 cWhite", "Segoe UI")
    
    GuiInstance.Add("Text", "w250 Center", "Refresh Rate Overlay")
    GuiInstance.SetFont("s9 cGray")
    GuiInstance.Add("Text", "w250 Center", "Active: " activeApp)
    GuiInstance.SetFont("s10 cWhite") ; Restore
    Text_ActiveApp := activeApp
    
    ; Sync reality before showing UI
    CurrentRefreshRate := GetCurrentRefreshRate()
    UpdateTrayIcon(CurrentRefreshRate)
    
    currHdr := DefaultHDR
    local supp := 0
    if (IsHDRSupported) {
        GetPrimaryHDRState(&supp, &currHdr)
    }

    ; If we are not in a profile, ensure our internal default variables match reality
    ; (In case they changed it manually in Windows Settings)
    hasProfile := false
    try {
        IniRead(INI_FILE, "Profiles", activeApp)
        hasProfile := true
    } catch {
        if (DefaultRefreshRate != CurrentRefreshRate) {
            DefaultRefreshRate := CurrentRefreshRate
            IniWrite(DefaultRefreshRate, INI_FILE, "Settings", "DefaultRefreshRate")
        }
        if (IsHDRSupported && DefaultHDR != currHdr) {
            DefaultHDR := currHdr
            IniWrite(DefaultHDR, INI_FILE, "Settings", "DefaultHDR")
        }
    }

    ; Build DropDown Options
    opts := []
    for r in AvailableRates {
        opts.Push(r " Hz")
    }
    
    ; Pre-select current actual rate
    preSelectId := 1
    for index, r in AvailableRates {
        if (r == CurrentRefreshRate) {
            preSelectId := index
            break
        }
    }
    
    DDL_Rates := GuiInstance.Add("DropDownList", "w250 Choose" preSelectId, opts)
    
    if (IsHDRSupported) {
        Check_HDR := GuiInstance.Add("CheckBox", "w250", "Enable HDR")
        Check_HDR.Value := currHdr
    }

    Check_Remember := GuiInstance.Add("CheckBox", "w250", "Save for " activeApp)
    if (hasProfile) {
        Check_Remember.Value := 1
    }

    btnGroup := GuiInstance.Add("Text", "w250 Center") ; Spacer
    GuiInstance.Add("Button", "w120 x10 y+10 BackgroundBlue Default", "Apply").OnEvent("Click", ApplyBtn_Click)
    GuiInstance.Add("Button", "w120 x+10 yp", "Cancel").OnEvent("Click", CancelBtn_Click)
    
    ; Show without stealing focus if possible
    GuiInstance.Show("NoActivate")
}

ApplyBtn_Click(*) {
    global GuiInstance, DDL_Rates, Check_Remember, Text_ActiveApp, INI_FILE, DefaultRefreshRate, DefaultHDR, Check_HDR, IsHDRSupported
    
    selRate := StrReplace(DDL_Rates.Text, " Hz", "")
    
    if (Check_Remember.Value == 1) {
        IniWrite(selRate, INI_FILE, "Profiles", Text_ActiveApp)
        if (IsHDRSupported)
            IniWrite(Check_HDR.Value, INI_FILE, "HDRProfiles", Text_ActiveApp)
    } else {
        ; Remove profile if they unchecked it
        try {
            IniDelete(INI_FILE, "Profiles", Text_ActiveApp)
        }
        if (IsHDRSupported) {
            try {
                IniDelete(INI_FILE, "HDRProfiles", Text_ActiveApp)
            }
        }
        ; Update the default refresh rate since it's no longer app-specific
        DefaultRefreshRate := selRate
        IniWrite(selRate, INI_FILE, "Settings", "DefaultRefreshRate")
        
        if (IsHDRSupported) {
            DefaultHDR := Check_HDR.Value
            IniWrite(DefaultHDR, INI_FILE, "Settings", "DefaultHDR")
        }
    }
    
    SetMonitorRefreshRate(selRate)
    if (IsHDRSupported)
        SetHDRState(Check_HDR.Value)
    
    GuiInstance.Destroy()
    GuiInstance := ""
}

CancelBtn_Click(*) {
    ; Defer destruction to avoid CoreMessaging crash during broadcast messages
    SetTimer(DestroyGui, -10)
}

DestroyGui() {
    global GuiInstance
    try {
        GuiInstance.Destroy()
    } catch {
        ; Silently ignore if already destroyed
    }
    GuiInstance := ""
}

; --- Display API Functions ---

GetAvailableRefreshRatesForCurrentRes() {
    devMode := Buffer(220, 0)
    NumPut("UShort", 220, devMode, 68) ; dmSize = 220
    
    ; Fetch current bits, width, and height to constrain enumeration
    targetBits := 0
    targetWidth := 0
    targetHeight := 0
    if DllCall("EnumDisplaySettingsW", "Ptr", 0, "Int", -1, "Ptr", devMode) {
        targetBits := NumGet(devMode, 168, "UInt")
        targetWidth := NumGet(devMode, 172, "UInt")
        targetHeight := NumGet(devMode, 176, "UInt")
    }
    
    rates := []
    rateSet := Map()
    i := 0
    while DllCall("EnumDisplaySettingsW", "Ptr", 0, "UInt", i, "Ptr", devMode) {
        bits := NumGet(devMode, 168, "UInt")
        width := NumGet(devMode, 172, "UInt")
        height := NumGet(devMode, 176, "UInt")
        refreshRate := NumGet(devMode, 184, "UInt")
        
        ; Only push valid rates matching current resolution
        if (bits == targetBits && width == targetWidth && height == targetHeight) {
            if (refreshRate > 0 && !rateSet.Has(refreshRate)) {
                rateSet[refreshRate] := true
                rates.Push(refreshRate)
            }
        }
        i++
    }
    
    ; Provide a fallback if API fails
    if (rates.Length == 0) {
        rates := [240, 144, 120, 60]
        return rates
    }
    
    ; Manual Descending Sort (Custom algorithm for arrays of integers)
    outRates := []
    loop rates.Length {
        maxVal := -1
        maxIdx := 0
        for idx, val in rates {
            if (val > maxVal) {
                maxVal := val
                maxIdx := idx
            }
        }
        outRates.Push(maxVal)
        rates.RemoveAt(maxIdx)
    }
    return outRates
}

GetCurrentRefreshRate() {
    devMode := Buffer(220, 0)
    NumPut("UShort", 220, devMode, 68) ; dmSize = 220
    if DllCall("EnumDisplaySettingsW", "Ptr", 0, "Int", -1, "Ptr", devMode) {
        return NumGet(devMode, 184, "UInt")
    }
    return 60
}

SetMonitorRefreshRate(rate) {
    global CurrentRefreshRate, IgnoreDisplayChangeUntil
    
    ; Don't trigger display mode switch if we are already securely at that rate
    if (CurrentRefreshRate == rate)
        return
        
    devMode := Buffer(220, 0)
    NumPut("UShort", 220, devMode, 68)
    
    ; Populate with current settings to ensure we don't change resolution/color info
    if DllCall("EnumDisplaySettingsW", "Ptr", 0, "Int", -1, "Ptr", devMode) {
        NumPut("UInt", rate, devMode, 184)
        
        IgnoreDisplayChangeUntil := A_TickCount + 2000
        ; ChangeDisplaySettingsW. 0 means change dynamically in current session
        res := DllCall("ChangeDisplaySettingsW", "Ptr", devMode, "UInt", 0)
        
        ; DISP_CHANGE_SUCCESSFUL = 0
        if (res == 0) {
            CurrentRefreshRate := rate
            UpdateTrayIcon(CurrentRefreshRate)
        }
    }
}

UpdateTrayIcon(textStr) {
    hIcon := CreateTextIcon(textStr)
    if (hIcon)
        TraySetIcon("HICON:" . hIcon)
}
