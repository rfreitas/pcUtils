#Requires AutoHotkey v2.0
#SingleInstance Force
#NoTrayIcon
CoordMode "Mouse", "Screen"

; Hot Corners:
; - Bottom Left: Toggle taskbar visibility
; - Bottom Right: Show Task View (Spaces)
; Also supports Win+Shift+T as keyboard shortcut

if (A_LineFile == A_ScriptFullPath) {
    cornerSize := 5
    wasInCorner := false

    SetTimer(CheckHotCorners, 50)
}

CheckHotCorners() {
    global cornerSize, wasInCorner

    MouseGetPos(&mouseX, &mouseY)
    screenWidth := A_ScreenWidth

    screenHeight := A_ScreenHeight

    inBottomLeft := (mouseX <= cornerSize) && (mouseY >= screenHeight - cornerSize)
    inBottomRight := (mouseX >= screenWidth - cornerSize) && (mouseY >= screenHeight - cornerSize)

    if (!wasInCorner) {
        if (inBottomLeft) {
            ToggleTaskbarVisibility()
        } else if (inBottomRight) {
            ShowTaskbar()
        }
    }

    wasInCorner := inBottomLeft || inBottomRight
}

#+t:: ToggleTaskbarVisibility()

ToggleTaskbarVisibility() {
    savedDetect := A_DetectHiddenWindows
    DetectHiddenWindows(true)

    isVisible := false
    if (hwnd := WinExist("ahk_class Shell_TrayWnd")) {
        isVisible := DllCall("IsWindowVisible", "Ptr", hwnd)
    }

    DetectHiddenWindows(savedDetect)

    if (isVisible) {
        HideTaskbar()
    } else {
        ShowTaskbar()
    }
}

ShowTaskbar() {
    try WinShow("ahk_class Shell_TrayWnd")
    try WinShow("ahk_class Shell_SecondaryTrayWnd")
}

HideTaskbar() {
    try WinHide("ahk_class Shell_TrayWnd")
    try WinHide("ahk_class Shell_SecondaryTrayWnd")
}
