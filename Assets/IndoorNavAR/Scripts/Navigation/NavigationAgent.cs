// File: NavigationAgent.cs
// ============================================================================
//  AGENTE DE NAVEGACIÓN INDOOR — IndoorNavAR
// ============================================================================

using System;
using UnityEngine;
using UnityEngine.AI;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Data;
namespace IndoorNavAR.Navigation
{
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NavigationPathController))]
    public sealed class NavigationAgent : MonoBehaviour
    {
        // ─── Inspector ────────────────────────────────────────────────────────

        [Header("Multi-Nivel")]
        [SerializeField]
        private bool _detectFloorTransitions = true;

        [Header("Eventos")]
        [SerializeField]
        private bool _publishEvents = true;

        [Header("Debug")]
        [SerializeField]
        private Transform _debugDestination;

        [SerializeField]
        private bool _logVerbose = false;

        // ─── Eventos públicos ─────────────────────────────────────────────────

        public event Action<Vector3>           OnNavigationStarted;
        public event Action                    OnArrived;
        public event Action<NavMeshPathStatus> OnNavigationFailed;

        // ─── Propiedades ──────────────────────────────────────────────────────

        public bool IsNavigating       => _pathController != null && _pathController.IsNavigating;
        public float RemainingDistance => _pathController != null ? _pathController.RemainingDistance : -1f;
        public float CurrentSpeed      => _pathController != null ? _pathController.CurrentSpeed : 0f;
        public Vector3 LastDestination { get; private set; }
        public int CurrentLevel        { get; private set; } = 0;

        /// <summary>Distancia restante al destino (alias para compatibilidad con KeyboardTestingController).</summary>
        public float DistanceToDestination => RemainingDistance >= 0f ? RemainingDistance : 0f;

        /// <summary>Progreso de la navegación [0–1]. 0 = inicio, 1 = llegada.</summary>
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

        // ─── Componentes ──────────────────────────────────────────────────────

        private NavigationPathController _pathController;
        private NavMeshAgent             _navAgent;

        // ─────────────────────────────────────────────────────────────────────
        //  UNITY LIFECYCLE
        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            _navAgent       = GetComponent<NavMeshAgent>();
            _pathController = GetComponent<NavigationPathController>()
                           ?? gameObject.AddComponent<NavigationPathController>();

            _pathController.OnPathStarted     += HandlePathStarted;
            _pathController.OnPathCompleted   += HandlePathCompleted;
            _pathController.OnPathFailed      += HandlePathFailed;
            _pathController.OnWaypointReached += HandleWaypointReached;
        }

        private void OnDestroy()
        {
            if (_pathController == null) return;
            _pathController.OnPathStarted     -= HandlePathStarted;
            _pathController.OnPathCompleted   -= HandlePathCompleted;
            _pathController.OnPathFailed      -= HandlePathFailed;
            _pathController.OnWaypointReached -= HandleWaypointReached;
        }

        private void Update()
        {
            if (!IsNavigating) return;
            if (_detectFloorTransitions)
                UpdateCurrentLevel();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA — NAVEGACIÓN
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Inicia la navegación hacia una posición world-space.</summary>
        public void StartNavigation(Vector3 destination)
        {
            LastDestination = destination;
            if (_logVerbose)
                Debug.Log($"[NavigationAgent] StartNavigation → {destination:F2}");
            _pathController.NavigateTo(destination);
        }

        /// <summary>
        /// Navega hacia un WaypointData. Retorna true si el path se inició correctamente.
        /// </summary>
        public bool NavigateToWaypoint(WaypointData waypoint)
        {
            if (waypoint == null)
            {
                Debug.LogWarning("[NavigationAgent] NavigateToWaypoint: waypoint es null.");
                return false;
            }

            LastDestination = waypoint.Position;

            if (_logVerbose)
                Debug.Log($"[NavigationAgent] NavigateToWaypoint → {waypoint.WaypointName} @ {waypoint.Position:F2}");

            _pathController.NavigateTo(waypoint.Position);
            return _pathController.IsNavigating;
        }

        /// <summary>Cambia el destino mientras el agente navega (recálculo inmediato).</summary>
        public void SetDestination(Vector3 newDestination)
        {
            if (_logVerbose)
                Debug.Log($"[NavigationAgent] SetDestination → {newDestination:F2}");
            LastDestination = newDestination;
            _pathController.NavigateTo(newDestination, forceRecalculate: true);
        }

        /// <summary>Detiene la navegación inmediatamente.</summary>
        public void StopNavigation()
        {
            if (_logVerbose) Debug.Log("[NavigationAgent] StopNavigation");
            _pathController.StopNavigation();
        }

        /// <summary>Detiene la navegación con un motivo (compatible con KeyboardTestingController y NavigationManager).</summary>
        public void StopNavigation(string reason)
        {
            if (_logVerbose) Debug.Log($"[NavigationAgent] StopNavigation: {reason}");
            _pathController.StopNavigation();

            if (_publishEvents)
                EventBus.Instance?.Publish(new NavigationCancelledEvent { Reason = reason });
        }

        /// <summary>Navega al StartPoint del nivel indicado.</summary>
        public void NavigateToLevel(int levelIndex)
        {
            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            foreach (var pt in startPoints)
            {
                if (pt.Level == levelIndex)
                {
                    StartNavigation(pt.Position);
                    return;
                }
            }
            Debug.LogWarning($"[NavigationAgent] No hay StartPoint para nivel {levelIndex}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA — TELEPORT
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Teleporta el agente a <paramref name="position"/>.
        /// Requiere que la posición esté sobre el NavMesh (radio de búsqueda 1 m).
        /// Retorna true si el teleport tuvo éxito.
        /// </summary>
        public bool TeleportTo(Vector3 position)
        {
            if (!NavMesh.SamplePosition(position, out NavMeshHit hit, 1.0f, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[NavigationAgent] TeleportTo: sin NavMesh en {position:F2}");
                return false;
            }

            if (IsNavigating)
                _pathController.StopNavigation();

            transform.position = hit.position;
            _navAgent.Warp(hit.position);

            if (_logVerbose)
                Debug.Log($"[NavigationAgent] Teleport exitoso a {hit.position:F2}");

            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  DETECCIÓN DE NIVEL ACTUAL
        // ─────────────────────────────────────────────────────────────────────

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

            if (bestLevel != CurrentLevel)
            {
                int previousLevel = CurrentLevel;
                CurrentLevel      = bestLevel;

                if (_logVerbose)
                    Debug.Log($"[NavigationAgent] Transición nivel {previousLevel} → {bestLevel}");

                if (_publishEvents)
                    EventBus.Instance?.Publish(new FloorTransitionEvent
                    {
                        FromLevel     = previousLevel,
                        ToLevel       = bestLevel,
                        AgentPosition = transform.position
                    });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HANDLERS DEL PATH CONTROLLER
        // ─────────────────────────────────────────────────────────────────────

        private void HandlePathStarted(Vector3 destination)
        {
            OnNavigationStarted?.Invoke(destination);

            if (_publishEvents)
                EventBus.Instance?.Publish(new NavigationStartedEvent
                {
                    DestinationWaypointId = string.Empty,
                    StartPosition         = transform.position,
                    DestinationPosition   = destination,
                    EstimatedDistance     = Vector3.Distance(transform.position, destination)
                });
        }

        private void HandlePathCompleted()
        {
            OnArrived?.Invoke();

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
            Debug.LogWarning($"[NavigationAgent] Navegación fallida. Status={status}. Destino: {LastDestination:F2}");
        }

        private void HandleWaypointReached(int index, Vector3 position)
        {
            if (_logVerbose)
                Debug.Log($"[NavigationAgent] Waypoint {index} alcanzado @ {position:F2}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CONTEXT MENU (Debug)
        // ─────────────────────────────────────────────────────────────────────

        [ContextMenu("Start Navigation (Debug)")]
        private void DebugStartNavigation()
        {
            if (_debugDestination == null)
            {
                Debug.LogWarning("[NavigationAgent] Asignar _debugDestination en el Inspector.");
                return;
            }
            StartNavigation(_debugDestination.position);
        }

        [ContextMenu("Stop Navigation")]
        private void DebugStopNavigation() => StopNavigation();

        [ContextMenu("Log Path Status")]
        private void DebugLogStatus()
        {
            Debug.Log($"[NavigationAgent] IsNavigating={IsNavigating}, " +
                      $"Level={CurrentLevel}, " +
                      $"Remaining={RemainingDistance:F2}m, " +
                      $"Progress={ProgressPercent * 100f:F0}%, " +
                      $"Speed={CurrentSpeed:F2}m/s");

            if (_pathController?.CurrentPath != null)
            {
                OptimizedPath p = _pathController.CurrentPath;
                Debug.Log($"  Path: {p.Waypoints.Count} waypoints, {p.TotalLength:F1}m total, status={p.Status}");
            }
        }
    }
}