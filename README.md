# FFIX3DVR

https://youtu.be/EIp4YJxWL2s

FFIX3DVR is a depth-map dataset and experimental 3D/VR research project for Final Fantasy IX.

The current public handoff is **data only**: generated field and FMV depth assets plus camera/walkmesh metadata. The previous Memoria runtime mod/engine patch experiment is paused because the SBS/field/FMV renderer is not stable enough to recommend as a public gameplay install.

## Current Public Package

Download the current depth-map dataset from GitHub Releases:

- [FF9 Depth Map Dataset 2026-06-05](https://github.com/MiLO83/FFIX3DVR/releases/tag/v2026.06.05-depth-dataset)

Archive:

```text
FF9-depth-maps-dataset-20260605.zip
```

Verified package stats:

```text
Field depth maps:      674
Field camera metadata: 674
Field walkmesh data:   674
FMV depth streams:      60
Archive size:           1,008,603,351 bytes
SHA256:                 1AD1C89458B7DB11A499B6006743B5119FEFEBE208635670AAC9F1BBC5ECEC13
```

This package is not an installable mod. It is intended for researchers, Memoria contributors, Square Enix, or future implementers who want to build a clean renderer around the generated depth data.

## What Is Included

- `field/manifest.json` - sanitized field-depth manifest.
- `field/scenes/*/depth.png` - one generated field-background depth map per exported scene.
- `field/scenes/*/cameras.json` - extracted camera metadata where available.
- `field/scenes/*/walkmesh.json` - extracted walkmesh metadata where available.
- `fmv/*/*_depth.bytes` - packed depth streams for generated FMVs.
- `fmv/*/spec.json` and `fmv/*/progress.json` - FMV depth-stream metadata.
- `dataset-summary.json` and `checksums-sha256.txt` for verification.

## What Is Not Included

The dataset intentionally excludes copyrighted game/color assets and runtime patch files:

- Original Final Fantasy IX color background plates.
- Original FMV color frames or movie files.
- Raw generated PNG frame dumps.
- ERP/outpainted color panoramas.
- Gaussian splat/PLY/KSPLAT color assets.
- Patched `Assembly-CSharp.dll` binaries.
- IPS patches.
- Memoria mod/install scaffolding.
- Steam game files.

Use the dataset only with a legitimately owned copy of Final Fantasy IX and/or compatible assets supplied by the user.

## Status

Field background depth generation is complete for the current exported scene set: 674 scenes.

FMV depth generation is packaged as 60 depth-only streams. These do not include the original FMV videos; color playback must come from the user's own game install.

The earlier FF9DepthVR runtime work explored:

- Depth-reconstructed field backgrounds.
- SBS 3D and compare views.
- Actor, shadow, candle/fire, dialogue, and battle UI alignment.
- Mouse/right-stick/head-tracking style camera look.
- Experimental FMV depth playback.

That runtime branch proved the idea is possible, but it also exposed unstable edge cases around field masks, actor compositing, FMV field transitions, SBS camera state, and Memoria version compatibility. For now, the useful artifact is the generated depth dataset.

## Implementation Notes

Future renderers should treat the field depth maps as aligned to the original 2D field plates from the user's game install. The included camera and walkmesh JSON files are intended to help reconstruct projection, actor grounding, and collision alignment.

FMV depth should be driven as synchronized animated depth data alongside the original FMV color stream. FMV field backgrounds with actors or walkmesh interaction will need per-frame projection against the FMV depth stream.

Any future gameplay mod should be manually reviewed against current Memoria source before release. The historical runtime patches in this repository are research material, not a supported install path.

## Repository Layout

- `viewer/` - local browser depth gallery and WebXR-oriented preview code.
- `tools/` - extraction, depth, packaging, and verification scripts.
- `memoria-patch-source/` - experimental FF9DepthVR Memoria patch source from the paused runtime attempt.
- `runtime-patch/` - historical patch packaging work; not part of the current public dataset handoff.
- `artifacts/` - local generated outputs; sanitized releases exclude copyrighted/color-derived assets.

## Verifying The Dataset

After downloading the release ZIP:

```powershell
Get-FileHash .\FF9-depth-maps-dataset-20260605.zip -Algorithm SHA256
Expand-Archive .\FF9-depth-maps-dataset-20260605.zip -DestinationPath .\FF9-depth-maps-dataset-20260605
Get-Content .\FF9-depth-maps-dataset-20260605\dataset-summary.json
```

The SHA256 hash should match:

```text
1AD1C89458B7DB11A499B6006743B5119FEFEBE208635670AAC9F1BBC5ECEC13
```

## Credits

- Project creator and testing: MiLO83.
- AI coding collaborator: Cody.
