using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace RightMenuManager;


internal sealed partial class MainForm : Form
{
    // ---- 数据
    private readonly List<MenuEntry> _all = new();
    private List<MenuEntry> _view = new();
    private int _sortColumn = -1;
    private bool _sortAscending = true;
    private bool _busy;

    /// <summary>批量选择模式：打开后每行前面有勾选框，不用按 Ctrl。</summary>
    private bool _bulkMode;

    /// <summary>界面搭完之前不响应筛选，否则会去用还没造出来的控件。</summary>
    private bool _uiReady;

    private readonly ToolTip _hints = new();

    internal const string AnySource = "全部来源";
    internal const string AnyPlacement = "全部位置";

    /// <summary>「在哪儿点右键」的可选值，顺序按常见程度排。</summary>
    internal static readonly string[] PlacementChoices =
    {
        AnyPlacement,
        "右键文件时",
        "右键文件夹时",
        "右键文件夹空白处",
        "右键桌面空白处",
        "右键快捷方式时",
        "右键程序时",
        "Win11 现代菜单",
        "右键文件和文件夹时",
        "右键驱动器时",
        "「发送到」菜单",
        "仅某种文件类型",
        // 兜底：凡是没能归到上面任何一类的，都得有地方能选中它，
        // 否则那几项就永远筛不出来、等于被藏起来了
        "其他位置",
    };

    // ---- 菜单项页
    private TextBox _search = null!;
    private ComboBox _sourceFilter = null!;
    private ComboBox _placementFilter = null!;
    private CheckBox _onlyOrphan = null!;
    private CheckBox _showDisabled = null!;
    private CheckBox _includeFileTypes = null!;
    private RoundedButton _bulkButton = null!;
    private RoundedButton _selectAllButton = null!;
    private ListView _list = null!;

    // ---- 程序图标
    private readonly ImageList _icons = new();
    private readonly Dictionary<string, int> _iconIndexCache = new(StringComparer.OrdinalIgnoreCase);
    private SplitContainer _split = null!;
    private Panel _detailPanel = null!;
    private Panel _detailInfo = null!;
    private Label _detailName = null!;
    private Label _detailMeta = null!;
    private Label _commandLabel = null!;
    private Label _pathLabel = null!;
    private TextBox _detailCommand = null!;
    private TextBox _detailPath = null!;
    private TextBox _detailWarn = null!;
    private RoundedButton _btnToggle = null!;
    private RoundedButton _btnDelete = null!;
    private RoundedButton _btnCopyPath = null!;
    private RoundedButton _btnOpenRegedit = null!;

    // ---- 备份页
    private ListView _backupList = null!;
    private Label _backupFolderLabel = null!;
    private RoundedButton _btnRestore = null!;

    // ---- 通用
    private Label _permLabel = null!;
    private RoundedButton _elevateButton = null!;
    private RoundedButton _classicButton = null!;
    private RoundedButton _restartExplorerButton = null!;
    private MenuStrip _menu = null!;
    private ToolStripStatusLabel _statusMain = null!;
    private ToolStripStatusLabel _statusCounts = null!;

    public MainForm()
    {
        // 先把用户上次选的备份位置读回来
        BackupManager.LoadSettings();

        Text = "Windows 11 右键菜单管理器";
        Width = 1200;
        Height = 800;
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.UiFont(9f);
        BackColor = Theme.Page;
        // 关掉自动缩放：它会把代码里设的尺寸再按 DPI 缩放一次，和 FitWindowToScreen
        // 里已经乘过 DPI 的尺寸打架，结果窗口反而变小。尺寸统一由 FitWindowToScreen 决定。
        AutoScaleMode = AutoScaleMode.None;

        BuildLayout();
        UpdatePermissionUi();
        ApplyTheme();
        _uiReady = true;

        Load += (_, _) =>
        {
            FitWindowToScreen();
            ConfigureIconList();
            ApplyInitialSplitter();
        };
        Shown += async (_, _) => await RefreshScanAsync();
    }

    // ================================================================= 布局

