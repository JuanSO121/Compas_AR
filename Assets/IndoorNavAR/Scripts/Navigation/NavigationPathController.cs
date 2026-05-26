// File: NavigationPathController.cs
// ✅ v5.4 — FIX_VIO: throttle de agentSync elevado durante inicialización AR
//
// ============================================================================
//  CAMBIOS v5.3 → v5.4
// ============================================================================
//
//  FIX_VIO G — AgentSyncInterval elevado durante los primeros 10s post-init
//  ──────────────────────────────────────────────────────────────────────────
//  PROBLEMA v5.3:
//    _agentSyncInterval (0.05s = 20Hz) era insuficiente para evitar la
//    competencia con el VIO durante el arranque. NavMesh.SamplePosition()
//    y SyncAgentToUserPosition() corriendo 20 veces/segundo en los primeros
//    segundos después de que ARCore inicia el tracking causaba CPU starvation
//    en el pipeline de sensores IMU.
//
//    PersistenceManager.WaitForVIOStableBeforeHeavyWork() tiene polling de
//    300ms pero no detiene NavigationPathController si ya había navegación
//    activa de una sesión restaurada.
//
//  FIX:
//    _vioWarmupDuration (default 10s): durante los primeros 10s desde Start(),
//    _agentSyncInterval efectivo se eleva a _agentSyncIntervalDuringWarmup (0.15s).
//    Después del warmup, vuelve al valor normal (_agentSyncInterval = 0.05s).
//
//    Esto reduce SyncAgentToUserPosition a ~7Hz durante el arranque del VIO,
//    aliviando el CPU starvation sin afectar la experiencia de navegación
//    (el usuario apenas empieza a moverse en los primeros 10s).
//
//  TODO LO DEMÁS ES IDÉNTICO A v5.3.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Data;

