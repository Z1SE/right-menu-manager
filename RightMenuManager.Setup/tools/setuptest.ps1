# Setup wizard verification tool.
# Usage (Windows PowerShell 5.1):
#   powershell -File setuptest.ps1 -Action shot        only screenshot the welcome page
#   powershell -File setuptest.ps1 -Action install     full install run (writes registry, makes shortcuts)
#   powershell -File setuptest.ps1 -Action uninstall   full uninstall run through the GUI
#   powershell -File setuptest.ps1 -Action all         install then uninstall, with screenshots
#
# -Dir   where to install (default: workspace\__installtest)
# -Out   where to put screenshots (default: <project>\shots)
#
# ASCII only on purpose (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI,
# so any literal CJK text would be mangled -- build those from char codes).

param(
    [ValidateSet('shot', 'diag', 'install', 'uninstall', 'all')]
    [string]$Action = 'shot',
    [string]$Dir = '',
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

public static class SetupProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr c, string cls, string win);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] public static extern IntPtr SendMessageStr(IntPtr h, uint msg, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public delegate bool EnumProc(IntPtr h, IntPtr p);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(512); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
    public static string TextOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }

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

    public static List<string> Classes(IntPtr root)
    {
        var list = new List<string>();
        IntPtr c = IntPtr.Zero;
        while ((c = FindWindowEx(root, c, null, null)) != IntPtr.Zero)
            list.Add(ClassOf(c) + " :: " + TextOf(c));
        return list;
    }

    static bool IsButton(IntPtr h) { return ClassOf(h).IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) >= 0; }

    public static IntPtr FindButton(IntPtr root, string text)
    {
        IntPtr c = IntPtr.Zero;
        while ((c = FindWindowEx(root, c, null, null)) != IntPtr.Zero)
        {
            if (IsButton(c) && TextOf(c).Contains(text)) return c;
            IntPtr deep = FindButton(c, text);
            if (deep != IntPtr.Zero) return deep;
        }
        return IntPtr.Zero;
    }

    public static int CountButtons(IntPtr root)
    {
        int n = 0;
        IntPtr c = IntPtr.Zero;
        while ((c = FindWindowEx(root, c, null, null)) != IntPtr.Zero)
        {
            if (IsButton(c)) n++;
            n += CountButtons(c);
        }
        return n;
    }

    // full recursive control tree, for diagnosing "the button is not where I think it is"
    public static List<string> Tree(IntPtr root, int depth)
    {
        var list = new List<string>();
        IntPtr c = IntPtr.Zero;
        while ((c = FindWindowEx(root, c, null, null)) != IntPtr.Zero)
        {
            RECT r;
            GetWindowRect(c, out r);
            string vis = IsWindowVisible(c) ? "vis" : "HID";
            list.Add(new string(' ', depth * 2) + ClassOf(c) + " [" + vis + " " + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + "] :: " + TextOf(c));
            list.AddRange(Tree(c, depth + 1));
        }
        return list;
    }

    // top-level window of a process, matched by class and/or title
    public static IntPtr TopWindowOf(uint pid, string classPart, string titlePart)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner != pid) return true;
            if (!IsWindowVisible(h)) return true;
            if (classPart != null && ClassOf(h).IndexOf(classPart, StringComparison.OrdinalIgnoreCase) < 0) return true;
            if (titlePart != null && TextOf(h).IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
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

    // BM_CLICK: raises the control's Click exactly like a mouse click does, but it does not
    // depend on which window happens to be on top. A real mouse click here lands on whatever
    // covers our window (the DSH window sits on top), so the button never gets pressed.
    //
    // PostMessage, not SendMessage: the uninstall button opens a modal dialog, which blocks
    // the UI thread inside the click handler -- a synchronous SendMessage would block us too.
    public static void ClickMessage(IntPtr h)
    {
        PostMessage(h, 0x00F5, IntPtr.Zero, IntPtr.Zero);
    }

    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;

    // Screenshots use CopyFromScreen, which grabs whatever is actually on screen, so the
    // window has to be raised first or we photograph the window covering it.
    public static void Raise(IntPtr h)
    {
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW);
        SetForegroundWindow(h);
        System.Threading.Thread.Sleep(200);
    }

    public static void Unraise(IntPtr h)
    {
        SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE);
    }
}
'@ -ErrorAction Stop

