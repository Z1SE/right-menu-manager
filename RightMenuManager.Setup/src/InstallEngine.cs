using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace RightMenuManager.Setup;

/// <summary>
/// 安装／卸载的实际动作。界面只管问，干活都在这里。
/// </summary>
internal static class InstallEngine
{
    public const string AppName = "右键菜单管理器";
    public const string MainExe = "右键菜单管理器.exe";
    public const string UninstallerFolder = ".uninstall";
    public const string Version = "1.0.0";

    /// <summary>默认装到用户自己的目录下，这样不需要管理员权限。</summary>
    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", AppName);

    // ------------------------------------------------------------------ 读嵌入文件

    private static List<(string Resource, string FileName)> Payload()
    {
        const string marker = ".payload.";
        var list = new List<(string, string)>();

        foreach (string res in typeof(InstallEngine).Assembly.GetManifestResourceNames())
        {
            int at = res.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            list.Add((res, res[(at + marker.Length)..]));
        }
        return list;
    }

    /// <summary>这次安装总共要释放几个文件（界面上用来算进度）。</summary>
    public static int PayloadCount => Payload().Count;

    // ------------------------------------------------------------------ 安装

    /// <summary>
    /// 把程序文件释放到目标目录。onStep 回调用来更新进度和当前文件名。
    /// 任何一个文件写失败都会抛出来，由界面显示给用户。
    /// </summary>
    public static void ExtractAll(string targetDir, Action<int, int, string>? onStep)
    {
        Directory.CreateDirectory(targetDir);

        List<(string Resource, string FileName)> files = Payload();
        for (int i = 0; i < files.Count; i++)
        {
            (string resource, string fileName) = files[i];
            onStep?.Invoke(i, files.Count, fileName);

            using Stream? source = typeof(InstallEngine).Assembly.GetManifestResourceStream(resource);
            if (source == null) throw new InvalidOperationException("安装包内部数据损坏，缺少：" + fileName);

            string full = Path.Combine(targetDir, fileName);
            using FileStream target = File.Create(full);
            source.CopyTo(target);
        }

        onStep?.Invoke(files.Count, files.Count, "");
    }

    public static void CreateShortcut(string lnkPath, string targetPath, string workingDir, string description)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;

        object? shell = Activator.CreateInstance(shellType);
        if (shell == null) return;

        try
        {
            object? shortcut = shellType.InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (shortcut == null) return;

            Type st = shortcut.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath + ",0" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shell is IDisposable disposable) disposable.Dispose();
            else System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    public static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    public static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>
    /// 在「应用和功能」里登记一条卸载信息。
    /// 写 HKCU 而不是 HKLM：这样不需要管理员权限，也不需要 UAC。
    /// </summary>
    public static void RegisterUninstall(string installDir, string uninstallerPath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName, true)
            ?? throw new InvalidOperationException("无法写入卸载登记信息。");

        key.SetValue("DisplayName", AppName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", Version, RegistryValueKind.String);
        key.SetValue("Publisher", "本地工具", RegistryValueKind.String);
        key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
        key.SetValue("DisplayIcon", Path.Combine(installDir, MainExe), RegistryValueKind.String);
        key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// 把安装程序自己复制一份到目标目录，留作卸载程序。
    ///
    /// 注意：这个程序是"框架依赖"的，exe 只是个壳，真正的代码在同名的 .dll 里。
    /// 所以只复制 exe 是不够的 —— 少了大写一份 .dll，双击时会报
    /// "The application to execute does not exist"。必须把自己这一整套文件都带过去。
    /// 放在一个隐藏子目录里，免得安装目录看起来乱。
    /// </summary>
    public static string DeployUninstaller(string installDir)
    {
        string? self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self)) throw new InvalidOperationException("找不到安装程序自身的位置。");

        string sourceDir = Path.GetDirectoryName(self)
                           ?? throw new InvalidOperationException("找不到安装程序所在的目录。");

        string targetDir = Path.Combine(installDir, UninstallerFolder);
        Directory.CreateDirectory(targetDir);

        string targetExe = Path.Combine(targetDir, Path.GetFileName(self));
        if (!string.Equals(self, targetExe, StringComparison.OrdinalIgnoreCase))
        {
            string stem = Path.GetFileNameWithoutExtension(self);
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string name = Path.GetFileName(file);
                // 自己这套文件：exe 本体 + 同名的 .dll/.deps.json/.runtimeconfig.json
                if (!name.Equals(Path.GetFileName(self), StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                    continue;

                string destination = Path.Combine(targetDir, name);

                // Windows 不允许覆盖一个带"隐藏"属性的文件，会直接报"拒绝访问"。
                // 上次装进去的卸载程序就是隐藏的，所以"重新安装"必然踩到这里：
                // 先把属性还原、删掉，最后才复制。
                if (File.Exists(destination))
                {
                    try { File.SetAttributes(destination, FileAttributes.Normal); } catch { }
                    try { File.Delete(destination); } catch { }
                }

                File.Copy(file, destination, overwrite: true);
            }

            if (!File.Exists(targetExe))
                throw new InvalidOperationException("卸载程序没有复制成功：" + targetExe);
        }

        MarkHidden(targetDir);
        return targetExe;
    }

