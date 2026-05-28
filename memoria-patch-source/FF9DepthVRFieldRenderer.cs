using System;
using System.Collections.Generic;
using System.Text;
using Memoria.Prime;
using Memoria.Scripts;
using SimpleJSON;
using UnityEngine;
using UnityEngine.Rendering;

namespace Memoria.FF9DepthVR
{
    public static class FF9DepthVRFieldRenderer
    {
        private const String ManifestPath = "Data/FF9DepthVR/manifest.json";
        internal const String RootName = "FF9DepthVR_Background";
        private const Single DepthUnitScale = 32f;
        private const Int32 ReplacementPlateRenderQueue = 2998;
        internal const Single ViewAngleMultiplier = 3f;

        private static readonly Dictionary<String, SceneEntry> ScenesByMapName = new Dictionary<String, SceneEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Material, Int32> OriginalBackgroundQueues = new Dictionary<Material, Int32>();
        private static readonly Dictionary<Camera, CameraFrameState> CameraFrames = new Dictionary<Camera, CameraFrameState>();
        private static FF9DepthVRActorComposite _activeActorComposite;
        private static Boolean _manifestLoaded;
        private static RenderDefaults _defaults = new RenderDefaults();
        private static String _lastLoggedSceneId;
        internal static Boolean PlateVisible = true;

        internal static void ApplyActorCameraScroll(global::FieldMap fieldMap)
        {
            if (fieldMap == null || fieldMap.scene == null || fieldMap.camIdx < 0 || fieldMap.camIdx >= fieldMap.scene.cameraList.Count)
                return;

            Camera camera = fieldMap.GetMainCamera();
            CameraFrameState frame = camera != null ? GetCameraFrame(camera) : null;
            EnsureManifestLoaded();
            if (!PlateVisible || FindScene(fieldMap) == null)
            {
                if (frame != null)
                    ApplyCameraZoom(camera, frame, 1f);
                return;
            }

            BGCAM_DEF bgCamera = fieldMap.scene.cameraList[fieldMap.camIdx];
            Vector3 averageProjected;
            Vector2 minProjected;
            Vector2 maxProjected;
            Int32 actorCount;
            if (!TryGetActorProjectionBounds(fieldMap, bgCamera, out averageProjected, out minProjected, out maxProjected, out actorCount))
            {
                if (frame != null)
                    ApplyCameraZoom(camera, frame, 1f);
                return;
            }

            Vector2 targetCamera = ClampCameraToBgExtents(fieldMap, bgCamera, averageProjected);
            Vector2 targetCharOffset = new Vector2(targetCamera.x - bgCamera.centerOffset[0], -(targetCamera.y - bgCamera.centerOffset[1]));

            Single aimX = (bgCamera.w >> 1) + bgCamera.centerOffset[0] + targetCamera.x - FieldMap.HalfFieldWidth;
            Single aimY = (bgCamera.h >> 1) + bgCamera.centerOffset[1] + targetCamera.y - FieldMap.HalfFieldHeight;
            aimX -= fieldMap.offset.x - FieldMap.HalfFieldWidth;
            aimY += fieldMap.offset.y - FieldMap.HalfFieldHeight;
            aimY *= -1f;
            Vector2 targetVrp = new Vector2(
                Mathf.Clamp(aimX, bgCamera.vrpMinX, bgCamera.vrpMaxX) - bgCamera.centerOffset[0] - FieldMap.HalfFieldWidth,
                Mathf.Clamp(aimY, bgCamera.vrpMinY, bgCamera.vrpMaxY) + bgCamera.centerOffset[1] - FieldMap.HalfFieldHeight
            );

            Single t = Mathf.Clamp01(Time.deltaTime * 2.75f);
            fieldMap.charOffset = Vector2.Lerp(fieldMap.charOffset, targetCharOffset, t);
            fieldMap.curVRP = Vector2.Lerp(fieldMap.curVRP, targetVrp, t);

            if (frame != null)
            {
                Single actorSpanX = Mathf.Max(1f, maxProjected.x - minProjected.x);
                Single actorSpanY = Mathf.Max(1f, maxProjected.y - minProjected.y);
                Single fitZoomX = FieldMap.PsxFieldWidth * 0.58f / actorSpanX;
                Single fitZoomY = FieldMap.PsxFieldHeightNative * 0.58f / actorSpanY;
                Single targetZoom = Mathf.Clamp(Mathf.Min(fitZoomX, fitZoomY), 1f, 1.05f);
                ApplyCameraZoom(camera, frame, targetZoom);
            }
        }

        internal static void RegisterActorComposite(FF9DepthVRActorComposite composite)
        {
            _activeActorComposite = composite;
        }

        internal static void UnregisterActorComposite(FF9DepthVRActorComposite composite)
        {
            if (_activeActorComposite == composite)
                _activeActorComposite = null;
        }

        internal static void ApplyActorDepthOffsets()
        {
            if (_activeActorComposite != null)
                _activeActorComposite.ApplyActorDepthOffsets(false);
        }

        internal static void ApplyActorCameraFraming(global::FieldMap fieldMap, BGCAM_DEF bgCamera, ref Single cameraX, ref Single cameraY)
        {
            if (fieldMap == null || bgCamera == null)
                return;

            Camera camera = fieldMap.GetMainCamera();
            if (camera == null)
                return;

            CameraFrameState frame = GetCameraFrame(camera);
            if (!PlateVisible || FindScene(fieldMap) == null)
            {
                frame.Offset = Vector2.Lerp(frame.Offset, Vector2.zero, Time.deltaTime * 4f);
                ApplyCameraZoom(camera, frame, 1f);
                return;
            }

            FieldMapActor[] actors = UnityEngine.Object.FindObjectsOfType<FieldMapActor>();
            Vector3 projectedSum = Vector3.zero;
            Vector2 minProjected = new Vector2(Single.MaxValue, Single.MaxValue);
            Vector2 maxProjected = new Vector2(Single.MinValue, Single.MinValue);
            Int32 actorCount = 0;
            for (Int32 i = 0; i < actors.Length; i++)
            {
                FieldMapActor actor = actors[i];
                if (actor == null || actor.transform == null || actor.transform.root != fieldMap.transform.root)
                    continue;

                Vector3 aim = actor.transform.position;
                aim.y += fieldMap.charAimHeight * 0.55f;
                Vector3 projected = PSX.CalculateGTE_RTPT(aim, Matrix4x4.identity, bgCamera.GetMatrixRT(), bgCamera.GetViewDistance(), fieldMap.offset);
                if (Single.IsNaN(projected.x) || Single.IsNaN(projected.y))
                    continue;

                projectedSum += projected;
                minProjected.x = Mathf.Min(minProjected.x, projected.x);
                minProjected.y = Mathf.Min(minProjected.y, projected.y);
                maxProjected.x = Mathf.Max(maxProjected.x, projected.x);
                maxProjected.y = Mathf.Max(maxProjected.y, projected.y);
                actorCount++;
            }

            if (actorCount == 0)
            {
                frame.Offset = Vector2.Lerp(frame.Offset, Vector2.zero, Time.deltaTime * 4f);
                cameraX += frame.Offset.x;
                cameraY += frame.Offset.y;
                ApplyCameraZoom(camera, frame, 1f);
                return;
            }

            Vector3 averageProjected = projectedSum / actorCount;
            Vector2 targetCamera = ClampCameraToBgExtents(fieldMap, bgCamera, averageProjected);
            Vector2 desiredOffset = targetCamera - new Vector2(cameraX, cameraY);
            Single maxShiftX = Mathf.Max(8f, (bgCamera.w - FieldMap.PsxFieldWidth) * 0.5f);
            Single maxShiftY = Mathf.Max(8f, (bgCamera.h - FieldMap.PsxFieldHeightNative) * 0.5f);
            desiredOffset.x = Mathf.Clamp(desiredOffset.x, -maxShiftX, maxShiftX);
            desiredOffset.y = Mathf.Clamp(desiredOffset.y, -maxShiftY, maxShiftY);
            frame.Offset = Vector2.Lerp(frame.Offset, desiredOffset, Time.deltaTime * 3.5f);

            cameraX += frame.Offset.x;
            cameraY += frame.Offset.y;

            Single actorSpanX = Mathf.Max(1f, maxProjected.x - minProjected.x);
            Single actorSpanY = Mathf.Max(1f, maxProjected.y - minProjected.y);
            Single fitZoomX = FieldMap.PsxFieldWidth * 0.58f / actorSpanX;
            Single fitZoomY = FieldMap.PsxFieldHeightNative * 0.58f / actorSpanY;
            Single targetZoom = Mathf.Clamp(Mathf.Min(fitZoomX, fitZoomY), 1f, 1.08f);
            ApplyCameraZoom(camera, frame, targetZoom);
        }

        private static Boolean TryGetActorProjectionBounds(global::FieldMap fieldMap, BGCAM_DEF bgCamera, out Vector3 averageProjected, out Vector2 minProjected, out Vector2 maxProjected, out Int32 actorCount)
        {
            FieldMapActor[] actors = UnityEngine.Object.FindObjectsOfType<FieldMapActor>();
            Vector3 projectedSum = Vector3.zero;
            minProjected = new Vector2(Single.MaxValue, Single.MaxValue);
            maxProjected = new Vector2(Single.MinValue, Single.MinValue);
            actorCount = 0;
            for (Int32 i = 0; i < actors.Length; i++)
            {
                FieldMapActor actor = actors[i];
                if (actor == null || actor.transform == null || actor.transform.root != fieldMap.transform.root)
                    continue;

                Vector3 aim = actor.transform.position;
                aim.y += fieldMap.charAimHeight * 0.55f;
                Vector3 projected = PSX.CalculateGTE_RTPT(aim, Matrix4x4.identity, bgCamera.GetMatrixRT(), bgCamera.GetViewDistance(), fieldMap.offset);
                if (Single.IsNaN(projected.x) || Single.IsNaN(projected.y))
                    continue;

                projectedSum += projected;
                minProjected.x = Mathf.Min(minProjected.x, projected.x);
                minProjected.y = Mathf.Min(minProjected.y, projected.y);
                maxProjected.x = Mathf.Max(maxProjected.x, projected.x);
                maxProjected.y = Mathf.Max(maxProjected.y, projected.y);
                actorCount++;
            }

            averageProjected = actorCount > 0 ? projectedSum / actorCount : Vector3.zero;
            return actorCount > 0;
        }

        private static CameraFrameState GetCameraFrame(Camera camera)
        {
            CameraFrameState frame;
            if (!CameraFrames.TryGetValue(camera, out frame))
            {
                frame = new CameraFrameState();
                frame.BaseOrthographicSize = camera.orthographicSize;
                frame.BaseFieldOfView = camera.fieldOfView;
                CameraFrames[camera] = frame;
            }
            return frame;
        }

