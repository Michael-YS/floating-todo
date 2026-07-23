param(
    [string]$Version,
    [switch]$SkipRestore
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repositoryRoot "FloatingTodo.csproj"
$artifactRoot = Join-Path $repositoryRoot "artifacts"
$publishRoot = Join-Path $artifactRoot "publish"
$releaseRoot = Join-Path $artifactRoot "releases"
$portableOutput = Join-Path $publishRoot "portable"
$slimOutput = Join-Path $publishRoot "slim"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content -Raw -Encoding UTF8 $projectPath
    $Version = @($project.Project.PropertyGroup.Version) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Unable to determine the package version."
}

$localDotnet = Join-Path $repositoryRoot ".dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

$artifactPrefix = [System.IO.Path]::GetFullPath($artifactRoot) + [System.IO.Path]::DirectorySeparatorChar
foreach ($target in @($publishRoot, $releaseRoot)) {
    $resolvedTarget = [System.IO.Path]::GetFullPath($target)
    if (-not $resolvedTarget.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the artifact directory: $resolvedTarget"
    }

    if (Test-Path -LiteralPath $resolvedTarget) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}

New-Item -ItemType Directory -Force -Path $portableOutput, $slimOutput, $releaseRoot | Out-Null

if (-not $SkipRestore) {
    & $dotnet restore $projectPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed."
    }
}

function Invoke-Publish([string]$Profile, [string]$OutputPath) {
    $arguments = @(
        "publish",
        $projectPath,
        "-c", "Release",
        "-r", "win-x64",
        "-p:PublishProfile=$Profile",
        "-p:Version=$Version",
        "-p:PublishDir=$OutputPath\"
    )
    if ($SkipRestore) {
        $arguments += "--no-restore"
    }

    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Profile publish failed."
    }
}

Invoke-Publish "Portable" $portableOutput
Invoke-Publish "Slim" $slimOutput

$portableZip = Join-Path $releaseRoot "FloatingTodo-v$Version-win-x64-portable.zip"
$slimZip = Join-Path $releaseRoot "FloatingTodo-v$Version-win-x64-slim.zip"
Compress-Archive -Path (Join-Path $portableOutput "*") -DestinationPath $portableZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $slimOutput "*") -DestinationPath $slimZip -CompressionLevel Optimal

Get-Item -LiteralPath $portableZip, $slimZip |
    Select-Object FullName, @{ Name = "SizeMB"; Expression = { [Math]::Round($_.Length / 1MB, 2) } }
