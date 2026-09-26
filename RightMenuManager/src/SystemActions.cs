using Microsoft.Win32;
using System.Diagnostics;
using System.Text;

namespace RightMenuManager;


/// <summary>
/// 所有会改动系统的动作都集中在这里，并且遵守一条铁律：
/// 删除之前一定先留下备份文件，备份文件放在用户能找到的地方。
/// </summary>
internal static class BackupManager
{
    /// <summary>用户自己选的备份位置；没选过就是 null，用默认的「我的文档\右键菜单备份」。</summary>
    private static string? _customFolder;

    /// <summary>
    /// 备份文件存放位置。默认放在我的文档下（方便自己去翻），
    /// 用户可以在「备份与还原」页里改成别的地方 —— 比如放到 D 盘或网盘同步目录。
    /// </summary>
    public static string BackupFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_customFolder)) return _customFolder!;

            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs))
            {
                docs = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            }
            return Path.Combine(docs, "右键菜单备份");
        }
    }

    /// <summary>用户是不是自己指定过位置（界面上要区分「默认」和「自定义」）。</summary>
    public static bool HasCustomFolder => !string.IsNullOrWhiteSpace(_customFolder);

    /// <summary>改备份位置。传 null 就退回默认位置。</summary>
    public static void SetBackupFolder(string? path)
    {
        _customFolder = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    // ------------------------------------------------------------------ 设置存取

    /// <summary>设置文件。只存这一件事，放用户自己的 AppData 下。</summary>
    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RightMenuManager", "settings.ini");

    public static void LoadSettings()
    {
        try
        {
            string file = SettingsFile;
            if (!File.Exists(file)) return;

            foreach (string line in File.ReadAllLines(file))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();
                if (key.Equals("BackupFolder", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                {
                    _customFolder = value;
                }
            }
        }
        catch
        {
            // 读不出来就用默认位置，不影响用
        }
    }

    public static void SaveSettings()
    {
        try
        {
            string file = SettingsFile;
            string? dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(file, "BackupFolder=" + (_customFolder ?? "") + Environment.NewLine);
        }
        catch
        {
            // 存不下也没关系：这次的选择在本次运行里仍然有效
        }
    }

    public static void EnsureBackupFolder()
    {
        Directory.CreateDirectory(BackupFolder);
    }

    private static string TimestampedName(string baseName)
    {
        string safe = string.Join("_", baseName.Split(Path.GetInvalidFileNameChars()));
        if (safe.Length > 60) safe = safe[..60];
        return $"{DateTime.Now:yyyyMMdd-HHmmss}_{safe}.reg";
    }

    // ================================================================ 备份

    /// <summary>
    /// 把一个注册表键连同它下面的所有内容导出成 .reg 文件。返回文件路径。
    /// </summary>
    public static string ExportEntry(MenuEntry entry)
    {
        if (entry.Kind == EntryKind.SendTo)
        {
            return BackupSendToFile(entry);
        }

        EnsureBackupFolder();
        string file = Path.Combine(BackupFolder, TimestampedName(entry.DisplayName));

        using RegistryKey? baseKey = Reg.Base(entry.Hive, entry.View);
        if (baseKey == null) throw new InvalidOperationException("无法访问注册表。");

        using RegistryKey? key = baseKey.OpenSubKey(entry.SubKeyPath);
        if (key == null) throw new InvalidOperationException("这一项已经不存在了，可能已被其他程序处理。");

        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();
        WriteKeyRecursive(sb, key, Reg.ToPhysicalPath(entry.Hive, entry.View, entry.SubKeyPath));

        File.WriteAllText(file, sb.ToString(), new UnicodeEncoding(false, true));
        return file;
    }

    /// <summary>把当前扫描到的全部项目一次性导出成一个大备份。</summary>
    public static string ExportAll(IEnumerable<MenuEntry> entries)
    {
        EnsureBackupFolder();
        string file = Path.Combine(BackupFolder, $"全部菜单项备份_{DateTime.Now:yyyyMMdd-HHmmss}.reg");

        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();

        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MenuEntry entry in entries)
        {
            if (entry.Kind == EntryKind.SendTo) continue;
            if (!done.Add(entry.PhysicalPath + "|" + entry.View)) continue;

            using RegistryKey? baseKey = Reg.Base(entry.Hive, entry.View);
            using RegistryKey? key = baseKey?.OpenSubKey(entry.SubKeyPath);
            if (key == null) continue;
            WriteKeyRecursive(sb, key, entry.PhysicalPath);
        }

        File.WriteAllText(file, sb.ToString(), new UnicodeEncoding(false, true));
        return file;
    }

    private static string BackupSendToFile(MenuEntry entry)
    {
        EnsureBackupFolder();
        string sub = Path.Combine(BackupFolder, "发送到_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(sub);
        string target = Path.Combine(sub, Path.GetFileName(entry.FilePath ?? entry.DisplayName));
        if (entry.FilePath != null && File.Exists(entry.FilePath))
        {
            File.Move(entry.FilePath, target);
        }
        return target;
    }

    private static void WriteKeyRecursive(StringBuilder sb, RegistryKey key, string physicalPath)
    {
        sb.AppendLine($"[{physicalPath}]");

        string[] names;
        try { names = key.GetValueNames(); }
        catch { names = Array.Empty<string>(); }

        foreach (string name in names)
        {
            string label = string.IsNullOrEmpty(name) ? "@" : "\"" + EscapeRegString(name) + "\"";
            try
            {
                RegistryValueKind kind = key.GetValueKind(name);
                object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                sb.AppendLine($"{label}={FormatValue(kind, value)}");
            }
            catch
            {
                // 读不出来的值就跳过，不因为一条坏数据中断整份备份
            }
        }
        sb.AppendLine();

        string[] subs;
        try { subs = key.GetSubKeyNames(); }
        catch { subs = Array.Empty<string>(); }

        foreach (string sub in subs)
        {
            using RegistryKey? child = key.OpenSubKey(sub);
            if (child == null) continue;
            WriteKeyRecursive(sb, child, physicalPath + "\\" + sub);
        }
    }

    private static string EscapeRegString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string FormatValue(RegistryValueKind kind, object? value)
    {
        switch (kind)
        {
            case RegistryValueKind.String:
                return "\"" + EscapeRegString(value as string ?? "") + "\"";

            case RegistryValueKind.ExpandString:
                return HexBlock("hex(2):", Encoding.Unicode.GetBytes((value as string ?? "") + "\0"));

            case RegistryValueKind.MultiString:
            {
                var parts = value as string[] ?? Array.Empty<string>();
                var bytes = new List<byte>();
                foreach (string part in parts) bytes.AddRange(Encoding.Unicode.GetBytes(part + "\0"));
                bytes.AddRange(new byte[] { 0, 0 });
                return HexBlock("hex(7):", bytes.ToArray());
            }

            case RegistryValueKind.Binary:
                return HexBlock("hex:", value as byte[] ?? Array.Empty<byte>());

            case RegistryValueKind.DWord:
                return "dword:" + Convert.ToUInt32(value ?? 0).ToString("x8");

            case RegistryValueKind.QWord:
                return HexBlock("hex(b):", BitConverter.GetBytes(Convert.ToInt64(value ?? 0L)));

            case RegistryValueKind.None:
                return HexBlock("hex(0):", value as byte[] ?? Array.Empty<byte>());

            default:
                return "\"" + EscapeRegString(value?.ToString() ?? "") + "\"";
        }
    }

    private static string HexBlock(string prefix, byte[] bytes)
    {
        var sb = new StringBuilder(prefix);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                if (i % 25 == 0)
                {
                    sb.Append(",\\\r\n  ");
                }
                else
                {
                    sb.Append(',');
                }
            }
            sb.Append(bytes[i].ToString("x2"));
        }
        return sb.ToString();
    }

    // ================================================================ 还原

    /// <summary>把备份文件导回注册表。</summary>
    public static (bool Ok, string Message) RestoreFromRegFile(string regFile)
    {
        try
        {
            var psi = new ProcessStartInfo("reg.exe", $"import \"{regFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process? p = Process.Start(psi);
            if (p == null) return (false, "无法启动还原程序。");
            p.WaitForExit(30000);
            return p.HasExited && p.ExitCode == 0
                ? (true, "已还原。")
                : (false, $"还原失败（返回码 {p.ExitCode}）。如果这一项属于系统范围，请用管理员身份重试。");
        }
        catch (Exception ex)
        {
            return (false, "还原失败：" + ex.Message);
        }
    }

    // ================================================================ 停用 / 启用

    /// <summary>停用一项。不删除任何东西，随时可以恢复。</summary>
    public static void Disable(MenuEntry entry)
    {
        switch (entry.Kind)
        {
            case EntryKind.ShellVerb:
            {
                using RegistryKey? baseKey = Reg.Base(entry.Hive, entry.View);
                using RegistryKey? key = baseKey?.OpenSubKey(entry.SubKeyPath, writable: true);
                if (key == null)
                {
                    // 只有 Software\Classes 下的项才有「当前用户覆盖」这回事 ——
                    // HKCU 的同名键优先于 HKLM，写在那儿是真盖得住系统项的。
                    // 换成别的路径（比如 Windows 内置命令的 CommandStore）就不成立了：
                    // 在 HKCU 下建个同名键根本没人看，用户会以为停用成功，实际菜单纹丝不动。
                    if (!entry.SubKeyPath.StartsWith("Software\\Classes\\", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "这一项属于系统范围，需要管理员权限才能停用。请点右上角「以管理员身份重启」后再试。");
                    }

                    // 系统范围的项普通权限改不动，退而求其次写到当前用户自己的覆盖层
                    using RegistryKey mine = Registry.CurrentUser.CreateSubKey(entry.SubKeyPath, true)
                        ?? throw new InvalidOperationException("没有权限停用这一项，请以管理员身份重试。");
                    mine.SetValue("LegacyDisable", "", RegistryValueKind.String);
                    return;
                }
                key.SetValue("LegacyDisable", "", RegistryValueKind.String);
                return;
            }

            case EntryKind.ShellExtension:
            {
                if (string.IsNullOrWhiteSpace(entry.Clsid))
                    throw new InvalidOperationException("这一项没有可识别的标识，无法停用。请改用删除。");
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", true)
                    ?? throw new InvalidOperationException("没有权限写入屏蔽名单。");
                key.SetValue(entry.Clsid, entry.DisplayName, RegistryValueKind.String);
                return;
            }

            case EntryKind.SendTo:
            {
                if (entry.FilePath != null && File.Exists(entry.FilePath))
                {
                    string parked = entry.FilePath + ".disabled";
                    File.Move(entry.FilePath, parked, overwrite: true);
                }
                return;
            }
        }
    }

    /// <summary>恢复一项被停用的项目。</summary>
    public static void Enable(MenuEntry entry)
    {
        switch (entry.Kind)
        {
            case EntryKind.ShellVerb:
            {
                using RegistryKey? baseKey = Reg.Base(entry.Hive, entry.View);
                using RegistryKey? key = baseKey?.OpenSubKey(entry.SubKeyPath, writable: true);
                if (key == null)
                {
                    using RegistryKey? mine = Registry.CurrentUser.OpenSubKey(entry.SubKeyPath, writable: true);
                    mine?.DeleteValue("LegacyDisable", false);
                    return;
                }
                key.DeleteValue("LegacyDisable", false);
                return;
            }

            case EntryKind.ShellExtension:
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", true);
                if (key != null && entry.Clsid != null) key.DeleteValue(entry.Clsid, false);

                // 也清一下系统级的屏蔽名单（有权限才动得了）
                using RegistryKey? sysKey = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", true);
                if (sysKey != null && entry.Clsid != null) sysKey.DeleteValue(entry.Clsid, false);
                return;
            }

            case EntryKind.SendTo:
            {
                if (entry.FilePath != null)
                {
                    string parked = entry.FilePath + ".disabled";
                    if (File.Exists(parked)) File.Move(parked, entry.FilePath, overwrite: true);
                }
                return;
            }
        }
    }

    // ================================================================ 删除

    /// <summary>
    /// 删除一项。返回 (是否成功, 备份文件路径, 说明)。
    /// 一定会先备份；备份失败就不删。
    /// </summary>
    public static (bool Ok, string? BackupFile, string Message) Delete(MenuEntry entry)
    {
        // Windows 11 现代菜单扩展（稀疏包）的注册属于那个软件的安装包本身，
        // 删掉可能让软件自己的功能出问题，备份也未必能完整还原。
        // 这类项只允许「停用」—— 效果一样，而且完全可逆。
        if (entry.ScopeKey == "PackagedCom")
        {
            return (false, null,
                "这是 Windows 11 现代菜单扩展，它的注册属于那个软件的安装包，"
                + "删掉可能让软件本身出问题。请改用「停用」—— 效果一样，而且随时能恢复。");
        }

        string backupFile;
        try
        {
            backupFile = ExportEntry(entry);
        }
        catch (Exception ex)
        {
            return (false, null, "备份没做成，出于安全考虑没有执行删除。" + ex.Message);
        }

        try
        {
            if (entry.Kind == EntryKind.SendTo)
            {
                return (true, backupFile, "已移入备份文件夹。");
            }

            using RegistryKey? baseKey = Reg.Base(entry.Hive, entry.View);
            if (baseKey == null) return (false, backupFile, "无法访问注册表。");

            int split = entry.SubKeyPath.LastIndexOf('\\');
            if (split <= 0) return (false, backupFile, "注册表路径异常，未执行删除。");
            string parent = entry.SubKeyPath[..split];
            string leaf = entry.SubKeyPath[(split + 1)..];

            using RegistryKey? parentKey = baseKey.OpenSubKey(parent, writable: true);
            if (parentKey == null)
            {
                return (false, backupFile,
                    "没有权限删除这一项。它属于系统范围，请点上面的「以管理员身份重启」后再试。");
            }

            parentKey.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
            return (true, backupFile, "已删除。");
        }
        catch (Exception ex)
        {
            return (false, backupFile, "删除失败：" + ex.Message);
        }
    }
}


