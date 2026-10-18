<#
.SYNOPSIS
  End-to-end check of a published BetterBanner.exe on a real Windows machine (used by CI; needs admin).
  Installs, verifies files/registry/settings, runs the banner briefly, then uninstalls and verifies cleanup.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    # Optional: published v1.0.0-beta.1 and v1.1.0 exes, to test upgrading from them.
    [string] $Beta1Exe,
    [string] $PreviousExe
)

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

# Upgrading from an earlier release: settings and the sign-in notice carry over, the old Run-key banner is stopped and
# the Run key removed (the service replaces it), and beta.1's ProgramData folder is removed.
function Test-UpgradeFrom([string] $OldExe, [string] $Label) {
    $legacyDir = Join-Path $env:ProgramData 'BetterBanner'
    $p = Start-Process -FilePath $OldExe -ArgumentList 'install --level S --caption "SECRET//NOFORN" --logon-title Notice --logon-text "Authorized use only" --no-start --quiet' -Wait -PassThru -NoNewWindow
    Assert ($p.ExitCode -eq 0 -and (Test-Path "$installDir\BetterBanner.exe")) "$Label installed"
    $oldBanner = Start-Process -FilePath "$installDir\BetterBanner.exe" -PassThru
    Start-Sleep -Seconds 3

    Invoke-Banner @('install', '--no-start')
    $settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
    Assert ($settings.Level -eq 'Secret' -and $settings.Caption -eq 'SECRET//NOFORN') "$Label settings kept without --level"
    Assert (-not (Test-Path $legacyDir)) "$Label ProgramData folder removed"
    $notice = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    Assert ($notice.legalnoticetext -eq 'Authorized use only') "$Label sign-in notice kept"
    Assert ($oldBanner.HasExited) "$Label banner stopped"
    Assert ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run').BetterBanner) "$Label Run key replaced"
    Assert ($null -ne (Get-Service BetterBanner -ErrorAction SilentlyContinue)) "$Label replaced by the service"

    Invoke-Banner @('uninstall')
    Start-Sleep -Seconds 1
    Assert (-not (Test-Path $installDir)) "clean slate after the $Label upgrade test"
}
if ($Beta1Exe) { Test-UpgradeFrom $Beta1Exe 'beta.1' }
if ($PreviousExe) { Test-UpgradeFrom $PreviousExe '1.1.0' }

# Double-clicking the downloaded exe opens the setup window (and stays open until closed).
$setup = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 3
Assert (-not $setup.HasExited) 'downloaded exe opens the setup window'
Stop-Process -Id $setup.Id

# A first install must say which classification; it never silently becomes UNCLASSIFIED.
Invoke-Banner @('install', '--no-start') -ExpectedExitCode 2
Assert (-not (Test-Path "$installDir\BetterBanner.exe")) 'install without --level is refused'
Invoke-Banner @('install', '--level', 'SECRET', '--caption', 'SECRET//NOFORN', '--logon-title', 'Notice',
                '--logon-text', 'Authorized use only', '--set', 'ClockEnabled=1', '--no-start')

Assert (Test-Path "$installDir\BetterBanner.exe") 'exe installed'
Assert (Test-Path "$installDir\BetterBanner.scr") 'screen saver installed'
$service = Get-Service BetterBanner -ErrorAction SilentlyContinue
Assert ($service -and $service.StartType -eq 'Automatic' -and $service.Status -eq 'Stopped') 'service registered (automatic, not started with --no-start)'
Assert ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run').BetterBanner) 'no Run key'
$uninstall = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BetterBanner'
Assert ($uninstall.Publisher -eq 'Austin Case') 'listed in Apps & features with publisher'
$cpl = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ControlPanel\NameSpace\{7B3E2C5A-9D41-4F6B-8E2A-5C1D0B9F4A21}'
Assert (Test-Path $cpl) 'registered in Control Panel'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Better Banner.lnk'
Assert (Test-Path $shortcut) 'Start menu entry created'
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

# A value can't be one of our options, but may start with "--".
Invoke-Banner @('config', '--caption', '--quiet') -ExpectedExitCode 2
Invoke-Banner @('config', '--logon-text', '-- Authorized use only --', '--quiet')
$notice = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Assert ($notice.legalnoticetext -eq '-- Authorized use only --') 'value starting with -- accepted'

# Banner text that contradicts the level is refused (issue #18), including after a level change.
Invoke-Banner @('config', '--caption', 'UNCLASSIFIED', '--quiet') -ExpectedExitCode 2
Invoke-Banner @('config', '--level', 'C', '--quiet') -ExpectedExitCode 2
$settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
Assert ($settings.Level -eq 'Secret' -and $settings.Caption -eq 'SECRET//NOFORN') 'caption contradicting the level refused'

