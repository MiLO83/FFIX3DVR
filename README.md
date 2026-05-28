# FFIX3DVR

Prototype tooling for exploring Final Fantasy IX field backgrounds as depth-driven 3D/parallax scenes, WebXR-friendly previews, and a Memoria Engine runtime patch.

This repository is intentionally sanitized. It does **not** include original FFIX color background plates, ERP/outpainted color panoramas, Gaussian splat color assets, or game binaries. Use it with a legally owned Steam copy of Final Fantasy IX and generate local assets from your own install.

## What Is Included

- `viewer/` - local WebXR/depth gallery prototype.
- `tools/` - extraction, depth, ERP, ComfyUI, packaging, and Memoria export scripts.
- `memoria-patch-source/` - mirrored FF9DepthVR Memoria patch source file.
- `artifacts/*/manifest.json` - scene manifests and metadata.
- `artifacts/memoria-mod/.../manifest.json` - sanitized Memoria depth scene manifest.

## What Is Not Included

- Original FFIX `source_plate*.png` background plates.
- Outpainted/color ERP panoramas.
- Gaussian splat/PLY scene assets.
- Steam game files, Memoria build outputs, or patched DLLs.
- Local virtual environments, `node_modules`, and generated build folders.

## Local Package

The local sanitized ZIP can be rebuilt with:

```powershell
powershell -ExecutionPolicy Bypass -File tools\package_sanitized_release.ps1
```

That package keeps source code, scripts, depth maps, manifests, and JSON metadata while excluding copyrighted/color-derived background assets.

## Development Notes

The current Memoria patch experiments with:

- replacing static 2D field plates with a generated depth plate,
- warping foreground masks with the same plate projection,
- pinning actors/shadows to depth-adjusted background motion,
- toggling the replacement plate with vanilla fallback behavior,
- DOF/focus and mouse-driven parallax experiments.

This is research/prototype code, not a finished mod release.
