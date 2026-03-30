// File: ARSessionManager.cs
// ✅ v3.0 — AR Foundation 6.5: TryAddAnchorAsync + XRResultStatus + estabilidad inicial.
//
// ============================================================================
//  CAMBIOS v2.1 → v3.0
// ============================================================================
//
//  MIGRACIÓN AF 6.5:
//
//  CAMBIO 1 — CreateAnchor() → CreateAnchorAsync():
//    AttachAnchor(plane, pose) fue eliminado en AF 6.x.
//    Reemplazado por TryAddAnchorAsync(pose) que retorna
//    Result<ARAnchor> con XRResultStatus para diagnóstico preciso.
//
//  CAMBIO 2 — Nuevos códigos de error XRResultStatus (AF 6.5):
//    XRResultStatus.StatusCode.NotFound     → plano no disponible
//    XRResultStatus.StatusCode.NotTracking  → ARCore sin tracking estable
//    Usados para logging de diagnóstico en CreateAnchorAsync().
//
//  FIXES DE ESTABILIDAD:
//
//  FIX 1 — Desestabilización al inicio de sesión:
//    CAUSA: El modelo se posicionaba antes de que ARCore tuviera tracking
//    estable (SessionTracking). ARCore terminaba de inicializarse, movía
//    el world origin, y el modelo quedaba desalineado.
//    FIX: WaitForStableTracking() — espera SessionTracking + _initialStableFrames
//    frames consecutivos antes de publicar ARSessionReadyEvent. La alineación
//    del XR Origin (AROriginAligner) solo ocurre después de este evento.
//
//  FIX 2 — Desestabilización al mover el teléfono rápido (VIO drift):
//    CAUSA: ARCore hace micro-resets del world origin durante movimiento
//    rápido. El modelo se desplaza porque el anchor no se recaptura con
//    suficiente frecuencia.
//    FIX: _quickMoveStabilityFrames — tras detección de movimiento brusco
//    (delta de cámara > _quickMoveThreshold en un frame), se pausa la
//    sincronización del agente por _quickMoveStabilityFrames frames para
//    dejar que ARCore se re-estabilice antes de actualizar posiciones.
//
//  FIX 3 — Guard de tracking en CreateAnchorAsync():
//    Si ARCore no está en SessionTracking, rechazar inmediatamente la
//    creación de anchors con StatusCode.NotTracking. Evita crear anchors
//    con datos de pose inválidos durante tracking inestable.

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
        [SerializeField] private ARPlaneManager _planeManager;
        [SerializeField] private ARRaycastManager _raycastManager;
        [SerializeField] private ARAnchorManager _anchorManager;

        [Header("Configuración")]
        [SerializeField] private bool _detectVerticalPlanes = false;
        [SerializeField] private bool _showPlaneVisualization = true;
        [SerializeField] private float _minimumPlaneArea = 0.5f;

        [Header("─── Estabilidad inicial (FIX 1) ─────────────────────────")]
        [Tooltip("Frames consecutivos de SessionTracking requeridos antes de " +
                 "considerar la sesión AR lista para posicionar el modelo. " +
                 "Evita desalineación por world origin shift de ARCore al arrancar. " +
                 "Default 30 ≈ 0.5s a 60fps.")]
        [SerializeField] private int _initialStableFrames = 30;

        [Tooltip("Segundos máximos esperando tracking estable al inicio. " +
                 "Si se supera, se publica el evento igualmente para no bloquear.")]
        [SerializeField] private float _initialTrackingTimeout = 10f;

        [Header("─── Estabilidad en movimiento rápido (FIX 2) ─────────────")]
        [Tooltip("Delta de posición de cámara en un frame (m) que se considera " +
                 "movimiento brusco. ARCore puede hacer micro-resets del world " +
                 "origin en estos casos.")]
        [SerializeField] private float _quickMoveThreshold = 0.15f;

        [Tooltip("Frames de pausa en sincronización tras movimiento brusco. " +
                 "Da tiempo a ARCore para re-estabilizar el world origin. " +
                 "Default 20 ≈ 0.33s a 60fps.")]
        [SerializeField] private int _quickMoveStabilityFrames = 20;

        [Header("─── Debug ───────────────────────────────────────────────")]
        [SerializeField] private bool _logTracking = true;

        // ─── Estado ───────────────────────────────────────────────────────

        private readonly Dictionary<TrackableId, ARPlane> _detectedPlanes
            = new Dictionary<TrackableId, ARPlane>();
        private readonly List<ARRaycastHit> _raycastHits = new List<ARRaycastHit>();

        // FIX 1 — tracking inicial
        private bool _initialTrackingAchieved = false;
        private int _consecutiveStableFrames = 0;

        // FIX 2 — movimiento rápido
        private Vector3 _lastCameraPos = Vector3.zero;
        private bool _cameraInitialized = false;
        private int _quickMovePauseFrames = 0;

        // ─── Propiedades públicas ─────────────────────────────────────────

        public bool IsSessionReady { get; private set; }
        public int DetectedPlaneCount => _detectedPlanes.Count;
        public IReadOnlyDictionary<TrackableId, ARPlane> DetectedPlanes => _detectedPlanes;

        /// <summary>
        /// True si ARCore está en tracking estable Y pasó el período de
        /// estabilización inicial. Usar esto antes de posicionar el modelo.
        /// </summary>
        public bool IsFullyStable => _initialTrackingAchieved &&
                                     ARSession.state == ARSessionState.SessionTracking;

        /// <summary>
        /// True si hay pausa activa por movimiento rápido de cámara.
        /// AROriginAligner puede consultar esto para diferir la sincronización.
        /// </summary>
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

        private void Update()
        {
            UpdateQuickMoveDetection();
        }

        // ─── Inicialización ───────────────────────────────────────────────

        private void ValidateDependencies()
        {
            if (_planeManager == null) _planeManager = FindFirstObjectByType<ARPlaneManager>();
            if (_raycastManager == null) _raycastManager = FindFirstObjectByType<ARRaycastManager>();
            if (_anchorManager == null) _anchorManager = FindFirstObjectByType<ARAnchorManager>();

            if (_planeManager == null)
            {
                Debug.LogError("[ARSessionManager] ARPlaneManager no encontrado. " +
                               "Asegúrate de que XR Origin (Mobile AR) tiene AR Plane Manager.");
                enabled = false; return;
            }
            if (_raycastManager == null)
            {
                Debug.LogError("[ARSessionManager] ARRaycastManager no encontrado.");
                enabled = false; return;
            }

            Debug.Log("[ARSessionManager] ✅ v3.0 Dependencias validadas. " +
                      $"PlaneManager en '{_planeManager.gameObject.name}'");
        }

        private void InitializeARSession()
        {
            try
            {
                ConfigurePlaneDetection();
                IsSessionReady = true;

                // ✅ FIX 1: No publicar ARSessionReadyEvent inmediatamente.
                // Esperar tracking estable antes de dejar que AROriginAligner
                // posicione el modelo — evita desalineación por world origin shift.
                StartCoroutine(WaitForStableTracking());

                Debug.Log("[ARSessionManager] ✅ Sesión AR inicializada. " +
                          $"Esperando {_initialStableFrames} frames estables...");
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
            Debug.Log($"[ARSessionManager] Detección de planos: {mode}");
        }

        // ─── FIX 1 — Esperar tracking estable al inicio ───────────────────

        /// <summary>
        /// ✅ FIX 1: Espera SessionTracking + _initialStableFrames consecutivos
        /// antes de notificar que la sesión AR está lista para posicionar el modelo.
        ///
        /// ARCore necesita varios frames tras SessionTracking para estabilizar
        /// el world origin. Si posicionamos el modelo en el primer frame de
        /// SessionTracking, ARCore puede mover el origin inmediatamente después
        /// y desalinear el modelo.
        ///
        /// Con _initialTrackingTimeout como fallback para evitar bloqueo total
        /// en dispositivos lentos o con poco feature tracking.
        /// </summary>
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
                            $"en {elapsed:F1}s). Publicando ARSessionReadyEvent.");
                        PublishSessionReady();
                        yield break;
                    }
                }
                else
                {
                    // Resetear contador si el tracking se interrumpe
                    if (_consecutiveStableFrames > 0)
                    {
                        Log($"⚠️ Tracking interrumpido en frame {_consecutiveStableFrames} " +
                            $"— reseteando contador. Estado: {ARSession.state}");
                        _consecutiveStableFrames = 0;
                    }
                }
            }

            // Timeout — publicar igualmente para no bloquear la app
            Debug.LogWarning($"[ARSessionManager] ⚠️ Timeout esperando tracking estable " +
                             $"({_initialTrackingTimeout}s). Publicando ARSessionReadyEvent " +
                             $"igualmente. Estado actual: {ARSession.state}");
            _initialTrackingAchieved = true;
            PublishSessionReady();
        }

        private void PublishSessionReady()
        {
            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message = "Sesión AR lista. Busca superficies horizontales.",
                Type = MessageType.Info,
                Duration = 3f
            });

            // Publicar evento de sesión lista para que AROriginAligner
            // y otros sistemas puedan empezar a operar
            EventBus.Instance?.Publish(new ARSessionReadyEvent { });
        }

        // ─── FIX 2 — Detección de movimiento rápido ──────────────────────

        /// <summary>
        /// ✅ FIX 2: Detecta movimiento brusco de cámara cada frame.
        ///
        /// Cuando el usuario mueve el teléfono rápidamente, ARCore puede
        /// hacer micro-resets del world origin, causando que el modelo
        /// "salte" o "flote". Al detectar este movimiento, activamos una
        /// pausa de _quickMoveStabilityFrames frames durante la cual
        /// AROriginAligner no debería sincronizar el agente.
        ///
        /// IsQuickMovePaused es una propiedad pública que AROriginAligner
        /// puede consultar en SyncAgentToCameraFullAR().
        /// </summary>
        private void UpdateQuickMoveDetection()
        {
            if (!_initialTrackingAchieved) return;

            // Obtener posición actual de la cámara AR
            var xrOrigin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (xrOrigin?.Camera == null) return;

            Vector3 currentCameraPos = xrOrigin.Camera.transform.position;

            if (!_cameraInitialized)
            {
                _lastCameraPos = currentCameraPos;
                _cameraInitialized = true;
                return;
            }

            // Calcular delta de movimiento en este frame
            float cameraDelta = Vector3.Distance(currentCameraPos, _lastCameraPos);
            _lastCameraPos = currentCameraPos;

            if (_quickMovePauseFrames > 0)
            {
                _quickMovePauseFrames--;

                if (_quickMovePauseFrames == 0)
                    Log("✅ Pausa por movimiento rápido terminada — reanudando sincronización.");

                return;
            }

            // Detectar movimiento brusco
            if (cameraDelta > _quickMoveThreshold)
            {
                _quickMovePauseFrames = _quickMoveStabilityFrames;
                Log($"⚡ Movimiento rápido detectado (Δ={cameraDelta:F3}m > umbral {_quickMoveThreshold:F3}m). " +
                    $"Pausando sincronización por {_quickMoveStabilityFrames} frames.");
            }
        }

        // ─── Plane tracking ───────────────────────────────────────────────

        private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            foreach (var plane in args.added) ProcessAddedPlane(plane);
            foreach (var plane in args.updated) ProcessUpdatedPlane(plane);
            foreach (var kvp in args.removed) ProcessRemovedPlane(kvp.Value);
        }

        /// <summary>
        /// ✅ v2.0 FIX 1 (conservado): Filtrar planos usando PlaneClassifications.Floor.
        ///
        /// Caso A — ARCore clasificó el plano como Floor: aceptar siempre.
        /// Caso B — Sin clasificación aún y HorizontalUp: aceptar provisionalmente.
        /// Caso C — HorizontalDown (techos) / Ceiling: rechazar.
        /// Caso D — Planos verticales: rechazar.
        /// </summary>
        private void ProcessAddedPlane(ARPlane plane)
        {
            bool isFloor = plane.classifications.HasFlag(PlaneClassifications.Floor);
            bool isUnclassifiedHorizontalUp =
                plane.classifications == PlaneClassifications.None &&
                plane.alignment == PlaneAlignment.HorizontalUp;

            if (!isFloor && !isUnclassifiedHorizontalUp)
            {
                if (plane.alignment == PlaneAlignment.HorizontalDown ||
                    plane.classifications.HasFlag(PlaneClassifications.Ceiling))
                {
                    Log($"🚫 Plano techo ignorado: " +
                        $"alignment={plane.alignment} class={plane.classifications}");
                }
                return;
            }

            if (plane.size.x * plane.size.y < _minimumPlaneArea)
                return;

            _detectedPlanes[plane.trackableId] = plane;
            ConfigurePlaneVisualization(plane);

            EventBus.Instance?.Publish(new PlaneDetectedEvent
            {
                Plane = plane,
                Center = plane.center,
                Area = plane.size.x * plane.size.y
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
                Plane = plane,
                NewCenter = plane.center,
                NewArea = plane.size.x * plane.size.y
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
            Vector2 screenPosition,
            out ARRaycastHit hit,
            TrackableType trackableTypes = TrackableType.PlaneWithinPolygon)
        {
            hit = default;
            if (_raycastManager == null) return false;

            _raycastHits.Clear();
            if (_raycastManager.Raycast(screenPosition, _raycastHits, trackableTypes))
            {
                hit = _raycastHits[0];
                return true;
            }
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
                {
                    if (h.distance <= maxDistance) { hit = h; return true; }
                }
            }
            return false;
        }

        // ─── Anchors — AF 6.5 ─────────────────────────────────────────────

        /// <summary>
        /// ✅ AF 6.5: Crea un anchor usando TryAddAnchorAsync().
        ///
        /// MIGRACIÓN desde v2.1:
        ///   AttachAnchor(plane, pose) → TryAddAnchorAsync(pose)
        ///
        /// La nueva API es async y retorna Result con XRResultStatus,
        /// lo que permite diagnóstico preciso del fallo.
        ///
        /// FIX 3: Guard de tracking — rechaza inmediatamente si ARCore
        /// no está en SessionTracking. Evita anchors con pose inválida.
        ///
        /// NOTA: Este método es async. Los callers deben usar await.
        /// Si necesitas un wrapper síncrono, usa CreateAnchorFireAndForget().
        /// </summary>
        public async Task<ARAnchor> CreateAnchorAsync(Pose pose)
        {
            if (_anchorManager == null)
            {
                Debug.LogWarning("[ARSessionManager] ARAnchorManager no disponible.");
                return null;
            }

            // ✅ FIX 3: Guard de tracking — no crear anchors sin tracking estable
            if (ARSession.state != ARSessionState.SessionTracking)
            {
                Debug.LogWarning($"[ARSessionManager] ⚠️ Anchor rechazado: " +
                                 $"ARCore no está en SessionTracking " +
                                 $"(estado actual: {ARSession.state}). " +
                                 $"Equivale a XRResultStatus.NotTracking.");
                return null;
            }

            try
            {
                // ✅ AF 6.5: TryAddAnchorAsync reemplaza AttachAnchor
                var result = await _anchorManager.TryAddAnchorAsync(pose);

                if (result.status.IsSuccess())
                {
                    ARAnchor anchor = result.value;

                    if (anchor == null || !anchor.enabled)
                    {
                        Debug.LogWarning("[ARSessionManager] ⚠️ Anchor creado pero nulo/desactivado.");
                        return null;
                    }

                    Log($"⚓ Anchor creado: {anchor.trackableId} @ {pose.position:F3}");
                    return anchor;
                }

                // ✅ AF 6.5: Diagnóstico preciso con nuevos StatusCodes
                LogAnchorFailure(result.status);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ARSessionManager] Error creando anchor: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Wrapper síncrono (fire-and-forget) para callers que no pueden usar await.
        /// Usa CreateAnchorAsync() cuando sea posible — este método no retorna el anchor.
        /// </summary>
        public void CreateAnchorFireAndForget(Pose pose, Action<ARAnchor> onComplete = null)
        {
            _ = CreateAnchorAndCallback(pose, onComplete);
        }

        private async Task CreateAnchorAndCallback(Pose pose, Action<ARAnchor> onComplete)
        {
            ARAnchor anchor = await CreateAnchorAsync(pose);
            onComplete?.Invoke(anchor);
        }

        /// <summary>
        /// ✅ AF 6.5: Log de diagnóstico con XRResultStatus.
        /// Usa IsError() y toString para diagnóstico seguro cross-version.
        /// </summary>
        private void LogAnchorFailure(XRResultStatus status)
        {
            string reason = $"status={status} | ARSession={ARSession.state} | reason={ARSession.notTrackingReason}";
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

        // ─── Búsqueda de plano más cercano ────────────────────────────────

        /// <summary>
        /// Busca el plano suelo más cercano.
        /// _detectedPlanes solo contiene suelos gracias a ProcessAddedPlane,
        /// pero aplicamos el mismo filtro como defensa en profundidad.
        /// </summary>
        private ARPlane FindClosestPlane(Vector3 position)
        {
            ARPlane closestFloor = null;
            ARPlane closestFallback = null;
            float minDistFloor = float.MaxValue;
            float minDistFallback = float.MaxValue;

            foreach (var kvp in _detectedPlanes)
            {
                var plane = kvp.Value;
                if (plane == null) continue;

                float dist = Vector3.Distance(position, plane.center);

                if (plane.classifications.HasFlag(PlaneClassifications.Floor))
                {
                    if (dist < minDistFloor) { minDistFloor = dist; closestFloor = plane; }
                }
                else if (plane.alignment == PlaneAlignment.HorizontalUp)
                {
                    if (dist < minDistFallback) { minDistFallback = dist; closestFallback = plane; }
                }
            }

            return closestFloor ?? closestFallback;
        }

        // ─── Utilities ────────────────────────────────────────────────────

        public void TogglePlaneVisualization(bool show)
        {
            _showPlaneVisualization = show;
            foreach (var kvp in _detectedPlanes)
            {
                if (kvp.Value != null && kvp.Value.TryGetComponent<MeshRenderer>(out var r))
                    r.enabled = show;
            }
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
            float maxArea = 0f;

            foreach (var kvp in _detectedPlanes)
            {
                var plane = kvp.Value;
                if (plane == null) continue;
                float area = plane.size.x * plane.size.y;
                if (area > maxArea) { maxArea = area; largestPlane = plane; }
            }
            return largestPlane;
        }

        // ─── Debug ────────────────────────────────────────────────────────

        private void Log(string msg)
        {
            if (_logTracking) Debug.Log($"[ARSessionManager] {msg}");
        }

        [ContextMenu("ℹ️ Estado actual")]
        private void DebugState()
        {
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log("  ARSessionManager v3.0 — Estado");
            Debug.Log("══════════════════════════════════════════════");
            Debug.Log($"  ARSession state:        {ARSession.state}");
            Debug.Log($"  IsSessionReady:         {IsSessionReady}");
            Debug.Log($"  IsFullyStable:          {IsFullyStable}");
            Debug.Log($"  InitialTracking logrado:{_initialTrackingAchieved}");
            Debug.Log($"  Frames estables actual: {_consecutiveStableFrames}/{_initialStableFrames}");
            Debug.Log($"  IsQuickMovePaused:      {IsQuickMovePaused} ({_quickMovePauseFrames} frames restantes)");
            Debug.Log($"  Planos detectados:      {DetectedPlaneCount}");
            Debug.Log($"  PlaneManager:           {(_planeManager != null ? _planeManager.gameObject.name : "NULL")}");
            Debug.Log($"  RaycastManager:         {(_raycastManager != null ? _raycastManager.gameObject.name : "NULL")}");
            Debug.Log($"  AnchorManager:          {(_anchorManager != null ? _anchorManager.gameObject.name : "NULL")}");
            Debug.Log("══════════════════════════════════════════════");
        }

        [ContextMenu("🧪 Test CreateAnchorAsync (pose cámara)")]
        private void DebugTestAnchor()
        {
            var xrOrigin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (xrOrigin?.Camera == null) { Log("Sin cámara para test"); return; }
            Pose pose = new Pose(xrOrigin.Camera.transform.position,
                                 xrOrigin.Camera.transform.rotation);
            _ = CreateAnchorAsync(pose);
        }
    }
}