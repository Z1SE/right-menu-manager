using Microsoft.Win32;
using System.Text;

namespace RightMenuManager;

/// <summary>
/// 扫描引擎：把 Windows 上所有会往右键菜单里塞东西的地方翻一遍。
/// 只读，绝不修改任何东西。
/// </summary>
internal static class Scanner
{
    /// <summary>作用域：右键点在不同东西上，菜单来自不同的注册表位置。</summary>
    private static readonly (string Key, string Label)[] Scopes =
    {
        ("*", "所有文件"),
        ("AllFilesystemObjects", "所有文件与文件夹"),
        ("Directory", "文件夹"),
        ("Directory\\Background", "文件夹空白处"),
        ("Drive", "驱动器"),
        ("Folder", "文件夹（通用）"),
        ("DesktopBackground", "桌面空白处"),
        ("LibraryFolder", "库"),
        ("LibraryFolder\\background", "库空白处"),
    };

    /// <summary>Windows 自带、删了会出问题的项关键字，命中就标记为“系统自带”。</summary>
    private static readonly string[] SystemMarkers =
    {
        "shell32.dll", "windows.storage", "windows.immersive", "explorer.exe",
        "「打开方式」", "OpenWith", "Windows.Photo", "Microsoft.Windows",
    };

    public static ScanResult Scan(Action<string>? progress = null)
    {
        var result = new ScanResult { IsAdministrator = Reg.IsAdministrator };

        var blocked = CollectBlockedClsids(result);

        // HKEY_CURRENT_USER\Software\Classes 不会被 32/64 位重定向，两个视图读到的内容重复，
        // 这里记住 64 位那遍已经见过的项，避免同一项在列表里出现两次。
        var seenCurrentUser = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        progress?.Invoke("正在扫描常规菜单项…");
        foreach (var (scopeKey, scopeLabel) in Scopes)
        {
            foreach (RegistryHive hive in Reg.Hives)
            {
                foreach (RegistryView view in Reg.Views)
                {
                    ScanScope(result, blocked, hive, view, scopeKey, scopeLabel, seenCurrentUser);
                }
            }
        }

        progress?.Invoke("正在扫描文件类型专属菜单…");
        ScanSystemFileAssociations(result, blocked, seenCurrentUser);

        progress?.Invoke("正在扫描各种文件类型自己的菜单…");
        ScanFileTypeKeys(result, blocked, seenCurrentUser);

        progress?.Invoke("正在扫描 Win11 现代菜单扩展…");
        ScanPackagedCom(result, blocked);

        progress?.Invoke("正在扫描“发送到”菜单…");
        ScanSendTo(result);

        result.Entries.Sort((a, b) =>
        {
            int c = string.Compare(a.ScopeLabel, b.ScopeLabel, StringComparison.CurrentCulture);
            if (c != 0) return c;
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture);
        });

        progress?.Invoke("正在合并重复项…");
        List<MenuEntry> merged = MergeDuplicates(result.Entries);
        result.Entries.Clear();
        result.Entries.AddRange(merged);

