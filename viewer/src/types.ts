export type SceneAsset = {
  id: string;
  index: number;
  mapName: string;
  bundle: string;
  status: string;
  thumbnail: string;
  erp360: {
    url: string;
    bytes: number;
    projection: "equirectangular";
  };
  splat?: {
    url: string;
    bytes: number;
    type: "gaussian_splat_ply";
    variant: string;
    vertices: number;
  };
  sourcePlate: {
    url: string;
    bytes: number;
  };
  sourceProjection?: {
    yawDeg: number;
    pitchDeg: number;
    hFovDeg: number;
    vFovDeg: number;
  };
  colorCorrection?: ColorCorrection;
};

export type OriginalBackground = {
  id: string;
  index: number;
  mapName: string;
  bundle: string;
  status: string;
  thumbnail: string;
  sourcePlate: {
    url: string;
    bytes: number;
  };
  walkmesh?: {
    url: string;
    triangles: number;
    vertices: number;
  };
  cameraMetadata?: {
    url: string;
  };
  generatedAssetId?: string;
};

export type AssetManifest = {
  generatedAt: string;
  sourceQueue: string;
  totalGenerated: number;
  totalKnownMaps: number;
  assets: SceneAsset[];
  originalBackgrounds?: OriginalBackground[];
};

export type RenderMode = "erp" | "splat";

export type PanoTuning = {
  contrast: number;
  brightness: number;
  saturation: number;
  match: number;
};

export type ColorCorrection = {
  contrast: number;
  brightness?: number;
  saturation?: number;
  match?: number;
  url?: string;
  savedAt?: string;
};

export type ColorCorrectionMap = Record<string, ColorCorrection>;

export type PipelineJobSummary = {
  id: string;
  mapName?: string;
  status?: string;
  stage?: string;
  message?: string;
  updatedAt?: string;
  backend?: string;
  preferredBackend?: string;
};

export type PipelineStatus = {
  available: boolean;
  running: boolean;
  startedAt?: string | null;
  lastExit?: { code: number | null; signal: string | null; at: string } | null;
  total: number;
  counts: Record<string, number>;
  active: PipelineJobSummary[];
  jobs: PipelineJobSummary[];
};
