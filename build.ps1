# Builds the virtual camera (C++) and the app. Needs Visual Studio 2022 with the C++ workload.
#   .\build.ps1              debug build of both
#   .\build.ps1 -Publish     self-contained release in artifacts\ (zip + .sha256)
param(
    [string]$Configuration = "Debug",
    [switch]$Publish
)
$ErrorActionPreference = "Stop"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "Visual Studio with the C++ workload was not found." }

& $msbuild "$PSScriptRoot\src\Kadraj.Camera\Kadraj.Camera.vcxproj" -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$project = "$PSScriptRoot\src\Kadraj.App\Kadraj.App.csproj"
if (-not $Publish) {
    dotnet build $project -c $Configuration
    exit $LASTEXITCODE
}

# Release: .NET runtime included, so users need nothing else installed.
[xml]$csproj = Get-Content $project
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$artifacts = "$PSScriptRoot\artifacts"
$folder = "$artifacts\Kadraj"
Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish $project -c Release -r win-x64 --self-contained -o $folder -p:DebugType=none
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Copy-Item "$PSScriptRoot\LICENSE", "$PSScriptRoot\THIRD-PARTY-NOTICES.md" $folder

# Stable file name, so "releases/latest/download/Kadraj-win-x64.zip" always works.
$zip = "$artifacts\Kadraj-win-x64.zip"
Compress-Archive -Path $folder -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ascii -NoNewline
Write-Host "Built Kadraj $version -> $zip"
Write-Host "SHA-256 $hash"
