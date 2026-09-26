using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace RightMenuManager;


/// <summary>
/// 对 Windows 系统接口的直接调用（.NET 没有包装的那几个）。
/// </summary>
internal static class Native
{
    // ---------------------------------------------------------------- 名称解析

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(
        string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    /// <summary>
    /// 把注册表里的间接字符串（形如 @shell32.dll,-8506）翻译成人能读的文字。
    /// 翻译不出来时返回 null，调用方负责回退显示原文。
    /// </summary>
    public static string? ResolveIndirectString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();

        if (!raw.StartsWith('@'))
        {
            return raw; // 本来就是普通文字
        }

        string? direct = TryLoad(raw);
        if (direct != null) return direct;

        // 退一步：把相对的文件名补成完整路径再试一次
        string body = raw[1..];
        int comma = body.LastIndexOf(',');
        if (comma > 0)
        {
            string file = body[..comma];
            string rest = body[comma..];
            string expanded = Environment.ExpandEnvironmentVariables(file);
            if (!Path.IsPathRooted(expanded))
            {
                string inSystem = Path.Combine(Environment.SystemDirectory, expanded);
                if (File.Exists(inSystem)) expanded = inSystem;
                else
                {
                    string inWindows = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows), expanded);
                    if (File.Exists(inWindows)) expanded = inWindows;
                }
            }
            string? retried = TryLoad("@" + expanded + rest);
            if (retried != null) return retried;
        }

        return null;
    }

    private static string? TryLoad(string source)
    {
        try
        {
            var sb = new StringBuilder(2048);
            int hr = SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero);
            if (hr == 0 && sb.Length > 0)
            {
                string text = sb.ToString();
                if (!text.StartsWith('@') && text.Trim().Length > 0) return text.Trim();
            }
        }
        catch
        {
            // 忽略：调用方会回退
        }
        return null;
    }

    // ---------------------------------------------------------------- 图标提取

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconExW(
        string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 从 exe/dll/ico 里取出第 index 个图标。取不到返回 null。
    /// </summary>
    public static Icon? ExtractIcon(string? file, int index)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        string path = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
        if (!Path.IsPathRooted(path) || !File.Exists(path)) return null;

        try
        {
            var handles = new IntPtr[1];
            uint got = ExtractIconExW(path, index, null, handles, 1);
            if (got == 0 || handles[0] == IntPtr.Zero) return null;
            try
            {
                return (Icon)Icon.FromHandle(handles[0]).Clone();
            }
            finally
            {
                DestroyIcon(handles[0]);
            }
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 控制台

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    /// <summary>
    /// 命令行自测模式下，尝试把输出挂到父窗口的控制台上。
    /// 不新开控制台窗口（那会莫名其妙弹一个黑框出来），失败就算了，结果反正会写进文件。
    /// </summary>
    public static bool TryAttachParentConsole()
    {
        try { return AttachConsole(0xFFFFFFFF); }
        catch { return false; }
    }
}


/// <summary>注册表路径与读写的小工具，统一处理 32/64 位视图。</summary>
internal static class Reg
{
    public static readonly RegistryHive[] Hives = { RegistryHive.CurrentUser, RegistryHive.LocalMachine };

    public static readonly RegistryView[] Views = { RegistryView.Registry64, RegistryView.Registry32 };

    public static RegistryKey? Base(RegistryHive hive, RegistryView view)
    {
        try { return RegistryKey.OpenBaseKey(hive, view); }
        catch { return null; }
    }

    /// <summary>打开子键，打不开就返回 null（不抛异常）。</summary>
    public static RegistryKey? Open(RegistryHive hive, RegistryView view, string subKey, bool writable = false)
    {
        RegistryKey? b = null;
        try
        {
            b = Base(hive, view);
            return b?.OpenSubKey(subKey, writable);
        }
        catch
        {
            return null;
        }
        finally
        {
            b?.Dispose();
        }
    }

    public static string[] SubKeyNames(RegistryKey? key)
    {
        if (key == null) return Array.Empty<string>();
        try { return key.GetSubKeyNames(); }
        catch { return Array.Empty<string>(); }
    }

    public static string? GetString(RegistryKey? key, string? name)
    {
        if (key == null) return null;
        try
        {
            object? v = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return v as string;
        }
        catch { return null; }
    }

    public static bool ValueExists(RegistryKey? key, string? name)
    {
        if (key == null) return false;
        try { return key.GetValue(name, null) != null; }
        catch { return false; }
    }

    /// <summary>
    /// 把“逻辑子键 + 视图”翻译成完整的物理注册表路径。
    /// 只有 HKEY_LOCAL_MACHINE 的 Software 分支会被 Windows 重定向到 WOW6432Node；
    /// HKEY_CURRENT_USER 下的 Software\Classes 不重定向，两个视图读到的是同一份数据，
    /// 所以这里绝不能对当前用户也加 WOW6432Node，否则备份文件会指向一个不存在的位置。
    /// </summary>
    public static string ToPhysicalPath(RegistryHive hive, RegistryView view, string subKey)
    {
        string head = hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
        string sub = subKey.TrimStart('\\');

        if (view == RegistryView.Registry32 &&
            hive == RegistryHive.LocalMachine &&
            !sub.StartsWith("WOW6432Node", StringComparison.OrdinalIgnoreCase))
        {
            const string classes = "Software\\Classes\\";
            if (sub.StartsWith(classes, StringComparison.OrdinalIgnoreCase))
            {
                sub = classes + "WOW6432Node\\" + sub[classes.Length..];
            }
            else if (sub.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase))
            {
                sub = "Software\\WOW6432Node\\" + sub["Software\\".Length..];
            }
        }

        return head + "\\" + sub;
    }

    /// <summary>某个 hive 在“所有文件”这类作用域下的根键。</summary>
    public static string ClassesRoot(string scope) => "Software\\Classes\\" + scope;

    public static bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
