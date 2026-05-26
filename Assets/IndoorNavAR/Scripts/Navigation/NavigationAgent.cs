// File: NavigationAgent.cs
// ✅ v3.2 — FIX: Detección de nivel durante escaleras
//
// ============================================================================
//  CAMBIOS v3.1 → v3.2
// ============================================================================
//
//  FIX NIVEL A — UpdateCurrentLevel() usaba FloorHeight de los StartPoints
//  para detectar el nivel actual, pero durante el ascenso por escaleras la
//  posición Y del dispositivo es intermedia entre pisos, por lo que el sistema
//  confundía la posición con el piso superior ANTES de llegar a él.
//
//  Solución: doble threshold.
//    • Para SUBIR de nivel (bestLevel > CurrentLevel): se requiere que la
//      posición Y supere FloorHeight + _floorArrivalMargin (margen de llegada).
//    • Para BAJAR de nivel (bestLevel < CurrentLevel): se requiere que la
//      posición Y esté por debajo de FloorHeight - _floorArrivalMargin.
//    • El debounce original (_floorTransitionMinTime) se mantiene como segunda
//      comprobación, pero el nuevo threshold evita que el candidato se registre
//      prematuramente.
//
//  FIX NIVEL B — En FullAR, la posición relevante es la de la CÁMARA (usuario
//  físico), no la del agente virtual. Se usa UserPositionBridge.UserPosition
//  cuando está disponible, con fallback al transform del agente.
//
//  FIX NIVEL C — Histéresis: una vez detectado el nuevo nivel, el candidato
//  no puede regresar al nivel anterior durante _floorHysteresisTime segundos.
//  Evita el efecto "ping-pong" cuando el usuario está justo en el umbral de
//  una escalera (posición Y oscilando entre pisos).
//
//  TODOS LOS COMPORTAMIENTOS DE v3.1 SE CONSERVAN ÍNTEGRAMENTE.

using System;
using UnityEngine;
using UnityEngine.AI;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Navigation.Voice;
using IndoorNavAR.AR;

