#Requires AutoHotkey v2.0
#SingleInstance Force
#NoTrayIcon

; Mouse Gestures
; Right Click + Drag Left = Previous Desktop
; Right Click + Drag Right = Next Desktop
; Right Click + Drag Up = Task View (Spaces)
; Right Click + Drag Down = Show Desktop

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
    switch dir {
        case "Right": Send "^#{Right}"
        case "Left":  Send "^#{Left}"
        case "Down":
            ; Drag Down:
            ; If Task View -> Close it
            ; Else if Normal (Windows Visible) -> Minimize All
            ; Else (AllMinimized or Empty) -> Do Nothing
            if (WinActive("Task View")) {
                Send "#{Tab}"
            } else {
                state := GetDesktopState()
                if (state == "Normal") {
                    Send "#d"
                }
            }
        case "Up":
            ; Drag Up:
            ; If Task View -> Do Nothing
            ; Else If AllMinimized -> Restore Windows
            ; Else (Normal or Empty) -> Show Task View
            if (!WinActive("Task View")) {
                state := GetDesktopState()
                if (state == "AllMinimized") {
                    Send "#d"
                    Sleep 150
                    if (GetDesktopState() == "AllMinimized") {
                         Send "#{Tab}"
                    }
                } else {
                    ; Normal or Empty
                    Send "#{Tab}"
                }
            }
    }
}

GetDesktopState() {
    hasVisible := false
    hasMinimized := false
    
    ids := WinGetList(,, "Program Manager") ; Exclude Program Manager
    for id in ids {
        ; Skip specific system classes
        class := WinGetClass(id)
        if (class == "Shell_TrayWnd" || class == "Windows.UI.Core.CoreWindow" || class == "WorkerW")
            continue
            
        ; Style Checks
        style := WinGetStyle(id)
        exStyle := WinGetExStyle(id)
        
        ; Must have WS_VISIBLE (0x10000000)
        if !(style & 0x10000000)
            continue
            
        ; Must NOT be WS_EX_TOOLWINDOW (0x80)
        if (exStyle & 0x80)
            continue
            
        ; Skip empty titles (unless checking for non-titled apps, but usually safe to skip)
        if (WinGetTitle(id) == "")
            continue
            
        ; Check State
        if (WinGetMinMax(id) == -1) {
            hasMinimized := true
        } else {
            hasVisible := true
            ; If we found a visible window, the state is Normal. 
            ; We can return early if we don't care about counting.
            return "Normal"
        }
    }
    
    if (hasMinimized)
        return "AllMinimized"
        
    return "Empty"
}
