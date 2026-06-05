using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Assets.Sources.Graphics.Movie;
using Memoria.Prime;
using Memoria.Scripts;
using SimpleJSON;
using UnityEngine;
using UnityEngine.Rendering;

namespace Memoria.FF9DepthVR
{
    public static class FF9DepthVRFieldRenderer
    {
        public enum DepthViewMode
        {
            Depth = 0,
            Stereo3D = 1,
            Compare = 2
        }

        internal enum StereoIpdProfile
        {
            Field = 0,
            FieldMovie = 1,
            StandaloneMovie = 2,
            Battle = 3
        }

        private const String ManifestPath = "Data/FF9DepthVR/manifest.json";
        internal const String RootName = "FF9DepthVR_Background";
        internal const String FallbackRootName = "FF9DepthVR_FallbackBridge";
        private const Single DepthUnitScale = 32f;
        internal const Int32 ReplacementPlateRenderQueue = 1500;
        internal const Single ViewAngleMultiplier = 3f;
        internal const Single ViewAngleXMultiplier = 2f;
        private const Single DefaultFieldStereoIpd = 0f;
        private const Single DefaultFieldMovieStereoIpd = 0f;
        private const Single DefaultStandaloneMovieStereoIpd = 0.045f;
        private const Single DefaultBattleStereoIpd = 80f;
        private const Single FieldStereoIpdStep = 0.01f;
        private const Single FieldMovieStereoIpdStep = 0.01f;
        private const Single StandaloneMovieStereoIpdStep = 0.005f;
        private const Single BattleStereoIpdStep = 10f;

        private static readonly Dictionary<String, SceneEntry> ScenesByMapName = new Dictionary<String, SceneEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Material, Int32> OriginalBackgroundQueues = new Dictionary<Material, Int32>();
        private static readonly Dictionary<Camera, CameraFrameState> CameraFrames = new Dictionary<Camera, CameraFrameState>();
        private static FF9DepthVRActorComposite _activeActorComposite;
        private static Boolean _manifestLoaded;
        private static RenderDefaults _defaults = new RenderDefaults();
        private static String _lastLoggedSceneId;
        private static Int32 _lastSbsToggleFrame = -1;
        private static Int32 _lastActorLookToggleFrame = -1;
        private static Int32 _lastVrCaptureToggleFrame = -1;
        private static Int32 _lastMovieDebugToggleFrame = -1;
        private static Int32 _lastStereoIpdAdjustFrame = -1;
        private static Int32 _lastActorCameraScrollFrame = -1;
        private static Vector3 _lastInputLookMousePosition;
        private static Boolean _hasLastInputLookMousePosition;
        private static Boolean _controllerInputLookMode;
        private static Boolean _movieDebugOverlayEnabled;
        private static FF9DepthVRMovieBgPlate _activeMoviePlate;
        private static global::FieldMap _activeMovieFieldMap;
        private static FF9DepthVRNativeMoviePlaneSbsScaler _activeNativeMoviePlaneScaler;
        private static Single _fieldStereoIpd = DefaultFieldStereoIpd;
        private static Single _fieldMovieStereoIpd = DefaultFieldMovieStereoIpd;
        private static Single _standaloneMovieStereoIpd = DefaultStandaloneMovieStereoIpd;
        private static Single _battleStereoIpd = DefaultBattleStereoIpd;
        internal static Boolean PlateVisible = true;
        public static DepthViewMode ViewMode = DepthViewMode.Depth;
        public static Boolean SbsEnabled = false;
        public static Boolean VrCaptureEnabled = false;
        public static Boolean ActorLookEnabled = true;
        internal static Boolean ForegroundMaskDepthWarpEnabled => true;
        public static Boolean WasSbsToggleInputHandledThisFrame => _lastSbsToggleFrame == Time.frameCount;
        internal static Boolean SbsActive => VrCaptureEnabled || ViewMode != DepthViewMode.Depth;
        internal static Boolean CompareEnabled => !VrCaptureEnabled && ViewMode == DepthViewMode.Compare;
        internal static Boolean MovieDebugOverlayEnabled => _movieDebugOverlayEnabled;
        internal static Boolean MoviePlateActive => _activeMoviePlate != null && _activeMoviePlate.IsActive;
        internal static String ModeStatusText
        {
            get
            {
                String view = VrCaptureEnabled ? "VR" : ViewMode.ToString();
                String look = ActorLookEnabled ? "Actor+input look" : "Input look";
                StereoIpdProfile profile = GetCurrentFieldStereoIpdProfile();
                return "FF9DepthVR | View: " + view + " | Depth masks | " + look + " (F8) | IPD " + StereoIpdProfileLabel(profile) + " " + GetStereoIpd(profile).ToString("0.###", CultureInfo.InvariantCulture) + " (+/-)" + (IsMbgPlaybackActive() ? " | MBG" : String.Empty);
            }
        }
        internal static Boolean IsMbgPlaybackActive()
        {
            try
            {
                if (global::MBG.IsNull)
                    return false;

                global::MBG mbg = global::MBG.Instance;
                return mbg != null && !mbg.IsFinishedForDisableBooster();
            }
            catch
            {
                return false;
            }
        }

        private static Boolean IsFieldMovieKey(String movieKey)
        {
            return !String.IsNullOrEmpty(movieKey) && movieKey.StartsWith("mbg", StringComparison.OrdinalIgnoreCase);
        }

        internal static Single DefaultSourceScale => _defaults.SourceScale;
        internal static Single DefaultGeometryDepthScale => _defaults.GeometryDepthScale > 0f ? _defaults.GeometryDepthScale : Mathf.Max(1f, _defaults.DepthStrength * DepthUnitScale);
        internal static Single DefaultParallaxStrength => _defaults.ParallaxStrength;
        internal static Single DefaultIdleParallaxStrength => _defaults.IdleParallaxStrength;
        internal static Single MoviePlateDistance => 2f;
        internal static Single MovieStandaloneDepthScale => 0.06f;
        private static readonly Boolean EnableFieldMovieDepthPlate = false;
        private const Single ControllerLookDeadzone = 0.18f;
        private const Single ControllerLookMouseWakePixels = 2f;

        public static Boolean TryHandleSbsToggleInput()
        {
            Boolean actorLookHandled = TryHandleActorLookToggleInput();
            Boolean vrCaptureHandled = TryHandleVrCaptureToggleInput();
            if (!Input.GetKeyDown(KeyCode.F9) || _lastSbsToggleFrame == Time.frameCount)
                return actorLookHandled || vrCaptureHandled;

            _lastSbsToggleFrame = Time.frameCount;
            ViewMode = ViewMode == DepthViewMode.Depth ? DepthViewMode.Stereo3D : ViewMode == DepthViewMode.Stereo3D ? DepthViewMode.Compare : DepthViewMode.Depth;
            ApplySbsState();
            Log.Message("[FF9DepthVR] View mode = " + ViewMode + " (F9)");
            return true;
        }

        public static Boolean TryHandleVrCaptureToggleInput()
        {
            if (!Input.GetKeyDown(KeyCode.F7) || _lastVrCaptureToggleFrame == Time.frameCount)
                return false;

            _lastVrCaptureToggleFrame = Time.frameCount;
            VrCaptureEnabled = !VrCaptureEnabled;
            ApplySbsState();
            Log.Message("[FF9DepthVR] VR capture mode enabled = " + VrCaptureEnabled + " (F7)");
            return true;
        }

        private static void ApplySbsState()
        {
            SbsEnabled = VrCaptureEnabled || ViewMode != DepthViewMode.Depth;
        }

        internal static StereoIpdProfile GetCurrentFieldStereoIpdProfile()
        {
            return IsMbgPlaybackActive() ? StereoIpdProfile.FieldMovie : StereoIpdProfile.Field;
        }

        internal static Single GetStereoIpd(StereoIpdProfile profile)
        {
            switch (profile)
            {
                case StereoIpdProfile.FieldMovie:
                    return _fieldMovieStereoIpd;
                case StereoIpdProfile.StandaloneMovie:
                    return _standaloneMovieStereoIpd;
                case StereoIpdProfile.Battle:
                    return _battleStereoIpd;
                default:
                    return _fieldStereoIpd;
            }
        }

        internal static Single GetStereoEyeOffset(StereoIpdProfile profile)
        {
            return GetStereoIpd(profile) * 0.5f;
        }

        internal static Boolean TryHandleStereoIpdInput(StereoIpdProfile profile)
        {
            if (_lastStereoIpdAdjustFrame == Time.frameCount)
                return false;

            Single direction = 0f;
            if (Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Equals))
                direction = 1f;
            else if (Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Minus))
                direction = -1f;

            if (Mathf.Abs(direction) <= Single.Epsilon)
                return false;