Add-Type -AssemblyName System.Drawing
$null = [SetupProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))

# CJK text, built from char codes so this file can stay pure ASCII.
function CN([int[]]$codes) { -join ($codes | ForEach-Object { [char]$_ }) }

$APP_NAME   = CN 0x53F3, 0x952E, 0x83DC, 0x5355, 0x7BA1, 0x7406, 0x5668            # app name
$SETUP_NAME = (CN 0x5B89, 0x88C5, 0x7A0B, 0x5E8F) + '.exe'                         # setup exe suffix
$BTN_START  = CN 0x5F00, 0x59CB, 0x5B89, 0x88C5                                    # start install
$BTN_AGAIN  = CN 0x91CD, 0x65B0, 0x5B89, 0x88C5                                    # reinstall (shown when already installed)
$BTN_DONE   = CN 0x5B8C, 0x6210                                                    # done
$BTN_REMOVE = CN 0x5378, 0x8F7D                                                    # uninstall
$BTN_YES    = CN 0x662F                                                            # yes
$UNINST_KEY = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $APP_NAME

$projectRoot = Split-Path -Parent $PSScriptRoot
$binDir = Join-Path $projectRoot "bin\Release\net8.0-windows"
$setupExe = (Get-ChildItem -LiteralPath $binDir -File | Where-Object { $_.Name.EndsWith($SETUP_NAME) } | Select-Object -First 1).FullName
if (-not $setupExe) { throw "setup exe not found in $binDir" }
if (-not $Out) { $Out = Join-Path $projectRoot "shots" }
if (-not (Test-Path -LiteralPath $Out)) { New-Item -ItemType Directory -Path $Out | Out-Null }
if (-not $Dir) { $Dir = Join-Path (Split-Path -Parent $projectRoot) "__installtest" }

$WM_SETTEXT = 0x000C

function Start-Setup([string]$arguments = '') {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $setupExe
    $psi.Arguments = $arguments
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = Split-Path $setupExe
    $p = [System.Diagnostics.Process]::Start($psi)

    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        if ($p.HasExited) { throw "setup exited by itself, code $($p.ExitCode)" }
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { throw "no setup window appeared" }
    [SetupProbe]::Raise($p.MainWindowHandle)
    Start-Sleep -Milliseconds 900
    return $p
}

function Stop-Setup($p) {
    if ($null -eq $p) { return }
    try {
        if (-not $p.HasExited) { $null = $p.CloseMainWindow(); Start-Sleep -Milliseconds 1500 }
        if (-not $p.HasExited) { $p.Kill(); Start-Sleep -Milliseconds 600 }
    } catch { }
}

function Save-Window([IntPtr]$h, [string]$name) {
    $r = New-Object SetupProbe+RECT
    if (-not [SetupProbe]::GetWindowRect($h, [ref]$r)) { Write-Output "FAIL: cannot measure window"; return }
    [SetupProbe]::Raise($h)
    $w = $r.Right - $r.Left
    $ht = $r.Bottom - $r.Top
    $bmp = [System.Drawing.Bitmap]::new($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, [System.Drawing.Size]::new($w, $ht))
    $g.Dispose()
    $path = Join-Path $Out $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [SetupProbe]::Unraise($h)
    Write-Output "saved $name  ($w x $ht)"
}