/// <summary>
/// Windows 11 默认把右键菜单折叠成一小撮，要点“显示更多选项”才看得到全部。
/// 这里负责在「Win11 精简菜单」和「Win10 完整菜单」之间切换。
/// </summary>
internal static class ClassicMenu
{
    private const string ClsidKey =
        @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    /// <summary>当前是否已经切成完整版（Win10 风格）菜单。</summary>
    public static bool IsLegacyMenuEnabled()
    {
        using RegistryKey? key = Reg.Open(
            RegistryHive.CurrentUser, RegistryView.Registry64, ClsidKey + "\\InprocServer32");
        if (key == null) return false;
        string? value = Reg.GetString(key, null);
        return value != null && value.Length == 0;
    }

    /// <summary>切成 Win10 风格的完整右键菜单。</summary>
    public static void EnableLegacyMenu()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(ClsidKey + "\\InprocServer32", true)
            ?? throw new InvalidOperationException("无法写入注册表。");
        key.SetValue(null, "", RegistryValueKind.String);
    }

    /// <summary>恢复到 Windows 11 默认的精简菜单。</summary>
    public static void DisableLegacyMenu()
    {
        Registry.CurrentUser.DeleteSubKeyTree(ClsidKey, throwOnMissingSubKey: false);
    }

    /// <summary>
    /// 重启资源管理器让改动生效。桌面和任务栏会闪一下，这是正常的。
    /// </summary>
    public static bool RestartExplorer()
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c taskkill /f /im explorer.exe & start explorer.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 以管理员身份重新启动本程序。用户点 UAC 的“是”之后新窗口才会出现。
    /// </summary>
    public static bool RestartAsAdministrator()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            // 用户在 UAC 弹窗上点了“否”
            return false;
        }
    }
}
