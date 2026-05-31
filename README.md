# FFIX3DVR

https://youtu.be/EIp4YJxWL2s

FFIX3DVR is a work-in-progress Memoria mod and tooling project for playing Final Fantasy IX with depth-reconstructed field backgrounds, side-by-side 3D output, and WebXR/VR-oriented preview tools.

The current public package is an alpha test. Field scenes, actors, shadows, dialogue UI, battles, and SBS/compare preview modes are the focus right now. Full 3D FMVs are not included yet.

## Current Alpha

Download the latest sanitized alpha from the GitHub releases page:

- [FF9DepthVR 0.2.0 Open Beta 2 (sanitized)](https://github.com/MiLO83/FFIX3DVR/releases/tag/v0.2.0-open-beta.2)

The release is intended for testing and feedback. Expect rough edges, scene-specific bugs, and changes between builds.

## What Works Now

- Depth-reconstructed field backgrounds for the currently exported scene set.
- SBS 3D mode for field gameplay.
- Compare mode for showing vanilla-style 2D on one side and depth/SBS work on the other.
- Battle SBS camera and hand cursor handling.
- Dialogue/menu duplication for SBS play.
- Actor, shadow, and small field effect grounding fixes for camera perspective movement.
- `depth-gallery.html` for browsing depth scenes and comparing 2D/3D output in a browser.

## FMV Status

3D FMVs are still being generated and integrated.

The FMV Depth-Anything batch is still rendering frame-by-frame depth data locally. As of the May 31 alpha work session, the batch had completed roughly 20k frames and was still progressing through later FMVs. The full target is expected to be around 72k FMV frames, so completion depends on uninterrupted GPU runtime, failed-frame reruns, and final packaging.

Current estimate: a few days of continued local batch processing for the full FMV depth set, followed by an implementation/testing pass before 3D FMVs can be included in a release.

Important FMV limitations in this alpha:

- 3D FMV depth playback is not shipped in the public package yet.
- FMVs currently use the safer native playback path instead of full depth-displaced 3D movie surfaces.
- Field backgrounds that transition into FMV playback still need a consistently working implementation for animated FMV background plates.
- FMVs with actors or walkmesh interaction will need per-frame depth projection once the depth sequences are complete.

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
- Original FMV color frames or movies.
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
