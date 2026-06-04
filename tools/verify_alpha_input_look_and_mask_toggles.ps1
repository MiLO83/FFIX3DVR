param(
    [string]$SourcePath = "memoria-patch-source/FF9DepthVRFieldRenderer.cs"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $SourcePath)) {
    throw "Source file not found: $SourcePath"
}

$source = Get-Content $SourcePath -Raw

function Assert-Matches {
    param(
        [string]$Pattern,
        [string]$Message
    )
    if ($source -notmatch $Pattern) {
        throw $Message
    }
}

function Assert-NotMatches {
    param(
        [string]$Pattern,
        [string]$Message
    )
    if ($source -match $Pattern) {
        throw $Message
    }
}

Assert-Matches "public\s+static\s+Boolean\s+ActorLookEnabled\s*=\s*true\s*;" "Actor-average look assist should default on for alpha builds."
Assert-Matches "internal\s+static\s+Boolean\s+ForegroundMaskDepthWarpEnabled\s*=>\s*true\s*;" "Foreground mask depth warp should be fixed on, not toggled at runtime."
Assert-NotMatches "KeyCode\.F6" "F6 should no longer be bound to any depth/mask toggle."
Assert-NotMatches "\(F6\)" "The status overlay should not advertise an F6 toggle."
Assert-NotMatches "TryHandleForegroundMaskWarpToggleInput" "The foreground-mask F6 toggle handler should be removed."
Assert-Matches "internal\s+static\s+Vector2\s+InputLookNormalized\s*\(" "Shared input look should be exposed for non-field renderers."
Assert-Matches "internal\s+static\s+Vector2\s+ApplyHeadTrackLook\s*\(" "Shared input look should include VR head-tracking."
Assert-Matches "FF9DepthVRFieldRenderer\.InputLookNormalized\s*\(\s*true\s*\)" "Battle stereo should read the shared mouse/right-stick/head look vector."
Assert-Matches "Quaternion\.Euler\s*\([^;]*look\.y\s*\*\s*FF9DepthVRFieldRenderer\.ViewAngleMultiplier[^;]*look\.x\s*\*\s*FF9DepthVRFieldRenderer\.ViewAngleMultiplier\s*\*\s*FF9DepthVRFieldRenderer\.ViewAngleXMultiplier" "Battle stereo should turn the shared look vector into an additive pitch/yaw rotation."
Assert-Matches "MovieStandaloneDepthScale\s*=>\s*0\.0?6f\s*;" "Standalone FMV depth scale should stay softened at 0.06f for the alpha."

Write-Host "Alpha input look and mask toggle checks passed."
