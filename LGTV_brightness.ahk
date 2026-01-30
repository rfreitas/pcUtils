#Requires AutoHotkey v2.0
#SingleInstance Force

; =======================
; LG TV Backlight Hotkeys
; - No console popups
; - Ctrl+Win+Up/Down adjusts backlight
; - Tray tooltip + tray menu show value
; - Tray ICON changes (auto-generated) in steps of 10 (0..100)
; =======================

; ===== CONFIG =====
cli := "C:\Program Files\LGTV Companion\LGTVcli.exe"
step := 10
minIntervalMs := 90     ; debounce key-repeat
syncEveryMs := 15000    ; re-sync from TV occasionally

; State
last := 0
cur := 50
lastSync := 0

; Icon cache
iconsDir := A_Temp "\lgtv_icons"
iconsReady := false

; ----- Slider GUI -----
sliderGui := Gui("+AlwaysOnTop -Caption +Border +ToolWindow", "Backlight")
sliderGui.BackColor := "2d2d2d"
sliderGui.MarginX := 10
sliderGui.MarginY := 15

sliderGui.SetFont("s9 ccccccc", "Segoe UI")
sliderGui.AddText("vLabelText w40 Center", "50")

sliderGui.SetFont("s9", "Segoe UI")
brightnessSlider := sliderGui.AddSlider("vSlider h150 w30 Range0-100 Vertical ToolTip Invert", 50)
brightnessSlider.OnEvent("Change", OnSliderChange)

sliderVisible := false

OnSliderChange(ctrl, *) {
    global cur
    newVal := ctrl.Value
    sliderGui["LabelText"].Value := newVal
    ApplyToTV(newVal)
}

