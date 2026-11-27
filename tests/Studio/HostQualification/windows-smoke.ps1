# Native window qualification on a disposable Windows runner only.
param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $Executable).Path
$out = (Resolve-Path $EvidenceDirectory).Path
$report = Join-Path $out 'host.json'
if (Test-Path $report) { throw 'Evidence path must be fresh.' }
$rules = @()
$process = $null
function Wait-Probe([scriptblock]$Condition) {
    for ($i = 0; $i -lt 120; $i++) {
        if (Test-Path $report) {
            try { $value = Get-Content $report -Raw | ConvertFrom-Json }
            catch { Start-Sleep -Milliseconds 250; continue }
            if (& $Condition $value) { return $value }
        }
        if ($process.HasExited) { throw "Host exited before evidence: $($process.ExitCode)" }
        Start-Sleep -Milliseconds 250
    }
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
    if (@(Get-NetFirewallProfile | Where-Object { -not $_.Enabled }).Count -gt 0) {
        throw 'Firewall profile is disabled; offline condition cannot be established.'
    }
    foreach ($binary in @($exe) + $webviews) {
        $name = 'WowDiHostQualification-' + [guid]::NewGuid().ToString('N')
        New-NetFirewallRule -Name $name -DisplayName $name -Direction Outbound -Program $binary -Action Block -Profile Any | Out-Null
        $rules += $name
    }
    $process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList @('--evidence', ('"' + $report + '"')) -PassThru
    $initial = Wait-Probe { param($p) $p.Rendered -and $p.Direction -eq 'ltr' -and $p.FixtureRevision -eq 'host-qualification-v1' -and $p.DocumentUrl -eq 'http://localhost/' }
    $process.Refresh()
    if ($process.MainWindowHandle -eq [IntPtr]::Zero -or $process.MainWindowTitle -ne 'WOW DI Host Qualification') {
        throw 'Expected native window was not found.'
    }
    $shell = New-Object -ComObject WScript.Shell
    if (-not $shell.AppActivate($process.Id)) { throw 'Could not activate the native window.' }
    if ($initial.FocusedId -ne 'direction') {
        $shell.SendKeys('{TAB}')
        $null = Wait-Probe { param($p) $p.FocusedId -eq 'direction' }
    }
    $shell.SendKeys('{ENTER}')
    $null = Wait-Probe { param($p) $p.Direction -eq 'rtl' -and $p.Events -contains 'direction:rtl' }
    $shell.SendKeys('{TAB}')
    $null = Wait-Probe { param($p) $p.FocusedId -eq 'confirm' -and $p.Events -contains 'focus:confirm' }
    $shell.SendKeys('{ENTER}')
    $null = Wait-Probe { param($p) $p.Events -contains 'confirm' }
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(20000) -or $process.ExitCode -ne 0) { throw 'Native window did not close normally.' }
    Get-Content $report
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    foreach ($name in $rules) { Remove-NetFirewallRule -Name $name }
}
