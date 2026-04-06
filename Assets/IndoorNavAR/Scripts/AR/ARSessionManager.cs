// File: ARSessionManager.cs
// ✅ v3.3 — REFACTOR_ANCHOR: Quitar publicación de ARSessionReadyEvent de
//           WaitForStableTracking. Ese evento ahora es responsabilidad exclusiva
//           de PersistenceManager v14 cuando la sesión está lista de verdad.
//
// ============================================================================
//  CAMBIOS v3.2 → v3.3
// ============================================================================
//
//  ÚNICO CAMBIO: PublishSessionReady() ya no publica ARSessionReadyEvent.
//
//  RAZÓN:
//    En la arquitectura v14, ARSessionReadyEvent es la señal de que la sesión
//    AR está completamente lista — modelo restaurado, waypoints recreados,
//    anchor creado. Solo PersistenceManager sabe cuándo eso ocurrió.
//
//    ARSessionManager.WaitForStableTracking() detecta cuando el VIO alcanza
//    tracking estable, que es una condición PREVIA a la carga de sesión,
//    no posterior. Publicar ARSessionReadyEvent aquí era prematuro y podía
//    hacer que SceneReadyNotifier y Flutter creyeran que todo estaba listo
//    antes de que el modelo y los waypoints fueran restaurados.
//
//    ARSessionManager SIGUE publicando ShowMessageEvent para la UI local.
//    Solo se elimina la publicación de ARSessionReadyEvent.
//
//  TODOS LOS COMPORTAMIENTOS DE v3.2 SE CONSERVAN ÍNTEGRAMENTE.

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

        [Header("Configuración")]
        [SerializeField] private bool  _detectVerticalPlanes    = false;
        [SerializeField] private bool  _showPlaneVisualization  = true;
        [SerializeField] private float _minimumPlaneArea        = 0.5f;

        [Header("─── Estabilidad inicial (FIX 1) ─────────────────────────")]
        [Tooltip("Frames consecutivos de SessionTracking requeridos antes de " +
                 "considerar la sesión AR lista para posicionar el modelo. " +
                 "Evita desalineación por world origin shift de ARCore al arrancar. " +
                 "Default 30 ≈ 0.5s a 60fps.")]
        [SerializeField] private int   _initialStableFrames    = 30;

        [Tooltip("Segundos máximos esperando tracking estable al inicio.")]
        [SerializeField] private float _initialTrackingTimeout = 10f;

        [Header("─── Estabilidad en movimiento rápido (FIX 2) ─────────────")]
        [Tooltip("Delta de posición de cámara en un frame (m) que se considera " +
                 "movimiento brusco del USUARIO.\n\n" +
                 "⚠️ v3.1: Este umbral solo aplica cuando NO hay una supresión " +
                 "activa (SuppressQuickMoveDetection). Los saltos intencionales " +
                 "del XR Origin (AlignXROriginOnce) se suprimen automáticamente " +
                 "y no activan este detector.")]
        [SerializeField] private float _quickMoveThreshold      = 0.15f;

        [Tooltip("Frames de pausa en sincronización tras movimiento brusco del USUARIO. " +
                 "Default 20 ≈ 0.33s a 60fps.")]
        [SerializeField] private int   _quickMoveStabilityFrames = 20;

        [Header("─── Debug ───────────────────────────────────────────────")]
        [SerializeField] private bool _logTracking = true;

        // ─── Estado ───────────────────────────────────────────────────────

        private readonly Dictionary<TrackableId, ARPlane> _detectedPlanes
            = new Dictionary<TrackableId, ARPlane>();
        private readonly List<ARRaycastHit> _raycastHits = new List<ARRaycastHit>();

        // FIX 1 — tracking inicial
        private bool _initialTrackingAchieved  = false;
        private int  _consecutiveStableFrames  = 0;

        // FIX 2 — movimiento rápido del usuario
        private Vector3 _lastCameraPos     = Vector3.zero;
        private bool    _cameraInitialized = false;
        private int     _quickMovePauseFrames = 0;

        // v3.1 — Supresión de detección para saltos intencionales del XR Origin
        private int _suppressQuickMoveFrames = 0;

        // ✅ v3.2 — Estado de plane detection para restauración
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
            if (_planeManager != null)
                _planeManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
        }

        private void Update() => UpdateQuickMoveDetection();

        // ─── Inicialización ───────────────────────────────────────────────

        private void ValidateDependencies()
        {
            if (_planeManager    == null) _planeManager    = FindFirstObjectByType<ARPlaneManager>();
            if (_raycastManager  == null) _raycastManager  = FindFirstObjectByType<ARRaycastManager>();
            if (_anchorManager   == null) _anchorManager   = FindFirstObjectByType<ARAnchorManager>();

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

            Debug.Log("[ARSessionManager] ✅ v3.3 Dependencias validadas. " +
                      $"PlaneManager en '{_planeManager.gameObject.name}'");
        }

        private void InitializeARSession()
        {
            try
            {
                ConfigurePlaneDetection();
                IsSessionReady = true;
                StartCoroutine(WaitForStableTracking());

                Debug.Log("[ARSessionManager] ✅ v3.3 Sesión AR inicializada. " +
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

                        // ✅ v3.3: Solo publicar ShowMessageEvent para la UI local.
                        // ARSessionReadyEvent es responsabilidad exclusiva de PersistenceManager v14
                        // — se publica cuando modelo + waypoints + anchor están listos.
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
                             $"({_initialTrackingTimeout}s). " +
                             $"Estado: {ARSession.state}");
            _initialTrackingAchieved = true;
            NotifyTrackingStable();
        }

        /// <summary>
        /// ✅ v3.3 — Notifica que el tracking es estable (solo UI local).
        /// NO publica ARSessionReadyEvent — eso es responsabilidad de PersistenceManager.
        /// </summary>
        private void NotifyTrackingStable()
        {
            Core.Events.EventBus.Instance?.Publish(new Core.Events.ShowMessageEvent
            {
                Message  = "Tracking AR estable.",
                Type     = Core.Events.MessageType.Info,
                Duration = 3f
            });

            Log("✅ v3.3 Tracking estable notificado (sin ARSessionReadyEvent — " +
                "PersistenceManager lo publica cuando la sesión esté lista).");
        }

        // ─── FIX 2 — Detección de movimiento rápido ──────────────────────

        /// <summary>
        /// ✅ v3.1 — Suprime la detección de movimiento rápido por N frames.
        /// AROriginAligner debe llamar esto ANTES de MoveCameraToWorldLocation().
        /// </summary>
        public void SuppressQuickMoveDetection(int frames = 5)
        {
            _suppressQuickMoveFrames = frames;
            _cameraInitialized = false;
            Log($"⏸ QuickMove suprimido por {frames} frames " +
                "(salto intencional de XR Origin — AlignXROriginOnce).");
        }

        private void UpdateQuickMoveDetection()
        {
            if (!_initialTrackingAchieved) return;

            var xrOrigin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (xrOrigin?.Camera == null) return;

            Vector3 currentCameraPos = xrOrigin.Camera.transform.position;

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
                    Log("✅ Supresión de QuickMove terminada — detector activo nuevamente.");
                return;
            }

            if (_quickMovePauseFrames > 0)
            {
                _quickMovePauseFrames--;
                if (_quickMovePauseFrames == 0)
                    Log("✅ Pausa por movimiento rápido terminada — reanudando sincronización.");
                return;
            }

            if (cameraDelta > _quickMoveThreshold)
            {
                _quickMovePauseFrames = _quickMoveStabilityFrames;
                Log($"⚡ Movimiento rápido del USUARIO detectado " +
                    $"(Δ={cameraDelta:F3}m > umbral {_quickMoveThreshold:F3}m). " +
                    $"Pausando sincronización por {_quickMoveStabilityFrames} frames.");
            }
        }

        // ─── ✅ v3.2 FIX_CPU — Control de Plane Detection ─────────────────

        /// <summary>
        /// ✅ v3.2 — Deshabilita plane detection para liberar CPU al VIO.
        /// Llamado por ModelLoadManager tras anclar el modelo.
        /// </summary>
        public void DisablePlaneDetection()
        {
            if (_planeManager == null) return;
            if (_planeDetectionDisabled) return;

            _planeManager.requestedDetectionMode = PlaneDetectionMode.None;
            _planeDetectionDisabled = true;

            Log("✅ [FIX_CPU] Plane detection DESHABILITADA — CPU liberada para VIO. " +
                "VIO debería recuperar frecuencia ~30Hz.");
        }

        /// <summary>
        /// ✅ v3.2 — Re-habilita plane detection.
        /// Llamar si el usuario necesita reposicionar el modelo.
        /// </summary>
        public void EnablePlaneDetection()
        {
            if (_planeManager == null) return;

            PlaneDetectionMode mode = _detectVerticalPlanes
                ? PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical
                : PlaneDetectionMode.Horizontal;

            _planeManager.requestedDetectionMode = mode;
            _planeDetectionDisabled = false;

            Log($"✅ [FIX_CPU] Plane detection RE-HABILITADA: {mode}");
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

            if (!isFloor && !isUnclassifiedHorizontalUp)
            {
                if (plane.alignment == PlaneAlignment.HorizontalDown ||
                    plane.classifications.HasFlag(PlaneClassifications.Ceiling))
                    Log($"🚫 Plano techo ignorado: alignment={plane.alignment} " +
                        $"class={plane.classifications}");
                return;
            }

            if (plane.size.x * plane.size.y < _minimumPlaneArea) return;

            _detectedPlanes[plane.trackableId] = plane;
            ConfigurePlaneVisualization(plane);

            EventBus.Instance?.Publish(new PlaneDetectedEvent
            {
                Plane  = plane,
                Center = plane.center,
                Area   = plane.size.x * plane.size.y
            });

            string classLabel = isFloor ? "Floor✓" : "HorizUp(sin clasificar)";
            Log($"✅ Plano [{classLabel}]: {plane.trackableId} | " +
                $"Área: {plane.size.x * plane.size.y:F2}m²");
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
            {
                EventBus.Instance?.Publish(new PlaneRemovedEvent { Plane = plane });
                Log($"Plano removido: {plane.trackableId}");
            }
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

        public bool Raycast(
            Vector2      screenPosition,
            out ARRaycastHit hit,
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

        // ─── Anchors — AF 6.5 ─────────────────────────────────────────────

        public async Task<ARAnchor> CreateAnchorAsync(Pose pose)
        {
            if (_anchorManager == null)
            { Debug.LogWarning("[ARSessionManager] ARAnchorManager no disponible."); return null; }

            if (ARSession.state != ARSessionState.SessionTracking)
            {
                Debug.LogWarning($"[ARSessionManager] ⚠️ Anchor rechazado: " +
                                 $"ARCore no está en SessionTracking (estado: {ARSession.state}).");
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
                    Log($"⚓ Anchor creado: {anchor.trackableId} @ {pose.position:F3}");
                    return anchor;
                }
                LogAnchorFailure(result.status);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ARSessionManager] Error creando anchor: {ex.Message}");
                return null;
            }
        }

        public void CreateAnchorFireAndForget(Pose pose, Action<ARAnchor> onComplete = null)
            => _ = CreateAnchorAndCallback(pose, onComplete);

        private async Task CreateAnchorAndCallback(Pose pose, Action<ARAnchor> onComplete)
        {
            ARAnchor anchor = await CreateAnchorAsync(pose);
            onComplete?.Invoke(anchor);
        }

        private void LogAnchorFailure(XRResultStatus status)
        {
            string reason = $"status={status} | ARSession={ARSession.state} " +
                            $"| reason={ARSession.notTrackingReason}";
            Debug.LogWarning($"[ARSessionManager] ⚠️ TryAddAnchorAsync falló: {reason}");
        }

        public void RemoveAnchor(ARAnchor anchor)
        {
            if (anchor == null || _anchorManager == null) return;
            try
            {
                if (_anchorManager.TryRemoveAnchor(anchor))
                    Log($"Ancla removida: {anchor.trackableId}");
                else
                    Debug.LogWarning($"[ARSessionManager] No se pudo remover: {anchor.trackableId}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ARSessionManager] Error removiendo ancla: {ex.Message}");
            }
        }

        // ─── Utilities ────────────────────────────────────────────────────

        public void TogglePlaneVisualization(bool show)
        {
            _showPlaneVisualization = show;
            foreach (var kvp in _detectedPlanes)
                if (kvp.Value != null && kvp.Value.TryGetComponent<MeshRenderer>(out var r))
                    r.enabled = show;
            Log($"Visualización de planos: {show}");
        }

        public void ClearAllPlanes()
        {
            foreach (var kvp in _detectedPlanes)
                if (kvp.Value != null) Destroy(kvp.Value.gameObject);
            _detectedPlanes.Clear();
            Log("Todos los planos limpiados.");
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

        // ─── Helpers ─────────────────────────────────────────────────────

        private void Log(string msg)
        {
            if (_logTracking) Debug.Log($"[ARSessionManager] {msg}");
        }

        // ─── Debug ────────────────────────────────────────────────────────

        [ContextMenu("ℹ️ Estado actual")]
        private void DebugState()
        {
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log("  ARSessionManager v3.3 — Estado");
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log($"  ARSession state:           {ARSession.state}");
            Debug.Log($"  IsSessionReady:            {IsSessionReady}");
            Debug.Log($"  IsFullyStable:             {IsFullyStable}");
            Debug.Log($"  InitialTracking logrado:   {_initialTrackingAchieved}");
            Debug.Log($"  Frames estables actual:    {_consecutiveStableFrames}/{_initialStableFrames}");
            Debug.Log($"  IsQuickMovePaused:         {IsQuickMovePaused} ({_quickMovePauseFrames} frames)");
            Debug.Log($"  SuppressFrames restantes:  {_suppressQuickMoveFrames}");
            Debug.Log($"  PlaneDetectionDisabled:    {_planeDetectionDisabled}");
            Debug.Log($"  Planos detectados:         {DetectedPlaneCount}");
            Debug.Log($"  PlaneManager:              {(_planeManager    != null ? _planeManager.gameObject.name    : "NULL")}");
            Debug.Log($"  RaycastManager:            {(_raycastManager  != null ? _raycastManager.gameObject.name  : "NULL")}");
            Debug.Log($"  AnchorManager:             {(_anchorManager   != null ? _anchorManager.gameObject.name   : "NULL")}");
            Debug.Log($"  [v3.3] ARSessionReadyEvent: publicado SOLO por PersistenceManager");
            Debug.Log("══════════════════════════════════════════════");
        }

        [ContextMenu("🚫 Deshabilitar Plane Detection ahora")]
        private void DebugDisablePlanes() => DisablePlaneDetection();

        [ContextMenu("✅ Re-habilitar Plane Detection ahora")]
        private void DebugEnablePlanes() => EnablePlaneDetection();

        [ContextMenu("🧪 Test SuppressQuickMove (5 frames)")]
        private void DebugSuppressQuickMove() => SuppressQuickMoveDetection(5);
    }
}