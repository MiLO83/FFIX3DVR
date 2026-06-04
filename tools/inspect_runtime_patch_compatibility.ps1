param(
    [string]$GamePath,
    [string]$PatchPath = "runtime-patch",
    [string]$MemoriaReference = ".memoria-src\Memoria.Patcher\StreamingAssets\Scripts\Project\References\Assembly-CSharp.dll",
    [string]$PatchedRuntime = ".memoria-src\Output\Assembly-CSharp.dll"
)

$ErrorActionPreference = "Stop"

function Get-Sha256OrEmpty([string]$Path) {
    if (-not (Test-Path $Path)) { return "" }
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

function Describe-Dll([string]$Label, [string]$Path) {
    if (-not (Test-Path $Path)) {
        [pscustomobject]@{
            Label = $Label
            Status = "missing"
            Hash = ""
            Length = ""
            Path = $Path
        }
        return
    }
    $item = Get-Item -LiteralPath $Path
    [pscustomobject]@{
        Label = $Label
        Status = "present"
        Hash = Get-Sha256OrEmpty $Path
        Length = $item.Length
        Path = $item.FullName
    }
}

$metadataPath = Join-Path $PatchPath "patches.json"
if (-not (Test-Path $metadataPath)) {
    throw "Missing runtime patch metadata: $metadataPath"
}

$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$rows = @()
$rows += Describe-Dll "Memoria reference" $MemoriaReference
$rows += Describe-Dll "FF9DepthVR patched" $PatchedRuntime

if ($GamePath) {
    $dllPath = Join-Path $GamePath (($metadata.dllRelativePath -replace "/", "\"))
    $rows += Describe-Dll "Installed game" $dllPath
}

$rows | Format-Table -AutoSize

if ($GamePath) {
    $installedHash = ($rows | Where-Object Label -eq "Installed game").Hash
    if ($installedHash -eq $metadata.target.sha256) {
        Write-Host "Compatibility: already patched for FF9DepthVR $($metadata.version)."
    } else {
        $patch = $metadata.patches | Where-Object { $_.baseSha256 -eq $installedHash } | Select-Object -First 1
        if ($patch) {
            Write-Host "Compatibility: installable using '$($patch.label)' patch."
        } else {
            Write-Host "Compatibility: unsupported installed DLL hash."
            Write-Host "Supported base hashes:"
            foreach ($entry in $metadata.patches) {
                Write-Host "  $($entry.baseSha256) - $($entry.label)"
            }
        }
    }
}
