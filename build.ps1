# MP-Commander (Multi-Pane Commander) build script
#   .\build.ps1                    Release build (C++ FmCore + WPF app, .NET Framework 4.7.2)
#   .\build.ps1 -Configuration Debug
#   .\build.ps1 -Publish           copy the runnable files to .\publish and make a release zip (~0.2 MB)
#                                  No install needed on Windows 10 1803+ / LTSC 2019 / Windows 11
#                                  (.NET Framework 4.7.2 is built in)
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild with C++ tools (VC.Tools.x86.x64) not found.' }

Write-Host "== FmCore (C++) $Configuration" -ForegroundColor Cyan
& $msbuild "$root\src\FmCore\FmCore.vcxproj" /p:Configuration=$Configuration /p:Platform=x64 /m /nologo /v:minimal
if ($LASTEXITCODE) { throw 'FmCore build failed' }

$proj = "$root\src\MPCommander\MPCommander.csproj"
Write-Host "== MP-Commander build $Configuration (net472)" -ForegroundColor Cyan
dotnet build $proj -c $Configuration --nologo -v minimal
if ($LASTEXITCODE) { throw 'MP-Commander build failed' }

if ($Publish) {
    $out = "$root\publish"
    Write-Host "== publish -> $out" -ForegroundColor Cyan
    if (Test-Path $out) { Remove-Item "$out\*" -Recurse -Force }
    New-Item -ItemType Directory -Force $out | Out-Null
    $bin = "$root\src\MPCommander\bin\$Configuration\net472"
    Copy-Item "$bin\MP-Commander.exe", "$bin\MP-Commander.exe.config", "$bin\FmCore.dll" $out

    # 릴리스용 zip: MP-Commander-<version>-win-x64.zip
    $version = (Get-Item "$out\MP-Commander.exe").VersionInfo.ProductVersion -replace '\+.*$', ''
    $zip = "$root\MP-Commander-$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$out\*" -DestinationPath $zip
    Get-ChildItem $out, $zip | Format-Table Name, Length -AutoSize
}
