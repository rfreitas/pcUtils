#Requires AutoHotkey v2.0
#SingleInstance Force
#Include "..\Modules\VerticalSlider\index.ahk"

; =======================
; LG TV Backlight Hotkeys
; =======================
; Purpose:
;   - Adjust LG TV Backlight via lgwebos-cli (command line tool)
;   - Provide a visual slider overlay
;   - Support Hotkeys (Ctrl+Win+Up/Down)
;   - Support Tray Icon interaction (Click to show slider)
;   - Sync state with TV periodically
;
; Dependencies:
;   - LGTV Companion CLI (LGTVcli.exe)
; =======================

; =======================
; CONFIGURATION
; =======================
cli := "C:\Program Files\LGTV Companion\LGTVcli.exe" ; Path to CLI tool
step := 10                  ; Step size for brightness adjustments
minIntervalMs := 90         ; Throttle for key repeat (keyboard hotkeys)
syncEveryMs := 15000        ; Interval to re-read actual value from TV
iniFile := A_ScriptDir "\LGTV_brightness.ini"
autoBrightness := IniRead(iniFile, "Settings", "AutoBrightness", 1) ; Default to 1 (true)
hotkeyUp := IniRead(iniFile, "Settings", "HotkeyUp", "^#Up")
hotkeyDown := IniRead(iniFile, "Settings", "HotkeyDown", "^#Down")


; =======================
; GLOBAL STATE
; =======================
last := 0                   ; Timestamp of last hotkey action
cur := 50                   ; Current tracked brightness value (0-100)
lastSync := 0               ; Timestamp of last sync from TV
lastRefreshRate := 0        ; Previous refresh rate for ratio calculation

; Icon cache settings
iconsDir := A_Temp "\lgtv_icons"
iconsReady := false
hoverLastSync := 0          ; Timestamp of last sync triggered by hover
hoverMinInterval := 5000    ; Minimum ms between hover syncs


; =======================
; GUI SETUP (Using VerticalSlider Module)
; =======================
; Module handles: GUI styling, positioning, click-away dismissal
; Script handles: Value interpretation, throttling, debouncing, label formatting
brightnessSlider := VerticalSlider({
    title: "Backlight",
    min: 0,
    max: 100,
    onChange: OnSliderChange
})

; =======================
; EVENT HANDLERS
; =======================

pendingVal := 0  ; Latest value from slider

/**
 * Called when the slider is moved by the user.
 * Debounce-only: update label immediately, send to TV after user stops.
 */
OnSliderChange(rawVal, *) {
    global pendingVal, brightnessSlider
    
    ; Invert and store
    pendingVal := 100 - rawVal
    
    ; Update label immediately
    brightnessSlider.SetLabel(pendingVal)
    
    ; Debounce: send 150ms after last movement
    SetTimer(SendFinal, -150)
}

/**
 * Send the stored pending value (fires 150ms after last movement)
 */
SendFinal() {
    global pendingVal, cur
    
    ; Only send if different from known state
    if (pendingVal != cur) {
        ApplyToTV(pendingVal)
    }
}

/**
 * Shows the brightness slider (delegates to module after syncing state)
 */
ShowSlider(*) {
    global brightnessSlider, cur
    ; Update slider to match current value before showing
    brightnessSlider.SetRawValue(100 - cur)  ; Invert for display
    brightnessSlider.SetLabel(cur)
    brightnessSlider.Show()
}

/**
 * Hides the brightness slider (delegates to module)
 */
HideSlider(*) {
    global brightnessSlider
    brightnessSlider.Hide()
}

; =======================
; TRAY MENU & ICON
; =======================
A_TrayMenu.Delete()
valueLabel := "Backlight: (starting...)"
A_TrayMenu.Add(valueLabel, ShowSlider)
A_TrayMenu.Add()
A_TrayMenu.Add("Auto-Brightness (Refresh Rate)", ToggleAutoBrightness)
if (autoBrightness)
    A_TrayMenu.Check("Auto-Brightness (Refresh Rate)")
A_TrayMenu.Add("Sync from TV now", (*) => SyncFromTV(true))
A_TrayMenu.Add("Change Hotkeys…", ShowHotkeySettings)

A_TrayMenu.Add()
A_TrayMenu.Add("Exit", (*) => ExitApp())

; Handle left/right clicks on Tray Icon
OnMessage(0x404, TrayClick)
TrayClick(wParam, lParam, *) {
    global hoverLastSync, hoverMinInterval
    
    ; WM_MOUSEMOVE = 0x200
    if (lParam = 0x200) {
        if (A_TickCount - hoverLastSync > hoverMinInterval) {
            hoverLastSync := A_TickCount
            SetTimer(SyncFromTV, -1) ; Async call to avoid blocking UI thread
        }
        return
    }

    if (lParam = 0x202 || lParam = 0x205) {  ; WM_LBUTTONUP or WM_RBUTTONUP
        if (lParam = 0x202) { ; Left-click toggles slider
            ShowSlider()
            ; Sync immediately on click too, partially to update the slider we just showed
            SetTimer(SyncFromTV, -10)
        }
    }
}

