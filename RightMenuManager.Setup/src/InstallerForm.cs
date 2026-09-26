using System.Diagnostics;

namespace RightMenuManager.Setup;

/// <summary>
/// 安装向导：欢迎/选项 → 安装进度 → 完成。
/// 带 --uninstall 启动时走另一条线：确认 → 卸载 → 完成。
///
/// 布局全部在 OnLoad 里做：那里才拿得到真实 DPI。窗口尺寸和每个控件的坐标都乘 DPI 系数，
/// 手写缩放而不用 AutoScaleMode.Dpi —— 自动缩放这套在 150% 屏上不生效（主程序当初也踩过）。
/// 文字用 AutoSize + MaximumSize 自动换行、自动撑高，避免"字被裁掉"。
/// </summary>
internal sealed class InstallerForm : Form
{
    private readonly bool _uninstallMode;

    private HeaderPanel? _header;
    private Panel? _body;
    private Panel? _footer;
    private FlatButton? _primary;
    private FlatButton? _secondary;

    private readonly Panel _pageOptions = new() { Dock = DockStyle.Fill, BackColor = SetupTheme.Background, Visible = false };
    private readonly Panel _pageProgress = new() { Dock = DockStyle.Fill, BackColor = SetupTheme.Background, Visible = false };
    private readonly Panel _pageDone = new() { Dock = DockStyle.Fill, BackColor = SetupTheme.Background, Visible = false };
    private readonly Panel _pageRemoveConfirm = new() { Dock = DockStyle.Fill, BackColor = SetupTheme.Background, Visible = false };
    private readonly Panel _pageRemoveDone = new() { Dock = DockStyle.Fill, BackColor = SetupTheme.Background, Visible = false };

    private readonly TextBox _pathBox = new();
    private readonly CheckBox _desktop = new();
    private readonly CheckBox _startMenu = new();
    private readonly CheckBox _runNow = new();
    private readonly ProgressBar _bar = new();
    private readonly Label _percent = new();
    private readonly Label _step = new();
    private readonly Label _doneDetail = new();
    private readonly Label _removeDetail = new();
    private readonly Label _hint = new();

    private Action? _primaryAction;
    private string _installDir = InstallEngine.DefaultInstallDir;
    private bool _installed;
    private float _k = 1f;

    public InstallerForm(bool uninstallMode)
    {
        _uninstallMode = uninstallMode;

        Text = uninstallMode ? InstallEngine.AppName + " 卸载" : InstallEngine.AppName + " 安装程序";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = SetupTheme.Background;
        Font = SetupTheme.UiFont(9f);
        AutoScaleMode = AutoScaleMode.None;
        Icon = LoadOwnIcon();
    }

    /// <summary>按 96dpi 设计值换算出真实像素。</summary>
    private int S(int value) => (int)Math.Round(value * _k);

