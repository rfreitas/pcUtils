#Requires AutoHotkey v2.0
#SingleInstance Force
#NoTrayIcon

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
