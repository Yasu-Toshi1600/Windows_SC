[CmdletBinding()]
param(
    [ValidateSet("All", "Folder", "SingleFile")]
    [string]$Format = "All"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repositoryRoot "Windows_SC\Windows_SC.csproj"
$versionPath = Join-Path $repositoryRoot "Version.props"
$readmeTemplatePath = Join-Path $repositoryRoot "distribution\README.txt"
$artifactsRoot = Join-Path $repositoryRoot "artifacts\distribution"
$stagingRoot = Join-Path $artifactsRoot "staging"

[xml]$versionDocument = Get-Content -LiteralPath $versionPath -Raw -Encoding utf8
$versionPrefix = [string]$versionDocument.Project.PropertyGroup.VersionPrefix
$versionSuffix = [string]$versionDocument.Project.PropertyGroup.VersionSuffix
$displayVersion = if ([string]::IsNullOrWhiteSpace($versionSuffix)) {
    $versionPrefix
}
else {
    "$versionPrefix-$versionSuffix"
}

$resolvedArtifactsRoot = [System.IO.Path]::GetFullPath($artifactsRoot)
if (-not $resolvedArtifactsRoot.StartsWith(
        [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts")),
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Artifacts path escaped the repository: $resolvedArtifactsRoot"
}

if (Test-Path -LiteralPath $artifactsRoot) {
    Remove-Item -LiteralPath $artifactsRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

$readmeText = (Get-Content -LiteralPath $readmeTemplatePath -Raw -Encoding utf8).Replace(
    "{{VERSION}}",
    $displayVersion)
$results = [System.Collections.Generic.List[object]]::new()

function Remove-UnusedLanguageDirectories {
    param(
        [Parameter(Mandatory)]
        [string]$PublishDirectory
    )

    $allowedCultures = @("en", "en-US", "ja", "ja-JP")
    foreach ($directory in Get-ChildItem -LiteralPath $PublishDirectory -Directory) {
        try {
            $culture = [System.Globalization.CultureInfo]::GetCultureInfo($directory.Name)
        }
        catch [System.Globalization.CultureNotFoundException] {
            continue
        }

        if ($culture.Name -notin $allowedCultures) {
            Remove-Item -LiteralPath $directory.FullName -Recurse -Force
        }
    }
}

if ($Format -in @("All", "Folder")) {
    $folderName = "Windows_SC-$displayVersion-x64"
    $folderStage = Join-Path $stagingRoot $folderName
    $folderZip = Join-Path $artifactsRoot "$folderName.zip"

    & dotnet publish $projectPath `
        -c Release `
        --no-restore `
        -p:Platform=x64 `
        -p:PublishProfile=win-x64-folder.pubxml `
        -p:PublishDir="$folderStage\"
    if ($LASTEXITCODE -ne 0) {
        throw "Folder publish failed with exit code $LASTEXITCODE."
    }

    Remove-UnusedLanguageDirectories -PublishDirectory $folderStage
    Set-Content -LiteralPath (Join-Path $folderStage "README.txt") `
        -Value $readmeText `
        -Encoding utf8
    Compress-Archive -LiteralPath $folderStage -DestinationPath $folderZip -CompressionLevel Optimal

    $folderFiles = Get-ChildItem -LiteralPath $folderStage -Recurse -File
    $results.Add([pscustomobject]@{
        Format = "Folder ZIP"
        Path = $folderZip
        Files = $folderFiles.Count
        UnpackedMB = [math]::Round(
            (($folderFiles | Measure-Object Length -Sum).Sum / 1MB),
            1)
        PackageMB = [math]::Round((Get-Item -LiteralPath $folderZip).Length / 1MB, 1)
        SHA256 = (Get-FileHash -LiteralPath $folderZip -Algorithm SHA256).Hash
    })
}

if ($Format -in @("All", "SingleFile")) {
    $singlePublish = Join-Path $stagingRoot "single-file"
    $singleAsset = Join-Path $artifactsRoot "Windows_SC-$displayVersion-x64-single.exe"

    & dotnet publish $projectPath `
        -c Release `
        --no-restore `
        -p:Platform=x64 `
        -p:PublishProfile=win-x64-singlefile.pubxml `
        -p:PublishDir="$singlePublish\"
    if ($LASTEXITCODE -ne 0) {
        throw "Single-file publish failed with exit code $LASTEXITCODE."
    }

    Remove-UnusedLanguageDirectories -PublishDirectory $singlePublish
    $singleExecutable = Join-Path $singlePublish "Windows_SC.exe"
    if (-not (Test-Path -LiteralPath $singleExecutable)) {
        throw "Single-file publish did not produce Windows_SC.exe."
    }

    Copy-Item -LiteralPath $singleExecutable -Destination $singleAsset
    $singleOutputFiles = Get-ChildItem -LiteralPath $singlePublish -File
    $unexpectedFiles = @($singleOutputFiles | Where-Object Name -ne "Windows_SC.exe")
    if ($unexpectedFiles.Count -gt 0) {
        $unexpectedNames = $unexpectedFiles.Name -join ", "
        throw "Single-file publish left additional files: $unexpectedNames"
    }

    $results.Add([pscustomobject]@{
        Format = "Single EXE"
        Path = $singleAsset
        Files = 1
        UnpackedMB = "-"
        PackageMB = [math]::Round((Get-Item -LiteralPath $singleAsset).Length / 1MB, 1)
        SHA256 = (Get-FileHash -LiteralPath $singleAsset -Algorithm SHA256).Hash
    })
}

Remove-Item -LiteralPath $stagingRoot -Recurse -Force
$results | Format-Table -AutoSize
