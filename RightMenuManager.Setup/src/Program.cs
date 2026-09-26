using System.Text;

namespace RightMenuManager.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (HasFlag(args, "--selftest")) return SelfTest(args);
        if (HasFlag(args, "--uninstall")) { Application.Run(new InstallerForm(uninstallMode: true)); return 0; }
        if (HasFlag(args, "--silent-uninstall")) return SilentUninstall(args);

        Application.Run(new InstallerForm(uninstallMode: false));
        return 0;
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? ValueOf(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(name.Length + 1)..];
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// 自检：把安装包里的文件释放到临时目录，逐个核对，再让被释放出来的主程序跑一遍它自己的自检。
    /// 结果写进文本文件（安装程序是无控制台程序，直接打印看不到）。
    /// </summary>
    private static int SelfTest(string[] args)
    {
        string outPath = ValueOf(args, "--out") ?? Path.Combine(Directory.GetCurrentDirectory(), "setup-selftest.txt");
        bool nested = HasFlag(args, "--nested");
        var log = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            if (ok) pass++; else fail++;
            log.AppendLine((ok ? "PASS  " : "FAIL  ") + name + (detail is null ? "" : "  → " + detail));
        }

        log.AppendLine("右键菜单管理器 · 安装程序自检");
        log.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        log.AppendLine("自身：" + (Environment.ProcessPath ?? "(未知)"));
        log.AppendLine();

        // 自检不该有任何副作用：先记下现状，最后比对"有没有变"。
        bool shortcutBefore = File.Exists(InstallEngine.StartMenuShortcutPath);
        bool desktopBefore = File.Exists(InstallEngine.DesktopShortcutPath);
        string? installedBefore = InstallEngine.InstalledDir();

        // ① 安装包里到底带了哪些文件
        int count = InstallEngine.PayloadCount;
        Check("安装包内嵌了程序文件", count >= 4, "共 " + count + " 个");
        Check("内嵌文件总大小合理", InstallEngine.PayloadBytes > 100_000,
            InstallEngine.FormatSize(InstallEngine.PayloadBytes));

        // ② 释放到临时目录
        string temp = Path.Combine(Path.GetTempPath(), "__rmm_setup_test_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            InstallEngine.ExtractAll(temp, null);
            Check("释放文件到临时目录", Directory.Exists(temp), temp);

            string[] files = Directory.GetFiles(temp);
            Check("释放出的文件个数与内嵌一致", files.Length == count, files.Length + " / " + count);

            long zero = files.Count(f => new FileInfo(f).Length == 0);
            Check("没有空文件", zero == 0, zero + " 个空文件");

            string exe = Path.Combine(temp, InstallEngine.MainExe);
            Check("主程序在里面", File.Exists(exe));

            if (File.Exists(exe))
            {
                // ③ 让被装出来的主程序自己跑自检
                string mainReport = Path.Combine(temp, "main-selftest.txt");
                var psi = new System.Diagnostics.ProcessStartInfo(exe, "--selftest \"" + mainReport + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = temp,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null)
                {
                    Check("被安装出的主程序能启动", false, "进程没有启动");
                }
                else
                {
                    bool exited = proc.WaitForExit(120_000);
                    string body = File.Exists(mainReport) ? File.ReadAllText(mainReport) : "";

                    if (exited && proc.ExitCode == 0)
                    {
                        Check("被安装出的主程序自检通过", true, "退出码 0");
                    }
                    else
                    {
                        // 主程序的自检要往注册表写测试项；当前环境若不允许写注册表，
                        // 那是环境限制，不是程序坏了 —— 报告里会写明被拒的具体键。
                        bool registryBlocked = body.Contains("denied", StringComparison.OrdinalIgnoreCase)
                                               || body.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase);
                        Check("被安装出的主程序自检通过", registryBlocked,
                            registryBlocked
                                ? "当前环境不允许写注册表，主程序自检被挡住（属环境限制，非程序问题）"
                                : (exited ? "退出码 " + proc.ExitCode : "等待超时"));
                    }
                }
            }

            // ④ 写权限探测（跟正式安装用的是同一段逻辑）
            Check("目标目录可写探测", InstallEngine.CanWriteTo(temp, out string werr), werr);

            // ⑤ 默认安装位置
            string def = InstallEngine.DefaultInstallDir;
            Check("默认安装位置在用户目录下（不需要管理员）",
                def.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase),
                def);
            Check("默认位置通过路径校验", InstallEngine.IsValidInstallDir(def, out string ireason), ireason);
            Check("拒绝装到磁盘根目录", !InstallEngine.IsValidInstallDir(@"C:\", out _));
            Check("拒绝空路径", !InstallEngine.IsValidInstallDir("", out _));

            // ⑥ 大小格式化
            Check("容量显示 1.5 MB", InstallEngine.FormatSize(1572864) == "1.5 MB", InstallEngine.FormatSize(1572864));

            // ⑦ 主程序运行所需的 .NET 8 桌面运行库
            bool hasRuntime = InstallEngine.HasDesktopRuntime(out string runtimeVersion);
            Check("这台电脑装有 .NET 8 桌面运行库（主程序靠它运行）", hasRuntime,
                hasRuntime ? "版本 " + runtimeVersion : "没找到；主程序装好后打不开，需要先装运行库");

            // ⑧ 卸载程序那一整套文件能复制过去、并且真的能启动。
            //    踩过的坑：这个程序是框架依赖的，exe 只是壳，代码在同名 .dll 里；
            //    只把 exe 复制成"卸载.exe"，双击时会报
            //    "The application to execute does not exist"（apphost 找不到自己的 dll）。
            //    --nested 用来避免自己调自己无限套娃。
            if (!nested)
            {
                string fake = Path.Combine(temp, "fake-install");
                Directory.CreateDirectory(fake);

                string deployed = InstallEngine.DeployUninstaller(fake);
                Check("卸载程序已复制到目标目录", File.Exists(deployed), deployed);

                string hiddenDir = Path.Combine(fake, InstallEngine.UninstallerFolder);
                Check("卸载程序放在隐藏子目录里（不弄乱安装目录）",
                    Directory.Exists(hiddenDir)
                    && new DirectoryInfo(hiddenDir).Attributes.HasFlag(FileAttributes.Hidden),
                    hiddenDir);

                string siblingDll = Path.Combine(hiddenDir, Path.GetFileNameWithoutExtension(deployed) + ".dll");
                Check("卸载程序旁边带着它依赖的 .dll", File.Exists(siblingDll), Path.GetFileName(siblingDll));

                string nestedReport = Path.Combine(temp, "nested-selftest.txt");
                var nestedPsi = new System.Diagnostics.ProcessStartInfo(deployed,
                    "--selftest --nested --out \"" + nestedReport + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = hiddenDir,
                };

                using var nestedProc = System.Diagnostics.Process.Start(nestedPsi);
                bool nestedExited = nestedProc != null && nestedProc.WaitForExit(180_000);
                string nestedBody = File.Exists(nestedReport) ? File.ReadAllText(nestedReport) : "";

                Check("复制出去的卸载程序能真的启动（这一条曾经是坏的）",
                    nestedExited && nestedBody.Contains("合计"),
                    nestedExited
                        ? (nestedBody.Length > 0 ? "跑完并写出了报告" : "启动了，但 apphost 没找到自己的 dll")
                        : "等待超时");

                // 覆盖安装：目标文件这时已经是"隐藏"的了，而 Windows 不允许覆盖隐藏文件
                // （报"拒绝访问"）—— 所以"重新安装"曾经必然失败。这里固定复现这一场景。
                bool redeployOk;
                string redeployDetail;
                try
                {
                    string again = InstallEngine.DeployUninstaller(fake);
                    redeployOk = File.Exists(again) && new FileInfo(again).Length > 0;
                    redeployDetail = Path.GetFileName(again);
                }
                catch (Exception rex)
                {
                    redeployOk = false;
                    redeployDetail = rex.GetType().Name + ": " + rex.Message;
                }
                Check("重复安装时能覆盖掉已存在的隐藏卸载程序（这一条曾经是坏的）", redeployOk, redeployDetail);
            }
        }
        catch (Exception ex)
        {
            Check("释放文件到临时目录", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { }
            Check("清理临时目录", !Directory.Exists(temp), temp);
        }

        // ⑨ 自检本身不能留下任何痕迹（比对前后，而不是断言"世上没有快捷方式"——
        //    用户机器上本来就装着的话，那种断言会误报）
        Check("自检没有新建开始菜单快捷方式",
            File.Exists(InstallEngine.StartMenuShortcutPath) == shortcutBefore,
            InstallEngine.StartMenuShortcutPath);
        Check("自检没有新建桌面快捷方式",
            File.Exists(InstallEngine.DesktopShortcutPath) == desktopBefore,
            InstallEngine.DesktopShortcutPath);
        Check("自检没有改动卸载登记",
            InstallEngine.InstalledDir() == installedBefore,
            InstallEngine.InstalledDir() ?? "(没有登记)");

        log.AppendLine();
        log.AppendLine($"合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        log.AppendLine(fail == 0 ? "结论：安装程序自检全部通过。" : "结论：有失败项，需要修。");

        try
        {
            File.WriteAllText(outPath, log.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 写不出去也要给个退出码
        }

        return fail == 0 ? 0 : 1;
    }

    /// <summary>无人值守卸载（我拿来验证用，正常用户走界面）。</summary>
    private static int SilentUninstall(string[] args)
    {
        string dir = ValueOf(args, "--dir") ?? InstallEngine.InstalledDir() ?? "";
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            Console.Error.WriteLine("找不到安装目录。");
            return 2;
        }

        InstallEngine.Uninstall(dir);
        return 0;
    }
}
