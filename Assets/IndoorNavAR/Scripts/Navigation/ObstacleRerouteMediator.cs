// File: ObstacleRerouteMediator.cs
// Assets/IndoorNavAR/Scripts/Navigation/
// ✅ v1.1 — Fixes aplicados:
//   - FIX #3: Usa RerouteToWaypoint() para evitar doble TTS al reroutear
//   - FIX #4: Publica IsActive para que SegmentationController suprima alertas duplicadas
//   - FIX #6: NullReference en log de PlaceObstacleAndReroute corregido
//   - FIX #8: Fallback usa GetWaypoint(id) en lugar de SearchWaypointsByName
//   - EDITOR: CameraTransform se resuelve desde Camera.main si no está asignado

using UnityEngine;
using IndoorNavAR.Core;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Segmentation;

namespace IndoorNavAR.Navigation
{
    public class ObstacleRerouteMediator : MonoBehaviour
    {
        [Header("Referencias")]
        [SerializeField] private SegmentationController _segController;
        [SerializeField] private NavigationManager      _navManager;
        [SerializeField] private NavMeshObstacleAgent   _obstacleAgent;

        // La cámara AR — se resuelve automáticamente.
        // En Editor usa Camera.main. En dispositivo usa ARCameraManager.
        [SerializeField] private Transform _cameraTransform;

        [Header("Detección")]
        [Tooltip("ObstacleRatio mínimo para considerar que hay un obstáculo real.")]
        [SerializeField, Range(0.05f, 0.5f)]
        private float _obstacleThreshold = 0.12f;

        [Tooltip("Frames consecutivos confirmados antes de actuar.")]
        [SerializeField, Range(1, 8)]
        private int _confirmationFrames = 3;

        [Header("Recálculo")]
        [Tooltip("Segundos de espera después de un recálculo antes de poder disparar otro.")]
        [SerializeField] private float _rerouteCooldown = 12f;

        [Tooltip("Distancia mínima al destino para intentar recalcular.")]
        [SerializeField] private float _minDistanceToReroute = 2.0f;

        [Tooltip("Offset adelante de la cámara donde se coloca el obstáculo virtual (m).")]
        [SerializeField] private float _obstacleForwardOffset = 1.5f;

        [Header("Debug")]
        [SerializeField] private bool _logEvents = true;

        // ── Estado ────────────────────────────────────────────────────────
        private int   _consecutiveObstacleFrames = 0;
        private float _lastRerouteTime           = -999f;
        private bool  _isActive                  = false;

        // ✅ FIX #4: Propiedad estática para que SegmentationController
        //   suprima sus alertas cuando este mediador está manejando el obstáculo.
        public static bool IsActive { get; private set; } = false;

        private WaypointData _currentDestination;
        private string  _fallbackDestId       = string.Empty; // ✅ FIX #8: es un ID, no un nombre
        private Vector3 _fallbackDestPosition = Vector3.zero;

        private Vector3 DestinationPosition => _currentDestination != null
            ? _currentDestination.Position
            : _fallbackDestPosition;

        private bool HasDestination => _currentDestination != null
            || !string.IsNullOrEmpty(_fallbackDestId);
        // ─────────────────────────────────────────────────────────────────

        private void Start()
        {
            if (_segController == null)
                _segController = FindFirstObjectByType<SegmentationController>(
                    FindObjectsInactive.Include);

            if (_navManager == null)
                _navManager = FindFirstObjectByType<NavigationManager>(
                    FindObjectsInactive.Include);

            if (_obstacleAgent == null)
                _obstacleAgent = FindFirstObjectByType<NavMeshObstacleAgent>(
                    FindObjectsInactive.Include);

            // ✅ EDITOR FIX: resolución de cámara con fallback a Camera.main
            //   En dispositivo: busca ARCameraManager
            //   En Editor (NoAR/Simulator): usa Camera.main
            if (_cameraTransform == null)
            {
                var camManager = FindFirstObjectByType<UnityEngine.XR.ARFoundation.ARCameraManager>(
                    FindObjectsInactive.Include);

                if (camManager != null)
                    _cameraTransform = camManager.transform;
                else if (Camera.main != null)
                    _cameraTransform = Camera.main.transform;
                else
                    Debug.LogWarning("[ObstacleMediator] ⚠️ No se encontró cámara. " +
                                     "Asignar Camera Transform en el Inspector.");
            }

            EventBus.Instance?.Subscribe<NavigationStartedEvent>(OnNavigationStarted);
            EventBus.Instance?.Subscribe<NavigationCompletedEvent>(OnNavigationCompleted);
            EventBus.Instance?.Subscribe<NavigationCancelledEvent>(OnNavigationCancelled);

            ValidateSetup();
        }

        private void OnDestroy()
        {
            EventBus.Instance?.Unsubscribe<NavigationStartedEvent>(OnNavigationStarted);
            EventBus.Instance?.Unsubscribe<NavigationCompletedEvent>(OnNavigationCompleted);
            EventBus.Instance?.Unsubscribe<NavigationCancelledEvent>(OnNavigationCancelled);

            // Limpiar estado estático al destruir
            IsActive = false;
        }

