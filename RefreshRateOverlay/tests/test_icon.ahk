#Requires AutoHotkey v2.0
#Include "..\TrayIconRenderer.ahk"

Log(msg) {
    FileAppend(msg "`n", "*")
}

Test(name, condition) {
    if (condition) {
        Log("✓ " name)
    } else {
        Log("✗ " name)
    }
}

; Test that drawing an icon produces a valid handle
hIcon1 := CreateTextIcon("144")
Test("Generates valid HICON handle for 3 digits", hIcon1 != 0)

hIcon2 := CreateTextIcon("60")
Test("Generates valid HICON handle for 2 digits", hIcon2 != 0)

if (hIcon1)
    DllCall("DestroyIcon", "Ptr", hIcon1)

if (hIcon2)
    DllCall("DestroyIcon", "Ptr", hIcon2)

; Test the new pixel bounds solver mathematically
result1 := SolveFontToFit("144", 40, 40)
Test("3-digit text (144) mathematically scales within 40px container", result1.width <= 39)

result2 := SolveFontToFit("60", 16, 16)
Test("2-digit text (60) mathematically scales within 16px container", result2.width <= 15)

result3 := SolveFontToFit("1000", 24, 24)
Test("4-digit text (1000) heavily scales down to fit 24px container", result3.width <= 23)

ExitApp
