// File: ObstacleRerouteMediator.cs
// Assets/IndoorNavAR/Scripts/Navigation/
// ✅ v1.2 — FIX #OBSTACLE-A/B/C: Resolución de waypoint robusta + búsqueda por nombre
//
// ============================================================================
//  CAMBIOS v1.1 → v1.2
// ============================================================================
//
//  FIX #OBSTACLE-A — Resolución de waypoint por nombre como fallback
//  ──────────────────────────────────────────────────────────────────────────
//    PROBLEMA v1.1: RerouteAfterNavMeshUpdate() buscaba el waypoint ÚNICAMENTE
//    por ID (GUID). Flutter envía el "nombre display" del waypoint como
//    DestinationWaypointId en NavigationStartedEvent, no el GUID interno.
//
//    Cuando WaypointManager.GetWaypoint(id) falla porque el string que llega
//    es el nombre (p.ej. "Baño 2") y no el GUID, _currentDestination queda null
//    y _fallbackDestId contiene el nombre — que GetWaypoint() tampoco resuelve.
//
//    FIX: Se añade ResolveWaypointFlexible(string idOrName) que intenta:
//      1. GetWaypoint(id)          — búsqueda por GUID exacto
//      2. GetWaypointByName(name)  — búsqueda por nombre display
//      3. SearchWaypointsByName(name).First() — búsqueda parcial/fuzzy
//    Con ese helper, tanto OnNavigationStarted() como RerouteAfterNavMeshUpdate()
//    siempre encuentran el waypoint independientemente de si llega GUID o nombre.
//
//  FIX #OBSTACLE-B — _isActive guard en Update() y cooldown más conservador
//  ──────────────────────────────────────────────────────────────────────────
//    PROBLEMA: El cooldown de 12s se reiniciaba al llamar SimulateObstacleFromFlutter()
//    desde FlutterUnityBridge mientras _isActive=false, dejando una ventana de
//    12s sin protección en la siguiente navegación.
//
//    FIX: _lastRerouteTime solo se actualiza dentro de PlaceObstacleAndReroute().
//    Update() ahora verifica HasDestination antes de acumular frames.
//
//  FIX #OBSTACLE-C — Log de WaypointManager.GetWaypointByName() guard
//  ──────────────────────────────────────────────────────────────────────────
//    PROBLEMA: Si WaypointManager no implementa GetWaypointByName(), hay un
//    MissingMethodException silenciado que confunde el diagnóstico.
//
//    FIX: ResolveWaypointFlexible() captura NotImplementedException y
//    registra cuál estrategia falló. La cadena de fallback continúa aunque
//    un método intermedio lance excepción.
//
//  TODOS LOS FIXES DE v1.1 SE CONSERVAN ÍNTEGRAMENTE.

