#Requires AutoHotkey v2.0

GetPrimaryHDRState(&supported, &enabled) {
    supported := false
    enabled := false
    
    local numPathArrayElements := 0
    local numModeInfoArrayElements := 0
    if DllCall("user32\GetDisplayConfigBufferSizes", "UInt", 2, "UInt*", &numPathArrayElements, "UInt*", &numModeInfoArrayElements) != 0
        return
        
    local pathArray := Buffer(numPathArrayElements * 72, 0)
    local modeInfoArray := Buffer(numModeInfoArrayElements * 64, 0)
    
    if DllCall("user32\QueryDisplayConfig", "UInt", 2, "UInt*", &numPathArrayElements, "Ptr", pathArray, "UInt*", &numModeInfoArrayElements, "Ptr", modeInfoArray, "Ptr", 0) != 0
        return

    loop numPathArrayElements {
        local offset := (A_Index - 1) * 72
        local flags := NumGet(pathArray, offset + 64, "UInt")
        if !(flags & 1) ; DISPLAYCONFIG_PATH_ACTIVE
            continue

        local targetAdapterIdLow := NumGet(pathArray, offset + 20, "UInt")
        local targetAdapterIdHigh := NumGet(pathArray, offset + 24, "Int")
        local targetId := NumGet(pathArray, offset + 28, "UInt")

        local info := Buffer(32, 0)
        NumPut("UInt", 9, info, 0) ; type = GET
        NumPut("UInt", 32, info, 4) ; size
        NumPut("UInt", targetAdapterIdLow, info, 8)
        NumPut("Int", targetAdapterIdHigh, info, 12)
        NumPut("UInt", targetId, info, 16)
        
        if DllCall("user32\DisplayConfigGetDeviceInfo", "Ptr", info) == 0 {
            local val := NumGet(info, 20, "UInt")
            supported := (val & 1) != 0
            enabled := (val & 2) != 0
            if (supported)
                return ; Found an active supported display
        }
    }
}

SetHDRState(enable) {
    global IgnoreDisplayChangeUntil
    local numPathArrayElements := 0
    local numModeInfoArrayElements := 0
    if DllCall("user32\GetDisplayConfigBufferSizes", "UInt", 2, "UInt*", &numPathArrayElements, "UInt*", &numModeInfoArrayElements) != 0
        return
        
    local pathArray := Buffer(numPathArrayElements * 72, 0)
    local modeInfoArray := Buffer(numModeInfoArrayElements * 64, 0)
    
    if DllCall("user32\QueryDisplayConfig", "UInt", 2, "UInt*", &numPathArrayElements, "Ptr", pathArray, "UInt*", &numModeInfoArrayElements, "Ptr", modeInfoArray, "Ptr", 0) != 0
        return

    loop numPathArrayElements {
        local offset := (A_Index - 1) * 72
        local flags := NumGet(pathArray, offset + 64, "UInt")
        if !(flags & 1) ; DISPLAYCONFIG_PATH_ACTIVE
            continue

        local targetAdapterIdLow := NumGet(pathArray, offset + 20, "UInt")
        local targetAdapterIdHigh := NumGet(pathArray, offset + 24, "Int")
        local targetId := NumGet(pathArray, offset + 28, "UInt")

        local info := Buffer(32, 0)
        NumPut("UInt", 9, info, 0) ; type = GET
        NumPut("UInt", 32, info, 4) ; size
        NumPut("UInt", targetAdapterIdLow, info, 8)
        NumPut("Int", targetAdapterIdHigh, info, 12)
        NumPut("UInt", targetId, info, 16)
        
        if DllCall("user32\DisplayConfigGetDeviceInfo", "Ptr", info) == 0 {
            local val := NumGet(info, 20, "UInt")
            local supported := (val & 1) != 0
            if (supported) {
                ; Type 10 = DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE
                local setInfo := Buffer(24, 0)
                NumPut("UInt", 10, setInfo, 0) ; type
                NumPut("UInt", 24, setInfo, 4) ; size
                NumPut("UInt", targetAdapterIdLow, setInfo, 8)
                NumPut("Int", targetAdapterIdHigh, setInfo, 12)
                NumPut("UInt", targetId, setInfo, 16)
                NumPut("UInt", enable ? 1 : 0, setInfo, 20) ; enableAdvancedColor
                
                IgnoreDisplayChangeUntil := A_TickCount + 2000
                DllCall("user32\DisplayConfigSetDeviceInfo", "Ptr", setInfo)
                
                ; After changing Advanced Color State, we must call SetDisplayConfig to apply it
                DllCall("user32\SetDisplayConfig", "UInt", 0, "Ptr", 0, "UInt", 0, "Ptr", 0, "UInt", 0x00000080) ; SDC_APPLY
                return 
            }
        }
    }
}

; CLI Interface
if (A_LineFile == A_ScriptFullPath) {
    if (A_Args.Length > 0) {
        cmd := StrLower(A_Args[1])
        if (cmd == "status") {
            GetPrimaryHDRState(&supp, &en)
            FileAppend("Supported: " (supp ? "Yes" : "No") "`nEnabled: " (en ? "Yes" : "No") "`n", "*")
            ExitApp(0)
        } else if (cmd == "on") {
            SetHDRState(1)
            FileAppend("HDR Enabled`n", "*")
            ExitApp(0)
        } else if (cmd == "off") {
            SetHDRState(0)
            FileAppend("HDR Disabled`n", "*")
            ExitApp(0)
        } else {
            FileAppend("Usage: HDRControl.ahk [status|on|off]`n", "*")
            ExitApp(1)
        }
    } else {
        FileAppend("Usage: HDRControl.ahk [status|on|off]`n", "*")
        ExitApp(1)
    }
}