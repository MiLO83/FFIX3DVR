# Memoria All-Scene Depth Renderer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Package every generated FFIX field background/depth pair and render them in Memoria as browser-calibrated depth plates while preserving native gameplay and walkmesh logic.

**Architecture:** The browser gallery remains the calibration oracle. A Python exporter writes a Memoria mod asset pack under `StreamingAssets/Data/FF9DepthVR`, and a small Memoria runtime hook loads the current scene asset by map name, hides the original still background, and creates a CPU-displaced textured mesh with conservative defaults.

**Tech Stack:** Python 3 exporter, Memoria C# / Unity runtime, existing `AssetManager` mod file lookup, existing viewer assets.

---

### Task 1: Export all calibrated browser assets as a Memoria mod pack

**Files:**
- Create: `tools/export_memoria_depth_pack.py`
- Output: `artifacts/memoria-mod/FF9DepthVR/StreamingAssets/Data/FF9DepthVR/manifest.json`

- [x] **Step 1: Read `viewer/public/assets/index.json` and collect `originalBackgrounds`.**

Use each original entry's `id`, `index`, `mapName`, `bundle`, `sourcePlate.url`, `walkmesh.url`, and `cameraMetadata.url`.

- [x] **Step 2: Copy source/depth/camera/walkmesh files.**

For every background, copy:

```text
viewer/public/assets/originals/<id>/source_plate.png
viewer/public/assets/depth/<id>/depth.png
viewer/public/assets/originals/<id>/cameras.json
viewer/public/assets/originals/<id>/walkmesh.json
```

to:

```text
artifacts/memoria-mod/FF9DepthVR/StreamingAssets/Data/FF9DepthVR/scenes/<id>/
```

- [x] **Step 3: Write `manifest.json` with shared defaults.**

Each scene entry must include:

```json
{
  "id": "fbg_n00_tshp_map001_th_cgr_0",
  "mapName": "FBG_N00_TSHP_MAP001_TH_CGR_0",
  "sourcePlate": "Data/FF9DepthVR/scenes/fbg_n00_tshp_map001_th_cgr_0/source_plate.png",
  "depth": "Data/FF9DepthVR/scenes/fbg_n00_tshp_map001_th_cgr_0/depth.png",
  "cameras": "Data/FF9DepthVR/scenes/fbg_n00_tshp_map001_th_cgr_0/cameras.json",
  "walkmesh": "Data/FF9DepthVR/scenes/fbg_n00_tshp_map001_th_cgr_0/walkmesh.json",
  "enabled": true
}
```

Global defaults:

```json
{
  "projectionYMode": "flipped",
  "depthSurfaceMode": "farthest",
  "depthStrength": 2.5,
  "wiggleStrength": 1.2,
  "dofStrength": 1.0,
  "sbs3d": false
}
```

- [x] **Step 4: Run exporter and verify counts.**

Run:

```powershell
python tools/export_memoria_depth_pack.py
```

Expected: a summary reporting copied scenes, skipped scenes, and output path.

### Task 2: Add a Memoria runtime depth renderer

**Files:**
- Create: `.memoria-src/Assembly-CSharp/Memoria/FF9DepthVR/FF9DepthVRFieldRenderer.cs`
- Modify: `.memoria-src/Assembly-CSharp/Assembly-CSharp.csproj`

- [x] **Step 1: Load manifest once using Memoria asset paths.**

Use:

```csharp
AssetManager.LoadString("Data/FF9DepthVR/manifest.json", true)
```

and parse it with `Newtonsoft.Json.Linq`.

- [x] **Step 2: Match current field by `FF9StateSystem.Common.FF9.mapNameStr`.**

Normalize both keys with uppercase invariant comparison. If the manifest or scene entry is missing, leave the vanilla background untouched.

- [x] **Step 3: Load `source_plate.png` and `depth.png`.**

Use:

```csharp
AssetManager.Load<Texture2D>(entry.SourcePlate, true)
AssetManager.Load<Texture2D>(entry.Depth, true)
```

- [x] **Step 4: Build a conservative displaced plate.**

Create a grid mesh matching the source aspect ratio, UV it exactly once, sample the depth texture, and displace vertices on Z by `depthStrength`. Parent it under the current `FieldMap`.

- [x] **Step 5: Keep native gameplay.**

Only hide the original `"Background"` transform after the custom plate is successfully created. Do not touch `WalkMesh`, actors, triggers, scripts, or collision.

- [x] **Step 6: Add the file to the csproj.**

Add:

```xml
<Compile Include="Memoria\FF9DepthVR\FF9DepthVRFieldRenderer.cs" />
```

### Task 3: Hook the renderer into field loading/camera changes

**Files:**
- Modify: `.memoria-src/Assembly-CSharp/Global/Field/Map/FieldMap.cs`

- [x] **Step 1: Refresh after normal field load.**

After `this.walkMesh.BGI_simInit();`, call:

```csharp
Memoria.FF9DepthVR.FF9DepthVRFieldRenderer.TryRefresh(this);
```

- [x] **Step 2: Refresh after camera changes.**

After `this.walkMesh.UpdateActiveCameraWalkmesh();`, call the same refresh method.

### Task 4: Verify and hand off playtest loop

**Files:**
- Validate: `viewer/src/depth-gallery.ts`
- Validate: `.memoria-src/Assembly-CSharp/Assembly-CSharp.csproj`

- [x] **Step 1: Build the browser calibration view.**

Run:

```powershell
cd viewer
npm run build
```

Expected: build succeeds.

- [x] **Step 2: Try building Memoria source.**

Run:

```powershell
dotnet build .memoria-src\Assembly-CSharp\Assembly-CSharp.csproj
```

Result: browser build passes. The Windows `NetFx3` feature could not be enabled from the non-elevated Codex shell, so `Memoria.XInputDotNetPure.csproj` now points at Memoria's local Unity/mono reference folder with `FrameworkPathOverride`. `dotnet build .memoria-src\Assembly-CSharp\Assembly-CSharp.csproj` succeeds.

- [x] **Step 3: Copy the generated mod pack into FFIX.**

Copy:

```text
artifacts/memoria-mod/FF9DepthVR
```

to:

```text
F:\SteamLibrary\steamapps\common\FINAL FANTASY IX\FF9DepthVR
```

and add `FF9DepthVR` to `[Mod] FolderNames` in `Memoria.ini`.

Result: copied to `F:\SteamLibrary\steamapps\common\FINAL FANTASY IX\FF9DepthVR`; `FolderNames = FF9DepthVR`; `Priorities = FF9DepthVR`; 674 scene folders present.

- [ ] **Step 4: Playtest all scenes with fallback.**

Scenes missing a valid source/depth asset must show vanilla Memoria backgrounds instead of failing.
