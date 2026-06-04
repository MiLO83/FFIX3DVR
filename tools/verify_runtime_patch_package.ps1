param(
    [string]$PatchPath = "runtime-patch",
    [string]$BaseDll = ".memoria-src\Memoria.Patcher\StreamingAssets\Scripts\Project\References\Assembly-CSharp.dll",
    [string]$PackageScript = "tools\package_sanitized_release.ps1",
    [string]$ReadmePath = "README.md",
    [string]$ModFileList = "artifacts\memoria-mod\FF9DepthVR\ModFileList.txt"
)

$ErrorActionPreference = "Stop"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

$metadataPath = Join-Path $PatchPath "patches.json"
$installerPath = Join-Path $PatchPath "Install-FF9DepthVR.ps1"
Assert-True (Test-Path $metadataPath) "Missing runtime patch metadata."
Assert-True (Test-Path $installerPath) "Missing runtime patch installer."
Assert-True (-not (Get-ChildItem -Path $PatchPath -Recurse -File -Filter "Assembly-CSharp.dll" -ErrorAction SilentlyContinue)) "Runtime patch folder must not contain Assembly-CSharp.dll."

$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
foreach ($patch in $metadata.patches) {
    $ipsPath = Join-Path $PatchPath $patch.ipsFile
    Assert-True (Test-Path $ipsPath) "Missing IPS payload $($patch.ipsFile)."
    Assert-True ((Get-Sha256 $ipsPath) -eq $patch.ipsSha256) "IPS payload hash mismatch for $($patch.ipsFile)."
}

$packageText = Get-Content -LiteralPath $PackageScript -Raw
Assert-True ($packageText -match "runtime-patch") "Package script must include runtime-patch."
Assert-True ($packageText -notmatch "patched-runtime") "Package script must not ship patched-runtime."
Assert-True ($packageText -notmatch "Copy-Item[^\r\n]+Assembly-CSharp\.dll") "Package script must not copy Assembly-CSharp.dll."

$readme = Get-Content -LiteralPath $ReadmePath -Raw
Assert-True ($readme -match "Install-FF9DepthVR\.ps1") "README must document the runtime patch installer."
Assert-True ($readme -match "2C175D936B8E1D42820DC347D9F491EB6D5B01870CF868ADB126A76D5649901E") "README must document the supported Memoria hash."

if (Test-Path $ModFileList) {
    $modFileListText = Get-Content -LiteralPath $ModFileList -Raw
    Assert-True ($modFileListText -notmatch "source_plate") "ModFileList must not reference source plates."
    Assert-True ($modFileListText -notmatch "Assembly-CSharp\.dll") "ModFileList must not reference Assembly-CSharp.dll."
    Assert-True ($modFileListText -notmatch "FF9_Data") "ModFileList must not reference FF9_Data."
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("ff9depthvr-patch-test-" + [Guid]::NewGuid().ToString("N"))
$managed = Join-Path $tempRoot "x64\FF9_Data\Managed"
New-Item -ItemType Directory -Force -Path $managed | Out-Null
Copy-Item -LiteralPath $BaseDll -Destination (Join-Path $managed "Assembly-CSharp.dll") -Force

try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installerPath -GamePath $tempRoot | Out-Host
    $patchedHash = Get-Sha256 (Join-Path $managed "Assembly-CSharp.dll")
    Assert-True ($patchedHash -eq $metadata.target.sha256) "Installer did not produce the expected target hash."
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installerPath -GamePath $tempRoot | Out-Host
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Host "Runtime patch package verification passed."
