#Requires AutoHotkey v2.0
#Include ..\AggressiveScreensaver.ahk

; ========================================
; Test Harness
; ========================================
testsPassed := 0
testsFailed := 0
totalTests := 0

Log(msg) {
    try FileAppend(msg "`n", "*")
}

Test(name, condition) {
    global testsPassed, testsFailed, totalTests
    totalTests++
    if (condition) {
        testsPassed++
        Log("✓ " name)
    } else {
        testsFailed++
        Log("✗ [Test #" totalTests "] " name)
    }
}

SectionHeader(title) {
    Log("")
    Log("=== " title " ===")
}

; ========================================
; Mock State & Dependency Injection
; ========================================
global mockTickCount := 0
global mockJoystickState := Map()

MockGetTimeMs() {
    global mockTickCount
    return mockTickCount
}

MockGetKeyState(KeyName, Mode := "") {
    global mockJoystickState
    
    if (RegExMatch(KeyName, "i)^(\d+)Joy(.*)$", &match)) {
        id := match[1]
        prop := match[2]
        
        joyName := id "Joy"
        if (!mockJoystickState.Has(joyName))
            return "" ; Controller disconnected
        
        dev := mockJoystickState[joyName]
        if (dev.Has(prop))
            return dev[prop]
        
        ; Fallbacks
        if (prop == "Name")
            return "Mock"
        if (IsNumber(prop)) ; Button
            return 0
        if (prop == "POV")
            return -1
        return 50 ; Default axis center
    }
    return ""
}

; Inject mocks
global GetTimeMsFn := MockGetTimeMs
global GetKeyStateFn := MockGetKeyState

; ========================================
; Tests
; ========================================

SectionHeader("HasJoystickActivity: Device Connection")

; Reset states
mockTickCount := 0
mockJoystickState := Map()
HasJoystickActivity(true)

Test("Initial check returns false (no devices)", HasJoystickActivity() == false)

; Connect device on slot 5
mockJoystickState["5Joy"] := Map("Name", "Mock", "X", 50, "Y", 50)
mockTickCount := 5001 ; Advance time to trigger polling

Test("Connecting device triggers detection but false immediately (caching baseline)", HasJoystickActivity() == false)

SectionHeader("HasJoystickActivity: Deadzones & Axis Movement")

; Axis move within deadzone (50 -> 51)
mockJoystickState["5Joy"]["X"] := 51
Test("Ignored movement inside deadzone (change <= 2)", HasJoystickActivity() == false)

; Axis move outside deadzone (51 -> 60)
mockJoystickState["5Joy"]["X"] := 60
Test("Detected movement outside deadzone (change > 2)", HasJoystickActivity() == true)

; Check if state reset
Test("Next poll with no movement returns false", HasJoystickActivity() == false)

SectionHeader("HasJoystickActivity: Buttons & POV")

; Button press
mockJoystickState["5Joy"]["1"] := 1
Test("Detected button press", HasJoystickActivity() == true)

; POV change
mockJoystickState["5Joy"]["POV"] := 9000
Test("Detected POV hat switch change", HasJoystickActivity() == true)

SectionHeader("HasJoystickActivity: Device Disconnection")

; Disconnect device while polling
mockTickCount := 10500 ; Trigger another polling loop explicitly
mockJoystickState.Delete("5Joy") ; Poof! Gone.

; This should gracefully return false and not crash
Test("Gracefully handles missing device on next poll", HasJoystickActivity() == false)

; ========================================
; SUMMARY
; ========================================
SectionHeader("Test Summary")
Log("Passed: " testsPassed)
Log("Failed: " testsFailed)
Log("Total: " (testsPassed + testsFailed))

if (testsFailed > 0) {
    Log("")
    Log("FAILURE: Some tests failed")
    ExitApp(1)
} else {
    Log("")
    Log("SUCCESS: All tests passed")
    ExitApp(0)
}
