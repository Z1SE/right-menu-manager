using Microsoft.Win32;

namespace RightMenuManager;

internal enum EntryKind
{
    /// <summary>常规菜单项：注册表 shell 子键里的一条命令。</summary>
    ShellVerb,

    /// <summary>系统扩展：某个程序注册的 COM 处理器（WinRAR、7-Zip 这类多半是它）。</summary>
    ShellExtension,

    /// <summary>“发送到”菜单里的一个快捷方式。</summary>
    SendTo,
}

/// <summary>一条右键菜单项在界面上的全部信息。</summary>
internal sealed class MenuEntry
{
    /// <summary>界面表格里显示的名字。</summary>
    public string DisplayName { get; set; } = "(未命名)";

    /// <summary>注册表里存的原始名字，可能是 @dll,-123 这种间接写法。</summary>
    public string? RawName { get; set; }

    public EntryKind Kind { get; set; }

    /// <summary>出现在什么位置，例如 “所有文件”“文件夹空白处”。</summary>
    public string ScopeLabel { get; set; } = "";

    /// <summary>注册表里的作用域键名，例如 “*”“Directory\Background”。</summary>
    public string ScopeKey { get; set; } = "";

    public RegistryHive Hive { get; set; }
    public RegistryView View { get; set; }

    /// <summary>逻辑子键路径，例如 Software\Classes\*\shell\某某。</summary>
    public string SubKeyPath { get; set; } = "";

    /// <summary>完整的物理注册表路径，可直接写进 .reg 备份文件。</summary>
    public string PhysicalPath { get; set; } = "";

    /// <summary>这一项实际执行的命令。</summary>
    public string? Command { get; set; }

    /// <summary>从命令里提取出来的可执行文件路径（用于判断失效）。</summary>
    public string? ExePath { get; set; }

    /// <summary>图标来源。</summary>
    public string? IconRef { get; set; }

    /// <summary>COM 处理器的类标识。</summary>
    public string? Clsid { get; set; }

    /// <summary>当前是否已被停用（LegacyDisable 或进了屏蔽名单）。</summary>
    public bool IsDisabled { get; set; }

    /// <summary>停用方式说明。</summary>
    public string? DisabledHow { get; set; }

    /// <summary>指向的程序已经不存在（残留项）。</summary>
    public bool IsOrphan { get; set; }

    /// <summary>看起来是 Windows 自带的东西，不建议删。</summary>
    public bool IsSystem { get; set; }

    /// <summary>对“发送到”里的文件项，记录磁盘路径。</summary>
    public string? FilePath { get; set; }

    /// <summary>只在这个条件下才出现的项（Shift 右键、仅程序调用）。</summary>
    public string? ConditionalNote { get; set; }

    /// <summary>
    /// 属于“仅某种文件类型”的专属菜单项。这类项数量极大（每一种扩展名都可能注册一遍），
    /// 而且绝大多数是系统自带的，界面上默认不显示。
    /// </summary>
    public bool IsFileTypeSpecific { get; set; }

    /// <summary>
    /// 这一项会在什么场景下冒出来。
    ///
    /// 直接用注册表键名（比如 Directory\Background）用户看不懂，这里翻译成人话：
    /// 「在文件夹空白处点右键时会出现」。筛选就是按这个维度做的。
    /// </summary>
    public string PlacementKey => ScopeKey switch
    {
        "*" => "file",
        "AllFilesystemObjects" => "fs",
        "Directory" or "Folder" or "LibraryFolder" => "folder",
        "Directory\\Background" or "LibraryFolder\\background" => "folderbg",
        "DesktopBackground" => "desktop",
        "Drive" => "drive",
        "SendTo" => "sendto",
        "lnkfile" => "shortcut",
        "exefile" => "program",
        "PackagedCom" => "modern",
        _ => IsFileTypeSpecific ? "filetype" : "other",
    };

