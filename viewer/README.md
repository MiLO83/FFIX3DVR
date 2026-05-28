# FFIX3DVR WebXR Viewer

Static Cloudflare Pages viewer for generated FFIX 360 ERP and splat backgrounds.

## Local Run

```powershell
cd C:\Users\rxcam\Documents\FFIX3DVR\viewer
npm install
npm run publish-assets
npm run dev
```

Open `http://127.0.0.1:5173/`.

Desktop controls:

- Drag to look.
- `WASD` or arrow keys to move.
- Hold `Shift` to move faster.
- `Space`/`E` moves up, `Q`/`Ctrl` moves down.

## Verify

```powershell
npm run build
npm run smoke
```

The smoke test launches Microsoft Edge, loads the first generated scene, checks ERP and splat rendering, samples WebGL pixels, and writes screenshots to `viewer/.logs`.

## Deploy To Cloudflare Pages

```powershell
cd C:\Users\rxcam\Documents\FFIX3DVR\viewer
npm run publish-assets
npm run build
npx wrangler login
npx wrangler pages project create ffix3dvr --production-branch main
npx wrangler pages deploy dist --project-name ffix3dvr
```

After the first deploy, this shortcut is enough:

```powershell
npm run deploy:pages
```

The share URL will look like:

```text
https://ffix3dvr.pages.dev/#/scene/fbg_n00_tshp_map001_th_cgr_0
```

## WebXR Notes

WebXR requires a secure context. `pages.dev` provides HTTPS, and local `127.0.0.1` is allowed for development. Use a WebXR-capable browser such as Meta Quest Browser for headset viewing.

The current splat renderer displays generated 3DGS PLY files as colored point splats. That keeps the preview portable and VR-compatible; a fuller Gaussian splat renderer can replace `src/ply.ts` later without changing the asset manifest.

## Asset Scaling

The current 10-scene sample publishes ERP PNGs, PLY splats, source thumbnails, and `public/assets/index.json`. If all 674 maps make Pages deploys too heavy, keep the viewer on Pages and move large files to Cloudflare R2 while preserving the same manifest fields.
