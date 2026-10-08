<#
.SYNOPSIS
  End-to-end check of a published BetterBanner.exe on a real Windows machine (used by CI; needs admin).
  Installs, verifies files/registry/settings, runs the banner briefly, then uninstalls and verifies cleanup.
#>
param([Parameter(Mandatory)] [string] $Exe)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:ProgramFiles 'BetterBanner'
$settingsFile = Join-Path $installDir 'settings.json'
$log = Join-Path $env:LOCALAPPDATA 'BetterBanner\BetterBanner.log'

function Invoke-Banner([string[]] $Arguments, [int] $ExpectedExitCode = 0) {
    # Start-Process does not quote array elements, so quote anything with spaces or quotes ourselves.
    $commandLine = ($Arguments | ForEach-Object { if ($_ -eq '') { '""' } elseif ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $p = Start-Process -FilePath $Exe -ArgumentList $commandLine -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne $ExpectedExitCode) { throw "BetterBanner $($Arguments -join ' ') exited with $($p.ExitCode), expected $ExpectedExitCode" }
}
function Get-Banners { @(Get-Process -Name BetterBanner -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$installDir\*" }) }
function Assert($Condition, [string] $Message) {
    if ($Condition) { Write-Host "ok - $Message"; return }
    Write-Host "--- BetterBanner.log ---"
    if (Test-Path $log) { Get-Content $log | Write-Host } else { Write-Host '(no log)' }
    throw "FAILED: $Message"
}

$size = (Get-Item $Exe).Length / 1MB
Write-Host ("BetterBanner.exe is {0:N2} MB" -f $size)
Assert ($size -lt 15) 'exe is small'

Invoke-Banner @('version')

# A first install must say which classification; it never silently becomes UNCLASSIFIED.
Invoke-Banner @('install', '--no-start') -ExpectedExitCode 2
Assert (-not (Test-Path "$installDir\BetterBanner.exe")) 'install without --level is refused'
Invoke-Banner @('install', '--level', 'SECRET', '--caption', 'SECRET//NOFORN', '--logon-title', 'Notice',
                '--logon-text', 'Authorized use only', '--set', 'ClockEnabled=1', '--no-start')

Assert (Test-Path "$installDir\BetterBanner.exe") 'exe installed'
Assert (Test-Path "$installDir\BetterBanner.scr") 'screen saver installed'
$run = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
Assert ($run.BetterBanner -like '*BetterBanner.exe*') 'starts at sign-in'
$uninstall = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BetterBanner'
Assert ($uninstall.Publisher -eq 'Austin Case') 'listed in Apps & features with publisher'
$cpl = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ControlPanel\NameSpace\{7B3E2C5A-9D41-4F6B-8E2A-5C1D0B9F4A21}'
Assert (Test-Path $cpl) 'registered in Control Panel'
$cplCommand = (Get-ItemProperty 'HKLM:\SOFTWARE\Classes\CLSID\{7B3E2C5A-9D41-4F6B-8E2A-5C1D0B9F4A21}\Shell\Open\Command').'(default)'
Assert ($cplCommand -like '*BetterBanner.exe" settings') 'Control Panel opens the settings window'
$notice = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Assert ($notice.legalnoticetext -eq 'Authorized use only') 'sign-in notice set'
$settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
Assert ($settings.Level -eq 'Secret' -and $settings.Caption -eq 'SECRET//NOFORN') 'settings saved'

Invoke-Banner @('show')

# One bad value in settings.json must not discard the rest (tester's bug), and config must keep the good values.
Set-Content $settingsFile '{ "Level": "S", "Caption": "SECRET//NOFORN", "FontSize": "huge", "ClockEnabled": true }'
Invoke-Banner @('config', '--set', 'HoverTransparency=30', '--quiet')
$settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
Assert ($settings.Level -eq 'Secret' -and $settings.Caption -eq 'SECRET//NOFORN' -and $settings.ClockEnabled -and $settings.HoverTransparency -eq 30) 'config keeps good values next to a bad one'

# An unreadable settings file is never overwritten.
$good = Get-Content $settingsFile -Raw
Set-Content $settingsFile '{ this is not json'
Invoke-Banner @('config', '--set', 'FontSize=12', '--quiet') -ExpectedExitCode 1
Assert ((Get-Content $settingsFile -Raw).Trim() -eq '{ this is not json') 'config refuses to overwrite an unreadable file'
Set-Content $settingsFile $good

# A value can't be another option.
Invoke-Banner @('config', '--caption', '--quiet') -ExpectedExitCode 2

# Settings window opens and stays up until closed.
$settingsWindow = Start-Process -FilePath "$installDir\BetterBanner.exe" -ArgumentList 'settings' -PassThru
Start-Sleep -Seconds 3
if ($settingsWindow.HasExited) { Write-Host "settings exited with code $($settingsWindow.ExitCode)" }
Assert (-not $settingsWindow.HasExited) 'settings window opens'
Stop-Process -Id $settingsWindow.Id

if (Test-Path $log) { Remove-Item $log }
$banner = Start-Process -FilePath "$installDir\BetterBanner.exe" -PassThru
Start-Sleep -Seconds 6
Assert (-not $banner.HasExited) 'banner keeps running'
$second = Start-Process -FilePath "$installDir\BetterBanner.exe" -PassThru
Assert ($second.WaitForExit(5000)) 'second instance exits (single instance per session)'

# Upgrading in place: install no longer kills banners; each one restarts itself into the new version.
Invoke-Banner @('install', '--no-start', '--quiet')
Assert (Test-Path "$installDir\BetterBanner.exe") 'reinstall replaced the running exe'
Start-Sleep -Seconds 8
Assert ($banner.HasExited) 'old banner process restarted after upgrade'
$restarted = Get-Banners
Assert ($restarted.Count -eq 1 -and $restarted[0].Id -ne $banner.Id) 'exactly one new banner running after upgrade'
$restarted | Stop-Process
Get-Content $log | Write-Host
Assert (-not (Select-String -Path $log -Pattern 'ERROR' -Quiet)) 'no errors logged'

Invoke-Banner @('uninstall')
Start-Sleep -Seconds 1
Assert (-not (Test-Path $installDir)) 'install folder and settings removed'
$notice = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Assert ([string]::IsNullOrEmpty($notice.legalnoticetext)) 'sign-in notice cleared'
Assert ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run').BetterBanner) 'no longer starts at sign-in'
Assert (-not (Test-Path $cpl)) 'removed from Control Panel'

Write-Host 'Smoke test passed.'
