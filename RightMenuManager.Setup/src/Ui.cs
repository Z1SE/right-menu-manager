using System.Drawing.Drawing2D;

namespace RightMenuManager.Setup;

/// <summary>安装程序用的一套颜色，跟主程序保持同一个蓝调。</summary>
internal static class SetupTheme
{
    public static readonly Color Accent = Color.FromArgb(45, 127, 249);
    public static readonly Color AccentDark = Color.FromArgb(30, 95, 208);
    public static readonly Color AccentHover = Color.FromArgb(66, 143, 252);
    public static readonly Color Danger = Color.FromArgb(224, 76, 76);
    public static readonly Color DangerDark = Color.FromArgb(196, 58, 58);
    public static readonly Color Background = Color.FromArgb(245, 246, 248);
    public static readonly Color Surface = Color.White;
    public static readonly Color Ink = Color.FromArgb(32, 36, 44);
    public static readonly Color SubInk = Color.FromArgb(122, 130, 142);
    public static readonly Color Line = Color.FromArgb(220, 224, 230);
    public static readonly Color Disabled = Color.FromArgb(198, 203, 212);

    public static Font UiFont(float size, FontStyle style = FontStyle.Regular)
        => new("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
}

/// <summary>圆角扁平按钮。主按钮实心，次按钮白底描边。</summary>
internal sealed class FlatButton : Button
{
    private bool _hover;
    private bool _down;

    /// <summary>按钮背后那块底色，重绘时先铺上，避免出现方角。</summary>
    public Color SurfaceColor { get; set; } = SetupTheme.Background;

    public Color BaseColor { get; set; } = SetupTheme.Accent;

    public bool Primary { get; set; } = true;

    public int CornerRadius { get; set; } = 8;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        Font = SetupTheme.UiFont(9.5f);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(SurfaceColor)) g.FillRectangle(back, ClientRectangle);

        Color fill;
        Color text;
        if (!Enabled)
        {
            fill = Primary ? SetupTheme.Disabled : Color.FromArgb(250, 250, 252);
            text = Primary ? Color.White : SetupTheme.Disabled;
        }
        else if (Primary)
        {
            fill = _down ? BaseColor : (_hover ? SetupTheme.AccentHover : BaseColor);
            text = Color.White;
        }
        else
        {
            fill = _hover ? Color.FromArgb(237, 240, 245) : Color.White;
            text = SetupTheme.Ink;
        }

        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Rounded(box, CornerRadius))
        {
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            if (!Primary)
            {
                using var pen = new Pen(SetupTheme.Line);
                g.DrawPath(pen, path);
            }
        }

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { path.AddRectangle(r); return path; }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>顶部渐变标题条，左边画图标、右边写字。</summary>
internal sealed class HeaderPanel : Panel
{
    private readonly Icon? _icon;

    public string Caption { get; set; } = "";
    public string SubCaption { get; set; } = "";

    public HeaderPanel(Icon? icon)
    {
        _icon = icon;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = SetupTheme.AccentDark;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var brush = new LinearGradientBrush(ClientRectangle,
            SetupTheme.Accent, SetupTheme.AccentDark, LinearGradientMode.Horizontal))
        {
            g.FillRectangle(brush, ClientRectangle);
        }

        // 这里不做自动缩放，位置按当前 DPI 手工换算
        float k = DeviceDpi / 96f;
        int S(int v) => (int)Math.Round(v * k);

        int left = S(22);
        if (_icon != null)
        {
            int size = S(40);
            g.DrawIcon(_icon, new Rectangle(left, (Height - size) / 2, size, size));
            left += size + S(14);
        }

        var textRect = new Rectangle(left, 0, Math.Max(S(10), Width - left - S(18)), Height);
        bool twoLine = !string.IsNullOrEmpty(SubCaption);

        if (!twoLine)
        {
            TextRenderer.DrawText(g, Caption, SetupTheme.UiFont(15f, FontStyle.Bold), textRect,
                Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        int captionH = S(26);
        int subH = S(20);
        int blockTop = Math.Max(0, (Height - captionH - subH) / 2);

        var top = new Rectangle(textRect.X, blockTop, textRect.Width, captionH);
        var bottom = new Rectangle(textRect.X, blockTop + captionH, textRect.Width, subH);

        TextRenderer.DrawText(g, Caption, SetupTheme.UiFont(15f, FontStyle.Bold), top,
            Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, SubCaption, SetupTheme.UiFont(9f), bottom,
            Color.FromArgb(219, 231, 252), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
