#Requires AutoHotkey v2.0
#SingleInstance Force

; Hot Corners - Bottom left corner toggles taskbar auto-hide
; Also supports Win+Shift+T as keyboard shortcut

cornerSize := 5
wasInCorner := false

SetTimer(CheckHotCorners, 50)

CheckHotCorners() {
    global cornerSize, wasInCorner
    
    MouseGetPos(&mouseX, &mouseY)
    screenWidth := A_ScreenWidth
    
    screenHeight := A_ScreenHeight
    
    inCorner := (mouseX <= cornerSize) && (mouseY >= screenHeight - cornerSize)
    
    if (inCorner && !wasInCorner) {
        ToggleTaskbar()
    }
    
    wasInCorner := inCorner
}

#+t::ToggleTaskbar()

ToggleTaskbar() {
    static ABM_SETSTATE := 0xA
    static ABM_GETSTATE := 0x4
    static ABS_AUTOHIDE := 0x1
    static ABS_ALWAYSONTOP := 0x2
    
    abd := Buffer(48, 0)
    NumPut("UInt", 48, abd, 0)
    NumPut("Ptr", WinExist("ahk_class Shell_TrayWnd"), abd, 8)
    
    currentState := DllCall("Shell32\SHAppBarMessage", "UInt", ABM_GETSTATE, "Ptr", abd)
    
    if (currentState & ABS_AUTOHIDE) {
        NumPut("UInt", ABS_ALWAYSONTOP, abd, 40)
    } else {
        NumPut("UInt", ABS_AUTOHIDE | ABS_ALWAYSONTOP, abd, 40)
    }
    
    DllCall("Shell32\SHAppBarMessage", "UInt", ABM_SETSTATE, "Ptr", abd)
}
