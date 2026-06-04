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

function Assert-Matches([string]$pattern, [string]$message) {
    if ($text -notmatch $pattern) {
        throw $message
    }
}

Assert-Contains "_hasDepthSurfaceFrame" "Standalone FMVs need an explicit depth-surface state."
Assert-Contains "SetCenteredPlateCoordinates" "Standalone camera-space FMV meshes need centered plate coordinate handling."
Assert-Contains "ApplyStandaloneDepthSurfaceState" "Standalone FMVs need a depth-mesh/native-plane state switch."
Assert-Contains "_parallax.Initialize(_mesh" "Standalone FMV depth meshes must attach FF9DepthVRParallax."
Assert-Contains "StandaloneMovieParallaxScale" "Standalone FMV parallax must convert field pixel offsets into movie plate units."
Assert-Contains "TryCalculateNativeMoviePlaneFrame" "Standalone FMV depth meshes should derive sane bgplate coordinates from the native movie plane when possible."
Assert-Contains "CalculateFieldMoviePlateSize" "Field FMVs should reuse the active depth BGPlate dimensions instead of raw movie pixels."
Assert-Contains "MovieCameraNearZ" "Standalone FMV depth meshes must guard against near-plane clipping."
Assert-Contains "Mathf.Clamp(cameraFrame.CenterZ + z" "Standalone FMV depth displacement must stay inside the camera clip range."
Assert-Contains "_meshRenderer.enabled = true;" "Depth-available FMVs must enable the mesh renderer in SBS."
Assert-Contains "HideNativeMoviePlane();" "Depth-available FMVs must hide the native flat plane."
Assert-Matches "MovieStandaloneDepthScale\s*=>\s*0\.0?6f\s*;" "Standalone FMV depth scale should stay softened at 0.06f for the alpha."

Write-Host "Standalone FMV depth mesh wiring markers present in $SourcePath"
