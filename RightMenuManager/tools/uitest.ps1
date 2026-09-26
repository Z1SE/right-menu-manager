# UI 验证工具（合并了原来 6 个一次性脚本）。
# 用法：
#   powershell -File uitest.ps1 -Action shot    截一张当前界面
#   powershell -File uitest.ps1 -Action theme   浅色/深色各截一张，并验证按钮文字翻转
#   powershell -File uitest.ps1 -Action scroll  验证列表重建后不跳回顶端
#   powershell -File uitest.ps1 -Action bulk    验证「批量选择」真的打开了勾选框
#
# ASCII only on purpose (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).

param(
    [ValidateSet('shot', 'theme', 'scroll', 'bulk', 'search')]
    [string]$Action = 'shot',
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class UiProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr c, string cls, string win);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageStr(IntPtr h, uint msg, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    static string ClassOf(IntPtr h) { var sb = new StringBuilder(512); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
    static string TextOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }

    public static IntPtr FindByClass(IntPtr root, string part)
    {
        IntPtr c = IntPtr.Zero;
        while ((c = FindWindowEx(root, c, null, null)) != IntPtr.Zero)
        {
            if (ClassOf(c).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return c;
            IntPtr deep = FindByClass(c, part);
            if (deep != IntPtr.Zero) return deep;
        }
        return IntPtr.Zero;
    }

    public static IntPtr FindButton(IntPtr root, string text)
    {
        IntPtr c = IntPtr.Zero;
        while ((c = FindWindowEx(root, c, null, null)) != IntPtr.Zero)
        {
            if (ClassOf(c).IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) >= 0 && TextOf(c).Contains(text)) return c;
            IntPtr deep = FindButton(c, text);
            if (deep != IntPtr.Zero) return deep;
        }
        return IntPtr.Zero;
    }

    public static void ClickCenter(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) return;
        SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        System.Threading.Thread.Sleep(200);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    public static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(180);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
}
'@ -ErrorAction Stop

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$null = [UiProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))

$projectRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $projectRoot
if (-not $Out) { $Out = $workspaceRoot }
$binDir = Join-Path $projectRoot "bin\Release\net8.0-windows"
$exe = (Get-ChildItem -LiteralPath $binDir -Filter "*.exe" | Select-Object -First 1).FullName

function Start-App {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = Split-Path $exe
    $p = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Seconds 12
    if ($p.HasExited) { throw "app exited with $($p.ExitCode)" }
    $p.Refresh()
    $null = [UiProbe]::SetForegroundWindow($p.MainWindowHandle)
    Start-Sleep -Seconds 2
    return $p
}

function Stop-App($p) {
    $null = $p.CloseMainWindow()
    Start-Sleep -Seconds 3
    if (-not $p.HasExited) { $p.Kill(); Start-Sleep -Seconds 2 }
}

function Save-Shot([string]$name, [int]$offsetY = 0, [int]$height = 0) {
    $w = [UiProbe]::GetSystemMetrics(0)
    $h = [UiProbe]::GetSystemMetrics(1)
    $bmp = [System.Drawing.Bitmap]::new($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, [System.Drawing.Size]::new($w, $h))

    if ($height -gt 0) {
        $rect = [System.Drawing.Rectangle]::new(395, 215 + $offsetY, 1770, $height)
        $crop = [System.Drawing.Bitmap]::new($rect.Width, $rect.Height * 2)
        $g2 = [System.Drawing.Graphics]::FromImage($crop)
        $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g2.DrawImage($bmp, [System.Drawing.Rectangle]::new(0, 0, $crop.Width, $crop.Height), $rect, [System.Drawing.GraphicsUnit]::Pixel)
        $g2.Dispose()
        $crop.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png)
        $crop.Dispose()
    }
    else {
        $bmp.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    }

    $g.Dispose(); $bmp.Dispose()
    Write-Output "saved $name"
}

$DARK   = [string]([char]0x6DF1 + [char]0x8272 + [char]0x6A21 + [char]0x5F0F)   # 深色模式
$LIGHT  = [string]([char]0x6D45 + [char]0x8272 + [char]0x6A21 + [char]0x5F0F)   # 浅色模式
$BULK   = [string]([char]0x6279 + [char]0x91CF + [char]0x9009 + [char]0x62E9)   # 批量选择
$BULKOFF= [string]([char]0x9000 + [char]0x51FA + [char]0x6279 + [char]0x91CF + [char]0x9009 + [char]0x62E9)  # 退出批量选择
$RESCAN = [string]([char]0x91CD + [char]0x65B0 + [char]0x626B + [char]0x63CF)   # 重新扫描

$LVM_GETTOPINDEX              = 0x1027
$LVM_SCROLL                   = 0x1014
$LVM_GETEXTENDEDLISTVIEWSTYLE = 0x1037
$LVM_GETITEMCOUNT             = 0x1004
$WM_SETTEXT                   = 0x000C
$LVS_EX_CHECKBOXES            = 0x4

