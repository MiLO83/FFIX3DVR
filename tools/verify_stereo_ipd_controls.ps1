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

function Assert-Contains {
    param(
        [string]$Needle,
        [string]$Message
    )
    if (-not $source.Contains($Needle)) {
        throw $Message
    }
}

Assert-Contains "internal enum StereoIpdProfile" "Renderer should define explicit IPD profiles."
Assert-Contains "FieldMovie" "IPD profiles should include field FMV/MBG."
Assert-Contains "StandaloneMovie" "IPD profiles should include standalone FMV."
Assert-Contains "Battle" "IPD profiles should include battle."
Assert-Contains "TryHandleStereoIpdInput" "Renderer should handle +/- IPD input centrally."
Assert-Contains "KeyCode.KeypadPlus" "IPD input should support keypad plus."
Assert-Contains "KeyCode.Equals" "IPD input should support the main keyboard plus/equals key."
Assert-Contains "KeyCode.KeypadMinus" "IPD input should support keypad minus."
Assert-Contains "KeyCode.Minus" "IPD input should support the main keyboard minus key."
Assert-Matches "GetStereoEyeOffset\s*\(\s*(FF9DepthVRFieldRenderer\.)?StereoIpdProfile\.Battle\s*\)" "Battle stereo should use the battle IPD profile."
Assert-Matches "GetStereoEyeOffset\s*\(\s*(FF9DepthVRFieldRenderer\.)?StereoIpdProfile\.StandaloneMovie\s*\)" "Standalone FMV stereo should use the standalone FMV IPD profile."
Assert-Matches "GetCurrentFieldStereoIpdProfile\s*\(" "Field stereo should pick field vs field-FMV IPD profile dynamically."
Assert-Contains "IPD " "Status overlay should include the active IPD profile/value."

Write-Host "Stereo IPD control markers present."