    public string PlacementLabel => PlacementKey switch
    {
        "file" => "右键文件时",
        "fs" => "右键文件和文件夹时",
        "folder" => "右键文件夹时",
        "folderbg" => "右键文件夹空白处",
        "desktop" => "右键桌面空白处",
        "drive" => "右键驱动器时",
        "sendto" => "「发送到」菜单",
        "shortcut" => "右键快捷方式时",
        "program" => "右键程序时",
        "modern" => "Win11 现代菜单",
        "filetype" => "仅某种文件类型",
        _ => "其他位置",
    };

    /// <summary>
    /// 同一条菜单项常常在几十个文件类型下各注册一份 —— 比如 Bandizip 的「打开」，
    /// 在它认识的每种压缩包扩展名下都有一份。展开显示会变成几百行一模一样的东西，
    /// 所以扫描结束后会把它们合并成一条，多出来的放在这里。
    /// 删除／停用时会一并作用到所有成员，不会漏掉。
    /// </summary>
    public List<MenuEntry> Members { get; } = new();

    /// <summary>合并之后这一条代表了几处注册。</summary>
    public int GroupCount => 1 + Members.Count;

    /// <summary>展开成本条实际要操作的所有项。</summary>
    public IEnumerable<MenuEntry> Expand()
    {
        yield return this;
        foreach (MenuEntry member in Members) yield return member;
    }

    /// <summary>列表里显示的名字。重复的会标出「共 N 处」。</summary>
    public string ListTitle => GroupCount > 1
        ? $"{DisplayName}（共 {GroupCount} 处）"
        : DisplayName;

    /// <summary>「谁装的」这个维度上的人话标签。</summary>
    public string OriginLabel => IsSystem ? "Windows 自带" : "第三方软件";

    /// <summary>
    /// 这一项背后是哪个程序（显示成文件名）。
    /// 用途：拿它跟右键菜单里看到的项对号 —— 比如「压缩为 Eplan.zip」背后是 Bandizip.exe。
    /// </summary>
    public string ProviderLabel
    {
        get
        {
            // 优先用 ExePath（已经解析好的程序路径）。
            // 绝不能直接拿 Command 原文去取文件名：有些项的命令是「"%1" %*」这种
            // 把第一个参数当程序用的写法，直接取文件名会显示出「%1" %*」这种垃圾。
            string? source = ExePath;
            if (string.IsNullOrWhiteSpace(source)) source = Scanner.ExtractExecutablePath(Command);
            if (string.IsNullOrWhiteSpace(source)) return "未知";

            try
            {
                string name = Path.GetFileName(source.Trim().Trim('"'));
                if (string.IsNullOrWhiteSpace(name)) return "未知";
                return name.Contains('%') ? "未知" : name;
            }
            catch
            {
                return "未知";
            }
        }
    }

    /// <summary>列表里显示的程序图标在图标库中的序号；-1 表示没取到图标。</summary>
    public int IconIndex { get; set; } = -1;

    /// <summary>附加提醒，逐条显示在详情里。</summary>
    public List<string> Warnings { get; } = new();

    public string HiveLabel => Hive == RegistryHive.CurrentUser ? "当前用户" : "系统（本机）";

    public string ViewLabel => View == RegistryView.Registry32 ? "32 位" : "64 位";

    public string KindLabel => Kind switch
    {
        EntryKind.ShellVerb => "菜单项",
        EntryKind.ShellExtension => "扩展程序",
        EntryKind.SendTo => "发送到",
        _ => "其他",
    };

    /// <summary>表格里“状态”一列的取值。</summary>
    public string StatusLabel
    {
        get
        {
            if (IsDisabled) return "已停用";
            if (IsOrphan) return "已失效";
            if (IsSystem) return "系统自带";
            return "正常";
        }
    }

    public string SourceLabel => Kind == EntryKind.SendTo ? "发送到文件夹" : $"{HiveLabel} · {ViewLabel}";

    /// <summary>一键备份/删除真正要操作的注册表键。</summary>
    public string TargetKeyPath => PhysicalPath;
}

/// <summary>一次扫描的完整结果。</summary>
internal sealed class ScanResult
{
    public List<MenuEntry> Entries { get; } = new();
    public List<string> Notes { get; } = new();
    public DateTime ScannedAt { get; } = DateTime.Now;
    public bool IsAdministrator { get; set; }
}