using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using IndoorNavAR.Core;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
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

        // ✅ FIX #4 (v1.1): Propiedad estática para que SegmentationController
        //   suprima sus alertas cuando este mediador está manejando el obstáculo.
        public static bool IsActive { get; private set; } = false;

        private WaypointData _currentDestination;

        // ✅ v1.2: Guardamos el string original recibido de Flutter (puede ser GUID o nombre)
        private string  _rawDestinationId    = string.Empty;
        private Vector3 _fallbackDestPosition = Vector3.zero;

        private Vector3 DestinationPosition => _currentDestination != null
            ? _currentDestination.Position
            : _fallbackDestPosition;

        private bool HasDestination => _currentDestination != null
            || !string.IsNullOrEmpty(_rawDestinationId);

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

            // ✅ EDITOR FIX (v1.1): resolución de cámara con fallback a Camera.main
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

            Debug.Log("[ObstacleMediator] ✅ v1.2 inicializado. " +
                      $"Camera: {(_cameraTransform != null ? _cameraTransform.name : "NULL")}");
        }

        // ── Update ────────────────────────────────────────────────────────

        private void Update()
        {
            if (!_isActive || _segController == null) return;

            // ✅ v1.2 FIX #OBSTACLE-B: No acumular frames si no hay destino resuelto
            if (!HasDestination) return;

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
            // ✅ v1.2 FIX #OBSTACLE-B: _lastRerouteTime solo se toca aquí
            _lastRerouteTime           = Time.unscaledTime;
            _consecutiveObstacleFrames = 0;

            _obstacleAgent?.PlaceAt(obstaclePos);

            StartCoroutine(RerouteAfterNavMeshUpdate());

            EventBus.Instance?.Publish(new ObstacleDetectedEvent
            {
                ObstaclePosition = obstaclePos,
                DetectedRatio    = ObstacleSegmentationWorker.Instance?.ObstacleRatio ?? 0f,
            });

            if (_logEvents)
            {
                string destLabel = _currentDestination?.WaypointName ?? _rawDestinationId;
                Debug.Log($"[ObstacleMediator] 🚧 Obstáculo en {obstaclePos:F2}. " +
                          $"Recalculando ruta a '{destLabel}'...");
            }
        }

        private IEnumerator RerouteAfterNavMeshUpdate()
        {
            // 3 frames para que NavMeshObstacle termine el carving
            yield return null;
            yield return null;
            yield return null;

            if (_navManager == null) yield break;

            // ✅ v1.2 FIX #OBSTACLE-A: Si _currentDestination es null, intentar resolver
            if (_currentDestination == null && !string.IsNullOrEmpty(_rawDestinationId))
            {
                _currentDestination = ResolveWaypointFlexible(_rawDestinationId);

                if (_currentDestination != null && _logEvents)
                    Debug.Log($"[ObstacleMediator] ✅ Waypoint resuelto late-binding: " +
                              $"'{_currentDestination.WaypointName}' (id={_currentDestination.WaypointId})");
            }

            bool ok = false;

            if (_currentDestination != null)
            {
                // ✅ FIX #3 (v1.1): Usa RerouteToWaypoint() — NO llama TriggerFromWaypoint()
                ok = _navManager.RerouteToWaypoint(_currentDestination);
            }
            else
            {
                Debug.LogWarning($"[ObstacleMediator] ❌ No se pudo resolver waypoint " +
                                 $"'{_rawDestinationId}' — recálculo cancelado.");
            }

            if (_logEvents)
                Debug.Log($"[ObstacleMediator] 🔄 Recálculo: {(ok ? "✅ OK" : "❌ falló")}");
        }

        // ── ✅ v1.2 FIX #OBSTACLE-A — Resolución flexible de waypoint ────

        /// <summary>
        /// ✅ v1.2 — Intenta resolver un waypoint por tres estrategias en cascada:
        ///   1. GetWaypoint(id)          — GUID exacto (caso ideal)
        ///   2. GetWaypointByName(name)  — nombre display exacto (caso Flutter)
        ///   3. SearchWaypointsByName(name).FirstOrDefault() — coincidencia parcial
        ///
        /// Flutter envía el nombre display ("Baño 2") en DestinationWaypointId,
        /// no el GUID interno. Este helper normaliza ambos flujos.
        /// </summary>
        private WaypointData ResolveWaypointFlexible(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) return null;

            WaypointManager wm = FindFirstObjectByType<WaypointManager>(
                FindObjectsInactive.Include);

            if (wm == null)
            {
                Debug.LogWarning("[ObstacleMediator] ⚠️ WaypointManager no encontrado.");
                return null;
            }

            // Estrategia 1: búsqueda por GUID
            try
            {
                var byId = wm.GetWaypoint(idOrName);
                if (byId != null)
                {
                    if (_logEvents)
                        Debug.Log($"[ObstacleMediator] Waypoint resuelto por GUID: '{byId.WaypointName}'");
                    return byId;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ObstacleMediator] GetWaypoint() lanzó excepción: {ex.Message}");
            }

            // Estrategia 2: búsqueda por nombre exacto
            try
            {
                var byName = wm.GetWaypointByName(idOrName);
                if (byName != null)
                {
                    if (_logEvents)
                        Debug.Log($"[ObstacleMediator] Waypoint resuelto por nombre exacto: '{byName.WaypointName}'");
                    return byName;
                }
            }
            catch (NotImplementedException)
            {
                Debug.LogWarning("[ObstacleMediator] GetWaypointByName() no implementado — usando SearchWaypointsByName().");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ObstacleMediator] GetWaypointByName() lanzó excepción: {ex.Message}");
            }

            // Estrategia 3: búsqueda parcial/fuzzy
            try
            {
                var results = wm.SearchWaypointsByName(idOrName);
                if (results != null)
                {
                    // Preferir coincidencia exacta (case-insensitive), luego primera parcial
                    var exact   = results.FirstOrDefault(w =>
                        string.Equals(w.WaypointName, idOrName, StringComparison.OrdinalIgnoreCase));
                    var partial = results.FirstOrDefault();
                    var winner  = exact ?? partial;

                    if (winner != null)
                    {
                        if (_logEvents)
                            Debug.Log($"[ObstacleMediator] Waypoint resuelto por búsqueda parcial: " +
                                      $"'{winner.WaypointName}' (query='{idOrName}')");
                        return winner;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ObstacleMediator] SearchWaypointsByName() lanzó excepción: {ex.Message}");
            }

            Debug.LogError($"[ObstacleMediator] ❌ No se encontró waypoint con id/nombre='{idOrName}' " +
                           "por ninguna estrategia (GUID, nombre exacto, búsqueda parcial).");
            return null;
        }

        // ── Eventos del bus ───────────────────────────────────────────────

        private void OnNavigationStarted(NavigationStartedEvent evt)
        {
            _isActive = true;
            IsActive  = true;
            _consecutiveObstacleFrames = 0;

            // ✅ v1.2 FIX #OBSTACLE-A: Guardar el string raw y resolver con helper flexible
            _rawDestinationId    = evt.DestinationWaypointId;
            _fallbackDestPosition = evt.DestinationPosition;

            // Intentar resolver inmediatamente (puede fallar si WaypointManager no está listo)
            _currentDestination = ResolveWaypointFlexible(evt.DestinationWaypointId);

            if (_logEvents)
            {
                bool resolved = _currentDestination != null;
                Debug.Log($"[ObstacleMediator] ▶️ Activo → '{evt.DestinationWaypointId}' " +
                          $"Destino resuelto: {(resolved ? $"✅ '{_currentDestination.WaypointName}'" : "⏳ (se intentará en recálculo)")}");
            }
        }

        private void OnNavigationCompleted(NavigationCompletedEvent evt)
        {
            ResetState();
            if (_logEvents) Debug.Log("[ObstacleMediator] ✅ Navegación completada — mediator inactivo.");
        }

        private void OnNavigationCancelled(NavigationCancelledEvent evt)
        {
            ResetState();
            if (_logEvents) Debug.Log("[ObstacleMediator] 🛑 Navegación cancelada — mediator inactivo.");
        }

        private void ResetState()
        {
            _isActive  = false;
            IsActive   = false;
            _currentDestination   = null;
            _rawDestinationId     = string.Empty;
            _fallbackDestPosition = Vector3.zero;
            _consecutiveObstacleFrames = 0;
            _obstacleAgent?.Remove();
        }

        // ── API pública ───────────────────────────────────────────────────

        public void SetCurrentDestination(WaypointData destination)
        {
            _currentDestination = destination;
            if (destination != null)
                _rawDestinationId = destination.WaypointId;
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

        // ── ContextMenu ───────────────────────────────────────────────────

        [ContextMenu("🧪 Simular obstáculo")]
        private void DebugSimulateObstacle() => SimulateObstacleFromFlutter();

        [ContextMenu("🔍 Diagnosticar resolución de waypoint")]
        private void DebugResolveWaypoint()
        {
            if (string.IsNullOrEmpty(_rawDestinationId))
            {
                Debug.Log("[ObstacleMediator] _rawDestinationId vacío — sin navegación activa.");
                return;
            }

            Debug.Log($"[ObstacleMediator] 🔍 Intentando resolver '{_rawDestinationId}'...");
            var result = ResolveWaypointFlexible(_rawDestinationId);
            Debug.Log(result != null
                ? $"[ObstacleMediator] ✅ Resuelto: '{result.WaypointName}' id={result.WaypointId}"
                : $"[ObstacleMediator] ❌ No resuelto.");
        }

        [ContextMenu("ℹ️ Estado actual")]
        private void DebugStatus()
        {
            Debug.Log($"[ObstacleMediator] v1.2\n" +
                      $"  isActive={_isActive} | IsActive(static)={IsActive}\n" +
                      $"  rawDestId='{_rawDestinationId}'\n" +
                      $"  currentDest='{_currentDestination?.WaypointName ?? "NULL"}'\n" +
                      $"  fallbackPos={_fallbackDestPosition:F2}\n" +
                      $"  consecutiveFrames={_consecutiveObstacleFrames}/{_confirmationFrames}\n" +
                      $"  cooldown={_rerouteCooldown}s | elapsed={Time.unscaledTime - _lastRerouteTime:F1}s\n" +
                      $"  camera='{(_cameraTransform != null ? _cameraTransform.name : "NULL")}'");
        }

        [ContextMenu("🧪 [Editor] Activar mediator y simular")]
        private void DebugForceActivateAndSimulate()
        {
            if (!_isActive)
            {
                Debug.Log("[ObstacleMediator] [Editor] Activando mediator temporalmente para prueba...");
                _isActive = true;
                IsActive  = true;

                if (_cameraTransform != null)
                    _fallbackDestPosition = _cameraTransform.position + _cameraTransform.forward * 5f;

                _rawDestinationId = "editor_test";
            }
            SimulateObstacleFromFlutter();
        }
    }
}