        private static Vector2 ClampCameraToBgExtents(global::FieldMap fieldMap, BGCAM_DEF bgCamera, Vector3 projected)
        {
            Single aimX = (bgCamera.w >> 1) + bgCamera.centerOffset[0] + projected.x - FieldMap.HalfFieldWidth;
            Single aimY = (bgCamera.h >> 1) + bgCamera.centerOffset[1] + projected.y - FieldMap.HalfFieldHeight;
            aimX -= fieldMap.offset.x - FieldMap.HalfFieldWidth;
            aimY += fieldMap.offset.y - FieldMap.HalfFieldHeight;
            aimY *= -1f;

            Single clampedX = projected.x;
            Single clampedY = projected.y;
            if (aimX < bgCamera.vrpMinX)
                clampedX = fieldMap.offset.x - (bgCamera.w >> 1) - bgCamera.centerOffset[0] + bgCamera.vrpMinX;
            else if (aimX > bgCamera.vrpMaxX)
                clampedX = fieldMap.offset.x - (bgCamera.w >> 1) - bgCamera.centerOffset[0] + bgCamera.vrpMaxX;

            if (aimY < bgCamera.vrpMinY)
                clampedY = fieldMap.offset.y + (bgCamera.h >> 1) + bgCamera.centerOffset[1] - bgCamera.vrpMinY;
            else if (aimY > bgCamera.vrpMaxY)
                clampedY = fieldMap.offset.y + (bgCamera.h >> 1) + bgCamera.centerOffset[1] - bgCamera.vrpMaxY;

            return new Vector2(clampedX, clampedY);
        }

        private static void ApplyCameraZoom(Camera camera, CameraFrameState frame, Single targetZoom)
        {
            frame.Zoom = Mathf.Lerp(frame.Zoom <= 0f ? 1f : frame.Zoom, targetZoom, Time.deltaTime * 2f);
            if (camera.orthographic)
                camera.orthographicSize = frame.BaseOrthographicSize / Mathf.Max(1f, frame.Zoom);
            else
                camera.fieldOfView = frame.BaseFieldOfView / Mathf.Max(1f, frame.Zoom);
        }

        public static void TryRefresh(global::FieldMap fieldMap)
        {
            if (fieldMap == null || fieldMap.scene == null)
                return;

            try
            {
                EnsureManifestLoaded();
                SceneEntry entry = FindScene(fieldMap);
                if (entry == null || !entry.Enabled)
                {
                    DestroyExisting(fieldMap);
                    SetOriginalBackgroundVisible(fieldMap, true);
                    return;
                }

                Texture2D colorTexture = global::AssetManager.Load<Texture2D>(entry.SourcePlate, true);
                Texture2D depthTexture = global::AssetManager.Load<Texture2D>(entry.Depth, true);
                if (colorTexture == null || depthTexture == null)
                {
                    DestroyExisting(fieldMap);
                    SetOriginalBackgroundVisible(fieldMap, true);
                    return;
                }

                ApplyAlphaCutoff(colorTexture, _defaults.AlphaCutoff);
                DestroyExisting(fieldMap);
                GameObject root = BuildDepthPlate(fieldMap, entry, colorTexture, depthTexture);
                if (root != null)
                {
                    SetOriginalBackgroundVisible(fieldMap, true);
                }
                else
                    SetOriginalBackgroundVisible(fieldMap, true);
            }
            catch (Exception ex)
            {
                DestroyExisting(fieldMap);
                SetOriginalBackgroundVisible(fieldMap, true);
                Log.Message("[FF9DepthVR] Field renderer failed: " + ex);
            }
        }

        private static void EnsureManifestLoaded()
        {
            if (_manifestLoaded)
                return;

            _manifestLoaded = true;
            String manifest = global::AssetManager.LoadString(ManifestPath, true);
            if (String.IsNullOrEmpty(manifest))
            {
                Log.Message("[FF9DepthVR] No manifest found at " + ManifestPath + "; vanilla fields remain active.");
                return;
            }

            JSONNode root = JSONNode.Parse(manifest);
            JSONClass defaults = root["defaults"].AsObject;
            if (defaults != null)
                _defaults = RenderDefaults.FromJson(defaults);

            JSONArray scenes = root["scenes"].AsArray;
            if (scenes == null)
                return;

            foreach (JSONNode token in scenes.Childs)
            {
                JSONClass sceneJson = token.AsObject;
                if (sceneJson == null)
                    continue;

                SceneEntry scene = SceneEntry.FromJson(sceneJson);
                if (scene == null || String.IsNullOrEmpty(scene.MapName))
                    continue;

                ScenesByMapName[scene.MapName] = scene;
            }

            Log.Message("[FF9DepthVR] Loaded " + ScenesByMapName.Count + " depth scenes.");
        }

        private static SceneEntry FindScene(global::FieldMap fieldMap)
        {
            SceneEntry entry;
            if (!String.IsNullOrEmpty(fieldMap.mapName) && ScenesByMapName.TryGetValue(fieldMap.mapName, out entry))
                return entry;

            String stateMapName = global::FF9StateSystem.Common.FF9.mapNameStr;
            if (!String.IsNullOrEmpty(stateMapName) && ScenesByMapName.TryGetValue(stateMapName, out entry))
                return entry;

            return null;
        }

        private static GameObject BuildDepthPlate(global::FieldMap fieldMap, SceneEntry entry, Texture2D colorTexture, Texture2D depthTexture)
        {
            Int32 columns = Mathf.Clamp(_defaults.MeshColumns, 8, 192);
            Int32 rows = Mathf.Clamp(_defaults.MeshRows, 8, 108);
            Single width = colorTexture.width * _defaults.SourceScale;
            Single height = colorTexture.height * _defaults.SourceScale;
            Single depthScale = _defaults.GeometryDepthScale > 0f ? _defaults.GeometryDepthScale : Mathf.Max(1f, _defaults.DepthStrength * DepthUnitScale);
            if (_lastLoggedSceneId != entry.Id)
            {
                _lastLoggedSceneId = entry.Id;
                BGCAM_DEF bgCamera = fieldMap.scene.cameraList[fieldMap.camIdx];
                Log.Message("[FF9DepthVR] Rendering " + entry.Id + " tex=" + colorTexture.width + "x" + colorTexture.height + " plate=" + width + "x" + height + " cam=" + bgCamera.w + "x" + bgCamera.h + " sourceScale=" + _defaults.SourceScale + " geometryDepthScale=" + depthScale.ToString("F2"));
            }

            Transform plateParent = GetPlateParent(fieldMap);
            GameObject root = new GameObject(RootName);
            root.transform.parent = plateParent;
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            SetLayerRecursive(root, plateParent.gameObject.layer);

            Mesh mesh = new Mesh();
            mesh.name = "FF9DepthVR_" + entry.Id;

            Int32 vertexCount = (columns + 1) * (rows + 1);
            Vector3[] vertices = new Vector3[vertexCount];
            Vector2[] uvs = new Vector2[vertexCount];
            Color[] colors = new Color[vertexCount];

            for (Int32 y = 0; y <= rows; y++)
            {
                Single v = (Single)y / rows;
                for (Int32 x = 0; x <= columns; x++)
                {
                    Single u = (Single)x / columns;
                    Int32 index = y * (columns + 1) + x;
                    Single sampledDepth = SampleDepth(depthTexture, u, 1f - v);
                    Single z = (sampledDepth - 0.5f) * depthScale;
                    vertices[index] = new Vector3(u * width, v * height, z);
                    uvs[index] = new Vector2(u, 1f - v);
                    colors[index] = new Color(sampledDepth, sampledDepth, sampledDepth, 1f);
                }
            }

            Int32[] triangles = new Int32[columns * rows * 6];
            Int32 tri = 0;
            for (Int32 y = 0; y < rows; y++)
            {
                for (Int32 x = 0; x < columns; x++)
                {
                    Int32 a = y * (columns + 1) + x;
                    Int32 b = a + 1;
                    Int32 c = a + columns + 1;
                    Int32 d = c + 1;
                    triangles[tri++] = a;
                    triangles[tri++] = c;
                    triangles[tri++] = b;
                    triangles[tri++] = b;
                    triangles[tri++] = c;
                    triangles[tri++] = d;
                }
            }

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            colorTexture.wrapMode = TextureWrapMode.Clamp;
            colorTexture.filterMode = FilterMode.Bilinear;
            depthTexture.wrapMode = TextureWrapMode.Clamp;
            depthTexture.filterMode = FilterMode.Bilinear;

            Material material = CreateMaterial(colorTexture, depthTexture, _defaults.RenderQueue, _defaults.PlateOpacity);
            Log.Message("[FF9DepthVR] Plate material shader=" + (material.shader != null ? material.shader.name : "null") + " queue=" + material.renderQueue + " parent=" + GetPath(root.transform.parent) + " layer=" + root.layer);
            MeshFilter meshFilter = root.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = root.AddComponent<MeshRenderer>();
            meshFilter.sharedMesh = mesh;
            meshRenderer.sharedMaterial = material;
            meshRenderer.enabled = PlateVisible;

            FF9DepthVRParallax parallax = root.AddComponent<FF9DepthVRParallax>();
            parallax.Initialize(mesh, vertices, colors, width, height, _defaults.ParallaxStrength, _defaults.IdleParallaxStrength, material, colorTexture, depthTexture);
            FF9DepthVRActorComposite actorComposite = root.AddComponent<FF9DepthVRActorComposite>();
            actorComposite.Initialize(fieldMap, depthTexture, width, height, _defaults.ActorRenderQueue, _defaults.ActorParallaxStrength, _defaults.ActorCompositeShader, parallax);
            FF9DepthVRMaskWarp maskWarp = root.AddComponent<FF9DepthVRMaskWarp>();
            maskWarp.Initialize(fieldMap, parallax, depthTexture, width, height, _defaults.ActorRenderQueue);
            FF9DepthVRDiagnostics diagnostics = root.AddComponent<FF9DepthVRDiagnostics>();
            diagnostics.Initialize(fieldMap, meshRenderer);

            return root;
        }

