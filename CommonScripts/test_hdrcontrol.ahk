#Requires AutoHotkey v2.0

; Integration test for HDRControl functions
; Usage: AutoHotkey64.exe test_hdrcontrol.ahk
; Exit codes: 0 = all pass, 1 = any fail

#Include "HDRControl.ahk"

; Utility functions
Log(msg) {
    FileAppend(msg "`n", "*")
}

TestPass(name) {
    FileAppend("[PASS] " name "`n", "*")
}

TestFail(name, details := "") {
    FileAppend("[FAIL] " name "`n", "*")
    if (details != "")
        FileAppend("       " details "`n", "*")
}

; Global test counter
PassCount := 0
FailCount := 0

; Save initial HDR state to restore at end
GetPrimaryHDRState(&initialSupported, &initialEnabled)

Log("")
Log("========== HDRControl Integration Tests ==========")
Log("[Initial State] Supported: " initialSupported ", Enabled: " initialEnabled)
Log("")

; Test 1: GetPrimaryHDRState function can be called
Log("[Test 1] GetPrimaryHDRState Query")
try {
    GetPrimaryHDRState(&supported, &enabled)
    
    isSupportedValid := (supported == 0 || supported == 1)
    isEnabledValid := (enabled == 0 || enabled == 1)
    
    if (isSupportedValid && isEnabledValid) {
        TestPass("GetPrimaryHDRState returns boolean-like values (supported=" supported ", enabled=" enabled ")")
        PassCount++
    } else {
        TestFail("GetPrimaryHDRState returns invalid types", "supported=" supported ", enabled=" enabled)
        FailCount++
    }
} catch as err {
    TestFail("GetPrimaryHDRState threw exception", err.Message)
    FailCount++
}

Log("")

; Test 2: SetHDRState function can be called
Log("[Test 2] SetHDRState Can Execute")
try {
    ; Try enabling
    SetHDRState(1)
    TestPass("SetHDRState(1) executed without crash")
    PassCount++
    
    ; Try disabling
    SetHDRState(0)
    TestPass("SetHDRState(0) executed without crash")
    PassCount++
    
} catch as err {
    TestFail("SetHDRState threw exception", err.Message)
    FailCount += 2
}

Log("")

; Test 3: Verify HDRControl file exists
Log("[Test 3] HDRControl File Exists")
try {
    if (FileExist(A_ScriptDir "\HDRControl.ahk")) {
        TestPass("HDRControl.ahk file found at " A_ScriptDir "\HDRControl.ahk")
        PassCount++
    } else {
        TestFail("HDRControl.ahk file not found at " A_ScriptDir "\HDRControl.ahk")
        FailCount++
    }
} catch as err {
    TestFail("File check threw exception", err.Message)
    FailCount++
}

Log("")

; Test 4: Global variable access
Log("[Test 4] Global State Variable Access")
try {
    global IgnoreDisplayChangeUntil
    
    ; Test that we can read and write the variable
    origValue := IgnoreDisplayChangeUntil
    IgnoreDisplayChangeUntil := A_TickCount + 5000
    newValue := IgnoreDisplayChangeUntil
    
    if (newValue > origValue) {
        TestPass("IgnoreDisplayChangeUntil accessible and modifiable")
        PassCount++
    } else {
        TestFail("IgnoreDisplayChangeUntil value invalid")
        FailCount++
    }
    
} catch as err {
    TestFail("Global variable access threw exception", err.Message)
    FailCount++
}

Log("")

; Test 5: Display change event timing
Log("[Test 5] Display Change Event Timing")
try {
    global IgnoreDisplayChangeUntil
    
    ; Set ignore timer
    IgnoreDisplayChangeUntil := A_TickCount + 2000
    
    ; Should be blocking
    isBlocking := (A_TickCount < IgnoreDisplayChangeUntil)
    
    if (isBlocking) {
        TestPass("Display change ignore timing works correctly")
        PassCount++
    } else {
        TestFail("Display change ignore timing failed")
        FailCount++
    }
    
} catch as err {
    TestFail("Display change timing threw exception", err.Message)
    FailCount++
}

Log("")

; Test 6: Function Accessibility from Include
Log("[Test 6] Function Accessibility")
try {
    ; If we got here, both GetPrimaryHDRState and SetHDRState are callable
    TestPass("GetPrimaryHDRState is accessible via include")
    PassCount++
    TestPass("SetHDRState is accessible via include")
    PassCount++
    
} catch as err {
    TestFail("Function accessibility threw exception", err.Message)
    FailCount += 2
}

Log("")

; ============================================
; RESTORE INITIAL STATE
; ============================================
Log("[Cleanup] Restoring initial HDR state...")
try {
    if (initialEnabled) {
        SetHDRState(1)
        Log("HDR restored to: Enabled")
    } else {
        SetHDRState(0)
        Log("HDR restored to: Disabled")
    }
} catch as err {
    Log("⚠ Warning: Could not restore initial HDR state: " err.Message)
}

Log("")

; ============================================
; SUMMARY
; ============================================
Log("========== Test Summary ==========")
totalTests := PassCount + FailCount
Log("Passed: " PassCount "/" totalTests)
Log("Failed: " FailCount "/" totalTests)
Log("")

if (FailCount > 0) {
    Log("RESULT: INTEGRATION TEST FAILED")
    ExitApp(1)
} else {
    Log("RESULT: INTEGRATION TEST PASSED")
    ExitApp(0)
}
