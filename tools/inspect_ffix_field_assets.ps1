param(
    [string]$GamePath = "F:\SteamLibrary\steamapps\common\FINAL FANTASY IX",
    [string]$MapName = "FBG_N00_TSHP_MAP001_TH_CGR_0",
    [string]$OutputPath = "artifacts\first-field\logs\field_asset_probe.txt"
)

$ErrorActionPreference = "Stop"
$streaming = Join-Path $GamePath "StreamingAssets"
$managed = Join-Path $GamePath "x64\FF9_Data\Managed"
$lines = @()
$lines += "MapName=$MapName"
$lines += "StreamingAssets=$streaming"
$lines += "Managed=$managed"
$lines += "ExpectedFieldBundle=p0data11.bin"
$lines += "ExpectedFieldResource=FieldMaps/$MapName/"
$lines += ""
$lines += "StreamingAssets files:"
$lines += Get-ChildItem -LiteralPath $streaming -File | Sort-Object Name | ForEach-Object { "$($_.Name) $($_.Length)" }
$lines += ""
$lines += "Memoria installed markers:"
$markers = "Memoria.ini", "Memoria.Patcher.exe", "Assembly-CSharp.Memoria.dll"
foreach ($marker in $markers) {
    $matches = Get-ChildItem -LiteralPath $GamePath -Recurse -Force -File -Filter $marker -ErrorAction SilentlyContinue
    $lines += "$marker=$($matches.Count)"
}
New-Item -ItemType Directory -Force -Path (Split-Path $OutputPath) | Out-Null
Set-Content -LiteralPath $OutputPath -Value $lines
Write-Host "Wrote $OutputPath"
