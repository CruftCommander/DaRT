using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DaRT.Properties;

namespace DaRT
{
    // ThemeManager applies (or restores) a dark color theme to a Form and its full
    // control tree, driven by Settings.Default.darkMode.
    //
    // Usage: call ThemeManager.Apply(this) once a form's controls are fully built -
    // at the end of a dialog's constructor, or for GUImain (whose ListViews and
    // context menus are built later, in its Load handler) at the end of GUI_Load.
    // Call it again on any already-open form after toggling Settings.Default.darkMode
    // to re-theme it live. Each control's original designer colors are captured the
    // first time it is touched (keyed via ConditionalWeakTable) and restored exactly
    // when switching back to light, so toggling back and forth is lossless.
    //
    // ThemeManager.LogColor(color) is for use inside GUImain's RichTextBox logger:
    // a no-op in light mode, and in dark mode it lightens colors that would render
    // unreadably dark (e.g. near-black chat colors) against the dark background.
    //
    // Known limitations:
    // - ComboBox with DropDownStyle.DropDownList keeps native (light) popup-list
    //   chrome; WinForms renders that popup as a system listbox that ignores
    //   BackColor/ForeColor.
    // - GroupBox borders are drawn by the OS visual style and remain light-colored.
    // - ProgressBar chrome is drawn by the OS visual style and is not recolored.
    // - Text already appended to a RichTextBox before a theme switch keeps whatever
    //   color it was written with; only newly appended text is affected.
    // - A thin OS-drawn filler strip can remain visible past the last tab on an
    //   owner-drawn TabControl's header; this is native tab-strip chrome outside
    //   any single tab's paint rect and isn't worth hooking WM_PAINT to hide.
    // - ListView scrollbars only render dark on Win10 1809+/Win11 builds that honor
    //   the undocumented uxtheme SetPreferredAppMode opt-in (called once in
    //   Program.cs); on older or unsupported builds they remain light. This is
    //   cosmetic-only and silently falls back.
    public static class ThemeManager
    {
        private static readonly Color FormBack = Color.FromArgb(32, 32, 32);
        private static readonly Color SurfaceBack = Color.FromArgb(45, 45, 48);
        private static readonly Color InputBack = Color.FromArgb(51, 51, 55);
        private static readonly Color BorderColor = Color.FromArgb(67, 67, 70);
        private static readonly Color TextColor = Color.FromArgb(240, 240, 240);
        private static readonly Color LinkTextColor = Color.FromArgb(86, 156, 214);

        private class ControlState
        {
            public bool Captured;
            public Color BackColor;
            public Color ForeColor;
            public FlatStyle FlatStyle;
            public bool UseVisualStyleBackColor;
            public Color LinkColor;
            public TabDrawMode TabDrawMode;
            public TabSizeMode TabSizeMode;
            public Size TabItemSize;
        }

        private static readonly ConditionalWeakTable<Control, ControlState> _states = new ConditionalWeakTable<Control, ControlState>();
        private static ToolStripRenderer _lightRenderer;
        private static ToolStripProfessionalRenderer _darkRenderer;

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int LVM_GETHEADER = 0x101F;

        public static void Apply(Form form)
        {
            if (form == null)
                return;

            bool dark = Settings.Default.darkMode;

            ApplyControl(form, dark);
            foreach (Control child in AllControls(form))
                ApplyControl(child, dark);

            ApplyTitleBar(form, dark);
            ApplyToolStripRenderer(dark);
        }

        public static Color LogColor(Color color)
        {
            if (!Settings.Default.darkMode)
                return color;

            double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
            if (luminance >= 0.55)
                return color;

            const double factor = 0.65;
            int r = color.R + (int)((255 - color.R) * factor);
            int g = color.G + (int)((255 - color.G) * factor);
            int b = color.B + (int)((255 - color.B) * factor);
            return Color.FromArgb(color.A, r, g, b);
        }

        private static IEnumerable<Control> AllControls(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (Control grandchild in AllControls(child))
                    yield return grandchild;
            }
        }

        private static ControlState GetState(Control control)
        {
            ControlState state;
            if (!_states.TryGetValue(control, out state))
            {
                state = new ControlState();
                _states.Add(control, state);
            }
            return state;
        }

        private static void Capture(Control control, ControlState state)
        {
            if (state.Captured)
                return;

            state.BackColor = control.BackColor;
            state.ForeColor = control.ForeColor;

            Button button = control as Button;
            if (button != null)
            {
                state.FlatStyle = button.FlatStyle;
                state.UseVisualStyleBackColor = button.UseVisualStyleBackColor;
            }

            LinkLabel link = control as LinkLabel;
            if (link != null)
                state.LinkColor = link.LinkColor;

            TabControl tab = control as TabControl;
            if (tab != null)
            {
                state.TabDrawMode = tab.DrawMode;
                state.TabSizeMode = tab.SizeMode;
                state.TabItemSize = tab.ItemSize;
            }

            state.Captured = true;
        }

