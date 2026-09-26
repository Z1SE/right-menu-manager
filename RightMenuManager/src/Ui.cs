using System.Drawing.Drawing2D;
using System.Drawing;
using System.Windows.Forms;

namespace RightMenuManager;


/// <summary>控件在主题里承担什么底色。</summary>
internal enum Surface
{
    None,
    Page,
    Card,
}

/// <summary>控件在主题里承担什么文字色。</summary>
internal enum Ink
{
    None,
    Primary,
    Secondary,
    Danger,
}

/// <summary>挂在控件 Tag 上的主题角色。切主题时按它重新上色。</summary>
internal readonly record struct ThemeSpec(Surface Surface = Surface.None, Ink Ink = Ink.None);

/// <summary>
/// 界面配色。白天／黑夜两套，切换时由 <see cref="Apply"/> 递归刷一遍控件。
/// </summary>
internal static class Theme
{
    // ------------------------------------------------------------ 配色
    private static readonly Color PageColor = Color.FromArgb(243, 243, 243);
    private static readonly Color CardColor = Color.White;
    private static readonly Color TextColor = Color.FromArgb(27, 27, 27);
    private static readonly Color MutedColor = Color.FromArgb(97, 97, 97);
    private static readonly Color BorderColor = Color.FromArgb(214, 214, 214);
    private static readonly Color WarnColor = Color.FromArgb(255, 244, 206);
    private static readonly Color DangerColor = Color.FromArgb(253, 231, 233);
    private static readonly Color DisabledColor = Color.FromArgb(150, 150, 150);

    public static Color Page => PageColor;
    public static Color Card => CardColor;
    public static Color Text => TextColor;
    public static Color Muted => MutedColor;
    public static Color Border => BorderColor;
    public static Color WarnBg => WarnColor;
    public static Color DangerBg => DangerColor;
    public static Color DisabledText => DisabledColor;

    public static readonly Color Accent = Color.FromArgb(15, 108, 189);
    public static readonly Color Danger = Color.FromArgb(196, 43, 28);

    /// <summary>
    /// 按 Tag 上的角色把整棵控件树重新上一遍色。
    /// 圆角按钮的四个角是用父容器底色补出来的，父容器换色后必须让它重画，否则会留下旧色的方角。
    /// </summary>
    public static void Apply(Control root)
    {
        if (root.Tag is ThemeSpec spec)
        {
            if (spec.Surface != Surface.None)
            {
                root.BackColor = spec.Surface == Surface.Page ? Page : Card;
            }

            if (spec.Ink != Ink.None)
            {
                root.ForeColor = spec.Ink switch
                {
                    Ink.Secondary => Muted,
                    Ink.Danger => Danger,
                    _ => Text,
                };
            }
        }

        if (root is RoundedButton button) button.Invalidate();

        foreach (Control child in root.Controls) Apply(child);
    }

    public static Font UiFont(float size = 9f, FontStyle style = FontStyle.Regular)
    {
        try { return new Font("Microsoft YaHei UI", size, style); }
        catch { return new Font(FontFamily.GenericSansSerif, size, style); }
    }

