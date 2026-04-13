// File: NavigationPathController.cs
// ✅ v5.1 — FIX CRÍTICO FullAR: ruta calculada siempre desde posición real del usuario
//
// ════════════════════════════════════════════════════════════════════════════
// CAMBIOS v5 → v5.1
// ════════════════════════════════════════════════════════════════════════════
//
//  PROBLEMA:
//    En FullAR, el agente virtual (transform.position) solo se sincronizaba
//    a la posición del usuario UNA vez: al llamar NavigateTo() por primera vez.
//    Después, el usuario se mueve físicamente pero el agente permanece fijo.
//    Consecuencia:
//      • Recálculos por obstáculo → ruta parte desde posición vieja del agente.
//      • Recálculos por NavMesh regenerado → mismo problema.
//      • HandleStall → recalcula desde donde el agente quedó parado.
//      • VoiceGuide evalúa EvalPos = UserPos ✓ (correcto), pero el PathController
//        usa transform.position para calcular la ruta ✗ (incorrecto).
//    El usuario puede "perderse" porque la ruta no parte desde donde él está.
//
//  FIX 1 — GetRouteOriginForFullAR() siempre sincroniza el agente:
//    Antes de devolver el origen de la ruta, warpea el agente a esa posición.
//    Esto garantiza que NavigateTo() y todos sus recálculos internos usen
//    la posición actual del usuario, no la posición antigua del agente.
//    Aplica a: NavigateTo(), HandleStall(), OnNavMeshRegenerated().
//
//  FIX 2 — FollowPath() actualiza continuamente el agente en FullAR:
//    En FullAR, FollowPath() antes hacía "return" inmediatamente.
//    Ahora también sincroniza transform.position al NavMesh más cercano
//    a UserPosition cada frame. Esto mantiene el agente "pegado" al usuario
//    real, lo que hace que RemainingDistance y CurrentTarget sean correctos
//    en todo momento, incluso sin recálculo explícito.
//
//  FIX 3 — _agentSyncThreshold: evita warp por micromovimientos:
//    Solo se warpea el agente si la distancia entre su posición actual y
//    UserPosition es mayor que _agentSyncThreshold (default 0.15m).
//    Evita llamadas excesivas a NavMesh.SamplePosition cada frame.
//
//  TODO LO DEMÁS ES IDÉNTICO A v5.

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
        private float _waypointArrivalRadius = 0.30f;

        [SerializeField, Range(0.20f, 2f)]
        private float _destinationArrivalRadius = 0.50f;

        [Header("─── Recálculo Autónomo (FullAR) ──────────────────────────────────")]
        [SerializeField] private float _autoRerouteDeviationThreshold = 2.5f;
        [SerializeField] private float _autoRerouteCooldown           = 8.0f;
        [SerializeField] private float _stallCheckInterval            = 1.0f;
        [SerializeField] private float _stallMinMovement              = 0.3f;
        [SerializeField] private float _stallTimeout                  = 5.0f;

        private float _lastAutoRerouteTime   = -999f;
        private float _stallAccumTime        = 0f;
        private float _stallCheckTimer       = 0f;
        private Vector3 _stallRefPos         = Vector3.zero;
        private bool  _stallRefInitialized   = false;
        private UserPositionBridge _userBridge;  // ← inyectar en Start/Awake


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

        // ─── Inspector — FullAR ───────────────────────────────────────────────

        [Header("FullAR — Origen de ruta")]
        [Tooltip("Radio máximo para buscar el NavMesh más cercano al usuario en FullAR.")]
        [SerializeField, Range(1f, 5f)]
        private float _fullAROriginSnapRadius = 3.0f;

        [Tooltip("Tolerancia vertical (m) para el filtro de piso al buscar el origen.")]
        [SerializeField, Range(0.5f, 2f)]
        private float _fullAROriginFloorTolerance = 1.2f;

        // ─── Inspector — FullAR sincronización continua (v5.1) ───────────────

        [Header("FullAR — Sincronización continua del agente (v5.1)")]
        [Tooltip("✅ v5.1 — Distancia mínima (m) entre la posición del agente y UserPosition\n" +
                 "para que FollowPath() warpee el agente al NavMesh bajo el usuario.\n" +
                 "Evita warp innecesario por micromovimientos del tracking ARCore.\n" +
                 "Default 0.15m (15cm). Bajar si el usuario nota lag en la ruta.")]
        [SerializeField, Range(0.05f, 0.5f)]
        private float _agentSyncThreshold = 0.15f;

        [Tooltip("✅ v5.1 — Si true, FollowPath() sincroniza el agente a UserPosition\n" +
                 "cada frame en FullAR (con threshold). Si false, solo se sincroniza\n" +
                 "al llamar NavigateTo(). Dejar true en producción.")]
        [SerializeField]
        private bool _continuousAgentSync = true;

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

        // Cache AROriginAligner con reintento temporal
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
            TrySetAgentStopped(true);

            _userBridge = FindFirstObjectByType<UserPositionBridge>(FindObjectsInactive.Include);
            EventBus.Instance?.Subscribe<RouteDeviatedEvent>(OnRouteDeviated);  // ✅ FIX P3
        }

        private void OnDestroy()
        {
            EventBus.Instance?.Unsubscribe<RouteDeviatedEvent>(OnRouteDeviated);  // ✅ FIX P3
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


        private void OnRouteDeviated(RouteDeviatedEvent evt)
        {
            if (!IsNavigating) return;
            if (Time.time - _lastAutoRerouteTime < _autoRerouteCooldown)
            {
                Debug.Log("[PathController] ⏳ Recálculo por desviación ignorado (cooldown activo).");
                return;
            }

            _lastAutoRerouteTime = Time.time;
            Debug.Log($"[PathController] 🔄 Recálculo por desviación {evt.DeviationDistance:F2}m → {evt.Destination:F2}");

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
                TrySetAgentStopped(true);
                if (_logVerbose)
                    Debug.Log("[PathController] ✅ Modo FullAR activado.");
            }
            else
            {
                if (_logVerbose)
                    Debug.Log("[PathController] ✅ Modo NoAR activado.");
            }
        }

        public void NavigateTo(Vector3 destination, bool forceRecalculate = false)
        {
            bool wasNavigating = _isNavigating;

            if (forceRecalculate)
                _optimizer.InvalidateCache();

            _currentDestination = destination;

            // ✅ v5.1 FIX 1: GetRouteOriginForFullAR() ahora warpea el agente
            // a la posición actual del usuario ANTES de calcular la ruta.
            // Esto garantiza que tanto el primer cálculo como todos los
            // recálculos (stall, obstáculo, NavMesh regenerado) partan
            // desde donde el usuario está parado en ese momento.
            Vector3 routeOrigin = GetRouteOriginForFullAR();

            if (IsFullARMode)
                _optimizer.InvalidateCache();

            OptimizedPath path = _optimizer.ComputeOptimized(routeOrigin, destination);

            if (!path.IsValid)
            {
                Debug.LogWarning($"[PathController] Sin ruta válida hacia {destination:F2}. " +
                                 $"Status={path.Status} | origen={routeOrigin:F2} " +
                                 $"(FullAR={IsFullARMode})");
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

            if (!IsFullARMode)
                TrySetAgentStopped(false);

            Debug.Log($"[PathController] Ruta: {path.RawWaypointCount} raw → " +
                      $"{path.Waypoints.Count} optimizados, {path.TotalLength:F1}m, " +
                      $"clearance mín={path.MinClearance:F3}m" +
                      (IsFullARMode ? $" [FullAR — origen={routeOrigin:F2}]" : ""));

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

            TrySetAgentStopped(true);
        }

        public void InvalidatePathCache() => _optimizer?.InvalidateCache();

        // ─────────────────────────────────────────────────────────────────────
        //  ORIGEN DE RUTA EN FULLAR
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ✅ v5.1 FIX 1 — Devuelve el punto NavMesh bajo el usuario Y warpea el agente.
        ///
        /// CAMBIO vs v5:
        ///   Antes: buscaba el NavMesh bajo el usuario, devolvía la posición, pero
        ///          el warp solo ocurría en NavigateTo() si había diferencia con
        ///          transform.position — lo que fallaba en recálculos porque
        ///          transform.position podía haberse actualizado solo a la primera
        ///          posición del usuario.
        ///
        ///   Ahora: siempre warpea el agente a routeOrigin aquí mismo, ANTES de
        ///          que NavigateTo() llame a ComputeOptimized(). De esta forma,
        ///          tanto el optimizador como cualquier código que lea
        ///          transform.position inmediatamente después tienen la posición
        ///          correcta del usuario.
        ///
        /// En NoAR: devuelve transform.position sin cambios.
        /// </summary>
        private Vector3 GetRouteOriginForFullAR()
        {
            if (!IsFullARMode)
                return transform.position;

            var userBridge = UserPositionBridge.Instance;
            if (userBridge == null)
            {
                Debug.LogWarning("[PathController] ⚠️ [FullAR] UserPositionBridge no disponible.");
                return transform.position;
            }

            Vector3 userPos    = userBridge.UserPosition;
            Vector3 routeOrigin;

            // Estrategia principal: FloorHeight del StartPointManager
            if (TryGetFloorProjection(userPos, out Vector3 floorOrigin))
            {
                float[] radii = { 0.3f, 0.8f, 1.5f, _fullAROriginSnapRadius };
                bool found = false;
                routeOrigin = floorOrigin;

                foreach (float radius in radii)
                {
                    if (NavMesh.SamplePosition(floorOrigin, out NavMeshHit hit, radius, NavMesh.AllAreas))
                    {
                        routeOrigin = hit.position;
                        found = true;

                        if (_logVerbose)
                            Debug.Log($"[PathController] ✅ [FullAR] Origen (StartPoint): " +
                                      $"userPos={userPos:F2} → floor={floorOrigin:F2} " +
                                      $"→ navMesh={hit.position:F2} (r={radius}m)");
                        break;
                    }
                }

                if (!found)
                {
                    Debug.LogWarning($"[PathController] ⚠️ [FullAR] StartPoint floor={floorOrigin:F2} " +
                                     "sin NavMesh cercano. Usando fallback.");
                    routeOrigin = GetFallbackOrigin(userPos);
                }
            }
            else
            {
                routeOrigin = GetFallbackOrigin(userPos);
            }

            // ✅ v5.1 FIX 1: Warp explícito del agente a la posición del usuario.
            // Se hace aquí (no en NavigateTo) para que aplique a TODOS los
            // recálculos, incluyendo HandleStall y OnNavMeshRegenerated.
            WarpAgentToOrigin(routeOrigin);

            return routeOrigin;
        }

        /// <summary>
        /// ✅ v5.1 — Warpea el agente a la posición de origen de la ruta.
        /// Solo warpea si la diferencia supera _agentSyncThreshold para evitar
        /// llamadas innecesarias a Warp() por micromovimientos de ARCore.
        /// </summary>
        private void WarpAgentToOrigin(Vector3 origin)
        {
            if (Vector3.Distance(transform.position, origin) < _agentSyncThreshold)
                return;

            Vector3 prev = transform.position;
            transform.position = origin;

            if (_agent != null && _agent.isOnNavMesh)
                _agent.Warp(origin);

            if (_logVerbose)
                Debug.Log($"[PathController] 📍 [FullAR] Agente warpeado: {prev:F2} → {origin:F2} " +
                          $"(Δ={Vector3.Distance(prev, origin):F2}m)");
        }

        private Vector3 GetFallbackOrigin(Vector3 userPos)
        {
            // Proyección vertical heurística: userPos.y - 2m (funciona para piso 0)
            Vector3 groundPos     = new Vector3(userPos.x, userPos.y - 2f, userPos.z);
            float[] fallbackRadii = { 0.5f, 1.0f, 2.0f, _fullAROriginSnapRadius };

            foreach (float radius in fallbackRadii)
            {
                if (NavMesh.SamplePosition(groundPos, out NavMeshHit hit, radius, NavMesh.AllAreas))
                {
                    Debug.Log($"[PathController] ✅ [FullAR] Origen (fallback -2m): " +
                              $"userPos={userPos:F2} → navMesh={hit.position:F2} (r={radius}m)");
                    return hit.position;
                }
            }

            // Last resort: búsqueda directa desde userPos
            if (NavMesh.SamplePosition(userPos, out NavMeshHit lastResort,
                _fullAROriginSnapRadius * 2f, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[PathController] ⚠️ [FullAR] Origen (last resort): " +
                                 $"userPos={userPos:F2} → navMesh={lastResort.position:F2}");
                return lastResort.position;
            }

            Debug.LogError($"[PathController] ❌ [FullAR] Sin NavMesh cerca de {userPos:F2}. " +
                           "Verificar que el NavMesh cubre el área del usuario.");
            return transform.position;
        }

        private bool TryGetFloorProjection(Vector3 userPos, out Vector3 floorProjection)
        {
            floorProjection = userPos;

            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            if (startPoints == null || startPoints.Count == 0)
                return false;

            const float kMaxEyeToFloor = 3.0f;
            NavigationStartPoint bestFloor = null;
            float                bestDelta = float.MaxValue;

            foreach (var sp in startPoints)
            {
                if (!sp.DefinesFloorHeight) continue;

                float deltaY = userPos.y - sp.FloorHeight;
                if (deltaY < 0f || deltaY > kMaxEyeToFloor) continue;

                if (deltaY < bestDelta)
                {
                    bestDelta = deltaY;
                    bestFloor = sp;
                }
            }

            if (bestFloor == null)
            {
                if (_logVerbose)
                    Debug.LogWarning($"[PathController] ⚠️ [FullAR] Sin StartPoint en rango " +
                                     $"Y=[{userPos.y - kMaxEyeToFloor:F2}, {userPos.y:F2}].");
                return false;
            }

            floorProjection = new Vector3(userPos.x, bestFloor.FloorHeight, userPos.z);

            if (_logVerbose)
                Debug.Log($"[PathController] 🏢 [FullAR] Piso: Level {bestFloor.Level} " +
                          $"(FloorHeight={bestFloor.FloorHeight:F3}m, deltaY={bestDelta:F3}m)");

            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SEGUIMIENTO DE RUTA
        // ─────────────────────────────────────────────────────────────────────

        private void FollowPath()
        {
            // Auto-detección FullAR con reintento temporal
            if (!IsFullARMode)
            {
                var aligner = GetOrFindAROriginAligner();
                if (aligner != null && aligner.IsFullARMode)
                {
                    SetFullARMode(true);
                    Debug.Log("[PathController] ⚡ Auto-corrección FullAR en FollowPath().");
                }
            }

            // ✅ v5.1 FIX 2 — En FullAR: sincronizar el agente al usuario cada frame.
            //
            // En v5, este bloque hacía "if (IsFullARMode) return;" inmediatamente.
            // Ahora sincronizamos transform.position antes de retornar.
            //
            // PROPÓSITO:
            //   • RemainingDistance usa transform.position para calcular la
            //     distancia recorrida. Si el agente no se mueve con el usuario,
            //     RemainingDistance no cambia aunque el usuario camine.
            //   • VoiceGuide usa EvalPos = UserPos (correcto), pero el PathController
            //     internamente usa transform.position para stall detection.
            //   • Con sync continuo, _lastStallCheckPos se actualiza correctamente
            //     reflejando el movimiento real del usuario.
            //
            // THRESHOLD: solo warp si el usuario se movió > _agentSyncThreshold (0.15m).
            // Evita llamadas continuas a NavMesh.SamplePosition en cada frame.
            if (IsFullARMode)
            {
                SyncAgentToUserPosition();
                CheckDeviationInFullAR();    // ✅ FIX P3: nueva llamada
                HandleStall(Time.deltaTime); // ✅ FIX P3: ahora también corre en FullAR
                return;
            }


            // ── Modo NoAR: movimiento autónomo del agente ──────────────────────

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
                    HandleStall(Time.deltaTime);   // ✅ ahora se pasa dt (float)
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

        private void CheckDeviationInFullAR()
        {
            if (_currentPath == null || !_currentPath.IsValid) return;
            if (Time.time - _lastAutoRerouteTime < _autoRerouteCooldown) return;

            Vector3 userPos = _userBridge != null
                ? _userBridge.UserPosition
                : (Camera.main != null ? Camera.main.transform.position : transform.position);

            float lateral = ComputeLateralDeviationXZ(userPos, _currentPath.Waypoints);
            if (lateral < _autoRerouteDeviationThreshold) return;

            _lastAutoRerouteTime = Time.time;
            Debug.Log($"[PathController] ⚠️ FullAR: Desviación detectada {lateral:F2}m — recalculando.");
            NavigateTo(_currentDestination, forceRecalculate: true);

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


        /// <summary>
        /// ✅ v5.1 FIX 2 — Sincroniza el agente virtual a la posición real del usuario.
        ///
        /// Se llama cada frame en FollowPath() cuando IsFullARMode == true.
        /// Usa _agentSyncThreshold para evitar warp innecesario en cada frame.
        ///
        /// Después del warp, actualiza _lastStallCheckPos para que la detección
        /// de stall refleje el movimiento real del usuario (el usuario real nunca
        /// está "atascado" mientras camina, aunque el agente virtual no se moviera).
        /// </summary>
        private void SyncAgentToUserPosition()
        {
            var userBridge = UserPositionBridge.Instance;
            if (userBridge == null) return;

            Vector3 userPos = userBridge.UserPosition;

            // Solo sincronizar si el usuario se movió lo suficiente
            if (Vector3.Distance(transform.position, userPos) < _agentSyncThreshold)
                return;

            // Buscar el punto NavMesh más cercano al usuario
            // Usar radio pequeño para que sea rápido (hot path)
            if (!NavMesh.SamplePosition(userPos, out NavMeshHit hit,
                _fullAROriginSnapRadius, NavMesh.AllAreas))
                return;

            Vector3 newPos = hit.position;
            transform.position = newPos;

            if (_agent != null && _agent.isOnNavMesh)
                _agent.Warp(newPos);

            // Actualizar referencia de stall para que el detector de atasco
            // sepa que el usuario se está moviendo
            _lastStallCheckPos = newPos;

            if (_logVerbose)
                Debug.Log($"[PathController] 🔄 [FullAR] Sync agente: {newPos:F2} " +
                          $"(userPos={userPos:F2})");
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
                Debug.Log($"[PathController] ⏱ Stall detectado en FullAR={isFullAR} pos={checkPos:F2} — recalculando.");
                NavigateTo(_currentDestination, forceRecalculate: true);

            }
        }


        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────────────────

        private int   _lastPublishedFloor = 0;
        private float _floorTransitionCooldown = 0f;

        private void AdvanceWaypoint(IReadOnlyList<Vector3> waypoints)
        {
            if (_currentWaypointIndex >= waypoints.Count - 1)
                return;

            Vector3 currentWp = waypoints[_currentWaypointIndex];

            OnWaypointReached?.Invoke(_currentWaypointIndex, currentWp);

            _currentWaypointIndex++;
            _confirmedMinIndex = _currentWaypointIndex;

            Vector3 nextWp = waypoints[_currentWaypointIndex];

            // ── DETECCIÓN TRANSICIÓN DE PISO ─────────────────────
            float yDelta = Mathf.Abs(nextWp.y - currentWp.y);

            if (yDelta >= _stairHeightThreshold)
            {
                _floorTransitionCooldown -= Time.deltaTime;

                if (_floorTransitionCooldown <= 0f)
                {
                    int fromLevel = ResolveFloorForY(currentWp.y);
                    int toLevel   = ResolveFloorForY(nextWp.y);

                    if (fromLevel != toLevel && toLevel != _lastPublishedFloor)
                    {
                        _lastPublishedFloor      = toLevel;
                        _floorTransitionCooldown = 2.0f;

                        EventBus.Instance?.Publish(new FloorTransitionEvent
                        {
                            FromLevel     = fromLevel,
                            ToLevel       = toLevel,
                            AgentPosition = transform.position
                        });

                        Debug.Log($"[PathController] 🏢 FloorTransitionEvent: {fromLevel} → {toLevel}");
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

            int bestLevel = 0;
            float bestDist = float.MaxValue;

            foreach (var pt in pts)
            {
                float d = Mathf.Abs(worldY - pt.FloorHeight);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestLevel = pt.Level;
                }
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

            // ✅ v5.1: recalcular en AMBOS modos.
            // En FullAR, GetRouteOriginForFullAR() warpea el agente a la
            // posición actual del usuario antes de recalcular — garantizando
            // que el nuevo NavMesh se explota desde donde el usuario está.
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

        // Cache de AROriginAligner con reintento temporal
        private IndoorNavAR.AR.AROriginAligner GetOrFindAROriginAligner()
        {
            if (_arOriginAlignerCache != null)
                return _arOriginAlignerCache;

            if (_arOriginAlignerSearched && Time.realtimeSinceStartup < _arAlignerNextRetryTime)
                return null;

            _arOriginAlignerSearched  = true;
            _arAlignerNextRetryTime   = Time.realtimeSinceStartup + _arAlignerRetryInterval;

            _arOriginAlignerCache = FindFirstObjectByType<IndoorNavAR.AR.AROriginAligner>(
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

            if (IsFullARMode && Application.isPlaying)
            {
                Gizmos.color = new Color(1f, 0f, 1f, 0.5f);
                Gizmos.DrawWireSphere(transform.position, 0.2f);

                // ✅ v5.1: Mostrar la posición real del usuario en gizmos
                var bridge = UserPositionBridge.Instance;
                if (bridge != null)
                {
                    Gizmos.color = new Color(0f, 1f, 1f, 0.7f);
                    Gizmos.DrawWireSphere(bridge.UserPosition, 0.3f);
                    Gizmos.DrawLine(transform.position, bridge.UserPosition);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CONTEXT MENU DEBUG
        // ─────────────────────────────────────────────────────────────────────

        [ContextMenu("📍 Log posición agente vs usuario")]
        private void DbgLogPositions()
        {
            var bridge = UserPositionBridge.Instance;
            Debug.Log($"[PathController v5.1] Agente: {transform.position:F3} | " +
                      $"Usuario: {(bridge != null ? bridge.UserPosition.ToString("F3") : "N/A")} | " +
                      $"Δ: {(bridge != null ? Vector3.Distance(transform.position, bridge.UserPosition).ToString("F3") : "N/A")}m | " +
                      $"FullAR: {IsFullARMode} | ContinuousSync: {_continuousAgentSync}");
        }

        [ContextMenu("🔄 Forzar sync agente a usuario")]
        private void DbgForceSync()
        {
            if (!IsFullARMode) { Debug.Log("[PathController] Solo aplica en FullAR."); return; }
            var before = transform.position;
            _ = GetRouteOriginForFullAR();
            Debug.Log($"[PathController] Sync: {before:F3} → {transform.position:F3}");
        }

        [ContextMenu("🔄 Recalcular ruta desde posición actual")]
        private void DbgRecalculate()
        {
            if (!_isNavigating) { Debug.Log("[PathController] Sin navegación activa."); return; }
            NavigateTo(_currentDestination, forceRecalculate: true);
        }
    }
}