namespace IndoorNavAR.Navigation
{
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class NavigationPathController : MonoBehaviour
    {
        // ─── Inspector — Waypoints ────────────────────────────────────────────

        [Header("Seguimiento de Waypoints")]
        [SerializeField, Range(0.05f, 1f)]
        private float _waypointArrivalRadius = 0.33f;

        [SerializeField, Range(0.20f, 2f)]
        private float _destinationArrivalRadius = 0.50f;

        [Header("─── Recálculo Autónomo (FullAR) ──────────────────────────────────")]
        [SerializeField] private float _autoRerouteDeviationThreshold = 2.5f;
        [SerializeField] private float _autoRerouteCooldown           = 8.0f;
        [SerializeField] private float _stallCheckInterval            = 1.0f;
        [SerializeField] private float _stallMinMovement              = 0.3f;
        [SerializeField] private float _stallTimeout                  = 5.0f;

        [Header("─── v5.2: Recálculo por desviación (cooldown independiente) ──────")]
        [SerializeField] private float _deviationRerouteCooldown = 5.0f;
        private float _lastDeviationRerouteTime = -999f;

        [Header("─── v5.3 FIX D — Off-route recalculation mejorado ────────────────")]
        [SerializeField, Range(1, 10)]
        private int _deviationConfirmFrames = 3;
        private int _deviationConsecutiveFrames = 0;

        [SerializeField, Range(0.05f, 1.0f)]
        private float _deviationCheckInterval = 0.25f;
        private float _deviationCheckTimer = 0f;

        private float _lastAutoRerouteTime   = -999f;
        private float _stallAccumTime        = 0f;
        private float _stallCheckTimer       = 0f;
        private Vector3 _stallRefPos         = Vector3.zero;
        private bool  _stallRefInitialized   = false;
        private UserPositionBridge _userBridge;

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

        [SerializeField] private float _stairHeightThreshold = 0.3f;

        // ─── Inspector — Escaleras ────────────────────────────────────────────

        [Header("Escaleras / Rampas")]
        [SerializeField, Range(0.05f, 1.0f)]
        private float _stairYThreshold = 0.20f;

        [SerializeField, Range(0.1f, 2.0f)]
        private float _stairYSpeed = 0.8f;

        [SerializeField, Range(0.1f, 1.0f)]
        private float _stairWaypointRadius = 0.50f;

        // ─── Inspector — Anti-Stuck ───────────────────────────────────────────

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

        // ─── Inspector — FullAR ───────────────────────────────────────────────

        [Header("FullAR — Origen de ruta")]
        [SerializeField, Range(1f, 5f)]
        private float _fullAROriginSnapRadius = 3.0f;

        [SerializeField, Range(0.5f, 2f)]
        private float _fullAROriginFloorTolerance = 1.2f;

        // ─── Inspector — FullAR sincronización continua ───────────────────────

        [Header("FullAR — Sincronización continua del agente")]
        [SerializeField, Range(0.05f, 0.5f)]
        private float _agentSyncThreshold = 0.15f;

        [SerializeField]
        private bool _continuousAgentSync = true;

        [Header("─── v5.3 FIX E — ARCore mapping throttle ───────────────────────")]
        [Tooltip("Intervalo entre sincronizaciones de agente en operación normal (s).\n" +
                 "Default: 0.05s (20Hz).")]
        [SerializeField, Range(0.016f, 0.2f)]
        private float _agentSyncInterval = 0.05f;
        private float _agentSyncTimer = 0f;

        [Header("─── v5.4 FIX_VIO G — Warmup throttle ───────────────────────────")]
        [Tooltip("✅ v5.4 — Duración (s) del período de warmup AR desde Start().\n" +
                 "Durante este período, agentSyncInterval efectivo = _agentSyncIntervalDuringWarmup.\n" +
                 "Default: 10s — cubre la inicialización del VIO en dispositivos lentos.")]
        [SerializeField, Range(5f, 30f)]
        private float _vioWarmupDuration = 10f;

        [Tooltip("✅ v5.4 — Intervalo entre sincronizaciones durante warmup (s).\n" +
                 "Default: 0.15s (~7Hz) — reduce CPU starvation del VIO al arrancar.\n" +
                 "Vuelve a _agentSyncInterval tras _vioWarmupDuration.")]
        [SerializeField, Range(0.05f, 0.5f)]
        private float _agentSyncIntervalDuringWarmup = 0.15f;

        private float _startTime = 0f;    // ✅ v5.4: registrado en Start()

        // ─── Inspector — v5.2 Filtro VIO ─────────────────────────────────────

        [Header("FullAR — Filtro VIO anti-jitter (v5.2 + v5.3)")]
        [SerializeField, Range(0.1f, 1.0f)]
        private float _vioJumpThreshold = 0.4f;

        [SerializeField, Range(0.05f, 1.0f)]
        private float _vioSmoothFactor = 0.3f;

        [Header("─── v5.3 FIX F — Filtro de drift Y (altura) ────────────────────")]
        [SerializeField, Range(0.3f, 3.0f)]
        private float _maxYDrift = 0.8f;

        [SerializeField, Range(0.05f, 1.0f)]
        private float _vioYSmoothFactor = 0.15f;

        private Vector3 _smoothedUserPos = new Vector3(float.PositiveInfinity, 0, 0);
        private bool    _smoothedUserPosInitialized = false;

        // ─── Inspector — FloorTransition histéresis ───────────────────────────

        [Header("FloorTransition — Histéresis (v5.2)")]
        [SerializeField, Range(0.3f, 2.0f)]
        private float _floorTransitionMinDelta = 0.8f;

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

        public bool          IsNavigating => _isNavigating;
        public OptimizedPath CurrentPath  => _currentPath;
        public bool          IsFullARMode { get; private set; } = false;

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

        // ✅ v5.4: Intervalo efectivo según warmup
        private float EffectiveAgentSyncInterval =>
            (Time.realtimeSinceStartup - _startTime) < _vioWarmupDuration
                ? _agentSyncIntervalDuringWarmup
                : _agentSyncInterval;

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

        private Vector3 _lastStallCheckPos;
        private float   _stallTimer;
        private int     _stallRetryCount;
        private int     _confirmedMinIndex = 1;

        private IndoorNavAR.AR.AROriginAligner _arOriginAlignerCache    = null;
        private bool                           _arOriginAlignerSearched = false;
        private float                          _arAlignerNextRetryTime  = 0f;
        private const float                    _arAlignerRetryInterval  = 5f;

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
            _startTime  = Time.realtimeSinceStartup;   // ✅ v5.4: registrar tiempo de inicio
            TrySetAgentStopped(true);

            _userBridge = FindFirstObjectByType<UserPositionBridge>(FindObjectsInactive.Include);
            EventBus.Instance?.Subscribe<RouteDeviatedEvent>(OnRouteDeviated);

            float warmupRemaining = _vioWarmupDuration;
            Debug.Log($"[PathController v5.4] ✅ Start — VIO warmup activo por {warmupRemaining}s " +
                      $"(syncInterval efectivo: {_agentSyncIntervalDuringWarmup}s → {_agentSyncInterval}s)");
        }

