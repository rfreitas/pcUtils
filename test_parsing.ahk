#Requires AutoHotkey v2.0
; We include the main script to access its functions
#Include AggressiveScreensaver.ahk

; Mock output representing the bug case:
; Two apps in DISPLAY, where the first has an indented "Reason:" line
mockOutput := "
(
DISPLAY:
[PROCESS] \Device\HarddiskVolume3\Program Files\VLC\vlc.exe
  Display is required by:
  Something something
[PROCESS] \Device\HarddiskVolume3\Windows\System32\DisplayApp.exe

SYSTEM:
None.

AWAYMODE:
[DRIVER] Realtek Audio (HDAUDIO\FUNC_01)
)"

testsPassed := 0
testsFailed := 0

Log(msg) {
    FileAppend(msg "`n", "*")
}

Test(name, condition) {
    global testsPassed, testsFailed
    if (condition) {
        testsPassed++
        Log("✓ " name)
    } else {
        testsFailed++
        Log("✗ " name)
    }
}

results := ParsePowercfgOutput(mockOutput)

; Test 1: Count of SCREEN apps
Test("Found 2 screen apps", results.screen.Length == 2)

; Test 2: Verify first filename
Test("First app is vlc.exe", results.screen[1].filename == "vlc.exe")

; Test 3: Verify second filename
Test("Second app is DisplayApp.exe", results.screen[2].filename == "DisplayApp.exe")

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