    /// <summary>
    /// 按钮配色：每个按钮一种颜色，方便一眼区分。
    /// 颜色本身带一点语义，避免纯装饰——危险动作用红系，只读/查看用蓝灰系，
    /// 安全动作用绿系，好让人在按下去之前就知道轻重。
    /// </summary>
    internal static class Palette
    {
        public static readonly Color Blue = Color.FromArgb(15, 108, 189);      // 主操作
        public static readonly Color Green = Color.FromArgb(16, 124, 65);      // 备份（安全）
        public static readonly Color Red = Color.FromArgb(196, 43, 28);        // 删除（危险）
        public static readonly Color Orange = Color.FromArgb(179, 92, 0);      // 提权
        public static readonly Color Purple = Color.FromArgb(107, 79, 187);    // 款式切换
        public static readonly Color Teal = Color.FromArgb(14, 124, 134);      // 停用/恢复
        public static readonly Color Indigo = Color.FromArgb(46, 90, 172);     // 复制
        public static readonly Color Slate = Color.FromArgb(85, 96, 110);      // 打开外部程序 / 主题
        public static readonly Color Brown = Color.FromArgb(122, 92, 46);      // 打开文件夹
        public static readonly Color Emerald = Color.FromArgb(31, 122, 107);   // 刷新
        public static readonly Color Magenta = Color.FromArgb(138, 63, 168);   // 还原
        public static readonly Color Rose = Color.FromArgb(179, 56, 107);      // 删除备份
        public static readonly Color Cyan = Color.FromArgb(14, 116, 144);      // 批量选择
        public static readonly Color Olive = Color.FromArgb(95, 111, 31);      // 全选/清空
        public static readonly Color DarkTeal = Color.FromArgb(0, 105, 92);    // 重启资源管理器
    }

    /// <summary>造一个圆角彩色按钮。宽度由文字自动决定，不会出现半个字。</summary>
    public static RoundedButton MakeButton(string text, Color background, int height = 32)
    {
        var button = new RoundedButton
        {
            Text = text,
            BackColor = background,
            ForeColor = Color.White,
            Font = UiFont(9f),
            Height = height,
            CornerRadius = 8,
            // 按钮底色是功能色，不受主题影响；打标记是为了让它在切主题时重绘
            Tag = new ThemeSpec(),
        };
        return button;
    }

    /// <summary>造一个跟随主题的标签。传 Muted / Danger 会映射成对应的主题角色。</summary>
    public static Label MakeLabel(string text, float size = 9f, Color? color = null, FontStyle style = FontStyle.Regular)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = UiFont(size, style),
            ForeColor = color ?? Text,
            BackColor = Color.Transparent,
        };

        // 注意：这里比较的是「创建那一刻」的颜色值，那时候两者是同一个主题，等得上。
        // 切主题时靠的是记下来的角色，不会再拿颜色去比 —— 比也不准（值已经变了）。
        Ink ink = Ink.Primary;
        if (color.HasValue)
        {
            if (color.Value == Muted) ink = Ink.Secondary;
            else if (color.Value == Danger) ink = Ink.Danger;
        }

        label.Tag = new ThemeSpec(Ink: ink);
        return label;
    }
}


/// <summary>
/// 圆角按钮。
///
/// 为什么要自己画：WinForms 自带的按钮只能是直角，而且宽度写死之后
/// 一旦字体或缩放变大，文字就会被裁掉（只显示半个字）。
/// 这里做了两件事：
///   ① 按钮宽度按文字实际占用的宽度自动撑开，永远够显示完整；
///   ② 背景色由外部指定，每个按钮可以不一样。
/// </summary>
internal sealed class RoundedButton : Button
{
    private bool _hover;
    private bool _pressed;
    private bool _autoFitWidth = true;

