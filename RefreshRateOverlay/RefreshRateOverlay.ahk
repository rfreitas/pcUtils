#Requires AutoHotkey v2.0
#SingleInstance Force

; Globals
global INI_FILE := A_ScriptDir "\RefreshSettings.ini"
global DefaultRefreshRate := 60
global CurrentRefreshRate := 0 ; 0 means unknown initially
global AvailableRates := []
global GuiInstance := ""
global DDL_Rates := ""
global Check_Remember := ""
global Text_ActiveApp := ""
global LastForegroundProcess := ""

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

; Start tracking foreground apps
SetTimer(TrackForegroundApp, 500)

; Hotkey to toggle the overlay
#+r::ToggleOverlay()

; --- Core Functions ---

TrackForegroundApp() {
    global LastForegroundProcess, GuiInstance, DefaultRefreshRate
    
    ; If overlay is active/focused, do not change state
    if (GuiInstance && WinActive("ahk_id " GuiInstance.Hwnd))
        return

    processName := ""
    try {
        processName := WinGetProcessName("A")
    } catch {
        return
    }
    
    ; Ignore the Windows Shell Experience Host or standard AHK windows if needed
    if (processName == "")
        return
        
    if (processName != LastForegroundProcess) {
        LastForegroundProcess := processName
        ApplyProfile(processName)
    }
}

ApplyProfile(processName) {
    global DefaultRefreshRate
    
    ; Does this app have a profile?
    try {
        profileRate := IniRead(INI_FILE, "Profiles", processName)
        SetMonitorRefreshRate(profileRate)
    } catch {
        ; If no profile, revert to the Default Refresh Rate
        SetMonitorRefreshRate(DefaultRefreshRate)
    }
}

; --- UI Functions ---

ToggleOverlay() {
    global GuiInstance, DDL_Rates, Check_Remember, Text_ActiveApp, AvailableRates
    global DefaultRefreshRate, INI_FILE, CurrentRefreshRate
    
    if (GuiInstance) {
        GuiInstance.Destroy()
        GuiInstance := ""
        return
    }
    
    activeApp := ""
    try {
        activeApp := WinGetProcessName("A")
    } catch {
        activeApp := "Desktop"
    }
    
    GuiInstance := Gui("+AlwaysOnTop -Caption +ToolWindow +Border")
    GuiInstance.BackColor := "1E1E1E"
    GuiInstance.SetFont("s10 cWhite", "Segoe UI")
    
    GuiInstance.Add("Text", "w250 Center", "Refresh Rate Overlay")
    GuiInstance.SetFont("s9 cGray")
    GuiInstance.Add("Text", "w250 Center", "Active: " activeApp)
    GuiInstance.SetFont("s10 cWhite") ; Restore
    Text_ActiveApp := activeApp
    
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
    Check_Remember := GuiInstance.Add("CheckBox", "w250", "Save for " activeApp)
    
    ; Check if profile exists
    try {
        IniRead(INI_FILE, "Profiles", activeApp)
        Check_Remember.Value := 1
    }
    
    ; Set Default Button
    GuiInstance.Add("Button", "w250", "Set as Desktop Default").OnEvent("Click", SetDefaultBtn_Click)

    btnGroup := GuiInstance.Add("Text", "w250 Center") ; Spacer
    GuiInstance.Add("Button", "w120 x10 y+10 BackgroundBlue Default", "Apply").OnEvent("Click", ApplyBtn_Click)
    GuiInstance.Add("Button", "w120 x+10 yp", "Cancel").OnEvent("Click", CancelBtn_Click)
    
    ; Show without stealing focus if possible
    GuiInstance.Show("NoActivate")
}

SetDefaultBtn_Click(*) {
    global GuiInstance, DDL_Rates, DefaultRefreshRate, INI_FILE, Text_ActiveApp
    
    selRate := StrReplace(DDL_Rates.Text, " Hz", "")
    DefaultRefreshRate := selRate
    IniWrite(selRate, INI_FILE, "Settings", "DefaultRefreshRate")
    
    ; Apply instantly
    SetMonitorRefreshRate(selRate)
    
    GuiInstance.Destroy()
    GuiInstance := ""
}

ApplyBtn_Click(*) {
    global GuiInstance, DDL_Rates, Check_Remember, Text_ActiveApp, INI_FILE
    
    selRate := StrReplace(DDL_Rates.Text, " Hz", "")
    
    if (Check_Remember.Value == 1) {
        IniWrite(selRate, INI_FILE, "Profiles", Text_ActiveApp)
    } else {
        ; Remove profile if they unchecked it
        try {
            IniDelete(INI_FILE, "Profiles", Text_ActiveApp)
        }
    }
    
    SetMonitorRefreshRate(selRate)
    
    GuiInstance.Destroy()
    GuiInstance := ""
}

