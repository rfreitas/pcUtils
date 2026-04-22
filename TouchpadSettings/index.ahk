#Requires AutoHotkey v2.0
#SingleInstance Force
A_IconTip := "TouchpadSettings"

; Defines Registry Keys
Key_AAP_Root := "HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad"
Key_AAP_Status := "HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status"
Key_Mouse := "HKEY_CURRENT_USER\Control Panel\Mouse"

; Determine which AAP key to use (Root is standard, but some systems use Status)
Target_Key_AAP := Key_AAP_Root
Target_Key_AAP_HKLM := "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad"

try {
    if (RegRead(Key_AAP_Root, "AAPThreshold") == "")
        throw Error("Empty")
} catch {
    try {
        if (RegRead(Key_AAP_Status, "AAPThreshold") != "")
            Target_Key_AAP := Key_AAP_Status
    }
}

; Read current values
try {
    CurrentAAP := RegRead(Target_Key_AAP, "AAPThreshold")
} catch {
    CurrentAAP := 2 ; Default to Medium
}

try {
    CurrentAAPDisabled_User := RegRead(Target_Key_AAP, "AAPDisabled")
} catch {
    CurrentAAPDisabled_User := 0
}

try {
    CurrentAAPDisabled_HKLM := RegRead(Target_Key_AAP_HKLM, "AAPDisabled")
} catch {
    CurrentAAPDisabled_HKLM := 0
}

try {
    CurrentM1 := RegRead(Key_Mouse, "MouseThreshold1")
    CurrentM2 := RegRead(Key_Mouse, "MouseThreshold2")
} catch {
    CurrentM1 := "6"
    CurrentM2 := "10"
}

; GUI Creation
AdminStatus := A_IsAdmin ? " [ADMIN]" : ""
MyGui := Gui("+OwnDialogs", "Microsoft Keyboard Fix" . AdminStatus)
MyGui.BackColor := "2d2d2d"
MyGui.SetFont("s9 cWhite", "Segoe UI")

; --- Accidental Activation Prevention (AAP) ---
MyGui.Add("GroupBox", "w340 h170 cWhite", "1. Fix 'Touchpad Disabled While Typing'")
MyGui.Add("Text", "xp+20 yp+30", "Typing Delay (AAP):")

; 0=Always On, 1=Short, 2=Medium, 3=Long
AAP_Choices := ["Always On (Fixes Issue)", "Short Delay", "Medium Delay (Default)", "Long Delay"]
ChoiceIndex := CurrentAAP + 1 
if (ChoiceIndex > 4 || ChoiceIndex < 1)
    ChoiceIndex := 3 ; Default to Medium if unknown value

MyGui.Add("DropDownList", "vAAP Choose" . ChoiceIndex . " w280", AAP_Choices)

ForceUser_Checked := (CurrentAAPDisabled_User == 1) ? "Checked" : ""
MyGui.Add("Checkbox", "vForceUser " . ForceUser_Checked . " xm+20 y+40", "Force 'AAPDisabled' (User Profile)")

ForceHKLM_Checked := (CurrentAAPDisabled_HKLM == 1) ? "Checked" : ""
MyGui.Add("Checkbox", "vForceHKLM " . ForceHKLM_Checked . " xm+20 y+25", "Force 'AAPDisabled' (System-Wide - Admin)")

MyGui.SetFont("s8 cGray")
MyGui.Add("Text", "xp yp+20 w300", "Try these if the dropdown above doesn't work.")
MyGui.SetFont("s9 cWhite") ; Reset to default

; --- Mouse Thresholds (Deadzone) ---
MyGui.Add("GroupBox", "xm y+30 w340 h160 cWhite", "2. Fix 'Cursor Deadzone / Lag'")
MyGui.Add("Text", "xp+20 yp+30", "Mouse Threshold 1:")
MyGui.Add("Edit", "vM1 w100 cBlack", CurrentM1)

; Small Gray Text for M1
MyGui.SetFont("s8 cGray")
MyGui.Add("Text", "xp+120 yp", "(Default: 6)")
MyGui.SetFont("s9 cWhite")

MyGui.Add("Text", "xm+20 y+20", "Mouse Threshold 2:")
MyGui.Add("Edit", "vM2 w100 cBlack", CurrentM2)

; Small Gray Text for M2
MyGui.SetFont("s8 cGray")
MyGui.Add("Text", "xp+120 yp", "(Default: 10)")

; Small Gray Text for Note
MyGui.Add("Text", "xm+20 y+35 w300", "Set BOTH to 0 to remove 'sticky' cursor behavior.")
MyGui.SetFont("s9 cWhite")

; --- Apply & Close Buttons ---
ApplyBtn := MyGui.Add("Button", "xm y+20 w240 h40", "Apply Settings")
ApplyBtn.SetFont("bold s10")
ApplyBtn.OnEvent("Click", ApplySettings)

CloseBtn := MyGui.Add("Button", "x+10 yp w90 h40", "Close")
CloseBtn.OnEvent("Click", (*) => MyGui.Destroy())

MyGui.Show()

ApplySettings(*) {
    Saved := MyGui.Submit(false) ; Get values without hiding
    
    ; Convert Choice back to 0-3
    NewAAP := 2
    Switch Saved.AAP {
        Case "Always On (Fixes Issue)": NewAAP := 0
        Case "Short Delay": NewAAP := 1
        Case "Medium Delay (Default)": NewAAP := 2
        Case "Long Delay": NewAAP := 3
    }

    try {
        ; Write AAP (DWORD)
        RegWrite(NewAAP, "REG_DWORD", Target_Key_AAP, "AAPThreshold")
        
        ; User Profile AAPDisabled
        try {
            RegWrite(Saved.ForceUser ? 1 : 0, "REG_DWORD", Target_Key_AAP, "AAPDisabled")
        } catch {
            ; Ignore
        }

        ; System-Wide AAPDisabled
        try {
            CurrentHKLM := RegRead(Target_Key_AAP_HKLM, "AAPDisabled")
        } catch {
            CurrentHKLM := -1
        }
        
        NewHKLM := Saved.ForceHKLM ? 1 : 0
        
        if (NewHKLM != CurrentHKLM) {
            try {
                RegWrite(NewHKLM, "REG_DWORD", Target_Key_AAP_HKLM, "AAPDisabled")
            } catch {
                MsgBox("Could not update System-Wide (HKLM) settings.`n`nRun as Administrator to apply this change.", "Admin Rights Needed", "Icon!")
            }
        }

        ; Write Thresholds (String/SZ)
        RegWrite(String(Saved.M1), "REG_SZ", Key_Mouse, "MouseThreshold1")
        RegWrite(String(Saved.M2), "REG_SZ", Key_Mouse, "MouseThreshold2")
        
        ResultMsg := "Registry Updated Successfully!`n`n"
        ResultMsg .= "AAP Threshold: " . NewAAP . "`n"
        ResultMsg .= "MouseThreshold1: " . Saved.M1 . "`n"
        ResultMsg .= "MouseThreshold2: " . Saved.M2 . "`n`n"
        ResultMsg .= "IMPORTANT: You must RESTART your computer (or Log Off) for these changes to take effect."
        
        MsgBox(ResultMsg, "Success", "Iconi")
    } catch as err {
        MsgBox("Failed to write to registry.`nError: " . err.Message, "Error", "Iconx")
    }
}
