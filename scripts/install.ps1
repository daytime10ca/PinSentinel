#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes PinSentinel and installs it as an auto-start Windows service.
.PARAMETER Uninstall
    Stops and removes the service. Logs in %ProgramData%\PinSentinel are kept.
#>
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$name = 'PinSentinel'
$installDir = Join-Path $env:ProgramFiles $name
$project = Join-Path $PSScriptRoot '..\src\PinSentinel.Service'
$trayProject = Join-Path $PSScriptRoot '..\src\PinSentinel.Tray'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\PinSentinel.lnk'

Get-Process PinSentinel.Tray -ErrorAction SilentlyContinue | Stop-Process -Force

if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-Service $name -Force -ErrorAction SilentlyContinue
    sc.exe delete $name | Out-Null
    Start-Sleep 2
}

if ($Uninstall) {
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
    if (Test-Path $shortcut) { Remove-Item $shortcut }
    Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PinSentinel -ErrorAction SilentlyContinue
    Write-Host "$name removed."
    return
}

# Keep an edited config across upgrades.
$settings = Join-Path $installDir 'appsettings.json'
$saved = if (Test-Path $settings) { Get-Content $settings -Raw }

dotnet publish $project -c Release -r win-x64 --self-contained false -o $installDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
if ($saved) { Set-Content $settings $saved -NoNewline }

dotnet publish $trayProject -c Release -r win-x64 --self-contained false -o (Join-Path $installDir 'Tray')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (tray) failed' }
$link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$link.TargetPath = Join-Path $installDir 'Tray\PinSentinel.Tray.exe'
$link.Save()

New-Service -Name $name -BinaryPathName "`"$installDir\PinSentinel.Service.exe`"" -DisplayName 'PinSentinel GPU connector guard' `
    -Description 'Monitors per-pin 12V-2x6 current on the ROG Astral and throttles or shuts down on a fault.' -StartupType Automatic | Out-Null
sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null
Start-Service $name

Start-Sleep 5
Get-Service $name | Format-Table Name, Status, StartType
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $name } -MaxEvents 5 -ErrorAction SilentlyContinue |
    Format-List TimeCreated, LevelDisplayName, Message
Write-Host "Config: $settings (DryRun is true until you change it; restart the service after editing)"
Write-Host 'Tray app: start PinSentinel from the Start menu, then tick Start with Windows in its right-click menu.'