A_IconTip := "LGTV Backlight: starting..."

; =======================
; HOTKEYS
; =======================
; Registered dynamically (not via `::`) so they can be changed at runtime
; from the "Change Hotkeys…" tray menu item.
AdjustUp(*) => AdjustBacklight("up")
AdjustDown(*) => AdjustBacklight("down")

/**
 * Settings window to record new Up/Down hotkeys.
 * Uses the native Gui "Hotkey" control (a real Windows common control) to
 * capture the combination — it reliably sees the Windows key, unlike a
 * plain script-level key hook. WM_SETRULES is sent to each control to lift
 * its default restriction against modifier-less (single-key) shortcuts.
 */
ShowHotkeySettings(*) {
    global hotkeyUp, hotkeyDown, iniFile

    hsGui := Gui("+AlwaysOnTop -Caption +ToolWindow +Border", "Hotkeys")
    hsGui.BackColor := "2d2d2d"
    hsGui.MarginX := 14
    hsGui.MarginY := 14

    hsGui.SetFont("s10 cffffff", "Segoe UI")
    hsGui.Add("Text", "w220 Center", "Brightness Hotkeys")

    hsGui.SetFont("s10 cffffff")
    hsGui.Add("Text", "w220 y+14", "Increase backlight:")
    upCtrl := hsGui.Add("Hotkey", "w220 y+4", hotkeyUp)
    AllowSingleKey(upCtrl)

    hsGui.Add("Text", "w220 y+10", "Decrease backlight:")
    downCtrl := hsGui.Add("Hotkey", "w220 y+4", hotkeyDown)
    AllowSingleKey(downCtrl)

    hsGui.SetFont("s9 cff8888")
    statusText := hsGui.Add("Text", "w220 y+10", "")
    statusText.Visible := false

    btnSave := hsGui.Add("Button", "w106 y+12", "Save")
    btnClose := hsGui.Add("Button", "w106 x+8 yp", "Close")

    btnSave.OnEvent("Click", SaveClick)
    btnClose.OnEvent("Click", (*) => hsGui.Destroy())
    hsGui.OnEvent("Escape", (*) => hsGui.Destroy())
    hsGui.OnEvent("Close", (*) => hsGui.Destroy())

    SaveClick(*) {
        global hotkeyUp, hotkeyDown, iniFile
        newUp := upCtrl.Value
        newDown := downCtrl.Value

        if (newUp = "" || newDown = "") {
            statusText.Text := "Both hotkeys must be set."
            statusText.Visible := true
            return
        }
        if (newUp = newDown) {
            statusText.Text := "Up and Down hotkeys must be different."
            statusText.Visible := true
            return
        }

        try {
            RebindHotkey(&hotkeyUp, newUp, AdjustUp)
            RebindHotkey(&hotkeyDown, newDown, AdjustDown)
        } catch as e {
            statusText.Text := "Could not register: " e.Message
            statusText.Visible := true
            return
        }

        IniWrite(hotkeyUp, iniFile, "Settings", "HotkeyUp")
        IniWrite(hotkeyDown, iniFile, "Settings", "HotkeyDown")
        hsGui.Destroy()
    }

    hsGui.Show()
}

/** Lifts the Hotkey control's default rule disallowing a modifier-less key. */
AllowSingleKey(ctrl) {
    DllCall("SendMessage", "Ptr", ctrl.Hwnd, "UInt", 0x102, "Ptr", 0, "Ptr", 0) ; WM_SETRULES
}

/** Unregisters oldCombo (if bound) and registers newCombo for callback, updating oldCombo by reference. */
RebindHotkey(&combo, newCombo, callback) {
    if (combo != "" && combo != newCombo)
        try Hotkey(combo, "Off")
    Hotkey(newCombo, callback)
    combo := newCombo
}

; =======================
; INITIALIZATION
; =======================
Hotkey(hotkeyUp, AdjustUp)
Hotkey(hotkeyDown, AdjustDown)
SyncFromTV(true) ; Initial sync
CheckRefreshRate() ; Initialize refresh rate
OnMessage(0x007E, OnDisplayChange) ; Listen for display changes (WM_DISPLAYCHANGE)
UpdateTray()

; =======================
; CORE LOGIC
; =======================

/**
 * Adjusts backlight up or down via Hotkey.
 * @param dir "up" or "down"
 */
