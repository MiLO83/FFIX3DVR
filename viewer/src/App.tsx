import { Box, Check, Image, Minus, Play, Plus, Save, Search, Share2, Sparkles, Square, Star } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import type { Dispatch, SetStateAction } from "react";
import SceneViewer from "./SceneViewer";
import { DEFAULT_PANO_TUNING, renderTunedPano } from "./panoTuning";
import type {
  AssetManifest,
  ColorCorrection,
  ColorCorrectionMap,
  OriginalBackground,
  PanoTuning,
  PipelineStatus,
  RenderMode,
  SceneAsset,
} from "./types";

type ViewMode = "original" | "erp" | "pano" | "splat";

const DEFAULT_MANIFEST: AssetManifest = {
  generatedAt: "",
  sourceQueue: "",
  totalGenerated: 0,
  totalKnownMaps: 0,
  assets: [],
  originalBackgrounds: [],
};

const DEFAULT_PIPELINE_STATUS: PipelineStatus = {
  available: false,
  running: false,
  total: 0,
  counts: {},
  active: [],
  jobs: [],
};

function formatBytes(bytes: number) {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function loadSet(key: string) {
  try {
    const raw = window.localStorage.getItem(key);
    return new Set<string>(raw ? (JSON.parse(raw) as string[]) : []);
  } catch {
    return new Set<string>();
  }
}

function saveSet(key: string, value: Set<string>) {
  window.localStorage.setItem(key, JSON.stringify([...value]));
}

function getRouteId() {
  return window.location.hash.replace(/^#\/(?:scene|bg)\//, "");
}

function blobToDataUrl(blob: Blob) {
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error ?? new Error("Unable to read PNG blob"));
    reader.readAsDataURL(blob);
  });
}

async function fetchCorrections(): Promise<ColorCorrectionMap> {
  for (const url of ["/api/color-corrections", "/assets/color-corrections.json"]) {
    try {
      const response = await fetch(url, { cache: "no-store" });
      if (response.ok) return (await response.json()) as ColorCorrectionMap;
    } catch {
      // Static deployments use local storage only.
    }
  }
  const raw = window.localStorage.getItem("ffix3dvr-color-corrections");
  return raw ? (JSON.parse(raw) as ColorCorrectionMap) : {};
}

async function fetchPipelineStatus(): Promise<PipelineStatus> {
  const response = await fetch("/api/pipeline/status", { cache: "no-store" });
  if (!response.ok) throw new Error(`Pipeline status failed: ${response.status}`);
  return (await response.json()) as PipelineStatus;
}

function correctionToTuning(correction?: ColorCorrection): PanoTuning {
  return {
    contrast: correction?.contrast ?? DEFAULT_PANO_TUNING.contrast,
    brightness: correction?.brightness ?? DEFAULT_PANO_TUNING.brightness,
    saturation: correction?.saturation ?? DEFAULT_PANO_TUNING.saturation,
    match: correction?.match ?? DEFAULT_PANO_TUNING.match,
  };
}

function toBackgrounds(manifest: AssetManifest): OriginalBackground[] {
  if (manifest.originalBackgrounds?.length) return manifest.originalBackgrounds;
  return manifest.assets.map((asset) => ({
    id: asset.id,
    index: asset.index,
    mapName: asset.mapName,
    bundle: asset.bundle,
    status: asset.status,
    thumbnail: asset.thumbnail,
    sourcePlate: asset.sourcePlate,
    generatedAssetId: asset.id,
  }));
}

