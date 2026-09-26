using Microsoft.Win32;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace RightMenuManager;


internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 自测模式：不开窗口，把扫描结果写到文件里，用于验证扫描逻辑是否正常。
        if (args.Length >= 2 && string.Equals(args[0], "--dump", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Scanner.DumpTo(args[1]);
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(args[1] + ".error.txt", ex.ToString()); } catch { }
                Environment.ExitCode = 1;
            }
            return;
        }

        if (args.Length >= 1 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
        {
            Environment.ExitCode = SelfTest.Run(args.Length >= 2 ? args[1] : null);
            return;
        }

        // 由项目文件里的 ApplicationHighDpiMode / 字体等设置生成，
        // 会正确地在进程最早阶段把高 DPI 支持打开。
        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) =>
            MessageBox.Show("程序遇到了一个问题：\r\n" + e.Exception.Message, "出错了",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            // 窗口都没能建起来的时候，至少让用户看见发生了什么，而不是程序一声不响地消失
            MessageBox.Show(
                "程序启动失败：\r\n\r\n" + ex.Message,
                "右键菜单管理器 · 启动失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }
}


/// <summary>
/// 隔离自测：临时造几个假的右键菜单项，把「扫描 → 备份 → 停用 → 恢复 → 删除 → 还原」
/// 整条链路真跑一遍，最后把自己造的东西全部清掉。
///
/// 设计原则：
/// ① 所有测试键都带 __RMM_TEST 前缀，只删自己建的，绝不碰真实菜单项；
/// ② 只写在 HKEY_CURRENT_USER 下，不需要管理员权限，也不影响其他用户；
/// ③ 无论成功失败，finally 里一定清理干净。
/// </summary>
internal static class SelfTest
{
    private const string GoodVerbPath = @"Software\Classes\*\shell\__RMM_TEST_正常项";
    private const string OrphanVerbPath = @"Software\Classes\*\shell\__RMM_TEST_残留项";
    private const string BackgroundVerbPath = @"Software\Classes\Directory\Background\shell\__RMM_TEST_空白处项";
    private const string HandlerPath = @"Software\Classes\*\shellex\ContextMenuHandlers\__RMM_TEST_扩展";
    private const string TestClsid = "{A1B2C3D4-0000-1111-2222-333344445555}";

    private const string GoodName = "自测项（正常）";
    private const string OrphanName = "自测项（指向不存在的程序）";
    private const string BackgroundName = "自测项（文件夹空白处）";

    public static int Run(string? outputPath)
    {
        string outFile = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Path.GetTempPath(), "右键菜单管理器-自测结果.txt")
            : outputPath;

        var report = new Report();
        var createdBackups = new List<string>();

        report.Section("运行环境");
        report.Info($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.Info($"管理员权限：{(Reg.IsAdministrator ? "是" : "否")}（本自测不需要管理员，只写当前用户）");
        report.Info($"测试位置：HKEY_CURRENT_USER\\Software\\Classes（全部带 __RMM_TEST 前缀）");

        try
        {
            report.Section("准备隔离测试数据");
            CleanupTestKeys();
            SetupTestKeys();
            report.Check("测试用的假菜单项已写入注册表", KeyExists(GoodVerbPath) && KeyExists(OrphanVerbPath) && KeyExists(HandlerPath));

            // ---------------------------------------------------------- 扫描
            report.Section("扫描能力");
            ScanResult first = Scanner.Scan();
            report.Info($"本次全盘扫描共读到 {first.Entries.Count} 个菜单项。");

            MenuEntry? good = first.Entries.FirstOrDefault(e => e.DisplayName == GoodName);
            MenuEntry? orphan = first.Entries.FirstOrDefault(e => e.DisplayName == OrphanName);
            MenuEntry? handler = first.Entries.FirstOrDefault(e => e.Kind == EntryKind.ShellExtension && e.Clsid == TestClsid);
            MenuEntry? background = first.Entries.FirstOrDefault(e => e.DisplayName == BackgroundName);

            report.Check("扫到了普通菜单项", good != null);
            report.Check("扫到了系统扩展项（COM 处理器）", handler != null);
            report.Check("菜单项归属位置正确（所有文件）", good?.ScopeLabel == "所有文件", good?.ScopeLabel);
            report.Check("菜单项来源识别正确（当前用户）", good?.Hive == RegistryHive.CurrentUser);

            int goodCount = first.Entries.Count(e => e.DisplayName == GoodName);
            report.Check("同一项没有重复出现（32/64 位去重生效）", goodCount == 1, $"实际出现 {goodCount} 次");

            // ---------------------------------------------------------- 按右键位置分类
            report.Section("按「在哪儿右键」分类");
            report.Check("「所有文件」下的项归到「右键文件时」",
                good?.PlacementLabel == "右键文件时", good?.PlacementLabel ?? "(没扫到)");
            report.Check("「文件夹空白处」下的项归到「右键文件夹空白处」",
                background?.PlacementLabel == "右键文件夹空白处", background?.PlacementLabel ?? "(没扫到)");
            report.Check("「发送到」里的项归到「「发送到」菜单」",
                first.Entries.FirstOrDefault(e => e.Kind == EntryKind.SendTo)?.PlacementLabel == "「发送到」菜单");

            // 这一条最关键：只要有一项的分类不在下拉选项里，筛选就会把它永久藏起来
            int unclassified = first.Entries.Count(e => !MainForm.PlacementChoices.Contains(e.PlacementLabel));
            report.Check("每一顶都能被位置筛选的下拉选中（没有漏网之项）", unclassified == 0,
                $"有 {unclassified} 项落在下拉选项之外");

            // ---------------------------------------------------------- 扫描覆盖面
            report.Section("扫描覆盖面（这几种以前都漏过）");
            report.Check("扫到了 Win11 现代菜单扩展（PackagedCom）",
                first.Entries.Any(e => e.ScopeKey == "PackagedCom"));
            report.Check("扫到了「发送到」菜单里的项",
                first.Entries.Any(e => e.Kind == EntryKind.SendTo));
            report.Check("扫到了挂在快捷方式／程序类型上的项",
                first.Entries.Any(e => e.PlacementKey is "shortcut" or "program"));

            int covered = first.Entries.Sum(e => e.Expand().Count());
            report.Check("同名同程序的重复项已合并（一条代表多处）", covered > first.Entries.Count);
            report.Info($"列表显示 {first.Entries.Count} 条，实际覆盖 {covered} 处注册。");

            int junkNames = first.Entries.Count(e => e.ProviderLabel.Contains('%'));
            report.Check("「哪个程序」列没有显示成命令原文（如 %1）", junkNames == 0, $"有 {junkNames} 条异常");

            // 不是 Software\Classes 下的项，在 HKCU 里没有对应位置 ——
            // 写进去等于没写，却会报「停用成功」。这里拿一个不存在的路径试，
            // 确认它会被拦住（不会真写任何东西）。
            bool guarded = false;
            try
            {
                BackupManager.Disable(new MenuEntry
                {
                    Kind = EntryKind.ShellVerb,
                    Hive = RegistryHive.LocalMachine,
                    View = RegistryView.Registry64,
                    SubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\__RMM_TEST_不存在__",
                    DisplayName = "自测用假项",
                });
            }
            catch (InvalidOperationException)
            {
                guarded = true;
            }
            report.Check("停用系统级项时会被拦住并提示要管理员权限（而不是假装成功）", guarded);

            // ---------------------------------------------------------- 程序图标
            report.Section("程序图标提取");
            using (Icon? sampleIcon = Native.ExtractIcon(Path.Combine(Environment.SystemDirectory, "cmd.exe"), 0))
            {
                report.Check("能从程序文件里取出图标（列表左边显示的就是它）",
                    sampleIcon != null && sampleIcon.Width > 0);
            }
            report.Check("碰上不存在的文件不会崩，只是没有图标",
                Native.ExtractIcon(Path.Combine(Environment.SystemDirectory, "__RMM_TEST_不存在__.exe"), 0) == null);

            // ---------------------------------------------------------- 备份位置设置
            report.Section("备份位置设置");
            string originalFolder = BackupManager.HasCustomFolder ? BackupManager.BackupFolder : "";
            try
            {
                const string probeFolder = @"C:\__RMM_TEST_自定义备份位置__";

                BackupManager.SetBackupFolder(probeFolder);
                report.Check("能记住用户选的备份位置", BackupManager.BackupFolder == probeFolder);

                BackupManager.SaveSettings();
                BackupManager.SetBackupFolder(null);       // 先清掉内存里的
                BackupManager.LoadSettings();              // 再从文件读回来
                report.Check("关掉再开还能读回用户选的位置", BackupManager.BackupFolder == probeFolder,
                    BackupManager.BackupFolder);

                BackupManager.SetBackupFolder(null);
                report.Check("清空设置后回到默认位置", !BackupManager.HasCustomFolder);
            }
            finally
            {
                // 把用户原本的设置放回去，别让自测改了他的配置
                BackupManager.SetBackupFolder(string.IsNullOrEmpty(originalFolder) ? null : originalFolder);
                BackupManager.SaveSettings();
            }

            // ---------------------------------------------------------- 失效识别
            report.Section("失效残留识别");
            report.Check("指向不存在程序的项被标为失效", orphan?.IsOrphan == true,
                $"IsOrphan={orphan?.IsOrphan}, 解析出的路径={orphan?.ExePath ?? "(空)"}");
            report.Check("指向真实存在的程序不会被误报失效", good?.IsOrphan == false,
                $"IsOrphan={good?.IsOrphan}");
            report.Check("失效项会给出可读的提醒文字", orphan?.Warnings.Count > 0);

            // ---------------------------------------------------------- 备份
            report.Section("备份导出");
            if (good == null)
            {
                report.Check("备份测试前置条件", false, "没扫到测试项，后续测试跳过");
            }
            else
            {
                string backup = BackupManager.ExportEntry(good);
                createdBackups.Add(backup);

                report.Check("备份文件已生成", File.Exists(backup), backup);
                string content = File.ReadAllText(backup, Encoding.Unicode);
                report.Check("备份文件头是标准 .reg 格式", content.StartsWith("Windows Registry Editor Version 5.00"));
                report.Check("备份里带着正确的注册表路径",
                    content.Contains(@"HKEY_CURRENT_USER\Software\Classes\*\shell\__RMM_TEST_正常项"));
                report.Check("备份里保存了名称", content.Contains("MUIVerb"));
                report.Check("备份里保存了命令", content.Contains("cmd.exe"));
            }

            // ---------------------------------------------------------- 停用 / 恢复
            report.Section("停用与恢复（可逆操作）");
            if (good != null)
            {
                BackupManager.Disable(good);
                ScanResult afterDisable = Scanner.Scan();
                MenuEntry? disabled = afterDisable.Entries.FirstOrDefault(e => e.DisplayName == GoodName);
                report.Check("停用后状态显示为「已停用」", disabled?.IsDisabled == true, disabled?.StatusLabel);
                report.Check("停用只是打个标记，没有真的删除", KeyExists(GoodVerbPath));

                BackupManager.Enable(good);
                ScanResult afterEnable = Scanner.Scan();
                MenuEntry? enabled = afterEnable.Entries.FirstOrDefault(e => e.DisplayName == GoodName);
                report.Check("恢复后状态回到正常", enabled != null && !enabled.IsDisabled);
            }

            // ---------------------------------------------------------- 删除 / 还原
            report.Section("删除与还原");
            if (orphan != null)
            {
                (bool ok, string? backupFile, string message) = BackupManager.Delete(orphan);
                if (backupFile != null) createdBackups.Add(backupFile);

                report.Check("删除已执行", ok, message);
                report.Check("删除前一定留下了备份", backupFile != null && File.Exists(backupFile), backupFile ?? "(没有备份文件)");
                report.Check("删除后注册表键确实消失了", !KeyExists(OrphanVerbPath));

                if (backupFile != null && File.Exists(backupFile))
                {
                    (bool restored, string restoreMessage) = BackupManager.RestoreFromRegFile(backupFile);
                    report.Check("备份文件能被系统正常导回（证明备份是真能用的）", restored, restoreMessage);
                    report.Check("导回后注册表键回来了", KeyExists(OrphanVerbPath));

                    // 还原出来的那一份内容要和原来一致
                    using RegistryKey? backKey = Reg.Open(RegistryHive.CurrentUser, RegistryView.Registry64,
                        OrphanVerbPath + "\\command");
                    string? cmd = Reg.GetString(backKey, null);
                    report.Check("还原后的命令内容与原来一致", cmd != null && cmd.Contains("不存在的程序"), cmd ?? "(空)");
                }
            }
        }
        catch (Exception ex)
        {
            report.Check("自测过程未抛异常", false, ex.Message);
            report.Info(ex.ToString());
        }
        finally
        {
            report.Section("清理");
            CleanupTestKeys();
            int removedBackups = 0;
            foreach (string file in createdBackups)
            {
                try { if (File.Exists(file)) { File.Delete(file); removedBackups++; } } catch { }
            }
            report.Check("测试用的假菜单项已全部清除", !KeyExists(GoodVerbPath) && !KeyExists(OrphanVerbPath) && !KeyExists(HandlerPath));
            report.Info($"顺带删掉了 {removedBackups} 个测试过程中产生的备份文件（真实备份一个没动）。");
        }

        report.Section("结论");
        report.Info($"通过 {report.Passed} 项，失败 {report.Failed} 项。");

        string text = report.Text;
        try { File.WriteAllText(outFile, text, new UTF8Encoding(true)); } catch { }

        if (Native.TryAttachParentConsole())
        {
            try { Console.WriteLine(text); } catch { }
        }

        return report.Failed == 0 ? 0 : 2;
    }

    // ------------------------------------------------------------------ 测试数据

    private static void SetupTestKeys()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(GoodVerbPath, true)!)
        {
            key.SetValue("MUIVerb", GoodName, RegistryValueKind.String);
            key.SetValue("Icon", @"C:\Windows\System32\cmd.exe", RegistryValueKind.String);
            using RegistryKey cmd = key.CreateSubKey("command", true)!;
            cmd.SetValue(null, @"""C:\Windows\System32\cmd.exe"" /c echo 自测 ""%1""", RegistryValueKind.String);
        }

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(OrphanVerbPath, true)!)
        {
            key.SetValue("MUIVerb", OrphanName, RegistryValueKind.String);
            using RegistryKey cmd = key.CreateSubKey("command", true)!;
            cmd.SetValue(null, @"""C:\Windows\System32\__RMM_TEST_不存在的程序__.exe"" ""%1""", RegistryValueKind.String);
        }

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(HandlerPath, true)!)
        {
            key.SetValue(null, TestClsid, RegistryValueKind.String);
        }

        // 放在「文件夹空白处」下的一项，用来验证按右键位置分类是否正确
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(BackgroundVerbPath, true)!)
        {
            key.SetValue("MUIVerb", BackgroundName, RegistryValueKind.String);
            using RegistryKey cmd = key.CreateSubKey("command", true)!;
            cmd.SetValue(null, @"""C:\Windows\System32\cmd.exe"" /c echo bg", RegistryValueKind.String);
        }
    }

    private static void CleanupTestKeys()
    {
        TryDeleteTree(GoodVerbPath);
        TryDeleteTree(OrphanVerbPath);
        TryDeleteTree(BackgroundVerbPath);
        TryDeleteTree(HandlerPath);

        // 顺手收掉可能被我们建出来的空壳目录，但只在它确实为空时才删
        RemoveIfEmpty(@"Software\Classes\*\shellex\ContextMenuHandlers");
        RemoveIfEmpty(@"Software\Classes\*\shellex");
    }

    private static void TryDeleteTree(string subKey)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); }
        catch { }
    }

    private static void RemoveIfEmpty(string subKey)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey);
            if (key == null) return;
            if (key.SubKeyCount != 0 || key.ValueCount != 0) return;
            key.Close();
            Registry.CurrentUser.DeleteSubKey(subKey, throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static bool KeyExists(string subKey)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 报告

    private sealed class Report
    {
        private readonly StringBuilder _sb = new();

        public int Passed { get; private set; }
        public int Failed { get; private set; }
        public string Text => _sb.ToString();

        public void Section(string title)
        {
            _sb.AppendLine();
            _sb.AppendLine("== " + title + " " + new string('=', Math.Max(2, 56 - title.Length)));
        }

        public void Info(string text) => _sb.AppendLine("   · " + text);

        public void Check(string name, bool ok, string? detail = null)
        {
            if (ok)
            {
                Passed++;
                _sb.AppendLine("   [通过] " + name);
            }
            else
            {
                Failed++;
                _sb.AppendLine("   [失败] " + name + (string.IsNullOrEmpty(detail) ? "" : "   → " + detail));
            }
        }
    }
}
