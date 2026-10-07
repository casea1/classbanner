<#
.SYNOPSIS
  End-to-end check of a published BetterBanner.exe on a real Windows machine (used by CI; needs admin).
  Installs, verifies files/registry/settings, runs the banner briefly, then uninstalls and verifies cleanup.
#>
param([Parameter(Mandatory)] [string] $Exe)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:ProgramFiles 'BetterBanner'
$settingsFile = Join-Path $env:ProgramData 'BetterBanner\settings.json'
$log = Join-Path $env:LOCALAPPDATA 'BetterBanner\BetterBanner.log'

function Invoke-Banner([string[]] $Arguments) {
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) { throw "BetterBanner $($Arguments -join ' ') exited with $($p.ExitCode)" }
}
function Assert($Condition, [string] $Message) { if (-not $Condition) { throw "FAILED: $Message" } else { Write-Host "ok - $Message" } }

$size = (Get-Item $Exe).Length / 1MB
Write-Host ("BetterBanner.exe is {0:N2} MB" -f $size)
Assert ($size -lt 15) 'exe is small'

Invoke-Banner @('version')
Invoke-Banner @('install', '--level', 'SECRET', '--caption', 'SECRET//NOFORN', '--logon-title', 'Notice',
                '--logon-text', 'Authorized use only', '--set', 'ClockEnabled=1', '--no-start')

Assert (Test-Path "$installDir\BetterBanner.exe") 'exe installed'
Assert (Test-Path "$installDir\BetterBanner.scr") 'screen saver installed'
$run = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
Assert ($run.BetterBanner -like '*BetterBanner.exe*') 'starts at sign-in'
Assert (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BetterBanner') 'listed in Apps & features'
$notice = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Assert ($notice.legalnoticetext -eq 'Authorized use only') 'sign-in notice set'
$settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
Assert ($settings.Level -eq 'Secret' -and $settings.Caption -eq 'SECRET//NOFORN') 'settings saved'

Invoke-Banner @('show')

if (Test-Path $log) { Remove-Item $log }
$banner = Start-Process -FilePath "$installDir\BetterBanner.exe" -PassThru
Start-Sleep -Seconds 6
Assert (-not $banner.HasExited) 'banner keeps running'
$second = Start-Process -FilePath "$installDir\BetterBanner.exe" -PassThru
Assert ($second.WaitForExit(5000)) 'second instance exits (single instance per session)'
Stop-Process -Id $banner.Id
Get-Content $log | Write-Host
Assert (-not (Select-String -Path $log -Pattern 'ERROR' -Quiet)) 'no errors logged'

Invoke-Banner @('uninstall')
Start-Sleep -Seconds 1
Assert (-not (Test-Path $installDir)) 'install folder removed'
Assert (-not (Test-Path $settingsFile)) 'settings removed'
$notice = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Assert ([string]::IsNullOrEmpty($notice.legalnoticetext)) 'sign-in notice cleared'
Assert ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run').BetterBanner) 'no longer starts at sign-in'

Write-Host 'Smoke test passed.'
