[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-x86", "win-arm64")]
    [string]$Runtime = "win-x64",

    [switch]$SelfContained,

    [switch]$Standalone,

    [string]$Configuration = "Release",

    [string]$OutputDir
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src\Graphviz2Visio.Cli\Graphviz2Visio.Cli.csproj"
$exeName = "Graphviz2Visio.Cli.exe"

function Get-GraphvizDirectories {
    param([string]$Path)

    $preferredDirs = @(Get-ChildItem -LiteralPath $Path -Directory -Filter "Graphviz*" -ErrorAction SilentlyContinue)
    if ($preferredDirs.Count -gt 0) {
        return $preferredDirs
    }

    return @(Get-ChildItem -LiteralPath $Path -Directory -Filter "*Graphviz*" -ErrorAction SilentlyContinue |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName "bin\dot.exe")) -or
            (Test-Path -LiteralPath (Join-Path $_.FullName "dot.exe"))
        })
}

function Get-PackageGraphvizDirectoryName {
    param([string]$Name)

    if ($Name.StartsWith("Graphviz", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Name
    }

    $match = [regex]::Match($Name, "Graphviz.*", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($match.Success) {
        return $match.Value
    }

    return "Graphviz"
}

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project file not found: $project"
}

if ($Standalone) {
    $SelfContained = $true

    if ($Runtime -ne "win-x64") {
        throw "Standalone mode embeds the bundled win-x64 Graphviz package. Use -Runtime win-x64."
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $flavor = if ($Standalone) { "standalone" } elseif ($SelfContained) { "selfcontained" } else { "framework" }
    $OutputDir = Join-Path $root "publish\$Runtime-$flavor"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $root $OutputDir
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

Write-Host "Publishing $Runtime ($Configuration, self-contained: $selfContainedValue)..."
$publishArgs = @(
    "publish", $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", $selfContainedValue,
    "-o", $OutputDir
)

if ($Standalone) {
    $publishArgs += @(
        "-p:PublishSingleFile=true",
        "-p:EmbedGraphviz=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false"
    )
}

dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishedExe = Join-Path $OutputDir $exeName
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "Published exe not found: $publishedExe"
}

if ($Standalone) {
    Write-Host ""
    Write-Host "Standalone package created:"
    Write-Host "  $publishedExe"
    Write-Host ""
    Write-Host "Graphviz is embedded and will be extracted to the local app data cache on first use."
    Write-Host ""
    Write-Host "Verify with:"
    Write-Host "  $publishedExe where-dot"
    exit 0
}

$toolsDir = Join-Path $root "tools"
if (-not (Test-Path -LiteralPath $toolsDir)) {
    throw "tools directory not found: $toolsDir"
}

$graphvizDirs = @(Get-GraphvizDirectories -Path $toolsDir)

if ($graphvizDirs.Count -eq 0) {
    $graphvizZip = Get-ChildItem -LiteralPath $toolsDir -File -Filter "*Graphviz*.zip" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $graphvizZip) {
        throw "Graphviz directory or zip file not found under: $toolsDir"
    }

    Write-Host "Extracting Graphviz from $($graphvizZip.Name)..."
    Expand-Archive -LiteralPath $graphvizZip.FullName -DestinationPath $toolsDir -Force
    $graphvizDirs = @(Get-GraphvizDirectories -Path $toolsDir)
}

if ($graphvizDirs.Count -eq 0) {
    throw "Graphviz was not found after extraction."
}

$targetToolsDir = Join-Path $OutputDir "tools"
if (Test-Path -LiteralPath $targetToolsDir) {
    Remove-Item -LiteralPath $targetToolsDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $targetToolsDir | Out-Null
foreach ($dir in $graphvizDirs) {
    $packageDirName = Get-PackageGraphvizDirectoryName -Name $dir.Name
    $packageDir = Join-Path $targetToolsDir $packageDirName
    Copy-Item -LiteralPath $dir.FullName -Destination $packageDir -Recurse -Force
}

$targetDotExe = Get-ChildItem -LiteralPath $targetToolsDir -Recurse -Filter "dot.exe" -File -ErrorAction SilentlyContinue |
    Select-Object -First 1

if ($null -eq $targetDotExe) {
    throw "dot.exe was not copied into the package tools directory."
}

Write-Host ""
Write-Host "Package created:"
Write-Host "  $OutputDir"
Write-Host ""
Write-Host "Graphviz included:"
Write-Host "  $($targetDotExe.FullName)"
Write-Host ""
Write-Host "Verify with:"
Write-Host "  $publishedExe where-dot"
