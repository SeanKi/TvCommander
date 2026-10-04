# TvCommander build script
#   .\build.ps1                    Release build (C++ FmCore + WPF app)
#   .\build.ps1 -Configuration Debug
#   .\build.ps1 -Publish           self-contained publish to .\publish (no .NET install needed)
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

$proj = "$root\src\TvCommander\TvCommander.csproj"
if ($Publish) {
    Write-Host "== TvCommander publish (self-contained, win-x64)" -ForegroundColor Cyan
    dotnet publish $proj -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -o "$root\publish" --nologo
} else {
    Write-Host "== TvCommander build $Configuration" -ForegroundColor Cyan
    dotnet build $proj -c $Configuration --nologo -v minimal
}
if ($LASTEXITCODE) { throw 'TvCommander build failed' }
