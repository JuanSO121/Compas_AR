// File: ARSessionManager.cs
// ✅ v3.4 — Integrar ARPerformanceManager: matchFrameRateRequested + notificación de fases.
//
// ============================================================================
//  CAMBIOS v3.3 → v3.4
// ============================================================================
//
//  PROBLEMA RAÍZ (confirmado en logs del dispositivo):
//    GetRecentDevicePose failed. INVALID_ARGUMENT: Passed timestamp is too new.
//    RESOURCE_EXHAUSTED: Behind by 156ms/180ms, skip current frame
//    FeatureExtraction is taking too long: 112ms
//
//  Estos errores ocurren porque Unity renderiza más rápido de lo que ARCore
//  produce frames VIO. El VIO descarta frames para "ponerse al día", causando
//  el flicker SessionTracking ↔ SessionInitializing observado en los logs.
//
//  SOLUCIÓN OFICIAL — ARSession.matchFrameRateRequested:
//    "If True, the session will block execution until a new AR frame is
//     available and set Application.targetFrameRate to match the native
//     update frequency of the AR session."
//    Ref: AR Foundation 3.x+ docs (ARSession component)
//
//  CAMBIOS v3.4:
//  ─────────────────────────────────────────────────────────────────────────
//  1. InitializeARSession() delega la configuración de framerate a
//     ARPerformanceManager, que aplica matchFrameRateRequested y targetFrameRate.
//     ARSessionManager ya no toca Application.targetFrameRate directamente.
//
//  2. DisablePlaneDetection() notifica BeginHeavyLoad al ARPerformanceManager
//     porque es llamado justo antes de operaciones pesadas de carga.
//     (DisablePlaneDetection no indica "todo terminó" — indica "el modelo
//      está posicionado pero la carga pesada (NavMesh+escaleras) continúa.)
//
//  3. EnablePlaneDetection() notifica EndHeavyLoad — indica que el sistema
//     volvió a modo normal (reposicionamiento de modelo).
//
//  TODOS LOS COMPORTAMIENTOS DE v3.3 SE CONSERVAN ÍNTEGRAMENTE.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.AR
{
    public class ARSessionManager : MonoBehaviour
    {
        [Header("Referencias AR")]
        [SerializeField] private ARPlaneManager    _planeManager;
        [SerializeField] private ARRaycastManager  _raycastManager;
        [SerializeField] private ARAnchorManager   _anchorManager;
        [SerializeField] private ARSession         _arSession;  // ✅ v3.4: referencia directa
        private Unity.XR.CoreUtils.XROrigin _xrOriginCache;

        [Header("Configuración")]
        [SerializeField] private bool  _detectVerticalPlanes    = false;
        [SerializeField] private bool  _showPlaneVisualization  = true;
        [SerializeField] private float _minimumPlaneArea        = 0.5f;

        [Header("─── Estabilidad inicial (FIX 1) ─────────────────────────")]
        [SerializeField] private int   _initialStableFrames    = 30;
        [SerializeField] private float _initialTrackingTimeout = 10f;

        [Header("─── Estabilidad en movimiento rápido (FIX 2) ─────────────")]
        [SerializeField] private float _quickMoveThreshold      = 0.15f;
        [SerializeField] private int   _quickMoveStabilityFrames = 20;

        [Header("─── Debug ───────────────────────────────────────────────────")]
        [SerializeField] private bool _logTracking = true;

        // ─── Estado ───────────────────────────────────────────────────────

        private readonly Dictionary<TrackableId, ARPlane> _detectedPlanes
            = new Dictionary<TrackableId, ARPlane>();
        private readonly List<ARRaycastHit> _raycastHits = new List<ARRaycastHit>();

        private bool _initialTrackingAchieved  = false;
        private int  _consecutiveStableFrames  = 0;

        private Vector3 _lastCameraPos     = Vector3.zero;
        private bool    _cameraInitialized = false;
        private int     _quickMovePauseFrames = 0;
        private int     _suppressQuickMoveFrames = 0;

        private bool _planeDetectionDisabled = false;

        // ─── Propiedades públicas ─────────────────────────────────────────

        public bool IsSessionReady    { get; private set; }
        public int  DetectedPlaneCount => _detectedPlanes.Count;
        public IReadOnlyDictionary<TrackableId, ARPlane> DetectedPlanes => _detectedPlanes;

        public bool IsFullyStable    => _initialTrackingAchieved &&
                                        ARSession.state == ARSessionState.SessionTracking;

        public bool IsQuickMovePaused => _quickMovePauseFrames > 0;

        // ─── Lifecycle ────────────────────────────────────────────────────

        private void Awake() => ValidateDependencies();
        private void Start() => InitializeARSession();

        private void OnEnable()
        {
            if (_planeManager != null)
                _planeManager.trackablesChanged.AddListener(OnTrackablesChanged);
        }

        private void OnDisable()
        {
            _planeManager?.trackablesChanged.RemoveListener(OnTrackablesChanged);
            _xrOriginCache = null;
        }

        private void Update() => UpdateQuickMoveDetection();

        // ─── Inicialización ───────────────────────────────────────────────

        private void ValidateDependencies()
        {
            if (_planeManager    == null) _planeManager    = FindFirstObjectByType<ARPlaneManager>();
            if (_raycastManager  == null) _raycastManager  = FindFirstObjectByType<ARRaycastManager>();
            if (_anchorManager   == null) _anchorManager   = FindFirstObjectByType<ARAnchorManager>();
            if (_arSession       == null) _arSession       = FindFirstObjectByType<ARSession>();

            if (_planeManager == null)
            {
                Debug.LogError("[ARSessionManager] ARPlaneManager no encontrado.");
                enabled = false; return;
            }
            if (_raycastManager == null)
            {
                Debug.LogError("[ARSessionManager] ARRaycastManager no encontrado.");
                enabled = false; return;
            }

            Log("✅ v3.4 Dependencias validadas.");
        }

        private void InitializeARSession()
        {
            try
            {
                ConfigurePlaneDetection();

                // ✅ v3.4: ARPerformanceManager gestiona matchFrameRateRequested y targetFrameRate.
                // ARSessionManager ya no toca Application.targetFrameRate directamente.
                // Si ARPerformanceManager no existe, aplicar matchFrameRate aquí como fallback.
                if (ARPerformanceManager.Instance == null && _arSession != null)
                {
                    _arSession.matchFrameRateRequested = true;
                    Application.targetFrameRate = 30;
                    QualitySettings.vSyncCount  = 0;
                    Log("⚠️ ARPerformanceManager no encontrado — aplicando matchFrameRate fallback.");
                }

                IsSessionReady = true;
                StartCoroutine(WaitForStableTracking());

                Log($"✅ v3.4 Sesión AR inicializada. " +
                    $"Esperando {_initialStableFrames} frames para estabilidad...");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ARSessionManager] Error inicializando: {ex.Message}");
                IsSessionReady = false;
            }
        }

        private void ConfigurePlaneDetection()
        {
            PlaneDetectionMode mode = _detectVerticalPlanes
                ? PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical
                : PlaneDetectionMode.Horizontal;

            _planeManager.requestedDetectionMode = mode;
            Log($"Detección de planos configurada: {mode}");
        }

        // ─── FIX 1 — Esperar tracking estable al inicio ───────────────────

        private IEnumerator WaitForStableTracking()
        {
            float elapsed = 0f;
            _consecutiveStableFrames = 0;

            Log($"⏳ Esperando tracking estable ({_initialStableFrames} frames " +
                $"consecutivos, timeout={_initialTrackingTimeout}s)...");

            while (elapsed < _initialTrackingTimeout)
            {
                yield return null;
                elapsed += Time.deltaTime;

                if (ARSession.state == ARSessionState.SessionTracking)
                {
                    _consecutiveStableFrames++;
                    if (_consecutiveStableFrames >= _initialStableFrames)
                    {
                        _initialTrackingAchieved = true;
                        Log($"✅ Tracking estable alcanzado ({_initialStableFrames} frames " +
                            $"en {elapsed:F1}s).");
                        NotifyTrackingStable();
                        yield break;
                    }
                }
                else
                {
                    if (_consecutiveStableFrames > 0)
                    {
                        Log($"⚠️ Tracking interrumpido en frame {_consecutiveStableFrames} " +
                            $"— reseteando. Estado: {ARSession.state}");
                        _consecutiveStableFrames = 0;
                    }
                }
            }

            Debug.LogWarning($"[ARSessionManager] ⚠️ Timeout esperando tracking estable " +
                             $"({_initialTrackingTimeout}s). Estado: {ARSession.state}");
            _initialTrackingAchieved = true;
            NotifyTrackingStable();
        }

        private void NotifyTrackingStable()
        {
            Core.Events.EventBus.Instance?.Publish(new Core.Events.ShowMessageEvent
            {
                Message  = "Tracking AR estable.",
                Type     = Core.Events.MessageType.Info,
                Duration = 3f
            });

            Log("✅ v3.4 Tracking estable notificado.");
        }

        // ─── FIX 2 — Detección de movimiento rápido ──────────────────────

        public void SuppressQuickMoveDetection(int frames = 5)
        {
            _suppressQuickMoveFrames = frames;
            _cameraInitialized = false;
            Log($"⏸ QuickMove suprimido por {frames} frames.");
        }

        private void UpdateQuickMoveDetection()
        {
            if (!_initialTrackingAchieved) return;

            if (_xrOriginCache == null)
                _xrOriginCache = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();

            if (_xrOriginCache?.Camera == null) return;

            Vector3 currentCameraPos = _xrOriginCache.Camera.transform.position;

            if (!_cameraInitialized)
            {
                _lastCameraPos     = currentCameraPos;
                _cameraInitialized = true;
                return;
            }

            float cameraDelta  = Vector3.Distance(currentCameraPos, _lastCameraPos);
            _lastCameraPos     = currentCameraPos;

            if (_suppressQuickMoveFrames > 0)
            {
                _suppressQuickMoveFrames--;
                if (_suppressQuickMoveFrames == 0)
                    Log("✅ Supresión de QuickMove terminada.");
                return;
            }

            if (_quickMovePauseFrames > 0)
            {
                _quickMovePauseFrames--;
                return;
            }

            if (cameraDelta > _quickMoveThreshold)
            {
                _quickMovePauseFrames = _quickMoveStabilityFrames;
                Log($"⚡ Movimiento rápido detectado (Δ={cameraDelta:F3}m). " +
                    $"Pausando {_quickMoveStabilityFrames} frames.");
            }
        }

        // ─── ✅ v3.4 FIX_CPU — Control de Plane Detection + ARPerformanceManager ────

        /// <summary>
        /// ✅ v3.4 — Deshabilita plane detection Y notifica al ARPerformanceManager
        /// que está por comenzar una fase de carga pesada (NavMesh + escaleras).
        /// </summary>
        public void DisablePlaneDetection()
        {
            if (_planeManager == null) return;
            if (_planeDetectionDisabled) return;

            _planeManager.requestedDetectionMode = PlaneDetectionMode.None;
            _planeDetectionDisabled = true;

            // ✅ v3.4: Notificar inicio de fase pesada para bajar framerate
            // y ceder CPU al VIO. El 'EndHeavyLoad' correspondiente lo llama
            // PersistenceManager cuando ReparentWaypointsAfterAlignment() termina.
            ARPerformanceManager.Instance?.BeginHeavyLoad("PlaneDetection deshabilitada — inicio carga NavMesh+waypoints");

            Log("✅ [v3.4] Plane detection DESHABILITADA — CPU cedida al VIO. " +
                "ARPerformanceManager en modo heavy-load.");
        }

        /// <summary>
        /// ✅ v3.4 — Re-habilita plane detection y notifica fin de fase pesada.
        /// </summary>
        public void EnablePlaneDetection()
        {
            if (_planeManager == null) return;

            PlaneDetectionMode mode = _detectVerticalPlanes
                ? PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical
                : PlaneDetectionMode.Horizontal;

            _planeManager.requestedDetectionMode = mode;
            _planeDetectionDisabled = false;

            ARPerformanceManager.Instance?.EndHeavyLoad("PlaneDetection re-habilitada");

            Log($"✅ [v3.4] Plane detection RE-HABILITADA: {mode}");
        }

        // ─── Plane tracking ───────────────────────────────────────────────

        private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            foreach (var plane in args.added)       ProcessAddedPlane(plane);
            foreach (var plane in args.updated)     ProcessUpdatedPlane(plane);
            foreach (var kvp   in args.removed)     ProcessRemovedPlane(kvp.Value);
        }

        private void ProcessAddedPlane(ARPlane plane)
        {
            bool isFloor = plane.classifications.HasFlag(PlaneClassifications.Floor);
            bool isUnclassifiedHorizontalUp =
                plane.classifications == PlaneClassifications.None &&
                plane.alignment       == PlaneAlignment.HorizontalUp;

            if (!isFloor && !isUnclassifiedHorizontalUp) return;
            if (plane.size.x * plane.size.y < _minimumPlaneArea) return;

            _detectedPlanes[plane.trackableId] = plane;
            ConfigurePlaneVisualization(plane);

            EventBus.Instance?.Publish(new PlaneDetectedEvent
            {
                Plane  = plane,
                Center = plane.center,
                Area   = plane.size.x * plane.size.y
            });
        }

        private void ProcessUpdatedPlane(ARPlane plane)
        {
            if (!_detectedPlanes.ContainsKey(plane.trackableId)) return;
            _detectedPlanes[plane.trackableId] = plane;
            EventBus.Instance?.Publish(new PlaneUpdatedEvent
            {
                Plane     = plane,
                NewCenter = plane.center,
                NewArea   = plane.size.x * plane.size.y
            });
        }

        private void ProcessRemovedPlane(ARPlane plane)
        {
            if (_detectedPlanes.Remove(plane.trackableId))
                EventBus.Instance?.Publish(new PlaneRemovedEvent { Plane = plane });
        }

        private void ConfigurePlaneVisualization(ARPlane plane)
        {
            if (plane.TryGetComponent<MeshRenderer>(out var renderer))
            {
                renderer.enabled = _showPlaneVisualization;
                if (_showPlaneVisualization && renderer.material != null)
                {
                    Color c = renderer.material.color;
                    c.a = 0.3f;
                    renderer.material.color = c;
                }
            }
        }

        // ─── Raycast ──────────────────────────────────────────────────────

        public bool Raycast(Vector2 screenPosition, out ARRaycastHit hit,
            TrackableType trackableTypes = TrackableType.PlaneWithinPolygon)
        {
            hit = default;
            if (_raycastManager == null) return false;
            _raycastHits.Clear();
            if (_raycastManager.Raycast(screenPosition, _raycastHits, trackableTypes))
            { hit = _raycastHits[0]; return true; }
            return false;
        }

        public bool Raycast(Ray ray, out ARRaycastHit hit, float maxDistance = 10f)
        {
            hit = default;
            if (_raycastManager == null) return false;
            _raycastHits.Clear();
            if (_raycastManager.Raycast(ray, _raycastHits, TrackableType.PlaneWithinPolygon))
            {
                foreach (var h in _raycastHits)
                    if (h.distance <= maxDistance) { hit = h; return true; }
            }
            return false;
        }

        // ─── Anchors ──────────────────────────────────────────────────────

        public async Task<ARAnchor> CreateAnchorAsync(Pose pose)
        {
            if (_anchorManager == null)
            { Debug.LogWarning("[ARSessionManager] ARAnchorManager no disponible."); return null; }

            if (ARSession.state != ARSessionState.SessionTracking)
            {
                Debug.LogWarning($"[ARSessionManager] ⚠️ Anchor rechazado: no SessionTracking " +
                                 $"(estado: {ARSession.state}).");
                return null;
            }

            try
            {
                var result = await _anchorManager.TryAddAnchorAsync(pose);
                if (result.status.IsSuccess())
                {
                    ARAnchor anchor = result.value;
                    if (anchor == null || !anchor.enabled)
                    { Debug.LogWarning("[ARSessionManager] ⚠️ Anchor creado pero nulo/desactivado."); return null; }
                    return anchor;
                }
                Debug.LogWarning($"[ARSessionManager] ⚠️ TryAddAnchorAsync falló: " +
                                 $"status={result.status} | ARSession={ARSession.state}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ARSessionManager] Error creando anchor: {ex.Message}");
                return null;
            }
        }

        public void RemoveAnchor(ARAnchor anchor)
        {
            if (anchor == null || _anchorManager == null) return;
            try { _anchorManager.TryRemoveAnchor(anchor); }
            catch (Exception ex)
            { Debug.LogError($"[ARSessionManager] Error removiendo ancla: {ex.Message}"); }
        }

        // ─── Utilities ────────────────────────────────────────────────────

        public void TogglePlaneVisualization(bool show)
        {
            _showPlaneVisualization = show;
            foreach (var kvp in _detectedPlanes)
                if (kvp.Value != null && kvp.Value.TryGetComponent<MeshRenderer>(out var r))
                    r.enabled = show;
        }

        public void ClearAllPlanes()
        {
            foreach (var kvp in _detectedPlanes)
                if (kvp.Value != null) Destroy(kvp.Value.gameObject);
            _detectedPlanes.Clear();
        }

        public ARPlane GetLargestPlane()
        {
            ARPlane largestPlane = null;
            float   maxArea      = 0f;
            foreach (var kvp in _detectedPlanes)
            {
                var plane = kvp.Value;
                if (plane == null) continue;
                float area = plane.size.x * plane.size.y;
                if (area > maxArea) { maxArea = area; largestPlane = plane; }
            }
            return largestPlane;
        }

        private void Log(string msg)
        {
            if (_logTracking) Debug.Log($"[ARSessionManager] {msg}");
        }

        // ─── Debug ────────────────────────────────────────────────────────

        [ContextMenu("ℹ️ Estado actual")]
        private void DebugState()
        {
            bool matchAR = _arSession != null && _arSession.matchFrameRateRequested;
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log("  ARSessionManager v3.4");
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log($"  ARSession state:           {ARSession.state}");
            Debug.Log($"  IsSessionReady:            {IsSessionReady}");
            Debug.Log($"  IsFullyStable:             {IsFullyStable}");
            Debug.Log($"  InitialTracking logrado:   {_initialTrackingAchieved}");
            Debug.Log($"  matchFrameRateRequested:   {matchAR}");
            Debug.Log($"  Application.targetFPS:     {Application.targetFrameRate}");
            Debug.Log($"  ARPerfMgr:                 {(ARPerformanceManager.Instance != null ? "OK" : "NULL")}");
            Debug.Log($"  PlaneDetectionDisabled:    {_planeDetectionDisabled}");
            Debug.Log($"  Planos detectados:         {DetectedPlaneCount}");
            Debug.Log("══════════════════════════════════════════════");
        }

        [ContextMenu("🚫 Deshabilitar Plane Detection")]
        private void DebugDisablePlanes() => DisablePlaneDetection();

        [ContextMenu("✅ Re-habilitar Plane Detection")]
        private void DebugEnablePlanes() => EnablePlaneDetection();
    }
}