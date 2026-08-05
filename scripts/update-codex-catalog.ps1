#Requires -Version 7.0

<#
.SYNOPSIS
Captures an exact official Codex models.json as a test fixture.

.DESCRIPTION
The bridge no longer embeds runtime catalog baselines. Production resolves the
complete client version on demand from the exact official rust-v{version} tag
and persists validated bytes in the per-user cache.

This script exists only to refresh explicit contract-test fixtures. It accepts a
complete stable or prerelease client version, downloads exactly that tag, checks
the catalog shape used by the bridge, and writes models.json plus capture.json
under tests/Fixtures/Codex/rust-v{version}. It never searches a neighboring tag
or modifies src/CopilotBridge.Cli.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string] $ClientVersion,

    [Parameter()]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
            throw "$Source model '$($model.slug)' has no base_instructions."
        }
        foreach ($required in @('context_window', 'max_context_window', 'auto_compact_token_limit', 'supported_in_api', 'visibility')) {
            if ($model.PSObject.Properties.Name -notcontains $required) {
                throw "$Source model '$($model.slug)' is missing '$required'."
            }
        }
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'tests\Fixtures\Codex'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $fixtureRoot "rust-v$ClientVersion"
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $outputRoot.StartsWith($fixtureRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must resolve beneath the Codex fixture root: $fixtureRoot"
}

$sourceUrl = "https://raw.githubusercontent.com/openai/codex/rust-v$ClientVersion/codex-rs/models-manager/models.json"
$tempPath = Join-Path ([IO.Path]::GetTempPath()) ("codex-models-" + [Guid]::NewGuid().ToString('N') + '.json')
try {
    $response = Invoke-WebRequest -Uri $sourceUrl -OutFile $tempPath -PassThru -TimeoutSec 30
    if ($response.StatusCode -ne 200) {
        throw "Exact source returned HTTP $($response.StatusCode): $sourceUrl"
    }
    if ((Get-Item -LiteralPath $tempPath).Length -gt 16MB) {
        throw 'Exact source exceeds the maximum supported fixture size (16 MiB).'
    }

    $catalog = Get-Content -Raw -LiteralPath $tempPath | ConvertFrom-Json -Depth 100
    Assert-Catalog $catalog "Official rust-v$ClientVersion catalog"
    $sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $tempPath).Hash.ToLowerInvariant()
    $capture = [ordered]@{
        schema_version = 1
        client_version = $ClientVersion
        source_url = $sourceUrl
        source_etag = [string] $response.Headers.ETag
        sha256 = $sha256
        captured_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
    }

    if ($PSCmdlet.ShouldProcess($outputRoot, "replace exact Codex test fixture rust-v$ClientVersion")) {
        New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
        Copy-Item -LiteralPath $tempPath -Destination (Join-Path $outputRoot 'models.json') -Force
        $capture | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'capture.json') -Encoding utf8NoBOM
    }
    Write-Output "Captured rust-v${ClientVersion}: $($catalog.models.Count) models, sha256=$sha256"
}
finally {
    if (Test-Path -LiteralPath $tempPath) {
        [IO.File]::Delete($tempPath)
    }
}
