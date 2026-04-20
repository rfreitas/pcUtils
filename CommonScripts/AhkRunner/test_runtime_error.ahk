#Requires AutoHotkey v2.0

FileAppend("before error`n", "*")

; Runtime error: explicit throw
throw Error("intentional runtime error", -1)

FileAppend("after error (should not appear)`n", "*")