    private void BuildLayout()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = Theme.Card,
            Tag = new ThemeSpec(Surface: Surface.Card),
        };

        // 这里原本有一行 15pt 的大标题，去掉了：窗口标题栏和菜单栏都已经表明
        // 这是什么程序，顶上再挂一遍只是白占一行高度。
        // 副标题也压短了 —— 原来那句话太长，会顶到右边的权限标签上。
        Label subtitle = Theme.MakeLabel("看一眼右键菜单里被塞了些什么。删除前都会自动备份。", 9f, Theme.Muted);
        subtitle.Location = new Point(26, 22);
        subtitle.Padding = new Padding(0, 2, 0, 0);
        header.Controls.Add(subtitle);

        _classicButton = Theme.MakeButton("切换完整右键菜单", Theme.Palette.Purple, 34);
        _classicButton.Click += (_, _) => ToggleClassicMenu();

        _restartExplorerButton = Theme.MakeButton("重启资源管理器", Theme.Palette.DarkTeal, 34);
        _restartExplorerButton.Click += (_, _) => RestartExplorerNow();

        _elevateButton = Theme.MakeButton("以管理员身份重启", Theme.Palette.Orange, 34);
        _elevateButton.Click += (_, _) => ElevateAndRestart();

        _permLabel = Theme.MakeLabel("", 9f, Theme.Muted);
        _permLabel.Margin = new Padding(0, 4, 18, 0);

        // 右上角操作区用流式布局从右往左排：按钮宽度会随文字自动变化，
        // 加多少按钮、字体怎么变，都不会互相挤掉或裁字。
        var headerActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Card,
            Padding = new Padding(0, 15, 24, 0),
            Tag = new ThemeSpec(Surface: Surface.Card),
        };
        headerActions.Controls.Add(_classicButton);        // 最右
        headerActions.Controls.Add(_restartExplorerButton);
        headerActions.Controls.Add(_elevateButton);
        headerActions.Controls.Add(_permLabel);            // 最左
        header.Controls.Add(headerActions);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = Theme.UiFont(9.5f),
            Padding = new Point(16, 6),
            BackColor = Theme.Page,
            Tag = new ThemeSpec(Surface: Surface.Page),
            // 选项卡标题栏的颜色 WinForms 不会跟着主题走，只能自己画
            DrawMode = TabDrawMode.OwnerDrawFixed,
        };
        tabs.DrawItem += (_, e) => DrawTabHeader(tabs, e);
        tabs.TabPages.Add(BuildMenuItemsPage());
        tabs.TabPages.Add(BuildBackupPage());

        var status = new StatusStrip { BackColor = Theme.Card, SizingGrip = false, Tag = new ThemeSpec(Surface: Surface.Card) };
        _statusMain = new ToolStripStatusLabel("准备就绪")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _statusCounts = new ToolStripStatusLabel("");
        status.Items.Add(_statusMain);
        status.Items.Add(_statusCounts);

        // Fill 的先加，顶部的最后加，Dock 停靠顺序才对。
        // 菜单栏要在最顶上，所以它最后加。
        Controls.Add(tabs);
        Controls.Add(status);
        Controls.Add(header);
        _menu = BuildMenuBar();
        Controls.Add(_menu);
    }

    /// <summary>
    /// 顶部文字菜单栏。菜单项不引入新功能，只是把已有的操作按名字再挂一份，
    /// 让人不用满界面找按钮。
    /// </summary>
    private MenuStrip BuildMenuBar()
    {
        MenuStrip menu = new()
        {
            Dock = DockStyle.Top,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            Font = Theme.UiFont(9f),
            Padding = new Padding(8, 2, 0, 2),
            Renderer = new ThemedMenuRenderer(),
            Tag = new ThemeSpec(Surface: Surface.Card),
        };

        ToolStripMenuItem file = new("文件(&F)");
        file.DropDownItems.Add(MenuItem("备份全部菜单项…", BackupAll));
        file.DropDownItems.Add(MenuItem("更改备份位置…", ChangeBackupFolder));
        file.DropDownItems.Add(MenuItem("打开备份文件夹", OpenBackupFolder));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MenuItem("退出", Close));

        ToolStripMenuItem edit = new("编辑(&E)");
        edit.DropDownItems.Add(MenuItem("停用选中的项", ToggleSelected));
        edit.DropDownItems.Add(MenuItem("删除选中的项（先备份）", DeleteSelected));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(MenuItem("批量选择模式", ToggleBulkMode));
        edit.DropDownItems.Add(MenuItem("全选 / 清空勾选", ToggleCheckAll));

        ToolStripMenuItem view = new("视图(&V)");
        view.DropDownItems.Add(MenuItem("重新扫描", () => _ = RefreshScanAsync()));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(MenuItem("只看第三方软件", () => SetSourceFilter("第三方软件")));
        view.DropDownItems.Add(MenuItem("只看 Windows 自带", () => SetSourceFilter("Windows 自带")));
        view.DropDownItems.Add(MenuItem("显示全部来源", () => SetSourceFilter(AnySource)));
        view.DropDownItems.Add(MenuItem("只看失效残留（开/关）", () => _onlyOrphan.Checked = !_onlyOrphan.Checked));
        view.DropDownItems.Add(MenuItem("含文件类型专属项（开/关）", () => _includeFileTypes.Checked = !_includeFileTypes.Checked));

        ToolStripMenuItem tools = new("工具(&T)");
        tools.DropDownItems.Add(MenuItem("重启资源管理器", RestartExplorerNow));
        tools.DropDownItems.Add(MenuItem("切换完整右键菜单", ToggleClassicMenu));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(MenuItem("复制选中项的注册表路径", CopySelectedPath));
        tools.DropDownItems.Add(MenuItem("打开注册表编辑器", OpenRegedit));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(MenuItem("以管理员身份重启程序", ElevateAndRestart));

        ToolStripMenuItem help = new("帮助(&H)");
        help.DropDownItems.Add(MenuItem("使用说明", ShowHelp));
        help.DropDownItems.Add(MenuItem("关于", ShowAbout));

        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(view);
        menu.Items.Add(tools);
        menu.Items.Add(help);
        return menu;
    }

    private static ToolStripMenuItem MenuItem(string text, Action action)
    {
        ToolStripMenuItem item = new(text);
        item.Click += (_, _) => action();
        return item;
    }

    private void SetSourceFilter(string value)
    {
        _sourceFilter.SelectedItem = value;
        ApplyFilter();
    }

    private void ShowHelp()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "使用说明.txt");
        if (File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            catch
            {
                // 打不开就退回弹窗
            }
        }

        MessageBox.Show(this,
            "常用操作：\r\n\r\n"
            + "· 上面两个下拉：按「谁装的」和「在哪儿右键」筛\r\n"
            + "· 批量选择：勾选多项后一次处理\r\n"
            + "· 停用：不删任何东西，随时恢复\r\n"
            + "· 删除：一定先备份，存到「我的文档\\右键菜单备份」\r\n"
            + "· 改完点右上角「重启资源管理器」才会生效",
            "使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowAbout()
    {
        Version? version = typeof(MainForm).Assembly.GetName().Version;

        MessageBox.Show(this,
            "Windows 11 右键菜单管理器\r\n\r\n"
            + "把右键菜单里平时看不见的东西列出来，让你决定留还是清。\r\n"
            + "所有删除都会先自动备份；程序不联网、不收集任何信息。\r\n\r\n"
            + "版本：" + (version?.ToString() ?? "未知"),
            "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private TabPage BuildMenuItemsPage()
    {
        var page = new TabPage("菜单项") { BackColor = Theme.Page, Padding = new Padding(12), Tag = new ThemeSpec(Surface: Surface.Page) };

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Theme.Card, Tag = new ThemeSpec(Surface: Surface.Card) };

        // ---- 第一行：搜索、位置筛选、重新扫描
        var row1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Card,
            Padding = new Padding(12, 8, 12, 0),
            Tag = new ThemeSpec(Surface: Surface.Card),
        };

        _search = new TextBox
        {
            Width = 250,
            Font = Theme.UiFont(9f),
            PlaceholderText = "按名称或程序路径筛选…",
            Margin = new Padding(0, 4, 14, 0),
        };
        _search.TextChanged += (_, _) => ApplyFilter();
        row1.Controls.Add(_search);

        // 「这东西是谁装的」
        _sourceFilter = new ComboBox
        {
            Width = 170,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Theme.UiFont(9f),
            Margin = new Padding(0, 3, 12, 0),
        };
        _sourceFilter.Items.Add(AnySource);
        _sourceFilter.Items.Add("Windows 自带");
        _sourceFilter.Items.Add("第三方软件");
        _sourceFilter.SelectedIndex = 0;
        _sourceFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        row1.Controls.Add(_sourceFilter);

        // 「你在哪儿点右键」
        _placementFilter = new ComboBox
        {
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Theme.UiFont(9f),
            Margin = new Padding(0, 3, 12, 0),
        };
        foreach (string choice in PlacementChoices) _placementFilter.Items.Add(choice);
        _placementFilter.SelectedIndex = 0;
        _placementFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        row1.Controls.Add(_placementFilter);

        _hints.SetToolTip(_sourceFilter, "按「谁装的」筛选：Windows 自带的，还是各种软件塞进来的。");
        _hints.SetToolTip(_placementFilter, "按「你在哪儿点右键」筛选。选「右键文件夹时」，列出的就是右击文件夹会看到的那些项。");

        RoundedButton refresh = Theme.MakeButton("重新扫描", Theme.Palette.Blue, 30);
        refresh.Margin = new Padding(0, 2, 8, 6);
        refresh.Click += async (_, _) => await RefreshScanAsync();
        row1.Controls.Add(refresh);

        // 「批量选择」：给不习惯按 Ctrl / Shift 的人一个开关，
        // 打开之后每一行前面出现勾选框，点勾就行。
        _bulkButton = Theme.MakeButton("批量选择", Theme.Palette.Cyan, 30);
        _bulkButton.Margin = new Padding(0, 2, 8, 6);
        _bulkButton.Click += (_, _) => ToggleBulkMode();
        row1.Controls.Add(_bulkButton);

        _selectAllButton = Theme.MakeButton("全选", Theme.Palette.Olive, 30);
        _selectAllButton.Margin = new Padding(0, 2, 8, 6);
        _selectAllButton.Visible = false;
        _selectAllButton.Click += (_, _) => ToggleCheckAll();
        row1.Controls.Add(_selectAllButton);

        _hints.SetToolTip(_bulkButton, "打开后每一行左边会出现勾选框，勾选要处理的项，再点右边的按钮。不用按 Ctrl。");
        _hints.SetToolTip(_selectAllButton, "一次勾选当前列表里的全部项，或者全部取消。");

        // ---- 第二行：筛选开关
        var row2 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Card,
            Padding = new Padding(12, 2, 12, 0),
            Tag = new ThemeSpec(Surface: Surface.Card),
        };

        _onlyOrphan = MakeFilterCheck("只看失效残留",
            "显示指向的程序已经不在硬盘上的项——这些是软件卸载后留下的空壳。");
        _showDisabled = MakeFilterCheck("含已停用",
            "把已经被停用的项也一起显示出来。", true);
        _includeFileTypes = MakeFilterCheck("含文件类型专属项",
            "例如「仅 .zip 文件」右键才出现的项。数量很多而且几乎都是系统自带的，默认不显示。");

        row2.Controls.Add(_onlyOrphan);
        row2.Controls.Add(_showDisabled);
        row2.Controls.Add(_includeFileTypes);

        // Dock=Top 的堆叠顺序：后加的排在上面
        toolbar.Controls.Add(row2);
        toolbar.Controls.Add(row1);

        // ---- 主体：左列表 + 右详情
        _split = new SplitContainer
        {
            // 这里不能设 SplitterDistance / PanelMinSize：
            // 构造阶段控件还没有真实宽度，设了会让 WinForms 直接抛异常、窗口根本建不起来。
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 8,
            BackColor = Theme.Page,
            Tag = new ThemeSpec(Surface: Surface.Page),
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            GridLines = false,
            Font = Theme.UiFont(9f),
            BackColor = Theme.Card,
            BorderStyle = BorderStyle.FixedSingle,
            SmallImageList = _icons,
            // 名称可能很长，列里显示不下时鼠标停上去能看全
            ShowItemToolTips = true,
        };
        // 列名和上面两个筛选下拉用的是同一套说法，避免同名不同义让人犯迷糊
        _list.Columns.Add("名称", 260);
        _list.Columns.Add("在哪儿右键", 150);
        _list.Columns.Add("哪个程序", 150);
        _list.Columns.Add("类型", 80);
        _list.Columns.Add("谁装的", 110);
        _list.Columns.Add("状态", 80);
        _list.ColumnClick += (_, e) => SortBy(e.Column);
        _list.SelectedIndexChanged += (_, _) =>
        {
            UpdateDetail();
            UpdateCounts();
        };
        _list.ItemChecked += (_, _) =>
        {
            UpdateBulkButtonText();
            UpdateDetail();
            UpdateCounts();
        };
        _list.DoubleClick += (_, _) => ShowRegistryInfo();
        _split.Panel1.Controls.Add(_list);

        _detailPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Card,
            Padding = new Padding(16),
            Tag = new ThemeSpec(Surface: Surface.Card),
        };
        BuildDetailPanel(_detailPanel);
        _split.Panel2.Controls.Add(_detailPanel);

        page.Controls.Add(_split);
        page.Controls.Add(toolbar);
        return page;
    }

    private CheckBox MakeFilterCheck(string text, string hint, bool isChecked = false)
    {
        var box = new CheckBox
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            Font = Theme.UiFont(9f),
            Margin = new Padding(0, 8, 22, 0),
        };
        box.CheckedChanged += (_, _) => ApplyFilter();
        _hints.SetToolTip(box, hint);
        return box;
    }

    private void BuildDetailPanel(Panel host)
    {
        // 操作按钮固定在底部，上面的信息区自己滚动。
        // 这样无论窗口多矮、信息多长，四个按钮都永远看得见、点得到。
        var actions = new FlowLayoutPanel
        {
            // 竖着排，一个按钮一行。流式换行的容器高度算不准，
            // 窗口一窄就会把最后一个按钮挤到看不见的地方 —— 竖排从结构上杜绝这个问题。
            Dock = DockStyle.Bottom,
            Height = 158,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Card,
            Padding = new Padding(0, 6, 0, 0),
            Tag = new ThemeSpec(Surface: Surface.Card),
        };

        _btnToggle = Theme.MakeButton("停用", Theme.Palette.Teal);
        _btnToggle.Click += (_, _) => ToggleSelected();
        actions.Controls.Add(_btnToggle);

        _btnDelete = Theme.MakeButton("删除（先备份）", Theme.Palette.Red);
        _btnDelete.Click += (_, _) => DeleteSelected();
        actions.Controls.Add(_btnDelete);

        _btnCopyPath = Theme.MakeButton("复制注册表路径", Theme.Palette.Indigo);
        _btnCopyPath.Click += (_, _) => CopySelectedPath();
        actions.Controls.Add(_btnCopyPath);

        _btnOpenRegedit = Theme.MakeButton("打开注册表编辑器", Theme.Palette.Slate);
        _btnOpenRegedit.Click += (_, _) => OpenRegedit();
        actions.Controls.Add(_btnOpenRegedit);

        var info = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Card,
            Tag = new ThemeSpec(Surface: Surface.Card),
        };

        _detailName = Theme.MakeLabel("选中左侧任意一项查看详情", 12f, Theme.Text, FontStyle.Bold);
        _detailName.Location = new Point(0, 0);
        _detailName.MaximumSize = new Size(320, 0);
        info.Controls.Add(_detailName);

        _detailMeta = Theme.MakeLabel("", 9f, Theme.Muted);
        _detailMeta.Location = new Point(0, 48);
        _detailMeta.MaximumSize = new Size(320, 0);
        info.Controls.Add(_detailMeta);

        // 这两个小标题留成字段，位置由 RelayoutDetail 按实际文字高度算。
        // 详情文字的行数是会变的（多选那段有三行），写死坐标就会被压在下面的框底下。
        _commandLabel = Theme.MakeLabel("这个菜单项实际执行什么", 9f, Theme.Muted, FontStyle.Bold);
        _commandLabel.Location = new Point(0, 100);
        info.Controls.Add(_commandLabel);

        _detailCommand = new TextBox
        {
            Location = new Point(0, 124),
            Width = 320,
            Height = 78,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Theme.Page,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.UiFont(8.5f),
            WordWrap = true,
        };
        info.Controls.Add(_detailCommand);

        _pathLabel = Theme.MakeLabel("它来自注册表的哪里", 9f, Theme.Muted, FontStyle.Bold);
        _pathLabel.Location = new Point(0, 220);
        info.Controls.Add(_pathLabel);

        _detailPath = new TextBox
        {
            Location = new Point(0, 244),
            Width = 320,
            Height = 60,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Theme.Page,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.UiFont(8.5f),
            WordWrap = true,
        };
        info.Controls.Add(_detailPath);

        _detailWarn = new TextBox
        {
            Location = new Point(0, 320),
            Width = 320,
            Height = 92,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Theme.WarnBg,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.UiFont(8.5f),
            WordWrap = true,
            Visible = false,
        };
        info.Controls.Add(_detailWarn);

        _detailInfo = info;

        info.Resize += (_, _) => RelayoutDetail();

        host.Controls.Add(info);
        host.Controls.Add(actions);
    }

    /// <summary>
    /// 把详情区从上到下重新排一遍。
    ///
    /// 必须按「实际文字高度」算，不能用写死的纵坐标：多选时那段说明有三行，
    /// 写死坐标的话下面的小标题正好被压在输入框底下 —— 用户看到的就是一行被挡住的字。
    /// </summary>
    private void RelayoutDetail()
    {
        if (_detailInfo is null) return;

        int width = _detailInfo.ClientSize.Width - 26;
        if (width < 140) width = 140;

        int y = 0;

        _detailName.Location = new Point(0, y);
        _detailName.MaximumSize = new Size(width, 0);
        y += _detailName.PreferredHeight + 8;

        _detailMeta.Location = new Point(0, y);
        _detailMeta.MaximumSize = new Size(width, 0);
        y += _detailMeta.PreferredHeight + 18;

        _commandLabel.Location = new Point(0, y);
        y += _commandLabel.PreferredHeight + 6;

        _detailCommand.Location = new Point(0, y);
        _detailCommand.Width = width;
        y += _detailCommand.Height + 18;

        _pathLabel.Location = new Point(0, y);
        y += _pathLabel.PreferredHeight + 6;

        _detailPath.Location = new Point(0, y);
        _detailPath.Width = width;
        y += _detailPath.Height + 18;

        _detailWarn.Location = new Point(0, y);
        _detailWarn.Width = width;
    }

    private TabPage BuildBackupPage()
    {
        var page = new TabPage("备份与还原") { BackColor = Theme.Page, Padding = new Padding(12), Tag = new ThemeSpec(Surface: Surface.Page) };

        // 高度要同时装下「提示文字」和「两行按钮」：
        // 文字到 32 左右，按钮区 76 高，中间再留点空隙，少了就会像上次那样叠在一起。
        var top = new Panel { Dock = DockStyle.Top, Height = 118, BackColor = Theme.Card, Tag = new ThemeSpec(Surface: Surface.Card) };

        _backupFolderLabel = Theme.MakeLabel("", 9f, Theme.Muted);
        _backupFolderLabel.Location = new Point(14, 14);
        top.Controls.Add(_backupFolderLabel);

        // 按钮文字都压短了：原来「立即备份全部菜单项」这种长度，一排排不下会被裁。
        // 完整说明放进了悬浮提示。
        RoundedButton backupAll = Theme.MakeButton("备份全部", Theme.Palette.Green, 30);
        backupAll.Click += (_, _) => BackupAll();
        _hints.SetToolTip(backupAll, "把当前扫描到的全部菜单项导出成一份 .reg 备份。");

        RoundedButton changeFolder = Theme.MakeButton("更改位置", Theme.Palette.Slate, 30);
        changeFolder.Click += (_, _) => ChangeBackupFolder();
        _hints.SetToolTip(changeFolder, "把备份存到别的地方，比如 D 盘或网盘同步目录。");

        RoundedButton openFolder = Theme.MakeButton("打开文件夹", Theme.Palette.Brown, 30);
        openFolder.Click += (_, _) => OpenBackupFolder();
        _hints.SetToolTip(openFolder, "打开「我的文档\\右键菜单备份」，看看里面的备份文件。");

        RoundedButton refresh = Theme.MakeButton("刷新", Theme.Palette.Emerald, 30);
        refresh.Click += (_, _) => RefreshBackupList();
        _hints.SetToolTip(refresh, "重新读一遍备份文件夹。");

        _btnRestore = Theme.MakeButton("还原选中", Theme.Palette.Magenta, 30);
        _btnRestore.Click += (_, _) => RestoreSelectedBackup();
        _hints.SetToolTip(_btnRestore, "把选中的备份导回注册表，菜单项就回来了。");

        RoundedButton deleteBackup = Theme.MakeButton("删除备份", Theme.Palette.Rose, 30);
        deleteBackup.Click += (_, _) => DeleteSelectedBackupFile();
        _hints.SetToolTip(deleteBackup, "永久删掉选中的备份文件，删了就没法再用它还原。");

        var actions = new FlowLayoutPanel
        {
            // 放在底部并给足高度，就算窗口很窄换行了也看得见
            Dock = DockStyle.Bottom,
            Height = 76,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Theme.Card,
            Padding = new Padding(14, 6, 14, 0),
        };
        actions.Controls.Add(backupAll);
        actions.Controls.Add(changeFolder);
        actions.Controls.Add(openFolder);
        actions.Controls.Add(refresh);
        actions.Controls.Add(_btnRestore);
        actions.Controls.Add(deleteBackup);
        top.Controls.Add(actions);

        _backupList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            Font = Theme.UiFont(9f),
            BackColor = Theme.Card,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _backupList.Columns.Add("备份时间", 170);
        _backupList.Columns.Add("备份内容", 520);
        _backupList.Columns.Add("大小", 100);

        page.Controls.Add(_backupList);
        page.Controls.Add(top);
        UpdateBackupFolderLabel();
        return page;
    }

    /// <summary>让用户自己挑备份存放的位置，比如放到 D 盘或网盘同步目录。</summary>
    private void ChangeBackupFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择备份文件存到哪个文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = BackupManager.BackupFolder,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string chosen = dialog.SelectedPath;
        if (string.IsNullOrWhiteSpace(chosen)) return;

        // 当场试着写一个文件 —— 选到只读目录或系统目录，要现在就说，而不是等到删除时备份失败
        try
        {
            Directory.CreateDirectory(chosen);
            string probe = Path.Combine(chosen, "__rmm_write_test.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "这个文件夹写不进去，换一个吧。\r\n\r\n" + chosen + "\r\n\r\n" + ex.Message,
                "不能用这个位置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        BackupManager.SetBackupFolder(chosen);
        BackupManager.SaveSettings();
        UpdateBackupFolderLabel();
        RefreshBackupList();
        SetStatus("备份位置已改为：" + chosen);
    }

    private void UpdateBackupFolderLabel()
    {
        string suffix = BackupManager.HasCustomFolder ? "" : "（默认位置）";
        _backupFolderLabel.Text = "删除前都会自动备份到：" + BackupManager.BackupFolder + suffix;
    }

    private static Control Place(Control control, int x, int y)
    {
        control.Location = new Point(x, y);
        return control;
    }

    /// <summary>
    /// 自己画选项卡标题栏。WinForms 原生那一条不认主题，
    /// 深色模式下会留一块刺眼的浅色，只能重画。
    /// </summary>
    private static void DrawTabHeader(TabControl tabs, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= tabs.TabPages.Count) return;

        Rectangle rect = tabs.GetTabRect(e.Index);
        bool selected = e.Index == tabs.SelectedIndex;

        using (var brush = new SolidBrush(selected ? Theme.Card : Theme.Page))
        {
            e.Graphics.FillRectangle(brush, rect);
        }

        TextRenderer.DrawText(
            e.Graphics,
            tabs.TabPages[e.Index].Text,
            tabs.Font,
            rect,
            selected ? Theme.Text : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>
    /// 窗口真正显示出来之后再决定左右分栏的比例。
    /// 构造阶段控件的宽度还是默认值，那时候设分栏距离会让 WinForms 抛异常。
    /// </summary>
    private void ApplyInitialSplitter()
    {
        try
        {
            int width = _split.Width;
            if (width < 500) return;

            int rightWidth = Math.Clamp(width / 3, 360, 480);

            // 先把下限清掉再设距离，最后才设下限，避免中途出现“距离越界”
            _split.Panel1MinSize = 0;
            _split.Panel2MinSize = 0;
            _split.SplitterDistance = Math.Max(240, width - rightWidth);
            _split.Panel1MinSize = 300;
            _split.Panel2MinSize = 300;
        }
        catch
        {
            // 窗口特别窄时保持默认分栏即可
        }
    }

    /// <summary>
    /// 按屏幕缩放比例安排窗口大小。
    ///
    /// 关键点：窗口和控件的尺寸都是物理像素，而高缩放屏幕上字体本身就是放大的，
    /// 所以窗口必须跟着放大同样的比例，否则内容会挤成一团、按钮还会被裁掉。
    /// 这就是 150% 缩放下「字显示不全」的根因。
    /// </summary>
    private void FitWindowToScreen()
    {
        try
        {
            Screen screen = Screen.FromControl(this);
            Rectangle work = screen.WorkingArea;
            if (work.Width <= 0 || work.Height <= 0) return;

            float scale = DeviceDpi / 96f;
            if (scale < 1f) scale = 1f;
            if (scale > 4f) scale = 4f;

            int minWidth = (int)(880 * scale);
            int minHeight = (int)(600 * scale);
            MinimumSize = new Size(minWidth, minHeight);

            int desiredWidth = Math.Max(minWidth, (int)(1180 * scale));
            int desiredHeight = Math.Max(minHeight, (int)(780 * scale));

            Width = Math.Max(minWidth, Math.Min(desiredWidth, work.Width - 20));
            Height = Math.Max(minHeight, Math.Min(desiredHeight, work.Height - 20));

            Left = work.X + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Y + Math.Max(0, (work.Height - Height) / 2);

            // 列表的列宽同样是物理像素，不放大在高缩放下会被截成「Library Folder Context Me…」
            foreach (ColumnHeader column in _list.Columns)
            {
                column.Width = (int)(column.Width * scale);
            }
            foreach (ColumnHeader column in _backupList.Columns)
            {
                column.Width = (int)(column.Width * scale);
            }
        }
        catch
        {
            // 拿不到屏幕信息就保持原样
        }
    }

    // ================================================================= 程序图标

    /// <summary>
    /// 按屏幕缩放决定图标显示多大。
    /// 必须在往里塞图片之前调用 —— 改尺寸会把已经塞进去的图标全部清空。
    /// </summary>
    private void ConfigureIconList()
    {
        float scale = DeviceDpi / 96f;
        int size = (int)Math.Round(16 * scale);
        size = Math.Clamp(size, 16, 32);

        _icons.ImageSize = new Size(size, size);
        _icons.ColorDepth = ColorDepth.Depth32Bit;
    }

    /// <summary>
    /// 把每一项对应程序的图标取出来。
    /// 取图标要读 exe/dll 里的资源，比较费时，所以先按文件去重、再丢到后台线程做，
    /// 不然扫描完那一下界面会卡住。
    /// </summary>
    private async Task LoadIconsAsync()
    {
        var bySource = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (MenuEntry entry in _all)
        {
            (string Path, int Index)? source = ResolveIconSource(entry);
            if (source == null) continue;
            bySource[source.Value.Path + "|" + source.Value.Index] = 0;
        }

        Size target = _icons.ImageSize;

        List<(string Key, Bitmap Image)> extracted = await Task.Run(() =>
        {
            var done = new List<(string, Bitmap)>();
            foreach (string key in bySource.Keys)
            {
                int bar = key.LastIndexOf('|');
                string path = key[..bar];
                int index = int.TryParse(key[(bar + 1)..], out int parsed) ? parsed : 0;

                Icon? icon = Native.ExtractIcon(path, index);
                if (icon == null) continue;

                using (icon)
                {
                    done.Add((key, RenderIcon(icon, target)));
                }
            }
            return done;
        });

        // 换一批图标之前先把旧的图片资源释放掉，否则反复扫描会一直涨内存
        foreach (Image old in _icons.Images) old.Dispose();
        _icons.Images.Clear();
        _iconIndexCache.Clear();

        foreach ((string key, Bitmap image) in extracted)
        {
            _icons.Images.Add(image);
            _iconIndexCache[key] = _icons.Images.Count - 1;
        }

        foreach (MenuEntry entry in _all)
        {
            (string Path, int Index)? source = ResolveIconSource(entry);
            if (source == null)
            {
                entry.IconIndex = -1;
                continue;
            }

            string key = source.Value.Path + "|" + source.Value.Index;
            entry.IconIndex = _iconIndexCache.TryGetValue(key, out int idx) ? idx : -1;
        }
    }

    private static Bitmap RenderIcon(Icon icon, Size target)
    {
        var bitmap = new Bitmap(target.Width, target.Height);
        using Graphics g = Graphics.FromImage(bitmap);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawIcon(icon, new Rectangle(0, 0, target.Width, target.Height));
        return bitmap;
    }

    /// <summary>这一项的图标该去哪个文件里取。</summary>
    private static (string Path, int Index)? ResolveIconSource(MenuEntry entry)
    {
        // 「发送到」里的快捷方式，直接读它自己的图标，拿到的就是目标程序的图标
        if (entry.Kind == EntryKind.SendTo &&
            !string.IsNullOrEmpty(entry.FilePath) &&
            File.Exists(entry.FilePath))
        {
            return (entry.FilePath!, 0);
        }

        return ParseIconReference(entry.IconRef, entry.ExePath);
    }

    /// <summary>
    /// 解析注册表里写的图标来源，形如 "C:\Windows\system32\imageres.dll,-1024"
    /// 或者 "%SystemRoot%\system32\shell32.dll"。
    /// </summary>
    private static (string Path, int Index)? ParseIconReference(string? iconRef, string? fallbackPath)
    {
        if (!string.IsNullOrWhiteSpace(iconRef))
        {
            string raw = iconRef.Trim();
            string path = raw;
            int index = 0;

            int comma = raw.LastIndexOf(',');
            if (comma > 0 && int.TryParse(raw[(comma + 1)..].Trim(), out int parsed))
            {
                path = raw[..comma];
                index = parsed;
            }

            string? resolved = ResolveExistingFile(path);
            if (resolved != null) return (resolved, index);
        }

        if (!string.IsNullOrWhiteSpace(fallbackPath))
        {
            string? resolved = ResolveExistingFile(fallbackPath);
            if (resolved != null) return (resolved, 0);
        }

        return null;
    }

    private static string? ResolveExistingFile(string raw)
    {
        try
        {
            string path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            if (path.Length == 0) return null;
            if (File.Exists(path)) return path;

            // 有些注册表项只写文件名不写路径，去系统目录补一下
            if (!Path.IsPathRooted(path))
            {
                string inSystem = Path.Combine(Environment.SystemDirectory, path);
                if (File.Exists(inSystem)) return inSystem;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}


internal sealed partial class MainForm
{
    // ================================================================= 扫描

    private async Task RefreshScanAsync()
    {
        if (_busy) return;
        SetBusy(true, "正在扫描右键菜单，请稍候…");
        try
        {
            ScanResult result = await Task.Run(() => Scanner.Scan());
            _all.Clear();
            _all.AddRange(result.Entries);

            SetStatus("正在读取程序图标…");
            await LoadIconsAsync();

            ApplyFilter();

            string notes = result.Notes.Count > 0 ? "  " + string.Join("  ", result.Notes) : "";
            SetStatus($"扫描完成，共找到 {_all.Count} 个右键菜单项。{notes}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "扫描时出错了：\r\n" + ex.Message, "出错了",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("扫描失败。");
        }
        finally
        {
            SetBusy(false, null);
            UpdatePermissionUi();
            RefreshBackupList();
        }
    }

    // ================================================================= 筛选与列表

    private void ApplyFilter()
    {
        if (!_uiReady) return;

        if (_all.Count == 0)
        {
            _view = new List<MenuEntry>();
            RebuildList();
            return;
        }

        string query = _search.Text.Trim();
        IEnumerable<MenuEntry> filtered = _all;

        // 文件类型专属项数量极大且几乎全是系统自带的，默认不显示
        if (!_includeFileTypes.Checked) filtered = filtered.Where(e => !e.IsFileTypeSpecific);

        // 维度一：这东西是谁装的
        string source = _sourceFilter.SelectedItem as string ?? AnySource;
        if (string.Equals(source, "Windows 自带", StringComparison.Ordinal))
        {
            filtered = filtered.Where(e => e.IsSystem);
        }
        else if (string.Equals(source, "第三方软件", StringComparison.Ordinal))
        {
            filtered = filtered.Where(e => !e.IsSystem);
        }

        // 维度二：你在哪儿点右键
        string placement = _placementFilter.SelectedItem as string ?? AnyPlacement;
        if (!string.Equals(placement, AnyPlacement, StringComparison.Ordinal))
        {
            filtered = filtered.Where(e => string.Equals(e.PlacementLabel, placement, StringComparison.Ordinal));
        }

        if (_onlyOrphan.Checked) filtered = filtered.Where(e => e.IsOrphan);
        if (!_showDisabled.Checked) filtered = filtered.Where(e => !e.IsDisabled);

        if (query.Length > 0)
        {
            filtered = filtered.Where(e =>
                Contains(e.DisplayName, query) ||
                Contains(e.Command, query) ||
                Contains(e.PhysicalPath, query) ||
                Contains(e.Clsid, query) ||
                Contains(e.ScopeLabel, query));
        }

        _view = filtered.ToList();
        RebuildList();
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void RebuildList()
    {
        // 重建前先记住「现在停在哪儿」和「选了哪些」。
        // 不记的话，每做一次筛选或操作，列表都会跳回最顶端，用户得重新往下翻。
        //
        // 关键：必须用稳定标识（注册表路径）来找回，不能用对象引用 ——
        // 重新扫描会造出一批全新的 MenuEntry 对象，旧引用在新列表里根本不存在，
        // 按引用找的结果就是「找不到 → 滚回顶端」。
        string? topKey = CurrentTopKey();
        var selectedKeys = new HashSet<string>(SelectedEntries.Select(EntryKey));

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (MenuEntry entry in _view)
        {
            var item = new ListViewItem(new[]
            {
                entry.ListTitle,
                entry.PlacementLabel,
                entry.ProviderLabel,
                entry.KindLabel,
                entry.OriginLabel,
                entry.StatusLabel,
            })
            {
                Tag = entry,
                ImageIndex = entry.IconIndex,
                ToolTipText = $"{entry.DisplayName}\r\n哪个程序：{entry.ProviderLabel}\r\n{entry.PlacementLabel}　·　{entry.OriginLabel}",
            };
            ApplyRowColour(item, entry);
            _list.Items.Add(item);

            if (selectedKeys.Contains(EntryKey(entry))) item.Selected = true;
        }
        _list.EndUpdate();

        RestoreTopItem(topKey);

        UpdateDetail();
        UpdateCounts();
    }

    /// <summary>菜单项的唯一标识：类型 + 注册表路径。重新扫描之后它保持不变。</summary>
    private static string EntryKey(MenuEntry entry) => entry.Kind + "|" + entry.PhysicalPath;

    private string? CurrentTopKey()
    {
        try
        {
            return _list.TopItem?.Tag is MenuEntry entry ? EntryKey(entry) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把原来停在最上面的那一项滚回最上面，让用户看到的还是同一片区域。</summary>
    private void RestoreTopItem(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;

        try
        {
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is MenuEntry entry && EntryKey(entry) == key)
                {
                    _list.TopItem = item;
                    return;
                }
            }
        }
        catch
        {
            // 列表还没显示出来时设 TopItem 会抛异常，忽略即可
        }
    }

    private ListViewItem? FindItem(MenuEntry entry)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if (ReferenceEquals(item.Tag, entry)) return item;
        }
        return null;
    }

    /// <summary>只刷新某一行的状态和颜色，不动别的行，因此不会影响滚动位置。</summary>
    private void RefreshRow(MenuEntry entry)
    {
        ListViewItem? item = FindItem(entry);
        if (item == null) return;

        item.SubItems[5].Text = entry.StatusLabel;
        ApplyRowColour(item, entry);
    }

    /// <summary>把某一项从列表里摘掉，不重建整个列表。</summary>
    private void RemoveRow(MenuEntry entry)
    {
        ListViewItem? item = FindItem(entry);
        if (item != null) _list.Items.Remove(item);
    }

    /// <summary>
    /// 给一行上色。默认什么都不设，用列表本身的底色。
    /// 系统自带＝淡黄底、失效残留＝淡红底、已停用＝灰字。
    /// </summary>
    private static void ApplyRowColour(ListViewItem item, MenuEntry entry)
    {
        if (entry.IsOrphan && !entry.IsDisabled)
        {
            item.BackColor = Theme.DangerBg;
            item.ForeColor = Theme.Danger;
        }
        else if (entry.IsDisabled)
        {
            item.ForeColor = Theme.DisabledText;
        }
        else if (entry.IsSystem)
        {
            item.BackColor = Theme.WarnBg;
        }
    }

    private void SortBy(int column)
    {
        if (_sortColumn == column) _sortAscending = !_sortAscending;
        else { _sortColumn = column; _sortAscending = true; }

        Comparison<MenuEntry> comparison = column switch
        {
            0 => (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture),
            1 => (a, b) => string.Compare(a.PlacementLabel, b.PlacementLabel, StringComparison.CurrentCulture),
            2 => (a, b) => string.Compare(a.ProviderLabel, b.ProviderLabel, StringComparison.CurrentCulture),
            3 => (a, b) => string.Compare(a.KindLabel, b.KindLabel, StringComparison.CurrentCulture),
            4 => (a, b) => string.Compare(a.OriginLabel, b.OriginLabel, StringComparison.CurrentCulture),
            5 => (a, b) => string.Compare(a.StatusLabel, b.StatusLabel, StringComparison.CurrentCulture),
            _ => (a, b) => 0,
        };

        _view.Sort((a, b) => _sortAscending ? comparison(a, b) : comparison(b, a));
        RebuildList();
    }

    private void UpdateCounts()
    {
        int disabled = _all.Count(e => e.IsDisabled);
        int orphan = _all.Count(e => e.IsOrphan);
        int picked = TargetEntries.Count;

        string text = $"显示 {_view.Count} / 共 {_all.Count} 项　·　已停用 {disabled}　·　失效残留 {orphan}";
        if (picked > 1) text += _bulkMode ? $"　·　已勾选 {picked} 项" : $"　·　已选中 {picked} 项";

        _statusCounts.Text = text;
    }

    // ================================================================= 详情

    private MenuEntry? SelectedEntry =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as MenuEntry : null;

    private List<MenuEntry> SelectedEntries =>
        _list.SelectedItems.Cast<ListViewItem>()
             .Select(i => i.Tag as MenuEntry)
             .Where(e => e != null)
             .Select(e => e!)
             .ToList();

    /// <summary>
    /// 当前要处理的对象：批量选择模式下取「勾选的项」，否则取「选中的项」。
    /// </summary>
    private List<MenuEntry> TargetEntries
    {
        get
        {
            List<MenuEntry> picked;

            if (_bulkMode && _list.CheckedItems.Count > 0)
            {
                picked = _list.CheckedItems.Cast<ListViewItem>()
                    .Select(i => i.Tag as MenuEntry)
                    .Where(e => e != null)
                    .Select(e => e!)
                    .ToList();
            }
            else
            {
                picked = SelectedEntries;
            }

            // 一条合并项背后可能对应几十处注册（比如同一个程序在几十种文件类型下各有一份），
            // 操作时必须全部带上，否则会出现「删了但菜单里还在」的怪事。
            return picked.SelectMany(e => e.Expand()).ToList();
        }
    }

    // ================================================================= 批量选择

    private void ToggleBulkMode()
    {
        _bulkMode = !_bulkMode;
        _list.CheckBoxes = _bulkMode;

        if (!_bulkMode)
        {
            foreach (ListViewItem item in _list.Items) item.Checked = false;
        }

        _bulkButton.Text = _bulkMode ? "退出批量选择" : "批量选择";
        _selectAllButton.Visible = _bulkMode;
        UpdateBulkButtonText();

        UpdateDetail();
        UpdateCounts();
        _list.Focus();

        SetStatus(_bulkMode
            ? "批量选择已打开：勾选要处理的项，然后点右侧的「停用」或「删除」。"
            : "批量选择已关闭。");
    }

    private void ToggleCheckAll()
    {
        bool allChecked = _list.Items.Count > 0 && _list.CheckedItems.Count == _list.Items.Count;
        foreach (ListViewItem item in _list.Items) item.Checked = !allChecked;

        UpdateBulkButtonText();
        UpdateDetail();
        UpdateCounts();
    }

    private void UpdateBulkButtonText()
    {
        if (!_bulkMode) return;
        bool allChecked = _list.Items.Count > 0 && _list.CheckedItems.Count == _list.Items.Count;
        _selectAllButton.Text = allChecked ? "清空勾选" : "全选";
    }

    private void UpdateDetail()
    {
        List<MenuEntry> selected = TargetEntries;

        if (selected.Count == 0)
        {
            _detailName.Text = _bulkMode ? "勾选左侧要处理的项" : "选中左侧任意一项查看详情";
            _detailMeta.Text = _bulkMode
                ? "每一行左边都有勾选框。勾好之后，下面的按钮会一次作用在所有勾选的项上。"
                : "按住 Ctrl 可以多选，按住 Shift 可以连选一段。\r\n不想按键盘，就点上面的「批量选择」，用勾选框来挑。";
            _detailCommand.Text = "";
            _detailPath.Text = "";
            _detailWarn.Visible = false;
            _btnToggle.Text = "停用";
            _btnToggle.Enabled = false;
            _btnDelete.Text = "删除（先备份）";
            _btnDelete.Enabled = false;
            _btnCopyPath.Enabled = false;
            RelayoutDetail();
            return;
        }

        if (selected.Count > 1)
        {
            ShowBulkDetail(selected);
            RelayoutDetail();
            return;
        }

        MenuEntry entry = selected[0];

        _detailName.Text = entry.DisplayName;

        var meta = new List<string>
        {
            $"{entry.KindLabel} · {entry.PlacementLabel}",
            $"谁装的：{entry.OriginLabel}　　状态：{entry.StatusLabel}",
            $"注册位置：{entry.SourceLabel}",
        };
        if (entry.Clsid != null) meta.Add("标识：" + entry.Clsid);
        if (entry.ConditionalNote != null) meta.Add(entry.ConditionalNote);
        if (entry.GroupCount > 1)
        {
            meta.Add($"同一个程序在 {entry.GroupCount} 个位置注册了它，「停用」「删除」会一次全部处理。");
        }
        _detailMeta.Text = string.Join("\r\n", meta);

        _detailCommand.Text = entry.Command ?? "(没有记录到具体命令)";
        _detailPath.Text = entry.PhysicalPath;

        var warnings = new List<string>();
        if (entry.IsDisabled)
        {
            warnings.Add("当前是停用状态，右键菜单里不会出现它。"
                + (entry.DisabledHow != null ? $"（{entry.DisabledHow}）" : ""));
        }
        // 失效残留的提醒文字由扫描引擎一并给出，这里不再重复拼一遍
        warnings.AddRange(entry.Warnings);

        _detailWarn.Text = string.Join("\r\n\r\n", warnings);
        _detailWarn.Visible = warnings.Count > 0;

        _btnToggle.Text = entry.IsDisabled ? "恢复启用" : "停用";
        _btnToggle.Enabled = entry.Kind != EntryKind.SendTo || entry.FilePath != null;
        _btnDelete.Text = "删除（先备份）";
        _btnDelete.Enabled = true;
        _btnCopyPath.Enabled = true;
        RelayoutDetail();
    }

    /// <summary>
    /// 一次选中多项时，详情区切换成「批量操作」的样子。
    /// 多选本身一直是支持的，但界面之前完全没有反馈 —— 用户根本不知道
    /// 这两个按钮会作用在几项上，自然就觉得「不能多选」。
    /// </summary>
    private void ShowBulkDetail(List<MenuEntry> selected)
    {
        int disabled = selected.Count(e => e.IsDisabled);
        int orphan = selected.Count(e => e.IsOrphan);
        int thirdParty = selected.Count(e => !e.IsSystem);

        _detailName.Text = $"已选中 {selected.Count} 项";
        _detailMeta.Text =
            $"下面的按钮会一次作用在这 {selected.Count} 项上。\r\n" +
            $"其中：第三方软件 {thirdParty} 项，已停用 {disabled} 项，失效残留 {orphan} 项。";

        var lines = selected.Take(30).Select(e => "· " + e.DisplayName).ToList();
        if (selected.Count > 30) lines.Add($"… 以及另外 {selected.Count - 30} 项");
        _detailCommand.Text = string.Join("\r\n", lines);

        _detailPath.Text = "";
        _detailWarn.Visible = false;

        bool allDisabled = disabled == selected.Count;
        _btnToggle.Text = allDisabled ? $"恢复启用这 {selected.Count} 项" : $"停用这 {selected.Count} 项";
        _btnToggle.Enabled = true;

        _btnDelete.Text = $"删除这 {selected.Count} 项（先备份）";
        _btnDelete.Enabled = true;

        _btnCopyPath.Enabled = false;
    }

    private void ShowRegistryInfo()
    {
        MenuEntry? entry = SelectedEntry;
        if (entry == null) return;

        string text =
            $"名称：{entry.DisplayName}\r\n" +
            $"位置：{entry.ScopeLabel}\r\n" +
            $"类型：{entry.KindLabel}\r\n" +
            $"来源：{entry.SourceLabel}\r\n" +
            $"状态：{entry.StatusLabel}\r\n\r\n" +
            $"命令：\r\n{entry.Command ?? "(无)"}\r\n\r\n" +
            $"注册表位置：\r\n{entry.PhysicalPath}";

        MessageBox.Show(this, text, "菜单项详情", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ================================================================= 停用 / 启用

    private void ToggleSelected()
    {
        List<MenuEntry> targets = TargetEntries;
        if (targets.Count == 0) return;

        bool wantEnable = targets.All(e => e.IsDisabled);
        string verb = wantEnable ? "恢复启用" : "停用";

        DialogResult confirm = MessageBox.Show(this,
            $"确定要{verb}选中的 {targets.Count} 项吗？\r\n\r\n" +
            (wantEnable
                ? "恢复后它们会重新出现在右键菜单里。"
                : "停用不会删除任何东西，随时可以在这里恢复。"),
            verb, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        var failures = new List<string>();
        int done = 0;
        foreach (MenuEntry entry in targets)
        {
            try
            {
                if (wantEnable) BackupManager.Enable(entry);
                else BackupManager.Disable(entry);

                // 就地改这一项的状态、只刷新它那一行。
                // 不重新扫描、不重建列表 —— 重建会把滚动位置和选中状态全丢掉，
                // 用户正翻到一半就被弹回顶端，非常难用。
                entry.IsDisabled = !wantEnable;
                entry.DisabledHow = wantEnable ? null : "由本程序停用";
                RefreshRow(entry);
                done++;
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.DisplayName}：{ex.Message}");
            }
        }

        UpdateCounts();
        UpdateDetail();

        if (failures.Count == 0)
        {
            MessageBox.Show(this,
                $"{verb}完成 {done} 项。\r\n\r\n重启一下资源管理器就能看到效果（桌面会闪一下）。",
                "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OfferRestartExplorer();
        }
        else
        {
            MessageBox.Show(this,
                $"{verb}完成 {done} 项。\r\n\r\n以下项目没能处理：\r\n" + string.Join("\r\n", failures) +
                "\r\n\r\n系统范围的项目需要管理员权限，请点右上角「以管理员身份重启」后再试。",
                "部分未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ================================================================= 删除

    private void DeleteSelected()
    {
        List<MenuEntry> targets = TargetEntries;
        if (targets.Count == 0) return;

        string names = string.Join("\r\n", targets.Take(12).Select(e => "· " + e.DisplayName));
        if (targets.Count > 12) names += $"\r\n… 以及另外 {targets.Count - 12} 项";

        DialogResult confirm = MessageBox.Show(this,
            $"将删除以下 {targets.Count} 个菜单项：\r\n\r\n{names}\r\n\r\n" +
            $"每一项都会先单独备份成文件，存放在：\r\n{BackupManager.BackupFolder}\r\n\r\n" +
            "这些项目会从右键菜单里消失。如果发现删错了，到「备份与还原」页面点一下就回来了。\r\n\r\n确定继续吗？",
            "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        var failures = new List<string>();
        var undone = new List<string>();
        int done = 0;

        foreach (MenuEntry entry in targets)
        {
            (bool ok, string? backup, string message) = BackupManager.Delete(entry);
            if (ok)
            {
                done++;
                undone.Add($"{entry.DisplayName} → {backup}");
                // 只把这一行摘掉，其余行原地不动，列表不会跳回顶端
                _all.Remove(entry);
                _view.Remove(entry);
                RemoveRow(entry);
            }
            else
            {
                failures.Add($"{entry.DisplayName}：{message}");
            }
        }

        UpdateCounts();
        UpdateDetail();
        RefreshBackupList();

        string summary = $"已删除 {done} 项。";
        if (undone.Count > 0)
        {
            summary += "\r\n\r\n备份文件：\r\n" + string.Join("\r\n", undone.Take(10));
            if (undone.Count > 10) summary += $"\r\n… 以及另外 {undone.Count - 10} 个";
        }

        if (failures.Count == 0)
        {
            MessageBox.Show(this,
                summary + "\r\n\r\n点确定后我帮你重启一下资源管理器，菜单立刻更新（桌面会闪一下）。",
                "删除完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OfferRestartExplorer();
        }
        else
        {
            MessageBox.Show(this,
                summary + "\r\n\r\n以下项目没能删除：\r\n" + string.Join("\r\n", failures) +
                "\r\n\r\n这些多半属于系统范围。请点右上角「以管理员身份重启」后再试一次。",
                "部分项目未删除", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>改动之后重新扫一遍，让界面显示的是真实状态。会连图标一起刷新。</summary>
    private async Task RefreshScanAfterChangeAsync()
    {
        ScanResult result = Scanner.Scan();
        _all.Clear();
        _all.AddRange(result.Entries);
        await LoadIconsAsync();
        ApplyFilter();
        RefreshBackupList();
    }

    // ================================================================= 其他操作

    private void CopySelectedPath()
    {
        MenuEntry? entry = SelectedEntry;
        if (entry == null) return;
        try
        {
            Clipboard.SetText(entry.PhysicalPath);
            SetStatus("注册表路径已复制到剪贴板。");
        }
        catch
        {
            MessageBox.Show(this, entry.PhysicalPath, "注册表路径（请手动复制）",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void OpenRegedit()
    {
        try
        {
            Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
            SetStatus("注册表编辑器已打开。改注册表有风险，不熟悉的话建议只用本程序操作。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打不开注册表编辑器：" + ex.Message, "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void BackupAll()
    {
        if (_all.Count == 0)
        {
            MessageBox.Show(this, "还没有扫描到任何项目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            string file = BackupManager.ExportAll(_all);
            RefreshBackupList();
            MessageBox.Show(this,
                $"已经把当前 {_all.Count} 个菜单项全部备份成一个文件：\r\n\r\n{file}\r\n\r\n" +
                "以后想恢复，双击这个文件，或者在「备份与还原」页面里点还原。",
                "备份完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "备份失败：" + ex.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenBackupFolder()
    {
        try
        {
            BackupManager.EnsureBackupFolder();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{BackupManager.BackupFolder}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打不开备份文件夹：\r\n" + BackupManager.BackupFolder + "\r\n" + ex.Message,
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ================================================================= 备份页

    private void RefreshBackupList()
    {
        _backupList.BeginUpdate();
        _backupList.Items.Clear();

        try
        {
            BackupManager.EnsureBackupFolder();
            string folder = BackupManager.BackupFolder;

            foreach (string file in Directory.EnumerateFiles(folder, "*.reg")
                                             .OrderByDescending(File.GetLastWriteTime))
            {
                var info = new FileInfo(file);
                var item = new ListViewItem(new[]
                {
                    info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    Path.GetFileNameWithoutExtension(file),
                    $"{info.Length / 1024.0:F1} KB",
                })
                {
                    Tag = file,
                    ToolTipText = file,
                };
                _backupList.Items.Add(item);
            }

            foreach (string dir in Directory.EnumerateDirectories(folder)
                                            .OrderByDescending(Directory.GetLastWriteTime))
            {
                var item = new ListViewItem(new[]
                {
                    Directory.GetLastWriteTime(dir).ToString("yyyy-MM-dd HH:mm:ss"),
                    Path.GetFileName(dir) + "（“发送到”快捷方式）",
                    "文件夹",
                })
                {
                    Tag = null,
                    ToolTipText = dir,
                };
                _backupList.Items.Add(item);
            }
        }
        catch
        {
            // 备份文件夹读不了就先空着，不影响主功能
        }

        _backupList.EndUpdate();
    }

    private async void RestoreSelectedBackup()
    {
        if (_backupList.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "请先在列表里选中一个 .reg 备份文件。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var files = _backupList.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as string)
            .Where(f => f != null)
            .Select(f => f!)
            .ToList();

        if (files.Count == 0)
        {
            MessageBox.Show(this,
                "选中的是“发送到”快捷方式的备份文件夹。请到备份文件夹里手动把里面的文件拖回“发送到”目录，或者直接打开该文件夹查看。",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OpenBackupFolder();
            return;
        }

        DialogResult confirm = MessageBox.Show(this,
            $"将从备份还原 {files.Count} 个文件，还原的菜单项会重新出现在右键菜单里。\r\n\r\n确定吗？",
            "确认还原", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        var failures = new List<string>();
        int ok = 0;
        foreach (string file in files)
        {
            (bool success, string message) = BackupManager.RestoreFromRegFile(file);
            if (success) ok++;
            else failures.Add($"{Path.GetFileName(file)}：{message}");
        }

        await RefreshScanAfterChangeAsync();

        string text = $"已还原 {ok} 个备份。";
        if (failures.Count > 0)
        {
            text += "\r\n\r\n以下没能还原：\r\n" + string.Join("\r\n", failures);
        }
        MessageBox.Show(this, text, "还原结果", MessageBoxButtons.OK,
            failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

        if (ok > 0) OfferRestartExplorer();
    }

    private void DeleteSelectedBackupFile()
    {
        if (_backupList.SelectedItems.Count == 0) return;

        var files = _backupList.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as string)
            .Where(f => f != null)
            .Select(f => f!)
            .ToList();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "选中的是备份文件夹，请到文件夹里手动删除。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        DialogResult confirm = MessageBox.Show(this,
            $"要永久删除这 {files.Count} 个备份文件吗？\r\n\r\n删掉之后就无法再用它们还原对应的菜单项了。",
            "确认删除备份", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        foreach (string file in files)
        {
            try { File.Delete(file); } catch { /* 删不掉就留着 */ }
        }
        RefreshBackupList();
    }

    // ================================================================= 经典菜单

    private void ToggleClassicMenu()
    {
        bool currentlyLegacy = ClassicMenu.IsLegacyMenuEnabled();

        string action = currentlyLegacy
            ? "恢复 Windows 11 默认的精简右键菜单（要点「显示更多选项」才看得到全部）。"
            : "切换到 Windows 10 风格的完整右键菜单（所有项目直接展开，不用再点「显示更多选项」）。";

        DialogResult confirm = MessageBox.Show(this,
            action + "\r\n\r\n改完之后需要重启一次资源管理器才会生效（桌面和任务栏会闪一下，正在开的窗口不受影响）。\r\n\r\n继续吗？",
            "切换右键菜单样式", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        try
        {
            if (currentlyLegacy) ClassicMenu.DisableLegacyMenu();
            else ClassicMenu.EnableLegacyMenu();

            UpdatePermissionUi();
            OfferRestartExplorer();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "切换失败：" + ex.Message, "出错了",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 重启资源管理器。所有右键菜单的改动，都要重启它之后才会生效。
    /// 右上角有个按钮可以随时手动触发，改动之后程序也会自动问一次。
    /// </summary>
    private void RestartExplorerNow()
    {
        DialogResult confirm = MessageBox.Show(this,
            "重启资源管理器？\r\n\r\n" +
            "桌面图标和任务栏会消失一两秒然后自己回来，已经打开的窗口和正在做的事不受影响。\r\n\r\n" +
            "所有关于右键菜单的改动，都要重启它之后才会生效。",
            "重启资源管理器", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        if (ClassicMenu.RestartExplorer())
        {
            SetStatus("资源管理器正在重启，右键菜单马上就是新的了。");
        }
        else
        {
            MessageBox.Show(this, "重启资源管理器失败，请手动重启电脑。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OfferRestartExplorer() => RestartExplorerNow();

    /// <summary>
    /// 把配色刷到所有控件上。建完界面调一次。
    ///
    /// 大部分控件靠 Tag 上的角色自动上色；但列表、输入框、下拉框这些原生控件的
    /// 颜色 WinForms 不会跟着 Tag 走，得单独再设一遍。
    /// </summary>
    private void ApplyTheme()
    {
        BackColor = Theme.Page;
        Theme.Apply(this);

        _list.BackColor = Theme.Card;
        _list.ForeColor = Theme.Text;
        _backupList.BackColor = Theme.Card;
        _backupList.ForeColor = Theme.Text;

        _search.BackColor = Theme.Card;
        _search.ForeColor = Theme.Text;

        foreach (ComboBox box in new[] { _sourceFilter, _placementFilter })
        {
            box.BackColor = Theme.Card;
            box.ForeColor = Theme.Text;
        }

        foreach (CheckBox box in new[] { _onlyOrphan, _showDisabled, _includeFileTypes })
        {
            box.BackColor = Color.Transparent;
            box.ForeColor = Theme.Text;
        }

        _detailCommand.BackColor = Theme.Page;
        _detailCommand.ForeColor = Theme.Text;
        _detailPath.BackColor = Theme.Page;
        _detailPath.ForeColor = Theme.Text;
        _detailWarn.BackColor = Theme.WarnBg;
        _detailWarn.ForeColor = Theme.Text;

        _statusMain.ForeColor = Theme.Text;
        _statusCounts.ForeColor = Theme.Muted;

        UpdatePermissionUi();

        // 列表每一行的底色（失效=红、系统自带=黄）是建行那一刻定死的，
        // 切主题后必须重建一遍，否则深色模式下会留着一片浅色的行。
        RebuildList();

        Invalidate(true);
        _list.Invalidate();
        _backupList.Invalidate();
    }

    // ================================================================= 权限

    private void UpdatePermissionUi()
    {
        bool admin = Reg.IsAdministrator;

        _permLabel.Text = admin
            ? "当前权限：管理员（系统级项目也能清理）"
            : "当前权限：普通用户（系统级项目需要管理员）";
        _permLabel.ForeColor = admin ? Theme.Muted : Theme.Danger;

        // 显隐交给右上角的流式布局自动重排，不再手动算坐标
        _elevateButton.Visible = !admin;

        _classicButton.Text = ClassicMenu.IsLegacyMenuEnabled() ? "返回 Win11 默认菜单" : "切换完整右键菜单";
    }

    private void ElevateAndRestart()
    {
        DialogResult confirm = MessageBox.Show(this,
            "将以管理员身份重新打开本程序，Windows 会弹出一次权限确认框。\r\n当前这个窗口会关闭。\r\n\r\n继续吗？",
            "以管理员身份重启", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (confirm != DialogResult.OK) return;

        if (ClassicMenu.RestartAsAdministrator())
        {
            Close();
        }
        else
        {
            MessageBox.Show(this, "没有拿到管理员权限（可能是在权限确认框上点了「否」）。\r\n普通权限下仍然可以使用，只是系统级的项目改不动。",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ================================================================= 状态与忙碌

    private void SetStatus(string text) => _statusMain.Text = text;

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _list.Enabled = !busy;

        if (message != null) SetStatus(message);
    }
}
