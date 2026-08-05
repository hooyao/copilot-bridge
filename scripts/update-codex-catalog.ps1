#Requires -Version 7.0

<#
.SYNOPSIS
Fetches and validates a reviewed openai/codex model-catalog snapshot.

.DESCRIPTION
The target directory's provenance.json is the review boundary. The requested
tag must belong to exactly the same Codex minor-version interval recorded in
that manifest. A new interval therefore starts with a separately reviewed
catalog directory and provenance manifest; this script never widens an existing
interval by inference.

Without -Update, the command checks that the remote tag, vendored bytes,
provenance hashes, schema keys, required models, and instruction sources agree.
With -Update, it replaces models.json and the upstream LICENSE after all checks
pass, then updates the pinned tag, commit, and hashes in provenance.json.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^rust-v\d+\.\d+\.\d+$')]
    [string] $Tag,

    [Parameter()]
    [string] $CatalogDirectory = (Join-Path $PSScriptRoot '..\src\CopilotBridge.Cli\Catalogs\Codex\0.144'),

    [Parameter()]
    [switch] $Update
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredSlugs = @(
    'gpt-5.4',
    'gpt-5.5',
    'gpt-5.6-luna',
    'gpt-5.6-sol',
    'gpt-5.6-terra'
)

function Get-Sha256([string] $Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-CatalogKeys($Catalog) {
    @(
        $Catalog.models |
            ForEach-Object { $_.PSObject.Properties.Name } |
            Sort-Object -Unique
    )
}

function Assert-Catalog($Catalog, [string] $Source) {
    if ($null -eq $Catalog.models -or $Catalog.models.Count -eq 0) {
        throw "$Source does not contain a non-empty top-level models array."
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($model in $Catalog.models) {
        if ([string]::IsNullOrWhiteSpace($model.slug)) {
            throw "$Source contains a model without a non-empty slug."
        }
        if (-not $seen.Add([string] $model.slug)) {
            throw "$Source contains duplicate slug '$($model.slug)'."
        }
        if ([string]::IsNullOrWhiteSpace($model.base_instructions)) {
            throw "$Source model '$($model.slug)' has no non-empty base_instructions source."
        }
    }

    foreach ($slug in $requiredSlugs) {
        if (-not $seen.Contains($slug)) {
            throw "$Source is missing required reviewed model '$slug'."
        }
    }
}

$catalogRoot = [IO.Path]::GetFullPath($CatalogDirectory)
$manifestPath = Join-Path $catalogRoot 'provenance.json'
$modelsPath = Join-Path $catalogRoot 'models.json'
$licensePath = Join-Path $catalogRoot 'LICENSE.openai-codex'

foreach ($path in @($manifestPath, $modelsPath, $licensePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required reviewed catalog asset is missing: $path"
    }
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.schema_version -ne 1) {
    throw "Unsupported provenance schema_version '$($manifest.schema_version)'."
}
if ($manifest.source_repository -ne 'https://github.com/openai/codex' -or
    $manifest.source_path -ne 'codex-rs/models-manager/models.json' -or
    $manifest.source_license -ne 'Apache-2.0') {
    throw 'The provenance manifest does not identify the reviewed openai/codex source contract.'
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = [string] $manifest.source_tag
}
$match = [regex]::Match($Tag, '^rust-v(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$')
if (-not $match.Success) {
    throw "Tag '$Tag' is not a release tag of the form rust-v<major>.<minor>.<patch>."
}

$major = [int] $match.Groups['major'].Value
$minor = [int] $match.Groups['minor'].Value
$expectedMinimum = "$major.$minor.0"
$expectedMaximum = "$major.$($minor + 1).0"
$reviewedMinimum = [string] $manifest.supported_client_version.minimum_inclusive
$reviewedMaximum = [string] $manifest.supported_client_version.maximum_exclusive
if ($reviewedMinimum -ne $expectedMinimum -or $reviewedMaximum -ne $expectedMaximum) {
    throw "Refusing unreviewed client interval [$reviewedMinimum, $reviewedMaximum) for $Tag; expected the exact release-family interval [$expectedMinimum, $expectedMaximum). Add and review a new catalog directory/provenance manifest first."
}

$remoteRefs = @(git ls-remote --tags $manifest.source_repository "refs/tags/$Tag" "refs/tags/$Tag^{}")
if ($LASTEXITCODE -ne 0) {
    throw "git ls-remote failed for $Tag."
}
$peeled = $remoteRefs | Where-Object { $_ -match "refs/tags/$([regex]::Escape($Tag))\^\{\}$" } | Select-Object -First 1
$direct = $remoteRefs | Where-Object { $_ -match "refs/tags/$([regex]::Escape($Tag))$" } | Select-Object -First 1
$commitLine = if ($null -ne $peeled) { $peeled } else { $direct }
if ($null -eq $commitLine) {
    throw "Remote tag '$Tag' was not found."
}
$sourceCommit = ($commitLine -split '\s+')[0].ToLowerInvariant()
if ($sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Could not resolve '$Tag' to a 40-character commit."
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("copilot-bridge-codex-catalog-" + [Guid]::NewGuid().ToString('N'))
$resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
if (-not $resolvedTempRoot.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unexpected temporary directory '$resolvedTempRoot'."
}

New-Item -ItemType Directory -Path $resolvedTempRoot | Out-Null
try {
    $downloadedModels = Join-Path $resolvedTempRoot 'models.json'
    $downloadedLicense = Join-Path $resolvedTempRoot 'LICENSE'
    $rawRoot = "https://raw.githubusercontent.com/openai/codex/$Tag"
    Invoke-WebRequest -Uri "$rawRoot/codex-rs/models-manager/models.json" -OutFile $downloadedModels
    Invoke-WebRequest -Uri "$rawRoot/LICENSE" -OutFile $downloadedLicense

    $currentCatalog = Get-Content -Raw -LiteralPath $modelsPath | ConvertFrom-Json
    $downloadedCatalog = Get-Content -Raw -LiteralPath $downloadedModels | ConvertFrom-Json
    Assert-Catalog $currentCatalog 'Vendored catalog'
    Assert-Catalog $downloadedCatalog "Upstream $Tag catalog"

    $currentKeys = Get-CatalogKeys $currentCatalog
    $downloadedKeys = Get-CatalogKeys $downloadedCatalog
    $addedKeys = @($downloadedKeys | Where-Object { $_ -notin $currentKeys })
    $removedKeys = @($currentKeys | Where-Object { $_ -notin $downloadedKeys })
    if ($addedKeys.Count -gt 0 -or $removedKeys.Count -gt 0) {
        Write-Warning "Catalog key drift detected. Added: [$($addedKeys -join ', ')]; removed: [$($removedKeys -join ', ')]."
        throw 'Refusing schema/key drift until the Codex consumer contract and baseline projection are reviewed.'
    }

    $downloadedModelsHash = Get-Sha256 $downloadedModels
    $downloadedLicenseHash = Get-Sha256 $downloadedLicense
    if ($Update) {
        Copy-Item -LiteralPath $downloadedModels -Destination $modelsPath -Force
        Copy-Item -LiteralPath $downloadedLicense -Destination $licensePath -Force
        $manifest.source_tag = $Tag
        $manifest.source_commit = $sourceCommit
        $manifest.models_sha256 = $downloadedModelsHash
        $manifest.license_sha256 = $downloadedLicenseHash
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
        Write-Output "Updated reviewed Codex catalog $Tag ($sourceCommit) for [$reviewedMinimum, $reviewedMaximum)."
    }
    else {
        $failures = [System.Collections.Generic.List[string]]::new()
        if ([string] $manifest.source_tag -ne $Tag) { $failures.Add("manifest tag '$($manifest.source_tag)' != '$Tag'") }
        if ([string] $manifest.source_commit -ne $sourceCommit) { $failures.Add("manifest commit '$($manifest.source_commit)' != '$sourceCommit'") }
        if ((Get-Sha256 $modelsPath) -ne [string] $manifest.models_sha256) { $failures.Add('vendored models hash != manifest') }
        if ((Get-Sha256 $licensePath) -ne [string] $manifest.license_sha256) { $failures.Add('vendored license hash != manifest') }
        if ($downloadedModelsHash -ne [string] $manifest.models_sha256) { $failures.Add('upstream models hash != manifest') }
        if ($downloadedLicenseHash -ne [string] $manifest.license_sha256) { $failures.Add('upstream license hash != manifest') }
        if ($failures.Count -gt 0) {
            throw "Catalog check failed: $($failures -join '; ')."
        }
        Write-Output "Verified reviewed Codex catalog $Tag ($sourceCommit): $($downloadedCatalog.models.Count) complete entries, $($downloadedKeys.Count) model keys, interval [$reviewedMinimum, $reviewedMaximum)."
    }
}
finally {
    if (Test-Path -LiteralPath $resolvedTempRoot) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
}