ShowSlider(*) {
    global sliderVisible, cur, brightnessSlider
    
    if (sliderVisible) {
        sliderGui.Hide()
        sliderVisible := false
        return
    }
    
    ; Update slider to current value
    brightnessSlider.Value := cur
    sliderGui["LabelText"].Value := cur
    
    ; Get mouse position for centering
    CoordMode("Mouse", "Screen")
    MouseGetPos(&mx, &my)
    
    ; DPI scaling factor
    dpiScale := A_ScreenDPI / 96
    
    ; GUI dimensions (scaled for DPI, converted to integer)
    guiW := Integer(60 * dpiScale)
    guiH := Integer(210 * dpiScale)
    
    ; Get taskbar height directly from taskbar window
    try {
        WinGetPos(, &taskbarY, , &taskbarH, "ahk_class Shell_TrayWnd")
    } catch {
        taskbarY := A_ScreenHeight - 48
        taskbarH := 48
    }
    
    ; Position: centered on mouse X, bottom of GUI at top of taskbar
    xPos := mx - (guiW // 2)
    yPos := taskbarY - guiH
    
    sliderGui.Show("x" xPos " y" yPos " NoActivate")
    sliderVisible := true
}

; Close slider on Escape
sliderGui.OnEvent("Escape", HideSlider)

HideSlider(*) {
    global sliderVisible
    sliderGui.Hide()
    sliderVisible := false
}

; ----- Tray menu setup -----
A_TrayMenu.Delete()
valueLabel := "Backlight: (starting...)"
A_TrayMenu.Add(valueLabel, ShowSlider)
A_TrayMenu.Add()
A_TrayMenu.Add("Sync from TV now", (*) => SyncFromTV(true))
A_TrayMenu.Add()
A_TrayMenu.Add("Exit", (*) => ExitApp())

; Click tray icon to show slider
OnMessage(0x404, TrayClick)
TrayClick(wParam, lParam, *) {
    if (lParam = 0x202 || lParam = 0x205) {  ; Left-click or right-click release
        if (lParam = 0x202)  ; Left-click shows slider
            ShowSlider()
    }
}

A_IconTip := "LGTV Backlight: starting..."

; ===== HOTKEYS =====
^#Up::AdjustBacklight("up")      ; Ctrl + Win + Up
^#Down::AdjustBacklight("down")  ; Ctrl + Win + Down

; ===== Startup sync/UI =====
SyncFromTV(true)
UpdateTray()

; =======================
; Core logic
; =======================

AdjustBacklight(dir) {
    global cur, step, last, minIntervalMs

    now := A_TickCount
    if (now - last < minIntervalMs)
        return
    last := now

    ; Keep in sync occasionally, but don't GET every keypress
    SyncFromTV(false)

    if (dir = "up")
        ApplyToTV(cur + step)
    else
        ApplyToTV(cur - step)
}

ApplyToTV(value) {
    global cur
    cur := ClampVal(value, 0, 100)
    UpdateTray()
    ; SET output may be noisy JSON (e.g. "Cannot relay luna response.") — ignore it.
    RunCliCapture("-backlight " cur)
}

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

UpdateTray() {
    global cur, valueLabel
    newLabel := "Backlight: " cur

    ; Rename the existing menu item label (most compatible)
    try A_TrayMenu.Rename(valueLabel, newLabel)
    valueLabel := newLabel

    ; Tooltip on hover
    A_IconTip := "LGTV Backlight: " cur

    ; Replace tray icon (bucketed to tens)
    UpdateIconFromValue(cur)
}

; =======================
; Running LGTVcli silently
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

ClampVal(v, lo, hi) => v < lo ? lo : (v > hi ? hi : v)

; =======================
; Tray icon generation
; =======================

EnsureIcons() {
    global iconsDir, iconsReady
    if iconsReady
        return

    if !DirExist(iconsDir)
        DirCreate(iconsDir)

    ; Generate 0..100 in steps of 10
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

    iconFile := iconsDir "\b" bucket ".ico"
    if FileExist(iconFile)
        TraySetIcon(iconFile)
}

; Writes a 16x16 32-bit ICO with a simple vertical bar.
; Transparent background, light outline, white fill.
FileWriteIcoBar(path, value) {
    w := 16, h := 16

    ; bar height 0..14 (leave 1px border top/bottom)
    barH := Floor((ClampVal(value, 0, 100) / 100) * 14)

    ; Build pixel buffer (BGRA), bottom-up for BMP
    pixels := Buffer(w * h * 4, 0)

    ; Colors (BGRA)
    outlineB := 200, outlineG := 200, outlineR := 200, outlineA := 255
    barB := 255, barG := 255, barR := 255, barA := 255

    SetPx(buf, x, y, b, g, r, a) {
        w := 16, h := 16
        yy := (h - 1 - y)               ; BMP bottom-up
        off := (yy * w + x) * 4
        NumPut("UChar", b, buf, off + 0)
        NumPut("UChar", g, buf, off + 1)
        NumPut("UChar", r, buf, off + 2)
        NumPut("UChar", a, buf, off + 3)
    }

    ; Draw outline box (x:2..13, y:1..14)
    for x in [2,3,4,5,6,7,8,9,10,11,12,13] {
        SetPx(pixels, x, 1,  outlineB, outlineG, outlineR, outlineA)
        SetPx(pixels, x, 14, outlineB, outlineG, outlineR, outlineA)
    }
    Loop 14 {
        y := A_Index
        SetPx(pixels, 2,  y, outlineB, outlineG, outlineR, outlineA)
        SetPx(pixels, 13, y, outlineB, outlineG, outlineR, outlineA)
    }

    ; Fill bar from bottom inside box (x:3..12, y:14-barH .. 13)
    if (barH > 0) {
        yStart := 14 - barH
        y := yStart
        while (y <= 13) {
            Loop 10 {
                x := A_Index + 2 ; 3..12
                SetPx(pixels, x, y, barB, barG, barR, barA)
            }
            y += 1
        }
    }

    ; AND mask (1=transparent). 16 bits => 2 bytes, row padded to 4 bytes
    maskStride := 4
    mask := Buffer(maskStride * h, 0x00)

    ; Set mask bit where alpha==0
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

    ; ICONDIR
    NumPut("UShort", 0, ico, 0)
    NumPut("UShort", 1, ico, 2)
    NumPut("UShort", 1, ico, 4)

    ; ICONDIRENTRY
    NumPut("UChar", 16, ico, 6)      ; width
    NumPut("UChar", 16, ico, 7)      ; height
    NumPut("UChar", 0,  ico, 8)
    NumPut("UChar", 0,  ico, 9)
    NumPut("UShort", 1, ico, 10)     ; planes
    NumPut("UShort", 32, ico, 12)    ; bitcount
    NumPut("UInt",  bmpSize, ico, 14)
    NumPut("UInt",  6 + 16, ico, 18) ; image offset

    imgOff := 6 + 16
    NumPut("UInt", 40, ico, imgOff + 0)
    NumPut("Int",  16, ico, imgOff + 4)
    NumPut("Int",  16 * 2, ico, imgOff + 8) ; height includes AND mask
    NumPut("UShort", 1, ico, imgOff + 12)
    NumPut("UShort", 32, ico, imgOff + 14)
    NumPut("UInt", 0, ico, imgOff + 16) ; BI_RGB
    NumPut("UInt", xorSize + andSize, ico, imgOff + 20)

    ; Copy XOR pixels then AND mask
    DllCall("RtlMoveMemory", "Ptr", ico.Ptr + imgOff + 40, "Ptr", pixels.Ptr, "UPtr", xorSize)
    DllCall("RtlMoveMemory", "Ptr", ico.Ptr + imgOff + 40 + xorSize, "Ptr", mask.Ptr, "UPtr", andSize)

    f := FileOpen(path, "w")
    f.RawWrite(ico, ico.Size)
    f.Close()
}
