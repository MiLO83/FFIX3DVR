param(
    [string]$PackageName = "FF9DepthVR-sanitized-depth-dev"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$dist = Join-Path $root "dist"
$stage = Join-Path $dist $PackageName
$zip = Join-Path $dist "$PackageName.zip"

function Assert-UnderRoot([string]$path) {
    $resolvedRoot = [System.IO.Path]::GetFullPath($root)
    $resolvedPath = [System.IO.Path]::GetFullPath($path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside workspace: $resolvedPath"
    }
}

function Copy-Directory([string]$from, [string]$to, [string[]]$excludeDirs = @(), [string[]]$excludeFiles = @()) {
    if (-not (Test-Path $from)) { return }
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    $args = @($from, $to, "/E", "/NFL", "/NDL", "/NJH", "/NJS", "/NP")
    if ($excludeDirs.Count -gt 0) { $args += @("/XD") + $excludeDirs }
    if ($excludeFiles.Count -gt 0) { $args += @("/XF") + $excludeFiles }
    & robocopy @args | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "robocopy failed with exit code $LASTEXITCODE copying $from"
    }
}

function Convert-ToMemoriaFileListEntry([string]$relativePath) {
    $entry = $relativePath.Replace("\", "/").Trim()
    if ($entry.StartsWith("StreamingAssets/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $entry.Substring("StreamingAssets/".Length)
    }
    if ($entry.StartsWith("FF9_Data/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $entry.Substring("FF9_Data/".Length)
    }
    return $entry
}

function Write-MemoriaFileList([string]$modPath) {
    $modFull = [System.IO.Path]::GetFullPath($modPath).TrimEnd('\') + '\'
    $escaped = [Regex]::Escape($modFull)
    $entries = Get-ChildItem -Path $modPath -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne "ModFileList.txt" } |
        ForEach-Object {
            $relative = [System.IO.Path]::GetFullPath($_.FullName) -replace "^$escaped", ""
            Convert-ToMemoriaFileListEntry $relative
        } |
        Where-Object { $_ -and ($_ -notmatch "source_plate|field_erp|\.ply|\.ksplat|\.splat") } |
        Sort-Object -Unique
    Set-Content -Path (Join-Path $modPath "ModFileList.txt") -Value $entries -Encoding UTF8
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Assert-UnderRoot $stage
Assert-UnderRoot $zip
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

@"
FF9DepthVR sanitized depth/dev package

This package intentionally excludes copyrighted/color Final Fantasy IX background plates:
- source_plate*.png
- viewer/public/assets/originals
- viewer/public/assets/maps
- ERP/outpainted color panoramas
- gaussian splat/PLY color assets
- debug color plate variants

Kept:
- generated depth maps
- walkmesh/camera JSON
- manifests
- viewer/source code
- tools and docs
- FF9DepthVR patch source file

To build or run the full in-game mod, regenerate or provide source plates locally from a legally owned Steam FFIX install.
"@ | Set-Content -Path (Join-Path $stage "README_SANITIZED_PACKAGE.txt") -Encoding UTF8

Copy-Directory (Join-Path $root "tools") (Join-Path $stage "tools") @("__pycache__") @("*.pyc")
Copy-Directory (Join-Path $root "docs") (Join-Path $stage "docs") @() @()

Copy-Directory (Join-Path $root "viewer") (Join-Path $stage "viewer") `
    @("node_modules", "dist", ".logs", "originals", "maps") `
    @("source_plate*.png", "field_erp*.png", "*.ply", "*.ksplat", "*.splat")

$allFields = Join-Path $stage "artifacts\all-fields"
New-Item -ItemType Directory -Force -Path $allFields | Out-Null
Copy-Item -LiteralPath (Join-Path $root "artifacts\all-fields\manifest.json") -Destination $allFields -Force
Copy-Directory (Join-Path $root "artifacts\all-fields\depth") (Join-Path $allFields "depth") @() @()
Copy-Directory (Join-Path $root "artifacts\all-fields\logs") (Join-Path $allFields "logs") @() @("*.tmp")

$modSrc = Join-Path $root "artifacts\memoria-mod\FF9DepthVR"
$modDst = Join-Path $stage "artifacts\memoria-mod\FF9DepthVR"
Copy-Directory $modSrc $modDst @() @("source_plate*.png", "field_erp*.png", "*.ply", "*.ksplat", "*.splat")

$fmvDepthSrc = Join-Path $root "artifacts\fmv-depth"
$fmvDepthDst = Join-Path $modDst "StreamingAssets\Data\FF9DepthVR\fmv-depth"
Copy-Directory $fmvDepthSrc $fmvDepthDst @("color_frames", "depth_frames") @("frame_*.png", "*.tmp")
Write-MemoriaFileList $modDst

$patchDst = Join-Path $stage "memoria-patch-source"
New-Item -ItemType Directory -Force -Path $patchDst | Out-Null
Copy-Item -LiteralPath (Join-Path $root "memoria-patch-source\FF9DepthVRFieldRenderer.cs") -Destination $patchDst -Force

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal

$stageFull = [Regex]::Escape([System.IO.Path]::GetFullPath($stage).TrimEnd('\') + '\')
$bad = Get-ChildItem -Path $stage -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $relative = ([System.IO.Path]::GetFullPath($_.FullName) -replace "^$stageFull", "")
        ($relative -match "viewer\\public\\assets\\(originals|maps)\\") -or
        ($relative -match "artifacts\\all-fields\\(source|debug|erp|erp_|splat|splat_)\\") -or
        ($relative -match "artifacts\\memoria-mod\\.*fmv-depth\\.*\\(color_frames|depth_frames)\\") -or
        ($relative -match "artifacts\\memoria-mod\\.*fmv-depth\\.*frame_\d+\.png$") -or
        ($relative -match "artifacts\\memoria-mod\\.*(source_plate|field_erp).*\.(png|jpg|jpeg|webp)$") -or
        ($relative -match "viewer\\public\\assets\\.*(source_plate|field_erp).*\.(png|jpg|jpeg|webp)$") -or
        ($relative -match "\.(ply|ksplat|splat)$")
    }

if ($bad.Count -gt 0) {
    $bad | Select-Object -First 20 FullName | Format-Table -AutoSize
    throw "Sanitized package contains excluded color/splat assets."
}

$summary = Get-ChildItem -Path $stage -Recurse -File | Measure-Object -Sum Length
Write-Host "Created $zip"
Write-Host "Staged files: $($summary.Count)"
Write-Host "Staged bytes: $($summary.Sum)"
