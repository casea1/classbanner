<#
.SYNOPSIS
  End-to-end check of a published ClassBanner.exe on a real Windows machine (used by CI; needs admin).
  Installs, verifies files/registry/settings, runs the banner briefly, then uninstalls and verifies cleanup.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    # Optional: published Better Banner exes (v1.0.0-beta.1, v1.1.0, v1.2.0), to test upgrading from them.
    [string] $Beta1Exe,
    [string] $PreviousExe,
    [string] $ServiceExe
)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:ProgramFiles 'ClassBanner'
$settingsFile = Join-Path $installDir 'settings.json'
$log = Join-Path $env:LOCALAPPDATA 'ClassBanner\ClassBanner.log'

function Invoke-Banner([string[]] $Arguments, [int] $ExpectedExitCode = 0) {
    # Start-Process does not quote array elements, so quote anything with spaces or quotes ourselves.
    $commandLine = ($Arguments | ForEach-Object { if ($_ -eq '') { '""' } elseif ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $p = Start-Process -FilePath $Exe -ArgumentList $commandLine -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne $ExpectedExitCode) { throw "ClassBanner $($Arguments -join ' ') exited with $($p.ExitCode), expected $ExpectedExitCode" }
}
function Get-Banners { @(Get-Process -Name ClassBanner -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$installDir\*" }) }
function Assert($Condition, [string] $Message) {
    if ($Condition) { Write-Host "ok - $Message"; return }
    Write-Host "--- ClassBanner.log ---"
    if (Test-Path $log) { Get-Content $log | Write-Host } else { Write-Host '(no log)' }
    throw "FAILED: $Message"
}

$dwmKey = 'HKCU:\SOFTWARE\Microsoft\Windows\DWM'
if (-not (Test-Path $dwmKey)) { New-Item $dwmKey | Out-Null }
Set-ItemProperty $dwmKey ColorPrevalence 0 -Type DWord
$size = (Get-Item $Exe).Length / 1MB
Write-Host ("ClassBanner.exe is {0:N2} MB" -f $size)
Assert ($size -lt 15) 'exe is small'

Invoke-Banner @('version')

# Upgrading from Better Banner (the name up to 1.2): settings and the sign-in notice carry over; its banners, service,
# Run key, Apps & features entry, Start menu entry and folders are removed; ClassBanner's service replaces them.
function Test-UpgradeFrom([string] $OldExe, [string] $Label) {
    $oldDir = Join-Path $env:ProgramFiles 'BetterBanner'
    $legacyDir = Join-Path $env:ProgramData 'BetterBanner'
    $p = Start-Process -FilePath $OldExe -ArgumentList 'install --level S --caption "SECRET//NOFORN" --logon-title Notice --logon-text "Authorized use only" --no-start --quiet' -Wait -PassThru -NoNewWindow
    Assert ($p.ExitCode -eq 0 -and (Test-Path "$oldDir\BetterBanner.exe")) "$Label installed"
    if (Get-Service BetterBanner -ErrorAction SilentlyContinue) { Start-Service BetterBanner } else { Start-Process -FilePath "$oldDir\BetterBanner.exe" | Out-Null }
    Start-Sleep -Seconds 4

    Invoke-Banner @('install', '--no-start')
    $settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
    Assert ($settings.Level -eq 'Secret' -and $settings.Caption -eq 'SECRET//NOFORN') "$Label settings imported without --level"
    $notice = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    Assert ($notice.legalnoticetext -eq 'Authorized use only') "$Label sign-in notice kept"
    Assert (@(Get-Process -Name BetterBanner -ErrorAction SilentlyContinue).Count -eq 0) "$Label banners stopped"
    Assert ($null -eq (Get-Service BetterBanner -ErrorAction SilentlyContinue)) "$Label service removed"
    Assert ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run').BetterBanner) "$Label Run key removed"
    Assert (-not (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BetterBanner')) "$Label removed from Apps & features"
    Assert (-not (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Better Banner.lnk'))) "$Label Start menu entry removed"
    Assert (-not (Test-Path $oldDir) -and -not (Test-Path $legacyDir)) "$Label folders removed"
    Assert ($null -ne (Get-Service ClassBanner -ErrorAction SilentlyContinue)) "$Label replaced by the ClassBanner service"
    # Issue #25: the new banner runs right after the upgrade, while the old banner's colors are still in the profile.
    Start-Service ClassBanner
    Start-Sleep -Seconds 6

    Invoke-Banner @('uninstall')
    Start-Sleep -Seconds 1
    Assert (-not (Test-Path $installDir)) "clean slate after the $Label upgrade test"
    $notice = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    Assert ([string]::IsNullOrEmpty($notice.legalnoticetext)) "$Label sign-in notice still cleared by uninstall"
    # The old banner colored this profile's title bars; uninstall puts the user's own setting back (issue #19).
    $dwm = Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\DWM' -ErrorAction SilentlyContinue
    Assert ($dwm.ColorPrevalence -ne 1) "$Label classification accent removed from title bars by uninstall"
}
if ($Beta1Exe) { Test-UpgradeFrom $Beta1Exe 'beta.1' }
if ($PreviousExe) { Test-UpgradeFrom $PreviousExe '1.1.0' }
if ($ServiceExe) { Test-UpgradeFrom $ServiceExe '1.2.0' }

# Double-clicking the downloaded exe opens the setup window (and stays open until closed).
$setup = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 3
Assert (-not $setup.HasExited) 'downloaded exe opens the setup window'
Stop-Process -Id $setup.Id

# A first install must say which classification; it never silently becomes UNCLASSIFIED.
Invoke-Banner @('install', '--no-start') -ExpectedExitCode 2
Assert (-not (Test-Path "$installDir\ClassBanner.exe")) 'install without --level is refused'
Invoke-Banner @('install', '--level', 'SECRET', '--caption', 'SECRET//NOFORN', '--logon-title', 'Notice',
                '--logon-text', 'Authorized use only', '--set', 'ClockEnabled=1',
                '--set', 'ShowUserName=1', '--set', 'ShowComputerName=1', '--set', 'ComputerNamePosition=BottomRight',
                '--no-start')

Assert (Test-Path "$installDir\ClassBanner.exe") 'exe installed'
Assert (Test-Path "$installDir\ClassBanner.scr") 'screen saver installed'
$service = Get-Service ClassBanner -ErrorAction SilentlyContinue
Assert ($service -and $service.StartType -eq 'Automatic' -and $service.Status -eq 'Stopped') 'service registered (automatic, not started with --no-start)'
Assert ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run').ClassBanner) 'no Run key'
$uninstall = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ClassBanner'
Assert ($uninstall.Publisher -eq 'Austin Case') 'listed in Apps & features with publisher'
$cpl = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ControlPanel\NameSpace\{7B3E2C5A-9D41-4F6B-8E2A-5C1D0B9F4A21}'
Assert (Test-Path $cpl) 'registered in Control Panel'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\ClassBanner.lnk'
Assert (Test-Path $shortcut) 'Start menu entry created'
$cplCommand = (Get-ItemProperty 'HKLM:\SOFTWARE\Classes\CLSID\{7B3E2C5A-9D41-4F6B-8E2A-5C1D0B9F4A21}\Shell\Open\Command').'(default)'
Assert ($cplCommand -like '*ClassBanner.exe" settings') 'Control Panel opens the settings window'
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

# --reset (used by exported commands) starts from the defaults instead of the current settings.
$beforeReset = Get-Content $settingsFile -Raw
Invoke-Banner @('config', '--reset', '--level', 'S', '--set', 'FontSize=14', '--quiet')
$settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
Assert ($settings.FontSize -eq 14 -and $settings.HoverTransparency -eq 40 -and -not $settings.ClockEnabled -and $settings.Caption -eq $null) 'config --reset replaces the settings'
Invoke-Banner @('export')
Set-Content $settingsFile $beforeReset

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
$settingsWindow = Start-Process -FilePath "$installDir\ClassBanner.exe" -ArgumentList 'settings' -PassThru
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
Start-Service ClassBanner
Start-Sleep -Seconds 3
Assert ((Get-Service ClassBanner).Status -eq 'Running') 'service running'
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
    $event = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ClassBanner'; Id = 1001; StartTime = $started } -MaxEvents 1 -ErrorAction SilentlyContinue
    Assert ($event -and $event.Message -like "*session $mySession*restarted*") 'unexpected exit and restart in the Application log'
    Write-Host "event: $($event.Message)"

    # Issue #17 re-test: hiding a banner window, suspending its thread or ending its thread must not remove the marking.
    Add-Type -Namespace CB -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenThread(int access, bool inherit, int id);
[DllImport("kernel32.dll")] public static extern int SuspendThread(IntPtr h);
[DllImport("kernel32.dll")] public static extern bool TerminateThread(IntPtr h, int code);
'@
    function Get-MainThread($p) { ($p.Threads | Sort-Object StartTime | Select-Object -First 1).Id }

    $window = [CB.W]::FindWindow('ClassBanner.Banner', $null)
    Write-Host "banner window: $window (visible: $([CB.W]::IsWindowVisible($window)))"
    if ($window -eq [IntPtr]::Zero) {
        Write-Host "note - the banner's window isn't on this runner's desktop; hidden-window check skipped"
    } else {
        [CB.W]::ShowWindow($window, 0) | Out-Null
        Write-Host "hidden: visible now $([CB.W]::IsWindowVisible($window))"
        Start-Sleep -Seconds 5
        Assert ([CB.W]::IsWindowVisible($window)) 'hidden banner window shown again'
    }

    $current = (Get-SessionBanners)[0]
    $t0 = Get-Date
    [CB.W]::SuspendThread([CB.W]::OpenThread(0x0002, $false, (Get-MainThread $current))) | Out-Null
    Start-Sleep -Seconds 4
    $event = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ClassBanner'; Id = 1005; StartTime = $t0 } -MaxEvents 1 -ErrorAction SilentlyContinue
    Assert ($event -and $event.Message -like '*suspended*resumed*') 'suspended banner resumed by the service (event 1005)'

    $t0 = Get-Date
    [CB.W]::TerminateThread([CB.W]::OpenThread(0x0001, $false, (Get-MainThread $current)), 0) | Out-Null
    $replaced = Wait-SessionBanner -ExceptId $current.Id
    Assert ($replaced.Count -eq 1 -and $replaced[0].Id -ne $current.Id) 'banner whose window thread was ended is restarted'
    $event = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ClassBanner'; Id = 1005; StartTime = $t0 } -MaxEvents 1 -ErrorAction SilentlyContinue
    Assert ($event -and $event.Message -like '*showing nothing*') 'ended window thread logged (event 1005)'
    $restarted = $replaced

    # The banner's own user, not elevated, can't suspend or end its threads or end the process.
    $probe = Join-Path $env:TEMP 'cb-probe.ps1'
    $probeOut = Join-Path $env:TEMP 'cb-probe.txt'
    Set-Content $probe @'
param($ProcessId, $Out)
Add-Type -Namespace P -Name K -MemberDefinition '[DllImport("kernel32.dll")] public static extern IntPtr OpenThread(int a, bool i, int id); [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(int a, bool i, int id);'
$granted = @()
foreach ($t in (Get-Process -Id $ProcessId).Threads) { if ([P.K]::OpenThread(0x0003, $false, $t.Id) -ne [IntPtr]::Zero) { $granted += "thread $($t.Id)" } }
if ([P.K]::OpenProcess(0x0001, $false, $ProcessId) -ne [IntPtr]::Zero) { $granted += 'process' }
"checked; granted: $($granted -join ', ')" | Set-Content $Out
'@
    Remove-Item $probeOut -ErrorAction SilentlyContinue
    & runas.exe /trustlevel:0x20000 "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$probe`" -ProcessId $($restarted[0].Id) -Out `"$probeOut`""
    for ($i = 0; $i -lt 30 -and -not (Test-Path $probeOut); $i++) { Start-Sleep -Seconds 1 }
    if (Test-Path $probeOut) {
        $probeResult = (Get-Content $probeOut -Raw).Trim()
        Write-Host "probe: $probeResult"
        Assert ($probeResult -eq 'checked; granted:') 'non-elevated user cannot suspend or end the banner threads or process'
    } else {
        Write-Host 'note - runas /trustlevel unavailable on this runner; non-elevated probe skipped'
    }

    $second = Start-Process -FilePath "$installDir\ClassBanner.exe" -ArgumentList 'banner' -PassThru
    Assert ($second.WaitForExit(5000)) 'second instance exits (single instance per session)'

    # Upgrading in place: the service and its banners are stopped, the exe replaced, and new banners started.
    # The banner saved this profile's own values before coloring the title bars (issue #19).
    Assert ((Get-ItemProperty $dwmKey).ColorPrevalence -eq 1) 'title bars colored'
    Assert (Test-Path 'HKCU:\Software\ClassBanner\Saved') "user's own values saved"

    Invoke-Banner @('install', '--quiet')
    $upgraded = Wait-SessionBanner -ExceptId $restarted[0].Id
    Assert ($upgraded.Count -eq 1 -and $upgraded[0].Id -ne $restarted[0].Id) 'exactly one new banner running after upgrade'
}
Get-Content $log -ErrorAction SilentlyContinue | Write-Host
Assert (-not (Test-Path $log) -or -not (Select-String -Path $log -Pattern 'ERROR' -Quiet)) 'no errors logged'

# Issue #24: a profile that is signed out during uninstall must be restored too (its NTUSER.DAT is loaded).
$otherUser = 'cbsmoke'
$otherPassword = 'Cb!' + [guid]::NewGuid().ToString('N').Substring(0, 16) + 'x9'
$otherSid = $null
try {
    New-LocalUser -Name $otherUser -Password (ConvertTo-SecureString $otherPassword -AsPlainText -Force) -AccountNeverExpires | Out-Null
    $credential = New-Object System.Management.Automation.PSCredential(".\$otherUser", (ConvertTo-SecureString $otherPassword -AsPlainText -Force))
    # What the banner leaves in a profile: the user's original saved, classification colors live.
    $seed = 'reg add HKCU\Software\Microsoft\Windows\DWM /v ColorPrevalence /t REG_DWORD /d 1 /f & ' +
            'reg add HKCU\Software\Microsoft\Windows\DWM /v AccentColor /t REG_DWORD /d 0xFF2E10C8 /f & ' +
            'reg add "HKCU\Software\ClassBanner\Saved\Software/Microsoft/Windows/DWM" /v ColorPrevalence /t REG_DWORD /d 0 /f'
    Start-Process cmd.exe -ArgumentList "/c $seed" -Credential $credential -LoadUserProfile -Wait -WindowStyle Hidden
    $otherSid = (Get-LocalUser $otherUser).SID.Value
    for ($i = 0; $i -lt 60 -and (Test-Path "Registry::HKEY_USERS\$otherSid"); $i++) { Start-Sleep -Seconds 1 }
    if (Test-Path "Registry::HKEY_USERS\$otherSid") { Write-Host "note - the second profile's hive is still loaded; uninstall restores it in place" }
    else { Write-Host 'ok - second profile is signed out (hive unloaded), so uninstall must load NTUSER.DAT' }
} catch {
    Write-Host "note - could not create a second profile ($($_.Exception.Message)); signed-out restore not tested"
    $otherSid = $null
}

Invoke-Banner @('uninstall')
Start-Sleep -Seconds 1
Assert (-not (Test-Path $installDir)) 'install folder and settings removed'

if ($otherSid) {
    $profilePath = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$otherSid").ProfileImagePath
    $hive = "Registry::HKEY_USERS\$otherSid"
    $loadedHere = -not (Test-Path $hive)
    if ($loadedHere) { & reg.exe load 'HKU\cbsmoke' "$profilePath\NTUSER.DAT" | Out-Null; $hive = 'Registry::HKEY_USERS\cbsmoke' }
    $restored = (Get-ItemProperty "$hive\Software\Microsoft\Windows\DWM").ColorPrevalence
    $savedLeft = Test-Path "$hive\Software\ClassBanner\Saved"
    [gc]::Collect()
    if ($loadedHere) { & reg.exe unload 'HKU\cbsmoke' | Out-Null }
    Assert ($restored -eq 0 -and -not $savedLeft) 'signed-out profile restored by uninstall'
    Get-CimInstance Win32_UserProfile | Where-Object { $_.SID -eq $otherSid } | Remove-CimInstance -ErrorAction SilentlyContinue
    Remove-LocalUser -Name $otherUser -ErrorAction SilentlyContinue
}
$notice = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Assert ([string]::IsNullOrEmpty($notice.legalnoticetext)) 'sign-in notice cleared'
Assert ($null -eq (Get-Service ClassBanner -ErrorAction SilentlyContinue)) 'service removed'
if ($signedIn) {
    Assert ((Get-ItemProperty $dwmKey).ColorPrevalence -eq 0) "uninstall restored the user's own title bar setting"
    Assert (-not (Test-Path 'HKCU:\Software\ClassBanner\Saved')) 'saved values removed'
}
Assert ((Get-Banners).Count -eq 0) 'no banners left running'
Assert (-not (Test-Path $cpl)) 'removed from Control Panel'
Assert (-not (Test-Path $shortcut)) 'Start menu entry removed'

Write-Host 'Smoke test passed.'
