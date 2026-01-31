#Requires AutoHotkey v2.0
#SingleInstance Force
#NoTrayIcon

; Mouse Gestures
; Right Click + Drag Left = Previous Desktop
; Right Click + Drag Right = Next Desktop
; Right Click + Drag Up = Task View (Spaces)
; Right Click + Drag Down = Show Desktop

; State Tracking
; 0 = Normal
; 1 = Task View Open
; 2 = Desktop Shown
viewState := 0

RButton:: {
    MouseGetPos(&startX, &startY)
    triggered := false
    everTriggered := false
    
    ; Stop detection vars
    lastMoveTime := A_TickCount
    lastX := startX
    lastY := startY
    
    while (GetKeyState("RButton", "P")) {
        MouseGetPos(&currX, &currY)
        
        ; Update stop detection if moving
        if (Abs(currX - lastX) > 2 || Abs(currY - lastY) > 2) {
            lastMoveTime := A_TickCount
            lastX := currX
            lastY := currY
        }
        
        if (!triggered) {
            dir := GetGestureDirection(startX, startY, currX, currY)
            if (dir) {
                PerformGestureAction(dir)
                triggered := true
                everTriggered := true
            }
        } else {
            ; Reset tracking if mouse stopped for 200ms
            if (A_TickCount - lastMoveTime > 200) {
                triggered := false
                startX := currX
                startY := currY
            }
        }
        
        Sleep 10
    }
    
    if (everTriggered)
        KeyWait "RButton"
    else
        Click "Right"
}

GetGestureDirection(x1, y1, x2, y2) {
    minDrag := 50
    dx := x2 - x1
    dy := y2 - y1
    
    if (Abs(dx) > Abs(dy)) { ; Horizontal
        if (dx > minDrag) {
            return "Right"
        }
        if (dx < -minDrag) {
            return "Left"
        }
    } else { ; Vertical
        if (dy > minDrag) {
            return "Down"
        }
        if (dy < -minDrag) {
            return "Up"
        }
    }
    return ""
}

PerformGestureAction(dir) {
    global viewState
    switch dir {
        case "Right": Send "^#{Right}"
        case "Left":  Send "^#{Left}"
        case "Down":
            if (viewState == 1) { ; In TaskView -> Close it
                Send "#{Tab}"
                viewState := 0
            } else { ; Toggle Desktop
                Send "#d"
                viewState := (viewState == 2) ? 0 : 2
            }
        case "Up":
            if (viewState == 2) { ; Desktop Shown -> Restore
                Send "#d"
                viewState := 0
            } else { ; Toggle TaskView
                Send "#{Tab}"
                viewState := (viewState == 1) ? 0 : 1
            }
    }
}