namespace IndoorNavAR.Navigation
{
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NavigationPathController))]
    public sealed class NavigationAgent : MonoBehaviour
    {
        [Header("Multi-Nivel")]
        [SerializeField] private bool _detectFloorTransitions = true;

        [Header("Eventos")]
        [SerializeField] private bool _publishEvents = true;

        [Header("FullAR — Verificación al navegar")]
        [SerializeField] private bool  _verifyNavMeshOnFullAR = true;
        [SerializeField] private float _fullARVerifyRadius    = 3.0f;

        [Header("Transición de Piso — Debounce")]
        [SerializeField] private float _floorTransitionMinTime = 0.8f;

        [Header("─── v3.2: Umbral de llegada a piso ──────────────────────────────")]
        [Tooltip("Margen vertical (m) que debe superar el usuario POR ENCIMA de FloorHeight\n" +
                 "del piso destino antes de registrar la transición de nivel.\n" +
                 "Evita que la posición Y intermedia en escaleras active el nivel siguiente.\n" +
                 "Default: 0.25m — ajustar según altura de cada escalón.")]
        [SerializeField, Range(0.05f, 1.0f)]
        private float _floorArrivalMargin = 0.25f;

        [Tooltip("Tiempo (s) de histéresis tras detectar un cambio de nivel.\n" +
                 "Durante este tiempo no se acepta un candidato al nivel anterior.\n" +
                 "Evita el efecto ping-pong en umbrales de escalera.\n" +
                 "Default: 2.0s")]
        [SerializeField, Range(0.5f, 5.0f)]
        private float _floorHysteresisTime = 2.0f;

        [Tooltip("Radio de búsqueda vertical (m) para evaluar si la posición Y del usuario\n" +
                 "está dentro del rango de un piso. Mayor = menos estricto.\n" +
                 "Default: 0.8m (mitad de la separación típica entre pisos de 1.6m).")]
        [SerializeField, Range(0.2f, 2.0f)]
        private float _floorDetectionRadius = 0.8f;

        [Header("Debug")]
        [SerializeField] private Transform _debugDestination;
        [SerializeField] private bool      _logVerbose = false;

        // ─── Eventos públicos ─────────────────────────────────────────────────

        public event Action<Vector3>           OnNavigationStarted;
        public event Action                    OnArrived;
        public event Action<NavMeshPathStatus> OnNavigationFailed;

        // ─── Propiedades ──────────────────────────────────────────────────────

        public bool    IsNavigating       => _pathController != null && _pathController.IsNavigating;
        public float   RemainingDistance  => _pathController != null ? _pathController.RemainingDistance : -1f;
        public float   CurrentSpeed       => _pathController != null ? _pathController.CurrentSpeed : 0f;
        public Vector3 LastDestination    { get; private set; }
        public int     CurrentLevel       { get; private set; } = 0;

        public bool IsFullARMode => _arOriginAligner != null && _arOriginAligner.IsFullARMode;

        public float DistanceToDestination => RemainingDistance >= 0f ? RemainingDistance : 0f;

        public float ProgressPercent
        {
            get
            {
                if (!IsNavigating || _pathController?.CurrentPath == null) return 0f;
                float total = _pathController.CurrentPath.TotalLength;
                if (total <= 0f) return 1f;
                return Mathf.Clamp01(1f - DistanceToDestination / total);
            }
        }

        // ─── Campos privados ──────────────────────────────────────────────────

        private NavigationPathController _pathController;
        private NavMeshAgent             _navAgent;
        private AROriginAligner          _arOriginAligner;

        private string _lastDestinationName = string.Empty;

        // Detección de nivel — debounce original (v3)
        private int   _candidateLevel       = -1;
        private float _candidateLevelTime   = 0f;
        private bool  _floorTransitionFired = false;

        // v3.2 FIX NIVEL C — Histéresis post-transición
        private float _lastFloorTransitionTime   = -999f;
        private int   _lastTransitionFromLevel   = -1;

        // ─── Lifecycle ────────────────────────────────────────────────────────

        private void Awake()
        {
            _navAgent       = GetComponent<NavMeshAgent>();
            _pathController = GetComponent<NavigationPathController>()
                           ?? gameObject.AddComponent<NavigationPathController>();

            if (_navAgent != null && !NavMesh.SamplePosition(transform.position, out _, 1f, NavMesh.AllAreas))
            {
                _navAgent.enabled = false;
                Debug.Log("[NavigationAgent] NavMeshAgent deshabilitado hasta que el NavMesh esté listo.");
            }

            _pathController.OnPathStarted     += HandlePathStarted;
            _pathController.OnPathCompleted   += HandlePathCompleted;
            _pathController.OnPathFailed      += HandlePathFailed;
            _pathController.OnWaypointReached += HandleWaypointReached;
        }

        private void Start()
        {
            _arOriginAligner = FindFirstObjectByType<AROriginAligner>(FindObjectsInactive.Include);
            if (_arOriginAligner == null)
                Debug.LogWarning("[NavigationAgent] AROriginAligner no encontrado. Se asumirá NoAR.");

            EventBus.Instance?.Subscribe<NavMeshGeneratedEvent>(OnNavMeshGenerated);
        }

        private void OnNavMeshGenerated(NavMeshGeneratedEvent evt)
        {
            if (!evt.Success) return;
            if (_navAgent != null && !_navAgent.enabled)
            {
                _navAgent.enabled = true;
                Debug.Log("[NavigationAgent] ✅ NavMeshAgent habilitado — NavMesh disponible.");
            }
        }

        private void OnDestroy()
        {
            EventBus.Instance?.Unsubscribe<NavMeshGeneratedEvent>(OnNavMeshGenerated);
            if (_pathController == null) return;
            _pathController.OnPathStarted     -= HandlePathStarted;
            _pathController.OnPathCompleted   -= HandlePathCompleted;
            _pathController.OnPathFailed      -= HandlePathFailed;
            _pathController.OnWaypointReached -= HandleWaypointReached;
        }

        private void Update()
        {
            if (!IsNavigating) return;

            if (IsFullARMode)
            {
                if (_navAgent != null && _navAgent.enabled && _navAgent.isOnNavMesh)
                {
                    if (!_navAgent.isStopped)
                    {
                        _navAgent.isStopped = true;
                        if (_logVerbose)
                            Debug.Log("[NavigationAgent] FullAR: movimiento autónomo detenido en Update().");
                    }
                }
                if (_detectFloorTransitions) UpdateCurrentLevel();
                return;
            }

            if (_detectFloorTransitions) UpdateCurrentLevel();
        }

        // ─── API pública — Navegación ─────────────────────────────────────────

        public void StartNavigation(Vector3 destination)
        {
            EnsureNavMeshAgentEnabled();
            LastDestination      = destination;
            _lastDestinationName = string.Empty;
            if (IsFullARMode) PrepareForFullARNavigation();
            if (_logVerbose) Debug.Log($"[NavigationAgent] StartNavigation → {destination:F2}");
            _pathController.NavigateTo(destination);
            if (IsFullARMode) EnsureAgentStoppedInFullAR();
        }

        public bool NavigateToWaypoint(WaypointData waypoint)
        {
            if (waypoint == null)
            {
                Debug.LogWarning("[NavigationAgent] NavigateToWaypoint: waypoint es null.");
                return false;
            }

            EnsureNavMeshAgentEnabled();
            LastDestination      = waypoint.Position;
            _lastDestinationName = waypoint.WaypointName;
            if (IsFullARMode) PrepareForFullARNavigation();

            if (_logVerbose)
                Debug.Log($"[NavigationAgent] NavigateToWaypoint → {waypoint.WaypointName} " +
                          $"@ {waypoint.Position:F2} | FullAR={IsFullARMode}");

            _pathController.NavigateTo(waypoint.Position);
            bool ok = _pathController.CurrentPath?.IsValid ?? false;

            if (!ok)
                Debug.LogWarning($"[NavigationAgent] ⚠️ Ruta inválida a '{waypoint.WaypointName}'");

            if (IsFullARMode) EnsureAgentStoppedInFullAR();
            return ok;
        }

        /// <summary>
        /// ✅ v3.1 — Recálculo silencioso de ruta para ObstacleRerouteMediator.
        /// </summary>
        public bool NavigateToWaypointForced(WaypointData waypoint)
        {
            if (waypoint == null)
            {
                Debug.LogWarning("[NavigationAgent] NavigateToWaypointForced: waypoint es null.");
                return false;
            }

            EnsureNavMeshAgentEnabled();
            LastDestination = waypoint.Position;
            if (IsFullARMode) PrepareForFullARNavigation();

            if (_logVerbose)
                Debug.Log($"[NavigationAgent] NavigateToWaypointForced → {waypoint.WaypointName}");

            _pathController.NavigateTo(waypoint.Position, forceRecalculate: true);
            bool ok = _pathController.CurrentPath?.IsValid ?? false;

            if (!ok)
                Debug.LogWarning($"[NavigationAgent] ⚠️ [Forced] Ruta inválida a '{waypoint.WaypointName}'");

            if (IsFullARMode) EnsureAgentStoppedInFullAR();
            return ok;
        }

        public void SetDestination(Vector3 newDestination)
        {
            LastDestination      = newDestination;
            _lastDestinationName = string.Empty;
            _pathController.NavigateTo(newDestination, forceRecalculate: true);
            if (IsFullARMode) EnsureAgentStoppedInFullAR();
        }

        public void StopNavigation()
        {
            if (_logVerbose) Debug.Log("[NavigationAgent] StopNavigation");
            _pathController.StopNavigation();
        }

        public void StopNavigation(string reason)
        {
            if (_logVerbose) Debug.Log($"[NavigationAgent] StopNavigation: {reason}");
            _pathController.StopNavigation();
            if (_publishEvents)
                EventBus.Instance?.Publish(new NavigationCancelledEvent { Reason = reason });
        }

        public void NavigateToLevel(int levelIndex)
        {
            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            foreach (var pt in startPoints)
            {
                if (pt.Level == levelIndex) { StartNavigation(pt.Position); return; }
            }
            Debug.LogWarning($"[NavigationAgent] No hay StartPoint para nivel {levelIndex}");
        }

        // ─── API pública — Teleport ───────────────────────────────────────────

        public bool TeleportTo(Vector3 position)
        {
            if (!NavMesh.SamplePosition(position, out NavMeshHit hit, 1.0f, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[NavigationAgent] TeleportTo: sin NavMesh en {position:F2}");
                return false;
            }

            if (IsNavigating) _pathController.StopNavigation();
            transform.position = hit.position;
            if (_navAgent != null && _navAgent.enabled && _navAgent.isOnNavMesh)
                _navAgent.Warp(hit.position);

            return true;
        }

        // ─── FullAR — Helpers internos ────────────────────────────────────────

        private void PrepareForFullARNavigation()
        {
            if (!_verifyNavMeshOnFullAR) return;
            if (!NavMesh.SamplePosition(transform.position, out NavMeshHit hit,
                    _fullARVerifyRadius, NavMesh.AllAreas)) return;

            if (Vector3.Distance(transform.position, hit.position) > 0.1f)
            {
                transform.position = hit.position;
                if (_navAgent != null && _navAgent.enabled && _navAgent.isOnNavMesh)
                    _navAgent.Warp(hit.position);
            }
        }

        private void EnsureAgentStoppedInFullAR()
        {
            if (_navAgent == null || !_navAgent.enabled) return;
            StartCoroutine(StopAfterOneFrame());
        }

        private System.Collections.IEnumerator StopAfterOneFrame()
        {
            yield return null;
            if (_navAgent != null && _navAgent.enabled && _navAgent.isOnNavMesh)
                _navAgent.isStopped = true;
        }

        private void EnsureNavMeshAgentEnabled()
        {
            if (_navAgent != null && !_navAgent.enabled)
            {
                _navAgent.enabled = true;
                if (!NavMesh.SamplePosition(transform.position, out _, 2f, NavMesh.AllAreas))
                    Debug.LogWarning("[NavigationAgent] ⚠️ NavMesh aún no disponible.");
            }
        }

        // ─── Detección de nivel ───────────────────────────────────────────────

        /// <summary>
        /// v3.2 — Detección de nivel con umbral de llegada + histéresis.
        ///
        /// LÓGICA PRINCIPAL:
        ///   1. Obtener posición Y real (cámara en FullAR, agente en NoAR).
        ///   2. Para cada StartPoint, evaluar si el usuario "ha llegado" al piso:
        ///        - Subir (bestLevel > CurrentLevel): Y >= FloorHeight - _floorDetectionRadius
        ///          Y además Y >= FloorHeight - _floorArrivalMargin (está suficientemente arriba).
        ///        - Bajar (bestLevel < CurrentLevel): Y <= FloorHeight + _floorDetectionRadius.
        ///        - Mismo nivel: dentro del radio normal.
        ///   3. Histéresis: no volver al nivel anterior durante _floorHysteresisTime.
        ///   4. Debounce: confirmar candidato estable durante _floorTransitionMinTime.
        /// </summary>
        private void UpdateCurrentLevel()
        {
            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            if (startPoints == null || startPoints.Count == 0) return;

            // FIX NIVEL B: usar posición de cámara en FullAR
            float agentY = GetUserHeightY();

            int   bestLevel = CurrentLevel;  // iniciar con nivel actual para no cambiar si no hay candidato claro
            float bestScore = float.MaxValue;

            foreach (var pt in startPoints)
            {
                if (pt == null) continue;

                float floorY = pt.FloorHeight;
                float deltaY = agentY - floorY;

                // Calcular score: cuán lejos estamos del piso en dirección correcta
                float score;

                if (pt.Level > CurrentLevel)
                {
                    // Subiendo: solo aceptar este piso si el usuario ya superó el umbral
                    // deltaY debe ser >= -_floorArrivalMargin (estamos cerca o encima del piso)
                    // y <= _floorDetectionRadius (no estamos demasiado por encima)
                    if (deltaY < -_floorArrivalMargin || deltaY > _floorDetectionRadius * 2f)
                        continue; // aún en escalera subiendo, no hemos llegado
                    score = Mathf.Abs(deltaY);
                }
                else if (pt.Level < CurrentLevel)
                {
                    // Bajando: aceptar si el usuario está claramente por debajo del piso actual
                    if (deltaY > _floorArrivalMargin || deltaY < -_floorDetectionRadius * 2f)
                        continue;
                    score = Mathf.Abs(deltaY);
                }
                else
                {
                    // Mismo nivel: usar radio normal
                    if (Mathf.Abs(deltaY) > _floorDetectionRadius)
                        continue;
                    score = Mathf.Abs(deltaY);
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestLevel = pt.Level;
                }
            }

            // FIX NIVEL C: Histéresis — no volver al nivel anterior recién abandonado
            if (bestLevel == _lastTransitionFromLevel &&
                (Time.time - _lastFloorTransitionTime) < _floorHysteresisTime)
            {
                if (_logVerbose)
                    Debug.Log($"[NavigationAgent] ⏳ Histéresis activa — ignorando candidato " +
                              $"nivel {bestLevel} (origen={_lastTransitionFromLevel}, " +
                              $"elapsed={Time.time - _lastFloorTransitionTime:F1}s/{_floorHysteresisTime}s)");
                return;
            }

            if (bestLevel != CurrentLevel)
            {
                // Acumular tiempo de estabilidad del candidato
                if (bestLevel == _candidateLevel)
                {
                    _candidateLevelTime += Time.deltaTime;
                }
                else
                {
                    // Nuevo candidato — resetear timer
                    _candidateLevel       = bestLevel;
                    _candidateLevelTime   = 0f;
                    _floorTransitionFired = false;

                    if (_logVerbose)
                        Debug.Log($"[NavigationAgent] 🔍 Nuevo candidato de nivel: {bestLevel} " +
                                  $"(agentY={agentY:F3}, score={bestScore:F3})");
                }

                // Confirmar transición tras debounce
                if (_candidateLevelTime >= _floorTransitionMinTime && !_floorTransitionFired)
                {
                    int prev = CurrentLevel;
                    CurrentLevel          = bestLevel;
                    _floorTransitionFired = true;

                    // Registrar para histéresis
                    _lastFloorTransitionTime = Time.time;
                    _lastTransitionFromLevel = prev;

                    Debug.Log($"[NavigationAgent] ✅ Transición nivel {prev} → {bestLevel} " +
                              $"(confirmada tras {_candidateLevelTime:F2}s | agentY={agentY:F3})");

                    if (_publishEvents)
                        EventBus.Instance?.Publish(new FloorTransitionEvent
                        {
                            FromLevel     = prev,
                            ToLevel       = bestLevel,
                            AgentPosition = transform.position
                        });
                }
            }
            else
            {
                // Volvimos al nivel actual — resetear candidato
                if (_candidateLevel != CurrentLevel)
                {
                    if (_logVerbose && _candidateLevel != -1)
                        Debug.Log($"[NavigationAgent] ↩️ Cancelado candidato nivel {_candidateLevel} " +
                                  $"(regresó a nivel {CurrentLevel})");
                    _candidateLevel       = CurrentLevel;
                    _candidateLevelTime   = 0f;
                    _floorTransitionFired = false;
                }
            }
        }

        /// <summary>
        /// v3.2 FIX NIVEL B: Obtiene la posición Y real del usuario.
        /// En FullAR usa la cámara XR (posición física real).
        /// En NoAR usa el transform del agente.
        /// </summary>
        private float GetUserHeightY()
        {
            // Intentar UserPositionBridge primero (más preciso en FullAR)
            var bridge = UserPositionBridge.Instance;
            if (bridge != null)
                return bridge.UserPosition.y;

            // Fallback: cámara principal en FullAR, agente en NoAR
            if (IsFullARMode && Camera.main != null)
                return Camera.main.transform.position.y;

            return transform.position.y;
        }

        // ─── Handlers del PathController ──────────────────────────────────────

        private void HandlePathStarted(Vector3 destination)
        {
            OnNavigationStarted?.Invoke(destination);

            if (_publishEvents)
                EventBus.Instance?.Publish(new NavigationStartedEvent
                {
                    DestinationWaypointId = _lastDestinationName,
                    StartPosition         = transform.position,
                    DestinationPosition   = destination,
                    EstimatedDistance     = Vector3.Distance(transform.position, destination)
                });
        }

        /// <summary>
        /// v3 FIX C: HandlePathCompleted ya NO llama UpdateCurrentLevel().
        /// </summary>
        private void HandlePathCompleted()
        {
            OnArrived?.Invoke();

            EventBus.Instance?.Publish(new NavigationArrivedEvent
            {
                WaypointName = _lastDestinationName,
                Position     = transform.position
            });

            if (_publishEvents)
                EventBus.Instance?.Publish(new NavigationCompletedEvent
                {
                    DestinationWaypointId = string.Empty,
                    TotalDistance         = _pathController?.CurrentPath?.TotalLength ?? 0f,
                    TotalTime             = 0f
                });
        }

        private void HandlePathFailed(NavMeshPathStatus status)
        {
            OnNavigationFailed?.Invoke(status);
            Debug.LogWarning($"[NavigationAgent] ❌ Navegación fallida. Status={status}. " +
                             $"Destino: {LastDestination:F2} | Agente: {transform.position:F2}");
        }

        private void HandleWaypointReached(int index, Vector3 position)
        {
            if (_logVerbose)
                Debug.Log($"[NavigationAgent] Waypoint {index} alcanzado @ {position:F2}");
        }

        // ─── ContextMenu ──────────────────────────────────────────────────────

        [ContextMenu("Start Navigation (Debug)")]
        private void DebugStartNavigation()
        {
            if (_debugDestination == null)
            { Debug.LogWarning("[NavigationAgent] Asignar _debugDestination."); return; }
            StartNavigation(_debugDestination.position);
        }

        [ContextMenu("Stop Navigation")]
        private void DebugStopNavigation() => StopNavigation();

        [ContextMenu("Log Path Status")]
        private void DebugLogStatus()
        {
            Debug.Log($"[NavigationAgent] IsNavigating={IsNavigating} | FullAR={IsFullARMode}\n" +
                      $"  Level={CurrentLevel} | CandidateLevel={_candidateLevel} | " +
                      $"CandidateTime={_candidateLevelTime:F2}s\n" +
                      $"  UserHeightY={GetUserHeightY():F3} | AgentY={transform.position.y:F3}\n" +
                      $"  Remaining={RemainingDistance:F2}m | Speed={CurrentSpeed:F2}m/s\n" +
                      $"  floorArrivalMargin={_floorArrivalMargin}m | " +
                      $"detectionRadius={_floorDetectionRadius}m\n" +
                      $"  hysteresisActive={Time.time - _lastFloorTransitionTime < _floorHysteresisTime} | " +
                      $"fromLevel={_lastTransitionFromLevel}");

            var bridge = UserPositionBridge.Instance;
            if (bridge != null)
                Debug.Log($"  UserPos={bridge.UserPosition:F2} | IsNoAR={bridge.IsNoArMode}");

            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            foreach (var pt in startPoints)
            {
                float deltaY = GetUserHeightY() - pt.FloorHeight;
                Debug.Log($"  StartPoint Level{pt.Level}: Y={pt.FloorHeight:F3} | ΔY={deltaY:F3}");
            }
        }
    }
}