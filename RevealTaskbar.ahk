#Requires AutoHotkey v2.0
#SingleInstance Force
#NoTrayIcon

; Hot Corners:
; - Bottom Left: Toggle taskbar auto-hide
; - Bottom Right: Show Task View (Spaces)
; Also supports Win+Shift+T as keyboard shortcut

cornerSize := 5
wasInCorner := false

SetTimer(CheckHotCorners, 50)

CheckHotCorners() {
    global cornerSize, wasInCorner
    
    MouseGetPos(&mouseX, &mouseY)
    screenWidth := A_ScreenWidth
    
    screenHeight := A_ScreenHeight
    
    inBottomLeft := (mouseX <= cornerSize) && (mouseY >= screenHeight - cornerSize)
    inBottomRight := (mouseX >= screenWidth - cornerSize) && (mouseY >= screenHeight - cornerSize)
    
    if (!wasInCorner) {
        if (inBottomLeft) {
            ToggleTaskbar()
        } else if (inBottomRight) {
            Send "#{Tab}"
        }
    }
    
    wasInCorner := inBottomLeft || inBottomRight
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

; Mouse Gestures
; Right Click + Drag Left = Previous Desktop
; Right Click + Drag Right = Next Desktop

RButton:: {
    MouseGetPos(&startX, &startY)
    minDrag := 50  ; Minimum pixels to count as a drag
    triggered := false
    everTriggered := false
    
    ; Variables for stop detection
    lastMoveTime := A_TickCount
    lastX := startX
    
    while (GetKeyState("RButton", "P")) {
        MouseGetPos(&currentX, &currentY)
        
        ; Check if mouse is moving
        if (Abs(currentX - lastX) > 2) {
            lastMoveTime := A_TickCount
            lastX := currentX
        }
        
        if (!triggered) {
            xDiff := currentX - startX
            
            if (xDiff > minDrag) {
                ; Dragged Right -> Next Desktop
                Send "^#{Right}"
                triggered := true
                everTriggered := true
            } else if (xDiff < -minDrag) {
                ; Dragged Left -> Previous Desktop
                Send "^#{Left}"
                triggered := true
                everTriggered := true
            }
        } else {
            ; Already triggered, wait for mouse to stop moving before resetting
            if (A_TickCount - lastMoveTime > 200) { ; 200ms pause considered as "stop"
                triggered := false
                startX := currentX
            }
        }
        
        Sleep 10
    }
    
    if (everTriggered) {
        KeyWait "RButton" ; Wait for release if we already triggered the action
    } else {
        ; No significant drag -> Normal Right Click
        Click "Right"
    }
}