        private static void ApplyControl(Control control, bool dark)
        {
            ControlState state = GetState(control);
            Capture(control, state);

            if (control is Button)
                ApplyButton((Button)control, state, dark);
            else if (control is LinkLabel)
                ApplyLinkLabel((LinkLabel)control, state, dark);
            else if (control is CheckBox || control is RadioButton || control is GroupBox || control is Label)
                ApplyLabelLike(control, state, dark);
            else if (control is TextBox || control is RichTextBox || control is ListBox || control is ComboBox)
                ApplyInput(control, state, dark);
            else if (control is ListView)
                ApplyListView((ListView)control, state, dark);
            else if (control is TabControl)
                ApplyTabControl((TabControl)control, state, dark);
            else if (control is Form)
            {
                control.BackColor = dark ? FormBack : state.BackColor;
                control.ForeColor = dark ? TextColor : state.ForeColor;
            }
            else
                control.BackColor = dark ? SurfaceBack : state.BackColor; // Panel, PictureBox, TabPage, SplitContainer/SplitterPanel, etc.
        }

        private static void ApplyLabelLike(Control control, ControlState state, bool dark)
        {
            control.ForeColor = dark ? TextColor : state.ForeColor;

            if (dark)
                control.BackColor = state.BackColor == Color.Transparent ? Color.Transparent : FormBack;
            else
                control.BackColor = state.BackColor;
        }