function Wait-Button([IntPtr]$h, [string]$text, [int]$seconds = 20) {
    for ($i = 0; $i -lt ($seconds * 4); $i++) {
        if ($h -ne [IntPtr]::Zero) {
            $b = [SetupProbe]::FindButton($h, $text)
            if ($b -ne [IntPtr]::Zero) { return $b }
        }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}

# the primary button reads "start install" on a fresh machine and "reinstall" once
# an entry is already registered, so accept either.
function Wait-PrimaryButton([IntPtr]$h, [int]$seconds = 20) {
    for ($i = 0; $i -lt ($seconds * 4); $i++) {
        foreach ($t in @($BTN_START, $BTN_AGAIN)) {
            $b = [SetupProbe]::FindButton($h, $t)
            if ($b -ne [IntPtr]::Zero) { return $b }
        }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}

function Do-Install() {
    $p = Start-Setup
    $h = $p.MainWindowHandle
    Save-Window $h "setup-01-welcome.png"

    $edit = [SetupProbe]::FindByClass($h, "EDIT")
    if ($edit -eq [IntPtr]::Zero) { throw "install path box not found" }
    $null = [SetupProbe]::SendMessageStr($edit, $WM_SETTEXT, [IntPtr]::Zero, $Dir)
    Start-Sleep -Milliseconds 800
    Save-Window $h "setup-02-path.png"

    $btn = Wait-PrimaryButton $h 6
    if ($btn -eq [IntPtr]::Zero) { throw "start-install button not found" }
    [SetupProbe]::ClickMessage($btn)
    Start-Sleep -Milliseconds 400
    Save-Window $h "setup-03-progress.png"

    $done = Wait-Button $h $BTN_DONE 60
    if ($done -eq [IntPtr]::Zero) {
        Save-Window $h "setup-04-stuck.png"
        throw "install never reached the done page"
    }
    Start-Sleep -Milliseconds 600
    Save-Window $h "setup-04-done.png"

    $exe = Join-Path $Dir ($APP_NAME + '.exe')
    $hid = Join-Path $Dir '.uninstall'
    Write-Output "installed exe:        $(Test-Path -LiteralPath $exe)"
    Write-Output "installed file count: $((Get-ChildItem -LiteralPath $Dir -File).Count)"
    Write-Output "desktop shortcut:     $(Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Desktop')) ($APP_NAME + '.lnk')))"
    Write-Output "start menu shortcut:  $(Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Programs')) ($APP_NAME + '.lnk')))"
    Write-Output "uninstaller folder:   $(Test-Path -LiteralPath $hid)"
    if (Test-Path -LiteralPath $hid) {
        Write-Output "  hidden attr:        $((Get-Item -LiteralPath $hid -Force).Attributes)"
        Write-Output "  contents:           $((Get-ChildItem -LiteralPath $hid -Force | ForEach-Object { $_.Name }) -join ', ')"
    }
    Write-Output "registry entry:       $(Test-Path -LiteralPath $UNINST_KEY)"
    if (Test-Path -LiteralPath $UNINST_KEY) {
        $v = Get-ItemProperty -LiteralPath $UNINST_KEY
        Write-Output "  DisplayName     = $($v.DisplayName)"
        Write-Output "  DisplayVersion  = $($v.DisplayVersion)"
        Write-Output "  InstallLocation = $($v.InstallLocation)"
        Write-Output "  UninstallString = $($v.UninstallString)"
    }
    Stop-Setup $p
    return $null
}

function Do-Uninstall() {
    # run exactly the command Windows would run from "Apps & features"
    if (-not (Test-Path -LiteralPath $UNINST_KEY)) { throw "no registry entry to uninstall from" }
    $uninstallString = [string](Get-ItemProperty -LiteralPath $UNINST_KEY).UninstallString
    $m = [regex]::Match($uninstallString, '^"([^"]+)"\s*(.*)$')
    if (-not $m.Success) { throw "unexpected UninstallString: $uninstallString" }
    $uninstaller = $m.Groups[1].Value
    $arguments = $m.Groups[2].Value
    Write-Output "will run: $uninstaller $arguments"
    if (-not (Test-Path -LiteralPath $uninstaller)) { throw "uninstaller not found: $uninstaller" }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $uninstaller
    $psi.Arguments = $arguments
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = Split-Path $uninstaller
    $p = [System.Diagnostics.Process]::Start($psi)

    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        if ($p.HasExited) { throw "uninstaller exited by itself, code $($p.ExitCode)" }
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    $h = $p.MainWindowHandle
    if ($h -eq [IntPtr]::Zero) { throw "no uninstall window appeared" }
    [SetupProbe]::Raise($h)
    Start-Sleep -Milliseconds 900
    Save-Window $h "setup-05-uninstall-confirm.png"

    $btn = Wait-Button $h $BTN_REMOVE 6
    if ($btn -eq [IntPtr]::Zero) { throw "uninstall button not found" }
    [SetupProbe]::ClickMessage($btn)

    $dialog = [IntPtr]::Zero
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $dialog = [SetupProbe]::TopWindowOf([uint32]$p.Id, '#32770', $null)
        if ($dialog -ne [IntPtr]::Zero) { break }
    }
    if ($dialog -eq [IntPtr]::Zero) { throw "no confirmation dialog appeared" }
    Start-Sleep -Milliseconds 700
    Save-Window $dialog "setup-06-uninstall-ask.png"

    $yes = [SetupProbe]::FindButton($dialog, $BTN_YES)
    if ($yes -eq [IntPtr]::Zero) {
        Write-Output "dialog contents: $([string]::Join(' | ', [SetupProbe]::Classes($dialog)))"
        throw "no YES button in the confirmation dialog"
    }
    [SetupProbe]::ClickMessage($yes)

    $done = Wait-Button $h $BTN_DONE 60
    if ($done -eq [IntPtr]::Zero) {
        Save-Window $h "setup-07-uninstall-stuck.png"
        throw "uninstall never reached the done page"
    }
    Start-Sleep -Milliseconds 600
    Save-Window $h "setup-07-uninstall-done.png"

    Stop-Setup $p
    Start-Sleep -Seconds 7
    Write-Output "install dir removed:   $(-not (Test-Path -LiteralPath $Dir))"
    Write-Output "desktop shortcut gone: $(-not (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Desktop')) ($APP_NAME + '.lnk'))))"
    Write-Output "registry entry gone:   $(-not (Test-Path -LiteralPath $UNINST_KEY))"
    return $null
}

function Save-Screen([string]$name) {
    $w = [SetupProbe]::GetSystemMetrics(0)
    $h = [SetupProbe]::GetSystemMetrics(1)
    $bmp = [System.Drawing.Bitmap]::new($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, [System.Drawing.Size]::new($w, $h))
    $g.Dispose()
    $bmp.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "saved full screen $name ($w x $h)"
}

function Show-State([string]$tag, $p, [IntPtr]$h) {
    $p.Refresh()
    Write-Output "--- $tag ---"
    Write-Output "process alive: $(-not $p.HasExited)   mainWindow: $($p.MainWindowHandle)   h: $h"
    if ($h -ne [IntPtr]::Zero) {
        Write-Output "window visible: $([SetupProbe]::IsWindowVisible($h))  title: '$([SetupProbe]::TextOf($h))'"
        $r = New-Object SetupProbe+RECT
        if ([SetupProbe]::GetWindowRect($h, [ref]$r)) {
            Write-Output "window rect: $($r.Left),$($r.Top),$($r.Right),$($r.Bottom)"
        } else {
            Write-Output "window rect: <GetWindowRect failed>"
        }
        foreach ($line in [SetupProbe]::Tree($h, 0)) { Write-Output "  $line" }
    }
}

switch ($Action) {
    'shot' {
        $p = Start-Setup
        Save-Window $p.MainWindowHandle "setup-01-welcome.png"
        Stop-Setup $p
    }
    'diag' {
        $p = Start-Setup
        $h = $p.MainWindowHandle
        Show-State 'right after launch' $p $h

        $btn = Wait-PrimaryButton $h 6
        Write-Output "primary button handle: $btn"
        if ($btn -ne [IntPtr]::Zero) {
            [SetupProbe]::ClickMessage($btn)
            Write-Output "clicked button $btn"
        }

        for ($i = 1; $i -le 12; $i++) {
            Start-Sleep -Milliseconds 1000
            $p.Refresh()
            if ($p.HasExited) {
                Write-Output "process exited at +${i}s, code $($p.ExitCode)"
                break
            }
            Write-Output "+${i}s alive, mainWindow=$($p.MainWindowHandle), visible=$(if ($h -ne [IntPtr]::Zero) { [SetupProbe]::IsWindowVisible($h) } else { 'n/a' })"
        }
        Save-Screen "diag-fullscreen.png"
        Show-State 'after click' $p $h
        Stop-Setup $p
    }
    'install' {
        Do-Install
    }
    'uninstall' {
        Do-Uninstall
    }
    'all' {
        Do-Install
        Start-Sleep -Seconds 2
        Do-Uninstall
    }
}

Write-Output "done: $Action"
