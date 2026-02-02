; =======================
; VerticalSlider Module
; =======================
; A reusable vertical slider GUI component with dark theme,
; smart positioning, and click-away dismissal.
;
; Usage:
;   #Include VerticalSlider.ahk
;   mySlider := VerticalSlider({
;       title: "Brightness",
;       min: 0,
;       max: 100,
;       value: 50,
;       onChange: (val) => DoSomething(val)
;   })
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
     * @param options Object with: title, min, max, value, onChange, steps (optional array)
     */
    __New(options) {
        this.config := options
        this.config.title := options.HasProp("title") ? options.title : "Value"
        this.config.min := options.HasProp("min") ? options.min : 0
        this.config.max := options.HasProp("max") ? options.max : 100
        this.config.value := options.HasProp("value") ? options.value : 50
        this.config.onChange := options.HasProp("onChange") ? options.onChange : (*) => {}
        this.config.steps := options.HasProp("steps") ? options.steps : []
        this.config.inverted := options.HasProp("inverted") ? options.inverted : true
        
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
        this.label := this.gui.AddText("vLabelText w40 Center", this._FormatValue(this.config.value))
        
        ; Add vertical slider
        this.gui.SetFont("s9", "Segoe UI")
        
        ; Determine range
        if (this.config.steps.Length > 0) {
            sliderMin := 1
            sliderMax := this.config.steps.Length
            sliderVal := this._ValueToSliderPos(this.config.value)
        } else {
            sliderMin := this.config.min
            sliderMax := this.config.max
            sliderVal := this.config.inverted ? (this.config.max - this.config.value) : this.config.value
        }
        
        this.slider := this.gui.AddSlider("vSlider h150 w30 Range" sliderMin "-" sliderMax " Vertical AltSubmit", sliderVal)
        this.slider.OnEvent("Change", (ctrl, *) => this._OnChange(ctrl))
        
        ; Close on Escape
        this.gui.OnEvent("Escape", (*) => this.Hide())
    }
    
    /**
     * Handle slider value change
     */
    _OnChange(ctrl) {
        if (this.config.steps.Length > 0) {
            ; Discrete steps mode (inverted: top=max step, bottom=min step)
            stepIdx := this.config.inverted ? (this.config.steps.Length + 1 - ctrl.Value) : ctrl.Value
            newVal := this.config.steps[stepIdx]
        } else {
            ; Continuous mode
            newVal := this.config.inverted ? (this.config.max - ctrl.Value) : ctrl.Value
        }
        
        this.config.value := newVal
        this.label.Value := this._FormatValue(newVal)
        this.config.onChange.Call(newVal)
    }
    
    /**
     * Convert a value to slider position (for discrete steps)
     */
    _ValueToSliderPos(val) {
        if (this.config.steps.Length = 0)
            return this.config.inverted ? (this.config.max - val) : val
        
        ; Find closest step
        for i, sVal in this.config.steps {
            if (val <= sVal) {
                return this.config.inverted ? (this.config.steps.Length + 1 - i) : i
            }
        }
        return this.config.inverted ? 1 : this.config.steps.Length
    }
    
    /**
     * Format value for display
     */
    _FormatValue(val) {
        ; If steps are durations (seconds), format as time
        if (this.config.steps.Length > 0 && this.config.steps[1] >= 15) {
            if (val < 60)
                return val . "s"
            m := Floor(val / 60)
            rem := Mod(val, 60)
            return m . "m" . (rem > 0 ? " " . rem . "s" : "")
        }
        return String(val)
    }
    
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
        
        ; Update slider position to match current value
        this.slider.Value := this._ValueToSliderPos(this.config.value)
        this.label.Value := this._FormatValue(this.config.value)
        
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
     * Set the value programmatically
     */
    SetValue(val) {
        this.config.value := val
        this.slider.Value := this._ValueToSliderPos(val)
        this.label.Value := this._FormatValue(val)
    }
    
    /**
     * Get the current value
     */
    GetValue() {
        return this.config.value
    }
    
    /**
     * Check if visible
     */
    IsVisible() {
        return this.visible
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