        private static void ApplyButton(Button button, ControlState state, bool dark)
        {
            if (dark)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = BorderColor;
                button.FlatAppearance.MouseOverBackColor = InputBack;
                button.FlatAppearance.MouseDownBackColor = BorderColor;
                button.BackColor = SurfaceBack;
                button.ForeColor = TextColor;
            }
            else
            {
                button.FlatStyle = state.FlatStyle;
                button.UseVisualStyleBackColor = state.UseVisualStyleBackColor;
                button.BackColor = state.BackColor;
                button.ForeColor = state.ForeColor;
            }
        }

        private static void ApplyLinkLabel(LinkLabel link, ControlState state, bool dark)
        {
            if (dark)
            {
                link.ForeColor = TextColor;
                link.LinkColor = LinkTextColor;
                link.VisitedLinkColor = LinkTextColor;
                link.ActiveLinkColor = TextColor;
            }
            else
            {
                link.ForeColor = state.ForeColor;
                link.LinkColor = state.LinkColor;
            }
        }

        private static void ApplyInput(Control control, ControlState state, bool dark)
        {
            control.BackColor = dark ? InputBack : state.BackColor;
            control.ForeColor = dark ? TextColor : state.ForeColor;
            ApplyDarkScrollbar(control, dark);
        }

        private static void ApplyListView(ListView listView, ControlState state, bool dark)
        {
            listView.BackColor = dark ? InputBack : state.BackColor;
            listView.ForeColor = dark ? TextColor : state.ForeColor;

            listView.OwnerDraw = dark;
            listView.DrawColumnHeader -= ListViewDrawColumnHeader;
            listView.DrawItem -= ListViewDrawItem;
            listView.DrawSubItem -= ListViewDrawSubItem;

            if (dark)
            {
                listView.DrawColumnHeader += ListViewDrawColumnHeader;
                listView.DrawItem += ListViewDrawItem;
                listView.DrawSubItem += ListViewDrawSubItem;
            }

            listView.Invalidate();
            ApplyDarkScrollbar(listView, dark);
            ApplyDarkListViewHeader(listView, dark);
        }

        private static void ApplyDarkListViewHeader(ListView listView, bool dark)
        {
            try
            {
                IntPtr headerHandle = SendMessage(listView.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                if (headerHandle != IntPtr.Zero)
                    SetWindowTheme(headerHandle, dark ? "DarkMode_ItemsView" : "ItemsView", null);
            }
            catch
            {
                // Cosmetic only - ignore failures (unsupported OS, invalid handle, etc).
            }
        }

        private static void ListViewDrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (Brush backBrush = new SolidBrush(SurfaceBack))
                e.Graphics.FillRectangle(backBrush, e.Bounds);

            using (Pen borderPen = new Pen(BorderColor))
                e.Graphics.DrawRectangle(borderPen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);

            Rectangle textBounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(e.Graphics, e.Header.Text, e.Font, textBounds, TextColor, flags);
        }

        private static void ListViewDrawItem(object sender, DrawListViewItemEventArgs e)
        {
            e.DrawDefault = true;
        }

        private static void ListViewDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            e.DrawDefault = true;
        }

        private static void ApplyTabControl(TabControl tabControl, ControlState state, bool dark)
        {
            tabControl.DrawItem -= TabControlDrawItem;

            if (dark)
            {
                tabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
                tabControl.DrawItem += TabControlDrawItem;
                tabControl.SizeMode = TabSizeMode.Fixed;
                tabControl.ItemSize = MeasureTabItemSize(tabControl);
            }
            else
            {
                tabControl.DrawMode = state.TabDrawMode;
                tabControl.SizeMode = state.TabSizeMode;
                tabControl.ItemSize = state.TabItemSize;
            }

            tabControl.Invalidate();
        }

        private static Size MeasureTabItemSize(TabControl tabControl)
        {
            int maxTextWidth = 0;
            foreach (TabPage page in tabControl.TabPages)
            {
                int width = TextRenderer.MeasureText(page.Text, tabControl.Font).Width;
                if (width > maxTextWidth)
                    maxTextWidth = width;
            }

            int tabWidth = maxTextWidth + 24;
            int tabHeight = tabControl.Font.Height + 10;

            // For Alignment Left/Right, WinForms swaps ItemSize semantics: Width becomes
            // the on-screen tab height (perpendicular to the strip) and Height becomes
            // the on-screen tab width (along the strip, where the rotated text runs).
            if (tabControl.Alignment == TabAlignment.Left || tabControl.Alignment == TabAlignment.Right)
                return new Size(tabHeight, tabWidth);

            return new Size(tabWidth, tabHeight);
        }

        private static void TabControlDrawItem(object sender, DrawItemEventArgs e)
        {
            TabControl tabControl = sender as TabControl;
            if (tabControl == null || e.Index < 0 || e.Index >= tabControl.TabPages.Count)
                return;

            TabPage page = tabControl.TabPages[e.Index];
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            // The selected tab is rendered ~2px larger than GetTabRect() reports, so
            // inflate the fill/border to cover the system-painted sliver around it.
            Rectangle fillBounds = e.Bounds;
            if (selected)
                fillBounds.Inflate(2, 2);

            using (Brush backBrush = new SolidBrush(selected ? SurfaceBack : FormBack))
                e.Graphics.FillRectangle(backBrush, fillBounds);

            using (Pen borderPen = new Pen(BorderColor))
                e.Graphics.DrawRectangle(borderPen, fillBounds.X, fillBounds.Y, fillBounds.Width - 1, fillBounds.Height - 1);

            using (Brush textBrush = new SolidBrush(TextColor))
            {
                if (tabControl.Alignment == TabAlignment.Left || tabControl.Alignment == TabAlignment.Right)
                {
                    StringFormat format = new StringFormat();
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;

                    System.Drawing.Drawing2D.GraphicsState savedState = e.Graphics.Save();

                    PointF center = new PointF(e.Bounds.X + e.Bounds.Width / 2f, e.Bounds.Y + e.Bounds.Height / 2f);
                    e.Graphics.TranslateTransform(center.X, center.Y);
                    e.Graphics.RotateTransform(tabControl.Alignment == TabAlignment.Left ? -90 : 90);

                    RectangleF rotatedBounds = new RectangleF(-e.Bounds.Height / 2f, -e.Bounds.Width / 2f, e.Bounds.Height, e.Bounds.Width);
                    e.Graphics.DrawString(page.Text, tabControl.Font, textBrush, rotatedBounds, format);

                    e.Graphics.Restore(savedState);
                }
                else
                {
                    TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                    TextRenderer.DrawText(e.Graphics, page.Text, tabControl.Font, e.Bounds, TextColor, flags);
                }
            }
        }

        private static void ApplyDarkScrollbar(Control control, bool dark)
        {
            try
            {
                SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch
            {
                // Cosmetic only - ignore failures (unsupported OS, invalid handle, etc).
            }
        }

        private static void ApplyTitleBar(Form form, bool dark)
        {
            try
            {
                if (Environment.OSVersion.Version.Major < 10 || Environment.OSVersion.Version.Build < 17763)
                    return;

                int useDark = dark ? 1 : 0;
                int result = DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
                if (result != 0)
                    DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
            }
            catch
            {
                // Best-effort only; unsupported on this OS build or the call failed.
            }
        }

        private static void ApplyToolStripRenderer(bool dark)
        {
            if (_lightRenderer == null)
                _lightRenderer = ToolStripManager.Renderer;

            if (_darkRenderer == null)
                _darkRenderer = new ToolStripProfessionalRenderer(new DarkColorTable());

            ToolStripManager.Renderer = dark ? (ToolStripRenderer)_darkRenderer : _lightRenderer;
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return SurfaceBack; } }
            public override Color MenuItemSelectedGradientBegin { get { return SurfaceBack; } }
            public override Color MenuItemSelectedGradientEnd { get { return SurfaceBack; } }
            public override Color MenuItemPressedGradientBegin { get { return InputBack; } }
            public override Color MenuItemPressedGradientEnd { get { return InputBack; } }
            public override Color MenuItemBorder { get { return BorderColor; } }
            public override Color MenuBorder { get { return BorderColor; } }
            public override Color ToolStripDropDownBackground { get { return SurfaceBack; } }
            public override Color ImageMarginGradientBegin { get { return SurfaceBack; } }
            public override Color ImageMarginGradientMiddle { get { return SurfaceBack; } }
            public override Color ImageMarginGradientEnd { get { return SurfaceBack; } }
            public override Color SeparatorDark { get { return BorderColor; } }
            public override Color SeparatorLight { get { return BorderColor; } }
        }
    }
}
