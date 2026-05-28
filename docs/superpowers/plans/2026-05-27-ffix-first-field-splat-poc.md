# FFIX First Field Splat PoC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a first-scene proof of concept that extracts `FBG_N00_TSHP_MAP001_TH_CGR_0`, creates a 360 equirectangular panorama candidate, and prepares a Gaussian Splat output path for later Memoria runtime integration.

**Architecture:** Keep source-game extraction, ComfyUI generation, and Memoria runtime work separate. Use Memoria or Unity asset tools to obtain field plates and metadata, use a ComfyUI sidecar for panorama and splat generation, and write a manifest that records field ID, source files, generated assets, and next integration hooks.

**Tech Stack:** PowerShell, Python, Memoria source, FFIX Steam Unity bundles, ComfyUI portable, Panorama Stickers, pytorch360convert, preview360panorama, DreamScene360 or HY-World2.

---

### Task 1: Workspace Manifest

**Files:**
- Create: `artifacts/first-field/manifest.json`
- Create: `artifacts/first-field/README.md`

- [ ] **Step 1: Create artifact directories**

Run: `New-Item -ItemType Directory -Force -Path artifacts\first-field\source,artifacts\first-field\erp,artifacts\first-field\splat,artifacts\first-field\logs`

Expected: directories exist under `artifacts/first-field`.

- [ ] **Step 2: Write the manifest**

Create `artifacts/first-field/manifest.json`:

```json
{
  "fieldId": 51,
  "mapName": "FBG_N00_TSHP_MAP001_TH_CGR_0",
  "displayName": "Prima Vista / Cargo Room",
  "sourceGamePath": "F:\\SteamLibrary\\steamapps\\common\\FINAL FANTASY IX",
  "comfyUiPath": "C:\\Users\\rxcam\\ComfyUI_portable\\ComfyUI_windows_portable\\ComfyUI",
  "status": {
    "fieldLocated": true,
    "backgroundExtracted": false,
    "equirectangularGenerated": false,
    "gaussianSplatGenerated": false,
    "memoriaRuntimeIntegrated": false
  },
  "assets": {
    "sourcePlate": null,
    "equirectangularPng": null,
    "splatPly": null
  }
}
```

- [ ] **Step 3: Write the artifact README**

Create `artifacts/first-field/README.md`:

```markdown
# FFIX First Field Splat PoC

Target field: `FBG_N00_TSHP_MAP001_TH_CGR_0`, field ID `51`, Prima Vista / Cargo Room.

Pipeline:
1. Extract field background and metadata.
2. Generate a 2:1 equirectangular panorama.
3. Generate a Gaussian Splat `.ply`.
4. Preserve original FFIX/Memoria walkmesh for gameplay.
```

### Task 2: Extraction Route

**Files:**
- Create: `tools/inspect_ffix_field_assets.ps1`
- Output: `artifacts/first-field/logs/field_asset_probe.txt`

- [ ] **Step 1: Write asset probe script**

Create `tools/inspect_ffix_field_assets.ps1`:

```powershell
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
```

- [ ] **Step 2: Run asset probe**

Run: `powershell -ExecutionPolicy Bypass -File tools\inspect_ffix_field_assets.ps1`

Expected: `artifacts/first-field/logs/field_asset_probe.txt` records field bundle and Memoria marker status.

### Task 3: ComfyUI Sidecar Decision

**Files:**
- Create: `tools/check_comfy_sidecar.ps1`
- Output: `artifacts/first-field/logs/comfy_sidecar_probe.txt`

- [ ] **Step 1: Write ComfyUI probe**

Create `tools/check_comfy_sidecar.ps1`:

```powershell
$ErrorActionPreference = "Stop"
$python = "C:\Users\rxcam\ComfyUI_portable\ComfyUI_windows_portable\python_embeded\python.exe"
$out = "artifacts\first-field\logs\comfy_sidecar_probe.txt"
$lines = @()
$lines += (& $python --version)
$lines += (& $python -c "import torch; print('torch=' + torch.__version__); print('cuda=' + str(torch.cuda.is_available())); print('device=' + (torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'none'))")
$lines += "nvcc=$((Get-Command nvcc -ErrorAction SilentlyContinue).Source)"
$lines += "reason=DreamScene360 currently documents Python 3.10/3.11, so Python 3.13 should use an isolated sidecar."
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
Set-Content -LiteralPath $out -Value $lines
Write-Host "Wrote $out"
```

- [ ] **Step 2: Run ComfyUI probe**

Run: `powershell -ExecutionPolicy Bypass -File tools\check_comfy_sidecar.ps1`

Expected: probe confirms CUDA availability and records Python version mismatch risk.

### Task 4: First Visual Artifact

**Files:**
- Output: `artifacts/first-field/source/`
- Output: `artifacts/first-field/erp/`
- Output: `artifacts/first-field/splat/`

- [ ] **Step 1: Extract or reconstruct source plate**

Use the least destructive working option in this order:
1. Memoria field export if Memoria is installed.
2. Unity asset extraction from `p0data11.bin`.
3. Temporary runtime screenshot capture if extraction tooling blocks.

Expected: a PNG source plate exists at `artifacts/first-field/source/source_plate.png`.

- [ ] **Step 2: Generate equirectangular candidate**

Use ComfyUI with Panorama Stickers or an equivalent equirectangular outpaint workflow.

Expected: a 2:1 PNG exists at `artifacts/first-field/erp/field_0051_erp.png`.

- [ ] **Step 3: Generate splat candidate**

Use DreamScene360 first if its dependencies install; use HY-World2 only if DreamScene360 blocks on Python/CUDA dependencies.

Expected: a `.ply` exists at `artifacts/first-field/splat/field_0051.ply`.

### Task 5: Verification

**Files:**
- Modify: `artifacts/first-field/manifest.json`
- Create: `artifacts/first-field/logs/verification.txt`

- [ ] **Step 1: Verify artifact dimensions**

Run a Python check that confirms the ERP PNG is exactly 2:1 and the splat file is present.

Expected: verification log records ERP width, ERP height, and splat file size.

- [ ] **Step 2: Update manifest**

Set `backgroundExtracted`, `equirectangularGenerated`, and `gaussianSplatGenerated` to match the real outputs. Fill `sourcePlate`, `equirectangularPng`, and `splatPly` with relative paths.