        private static Material CreateMaterial(Texture2D colorTexture, Texture2D depthTexture, Int32 renderQueue, Single plateOpacity)
        {
            Shader shader = ShadersLoader.Find("Unlit/AdjustableTransparent");
            if (shader == null)
                shader = Shader.Find("Unlit/AdjustableTransparent");
            if (shader == null)
                shader = ShadersLoader.Find("Unlit/Transparent");
            if (shader == null)
                shader = Shader.Find("Unlit/Transparent");
            if (shader == null)
                shader = ShadersLoader.Find("Particles/Alpha Blended");
            if (shader == null)
                shader = Shader.Find("Particles/Alpha Blended");
            if (shader == null)
                shader = ShadersLoader.Find("PSX/FieldMap_Abr_None");
            if (shader == null)
                shader = Shader.Find("PSX/FieldMap_Abr_None");
            if (shader == null)
                shader = Shader.Find("Diffuse");

            Material material = new Material(shader);
            material.mainTexture = colorTexture;
            material.renderQueue = ReplacementPlateRenderQueue;
            if (material.HasProperty("_DepthTex"))
                material.SetTexture("_DepthTex", depthTexture);
            if (material.HasProperty("_ColorTexel"))
                material.SetVector("_ColorTexel", new Vector4(1f / Mathf.Max(1, colorTexture.width), 1f / Mathf.Max(1, colorTexture.height), 0f, 0f));
            if (material.HasProperty("_FocusUv"))
                material.SetVector("_FocusUv", new Vector4(0.5f, 0.5f, 0f, 0f));
            if (material.HasProperty("_FocusDepth"))
                material.SetFloat("_FocusDepth", 0.5f);
            if (material.HasProperty("_DofAmount"))
                material.SetFloat("_DofAmount", 1f);
            if (material.HasProperty("_AlphaCutoff"))
                material.SetFloat("_AlphaCutoff", 0.04f);
            if (material.HasProperty("_TintColor"))
                material.SetColor("_TintColor", Color.white);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", Color.white);
            if (material.HasProperty("_Cutoff"))
                material.SetFloat("_Cutoff", 0.5f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetInt("_ZWrite", 0);
            material.SetInt("_ZTest", (Int32)UnityEngine.Rendering.CompareFunction.LessEqual);
            material.SetInt("_SrcBlend", (Int32)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (Int32)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            return material;
        }

        private static void ApplyAlphaCutoff(Texture2D texture, Single cutoff)
        {
            try
            {
                Color32[] pixels = texture.GetPixels32();
                Byte threshold = (Byte)Mathf.Clamp(Mathf.RoundToInt(cutoff * 255f), 0, 255);
                for (Int32 i = 0; i < pixels.Length; i++)
                    pixels[i].a = pixels[i].a >= threshold ? (Byte)255 : (Byte)0;
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
            }
            catch
            {
                // Some runtime textures may be non-readable; shader cutoff still handles them.
            }
        }

        private static Single SampleDepth(Texture2D depthTexture, Single u, Single v)
        {
            try
            {
                return depthTexture.GetPixelBilinear(Mathf.Clamp01(u), Mathf.Clamp01(v)).grayscale;
            }
            catch
            {
                return 0.5f;
            }
        }

        private static void DestroyExisting(global::FieldMap fieldMap)
        {
            Transform existing = FindChildRecursive(fieldMap.transform, RootName);
            if (existing == null)
                return;

            if (Application.isPlaying)
                UnityEngine.Object.Destroy(existing.gameObject);
            else
                UnityEngine.Object.DestroyImmediate(existing.gameObject);
        }

        internal static void SyncOriginalBackgroundVisibility(global::FieldMap fieldMap)
        {
            SetOriginalBackgroundVisible(fieldMap, true);
        }

        private static void SetOriginalBackgroundVisible(global::FieldMap fieldMap, Boolean visible)
        {
            if (fieldMap == null)
                return;

            Transform background = fieldMap.transform.Find("Background");
            if (background == null)
                return;

            background.gameObject.SetActive(true);
            Renderer[] renderers = background.GetComponentsInChildren<Renderer>(true);
            Int32 changed = 0;
            for (Int32 i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || IsDepthReplacement(renderer.transform))
                    continue;

                renderer.enabled = visible;
                SyncOriginalBackgroundQueues(renderer);
                changed++;
            }

            Log.Message("[FF9DepthVR] Original background renderers visible=" + visible + " count=" + changed);
        }

        private static void SyncOriginalBackgroundQueues(Renderer renderer)
        {
            Material[] materials = renderer.materials;
            for (Int32 i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null)
                    continue;

                Int32 originalQueue;
                if (!OriginalBackgroundQueues.TryGetValue(material, out originalQueue))
                {
                    originalQueue = material.renderQueue;
                    OriginalBackgroundQueues[material] = originalQueue;
                }

                material.renderQueue = PlateVisible && !IsForegroundQueue(originalQueue) ? ActorRenderQueue.Background : originalQueue;
            }
        }

        private static Boolean IsForegroundQueue(Int32 renderQueue)
        {
            Int32 actorQueue = _defaults.ActorRenderQueue >= 0 ? _defaults.ActorRenderQueue : ActorRenderQueue.AlphaTest;
            return renderQueue >= actorQueue;
        }

        private static Boolean IsDepthReplacement(Transform transform)
        {
            while (transform != null)
            {
                if (transform.name == FF9DepthVRFieldRenderer.RootName)
                    return true;
                transform = transform.parent;
            }
            return false;
        }

        private static Transform GetPlateParent(global::FieldMap fieldMap)
        {
            if (fieldMap != null && fieldMap.scene != null && fieldMap.camIdx >= 0 && fieldMap.camIdx < fieldMap.scene.cameraList.Count)
            {
                Transform cameraTransform = fieldMap.scene.cameraList[fieldMap.camIdx].transform;
                if (cameraTransform != null)
                    return cameraTransform;
            }

            Transform background = fieldMap != null ? fieldMap.transform.Find("Background") : null;
            return background != null ? background : fieldMap.transform;
        }

        private static Transform FindChildRecursive(Transform parent, String name)
        {
            if (parent == null)
                return null;
            if (parent.name == name)
                return parent;
            for (Int32 i = 0; i < parent.childCount; i++)
            {
                Transform child = FindChildRecursive(parent.GetChild(i), name);
                if (child != null)
                    return child;
            }
            return null;
        }

        private static void SetLayerRecursive(GameObject root, Int32 layer)
        {
            if (root == null)
                return;
            root.layer = layer;
            Transform transform = root.transform;
            for (Int32 i = 0; i < transform.childCount; i++)
                SetLayerRecursive(transform.GetChild(i).gameObject, layer);
        }

        private static String GetPath(Transform transform)
        {
            if (transform == null)
                return String.Empty;
            String path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }

        private sealed class RenderDefaults
        {
            public Single DepthStrength = 2.5f;
            public Single GeometryDepthScale = 6f;
            public Single ParallaxStrength = 18f;
            public Single IdleParallaxStrength = 3f;
            public Int32 RenderQueue = 1200;
            public Int32 ActorRenderQueue = 2450;
            public Single ActorParallaxStrength = 7f;
            public Boolean ActorCompositeShader = false;
            public Single PlateOpacity = 0.82f;
            public Single AlphaCutoff = 0.5f;
            public Int32 MeshColumns = 96;
            public Int32 MeshRows = 54;
            public Single SourceScale = 0.5f;
            public Boolean HideOriginalBackground = false;

            public static RenderDefaults FromJson(JSONClass json)
            {
                RenderDefaults defaults = new RenderDefaults();
                defaults.DepthStrength = ReadSingle(json, "depthStrength", defaults.DepthStrength);
                defaults.GeometryDepthScale = ReadSingle(json, "geometryDepthScale", defaults.GeometryDepthScale);
                defaults.ParallaxStrength = ReadSingle(json, "parallaxStrength", defaults.ParallaxStrength);
                defaults.IdleParallaxStrength = ReadSingle(json, "idleParallaxStrength", defaults.IdleParallaxStrength);
                defaults.RenderQueue = ReadInt32(json, "renderQueue", defaults.RenderQueue);
                defaults.ActorRenderQueue = ReadInt32(json, "actorRenderQueue", defaults.ActorRenderQueue);
                defaults.ActorParallaxStrength = ReadSingle(json, "actorParallaxStrength", defaults.ActorParallaxStrength);
                defaults.ActorCompositeShader = ReadBoolean(json, "actorCompositeShader", defaults.ActorCompositeShader);
                defaults.PlateOpacity = ReadSingle(json, "plateOpacity", defaults.PlateOpacity);
                defaults.AlphaCutoff = ReadSingle(json, "alphaCutoff", defaults.AlphaCutoff);
                defaults.MeshColumns = ReadInt32(json, "meshColumns", defaults.MeshColumns);
                defaults.MeshRows = ReadInt32(json, "meshRows", defaults.MeshRows);
                defaults.SourceScale = ReadSingle(json, "sourceScale", defaults.SourceScale);
                defaults.HideOriginalBackground = ReadBoolean(json, "hideOriginalBackground", defaults.HideOriginalBackground);
                if (defaults.SourceScale <= 0f)
                    defaults.SourceScale = 0.5f;
                return defaults;
            }
        }

        private sealed class SceneEntry
        {
            public String Id;
            public String MapName;
            public String SourcePlate;
            public String Depth;
            public Boolean Enabled;

            public static SceneEntry FromJson(JSONClass json)
            {
                SceneEntry entry = new SceneEntry();
                entry.Id = ReadString(json, "id", String.Empty);
                entry.MapName = ReadString(json, "mapName", String.Empty);
                entry.SourcePlate = ReadString(json, "sourcePlate", String.Empty);
                entry.Depth = ReadString(json, "depth", String.Empty);
                entry.Enabled = ReadBoolean(json, "enabled", false);
                if (String.IsNullOrEmpty(entry.SourcePlate) || String.IsNullOrEmpty(entry.Depth))
                    entry.Enabled = false;
                return entry;
            }
        }

        private sealed class CameraFrameState
        {
            public Vector2 Offset;
            public Single Zoom = 1f;
            public Single BaseOrthographicSize;
            public Single BaseFieldOfView;
        }

        private static String ReadString(JSONClass json, String name, String fallback)
        {
            JSONNode token = json[name];
            return token == null || String.IsNullOrEmpty(token.Value) ? fallback : token.Value;
        }

        private static Boolean ReadBoolean(JSONClass json, String name, Boolean fallback)
        {
            JSONNode token = json[name];
            return token == null || String.IsNullOrEmpty(token.Value) ? fallback : token.AsBool;
        }

        private static Int32 ReadInt32(JSONClass json, String name, Int32 fallback)
        {
            JSONNode token = json[name];
            return token == null || String.IsNullOrEmpty(token.Value) ? fallback : token.AsInt;
        }

        private static Single ReadSingle(JSONClass json, String name, Single fallback)
        {
            JSONNode token = json[name];
            return token == null || String.IsNullOrEmpty(token.Value) ? fallback : token.AsFloat;
        }
    }

