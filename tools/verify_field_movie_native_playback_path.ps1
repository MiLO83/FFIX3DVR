param(
    [string]$SourcePath = "memoria-patch-source/FF9DepthVRFieldRenderer.cs"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path $root $SourcePath
if (!(Test-Path -LiteralPath $path)) {
    throw "Missing source file: $path"
}

$source = Get-Content -LiteralPath $path -Raw

function Assert-SourceContains {
    param(
        [string]$Pattern,
        [string]$Message
    )

    if ($source -notmatch $Pattern) {
        throw $Message
    }
}

Assert-SourceContains `
    'private\s+static\s+readonly\s+Boolean\s+EnableFieldMovieDepthPlate\s*=\s*false\s*;' `
    "Field movie depth BGPlate path should be disabled until the color/depth handoff is stable."

Assert-SourceContains `
    'Field movie BGPlate disabled; native movie remains active[\s\S]*_activeMovieFieldMap\s*=\s*fieldMap\s*;[\s\S]*SetDepthReplacementVisible\(fieldMap,\s*false\)\s*;[\s\S]*BeginNativeMoviePlaneSbs\(nativeMoviePlane,\s*false\)\s*;' `
    "Disabled field movie path should track the active field, hide the depth replacement, and start native SBS scaling."

Assert-SourceContains `
    'internal\s+static\s+void\s+EndFieldMoviePlate[\s\S]*SetDepthReplacementVisible\(target,\s*true\)\s*;' `
    "Field movie stop path should restore the depth replacement plate."

Write-Host "Field movie native playback path verifier passed."