AdjustBacklight(dir) {
    global cur, step, last, minIntervalMs

    now := A_TickCount
    if (now - last < minIntervalMs)
        return
    last := now

    ; Opportunistic sync (lazy)
    SyncFromTV(false)

    if (dir = "up")
        ApplyToTV(cur + step)
    else
        ApplyToTV(cur - step)
}

/**
 * Limits a value to a specific range.
 */
ClampVal(v, lo, hi) => v < lo ? lo : (v > hi ? hi : v)

/**
 * Updates internal state, tray icon, and sends command to TV.
 */
ApplyToTV(value) {
    global cur
    cur := ClampVal(value, 0, 100)
    UpdateTray()
    ; Send actual command
    RunCliCapture("-backlight " cur)
}

/**
 * Reads the current backlight setting from the TV.
 * @param force If true, ignores the time interval check.
 */
SyncFromTV(force := false) {
    global cur, lastSync, syncEveryMs, brightnessSlider
    now := A_TickCount
    if (!force && (now - lastSync < syncEveryMs))
        return

    r := RunCliCapture('-ok backlight -get_system_settings picture "[\"backlight\"]"')
    if (r.out != "") {
        try {
            cur := ClampVal(FirstInt(r.out), 0, 100)
            lastSync := now
        }
    }
    
    UpdateTray() ; Update icon/tooltip
    
    ; If slider is visible and NOT being dragged, update it
    if (brightnessSlider.IsVisible() && !GetKeyState("LButton", "P")) {
        brightnessSlider.SetRawValue(100 - cur)  ; Invert for display
        brightnessSlider.SetLabel(cur)
    }
}

/**
 * Updates the Tray Icon and Menu Text to match current value.
 */
UpdateTray() {
    global cur, valueLabel
    newLabel := "Backlight: " cur

    ; Update menu item text
    try A_TrayMenu.Rename(valueLabel, newLabel)
    valueLabel := newLabel

    ; Update hover tooltip
    A_IconTip := "LGTV Backlight: " cur

    ; Update dynamic icon
    UpdateIconFromValue(cur)
}

; =======================
; HELPER: CLI EXECUTION
; =======================
RunCliCapture(args) {
    global cli
    outFile := A_Temp "\lgtv_out.txt"
    errFile := A_Temp "\lgtv_err.txt"

    cmd := Format('{1} /c ""{2}" {3} 1> "{4}" 2> "{5}""'
        , A_ComSpec, cli, args, outFile, errFile)

    RunWait(cmd, , "Hide")

    out := FileExist(outFile) ? Trim(FileRead(outFile)) : ""
    err := FileExist(errFile) ? Trim(FileRead(errFile)) : ""
    return { out: out, err: err }
}

FirstInt(text) {
    if RegExMatch(text, "(\d+)", &m)
        return Integer(m[1])
    throw Error("Could not parse number from: " text)
}

; =======================
; HELPER: ICON GENERATION
; =======================
EnsureIcons() {
    global iconsDir, iconsReady
    if iconsReady
        return

    if !DirExist(iconsDir)
        DirCreate(iconsDir)

    ; Generate icons for 0, 10, 20... 100
    loop 11 {
        v := (A_Index - 1) * 10
        path := iconsDir "\b" v ".ico"
        if !FileExist(path)
            FileWriteIcoBar(path, v)
    }

    iconsReady := true
}

UpdateIconFromValue(val) {
    global iconsDir
    EnsureIcons()

    bucket := Round(val / 10) * 10
    if (bucket < 0)
        bucket := 0
    if (bucket > 100)
        bucket := 100

    targetPath := iconsDir "\b" bucket ".ico"
    if FileExist(targetPath)
        TraySetIcon(targetPath)
}

; =======================
; AUTO BRIGHTNESS (REFRESH RATE)
; =======================
ToggleAutoBrightness(*) {
    global autoBrightness, iniFile
    autoBrightness := !autoBrightness
    if (autoBrightness) {
        A_TrayMenu.Check("Auto-Brightness (Refresh Rate)")
        CheckRefreshRate() ; Run immediately if enabled
    } else {
        A_TrayMenu.Uncheck("Auto-Brightness (Refresh Rate)")
    }
    IniWrite(autoBrightness, iniFile, "Settings", "AutoBrightness")
}

OnDisplayChange(wParam, lParam, msg, hwnd) {
    ; Debounce slightly to allow display settings to settle
    SetTimer(CheckRefreshRate, -2000)
}

CheckRefreshRate() {
    global lastRefreshRate, cur, autoBrightness

    if (!autoBrightness)
        return
    
    currentRate := GetRefreshRate()
    if (currentRate > 0 && lastRefreshRate > 0 && currentRate != lastRefreshRate) {
        ratio := lastRefreshRate / currentRate
        newVal := Round(cur * ratio)
        ApplyToTV(newVal)
        lastRefreshRate := currentRate
    } else if (lastRefreshRate == 0 && currentRate > 0) {
        lastRefreshRate := currentRate
    }
}

