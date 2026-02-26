// File: NavigationPathController.cs
// ============================================================================
//  CONTROLADOR DE RUTA — IndoorNavAR  v2
// ============================================================================
//
//  PROBLEMAS DE v1 RESUELTOS:
//
//  1. AGENTE PEGADO A PAREDES:
//     v1 hacía SamplePosition en cada frame desde la posición actual, lo que
//     snappeaba a la posición más cercana aunque estuviera junto a un borde.
//     Ahora el movimiento sigue el path (ya centrado por el optimizer), y
//     SamplePosition solo valida que no salgamos del NavMesh — no redirige.
//     Eliminada la repulsión reactiva de SphereCast que entraba en conflicto.
//
//  2. FALSA LLEGADA AL DESTINO:
//     v1 declaraba arribo cuando el stall avanzaba artificialmente waypoints
//     hasta el final. Ahora:
//       - Arribo SOLO si distancia física al destino final < destinationArrivalRadius.
//       - Anti-stall recalcula la ruta, nunca avanza waypoints artificialmente.
//       - Si el recálculo falla N veces → OnPathFailed, nunca OnPathCompleted.
//
//  3. RECÁLCULO AGRESIVO ELIMINADO:
//     Eliminado el DeviationWatchRoutine. El recálculo ocurre solo cuando
//     el anti-stall lo determina necesario.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Navigation
{
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class NavigationPathController : MonoBehaviour
    {
        // ─── Inspector — Waypoints ────────────────────────────────────────────

        [Header("Seguimiento de Waypoints")]
        [SerializeField, Range(0.05f, 1f)]
        private float _waypointArrivalRadius = 0.30f;

        [SerializeField, Range(0.20f, 2f)]
        private float _destinationArrivalRadius = 0.50f;

        // ─── Inspector — Movimiento ───────────────────────────────────────────

        [Header("Movimiento")]
        [SerializeField, Range(0.5f, 4f)]
        private float _moveSpeed = 1.4f;

        [SerializeField, Range(60f, 720f)]
        private float _rotationSpeed = 300f;

        [SerializeField, Range(1f, 20f)]
        private float _acceleration = 6f;

        [SerializeField, Range(0.5f, 3f)]
        private float _brakingDistance = 1.2f;

        [SerializeField, Range(0.1f, 0.8f)]
        private float _minBrakingFactor = 0.3f;

        // ─── Inspector — Escaleras ────────────────────────────────────────────

        [Header("Escaleras / Rampas")]
        [SerializeField, Range(0.05f, 1.0f)]
        private float _stairYThreshold = 0.20f;

        [SerializeField, Range(0.1f, 2.0f)]
        private float _stairYSpeed = 0.8f;

        [SerializeField, Range(0.1f, 1.0f)]
        private float _stairWaypointRadius = 0.50f;

        // ─── Inspector — Anti-Stuck ───────────────────────────────────────────

        [Header("Anti-Stuck")]
        [Tooltip("Tiempo (s) sin movimiento antes de considerar atasco.")]
        [SerializeField, Range(2f, 15f)]
        private float _stallTimeoutSeconds = 4.0f;

        [Tooltip("Desplazamiento mínimo por ciclo de stall check (m).")]
        [SerializeField, Range(0.02f, 0.3f)]
        private float _stallMinMovement = 0.08f;

        [Tooltip("Intentos de recálculo antes de declarar NavigationFailed.")]
        [SerializeField, Range(1, 5)]
        private int _maxStallRetries = 3;

        // ─── Inspector — Optimizador ──────────────────────────────────────────

        [Header("Optimizador de Ruta")]
        [SerializeField, Range(1, 3)]
        private int _lookAheadMaxSkip = 2;

        [SerializeField, Range(5f, 35f)]
        private float _funnelAngleThreshold = 20f;

        [SerializeField, Range(0.05f, 0.5f)]
        private float _agentRadius = 0.10f;

        [SerializeField, Range(1.0f, 3.0f)]
        private float _clearanceSafetyFactor = 1.8f;

        [SerializeField, Range(0.3f, 2f)]
        private float _centerSearchRadius = 1.2f;

        [SerializeField, Range(0.3f, 1f)]
        private float _centerPullStrength = 0.65f;

        // ─── Inspector — Debug ────────────────────────────────────────────────

        [Header("Debug")]
        [SerializeField] private bool  _drawGizmos       = true;
        [SerializeField] private Color _pathColor        = Color.cyan;
        [SerializeField] private Color _lookAheadColor   = Color.yellow;
        [SerializeField] private Color _destinationColor = new Color(0f, 1f, 0.3f, 0.8f);
        [SerializeField] private Color _stairColor       = new Color(1f, 0.5f, 0f, 1f);
        [SerializeField] private bool  _logVerbose       = false;

        // ─── Eventos ──────────────────────────────────────────────────────────

        public event Action<Vector3>           OnPathStarted;
        public event Action<int, Vector3>      OnWaypointReached;
        public event Action                    OnPathCompleted;
        public event Action<NavMeshPathStatus> OnPathFailed;
        public event Action<OptimizedPath>     OnPathRecalculated;

        // ─── Propiedades ──────────────────────────────────────────────────────

        public bool          IsNavigating   => _isNavigating;
        public OptimizedPath CurrentPath    => _currentPath;

        public Vector3 CurrentTarget => (_isNavigating && _currentPath != null
                                         && _currentWaypointIndex < _currentPath.Waypoints.Count)
            ? _currentPath.Waypoints[_currentWaypointIndex]
            : transform.position;

        public float RemainingDistance
        {
            get
            {
                if (!_isNavigating || _currentPath == null || !_currentPath.IsValid) return -1f;
                return ComputeRemainingDistance();
            }
        }

        public float CurrentSpeed => _currentSpeed;
        public bool  IsOnStairs   => _isOnStairs;

        // ─── Estado interno ───────────────────────────────────────────────────

        private NavMeshAgent            _agent;
        private NavigationPathOptimizer _optimizer;
        private OptimizedPath           _currentPath;
        private int                     _currentWaypointIndex;
        private bool                    _isNavigating;
        private bool                    _agentReady;
        private Vector3                 _currentDestination;
        private float                   _currentSpeed;   // velocidad escalar actual
        private Vector3                 _smoothDampVel;  // solo para SmoothDamp de dirección
        private bool                    _isOnStairs;

        // Anti-stall
        private Vector3 _lastStallCheckPos;
        private float   _stallTimer;
        private int     _stallRetryCount;

        // Progreso garantizado
        private int _confirmedMinIndex = 1;

        // ─────────────────────────────────────────────────────────────────────
        //  UNITY LIFECYCLE
        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _agent.updatePosition = false;
            _agent.updateRotation = false;

            BuildOptimizer();
            EventBus.Instance?.Subscribe<NavMeshGeneratedEvent>(OnNavMeshRegenerated);
        }

        private void Start()
        {
            _agentReady = true;
            TrySetAgentStopped(true);
        }

        private void OnDestroy()
        {
            EventBus.Instance?.Unsubscribe<NavMeshGeneratedEvent>(OnNavMeshRegenerated);
        }

        private void OnEnable()  { if (_agentReady && _isNavigating) TrySetAgentStopped(false); }
        private void OnDisable() { if (_agentReady && _isNavigating) TrySetAgentStopped(true);  }

        private void Update()
        {
            if (_isNavigating && _currentPath != null)
                FollowPath();
        }

        private void OnValidate() => SyncOptimizerParams();

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA
        // ─────────────────────────────────────────────────────────────────────

        public void NavigateTo(Vector3 destination, bool forceRecalculate = false)
        {
            if (forceRecalculate)
                _optimizer.InvalidateCache();

            _currentDestination = destination;

            OptimizedPath path = _optimizer.ComputeOptimized(transform.position, destination);

            if (!path.IsValid)
            {
                Debug.LogWarning($"[PathController] Sin ruta válida hacia {destination:F2}. Status={path.Status}");
                OnPathFailed?.Invoke(path.Status);
                return;
            }

            _currentPath          = path;
            _currentWaypointIndex = 1;
            _confirmedMinIndex    = 1;
            _isNavigating         = true;
            _isOnStairs           = false;
            _currentSpeed         = 0f;
            _smoothDampVel        = Vector3.zero;
            _lastStallCheckPos    = transform.position;
            _stallTimer           = 0f;
            _stallRetryCount      = 0;

            TrySetAgentStopped(false);

            if (_logVerbose)
                Debug.Log($"[PathController] Ruta: {path.RawWaypointCount} raw → " +
                          $"{path.Waypoints.Count} optimizados, {path.TotalLength:F1}m, " +
                          $"clearance mín={path.MinClearance:F3}m");

            OnPathStarted?.Invoke(destination);
        }

        public void StopNavigation()
        {
            _isNavigating      = false;
            _isOnStairs        = false;
            _currentSpeed      = 0f;
            _smoothDampVel     = Vector3.zero;
            _confirmedMinIndex = 1;
            _stallRetryCount   = 0;

            TrySetAgentStopped(true);
        }

        public void InvalidatePathCache() => _optimizer?.InvalidateCache();

        // ─────────────────────────────────────────────────────────────────────
        //  SEGUIMIENTO DE RUTA
        // ─────────────────────────────────────────────────────────────────────

        private void FollowPath()
        {
            IReadOnlyList<Vector3> waypoints = _currentPath.Waypoints;
            Vector3 finalDest = waypoints[waypoints.Count - 1];

            // LLEGADA AL DESTINO — solo por distancia física real
            float distToFinal = Vector3.Distance(transform.position, finalDest);
            if (distToFinal <= _destinationArrivalRadius)
            {
                Arrive();
                return;
            }

            // ANTI-STALL
            _stallTimer += Time.deltaTime;
            if (_stallTimer >= _stallTimeoutSeconds)
            {
                float moved = Vector3.Distance(transform.position, _lastStallCheckPos);
                if (moved < _stallMinMovement)
                {
                    HandleStall(finalDest);
                    return;
                }
                _lastStallCheckPos = transform.position;
                _stallTimer        = 0f;
                _stallRetryCount   = 0;
            }

            // MODO ESCALERA
            bool nextIsStair = IsStairSegment(waypoints, _currentWaypointIndex);
            _isOnStairs = nextIsStair;

            if (!nextIsStair)
            {
                _currentWaypointIndex = _optimizer.GetLookAheadTarget(
                    transform.position, waypoints, _currentWaypointIndex);
            }

            if (_currentWaypointIndex >= waypoints.Count)
                return; // Siguiente frame comprobará distancia final

            Vector3 target       = waypoints[_currentWaypointIndex];
            float   arrivalRadius = nextIsStair ? _stairWaypointRadius : _waypointArrivalRadius;

            if (Vector3.Distance(transform.position, target) <= arrivalRadius)
            {
                AdvanceWaypoint(waypoints);
                if (!_isNavigating) return;
                target      = waypoints[_currentWaypointIndex];
                nextIsStair = IsStairSegment(waypoints, _currentWaypointIndex);
                _isOnStairs = nextIsStair;
            }

            MoveTowardsTarget(target, nextIsStair, finalDest);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  MOVIMIENTO LIMPIO
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sigue el path optimizado sin re-snappear a bordes.
        /// Velocidad escalar constante con aceleración/deceleración lineal.
        /// SmoothDamp solo se usa para suavizar la dirección, no la velocidad.
        /// </summary>
        private void MoveTowardsTarget(Vector3 target, bool isStair, Vector3 finalDest)
        {
            Vector3 toTarget = target - transform.position;

            // Dirección deseada
            Vector3 desiredDir;
            if (isStair)
            {
                desiredDir = toTarget.sqrMagnitude > 0.001f ? toTarget.normalized : transform.forward;
            }
            else
            {
                Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z);
                desiredDir = flat.sqrMagnitude > 0.001f ? flat.normalized : transform.forward;
            }

            // Velocidad objetivo: constante con frenado cerca del destino final
            float distFinal  = Vector3.Distance(transform.position, finalDest);
            float brakeT     = Mathf.InverseLerp(0f, _brakingDistance, distFinal);
            float targetSpeed = Mathf.Lerp(_moveSpeed * _minBrakingFactor, _moveSpeed, brakeT);
            if (isStair) targetSpeed *= 0.7f;

            // Acelerar/frenar linealmente — sin feedback loop
            _currentSpeed = Mathf.MoveTowards(_currentSpeed, targetSpeed,
                                               _acceleration * Time.deltaTime);

            // Suavizado de DIRECCIÓN solamente (no velocidad)
            // SmoothDamp en dirección normalizada previene giros bruscos
            Vector3 smoothDir = Vector3.SmoothDamp(
                transform.forward, desiredDir,
                ref _smoothDampVel,
                0.08f,           // tiempo de suavizado de dirección (s)
                float.MaxValue,  // sin límite de velocidad de giro de la dirección
                Time.deltaTime);

            smoothDir.y = isStair ? desiredDir.y : 0f;

            if (smoothDir.sqrMagnitude < 0.001f) return;
            smoothDir = smoothDir.normalized;

            // Desplazamiento final: dirección × velocidad escalar
            Vector3 delta  = smoothDir * (_currentSpeed * Time.deltaTime);
            if (isStair) delta.y = desiredDir.y * _stairYSpeed * Time.deltaTime;

            Vector3 newPos = transform.position + delta;

            // Validación conservadora: radio pequeño para no snappear a bordes
            if (!NavMesh.SamplePosition(newPos, out NavMeshHit hit, 0.15f, NavMesh.AllAreas))
            {
                if (!NavMesh.SamplePosition(newPos, out hit, 0.40f, NavMesh.AllAreas))
                    return;
            }

            transform.position  = hit.position;
            _agent.nextPosition = hit.position;

            // Rotación
            Vector3 rotDir = new Vector3(smoothDir.x, 0f, smoothDir.z);
            if (rotDir.sqrMagnitude > 0.01f)
            {
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    Quaternion.LookRotation(rotDir),
                    _rotationSpeed * Time.deltaTime);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ANTI-STALL — SIN AVANCE ARTIFICIAL
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Recalcula la ruta desde la posición actual.
        /// Si supera _maxStallRetries → OnPathFailed.
        /// NUNCA declara arribo por timeout.
        /// </summary>
        private void HandleStall(Vector3 finalDest)
        {
            _stallRetryCount++;
            _stallTimer        = 0f;
            _lastStallCheckPos = transform.position;

            if (_logVerbose)
                Debug.LogWarning($"[PathController] ⚠️ Stall #{_stallRetryCount}/{_maxStallRetries}, " +
                                 $"dist.destino={Vector3.Distance(transform.position, finalDest):F2}m");

            if (_stallRetryCount > _maxStallRetries)
            {
                Debug.LogError($"[PathController] ❌ Destino inalcanzable tras {_maxStallRetries} intentos.");
                StopNavigation();
                OnPathFailed?.Invoke(NavMeshPathStatus.PathPartial);
                return;
            }

            _optimizer.InvalidateCache();
            OptimizedPath newPath = _optimizer.ComputeOptimized(transform.position, _currentDestination);

            if (!newPath.IsValid)
            {
                Debug.LogError("[PathController] ❌ Recálculo de emergencia inválido.");
                StopNavigation();
                OnPathFailed?.Invoke(NavMeshPathStatus.PathInvalid);
                return;
            }

            _currentPath          = newPath;
            _currentWaypointIndex = 1;
            _confirmedMinIndex    = 1;
            _currentSpeed         = 0f;
            _smoothDampVel        = Vector3.zero;
            _isOnStairs           = false;

            if (_logVerbose)
                Debug.Log($"[PathController] 🔄 Ruta recalculada: {newPath.Waypoints.Count} wp, " +
                          $"clearance={newPath.MinClearance:F3}m");

            OnPathRecalculated?.Invoke(newPath);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────────────────

        private void AdvanceWaypoint(IReadOnlyList<Vector3> waypoints)
        {
            OnWaypointReached?.Invoke(_currentWaypointIndex, waypoints[_currentWaypointIndex]);
            _currentWaypointIndex++;
            _confirmedMinIndex = _currentWaypointIndex;
            _stallTimer        = 0f;
            _lastStallCheckPos = transform.position;
        }

        private void Arrive()
        {
            StopNavigation();
            if (_logVerbose) Debug.Log("[PathController] ✅ Destino alcanzado.");
            OnPathCompleted?.Invoke();
        }

        private bool IsStairSegment(IReadOnlyList<Vector3> waypoints, int index)
        {
            if (index <= 0 || index >= waypoints.Count) return false;
            float dY = Mathf.Abs(waypoints[index].y - waypoints[index - 1].y);
            if (dY >= _stairYThreshold) return true;
            if (index + 1 < waypoints.Count)
            {
                float dY2 = Mathf.Abs(waypoints[index + 1].y - waypoints[index].y);
                if (dY2 >= _stairYSpeed) return true;
            }
            return false;
        }

        private float ComputeRemainingDistance()
        {
            IReadOnlyList<Vector3> wp = _currentPath.Waypoints;
            if (_currentWaypointIndex >= wp.Count) return 0f;
            float dist = Vector3.Distance(transform.position, wp[_currentWaypointIndex]);
            for (int i = _currentWaypointIndex; i < wp.Count - 1; i++)
                dist += Vector3.Distance(wp[i], wp[i + 1]);
            return dist;
        }

        private void TrySetAgentStopped(bool stopped)
        {
            if (_agent == null || !_agent.isOnNavMesh) return;
            try   { _agent.isStopped = stopped; }
            catch (Exception e)
            { Debug.LogWarning($"[PathController] TrySetAgentStopped ignorado: {e.Message}"); }
        }

        private void OnNavMeshRegenerated(NavMeshGeneratedEvent evt)
        {
            if (!evt.Success) return;
            _optimizer.InvalidateCache();
            if (_isNavigating && !_isOnStairs)
                NavigateTo(_currentDestination, forceRecalculate: true);
        }

        private void BuildOptimizer()
        {
            _optimizer = new NavigationPathOptimizer();
            SyncOptimizerParams();
        }

        private void SyncOptimizerParams()
        {
            if (_optimizer == null) return;
            _optimizer.AgentRadius          = _agentRadius;
            _optimizer.SafetyFactor         = _clearanceSafetyFactor;
            _optimizer.CenterPullStrength   = _centerPullStrength;
            _optimizer.CenterSearchRadius   = _centerSearchRadius;
            _optimizer.FunnelAngleThreshold = _funnelAngleThreshold;
            _optimizer.LookAheadMaxSkip     = _lookAheadMaxSkip;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GIZMOS
        // ─────────────────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (!_drawGizmos || _currentPath == null || !_currentPath.IsValid) return;
            IReadOnlyList<Vector3> wp = _currentPath.Waypoints;

            for (int i = 0; i < wp.Count - 1; i++)
            {
                bool stair = IsStairSegment(wp, i + 1);
                Gizmos.color = stair ? _stairColor : _pathColor;
                Gizmos.DrawLine(wp[i], wp[i + 1]);
                Gizmos.DrawWireSphere(wp[i], stair ? 0.08f : 0.05f);
            }

            Gizmos.color = _destinationColor;
            if (wp.Count > 0) Gizmos.DrawWireSphere(wp[wp.Count - 1], 0.15f);

            if (_isNavigating && _currentWaypointIndex < wp.Count)
            {
                Gizmos.color = _isOnStairs ? _stairColor : _lookAheadColor;
                Gizmos.DrawLine(transform.position, wp[_currentWaypointIndex]);
                Gizmos.DrawWireSphere(wp[_currentWaypointIndex], 0.10f);
            }
        }
    }
}