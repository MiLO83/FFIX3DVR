# FFIX Pages WebXR Viewer Design

## Goal

Create a static Cloudflare Pages-ready viewer for generated FFIX field backgrounds. The page should let a friend open a link, choose a generated field, view the lightweight 360 ERP panorama, and optionally switch to the heavier splat representation. The viewer must also be WebXR-compatible so a headset can enter an immersive VR session from the same scene page.

## Approach

The viewer lives in `viewer/` as a Vite + React + Three.js app. It serves static assets from `viewer/public/assets`, using a generated `index.json` that lists completed maps. Each map entry points to:

- `field_erp.png` for low-power ERP 360 mode.
- `field_spherical_env.ply` for high-power splat mode.
- `source_plate.png` for catalog thumbnails and provenance checks.

The first implementation renders ERP as an inside-viewed sphere and the current generated PLY splats as colored Three.js points. This is intentionally conservative: it keeps the web demo portable and VR-safe while preserving the asset contract for a later true Gaussian splat renderer.

## WebXR

The app requests `immersive-vr`, not passthrough AR. It uses a floor-capable reference space when available and creates a high-resolution XR framebuffer before starting the session. Desktop remains fully usable with orbit controls, and the VR entry button only appears as enabled when `navigator.xr` reports support.

## Deployment

Cloudflare Pages hosts the built `dist` directory. Current sample assets are below the Pages 25 MiB single-file asset limit. If the full 674-map set grows too large for comfortable Pages deploys, the viewer shell remains on Pages while large ERP/PLY assets move to Cloudflare R2 with the same manifest shape.

## Safety

Because these assets are derived from FFIX data, the initial share target should be a private preview link or a Cloudflare Access-protected Pages project rather than a public gallery.