# config never saves a level nobody chose (issue #6), and never removes the notice unless told to.
$good = Get-Content $settingsFile -Raw
Set-Content $settingsFile '{ "Level": "Sekret", "Caption": "SECRET//NOFORN" }'
Invoke-Banner @('config', '--set', 'FontSize=12', '--quiet') -ExpectedExitCode 2
Assert ((Get-Content $settingsFile -Raw).Trim() -eq '{ "Level": "Sekret", "Caption": "SECRET//NOFORN" }') 'config refuses to save without a level'
Invoke-Banner @('config', '--set', 'FontSize=999', '--quiet') -ExpectedExitCode 2
Set-Content $settingsFile '{ "Level": "S" }'
Invoke-Banner @('config', '--set', 'FontSize=12', '--quiet')
$notice = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Assert ($notice.legalnoticetext -eq '-- Authorized use only --') 'notice kept when the file has no notice fields'
Set-Content $settingsFile $good

# Settings window opens and stays up until closed.
$settingsWindow = Start-Process -FilePath "$installDir\BetterBanner.exe" -ArgumentList 'settings' -PassThru
Start-Sleep -Seconds 3
if ($settingsWindow.HasExited) { Write-Host "settings exited with code $($settingsWindow.ExitCode)" }
Assert (-not $settingsWindow.HasExited) 'settings window opens'
Stop-Process -Id $settingsWindow.Id

if (Test-Path $log) { Remove-Item $log }
$mySession = (Get-Process -Id $PID).SessionId
function Get-SessionBanners { @(Get-Banners | Where-Object { $_.SessionId -eq $mySession }) }
function Wait-SessionBanner([int] $ExceptId = 0, [int] $Seconds = 20) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $found = @(Get-SessionBanners | Where-Object { $_.Id -ne $ExceptId })
        if ($found.Count -gt 0) { Start-Sleep -Seconds 2; return @(Get-SessionBanners) }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return @()
}

# Issue #17: the service starts the banner in every signed-in session and restarts it when it's ended.
Start-Service BetterBanner
Start-Sleep -Seconds 3
Assert ((Get-Service BetterBanner).Status -eq 'Running') 'service running'
$signedIn = $mySession -ne 0
if (-not $signedIn) {
    Write-Host "note - this runner has no signed-in session (session 0); banner restart checks skipped"
} else {
    $banners = Wait-SessionBanner
    Assert ($banners.Count -eq 1) 'service started exactly one banner in this session'
    $banner = $banners[0]
    $started = Get-Date

    Stop-Process -Id $banner.Id -Force
    $restarted = Wait-SessionBanner -ExceptId $banner.Id
    Assert ($restarted.Count -eq 1 -and $restarted[0].Id -ne $banner.Id) 'ended banner restarted within seconds'
    $event = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Better Banner'; Id = 1001; StartTime = $started } -MaxEvents 1 -ErrorAction SilentlyContinue
    Assert ($event -and $event.Message -like "*session $mySession*restarted*") 'unexpected exit and restart in the Application log'
    Write-Host "event: $($event.Message)"

    $second = Start-Process -FilePath "$installDir\BetterBanner.exe" -ArgumentList 'banner' -PassThru
    Assert ($second.WaitForExit(5000)) 'second instance exits (single instance per session)'

    # Upgrading in place: the service and its banners are stopped, the exe replaced, and new banners started.
    Invoke-Banner @('install', '--quiet')
    $upgraded = Wait-SessionBanner -ExceptId $restarted[0].Id
    Assert ($upgraded.Count -eq 1 -and $upgraded[0].Id -ne $restarted[0].Id) 'exactly one new banner running after upgrade'
}
Get-Content $log -ErrorAction SilentlyContinue | Write-Host
Assert (-not (Test-Path $log) -or -not (Select-String -Path $log -Pattern 'ERROR' -Quiet)) 'no errors logged'

Invoke-Banner @('uninstall')
Start-Sleep -Seconds 1
Assert (-not (Test-Path $installDir)) 'install folder and settings removed'
$notice = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Assert ([string]::IsNullOrEmpty($notice.legalnoticetext)) 'sign-in notice cleared'
Assert ($null -eq (Get-Service BetterBanner -ErrorAction SilentlyContinue)) 'service removed'
Assert ((Get-Banners).Count -eq 0) 'no banners left running'
Assert (-not (Test-Path $cpl)) 'removed from Control Panel'
Assert (-not (Test-Path $shortcut)) 'Start menu entry removed'

Write-Host 'Smoke test passed.'
