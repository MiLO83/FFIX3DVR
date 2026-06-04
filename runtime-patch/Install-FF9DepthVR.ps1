param(
    [string]$GamePath,
    [string]$PatchPath = $PSScriptRoot,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

function Read-U24BE([byte[]]$Bytes, [int]$Offset) {
    [int]$b0 = $Bytes[$Offset]
    [int]$b1 = $Bytes[$Offset + 1]
    [int]$b2 = $Bytes[$Offset + 2]
    (($b0 -shl 16) -bor ($b1 -shl 8) -bor $b2)
}

function Read-U16BE([byte[]]$Bytes, [int]$Offset) {
    [int]$b0 = $Bytes[$Offset]
    [int]$b1 = $Bytes[$Offset + 1]
    (($b0 -shl 8) -bor $b1)
}

function Resolve-GamePath([string]$RequestedPath) {
    if ($RequestedPath) {
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }
    $current = (Get-Location).Path
    if (Test-Path (Join-Path $current "x64\FF9_Data\Managed\Assembly-CSharp.dll")) {
        return $current
    }
    throw "Pass -GamePath pointing at your FINAL FANTASY IX folder, or run this script from that folder."
}

function Apply-IpsPatch([byte[]]$Source, [byte[]]$Ips, [int]$TargetLength) {
    $header = [Text.Encoding]::ASCII.GetString($Ips, 0, 5)
    if ($header -ne "PATCH") {
        throw "Invalid IPS header."
    }

    $bufferLength = [Math]::Max($Source.Length, $TargetLength)
    $buffer = New-Object byte[] $bufferLength
    [Array]::Copy($Source, 0, $buffer, 0, $Source.Length)

    $cursor = 5
    while ($cursor + 3 -le $Ips.Length) {
        $marker = [Text.Encoding]::ASCII.GetString($Ips, $cursor, 3)
        if ($marker -eq "EOF") {
            $cursor += 3
            break
        }

        $offset = Read-U24BE $Ips $cursor
        $cursor += 3
        $size = Read-U16BE $Ips $cursor
        $cursor += 2

        if ($size -eq 0) {
            $rleSize = Read-U16BE $Ips $cursor
            $cursor += 2
            $value = $Ips[$cursor]
            $cursor += 1
            for ($i = 0; $i -lt $rleSize; $i++) {
                $buffer[$offset + $i] = $value
            }
        } else {
            [Array]::Copy($Ips, $cursor, $buffer, $offset, $size)
            $cursor += $size
        }
    }

    if ($TargetLength -eq $buffer.Length) {
        return $buffer
    }

    $resized = New-Object byte[] $TargetLength
    [Array]::Copy($buffer, 0, $resized, 0, $TargetLength)
    return $resized
}

$resolvedPatchPath = (Resolve-Path -LiteralPath $PatchPath).Path
$metadataPath = Join-Path $resolvedPatchPath "patches.json"
if (-not (Test-Path $metadataPath)) {
    throw "Missing runtime patch metadata: $metadataPath"
}

$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$gameRoot = Resolve-GamePath $GamePath
$dllRelative = $metadata.dllRelativePath -replace "/", "\"
$dllPath = Join-Path $gameRoot $dllRelative
if (-not (Test-Path $dllPath)) {
    throw "Could not find Assembly-CSharp.dll at $dllPath"
}

$currentHash = Get-Sha256 $dllPath
$targetHash = [string]$metadata.target.sha256
if ($currentHash -eq $targetHash) {
    Write-Host "FF9DepthVR runtime patch is already installed."
    Write-Host "Assembly-CSharp.dll hash: $currentHash"
    exit 0
}

$patch = $metadata.patches | Where-Object { ([string]$_.baseSha256).ToUpperInvariant() -eq $currentHash } | Select-Object -First 1
if (-not $patch) {
    Write-Host "Unsupported Assembly-CSharp.dll hash: $currentHash"
    Write-Host "Supported base hashes:"
    foreach ($entry in $metadata.patches) {
        Write-Host "  $($entry.baseSha256) - $($entry.label)"
    }
    Write-Host "Target hash:"
    Write-Host "  $targetHash - FF9DepthVR $($metadata.version)"
    throw "The runtime patcher refuses to patch an unknown DLL."
}

$ipsPath = Join-Path $resolvedPatchPath $patch.ipsFile
if (-not (Test-Path $ipsPath)) {
    throw "Missing IPS payload: $ipsPath"
}

$sourceBytes = [IO.File]::ReadAllBytes($dllPath)
$ipsBytes = [IO.File]::ReadAllBytes($ipsPath)
$patchedBytes = Apply-IpsPatch $sourceBytes $ipsBytes ([int]$patch.targetLength)

$tempPath = Join-Path (Split-Path -Parent $dllPath) "Assembly-CSharp.dll.ff9depthvr.tmp"
[IO.File]::WriteAllBytes($tempPath, $patchedBytes)
$patchedHash = Get-Sha256 $tempPath
if ($patchedHash -ne $targetHash) {
    Remove-Item -LiteralPath $tempPath -Force
    throw "Patched DLL hash mismatch. Expected $targetHash but got $patchedHash."
}

$backupDir = Join-Path (Split-Path -Parent $dllPath) ".ff9depthvr-backups"
$backupName = "Assembly-CSharp.dll.$((Get-Date).ToString('yyyyMMdd-HHmmss')).bak"
$backupPath = Join-Path $backupDir $backupName

if ($WhatIf) {
    Remove-Item -LiteralPath $tempPath -Force
    Write-Host "Would patch $dllPath"
    Write-Host "Would back up original to $backupPath"
    Write-Host "Patch: $($patch.label)"
    Write-Host "Target hash: $targetHash"
    exit 0
}

New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
Copy-Item -LiteralPath $dllPath -Destination $backupPath -Force
Move-Item -LiteralPath $tempPath -Destination $dllPath -Force

Write-Host "Installed FF9DepthVR runtime patch $($metadata.version)."
Write-Host "Patched: $dllPath"
Write-Host "Backup:  $backupPath"
Write-Host "Hash:    $targetHash"
