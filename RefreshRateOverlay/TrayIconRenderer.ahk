#Requires AutoHotkey v2.0

SolveFontToFit(textStr, targetWidth, targetHeight) {
    hDC := DllCall("GetDC", "Ptr", 0, "Ptr")
    hMemDC := DllCall("CreateCompatibleDC", "Ptr", hDC, "Ptr")
    
    fontSize := -Floor(targetHeight * 0.90) 
    finalWidth := 0
    
    Loop {
        hFont := DllCall("CreateFont", "Int", fontSize, "Int", 0, "Int", 0, "Int", 0, "Int", 600
                       , "UInt", 0, "UInt", 0, "UInt", 0, "UInt", 1
                       , "UInt", 0, "UInt", 0, "UInt", 5, "UInt", 0
                       , "Str", "Segoe UI Variable Text", "Ptr")
        hOldFont := DllCall("SelectObject", "Ptr", hMemDC, "Ptr", hFont, "Ptr")
        
        sizeBuf := Buffer(8, 0)
        DllCall("GetTextExtentPoint32W", "Ptr", hMemDC, "Str", textStr, "Int", StrLen(textStr), "Ptr", sizeBuf)
        finalWidth := NumGet(sizeBuf, 0, "Int")
        
        DllCall("SelectObject", "Ptr", hMemDC, "Ptr", hOldFont)
        DllCall("DeleteObject", "Ptr", hFont)
        
        if (finalWidth <= targetWidth - 1) || (fontSize >= -6)
            break
            
        fontSize++
    }
    
    DllCall("DeleteDC", "Ptr", hMemDC)
    DllCall("ReleaseDC", "Ptr", 0, "Ptr", hDC)
    
    return { fontSize: fontSize, width: finalWidth }
}

CreateTextIcon(textStr) {
    IconSizeX := DllCall("GetSystemMetrics", "Int", 49) ; SM_CXSMICON
    IconSizeY := DllCall("GetSystemMetrics", "Int", 50) ; SM_CYSMICON
    
    if (IconSizeX == 0)
        IconSizeX := 16
    if (IconSizeY == 0)
        IconSizeY := 16
    
    bmi := Buffer(40, 0)
    NumPut("UInt", 40, bmi, 0)
    NumPut("Int", IconSizeX, bmi, 4)
    NumPut("Int", -IconSizeY, bmi, 8) ; Top-down
    NumPut("UShort", 1, bmi, 12)     ; Planes
    NumPut("UShort", 32, bmi, 14)    ; BitCount
    
    pBits := 0
    hDC := DllCall("GetDC", "Ptr", 0, "Ptr")
    hBitmap := DllCall("CreateDIBSection", "Ptr", hDC, "Ptr", bmi, "UInt", 0, "Ptr*", &pBits, "Ptr", 0, "UInt", 0, "Ptr")
    
    hMemDC := DllCall("CreateCompatibleDC", "Ptr", hDC, "Ptr")
    hOldBitmap := DllCall("SelectObject", "Ptr", hMemDC, "Ptr", hBitmap, "Ptr")
    
    fontData := SolveFontToFit(textStr, IconSizeX, IconSizeY)
    
    hFont := DllCall("CreateFont", "Int", fontData.fontSize, "Int", 0, "Int", 0, "Int", 0, "Int", 600
                   , "UInt", 0, "UInt", 0, "UInt", 0, "UInt", 1
                   , "UInt", 0, "UInt", 0, "UInt", 5, "UInt", 0
                   , "Str", "Segoe UI Variable Text", "Ptr")
    hOldFont := DllCall("SelectObject", "Ptr", hMemDC, "Ptr", hFont, "Ptr")
    
    DllCall("SetTextColor", "Ptr", hMemDC, "UInt", 0xFFFFFF)
    DllCall("SetBkColor", "Ptr", hMemDC, "UInt", 0x000000)
    DllCall("SetBkMode", "Ptr", hMemDC, "Int", 2) ; OPAQUE
    
    rect := Buffer(16, 0)
    NumPut("Int", 0, "Int", 0, "Int", IconSizeX, "Int", IconSizeY, rect)
    DllCall("DrawText", "Ptr", hMemDC, "Str", textStr, "Int", -1, "Ptr", rect, "UInt", 0x20 | 0x01 | 0x04)
    
    ; Parse the 32-bit DIB section to apply Alpha based on pixel luminance
    Loop IconSizeX * IconSizeY {
        offset := (A_Index - 1) * 4
        red := NumGet(pBits + offset + 2, "UChar")
        NumPut("UChar", red, pBits + offset + 3)
    }
    
    DllCall("SelectObject", "Ptr", hMemDC, "Ptr", hOldFont)
    DllCall("DeleteObject", "Ptr", hFont)
    DllCall("SelectObject", "Ptr", hMemDC, "Ptr", hOldBitmap)
    DllCall("DeleteDC", "Ptr", hMemDC)
    DllCall("ReleaseDC", "Ptr", 0, "Ptr", hDC)
    
    hbmMask := DllCall("CreateBitmap", "Int", IconSizeX, "Int", IconSizeY, "UInt", 1, "UInt", 1, "Ptr", 0, "Ptr")
    
    ii := Buffer(A_PtrSize == 8 ? 32 : 20, 0)
    NumPut("Int", 1, ii, 0)
    NumPut("Ptr", hbmMask, ii, A_PtrSize == 8 ? 16 : 12)
    NumPut("Ptr", hBitmap, ii, A_PtrSize == 8 ? 24 : 16)
    
    hIcon := DllCall("CreateIconIndirect", "Ptr", ii, "Ptr")
    
    DllCall("DeleteObject", "Ptr", hBitmap)
    DllCall("DeleteObject", "Ptr", hbmMask)
    
    return hIcon
}
