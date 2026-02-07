#Requires AutoHotkey v2.0
; We include the main script to access its functions
#Include AggressiveScreensaver.ahk

; ========================================
; Test Harness - Using OutputDebug for safe console output
; ========================================
testsPassed := 0
testsFailed := 0

Log(msg) {
    try {
        FileAppend(msg "`n", "*")
    } catch as e {
        ; Fallback if stdout not available (e.g., not run from console)
        ; Just silently skip - we'll see test summary at the end
        ; OutputDebug would be visible in debuggers but not in normal terminal
    }
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

; Track all tests
totalTests := 0

; ========================================
; SECTION 1: ParsePowercfgOutput Tests
; ========================================

SectionHeader("ParsePowercfgOutput: Happy Path")

mockOutput1 := "
(
DISPLAY:
[PROCESS] \Device\HarddiskVolume3\Program Files\VLC\vlc.exe
[PROCESS] \Device\HarddiskVolume3\Windows\System32\DisplayApp.exe

SYSTEM:
[DRIVER] Realtek Audio (HDAUDIO\FUNC_01)

AWAYMODE:
None.
)"

results := ParsePowercfgOutput(mockOutput1)
Test("Found 2 display apps", results.screen.Length == 2)
Test("Found 1 system app", results.sleep.Length == 1)
Test("First display app is vlc.exe", results.screen[1].filename == "vlc.exe")
Test("Second display app is DisplayApp.exe", results.screen[2].filename == "DisplayApp.exe")
Test("System app is Realtek Audio", InStr(results.sleep[1].filename, "Realtek") || InStr(results.sleep[1].text, "Realtek"))

; ========================================
; SECTION 2: ParsePowercfgOutput - Edge Cases
; ========================================

SectionHeader("ParsePowercfgOutput: Empty Sections")

mockOutput2 := "
(
DISPLAY:
None.

SYSTEM:
None.

AWAYMODE:
None.
)"

results := ParsePowercfgOutput(mockOutput2)
Test("Empty display returns 0 apps", results.screen.Length == 0)
Test("Empty system returns 0 apps", results.sleep.Length == 0)

; ========================================
; SECTION 3: ParsePowercfgOutput - Indented Lines
; ========================================

SectionHeader("ParsePowercfgOutput: Indented 'Reason:' Lines")

mockOutput3 := "
(
DISPLAY:
[PROCESS] C:\Program Files\VLC\vlc.exe
  Display is required by:
  Something something
[PROCESS] C:\Windows\System32\notepad.exe

SYSTEM:
None.

AWAYMODE:
None.
)"

results := ParsePowercfgOutput(mockOutput3)
Test("Handles indented lines - found 2 display apps", results.screen.Length == 2)
Test("First app extracted correctly with indents", results.screen[1].filename == "vlc.exe")
Test("Second app extracted correctly", results.screen[2].filename == "notepad.exe")

; ========================================
; SECTION 4: ParsePowercfgOutput - Driver Tags
; ========================================

SectionHeader("ParsePowercfgOutput: Driver Tags")

mockOutput4 := "
(
DISPLAY:
[DRIVER] NVIDIA Graphics (PCI\VEN_10DE)

SYSTEM:
[DRIVER] Realtek Audio (HDAUDIO\FUNC_01)
[DRIVER] Windows Search (WSearchTrigger)

AWAYMODE:
None.
)"

results := ParsePowercfgOutput(mockOutput4)
Test("DRIVER in display section recognized", results.screen.Length == 1)
Test("DRIVER has [DRIVER] tag in text", InStr(results.screen[1].text, "DRIVER"))
Test("Multiple drivers in sleep section recognized", results.sleep.Length == 2)

; ========================================
; SECTION 5: ParsePowercfgOutput - Path Extraction
; ========================================

SectionHeader("ParsePowercfgOutput: Path Extraction")

mockOutput5 := "
(
DISPLAY:
[PROCESS] C:\Program Files (x86)\Some App\app.exe
[PROCESS] \Device\HarddiskVolume3\Deep\Nested\Path\file.exe

SYSTEM:
None.

AWAYMODE:
None.
)"

results := ParsePowercfgOutput(mockOutput5)
Test("Path with parentheses - app.exe extracted", results.screen[1].filename == "app.exe")
Test("Device path - file.exe extracted", results.screen[2].filename == "file.exe")

; ========================================
; SECTION 6: ParsePowercfgOutput - Long Filenames
; ========================================

SectionHeader("ParsePowercfgOutput: Long Filename Truncation")

mockOutput6 := "
(
DISPLAY:
[PROCESS] C:\Program Files\SomeVeryLongApplicationName.exe

SYSTEM:
None.

AWAYMODE:
None.
)"

results := ParsePowercfgOutput(mockOutput6)
if (results.screen.Length > 0) {
    Test("Long filename truncated to 15 chars with ...", 
         StrLen(results.screen[1].text) <= 30)  ; Allow for tag and ellipsis (e.g., "SomeVeryLong... [PROCESS]")
    Test("Truncated name contains ellipsis",
         InStr(results.screen[1].text, "..."))
} else {
    Log("ERROR: mockOutput6 returned no screen apps!")
    Test("Long filename truncated to 15 chars with ...", false)
    Test("Truncated name contains ellipsis", false)
}

; ========================================
; SECTION 7: FormatTimeout Tests
; ========================================

SectionHeader("FormatTimeout: Basic Cases")

Test("Format 15 seconds", FormatTimeout(15) == "15s")
Test("Format 59 seconds", FormatTimeout(59) == "59s")
Test("Format 60 seconds (1 min)", FormatTimeout(60) == "1m")
Test("Format 90 seconds (1m 30s)", FormatTimeout(90) == "1m 30s")
Test("Format 120 seconds (2m)", FormatTimeout(120) == "2m")
Test("Format 180 seconds (3m)", FormatTimeout(180) == "3m")
Test("Format 125 seconds (2m 5s)", FormatTimeout(125) == "2m 5s")
Test("Format 900 seconds (15m)", FormatTimeout(900) == "15m")
Test("Format 1800 seconds (30m)", FormatTimeout(1800) == "30m")
Test("Format 1 second", FormatTimeout(1) == "1s")

; ========================================
; SECTION 8: FindStepIndex Tests
; ========================================

SectionHeader("FindStepIndex: Exact Step Matches")

; timeoutSteps = [15, 30, 60, 120, 180, 300, 600, 900, 1200, 1800]
Test("FindStepIndex(15) should map to step 1", FindStepIndex(15) == 10)   ; Inverted: step 1 -> raw 10
Test("FindStepIndex(30) should map to step 2", FindStepIndex(30) == 9)    ; Inverted: step 2 -> raw 9
Test("FindStepIndex(60) should map to step 3", FindStepIndex(60) == 8)
Test("FindStepIndex(120) should map to step 4", FindStepIndex(120) == 7)
Test("FindStepIndex(1800) should map to step 10", FindStepIndex(1800) == 1)  ; Inverted: step 10 -> raw 1

SectionHeader("FindStepIndex: Between-Step Values")

Test("FindStepIndex(10) rounds to first step", FindStepIndex(10) == 10)
Test("FindStepIndex(25) rounds to 30 (step 2)", FindStepIndex(25) == 9)
Test("FindStepIndex(50) rounds to 60 (step 3)", FindStepIndex(50) == 8)
Test("FindStepIndex(5000) exceeds max, returns 1", FindStepIndex(5000) == 1)

; ========================================
; SECTION 9: Summary
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

; Test 4: SYSTEM/AWAY section
Test("Found 1 sleep app", results.sleep.Length == 1)

; Test 5: Sleep app is Realtek Audio
Test("Sleep app is Realtek Audio", results.sleep[1].filename == "Realtek Audio")

summary := "Parser Tests Complete`n" testsPassed " passed, " testsFailed " failed"
if (testsFailed == 0) {
    Log("✓ ALL TESTS PASSED`n")
    Log(summary)
} else {
    Log("✗ SOME TESTS FAILED`n")
    Log(summary)
}

ExitApp()