        private void ValidateSetup()
        {
            if (_segController == null)
                Debug.LogError("[ObstacleMediator] ❌ SegmentationController no encontrado.");
            if (_navManager == null)
                Debug.LogError("[ObstacleMediator] ❌ NavigationManager no encontrado.");
            if (_obstacleAgent == null)
                Debug.LogError("[ObstacleMediator] ❌ NavMeshObstacleAgent no encontrado.");
            if (_cameraTransform == null)
                Debug.LogWarning("[ObstacleMediator] ⚠️ Camera Transform no resuelto. " +
                                 "En Editor: asigna la Main Camera manualmente en el Inspector.");

            Debug.Log("[ObstacleMediator] ✅ v1.1 inicializado. " +
                      $"Camera: {(_cameraTransform != null ? _cameraTransform.name : "NULL")}");
        }

        // ── Update ────────────────────────────────────────────────────────

        private void Update()
        {
            if (!_isActive || _segController == null) return;

            // ✅ Resolver cámara en runtime si quedó null (race condition al arrancar)
            if (_cameraTransform == null)
            {
                if (Camera.main != null) _cameraTransform = Camera.main.transform;
                else return;
            }

            float ratio = ObstacleSegmentationWorker.Instance?.ObstacleRatio ?? 0f;

            if (ratio >= _obstacleThreshold)
            {
                _consecutiveObstacleFrames++;
                if (_consecutiveObstacleFrames >= _confirmationFrames)
                    TryTriggerReroute();
            }
            else
            {
                _consecutiveObstacleFrames = 0;
            }
        }

        // ── Lógica principal ──────────────────────────────────────────────

        private void TryTriggerReroute()
        {
            if (Time.unscaledTime - _lastRerouteTime < _rerouteCooldown) return;

            if (!HasDestination)
            {
                if (_logEvents) Debug.LogWarning("[ObstacleMediator] Sin destino — ignorando.");
                return;
            }

            if (_cameraTransform != null)
            {
                float distToDest = Vector3.Distance(_cameraTransform.position, DestinationPosition);
                if (distToDest < _minDistanceToReroute)
                {
                    if (_logEvents)
                        Debug.Log($"[ObstacleMediator] Muy cerca del destino ({distToDest:F1}m) — ignorando.");
                    return;
                }
            }

            if (_cameraTransform == null ||
                !NavMeshObstacleAgent.TryGetPlacementPosition(
                    _cameraTransform, _obstacleForwardOffset, out Vector3 obstaclePos))
            {
                Debug.LogWarning("[ObstacleMediator] No se pudo proyectar obstáculo al NavMesh.");
                return;
            }

            PlaceObstacleAndReroute(obstaclePos);
        }

        private void PlaceObstacleAndReroute(Vector3 obstaclePos)
        {
            _lastRerouteTime           = Time.unscaledTime;
            _consecutiveObstacleFrames = 0;

            // 1. Colocar el obstáculo virtual
            _obstacleAgent?.PlaceAt(obstaclePos);

            // 2. Delay para que Unity procese el carving del NavMesh
            StartCoroutine(RerouteAfterNavMeshUpdate());

            // 3. Publicar evento para TTS (NavigationVoiceGuide lo escucha con p=3)
            EventBus.Instance?.Publish(new ObstacleDetectedEvent
            {
                ObstaclePosition = obstaclePos,
                DetectedRatio    = ObstacleSegmentationWorker.Instance?.ObstacleRatio ?? 0f,
            });

            // ✅ FIX #6: null-check antes de acceder a WaypointName
            if (_logEvents)
            {
                string destLabel = _currentDestination?.WaypointName ?? _fallbackDestId;
                Debug.Log($"[ObstacleMediator] 🚧 Obstáculo en {obstaclePos:F2}. " +
                          $"Recalculando ruta a '{destLabel}'...");
            }
        }

        private System.Collections.IEnumerator RerouteAfterNavMeshUpdate()
        {
            // 3 frames para que NavMeshObstacle termine el carving
            yield return null;
            yield return null;
            yield return null;

            if (_navManager == null) yield break;

            bool ok = false;

            if (_currentDestination != null)
            {
                // ✅ FIX #3: Usa RerouteToWaypoint() — NO llama TriggerFromWaypoint()
                //   evitando el doble TTS de "Listo, vamos a X."
                //   VoiceGuide se actualiza via OnPathRecalculated → Resync().
                ok = _navManager.RerouteToWaypoint(_currentDestination);
            }
            else if (!string.IsNullOrEmpty(_fallbackDestId))
            {
                // ✅ FIX #8: Buscar por ID con GetWaypoint(), no por nombre con SearchWaypointsByName()
                //   _fallbackDestId contiene el WaypointId (GUID), no el nombre display.
                var wm = FindFirstObjectByType<IndoorNavAR.Core.Managers.WaypointManager>(
                    FindObjectsInactive.Include);

                var wp = wm?.GetWaypoint(_fallbackDestId);

                if (wp != null)
                {
                    ok = _navManager.RerouteToWaypoint(wp);
                    _currentDestination = wp; // cachear para próximos recálculos
                }
                else
                {
                    Debug.LogWarning($"[ObstacleMediator] Fallback: no se encontró waypoint con ID '{_fallbackDestId}'.");
                }
            }

            if (_logEvents)
                Debug.Log($"[ObstacleMediator] 🔄 Recálculo: {(ok ? "✅ OK" : "❌ falló")}");
        }