    public RoundedButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw, true);

        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;

        BackColor = Color.FromArgb(15, 108, 189);
        ForeColor = Color.White;
        Height = 32;
        Margin = new Padding(0, 0, 8, 6);
        Cursor = Cursors.Hand;
    }

    /// <summary>圆角半径。</summary>
    public int CornerRadius { get; set; } = 8;

    /// <summary>描边颜色；不设就按背景色自动算一个略深的。</summary>
    public Color BorderColor { get; set; } = Color.Empty;

    /// <summary>鼠标悬停时的填充色；不设就按背景色自动调亮。</summary>
    public Color HoverColor { get; set; } = Color.Empty;

    /// <summary>按下时的填充色；不设就按背景色自动调暗。</summary>
    public Color PressedColor { get; set; } = Color.Empty;

    /// <summary>文字左右各留多少空白。</summary>
    public int HorizontalPadding { get; set; } = 18;

    /// <summary>是否按文字自动决定宽度（默认开）。</summary>
    public bool AutoFitWidth
    {
        get => _autoFitWidth;
        set
        {
            _autoFitWidth = value;
            if (value) FitToText();
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        FitToText();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        FitToText();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FitToText();
    }

    /// <summary>
    /// 按文字实际宽度把按钮撑开。用真实字体去量，所以缩放变了也不会裁字。
    /// </summary>
    public void FitToText()
    {
        if (!_autoFitWidth) return;

        string text = string.IsNullOrEmpty(Text) ? " " : Text;
        Size measured = TextRenderer.MeasureText(
            text, Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        int width = measured.Width + HorizontalPadding * 2;
        if (width < 48) width = 48;
        if (Width != width) Width = width;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        Graphics g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 圆角以外的四个角要还原成父容器的底色，否则会留下方块残影
        g.Clear(Parent?.BackColor ?? Theme.Card);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width <= 2 || rect.Height <= 2) return;

        using GraphicsPath path = CreateRoundedPath(rect, CornerRadius);

        Color baseColor = Enabled ? BackColor : Blend(BackColor, Theme.Page, 0.62f);
        Color fill = _pressed
            ? (PressedColor.IsEmpty ? Blend(baseColor, Color.Black, 0.20f) : PressedColor)
            : _hover
                ? (HoverColor.IsEmpty ? Blend(baseColor, Color.White, 0.16f) : HoverColor)
                : baseColor;

        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
        }

        if (Enabled)
        {
            using var pen = new Pen(
                BorderColor.IsEmpty ? Blend(baseColor, Color.Black, 0.18f) : BorderColor, 1f);
            g.DrawPath(pen, path);
        }

        if (Focused && ShowFocusCues)
        {
            var inner = Rectangle.Inflate(rect, -3, -3);
            if (inner.Width > 4 && inner.Height > 4)
            {
                using GraphicsPath focusPath = CreateRoundedPath(inner, Math.Max(2, CornerRadius - 2));
                using var focusPen = new Pen(Color.FromArgb(170, Color.White), 1f)
                {
                    DashStyle = DashStyle.Dot,
                };
                g.DrawPath(focusPen, focusPath);
            }
        }

        TextRenderer.DrawText(
            g, Text, Font, rect,
            Enabled ? ForeColor : Color.FromArgb(238, 238, 238),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 2)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Blend(Color from, Color to, float amount)
    {
        return Color.FromArgb(
            from.A,
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }
}

/// <summary>
/// 菜单栏的配色表。WinForms 的菜单和下拉默认是系统浅色，
/// 深色模式下一弹出来就是一片白，必须整张表换掉。
/// </summary>
internal sealed class ThemedColorTable : ProfessionalColorTable
{
    public override Color MenuStripGradientBegin => Theme.Card;
    public override Color MenuStripGradientEnd => Theme.Card;
    public override Color MenuItemSelected => Theme.Page;
    public override Color MenuItemSelectedGradientBegin => Theme.Page;
    public override Color MenuItemSelectedGradientEnd => Theme.Page;
    public override Color MenuItemPressedGradientBegin => Theme.Page;
    public override Color MenuItemPressedGradientEnd => Theme.Page;
    public override Color MenuItemBorder => Theme.Accent;
    public override Color MenuBorder => Theme.Border;
    public override Color ToolStripDropDownBackground => Theme.Card;
    public override Color ToolStripBorder => Theme.Border;
    public override Color ImageMarginGradientBegin => Theme.Card;
    public override Color ImageMarginGradientMiddle => Theme.Card;
    public override Color ImageMarginGradientEnd => Theme.Card;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;
}

/// <summary>菜单渲染器：文字颜色也交给主题，否则深色底上会写着深色字。</summary>
internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemedMenuRenderer() : base(new ThemedColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.DisabledText;
        base.OnRenderItemText(e);
    }
}