    private static Icon? LoadOwnIcon()
    {
        try
        {
            string? self = Environment.ProcessPath;
            return string.IsNullOrEmpty(self) ? null : Icon.ExtractAssociatedIcon(self);
        }
        catch
        {
            return null;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        float k = DeviceDpi / 96f;
        if (k < 1f) k = 1f;
        if (k > 4f) k = 4f;
        _k = k;

        ClientSize = new Size(S(620), S(440));
        BuildUi();

        if (_uninstallMode) StartUninstallFlow();
        else StartInstallFlow();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LayoutFooterButtons();
    }

    // ================================================================== 组装界面

    private void BuildUi()
    {
        _header = new HeaderPanel(LoadOwnIcon())
        {
            Dock = DockStyle.Top,
            Height = S(78),
            Caption = _uninstallMode ? "卸载 " + InstallEngine.AppName : "安装 " + InstallEngine.AppName,
            SubCaption = _uninstallMode ? "移除程序文件和快捷方式" : "管理 Windows 11 右键菜单 · 版本 " + InstallEngine.Version,
        };

        _body = new Panel { Dock = DockStyle.Fill, BackColor = SetupTheme.Background };

        _footer = new Panel { Dock = DockStyle.Bottom, Height = S(68), BackColor = SetupTheme.Surface };
        _footer.Paint += (_, ev) =>
        {
            using var pen = new Pen(SetupTheme.Line);
            ev.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
        };

        _primary = new FlatButton
        {
            Size = new Size(S(120), S(36)),
            SurfaceColor = SetupTheme.Surface,
            Primary = true,
        };
        _primary.Click += (_, _) => _primaryAction?.Invoke();

        _secondary = new FlatButton
        {
            Size = new Size(S(96), S(36)),
            SurfaceColor = SetupTheme.Surface,
            Primary = false,
            Text = "取消",
        };
        _secondary.Click += (_, _) => Close();

        _footer.Controls.Add(_primary);
        _footer.Controls.Add(_secondary);

        Controls.Add(_body);
        Controls.Add(_footer);
        Controls.Add(_header);

        BuildOptionsPage();
        BuildProgressPage();
        BuildDonePage();
        BuildRemoveConfirmPage();
        BuildRemoveDonePage();

        _body.Controls.Add(_pageOptions);
        _body.Controls.Add(_pageProgress);
        _body.Controls.Add(_pageDone);
        _body.Controls.Add(_pageRemoveConfirm);
        _body.Controls.Add(_pageRemoveDone);

        _footer.Resize += (_, _) => LayoutFooterButtons();
        LayoutFooterButtons();
    }

    private void LayoutFooterButtons()
    {
        if (_footer == null || _primary == null || _secondary == null) return;

        int right = _footer.ClientSize.Width - S(30);
        int top = (_footer.ClientSize.Height - _primary.Height) / 2;
        _primary.Location = new Point(right - _primary.Width, top);
        _secondary.Location = new Point(_primary.Left - S(12) - _secondary.Width, top);
    }

    private Label MakeText(string text, int x, int y, int maxWidth, float size, Color colour, FontStyle style = FontStyle.Regular)
    {
        return new Label
        {
            Text = text,
            Location = new Point(S(x), S(y)),
            MaximumSize = new Size(S(maxWidth), 0),
            AutoSize = true,
            Font = SetupTheme.UiFont(size, style),
            ForeColor = colour,
            BackColor = Color.Transparent,
        };
    }

    /// <summary>正文段落：宽度到 maxWidth 就自动折行，高度由内容决定。</summary>
    private Label MakeParagraph(string text, int x, int y, int maxWidth, float size = 9f)
        => MakeText(text, x, y, maxWidth, size, SetupTheme.SubInk);

    private CheckBox MakeCheck(string text, int x, int y, int width, bool @checked)
    {
        return new CheckBox
        {
            Text = text,
            Location = new Point(S(x), S(y)),
            Size = new Size(S(width), S(24)),
            Checked = @checked,
            Font = SetupTheme.UiFont(9.5f),
            ForeColor = SetupTheme.Ink,
            BackColor = Color.Transparent,
        };
    }

    private void BuildOptionsPage()
    {
        _pageOptions.Controls.Add(MakeText("欢迎使用「" + InstallEngine.AppName + "」安装向导",
            30, 24, 560, 15f, SetupTheme.Ink, FontStyle.Bold));

        _pageOptions.Controls.Add(MakeParagraph(
            "这个程序用来查看和管理 Windows 11 的右键菜单：找出系统自带和第三方软件加进去的项目，停用或删除不需要的。"
            + "删除每一项之前都会自动导出注册表备份，可以随时还原。",
            30, 62, 560));

        _pageOptions.Controls.Add(MakeText("安装位置", 30, 132, 200, 9.5f, SetupTheme.Ink, FontStyle.Bold));

        _pathBox.Location = new Point(S(30), S(158));
        _pathBox.Size = new Size(S(470), S(27));
        _pathBox.Font = SetupTheme.UiFont(9.5f);
        _pathBox.Text = _installDir;
        _pathBox.BorderStyle = BorderStyle.FixedSingle;
        _pathBox.TextChanged += (_, _) => { _installDir = _pathBox.Text.Trim(); UpdateHint(); };
        _pageOptions.Controls.Add(_pathBox);

        var browse = new FlatButton
        {
            Text = "浏览…",
            Location = new Point(S(508), S(157)),
            Size = new Size(S(82), S(29)),
            SurfaceColor = SetupTheme.Background,
            Primary = false,
        };
        browse.Click += (_, _) => Browse();
        _pageOptions.Controls.Add(browse);

        _desktop.Text = "创建桌面快捷方式";
        _desktop.Location = new Point(S(30), S(198));
        _desktop.Size = new Size(S(260), S(24));
        _desktop.Checked = true;
        _desktop.Font = SetupTheme.UiFont(9.5f);
        _desktop.ForeColor = SetupTheme.Ink;
        _desktop.BackColor = Color.Transparent;
        _pageOptions.Controls.Add(_desktop);

        _startMenu.Text = "创建开始菜单快捷方式";
        _startMenu.Location = new Point(S(30), S(228));
        _startMenu.Size = new Size(S(280), S(24));
        _startMenu.Checked = true;
        _startMenu.Font = SetupTheme.UiFont(9.5f);
        _startMenu.ForeColor = SetupTheme.Ink;
        _startMenu.BackColor = Color.Transparent;
        _pageOptions.Controls.Add(_startMenu);

        _hint.Location = new Point(S(30), S(262));
        _hint.MaximumSize = new Size(S(560), 0);
        _hint.AutoSize = true;
        _hint.Font = SetupTheme.UiFont(8.5f);
        _hint.ForeColor = SetupTheme.SubInk;
        _hint.BackColor = Color.Transparent;
        _pageOptions.Controls.Add(_hint);
    }

    private void BuildProgressPage()
    {
        _pageProgress.Controls.Add(MakeText("正在安装，请稍候…", 30, 30, 560, 15f, SetupTheme.Ink, FontStyle.Bold));

        _bar.Location = new Point(S(30), S(104));
        _bar.Size = new Size(S(560), S(20));
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Maximum = 100;
        _pageProgress.Controls.Add(_bar);

        _percent.Location = new Point(S(30), S(134));
        _percent.Size = new Size(S(560), S(24));
        _percent.Font = SetupTheme.UiFont(9f, FontStyle.Bold);
        _percent.ForeColor = SetupTheme.Accent;
        _percent.BackColor = Color.Transparent;
        _percent.Text = "0%";
        _pageProgress.Controls.Add(_percent);

        _step.Location = new Point(S(30), S(164));
        _step.Size = new Size(S(560), S(24));
        _step.Font = SetupTheme.UiFont(8.5f);
        _step.ForeColor = SetupTheme.SubInk;
        _step.BackColor = Color.Transparent;
        _pageProgress.Controls.Add(_step);
    }

    private void BuildDonePage()
    {
        _pageDone.Controls.Add(MakeText("安装完成", 30, 40, 560, 15f, SetupTheme.Ink, FontStyle.Bold));

        _doneDetail.Location = new Point(S(30), S(90));
        _doneDetail.MaximumSize = new Size(S(560), 0);
        _doneDetail.AutoSize = true;
        _doneDetail.Font = SetupTheme.UiFont(9f);
        _doneDetail.ForeColor = SetupTheme.SubInk;
        _doneDetail.BackColor = Color.Transparent;
        _pageDone.Controls.Add(_doneDetail);

        _runNow.Text = "立即运行 " + InstallEngine.AppName;
        _runNow.Location = new Point(S(30), S(180));
        _runNow.Size = new Size(S(320), S(24));
        _runNow.Checked = true;
        _runNow.Font = SetupTheme.UiFont(9.5f);
        _runNow.ForeColor = SetupTheme.Ink;
        _runNow.BackColor = Color.Transparent;
        _pageDone.Controls.Add(_runNow);
    }

    private void BuildRemoveConfirmPage()
    {
        _pageRemoveConfirm.Controls.Add(MakeText("确认卸载", 30, 40, 560, 15f, SetupTheme.Ink, FontStyle.Bold));

        _removeDetail.Location = new Point(S(30), S(90));
        _removeDetail.MaximumSize = new Size(S(560), 0);
        _removeDetail.AutoSize = true;
        _removeDetail.Font = SetupTheme.UiFont(9f);
        _removeDetail.ForeColor = SetupTheme.SubInk;
        _removeDetail.BackColor = Color.Transparent;
        _pageRemoveConfirm.Controls.Add(_removeDetail);

        var note = MakeText("你导出的注册表备份保存在自己的文档或指定文件夹里，卸载不会动它们。",
            30, 186, 560, 8.5f, SetupTheme.SubInk);
        note.Name = "note";
        _pageRemoveConfirm.Controls.Add(note);
    }

    private void BuildRemoveDonePage()
    {
        _pageRemoveDone.Controls.Add(MakeText("卸载完成", 30, 40, 560, 15f, SetupTheme.Ink, FontStyle.Bold));
        _pageRemoveDone.Controls.Add(MakeParagraph(
            "程序文件和快捷方式已经移除，「应用和功能」里的登记也一并删掉了。",
            30, 90, 560));
    }

    // ================================================================== 页面切换

    private void ShowPage(Panel page, string primaryText, string? secondaryText, Action primaryAction, bool danger = false)
    {
        foreach (Panel p in new[] { _pageOptions, _pageProgress, _pageDone, _pageRemoveConfirm, _pageRemoveDone })
            p.Visible = ReferenceEquals(p, page);

        if (_primary == null || _secondary == null) return;

        _primaryAction = primaryAction;
        _primary.Text = primaryText;
        _primary.Visible = true;
        _primary.Enabled = true;
        _primary.BaseColor = danger ? SetupTheme.Danger : SetupTheme.Accent;

        if (secondaryText == null) _secondary.Visible = false;
        else { _secondary.Visible = true; _secondary.Text = secondaryText; _secondary.Enabled = true; }

        LayoutFooterButtons();
    }

    private void UpdateHint()
    {
        if (!InstallEngine.IsValidInstallDir(_installDir, out string reason))
        {
            _hint.ForeColor = SetupTheme.Danger;
            _hint.Text = reason;
            return;
        }

        string drive = InstallEngine.DescribeDrive(_installDir);
        bool exists = Directory.Exists(_installDir);
        _hint.ForeColor = SetupTheme.SubInk;
        _hint.Text = (exists ? "这个文件夹已经存在，同名文件会被覆盖。" : "文件夹不存在，安装时会自动创建。")
                     + (drive.Length > 0 ? "　" + drive : "");
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择安装位置",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        try
        {
            string? parent = Path.GetDirectoryName(_installDir.TrimEnd('\\'));
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) dialog.SelectedPath = parent;
        }
        catch { }

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string chosen = dialog.SelectedPath;
        if (!chosen.EndsWith(InstallEngine.AppName, StringComparison.OrdinalIgnoreCase))
            chosen = Path.Combine(chosen, InstallEngine.AppName);

        _pathBox.Text = chosen;
    }

