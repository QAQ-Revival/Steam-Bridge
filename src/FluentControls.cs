using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EndfieldSteamBridge
{
    internal static class FluentTheme
    {
        public static readonly Color Canvas = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(239, 246, 248);
        public static readonly Color Layer = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(249, 249, 249);
        public static readonly Color Surface = SystemInformation.HighContrast ? SystemColors.Window : Color.White;
        public static readonly Color CardSurface = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(214, Color.White);
        public static readonly Color Ink = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(26, 26, 26);
        public static readonly Color Muted = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(96, 96, 96);
        public static readonly Color Border = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(225, 225, 229);
        public static readonly Color Accent = SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(0, 95, 184);
        public static readonly Color OnAccent = SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White;
        public static readonly Color AccentSoft = SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(232, 239, 245);
        public static readonly Color Success = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(15, 108, 47);
        public static readonly string IconFamily = FindIconFont();

        private static string FindIconFont()
        {
            using (var font = new Font("Segoe Fluent Icons", 12))
                return font.Name == "Segoe Fluent Icons" ? font.Name : "Segoe MDL2 Assets";
        }

        public static Font Font(float size) { return new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Point); }
        public static float Scale(Control control)
        {
            var form = control as MainForm ?? control.FindForm() as MainForm;
            return form == null ? control.DeviceDpi / 96f : form.LayoutScale;
        }
        public static int Px(Control control, int value) { return (int)Math.Round(value * Scale(control)); }

        public static GraphicsPath Round(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if (diameter <= 0) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void ApplyWindow(Form form)
        {
            if (SystemInformation.HighContrast) return;
            // Unsupported attributes return an HRESULT on Windows 10; native chrome remains intact.
            int corners = 2, caption = ColorTranslator.ToWin32(Canvas), text = ColorTranslator.ToWin32(Ink);
            DwmSetWindowAttribute(form.Handle, 33, ref corners, sizeof(int));
            DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(int));
        }
    }

    internal class FluentPanel : TableLayoutPanel
    {
        public FluentPanel()
        {
            DoubleBuffered = true; ResizeRedraw = true;
            BackColor = Color.Transparent; Margin = Padding.Empty;
        }
    }

    internal sealed class FluentCard : FluentPanel
    {
        public Color SurfaceColor { get; set; }
        public int CornerRadius { get; set; }
        public bool InputSurface { get; set; }
        public FluentCard() { SurfaceColor = FluentTheme.CardSurface; CornerRadius = 4; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = FluentTheme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), FluentTheme.Px(this, CornerRadius)))
            using (var fill = new SolidBrush(SurfaceColor))
            using (var border = new Pen(InputSurface && ContainsFocus ? FluentTheme.Accent : FluentTheme.Border))
            { e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(border, shape); }
        }
    }

    internal sealed class FluentPathBox : TextBox
    {
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x000F && m.Msg != 0x0318 && m.Msg != 0x0317) return;
            if (TextLength != 0) return;
            if (m.Msg == 0x000F)
                using (var graphics = Graphics.FromHwnd(Handle)) PaintPlaceholder(graphics);
            else if ((m.Msg == 0x0318 || m.Msg == 0x0317) && m.WParam != IntPtr.Zero)
                using (var graphics = Graphics.FromHdc(m.WParam)) PaintPlaceholder(graphics);
        }
        private void PaintPlaceholder(Graphics graphics)
        {
            TextRenderer.DrawText(graphics, "未检测到路径，请浏览选择文件", Font, ClientRectangle, FluentTheme.Muted,
                TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class FluentButton : Button
    {
        private bool hovered, pressed, selected;
        public bool Primary { get; set; }
        public bool Navigation { get; set; }
        public bool Quiet { get; set; }
        public string Glyph { get; set; }
        public string Subtitle { get; set; }
        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public FluentButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent; ForeColor = FluentTheme.Ink;
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand; Height = 36; UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { pressed = hovered = false; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool highlighted = Primary || (Navigation && Selected && SystemInformation.HighContrast);
            Color fill = highlighted ? FluentTheme.Accent : Selected ? FluentTheme.AccentSoft : FluentTheme.Surface;
            Color ink = highlighted ? FluentTheme.OnAccent : FluentTheme.Ink;
            if (!Enabled)
            {
                fill = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(241, 241, 241);
                ink = SystemColors.GrayText;
            }
            else if (!SystemInformation.HighContrast)
            {
                if (pressed) fill = highlighted ? Color.FromArgb(0, 78, 152) : Color.FromArgb(229, 229, 229);
                else if (hovered) fill = highlighted ? Color.FromArgb(25, 111, 191) : Selected ? Color.FromArgb(223, 235, 247) : Color.FromArgb(246, 246, 246);
            }
            using (var shape = FluentTheme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), FluentTheme.Px(this, Navigation ? 6 : 4)))
            {
                if ((!Navigation && !Quiet) || Selected || hovered || pressed)
                    using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, shape);
                if (!Navigation && !Quiet)
                    using (var pen = new Pen(highlighted && Enabled ? FluentTheme.Accent : FluentTheme.Border)) e.Graphics.DrawPath(pen, shape);
            }
            int left = FluentTheme.Px(this, Navigation || Quiet ? 14 : 12);
            if (Navigation && Selected)
            {
                int barHeight = FluentTheme.Px(this, 20);
                using (var bar = FluentTheme.Round(new RectangleF(FluentTheme.Px(this, 3), (Height - barHeight) / 2, FluentTheme.Px(this, 3), barHeight), FluentTheme.Px(this, 2)))
                using (var brush = new SolidBrush(SystemInformation.HighContrast ? ink : FluentTheme.Accent)) e.Graphics.FillPath(brush, bar);
            }
            if (!String.IsNullOrEmpty(Glyph))
            {
                int iconWidth = FluentTheme.Px(this, 22);
                using (var font = new Font(FluentTheme.IconFamily, (Navigation ? 20 : 14.67f) * FluentTheme.Scale(this), FontStyle.Regular, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(e.Graphics, Glyph, font, new Rectangle(left, 0, iconWidth, Height), ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                left += iconWidth + FluentTheme.Px(this, 10);
            }
            var bounds = new Rectangle(left, 0, Math.Max(0, Width - left - FluentTheme.Px(this, 12)), Height);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            if (!Navigation && !Quiet && String.IsNullOrEmpty(Glyph)) flags |= TextFormatFlags.HorizontalCenter;
            if (!String.IsNullOrEmpty(Subtitle))
            {
                bounds.Y = FluentTheme.Px(this, 11); bounds.Height = FluentTheme.Px(this, 23);
                TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ink, flags);
                bounds.Y += FluentTheme.Px(this, 25); bounds.Height = FluentTheme.Px(this, 18);
                using (var font = new Font("Segoe UI", 11.33f * FluentTheme.Scale(this), FontStyle.Regular, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(e.Graphics, Subtitle, font, bounds, Enabled ? (highlighted ? ink : FluentTheme.Muted) : SystemColors.GrayText, flags);
            }
            else TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ink, flags);
            if (Focused && ShowFocusCues)
            {
                int inset = FluentTheme.Px(this, 3);
                using (var shape = FluentTheme.Round(new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1), FluentTheme.Px(this, 3)))
                using (var pen = new Pen(highlighted ? ink : FluentTheme.Ink, FluentTheme.Px(this, 1))) e.Graphics.DrawPath(pen, shape);
            }
        }
    }

    // Keep the native combo box's keyboard, popup and accessibility behavior.
    internal sealed class FluentComboBox : ComboBox
    {
        private bool hovered;
        public FluentComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList; DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat; BackColor = FluentTheme.Surface; ForeColor = FluentTheme.Ink;
            IntegralHeight = false; DropDownHeight = 180;
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ItemHeight = Font.Height + FluentTheme.Px(this, 6); }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? FluentTheme.AccentSoft : FluentTheme.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
            Color ink = selected && SystemInformation.HighContrast ? FluentTheme.OnAccent : FluentTheme.Ink;
            var bounds = Rectangle.Inflate(e.Bounds, -FluentTheme.Px(this, 10), 0);
            TextRenderer.DrawText(e.Graphics, e.Index < 0 ? Text : GetItemText(Items[e.Index]), Font, bounds, ink, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F)
                using (var graphics = Graphics.FromHwnd(Handle)) PaintSurface(graphics);
            else if ((m.Msg == 0x0318 || m.Msg == 0x0317) && m.WParam != IntPtr.Zero)
                using (var graphics = Graphics.FromHdc(m.WParam)) PaintSurface(graphics);
        }
        private void PaintSurface(Graphics graphics)
        {
            if (Width < 2 || Height < 2) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // WM_PRINT can share its HDC with the whole form. Clear() would erase siblings.
            using (var background = new SolidBrush(FluentTheme.Surface)) graphics.FillRectangle(background, ClientRectangle);
            Color fill = !Enabled || hovered ? FluentTheme.Layer : FluentTheme.Surface;
            using (var shape = FluentTheme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), FluentTheme.Px(this, 4)))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(Focused ? FluentTheme.Accent : FluentTheme.Border))
            { graphics.FillPath(brush, shape); graphics.DrawPath(pen, shape); }
            Color ink = Enabled ? FluentTheme.Ink : SystemColors.GrayText;
            var text = new Rectangle(FluentTheme.Px(this, 10), 0, Math.Max(0, Width - FluentTheme.Px(this, 40)), Height);
            TextRenderer.DrawText(graphics, Text, Font, text, ink, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            float x = Width - FluentTheme.Px(this, 17), y = Height / 2f;
            using (var pen = new Pen(ink, Math.Max(1, FluentTheme.Scale(this))))
                graphics.DrawLines(pen, new[] { new PointF(x - FluentTheme.Px(this, 4), y - 2), new PointF(x, y + 2), new PointF(x + FluentTheme.Px(this, 4), y - 2) });
        }
    }

    internal sealed class FluentBadge : Control
    {
        private bool success;
        public bool Success { get { return success; } set { success = value; Invalidate(); } }
        public FluentBadge() { SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); BackColor = Color.Transparent; }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color ink = Success ? FluentTheme.Success : FluentTheme.Muted;
            Color fill = SystemInformation.HighContrast ? FluentTheme.Surface : Success ? Color.FromArgb(223, 246, 221) : Color.FromArgb(240, 240, 240);
            using (var shape = FluentTheme.Round(new RectangleF(0, 0, Width - 1, Height - 1), FluentTheme.Px(this, 4)))
            using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, shape);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
