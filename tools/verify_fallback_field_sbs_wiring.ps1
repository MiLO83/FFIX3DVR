param(
    [string]$SourcePath = "memoria-patch-source/FF9DepthVRFieldRenderer.cs"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $SourcePath)) {
    throw "Source file not found: $SourcePath"
}

$source = Get-Content $SourcePath -Raw

function Assert-Contains {
    param(
        [string]$Needle,
        [string]$Message
    )
    if (-not $source.Contains($Needle)) {
        throw $Message
    }
}

function Get-MethodBody {
    param([string]$Name)
    $match = [regex]::Match($source, "internal static void $Name\s*\([^\)]*\)\s*\{", [Text.RegularExpressions.RegexOptions]::Singleline)
    if (!$match.Success) {
        throw "Could not locate method $Name"
    }

    $start = $match.Index
    $depth = 0
    for ($i = $match.Index; $i -lt $source.Length; $i++) {
        if ($source[$i] -eq '{') {
            $depth++
        } elseif ($source[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) {
                return $source.Substring($start, $i - $start + 1)
            }
        }
    }
    throw "Could not parse method body for $Name"
}

Assert-Contains "EnsureFallbackFieldBridge(fieldMap)" "TryRefresh should install a fallback field bridge before returning for missing/disabled depth scenes."
Assert-Contains "public sealed class FF9DepthVRFallbackFieldBridge" "Renderer should define a fallback field bridge for vanilla/missing-depth fields."
Assert-Contains "AddComponent<FF9DepthVRSbsStereo>()" "Fallback bridge should attach the world SBS camera bridge."
Assert-Contains "Initialize(fieldMap, true)" "Fallback bridge should enable input-look steering on vanilla/missing-depth field SBS cameras."
Assert-Contains "AddComponent<FF9DepthVRSbsUiStereo>()" "Fallback bridge should attach the SBS UI camera bridge."
Assert-Contains "FF9DepthVRFieldRenderer.TryHandleSbsToggleInput()" "Fallback bridge should keep F9/F7/F8 controls active in missing-depth scenes."
Assert-Contains "eventIDToFBGID" "FindScene should resolve field map ids through Memoria's event-to-FBG table."

$scroll = Get-MethodBody "ApplyActorCameraScroll"
if ($scroll.Contains("FindScene(fieldMap) == null")) {
    throw "ApplyActorCameraScroll must not require a depth manifest scene; camera look should work in missing-depth fields."
}

$framing = Get-MethodBody "ApplyActorCameraFraming"
if ($framing.Contains("FindScene(fieldMap) == null")) {
    throw "ApplyActorCameraFraming must not require a depth manifest scene; camera look should work in missing-depth fields."
}

Write-Host "Fallback field SBS wiring markers present."
