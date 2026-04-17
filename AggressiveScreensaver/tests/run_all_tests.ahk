#Requires AutoHotkey v2.0

testsToRun := [
    "test_parsing.ahk",
    "test_joystick.ahk"
]

totalPassed := 0
totalFailed := 0

Print(msg) {
    try FileAppend(msg "`n", "*")
}

Print("`n=========================================")
Print("Starting AggressiveScreensaver Test Suite")
Print("=========================================`n")

ahkExe := "C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe"

for script in testsToRun {
    Print(">>> RUNNING SUITE: " script)
    Print("-----------------------------------------")
    
    scriptPath := A_ScriptDir "\" script
    
    try {
        ; Provide the path to the script
        exitCode := RunWait('"' ahkExe '" "' scriptPath '"')
        
        if (exitCode == 0) {
            totalPassed++
            Print("`n[SUITE PASSED]")
        } else {
            totalFailed++
            Print("`n[SUITE FAILED] (ExitCode: " exitCode ")")
        }
    } catch as e {
        Print("`n[SUITE EXCEPTION] " e.Message)
        totalFailed++
    }
    
    Print("-----------------------------------------`n")
}

Print("=========================================")
Print("FINAL SUITE SUMMARY")
Print("=========================================")
Print("Suites Passed: " totalPassed)
Print("Suites Failed: " totalFailed)

if (totalFailed > 0) {
    ExitApp(1)
} else {
    ExitApp(0)
}
