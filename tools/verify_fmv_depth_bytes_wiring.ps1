param(
    [string]$SourcePath = "memoria-patch-source/FF9DepthVRFieldRenderer.cs"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$source = Join-Path $root $SourcePath
if (-not (Test-Path $source)) {
    throw "Missing source file: $source"
}

$text = Get-Content -LiteralPath $source -Raw

function Assert-Contains([string]$needle, [string]$message) {
    if (-not $text.Contains($needle)) {
        throw $message
    }
}

Assert-Contains "DepthMoviePath" "FMV depth resolver must expose a depth .bytes path."
Assert-Contains "_depth.bytes" "FMV depth resolver must search for generated *_depth.bytes files."
Assert-Contains "FF9DepthVRTheoraDepthStream" "FMV BGPlate must include a Theora depth stream reader."
Assert-Contains "TryLoadDepthMovieFrame" "FMV BGPlate must prefer the depth .bytes frame path."
Assert-Contains "_movieMaterial.Material" "FMV BGPlate must reuse the native color MovieMaterial instead of requiring color PNG frames."
Assert-Contains "missing depth movie bytes or depth_frames directory" "Fallback reason should allow either depth .bytes or frame directories."

Write-Host "FMV depth .bytes wiring markers present in $SourcePath"