switch ($Action) {
    'shot' {
        $p = Start-App
        Save-Shot "ui-shot.png"
        Stop-App $p
    }

    'theme' {
        $p = Start-App
        Save-Shot "ui-light.png"
        $btn = [UiProbe]::FindButton($p.MainWindowHandle, $DARK)
        if ($btn -eq [IntPtr]::Zero) { Write-Output "FAIL: theme button not found"; Stop-App $p; exit 1 }
        [UiProbe]::ClickCenter($btn)
        Start-Sleep -Seconds 3
        Save-Shot "ui-dark.png"
        $back = [UiProbe]::FindButton($p.MainWindowHandle, $LIGHT)
        Write-Output "button relabelled to light: $($back -ne [IntPtr]::Zero)"
        Write-Output "app alive: $(-not $p.HasExited)"
        Stop-App $p
    }

    'scroll' {
        $p = Start-App
        $list = [UiProbe]::FindByClass($p.MainWindowHandle, "SysListView32")
        if ($list -eq [IntPtr]::Zero) { Write-Output "FAIL: list not found"; Stop-App $p; exit 1 }

        $topBefore = [UiProbe]::SendMessage($list, $LVM_GETTOPINDEX, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32()
        $null = [UiProbe]::SendMessage($list, $LVM_SCROLL, [IntPtr]::Zero, [IntPtr](900 -shl 16))
        Start-Sleep -Milliseconds 600
        $scrolled = [UiProbe]::SendMessage($list, $LVM_GETTOPINDEX, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32()

        $btn = [UiProbe]::FindButton($p.MainWindowHandle, $RESCAN)
        if ($btn -eq [IntPtr]::Zero) { Write-Output "FAIL: rescan button not found"; Stop-App $p; exit 1 }
        [UiProbe]::ClickCenter($btn)
        Start-Sleep -Seconds 10

        $after = [UiProbe]::SendMessage($list, $LVM_GETTOPINDEX, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32()
        Write-Output "top: before=$topBefore scrolled=$scrolled afterRebuild=$after"
        if ($scrolled -gt 5 -and [Math]::Abs($after - $scrolled) -le 3) {
            Write-Output "PASS: list stayed where the user had scrolled to"
        } else {
            Write-Output "FAIL: it jumped back (0 means top of list)"
        }
        Stop-App $p
    }

    'bulk' {
        $p = Start-App
        $list = [UiProbe]::FindByClass($p.MainWindowHandle, "SysListView32")

        function CheckboxesOn {
            $style = [UiProbe]::SendMessage($list, $LVM_GETEXTENDEDLISTVIEWSTYLE, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
            return (($style -band $LVS_EX_CHECKBOXES) -ne 0)
        }

        $before = CheckboxesOn
        $btn = [UiProbe]::FindButton($p.MainWindowHandle, $BULK)
        if ($btn -eq [IntPtr]::Zero) { Write-Output "FAIL: bulk button not found"; Stop-App $p; exit 1 }
        [UiProbe]::ClickCenter($btn)
        Start-Sleep -Seconds 3
        $after = CheckboxesOn
        $off = [UiProbe]::FindButton($p.MainWindowHandle, $BULKOFF)

        Write-Output "checkboxes before=$before after=$after relabelled=$($off -ne [IntPtr]::Zero)"
        if (-not $before -and $after -and $off -ne [IntPtr]::Zero) {
            Write-Output "PASS: bulk-select mode toggled on"
        } else {
            Write-Output "FAIL"
        }
        Stop-App $p
    }

    'search' {
        $p = Start-App
        $list = [UiProbe]::FindByClass($p.MainWindowHandle, "SysListView32")
        $edit = [UiProbe]::FindByClass($p.MainWindowHandle, "EDIT")
        if ($list -eq [IntPtr]::Zero -or $edit -eq [IntPtr]::Zero) {
            Write-Output "FAIL: list or search box not found"
            Stop-App $p
            exit 1
        }

        $all = [UiProbe]::SendMessage($list, $LVM_GETITEMCOUNT, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32()
        Write-Output "rows with empty search: $all"

        # typing into the box must narrow the list
        $null = [UiProbe]::SendMessageStr($edit, $WM_SETTEXT, [IntPtr]::Zero, "QQ")
        Start-Sleep -Seconds 2
        $filtered = [UiProbe]::SendMessage($list, $LVM_GETITEMCOUNT, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32()
        Write-Output "rows after typing QQ: $filtered"

        # clearing it must bring everything back
        $null = [UiProbe]::SendMessageStr($edit, $WM_SETTEXT, [IntPtr]::Zero, "")
        Start-Sleep -Seconds 2
        $restored = [UiProbe]::SendMessage($list, $LVM_GETITEMCOUNT, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32()
        Write-Output "rows after clearing: $restored"

        if ($filtered -gt 0 -and $filtered -lt $all -and $restored -eq $all) {
            Write-Output "PASS: search filters and restores"
        } else {
            Write-Output "FAIL: search did not behave as expected"
        }
        Stop-App $p
    }
}