            _lastStereoIpdAdjustFrame = Time.frameCount;
            Single value = AdjustStereoIpd(profile, direction);
            Log.Message("[FF9DepthVR] IPD " + StereoIpdProfileLabel(profile) + " = " + value.ToString("0.###", CultureInfo.InvariantCulture) + " (+/-)");
            return true;
        }

        private static Single AdjustStereoIpd(StereoIpdProfile profile, Single direction)
        {
            switch (profile)
            {
                case StereoIpdProfile.FieldMovie:
                    _fieldMovieStereoIpd = Mathf.Clamp(_fieldMovieStereoIpd + direction * FieldMovieStereoIpdStep, 0f, 0.25f);
                    return _fieldMovieStereoIpd;
                case StereoIpdProfile.StandaloneMovie:
                    _standaloneMovieStereoIpd = Mathf.Clamp(_standaloneMovieStereoIpd + direction * StandaloneMovieStereoIpdStep, 0f, 0.25f);
                    return _standaloneMovieStereoIpd;
                case StereoIpdProfile.Battle:
                    _battleStereoIpd = Mathf.Clamp(_battleStereoIpd + direction * BattleStereoIpdStep, 0f, 300f);
                    return _battleStereoIpd;
                default:
                    _fieldStereoIpd = Mathf.Clamp(_fieldStereoIpd + direction * FieldStereoIpdStep, 0f, 0.25f);
                    return _fieldStereoIpd;
            }
        }

        private static String StereoIpdProfileLabel(StereoIpdProfile profile)
        {
            switch (profile)
            {
                case StereoIpdProfile.FieldMovie:
                    return "Field-FMV";
                case StereoIpdProfile.StandaloneMovie:
                    return "FMV";
                case StereoIpdProfile.Battle:
                    return "Battle";
                default:
                    return "Field";
            }
        }

        internal static Boolean TryReadHeadTrackLook(out Vector2 look)
        {
            look = Vector2.zero;
            return VrCaptureEnabled && FF9DepthVRHeadTrackingBridge.TryReadLook(out look);
        }

        internal static Vector2 InputLookNormalized(Boolean includeMouse)
        {
            if (Screen.width <= 0 || Screen.height <= 0)
                return ApplyHeadTrackLook(Vector2.zero);

            Vector3 mousePosition = Input.mousePosition;
            Vector2 mouseLook = includeMouse
                ? new Vector2(
                    Mathf.Clamp((mousePosition.x / Screen.width - 0.5f) * 2f, -1f, 1f),
                    Mathf.Clamp((mousePosition.y / Screen.height - 0.5f) * 2f, -1f, 1f)
                )
                : Vector2.zero;

            Vector2 controllerLook;
            if (TryReadControllerLook(out controllerLook))
            {
                _controllerInputLookMode = true;
                _lastInputLookMousePosition = mousePosition;
                _hasLastInputLookMousePosition = true;
                return ApplyHeadTrackLook(controllerLook);
            }

            if (_controllerInputLookMode)
            {
                if (!_hasLastInputLookMousePosition)
                {
                    _lastInputLookMousePosition = mousePosition;
                    _hasLastInputLookMousePosition = true;
                }

                if ((mousePosition - _lastInputLookMousePosition).sqrMagnitude > ControllerLookMouseWakePixels * ControllerLookMouseWakePixels)
                {
                    _controllerInputLookMode = false;
                    _lastInputLookMousePosition = mousePosition;
                    return ApplyHeadTrackLook(mouseLook);
                }

                return ApplyHeadTrackLook(Vector2.zero);
            }

            _lastInputLookMousePosition = mousePosition;
            _hasLastInputLookMousePosition = true;
            return ApplyHeadTrackLook(mouseLook);
        }

        internal static Vector2 ApplyHeadTrackLook(Vector2 manualLook)
        {
            Vector2 headLook;
            if (!TryReadHeadTrackLook(out headLook))
                return manualLook;

            return new Vector2(
                Mathf.Clamp(manualLook.x + headLook.x, -1f, 1f),
                Mathf.Clamp(manualLook.y + headLook.y, -1f, 1f)
            );
        }

        private static Boolean TryReadControllerLook(out Vector2 look)
        {
            look = Vector2.zero;
            try
            {
                if (!HonoInputManager.ApplicationIsActivated())
                    return false;

                var state = UnityXInput.XInputManager.Instance.CurrentState;
                if (!state.IsConnected)
                    return false;

                Vector2 raw = new Vector2(state.ThumbSticks.Right.X, state.ThumbSticks.Right.Y);
                Single magnitude = raw.magnitude;
                if (magnitude <= ControllerLookDeadzone)
                    return false;

                Single scaledMagnitude = Mathf.InverseLerp(ControllerLookDeadzone, 1f, Mathf.Clamp01(magnitude));
                look = raw.normalized * scaledMagnitude;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static Boolean TryHandleActorLookToggleInput()
        {
            if (!Input.GetKeyDown(KeyCode.F8) || _lastActorLookToggleFrame == Time.frameCount)
                return false;

            _lastActorLookToggleFrame = Time.frameCount;
            ActorLookEnabled = !ActorLookEnabled;
            Log.Message("[FF9DepthVR] Actor look assist enabled = " + ActorLookEnabled + " (F8)");
            return true;
        }

        internal static Boolean TryHandleMovieDebugOverlayInput()
        {
            _movieDebugOverlayEnabled = false;
            return false;
        }

        public static Boolean TryWorldToSbsUiScreenPoint(Camera worldCamera, Vector3 worldPosition, out Vector3 screenPosition)
        {
            screenPosition = Vector3.zero;
            if (!SbsActive || worldCamera == null || Screen.width <= 1 || Screen.height <= 0)
                return false;

            if (FF9DepthVRBattleStereo.TryProjectSbsUiPoint(worldCamera, worldPosition, out screenPosition))
                return true;

            Rect pixelRect = worldCamera.pixelRect;
            if (pixelRect.width <= 0f || pixelRect.height <= 0f || pixelRect.width > Screen.width * 0.75f)
                return false;

            Vector3 viewportPosition = worldCamera.WorldToViewportPoint(worldPosition);
            screenPosition = new Vector3(viewportPosition.x * Screen.width * 0.5f, viewportPosition.y * Screen.height, viewportPosition.z);
            return true;
        }

        internal static void BeginFieldMoviePlate(global::FieldMap fieldMap, MovieMaterial movieMaterial, GameObject nativeMoviePlane)
        {
            if (fieldMap == null)
            {
                Log.Message("[FF9DepthVR] Field movie BGPlate skipped: no active FieldMap.");
                return;
            }
            if (movieMaterial == null)
            {
                Log.Message("[FF9DepthVR] Field movie BGPlate skipped: no MovieMaterial.");
                return;
            }
            if (!EnableFieldMovieDepthPlate)
            {
                Log.Message("[FF9DepthVR] Field movie BGPlate disabled; native movie remains active for movie=" + movieMaterial.movieKey + ".");
                if (_activeMoviePlate != null && _activeMovieFieldMap != fieldMap)
                    EndFieldMoviePlate(_activeMovieFieldMap);
                _activeMovieFieldMap = fieldMap;
                _activeMoviePlate = null;
                SetDepthReplacementVisible(fieldMap, SbsActive);
                BeginNativeMoviePlaneSbs(nativeMoviePlane, false, true, fieldMap);
                return;
            }
            String movieDepthReason;
            if (!HasUsableMovieDepthFrames(movieMaterial, out movieDepthReason))
            {
                BeginNativeMovieFallback(movieMaterial, nativeMoviePlane, "Field movie BGPlate fallback", movieDepthReason, false);
                return;
            }
            if (!HasDepthReplacement(fieldMap))
            {
                Log.Message("[FF9DepthVR] Field movie BGPlate skipped: no active field depth BGPlate for movie=" + movieMaterial.movieKey + ".");
                BeginNativeMoviePlaneSbs(nativeMoviePlane, false);
                return;
            }

            if (_activeMoviePlate != null && _activeMovieFieldMap != fieldMap)
                EndFieldMoviePlate(_activeMovieFieldMap);

            FF9DepthVRMovieBgPlate moviePlate = fieldMap.GetComponent<FF9DepthVRMovieBgPlate>();
            if (moviePlate == null)
                moviePlate = fieldMap.gameObject.AddComponent<FF9DepthVRMovieBgPlate>();

            _activeMovieFieldMap = fieldMap;
            _activeMoviePlate = moviePlate;
            moviePlate.Initialize(fieldMap, movieMaterial, nativeMoviePlane);
        }

        internal static void BeginMoviePlate(global::FieldMap fieldMap, MovieMaterial movieMaterial, GameObject nativeMoviePlane, Camera movieCamera)
        {
            if (movieMaterial == null)
            {
                Log.Message("[FF9DepthVR] Movie BGPlate skipped: no MovieMaterial.");
                return;
            }

            if (fieldMap != null && HasDepthReplacement(fieldMap) && IsFieldMovieKey(movieMaterial.movieKey))
            {
                BeginFieldMoviePlate(fieldMap, movieMaterial, nativeMoviePlane);
                return;
            }

            if (movieCamera == null)
            {
                Log.Message("[FF9DepthVR] Movie BGPlate skipped: no movie camera for movie=" + movieMaterial.movieKey + ".");
                return;
            }

            String movieDepthReason;
            if (!HasUsableMovieDepthFrames(movieMaterial, out movieDepthReason))
            {
                BeginNativeMovieFallback(movieMaterial, nativeMoviePlane, "Standalone movie BGPlate fallback", movieDepthReason, true);
                return;
            }

            EndNativeMoviePlaneSbs();
            if (_activeMoviePlate != null && _activeMovieFieldMap != null)
                EndFieldMoviePlate(_activeMovieFieldMap);

            FF9DepthVRMovieBgPlate moviePlate = movieCamera.GetComponent<FF9DepthVRMovieBgPlate>();
            if (moviePlate == null)
                moviePlate = movieCamera.gameObject.AddComponent<FF9DepthVRMovieBgPlate>();

            _activeMovieFieldMap = null;
            _activeMoviePlate = moviePlate;
            moviePlate.InitializeStandalone(movieMaterial, nativeMoviePlane, movieCamera);
        }

        internal static void EndFieldMoviePlate(global::FieldMap fieldMap)
        {
            EndNativeMoviePlaneSbs();
            global::FieldMap target = fieldMap != null ? fieldMap : _activeMovieFieldMap;
            FF9DepthVRMovieBgPlate moviePlate = _activeMoviePlate;
            if (moviePlate == null && target != null)
                moviePlate = target.GetComponent<FF9DepthVRMovieBgPlate>();

            if (moviePlate != null)
                moviePlate.Shutdown();
            if (target != null)
                SetDepthReplacementVisible(target, true);

            if (_activeMoviePlate == moviePlate)
                _activeMoviePlate = null;
            if (_activeMovieFieldMap == target)
                _activeMovieFieldMap = null;
        }

        internal static Boolean IsMoviePlateActiveFor(global::FieldMap fieldMap)
        {
            return MoviePlateActive && (_activeMovieFieldMap == null || fieldMap == null || _activeMovieFieldMap == fieldMap);
        }

        private static void BeginNativeMoviePlaneSbs(GameObject nativeMoviePlane, Boolean scaleForSbs)
        {
            BeginNativeMoviePlaneSbs(nativeMoviePlane, scaleForSbs, false, null);
        }

        private static void BeginNativeMoviePlaneSbs(GameObject nativeMoviePlane, Boolean scaleForSbs, Boolean suppressInSbs, global::FieldMap suppressionFieldMap)
        {
            if (nativeMoviePlane == null)
            {
                Log.Message("[FF9DepthVR] Native field movie SBS scaler skipped: no native movie plane.");
                return;
            }

            if (_activeNativeMoviePlaneScaler != null && _activeNativeMoviePlaneScaler.gameObject != nativeMoviePlane)
                EndNativeMoviePlaneSbs();

            FF9DepthVRNativeMoviePlaneSbsScaler scaler = nativeMoviePlane.GetComponent<FF9DepthVRNativeMoviePlaneSbsScaler>();
            if (scaler == null)
                scaler = nativeMoviePlane.AddComponent<FF9DepthVRNativeMoviePlaneSbsScaler>();

            _activeNativeMoviePlaneScaler = scaler;
            scaler.Initialize(nativeMoviePlane, scaleForSbs, suppressInSbs, suppressionFieldMap);
        }

        private static void BeginNativeMovieFallback(MovieMaterial movieMaterial, GameObject nativeMoviePlane, String context, String reason, Boolean scaleForSbs)
        {
            if (_activeMoviePlate != null)
                EndFieldMoviePlate(_activeMovieFieldMap);

            String movieKey = movieMaterial != null ? movieMaterial.movieKey : "unknown";
            Log.Message("[FF9DepthVR] " + context + ": native 2D movie remains active for movie=" + movieKey + " reason=" + reason + ".");
            BeginNativeMoviePlaneSbs(nativeMoviePlane, scaleForSbs);
        }

        private static void EndNativeMoviePlaneSbs()
        {
            if (_activeNativeMoviePlaneScaler == null)
                return;

            _activeNativeMoviePlaneScaler.Shutdown();
            _activeNativeMoviePlaneScaler = null;
        }

        internal sealed class MovieFrameSet
        {
            public String MovieKey;
            public String RootPath;
            public String ColorFramesDirectory;
            public String DepthFramesDirectory;
            public String DepthMoviePath;
            public String ProgressPath;
        }

        internal static Boolean TryResolveMovieFrameDirectory(String movieKey, out String basePath)
        {
            MovieFrameSet frameSet;
            String reason;
            if (TryResolveMovieFrameSet(movieKey, out frameSet, out reason))
            {
                basePath = frameSet.RootPath;
                return true;
            }

            basePath = null;
            return false;
        }

        internal static Boolean TryResolveMovieFrameSet(String movieKey, out MovieFrameSet frameSet, out String reason)
        {
            frameSet = null;
            reason = "missing movie key";
            if (String.IsNullOrEmpty(movieKey))
                return false;

            List<String> roots = GetMovieFrameRootCandidates(movieKey);
            if (roots.Count == 0)
            {
                reason = "no fmv-depth search roots";
                return false;
            }

            String colorRoot = null;
            String depthRoot = null;
            String depthMovieRoot = null;
            String depthMoviePath = null;
            foreach (String root in roots)
            {
                String candidate = Path.Combine(root, movieKey + "_depth.bytes");
                if (File.Exists(candidate))
                {
                    depthMovieRoot = root;
                    depthMoviePath = candidate;
                    break;
                }
            }

            foreach (String root in roots)
            {
                if (Directory.Exists(Path.Combine(root, "color_frames")) && Directory.Exists(Path.Combine(root, "depth_frames")))
                {
                    colorRoot = root;
                    depthRoot = root;
                    break;
                }
            }

            if (depthRoot == null)
            {
                foreach (String root in roots)
                {
                    if (Directory.Exists(Path.Combine(root, "depth_frames")))
                    {
                        depthRoot = root;
                        break;
                    }
                }
            }

            if (colorRoot == null)
            {
                foreach (String root in roots)
                {
                    if (Directory.Exists(Path.Combine(root, "color_frames")))
                    {
                        colorRoot = root;
                        break;
                    }
                }
            }

            if (depthRoot == null && depthMoviePath == null)
            {
                reason = "missing depth movie bytes or depth_frames directory";
                return false;
            }
            if (depthMoviePath == null && colorRoot == null)
            {
                reason = "missing user color_frames directory";
                return false;
            }

            String rootPath = depthRoot != null ? depthRoot : depthMovieRoot;
            frameSet = new MovieFrameSet
            {
                MovieKey = movieKey,
                RootPath = rootPath,
                ColorFramesDirectory = colorRoot != null ? Path.Combine(colorRoot, "color_frames") : null,
                DepthFramesDirectory = depthRoot != null ? Path.Combine(depthRoot, "depth_frames") : null,
                DepthMoviePath = depthMoviePath,
                ProgressPath = Path.Combine(rootPath, "progress.json")
            };
            reason = depthMoviePath != null
                ? "depthMovie=" + frameSet.DepthMoviePath
                : "color=" + frameSet.ColorFramesDirectory + " depth=" + frameSet.DepthFramesDirectory;
            return true;
        }

        private static List<String> GetMovieFrameRootCandidates(String movieKey)
        {
            List<String> roots = new List<String>();
            String relative = Path.Combine(Path.Combine(Path.Combine("Data", "FF9DepthVR"), "fmv-depth"), movieKey);
            AddUniquePath(roots, Path.Combine(Application.streamingAssetsPath, relative));

            String gameRoot = TryGetGameRootPath();
            if (!String.IsNullOrEmpty(gameRoot))
            {
                AddUniquePath(roots, Path.Combine(Path.Combine(Path.Combine(Path.Combine(Path.Combine(gameRoot, "FF9DepthVR"), "StreamingAssets"), "Data"), "FF9DepthVR"), Path.Combine("fmv-depth", movieKey)));
                AddUniquePath(roots, Path.Combine(Path.Combine(Path.Combine(gameRoot, "FF9DepthVR"), "UserAssets"), Path.Combine("fmv-depth", movieKey)));
            }

            AddUniquePath(roots, Path.Combine(Path.Combine(Application.persistentDataPath, "FF9DepthVR"), Path.Combine("fmv-depth", movieKey)));
            AddUniquePath(roots, Path.Combine("C:\\Users\\rxcam\\Documents\\FFIX3DVR\\artifacts\\fmv-depth", movieKey));
            return roots;
        }

        private static String TryGetGameRootPath()
        {
            try
            {
                DirectoryInfo dataDir = Directory.GetParent(Application.dataPath);
                if (dataDir == null)
                    return null;
                DirectoryInfo gameDir = dataDir.Parent;
                return gameDir != null ? gameDir.FullName : null;
            }
            catch
            {
                return null;
            }
        }

        private static void AddUniquePath(List<String> paths, String path)
        {
            if (String.IsNullOrEmpty(path))
                return;
            foreach (String existing in paths)
            {
                if (String.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            paths.Add(path);
        }

        internal static Boolean HasUsableMovieDepthFrames(MovieMaterial movieMaterial, out String reason)
        {
            reason = "missing movie material";
            if (movieMaterial == null)
                return false;

            String movieKey = movieMaterial.movieKey;
            if (String.IsNullOrEmpty(movieKey))
            {
                reason = "missing movie key";
                return false;
            }

            MovieFrameSet frameSet;
            if (!TryResolveMovieFrameSet(movieKey, out frameSet, out reason))
                return false;

            return HasUsableMovieDepthFrames(movieKey, frameSet, out reason);
        }

        internal static Boolean HasUsableMovieDepthFrames(String movieKey, String basePath, out String reason)
        {
            MovieFrameSet frameSet = new MovieFrameSet
            {
                MovieKey = movieKey,
                RootPath = basePath,
                ColorFramesDirectory = Path.Combine(basePath, "color_frames"),
                DepthFramesDirectory = Path.Combine(basePath, "depth_frames"),
                DepthMoviePath = Path.Combine(basePath, movieKey + "_depth.bytes"),
                ProgressPath = Path.Combine(basePath, "progress.json")
            };
            return HasUsableMovieDepthFrames(movieKey, frameSet, out reason);
        }

        internal static Boolean HasUsableMovieDepthFrames(String movieKey, MovieFrameSet frameSet, out String reason)
        {
            reason = "missing movie depth directory";
            if (String.IsNullOrEmpty(movieKey) || frameSet == null)
                return false;

            if (!String.IsNullOrEmpty(frameSet.DepthMoviePath) && File.Exists(frameSet.DepthMoviePath))
            {
                reason = "usable depth movie bytes for movie=" + movieKey + " path=" + frameSet.DepthMoviePath;
                return true;
            }

            String colorDir = frameSet.ColorFramesDirectory;
            String depthDir = frameSet.DepthFramesDirectory;
            if (String.IsNullOrEmpty(colorDir) || String.IsNullOrEmpty(depthDir))
            {
                reason = "missing depth movie bytes or frame directories";
                return false;
            }
            if (!Directory.Exists(colorDir))
            {
                reason = "missing color_frames directory";
                return false;
            }
            if (!Directory.Exists(depthDir))
            {
                reason = "missing depth_frames directory";
                return false;
            }

            String firstFrame = "frame_000001.png";
            if (!File.Exists(Path.Combine(colorDir, firstFrame)))
            {
                reason = "missing first color frame";
                return false;
            }
            if (!File.Exists(Path.Combine(depthDir, firstFrame)))
            {
                reason = "missing first depth frame";
                return false;
            }

            String progressPath = frameSet.ProgressPath;
            if (File.Exists(progressPath))
                return HasCompleteMovieDepthProgress(movieKey, colorDir, depthDir, progressPath, out reason);

            reason = "usable frames without progress metadata color=" + colorDir + " depth=" + depthDir;
            return true;
        }

        private static Boolean HasCompleteMovieDepthProgress(String movieKey, String colorDir, String depthDir, String progressPath, out String reason)
        {
            try
            {
                JSONNode progress = JSONNode.Parse(File.ReadAllText(progressPath));
                String status = progress["status"].Value;
                Int32 frames = progress["frames"].AsInt;
                if (frames <= 0)
                    frames = progress["totalFrames"].AsInt;
                Int32 done = progress["done"].AsInt;
                Int32 failed = progress["failed"].AsInt;

                if (!String.Equals(status, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "progress status=" + status;
                    return false;
                }
                if (failed > 0)
                {
                    reason = "progress failed frames=" + failed;
                    return false;
                }
                if (frames <= 0)
                {
                    reason = "progress has no frame count";
                    return false;
                }
                if (done < frames)
                {
                    reason = "progress done=" + done + "/" + frames;
                    return false;
                }

                String lastFrame = "frame_" + frames.ToString("D6") + ".png";
                if (!File.Exists(Path.Combine(colorDir, lastFrame)))
                {
                    reason = "missing final color frame " + lastFrame;
                    return false;
                }
                if (!File.Exists(Path.Combine(depthDir, lastFrame)))
                {
                    reason = "missing final depth frame " + lastFrame;
                    return false;
                }

                reason = "complete depth frames for movie=" + movieKey + " frames=" + frames;
                return true;
            }
            catch (Exception ex)
            {
                reason = "invalid progress metadata: " + ex.Message;
                return false;
            }
        }

        internal static void ApplyActorCameraScroll(global::FieldMap fieldMap)
        {
            if (fieldMap == null || fieldMap.scene == null || fieldMap.camIdx < 0 || fieldMap.camIdx >= fieldMap.scene.cameraList.Count)
                return;
            if (_lastActorCameraScrollFrame == Time.frameCount)
                return;

            Camera camera = fieldMap.GetMainCamera();
            CameraFrameState frame = camera != null ? GetCameraFrame(camera) : null;
            EnsureManifestLoaded();
            if (!PlateVisible)
            {
                if (frame != null)
                    ApplyCameraZoom(camera, frame, 1f);
                return;
            }
            _lastActorCameraScrollFrame = Time.frameCount;

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

        public static Boolean TryAdjustFieldSpsLocalPosition(global::FieldMap fieldMap, Vector3 projectedPoint, ref Vector3 localPosition)
        {
            if (_activeActorComposite == null)
                return false;
            return _activeActorComposite.TryAdjustProjectedLocalPosition(fieldMap, projectedPoint, ref localPosition);
        }

        internal static void ApplyActorCameraFraming(global::FieldMap fieldMap, BGCAM_DEF bgCamera, ref Single cameraX, ref Single cameraY)
        {
            if (fieldMap == null || bgCamera == null)
                return;

            Camera camera = fieldMap.GetMainCamera();
            if (camera == null)
                return;

            CameraFrameState frame = GetCameraFrame(camera);
            if (!PlateVisible)
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
                    EnsureFallbackFieldBridge(fieldMap);
                    SetOriginalBackgroundVisible(fieldMap, true);
                    return;
                }

                Texture2D depthTexture = global::AssetManager.Load<Texture2D>(entry.Depth, true);
                if (depthTexture == null)
                {
                    DestroyExisting(fieldMap);
                    EnsureFallbackFieldBridge(fieldMap);
                    SetOriginalBackgroundVisible(fieldMap, true);
                    Log.Message("[FF9DepthVR] Missing depth texture for " + entry.Id + " at " + entry.Depth + "; using original field renderer.");
                    return;
                }

                DestroyExisting(fieldMap);
                DestroyFallbackFieldBridge(fieldMap);
                Texture2D colorTexture = global::AssetManager.Load<Texture2D>(entry.SourcePlate, true);
                if (colorTexture == null)
                    colorTexture = CaptureOriginalBackgroundPlate(fieldMap, entry, depthTexture);
                else
                    ApplyAlphaCutoff(colorTexture, _defaults.AlphaCutoff);

                if (colorTexture == null)
                {
                    EnsureFallbackFieldBridge(fieldMap);
                    SetOriginalBackgroundVisible(fieldMap, true);
                    Log.Message("[FF9DepthVR] Missing source plate for " + entry.Id + " at " + entry.SourcePlate + " and runtime capture failed; using original field renderer.");
                    return;
                }

                GameObject root = BuildDepthPlate(fieldMap, entry, colorTexture, depthTexture);
                if (root != null)
                {
                    SetOriginalBackgroundVisible(fieldMap, true);
                }
                else
                {
                    EnsureFallbackFieldBridge(fieldMap);
                    SetOriginalBackgroundVisible(fieldMap, true);
                }
            }
            catch (Exception ex)
            {
                DestroyExisting(fieldMap);
                EnsureFallbackFieldBridge(fieldMap);
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

            try
            {
                Int32 fieldMapNo = global::FF9StateSystem.Common.FF9.fldMapNo;
                String eventMapName;
                if (global::EventEngineUtils.eventIDToFBGID.TryGetValue(fieldMapNo, out eventMapName)
                    && !String.IsNullOrEmpty(eventMapName)
                    && ScenesByMapName.TryGetValue(eventMapName, out entry))
                    return entry;
            }
            catch
            {
                // Some early scene transitions do not have the event engine fully populated yet.
            }

            String stateMapName = global::FF9StateSystem.Common.FF9.mapNameStr;
            if (!String.IsNullOrEmpty(stateMapName) && ScenesByMapName.TryGetValue(stateMapName, out entry))
                return entry;

            return null;
        }

        private static Texture2D CaptureOriginalBackgroundPlate(global::FieldMap fieldMap, SceneEntry entry, Texture2D depthTexture)
        {
            if (fieldMap == null)
                return null;

            Camera camera = fieldMap.GetMainCamera();
            Transform background = fieldMap.transform.Find("Background");
            BGCAM_DEF bgCamera = fieldMap.scene != null && fieldMap.camIdx >= 0 && fieldMap.camIdx < fieldMap.scene.cameraList.Count
                ? fieldMap.scene.cameraList[fieldMap.camIdx]
                : null;
            if (camera == null || background == null || bgCamera == null)
                return null;

            Single sourceScale = Mathf.Max(0.001f, _defaults.SourceScale);
            Int32 width = Mathf.RoundToInt(bgCamera.w / sourceScale);
            Int32 height = Mathf.RoundToInt(bgCamera.h / sourceScale);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
            {
                width = depthTexture != null ? depthTexture.width : 0;
                height = depthTexture != null ? depthTexture.height : 0;
            }
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
                return null;

            RenderTexture target = null;
            Texture2D captured = null;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            Rect previousRect = camera.rect;
            CameraClearFlags previousClearFlags = camera.clearFlags;
            Color previousBackgroundColor = camera.backgroundColor;
            List<RendererState> hiddenRenderers = new List<RendererState>();

            try
            {
                background.gameObject.SetActive(true);
                RestoreOriginalBackgroundQueues(background);
                SetBackgroundRenderersEnabled(background, true);
                HideNonBackgroundFieldRenderers(fieldMap, background, hiddenRenderers);

                target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                camera.targetTexture = target;
                camera.rect = new Rect(0f, 0f, 1f, 1f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.Render();

                RenderTexture.active = target;
                captured = new Texture2D(width, height, TextureFormat.RGBA32, false);
                captured.name = "FF9DepthVR_RuntimeSource_" + entry.Id;
                captured.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                captured.Apply(false, false);
                captured.wrapMode = TextureWrapMode.Clamp;
                captured.filterMode = FilterMode.Bilinear;
                ApplyAlphaCutoff(captured, _defaults.AlphaCutoff);
                Log.Message("[FF9DepthVR] Runtime source plate captured for " + entry.Id + " size=" + width + "x" + height + " sourceScale=" + sourceScale.ToString("F3"));
                return captured;
            }
            catch (Exception ex)
            {
                if (captured != null)
                    UnityEngine.Object.Destroy(captured);
                Log.Message("[FF9DepthVR] Runtime source plate capture failed for " + entry.Id + ": " + ex.Message);
                return null;
            }
            finally
            {
                for (Int32 i = 0; i < hiddenRenderers.Count; i++)
                {
                    RendererState state = hiddenRenderers[i];
                    if (state.Renderer != null)
                        state.Renderer.enabled = state.Enabled;
                }
                camera.targetTexture = previousTarget;
                camera.rect = previousRect;
                camera.clearFlags = previousClearFlags;
                camera.backgroundColor = previousBackgroundColor;
                RenderTexture.active = previousActive;
                if (target != null)
                    RenderTexture.ReleaseTemporary(target);
            }
        }

        private static void HideNonBackgroundFieldRenderers(global::FieldMap fieldMap, Transform background, List<RendererState> hiddenRenderers)
        {
            if (fieldMap == null || background == null || hiddenRenderers == null)
                return;

            Renderer[] renderers = fieldMap.GetComponentsInChildren<Renderer>(true);
            for (Int32 i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.transform == null || renderer.transform == background || renderer.transform.IsChildOf(background))
                    continue;

                hiddenRenderers.Add(new RendererState(renderer, renderer.enabled));
                renderer.enabled = false;
            }
        }

        private static void SetBackgroundRenderersEnabled(Transform background, Boolean enabled)
        {
            Renderer[] renderers = background.GetComponentsInChildren<Renderer>(true);
            for (Int32 i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = enabled;
            }
        }

        private static void RestoreOriginalBackgroundQueues(Transform background)
        {
            if (background == null)
                return;

            Renderer[] renderers = background.GetComponentsInChildren<Renderer>(true);
            for (Int32 r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null)
                    continue;

                Material[] materials = renderer.materials;
                for (Int32 m = 0; m < materials.Length; m++)
                {
                    Material material = materials[m];
                    Int32 originalQueue;
                    if (material != null && OriginalBackgroundQueues.TryGetValue(material, out originalQueue))
                        material.renderQueue = originalQueue;
                }
            }
        }

        private struct RendererState
        {
            public readonly Renderer Renderer;
            public readonly Boolean Enabled;

            public RendererState(Renderer renderer, Boolean enabled)
            {
                Renderer = renderer;
                Enabled = enabled;
            }
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
            FF9DepthVRSbsStereo sbsStereo = root.AddComponent<FF9DepthVRSbsStereo>();
            sbsStereo.Initialize(fieldMap);
            FF9DepthVRSbsUiStereo sbsUiStereo = root.AddComponent<FF9DepthVRSbsUiStereo>();
            sbsUiStereo.Initialize();

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
            material.renderQueue = renderQueue >= 0 ? renderQueue : ReplacementPlateRenderQueue;
            if (material.HasProperty("_DepthTex"))
                material.SetTexture("_DepthTex", depthTexture);
            if (material.HasProperty("_ColorTexel"))
                material.SetVector("_ColorTexel", new Vector4(1f / Mathf.Max(1, colorTexture.width), 1f / Mathf.Max(1, colorTexture.height), 0f, 0f));
            if (material.HasProperty("_FocusUv"))
                material.SetVector("_FocusUv", new Vector4(0.5f, 0.5f, 0f, 0f));
            if (material.HasProperty("_FocusDepth"))
                material.SetFloat("_FocusDepth", 0.5f);
            if (material.HasProperty("_DofAmount"))
                material.SetFloat("_DofAmount", 0f);
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

        internal static Material CreateMoviePlateMaterial(Texture2D colorTexture, Texture2D depthTexture)
        {
            Shader shader = ShadersLoader.Find("Unlit/AdjustableTransparent");
            if (shader == null)
                shader = Shader.Find("Unlit/AdjustableTransparent");
            if (shader == null)
                shader = ShadersLoader.Find("Unlit/Texture");
            if (shader == null)
                shader = Shader.Find("Unlit/Texture");
            if (shader == null)
                shader = ShadersLoader.Find("Unlit/Transparent");
            if (shader == null)
                shader = Shader.Find("Unlit/Transparent");
            if (shader == null)
                shader = ShadersLoader.Find("Particles/Alpha Blended");
            if (shader == null)
                shader = Shader.Find("Particles/Alpha Blended");
            if (shader == null)
                shader = Shader.Find("Diffuse");

            Material material = new Material(shader);
            material.mainTexture = colorTexture;
            material.renderQueue = ReplacementPlateRenderQueue;
            if (material.HasProperty("_MainTex"))
                material.SetTexture("_MainTex", colorTexture);
            if (material.HasProperty("_ColorTexel"))
                material.SetVector("_ColorTexel", new Vector4(1f / Mathf.Max(1, colorTexture.width), 1f / Mathf.Max(1, colorTexture.height), 0f, 0f));
            if (material.HasProperty("_FocusUv"))
                material.SetVector("_FocusUv", new Vector4(0.5f, 0.5f, 0f, 0f));
            if (material.HasProperty("_FocusDepth"))
                material.SetFloat("_FocusDepth", 0.5f);
            if (material.HasProperty("_DofAmount"))
                material.SetFloat("_DofAmount", 0f);
            if (material.HasProperty("_AlphaCutoff"))
                material.SetFloat("_AlphaCutoff", 0.04f);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", Color.white);
            if (material.HasProperty("_TintColor"))
                material.SetColor("_TintColor", Color.white);
            if (material.HasProperty("_DepthTex"))
                material.SetTexture("_DepthTex", depthTexture);
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetInt("_Cull", (Int32)UnityEngine.Rendering.CullMode.Off);
            material.SetInt("_ZWrite", 0);
            material.SetInt("_ZTest", (Int32)UnityEngine.Rendering.CompareFunction.Always);
            material.SetInt("_SrcBlend", (Int32)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (Int32)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            return material;
        }

        internal static Single SampleDepthValue(Texture2D depthTexture, Single u, Single v)
        {
            return SampleDepth(depthTexture, u, v);
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

        private static void EnsureFallbackFieldBridge(global::FieldMap fieldMap)
        {
            if (fieldMap == null || fieldMap.transform == null)
                return;

            Transform existing = FindChildRecursive(fieldMap.transform, FallbackRootName);
            GameObject root = existing != null ? existing.gameObject : new GameObject(FallbackRootName);
            root.transform.SetParent(fieldMap.transform, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            FF9DepthVRFallbackFieldBridge bridge = root.GetComponent<FF9DepthVRFallbackFieldBridge>();
            if (bridge == null)
                bridge = root.AddComponent<FF9DepthVRFallbackFieldBridge>();
            bridge.Initialize(fieldMap);
        }

        private static void DestroyFallbackFieldBridge(global::FieldMap fieldMap)
        {
            if (fieldMap == null || fieldMap.transform == null)
                return;

            Transform existing = FindChildRecursive(fieldMap.transform, FallbackRootName);
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

        internal static void SetDepthReplacementVisible(global::FieldMap fieldMap, Boolean visible)
        {
            if (fieldMap == null)
                return;

            Transform depthRoot = FindChildRecursive(fieldMap.transform, RootName);
            if (depthRoot == null)
                return;

            Renderer[] renderers = depthRoot.GetComponentsInChildren<Renderer>(true);
            for (Int32 i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer != null)
                    renderer.enabled = visible && PlateVisible;
            }
        }

        private static Boolean HasDepthReplacement(global::FieldMap fieldMap)
        {
            return fieldMap != null && FindChildRecursive(fieldMap.transform, RootName) != null;
        }

        internal static Boolean TryGetDepthReplacementPlateSize(global::FieldMap fieldMap, out Single width, out Single height)
        {
            width = 0f;
            height = 0f;
            if (fieldMap == null)
                return false;

            Transform depthRoot = FindChildRecursive(fieldMap.transform, RootName);
            if (depthRoot == null)
                return false;

            Transform plateParent = GetPlateParent(fieldMap);
            MeshFilter[] filters = depthRoot.GetComponentsInChildren<MeshFilter>(true);
            if (filters == null || filters.Length == 0)
                return false;

            Boolean hasPoint = false;
            Single minX = Single.MaxValue;
            Single maxX = Single.MinValue;
            Single minY = Single.MaxValue;
            Single maxY = Single.MinValue;
            for (Int32 i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                if (filter == null || filter.sharedMesh == null)
                    continue;

                Bounds bounds = filter.sharedMesh.bounds;
                Vector3 min = bounds.min;
                Vector3 max = bounds.max;
                Vector3[] corners = new Vector3[8]
                {
                    new Vector3(min.x, min.y, min.z),
                    new Vector3(max.x, min.y, min.z),
                    new Vector3(min.x, max.y, min.z),
                    new Vector3(max.x, max.y, min.z),
                    new Vector3(min.x, min.y, max.z),
                    new Vector3(max.x, min.y, max.z),
                    new Vector3(min.x, max.y, max.z),
                    new Vector3(max.x, max.y, max.z)
                };

                for (Int32 c = 0; c < corners.Length; c++)
                {
                    Vector3 world = filter.transform.TransformPoint(corners[c]);
                    Vector3 local = plateParent != null ? plateParent.InverseTransformPoint(world) : world;
                    if (!IsFinite(local.x) || !IsFinite(local.y))
                        continue;

                    hasPoint = true;
                    minX = Mathf.Min(minX, local.x);
                    maxX = Mathf.Max(maxX, local.x);
                    minY = Mathf.Min(minY, local.y);
                    maxY = Mathf.Max(maxY, local.y);
                }
            }

            if (!hasPoint)
                return false;

            width = maxX - minX;
            height = maxY - minY;
            return IsFinite(width) && IsFinite(height) && width > 0.001f && height > 0.001f && width < 100000f && height < 100000f;
        }

        private static Boolean IsFinite(Single value)
        {
            return !Single.IsNaN(value) && !Single.IsInfinity(value);
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

        internal static Transform GetPlateParent(global::FieldMap fieldMap)
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

        internal static void SetLayerRecursive(GameObject root, Int32 layer)
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

    internal static class FF9DepthVRHeadTrackingBridge
    {
        private const Int32 Port = 29710;
        private const Single StaleSeconds = 0.35f;
        private const Single YawDegreesForFullLook = 25f;
        private const Single PitchDegreesForFullLook = 18f;
        private static UdpClient _client;
        private static Boolean _unavailable;
        private static Boolean _loggedFirstPacket;
        private static Int32 _lastPumpFrame = -1;
        private static Single _lastPacketTime = -999f;
        private static Vector2 _look;

        public static Boolean TryReadLook(out Vector2 look)
        {
            look = Vector2.zero;
            Pump();
            if (Time.realtimeSinceStartup - _lastPacketTime > StaleSeconds)
                return false;

            look = _look;
            return true;
        }

        private static void Pump()
        {
            if (_lastPumpFrame == Time.frameCount)
                return;
            _lastPumpFrame = Time.frameCount;

            EnsureClient();
            if (_client == null)
                return;

            try
            {
                while (_client.Available > 0)
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    Byte[] data = _client.Receive(ref remote);
                    Single yaw;
                    Single pitch;
                    Single roll;
                    if (!TryParsePacket(Encoding.ASCII.GetString(data), out yaw, out pitch, out roll))
                        continue;

                    _look = new Vector2(
                        Mathf.Clamp(yaw / YawDegreesForFullLook, -1f, 1f),
                        Mathf.Clamp(pitch / PitchDegreesForFullLook, -1f, 1f)
                    );
                    _lastPacketTime = Time.realtimeSinceStartup;
                    if (!_loggedFirstPacket)
                    {
                        _loggedFirstPacket = true;
                        Log.Message("[FF9DepthVR] Head tracking UDP bridge received first packet yaw=" + yaw.ToString("0.###") + " pitch=" + pitch.ToString("0.###") + " roll=" + roll.ToString("0.###"));
                    }
                }
            }
            catch (SocketException)
            {
            }
            catch (Exception ex)
            {
                Log.Message("[FF9DepthVR] Head tracking UDP bridge read failed: " + ex.Message);
            }
        }

        private static void EnsureClient()
        {
            if (_client != null || _unavailable)
                return;

            try
            {
                _client = new UdpClient(Port);
                _client.Client.Blocking = false;
                Log.Message("[FF9DepthVR] Head tracking UDP bridge listening on 127.0.0.1:" + Port + " (yaw,pitch,roll degrees).");
            }
            catch (Exception ex)
            {
                _unavailable = true;
                Log.Message("[FF9DepthVR] Head tracking UDP bridge unavailable: " + ex.Message);
            }
        }

        private static Boolean TryParsePacket(String payload, out Single yaw, out Single pitch, out Single roll)
        {
            yaw = 0f;
            pitch = 0f;
            roll = 0f;
            if (String.IsNullOrEmpty(payload))
                return false;

            String[] tokens = payload.Trim()
                .Replace(';', ',')
                .Replace(' ', ',')
                .Split(new Char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            Int32 ordered = 0;
            Boolean foundYaw = false;
            Boolean foundPitch = false;
            for (Int32 i = 0; i < tokens.Length; i++)
            {
                String token = tokens[i].Trim();
                if (String.IsNullOrEmpty(token))
                    continue;

                Int32 equals = token.IndexOf('=');
                if (equals > 0)
                {
                    String key = token.Substring(0, equals).Trim().ToLowerInvariant();
                    Single value;
                    if (!TryParseFloat(token.Substring(equals + 1), out value))
                        continue;
                    if (key == "yaw" || key == "y")
                    {
                        yaw = value;
                        foundYaw = true;
                    }
                    else if (key == "pitch" || key == "p")
                    {
                        pitch = value;
                        foundPitch = true;
                    }
                    else if (key == "roll" || key == "r")
                    {
                        roll = value;
                    }
                    continue;
                }

                Single parsed;
                if (!TryParseFloat(token, out parsed))
                    continue;
                if (ordered == 0)
                {
                    yaw = parsed;
                    foundYaw = true;
                }
                else if (ordered == 1)
                {
                    pitch = parsed;
                    foundPitch = true;
                }
                else if (ordered == 2)
                {
                    roll = parsed;
                }
                ordered++;
            }

            return foundYaw || foundPitch;
        }

        private static Boolean TryParseFloat(String text, out Single value)
        {
            return Single.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
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
        private Vector2 _lookFocusUv = new Vector2(0.5f, 0.5f);
        private Vector2 _actorLookUv = new Vector2(0.5f, 0.5f);
        private Boolean _hasActorLookTarget;
        private Boolean _centeredPlateCoordinates;
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

        public void SetCenteredPlateCoordinates(Boolean centered)
        {
            _centeredPlateCoordinates = centered;
        }

        public void ReplaceSource(Mesh mesh, Vector3[] baseVertices, Color[] depthColors, Single width, Single height, Texture2D colorTexture, Texture2D depthTexture)
        {
            _mesh = mesh;
            _baseVertices = (Vector3[])baseVertices.Clone();
            _workingVertices = (Vector3[])baseVertices.Clone();
            _depth = new Single[depthColors.Length];
            for (Int32 i = 0; i < depthColors.Length; i++)
                _depth[i] = depthColors[i].r - 0.5f;
            _width = width;
            _height = height;
            _colorTexture = colorTexture;
            _depthTexture = depthTexture;
            if (_material != null)
            {
                if (colorTexture != null)
                    _material.mainTexture = colorTexture;
                if (_material.HasProperty("_DepthTex") && depthTexture != null)
                    _material.SetTexture("_DepthTex", depthTexture);
                if (_material.HasProperty("_ColorTexel") && colorTexture != null)
                    _material.SetVector("_ColorTexel", new Vector4(1f / Mathf.Max(1, colorTexture.width), 1f / Mathf.Max(1, colorTexture.height), 0f, 0f));
            }
            _lastUpdatedFrame = -1;
            RefreshGeometryNow();
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
                Vector2 look = CameraLookNormalized();
                _lookFocusUv = new Vector2(look.x * 0.5f + 0.5f, look.y * 0.5f + 0.5f);
                Single xMultiplier = FF9DepthVRFieldRenderer.SbsActive ? FF9DepthVRFieldRenderer.ViewAngleXMultiplier : 1f;
                targetX = look.x * FF9DepthVRFieldRenderer.ViewAngleMultiplier * xMultiplier;
                targetY = look.y * FF9DepthVRFieldRenderer.ViewAngleMultiplier;
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
                return;
            }

            _material.SetVector("_FocusUv", new Vector4(focusUv.x, focusUv.y, 0f, 0f));
            _material.SetFloat("_FocusDepth", focusDepth);
            _material.SetFloat("_DofAmount", 0f);
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
            return new Vector2(Mathf.Clamp01(_lookFocusUv.x), Mathf.Clamp01(_lookFocusUv.y));
        }

        private Vector2 CameraLookNormalized()
        {
            return AddActorLookBaseline(FF9DepthVRFieldRenderer.InputLookNormalized(true));
        }

        private Vector2 AddActorLookBaseline(Vector2 manualLook)
        {
            Vector2 actorLook = ActorLookBaselineNormalized();
            return new Vector2(
                Mathf.Clamp(manualLook.x + actorLook.x, -1f, 1f),
                Mathf.Clamp(manualLook.y + actorLook.y, -1f, 1f)
            );
        }

        private Vector2 ActorLookBaselineNormalized()
        {
            if (!FF9DepthVRFieldRenderer.ActorLookEnabled || !_hasActorLookTarget)
                return Vector2.zero;

            return new Vector2(
                Mathf.Clamp((_actorLookUv.x - 0.5f) * 2f, -1f, 1f),
                Mathf.Clamp((_actorLookUv.y - 0.5f) * 2f, -1f, 1f)
            );
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
            Vector2 transformed = PlatePoint(plateX, plateY, depthCenter);
            return new Vector2(transformed.x - plateX, transformed.y - plateY);
        }

        public Vector2 PlatePoint(Single plateX, Single plateY, Single depthCenter)
        {
            Vector2 plateSpace = ToPlateSpace(plateX, plateY);
            Single edgeFadeX = Mathf.Clamp01(Mathf.Min(plateSpace.x, _width - plateSpace.x) / Mathf.Max(1f, _width * 0.08f));
            Single edgeFadeY = Mathf.Clamp01(Mathf.Min(plateSpace.y, _height - plateSpace.y) / Mathf.Max(1f, _height * 0.08f));
            Single edgeFade = Mathf.Min(edgeFadeX, edgeFadeY);
            Single x = plateSpace.x + depthCenter * _currentOffsetX * edgeFade;
            Single y = plateSpace.y + depthCenter * _currentOffsetY * edgeFade;
            if (_smoothedZoom > 0.0001f)
            {
                Single pivotX = _focusPivotX * _width;
                Single pivotY = _focusPivotY * _height;
                Single scale = 1f + _smoothedZoom;
                x = pivotX + (x - pivotX) * scale;
                y = pivotY + (y - pivotY) * scale;
            }
            return FromPlateSpace(x, y);
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
                _hasActorLookTarget = false;
                return;
            }

            _focusPivotX = Mathf.Clamp01(plateX / Mathf.Max(1f, _width));
            _focusPivotY = Mathf.Clamp01(plateY / Mathf.Max(1f, _height));
            _actorLookUv = new Vector2(_focusPivotX, _focusPivotY);
            _hasActorLookTarget = true;
            _focusBiasX = 0f;
            _focusBiasY = 0f;
            _focusZoom = 0f;
        }

        private Vector2 ToPlateSpace(Single plateX, Single plateY)
        {
            if (!_centeredPlateCoordinates)
                return new Vector2(plateX, plateY);
            return new Vector2(plateX + _width * 0.5f, _height * 0.5f - plateY);
        }

        private Vector2 FromPlateSpace(Single plateX, Single plateY)
        {
            if (!_centeredPlateCoordinates)
                return new Vector2(plateX, plateY);
            return new Vector2(plateX - _width * 0.5f, _height * 0.5f - plateY);
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
        private const Single ActorPinStrength = 1f;
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

            if (FF9DepthVRFieldRenderer.IsMbgPlaybackActive())
            {
                ApplyActorsWithoutDepthOffsets();
                return;
            }

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
            Vector3 actorLookAccumulator = Vector3.zero;
            Int32 actorLookCount = 0;
            Vector3 playerLookProjected = Vector3.zero;
            Boolean hasPlayerLook = false;
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
                    plateFoot = ProjectedToPlateLocal(projectedFoot);
                    depthCenter = SampleDepthAt(plateFoot) - 0.5f;
                }
                FieldMapActorController actorController = actor.GetComponent<FieldMapActorController>();
                if (actorController != null && actorController.isPlayer)
                {
                    playerLookProjected = projectedFoot;
                    hasPlayerLook = true;
                }
                if (IsPlatePointOnscreen(plateFoot))
                {
                    actorLookAccumulator += projectedFoot;
                    actorLookCount++;
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
            {
                if (FF9DepthVRFieldRenderer.ActorLookEnabled && (actorLookCount > 0 || hasPlayerLook))
                {
                    Vector3 actorAverage = actorLookCount > 0 ? actorLookAccumulator / actorLookCount : playerLookProjected;
                    Vector3 weightedTarget = hasPlayerLook ? (actorAverage + playerLookProjected * 3f) * 0.25f : actorAverage;
                    Vector3 plateTarget = ProjectedToPlateLocal(weightedTarget);
                    _parallax.SetActorFocus(plateTarget.x, plateTarget.y, true);
                }
                else
                {
                    _parallax.SetActorFocus(0f, 0f, false);
                }
            }

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
                Log.Message("[FF9DepthVR] Actor composite actors=" + compositedActors + " bodyRenderers=" + totalRenderers + " prepared=" + compositedRenderers + " queue=" + _renderQueue + " shader=" + (_forceCompositeShader && _compositeShader != null ? _compositeShader.name : "original") + " depthCast=walkmesh offset=depthPlate avgOffset=" + avgOffset + " maxOffset=" + maxOffset.ToString("F2") + " pinScale=" + ActorPinDiagnosticScale.ToString("F1") + " pinMode=walkmeshReproject surfaceHits=" + surfaceHitCount + " materialsWithOffset=" + materialOffsetCount + " materialPins=" + materialPinCount + " maxScreenDelta=" + maxScreenDelta.ToString("F2") + " afterSmooth=" + (!prepareRenderers));
            }
        }

        private void ApplyActorsWithoutDepthOffsets()
        {
            FieldMapActor[] actors = UnityEngine.Object.FindObjectsOfType<FieldMapActor>();
            _logTimer -= Time.deltaTime;

            Int32 actorCount = 0;
            Int32 materialCount = 0;
            for (Int32 i = 0; i < actors.Length; i++)
            {
                FieldMapActor actor = actors[i];
                if (actor == null || actor.transform == null || actor.transform.root != _fieldMap.transform.root)
                    continue;

                FieldMapActorController controller = actor.GetComponent<FieldMapActorController>();
                if (controller != null)
                    actor.transform.localPosition = controller.curPos;

                materialCount += ApplyActorProjectionOffset(actor, Vector2.zero);
                actorCount++;
            }

            if (_parallax != null)
                _parallax.SetActorFocus(0f, 0f, false);

            if (_logTimer <= 0f)
            {
                _logTimer = 5f;
                Log.Message("[FF9DepthVR] MBG playback active; actor depth offsets suppressed actors=" + actorCount + " materialsReset=" + materialCount);
            }
        }

        internal Boolean TryAdjustProjectedLocalPosition(global::FieldMap fieldMap, Vector3 projectedPoint, ref Vector3 localPosition)
        {
            if (_fieldMap == null || fieldMap == null || fieldMap.transform.root != _fieldMap.transform.root)
                return false;
            if (_parallax == null || !FF9DepthVRFieldRenderer.PlateVisible)
                return false;

            Vector2 offset;
            Vector2 plateOffset;
            Single depthCenter;
            Vector3 plateFoot;
            if (!TryActorSurfaceOffset(projectedPoint, out offset, out plateOffset, out depthCenter, out plateFoot))
                offset = ActorProjectionOffset(projectedPoint);

            if (!IsFinite(offset.x) || !IsFinite(offset.y))
                return false;

            localPosition.x += offset.x;
            localPosition.y += offset.y;
            return offset.sqrMagnitude > 0.0001f;
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

        private Boolean IsPlatePointOnscreen(Vector3 platePoint)
        {
            const Single margin = 16f;
            return platePoint.x >= -margin && platePoint.x <= _plateWidth + margin && platePoint.y >= -margin && platePoint.y <= _plateHeight + margin;
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
            BGCAM_DEF bgCamera = _fieldMap != null ? _fieldMap.GetCurrentBgCamera() : null;
            Single bgWidth = bgCamera != null && bgCamera.w > 0 ? bgCamera.w : FieldMap.PsxFieldWidth;
            Single bgHeight = bgCamera != null && bgCamera.h > 0 ? bgCamera.h : FieldMap.HalfFieldHeight * 2;
            Single xScale = bgWidth / Mathf.Max(1f, _plateWidth);
            Single yScale = bgHeight / Mathf.Max(1f, _plateHeight);
            return new Vector2(plateOffset.x * xScale * ActorPinStrength, -plateOffset.y * yScale * ActorPinStrength);
        }

        private Vector2 ActorProjectionOffset(Vector3 projectedFoot)
        {
            if (_parallax == null || !FF9DepthVRFieldRenderer.PlateVisible)
                return Vector2.zero;

            Vector3 plateFoot = ProjectedToPlateLocal(projectedFoot);
            Single depthCenter = SampleDepthAt(plateFoot) - 0.5f;
            Vector2 plateOffset = _parallax.PlateTransformOffset(plateFoot.x, plateFoot.y, depthCenter);
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

            Vector3 worldDelta;
            actor.transform.localPosition = realPosition;
            Vector3 shadowBaseLocal;
            Boolean hasShadowBase = TryComputeShadowBaseLocalPosition(actor, out shadowBaseLocal);
            if (CanApplyVisualWorldOffset(actor) && ProjectScreenOffsetThroughWalkmesh(actor, projectedFoot, screenOffset, out worldDelta))
            {
                actor.transform.localPosition = realPosition + worldDelta;
                if (hasShadowBase)
                    ApplyShadowVisualWorldOffset(actor, shadowBaseLocal, worldDelta);
                offsetMaterials = ApplyActorProjectionOffset(actor, Vector2.zero);
                return screenOffset.magnitude;
            }

            offsetMaterials = ApplyActorProjectionOffset(actor, screenOffset);
            return screenOffset.magnitude;
        }

        private Boolean CanApplyVisualWorldOffset(FieldMapActor actor)
        {
            if (actor == null || actor.transform == null || _fieldMap == null)
                return false;
            return actor.transform.parent == _fieldMap.transform;
        }

        private Boolean TryComputeShadowBaseLocalPosition(FieldMapActor actor, out Vector3 shadowLocalPosition)
        {
            shadowLocalPosition = Vector3.zero;
            if (actor == null || actor.shadowTran == null || actor.actor == null)
                return false;

            try
            {
                FF9Shadow ff9Shadow;
                Int32 uid = actor.actor.uid;
                if (!FF9StateSystem.Field.FF9Field.loc.map.shadowArray.TryGetValue(uid, out ff9Shadow))
                    return false;

                Int32 mapNo = FF9StateSystem.Common.FF9.fldMapNo;
                Vector3 shadowOff = Vector3.zero;
                if (mapNo == 661 && uid == 3)
                    shadowOff = new Vector3(-39f, -14f, 80f);
                else if (mapNo == 1659 && uid == 128)
                    shadowOff = new Vector3(0f, -66f, 0f);
                else if (mapNo == 1659 && uid == 129)
                    shadowOff = new Vector3(0f, -21f, 0f);
                else if (mapNo == 2363 && (uid == 16 || uid == 15 || uid == 32 || uid == 33))
                    shadowOff = new Vector3(0f, -15f, 0f);

                Vector3 shadowPos = actor.GetShadowCurrentPos();
                if ((mapNo == 2107 && uid == 5) || (mapNo == 2102 && uid == 4))
                {
                    shadowPos = actor.transform.position;
                    shadowPos.y = 0f;
                }

                shadowLocalPosition = shadowPos + new Vector3(ff9Shadow.xOffset, actor.shadowHeightOffset * 1f, ff9Shadow.zOffset) + shadowOff;
                return IsFinite(shadowLocalPosition.x) && IsFinite(shadowLocalPosition.y) && IsFinite(shadowLocalPosition.z);
            }
            catch
            {
                shadowLocalPosition = Vector3.zero;
                return false;
            }
        }

        private void ApplyShadowVisualWorldOffset(FieldMapActor actor, Vector3 shadowBaseLocal, Vector3 worldDelta)
        {
            if (actor == null || actor.shadowTran == null || _fieldMap == null)
                return;

            actor.shadowTran.localPosition = shadowBaseLocal + worldDelta;
            UpdateShadowDepth(actor);
        }

        private void UpdateShadowDepth(FieldMapActor actor)
        {
            if (actor == null || actor.shadowTran == null)
                return;

            Matrix4x4 cam = FF9StateSystem.Common.FF9.cam;
            UInt16 proj = FF9StateSystem.Common.FF9.proj;
            Vector2 projectionOffset = FF9StateSystem.Common.FF9.projectionOffset;
            Single shZ = PSX.CalculateGTE_RTPTZ(actor.shadowTran.position, Matrix4x4.identity, cam, (Single)proj, projectionOffset);
            shZ = (Int32)shZ / 4 + FF9StateSystem.Field.FF9Field.loc.map.charOTOffset;
            actor.shadowZ = -(Int32)shZ;
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
                if (!renderer.enabled)
                    continue;

                bodyRendererCount++;
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
                if (renderers[r] != null && renderers[r].enabled && !IsShadowRenderer(renderers[r]))
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
            Boolean warpEnabled = FF9DepthVRFieldRenderer.PlateVisible && FF9DepthVRFieldRenderer.ForegroundMaskDepthWarpEnabled;
            for (Int32 i = 0; i < _entries.Length; i++)
            {
                MeshEntry entry = _entries[i];
                if (entry == null || entry.Mesh == null || entry.Transform == null || entry.BaseVertices == null || entry.WorkingVertices == null)
                    continue;

                if (!warpEnabled)
                {
                    entry.Mesh.vertices = entry.BaseVertices;
                    entry.Mesh.RecalculateBounds();
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
                Log.Message("[FF9DepthVR] Mask warp enabled=" + warpEnabled + " meshes=" + warped + " vertices=" + _lastWarpedVertices);
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

    public sealed class FF9DepthVRCompareCameraCull : MonoBehaviour
    {
        private readonly Dictionary<Renderer, Boolean> _savedStates = new Dictionary<Renderer, Boolean>();
        private Renderer[] _depthRenderers;
        private Boolean _hideDepth;

        public void Configure(Renderer[] depthRenderers, Boolean hideDepth)
        {
            RestoreSavedStates();
            _depthRenderers = depthRenderers;
            _hideDepth = hideDepth;
            enabled = _hideDepth && _depthRenderers != null && _depthRenderers.Length > 0;
        }

        private void OnPreCull()
        {
            if (!_hideDepth || !FF9DepthVRFieldRenderer.CompareEnabled || _depthRenderers == null)
                return;

            RestoreSavedStates();
            for (Int32 i = 0; i < _depthRenderers.Length; i++)
            {
                Renderer renderer = _depthRenderers[i];
                if (renderer == null)
                    continue;

                _savedStates[renderer] = renderer.enabled;
                renderer.enabled = false;
            }
        }

        private void OnPostRender()
        {
            RestoreSavedStates();
        }

        private void OnDisable()
        {
            RestoreSavedStates();
        }

        private void RestoreSavedStates()
        {
            if (_savedStates.Count == 0)
                return;

            foreach (KeyValuePair<Renderer, Boolean> pair in _savedStates)
            {
                if (pair.Key != null)
                    pair.Key.enabled = pair.Value;
            }
            _savedStates.Clear();
        }
    }

    public sealed class FF9DepthVRFallbackFieldBridge : MonoBehaviour
    {
        private global::FieldMap _fieldMap;
        private FF9DepthVRSbsStereo _sbsStereo;
        private FF9DepthVRSbsUiStereo _sbsUiStereo;
        private GUIStyle _statusStyle;

        public void Initialize(global::FieldMap fieldMap)
        {
            _fieldMap = fieldMap;

            if (_sbsStereo == null)
            {
                _sbsStereo = gameObject.GetComponent<FF9DepthVRSbsStereo>();
                if (_sbsStereo == null)
                    _sbsStereo = gameObject.AddComponent<FF9DepthVRSbsStereo>();
            }
            _sbsStereo.Initialize(fieldMap, true);

            if (_sbsUiStereo == null)
            {
                _sbsUiStereo = gameObject.GetComponent<FF9DepthVRSbsUiStereo>();
                if (_sbsUiStereo == null)
                    _sbsUiStereo = gameObject.AddComponent<FF9DepthVRSbsUiStereo>();
            }
            _sbsUiStereo.Initialize();
        }

        private void Update()
        {
            FF9DepthVRFieldRenderer.TryHandleSbsToggleInput();
            FF9DepthVRFieldRenderer.ApplyActorCameraScroll(_fieldMap);
        }

        private void OnGUI()
        {
            if (_statusStyle == null)
            {
                _statusStyle = new GUIStyle(GUI.skin.box);
                _statusStyle.alignment = TextAnchor.MiddleLeft;
                _statusStyle.fontSize = 14;
                _statusStyle.normal.textColor = Color.white;
            }

            GUI.Box(new Rect(8f, 8f, Mathf.Min(620f, Screen.width - 16f), 26f), FF9DepthVRFieldRenderer.ModeStatusText, _statusStyle);
        }
    }

    public sealed class FF9DepthVRSbsStereo : MonoBehaviour
    {
        private global::FieldMap _fieldMap;
        private Camera _mainCamera;
        private Camera _rightCamera;
        private Rect _mainRect = new Rect(0f, 0f, 1f, 1f);
        private Single _mainAspect = 1f;
        private Vector3 _basePosition;
        private Quaternion _baseRotation;
        private Vector3 _baseScale = Vector3.one;
        private Boolean _hasBasePose;
        private Boolean _wasEnabled;
        private Boolean _applyFallbackInputLook;

        public void Initialize(global::FieldMap fieldMap)
        {
            Initialize(fieldMap, false);
        }

        public void Initialize(global::FieldMap fieldMap, Boolean applyFallbackInputLook)
        {
            _fieldMap = fieldMap;
            _applyFallbackInputLook = applyFallbackInputLook;
            _mainCamera = fieldMap != null ? fieldMap.GetMainCamera() : null;
            if (_mainCamera != null)
            {
                _mainRect = _mainCamera.rect;
                _mainAspect = _mainCamera.aspect;
            }
            ApplyState(true);
        }

        private void LateUpdate()
        {
            FF9DepthVRFieldRenderer.TryHandleStereoIpdInput(FF9DepthVRFieldRenderer.GetCurrentFieldStereoIpdProfile());
            if (_mainCamera == null && _fieldMap != null)
                _mainCamera = _fieldMap.GetMainCamera();
            ApplyState(false);
        }

        private void OnDisable()
        {
            RestoreMainCamera();
        }

        private void OnDestroy()
        {
            RestoreMainCamera();
            DestroyRightCamera();
        }

        private void ApplyState(Boolean force)
        {
            Boolean enabled = FF9DepthVRFieldRenderer.SbsActive;
            if (_mainCamera == null)
                return;

            if (enabled)
            {
                EnsureRightCamera();
                SyncStereoCameras();
            }
            else if (force || _wasEnabled)
            {
                RestoreMainCamera();
                if (_rightCamera != null)
                    _rightCamera.enabled = false;
            }

            _wasEnabled = enabled;
        }

        private void EnsureRightCamera()
        {
            if (_rightCamera != null)
                return;

            GameObject go = new GameObject("FF9DepthVR_SBS_RightCamera");
            go.transform.parent = _mainCamera.transform.parent;
            _rightCamera = go.AddComponent<Camera>();
            _rightCamera.enabled = false;
            Log.Message("[FF9DepthVR] SBS right-eye camera created.");
        }

        private void SyncStereoCameras()
        {
            if (_rightCamera == null || _mainCamera == null)
                return;

            if (_wasEnabled && _hasBasePose)
            {
                _mainCamera.transform.position = _basePosition;
                _mainCamera.transform.rotation = _baseRotation;
                _mainCamera.transform.localScale = _baseScale;
            }

            _basePosition = _mainCamera.transform.position;
            _baseRotation = _mainCamera.transform.rotation;
            _baseScale = _mainCamera.transform.localScale;
            _hasBasePose = true;

            Quaternion viewRotation = _baseRotation;
            if (_applyFallbackInputLook)
            {
                Vector2 look = FF9DepthVRFieldRenderer.InputLookNormalized(true);
                Single xMultiplier = FF9DepthVRFieldRenderer.SbsActive ? FF9DepthVRFieldRenderer.ViewAngleXMultiplier : 1f;
                Quaternion lookRotation = Quaternion.Euler(-look.y * FF9DepthVRFieldRenderer.ViewAngleMultiplier, look.x * FF9DepthVRFieldRenderer.ViewAngleMultiplier * xMultiplier, 0f);
                viewRotation = _baseRotation * lookRotation;
            }

            Single aspect = SbsEyeAspect();
            _mainCamera.pixelRect = new Rect(0f, 0f, Screen.width * 0.5f, Screen.height);
            _mainCamera.aspect = aspect;
            _rightCamera.CopyFrom(_mainCamera);
            _rightCamera.pixelRect = new Rect(Screen.width * 0.5f, 0f, Screen.width * 0.5f, Screen.height);
            _rightCamera.aspect = aspect;
            _rightCamera.depth = _mainCamera.depth + 0.01f;
            _rightCamera.enabled = true;

            Single eyeOffset = FF9DepthVRFieldRenderer.GetStereoEyeOffset(FF9DepthVRFieldRenderer.GetCurrentFieldStereoIpdProfile());
            if (eyeOffset <= 0.0001f)
            {
                // Keep both eyes on the mono field camera by default; this preserves the stable actor/mask grounding.
                _mainCamera.transform.position = _basePosition;
                _mainCamera.transform.rotation = viewRotation;
                _mainCamera.transform.localScale = _baseScale;
                _rightCamera.transform.position = _basePosition;
                _rightCamera.transform.rotation = viewRotation;
                _rightCamera.transform.localScale = _baseScale;
            }
            else
            {
                Vector3 right = viewRotation * Vector3.right;
                Vector3 up = viewRotation * Vector3.up;
                Vector3 target = _basePosition + viewRotation * Vector3.forward * 1000f;
                Vector3 leftEye = _basePosition - right * eyeOffset;
                Vector3 rightEye = _basePosition + right * eyeOffset;
                _mainCamera.transform.position = leftEye;
                _mainCamera.transform.rotation = Quaternion.LookRotation((target - leftEye).normalized, up);
                _mainCamera.transform.localScale = _baseScale;
                _rightCamera.transform.position = rightEye;
                _rightCamera.transform.rotation = Quaternion.LookRotation((target - rightEye).normalized, up);
                _rightCamera.transform.localScale = _baseScale;
            }
            SyncCompareCullers();
        }

        private Single SbsEyeAspect()
        {
            if (Screen.height <= 0)
                return _mainAspect;
            return (Screen.width * 0.5f) / Screen.height * 2f;
        }

        private void RestoreMainCamera()
        {
            DisableCompareCull(_mainCamera);
            if (_mainCamera != null)
            {
                if (_wasEnabled && _hasBasePose)
                {
                    _mainCamera.transform.position = _basePosition;
                    _mainCamera.transform.rotation = _baseRotation;
                    _mainCamera.transform.localScale = _baseScale;
                }
                _mainCamera.rect = _mainRect;
                _mainCamera.aspect = _mainAspect;
            }
        }

        private void DestroyRightCamera()
        {
            if (_rightCamera == null)
                return;
            DisableCompareCull(_rightCamera);
            Destroy(_rightCamera.gameObject);
            _rightCamera = null;
        }

        private void SyncCompareCullers()
        {
            Renderer[] depthRenderers = FF9DepthVRFieldRenderer.CompareEnabled ? GetComponentsInChildren<Renderer>(true) : null;
            ConfigureCompareCull(_mainCamera, depthRenderers, FF9DepthVRFieldRenderer.CompareEnabled);
            ConfigureCompareCull(_rightCamera, null, false);
        }

        private static void ConfigureCompareCull(Camera camera, Renderer[] depthRenderers, Boolean hideDepth)
        {
            if (camera == null)
                return;

            FF9DepthVRCompareCameraCull cull = camera.GetComponent<FF9DepthVRCompareCameraCull>();
            if (cull == null)
                cull = camera.gameObject.AddComponent<FF9DepthVRCompareCameraCull>();
            cull.Configure(depthRenderers, hideDepth);
        }

        private static void DisableCompareCull(Camera camera)
        {
            if (camera == null)
                return;

            FF9DepthVRCompareCameraCull cull = camera.GetComponent<FF9DepthVRCompareCameraCull>();
            if (cull != null)
                cull.Configure(null, false);
        }
    }

    public sealed class FF9DepthVRBattleStereo : MonoBehaviour
    {
        private const Single ConvergenceDistance = 4000f;

        private static readonly Dictionary<Camera, FF9DepthVRBattleStereo> Instances = new Dictionary<Camera, FF9DepthVRBattleStereo>();

        private Camera _mainCamera;
        private Camera _rightCamera;
        private FF9DepthVRBattleRightEyePostRenderBridge _rightEyePostRenderBridge;
        private Rect _mainRect = new Rect(0f, 0f, 1f, 1f);
        private Single _mainAspect = 1f;
        private Vector3 _basePosition;
        private Quaternion _baseRotation;
        private Vector3 _baseScale = Vector3.one;
        private Boolean _wasEnabled;

        private void Awake()
        {
            _mainCamera = GetComponent<Camera>();
            if (_mainCamera != null)
            {
                _mainRect = _mainCamera.rect;
                _mainAspect = _mainCamera.aspect;
                RegisterInstance();
            }
        }

        internal static Boolean TryProjectSbsUiPoint(Camera sourceCamera, Vector3 worldPosition, out Vector3 screenPosition)
        {
            screenPosition = Vector3.zero;
            if (!FF9DepthVRFieldRenderer.SbsActive || sourceCamera == null || Screen.width <= 1 || Screen.height <= 0)
                return false;

            FF9DepthVRBattleStereo stereo;
            if (!Instances.TryGetValue(sourceCamera, out stereo) || stereo == null || stereo._mainCamera == null || stereo._rightCamera == null || !stereo._rightCamera.enabled)
                return false;

            Vector3 leftViewport = stereo._mainCamera.WorldToViewportPoint(worldPosition);
            Vector3 rightViewport = stereo._rightCamera.WorldToViewportPoint(worldPosition);
            if (!IsFinite(leftViewport.x) || !IsFinite(leftViewport.y) || !IsFinite(rightViewport.x) || !IsFinite(rightViewport.y))
                return false;

            Single halfWidth = Screen.width * 0.5f;
            Single x = (leftViewport.x + rightViewport.x) * 0.5f * halfWidth;
            Single y = (leftViewport.y + rightViewport.y) * 0.5f * Screen.height;
            Single z = (leftViewport.z + rightViewport.z) * 0.5f;
            screenPosition = new Vector3(x, y, z);
            return true;
        }

        private void Update()
        {
            FF9DepthVRFieldRenderer.TryHandleSbsToggleInput();
            FF9DepthVRFieldRenderer.TryHandleMovieDebugOverlayInput();
            FF9DepthVRFieldRenderer.TryHandleStereoIpdInput(FF9DepthVRFieldRenderer.StereoIpdProfile.Battle);
        }

        private void LateUpdate()
        {
            ApplyState(false);
        }

        private void OnDisable()
        {
            RestoreMainCamera();
        }

        private void OnDestroy()
        {
            UnregisterInstance();
            RestoreMainCamera();
            DestroyRightCamera();
        }

        private void ApplyState(Boolean force)
        {
            if (_mainCamera == null)
                _mainCamera = GetComponent<Camera>();
            if (_mainCamera == null)
                return;
            RegisterInstance();

            Boolean enabled = FF9DepthVRFieldRenderer.SbsActive && IsBattleScene();
            if (enabled)
            {
                EnsureRightCamera();
                SyncStereoCameras();
            }
            else if (force || _wasEnabled)
            {
                RestoreMainCamera();
                if (_rightCamera != null)
                    _rightCamera.enabled = false;
            }

            _wasEnabled = enabled;
        }

        private void EnsureRightCamera()
        {
            if (_rightCamera != null)
                return;

            GameObject go = new GameObject("FF9DepthVR_SBS_RightBattleCamera");
            go.transform.parent = _mainCamera.transform.parent;
            _rightCamera = go.AddComponent<Camera>();
            _rightEyePostRenderBridge = go.AddComponent<FF9DepthVRBattleRightEyePostRenderBridge>();
            _rightCamera.enabled = false;
            Log.Message("[FF9DepthVR] SBS right-eye battle camera created.");
        }

        private void SyncStereoCameras()
        {
            if (_rightCamera == null || _mainCamera == null)
                return;

            if (!_wasEnabled)
            {
                _mainRect = _mainCamera.rect;
                _mainAspect = _mainCamera.aspect;
            }
            else
            {
                _mainCamera.transform.position = _basePosition;
                _mainCamera.transform.rotation = _baseRotation;
                _mainCamera.transform.localScale = _baseScale;
            }

            _basePosition = _mainCamera.transform.position;
            _baseRotation = _mainCamera.transform.rotation;
            _baseScale = _mainCamera.transform.localScale;

            Vector2 look = FF9DepthVRFieldRenderer.InputLookNormalized(true);
            Quaternion lookRotation = Quaternion.Euler(-look.y * FF9DepthVRFieldRenderer.ViewAngleMultiplier, look.x * FF9DepthVRFieldRenderer.ViewAngleMultiplier * FF9DepthVRFieldRenderer.ViewAngleXMultiplier, 0f);
            Quaternion battleRotation = _baseRotation * lookRotation;
            Vector3 baseForward = _baseRotation * Vector3.forward;
            Vector3 forward = battleRotation * Vector3.forward;
            Vector3 target = FindBattleConvergenceTarget();
            Single targetDistance = Mathf.Max(200f, Vector3.Dot(target - _basePosition, baseForward));
            target += (forward - baseForward) * targetDistance;
            Vector3 right = battleRotation * Vector3.right;
            Vector3 up = battleRotation * Vector3.up;
            Single eyeOffset = FF9DepthVRFieldRenderer.GetStereoEyeOffset(FF9DepthVRFieldRenderer.StereoIpdProfile.Battle);
            Vector3 leftEye = _basePosition - right * eyeOffset;
            Vector3 rightEye = _basePosition + right * eyeOffset;

            _mainCamera.rect = new Rect(0f, 0f, 0.5f, 1f);
            _mainCamera.aspect = _mainAspect;
            _mainCamera.transform.position = leftEye;
            _mainCamera.transform.rotation = Quaternion.LookRotation((target - leftEye).normalized, up);

            _rightCamera.CopyFrom(_mainCamera);
            _rightCamera.rect = new Rect(0.5f, 0f, 0.5f, 1f);
            _rightCamera.aspect = _mainAspect;
            _rightCamera.depth = _mainCamera.depth + 0.01f;
            _rightCamera.enabled = true;
            _rightCamera.transform.position = rightEye;
            _rightCamera.transform.rotation = Quaternion.LookRotation((target - rightEye).normalized, up);
            _rightCamera.transform.localScale = _baseScale;
            if (_rightEyePostRenderBridge != null)
                _rightEyePostRenderBridge.Configure(_rightCamera, _mainCamera.GetComponent<BattleRainRenderer>());
        }

        private void RegisterInstance()
        {
            if (_mainCamera != null)
                Instances[_mainCamera] = this;
        }

        private void UnregisterInstance()
        {
            if (_mainCamera != null && Instances.ContainsKey(_mainCamera) && Instances[_mainCamera] == this)
                Instances.Remove(_mainCamera);
        }

        private Vector3 FindBattleConvergenceTarget()
        {
            GameObject root = GameObject.Find("BattleMap Root");
            if (root == null)
                return _basePosition + _baseRotation * Vector3.forward * ConvergenceDistance;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
            Boolean hasBounds = false;
            Bounds bounds = new Bounds();
            for (Int32 i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.transform == null)
                    continue;
                if (renderer.name.StartsWith("FF9DepthVR_SBS", StringComparison.Ordinal))
                    continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds)
                return _basePosition + _baseRotation * Vector3.forward * ConvergenceDistance;
            return bounds.center;
        }

        private void RestoreMainCamera()
        {
            if (_mainCamera == null)
                return;
            if (_wasEnabled)
            {
                _mainCamera.transform.position = _basePosition;
                _mainCamera.transform.rotation = _baseRotation;
                _mainCamera.transform.localScale = _baseScale;
            }
            _mainCamera.rect = _mainRect;
            _mainCamera.aspect = _mainAspect;
        }

        private void DestroyRightCamera()
        {
            if (_rightCamera == null)
                return;
            Destroy(_rightCamera.gameObject);
            _rightCamera = null;
            _rightEyePostRenderBridge = null;
        }

        internal static Boolean IsBattleScene()
        {
            String scene = Application.loadedLevelName;
            return String.Equals(scene, "BattleMap", StringComparison.Ordinal) || String.Equals(scene, "BattleMapDebug", StringComparison.Ordinal);
        }

        private static Boolean IsFinite(Single value)
        {
            return !Single.IsNaN(value) && !Single.IsInfinity(value);
        }
    }

    public sealed class FF9DepthVRBattleRightEyePostRenderBridge : MonoBehaviour
    {
        private Camera _rightCamera;
        private BattleRainRenderer _sourceRainRenderer;

        public void Configure(Camera rightCamera, BattleRainRenderer sourceRainRenderer)
        {
            _rightCamera = rightCamera;
            _sourceRainRenderer = sourceRainRenderer;
        }

        private void OnPostRender()
        {
            if (!FF9DepthVRFieldRenderer.SbsActive || !FF9DepthVRBattleStereo.IsBattleScene())
                return;
            if (_rightCamera == null)
                _rightCamera = GetComponent<Camera>();
            if (_rightCamera == null || !_rightCamera.enabled)
                return;

            Single savedWidthOffset = SFX.screenWidthOffset;
            Single savedWidth = SFX.screenWidth;
            Single savedHeight = SFX.screenHeight;

            try
            {
                if (_sourceRainRenderer != null)
                    _sourceRainRenderer.nf_BbgRain();

                SFX.screenWidthOffset = Screen.width * 0.5f;
                SFX.screenWidth = Screen.width * 0.5f;
                SFX.screenHeight = Screen.height;
                SFX.PostRender();
                UnifiedBattleSequencer.LoopRender();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FF9DepthVR] Failed to mirror battle post-render effects for SBS right eye.");
            }
            finally
            {
                SFX.screenWidthOffset = savedWidthOffset;
                SFX.screenWidth = savedWidth;
                SFX.screenHeight = savedHeight;
            }
        }
    }

    public sealed class FF9DepthVRMovieSbsStereo : MonoBehaviour
    {
        private static readonly Dictionary<Camera, FF9DepthVRMovieSbsStereo> Instances = new Dictionary<Camera, FF9DepthVRMovieSbsStereo>();

        private Camera _mainCamera;
        private Camera _leftCamera;
        private Camera _rightCamera;
        private Rect _mainRect = new Rect(0f, 0f, 1f, 1f);
        private Single _mainAspect = 1f;
        private Boolean _mainCameraWasEnabled = true;
        private Vector3 _basePosition;
        private Quaternion _baseRotation;
        private Vector3 _baseScale = Vector3.one;
        private Behaviour _aspectController;
        private Boolean _aspectControllerWasEnabled;
        private Int32 _extraCullingMask;
        private Boolean _hasBasePose;
        private Boolean _wasEnabled;

        public void Initialize(Camera camera)
        {
            _mainCamera = camera != null ? camera : GetComponent<Camera>();
            if (_mainCamera != null)
            {
                _mainRect = _mainCamera.rect;
                _mainAspect = _mainCamera.aspect;
                _mainCameraWasEnabled = _mainCamera.enabled;
                _aspectController = _mainCamera.GetComponent("PSXCameraAspect") as Behaviour;
                _aspectControllerWasEnabled = _aspectController != null && _aspectController.enabled;
                CaptureBasePose();
                Instances[_mainCamera] = this;
            }
            ApplyState(true);
        }

        internal static Boolean TryGetBasePose(Camera camera, out Vector3 position, out Quaternion rotation)
        {
            FF9DepthVRMovieSbsStereo stereo;
            if (camera != null && Instances.TryGetValue(camera, out stereo) && stereo != null && stereo._hasBasePose)
            {
                position = stereo._basePosition;
                rotation = stereo._baseRotation;
                return true;
            }

            position = camera != null ? camera.transform.position : Vector3.zero;
            rotation = camera != null ? camera.transform.rotation : Quaternion.identity;
            return camera != null;
        }

        internal static Single GetRenderAspect(Camera camera)
        {
            if (FF9DepthVRFieldRenderer.SbsActive && Screen.width > 0 && Screen.height > 0)
                return (Screen.width * 0.5f) / Screen.height;
            if (camera != null && camera.aspect > 0f)
                return camera.aspect;
            if (Screen.width > 0 && Screen.height > 0)
                return (Single)Screen.width / Screen.height;
            return 4f / 3f;
        }

        internal static void IncludeLayer(Camera camera, Int32 layer)
        {
            if (camera == null || layer < 0 || layer >= 32)
                return;

            Int32 mask = 1 << layer;
            camera.cullingMask |= mask;

            FF9DepthVRMovieSbsStereo stereo;
            if (!Instances.TryGetValue(camera, out stereo) || stereo == null)
                return;

            stereo._extraCullingMask |= mask;
            if (stereo._leftCamera != null)
                stereo._leftCamera.cullingMask |= mask;
            if (stereo._rightCamera != null)
                stereo._rightCamera.cullingMask |= mask;
        }

        internal static void AppendDebugInfo(Camera camera, StringBuilder sb)
        {
            FF9DepthVRMovieSbsStereo stereo;
            if (camera == null || !Instances.TryGetValue(camera, out stereo) || stereo == null)
            {
                sb.AppendLine("movie stereo: none");
                return;
            }

            sb.Append("movie stereo: active=").Append(stereo._wasEnabled)
                .Append(" baseRect=").Append(RectText(stereo._mainRect))
                .Append(" baseAspect=").Append(FloatText(stereo._mainAspect))
                .Append(" aspectCtl=");
            if (stereo._aspectController == null)
                sb.AppendLine("none");
            else
                sb.Append(stereo._aspectController.GetType().Name).Append(" enabled=").Append(stereo._aspectController.enabled).AppendLine();

            AppendCameraLine(sb, "sourceCam", stereo._mainCamera);
            AppendCameraLine(sb, "leftCam", stereo._leftCamera);
            AppendCameraLine(sb, "rightCam", stereo._rightCamera);
        }

        private void Update()
        {
            FF9DepthVRFieldRenderer.TryHandleSbsToggleInput();
            FF9DepthVRFieldRenderer.TryHandleMovieDebugOverlayInput();
            FF9DepthVRFieldRenderer.TryHandleStereoIpdInput(FF9DepthVRFieldRenderer.StereoIpdProfile.StandaloneMovie);
        }

        private void LateUpdate()
        {
            ApplyState(false);
        }

        private void OnDisable()
        {
            RestoreMainCamera();
        }

        private void OnDestroy()
        {
            if (_mainCamera != null && Instances.ContainsKey(_mainCamera) && Instances[_mainCamera] == this)
                Instances.Remove(_mainCamera);
            RestoreMainCamera();
            DestroyLeftCamera();
            DestroyRightCamera();
        }

        private void ApplyState(Boolean force)
        {
            if (_mainCamera == null)
                _mainCamera = GetComponent<Camera>();
            if (_mainCamera == null)
                return;

            if (_wasEnabled)
                RestoreMainCamera();
            CaptureBasePose();

            Boolean enabled = FF9DepthVRFieldRenderer.SbsActive;
            if (enabled)
            {
                if (_mainCameraWasEnabled == false)
                    _mainCameraWasEnabled = _mainCamera.enabled;
                EnsureLeftCamera();
                EnsureRightCamera();
                SyncStereoCameras();
            }
            else if (force || _wasEnabled)
            {
                if (_leftCamera != null)
                    _leftCamera.enabled = false;
                if (_rightCamera != null)
                    _rightCamera.enabled = false;
                _mainCamera.enabled = _mainCameraWasEnabled;
            }

            _wasEnabled = enabled;
        }

        private void CaptureBasePose()
        {
            if (_mainCamera == null)
                return;
            _basePosition = _mainCamera.transform.position;
            _baseRotation = _mainCamera.transform.rotation;
            _baseScale = _mainCamera.transform.localScale;
            _hasBasePose = true;
        }

        private void EnsureRightCamera()
        {
            if (_rightCamera != null)
                return;

            GameObject go = new GameObject("FF9DepthVR_SBS_RightMovieCamera");
            go.transform.parent = _mainCamera.transform.parent;
            _rightCamera = go.AddComponent<Camera>();
            _rightCamera.enabled = false;
            Log.Message("[FF9DepthVR] SBS right-eye movie camera created.");
        }

        private void EnsureLeftCamera()
        {
            if (_leftCamera != null)
                return;

            GameObject go = new GameObject("FF9DepthVR_SBS_LeftMovieCamera");
            go.transform.parent = _mainCamera.transform.parent;
            _leftCamera = go.AddComponent<Camera>();
            _leftCamera.enabled = false;
            Log.Message("[FF9DepthVR] SBS left-eye movie camera created.");
        }

        private void SyncStereoCameras()
        {
            if (_leftCamera == null || _rightCamera == null || _mainCamera == null)
                return;

            if (_aspectController != null && _aspectController.enabled)
                _aspectController.enabled = false;

            Single aspect = GetRenderAspect(_mainCamera);
            Vector3 right = _baseRotation * Vector3.right;
            Vector3 up = _baseRotation * Vector3.up;
            Vector3 target = _basePosition + _baseRotation * Vector3.forward * FF9DepthVRFieldRenderer.MoviePlateDistance;
            Single eyeOffset = FF9DepthVRFieldRenderer.GetStereoEyeOffset(FF9DepthVRFieldRenderer.StereoIpdProfile.StandaloneMovie);
            Vector3 leftEye = _basePosition - right * eyeOffset;
            Vector3 rightEye = _basePosition + right * eyeOffset;

            _mainCamera.enabled = false;

            _leftCamera.CopyFrom(_mainCamera);
            _leftCamera.rect = new Rect(0f, 0f, 0.5f, 1f);
            _leftCamera.aspect = aspect;
            _leftCamera.depth = _mainCamera.depth;
            _leftCamera.cullingMask |= _extraCullingMask;
            _leftCamera.enabled = true;
            _leftCamera.transform.position = leftEye;
            _leftCamera.transform.rotation = Quaternion.LookRotation((target - leftEye).normalized, up);
            _leftCamera.transform.localScale = _baseScale;

            _rightCamera.CopyFrom(_mainCamera);
            _rightCamera.rect = new Rect(0.5f, 0f, 0.5f, 1f);
            _rightCamera.aspect = aspect;
            _rightCamera.depth = _mainCamera.depth + 0.01f;
            _rightCamera.cullingMask |= _extraCullingMask;
            _rightCamera.enabled = true;
            _rightCamera.transform.position = rightEye;
            _rightCamera.transform.rotation = Quaternion.LookRotation((target - rightEye).normalized, up);
            _rightCamera.transform.localScale = _baseScale;
        }

        private void RestoreMainCamera()
        {
            if (_mainCamera == null)
                return;
            if (_hasBasePose)
            {
                _mainCamera.transform.position = _basePosition;
                _mainCamera.transform.rotation = _baseRotation;
                _mainCamera.transform.localScale = _baseScale;
            }
            _mainCamera.rect = _mainRect;
            _mainCamera.aspect = _mainAspect;
            _mainCamera.enabled = _mainCameraWasEnabled;
            if (_aspectController != null)
                _aspectController.enabled = _aspectControllerWasEnabled;
        }

        private void DestroyRightCamera()
        {
            if (_rightCamera == null)
                return;
            Destroy(_rightCamera.gameObject);
            _rightCamera = null;
        }

        private void DestroyLeftCamera()
        {
            if (_leftCamera == null)
                return;
            Destroy(_leftCamera.gameObject);
            _leftCamera = null;
        }

        private static void AppendCameraLine(StringBuilder sb, String label, Camera camera)
        {
            if (camera == null)
            {
                sb.Append(label).AppendLine(": null");
                return;
            }

            Transform transform = camera.transform;
            sb.Append(label)
                .Append(": enabled=").Append(camera.enabled)
                .Append(" depth=").Append(FloatText(camera.depth))
                .Append(" aspect=").Append(FloatText(camera.aspect))
                .Append(" fov=").Append(FloatText(camera.fieldOfView))
                .Append(" ortho=").Append(camera.orthographic)
                .Append(" orthoSize=").Append(FloatText(camera.orthographicSize))
                .Append(" cull=").Append(camera.cullingMask)
                .Append(" rect=").Append(RectText(camera.rect))
                .Append(" pixel=").Append(RectText(camera.pixelRect))
                .Append(" pos=").Append(VectorText(transform.position))
                .Append(" rot=").Append(VectorText(transform.eulerAngles))
                .AppendLine();
        }

        private static String RectText(Rect rect)
        {
            return FloatText(rect.x) + "," + FloatText(rect.y) + "," + FloatText(rect.width) + "," + FloatText(rect.height);
        }

        private static String VectorText(Vector3 vector)
        {
            return FloatText(vector.x) + "," + FloatText(vector.y) + "," + FloatText(vector.z);
        }

        private static String FloatText(Single value)
        {
            return value.ToString("0.###");
        }
    }

    public sealed class FF9DepthVRNativeMoviePlaneSbsScaler : MonoBehaviour
    {
        private Transform _target;
        private Vector3 _baseScale = Vector3.one;
        private Renderer[] _renderers = new Renderer[0];
        private Boolean[] _baseRendererEnabled = new Boolean[0];
        private global::FieldMap _suppressionFieldMap;
        private Boolean _hasBaseScale;
        private Boolean _scaleForSbs;
        private Boolean _suppressInSbs;
        private Boolean _scaled;
        private Boolean _renderersSuppressed;
        private Boolean _hasSuppressionPlateVisible;
        private Boolean _suppressionPlateVisible;

        public void Initialize(GameObject nativeMoviePlane, Boolean scaleForSbs)
        {
            Initialize(nativeMoviePlane, scaleForSbs, false, null);
        }

        public void Initialize(GameObject nativeMoviePlane, Boolean scaleForSbs, Boolean suppressInSbs, global::FieldMap suppressionFieldMap)
        {
            if (nativeMoviePlane == null)
                return;

            if (_scaled)
                RestoreBaseScale();
            RestoreRendererStates();

            _target = nativeMoviePlane.transform;
            _baseScale = _target.localScale;
            _renderers = nativeMoviePlane.GetComponentsInChildren<Renderer>(true);
            _baseRendererEnabled = new Boolean[_renderers.Length];
            for (Int32 i = 0; i < _renderers.Length; i++)
                _baseRendererEnabled[i] = _renderers[i] != null && _renderers[i].enabled;
            _hasBaseScale = true;
            _scaleForSbs = scaleForSbs;
            _suppressInSbs = suppressInSbs;
            _suppressionFieldMap = suppressionFieldMap;
            _scaled = false;
            _renderersSuppressed = false;
            _hasSuppressionPlateVisible = false;
            enabled = true;
            ApplyState();
        }

        private void LateUpdate()
        {
            ApplyState();
        }

        private void OnDisable()
        {
            RestoreBaseScale();
            RestoreRendererStates();
        }

        private void OnDestroy()
        {
            RestoreBaseScale();
            RestoreRendererStates();
        }

        public void Shutdown()
        {
            RestoreBaseScale();
            RestoreRendererStates();
            enabled = false;
            Destroy(this);
        }

        private void ApplyState()
        {
            ApplyScale();
            ApplyRendererSuppression();
        }

        private void ApplyScale()
        {
            if (_target == null || !_hasBaseScale)
                return;

            if (FF9DepthVRFieldRenderer.SbsActive && _scaleForSbs)
            {
                _target.localScale = new Vector3(_baseScale.x * 0.5f, _baseScale.y, _baseScale.z);
                _scaled = true;
            }
            else
            {
                RestoreBaseScale();
            }
        }

        private void RestoreBaseScale()
        {
            if (_target == null || !_hasBaseScale || !_scaled)
                return;

            _target.localScale = _baseScale;
            _scaled = false;
        }

        private void ApplyRendererSuppression()
        {
            Boolean suppress = FF9DepthVRFieldRenderer.SbsActive && _suppressInSbs;
            ApplySuppressionPlateVisibility(suppress);

            if (suppress)
            {
                for (Int32 i = 0; i < _renderers.Length; i++)
                {
                    Renderer renderer = _renderers[i];
                    if (renderer != null)
                        renderer.enabled = false;
                }
                _renderersSuppressed = true;
            }
            else
            {
                RestoreRendererStates();
            }
        }

        private void ApplySuppressionPlateVisibility(Boolean visible)
        {
            if (_suppressionFieldMap == null)
                return;
            if (_hasSuppressionPlateVisible && _suppressionPlateVisible == visible)
                return;

            FF9DepthVRFieldRenderer.SetDepthReplacementVisible(_suppressionFieldMap, visible);
            _suppressionPlateVisible = visible;
            _hasSuppressionPlateVisible = true;
        }

        private void RestoreRendererStates()
        {
            if (_renderers == null || _baseRendererEnabled == null || !_renderersSuppressed)
                return;

            Int32 count = Mathf.Min(_renderers.Length, _baseRendererEnabled.Length);
            for (Int32 i = 0; i < count; i++)
            {
                Renderer renderer = _renderers[i];
                if (renderer != null)
                    renderer.enabled = _baseRendererEnabled[i];
            }
            _renderersSuppressed = false;
        }
    }

    internal sealed class FF9DepthVRTheoraDepthStream : IDisposable
    {
        private const Int32 ReadbackWidth = FF9DepthVRMovieBgPlate.MovieColumns + 1;
        private const Int32 ReadbackHeight = FF9DepthVRMovieBgPlate.MovieRows + 1;

        private readonly String _path;
        private IntPtr _context;
        private IntPtr _nativeTextureContext;
        private Texture2D _yTexture;
        private Texture2D _readableDepth;
        private RenderTexture _readbackTarget;
        private Boolean _open;
        private Boolean _disposed;
        private Boolean _loggedFirstFrame;

        [DllImport("theorawrapper")]
        private static extern IntPtr CreateContext();

        [DllImport("theorawrapper")]
        private static extern void DestroyContext(IntPtr context);

        [DllImport("theorawrapper")]
        private static extern Boolean OpenStream(IntPtr context, String path, Int32 offset, Int32 size, Boolean pot, Boolean scanDuration, Int32 maxSkipFrames);

        [DllImport("theorawrapper")]
        private static extern void CloseStream(IntPtr context);

        [DllImport("theorawrapper")]
        private static extern Int32 GetYStride(IntPtr context);

        [DllImport("theorawrapper")]
        private static extern Int32 GetYHeight(IntPtr context);

        [DllImport("theorawrapper")]
        private static extern IntPtr GetNativeHandle(IntPtr context, Int32 planeIndex);

        [DllImport("theorawrapper")]
        private static extern IntPtr GetNativeTextureContext(IntPtr context);

        [DllImport("theorawrapper")]
        private static extern void SetTargetDisplayDecodeTime(IntPtr context, Double targetTime);

        internal FF9DepthVRTheoraDepthStream(String path)
        {
            _path = path;
            _context = CreateContext();
            if (_context == IntPtr.Zero)
                throw new InvalidOperationException("[FF9DepthVR] Failed to create Theora depth context.");

            _open = OpenStream(_context, _path, 0, 0, false, false, 16);
            if (!_open)
                throw new FileLoadException("[FF9DepthVR] Failed to open depth movie stream.", _path);
        }

        internal Texture2D UpdateReadableDepth(Single seconds)
        {
            if (_disposed || !_open || _context == IntPtr.Zero)
                return null;

            SetTargetDisplayDecodeTime(_context, Math.Max(0.0, (Double)seconds));
            GL.IssuePluginEvent(7);
            EnsurePlaneTexture();
            if (_yTexture == null)
                return null;

            EnsureReadbackBuffers();
            Graphics.Blit(_yTexture, _readbackTarget);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _readbackTarget;
            _readableDepth.ReadPixels(new Rect(0f, 0f, ReadbackWidth, ReadbackHeight), 0, 0, false);
            _readableDepth.Apply(false, false);
            RenderTexture.active = previous;
            FlipDecodedReadbackVertical(_readableDepth);
            NormalizeAlphaDepthIfNeeded(_readableDepth);

            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                Log.Message("[FF9DepthVR] Depth movie stream first readback path=" + _path + " source=" + _yTexture.width + "x" + _yTexture.height + " readback=" + _readableDepth.width + "x" + _readableDepth.height);
            }
            return _readableDepth;
        }

        private void EnsurePlaneTexture()
        {
            IntPtr nativeTextureContext = GetNativeTextureContext(_context);
            if (nativeTextureContext == _nativeTextureContext && _yTexture != null)
                return;

            if (_yTexture != null)
                UnityEngine.Object.Destroy(_yTexture);

            Int32 width = Mathf.Max(1, GetYStride(_context));
            Int32 height = Mathf.Max(1, GetYHeight(_context));
            _yTexture = Texture2D.CreateExternalTexture(width, height, TextureFormat.Alpha8, false, true, GetNativeHandle(_context, 0));
            _yTexture.wrapMode = TextureWrapMode.Clamp;
            _yTexture.filterMode = FilterMode.Bilinear;
            _nativeTextureContext = nativeTextureContext;
        }

        private void EnsureReadbackBuffers()
        {
            if (_readableDepth == null)
            {
                _readableDepth = new Texture2D(ReadbackWidth, ReadbackHeight, TextureFormat.ARGB32, false, true);
                _readableDepth.wrapMode = TextureWrapMode.Clamp;
                _readableDepth.filterMode = FilterMode.Bilinear;
            }

            if (_readbackTarget == null)
            {
                _readbackTarget = new RenderTexture(ReadbackWidth, ReadbackHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                _readbackTarget.wrapMode = TextureWrapMode.Clamp;
                _readbackTarget.filterMode = FilterMode.Bilinear;
                _readbackTarget.Create();
            }
        }

        private static void FlipDecodedReadbackVertical(Texture2D texture)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 1)
                return;

            Color32[] pixels = texture.GetPixels32();
            Int32 width = texture.width;
            Int32 halfHeight = texture.height / 2;
            Color32[] row = new Color32[width];
            for (Int32 y = 0; y < halfHeight; y++)
            {
                Int32 top = y * width;
                Int32 bottom = (texture.height - 1 - y) * width;
                Array.Copy(pixels, top, row, 0, width);
                Array.Copy(pixels, bottom, pixels, top, width);
                Array.Copy(row, 0, pixels, bottom, width);
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        private static void NormalizeAlphaDepthIfNeeded(Texture2D texture)
        {
            Color32[] pixels = texture.GetPixels32();
            Int32 rgbMin = 255;
            Int32 rgbMax = 0;
            Int32 alphaMin = 255;
            Int32 alphaMax = 0;
            for (Int32 i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                Int32 rgb = Math.Max(pixel.r, Math.Max(pixel.g, pixel.b));
                rgbMin = Math.Min(rgbMin, rgb);
                rgbMax = Math.Max(rgbMax, rgb);
                alphaMin = Math.Min(alphaMin, pixel.a);
                alphaMax = Math.Max(alphaMax, pixel.a);
            }

            if (rgbMax > 2 || alphaMax - alphaMin <= 2)
                return;

            for (Int32 i = 0; i < pixels.Length; i++)
            {
                Byte depth = pixels[i].a;
                pixels[i] = new Color32(depth, depth, depth, 255);
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_open && _context != IntPtr.Zero)
            {
                CloseStream(_context);
                _open = false;
            }
            if (_context != IntPtr.Zero)
            {
                DestroyContext(_context);
                _context = IntPtr.Zero;
            }
            if (_yTexture != null)
            {
                UnityEngine.Object.Destroy(_yTexture);
                _yTexture = null;
            }
            if (_readableDepth != null)
            {
                UnityEngine.Object.Destroy(_readableDepth);
                _readableDepth = null;
            }
            if (_readbackTarget != null)
            {
                _readbackTarget.Release();
                UnityEngine.Object.Destroy(_readbackTarget);
                _readbackTarget = null;
            }
        }
    }

    public sealed class FF9DepthVRMovieBgPlate : MonoBehaviour
    {
        internal const Int32 MovieColumns = 96;
        internal const Int32 MovieRows = 54;
        private const Single StandaloneFrameOverscanX = 0.4f;
        private const Single StandaloneFrameOverscanY = 0.12f;
        private const Single StandaloneDepthEdgeFade = 0.12f;

        private struct MoviePlateFrame
        {
            public Single MinX;
            public Single MaxX;
            public Single MinY;
            public Single MaxY;
            public Single CenterZ;
            public Single NearZ;
            public Single FarZ;

            public Single Width => Mathf.Max(0f, MaxX - MinX);
            public Single Height => Mathf.Max(0f, MaxY - MinY);

            public static MoviePlateFrame Centered(Single width, Single height, Single centerZ, Single nearZ, Single farZ)
            {
                Single halfWidth = Mathf.Max(0.001f, width) * 0.5f;
                Single halfHeight = Mathf.Max(0.001f, height) * 0.5f;
                return new MoviePlateFrame
                {
                    MinX = -halfWidth,
                    MaxX = halfWidth,
                    MinY = -halfHeight,
                    MaxY = halfHeight,
                    CenterZ = centerZ,
                    NearZ = nearZ,
                    FarZ = farZ
                };
            }
        }

        private global::FieldMap _fieldMap;
        private Camera _movieCamera;
        private Int32 _movieCameraOriginalCullingMask;
        private MovieMaterial _movieMaterial;
        private Renderer _sourceRenderer;
        private Boolean _sourceRendererWasEnabled;
        private Vector3 _sourceRendererOriginalScale = Vector3.one;
        private Boolean _hasSourceRendererOriginalScale;
        private GameObject _root;
        private Mesh _mesh;
        private MeshRenderer _meshRenderer;
        private Material _material;
        private FF9DepthVRParallax _parallax;
        private Texture2D _colorTexture;
        private Texture2D _depthTexture;
        private FF9DepthVRTheoraDepthStream _depthMovieStream;
        private String _depthMoviePath;
        private Boolean _depthTextureFromMovieStream;
        private String _movieKey;
        private Int32 _lastFrame = -1;
        private Boolean _loggedFirstFrame;
        private Boolean _loggedMissingFrames;
        private Boolean _loggedStandaloneFrame;
        private Boolean _hidNativeMoviePlane;
        private Boolean _hidFieldDepthReplacement;
        private Boolean _hasDepthSurfaceFrame;
        private Boolean _standaloneMode;
        private Boolean _isActive;

        public Boolean IsActive => _isActive;

        public void Initialize(global::FieldMap fieldMap, MovieMaterial movieMaterial, GameObject nativeMoviePlane)
        {
            _fieldMap = fieldMap;
            _movieCamera = null;
            _movieCameraOriginalCullingMask = 0;
            _standaloneMode = false;
            _movieMaterial = movieMaterial;
            _sourceRenderer = nativeMoviePlane != null ? nativeMoviePlane.GetComponent<Renderer>() : null;
            if (_sourceRenderer != null)
            {
                _sourceRendererWasEnabled = _sourceRenderer.enabled;
                _sourceRendererOriginalScale = _sourceRenderer.transform.localScale;
                _hasSourceRendererOriginalScale = true;
            }
            else
            {
                _sourceRendererWasEnabled = false;
                _sourceRendererOriginalScale = Vector3.one;
                _hasSourceRendererOriginalScale = false;
            }
            _movieKey = null;
            _lastFrame = -1;
            _depthMoviePath = null;
            _depthTextureFromMovieStream = false;
            _loggedFirstFrame = false;
            _loggedMissingFrames = false;
            _loggedStandaloneFrame = false;
            _hidNativeMoviePlane = false;
            _hidFieldDepthReplacement = false;
            _hasDepthSurfaceFrame = false;
            _isActive = true;
            enabled = true;
            Log.Message("[FF9DepthVR] Field movie BGPlate renderer initialized.");
        }

        public void InitializeStandalone(MovieMaterial movieMaterial, GameObject nativeMoviePlane, Camera movieCamera)
        {
            _fieldMap = null;
            _movieCamera = movieCamera;
            _movieCameraOriginalCullingMask = movieCamera != null ? movieCamera.cullingMask : 0;
            _standaloneMode = true;
            _movieMaterial = movieMaterial;
            _sourceRenderer = nativeMoviePlane != null ? nativeMoviePlane.GetComponent<Renderer>() : null;
            if (_sourceRenderer != null)
            {
                _sourceRendererWasEnabled = _sourceRenderer.enabled;
                _sourceRendererOriginalScale = _sourceRenderer.transform.localScale;
                _hasSourceRendererOriginalScale = true;
            }
            else
            {
                _sourceRendererWasEnabled = false;
                _sourceRendererOriginalScale = Vector3.one;
                _hasSourceRendererOriginalScale = false;
            }
            _movieKey = null;
            _lastFrame = -1;
            _depthMoviePath = null;
            _depthTextureFromMovieStream = false;
            _loggedFirstFrame = false;
            _loggedMissingFrames = false;
            _loggedStandaloneFrame = false;
            _hidNativeMoviePlane = false;
            _hidFieldDepthReplacement = false;
            _hasDepthSurfaceFrame = false;
            _isActive = true;
            enabled = true;
            Log.Message("[FF9DepthVR] Standalone movie BGPlate renderer initialized.");
        }

        private void Update()
        {
            FF9DepthVRFieldRenderer.TryHandleSbsToggleInput();
        }

        private void LateUpdate()
        {
            if ((!_standaloneMode && _fieldMap == null) || (_standaloneMode && _movieCamera == null) || _movieMaterial == null)
                return;

            ApplyStandaloneDepthSurfaceState();
            SyncRootParent();
            ApplyFieldMovieDepthSurfaceState();

            String movieKey = _movieMaterial.movieKey;
            if (String.IsNullOrEmpty(movieKey))
                return;

            Int32 frame = Mathf.Max(1, _movieMaterial.Frame + 1);
            if (frame == _lastFrame && movieKey == _movieKey)
                return;

            if (!TryLoadFrame(movieKey, frame))
                return;

            _movieKey = movieKey;
            _lastFrame = frame;
        }

        private void OnGUI()
        {
            if (!FF9DepthVRFieldRenderer.MovieDebugOverlayEnabled || !_isActive)
                return;

            String text = BuildDebugOverlayText();
            GUIStyle style = new GUIStyle(GUI.skin.box);
            style.alignment = TextAnchor.UpperLeft;
            style.fontSize = 14;
            style.normal.textColor = Color.white;
            style.wordWrap = false;

            Single width = Mathf.Min(Mathf.Max(320f, Screen.width - 24f), 980f);
            Single height = Mathf.Min(Mathf.Max(240f, Screen.height - 48f), 560f);
            Color oldBackground = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0f, 0f, 0f, 0.85f);
            GUI.Box(new Rect(12f, 36f, width, height), text, style);
            GUI.backgroundColor = oldBackground;
        }

        private String BuildDebugOverlayText()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.AppendLine("FF9DepthVR FMV Debug (F10)");
            sb.Append("screen=").Append(Screen.width).Append("x").Append(Screen.height)
                .Append(" sbs=").Append(FF9DepthVRFieldRenderer.SbsActive)
                .Append(" vr=").Append(FF9DepthVRFieldRenderer.VrCaptureEnabled)
                .Append(" standalone=").Append(_standaloneMode)
                .Append(" depthSurface=").Append(_hasDepthSurfaceFrame)
                .Append(" active=").Append(_isActive)
                .AppendLine();

            if (_movieMaterial != null)
            {
                sb.Append("movie=").Append(_movieMaterial.movieKey)
                    .Append(" frame=").Append(_movieMaterial.Frame).Append("/").Append(_movieMaterial.TotalFrame)
                    .Append(" firstFrame=").Append(_movieMaterial.GetFirstFrame)
                    .Append(" size=").Append(_movieMaterial.Width).Append("x").Append(_movieMaterial.Height)
                    .Append(" aspect=").Append(FloatText(_movieMaterial.AspectRatio))
                    .Append(" fps=").Append(FloatText((Single)_movieMaterial.FPS))
                    .AppendLine();
            }
            else
            {
                sb.AppendLine("movie: null");
            }

            FF9DepthVRMovieSbsStereo.AppendDebugInfo(_movieCamera, sb);
            AppendRendererInfo(sb, "nativePlane", _sourceRenderer);
            AppendTransformInfo(sb, "bgplate", _root != null ? _root.transform : null);
            if (_mesh != null)
            {
                Bounds bounds = _mesh.bounds;
                sb.Append("mesh: verts=").Append(_mesh.vertexCount)
                    .Append(" boundsSize=").Append(VectorText(bounds.size))
                    .Append(" boundsCenter=").Append(VectorText(bounds.center))
                    .AppendLine();
            }
            else
            {
                sb.AppendLine("mesh: null");
            }

            sb.Append("textures: color=");
            AppendTextureInfo(sb, _colorTexture);
            sb.Append(" depth=");
            AppendTextureInfo(sb, _depthTexture);
            sb.AppendLine();
            if (_material != null)
                sb.Append("bgplate material=").Append(_material.shader != null ? _material.shader.name : "null").Append(" queue=").Append(_material.renderQueue).AppendLine();
            return sb.ToString();
        }

        public void Shutdown()
        {
            RestoreNativeMoviePlane();
            RestoreFieldDepthReplacement();
            RestoreMovieCameraMask();
            if (_root != null)
            {
                Destroy(_root);
                _root = null;
            }
            if (_colorTexture != null)
            {
                Destroy(_colorTexture);
                _colorTexture = null;
            }
            DisposeDepthMovieStream();
            if (_depthTexture != null)
            {
                Destroy(_depthTexture);
                _depthTexture = null;
            }
            _mesh = null;
            _meshRenderer = null;
            _material = null;
            _parallax = null;
            _fieldMap = null;
            _movieCamera = null;
            _movieCameraOriginalCullingMask = 0;
            _movieMaterial = null;
            _depthMoviePath = null;
            _hasDepthSurfaceFrame = false;
            _standaloneMode = false;
            _hidFieldDepthReplacement = false;
            _loggedStandaloneFrame = false;
            _isActive = false;
            enabled = false;
        }

        private Boolean TryLoadFrame(String movieKey, Int32 frame)
        {
            FF9DepthVRFieldRenderer.MovieFrameSet frameSet;
            String reason;
            if (!FF9DepthVRFieldRenderer.TryResolveMovieFrameSet(movieKey, out frameSet, out reason))
            {
                LogMissingFramePath("no depth assets for movie=" + movieKey + " reason=" + reason);
                FallbackToNativeMovie();
                return false;
            }

            Boolean depthMovieUnavailable;
            if (TryLoadDepthMovieFrame(movieKey, frameSet, frame, out depthMovieUnavailable))
                return true;
            if (!depthMovieUnavailable && !String.IsNullOrEmpty(frameSet.DepthMoviePath) && File.Exists(frameSet.DepthMoviePath))
                return false;

            return TryLoadPngFrame(movieKey, frameSet, frame);
        }

        private Boolean TryLoadDepthMovieFrame(String movieKey, FF9DepthVRFieldRenderer.MovieFrameSet frameSet, Int32 frame, out Boolean streamUnavailable)
        {
            streamUnavailable = false;
            if (frameSet == null || String.IsNullOrEmpty(frameSet.DepthMoviePath) || !File.Exists(frameSet.DepthMoviePath))
            {
                streamUnavailable = true;
                return false;
            }

            try
            {
                if (_depthMovieStream == null || !String.Equals(_depthMoviePath, frameSet.DepthMoviePath, StringComparison.OrdinalIgnoreCase))
                {
                    DisposeDepthMovieStream();
                    ReleaseOwnedFrameTextures();
                    _depthMovieStream = new FF9DepthVRTheoraDepthStream(frameSet.DepthMoviePath);
                    _depthMoviePath = frameSet.DepthMoviePath;
                    Log.Message("[FF9DepthVR] Movie BGPlate using depth movie bytes movie=" + movieKey + " path=" + frameSet.DepthMoviePath);
                }
            }
            catch (Exception ex)
            {
                Log.Message("[FF9DepthVR] Movie BGPlate depth movie open failed movie=" + movieKey + " path=" + frameSet.DepthMoviePath + " error=" + ex.Message);
                DisposeDepthMovieStream();
                streamUnavailable = true;
                return false;
            }

            Texture2D depth = _depthMovieStream.UpdateReadableDepth(_movieMaterial.PlayPosition);
            if (depth == null)
                return false;

            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                Log.Message("[FF9DepthVR] Movie BGPlate first depth movie frame movie=" + movieKey + " frame=" + frame + " colorMovie=" + _movieMaterial.Width + "x" + _movieMaterial.Height + " depthReadback=" + depth.width + "x" + depth.height + " source=bytes");
            }

            Single width;
            Single height;
            Single depthScale;
            MoviePlateFrame cameraFrame = default(MoviePlateFrame);
            if (_standaloneMode)
            {
                CalculateStandalonePlateFrame(_movieMaterial.Width, _movieMaterial.Height, out cameraFrame);
                width = cameraFrame.Width;
                height = cameraFrame.Height;
                depthScale = FF9DepthVRFieldRenderer.MovieStandaloneDepthScale;
            }
            else
            {
                CalculateFieldMoviePlateSize(_movieMaterial.Width, _movieMaterial.Height, out width, out height);
                depthScale = FF9DepthVRFieldRenderer.DefaultGeometryDepthScale;
            }

            EnsureRoot(null, depth, width, height, _movieMaterial.Material);
            if (_meshRenderer != null)
                _meshRenderer.enabled = true;
            Vector3[] vertices;
            Vector2[] uvs;
            Color[] colors;
            Int32[] triangles;
            BuildMovieMesh(depth, width, height, depthScale, _standaloneMode, cameraFrame, out vertices, out uvs, out colors, out triangles);
            _mesh.Clear();
            _mesh.vertices = vertices;
            _mesh.uv = uvs;
            _mesh.colors = colors;
            _mesh.triangles = triangles;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            if (_parallax != null)
                _parallax.ReplaceSource(_mesh, vertices, colors, width, height, null, depth);
            _hasDepthSurfaceFrame = true;
            if (_standaloneMode)
            {
                ApplyStandaloneDepthSurfaceState();
            }
            else
            {
                ApplyFieldMovieDepthSurfaceState();
            }

            if (_colorTexture != null)
            {
                Destroy(_colorTexture);
                _colorTexture = null;
            }
            if (_depthTexture != depth && _depthTexture != null && !_depthTextureFromMovieStream)
                Destroy(_depthTexture);
            _depthTexture = depth;
            _depthTextureFromMovieStream = true;
            return true;
        }

        private Boolean TryLoadPngFrame(String movieKey, FF9DepthVRFieldRenderer.MovieFrameSet frameSet, Int32 frame)
        {
            if (frameSet == null || String.IsNullOrEmpty(frameSet.ColorFramesDirectory) || String.IsNullOrEmpty(frameSet.DepthFramesDirectory))
            {
                LogMissingFramePath("missing frame directories for movie=" + movieKey);
                FallbackToNativeMovie();
                return false;
            }

            String frameName = "frame_" + frame.ToString("D6") + ".png";
            String colorPath = Path.Combine(frameSet.ColorFramesDirectory, frameName);
            String depthPath = Path.Combine(frameSet.DepthFramesDirectory, frameName);
            if (!File.Exists(colorPath) || !File.Exists(depthPath))
            {
                LogMissingFramePath("missing color/depth frame color=" + colorPath + " depth=" + depthPath);
                FallbackToNativeMovie();
                return false;
            }

            DisposeDepthMovieStream();
            Texture2D color = LoadPng(colorPath, false);
            Texture2D depth = LoadPng(depthPath, true);
            if (color == null || depth == null)
            {
                if (color != null)
                    Destroy(color);
                if (depth != null)
                    Destroy(depth);
                FallbackToNativeMovie();
                return false;
            }

            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                Log.Message("[FF9DepthVR] Movie BGPlate first frame movie=" + movieKey + " frame=" + frame + " color=" + color.width + "x" + color.height + " depth=" + depth.width + "x" + depth.height + " readable=true");
            }

            Single width;
            Single height;
            Single depthScale;
            MoviePlateFrame cameraFrame = default(MoviePlateFrame);
            if (_standaloneMode)
            {
                CalculateStandalonePlateFrame(color, out cameraFrame);
                width = cameraFrame.Width;
                height = cameraFrame.Height;
                depthScale = FF9DepthVRFieldRenderer.MovieStandaloneDepthScale;
            }
            else
            {
                CalculateFieldMoviePlateSize(color.width, color.height, out width, out height);
                depthScale = FF9DepthVRFieldRenderer.DefaultGeometryDepthScale;
            }
            EnsureRoot(color, depth, width, height);
            if (_meshRenderer != null)
                _meshRenderer.enabled = true;
            Vector3[] vertices;
            Vector2[] uvs;
            Color[] colors;
            Int32[] triangles;
            BuildMovieMesh(depth, width, height, depthScale, _standaloneMode, cameraFrame, out vertices, out uvs, out colors, out triangles);
            _mesh.Clear();
            _mesh.vertices = vertices;
            _mesh.uv = uvs;
            _mesh.colors = colors;
            _mesh.triangles = triangles;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            if (_material != null)
            {
                _material.mainTexture = color;
                if (_material.HasProperty("_MainTex"))
                    _material.SetTexture("_MainTex", color);
                if (_material.HasProperty("_DepthTex"))
                    _material.SetTexture("_DepthTex", depth);
            }
            if (_parallax != null)
                _parallax.ReplaceSource(_mesh, vertices, colors, width, height, color, depth);
            _hasDepthSurfaceFrame = true;
            if (_standaloneMode)
            {
                ApplyStandaloneDepthSurfaceState();
            }
            else
            {
                ApplyFieldMovieDepthSurfaceState();
            }

            if (_colorTexture != null)
                Destroy(_colorTexture);
            if (_depthTexture != null)
                Destroy(_depthTexture);
            _colorTexture = color;
            _depthTexture = depth;
            _depthTextureFromMovieStream = false;
            return true;
        }

        private void ReleaseOwnedFrameTextures()
        {
            if (_colorTexture != null)
            {
                Destroy(_colorTexture);
                _colorTexture = null;
            }
            if (_depthTexture != null && !_depthTextureFromMovieStream)
            {
                Destroy(_depthTexture);
                _depthTexture = null;
            }
            if (!_depthTextureFromMovieStream)
                _depthTexture = null;
        }

        private void DisposeDepthMovieStream()
        {
            if (_depthMovieStream != null)
            {
                _depthMovieStream.Dispose();
                _depthMovieStream = null;
            }
            if (_depthTextureFromMovieStream)
                _depthTexture = null;
            _depthTextureFromMovieStream = false;
            _depthMoviePath = null;
        }

        private void LogMissingFramePath(String message)
        {
            if (_loggedMissingFrames)
                return;

            _loggedMissingFrames = true;
            Log.Message("[FF9DepthVR] Movie BGPlate missing frames: " + message);
        }

        private void CalculateStandalonePlateSize(Texture2D color, out Single width, out Single height)
        {
            MoviePlateFrame frame;
            CalculateStandalonePlateFrame(color, out frame);
            width = frame.Width;
            height = frame.Height;
        }

        private void CalculateStandalonePlateSize(Int32 colorWidth, Int32 colorHeight, out Single width, out Single height)
        {
            MoviePlateFrame frame;
            CalculateStandalonePlateFrame(colorWidth, colorHeight, out frame);
            width = frame.Width;
            height = frame.Height;
        }

        private void CalculateFieldMoviePlateSize(Int32 colorWidth, Int32 colorHeight, out Single width, out Single height)
        {
            if (FF9DepthVRFieldRenderer.TryGetDepthReplacementPlateSize(_fieldMap, out width, out height))
                return;

            width = Mathf.Max(1, colorWidth) * FF9DepthVRFieldRenderer.DefaultSourceScale;
            height = Mathf.Max(1, colorHeight) * FF9DepthVRFieldRenderer.DefaultSourceScale;
        }

        private void CalculateStandalonePlateFrame(Texture2D color, out MoviePlateFrame frame)
        {
            Int32 colorWidth = color != null ? color.width : 0;
            Int32 colorHeight = color != null ? color.height : 0;
            CalculateStandalonePlateFrame(colorWidth, colorHeight, out frame);
        }

        private void CalculateStandalonePlateFrame(Int32 colorWidth, Int32 colorHeight, out MoviePlateFrame frame)
        {
            CalculateFallbackStandalonePlateFrame(colorWidth, colorHeight, out frame);
            if (!_loggedStandaloneFrame)
            {
                _loggedStandaloneFrame = true;
                String projection = _movieCamera != null && _movieCamera.orthographic ? "orthographic" : "perspective";
                Log.Message("[FF9DepthVR] Standalone movie BGPlate using camera " + projection + " frame size=" + FloatText(frame.Width) + "x" + FloatText(frame.Height) + " centerZ=" + FloatText(frame.CenterZ) + " clip=" + FloatText(frame.NearZ) + "-" + FloatText(frame.FarZ) + ".");
            }
        }

        private void CalculateFallbackStandalonePlateFrame(Int32 colorWidth, Int32 colorHeight, out MoviePlateFrame frame)
        {
            Single distance = FF9DepthVRFieldRenderer.MoviePlateDistance;
            Single viewHeight;
            if (_movieCamera != null && _movieCamera.orthographic)
            {
                viewHeight = Mathf.Max(0.001f, _movieCamera.orthographicSize) * 2f;
            }
            else
            {
                Single fov = _movieCamera != null ? Mathf.Max(1f, _movieCamera.fieldOfView) : 60f;
                viewHeight = 2f * distance * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            }
            Single viewAspect = FF9DepthVRMovieSbsStereo.GetRenderAspect(_movieCamera);
            Single viewWidth = viewHeight * Mathf.Max(0.01f, viewAspect);
            Single videoAspect = colorHeight > 0 ? (Single)Mathf.Max(1, colorWidth) / colorHeight : 4f / 3f;

            if (FF9DepthVRFieldRenderer.SbsActive)
            {
                frame = MoviePlateFrame.Centered(viewWidth, viewHeight, distance, MovieCameraNearZ(), MovieCameraFarZ());
                return;
            }

            Single height = viewHeight;
            Single width = height * videoAspect;
            if (width > viewWidth)
            {
                width = viewWidth;
                height = width / videoAspect;
            }
            frame = MoviePlateFrame.Centered(width, height, distance, MovieCameraNearZ(), MovieCameraFarZ());
        }

        private static MoviePlateFrame ExpandCameraFrame(MoviePlateFrame frame, Single fraction)
        {
            Single paddingX = frame.Width * Mathf.Max(0f, fraction);
            Single paddingY = frame.Height * Mathf.Max(0f, fraction);
            frame.MinX -= paddingX;
            frame.MaxX += paddingX;
            frame.MinY -= paddingY;
            frame.MaxY += paddingY;
            return frame;
        }

        private Boolean TryCalculateNativeMoviePlaneFrame(out MoviePlateFrame frame)
        {
            frame = default(MoviePlateFrame);
            if (_sourceRenderer == null || _movieCamera == null)
                return false;

            MeshFilter meshFilter = _sourceRenderer.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null && TryBuildCameraFrameFromLocalBounds(meshFilter.sharedMesh.bounds, _sourceRenderer.transform, out frame))
                return true;

            return TryBuildCameraFrameFromWorldBounds(_sourceRenderer.bounds, out frame);
        }

        private Boolean TryBuildCameraFrameFromLocalBounds(Bounds bounds, Transform sourceTransform, out MoviePlateFrame frame)
        {
            frame = default(MoviePlateFrame);
            if (sourceTransform == null || _movieCamera == null)
                return false;

            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            Vector3[] corners = new Vector3[8]
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, max.y, max.z)
            };
            return TryBuildCameraFrameFromWorldCorners(corners, sourceTransform, out frame);
        }

        private Boolean TryBuildCameraFrameFromWorldBounds(Bounds bounds, out MoviePlateFrame frame)
        {
            frame = default(MoviePlateFrame);
            if (_movieCamera == null)
                return false;

            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            Vector3[] corners = new Vector3[8]
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, max.y, max.z)
            };
            return TryBuildCameraFrameFromWorldCorners(corners, null, out frame);
        }

        private Boolean TryBuildCameraFrameFromWorldCorners(Vector3[] corners, Transform sourceTransform, out MoviePlateFrame frame)
        {
            frame = default(MoviePlateFrame);
            if (corners == null || corners.Length == 0 || _movieCamera == null)
                return false;

            Single minX = Single.MaxValue;
            Single maxX = Single.MinValue;
            Single minY = Single.MaxValue;
            Single maxY = Single.MinValue;
            Single minZ = Single.MaxValue;
            Single maxZ = Single.MinValue;
            Transform cameraTransform = _movieCamera.transform;
            for (Int32 i = 0; i < corners.Length; i++)
            {
                Vector3 world = sourceTransform != null ? sourceTransform.TransformPoint(corners[i]) : corners[i];
                Vector3 cameraPoint = cameraTransform.InverseTransformPoint(world);
                if (!IsFinite(cameraPoint))
                    return false;

                minX = Mathf.Min(minX, cameraPoint.x);
                maxX = Mathf.Max(maxX, cameraPoint.x);
                minY = Mathf.Min(minY, cameraPoint.y);
                maxY = Mathf.Max(maxY, cameraPoint.y);
                minZ = Mathf.Min(minZ, cameraPoint.z);
                maxZ = Mathf.Max(maxZ, cameraPoint.z);
            }

            Single nearZ = MovieCameraNearZ();
            Single farZ = MovieCameraFarZ();
            Single centerZ = Mathf.Clamp((minZ + maxZ) * 0.5f, nearZ, farZ);
            frame = new MoviePlateFrame
            {
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY,
                CenterZ = centerZ,
                NearZ = nearZ,
                FarZ = farZ
            };
            return IsSaneCameraFrame(frame);
        }

        private Boolean IsSaneCameraFrame(MoviePlateFrame frame)
        {
            if (!IsFinite(frame.MinX) || !IsFinite(frame.MaxX) || !IsFinite(frame.MinY) || !IsFinite(frame.MaxY) || !IsFinite(frame.CenterZ) || !IsFinite(frame.NearZ) || !IsFinite(frame.FarZ))
                return false;
            if (frame.Width < 0.001f || frame.Height < 0.001f)
                return false;
            if (frame.FarZ <= frame.NearZ)
                return false;
            if (Mathf.Abs(frame.CenterZ) > 10000f || frame.Width > 10000f || frame.Height > 10000f)
                return false;
            return true;
        }

        private Single MovieCameraNearZ()
        {
            if (_movieCamera == null)
                return 0.005f;
            return Mathf.Max(0.001f, _movieCamera.nearClipPlane + 0.005f);
        }

        private Single MovieCameraFarZ()
        {
            Single nearZ = MovieCameraNearZ();
            if (_movieCamera == null)
                return Mathf.Max(nearZ + 0.01f, 1000f);
            return Mathf.Max(nearZ + 0.01f, _movieCamera.farClipPlane - 0.005f);
        }

        private static Boolean IsFinite(Vector3 vector)
        {
            return IsFinite(vector.x) && IsFinite(vector.y) && IsFinite(vector.z);
        }

        private static Boolean IsFinite(Single value)
        {
            return !Single.IsNaN(value) && !Single.IsInfinity(value);
        }

        private void EnsureRoot(Texture2D color, Texture2D depth, Single width, Single height)
        {
            EnsureRoot(color, depth, width, height, null);
        }

        private void EnsureRoot(Texture2D color, Texture2D depth, Single width, Single height, Material overrideMaterial)
        {
            if (_root != null)
            {
                if (overrideMaterial != null && _meshRenderer != null && _meshRenderer.sharedMaterial != overrideMaterial)
                {
                    _material = overrideMaterial;
                    ConfigureMovieBgPlateMaterial(_material);
                    _meshRenderer.sharedMaterial = _material;
                }
                else if (overrideMaterial == null && color != null && _meshRenderer != null && _movieMaterial != null && _material == _movieMaterial.Material)
                {
                    _material = FF9DepthVRFieldRenderer.CreateMoviePlateMaterial(color, depth);
                    _meshRenderer.sharedMaterial = _material;
                }
                EnsureParallax(width, height, color, depth);
                return;
            }

            _root = new GameObject("FF9DepthVR_MovieBGPlate");
            SyncRootParent();
            _mesh = new Mesh();
            _mesh.name = "FF9DepthVR_MovieBGPlateMesh";
            MeshFilter meshFilter = _root.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = _root.AddComponent<MeshRenderer>();
            _meshRenderer = meshRenderer;
            meshFilter.sharedMesh = _mesh;
            _material = overrideMaterial != null ? overrideMaterial : FF9DepthVRFieldRenderer.CreateMoviePlateMaterial(color, depth);
            ConfigureMovieBgPlateMaterial(_material);
            meshRenderer.sharedMaterial = _material;
            if (_standaloneMode)
                meshRenderer.enabled = false;
            EnsureParallax(width, height, color, depth);
            Log.Message("[FF9DepthVR] " + (_standaloneMode ? "Standalone" : "Field") + " movie BGPlate mesh created shader=" + (_material != null && _material.shader != null ? _material.shader.name : "null") + ".");
        }

        private static void ConfigureMovieBgPlateMaterial(Material material)
        {
            if (material == null)
                return;

            material.renderQueue = FF9DepthVRFieldRenderer.ReplacementPlateRenderQueue;
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetInt("_Cull", (Int32)UnityEngine.Rendering.CullMode.Off);
            material.SetInt("_ZWrite", 0);
            material.SetInt("_ZTest", (Int32)UnityEngine.Rendering.CompareFunction.Always);
            material.SetInt("_SrcBlend", (Int32)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (Int32)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", Color.white);
            if (material.HasProperty("_TintColor"))
                material.SetColor("_TintColor", Color.white);
        }

        private void EnsureParallax(Single width, Single height, Texture2D color, Texture2D depth)
        {
            if (_root == null || _mesh == null || _parallax != null)
                return;

            Single parallaxStrength = FF9DepthVRFieldRenderer.DefaultParallaxStrength;
            Single idleStrength = FF9DepthVRFieldRenderer.DefaultIdleParallaxStrength;
            if (_standaloneMode)
            {
                Single standaloneScale = StandaloneMovieParallaxScale(width, height);
                parallaxStrength *= standaloneScale;
                idleStrength *= standaloneScale;
            }

            _parallax = _root.AddComponent<FF9DepthVRParallax>();
            _parallax.Initialize(_mesh, new Vector3[(MovieColumns + 1) * (MovieRows + 1)], new Color[(MovieColumns + 1) * (MovieRows + 1)], width, height, parallaxStrength, idleStrength, _material, color, depth);
            if (_standaloneMode)
                _parallax.SetCenteredPlateCoordinates(true);
        }

        private Single StandaloneMovieParallaxScale(Single width, Single height)
        {
            Single sourceWidth = _movieMaterial != null ? Mathf.Max(1f, _movieMaterial.Width) : 1280f;
            Single sourceHeight = _movieMaterial != null ? Mathf.Max(1f, _movieMaterial.Height) : 960f;
            Single scaleX = Mathf.Abs(width) / sourceWidth;
            Single scaleY = Mathf.Abs(height) / sourceHeight;
            return Mathf.Max(0.0001f, Mathf.Min(scaleX, scaleY));
        }

        private void SyncRootParent()
        {
            if (_root == null)
                return;

            if (_standaloneMode)
            {
                if (_movieCamera == null)
                    return;

                Transform movieParent = _movieCamera.transform;
                if (_root.transform.parent != movieParent)
                    _root.transform.SetParent(movieParent, false);
                _root.transform.localPosition = Vector3.zero;
                _root.transform.localRotation = Quaternion.identity;
                _root.transform.localScale = Vector3.one;
                Int32 layer = _sourceRenderer != null ? _sourceRenderer.gameObject.layer : _movieCamera.gameObject.layer;
                FF9DepthVRFieldRenderer.SetLayerRecursive(_root, layer);
                if (layer >= 0 && layer < 32)
                {
                    _movieCamera.cullingMask |= 1 << layer;
                    FF9DepthVRMovieSbsStereo.IncludeLayer(_movieCamera, layer);
                }
                return;
            }

            if (_fieldMap == null)
                return;

            Transform plateParent = FF9DepthVRFieldRenderer.GetPlateParent(_fieldMap);
            if (plateParent == null)
                return;

            Transform activeParent = _fieldMap.transform;
            if (_root.transform.parent != activeParent)
                _root.transform.SetParent(activeParent, true);

            _root.transform.position = plateParent.position;
            _root.transform.rotation = plateParent.rotation;
            _root.transform.localScale = Vector3.one;
            FF9DepthVRFieldRenderer.SetLayerRecursive(_root, plateParent.gameObject.layer);
        }

        private static void BuildMovieMesh(Texture2D depthTexture, Single width, Single height, Single depthScale, Boolean centeredCameraPlate, MoviePlateFrame cameraFrame, out Vector3[] vertices, out Vector2[] uvs, out Color[] colors, out Int32[] triangles)
        {
            Int32 columns = MovieColumns;
            Int32 rows = MovieRows;
            Int32 vertexCount = (columns + 1) * (rows + 1);
            vertices = new Vector3[vertexCount];
            uvs = new Vector2[vertexCount];
            colors = new Color[vertexCount];
            for (Int32 y = 0; y <= rows; y++)
            {
                    Single v = (Single)y / rows;
                    for (Int32 x = 0; x <= columns; x++)
                    {
                        Single u = (Single)x / columns;
                        Int32 index = y * (columns + 1) + x;
                    Single geometryU = u;
                    Single geometryV = v;
                    Single sampleU = u;
                    Single sampleV = v;
                    if (centeredCameraPlate)
                    {
                        geometryU = -StandaloneFrameOverscanX + u * (1f + StandaloneFrameOverscanX * 2f);
                        geometryV = -StandaloneFrameOverscanY + v * (1f + StandaloneFrameOverscanY * 2f);
                        sampleU = Mathf.Clamp01(geometryU);
                        sampleV = Mathf.Clamp01(geometryV);
                    }
                    Single sampledDepth = FF9DepthVRFieldRenderer.SampleDepthValue(depthTexture, sampleU, 1f - sampleV);
                    Single z = (sampledDepth - 0.5f) * depthScale;
                    if (centeredCameraPlate)
                        z *= MovieDepthEdgeWeight(sampleU, sampleV);
                    vertices[index] = centeredCameraPlate
                        ? new Vector3(LerpUnclamped(cameraFrame.MinX, cameraFrame.MaxX, geometryU), LerpUnclamped(cameraFrame.MaxY, cameraFrame.MinY, geometryV), Mathf.Clamp(cameraFrame.CenterZ + z, cameraFrame.NearZ, cameraFrame.FarZ))
                        : new Vector3(u * width, v * height, z);
                    uvs[index] = new Vector2(sampleU, 1f - sampleV);
                    colors[index] = new Color(sampledDepth, sampledDepth, sampledDepth, 1f);
                }
            }

            triangles = new Int32[columns * rows * 12];
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
                    triangles[tri++] = b;
                    triangles[tri++] = c;
                    triangles[tri++] = a;
                    triangles[tri++] = d;
                    triangles[tri++] = c;
                    triangles[tri++] = b;
                }
            }
        }

        private static Single MovieDepthEdgeWeight(Single u, Single v)
        {
            Single edge = Mathf.Min(Mathf.Min(u, 1f - u), Mathf.Min(v, 1f - v));
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(edge / StandaloneDepthEdgeFade));
        }

        private static Single LerpUnclamped(Single a, Single b, Single t)
        {
            return a + (b - a) * t;
        }

        private static Texture2D LoadPng(String path, Boolean linear)
        {
            try
            {
                Byte[] bytes = File.ReadAllBytes(path);
                Texture2D texture = new Texture2D(2, 2, TextureFormat.ARGB32, false, linear);
                if (!texture.LoadImage(bytes))
                {
                    Destroy(texture);
                    return null;
                }
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                return texture;
            }
            catch (Exception ex)
            {
                Log.Message("[FF9DepthVR] Movie BGPlate failed loading " + path + ": " + ex.Message);
                return null;
            }
        }

        private static String ResolveMovieFrameDirectory(String movieKey)
        {
            String basePath;
            return FF9DepthVRFieldRenderer.TryResolveMovieFrameDirectory(movieKey, out basePath) ? basePath : null;
        }

        private void HideNativeMoviePlane()
        {
            if (_sourceRenderer == null || _hidNativeMoviePlane)
                return;

            _sourceRenderer.enabled = false;
            _hidNativeMoviePlane = true;
        }

        private void ShowNativeMoviePlaneDuringPlayback()
        {
            if (_sourceRenderer == null)
                return;

            _sourceRenderer.enabled = true;
            if (_hasSourceRendererOriginalScale)
                _sourceRenderer.transform.localScale = _sourceRendererOriginalScale;
            _hidNativeMoviePlane = false;
        }

        private void RestoreNativeMoviePlane()
        {
            if (_sourceRenderer != null)
            {
                _sourceRenderer.enabled = _sourceRendererWasEnabled;
                if (_hasSourceRendererOriginalScale)
                    _sourceRenderer.transform.localScale = _sourceRendererOriginalScale;
            }
            _hidNativeMoviePlane = false;
        }

        private void HideFieldDepthReplacement()
        {
            if (_standaloneMode || _fieldMap == null || _hidFieldDepthReplacement)
                return;

            FF9DepthVRFieldRenderer.SetDepthReplacementVisible(_fieldMap, false);
            _hidFieldDepthReplacement = true;
        }

        private void RestoreFieldDepthReplacement()
        {
            if (_standaloneMode || _fieldMap == null || !_hidFieldDepthReplacement)
                return;

            FF9DepthVRFieldRenderer.SetDepthReplacementVisible(_fieldMap, true);
            _hidFieldDepthReplacement = false;
        }

        private void FallbackToNativeMovie()
        {
            _hasDepthSurfaceFrame = false;
            if (_meshRenderer != null)
                _meshRenderer.enabled = false;
            ShowNativeMoviePlaneDuringPlayback();
            RestoreFieldDepthReplacement();
        }

        private void ApplyFieldMovieDepthSurfaceState()
        {
            if (_standaloneMode)
                return;

            if (!_hasDepthSurfaceFrame || _movieMaterial == null || !_movieMaterial.GetFirstFrame)
            {
                if (_meshRenderer != null)
                    _meshRenderer.enabled = false;
                ShowNativeMoviePlaneDuringPlayback();
                RestoreFieldDepthReplacement();
                return;
            }

            if (_meshRenderer != null)
                _meshRenderer.enabled = true;
            HideFieldDepthReplacement();
            HideNativeMoviePlane();
        }

        private void ApplyStandaloneDepthSurfaceState()
        {
            if (!_standaloneMode)
                return;

            if (FF9DepthVRFieldRenderer.SbsActive && _hasDepthSurfaceFrame && _meshRenderer != null)
            {
                _meshRenderer.enabled = true;
                HideNativeMoviePlane();
                return;
            }

            if (_meshRenderer != null)
                _meshRenderer.enabled = false;

            if (_sourceRenderer == null)
                return;

            if (FF9DepthVRFieldRenderer.SbsActive)
            {
                _sourceRenderer.enabled = true;
                if (_hasSourceRendererOriginalScale)
                    _sourceRenderer.transform.localScale = new Vector3(_sourceRendererOriginalScale.x * 0.5f, _sourceRendererOriginalScale.y, _sourceRendererOriginalScale.z);
                _hidNativeMoviePlane = false;
            }
            else
            {
                RestoreNativeMoviePlane();
            }
        }

        private void RestoreMovieCameraMask()
        {
            if (_standaloneMode && _movieCamera != null)
                _movieCamera.cullingMask = _movieCameraOriginalCullingMask;
        }

        private static void AppendRendererInfo(StringBuilder sb, String label, Renderer renderer)
        {
            if (renderer == null)
            {
                sb.Append(label).AppendLine(": null");
                return;
            }

            Transform transform = renderer.transform;
            Material material = renderer.sharedMaterial;
            sb.Append(label)
                .Append(": enabled=").Append(renderer.enabled)
                .Append(" layer=").Append(renderer.gameObject.layer)
                .Append(" parent=").Append(transform.parent != null ? transform.parent.name : "none")
                .Append(" localPos=").Append(VectorText(transform.localPosition))
                .Append(" localScale=").Append(VectorText(transform.localScale))
                .Append(" localRot=").Append(VectorText(transform.localEulerAngles))
                .Append(" material=").Append(material != null && material.shader != null ? material.shader.name : "null")
                .AppendLine();
        }

        private static void AppendTransformInfo(StringBuilder sb, String label, Transform transform)
        {
            if (transform == null)
            {
                sb.Append(label).AppendLine(": null");
                return;
            }

            sb.Append(label)
                .Append(": layer=").Append(transform.gameObject.layer)
                .Append(" parent=").Append(transform.parent != null ? transform.parent.name : "none")
                .Append(" localPos=").Append(VectorText(transform.localPosition))
                .Append(" localScale=").Append(VectorText(transform.localScale))
                .Append(" localRot=").Append(VectorText(transform.localEulerAngles))
                .AppendLine();
        }

        private static void AppendTextureInfo(StringBuilder sb, Texture2D texture)
        {
            if (texture == null)
                sb.Append("null");
            else
                sb.Append(texture.width).Append("x").Append(texture.height);
        }

        private static String VectorText(Vector3 vector)
        {
            return FloatText(vector.x) + "," + FloatText(vector.y) + "," + FloatText(vector.z);
        }

        private static String FloatText(Single value)
        {
            return value.ToString("0.###");
        }

        private void OnDestroy()
        {
            Shutdown();
        }
    }

    public sealed class FF9DepthVRSbsUiStereo : MonoBehaviour
    {
        private static FF9DepthVRSbsUiStereo _activeInstance;

        private readonly Dictionary<Camera, UiCameraPair> _uiCameras = new Dictionary<Camera, UiCameraPair>();
        private Boolean _wasEnabled;

        public void Initialize()
        {
            if (_activeInstance != null && _activeInstance != this)
            {
                enabled = false;
                return;
            }

            _activeInstance = this;
            ApplyState(true);
        }

        private void LateUpdate()
        {
            if (_activeInstance != this)
                return;

            ApplyState(false);
        }

        private void OnDisable()
        {
            RestoreAll();
        }

        private void OnDestroy()
        {
            if (_activeInstance == this)
                _activeInstance = null;

            RestoreAll();
            DestroyAll();
        }

        private void ApplyState(Boolean force)
        {
            Boolean enabled = FF9DepthVRFieldRenderer.SbsActive;
            if (enabled)
            {
                DiscoverUiCameras();
                foreach (UiCameraPair pair in _uiCameras.Values)
                    SyncUiCamera(pair);
            }
            else if (force || _wasEnabled)
            {
                RestoreAll();
            }

            _wasEnabled = enabled;
        }

        private void DiscoverUiCameras()
        {
            global::UICamera[] uiCameras = UnityEngine.Object.FindObjectsOfType<global::UICamera>();
            for (Int32 i = 0; i < uiCameras.Length; i++)
            {
                global::UICamera uiCamera = uiCameras[i];
                if (uiCamera == null)
                    continue;

                Camera source = uiCamera.cachedCamera;
                if (source == null || source.name.StartsWith("FF9DepthVR_SBS_RightUI", StringComparison.Ordinal))
                    continue;
                if (_uiCameras.ContainsKey(source))
                    continue;

                UiCameraPair pair = new UiCameraPair();
                pair.Source = source;
                pair.SourceRect = source.rect;
                pair.SourceAspect = source.aspect;
                pair.Right = CreateRightUiCamera(source);
                _uiCameras[source] = pair;
            }
        }

        private Camera CreateRightUiCamera(Camera source)
        {
            GameObject go = new GameObject("FF9DepthVR_SBS_RightUI_" + source.name);
            go.transform.parent = source.transform.parent;
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            Log.Message("[FF9DepthVR] SBS right-eye UI camera created from " + source.name + ".");
            return camera;
        }

        private void SyncUiCamera(UiCameraPair pair)
        {
            if (pair == null || pair.Source == null || pair.Right == null)
                return;

            pair.Source.rect = new Rect(0f, 0f, 0.5f, 1f);
            pair.Source.aspect = pair.SourceAspect;
            pair.Right.CopyFrom(pair.Source);
            pair.Right.rect = new Rect(0.5f, 0f, 0.5f, 1f);
            pair.Right.aspect = pair.SourceAspect;
            pair.Right.depth = pair.Source.depth + 0.01f;
            pair.Right.enabled = pair.Source.enabled && pair.Source.gameObject.activeInHierarchy;
            pair.Right.transform.position = pair.Source.transform.position;
            pair.Right.transform.rotation = pair.Source.transform.rotation;
            pair.Right.transform.localScale = pair.Source.transform.localScale;
        }

        private void RestoreAll()
        {
            foreach (UiCameraPair pair in _uiCameras.Values)
            {
                if (pair == null)
                    continue;
                if (pair.Source != null)
                {
                    pair.Source.rect = pair.SourceRect;
                    pair.Source.aspect = pair.SourceAspect;
                }
                if (pair.Right != null)
                    pair.Right.enabled = false;
            }
        }

        private void DestroyAll()
        {
            foreach (UiCameraPair pair in _uiCameras.Values)
                if (pair != null && pair.Right != null)
                    Destroy(pair.Right.gameObject);
            _uiCameras.Clear();
        }

        private sealed class UiCameraPair
        {
            public Camera Source;
            public Camera Right;
            public Rect SourceRect;
            public Single SourceAspect;
        }
    }

    public sealed class FF9DepthVRDiagnostics : MonoBehaviour
    {
        private global::FieldMap _fieldMap;
        private Renderer _renderer;
        private Single _logTimer;
        private GUIStyle _statusStyle;

        public void Initialize(global::FieldMap fieldMap, Renderer renderer)
        {
            _fieldMap = fieldMap;
            _renderer = renderer;
            ApplyVisibility();
        }

        private void Update()
        {
            FF9DepthVRFieldRenderer.TryHandleSbsToggleInput();

            _logTimer -= Time.deltaTime;
            if (_logTimer <= 0f)
            {
                _logTimer = 5f;
                FieldMapActor[] actors = UnityEngine.Object.FindObjectsOfType<FieldMapActor>();
                Log.Message("[FF9DepthVR] Diagnostics actors=" + actors.Length + " plateVisible=" + FF9DepthVRFieldRenderer.PlateVisible + " sbs=" + FF9DepthVRFieldRenderer.SbsActive + " vr=" + FF9DepthVRFieldRenderer.VrCaptureEnabled + " maskWarp=" + FF9DepthVRFieldRenderer.ForegroundMaskDepthWarpEnabled);
            }
        }

        private void OnGUI()
        {
            if (_statusStyle == null)
            {
                _statusStyle = new GUIStyle(GUI.skin.box);
                _statusStyle.alignment = TextAnchor.MiddleLeft;
                _statusStyle.fontSize = 14;
                _statusStyle.normal.textColor = Color.white;
            }

            GUI.Box(new Rect(8f, 8f, Mathf.Min(620f, Screen.width - 16f), 26f), FF9DepthVRFieldRenderer.ModeStatusText, _statusStyle);
        }

        private void ApplyVisibility()
        {
            if (_renderer != null)
                _renderer.enabled = FF9DepthVRFieldRenderer.PlateVisible && !FF9DepthVRFieldRenderer.IsMoviePlateActiveFor(_fieldMap);
        }
    }
}
