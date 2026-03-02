// File: NavigationAgent.cs

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
        [Header("Multi-Nivel")]
        [SerializeField] private bool _detectFloorTransitions = true;

        [Header("Eventos")]
        [SerializeField] private bool _publishEvents = true;

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

        // ✅ Guarda el nombre del waypoint de destino para NavigationArrivedEvent
        private string _lastDestinationName = string.Empty;

        // ─── Lifecycle ────────────────────────────────────────────────────────

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
            if (_detectFloorTransitions) UpdateCurrentLevel();
        }

        // ─── API pública — Navegación ─────────────────────────────────────────

        public void StartNavigation(Vector3 destination)
        {
            LastDestination      = destination;
            _lastDestinationName = string.Empty; // posición directa, sin nombre de waypoint
            if (_logVerbose) Debug.Log($"[NavigationAgent] StartNavigation → {destination:F2}");
            _pathController.NavigateTo(destination);
        }

        public bool NavigateToWaypoint(WaypointData waypoint)
        {
            if (waypoint == null)
            {
                Debug.LogWarning("[NavigationAgent] NavigateToWaypoint: waypoint es null.");
                return false;
            }

            LastDestination      = waypoint.Position;
            _lastDestinationName = waypoint.WaypointName; // ✅ guardado aquí

            if (_logVerbose)
                Debug.Log($"[NavigationAgent] NavigateToWaypoint → {waypoint.WaypointName} @ {waypoint.Position:F2}");

            _pathController.NavigateTo(waypoint.Position);
            return _pathController.IsNavigating;
        }

        public void SetDestination(Vector3 newDestination)
        {
            LastDestination      = newDestination;
            _lastDestinationName = string.Empty;
            if (_logVerbose) Debug.Log($"[NavigationAgent] SetDestination → {newDestination:F2}");
            _pathController.NavigateTo(newDestination, forceRecalculate: true);
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
            _navAgent.Warp(hit.position);

            if (_logVerbose) Debug.Log($"[NavigationAgent] Teleport exitoso a {hit.position:F2}");
            return true;
        }

        // ─── Detección de nivel ───────────────────────────────────────────────

        private void UpdateCurrentLevel()
        {
            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            if (startPoints.Count == 0) return;

            float agentY   = transform.position.y;
            int   bestLevel = 0;
            float bestDist  = float.MaxValue;

            foreach (var pt in startPoints)
            {
                float dist = Mathf.Abs(agentY - pt.FloorHeight);
                if (dist < bestDist) { bestDist = dist; bestLevel = pt.Level; }
            }

            if (bestLevel == CurrentLevel) return;

            int prev  = CurrentLevel;
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
                    DestinationWaypointId = string.Empty,
                    StartPosition         = transform.position,
                    DestinationPosition   = destination,
                    EstimatedDistance     = Vector3.Distance(transform.position, destination)
                });
        }

        private void HandlePathCompleted()
        {
            OnArrived?.Invoke();

            // ✅ Notifica a Flutter con el nombre del waypoint para TTS de llegada
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
            Debug.LogWarning($"[NavigationAgent] Navegación fallida. Status={status}. Destino: {LastDestination:F2}");
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
            Debug.Log($"[NavigationAgent] IsNavigating={IsNavigating}, " +
                      $"Level={CurrentLevel}, Remaining={RemainingDistance:F2}m, " +
                      $"Progress={ProgressPercent * 100f:F0}%, Speed={CurrentSpeed:F2}m/s");

            if (_pathController?.CurrentPath != null)
            {
                var p = _pathController.CurrentPath;
                Debug.Log($"  Path: {p.Waypoints.Count} waypoints, {p.TotalLength:F1}m total, status={p.Status}");
            }
        }
    }
}