# Native window qualification on a disposable Windows runner only.
param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [ValidateSet("Connected","Blocked")][string]$NetworkMode = "Blocked"
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $Executable).Path
$out = (Resolve-Path $EvidenceDirectory).Path
$report = Join-Path $out 'host.json'
if (Test-Path $report) { throw 'Evidence path must be fresh.' }
$rules = @()
$process = $null
function Wait-Probe([scriptblock]$Condition) {
    for ($i = 0; $i -lt 240; $i++) {
        if (Test-Path $report) {
            try { $value = Get-Content $report -Raw | ConvertFrom-Json }
            catch { Start-Sleep -Milliseconds 250; continue }
            if (& $Condition $value) { return $value }
        }
        if ($process.HasExited) { throw "Host exited before evidence: $($process.ExitCode)" }
        Start-Sleep -Milliseconds 250
    }
    if (Test-Path $report) { Get-Content $report -Raw | Write-Host }
    if (Test-Path "$report.startup.log") { Get-Content "$report.startup.log" -Raw | Write-Host }
    $process.Refresh()
    Write-Host "Window: $($process.MainWindowTitle), Handle: $($process.MainWindowHandle)"
    Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Select-Object Id,SessionId,MainWindowTitle | Format-Table | Out-String | Write-Host
    throw "Timeout: $Condition"
}
try {
    # The network remains available to the CI runner. Block outbound network for
    # the app and the installed WebView2 executable(s) during this smoke test.
    $runtimeRoots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\EdgeWebView\Application'),
        (Join-Path $env:ProgramFiles 'Microsoft\EdgeWebView\Application'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\EdgeWebView\Application')
    ) | Where-Object { Test-Path $_ }
    $webviews = @($runtimeRoots | ForEach-Object {
        Get-ChildItem $_ -Filter msedgewebview2.exe -Recurse -File -ErrorAction SilentlyContinue
    } | Select-Object -ExpandProperty FullName -Unique)
    if ($webviews.Count -eq 0) { throw 'Installed WebView2 runtime not found.' }
    if ($NetworkMode -eq "Blocked" -and @(Get-NetFirewallProfile | Where-Object { -not $_.Enabled }).Count -gt 0) {
        throw 'Firewall profile is disabled; offline condition cannot be established.'
    }
    if ($NetworkMode -eq "Blocked") {
    foreach ($binary in @($exe) + $webviews) {
        $name = 'WowDiHostQualification-' + [guid]::NewGuid().ToString('N')
        New-NetFirewallRule -Name $name -DisplayName $name -Direction Outbound -Program $binary -Action Block -Profile Any | Out-Null
        $rules += $name
    }
    }
    $env:WEBVIEW2_USER_DATA_FOLDER = Join-Path ([System.IO.Path]::GetTempPath()) ("wowdi-host-" + [guid]::NewGuid().ToString("N"))
    Write-Host "Native probe network mode: $NetworkMode"
    Set-Content -Path (Join-Path $out "network-mode.txt") -Value $NetworkMode
    $process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList @('--evidence', ('"' + $report + '"')) -PassThru
    $initial = Wait-Probe { param($p) $p.Rendered -and $p.Direction -eq 'ltr' -and $p.FixtureRevision -eq 'host-qualification-v1' -and $p.DocumentUrl -eq 'http://localhost/' }
    $process.Refresh()
    if ($process.MainWindowHandle -eq [IntPtr]::Zero -or $process.MainWindowTitle -ne 'WOW DI Host Qualification') {
        throw 'Expected native window was not found.'
    }
    Add-Type -AssemblyName System.Windows.Forms
    if (-not ("QualificationForeground" -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class QualificationForeground {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll", SetLastError=true)] static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr hwnd,System.Text.StringBuilder name,int count);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumChild callback, IntPtr param);
    delegate bool EnumChild(IntPtr hwnd,IntPtr param);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int left,top,right,bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int x,y; }
    public static string Children(IntPtr parent) {
        var list=new System.Collections.Generic.List<string>();
        EnumChildWindows(parent,(hwnd,param)=>{
            var name=new System.Text.StringBuilder(256);GetClassName(hwnd,name,name.Capacity);
            uint pid;uint tid=GetWindowThreadProcessId(hwnd,out pid);
            list.Add($"hwnd={hwnd};class={name};pid={pid};thread={tid};"+FocusState(hwnd));return true;
        },IntPtr.Zero);
        return string.Join("\n",list);
    }
    // Diagnostic only: acquire focus in the blank lower part of the fixture.
    // Never click a control. The original keyboard-only failure remains a failure.
    public static void ClickBlank(IntPtr parent) {
        Rect r;if(!GetClientRect(parent,out r))throw new InvalidOperationException("client rectangle");
        var p=new Point{x=(r.right-r.left)/2,y=(r.bottom-r.top)*4/5};
        if(!ClientToScreen(parent,ref p))throw new InvalidOperationException("screen coordinate");
        var hit=WindowFromPoint(p);
        if(hit!=parent&&!IsChild(parent,hit))throw new InvalidOperationException("fixture does not own click point");
        if(!SetCursorPos(p.x,p.y))throw new InvalidOperationException("cursor position");
        var clicks=new[]{new Input{type=0,value=new InputUnion{mouse=new MouseInput{flags=2}}},new Input{type=0,value=new InputUnion{mouse=new MouseInput{flags=4}}}};
        if(SendInput(2,clicks,Marshal.SizeOf<Input>())!=2)throw new InvalidOperationException("mouse input rejected");
    }
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardInput { public ushort key, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput { public int x,y; public uint data, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion {
        [FieldOffset(0)] public KeyboardInput keyboard;
        [FieldOffset(0)] public MouseInput mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Input { public uint type; public InputUnion value; }
    static Input Key(ushort key, bool up) => new Input {
        type = 1, value = new InputUnion { keyboard = new KeyboardInput { key = key, flags = up ? 2u : 0u } }
    };
    public static void SendKey(ushort key, bool shift) {
        var sequence = shift
            ? new[] { Key(0x10, false), Key(key, false), Key(key, true), Key(0x10, true) }
            : new[] { Key(key, false), Key(key, true) };
        var accepted = SendInput((uint)sequence.Length, sequence, Marshal.SizeOf<Input>());
        if (accepted != sequence.Length)
            throw new InvalidOperationException("SendInput accepted " + accepted + "/" + sequence.Length + "; last error=" + Marshal.GetLastWin32Error());
    }
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError=true)] static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [StructLayout(LayoutKind.Sequential)]
    struct GuiThreadInfo {
        public uint cbSize, flags;
        public IntPtr active, focus, capture, menuOwner, moveSize, caret;
        public int left, top, right, bottom;
    }
    public static string FocusState(IntPtr window) {
        uint pid;
        uint thread = GetWindowThreadProcessId(window, out pid);
        var info = new GuiThreadInfo { cbSize = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info)) return "GetGUIThreadInfo failed: " + Marshal.GetLastWin32Error();
        return $"thread={thread};pid={pid};active={info.active};focus={info.focus};flags={info.flags}";
    }
}
'@
    }
    $shell = New-Object -ComObject WScript.Shell
    if (-not $shell.AppActivate($process.Id)) { throw 'Could not activate the native window.' }
    Start-Sleep -Milliseconds 500
    # Force a genuine activation transition on this fixture's own window only.
    # Photino.Native v4.0.22 invokes WebView2 MoveFocus on WM_ACTIVATE.
    # No DOM click or focus mutation is used to establish keyboard traversal.
    $handle = $process.MainWindowHandle
    [QualificationForeground]::ShowWindow($handle, 6) | Out-Null
    Start-Sleep -Milliseconds 350
    [QualificationForeground]::ShowWindow($handle, 9) | Out-Null
    [QualificationForeground]::SetForegroundWindow($handle) | Out-Null
    if (-not $shell.AppActivate($process.Id)) { throw 'Could not reactivate the fixture after restore.' }
    Start-Sleep -Milliseconds 500
    Write-Host ("After native reactivation: " + [QualificationForeground]::FocusState($handle))
    function Send-ProbeKey([string]$Key) {
        $process.Refresh()
        $foreground = [QualificationForeground]::GetForegroundWindow()
        if ($foreground -ne $process.MainWindowHandle) {
            throw "Probe window is not foreground (actual $foreground; expected $($process.MainWindowHandle))."
        }
        Write-Host ("Before key " + $Key + ": " + [QualificationForeground]::FocusState($foreground))
        switch ($Key) {
            '{TAB}' { [QualificationForeground]::SendKey(9, $false) }
            '+{TAB}' { [QualificationForeground]::SendKey(9, $true) }
            '{ENTER}' { [QualificationForeground]::SendKey(13, $false) }
            default { throw "Unsupported qualification key: $Key" }
        }
        Start-Sleep -Milliseconds 250
        Write-Host ("After key " + $Key + ": " + [QualificationForeground]::FocusState($foreground))
    }
    # Initial focus is an explicit app accessibility behavior, not a keyboard test.
    $null = Wait-Probe { param($p) $p.FocusedId -eq 'direction' }
    # Real forward and backward OS keyboard navigation must both work.
    Write-Host ("Native descendants before keyboard: " + [QualificationForeground]::Children($handle))
    Send-ProbeKey '{TAB}'
    try {
        $null = Wait-Probe { param($p) $p.FocusedId -eq 'confirm' -and $p.Events -contains 'focus:confirm' }
    } catch {
        $original = $_
        Write-Host ("Native descendants after failed keyboard: " + [QualificationForeground]::Children($handle))
        # Distinguish OS/WebView focus acquisition from general keyboard dispatch.
        # This branch cannot turn the failed qualification green.
        try {
            [QualificationForeground]::ClickBlank($handle)
            Start-Sleep -Milliseconds 350
            Write-Host ("Native descendants after blank activation: " + [QualificationForeground]::Children($handle))
            Send-ProbeKey '{TAB}'
            Send-ProbeKey '{TAB}'
            Start-Sleep -Milliseconds 350
            if (Test-Path $report) {
                Copy-Item $report (Join-Path $out 'after-blank-activation.json')
                Get-Content $report -Raw | Write-Host
            }
        } catch { Write-Warning "Secondary focus diagnostic failed: $_" }
        throw $original
    }
    Send-ProbeKey '+{TAB}'
    $null = Wait-Probe { param($p) $p.FocusedId -eq 'direction' }
    Send-ProbeKey '{ENTER}'
    $null = Wait-Probe { param($p) $p.Direction -eq 'rtl' -and $p.Events -contains 'direction:rtl' }
    Send-ProbeKey '{TAB}'
    $null = Wait-Probe { param($p) $p.FocusedId -eq 'confirm' }
    Send-ProbeKey '{ENTER}'
    $null = Wait-Probe { param($p) $p.Events -contains 'confirm' }
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(20000) -or $process.ExitCode -ne 0) { throw 'Native window did not close normally.' }
    Get-Content $report
}
catch {
    if ($process -and -not $process.HasExited) {
        try {
            Add-Type -AssemblyName System.Drawing
            Add-Type -AssemblyName System.Windows.Forms
            $bounds = [System.Windows.Forms.Screen]::FromHandle($process.MainWindowHandle).Bounds
            $bitmap = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
            try {
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try { $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size) } finally { $graphics.Dispose() }
                $bitmap.Save((Join-Path $out "desktop-failure.png"), [System.Drawing.Imaging.ImageFormat]::Png)
            } finally { $bitmap.Dispose() }
        } catch { Write-Warning "Failure screenshot unavailable: $_" }
    }
    throw
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    foreach ($name in $rules) { Remove-NetFirewallRule -Name $name }
    if ($env:WEBVIEW2_USER_DATA_FOLDER) { Remove-Item -LiteralPath $env:WEBVIEW2_USER_DATA_FOLDER -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:WEBVIEW2_USER_DATA_FOLDER -ErrorAction SilentlyContinue
}
