# FFIX3DVR

https://youtu.be/EIp4YJxWL2s

FFIX3DVR is a work-in-progress Memoria mod and tooling project for playing Final Fantasy IX with depth-reconstructed field backgrounds, side-by-side 3D output, and WebXR/VR-oriented preview tools.

The current public package is an alpha test. Field scenes, actors, shadows, dialogue UI, battles, SBS/compare preview modes, and experimental FMV depth playback are the focus right now.

## Current Alpha

Download the latest sanitized alpha from the GitHub releases page:

- [FF9DepthVR 0.2.0 Open Beta 4 (FMV depth bytes alpha)](https://github.com/MiLO83/FFIX3DVR/releases/tag/v0.2.0-open-beta.4)

The release is intended for testing and feedback. Expect rough edges, scene-specific bugs, and changes between builds.

## What Works Now

- Depth-reconstructed field backgrounds for the currently exported scene set.
- SBS 3D mode for field gameplay.
- Compare mode for showing vanilla-style 2D on one side and depth/SBS work on the other.
- Battle SBS camera and hand cursor handling.
- Dialogue/menu duplication for SBS play.
- Actor, shadow, and small field effect grounding fixes for camera perspective movement.
- `F7` VR Capture Mode for forcing SBS output for headset desktop/theater capture.
- Optional localhost UDP head-tracking bridge on port `29710` using `yaw,pitch,roll` degree packets while VR Capture Mode is enabled.
- `depth-gallery.html` for browsing depth scenes and comparing 2D/3D output in a browser.

## FMV Status

The first generated FMV depth set is included as depth-only Theora `.bytes` streams. The package does not include original FMV color frames or movie files; color playback comes from the user's legally owned game install.

Important FMV limitations in this alpha:

- 3D FMV depth playback is experimental and may fall back to native 2D playback if a depth stream is missing or fails to open.
- FMV field background plates are still being stabilized across every transition.
- FMVs with actors or walkmesh interaction still need per-frame walkmesh projection against the generated depth.
- Future FMV depth passes may be regenerated as the projection and stereo tuning improves.

## Sanitized Repository

This repository and its release packages are intentionally sanitized. They do not include copyrighted Final Fantasy IX color background plates, original FMV frames, ERP/outpainted color panoramas, Gaussian splat color assets, Steam game files, or game binaries.

You need a legally owned Steam copy of Final Fantasy IX. Local tools can generate or consume assets from your own install.

## Included

- `viewer/` - local browser depth gallery and WebXR-oriented preview code.
- `tools/` - extraction, depth, packaging, and Memoria export scripts.
- `memoria-patch-source/` - mirrored FF9DepthVR Memoria patch source.
- Sanitized manifests, depth maps, walkmesh/camera metadata, and mod packaging metadata.

## Not Included

- Original FFIX `source_plate*.png` background plates.
- Original FMV color frames or movies. Only generated FMV depth `.bytes` streams are packaged.
- Outpainted/color ERP panoramas.
- Gaussian splat/PLY/KSPLAT color assets.
- Steam game files, local virtual environments, `node_modules`, and generated build folders.

## Rebuilding The Sanitized Package

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File tools\package_sanitized_release.ps1
```

That package keeps source code, scripts, generated depth data, manifests, and JSON metadata while excluding copyrighted/color-derived assets.

## Project Direction

The short-term goal is a playable alpha with stable field and battle SBS support, then full FMV depth playback once the batch render completes. After that, the next major target is VR compatibility testing on real headset hardware.

This is research-heavy modding work, not a finished release. Feedback, screenshots, bug reports, and scene-specific notes are welcome.

## Credits

- Project creator and testing: MiLO83.
- AI coding collaborator: Cody.
