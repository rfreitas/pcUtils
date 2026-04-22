#Requires AutoHotkey v2.0

; DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 (type 15, Windows 11 24H2+)
;   Offsets from struct start (header = 20 bytes):
;     20: bitfield (bit 4 = highDynamicRangeSupported)
;     32: activeColorMode  (0=SDR, 1=WCG, 2=HDR)
;   Total size: 36 bytes
;
; DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO (type 9, legacy)
;   Offsets:
;     20: bitfield (bit 0 = advancedColorSupported, bit 1 = advancedColorEnabled)
;   Total size: 32 bytes

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
        if !(flags & 1) ; DISPLAYCONFIG_TARGET_IN_USE
            continue

        local adapterLow  := NumGet(pathArray, offset + 20, "UInt")
        local adapterHigh := NumGet(pathArray, offset + 24, "Int")
        local targetId    := NumGet(pathArray, offset + 28, "UInt")

        ; Try Windows 11 24H2+ API first (type 15)
        local info2 := Buffer(36, 0)
        NumPut("UInt", 15,          info2, 0)  ; DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2
        NumPut("UInt", 36,          info2, 4)
        NumPut("UInt", adapterLow,  info2, 8)
        NumPut("Int",  adapterHigh, info2, 12)
        NumPut("UInt", targetId,    info2, 16)

        if DllCall("user32\DisplayConfigGetDeviceInfo", "Ptr", info2) == 0 {
            local val2 := NumGet(info2, 20, "UInt")
            supported := (val2 & 16) != 0  ; bit 4 = highDynamicRangeSupported
            if (supported) {
                local activeColorMode := NumGet(info2, 32, "UInt")
                enabled := (activeColorMode == 2)  ; DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR
                return
            }
        }

        ; Fallback: type 9 (pre-24H2)
        local info := Buffer(32, 0)
        NumPut("UInt", 9,           info, 0)  ; DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO
        NumPut("UInt", 32,          info, 4)
        NumPut("UInt", adapterLow,  info, 8)
        NumPut("Int",  adapterHigh, info, 12)
        NumPut("UInt", targetId,    info, 16)

        if DllCall("user32\DisplayConfigGetDeviceInfo", "Ptr", info) == 0 {
            local val := NumGet(info, 20, "UInt")
            supported := (val & 1) != 0  ; advancedColorSupported
            enabled := (val & 2) != 0    ; advancedColorEnabled
            if (supported)
                return
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
        if !(flags & 1)
            continue

        local adapterLow  := NumGet(pathArray, offset + 20, "UInt")
        local adapterHigh := NumGet(pathArray, offset + 24, "Int")
        local targetId    := NumGet(pathArray, offset + 28, "UInt")

        ; Try Windows 11 24H2+ set API first (type 16)
        local setInfo2 := Buffer(24, 0)
        NumPut("UInt", 16,          setInfo2, 0)  ; DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE
        NumPut("UInt", 24,          setInfo2, 4)
        NumPut("UInt", adapterLow,  setInfo2, 8)
        NumPut("Int",  adapterHigh, setInfo2, 12)
        NumPut("UInt", targetId,    setInfo2, 16)
        NumPut("UInt", enable ? 1 : 0, setInfo2, 20)  ; enableHdr

        IgnoreDisplayChangeUntil := A_TickCount + 2000
        if DllCall("user32\DisplayConfigSetDeviceInfo", "Ptr", setInfo2) == 0 {
            DllCall("user32\SetDisplayConfig", "UInt", 0, "Ptr", 0, "UInt", 0, "Ptr", 0, "UInt", 0x00000080)
            return
        }

        ; Fallback: type 10 (pre-24H2)
        local setInfo := Buffer(24, 0)
        NumPut("UInt", 10,          setInfo, 0)  ; DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE
        NumPut("UInt", 24,          setInfo, 4)
        NumPut("UInt", adapterLow,  setInfo, 8)
        NumPut("Int",  adapterHigh, setInfo, 12)
        NumPut("UInt", targetId,    setInfo, 16)
        NumPut("UInt", enable ? 1 : 0, setInfo, 20)

        IgnoreDisplayChangeUntil := A_TickCount + 2000
        DllCall("user32\DisplayConfigSetDeviceInfo", "Ptr", setInfo)
        DllCall("user32\SetDisplayConfig", "UInt", 0, "Ptr", 0, "UInt", 0, "Ptr", 0, "UInt", 0x00000080)
        return
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