    public sealed class FF9DepthVRParallax : MonoBehaviour
    {
        private Mesh _mesh;
        private Vector3[] _baseVertices;
        private Vector3[] _workingVertices;
        private Single[] _depth;
        private Single _width;
        private Single _height;
        private Single _strength;
        private Single _idleStrength;
        private Single _smoothedX;
        private Single _smoothedY;
        private Single _currentOffsetX;
        private Single _currentOffsetY;
        private Single _focusBiasX;
        private Single _focusBiasY;
        private Single _focusPivotX = 0.5f;
        private Single _focusPivotY = 0.5f;
        private Single _focusZoom;
        private Single _smoothedZoom;
        private Material _material;
        private Texture2D _colorTexture;
        private Texture2D _depthTexture;
        private Texture2D _dofTexture;
        private Color32[] _sourcePixels;
        private Color32[] _depthPixels;
        private Color32[] _dofPixels;
        private Int32 _dofWidth;
        private Int32 _dofHeight;
        private Single _dofTimer;
        private Single _lastDofDepth = -1f;
        private Vector2 _lastDofUv = new Vector2(-1f, -1f);
        private Single _targetFocusDepth = 0.5f;
        private Single _autofocusDepth = 0.5f;
        private Single _autofocusVelocity;
        private Single _rackStartDepth = 0.5f;
        private Single _rackTargetDepth = 0.5f;
        private Single _rackElapsedSeconds;
        private Int32 _lastUpdatedFrame = -1;
        private static readonly Boolean EnableCpuDofFallback = false;

        public void Initialize(Mesh mesh, Vector3[] baseVertices, Color[] depthColors, Single width, Single height, Single strength, Single idleStrength, Material material, Texture2D colorTexture, Texture2D depthTexture)
        {
            _mesh = mesh;
            _baseVertices = (Vector3[])baseVertices.Clone();
            _workingVertices = (Vector3[])baseVertices.Clone();
            _depth = new Single[depthColors.Length];
            for (Int32 i = 0; i < depthColors.Length; i++)
                _depth[i] = depthColors[i].r - 0.5f;
            _width = width;
            _height = height;
            _strength = Mathf.Max(0f, strength);
            _idleStrength = Mathf.Max(0f, idleStrength);
            _material = material;
            _colorTexture = colorTexture;
            _depthTexture = depthTexture;
            InitializeCpuDof();
        }

        private void LateUpdate()
        {
            RefreshGeometryNow();
        }

        public void RefreshGeometryNow()
        {
            if (_mesh == null || _baseVertices == null || _workingVertices == null || _depth == null)
                return;
            if (_lastUpdatedFrame == Time.frameCount)
                return;
            _lastUpdatedFrame = Time.frameCount;

            Single targetX = 0f;
            Single targetY = 0f;
            if (Screen.width > 0 && Screen.height > 0)
            {
                targetX = Mathf.Clamp((Input.mousePosition.x / Screen.width - 0.5f) * 2f, -1f, 1f) * FF9DepthVRFieldRenderer.ViewAngleMultiplier;
                targetY = Mathf.Clamp((Input.mousePosition.y / Screen.height - 0.5f) * 2f, -1f, 1f) * FF9DepthVRFieldRenderer.ViewAngleMultiplier;
            }

            Single idleX = Mathf.Sin(Time.time * 0.73f) * (_idleStrength / Mathf.Max(1f, _strength));
            Single idleY = Mathf.Cos(Time.time * 0.61f) * (_idleStrength / Mathf.Max(1f, _strength));
            _smoothedX = Mathf.Lerp(_smoothedX, targetX + idleX + _focusBiasX, Time.deltaTime * 5f);
            _smoothedY = Mathf.Lerp(_smoothedY, targetY + idleY + _focusBiasY, Time.deltaTime * 5f);
            _currentOffsetX = _smoothedX * _strength;
            _currentOffsetY = _smoothedY * _strength;
            _smoothedZoom = Mathf.Lerp(_smoothedZoom, _focusZoom, Time.deltaTime * 2.5f);
            UpdateMaterialFocus();

            for (Int32 i = 0; i < _baseVertices.Length; i++)
            {
                Vector3 vertex = _baseVertices[i];
                Single depth = _depth[i];
                Vector2 transformed = PlatePoint(vertex.x, vertex.y, depth);
                vertex.x = transformed.x;
                vertex.y = transformed.y;
                _workingVertices[i] = vertex;
            }

            _mesh.vertices = _workingVertices;
            _mesh.RecalculateBounds();
        }

        private void UpdateMaterialFocus()
        {
            Vector2 focusUv = MouseFocusUv();
            Single sampledDepth = SampleDepth(focusUv);
            if (Mathf.Abs(sampledDepth - _rackTargetDepth) > 0.018f)
            {
                Single travel = sampledDepth - _autofocusDepth;
                _rackStartDepth = _autofocusDepth;
                _rackTargetDepth = sampledDepth;
                _rackElapsedSeconds = 0f;
                _autofocusVelocity = -Mathf.Sign(Mathf.Abs(travel) < 0.0001f ? 1f : travel) * Mathf.Min(0.42f, 0.1f + Mathf.Abs(travel) * 1.8f);
            }

            _targetFocusDepth = sampledDepth;
            Single focusDepth = UpdateAutofocus(_targetFocusDepth, Time.deltaTime, Time.time);
            if (_material == null || !_material.HasProperty("_FocusDepth"))
            {
                UpdateCpuDof(focusUv, focusDepth);
                return;
            }

            _material.SetVector("_FocusUv", new Vector4(focusUv.x, focusUv.y, 0f, 0f));
            _material.SetFloat("_FocusDepth", focusDepth);
            _material.SetFloat("_DofAmount", FF9DepthVRFieldRenderer.PlateVisible ? 1f : 0f);
        }

        private void InitializeCpuDof()
        {
            // The CPU DOF fallback is too expensive for live field rendering on large plates.
            // Keep it disabled until the effect can run in a supported GPU shader.
            if (!EnableCpuDofFallback)
                return;

            if (_material == null || _colorTexture == null || _depthTexture == null || _material.HasProperty("_FocusDepth"))
                return;

            try
            {
                _sourcePixels = _colorTexture.GetPixels32();
                _depthPixels = _depthTexture.GetPixels32();
                _dofWidth = _colorTexture.width;
                _dofHeight = _colorTexture.height;
                if (_sourcePixels == null || _depthPixels == null || _sourcePixels.Length == 0 || _depthPixels.Length == 0)
                    return;

                _dofPixels = new Color32[_sourcePixels.Length];
                Array.Copy(_sourcePixels, _dofPixels, _sourcePixels.Length);
                _dofTexture = new Texture2D(_dofWidth, _dofHeight, TextureFormat.RGBA32, false);
                _dofTexture.wrapMode = TextureWrapMode.Clamp;
                _dofTexture.filterMode = FilterMode.Bilinear;
                _dofTexture.SetPixels32(_dofPixels);
                _dofTexture.Apply(false, false);
                _material.mainTexture = _dofTexture;
            }
            catch (Exception ex)
            {
                Log.Message("[FF9DepthVR] CPU DOF unavailable: " + ex.Message);
                _dofTexture = null;
            }
        }

        private void UpdateCpuDof(Vector2 focusUv, Single focusDepth)
        {
            if (_dofTexture == null || _sourcePixels == null || _depthPixels == null || _dofPixels == null)
                return;

            _dofTimer -= Time.deltaTime;
            if (_dofTimer > 0f && Mathf.Abs(focusDepth - _lastDofDepth) < 0.01f && (focusUv - _lastDofUv).sqrMagnitude < 0.001f)
                return;

            _dofTimer = 0.16f;
            _lastDofDepth = focusDepth;
            _lastDofUv = focusUv;
            Int32 depthWidth = _depthTexture.width;
            Int32 depthHeight = _depthTexture.height;
            for (Int32 y = 0; y < _dofHeight; y++)
            {
                Single v = _dofHeight > 1 ? (Single)y / (_dofHeight - 1) : 0f;
                for (Int32 x = 0; x < _dofWidth; x++)
                {
                    Single u = _dofWidth > 1 ? (Single)x / (_dofWidth - 1) : 0f;
                    Int32 index = y * _dofWidth + x;
                    Single depth = DepthPixel(u, v, depthWidth, depthHeight);
                    Single focusFalloff = Mathf.SmoothStep(0.015f, 0.18f, Vector2.Distance(new Vector2(u, v), focusUv));
                    Single edge = PixelEdge(index, x, y, depth, depthWidth, depthHeight, u, v);
                    Single blurAmount = Mathf.Clamp01(Mathf.Abs(depth - focusDepth) * (0.8f + focusFalloff * 1.7f) + edge * 1.2f);
                    Int32 radius = blurAmount < 0.08f ? 0 : Mathf.Clamp(Mathf.RoundToInt(blurAmount * 5f), 1, 5);
                    _dofPixels[index] = radius == 0 ? _sourcePixels[index] : BlurPixel(x, y, radius);
                }
            }

            _dofTexture.SetPixels32(_dofPixels);
            _dofTexture.Apply(false, false);
        }

        private Single DepthPixel(Single u, Single v, Int32 depthWidth, Int32 depthHeight)
        {
            Int32 x = Mathf.Clamp(Mathf.RoundToInt(u * (depthWidth - 1)), 0, depthWidth - 1);
            Int32 y = Mathf.Clamp(Mathf.RoundToInt(v * (depthHeight - 1)), 0, depthHeight - 1);
            Color32 c = _depthPixels[y * depthWidth + x];
            return c.r / 255f;
        }

