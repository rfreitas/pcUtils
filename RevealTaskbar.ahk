#Requires AutoHotkey v2.0
#SingleInstance Force
CoordMode "Mouse", "Screen"

; Hot Corners:
; - Bottom Left: Toggle taskbar visibility
; - Bottom Right: Show Task View (Spaces)
; Also supports Win+Shift+T as keyboard shortcut

if (A_LineFile == A_ScriptFullPath) {
    cornerSize := 5
    wasInCorner := false

    A_TrayMenu.Add()
    A_TrayMenu.Add("Taskbar Always Visible", ToggleAlwaysVisible)
    A_TrayMenu.Default := "Taskbar Always Visible"
    UpdateTrayCheckmark()

    SetTimer(CheckHotCorners, 50)
}

UpdateTrayCheckmark() {
    APPBARDATA := Buffer(A_PtrSize == 8 ? 48 : 36, 0)
    NumPut("UInt", APPBARDATA.Size, APPBARDATA, 0)
    hwnd := WinExist("ahk_class Shell_TrayWnd")
    NumPut("Ptr", hwnd, APPBARDATA, A_PtrSize == 8 ? 8 : 4)
    state := DllCall("Shell32\SHAppBarMessage", "UInt", 4, "Ptr", APPBARDATA) ; ABM_GETSTATE
    isAutoHide := state & 1

    if (isAutoHide) {
        A_TrayMenu.Uncheck("Taskbar Always Visible")
    } else {
        A_TrayMenu.Check("Taskbar Always Visible")
    }
}

ToggleAlwaysVisible(ItemName, ItemPos, MyMenu) {
    APPBARDATA := Buffer(A_PtrSize == 8 ? 48 : 36, 0)
    NumPut("UInt", APPBARDATA.Size, APPBARDATA, 0)
    hwnd := WinExist("ahk_class Shell_TrayWnd")
    NumPut("Ptr", hwnd, APPBARDATA, A_PtrSize == 8 ? 8 : 4)
    
    state := DllCall("Shell32\SHAppBarMessage", "UInt", 4, "Ptr", APPBARDATA)
    newState := state ^ 1 ; Toggle ABS_AUTOHIDE
    
    NumPut("UPtr", newState, APPBARDATA, A_PtrSize == 8 ? 40 : 32)
    DllCall("Shell32\SHAppBarMessage", "UInt", 10, "Ptr", APPBARDATA) ; ABM_SETSTATE
    
    UpdateTrayCheckmark()
}

CheckHotCorners() {
    global cornerSize, wasInCorner

    MouseGetPos(&mouseX, &mouseY)
    screenWidth := A_ScreenWidth

    screenHeight := A_ScreenHeight

    inBottomLeft := (mouseX <= cornerSize) && (mouseY >= screenHeight - cornerSize)
    inBottomRight := (mouseX >= screenWidth - cornerSize) && (mouseY >= screenHeight - cornerSize)

    if (!wasInCorner) {
        if (inBottomLeft || inBottomRight) {
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
