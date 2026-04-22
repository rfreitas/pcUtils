; =======================
; VerticalSlider Module
; =======================
; A reusable vertical slider GUI component with dark theme,
; smart positioning, and click-away dismissal.
;
; This is a DUMB VIEW - it handles only:
;   - GUI creation and styling
;   - Positioning above taskbar
;   - Show/Hide/Click-away dismissal
;
; The caller handles:
;   - Value interpretation (inversion, steps, etc.)
;   - Throttling and debouncing
;   - Label formatting
;
; Usage:
;   mySlider := VerticalSlider({
;       title: "Brightness",
;       min: 0, max: 100,
;       onChange: OnSliderChange
;   })
;   mySlider.SetLabel("50")
;   mySlider.SetRawValue(50)
;   mySlider.Show()
; =======================

class VerticalSlider {
    ; Instance properties
    gui := 0
    slider := 0
    label := 0
    visible := false
    lastHideTime := 0
    
    ; Configuration
    config := {}
    
    /**
     * Create a new VerticalSlider
     * @param options Object with: title, min, max, onChange
     */
    __New(options) {
        this.config := options
        this.config.title := options.HasProp("title") ? options.title : "Value"
        this.config.min := options.HasProp("min") ? options.min : 0
        this.config.max := options.HasProp("max") ? options.max : 100
        this.config.onChange := options.HasProp("onChange") ? options.onChange : (*) => {}
        
        this._CreateGui()
    }
    
    /**
     * Create the GUI elements
     */
    _CreateGui() {
        ; Create the GUI (dark theme)
        this.gui := Gui("+AlwaysOnTop -Caption +Border +ToolWindow", this.config.title)
        this.gui.BackColor := "2d2d2d"
        this.gui.MarginX := 10
        this.gui.MarginY := 15
        
        ; Add value text label at top
        this.gui.SetFont("s9 ccccccc", "Segoe UI")
        this.label := this.gui.AddText("vLabelText w40 Center", "")
        
        ; Add vertical slider
        this.gui.SetFont("s9", "Segoe UI")
        this.slider := this.gui.AddSlider("vSlider h150 w30 Range" this.config.min "-" this.config.max " Vertical AltSubmit", 0)
        this.slider.OnEvent("Change", (ctrl, *) => this._OnChange(ctrl))
        
        ; Close on Escape
        this.gui.OnEvent("Escape", (*) => this.Hide())
    }
    
    /**
     * Internal change handler - passes RAW value to callback
     * The caller is responsible for interpreting the value
     */
    _OnChange(ctrl) {
        this.config.onChange.Call(ctrl.Value)
    }
    
    ; =======================
    ; RAW CONTROL ACCESS
    ; =======================
    
    /**
     * Get the raw slider control value (0 at top, max at bottom)
     */
    GetRawValue() {
        return this.slider.Value
    }
    
    /**
     * Set the raw slider control value (does NOT trigger callback)
     */
    SetRawValue(val) {
        this.slider.Value := val
    }
    
    /**
     * Set the label text directly
     */
    SetLabel(text) {
        this.label.Value := text
    }
    
    /**
     * Get the label text
     */
    GetLabel() {
        return this.label.Value
    }
    
    ; =======================
    ; VISIBILITY CONTROL
    ; =======================
    
    /**
     * Show the slider at the current mouse position
     */
    Show() {
        ; Prevent reopening immediately after clicking to close
        if (A_TickCount - this.lastHideTime < 400)
            return
        
        if (this.visible) {
            this.Hide()
            return
        }
        
        ; Get mouse position
        CoordMode("Mouse", "Screen")
        MouseGetPos(&mx, &my)
        
        ; Calculate dimensions with DPI scaling
        dpiScale := A_ScreenDPI / 96
        guiW := Integer(60 * dpiScale)
        guiH := Integer(210 * dpiScale)
        
        ; --- Positioning Logic ---
        MonitorGetWorkArea(, , , , &workBottom)
        
        ; Determine effective screen bottom (handling auto-hide taskbars)
        isAutoHide := (workBottom >= A_ScreenHeight)
        minTaskbarHeight := Integer(48 * dpiScale)
        
        ; Try to detect actual taskbar position
        try {
            WinGetPos(, &tbY, , &tbH, "ahk_class Shell_TrayWnd")
            if (tbY > 0 && tbY < A_ScreenHeight) {
                finalBottom := tbY
            } else {
                finalBottom := A_ScreenHeight - minTaskbarHeight
            }
        } catch {
            finalBottom := A_ScreenHeight - minTaskbarHeight
        }
        
        ; Safety margin for Auto-Hide
        if (isAutoHide && (A_ScreenHeight - finalBottom) < minTaskbarHeight) {
            finalBottom := A_ScreenHeight - minTaskbarHeight
        }
        
        ; Final coordinates: Center on mouse X, adhere to bottom limit
        xPos := mx - (guiW // 2)
        yPos := finalBottom - guiH
        
        ; Force AlwaysOnTop
        this.gui.Opt("+AlwaysOnTop")
        this.gui.Show("x" xPos " y" yPos " NoActivate")
        this.visible := true
        
        ; Register this instance for click-away detection
        VerticalSlider._activeInstance := this
    }
    
    /**
     * Hide the slider
     */
    Hide() {
        this.gui.Hide()
        this.visible := false
        this.lastHideTime := A_TickCount
        
        if (VerticalSlider._activeInstance = this)
            VerticalSlider._activeInstance := 0
    }
    
    /**
     * Check if visible
     */
    IsVisible() {
        return this.visible
    }
    
    /**
     * Get the GUI's Hwnd (for external comparisons)
     */
    GetHwnd() {
        return this.gui.Hwnd
    }
    
    ; Static property for active instance (for click-away detection)
    static _activeInstance := 0
}

; =======================
; CLICK-AWAY DISMISSAL
; =======================
; This uses #HotIf to detect clicks outside any active slider

#HotIf VerticalSlider._activeInstance && VerticalSlider._activeInstance.visible
~LButton::
~RButton::
{
    instance := VerticalSlider._activeInstance
    if (instance) {
        MouseGetPos(,, &targetHwnd)
        if (targetHwnd != instance.gui.Hwnd) {
            instance.Hide()
        }
    }
}
#HotIf