        private Single PixelEdge(Int32 index, Int32 x, Int32 y, Single depth, Int32 depthWidth, Int32 depthHeight, Single u, Single v)
        {
            Color32 center = _sourcePixels[index];
            Single centerLuma = (center.r * 0.2126f + center.g * 0.7152f + center.b * 0.0722f) / 255f;
            Int32 left = y * _dofWidth + Mathf.Max(0, x - 1);
            Int32 right = y * _dofWidth + Mathf.Min(_dofWidth - 1, x + 1);
            Int32 up = Mathf.Min(_dofHeight - 1, y + 1) * _dofWidth + x;
            Int32 down = Mathf.Max(0, y - 1) * _dofWidth + x;
            Single lumaEdge = Mathf.Max(Mathf.Abs(centerLuma - Luma(_sourcePixels[left])), Mathf.Abs(centerLuma - Luma(_sourcePixels[right])));
            lumaEdge = Mathf.Max(lumaEdge, Mathf.Abs(centerLuma - Luma(_sourcePixels[up])));
            lumaEdge = Mathf.Max(lumaEdge, Mathf.Abs(centerLuma - Luma(_sourcePixels[down])));
            Single du = 1f / Mathf.Max(1, depthWidth);
            Single dv = 1f / Mathf.Max(1, depthHeight);
            Single depthEdge = Mathf.Max(Mathf.Abs(depth - DepthPixel(u + du, v, depthWidth, depthHeight)), Mathf.Abs(depth - DepthPixel(u - du, v, depthWidth, depthHeight)));
            depthEdge = Mathf.Max(depthEdge, Mathf.Abs(depth - DepthPixel(u, v + dv, depthWidth, depthHeight)));
            depthEdge = Mathf.Max(depthEdge, Mathf.Abs(depth - DepthPixel(u, v - dv, depthWidth, depthHeight)));
            return Mathf.Max(Mathf.SmoothStep(0.055f, 0.22f, lumaEdge), Mathf.SmoothStep(0.06f, 0.18f, depthEdge) * 0.85f);
        }

        private Single Luma(Color32 color)
        {
            return (color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f) / 255f;
        }

        private Color32 BlurPixel(Int32 x, Int32 y, Int32 radius)
        {
            Int32 r = 0;
            Int32 g = 0;
            Int32 b = 0;
            Int32 a = 0;
            Int32 count = 0;
            for (Int32 yy = -radius; yy <= radius; yy += radius)
            {
                for (Int32 xx = -radius; xx <= radius; xx += radius)
                {
                    Int32 sx = Mathf.Clamp(x + xx, 0, _dofWidth - 1);
                    Int32 sy = Mathf.Clamp(y + yy, 0, _dofHeight - 1);
                    Color32 sample = _sourcePixels[sy * _dofWidth + sx];
                    r += sample.r;
                    g += sample.g;
                    b += sample.b;
                    a += sample.a;
                    count++;
                }
            }
            return new Color32((Byte)(r / count), (Byte)(g / count), (Byte)(b / count), (Byte)(a / count));
        }

        private Vector2 MouseFocusUv()
        {
            if (Screen.width <= 0 || Screen.height <= 0)
                return new Vector2(0.5f, 0.5f);
            Single x = Mathf.Clamp01(Input.mousePosition.x / Screen.width);
            Single y = Mathf.Clamp01(Input.mousePosition.y / Screen.height);
            return new Vector2(x, y);
        }

        private Single SampleDepth(Vector2 focusUv)
        {
            if (_depthTexture == null)
                return 0.5f;

            try
            {
                return _depthTexture.GetPixelBilinear(focusUv.x, focusUv.y).grayscale;
            }
            catch
            {
                return 0.5f;
            }
        }

        private Single UpdateAutofocus(Single targetDepth, Single deltaSeconds, Single elapsedSeconds)
        {
            _rackElapsedSeconds += deltaSeconds;
            Single error = targetDepth - _autofocusDepth;
            _autofocusVelocity += error * 92f * deltaSeconds;
            _autofocusVelocity *= Mathf.Exp(-5.8f * deltaSeconds);
            _autofocusDepth = Mathf.Clamp01(_autofocusDepth + _autofocusVelocity * deltaSeconds);

            Single rackTravel = _rackTargetDepth - _rackStartDepth;
            Single rackDirection = Mathf.Sign(Mathf.Abs(rackTravel) < 0.0001f ? 1f : rackTravel);
            Single rackAmount = Mathf.Min(0.16f, 0.035f + Mathf.Abs(rackTravel) * 0.7f);
            Single rackEnvelope = Mathf.Exp(-_rackElapsedSeconds * 3.2f);
            Single wrongWayPull = -rackDirection * rackAmount * Mathf.Exp(-_rackElapsedSeconds * 10f);
            Single hunt = Mathf.Sin(_rackElapsedSeconds * 21f + Mathf.PI * 0.35f) * rackAmount * rackEnvelope;
            Single microHunt = Mathf.Sin(elapsedSeconds * 31f) * Mathf.Min(0.006f, Mathf.Abs(error) * 0.08f);
            return Mathf.Clamp01(_autofocusDepth + wrongWayPull + hunt + microHunt);
        }

        public Vector2 PlateOffset(Single plateX, Single plateY, Single depthCenter)
        {
            Single edgeFadeX = Mathf.Clamp01(Mathf.Min(plateX, _width - plateX) / Mathf.Max(1f, _width * 0.08f));
            Single edgeFadeY = Mathf.Clamp01(Mathf.Min(plateY, _height - plateY) / Mathf.Max(1f, _height * 0.08f));
            Single edgeFade = Mathf.Min(edgeFadeX, edgeFadeY);
            return new Vector2(depthCenter * _currentOffsetX * edgeFade, depthCenter * _currentOffsetY * edgeFade);
        }

        public Vector2 PlatePoint(Single plateX, Single plateY, Single depthCenter)
        {
            Vector2 offset = PlateOffset(plateX, plateY, depthCenter);
            Single x = plateX + offset.x;
            Single y = plateY + offset.y;
            if (_smoothedZoom > 0.0001f)
            {
                Single pivotX = _focusPivotX * _width;
                Single pivotY = _focusPivotY * _height;
                Single scale = 1f + _smoothedZoom;
                x = pivotX + (x - pivotX) * scale;
                y = pivotY + (y - pivotY) * scale;
            }
            return new Vector2(x, y);
        }

        public Vector2 PlateTransformOffset(Single plateX, Single plateY, Single depthCenter)
        {
            Vector2 transformed = PlatePoint(plateX, plateY, depthCenter);
            return new Vector2(transformed.x - plateX, transformed.y - plateY);
        }

        public void SetActorFocus(Single plateX, Single plateY, Boolean hasActors)
        {
            if (!hasActors)
            {
                _focusBiasX = 0f;
                _focusBiasY = 0f;
                _focusZoom = 0f;
                return;
            }

            _focusPivotX = Mathf.Clamp01(plateX / Mathf.Max(1f, _width));
            _focusPivotY = Mathf.Clamp01(plateY / Mathf.Max(1f, _height));
            _focusBiasX = Mathf.Clamp((_focusPivotX - 0.5f) * 0.35f, -0.2f, 0.2f);
            _focusBiasY = Mathf.Clamp((_focusPivotY - 0.5f) * 0.35f, -0.2f, 0.2f);
            _focusZoom = 0.025f;
        }
    }

    public sealed class FF9DepthVRPlateCommandBuffer : MonoBehaviour
    {
        private Camera _camera;
        private Mesh _mesh;
        private Material _material;
        private Transform _transform;
        private CommandBuffer _commandBuffer;
        private Boolean _isInstalled;
        private Boolean _lastVisible;

        public void Initialize(Camera camera, Mesh mesh, Material material, Transform transform)
        {
            _camera = camera;
            _mesh = mesh;
            _material = material;
            _transform = transform;
            _lastVisible = false;
            ApplyVisibility(true);
        }

        private void LateUpdate()
        {
            ApplyVisibility(FF9DepthVRFieldRenderer.PlateVisible);
        }

        private void OnDisable()
        {
            RemoveBuffer();
        }

        private void OnDestroy()
        {
            RemoveBuffer();
        }

        private void ApplyVisibility(Boolean visible)
        {
            if (_lastVisible == visible && _isInstalled == visible)
                return;

            _lastVisible = visible;
            if (visible)
                InstallBuffer();
            else
                RemoveBuffer();
        }

        private void InstallBuffer()
        {
            if (_isInstalled || _camera == null || _mesh == null || _material == null || _transform == null)
                return;

            _commandBuffer = new CommandBuffer();
            _commandBuffer.name = "FF9DepthVR Background Plate";
            _commandBuffer.DrawMesh(_mesh, _transform.localToWorldMatrix, _material);
            _camera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, _commandBuffer);
            _isInstalled = true;
            Log.Message("[FF9DepthVR] Plate command buffer installed on " + _camera.name + " event=BeforeForwardOpaque");
        }

