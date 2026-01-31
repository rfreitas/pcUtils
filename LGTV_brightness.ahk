#Requires AutoHotkey v2.0
#SingleInstance Force

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

; =======================
; GLOBAL STATE
; =======================
last := 0                   ; Timestamp of last hotkey action
cur := 50                   ; Current tracked brightness value (0-100)
lastSync := 0               ; Timestamp of last sync from TV
lastHideTime := 0           ; Timestamp when slider was last hidden (to prevent immediate reopen)

; Icon cache settings
iconsDir := A_Temp "\lgtv_icons"
iconsReady := false

; =======================
; GUI SETUP (SLIDER)
; =======================
; Create the GUI for the brightness slider (frameless, dark theme)
sliderGui := Gui("+AlwaysOnTop -Caption +Border +ToolWindow", "Backlight")
sliderGui.BackColor := "2d2d2d"
sliderGui.MarginX := 10
sliderGui.MarginY := 15

; Add value text label
sliderGui.SetFont("s9 ccccccc", "Segoe UI")
sliderGui.AddText("vLabelText w40 Center", "50")

; Add vertical slider
sliderGui.SetFont("s9", "Segoe UI")
; Note: Standard slider has 0 at top. We invert this logic (100 - Value) so Up=Brighter.
brightnessSlider := sliderGui.AddSlider("vSlider h150 w30 Range0-100 Vertical AltSubmit", 50)
brightnessSlider.OnEvent("Change", OnSliderChange)

sliderVisible := false
lastSent := 0 ; Timestamp for throttling slider CLI commands

; =======================
; EVENT HANDLERS
; =======================

/**
 * Called when the slider is moved by the user.
 * Throttles the actual TV commands to avoid flooding the connection.
 */
OnSliderChange(ctrl, *) {
    global cur, lastSent
    ; Invert value: GUI Slider 0 (Top) -> Brightness 100
    newVal := 100 - ctrl.Value
    
    ; Update text label immediately
    sliderGui["LabelText"].Value := newVal
    
    ; 1. Throttle: Send updates max once per 100ms
    elapsed := A_TickCount - lastSent
    if (elapsed > 100) {
        ApplyToTV(newVal)
        lastSent := A_TickCount
    }
    
    ; 2. Debounce: Schedule a final update to catch the end of drag
    SetTimer(SendToTV, -120)
}

/**
 * Delayed sender for the slider to ensure final value is sent.
 */
SendToTV() {
    global brightnessSlider, lastSent
    val := 100 - brightnessSlider.Value
    
    ; Only send if we haven't just sent it (via throttle)
    if (A_TickCount - lastSent > 20) { 
        ApplyToTV(val)
        lastSent := A_TickCount
    }
}

/**
 * Shows the brightness slider near the mouse cursor.
 * Positions itself intelligently above the taskbar.
 */
ShowSlider(*) {
    global sliderVisible, cur, brightnessSlider, lastHideTime
    
    ; Prevent reopening immediately after clicking tray icon to close
    if (A_TickCount - lastHideTime < 400)
        return
    
    if (sliderVisible) {
        HideSlider()
        return
    }
    
    ; Update UI to match current internal state
    brightnessSlider.Value := 100 - cur
    sliderGui["LabelText"].Value := cur
    
    ; Get mouse position
    CoordMode("Mouse", "Screen")
    MouseGetPos(&mx, &my)
    
    ; Calculate dimensions with DPI scaling
    dpiScale := A_ScreenDPI / 96
    guiW := Integer(60 * dpiScale)
    guiH := Integer(210 * dpiScale)
    
    ; --- Positioning Logic ---
    MonitorGetWorkArea(, , , , &workBottom)
    
    ; Determine effective screen bottom (handling auto-hide taskbars)
    isAutoHide := (workBottom >= A_ScreenHeight)
    minTaskbarHeight := Integer(48 * dpiScale) 

    ; Try to detect actual taskbar position
    try {
        WinGetPos(, &tbY, , &tbH, "ahk_class Shell_TrayWnd")
        if (tbY > 0 && tbY < A_ScreenHeight) {
            finalBottom := tbY
        } else {
            finalBottom := A_ScreenHeight - minTaskbarHeight
        }
    } catch {
        finalBottom := A_ScreenHeight - minTaskbarHeight
    }
    
    ; Safety margin for Auto-Hide
    if (isAutoHide && (A_ScreenHeight - finalBottom) < minTaskbarHeight) {
        finalBottom := A_ScreenHeight - minTaskbarHeight
    }
    
    ; Final coordinates: Center on mouse X, adhere to bottom limit
    xPos := mx - (guiW // 2)
    yPos := finalBottom - guiH
    
    ; Force AlwaysOnTop again just in case
    sliderGui.Opt("+AlwaysOnTop")
    sliderGui.Show("x" xPos " y" yPos " NoActivate")
    sliderVisible := true
}

; Close slider on Escape key
sliderGui.OnEvent("Escape", HideSlider)

/**
 * Hides the slider window and records the timestamp.
 */
HideSlider(*) {
    global sliderVisible, lastHideTime
    sliderGui.Hide()
    sliderVisible := false
    lastHideTime := A_TickCount ; Record time to prevent instant re-open
}

; Close slider when clicking outside of the GUI
#HotIf sliderVisible
~LButton::
~RButton::
{
    MouseGetPos(,, &targetHwnd)
    if (targetHwnd != sliderGui.Hwnd) {
        HideSlider()
    }
}
#HotIf

; =======================
; TRAY MENU & ICON
; =======================
A_TrayMenu.Delete()
valueLabel := "Backlight: (starting...)"
A_TrayMenu.Add(valueLabel, ShowSlider)
A_TrayMenu.Add()
A_TrayMenu.Add("Sync from TV now", (*) => SyncFromTV(true))
A_TrayMenu.Add()
A_TrayMenu.Add("Exit", (*) => ExitApp())

; Handle left/right clicks on Tray Icon
OnMessage(0x404, TrayClick)
TrayClick(wParam, lParam, *) {
    if (lParam = 0x202 || lParam = 0x205) {  ; WM_LBUTTONUP or WM_RBUTTONUP
        if (lParam = 0x202)  ; Left-click toggles slider
            ShowSlider()
    }
}

A_IconTip := "LGTV Backlight: starting..."

; =======================
; HOTKEYS
; =======================
^#Up::AdjustBacklight("up")      ; Ctrl + Win + Up
^#Down::AdjustBacklight("down")  ; Ctrl + Win + Down

; =======================
; INITIALIZATION
; =======================
SyncFromTV(true) ; Initial sync
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
    global cur, lastSync, syncEveryMs
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