GetRefreshRate() {
    DEVMODE := Buffer(220, 0)
    NumPut("Short", 220, DEVMODE, 68) ; dmSize
    
    ; EnumDisplaySettingsW(LPCWSTR lpszDeviceName, DWORD iModeNum, DEVMODEW *lpDevMode)
    ; iModeNum: -1 = ENUM_CURRENT_SETTINGS
    if DllCall("EnumDisplaySettingsW", "Ptr", 0, "Int", -1, "Ptr", DEVMODE) {
        ; dmDisplayFrequency is at offset 184
        return NumGet(DEVMODE, 184, "UInt")
    }
    return 0
}

/**
 * Generates a dynamic .ico file with a vertical progress bar.
 */
FileWriteIcoBar(path, value) {
    w := 16, h := 16

    ; Bar height 0..14
    barH := Floor((ClampVal(value, 0, 100) / 100) * 14)

    ; Pixel buffer (BGRA)
    pixels := Buffer(w * h * 4, 0)

    ; Colors
    outlineB := 200, outlineG := 200, outlineR := 200, outlineA := 255
    barB := 255, barG := 255, barR := 255, barA := 255

    SetPx(buf, x, y, b, g, r, a) {
        w := 16, h := 16
        yy := (h - 1 - y)
        off := (yy * w + x) * 4
        NumPut("UChar", b, buf, off + 0)
        NumPut("UChar", g, buf, off + 1)
        NumPut("UChar", r, buf, off + 2)
        NumPut("UChar", a, buf, off + 3)
    }

    ; Draw Box
    for x in [2,3,4,5,6,7,8,9,10,11,12,13] {
        SetPx(pixels, x, 1,  outlineB, outlineG, outlineR, outlineA)
        SetPx(pixels, x, 14, outlineB, outlineG, outlineR, outlineA)
    }
    Loop 14 {
        y := A_Index
        SetPx(pixels, 2,  y, outlineB, outlineG, outlineR, outlineA)
        SetPx(pixels, 13, y, outlineB, outlineG, outlineR, outlineA)
    }

    ; Fill Bar
    if (barH > 0) {
        yStart := 14 - barH
        y := yStart
        while (y <= 13) {
            Loop 10 {
                x := A_Index + 2
                SetPx(pixels, x, y, barB, barG, barR, barA)
            }
            y += 1
        }
    }

    ; Create Mask (Transparent parts)
    maskStride := 4
    mask := Buffer(maskStride * h, 0x00)

    Loop h {
        y := A_Index - 1
        rowOff := y * maskStride
        bits := 0
        Loop w {
            x := A_Index - 1
            yy := (h - 1 - y)
            off := (yy * w + x) * 4 + 3
            a := NumGet(pixels, off, "UChar")
            if (a = 0)
                bits |= (1 << (15 - x))
        }
        NumPut("UChar", (bits >> 8) & 0xFF, mask, rowOff + 0)
        NumPut("UChar", bits & 0xFF,        mask, rowOff + 1)
    }

    xorSize := w * h * 4
    andSize := maskStride * h
    bmpSize := 40 + xorSize + andSize
    fileSize := 6 + 16 + bmpSize

    ico := Buffer(fileSize, 0)
    ; Header
    NumPut("UShort", 0, ico, 0)
    NumPut("UShort", 1, ico, 2)
    NumPut("UShort", 1, ico, 4)
    ; DirEntry
    NumPut("UChar", 16, ico, 6)
    NumPut("UChar", 16, ico, 7)
    NumPut("UShort", 1, ico, 10)
    NumPut("UShort", 32, ico, 12)
    NumPut("UInt",  bmpSize, ico, 14)
    NumPut("UInt",  6 + 16, ico, 18)

    imgOff := 6 + 16
    NumPut("UInt", 40, ico, imgOff + 0)
    NumPut("Int",  16, ico, imgOff + 4)
    NumPut("Int",  32, ico, imgOff + 8) ; Height * 2
    NumPut("UShort", 1, ico, imgOff + 12)
    NumPut("UShort", 32, ico, imgOff + 14)
    NumPut("UInt", xorSize + andSize, ico, imgOff + 20)

    DllCall("RtlMoveMemory", "Ptr", ico.Ptr + imgOff + 40, "Ptr", pixels.Ptr, "UPtr", xorSize)
    DllCall("RtlMoveMemory", "Ptr", ico.Ptr + imgOff + 40 + xorSize, "Ptr", mask.Ptr, "UPtr", andSize)

    f := FileOpen(path, "w")
    f.RawWrite(ico, ico.Size)
    f.Close()
}