        private void RemoveBuffer()
        {
            if (!_isInstalled || _camera == null || _commandBuffer == null)
            {
                _isInstalled = false;
                _commandBuffer = null;
                return;
            }

            _camera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, _commandBuffer);
            _commandBuffer.Release();
            _commandBuffer = null;
            _isInstalled = false;
            Log.Message("[FF9DepthVR] Plate command buffer removed");
        }
    }

    public sealed class FF9DepthVRActorComposite : MonoBehaviour
    {
        private global::FieldMap _fieldMap;
        private Texture2D _depthTexture;
        private Single _plateWidth;
        private Single _plateHeight;
        private Int32 _renderQueue;
        private Single _strength;
        private Boolean _forceCompositeShader;
        private Single _prepareTimer;
        private Single _logTimer;
        private Shader _compositeShader;
        private FF9DepthVRParallax _parallax;
        private Single _deepLogTimer;
        private Int32 _deepLogFrame;
        private const Single ActorPinDiagnosticScale = 1f;
        private const Single ActorPinStrength = 0.5f;
        private const Single ActorPinMaxWorldDelta = 180f;
        private const Int32 DeepLogActorLimit = 8;

        public void Initialize(global::FieldMap fieldMap, Texture2D depthTexture, Single plateWidth, Single plateHeight, Int32 renderQueue, Single strength, Boolean forceCompositeShader, FF9DepthVRParallax parallax)
        {
            _fieldMap = fieldMap;
            _depthTexture = depthTexture;
            _plateWidth = Mathf.Max(1f, plateWidth);
            _plateHeight = Mathf.Max(1f, plateHeight);
            _renderQueue = renderQueue;
            _strength = Mathf.Max(0f, strength);
            _forceCompositeShader = forceCompositeShader;
            _parallax = parallax;
            FF9DepthVRFieldRenderer.RegisterActorComposite(this);
            _compositeShader = ShadersLoader.Find("PSX/FieldMapActor");
            if (_compositeShader == null)
                _compositeShader = Shader.Find("PSX/FieldMapActor");
        }

        private void OnDestroy()
        {
            FF9DepthVRFieldRenderer.UnregisterActorComposite(this);
        }

        private void LateUpdate()
        {
            ApplyActorDepthOffsets(true);
        }

        internal void ApplyActorDepthOffsets(Boolean prepareRenderers)
        {
            if (_fieldMap == null)
                return;
            if (_parallax != null)
                _parallax.RefreshGeometryNow();

            FieldMapActor[] actors = UnityEngine.Object.FindObjectsOfType<FieldMapActor>();
            _prepareTimer -= Time.deltaTime;
            _logTimer -= Time.deltaTime;
            Boolean shouldPrepare = prepareRenderers && _prepareTimer <= 0f;
            if (shouldPrepare)
                _prepareTimer = 1f;

            Int32 compositedActors = 0;
            Int32 compositedRenderers = 0;
            Int32 totalRenderers = 0;
            Vector3 focusAccumulator = Vector3.zero;
            Int32 focusCount = 0;
            Vector2 offsetAccumulator = Vector2.zero;
            Single maxOffset = 0f;
            Single maxScreenDelta = 0f;
            Int32 materialPinCount = 0;
            Int32 materialOffsetCount = 0;
            Int32 surfaceHitCount = 0;
            Boolean shouldDeepLog = _deepLogTimer <= 0f;
            StringBuilder deepLog = shouldDeepLog ? BeginDeepDebugLog(actors.Length, prepareRenderers) : null;
            Int32 deepLoggedActors = 0;
            for (Int32 i = 0; i < actors.Length; i++)
            {
                FieldMapActor actor = actors[i];
                if (actor == null || actor.transform == null || actor.transform.root != _fieldMap.transform.root)
                    continue;

                Vector3 projectedFoot = ProjectActorWalkmeshPoint(actor);
                Int32 actorRendererCount;
                if (shouldPrepare)
                    compositedRenderers += PrepareActorRenderers(actor, out actorRendererCount);
                else
                    actorRendererCount = CountBodyRenderers(actor);
                totalRenderers += actorRendererCount;
                compositedActors++;

                Vector2 actorOffset;
                Vector2 plateOffset;
                Single depthCenter;
                Vector3 plateFoot;
                if (TryActorSurfaceOffset(projectedFoot, out actorOffset, out plateOffset, out depthCenter, out plateFoot))
                    surfaceHitCount++;
                else
                {
                    actorOffset = ActorProjectionOffset(projectedFoot);
                    plateOffset = Vector2.zero;
                    depthCenter = SampleDepthAt(projectedFoot) - 0.5f;
                    plateFoot = ProjectedToPlateLocal(projectedFoot);
                }
                Int32 offsetMaterials;
                Single screenDelta = ApplyVisualActorGroundGlue(actor, projectedFoot, actorOffset, out offsetMaterials);
                if (shouldDeepLog && deepLoggedActors < DeepLogActorLimit)
                {
                    AppendActorDeepDebug(deepLog, actor, projectedFoot, plateFoot, depthCenter, plateOffset, actorOffset, offsetMaterials, screenDelta);
                    deepLoggedActors++;
                }
                materialOffsetCount += offsetMaterials;
                if (screenDelta > 0.001f)
                    materialPinCount++;
                offsetAccumulator += actorOffset;
                maxOffset = Mathf.Max(maxOffset, actorOffset.magnitude);
                maxScreenDelta = Mathf.Max(maxScreenDelta, screenDelta);
                focusAccumulator += projectedFoot;
                focusCount++;
            }
            if (_parallax != null)
                _parallax.SetActorFocus(0f, 0f, false);

            if (shouldDeepLog)
            {
                _deepLogTimer = 2f;
                _deepLogFrame = Time.frameCount;
                deepLog.Append(" totals actors=").Append(compositedActors)
                    .Append(" surfaceHits=").Append(surfaceHitCount)
                    .Append(" materialsWithOffset=").Append(materialOffsetCount)
                    .Append(" materialPins=").Append(materialPinCount)
                    .Append(" avgOffset=").Append(compositedActors > 0 ? (offsetAccumulator / compositedActors).ToString() : Vector2.zero.ToString())
                    .Append(" maxOffset=").Append(maxOffset.ToString("F3"))
                    .Append(" maxScreenDelta=").Append(maxScreenDelta.ToString("F3"));
                Log.Message(deepLog.ToString());
            }

            if (_logTimer <= 0f)
            {
                _logTimer = 5f;
                Vector2 avgOffset = compositedActors > 0 ? offsetAccumulator / compositedActors : Vector2.zero;
                Log.Message("[FF9DepthVR] Actor composite actors=" + compositedActors + " bodyRenderers=" + totalRenderers + " prepared=" + compositedRenderers + " queue=" + _renderQueue + " shader=" + (_forceCompositeShader && _compositeShader != null ? _compositeShader.name : "original") + " depthCast=walkmesh offset=depthPlate avgOffset=" + avgOffset + " maxOffset=" + maxOffset.ToString("F2") + " pinScale=" + ActorPinDiagnosticScale.ToString("F1") + " pinMode=plateWarp surfaceHits=" + surfaceHitCount + " materialsWithOffset=" + materialOffsetCount + " materialPins=" + materialPinCount + " maxScreenDelta=" + maxScreenDelta.ToString("F2") + " afterSmooth=" + (!prepareRenderers));
            }
        }

        private StringBuilder BeginDeepDebugLog(Int32 rawActorCount, Boolean prepareRenderers)
        {
            _deepLogTimer = 2f;
            StringBuilder builder = new StringBuilder(4096);
            Vector2 fieldOffset = FF9StateSystem.Common.FF9.projectionOffset;
            BGCAM_DEF bgCamera = _fieldMap != null ? _fieldMap.GetCurrentBgCamera() : null;
            Camera mainCamera = _fieldMap != null ? _fieldMap.GetMainCamera() : null;
            builder.Append("[FF9DepthVR:DeepActorPin] frame=").Append(Time.frameCount)
                .Append(" lastFrame=").Append(_deepLogFrame)
                .Append(" rawActors=").Append(rawActorCount)
                .Append(" prepare=").Append(prepareRenderers)
                .Append(" map=").Append(FF9StateSystem.Common.FF9.fldMapNo)
                .Append(" scene=").Append(FF9StateSystem.Common.FF9.mapNameStr)
                .Append(" plate=").Append(_plateWidth.ToString("F1")).Append("x").Append(_plateHeight.ToString("F1"))
                .Append(" psxField=").Append(FieldMap.PsxFieldWidth).Append("x").Append(FieldMap.HalfFieldHeight * 2)
                .Append(" half=").Append(FieldMap.HalfFieldWidth).Append(",").Append(FieldMap.HalfFieldHeight)
                .Append(" shaderMul=").Append(FieldMap.ShaderMulX.ToString("F5")).Append(",").Append(FieldMap.ShaderMulY.ToString("F5"))
                .Append(" fieldOffset=").Append(fieldOffset)
                .Append(" screen=").Append(Screen.width).Append("x").Append(Screen.height);
            if (bgCamera != null)
                builder.Append(" bgCam=").Append(bgCamera.w).Append("x").Append(bgCamera.h)
                    .Append(" center=").Append(bgCamera.centerOffset[0]).Append(",").Append(bgCamera.centerOffset[1])
                    .Append(" proj=").Append(bgCamera.proj)
                    .Append(" depthOffset=").Append(bgCamera.depthOffset);
            if (mainCamera != null)
                builder.Append(" unityCamPos=").Append(mainCamera.transform.localPosition)
                    .Append(" ortho=").Append(mainCamera.orthographic)
                    .Append(" fov=").Append(mainCamera.fieldOfView.ToString("F2"));
            return builder;
        }

        private void AppendActorDeepDebug(StringBuilder builder, FieldMapActor actor, Vector3 projectedFoot, Vector3 plateFoot, Single depthCenter, Vector2 plateOffset, Vector2 actorOffset, Int32 offsetMaterials, Single screenDelta)
        {
            if (builder == null || actor == null)
                return;

            FieldMapActorController controller = actor.GetComponent<FieldMapActorController>();
            Vector2 fieldOffset = FF9StateSystem.Common.FF9.projectionOffset;
            Single firstX;
            Single firstY;
            String firstShader;
            Boolean hasFirst = TryReadFirstProjectionMaterial(actor, out firstX, out firstY, out firstShader);
            Vector3 actorProjected = actor.projectedPos;
            Vector3 actorPos = actor.transform.localPosition;
            Vector3 curPos = controller != null ? controller.curPos : actorPos;
            Int32 uid = actor.actor != null ? actor.actor.uid : -1;
            Int32 sid = actor.actor != null ? actor.actor.sid : -1;
            Int32 activeTri = controller != null ? controller.activeTri : -1;
            builder.Append(" | actor name=").Append(actor.name)
                .Append(" uid=").Append(uid)
                .Append(" sid=").Append(sid)
                .Append(" tri=").Append(activeTri)
                .Append(" cur=").Append(curPos)
                .Append(" local=").Append(actorPos)
                .Append(" actorProj=").Append(actorProjected)
                .Append(" foot=").Append(projectedFoot)
                .Append(" plateFoot=").Append(plateFoot)
                .Append(" depth=").Append((depthCenter + 0.5f).ToString("F3"))
                .Append(" depthCenter=").Append(depthCenter.ToString("F3"))
                .Append(" plateOffset=").Append(plateOffset)
                .Append(" shaderOffset=").Append(actorOffset)
                .Append(" finalOffset=").Append(new Vector2(fieldOffset.x + actorOffset.x, fieldOffset.y + actorOffset.y))
                .Append(" screenDelta=").Append(screenDelta.ToString("F3"))
                .Append(" mats=").Append(offsetMaterials);
            if (hasFirst)
                builder.Append(" firstMat=").Append(firstShader).Append("@").Append(firstX.ToString("F3")).Append(",").Append(firstY.ToString("F3"));
            if (actor.shadowTran != null)
                builder.Append(" shadowLocal=").Append(actor.shadowTran.localPosition).Append(" shadowWorld=").Append(actor.shadowTran.position);
        }

        private Boolean TryActorSurfaceOffset(Vector3 projectedFoot, out Vector2 offset, out Vector2 plateOffset, out Single depthCenter, out Vector3 plateFoot)
        {
            offset = Vector2.zero;
            plateOffset = Vector2.zero;
            depthCenter = 0f;
            plateFoot = ProjectedToPlateLocal(projectedFoot);
            if (_parallax == null || !FF9DepthVRFieldRenderer.PlateVisible)
                return false;

            depthCenter = SampleDepthAt(plateFoot) - 0.5f;
            plateOffset = _parallax.PlateTransformOffset(plateFoot.x, plateFoot.y, depthCenter);
            offset = PlateOffsetToActorShaderOffset(plateOffset);
            if (!IsFinite(offset.x) || !IsFinite(offset.y))
            {
                offset = Vector2.zero;
                return false;
            }

            return true;
        }

        private Vector3 ProjectedToPlateLocal(Vector3 projected)
        {
            if (_fieldMap == null)
                return projected;

            BGCAM_DEF bgCamera = _fieldMap.GetCurrentBgCamera();
            if (bgCamera == null)
                return projected;

            Vector2 fieldOffset = _fieldMap.GetProjectionOffset();
            Single plateX = (bgCamera.w * 0.5f) + bgCamera.centerOffset[0] + projected.x - fieldOffset.x;
            Single plateY = -((bgCamera.h * 0.5f) + bgCamera.centerOffset[1] + projected.y + fieldOffset.y - (2f * FieldMap.HalfFieldHeight));
            if (bgCamera.w != 0)
                plateX *= _plateWidth / bgCamera.w;
            if (bgCamera.h != 0)
                plateY *= _plateHeight / bgCamera.h;
            return new Vector3(plateX, plateY, projected.z);
        }

        private Vector2 PlateOffsetToActorShaderOffset(Vector2 plateOffset)
        {
            Single xScale = (Single)FieldMap.PsxFieldWidth / Mathf.Max(1f, _plateWidth);
            Single yScale = (Single)(FieldMap.HalfFieldHeight * 2) / Mathf.Max(1f, _plateHeight);
            return new Vector2(plateOffset.x * xScale * ActorPinStrength, plateOffset.y * yScale * ActorPinStrength);
        }

        private Vector2 ActorProjectionOffset(Vector3 projectedFoot)
        {
            if (_parallax == null || !FF9DepthVRFieldRenderer.PlateVisible)
                return Vector2.zero;

            Single depthCenter = SampleDepthAt(projectedFoot) - 0.5f;
            Vector2 plateOffset = _parallax.PlateTransformOffset(projectedFoot.x, projectedFoot.y, depthCenter);
            Single actorScale = _strength / Mathf.Max(0.001f, 7f);
            Vector2 shaderOffset = PlateOffsetToActorShaderOffset(plateOffset);
            return new Vector2(shaderOffset.x * actorScale * ActorPinDiagnosticScale, shaderOffset.y * actorScale * ActorPinDiagnosticScale);
        }

        private Single ApplyVisualActorGroundGlue(FieldMapActor actor, Vector3 projectedFoot, Vector2 screenOffset, out Int32 offsetMaterials)
        {
            offsetMaterials = 0;
            FieldMapActorController controller = actor.GetComponent<FieldMapActorController>();
            if (controller == null)
                return 0f;

            Vector3 realPosition = controller.curPos;
            if (_parallax == null || !FF9DepthVRFieldRenderer.PlateVisible)
            {
                actor.transform.localPosition = realPosition;
                offsetMaterials = ApplyActorProjectionOffset(actor, Vector2.zero);
                return 0f;
            }

            actor.transform.localPosition = realPosition;
            offsetMaterials = ApplyActorProjectionOffset(actor, screenOffset);
            return screenOffset.magnitude;
        }

        private Boolean ProjectScreenOffsetThroughWalkmesh(FieldMapActor actor, Vector3 projectedFoot, Vector2 screenOffset, out Vector3 delta)
        {
            delta = Vector3.zero;
            if (screenOffset.sqrMagnitude < 0.0001f)
                return true;

            FieldMapActorController controller = actor.GetComponent<FieldMapActorController>();
            if (controller == null || controller.walkMesh == null || controller.walkMesh.tris == null)
                return false;

            Int32 activeTri = controller.activeTri;
            if (activeTri < 0 || activeTri >= controller.walkMesh.tris.Count)
                return false;

            WalkMeshTriangle triangle = controller.walkMesh.tris[activeTri];
            if (triangle == null || triangle.originalVertices == null || triangle.transformedVertices == null)
                return false;

            Vector2 targetScreen = new Vector2(projectedFoot.x + screenOffset.x, projectedFoot.y + screenOffset.y);
            Vector3 targetBarycentric = CalculateScreenBarycentric(
                targetScreen,
                triangle.transformedVertices[0],
                triangle.transformedVertices[1],
                triangle.transformedVertices[2]
            );

            if (!IsFinite(targetBarycentric.x) || !IsFinite(targetBarycentric.y) || !IsFinite(targetBarycentric.z))
                return false;

            Vector3 targetPosition = triangle.originalVertices[0] * targetBarycentric.x
                + triangle.originalVertices[1] * targetBarycentric.y
                + triangle.originalVertices[2] * targetBarycentric.z;
            delta = targetPosition - controller.curPos;
            if (!IsFinite(delta.x) || !IsFinite(delta.y) || !IsFinite(delta.z) || delta.magnitude > ActorPinMaxWorldDelta)
            {
                delta = Vector3.zero;
                return false;
            }
            return IsFinite(delta.x) && IsFinite(delta.y) && IsFinite(delta.z);
        }

        private static Vector3 CalculateScreenBarycentric(Vector2 point, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector2 a2 = new Vector2(a.x, a.y);
            Vector2 b2 = new Vector2(b.x, b.y);
            Vector2 c2 = new Vector2(c.x, c.y);
            Vector2 v0 = b2 - a2;
            Vector2 v1 = c2 - a2;
            Vector2 v2 = point - a2;
            Single d00 = Vector2.Dot(v0, v0);
            Single d01 = Vector2.Dot(v0, v1);
            Single d11 = Vector2.Dot(v1, v1);
            Single d20 = Vector2.Dot(v2, v0);
            Single d21 = Vector2.Dot(v2, v1);
            Single denominator = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denominator) < 0.0001f)
                return new Vector3(Single.NaN, Single.NaN, Single.NaN);

            Single y = (d11 * d20 - d01 * d21) / denominator;
            Single z = (d00 * d21 - d01 * d20) / denominator;
            Single x = 1f - y - z;
            return new Vector3(x, y, z);
        }

        private static Boolean IsFinite(Single value)
        {
            return !Single.IsNaN(value) && !Single.IsInfinity(value);
        }

        private Vector3 ProjectScreenOffsetToWorldDelta(Vector3 origin, Vector2 screenOffset)
        {
            if (_fieldMap == null || screenOffset.sqrMagnitude < 0.0001f)
                return Vector3.zero;

            if (Single.IsNaN(screenOffset.x) || Single.IsNaN(screenOffset.y) || Single.IsInfinity(screenOffset.x) || Single.IsInfinity(screenOffset.y))
                return Vector3.zero;

            BGCAM_DEF bgCamera = _fieldMap.GetCurrentBgCamera();
            if (bgCamera == null)
                return Vector3.zero;

            Vector2 projectionOffset = _fieldMap.GetProjectionOffset();
            Matrix4x4 matrix = bgCamera.GetMatrixRT();
            Single viewDistance = bgCamera.GetViewDistance();
            Vector3 p0 = PSX.CalculateGTE_RTPT(origin, Matrix4x4.identity, matrix, viewDistance, projectionOffset);
            const Single probe = 64f;
            Vector3 px = PSX.CalculateGTE_RTPT(origin + new Vector3(probe, 0f, 0f), Matrix4x4.identity, matrix, viewDistance, projectionOffset);
            Vector3 pz = PSX.CalculateGTE_RTPT(origin + new Vector3(0f, 0f, probe), Matrix4x4.identity, matrix, viewDistance, projectionOffset);
            Vector2 ax = new Vector2((px.x - p0.x) / probe, (px.y - p0.y) / probe);
            Vector2 az = new Vector2((pz.x - p0.x) / probe, (pz.y - p0.y) / probe);
            Single det = ax.x * az.y - ax.y * az.x;
            if (Mathf.Abs(det) < 0.0001f)
                return Vector3.zero;

            Single dx = (screenOffset.x * az.y - screenOffset.y * az.x) / det;
            Single dz = (ax.x * screenOffset.y - ax.y * screenOffset.x) / det;
            Vector3 delta = new Vector3(dx, 0f, dz);
            return delta.magnitude <= ActorPinMaxWorldDelta ? delta : Vector3.zero;
        }

        private Single SampleActorGroundDepth(FieldMapActor actor)
        {
            return SampleDepthAt(ProjectActorWalkmeshPoint(actor));
        }

        private Single SampleDepthAt(Vector3 projectedFoot)
        {
            if (_depthTexture == null)
                return 0.5f;

            Single u = Mathf.Clamp01(projectedFoot.x / _plateWidth);
            Single v = Mathf.Clamp01(projectedFoot.y / _plateHeight);
            try
            {
                return _depthTexture.GetPixelBilinear(u, 1f - v).grayscale;
            }
            catch
            {
                return 0.5f;
            }
        }

        private Vector3 ProjectActorWalkmeshPoint(FieldMapActor actor)
        {
            FieldMapActorController controller = actor.GetComponent<FieldMapActorController>();
            if (controller == null || controller.walkMesh == null || controller.walkMesh.tris == null)
                return actor.projectedPos;

            Int32 activeTri = controller.activeTri;
            if (activeTri < 0 || activeTri >= controller.walkMesh.tris.Count)
                return actor.projectedPos;

            WalkMeshTriangle triangle = controller.walkMesh.tris[activeTri];
            if (triangle == null || triangle.originalVertices == null || triangle.transformedVertices == null)
                return actor.projectedPos;

            Vector3 barycentric = Math3D.CalculateBarycentricRatioXZ(
                controller.curPos,
                triangle.originalVertices[0],
                triangle.originalVertices[1],
                triangle.originalVertices[2]
            );

            if (Single.IsNaN(barycentric.x) || Single.IsNaN(barycentric.y) || Single.IsNaN(barycentric.z))
                return actor.projectedPos;

            return triangle.transformedVertices[0] * barycentric.x
                + triangle.transformedVertices[1] * barycentric.y
                + triangle.transformedVertices[2] * barycentric.z;
        }

        private Int32 PrepareActorRenderers(FieldMapActor actor, out Int32 bodyRendererCount)
        {
            bodyRendererCount = 0;
            Renderer[] renderers = actor.GetComponentsInChildren<Renderer>(true);
            for (Int32 r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null)
                    continue;
                if (IsShadowRenderer(renderer))
                    continue;

                bodyRendererCount++;
                renderer.enabled = true;
                Material[] materials = renderer.materials;
                for (Int32 m = 0; m < materials.Length; m++)
                {
                    Material material = materials[m];
                    if (material == null)
                        continue;

                    if (_forceCompositeShader && _compositeShader != null && !IsShadowMaterial(material) && material.shader != _compositeShader)
                        material.shader = _compositeShader;
                    if (_renderQueue >= 0)
                        material.renderQueue = _renderQueue;
                    if (material.HasProperty("_Cutoff"))
                        material.SetFloat("_Cutoff", 0.1f);
                }
            }
            return bodyRendererCount;
        }

        private Int32 ApplyActorProjectionOffset(FieldMapActor actor, Vector2 actorOffset)
        {
            Int32 changed = 0;
            Vector2 fieldOffset = FF9StateSystem.Common.FF9.projectionOffset;
            Renderer[] renderers = actor.GetComponentsInChildren<Renderer>(true);
            for (Int32 r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null)
                    continue;

                changed += ApplyRendererProjectionOffset(renderer, fieldOffset, actorOffset);
            }

            if (actor.shadowTran != null)
            {
                Renderer shadowRenderer = actor.shadowTran.GetComponent<Renderer>();
                if (shadowRenderer != null)
                    changed += ApplyRendererProjectionOffset(shadowRenderer, fieldOffset, actorOffset);
            }
            return changed;
        }

        private Int32 ApplyRendererProjectionOffset(Renderer renderer, Vector2 fieldOffset, Vector2 actorOffset)
        {
            Int32 changed = 0;
            Material[] materials = renderer.materials;
            for (Int32 m = 0; m < materials.Length; m++)
            {
                Material material = materials[m];
                if (material == null)
                    continue;

                if (!material.HasProperty("_OffsetX") || !material.HasProperty("_OffsetY"))
                    continue;
                material.SetFloat("_OffsetX", fieldOffset.x + actorOffset.x);
                material.SetFloat("_OffsetY", fieldOffset.y + actorOffset.y);
                changed++;
            }
            return changed;
        }

        private Boolean TryReadFirstProjectionMaterial(FieldMapActor actor, out Single offsetX, out Single offsetY, out String shaderName)
        {
            offsetX = 0f;
            offsetY = 0f;
            shaderName = String.Empty;
            Renderer[] renderers = actor.GetComponentsInChildren<Renderer>(true);
            for (Int32 r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null)
                    continue;

                Material[] materials = renderer.materials;
                for (Int32 m = 0; m < materials.Length; m++)
                {
                    Material material = materials[m];
                    if (material == null || !material.HasProperty("_OffsetX") || !material.HasProperty("_OffsetY"))
                        continue;

                    offsetX = material.GetFloat("_OffsetX");
                    offsetY = material.GetFloat("_OffsetY");
                    shaderName = material.shader != null ? material.shader.name : "null";
                    return true;
                }
            }
            if (actor.shadowTran != null)
            {
                Renderer shadowRenderer = actor.shadowTran.GetComponent<Renderer>();
                if (shadowRenderer != null)
                {
                    Material material = shadowRenderer.material;
                    if (material != null && material.HasProperty("_OffsetX") && material.HasProperty("_OffsetY"))
                    {
                        offsetX = material.GetFloat("_OffsetX");
                        offsetY = material.GetFloat("_OffsetY");
                        shaderName = material.shader != null ? material.shader.name : "null";
                        return true;
                    }
                }
            }
            return false;
        }

        private Int32 CountBodyRenderers(FieldMapActor actor)
        {
            Int32 count = 0;
            Renderer[] renderers = actor.GetComponentsInChildren<Renderer>(true);
            for (Int32 r = 0; r < renderers.Length; r++)
                if (renderers[r] != null && !IsShadowRenderer(renderers[r]))
                    count++;
            return count;
        }

        private Boolean IsShadowRenderer(Renderer renderer)
        {
            if (renderer == null)
                return false;
            if (renderer.name != null && renderer.name.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            Material sharedMaterial = renderer.sharedMaterial;
            return IsShadowMaterial(sharedMaterial);
        }

        private Boolean IsShadowMaterial(Material material)
        {
            if (material == null || material.shader == null || material.shader.name == null)
                return false;
            return material.shader.name.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public sealed class FF9DepthVRMaskWarp : MonoBehaviour
    {
        private global::FieldMap _fieldMap;
        private FF9DepthVRParallax _parallax;
        private Texture2D _depthTexture;
        private Single _plateWidth;
        private Single _plateHeight;
        private Int32 _actorQueue;
        private Transform _plateParent;
        private MeshEntry[] _entries;
        private readonly Dictionary<MeshFilter, Vector3[]> _baseVerticesByFilter = new Dictionary<MeshFilter, Vector3[]>();
        private Single _refreshTimer;
        private Single _logTimer;
        private Int32 _lastWarpedVertices;
        private const Int32 MaxWarpVertices = 12000;

        public void Initialize(global::FieldMap fieldMap, FF9DepthVRParallax parallax, Texture2D depthTexture, Single plateWidth, Single plateHeight, Int32 actorQueue)
        {
            _fieldMap = fieldMap;
            _parallax = parallax;
            _depthTexture = depthTexture;
            _plateWidth = Mathf.Max(1f, plateWidth);
            _plateHeight = Mathf.Max(1f, plateHeight);
            _actorQueue = actorQueue >= 0 ? actorQueue : ActorRenderQueue.AlphaTest;
            _plateParent = transform.parent;
            RefreshEntries();
        }

        private void LateUpdate()
        {
            if (_fieldMap == null || _parallax == null || _plateParent == null)
                return;

            _refreshTimer -= Time.deltaTime;
            if (_refreshTimer <= 0f || _entries == null)
            {
                _refreshTimer = 2f;
                RefreshEntries();
            }

            if (_entries == null)
                return;

            Int32 warped = 0;
            for (Int32 i = 0; i < _entries.Length; i++)
            {
                MeshEntry entry = _entries[i];
                if (entry == null || entry.Mesh == null || entry.Transform == null || entry.BaseVertices == null || entry.WorkingVertices == null)
                    continue;

                if (!FF9DepthVRFieldRenderer.PlateVisible)
                {
                    entry.Mesh.vertices = entry.BaseVertices;
                    continue;
                }

                for (Int32 v = 0; v < entry.BaseVertices.Length; v++)
                {
                    Vector3 local = entry.BaseVertices[v];
                    Vector3 plateLocal = _plateParent.InverseTransformPoint(entry.Transform.TransformPoint(local));
                    Single depthCenter = SampleDepth(plateLocal.x, plateLocal.y) - 0.5f;
                    Vector2 offset = _parallax.PlateTransformOffset(plateLocal.x, plateLocal.y, depthCenter);
                    Vector3 localOffset = entry.Transform.InverseTransformVector(_plateParent.TransformVector(new Vector3(offset.x, offset.y, 0f)));
                    entry.WorkingVertices[v] = local + localOffset;
                }
                entry.Mesh.vertices = entry.WorkingVertices;
                entry.Mesh.RecalculateBounds();
                warped++;
            }

            _logTimer -= Time.deltaTime;
            if (_logTimer <= 0f)
            {
                _logTimer = 5f;
                Log.Message("[FF9DepthVR] Mask warp meshes=" + warped + " vertices=" + _lastWarpedVertices);
            }
        }

        private void RefreshEntries()
        {
            if (_fieldMap == null)
                return;

            Transform background = _fieldMap.transform.Find("Background");
            if (background == null)
                return;

            MeshFilter[] filters = background.GetComponentsInChildren<MeshFilter>(true);
            List<MeshEntry> entries = new List<MeshEntry>();
            _lastWarpedVertices = 0;
            for (Int32 i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                if (filter == null || filter.sharedMesh == null || IsDepthReplacement(filter.transform))
                    continue;

                Renderer renderer = filter.GetComponent<Renderer>();
                if (renderer == null)
                    continue;
                if (!IsForegroundMask(renderer))
                    continue;

                Mesh mesh = filter.mesh;
                Vector3[] baseVertices;
                if (!_baseVerticesByFilter.TryGetValue(filter, out baseVertices))
                {
                    baseVertices = (Vector3[])mesh.vertices.Clone();
                    _baseVerticesByFilter[filter] = baseVertices;
                }
                else
                {
                    mesh.vertices = baseVertices;
                    mesh.RecalculateBounds();
                }
                if (baseVertices == null || baseVertices.Length == 0)
                    continue;
                if (_lastWarpedVertices + baseVertices.Length > MaxWarpVertices)
                    continue;

                entries.Add(new MeshEntry
                {
                    Mesh = mesh,
                    Transform = filter.transform,
                    BaseVertices = baseVertices,
                    WorkingVertices = new Vector3[baseVertices.Length]
                });
                _lastWarpedVertices += baseVertices.Length;
            }
            _entries = entries.ToArray();
            Log.Message("[FF9DepthVR] Mask warp captured meshes=" + _entries.Length + " vertices=" + _lastWarpedVertices);
        }

        private Boolean IsForegroundMask(Renderer renderer)
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
                materials = renderer.materials;

            for (Int32 i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material != null && material.renderQueue >= _actorQueue)
                    return true;
            }
            return false;
        }

        private Single SampleDepth(Single plateX, Single plateY)
        {
            if (_depthTexture == null)
                return 0.5f;

            try
            {
                Single u = Mathf.Clamp01(plateX / _plateWidth);
                Single v = Mathf.Clamp01(plateY / _plateHeight);
                return _depthTexture.GetPixelBilinear(u, 1f - v).grayscale;
            }
            catch
            {
                return 0.5f;
            }
        }

        private Boolean IsDepthReplacement(Transform transform)
        {
            while (transform != null)
            {
                if (transform.name == FF9DepthVRFieldRenderer.RootName)
                    return true;
                transform = transform.parent;
            }
            return false;
        }

        private sealed class MeshEntry
        {
            public Mesh Mesh;
            public Transform Transform;
            public Vector3[] BaseVertices;
            public Vector3[] WorkingVertices;
        }
    }

    public sealed class FF9DepthVRDiagnostics : MonoBehaviour
    {
        private global::FieldMap _fieldMap;
        private Renderer _renderer;
        private Single _logTimer;

        public void Initialize(global::FieldMap fieldMap, Renderer renderer)
        {
            _fieldMap = fieldMap;
            _renderer = renderer;
            ApplyVisibility();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F10))
            {
                FF9DepthVRFieldRenderer.PlateVisible = !FF9DepthVRFieldRenderer.PlateVisible;
                ApplyVisibility();
                FF9DepthVRFieldRenderer.SyncOriginalBackgroundVisibility(_fieldMap);
                Log.Message("[FF9DepthVR] F10 plate visible = " + FF9DepthVRFieldRenderer.PlateVisible);
            }

            _logTimer -= Time.deltaTime;
            if (_logTimer <= 0f)
            {
                _logTimer = 5f;
                FieldMapActor[] actors = UnityEngine.Object.FindObjectsOfType<FieldMapActor>();
                Log.Message("[FF9DepthVR] Diagnostics actors=" + actors.Length + " plateVisible=" + FF9DepthVRFieldRenderer.PlateVisible);
            }
        }

        private void ApplyVisibility()
        {
            if (_renderer != null)
                _renderer.enabled = FF9DepthVRFieldRenderer.PlateVisible;
        }
    }
}