        private void OnDestroy()
        {
            EventBus.Instance?.Unsubscribe<RouteDeviatedEvent>(OnRouteDeviated);
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
        //  v5.2 — Recálculo por desviación
        // ─────────────────────────────────────────────────────────────────────

        private void OnRouteDeviated(RouteDeviatedEvent evt)
        {
            if (!IsNavigating) return;

            if (Time.time - _lastDeviationRerouteTime < _deviationRerouteCooldown)
            {
                if (_logVerbose)
                    Debug.Log($"[PathController] ⏳ Recálculo por desviación ignorado " +
                              $"(cooldown={_deviationRerouteCooldown}s).");
                return;
            }

            _lastDeviationRerouteTime = Time.time;
            _lastAutoRerouteTime      = Time.time;

            Debug.Log($"[PathController] 🔄 Recálculo por RouteDeviatedEvent: " +
                      $"desviación={evt.DeviationDistance:F2}m");

            NavigateTo(evt.Destination, forceRecalculate: true);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA
        // ─────────────────────────────────────────────────────────────────────

        public void SetFullARMode(bool isFullAR)
        {
            if (IsFullARMode == isFullAR) return;
            IsFullARMode = isFullAR;

            if (isFullAR)
            {
                _currentSpeed  = 0f;
                _smoothDampVel = Vector3.zero;
                _smoothedUserPosInitialized = false;
                _deviationConsecutiveFrames = 0;
                _deviationCheckTimer = 0f;
                _agentSyncTimer = 0f;
                TrySetAgentStopped(true);
                if (_logVerbose)
                    Debug.Log("[PathController] ✅ Modo FullAR activado.");
            }
        }

        public void NavigateTo(Vector3 destination, bool forceRecalculate = false)
        {
            bool wasNavigating = _isNavigating;

            if (forceRecalculate)
                _optimizer.InvalidateCache();

            _currentDestination = destination;

            Vector3 routeOrigin = GetRouteOriginForFullAR();

            if (IsFullARMode)
                _optimizer.InvalidateCache();

            OptimizedPath path = _optimizer.ComputeOptimized(routeOrigin, destination);

            if (!path.IsValid)
            {
                Debug.LogWarning($"[PathController] Sin ruta válida hacia {destination:F2}. " +
                                 $"Status={path.Status} | origen={routeOrigin:F2}");
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
            _stallRefInitialized  = false;
            _stallAccumTime       = 0f;
            _deviationConsecutiveFrames = 0;
            _deviationCheckTimer  = 0f;

            if (!IsFullARMode)
                TrySetAgentStopped(false);

            Debug.Log($"[PathController] Ruta: {path.RawWaypointCount} raw → " +
                      $"{path.Waypoints.Count} optimizados, {path.TotalLength:F1}m");

            if (forceRecalculate && wasNavigating)
                OnPathRecalculated?.Invoke(_currentPath);
            else
                OnPathStarted?.Invoke(destination);
        }

        public bool NavigateToWaypointForced(WaypointData waypoint)
        {
            if (waypoint == null) return false;
            NavigateTo(waypoint.Position, forceRecalculate: true);
            return _currentPath != null && _currentPath.IsValid;
        }

        public void StopNavigation()
        {
            _isNavigating      = false;
            _isOnStairs        = false;
            _currentSpeed      = 0f;
            _smoothDampVel     = Vector3.zero;
            _confirmedMinIndex = 1;
            _stallRetryCount   = 0;
            _stallRefInitialized = false;
            _deviationConsecutiveFrames = 0;

            TrySetAgentStopped(true);
        }

        public void InvalidatePathCache() => _optimizer?.InvalidateCache();

        // ─────────────────────────────────────────────────────────────────────
        //  ORIGEN DE RUTA EN FULLAR
        // ─────────────────────────────────────────────────────────────────────

        private Vector3 GetRouteOriginForFullAR()
        {
            if (!IsFullARMode)
                return transform.position;

            var userBridge = UserPositionBridge.Instance;
            if (userBridge == null)
                return transform.position;

            Vector3 userPos    = GetSmoothedUserPosition(userBridge.UserPosition);
            Vector3 routeOrigin;

            if (TryGetFloorProjection(userPos, out Vector3 floorOrigin))
            {
                // Radios en orden ASCENDENTE para break temprano (FIX E)
                float[] radii = { 0.3f, 0.8f, 1.5f, _fullAROriginSnapRadius };
                bool found = false;
                routeOrigin = floorOrigin;

                foreach (float radius in radii)
                {
                    if (NavMesh.SamplePosition(floorOrigin, out NavMeshHit hit, radius, NavMesh.AllAreas))
                    {
                        routeOrigin = hit.position;
                        found = true;
                        break;
                    }
                }

                if (!found)
                    routeOrigin = GetFallbackOrigin(userPos);
            }
            else
            {
                routeOrigin = GetFallbackOrigin(userPos);
            }

            WarpAgentToOrigin(routeOrigin);
            return routeOrigin;
        }

        private void WarpAgentToOrigin(Vector3 origin)
        {
            var userBridge = UserPositionBridge.Instance;
            float correctY = transform.position.y;
            if (userBridge != null) correctY = userBridge.UserPosition.y;

            Vector3 correctedOrigin = new Vector3(origin.x, correctY, origin.z);

            if (Vector3.Distance(transform.position, correctedOrigin) < _agentSyncThreshold)
                return;

            transform.position = correctedOrigin;

            if (_agent != null && _agent.isOnNavMesh)
                _agent.Warp(correctedOrigin);
        }

        private Vector3 GetFallbackOrigin(Vector3 userPos)
        {
            Vector3 groundPos     = new Vector3(userPos.x, userPos.y - 2f, userPos.z);
            float[] fallbackRadii = { 0.5f, 1.0f, 2.0f, _fullAROriginSnapRadius };

            foreach (float radius in fallbackRadii)
            {
                if (NavMesh.SamplePosition(groundPos, out NavMeshHit hit, radius, NavMesh.AllAreas))
                    return hit.position;
            }

            if (NavMesh.SamplePosition(userPos, out NavMeshHit lastResort,
                _fullAROriginSnapRadius * 2f, NavMesh.AllAreas))
                return lastResort.position;

            return transform.position;
        }

        private bool TryGetFloorProjection(Vector3 userPos, out Vector3 floorProjection)
        {
            floorProjection = userPos;
            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            if (startPoints == null || startPoints.Count == 0) return false;

            const float kMaxEyeToFloor = 3.0f;
            NavigationStartPoint bestFloor = null;
            float                bestDelta = float.MaxValue;

            foreach (var sp in startPoints)
            {
                if (!sp.DefinesFloorHeight) continue;
                float deltaY = userPos.y - sp.FloorHeight;
                if (deltaY < 0f || deltaY > kMaxEyeToFloor) continue;
                if (deltaY < bestDelta) { bestDelta = deltaY; bestFloor = sp; }
            }

            if (bestFloor == null) return false;

            floorProjection = new Vector3(userPos.x, bestFloor.FloorHeight, userPos.z);
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  v5.2 + v5.3 FIX B+F — Filtro de posición VIO
        // ─────────────────────────────────────────────────────────────────────

        private Vector3 GetSmoothedUserPosition(Vector3 rawUserPos)
        {
            if (!_smoothedUserPosInitialized)
            {
                _smoothedUserPos = rawUserPos;
                _smoothedUserPosInitialized = true;
                return rawUserPos;
            }

            float dxz = new Vector2(rawUserPos.x - _smoothedUserPos.x,
                                    rawUserPos.z - _smoothedUserPos.z).magnitude;
            float dy  = Mathf.Abs(rawUserPos.y - _smoothedUserPos.y);

            float smoothX = dxz > _vioJumpThreshold
                ? Mathf.Lerp(_smoothedUserPos.x, rawUserPos.x, _vioSmoothFactor)
                : rawUserPos.x;
            float smoothZ = dxz > _vioJumpThreshold
                ? Mathf.Lerp(_smoothedUserPos.z, rawUserPos.z, _vioSmoothFactor)
                : rawUserPos.z;

            float smoothY;
            if (dy > _maxYDrift)
                smoothY = _smoothedUserPos.y;
            else if (dy > _vioJumpThreshold)
                smoothY = Mathf.Lerp(_smoothedUserPos.y, rawUserPos.y, _vioYSmoothFactor);
            else
                smoothY = rawUserPos.y;

            _smoothedUserPos = new Vector3(smoothX, smoothY, smoothZ);
            return _smoothedUserPos;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SEGUIMIENTO DE RUTA
        // ─────────────────────────────────────────────────────────────────────

        private void FollowPath()
        {
            if (!IsFullARMode)
            {
                var aligner = GetOrFindAROriginAligner();
                if (aligner != null && aligner.IsFullARMode)
                {
                    SetFullARMode(true);
                    Debug.Log("[PathController] ⚡ Auto-corrección FullAR en FollowPath().");
                }
            }

            if (IsFullARMode)
            {
                // ✅ v5.4 FIX_VIO G: Usar intervalo efectivo según warmup
                _agentSyncTimer += Time.deltaTime;
                if (_agentSyncTimer >= EffectiveAgentSyncInterval)
                {
                    _agentSyncTimer = 0f;
                    SyncAgentToUserPosition();
                }

                _deviationCheckTimer += Time.deltaTime;
                if (_deviationCheckTimer >= _deviationCheckInterval)
                {
                    _deviationCheckTimer = 0f;
                    CheckDeviationInFullAR();
                }

                HandleStall(Time.deltaTime);
                return;
            }

            // ── Modo NoAR ──────────────────────────────────────────────────────

            IReadOnlyList<Vector3> waypoints = _currentPath.Waypoints;
            Vector3 finalDest = waypoints[waypoints.Count - 1];

            float distToFinal = Vector3.Distance(transform.position, finalDest);
            if (distToFinal <= _destinationArrivalRadius)
            {
                Arrive();
                return;
            }

            _stallTimer += Time.deltaTime;
            if (_stallTimer >= _stallTimeout)
            {
                float moved = Vector3.Distance(transform.position, _lastStallCheckPos);
                if (moved < _stallMinMovement)
                {
                    HandleStall(Time.deltaTime);
                    return;
                }
                _lastStallCheckPos = transform.position;
                _stallTimer = 0f;
                _stallRetryCount = 0;
            }

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
        //  v5.3 FIX D — CheckDeviationInFullAR
        // ─────────────────────────────────────────────────────────────────────

        private void CheckDeviationInFullAR()
        {
            if (_currentPath == null || !_currentPath.IsValid) return;

            Vector3 userPos = _userBridge != null
                ? _userBridge.UserPosition
                : (Camera.main != null ? Camera.main.transform.position : transform.position);

            float lateral = ComputeLateralDeviationXZ(userPos, _currentPath.Waypoints);

            if (lateral >= _autoRerouteDeviationThreshold)
            {
                _deviationConsecutiveFrames++;

                if (_deviationConsecutiveFrames < _deviationConfirmFrames) return;

                if (Time.time - _lastDeviationRerouteTime < _deviationRerouteCooldown)
                {
                    _deviationConsecutiveFrames = 0;
                    return;
                }

                _lastDeviationRerouteTime   = Time.time;
                _lastAutoRerouteTime        = Time.time;
                _deviationConsecutiveFrames = 0;

                Debug.Log($"[PathController] 🔄 [FullAR] Desviación confirmada {lateral:F2}m — recalculando.");

                EventBus.Instance?.Publish(new RouteDeviatedEvent
                {
                    UserPosition      = userPos,
                    DeviationDistance = lateral,
                    Destination       = _currentDestination,
                });

                NavigateTo(_currentDestination, forceRecalculate: true);
            }
            else
            {
                if (_deviationConsecutiveFrames > 0)
                    _deviationConsecutiveFrames = 0;
            }
        }

        private static float ComputeLateralDeviationXZ(
            Vector3 pos, IReadOnlyList<Vector3> waypoints)
        {
            if (waypoints == null || waypoints.Count < 2) return 0f;
            float min = float.MaxValue;
            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                Vector2 p  = new(pos.x, pos.z);
                Vector2 a  = new(waypoints[i].x, waypoints[i].z);
                Vector2 b  = new(waypoints[i + 1].x, waypoints[i + 1].z);
                Vector2 ab = b - a;
                float lenSq = ab.sqrMagnitude;
                float t  = lenSq > 0.0001f
                    ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq) : 0f;
                float d  = Vector2.Distance(p, a + t * ab);
                if (d < min) min = d;
            }
            return min < float.MaxValue ? min : 0f;
        }

        private void SyncAgentToUserPosition()
        {
            if (!_continuousAgentSync) return;

            var userBridge = UserPositionBridge.Instance;
            if (userBridge == null) return;

            Vector3 rawUserPos  = userBridge.UserPosition;
            Vector3 smoothedPos = GetSmoothedUserPosition(rawUserPos);

            Vector3 newPos = new Vector3(
                smoothedPos.x,
                transform.position.y,
                smoothedPos.z
            );

            if (Vector3.Distance(transform.position, newPos) < _agentSyncThreshold)
                return;

            if (_agent != null && _agent.isOnNavMesh)
                _agent.Warp(newPos);

            transform.position = newPos;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  MOVIMIENTO (solo NoAR)
        // ─────────────────────────────────────────────────────────────────────

        private void MoveTowardsTarget(Vector3 target, bool isStair, Vector3 finalDest)
        {
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
                transform.forward, desiredDir, ref _smoothDampVel,
                0.08f, float.MaxValue, Time.deltaTime);

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
        //  ANTI-STALL
        // ─────────────────────────────────────────────────────────────────────

        private void HandleStall(float dt)
        {
            if (!IsNavigating || _currentPath == null) return;

            _stallCheckTimer += dt;
            if (_stallCheckTimer < _stallCheckInterval) return;
            _stallCheckTimer = 0f;

            bool isFullAR = _userBridge != null && !_userBridge.IsNoArMode;
            Vector3 checkPos = isFullAR ? _userBridge.UserPosition : transform.position;

            if (!_stallRefInitialized)
            {
                _stallRefPos         = checkPos;
                _stallRefInitialized = true;
                _stallAccumTime      = 0f;
                return;
            }

            float moved = Vector3.Distance(checkPos, _stallRefPos);
            if (moved >= _stallMinMovement)
            {
                _stallRefPos    = checkPos;
                _stallAccumTime = 0f;
                return;
            }

            _stallAccumTime += _stallCheckInterval;

            if (_stallAccumTime >= _stallTimeout)
            {
                _stallAccumTime      = 0f;
                _stallRefInitialized = false;

                if (Time.time - _lastAutoRerouteTime < _autoRerouteCooldown) return;

                _lastAutoRerouteTime = Time.time;
                Debug.Log($"[PathController] ⏱ Stall detectado — recalculando.");
                NavigateTo(_currentDestination, forceRecalculate: true);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────────────────

        private int   _lastPublishedFloor     = 0;
        private float _floorTransitionCooldown = 0f;

        private void AdvanceWaypoint(IReadOnlyList<Vector3> waypoints)
        {
            if (_currentWaypointIndex >= waypoints.Count - 1) return;

            Vector3 currentWp = waypoints[_currentWaypointIndex];
            OnWaypointReached?.Invoke(_currentWaypointIndex, currentWp);

            _currentWaypointIndex++;
            _confirmedMinIndex = _currentWaypointIndex;

            Vector3 nextWp = waypoints[_currentWaypointIndex];
            float   yDelta = Mathf.Abs(nextWp.y - currentWp.y);

            if (yDelta >= _floorTransitionMinDelta)
            {
                _floorTransitionCooldown -= Time.deltaTime;
                if (_floorTransitionCooldown <= 0f)
                {
                    int fromLevel = ResolveFloorForY(currentWp.y);
                    int toLevel   = ResolveFloorForY(nextWp.y);

                    if (fromLevel != toLevel && toLevel != _lastPublishedFloor)
                    {
                        _lastPublishedFloor      = toLevel;
                        _floorTransitionCooldown = 3.0f;

                        EventBus.Instance?.Publish(new FloorTransitionEvent
                        {
                            FromLevel     = fromLevel,
                            ToLevel       = toLevel,
                            AgentPosition = transform.position
                        });
                    }
                }
            }

            _stallTimer        = 0f;
            _lastStallCheckPos = transform.position;
        }

        private int ResolveFloorForY(float worldY)
        {
            var pts = NavigationStartPointManager.GetAllStartPoints();
            if (pts == null || pts.Count == 0) return 0;

            int bestLevel = 0; float bestDist = float.MaxValue;
            foreach (var pt in pts)
            {
                float d = Mathf.Abs(worldY - pt.FloorHeight);
                if (d < bestDist) { bestDist = d; bestLevel = pt.Level; }
            }
            return bestLevel;
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

        private IndoorNavAR.AR.AROriginAligner GetOrFindAROriginAligner()
        {
            if (_arOriginAlignerCache != null) return _arOriginAlignerCache;
            if (_arOriginAlignerSearched && Time.realtimeSinceStartup < _arAlignerNextRetryTime) return null;

            _arOriginAlignerSearched = true;
            _arAlignerNextRetryTime  = Time.realtimeSinceStartup + _arAlignerRetryInterval;
            _arOriginAlignerCache    = FindFirstObjectByType<IndoorNavAR.AR.AROriginAligner>(
                FindObjectsInactive.Include);
            return _arOriginAlignerCache;
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
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CONTEXT MENU
        // ─────────────────────────────────────────────────────────────────────

        [ContextMenu("ℹ️ Estado v5.4")]
        private void DbgStatus()
        {
            float warmupElapsed  = Time.realtimeSinceStartup - _startTime;
            bool  inWarmup       = warmupElapsed < _vioWarmupDuration;
            float effectiveSync  = EffectiveAgentSyncInterval;

            Debug.Log(
                $"[PathController] v5.4\n" +
                $"  FullAR:              {IsFullARMode}\n" +
                $"  isNavigating:        {_isNavigating}\n" +
                $"  warmupElapsed:       {warmupElapsed:F1}s / {_vioWarmupDuration}s (inWarmup={inWarmup})\n" +
                $"  effectiveSyncInt:    {effectiveSync}s\n" +
                $"  deviationFrames:     {_deviationConsecutiveFrames}/{_deviationConfirmFrames}\n" +
                $"  smoothedPos:         {(_smoothedUserPosInitialized ? _smoothedUserPos.ToString("F3") : "no init")}\n" +
                $"  currentDest:         {_currentDestination:F2}");
        }

        [ContextMenu("🔄 Recalcular ruta")]
        private void DbgRecalculate()
        {
            if (!_isNavigating) return;
            NavigateTo(_currentDestination, forceRecalculate: true);
        }

        [ContextMenu("🔧 Reset filtro VIO")]
        private void DbgResetVIOFilter()
        {
            _smoothedUserPosInitialized = false;
            Debug.Log("[PathController] Filtro VIO reseteado.");
        }
    }
}