CancelBtn_Click(*) {
    global GuiInstance
    GuiInstance.Destroy()
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
    global CurrentRefreshRate
    
    ; Don't trigger display mode switch if we are already securely at that rate
    if (CurrentRefreshRate == rate)
        return
        
    devMode := Buffer(220, 0)
    NumPut("UShort", 220, devMode, 68)
    
    ; Populate with current settings to ensure we don't change resolution/color info
    if DllCall("EnumDisplaySettingsW", "Ptr", 0, "Int", -1, "Ptr", devMode) {
        NumPut("UInt", rate, devMode, 184)
        
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
    IconSizeX := DllCall("GetSystemMetrics", "Int", 49) ; SM_CXSMICON
    IconSizeY := DllCall("GetSystemMetrics", "Int", 50) ; SM_CYSMICON
    
    if (IconSizeX == 0)
        IconSizeX := 16
    if (IconSizeY == 0)
        IconSizeY := 16
    
    bmi := Buffer(40, 0)
    NumPut("UInt", 40, bmi, 0)
    NumPut("Int", IconSizeX, bmi, 4)
    NumPut("Int", -IconSizeY, bmi, 8) ; Top-down
    NumPut("UShort", 1, bmi, 12)     ; Planes
    NumPut("UShort", 32, bmi, 14)    ; BitCount
    
    pBits := 0
    hDC := DllCall("GetDC", "Ptr", 0, "Ptr")
    hBitmap := DllCall("CreateDIBSection", "Ptr", hDC, "Ptr", bmi, "UInt", 0, "Ptr*", &pBits, "Ptr", 0, "UInt", 0, "Ptr")
    
    hMemDC := DllCall("CreateCompatibleDC", "Ptr", hDC, "Ptr")
    hOldBitmap := DllCall("SelectObject", "Ptr", hMemDC, "Ptr", hBitmap, "Ptr")
    
    fontSize := -Floor(IconSizeY * 0.85) 
    
    ; 5 = CLEARTYPE_QUALITY, 600 = FW_SEMIBOLD
    hFont := DllCall("CreateFont", "Int", fontSize, "Int", 0, "Int", 0, "Int", 0, "Int", 600
                   , "UInt", 0, "UInt", 0, "UInt", 0, "UInt", 1
                   , "UInt", 0, "UInt", 0, "UInt", 5, "UInt", 0
                   , "Str", "Segoe UI Variable Text", "Ptr")
    hOldFont := DllCall("SelectObject", "Ptr", hMemDC, "Ptr", hFont, "Ptr")
    
    DllCall("SetTextColor", "Ptr", hMemDC, "UInt", 0xFFFFFF)
    DllCall("SetBkColor", "Ptr", hMemDC, "UInt", 0x000000)
    DllCall("SetBkMode", "Ptr", hMemDC, "Int", 2) ; OPAQUE
    
    rect := Buffer(16, 0)
    NumPut("Int", 0, "Int", 0, "Int", IconSizeX, "Int", IconSizeY, rect)
    DllCall("DrawText", "Ptr", hMemDC, "Str", textStr, "Int", -1, "Ptr", rect, "UInt", 0x20 | 0x01 | 0x04)
    
    ; Parse the 32-bit DIB section to apply Alpha based on pixel luminance
    Loop IconSizeX * IconSizeY {
        offset := (A_Index - 1) * 4
        red := NumGet(pBits + offset + 2, "UChar")
        NumPut("UChar", red, pBits + offset + 3)
    }
    
    DllCall("SelectObject", "Ptr", hMemDC, "Ptr", hOldFont)
    DllCall("DeleteObject", "Ptr", hFont)
    DllCall("SelectObject", "Ptr", hMemDC, "Ptr", hOldBitmap)
    DllCall("DeleteDC", "Ptr", hMemDC)
    DllCall("ReleaseDC", "Ptr", 0, "Ptr", hDC)
    
    hbmMask := DllCall("CreateBitmap", "Int", IconSizeX, "Int", IconSizeY, "UInt", 1, "UInt", 1, "Ptr", 0, "Ptr")
    
    ii := Buffer(A_PtrSize == 8 ? 32 : 20, 0)
    NumPut("Int", 1, ii, 0)
    NumPut("Ptr", hbmMask, ii, A_PtrSize == 8 ? 16 : 12)
    NumPut("Ptr", hBitmap, ii, A_PtrSize == 8 ? 24 : 16)
    
    hIcon := DllCall("CreateIconIndirect", "Ptr", ii, "Ptr")
    
    DllCall("DeleteObject", "Ptr", hBitmap)
    DllCall("DeleteObject", "Ptr", hbmMask)
    
    if (hIcon)
        TraySetIcon("HICON:" . hIcon)
}
