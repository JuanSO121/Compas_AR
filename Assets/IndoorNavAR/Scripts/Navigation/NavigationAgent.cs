// File: NavigationAgent.cs
//
// ✅ v2 — FullAR: el agente NO se teletransporta por su cuenta.
//         AROriginAligner.SyncAgentToCameraFullAR() ya lo posiciona cada frame.
//
// ============================================================================
//  CAMBIOS v1 → v2
// ============================================================================
//
//  PROBLEMA:
//    TeleportToUserIfFullAR() intentaba hacer Warp del agente a la posición
//    del usuario al inicio de cada navegación. Pero en FullAR, AROriginAligner
//    ya sincroniza el agente con la cámara XR cada frame. El Warp extra
//    podía interferir con esa sincronización o fallar si el NavMesh aún
//    no estaba disponible en ese momento preciso.
//
//    Adicionalmente: NavigateToWaypoint() devolvía _pathController.IsNavigating
//    evaluado INMEDIATAMENTE después de NavigateTo(), siempre false la primera vez.
//
//  SOLUCIÓN v2:
//    1. TeleportToUserIfFullAR() ya no hace Warp: confía en que AROriginAligner
//       ya posicionó correctamente el agente. Solo verifica que está en NavMesh.
//
//    2. NavigateToWaypoint() usa CurrentPath.IsValid (síncrono) en lugar de
//       IsNavigating (se activa un frame después).
//
//    3. Nueva propiedad pública IsFullARMode para que ARGuideController y
//       otros sistemas sepan el modo activo sin depender de AROriginAligner.
//
//  COMPORTAMIENTO EN FullAR:
//    - El agente está activo e invisible (gestionado por AROriginAligner).
//    - AROriginAligner lo mueve al NavMesh más cercano a la cámara cada frame.
//    - Al iniciar navegación, NavigateToWaypoint() calcula la ruta desde
//      la posición actual del agente (= posición real del usuario).
//    - NavigationPathController genera CurrentPath con la ruta.
//    - El NavMeshAgent tiene isStopped=true: el agente NO camina solo.
//      ARGuideController detecta FullAR y no activa el movimiento autónomo.
//    - NavigationVoiceGuide genera instrucciones basadas en AgentPosition.
//
//  TODOS LOS COMPORTAMIENTOS ANTERIORES SE CONSERVAN ÍNTEGRAMENTE.

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
        [Tooltip("En FullAR, verifica que el agente esté sobre el NavMesh antes de calcular la ruta.\n" +
                 "AROriginAligner es quien lo posiciona; aquí solo hacemos la comprobación.")]
        [SerializeField] private bool _verifyNavMeshOnFullAR = true;

        [Tooltip("Radio de verificación NavMesh en FullAR (m).")]
        [SerializeField] private float _fullARVerifyRadius = 3.0f;

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

        /// <summary>
        /// True si el sistema está en modo FullAR (ARCore activo).
        /// En FullAR el agente no debe moverse por su cuenta.
        /// </summary>
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

        // ─── Lifecycle ────────────────────────────────────────────────────────

        private void Awake()
        {
            _navAgent       = GetComponent<NavMeshAgent>();
            _pathController = GetComponent<NavigationPathController>()
                           ?? gameObject.AddComponent<NavigationPathController>();

            // Deshabilitar NavMeshAgent hasta que haya NavMesh disponible.
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

            // En FullAR el agente NO debe moverse por su cuenta.
            // AROriginAligner posiciona el agente; NavigationVoiceGuide
            // genera las instrucciones de voz basadas en esa posición.
            // Aquí solo detenemos cualquier movimiento autónomo residual.
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
                // En FullAR aún detectamos transiciones de piso (basado en posición del agente = usuario)
                if (_detectFloorTransitions) UpdateCurrentLevel();
                return;
            }

            // NoAR: comportamiento normal
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

            // En FullAR: la ruta fue calculada pero el agente no debe moverse.
            if (IsFullARMode) EnsureAgentStoppedInFullAR();
        }

        /// <summary>
        /// Inicia la navegación hacia un waypoint.
        ///
        /// En FullAR:
        ///   - AROriginAligner ya posicionó el agente en la posición del usuario.
        ///   - Solo verificamos que está en el NavMesh.
        ///   - Calculamos la ruta con NavigateTo().
        ///   - Forzamos isStopped=true: el agente NO camina hacia el destino.
        ///   - NavigationVoiceGuide genera instrucciones basadas en AgentPosition.
        ///
        /// En NoAR:
        ///   - Comportamiento original: el agente camina hacia el destino.
        /// </summary>
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
                          $"@ {waypoint.Position:F2} | agentPos={transform.position:F2} | FullAR={IsFullARMode}");

            _pathController.NavigateTo(waypoint.Position);

            // ✅ CurrentPath.IsValid es síncrono — IsNavigating se activa un frame después.
            bool ok = _pathController.CurrentPath?.IsValid ?? false;

            if (!ok)
                Debug.LogWarning($"[NavigationAgent] ⚠️ Ruta inválida a '{waypoint.WaypointName}' " +
                                 $"desde {transform.position:F2}. NavMesh disponible?");

            // En FullAR: asegurar que el agente no se mueve después del cálculo
            if (IsFullARMode) EnsureAgentStoppedInFullAR();

            return ok;
        }

        public void SetDestination(Vector3 newDestination)
        {
            LastDestination      = newDestination;
            _lastDestinationName = string.Empty;
            if (_logVerbose) Debug.Log($"[NavigationAgent] SetDestination → {newDestination:F2}");
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

            if (_logVerbose) Debug.Log($"[NavigationAgent] Teleport exitoso a {hit.position:F2}");
            return true;
        }

        // ─── FullAR — Helpers internos ────────────────────────────────────────

        /// <summary>
        /// En FullAR, verifica que el agente esté sobre el NavMesh antes de calcular
        /// la ruta. AROriginAligner ya lo posicionó; aquí solo confirmamos.
        /// Si hay una pequeña desviación, la corregimos.
        /// </summary>
        private void PrepareForFullARNavigation()
        {
            if (!_verifyNavMeshOnFullAR) return;

            if (!NavMesh.SamplePosition(transform.position, out NavMeshHit hit,
                    _fullARVerifyRadius, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[NavigationAgent] ⚠️ FullAR: agente en {transform.position:F2} " +
                                 $"no está sobre el NavMesh (radio {_fullARVerifyRadius}m). " +
                                 "AROriginAligner debería haberlo posicionado. " +
                                 "Verificar que el NavMesh está cargado.");
                return;
            }

            // Corrección menor si el agente se desvió ligeramente del NavMesh
            if (Vector3.Distance(transform.position, hit.position) > 0.1f)
            {
                transform.position = hit.position;
                if (_navAgent != null && _navAgent.enabled && _navAgent.isOnNavMesh)
                    _navAgent.Warp(hit.position);

                if (_logVerbose)
                    Debug.Log($"[NavigationAgent] FullAR: corrección menor al NavMesh → {hit.position:F2}");
            }
            else
            {
                if (_logVerbose)
                    Debug.Log($"[NavigationAgent] FullAR: agente en {transform.position:F2}, " +
                              "posición válida para calcular ruta.");
            }
        }

        /// <summary>
        /// En FullAR, fuerza isStopped=true después de calcular la ruta.
        /// El NavMeshAgent debe tener la ruta calculada pero NO ejecutarla.
        /// La posición del agente la gestiona AROriginAligner (sigue la cámara).
        /// </summary>
        private void EnsureAgentStoppedInFullAR()
        {
            if (_navAgent == null || !_navAgent.enabled) return;

            // Necesitamos un frame para que NavigateTo() procese el path
            // antes de detener el agente. Usamos una corrutina de un frame.
            StartCoroutine(StopAfterOneFrame());
        }

        private System.Collections.IEnumerator StopAfterOneFrame()
        {
            yield return null;
            if (_navAgent != null && _navAgent.enabled && _navAgent.isOnNavMesh)
            {
                _navAgent.isStopped = true;
                if (_logVerbose)
                    Debug.Log("[NavigationAgent] FullAR: NavMeshAgent detenido " +
                              "(ruta calculada pero agente no camina).");
            }
        }

        /// <summary>
        /// Re-habilita el NavMeshAgent si fue deshabilitado al inicio por ausencia de NavMesh.
        /// </summary>
        private void EnsureNavMeshAgentEnabled()
        {
            if (_navAgent != null && !_navAgent.enabled)
            {
                if (NavMesh.SamplePosition(transform.position, out _, 2f, NavMesh.AllAreas))
                {
                    _navAgent.enabled = true;
                    Debug.Log("[NavigationAgent] ✅ NavMeshAgent re-habilitado antes de navegar.");
                }
                else
                {
                    Debug.LogWarning("[NavigationAgent] ⚠️ NavMesh aún no disponible. La ruta puede fallar.");
                    _navAgent.enabled = true;
                }
            }
        }

        // ─── Detección de nivel ───────────────────────────────────────────────

        private void UpdateCurrentLevel()
        {
            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            if (startPoints.Count == 0) return;

            float agentY    = transform.position.y;
            int   bestLevel = 0;
            float bestDist  = float.MaxValue;

            foreach (var pt in startPoints)
            {
                float dist = Mathf.Abs(agentY - pt.FloorHeight);
                if (dist < bestDist) { bestDist = dist; bestLevel = pt.Level; }
            }

            if (bestLevel == CurrentLevel) return;

            int prev = CurrentLevel;
            CurrentLevel = bestLevel;

            if (_logVerbose)
                Debug.Log($"[NavigationAgent] Transición nivel {prev} → {bestLevel}");

            if (_publishEvents)
                EventBus.Instance?.Publish(new FloorTransitionEvent
                {
                    FromLevel     = prev,
                    ToLevel       = bestLevel,
                    AgentPosition = transform.position
                });
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
            { Debug.LogWarning("[NavigationAgent] Asignar _debugDestination en el Inspector."); return; }
            StartNavigation(_debugDestination.position);
        }

        [ContextMenu("Stop Navigation")]
        private void DebugStopNavigation() => StopNavigation();

        [ContextMenu("Log Path Status")]
        private void DebugLogStatus()
        {
            Debug.Log($"[NavigationAgent] IsNavigating={IsNavigating} | FullAR={IsFullARMode}\n" +
                      $"  Level={CurrentLevel} | Remaining={RemainingDistance:F2}m\n" +
                      $"  Progress={ProgressPercent * 100f:F0}% | Speed={CurrentSpeed:F2}m/s\n" +
                      $"  AgentPos={transform.position:F2} | Dest={LastDestination:F2}\n" +
                      $"  NavAgent stopped={_navAgent?.isStopped} | enabled={_navAgent?.enabled}");

            if (_pathController?.CurrentPath != null)
            {
                var p = _pathController.CurrentPath;
                Debug.Log($"  Path: {p.Waypoints.Count} waypoints | " +
                          $"{p.TotalLength:F1}m | status={p.Status} | valid={p.IsValid}");
            }

            var bridge = UserPositionBridge.Instance;
            if (bridge != null)
                Debug.Log($"  UserPos={bridge.UserPosition:F2} | " +
                          $"AgentPos={bridge.AgentPosition:F2} | " +
                          $"Speed={bridge.UserSpeed:F2}m/s");
        }
    }
}