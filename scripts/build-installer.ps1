<#
.SYNOPSIS
    Builds dist\PinSentinelSetup-<version>.exe: one file that installs the service and tray app.
    The result needs the .NET 10 Desktop Runtime on the target PC; Windows offers the download if it is missing.
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$stage = Join-Path $root 'dist\stage'
$payload = Join-Path $root 'src\PinSentinel.Setup\payload.zip'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version

if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
dotnet publish (Join-Path $root 'src\PinSentinel.Service') -c Release -r win-x64 --self-contained false -o $stage
if ($LASTEXITCODE -ne 0) { throw 'publish (service) failed' }
dotnet publish (Join-Path $root 'src\PinSentinel.Tray') -c Release -r win-x64 --self-contained false -o (Join-Path $stage 'Tray')
if ($LASTEXITCODE -ne 0) { throw 'publish (tray) failed' }
Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item

if (Test-Path $payload) { Remove-Item -LiteralPath $payload }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $payload -CompressionLevel Optimal

$out = Join-Path $root 'dist\setup'
dotnet publish (Join-Path $root 'src\PinSentinel.Setup') -c Release -o $out -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'publish (setup) failed' }

$installer = Join-Path $root "dist\PinSentinelSetup-$version.exe"
Copy-Item (Join-Path $out 'PinSentinelSetup.exe') $installer -Force
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash
"{0}  ({1:N1} MB)`nSHA256 {2}" -f $installer, ((Get-Item $installer).Length / 1MB), $hash
