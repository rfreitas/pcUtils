#Requires AutoHotkey v2.0
#SingleInstance Force

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
    CurrentM1 := RegRead(Key_Mouse, "MouseThreshold1")
    CurrentM2 := RegRead(Key_Mouse, "MouseThreshold2")
} catch {
    CurrentM1 := "6"
    CurrentM2 := "10"
}

; GUI Creation
MyGui := Gui(, "Microsoft Keyboard Fix")
MyGui.SetFont("s10", "Segoe UI")

; --- Accidental Activation Prevention (AAP) ---
MyGui.Add("GroupBox", "w340 h140", "1. Fix 'Touchpad Disabled While Typing'")
MyGui.Add("Text", "xp+20 yp+30", "Typing Delay (AAP):")

; 0=Always On, 1=Short, 2=Medium, 3=Long
AAP_Choices := ["Always On (Fixes Issue)", "Short Delay", "Medium Delay (Default)", "Long Delay"]
ChoiceIndex := CurrentAAP + 1 
if (ChoiceIndex > 4 || ChoiceIndex < 1)
    ChoiceIndex := 3 ; Default to Medium if unknown value

MyGui.Add("DropDownList", "vAAP Choose" . ChoiceIndex . " w280", AAP_Choices)

; Small Gray Text for AAP
MyGui.SetFont("s8 cGray") 
MyGui.Add("Text", "xp yp+40 w300", "Set this to 'Always On' to stop the trackpad from freezing when you press a key.")
MyGui.SetFont("s10 cDefault") ; Reset to default color/size

MyGui.Add("Checkbox", "vForceDisableAAP Checked xm+20 y+40", "Force 'AAPDisabled' (More aggressive fix)")
MyGui.SetFont("s8 cGray")
MyGui.Add("Text", "xp yp+20 w300", "Try this if the dropdown above doesn't work. Might require Admin.")
MyGui.SetFont("s10 cDefault")

; --- Mouse Thresholds (Deadzone) ---
MyGui.Add("GroupBox", "xm y+30 w340 h160", "2. Fix 'Cursor Deadzone / Lab'")
MyGui.Add("Text", "xp+20 yp+30", "Mouse Threshold 1:")
MyGui.Add("Edit", "vM1 w100", CurrentM1)

; Small Gray Text for M1
MyGui.SetFont("s8 cGray")
MyGui.Add("Text", "xp+120 yp", "(Default: 6)")
MyGui.SetFont("s10 cDefault")

MyGui.Add("Text", "xm+20 y+20", "Mouse Threshold 2:")
MyGui.Add("Edit", "vM2 w100", CurrentM2)

; Small Gray Text for M2
MyGui.SetFont("s8 cGray")
MyGui.Add("Text", "xp+120 yp", "(Default: 10)")

; Small Gray Text for Note
MyGui.Add("Text", "xm+20 y+35 w300", "Set BOTH to 0 to remove the 'sticky' cursor start-up behavior.")
MyGui.SetFont("s10 cDefault")

; --- Apply Button ---
ApplyBtn := MyGui.Add("Button", "xm y+20 w340 h50", "Apply Settings to Registry")
ApplyBtn.SetFont("bold s11")
ApplyBtn.OnEvent("Click", ApplySettings)

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
        
        ; Write AAPDisabled if checked
        if (Saved.ForceDisableAAP) {
             try {
                RegWrite(1, "REG_DWORD", Target_Key_AAP, "AAPDisabled") ; HKCU
             } catch {
                ; Ignore error
             }

             try {
                RegWrite(1, "REG_DWORD", Target_Key_AAP_HKLM, "AAPDisabled") ; HKLM
             } catch {
                MsgBox("Could not write to HKLM (System-wide) settings.`n`nTry running this script as Administrator for the 'Force Disable' fix to fully work.", "Admin Rights Needed", "Icon!")
             }
        } else {
             ; Reset if unchecked (optional, or leave as is)
             try { 
                RegWrite(0, "REG_DWORD", Target_Key_AAP, "AAPDisabled") 
             } catch {
                ; Ignore
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