    private static void MarkHidden(string dir)
    {
        try
        {
            new DirectoryInfo(dir).Attributes |= FileAttributes.Hidden;
            foreach (string file in Directory.GetFiles(dir))
                new FileInfo(file).Attributes |= FileAttributes.Hidden;
        }
        catch { }
    }

    // ------------------------------------------------------------------ 卸载

    /// <summary>已安装的目录；没装过返回 null。</summary>
    public static string? InstalledDir()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName);
        string? dir = key?.GetValue("InstallLocation") as string;
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    public static void Uninstall(string installDir)
    {
        // ① 删快捷方式
        foreach (string lnk in new[] { StartMenuShortcutPath, DesktopShortcutPath })
        {
            try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
        }

        // ② 删注册表里的卸载登记
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName, throwOnMissingSubKey: false);
        }
        catch { }

        // ③ 删程序文件（自己删不掉自己，留给最后一步）
        string self = Environment.ProcessPath ?? "";
        try
        {
            if (Directory.Exists(installDir))
            {
                foreach (string file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }

        // ④ 收尾：等本进程退出后，把剩下的目录连同卸载程序一起删掉。
        //    cmd 的工作目录必须换到别处：卸载程序自己的目录是 <安装目录>\.uninstall，
        //    如果 cmd 也以那里为工作目录，那个目录就永远删不掉（系统不允许删正在被当作
        //    工作目录的文件夹），会在磁盘上留一个空壳。
        string scratch = Path.GetTempPath().TrimEnd('\\');
        string script = $"cd /d \"{scratch}\" > nul & ping 127.0.0.1 -n 3 > nul & rd /s /q \"{installDir}\"";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + script)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = scratch,
            });
        }
        catch { }
    }

    // ------------------------------------------------------------------ 小工具

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => bytes + " B",
    };

    /// <summary>安装包自己有多大（用来在界面上显示"需要多少空间"）。</summary>
    public static long PayloadBytes
    {
        get
        {
            long total = 0;
            foreach ((string resource, _) in Payload())
            {
                using Stream? s = typeof(InstallEngine).Assembly.GetManifestResourceStream(resource);
                if (s != null) total += s.Length;
            }
            return total;
        }
    }

    public static string DescribeDrive(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return "";
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return "";
            return $"该磁盘可用空间 {FormatSize(drive.AvailableFreeSpace)}";
        }
        catch
        {
            return "";
        }
    }

    public static bool IsValidInstallDir(string dir, out string reason)    {
        reason = "";
        if (string.IsNullOrWhiteSpace(dir)) { reason = "安装位置不能为空。"; return false; }

        try
        {
            string full = Path.GetFullPath(dir);
            string? root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                reason = "不能直接装到磁盘根目录，请选一个文件夹。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = "这个路径不能用：" + ex.Message;
            return false;
        }
    }

    /// <summary>试着往目标目录写一个文件，提前发现"没权限"这种情况。</summary>
    public static bool CanWriteTo(string dir, out string error)
    {
        error = "";
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, "__setup_write_test.tmp");
            File.WriteAllBytes(probe, new byte[] { 0 });
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ 运行库检查

    public const string RuntimeDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/8.0";

    /// <summary>
    /// 主程序是"框架依赖"的：它自己不带 .NET，要靠电脑上装好的 .NET 8 桌面运行库。
    /// 这里先看看那份运行库在不在，不在就提前告诉用户，别等装完打不开。
    /// </summary>
    public static bool HasDesktopRuntime(out string version)
    {
        version = "";

        foreach (string root in RuntimeRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    // 目录名形如 8.0.15；只要主版本够 8 就行
                    int dot = name.IndexOf('.');
                    string major = dot < 0 ? name : name[..dot];
                    if (int.TryParse(major, out int m) && m >= 8)
                    {
                        version = name;
                        return true;
                    }
                }
            }
            catch { }
        }
        return false;
    }

    private static IEnumerable<string> RuntimeRoots()
    {
        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
            yield return Path.Combine(dotnetRoot, "shared", "Microsoft.WindowsDesktop.App");

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
            yield return Path.Combine(programFiles, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
    }
}