    // ================================================================== 安装流程

    private void StartInstallFlow()
    {
        string? existing = InstallEngine.InstalledDir();
        if (!string.IsNullOrEmpty(existing))
        {
            _installDir = existing;
            _pathBox.Text = existing;
            _installed = true;
            if (_header != null) _header.SubCaption = "已经装过，继续操作会覆盖原有文件";
        }

        UpdateHint();
        ShowPage(_pageOptions, _installed ? "重新安装" : "开始安装", "取消", () => _ = RunInstallAsync());
    }

    private async Task RunInstallAsync()
    {
        string dir = _pathBox.Text.Trim().TrimEnd('\\', '/');

        if (!InstallEngine.IsValidInstallDir(dir, out string reason))
        {
            MessageBox.Show(this, reason, "安装位置有问题", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _pathBox.Focus();
            return;
        }

        if (!CloseRunningApp()) return;

        if (!InstallEngine.HasDesktopRuntime(out _))
        {
            var pick = MessageBox.Show(this,
                "这台电脑上没找到 .NET 8 桌面运行库，程序装好后可能打不开。\r\n\r\n"
                + "要用浏览器打开微软的下载页面吗？（选「否」会直接继续安装）",
                "缺少运行库", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (pick == DialogResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo(InstallEngine.RuntimeDownloadUrl) { UseShellExecute = true }); }
                catch { }
            }
        }

        ShowPage(_pageProgress, "正在安装", null, () => { });
        if (_primary != null) _primary.Enabled = false;
        _bar.Value = 0;
        _percent.Text = "0%";
        _step.Text = "正在准备…";

        var progress = new Progress<(int Done, int Total, string Name)>(p =>
        {
            int value = p.Total <= 0 ? 0 : Math.Clamp(p.Done * 100 / p.Total, 0, 100);
            _bar.Value = Math.Min(100, value);
            _percent.Text = value + "%";
            if (p.Name.Length > 0) _step.Text = "正在复制 " + p.Name;
        });

        try
        {
            await Task.Run(() => InstallEngine.ExtractAll(dir,
                (done, total, name) => ((IProgress<(int, int, string)>)progress).Report((done, total, name))));
        }
        catch (Exception ex)
        {
            ShowFailure("复制程序文件失败", ex, dir);
            return;
        }

        try
        {
            _step.Text = "正在创建快捷方式…";
            string exe = Path.Combine(dir, InstallEngine.MainExe);
            if (_desktop.Checked)
                InstallEngine.CreateShortcut(InstallEngine.DesktopShortcutPath, exe, dir, InstallEngine.AppName);
            if (_startMenu.Checked)
                InstallEngine.CreateShortcut(InstallEngine.StartMenuShortcutPath, exe, dir, InstallEngine.AppName);
            else if (File.Exists(InstallEngine.StartMenuShortcutPath))
                File.Delete(InstallEngine.StartMenuShortcutPath);

            _step.Text = "正在登记卸载信息…";
            string uninstaller = InstallEngine.DeployUninstaller(dir);
            InstallEngine.RegisterUninstall(dir, uninstaller);
        }
        catch (Exception ex)
        {
            ShowFailure("安装收尾时出错", ex, dir);
            return;
        }

        _bar.Value = 100;
        _percent.Text = "100%";
        _step.Text = "";

        string desktopNote = _desktop.Checked ? "桌面和开始菜单上已经放好快捷方式。" : "开始菜单上已经放好快捷方式。";
        _doneDetail.Text = "程序装在这里：\r\n" + dir + "\r\n" + desktopNote
                         + "以后想卸载，可以在「设置 → 应用 → 已安装的应用」里找到它。";

        _runNow.Location = new Point(S(30), _doneDetail.Bottom + S(24));
        _runNow.Checked = true;

        ShowPage(_pageDone, "完成", null, Finish);
    }