        // ── Eventos del bus ───────────────────────────────────────────────

        private void OnNavigationStarted(NavigationStartedEvent evt)
        {
            _isActive = true;
            IsActive  = true; // ✅ FIX #4
            _consecutiveObstacleFrames = 0;

            if (_currentDestination == null ||
                _currentDestination.WaypointId != evt.DestinationWaypointId)
            {
                var waypointManager = FindFirstObjectByType<IndoorNavAR.Core.Managers.WaypointManager>(
                    FindObjectsInactive.Include);

                WaypointData fromManager = waypointManager?.GetWaypoint(evt.DestinationWaypointId);

                if (fromManager != null)
                {
                    _currentDestination = fromManager;
                }
                else
                {
                    // ✅ FIX #8: guardar como ID, no como nombre
                    _currentDestination    = null;
                    _fallbackDestId        = evt.DestinationWaypointId;
                    _fallbackDestPosition  = evt.DestinationPosition;
                }
            }

            if (_logEvents)
                Debug.Log($"[ObstacleMediator] ▶️ Activo → '{evt.DestinationWaypointId}' " +
                          $"Destino resuelto: {(_currentDestination != null ? "✅" : "❌ (usando fallback ID)")}");
        }

        private void OnNavigationCompleted(NavigationCompletedEvent evt)
        {
            _isActive  = false;
            IsActive   = false; // ✅ FIX #4
            _currentDestination   = null;
            _fallbackDestId       = string.Empty;
            _fallbackDestPosition = Vector3.zero;
            _obstacleAgent?.Remove();

            if (_logEvents) Debug.Log("[ObstacleMediator] ✅ Navegación completada — mediator inactivo.");
        }

        private void OnNavigationCancelled(NavigationCancelledEvent evt)
        {
            _isActive  = false;
            IsActive   = false; // ✅ FIX #4
            _currentDestination   = null;
            _fallbackDestId       = string.Empty;
            _fallbackDestPosition = Vector3.zero;
            _obstacleAgent?.Remove();

            if (_logEvents) Debug.Log("[ObstacleMediator] 🛑 Navegación cancelada — mediator inactivo.");
        }

        // ── API pública ───────────────────────────────────────────────────

        public void SetCurrentDestination(WaypointData destination)
        {
            _currentDestination = destination;
        }

        /// <summary>
        /// Llamable desde FlutterUnityBridge (action="reroute_obstacle")
        /// o desde el ContextMenu en Editor para pruebas.
        /// </summary>
        public void SimulateObstacleFromFlutter()
        {
            if (!_isActive)
            {
                Debug.LogWarning("[ObstacleMediator] SimulateObstacleFromFlutter: sin navegación activa.");
                return;
            }

            // ✅ EDITOR: Si no hay cámara asignada, intentar resolverla en este momento
            if (_cameraTransform == null && Camera.main != null)
                _cameraTransform = Camera.main.transform;

            if (_cameraTransform != null &&
                NavMeshObstacleAgent.TryGetPlacementPosition(
                    _cameraTransform, _obstacleForwardOffset, out Vector3 pos))
            {
                PlaceObstacleAndReroute(pos);
            }
            else
            {
                Debug.LogWarning("[ObstacleMediator] SimulateObstacleFromFlutter: " +
                                 "sin posición válida. Camera=" +
                                 (_cameraTransform != null ? _cameraTransform.name : "NULL"));
            }
        }

        [ContextMenu("🧪 Simular obstáculo")]
        private void DebugSimulateObstacle() => SimulateObstacleFromFlutter();

        /// <summary>
        /// ContextMenu extra para forzar activación en Editor sin navegación real.
        /// Útil para probar el flujo completo desde el Inspector.
        /// </summary>
        [ContextMenu("🧪 [Editor] Activar mediator y simular")]
        private void DebugForceActivateAndSimulate()
        {
            if (!_isActive)
            {
                Debug.Log("[ObstacleMediator] [Editor] Activando mediator temporalmente para prueba...");
                _isActive = true;
                IsActive  = true;

                // Destino de prueba: 5m al frente de la cámara
                if (_cameraTransform != null)
                    _fallbackDestPosition = _cameraTransform.position + _cameraTransform.forward * 5f;

                _fallbackDestId = "editor_test";
            }
            SimulateObstacleFromFlutter();
        }
    }
}