        return result;
    }

    /// <summary>
    /// 把「同一个程序、同一个名字」的项合并成一条。
    ///
    /// 扫完文件类型那一层之后，列表会膨胀到几千项：Bandizip 的「打开」在它认识的
    /// 每种扩展名下都注册了一份，VS Code 的「open」有几百份，看起来一模一样。
    /// 合并之后一条代表它们全体，操作时一起处理，既看得清也不会漏删。
    /// </summary>
    private static List<MenuEntry> MergeDuplicates(List<MenuEntry> entries)
    {
        var index = new Dictionary<string, MenuEntry>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<MenuEntry>();

        foreach (MenuEntry entry in entries)
        {
            // 分隔符用不可见字符，避免名字里正好含分隔符造成误合并
            string key = entry.Kind + "\u0001" + entry.DisplayName + "\u0001" + entry.ProviderLabel;

            if (index.TryGetValue(key, out MenuEntry? keeper))
            {
                keeper.Members.Add(entry);
            }
            else
            {
                index[key] = entry;
                merged.Add(entry);
            }
        }

        return merged;
    }

    // ------------------------------------------------------------------ 常规菜单项

    private static void ScanScope(
        ScanResult result, HashSet<string> blocked,
        RegistryHive hive, RegistryView view, string scopeKey, string scopeLabel,
        HashSet<string> seenCurrentUser)
    {
        string basis = Reg.ClassesRoot(scopeKey);

        // ① 常规菜单项：...\<作用域>\shell\<动词>
        using (RegistryKey? shell = Reg.Open(hive, view, basis + "\\shell"))
        {
            foreach (string verb in Reg.SubKeyNames(shell))
            {
                using RegistryKey? verbKey = Reg.Open(hive, view, basis + "\\shell\\" + verb);
                if (verbKey == null) continue;

                MenuEntry entry = BuildVerbEntry(hive, view, scopeKey, scopeLabel, basis + "\\shell\\" + verb, verb, verbKey);
                AddEntry(result, entry, seenCurrentUser);
            }
        }

        // ② 系统扩展（COM 处理器）：...\<作用域>\shellex\ContextMenuHandlers\<名字>
        string shellexBasis = basis + "\\shellex\\ContextMenuHandlers";
        using (RegistryKey? handlers = Reg.Open(hive, view, shellexBasis))
        {
            foreach (string name in Reg.SubKeyNames(handlers))
            {
                using RegistryKey? handlerKey = Reg.Open(hive, view, shellexBasis + "\\" + name);
                if (handlerKey == null) continue;

                MenuEntry entry = BuildHandlerEntry(hive, view, scopeKey, scopeLabel, shellexBasis + "\\" + name, name, handlerKey, blocked);
                AddEntry(result, entry, seenCurrentUser);
            }
        }
    }

    /// <summary>加入结果集，并拦掉当前用户 32/64 位视图重复读到的同一项。</summary>
    private static void AddEntry(ScanResult result, MenuEntry entry, HashSet<string> seenCurrentUser)
    {
        if (entry.Hive == RegistryHive.CurrentUser)
        {
            string key = entry.Kind + "|" + entry.SubKeyPath;
            if (!seenCurrentUser.Add(key)) return;
        }
        result.Entries.Add(entry);
    }

    private static MenuEntry BuildVerbEntry(
        RegistryHive hive, RegistryView view, string scopeKey, string scopeLabel,
        string subKeyPath, string verbName, RegistryKey verbKey)
    {
        string? muiVerb = Reg.GetString(verbKey, "MUIVerb");
        string? defaultVal = Reg.GetString(verbKey, null);
        string? delegateExecute = Reg.GetString(verbKey, "DelegateExecute");

        // 名称解析链：注册表里写的显示名 → 委托组件自己的本地化名 → 键名本身。
        // 中间那一步很关键：很多项（比如「固定到快速访问」）自己不带名字，
        // 名字写在它委托的那个组件上。
        string? readable =
            Native.ResolveIndirectString(muiVerb)
            ?? Native.ResolveIndirectString(defaultVal)
            ?? (delegateExecute != null ? ResolveClsidName(delegateExecute.ToUpperInvariant()) : null)
            ?? verbName;

        string rawName = muiVerb ?? defaultVal ?? verbName;

        var entry = new MenuEntry
        {
            Kind = EntryKind.ShellVerb,
            ScopeKey = scopeKey,
            ScopeLabel = scopeLabel,
            Hive = hive,
            View = view,
            SubKeyPath = subKeyPath,
            PhysicalPath = Reg.ToPhysicalPath(hive, view, subKeyPath),
            DisplayName = readable,
            RawName = rawName,
            IconRef = Reg.GetString(verbKey, "Icon"),
        };

        // 命令来源：command 子键的默认值，或 DelegateExecute 委托
        using (RegistryKey? cmdKey = Reg.Open(hive, view, subKeyPath + "\\command"))
        {
            entry.Command = Reg.GetString(cmdKey, null);
        }
        if (string.IsNullOrWhiteSpace(entry.Command) && !string.IsNullOrWhiteSpace(delegateExecute))
        {
            entry.Command = "(由系统组件处理)";
            entry.Clsid = delegateExecute;
            entry.Warnings.Add("这一项不直接调用程序，而是交给系统组件完成，删除后可能有功能对不上。");
        }

        entry.ExePath = ExtractExecutablePath(entry.Command);
        entry.IsOrphan = IsDefinitelyMissing(entry.ExePath);
        if (entry.IsOrphan)
        {
            entry.Warnings.Add("它指向的程序已经不在硬盘上了，多半是某个软件卸载后留下的空壳，清理掉不会有影响。");
        }

        if (Reg.ValueExists(verbKey, "LegacyDisable"))
        {
            entry.IsDisabled = true;
            entry.DisabledHow = "该项自带停用标记（LegacyDisable）";
        }

        if (Reg.ValueExists(verbKey, "Extended"))
        {
            entry.ConditionalNote = "只在按住 Shift 右键时出现";
        }

        if (Reg.ValueExists(verbKey, "ProgrammaticAccessOnly"))
        {
            entry.ConditionalNote = "只对程序调用可见，正常右键看不到";
        }

        ClassifySystem(entry);
        return entry;
    }

    private static MenuEntry BuildHandlerEntry(
        RegistryHive hive, RegistryView view, string scopeKey, string scopeLabel,
        string subKeyPath, string handlerName, RegistryKey handlerKey, HashSet<string> blocked)
    {
        string? value = Reg.GetString(handlerKey, null);
        string? clsid = NormalizeClsid(value) ?? NormalizeClsid(handlerName);

        string? clsidName = clsid != null ? ResolveClsidName(clsid) : null;
        string? readable = Native.ResolveIndirectString(clsidName);

        // 注册表里常常只写「AABdzCtx Class」这种没意义的内部名 ——
        // 用户认的是右键菜单里的「用 Bandizip 打开」，不是 AABdzCtx。
        // 这种情况就去问程序文件自己叫什么。
        if (IsInternalName(readable, handlerName) && clsid != null)
        {
            string? productName = ReadFileProductName(ResolveClsidServer(clsid));
            if (!string.IsNullOrWhiteSpace(productName)) readable = productName;
        }

        var entry = new MenuEntry
        {
            Kind = EntryKind.ShellExtension,
            ScopeKey = scopeKey,
            ScopeLabel = scopeLabel,
            Hive = hive,
            View = view,
            SubKeyPath = subKeyPath,
            PhysicalPath = Reg.ToPhysicalPath(hive, view, subKeyPath),
            DisplayName = readable ?? handlerName,
            RawName = handlerName,
            Clsid = clsid,
        };

        entry.IconRef = clsid != null ? Reg.GetString(Reg.Open(RegistryHive.LocalMachine, view, "Software\\Classes\\CLSID\\" + clsid), "Icon") : null;

        if (clsid != null && blocked.Contains(clsid))
        {
            entry.IsDisabled = true;
            entry.DisabledHow = "已在系统屏蔽名单里";
        }

        if (clsid == null)
        {
            entry.Command = "(注册表里没有写处理程序标识)";
            entry.IsOrphan = false;
        }
        else
        {
            string? server = ResolveClsidServer(clsid);
            if (server != null)
            {
                entry.Command = server;
                entry.ExePath = server;
                entry.IsOrphan = IsDefinitelyMissing(server);
                if (entry.IsOrphan)
                {
                    entry.Warnings.Add("它登记的处理程序文件已经不在硬盘上了，多半是软件卸载后的残留，清理掉不会有影响。");
                }
            }
            else if (!ClsidRegistered(clsid))
            {
                entry.IsOrphan = true;
                entry.Command = "(对应的处理程序在系统里查不到)";
                entry.Warnings.Add("这一项的标识在系统里查不到对应程序，属于软件卸载残留，可以放心清理。");
            }
            else
            {
                // 注册表里只写了模块名、没写完整路径，这是很多系统自带组件的正常写法，绝不是“失效”
                entry.Command = "(系统组件，注册表未直接给出文件路径)";
                entry.IsOrphan = false;
                entry.IsSystem = true;
                entry.Warnings.Add("这是 Windows 自带的组件（系统组件本来就不会写明文件路径），不建议删除。");
            }
        }

        ClassifySystem(entry);
        return entry;
    }

    // ------------------------------------------------------------------ 文件类型专属菜单

    private static void ScanSystemFileAssociations(ScanResult result, HashSet<string> blocked, HashSet<string> seenCurrentUser)
    {
        const string root = "Software\\Classes\\SystemFileAssociations";

        foreach (RegistryHive hive in Reg.Hives)
        {
            foreach (RegistryView view in Reg.Views)
            {
                string[] types = Reg.SubKeyNames(Reg.Open(hive, view, root));
                foreach (string type in types)
                {
                    string basis = root + "\\" + type;
                    string label = "仅 " + type + " 文件";

                    string shellPath = basis + "\\shell";
                    using (RegistryKey? shell = Reg.Open(hive, view, shellPath))
                    {
                        foreach (string verb in Reg.SubKeyNames(shell))
                        {
                            using RegistryKey? verbKey = Reg.Open(hive, view, shellPath + "\\" + verb);
                            if (verbKey == null) continue;
                            MenuEntry verbEntry = BuildVerbEntry(hive, view, type, label, shellPath + "\\" + verb, verb, verbKey);
                            verbEntry.IsFileTypeSpecific = true;
                            AddEntry(result, verbEntry, seenCurrentUser);
                        }
                    }

                    string shellexPath = basis + "\\shellex\\ContextMenuHandlers";
                    using (RegistryKey? handlers = Reg.Open(hive, view, shellexPath))
                    {
                        foreach (string name in Reg.SubKeyNames(handlers))
                        {
                            using RegistryKey? handlerKey = Reg.Open(hive, view, shellexPath + "\\" + name);
                            if (handlerKey == null) continue;
                            MenuEntry handlerEntry = BuildHandlerEntry(hive, view, type, label, shellexPath + "\\" + name, name, handlerKey, blocked);
                            handlerEntry.IsFileTypeSpecific = true;
                            AddEntry(result, handlerEntry, seenCurrentUser);
                        }
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ 文件类型自己的菜单

    /// <summary>
    /// 扫描「文件类型」这一类键（HKCR\lnkfile、HKCR\exefile、HKCR\.txt …）自己的菜单项。
    ///
    /// 这一层以前完全没扫，后果是：有些项明明在右键菜单里看得见，程序里却找不到。
    /// 比如：
    ///   · HiBit Uninstaller 把自己挂在 lnkfile 上 —— 只有右键快捷方式才出现
    ///   · 「打开文件所在的位置」挂在 lnkfile 的 shellex 上
    ///   · 「兼容性疑难解答」挂在 exefile 上
    /// 它们既不在 * 下，也不在 SystemFileAssociations 下，而是直接挂在文件类型键上。
    /// </summary>
    private static void ScanFileTypeKeys(ScanResult result, HashSet<string> blocked, HashSet<string> seenCurrentUser)
    {
        // 这些要么不是「文件类型」，要么前面已经单独扫过了
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CLSID", "AppID", "Interface", "TypeLib", "Record", "Wow6432Node",
            "Installer", "Component Categories", "Licenses", "Extensions", "MIME",
            "Applications", "AppPaths", "Drive", "*", "AllFilesystemObjects",
            "Directory", "Folder", "DesktopBackground", "LibraryFolder",
            "SystemFileAssociations", "Local Settings", "Protocols", "Shell",
        };

        foreach (RegistryHive hive in Reg.Hives)
        {
            foreach (RegistryView view in Reg.Views)
            {
                foreach (string typeKey in Reg.SubKeyNames(Reg.Open(hive, view, "Software\\Classes")))
                {
                    if (skip.Contains(typeKey)) continue;

                    string basis = "Software\\Classes\\" + typeKey;
                    string label = DescribeFileType(typeKey);

                    string shellPath = basis + "\\shell";
                    using (RegistryKey? shell = Reg.Open(hive, view, shellPath))
                    {
                        foreach (string verb in Reg.SubKeyNames(shell))
                        {
                            using RegistryKey? verbKey = Reg.Open(hive, view, shellPath + "\\" + verb);
                            if (verbKey == null) continue;
                            MenuEntry entry = BuildVerbEntry(hive, view, typeKey, label, shellPath + "\\" + verb, verb, verbKey);
                            entry.IsFileTypeSpecific = !IsCommonRightClickTarget(typeKey);
                            AddEntry(result, entry, seenCurrentUser);
                        }
                    }

                    string shellexPath = basis + "\\shellex\\ContextMenuHandlers";
                    using (RegistryKey? handlers = Reg.Open(hive, view, shellexPath))
                    {
                        foreach (string name in Reg.SubKeyNames(handlers))
                        {
                            using RegistryKey? handlerKey = Reg.Open(hive, view, shellexPath + "\\" + name);
                            if (handlerKey == null) continue;
                            MenuEntry entry = BuildHandlerEntry(hive, view, typeKey, label, shellexPath + "\\" + name, name, handlerKey, blocked);
                            entry.IsFileTypeSpecific = !IsCommonRightClickTarget(typeKey);
                            AddEntry(result, entry, seenCurrentUser);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 快捷方式和程序文件是大家最常右键的东西，挂在它们上面的菜单项不该被归进
    /// 「文件类型专属」里藏起来 —— HiBit Uninstaller 就是挂在 lnkfile 上的，
    /// 一藏起来，用户恰恰找不到它。
    /// </summary>
    private static bool IsCommonRightClickTarget(string typeKey) =>
        typeKey is "lnkfile" or "exefile" or "Unknown" or "InternetShortcut";

    /// <summary>把注册表里的类型键翻译成人话，例如 lnkfile → 「仅快捷方式」。</summary>
    private static string DescribeFileType(string typeKey)
    {
        if (typeKey.StartsWith('.')) return "仅 " + typeKey + " 文件";

        return typeKey switch
        {
            "lnkfile" => "仅快捷方式",
            "exefile" => "仅程序文件",
            "txtfile" => "仅文本文档",
            "InternetShortcut" => "仅网址快捷方式",
            "Unknown" => "仅未知类型文件",
            "htmlfile" => "仅网页文件",
            "imagefile" => "仅图片文件",
            "Word.Document.8" or "Word.Document.12" => "仅 Word 文档",
            "Excel.Sheet.12" or "Excel.Sheet.8" => "仅 Excel 表格",
            _ => "仅 " + typeKey + " 类型",
        };
    }

    // ------------------------------------------------------------------ Win11 现代菜单

    /// <summary>
    /// 扫描「稀疏包」注册的现代菜单扩展。
    ///
    /// 这类项在传统 shell 键里完全找不到 —— QQ 的「通过QQ发送到」就是这样：
    /// 它注册成一个 MSIX 稀疏包，壳子写在
    ///   HKLM\SOFTWARE\Classes\PackagedCom\Package\QQExtension_...\Server\0\DisplayName
    /// 里，右键菜单里显示的中文名则来自 DLL 内部。不扫这一层就永远找不到它。
    /// </summary>
    private static void ScanPackagedCom(ScanResult result, HashSet<string> blocked)
    {
        const string root = "Software\\Classes\\PackagedCom\\Package";
        const string serverRootTail = "\\Server";
        const string classRootTail = "\\Class";

        foreach (string package in Reg.SubKeyNames(Reg.Open(RegistryHive.LocalMachine, RegistryView.Registry64, root)))
        {
            string packagePath = root + "\\" + package;
            string serverRoot = packagePath + serverRootTail;
            string classRoot = packagePath + classRootTail;

            // 先把这个包各个 Server 的显示名收起来，Class 靠 ServerId 关联过去
            var serverNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string serverId in Reg.SubKeyNames(Reg.Open(RegistryHive.LocalMachine, RegistryView.Registry64, serverRoot)))
            {
                using RegistryKey? server = Reg.Open(RegistryHive.LocalMachine, RegistryView.Registry64, serverRoot + "\\" + serverId);
                if (server == null) continue;

                string? name = Reg.GetString(server, "DisplayName") ?? Reg.GetString(server, "ApplicationDisplayName");
                if (!string.IsNullOrWhiteSpace(name)) serverNames[serverId] = name!.Trim();
            }

            foreach (string classKey in Reg.SubKeyNames(Reg.Open(RegistryHive.LocalMachine, RegistryView.Registry64, classRoot)))
            {
                using RegistryKey? cls = Reg.Open(RegistryHive.LocalMachine, RegistryView.Registry64, classRoot + "\\" + classKey);
                if (cls == null) continue;

                string? serverId = Reg.GetString(cls, "ServerId");
                string displayName =
                    (serverId != null && serverNames.TryGetValue(serverId, out string? fromServer)) ? fromServer
                    : ShortPackageName(package);

                string? clsid = NormalizeClsid(classKey);
                string subKeyPath = classRoot + "\\" + classKey;

                var entry = new MenuEntry
                {
                    Kind = EntryKind.ShellExtension,
                    ScopeKey = "PackagedCom",
                    ScopeLabel = "Win11 现代菜单",
                    Hive = RegistryHive.LocalMachine,
                    View = RegistryView.Registry64,
                    SubKeyPath = subKeyPath,
                    PhysicalPath = Reg.ToPhysicalPath(RegistryHive.LocalMachine, RegistryView.Registry64, subKeyPath),
                    DisplayName = displayName,
                    RawName = package,
                    Clsid = clsid,
                    Command = Reg.GetString(cls, "DllPath"),
                };

                // 包的 DllPath 是包内相对路径，没法判断程序还在不在，所以不做失效判定
                if (clsid != null && blocked.Contains(clsid))
                {
                    entry.IsDisabled = true;
                    entry.DisabledHow = "已在系统屏蔽名单里";
                }

                entry.IsSystem = package.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
                if (!entry.IsSystem)
                {
                    entry.Warnings.Add("这是 Windows 11 现代菜单（稀疏包）注册的扩展，停用会把它加进系统的屏蔽名单。");
                }

                result.Entries.Add(entry);
            }
        }
    }

    /// <summary>把包名裁成人能读的样子：QQExtension_1.0.0.0_neutral__fv0hcek9q9tbw → QQExtension。</summary>
    private static string ShortPackageName(string package)
    {
        int underscore = package.IndexOf('_');
        return underscore > 0 ? package[..underscore] : package;
    }

    // ------------------------------------------------------------------ 发送到

    private static void ScanSendTo(ScanResult result)
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
        if (!Directory.Exists(folder)) return;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder); }
        catch { return; }

        foreach (string file in files)
        {
            string fileName = Path.GetFileName(file);

            // desktop.ini 之类是文件夹自身的配置文件，不是菜单项
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                FileAttributes attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.Hidden) != 0) continue;
            }
            catch { }

            // 只有能解析出目标的快捷方式才谈得上“失效”。
            // .ZFSendToTarget / .MAPIMail / .DeskLink 这类是由系统组件处理的特殊文件，
            // 它们本身就是一个文件，不适用“指向的程序还在不在”的判断。
            string? target = ResolveShortcutTarget(file);

            var entry = new MenuEntry
            {
                Kind = EntryKind.SendTo,
                ScopeLabel = "发送到",
                ScopeKey = "SendTo",
                Hive = RegistryHive.CurrentUser,
                View = RegistryView.Registry64,
                SubKeyPath = "",
                PhysicalPath = file,
                DisplayName = Path.GetFileNameWithoutExtension(file),
                RawName = fileName,
                FilePath = file,
                Command = target ?? fileName + "（由系统组件处理）",
            };

            if (target != null)
            {
                entry.ExePath = ExtractExecutablePath(target);
                entry.IsOrphan = IsDefinitelyMissing(entry.ExePath);
                if (entry.IsOrphan)
                {
                    entry.Warnings.Add("这个快捷方式指向的程序已经找不到了，多半是软件卸载后留下的，可以清理。");
                }
            }

            entry.IsSystem = IsWindowsBuiltInSendTo(fileName, target);

            result.Entries.Add(entry);
        }
    }

    /// <summary>
    /// 「发送到」文件夹里混着 Windows 自己放进去的东西（蓝牙传送、压缩文件夹、邮件收件人…）。
    /// 它们和第三方软件加的项躺在同一个用户目录里，光看位置分不出来，只能按特征认，
    /// 否则会被统统算成「第三方软件」，让筛选结果骗人。
    /// </summary>
    private static bool IsWindowsBuiltInSendTo(string fileName, string? targetPath)
    {
        // 这些扩展名是 Windows 自己那套「发送到」处理器专用的（注意 .desklink 中间有 i，
        // 别按印象写成 .desklnk —— 实际去目录里看一眼才对得上）
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".zfsendtotarget" or ".mapimail" or ".desklink" or ".mydocs")
        {
            return true;
        }

        if (!string.IsNullOrEmpty(targetPath))
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0 &&
                targetPath.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string? ResolveShortcutTarget(string lnkPath)
    {
        if (!lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            object? shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;
            try
            {
                object? shortcut = shellType.InvokeMember(
                    "CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (shortcut == null) return null;
                object? target = shortcut.GetType().InvokeMember(
                    "TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
                return target as string;
            }
            finally
            {
                if (shell is IDisposable d) d.Dispose();
                else System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ 屏蔽名单

    private static HashSet<string> CollectBlockedClsids(ScanResult result)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string path = "Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Blocked";

        foreach (RegistryHive hive in Reg.Hives)
        {
            foreach (RegistryView view in Reg.Views)
            {
                using RegistryKey? key = Reg.Open(hive, view, path);
                if (key == null) continue;
                foreach (string name in SafeValueNames(key))
                {
                    string? normalized = NormalizeClsid(name);
                    if (normalized != null) set.Add(normalized);
                }
            }
        }

        if (set.Count > 0)
        {
            result.Notes.Add($"系统屏蔽名单里有 {set.Count} 项被停用的扩展。");
        }
        return set;
    }

    private static string[] SafeValueNames(RegistryKey key)
    {
        try { return key.GetValueNames(); }
        catch { return Array.Empty<string>(); }
    }

    // ------------------------------------------------------------------ 解析辅助

    private static string? NormalizeClsid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim();
        if (v.StartsWith('{') && v.EndsWith('}') && v.Length >= 38) return v.ToUpperInvariant();
        return null;
    }

    /// <summary>
    /// 判断注册表给出的名字是不是「没意义的内部名」。
    /// 这种值对用户等于噪音：既看不出是哪个软件，也对不上右键菜单里显示的字。
    /// </summary>
    private static bool IsInternalName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.Equals(fallback, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(" Class", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 从程序文件的产品信息里取名字。
    /// 注册表没给可读名时，这是唯一能跟右键菜单对上号的线索。
    /// </summary>
    private static string? ReadFileProductName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            if (!File.Exists(path)) return null;

            System.Diagnostics.FileVersionInfo info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            string? candidate = info.FileDescription;
            if (string.IsNullOrWhiteSpace(candidate)) candidate = info.ProductName;
            if (string.IsNullOrWhiteSpace(candidate)) return null;

            candidate = candidate.Trim();
            return candidate.Length > 0 ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 查 {CLSID} 对应的可读名字。
    /// 必须跨 32/64 位两个视图找：菜单项登记在哪个视图、组件本身登记在哪个视图，两者不一定一致。
    ///
    /// 名称有三个来源，优先级不能搞错：
    ///   ① LocalizedString —— 本地化名称，中文系统上用户真正看到的字就是它
    ///   ② MUIVerb        —— 有些组件只写这个
    ///   ③ 默认值          —— 通常是英文原名，只有在上面两个都没有时才用
    /// 之前漏了 ①，结果列表里全是「Library Folder Context Menu」这种英文，
    /// 跟右键菜单里看到的中文对不上号。
    /// </summary>
    private static string? ResolveClsidName(string clsid)
    {
        foreach (RegistryView view in Reg.Views)
        {
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                using RegistryKey? key = Reg.Open(hive, view, "Software\\Classes\\CLSID\\" + clsid);
                if (key == null) continue;

                string? resolved = Native.ResolveIndirectString(Reg.GetString(key, "LocalizedString"));
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved;

                resolved = Native.ResolveIndirectString(Reg.GetString(key, "MUIVerb"));
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved;

                string? plain = Reg.GetString(key, null);
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    resolved = Native.ResolveIndirectString(plain);
                    if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 查 {CLSID} 对应的程序文件。
    /// 注意：很多 Windows 自带组件在这里只写一个模块名而不是完整路径，这是正常的，
    /// 这种情况返回 null，调用方绝不能据此判定“失效”。
    /// </summary>
    private static string? ResolveClsidServer(string clsid)
    {
        string[] serverKeys = { "InprocServer32", "LocalServer32" };

        foreach (RegistryView view in Reg.Views)
        {
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (string serverKey in serverKeys)
                {
                    using RegistryKey? key = Reg.Open(hive, view, "Software\\Classes\\CLSID\\" + clsid + "\\" + serverKey);
                    if (key == null) continue;

                    string? value = Reg.GetString(key, null);
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    string candidate = value.Trim().Trim('"');
                    if (candidate.StartsWith('@')) candidate = candidate[1..];
                    candidate = Environment.ExpandEnvironmentVariables(candidate);
                    if (candidate.Length == 0) continue;

                    if (Path.IsPathRooted(candidate)) return candidate;

                    // 只写了模块名时，到系统目录里猜一下
                    string inSystem = Path.Combine(Environment.SystemDirectory, candidate);
                    if (File.Exists(inSystem)) return inSystem;
                    if (File.Exists(inSystem + ".dll")) return inSystem + ".dll";
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 这个 {CLSID} 在系统里到底注册过没有。
    /// 用来区分两种情况：真的卸载残留（整个标识都不在了）vs 系统组件没写文件路径。
    /// </summary>
    private static bool ClsidRegistered(string clsid)
    {
        foreach (RegistryView view in Reg.Views)
        {
            foreach (RegistryHive hive in Reg.Hives)
            {
                using RegistryKey? key = Reg.Open(hive, view, "Software\\Classes\\CLSID\\" + clsid);
                if (key != null) return true;
            }
        }
        return false;
    }

    /// <summary>从命令行文本里抠出真正被调用的程序路径。</summary>
    public static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string text = command.Trim();

        // 丢弃常见的启动器前缀，尽量找到第一个像路径的片段
        string candidate;
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            candidate = end > 0 ? text[1..end] : text.Trim('"');
        }
        else
        {
            int space = text.IndexOf(' ');
            candidate = space > 0 ? text[..space] : text;
        }

        candidate = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
        if (candidate.Length == 0) return null;

        // 只有看起来像可执行文件的才当程序路径
        string ext = Path.GetExtension(candidate).ToLowerInvariant();
        if (ext is ".exe" or ".dll" or ".com" or ".bat" or ".cmd" or ".ps1" or ".msc" or ".cpl")
        {
            if (!Path.IsPathRooted(candidate))
            {
                // 命令里只写了程序名（例如 powershell.exe），先到系统目录和 PATH 里找一遍
                string inSystem = Path.Combine(Environment.SystemDirectory, candidate);
                if (File.Exists(inSystem)) return inSystem;

                string? onPath = FindOnPath(candidate);
                if (onPath != null) return onPath;

                // 实在找不到就原样返回。它只是个程序名，可能在别处能找到，不能据此断定失效。
                return candidate;
            }
            return candidate;
        }

        return Path.IsPathRooted(candidate) ? candidate : null;
    }

    private static string? FindOnPath(string fileName)
    {
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable)) return null;

        foreach (string directory in pathVariable.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                string full = Path.Combine(directory.Trim().Trim('"'), fileName);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // 路径里有非法字符，跳过
            }
        }
        return null;
    }

    /// <summary>
    /// 判断“它指向的程序确实已经不在了”。
    /// 只有拿到完整路径、而且文件真的不存在，才算数。
    /// 光是一个程序名（可能藏在 PATH 里）、或者环境变量没展开成功，都不算——否则会把
    /// 「在此处打开 PowerShell 窗口」这种正常项目误报成失效。
    /// </summary>
    private static bool IsDefinitelyMissing(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        if (exePath.Contains('%')) return false;
        if (!Path.IsPathRooted(exePath)) return false;
        try { return !File.Exists(exePath); }
        catch { return false; }
    }

    private static void ClassifySystem(MenuEntry entry)
    {
        string haystack = string.Join(" ", new[]
        {
            entry.DisplayName, entry.Command, entry.ExePath, entry.Clsid, entry.RawName,
        }.Where(s => !string.IsNullOrEmpty(s)));

        if (entry.Hive == RegistryHive.LocalMachine &&
            (haystack.Contains("shell32.dll", StringComparison.OrdinalIgnoreCase)
             || haystack.Contains("Windows\\System32", StringComparison.OrdinalIgnoreCase)
             || haystack.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
             || haystack.Contains("Windows.Storage", StringComparison.OrdinalIgnoreCase)))
        {
            entry.IsSystem = true;
            entry.Warnings.Add("看起来是 Windows 自带的功能，删掉可能会少一些系统操作（比如“打开方式”“固定到快速访问”），一般不建议动。");
        }
    }

    // ------------------------------------------------------------------ 命令行自测输出

    public static void DumpTo(string path)
    {
        ScanResult r = Scan();
        var sb = new StringBuilder();
        sb.AppendLine($"扫描时间：{r.ScannedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"管理员权限：{(r.IsAdministrator ? "是" : "否")}");
        sb.AppendLine($"共找到：{r.Entries.Count} 项");
        foreach (string note in r.Notes) sb.AppendLine("说明：" + note);
        sb.AppendLine(new string('-', 100));
        foreach (MenuEntry e in r.Entries)
        {
            sb.AppendLine($"[{e.KindLabel}] {e.DisplayName}");
            sb.AppendLine($"    位置：{e.ScopeLabel}    场景：{e.PlacementLabel}    归属：{e.OriginLabel}");
            sb.AppendLine($"    哪个程序：{e.ProviderLabel}");
            sb.AppendLine($"    来源：{e.SourceLabel}    状态：{e.StatusLabel}");
            sb.AppendLine($"    命令：{e.Command ?? "(无)"}");
            sb.AppendLine($"    注册表：{e.PhysicalPath}");
            if (e.Clsid != null) sb.AppendLine($"    CLSID：{e.Clsid}");
            if (e.ConditionalNote != null) sb.AppendLine($"    条件：{e.ConditionalNote}");
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