function MaskedErpPreview({ asset, tuning }: { asset: SceneAsset; tuning: PanoTuning }) {
  const [previewUrl, setPreviewUrl] = useState(asset.erp360.url);

  useEffect(() => {
    let cancelled = false;
    let objectUrl = "";

    renderTunedPano(asset, tuning)
      .then((blob) => {
        if (cancelled) return;
        objectUrl = URL.createObjectURL(blob);
        setPreviewUrl(objectUrl);
      })
      .catch(() => {
        if (!cancelled) setPreviewUrl(asset.erp360.url);
      });

    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [asset, tuning]);

  return <img src={previewUrl} alt="" />;
}

function TuningSlider({
  label,
  max,
  min,
  onChange,
  step,
  value,
}: {
  label: string;
  max: number;
  min: number;
  onChange: (value: number) => void;
  step: number;
  value: number;
}) {
  return (
    <label className="range-control">
      <span>{label}</span>
      <input max={max} min={min} onChange={(event) => onChange(Number(event.target.value))} step={step} type="range" value={value} />
      <output>{value.toFixed(2)}</output>
    </label>
  );
}

export default function App() {
  const [manifest, setManifest] = useState<AssetManifest>(DEFAULT_MANIFEST);
  const [activeId, setActiveId] = useState("");
  const [viewMode, setViewMode] = useState<ViewMode>("erp");
  const [query, setQuery] = useState("");
  const [selectedIds, setSelectedIds] = useState<Set<string>>(() => loadSet("ffix3dvr-selected-backgrounds"));
  const [favoriteIds, setFavoriteIds] = useState<Set<string>>(() => loadSet("ffix3dvr-favorite-backgrounds"));
  const [corrections, setCorrections] = useState<ColorCorrectionMap>({});
  const [tuning, setTuning] = useState<PanoTuning>(DEFAULT_PANO_TUNING);
  const [pipeline, setPipeline] = useState<PipelineStatus>(DEFAULT_PIPELINE_STATUS);
  const [pipelineError, setPipelineError] = useState("");
  const [pipelineBackend, setPipelineBackend] = useState<"preview" | "spag4d">("preview");
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "downloaded">("idle");
  const [copied, setCopied] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    function loadManifest() {
      return fetch("/assets/index.json")
      .then((response) => {
        if (!response.ok) throw new Error(`Manifest request failed: ${response.status}`);
        return response.json() as Promise<AssetManifest>;
      })
      .then((nextManifest) => {
        if (cancelled) return;
        setManifest(nextManifest);
        const backgrounds = toBackgrounds(nextManifest);
        const routeId = getRouteId();
        const initial = backgrounds.find((background) => background.id === routeId) ?? backgrounds[0];
        setActiveId(initial?.id ?? "");
      });
    }

    loadManifest()
      .catch((error: unknown) => {
        if (!cancelled) setLoadError(error instanceof Error ? error.message : "Unable to load assets");
      });

    fetchCorrections().then((nextCorrections) => {
      if (!cancelled) setCorrections(nextCorrections);
    });

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const completed = pipeline.counts.complete ?? 0;
    if (!completed || completed <= manifest.totalGenerated) return;
    let cancelled = false;
    fetch("/assets/index.json", { cache: "no-store" })
      .then((response) => {
        if (!response.ok) throw new Error(`Manifest refresh failed: ${response.status}`);
        return response.json() as Promise<AssetManifest>;
      })
      .then((nextManifest) => {
        if (!cancelled) setManifest(nextManifest);
      })
      .catch(() => {
        // The queue can still finish before publish-assets writes a fresh static manifest.
      });
    return () => {
      cancelled = true;
    };
  }, [manifest.totalGenerated, pipeline.counts.complete]);

  useEffect(() => saveSet("ffix3dvr-selected-backgrounds", selectedIds), [selectedIds]);
  useEffect(() => saveSet("ffix3dvr-favorite-backgrounds", favoriteIds), [favoriteIds]);

  useEffect(() => {
    let cancelled = false;
    async function refreshPipeline() {
      try {
        const nextPipeline = await fetchPipelineStatus();
        if (!cancelled) {
          setPipeline(nextPipeline);
          setPipelineError("");
        }
      } catch (error) {
        if (!cancelled) {
          setPipeline(DEFAULT_PIPELINE_STATUS);
          setPipelineError(error instanceof Error ? error.message : "Pipeline API unavailable");
        }
      }
    }
    refreshPipeline();
    const timer = window.setInterval(refreshPipeline, 2000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, []);

  const assetsById = useMemo(() => new Map(manifest.assets.map((asset) => [asset.id, asset])), [manifest.assets]);
  const backgrounds = useMemo(() => toBackgrounds(manifest), [manifest]);
  const activeBackground = backgrounds.find((background) => background.id === activeId) ?? backgrounds[0] ?? null;
  const activeAsset = activeBackground ? assetsById.get(activeBackground.generatedAssetId ?? activeBackground.id) ?? null : null;

  useEffect(() => {
    if (!activeBackground) return;
    if (activeAsset) window.history.replaceState(null, "", `#/scene/${activeAsset.id}`);
    else window.history.replaceState(null, "", `#/bg/${activeBackground.id}`);
  }, [activeAsset, activeBackground]);

  useEffect(() => {
    if (!activeAsset) {
      setViewMode("original");
      setTuning(DEFAULT_PANO_TUNING);
      return;
    }
    setTuning(correctionToTuning(corrections[activeAsset.id] ?? activeAsset.colorCorrection));
    setViewMode((currentMode) => (currentMode === "original" ? "erp" : currentMode));
    setSaveState("idle");
  }, [activeAsset, corrections]);

  const filteredBackgrounds = useMemo(() => {
    const term = query.trim().toLowerCase();
    const rows = term
      ? backgrounds.filter((background) => {
          return (
            background.id.includes(term) ||
            background.mapName.toLowerCase().includes(term) ||
            background.bundle.toLowerCase().includes(term) ||
            background.status.toLowerCase().includes(term)
          );
        })
      : backgrounds;

    return [...rows].sort((a, b) => {
      const favoriteSort = Number(favoriteIds.has(b.id)) - Number(favoriteIds.has(a.id));
      if (favoriteSort) return favoriteSort;
      const selectedSort = Number(selectedIds.has(b.id)) - Number(selectedIds.has(a.id));
      if (selectedSort) return selectedSort;
      return a.index - b.index;
    });
  }, [backgrounds, favoriteIds, query, selectedIds]);

  function chooseBackground(background: OriginalBackground, nextMode?: ViewMode) {
    const asset = assetsById.get(background.generatedAssetId ?? background.id);
    setActiveId(background.id);
    setViewMode(nextMode ?? (asset ? "erp" : "original"));
  }

  function toggleSet(id: string, setter: Dispatch<SetStateAction<Set<string>>>) {
    setter((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  async function copyShareUrl() {
    const id = activeAsset?.id ?? activeBackground?.id;
    if (!id) return;
    const path = activeAsset ? `#/scene/${activeAsset.id}` : `#/bg/${id}`;
    await navigator.clipboard.writeText(`${window.location.origin}${window.location.pathname}${path}`);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1400);
  }

  async function saveCorrection() {
    if (!activeAsset) return;
    setSaveState("saving");
    try {
      const blob = await renderTunedPano(activeAsset, tuning);
      const imageDataUrl = await blobToDataUrl(blob);
      const response = await fetch("/api/color-corrections", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sceneId: activeAsset.id, ...tuning, imageDataUrl }),
      });
      if (!response.ok) throw new Error("Local save API unavailable");
      const correction = (await response.json()) as ColorCorrection;
      setCorrections((current) => ({ ...current, [activeAsset.id]: correction }));
      setSaveState("saved");
    } catch {
      const fallbackCorrections = { ...corrections, [activeAsset.id]: { ...tuning, savedAt: new Date().toISOString() } };
      window.localStorage.setItem("ffix3dvr-color-corrections", JSON.stringify(fallbackCorrections));
      setCorrections(fallbackCorrections);
      setSaveState("downloaded");
    }
  }

  function updateTuning(key: keyof PanoTuning, value: number) {
    setTuning((current) => ({ ...current, [key]: value }));
    setSaveState("idle");
  }

  async function startPipeline(ids: string[], limit = ids.length || 1) {
    setPipelineError("");
    try {
      const response = await fetch("/api/pipeline/start", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ids, limit, backend: pipelineBackend }),
      });
      const payload = (await response.json()) as PipelineStatus | { error?: string; status?: PipelineStatus };
      if (!response.ok) {
        const message = "error" in payload && payload.error ? payload.error : `Pipeline start failed: ${response.status}`;
        if ("status" in payload && payload.status) setPipeline(payload.status);
        throw new Error(message);
      }
      setPipeline(payload as PipelineStatus);
    } catch (error) {
      setPipelineError(error instanceof Error ? error.message : "Pipeline start failed");
    }
  }

  async function stopPipeline() {
    setPipelineError("");
    try {
      const response = await fetch("/api/pipeline/stop", { method: "POST" });
      if (!response.ok) throw new Error(`Pipeline stop failed: ${response.status}`);
      setPipeline((await response.json()) as PipelineStatus);
    } catch (error) {
      setPipelineError(error instanceof Error ? error.message : "Pipeline stop failed");
    }
  }

  function activeProcessId() {
    return activeBackground ? (activeBackground.generatedAssetId ?? activeBackground.id) : "";
  }

  function nextProcessIds(count: number) {
    if (!activeBackground) return [];
    const activeIndex = backgrounds.findIndex((background) => background.id === activeBackground.id);
    return backgrounds
      .slice(Math.max(0, activeIndex), Math.max(0, activeIndex) + count)
      .map((background) => background.generatedAssetId ?? background.id);
  }

  const currentPipelineJob = activeProcessId() ? pipeline.jobs.find((job) => job.id === activeProcessId()) : null;
  const activePipelineText = pipeline.active.length
    ? pipeline.active.map((job) => `${job.mapName ?? job.id}: ${job.stage ?? job.status}`).join(" | ")
    : pipeline.lastExit
      ? `last exit ${pipeline.lastExit.code ?? pipeline.lastExit.signal ?? "ok"}`
      : "idle";

  const previewTitle = activeBackground?.mapName ?? "No backgrounds";

  return (
    <main className="app-shell">
      <aside className="catalog">
        <div className="brand-row">
          <div>
            <p className="eyebrow">FFIX3DVR</p>
            <h1>Background Lab</h1>
          </div>
          <Sparkles aria-hidden="true" size={22} />
        </div>

        <div className="stats-grid">
          <div>
            <span>{manifest.totalGenerated}</span>
            <small>generated</small>
          </div>
          <div>
            <span>{manifest.totalKnownMaps}</span>
            <small>originals</small>
          </div>
          <div>
            <span>{selectedIds.size}</span>
            <small>selected</small>
          </div>
          <div>
            <span>{favoriteIds.size}</span>
            <small>favorites</small>
          </div>
        </div>

        <label className="search-box">
          <Search aria-hidden="true" size={18} />
          <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search backgrounds" />
        </label>

        <div className="scene-list" aria-label="Original backgrounds">
          {loadError ? <p className="inline-error">{loadError}</p> : null}
          {filteredBackgrounds.map((background) => {
            const asset = assetsById.get(background.generatedAssetId ?? background.id);
            const selected = selectedIds.has(background.id);
            const favorite = favoriteIds.has(background.id);
            return (
              <div
                className={`scene-card ${background.id === activeBackground?.id ? "selected" : ""}`}
                key={background.id}
                onClick={() => chooseBackground(background)}
                role="button"
                tabIndex={0}
              >
                <img src={background.thumbnail} alt="" loading="lazy" />
                <span className="scene-card-copy">
                  <strong>{background.mapName}</strong>
                  <small>
                    #{background.index} - {background.bundle} - {background.status}
                  </small>
                  <span className="scene-card-actions">
                    <button
                      className={`mini-button ${selected ? "active-soft" : ""}`}
                      onClick={(event) => {
                        event.stopPropagation();
                        toggleSet(background.id, setSelectedIds);
                      }}
                      title={selected ? "Remove from selected" : "Add to selected"}
                      type="button"
                    >
                      {selected ? <Minus aria-hidden="true" size={15} /> : <Plus aria-hidden="true" size={15} />}
                    </button>
                    <button
                      className={`mini-button ${favorite ? "favorite" : ""}`}
                      onClick={(event) => {
                        event.stopPropagation();
                        toggleSet(background.id, setFavoriteIds);
                      }}
                      type="button"
                    >
                      <Star aria-hidden="true" fill={favorite ? "currentColor" : "none"} size={15} />
                    </button>
                    <button
                      className="mini-button text-mini"
                      disabled={!asset}
                      onClick={(event) => {
                        event.stopPropagation();
                        chooseBackground(background, "pano");
                      }}
                      type="button"
                    >
                      360
                    </button>
                    <button
                      className="mini-button text-mini"
                      disabled={!asset?.splat}
                      onClick={(event) => {
                        event.stopPropagation();
                        chooseBackground(background, "splat");
                      }}
                      type="button"
                    >
                      Splat
                    </button>
                  </span>
                </span>
              </div>
            );
          })}
        </div>
      </aside>

      <section className="viewer-panel">
        <header className="viewer-toolbar">
          <div>
            <p className="eyebrow">{activeAsset ? "Generated" : "Original"}</p>
            <h2>{previewTitle}</h2>
          </div>

          <div className="toolbar-actions">
            <div className="segmented" aria-label="View mode">
              <button className={viewMode === "original" ? "active" : ""} onClick={() => setViewMode("original")} type="button">
                <Image aria-hidden="true" size={17} />
                Original
              </button>
              <button className={viewMode === "erp" ? "active" : ""} disabled={!activeAsset} onClick={() => setViewMode("erp")} type="button">
                <Image aria-hidden="true" size={17} />
                ERP
              </button>
              <button className={viewMode === "pano" ? "active" : ""} disabled={!activeAsset} onClick={() => setViewMode("pano")} type="button">
                360
              </button>
              <button
                className={viewMode === "splat" ? "active" : ""}
                disabled={!activeAsset?.splat}
                onClick={() => setViewMode("splat")}
                type="button"
              >
                <Box aria-hidden="true" size={17} />
                Splat
              </button>
            </div>
            <button className="icon-button" onClick={copyShareUrl} title="Copy scene link" type="button">
              {copied ? <Check aria-hidden="true" size={18} /> : <Share2 aria-hidden="true" size={18} />}
            </button>
          </div>
        </header>

        <div className="pipeline-bar">
          <div className="pipeline-copy">
            <p className="eyebrow">Realtime Pipeline</p>
            <strong>{currentPipelineJob?.stage ?? currentPipelineJob?.status ?? "not started"}</strong>
            <small>{currentPipelineJob?.message ?? activePipelineText}</small>
          </div>
          <div className="pipeline-counts">
            <span>{pipeline.counts.complete ?? manifest.totalGenerated}</span>
            <small>done</small>
            <span>{pipeline.counts.running ?? 0}</span>
            <small>running</small>
            <span>{pipeline.counts.queued ?? Math.max(0, manifest.totalKnownMaps - manifest.totalGenerated)}</span>
            <small>queued</small>
          </div>
          <div className="pipeline-actions">
            <select value={pipelineBackend} onChange={(event) => setPipelineBackend(event.target.value === "spag4d" ? "spag4d" : "preview")}>
              <option value="preview">Preview</option>
              <option value="spag4d">SPAG-4D</option>
            </select>
            <button className="save-button" disabled={!activeProcessId() || pipeline.running} onClick={() => startPipeline([activeProcessId()])} type="button">
              <Play aria-hidden="true" size={16} />
              Current
            </button>
            <button
              className="save-button"
              disabled={!selectedIds.size || pipeline.running}
              onClick={() => startPipeline([...selectedIds], selectedIds.size)}
              type="button"
            >
              <Play aria-hidden="true" size={16} />
              Selected
            </button>
            <button className="save-button" disabled={pipeline.running} onClick={() => startPipeline(nextProcessIds(5), 5)} type="button">
              <Play aria-hidden="true" size={16} />
              Next 5
            </button>
            <button className="save-button secondary" disabled={!pipeline.running} onClick={stopPipeline} type="button">
              <Square aria-hidden="true" size={15} />
              Stop
            </button>
          </div>
          {pipelineError ? <p className="pipeline-error">{pipelineError}</p> : null}
        </div>

        {activeAsset && viewMode === "erp" ? (
          <div className="tuning-bar">
            <div className="tuning-controls">
              <TuningSlider label="Match" max={1} min={0} onChange={(value) => updateTuning("match", value)} step={0.01} value={tuning.match} />
              <TuningSlider
                label="Brightness"
                max={1.35}
                min={0.65}
                onChange={(value) => updateTuning("brightness", value)}
                step={0.01}
                value={tuning.brightness}
              />
              <TuningSlider
                label="Saturation"
                max={1.8}
                min={0.2}
                onChange={(value) => updateTuning("saturation", value)}
                step={0.01}
                value={tuning.saturation}
              />
              <TuningSlider
                label="Contrast"
                max={1.8}
                min={0.55}
                onChange={(value) => updateTuning("contrast", value)}
                step={0.01}
                value={tuning.contrast}
              />
            </div>
            <button
              className="save-button secondary"
              onClick={() => {
                setTuning(DEFAULT_PANO_TUNING);
                setSaveState("idle");
              }}
              type="button"
            >
              Reset
            </button>
            <button className="save-button" disabled={saveState === "saving"} onClick={saveCorrection} type="button">
              <Save aria-hidden="true" size={18} />
              {saveState === "saving" ? "Saving" : saveState === "saved" ? "Saved" : saveState === "downloaded" ? "Stored" : "Save"}
            </button>
          </div>
        ) : null}

        <div className="viewer-stage">
          {activeBackground && viewMode === "original" ? (
            <div className="flat-preview original-preview">
              <img src={activeBackground.sourcePlate.url} alt="" />
            </div>
          ) : activeAsset && viewMode === "erp" ? (
            <div className="flat-preview erp-preview">
              <MaskedErpPreview asset={activeAsset} tuning={tuning} />
            </div>
          ) : activeAsset && viewMode === "pano" ? (
            <SceneViewer asset={activeAsset} contrast={tuning.contrast} maskSourcePlate={false} mode={"erp" as RenderMode} />
          ) : activeAsset && viewMode === "splat" ? (
            <SceneViewer asset={activeAsset} contrast={tuning.contrast} maskSourcePlate={false} mode={"splat" as RenderMode} />
          ) : (
            <div className="empty-state">No preview available.</div>
          )}
        </div>
      </section>
    </main>
  );
}
