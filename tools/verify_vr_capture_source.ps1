param(
    [string]$SourcePath = "memoria-patch-source/FF9DepthVRFieldRenderer.cs"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path -LiteralPath $SourcePath)) {
    throw "Source file not found: $SourcePath"
}

$source = Get-Content -Raw -LiteralPath $SourcePath

$checks = @(
    @{ Name = "VR capture flag"; Pattern = "public static Boolean VrCaptureEnabled" },
    @{ Name = "VR capture effective SBS"; Pattern = "internal static Boolean SbsActive" },
    @{ Name = "F7 toggle handler"; Pattern = "TryHandleVrCaptureToggleInput" },
    @{ Name = "F7 key binding"; Pattern = "KeyCode.F7" },
    @{ Name = "VR capture log"; Pattern = "VR capture mode enabled" },
    @{ Name = "SBS toggle respects VR capture"; Pattern = "SbsEnabled = VrCaptureEnabled || ViewMode != DepthViewMode.Depth" },
    @{ Name = "Field stereo uses effective SBS"; Pattern = "FF9DepthVRFieldRenderer.SbsActive" },
    @{ Name = "Head tracking bridge"; Pattern = "FF9DepthVRHeadTrackingBridge" },
    @{ Name = "Head tracking UDP log"; Pattern = "Head tracking UDP bridge listening" },
    @{ Name = "Head tracking additive look"; Pattern = "AddHeadTrackLook" },
    @{ Name = "Head tracking VR gate"; Pattern = "TryReadHeadTrackLook" },
    @{ Name = "VR capture in diagnostics"; Pattern = '" vr="' }
)

$missing = @()
foreach ($check in $checks) {
    if ($source.IndexOf($check.Pattern, [System.StringComparison]::Ordinal) -lt 0) {
        $missing += $check.Name
    }
}

if ($missing.Count -gt 0) {
    throw "Missing VR capture source hooks: $($missing -join ', ')"
}

Write-Host "VR capture source hooks verified."