    private void ShowFailure(string title, Exception ex, string dir)
    {
        MessageBox.Show(this,
            title + "。\r\n\r\n" + ex.Message + "\r\n\r\n目标位置：" + dir
            + "\r\n\r\n可以换一个位置再试，比如装到 " + InstallEngine.DefaultInstallDir + "。",
            "安装没有完成", MessageBoxButtons.OK, MessageBoxIcon.Error);

        ShowPage(_pageOptions, _installed ? "重新安装" : "开始安装", "取消", () => _ = RunInstallAsync());
    }

    private bool CloseRunningApp()
    {
        string name = Path.GetFileNameWithoutExtension(InstallEngine.MainExe);
        Process[] running;
        try { running = Process.GetProcessesByName(name); }
        catch { return true; }

        if (running.Length == 0) return true;

        var answer = MessageBox.Show(this,
            InstallEngine.AppName + " 正在运行，必须先关掉才能更新文件。\r\n\r\n现在就关闭它吗？（未保存的操作会丢失）",
            "程序正在运行", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
        {
            foreach (Process p in running) p.Dispose();
            return false;
        }

        foreach (Process p in running)
        {
            try { p.Kill(); p.WaitForExit(4000); } catch { }
            finally { p.Dispose(); }
        }

        Thread.Sleep(400);
        return true;
    }

    private void Finish()
    {
        if (_runNow.Visible && _runNow.Checked)
        {
            try
            {
                string dir = _pathBox.Text.Trim();
                string exe = Path.Combine(dir, InstallEngine.MainExe);
                if (File.Exists(exe))
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
            }
            catch { }
        }
        Close();
    }

    // ================================================================== 卸载流程

    private void StartUninstallFlow()
    {
        string? dir = InstallEngine.InstalledDir();
        string selfDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            // 登记信息没了，退一步看卸载程序自己所在的位置 —— 它在安装目录的隐藏子目录里，
            // 所以自己那层和上一层都要看一眼。
            foreach (string candidate in new[] { selfDir, Path.GetDirectoryName(selfDir) ?? "" })
            {
                if (candidate.Length == 0) continue;
                if (File.Exists(Path.Combine(candidate, InstallEngine.MainExe))) { dir = candidate; break; }
            }

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show(this,
                    "没有找到「" + InstallEngine.AppName + "」的安装记录，可能已经卸载过了。",
                    "无需卸载", MessageBoxButtons.OK, MessageBoxIcon.Information);
                BeginInvoke(Close);
                return;
            }
        }

