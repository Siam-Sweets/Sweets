[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\PosApp.Wpf\PosApp.Wpf.csproj"
$installerScript = Join-Path $repoRoot "installer\PosApp.iss"
$publishDirectory = Join-Path $repoRoot "publish"
$outputDirectory = Join-Path $repoRoot "artifacts\installer"

$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw)
$versionNode = Select-Xml -Xml $projectXml -XPath "/Project/PropertyGroup/Version" | Select-Object -First 1
$version = if ($versionNode) { $versionNode.Node.InnerText } else { $null }
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not read the application version from $projectPath."
}

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php, then run this script again."
}

Write-Host "Publishing PosApp $version for $Runtime..."
dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --property:PublishSingleFile=true `
    --property:IncludeNativeLibrariesForSelfExtract=true `
    --property:EnableCompressionInSingleFile=true `
    --property:PublishReadyToRun=false `
    --output $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

Write-Host "Building the setup wizard..."
& $iscc "/DMyAppVersion=$version" "/O$outputDirectory" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installer = Join-Path $outputDirectory "PosApp-$version-Setup.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Installer was not created at $installer."
}

Write-Host "Installer ready: $installer"
