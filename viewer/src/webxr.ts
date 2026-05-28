import * as THREE from "three";

type XrNavigator = Navigator & {
  xr?: {
    isSessionSupported(mode: XRSessionMode): Promise<boolean>;
    requestSession(mode: XRSessionMode, options?: XRSessionInit): Promise<XRSession>;
  };
};

type XrWindow = Window &
  typeof globalThis & {
    XRWebGLLayer?: typeof XRWebGLLayer;
  };

export async function isImmersiveVrSupported() {
  const xr = (navigator as XrNavigator).xr;
  if (!xr) return false;

  try {
    return await xr.isSessionSupported("immersive-vr");
  } catch {
    return false;
  }
}

export async function startImmersiveVr(renderer: THREE.WebGLRenderer, onEnd: () => void) {
  const xr = (navigator as XrNavigator).xr;
  if (!xr) throw new Error("WebXR is not available in this browser");

  const session = await xr.requestSession("immersive-vr", {
    optionalFeatures: ["local-floor", "bounded-floor", "hand-tracking", "layers"],
  });

  session.addEventListener("end", onEnd, { once: true });
  renderer.xr.setReferenceSpaceType("local-floor");

  const XRWebGLLayerCtor = (window as XrWindow).XRWebGLLayer;
  if (XRWebGLLayerCtor) {
    const gl = renderer.getContext();
    const nativeScale = XRWebGLLayerCtor.getNativeFramebufferScaleFactor?.(session) ?? 1;
    const layer = new XRWebGLLayerCtor(session, gl, {
      antialias: true,
      framebufferScaleFactor: nativeScale,
    });
    session.updateRenderState({ baseLayer: layer });
  }

  await renderer.xr.setSession(session);
}