        _installDir = dir;

        long size = 0;
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                size += new FileInfo(f).Length;
        }
        catch { }

        _removeDetail.Text = "将删除这个文件夹里的全部程序文件（" + InstallEngine.FormatSize(size) + "）：\r\n"
                           + dir + "\r\n"
                           + "同时删除开始菜单和桌面上的快捷方式。";

        if (_pageRemoveConfirm.Controls["note"] is Label note)
            note.Location = new Point(S(30), _removeDetail.Bottom + S(22));

        if (_header != null) _header.SubCaption = "移除程序文件和快捷方式";

        ShowPage(_pageRemoveConfirm, "卸载", "取消", RunUninstall, danger: true);
    }

    private void RunUninstall()
    {
        var answer = MessageBox.Show(this,
            "确定要卸载「" + InstallEngine.AppName + "」吗？\r\n\r\n程序文件会被删除，此操作不可撤销。",
            "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (answer != DialogResult.Yes) return;

        string name = Path.GetFileNameWithoutExtension(InstallEngine.MainExe);
        try
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(4000); } catch { }
                finally { p.Dispose(); }
            }
            Thread.Sleep(400);
        }
        catch { }

        try
        {
            InstallEngine.Uninstall(_installDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "卸载过程中出错：" + ex.Message, "卸载未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ShowPage(_pageRemoveDone, "完成", null, Close);
    }
}
