param(
    [string]$ModPath = "artifacts/memoria-mod/FF9DepthVR"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([System.IO.Path]::IsPathRooted($ModPath)) {
    $mod = $ModPath
} else {
    $mod = Join-Path $root $ModPath
}
$list = Join-Path $mod "ModFileList.txt"
if (-not (Test-Path $list)) {
    throw "Missing ModFileList.txt: $list"
}

$entries = Get-Content -LiteralPath $list | Where-Object { $_.Trim().Length -gt 0 }
if ($entries -contains "StreamingAssets/Data/FF9DepthVR/manifest.json") {
    throw "ModFileList must not include StreamingAssets/ for Memoria Data assets."
}
if (-not ($entries -contains "Data/FF9DepthVR/manifest.json")) {
    throw "ModFileList must include Data/FF9DepthVR/manifest.json."
}

$badDataEntries = $entries | Where-Object { $_ -like "StreamingAssets/Data/*" }
if ($badDataEntries.Count -gt 0) {
    $badDataEntries | Select-Object -First 20 | ForEach-Object { Write-Host $_ }
    throw "Found StreamingAssets/Data entries; these will not match AssetManager's Data/* lookup."
}

Write-Host "Memoria ModFileList paths are normalized in $ModPath"
