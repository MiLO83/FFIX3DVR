# FFIX3DVR

https://youtu.be/EIp4YJxWL2s

FFIX3DVR is a work-in-progress Memoria mod and tooling project for playing Final Fantasy IX with depth-reconstructed field backgrounds, side-by-side 3D output, and WebXR/VR-oriented preview tools.

The current public package is an alpha test. Field scenes, actors, shadows, dialogue UI, battles, SBS/compare preview modes, and experimental FMV depth playback are the focus right now.

## Current Alpha

Download the latest sanitized alpha from the GitHub releases page:

- [FF9DepthVR 0.2.0 Open Beta 5 (stable alpha)](https://github.com/MiLO83/FFIX3DVR/releases/tag/v0.2.0-open-beta.5)

The release is intended for testing and feedback. Expect rough edges, scene-specific bugs, and changes between builds.

## Installation

Requirements:

- A legally owned Steam copy of Final Fantasy IX.
- A current Memoria Engine install for the Steam version.
- The latest `FF9DepthVR` release ZIP from GitHub.
- A Memoria `Assembly-CSharp.dll` matching one of the release patcher hashes. Open Beta 5 supports Memoria reference hash `2C175D936B8E1D42820DC347D9F491EB6D5B01870CF868ADB126A76D5649901E` and the previous FF9DepthVR alpha runtime hash.

Install steps:

1. Close Final Fantasy IX and the Memoria launcher.
2. Download the latest `FF9DepthVR-*.zip` from the [GitHub releases page](https://github.com/MiLO83/FFIX3DVR/releases).
3. Extract the ZIP somewhere temporary.
4. Copy the extracted `FF9DepthVR` mod folder into your Final Fantasy IX game folder, next to the existing game folders/files.
5. Confirm this path exists after copying: `FINAL FANTASY IX\FF9DepthVR\ModDescription.xml`.
6. Confirm the depth assets are present at `FINAL FANTASY IX\FF9DepthVR\StreamingAssets\Data\FF9DepthVR\`.
7. Install the runtime patch against your local Memoria DLL:

```powershell
powershell -ExecutionPolicy Bypass -File .\runtime-patch\Install-FF9DepthVR.ps1 -GamePath "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX"
```

8. The installer backs up your original `Assembly-CSharp.dll`, verifies the patched hash, and refuses unknown DLL versions.
9. Open the Memoria launcher/mod manager and enable `FF9 Depth VR`.
10. Launch the game through Memoria.

Controls and test toggles:

- `F9` cycles the view mode: original/depth view, SBS 3D, and compare mode.
- `F8` toggles actor-average look assist. It now defaults on; mouse/right-stick/head tracking input remains additive.
- `F7` toggles VR Capture Mode for headset desktop/theater capture.
- `F6` is no longer used; depth masks stay enabled.

Troubleshooting:

- If the mod does not appear, re-check that the folder is `FINAL FANTASY IX\FF9DepthVR\`, not `FINAL FANTASY IX\FF9DepthVR\FF9DepthVR\`.
- If scenes fall back to flat/2D, the matching depth or metadata file is probably missing from `StreamingAssets\Data\FF9DepthVR`.
- If FMVs play flat, the color movie still comes from your local game install, and 3D depth is used only when the matching generated depth stream is available.
- If the runtime patcher says the DLL hash is unsupported, reinstall/update Memoria to the supported build or wait for a refreshed FF9DepthVR patch target.
- If a new Memoria build changes loose-file loading behavior, reinstall the latest release package before debugging old files.

## What Works Now

- Depth-reconstructed field backgrounds for the currently exported scene set.
- SBS 3D mode for field gameplay.
- Compare mode for showing vanilla-style 2D on one side and depth/SBS work on the other.
- Battle SBS camera and hand cursor handling.
- Battle mouse/right-stick/head-tracking camera look support.
- Dialogue/menu duplication for SBS play.
- Actor, shadow, and small field effect grounding fixes for camera perspective movement.
- Actor-average camera look assist, enabled by default and toggleable with `F8`.
- `F7` VR Capture Mode for forcing SBS output for headset desktop/theater capture.
- Optional localhost UDP head-tracking bridge on port `29710` using `yaw,pitch,roll` degree packets while VR Capture Mode is enabled.
- `depth-gallery.html` for browsing depth scenes and comparing 2D/3D output in a browser.

## FMV Status

The first generated FMV depth set is included as depth-only Theora `.bytes` streams. The package does not include original FMV color frames or movie files; color playback comes from the user's legally owned game install.

Important FMV limitations in this alpha:

- 3D FMV depth playback is experimental and may fall back to native 2D playback if a depth stream is missing or fails to open.
- Standalone FMV depth strength is intentionally softened for the alpha while the generated depth maps are tuned.
- FMV field background plates are still being stabilized across every transition.
- FMVs with actors or walkmesh interaction still need per-frame walkmesh projection against the generated depth.
- Future FMV depth passes may be regenerated as the projection and stereo tuning improves.

## Sanitized Repository

This repository and its release packages are intentionally sanitized. They do not include copyrighted Final Fantasy IX color background plates, original FMV frames, ERP/outpainted color panoramas, Gaussian splat color assets, Steam game files, or raw game binaries. The release uses a hash-checked IPS patch installer instead of distributing a complete `Assembly-CSharp.dll`.

You need a legally owned Steam copy of Final Fantasy IX. Local tools can generate or consume assets from your own install.

## Included

- `viewer/` - local browser depth gallery and WebXR-oriented preview code.
- `tools/` - extraction, depth, packaging, and Memoria export scripts.
- `memoria-patch-source/` - mirrored FF9DepthVR Memoria patch source.
- `runtime-patch/` - IPS patch payload and installer for supported local Memoria DLLs.
- Sanitized manifests, depth maps, walkmesh/camera metadata, and mod packaging metadata.

## Not Included

- Original FFIX `source_plate*.png` background plates.
- Original FMV color frames or movies. Only generated FMV depth `.bytes` streams are packaged.
- Outpainted/color ERP panoramas.
- Gaussian splat/PLY/KSPLAT color assets.
- Raw `Assembly-CSharp.dll` binaries.
- Steam game files, local virtual environments, `node_modules`, and generated build folders.

## Rebuilding The Sanitized Package

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File tools\package_sanitized_release.ps1
```

That package keeps source code, scripts, generated depth data, manifests, and JSON metadata while excluding copyrighted/color-derived assets.

To inspect whether a future Memoria update is compatible with the runtime patcher:

```powershell
powershell -ExecutionPolicy Bypass -File tools\inspect_runtime_patch_compatibility.ps1 -GamePath "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX"
```

## Project Direction

The short-term goal is a playable alpha with stable field and battle SBS support, then full FMV depth playback once the batch render completes. After that, the next major target is VR compatibility testing on real headset hardware.

This is research-heavy modding work, not a finished release. Feedback, screenshots, bug reports, and scene-specific notes are welcome.

## Credits

- Project creator and testing: MiLO83.
- AI coding collaborator: Cody.
