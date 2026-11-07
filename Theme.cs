using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WebMConverter
{
    public enum ThemeMode
    {
        Automatic,
        Dark,
        Light,
    }

    /// <summary>
    /// Light/dark theming for WinForms (which has no dark mode of its own on .NET Framework).
    /// Every themed form is registered once; switching the mode re-themes all open forms.
    /// The light theme restores exactly what the designer set, so it looks like upstream.
    /// </summary>
    public static class Theme
    {
        #region Palette

        public static readonly Color DarkBack = Color.FromArgb(32, 32, 32);
        public static readonly Color DarkBackDeep = Color.FromArgb(22, 22, 22);
        public static readonly Color DarkInput = Color.FromArgb(45, 45, 48);
        public static readonly Color DarkButton = Color.FromArgb(55, 55, 58);
        public static readonly Color DarkBorder = Color.FromArgb(85, 85, 90);
        public static readonly Color DarkHover = Color.FromArgb(62, 62, 66);
        public static readonly Color DarkSelected = Color.FromArgb(0, 92, 160);
        public static readonly Color DarkText = Color.FromArgb(232, 232, 232);
        public static readonly Color DarkTextDim = Color.FromArgb(150, 150, 150);
        public static readonly Color DarkTextDisabled = Color.FromArgb(110, 110, 110);

        #endregion

        #region Mode

        static ThemeMode mode = ThemeMode.Automatic;
        static bool isDark;
        static readonly List<Form> forms = new List<Form>();

        /// <summary>Raised after the effective theme changed and all registered forms were re-themed.</summary>
        public static event EventHandler Changed;

        public static ThemeMode Mode
        {
            get => mode;
            set { mode = value; Refresh(); }
        }

        public static bool IsDark => isDark;

        static Theme()
        {
            Enum.TryParse(Properties.Settings.Default.ThemeMode, out mode);
            isDark = Evaluate();
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (mode == ThemeMode.Automatic && e.Category == UserPreferenceCategory.General)
                {
                    // Raised on a system thread; marshal to the UI thread of any open form
                    var form = forms.Count > 0 ? forms[0] : null;
                    if (form != null && form.IsHandleCreated)
                        form.BeginInvoke((Action)Refresh);
                }
            };
        }

        static bool Evaluate()
        {
            switch (mode)
            {
                case ThemeMode.Dark: return true;
                case ThemeMode.Light: return false;
                default: return SystemUsesDarkApps();
            }
        }

        /// <summary>Windows 10 1809+ "Choose your default app mode".</summary>
        public static bool SystemUsesDarkApps()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch
            {
                return false;
            }
        }

        static void Refresh()
        {
            bool dark = Evaluate();
            if (dark != isDark)
            {
                isDark = dark;
                ToolStripManager.Renderer = dark ? (ToolStripRenderer)new DarkToolStripRenderer() : new ToolStripProfessionalRenderer();
                foreach (var form in forms.ToArray())
                    Apply(form);
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Themes a form now and whenever the theme changes, including controls added to it later.
        /// Call once, right after InitializeComponent.
        /// </summary>
        public static void Register(Form form)
        {
            if (forms.Contains(form))
                return;
            forms.Add(form);
            form.FormClosed += (s, e) => forms.Remove(form);
            form.HandleCreated += (s, e) => SetDarkTitleBar(form, isDark);

            if (isDark && !(ToolStripManager.Renderer is DarkToolStripRenderer))
                ToolStripManager.Renderer = new DarkToolStripRenderer();

            Apply(form);
        }

        static void Apply(Form form)
        {
            form.SuspendLayout();
            ApplyRecursive(form);
            form.ResumeLayout(false);
            if (form.IsHandleCreated)
                SetDarkTitleBar(form, isDark);
            form.Invalidate(true);
        }

        static void ApplyRecursive(Control control)
        {
            ApplyTo(control);
            foreach (Control child in control.Controls)
                ApplyRecursive(child);
        }

        #endregion

        #region Per-control theming

        /// <summary>What the designer set, captured the first time a control is themed.</summary>
        sealed class Original
        {
            public Color Back, Fore;
            public bool BackSet, ForeSet;
            public FlatStyle? FlatStyle;
            public bool? UseVisualStyleBackColor;
            public ToolStripRenderMode? RenderMode;
            public BorderStyle? BorderStyle;
            public Color FlatBorder;
        }

        static readonly ConditionalWeakTable<Control, Original> originals = new ConditionalWeakTable<Control, Original>();

        static readonly MethodInfo shouldSerializeBack = typeof(Control).GetMethod("ShouldSerializeBackColor", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo shouldSerializeFore = typeof(Control).GetMethod("ShouldSerializeForeColor", BindingFlags.Instance | BindingFlags.NonPublic);

        static Original Capture(Control c)
        {
            return originals.GetValue(c, ctl =>
            {
                var o = new Original
                {
                    Back = ctl.BackColor,
                    Fore = ctl.ForeColor,
                    BackSet = (bool)shouldSerializeBack.Invoke(ctl, null),
                    ForeSet = (bool)shouldSerializeFore.Invoke(ctl, null),
                };
                if (ctl is ButtonBase b)
                {
                    o.FlatStyle = b.FlatStyle;
                    o.UseVisualStyleBackColor = b.UseVisualStyleBackColor;
                    o.FlatBorder = b.FlatAppearance.BorderColor;
                }
                if (ctl is ComboBox cb) o.FlatStyle = cb.FlatStyle;
                if (ctl is TabPage tp) o.UseVisualStyleBackColor = tp.UseVisualStyleBackColor;
                if (ctl is ToolStrip ts) o.RenderMode = ts.RenderMode;
                if (ctl is TextBoxBase tb) o.BorderStyle = tb.BorderStyle;

                // Theme controls that get added later (filter-specific panels, etc.)
                ctl.ControlAdded += (s, e) => ApplyRecursive(e.Control);
                return o;
            });
        }

        static void ApplyTo(Control c)
        {
            var o = Capture(c);
            if (isDark) ApplyDark(c, o);
            else ApplyLight(c, o);
        }

        static void ApplyLight(Control c, Original o)
        {
            if (o.BackSet) c.BackColor = o.Back; else c.ResetBackColor();
            if (o.ForeSet) c.ForeColor = o.Fore; else c.ResetForeColor();

            switch (c)
            {
                case ButtonBase b:
                    b.FlatStyle = o.FlatStyle.Value;
                    b.FlatAppearance.BorderColor = o.FlatBorder;
                    if (o.UseVisualStyleBackColor.Value) b.UseVisualStyleBackColor = true;
                    break;
                case ComboBox cb:
                    cb.FlatStyle = o.FlatStyle.Value;
                    break;
                case TextBoxBase tb:
                    tb.BorderStyle = o.BorderStyle.Value;
                    break;
                case TabPage tp:
                    tp.UseVisualStyleBackColor = o.UseVisualStyleBackColor.Value;
                    break;
                case TabControl tab:
                    DarkTabPainter.Detach(tab);
                    break;
                case ToolStrip ts:
                    ts.RenderMode = o.RenderMode.Value;
                    break;
            }

            if (c is TextBoxBase || c is ListView || c is TreeView || c is ListBox)
                SetControlTheme(c, "Explorer");
        }

        static void ApplyDark(Control c, Original o)
        {
            switch (c)
            {
                case Form f:
                    f.BackColor = DarkBack;
                    f.ForeColor = DarkText;
                    return;

                case TextBoxBase _:
                case ListView _:
                case ListBox _:
                case TreeView _:
                case NumericUpDown _:
                case DomainUpDown _:
                    c.BackColor = DarkInput;
                    c.ForeColor = DarkText;
                    if (c is TextBoxBase tb && tb.BorderStyle == BorderStyle.Fixed3D)
                        tb.BorderStyle = BorderStyle.FixedSingle; // the 3D border is glaring on dark
                    if (!(c is NumericUpDown) && !(c is DomainUpDown))
                        SetControlTheme(c, "DarkMode_Explorer"); // dark scrollbars
                    return;

                case ComboBox cb:
                    cb.FlatStyle = FlatStyle.Flat;
                    cb.BackColor = DarkInput;
                    cb.ForeColor = DarkText;
                    return;

                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.UseVisualStyleBackColor = false;
                    b.BackColor = DarkButton;
                    b.ForeColor = DarkText;
                    b.FlatAppearance.BorderColor = DarkBorder;
                    b.FlatAppearance.MouseOverBackColor = DarkHover;
                    return;

                case TabPage tp:
                    tp.UseVisualStyleBackColor = false;
                    tp.BackColor = DarkBack;
                    tp.ForeColor = DarkText;
                    return;

                case TabControl tab:
                    DarkTabPainter.Attach(tab);
                    break;

                case ToolStrip ts:
                    ts.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                    ts.BackColor = DarkBack;
                    ts.ForeColor = DarkText;
                    return;

                case GroupBox gb:
                    // A themed group box only uses ForeColor for its caption when it is set on the box itself
                    if (o.BackSet) gb.BackColor = MapBack(o.Back); else gb.ResetBackColor();
                    gb.ForeColor = o.ForeSet ? MapFore(o.Fore) : DarkText;
                    return;

                case ProgressBar _:
                    return;
            }

            // Plain surfaces (panels, labels, group boxes, check boxes, track bars, user controls):
            // inherit the form's colours unless the designer gave them their own.
            if (o.BackSet) c.BackColor = MapBack(o.Back); else c.ResetBackColor();
            if (o.ForeSet) c.ForeColor = MapFore(o.Fore); else c.ResetForeColor();

            if (c is TrackBar)
                c.BackColor = c.Parent?.BackColor ?? DarkBack; // track bars don't support transparent
        }

        static Color MapBack(Color color)
        {
            if (color == Color.Transparent || color.A < 255) return color;
            if (color.GetBrightness() < 0.3f) return color; // already dark (e.g. the preview canvas)
            if (color == SystemColors.ControlDark || color == SystemColors.ControlDarkDark || color == SystemColors.AppWorkspace)
                return DarkBackDeep;
            if (color == SystemColors.Window || color == SystemColors.ControlLightLight)
                return DarkInput;
            return DarkBack;
        }

        static Color MapFore(Color color)
        {
            if (color == SystemColors.GrayText) return DarkTextDim;
            if (color.GetBrightness() < 0.35f && color.GetSaturation() < 0.3f) return DarkText; // black-ish text
            return color; // keep coloured text (warnings etc.)
        }

        #endregion

        #region Native

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        static void SetDarkTitleBar(Form form, bool dark)
        {
            int value = dark ? 1 : 0;
            try
            {
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+), 19 on 1809-1909
                if (DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)) != 0)
                    DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            // Repaint the non-client area so the title bar switches immediately
            const int WM_NCACTIVATE = 0x86;
            if (form.Visible)
            {
                bool active = Form.ActiveForm == form;
                SendMessage(form.Handle, WM_NCACTIVATE, active ? IntPtr.Zero : new IntPtr(1), IntPtr.Zero);
                SendMessage(form.Handle, WM_NCACTIVATE, active ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
            }
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        static void SetControlTheme(Control c, string theme)
        {
            void set()
            {
                try { SetWindowTheme(c.Handle, theme, null); } catch (DllNotFoundException) { }
            }
            if (c.IsHandleCreated) set();
            else c.HandleCreated += (s, e) => { if (isDark == (theme != "Explorer")) set(); };
        }

        #endregion

        #region Tab control

        /// <summary>
        /// The native tab control paints its header strip and border in system colours. After it paints,
        /// this paints everything outside the page area again: background, border and the tab headers.
        /// </summary>
        sealed class DarkTabPainter : NativeWindow
        {
            static readonly ConditionalWeakTable<TabControl, DarkTabPainter> painters = new ConditionalWeakTable<TabControl, DarkTabPainter>();
            readonly TabControl tab;

            DarkTabPainter(TabControl tab)
            {
                this.tab = tab;
                if (tab.IsHandleCreated) AssignHandle(tab.Handle);
                tab.HandleCreated += OnHandleCreated;
                tab.HandleDestroyed += OnHandleDestroyed;
            }

            void OnHandleCreated(object s, EventArgs e) => AssignHandle(tab.Handle);
            void OnHandleDestroyed(object s, EventArgs e) => ReleaseHandle();

            public static void Attach(TabControl tab)
            {
                if (!painters.TryGetValue(tab, out _))
                    painters.Add(tab, new DarkTabPainter(tab));
                tab.Invalidate();
            }

            public static void Detach(TabControl tab)
            {
                if (painters.TryGetValue(tab, out var painter))
                {
                    tab.HandleCreated -= painter.OnHandleCreated;
                    tab.HandleDestroyed -= painter.OnHandleDestroyed;
                    painter.ReleaseHandle();
                    painters.Remove(tab);
                    tab.Invalidate();
                }
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                const int WM_PAINT = 0x000F;
                if (m.Msg == WM_PAINT)
                    using (var g = Graphics.FromHwnd(tab.Handle))
                        Paint(g);
            }

            void Paint(Graphics g)
            {
                var client = tab.ClientRectangle;
                var page = tab.DisplayRectangle;

                using (var region = new Region(client))
                {
                    region.Exclude(page);
                    g.Clip = region;
                    using (var back = new SolidBrush(DarkBack))
                        g.FillRectangle(back, client);

                    // A thin border around the page area
                    var frame = Rectangle.Inflate(page, 1, 1);
                    using (var pen = new Pen(DarkBorder))
                        g.DrawRectangle(pen, frame.X, frame.Y, frame.Width - 1, frame.Height - 1);

                    for (int i = 0; i < tab.TabCount; i++)
                    {
                        var r = tab.GetTabRect(i);
                        bool selected = i == tab.SelectedIndex;
                        if (selected) r = new Rectangle(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 3);

                        using (var fill = new SolidBrush(selected ? DarkBack : DarkBackDeep))
                            g.FillRectangle(fill, r);
                        using (var pen = new Pen(DarkBorder))
                        {
                            g.DrawLine(pen, r.Left, r.Bottom - (selected ? 0 : 1), r.Left, r.Top);
                            g.DrawLine(pen, r.Left, r.Top, r.Right - 1, r.Top);
                            g.DrawLine(pen, r.Right - 1, r.Top, r.Right - 1, r.Bottom - (selected ? 0 : 1));
                        }
                        if (selected)
                            using (var accent = new SolidBrush(DarkSelected))
                                g.FillRectangle(accent, r.Left + 1, r.Top + 1, r.Width - 2, 2);

                        TextRenderer.DrawText(g, tab.TabPages[i].Text, tab.Font, r,
                            selected ? DarkText : DarkTextDim,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    }
                    g.ResetClip();
                }
            }
        }

        #endregion

        #region Tool strips

        sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
        {
            public DarkToolStripRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? DarkText : DarkTextDisabled;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item?.Enabled != false ? DarkText : DarkTextDisabled;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is ToolStripDropDown)
                    using (var pen = new Pen(DarkBorder))
                        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                // no light border lines under tool/status strips
            }

            protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e) { }
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public DarkColors() { UseSystemColors = false; }

            public override Color ToolStripGradientBegin => DarkBack;
            public override Color ToolStripGradientMiddle => DarkBack;
            public override Color ToolStripGradientEnd => DarkBack;
            public override Color ToolStripBorder => DarkBack;
            public override Color StatusStripGradientBegin => DarkBack;
            public override Color StatusStripGradientEnd => DarkBack;
            public override Color ToolStripDropDownBackground => DarkInput;
            public override Color ToolStripContentPanelGradientBegin => DarkBack;
            public override Color ToolStripContentPanelGradientEnd => DarkBack;
            public override Color MenuStripGradientBegin => DarkBack;
            public override Color MenuStripGradientEnd => DarkBack;
            public override Color MenuBorder => DarkBorder;
            public override Color MenuItemBorder => DarkBorder;
            public override Color MenuItemSelected => DarkHover;
            public override Color MenuItemSelectedGradientBegin => DarkHover;
            public override Color MenuItemSelectedGradientEnd => DarkHover;
            public override Color MenuItemPressedGradientBegin => DarkInput;
            public override Color MenuItemPressedGradientMiddle => DarkInput;
            public override Color MenuItemPressedGradientEnd => DarkInput;
            public override Color ImageMarginGradientBegin => DarkInput;
            public override Color ImageMarginGradientMiddle => DarkInput;
            public override Color ImageMarginGradientEnd => DarkInput;
            public override Color ButtonSelectedHighlight => DarkHover;
            public override Color ButtonSelectedHighlightBorder => DarkBorder;
            public override Color ButtonSelectedBorder => DarkBorder;
            public override Color ButtonSelectedGradientBegin => DarkHover;
            public override Color ButtonSelectedGradientMiddle => DarkHover;
            public override Color ButtonSelectedGradientEnd => DarkHover;
            public override Color ButtonPressedHighlight => DarkSelected;
            public override Color ButtonPressedBorder => DarkBorder;
            public override Color ButtonPressedGradientBegin => DarkButton;
            public override Color ButtonPressedGradientMiddle => DarkButton;
            public override Color ButtonPressedGradientEnd => DarkButton;
            public override Color ButtonCheckedHighlight => DarkSelected;
            public override Color ButtonCheckedHighlightBorder => DarkBorder;
            public override Color ButtonCheckedGradientBegin => DarkButton;
            public override Color ButtonCheckedGradientMiddle => DarkButton;
            public override Color ButtonCheckedGradientEnd => DarkButton;
            public override Color CheckBackground => DarkButton;
            public override Color CheckSelectedBackground => DarkHover;
            public override Color CheckPressedBackground => DarkHover;
            public override Color SeparatorDark => DarkBorder;
            public override Color SeparatorLight => DarkBack;
            public override Color GripDark => DarkBorder;
            public override Color GripLight => DarkBack;
            public override Color OverflowButtonGradientBegin => DarkBack;
            public override Color OverflowButtonGradientMiddle => DarkBack;
            public override Color OverflowButtonGradientEnd => DarkBack;
        }

        #endregion
    }
}
