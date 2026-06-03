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
    'private\s+static\s+Boolean\s+IsFieldMovieKey\(String\s+movieKey\)[\s\S]*StartsWith\("mbg"' `
    "Movie routing must distinguish field MBG movies from standalone FMVs by movie key."

Assert-SourceContains `
    'if\s*\(fieldMap\s*!=\s*null\s*&&\s*HasDepthReplacement\(fieldMap\)\s*&&\s*IsFieldMovieKey\(movieMaterial\.movieKey\)\)' `
    "Standalone FMV keys must bypass the field movie branch even when a FieldMap exists."

Assert-SourceContains `
    'Field movie BGPlate disabled; native movie remains active[\s\S]*BeginNativeMoviePlaneSbs\(nativeMoviePlane,\s*false\)' `
    "Native field MBG playback should not half-scale the movie plane; field/SBS cameras own the viewport."

Assert-SourceContains `
    'BeginNativeMovieFallback\(movieMaterial,\s*nativeMoviePlane,\s*"Standalone movie BGPlate fallback",\s*movieDepthReason,\s*true\)' `
    "Standalone native FMV fallback should still apply SBS half-width plane scaling."

Assert-SourceContains `
    'BeginNativeMovieFallback\(movieMaterial,\s*nativeMoviePlane,\s*"Field movie BGPlate fallback",\s*movieDepthReason,\s*false\)' `
    "Field native MBG fallback should preserve the native field movie plane scale."

Assert-SourceContains `
    'Initialize\(GameObject\s+nativeMoviePlane,\s*Boolean\s+scaleForSbs\)' `
    "Native movie SBS scaler needs an explicit scale mode."

Assert-SourceContains `
    'FF9DepthVRFieldRenderer\.SbsActive\s*&&\s*_scaleForSbs' `
    "Native movie scaler should only change localScale when explicitly requested."

Write-Host "Movie routing and native field scale verifier passed."
