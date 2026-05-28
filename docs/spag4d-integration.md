# SPAG-4D Integration Notes

SPAG-4D is the preferred high-quality panorama-to-Gaussian-splat backend for FFIX3DVR. The repository is checked out at:

`C:\Users\rxcam\Documents\FFIX3DVR\.external\SPAG4d`

The viewer pipeline currently supports two backend choices:

- `Preview`: uses the local ERP-to-spherical-PLY converter for immediate review.
- `SPAG-4D`: records SPAG-4D as the preferred backend while still producing the preview PLY until the SPAG-4D Python/CUDA environment is installed.

## Adapter Contract

Input per scene:

- `artifacts/all-fields/erp/<scene_id>/field_erp.png`
- Optional tuned ERP: `artifacts/all-fields/erp_tuned/<scene_id>/field_erp.png`
- Optional camera path and occlusion masks under `artifacts/all-fields/source/<scene_id>/camera_moved_infill/`

Expected output per scene:

- Preview splat: `artifacts/all-fields/splat/<scene_id>/field_spherical_env.ply`
- SPAG-4D splat: `artifacts/all-fields/splat/<scene_id>/field_spag4d.ply` or `field_spag4d.splat`
- Logs: `artifacts/all-fields/logs/<scene_id>_spag4d.log`

## Occlusion Refinement Path

The planned high-quality path is:

1. Generate a source-locked ERP with ComfyUI.
2. Generate a quick preview PLY for immediate navigation.
3. Run SPAG-4D DA360 for a geometry-aware splat.
4. Use FFIX camera perturbation paths and ERP masks to identify bad novel-view regions.
5. Feed masked views through ComfyUI inpainting.
6. Run GSFix3D-style refinement on the SPAG-4D splat.

The local queue UI is designed so each background can advance through these stages without changing the viewer surface.
