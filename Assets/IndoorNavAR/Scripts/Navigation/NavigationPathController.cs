// File: NavigationPathController.cs
// ============================================================================
//  CONTROLADOR DE RUTA — IndoorNavAR  v3
// ============================================================================
//
//  CAMBIOS v2 → v3  (soporte FullAR)
// ============================================================================
//
//  PROBLEMA CRÍTICO:
//    En FullAR, NavigationPathController.Update() llamaba FollowPath() cada
//    frame, que ejecutaba MoveTowardsTarget() → transform.position = hit.position.
//
//    Esto movía el agente hacia el destino DIRECTAMENTE en el transform,
//    sin pasar por NavMeshAgent.isStopped (que solo controla el componente
//    de navegación de Unity, no el movimiento manual del transform).
//
//    Resultado: el agente caminaba solo hacia el destino en FullAR aunque
//    AROriginAligner, NavigationAgent y ARGuideController tuvieran
//    isStopped = true. El movimiento del transform bypaseaba todo.
//
//  SOLUCIÓN v3:
//    1. Nueva propiedad pública IsFullARMode (bool, default false).
//       Debe setearse a true ANTES de llamar NavigateTo() en FullAR.
//
//    2. FollowPath() retorna inmediatamente si IsFullARMode = true.
//       El path se calcula y CurrentPath queda válido (NavigationVoiceGuide
//       lo necesita para generar instrucciones), pero el agente NO se mueve.
//
//    3. AROriginAligner llama SetFullARMode(true) en InitializeCapabilityRoutine().
//       NavigationAgent llama SetFullARMode(true) en Start() si detecta FullAR.
//
//    4. En FullAR, IsNavigating sigue siendo true mientras haya ruta activa.
//       Esto permite que NavigationVoiceGuide evalúe la posición del usuario
//       (que AROriginAligner sincroniza con la cámara cada frame) contra la
//       ruta y genere instrucciones de giro, distancia, etc.
//
//    5. StopNavigation() no cambia IsFullARMode — solo limpia la ruta.
//
//  TODOS LOS FIXES DE v2 SE CONSERVAN ÍNTEGRAMENTE.

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

        /// <summary>
        /// ✅ v3 — En FullAR, FollowPath() no mueve el transform.
        /// El path se calcula y CurrentPath queda válido para VoiceGuide,
        /// pero MoveTowardsTarget() nunca se ejecuta.
        ///
        /// Setear a true ANTES de llamar NavigateTo() en FullAR.
        /// AROriginAligner y NavigationAgent son responsables de esto.
        /// </summary>
        public bool IsFullARMode { get; private set; } = false;

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
        private float                   _currentSpeed;
        private Vector3                 _smoothDampVel;
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

        /// <summary>
        /// ✅ v3 — Activa/desactiva el modo FullAR.
        /// En FullAR FollowPath() calcula posición de waypoints pero
        /// NO mueve el transform. El agente permanece donde AROriginAligner
        /// lo dejó (posición del usuario sobre el NavMesh).
        /// </summary>
        public void SetFullARMode(bool isFullAR)
        {
            if (IsFullARMode == isFullAR) return;
            IsFullARMode = isFullAR;

            if (isFullAR)
            {
                // En FullAR el agente no debe moverse: detener cualquier movimiento activo
                _currentSpeed  = 0f;
                _smoothDampVel = Vector3.zero;
                TrySetAgentStopped(true);
                if (_logVerbose)
                    Debug.Log("[PathController] ✅ Modo FullAR activado — " +
                              "FollowPath() no moverá el transform.");
            }
            else
            {
                if (_logVerbose)
                    Debug.Log("[PathController] ✅ Modo NoAR activado — " +
                              "movimiento del agente habilitado.");
            }
        }

        public void NavigateTo(Vector3 destination, bool forceRecalculate = false)
        {
            if (forceRecalculate)
                _optimizer.InvalidateCache();

            _currentDestination = destination;

            OptimizedPath path = _optimizer.ComputeOptimized(transform.position, destination);

            if (!path.IsValid)
            {
                Debug.LogWarning($"[PathController] Sin ruta válida hacia {destination:F2}. " +
                                 $"Status={path.Status} | agentPos={transform.position:F2}");
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

            // En FullAR no activamos el movimiento del agente
            if (!IsFullARMode)
                TrySetAgentStopped(false);

            if (_logVerbose)
                Debug.Log($"[PathController] Ruta: {path.RawWaypointCount} raw → " +
                          $"{path.Waypoints.Count} optimizados, {path.TotalLength:F1}m, " +
                          $"clearance mín={path.MinClearance:F3}m" +
                          (IsFullARMode ? " [FullAR — sin movimiento]" : ""));

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
            // ✅ v3 — En FullAR, el transform no se mueve.
            // El path existe y CurrentPath.IsValid = true para que
            // NavigationVoiceGuide pueda evaluar la ruta.
            // AROriginAligner es el único que mueve el transform en FullAR.
            if (IsFullARMode)
            {
                // En FullAR: verificar llegada basada en la posición actual del agente
                // (que AROriginAligner sincroniza con la cámara).
                // No se avanza waypoints, no se mueve el transform.
                // NavigationVoiceGuide usa EvalPos para evaluar la ruta, no este sistema.
                return;
            }

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
                return;

            Vector3 target        = waypoints[_currentWaypointIndex];
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
        //  MOVIMIENTO (solo NoAR)
        // ─────────────────────────────────────────────────────────────────────

        private void MoveTowardsTarget(Vector3 target, bool isStair, Vector3 finalDest)
        {
            // Nunca llamado en FullAR (FollowPath retorna antes)
            Vector3 toTarget = target - transform.position;

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

            float distFinal   = Vector3.Distance(transform.position, finalDest);
            float brakeT      = Mathf.InverseLerp(0f, _brakingDistance, distFinal);
            float targetSpeed = Mathf.Lerp(_moveSpeed * _minBrakingFactor, _moveSpeed, brakeT);
            if (isStair) targetSpeed *= 0.7f;

            _currentSpeed = Mathf.MoveTowards(_currentSpeed, targetSpeed,
                                               _acceleration * Time.deltaTime);

            Vector3 smoothDir = Vector3.SmoothDamp(
                transform.forward, desiredDir,
                ref _smoothDampVel,
                0.08f,
                float.MaxValue,
                Time.deltaTime);

            smoothDir.y = isStair ? desiredDir.y : 0f;

            if (smoothDir.sqrMagnitude < 0.001f) return;
            smoothDir = smoothDir.normalized;

            Vector3 delta = smoothDir * (_currentSpeed * Time.deltaTime);
            if (isStair) delta.y = desiredDir.y * _stairYSpeed * Time.deltaTime;

            Vector3 newPos = transform.position + delta;

            if (!NavMesh.SamplePosition(newPos, out NavMeshHit hit, 0.15f, NavMesh.AllAreas))
            {
                if (!NavMesh.SamplePosition(newPos, out hit, 0.40f, NavMesh.AllAreas))
                    return;
            }

            transform.position  = hit.position;
            _agent.nextPosition = hit.position;

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
        //  ANTI-STALL (solo NoAR)
        // ─────────────────────────────────────────────────────────────────────

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
            if (_isNavigating && !_isOnStairs && !IsFullARMode)
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
                Gizmos.color = IsFullARMode ? Color.magenta : (_isOnStairs ? _stairColor : _lookAheadColor);
                Gizmos.DrawLine(transform.position, wp[_currentWaypointIndex]);
                Gizmos.DrawWireSphere(wp[_currentWaypointIndex], 0.10f);
            }

            // En FullAR: indicador visual del modo
            if (IsFullARMode && Application.isPlaying)
            {
                Gizmos.color = new Color(1f, 0f, 1f, 0.5f);
                Gizmos.DrawWireSphere(transform.position, 0.2f);
            }
        }
    }
}