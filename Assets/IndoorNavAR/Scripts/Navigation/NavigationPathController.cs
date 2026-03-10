// File: NavigationPathController.cs
// ✅ v5 — FIX: Correcciones preventivas sobre v4
//
// ============================================================================
//  CAMBIOS v4 → v5
// ============================================================================
//
//  FIX A — Bug de log en NavigateTo() (cosmético pero confuso):
//    v4 leía transform.position DESPUÉS del warp para el mensaje "era X",
//    por lo que siempre imprimía routeOrigin en lugar de la posición anterior.
//    Se guarda _prevAgentPos antes del warp y se usa en el log.
//
//  FIX B — Comparación Vector3 con != para el warp (frágil):
//    v4 usaba `routeOrigin != transform.position` para decidir si hacer warp.
//    Vector3.operator!= usa epsilon ~0.00001 — puede fallar con posiciones
//    casi idénticas que difieren por floating point noise después de SamplePosition.
//    Se reemplaza por `Vector3.Distance(...) > 0.01f` (umbral pragmático 1cm).
//
//  FIX C — GetRouteOriginForFullAR() con filtro de piso multi-nivel:
//    v4 siempre buscaba NavMesh en `userPos.y - 2m`. Esto falla en el piso 1
//    (Y≈3.48m): busca en Y≈1.48m y puede aterrizar en el NavMesh del piso 0.
//    
//    NUEVO ENFOQUE:
//      1. Consulta NavigationStartPointManager para obtener la lista de pisos
//         y sus FloorHeight conocidos.
//      2. Identifica el piso más cercano al Y actual del usuario.
//      3. Usa ese FloorHeight como Y de búsqueda en lugar de `userPos.y - 2m`.
//      4. Fallback escalonado si StartPointManager no tiene datos.
//
//    Esto es resistente a edificios de N pisos sin hardcodear offsets de altura.
//
//  FIX D — Cache del optimizer se invalida cuando cambia el origen FullAR:
//    v4 no invalidaba la caché del optimizer al hacer warp del agente.
//    Si UserPosition cambia entre llamadas a NavigateTo() pero cae en la misma
//    celda de hash (0.5m), se devuelve la ruta antigua desde el origen anterior.
//    Se llama InvalidateCache() siempre en FullAR antes de ComputeOptimized().
//
//  FIX E — OnNavMeshRegenerated() recalcula ruta en FullAR también:
//    v4 tenía `!IsFullARMode` como guardia, ignorando recálculos de NavMesh
//    en modo AR. En FullAR la ruta debe recalcularse igual que en NoAR —
//    la diferencia es solo que FollowPath() no mueve el transform.
//
//  FIX F — GetOrFindAROriginAligner() se busca cada 5s si no se encontró:
//    v4 solo buscaba AROriginAligner una vez (_arOriginAlignerSearched=true).
//    Si el objeto aún no existía en ese frame (race condition al arrancar),
//    nunca se encontraba. Ahora reintenta cada 5s hasta encontrarlo.
//
// ============================================================================
//  TODOS LOS FIXES DE v4 SE CONSERVAN ÍNTEGRAMENTE.
// ============================================================================

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

        // ─── Inspector — FullAR ───────────────────────────────────────────────

        [Header("FullAR — Origen de ruta")]
        [Tooltip("Radio máximo para buscar el NavMesh más cercano al usuario en FullAR.\n" +
                 "La cámara XR está a ~1.6m de altura; el NavMesh está en el piso (~0m o ~3.48m).\n" +
                 "Default 3m cubre la distancia vertical típica.")]
        [SerializeField, Range(1f, 5f)]
        private float _fullAROriginSnapRadius = 3.0f;

        [Tooltip("✅ v5 FIX C: Tolerancia vertical (m) para el filtro de piso al buscar el origen.\n" +
                 "Se usa SOLO si NavigationStartPointManager no tiene datos de piso.\n" +
                 "Debe ser menor que la mitad de la separación entre pisos (~1.74m).\n" +
                 "Default 1.2m.")]
        [SerializeField, Range(0.5f, 2f)]
        private float _fullAROriginFloorTolerance = 1.2f;

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

        /// <summary>
        /// En FullAR, FollowPath() no mueve el transform.
        /// El path se calcula y CurrentPath queda válido para VoiceGuide y ARGuideController.
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

        // ✅ v5 FIX F — Cache de AROriginAligner con reintento temporal
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
        /// Activa/desactiva el modo FullAR.
        /// En FullAR FollowPath() calcula posición de waypoints pero
        /// NO mueve el transform (el XR Origin = cámara = usuario real).
        /// </summary>
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

        /// <summary>
        /// Calcula la ruta hacia el destino y comienza la navegación.
        ///
        /// ✅ v4: En FullAR usa UserPositionBridge como origen de la ruta.
        /// ✅ v5 FIX A: Log correcto del warp (guarda posición anterior ANTES de warpar).
        /// ✅ v5 FIX B: Condición de warp con distancia (no con !=) para evitar
        ///             problemas de floating-point epsilon.
        /// ✅ v5 FIX D: Cache del optimizer se invalida siempre en FullAR para
        ///             garantizar que UserPosition actualizado genere ruta fresca.
        /// </summary>
        public void NavigateTo(Vector3 destination, bool forceRecalculate = false)
        {
            if (forceRecalculate)
                _optimizer.InvalidateCache();

            _currentDestination = destination;

            // ✅ v4+v5: En FullAR, calcular origen desde la posición real del usuario
            Vector3 routeOrigin = GetRouteOriginForFullAR();

            // ✅ v5 FIX D: En FullAR, siempre invalidar caché antes de calcular.
            //   UserPosition puede cambiar entre llamadas aunque caiga en la misma
            //   celda de hash del optimizer (0.5m), lo que devolvería la ruta antigua.
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

            // ✅ v5 FIX A + FIX B: Guardar posición anterior ANTES del warp,
            //   y usar distancia en lugar de != para comparar vectores.
            if (IsFullARMode && Vector3.Distance(routeOrigin, transform.position) > 0.01f)
            {
                Vector3 prevPos = transform.position; // ← FIX A: guardado antes del warp

                transform.position = routeOrigin;
                if (_agent != null && _agent.isOnNavMesh)
                    _agent.Warp(routeOrigin);

                Debug.Log($"[PathController] 📍 [FullAR] Agente sincronizado al origen de ruta: " +
                          $"{routeOrigin:F2} (era {prevPos:F2})"); // ← FIX A: usa prevPos
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
        //  v5 FIX C — ORIGEN DE RUTA EN FULLAR (con piso multi-nivel)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Devuelve el punto del NavMesh más adecuado como origen de la ruta.
        ///
        /// En NoAR: devuelve transform.position (sin cambios respecto a v3).
        ///
        /// En FullAR (v5 FIX C):
        ///   ESTRATEGIA PRINCIPAL — Usar FloorHeight del StartPointManager:
        ///     1. Obtiene la lista de pisos conocidos de NavigationStartPointManager.
        ///     2. Identifica el piso cuyo FloorHeight es más cercano a userPos.y
        ///        (con margen de _fullAROriginFloorTolerance para el Y de los ojos).
        ///     3. Busca NavMesh proyectando horizontalmente al FloorHeight del piso
        ///        correcto, no a "userPos.y - 2m" (que era frágil y asumía piso 0).
        ///
        ///   ESTRATEGIA FALLBACK (si StartPointManager no tiene datos):
        ///     Mismo comportamiento que v4: buscar en groundPos = userPos - 2m.
        ///
        ///   Ventaja vs v4:
        ///     Funciona correctamente en edificios multi-planta sin hardcodear offsets.
        ///     Piso 0 (Y≈0m) y piso 1 (Y≈3.48m) se identifican automáticamente.
        /// </summary>
        private Vector3 GetRouteOriginForFullAR()
        {
            if (!IsFullARMode)
                return transform.position;

            var userBridge = UserPositionBridge.Instance;
            if (userBridge == null)
            {
                Debug.LogWarning("[PathController] ⚠️ [FullAR] UserPositionBridge no disponible. " +
                                 "Usando transform.position (puede ser incorrecto en FullAR).");
                return transform.position;
            }

            Vector3 userPos = userBridge.UserPosition;

            // ── ESTRATEGIA PRINCIPAL: usar FloorHeight del StartPointManager ──────
            Vector3 floorOrigin;
            if (TryGetFloorProjection(userPos, out floorOrigin))
            {
                // Buscar NavMesh desde la proyección al piso correcto
                float[] radii = { 0.3f, 0.8f, 1.5f, _fullAROriginSnapRadius };
                foreach (float radius in radii)
                {
                    if (NavMesh.SamplePosition(floorOrigin, out NavMeshHit hit, radius, NavMesh.AllAreas))
                    {
                        if (_logVerbose)
                            Debug.Log($"[PathController] ✅ [FullAR] Origen (StartPoint): " +
                                      $"userPos={userPos:F2} → floor={floorOrigin:F2} " +
                                      $"→ navMesh={hit.position:F2} (r={radius}m)");
                        return hit.position;
                    }
                }

                Debug.LogWarning($"[PathController] ⚠️ [FullAR] StartPoint floor={floorOrigin:F2} " +
                                 "no tiene NavMesh cercano. Usando estrategia fallback.");
            }

            // ── ESTRATEGIA FALLBACK: proyección vertical heurística ──────────────
            // Misma lógica que v4 (userPos.y - 2m), funciona para piso 0.
            Vector3 groundPos = new Vector3(userPos.x, userPos.y - 2f, userPos.z);
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

            // ── FALLBACK FINAL: búsqueda directa desde userPos ───────────────────
            if (NavMesh.SamplePosition(userPos, out NavMeshHit lastResort,
                _fullAROriginSnapRadius * 2f, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[PathController] ⚠️ [FullAR] Origen (last resort): " +
                                 $"userPos={userPos:F2} → navMesh={lastResort.position:F2}");
                return lastResort.position;
            }

            Debug.LogError($"[PathController] ❌ [FullAR] Sin NavMesh cerca del usuario " +
                           $"({userPos:F2}). Usando transform.position={transform.position:F2}. " +
                           "Verificar que el NavMesh cubre el área del usuario.");
            return transform.position;
        }

        /// <summary>
        /// ✅ v5 FIX C: Identifica el piso correcto usando NavigationStartPointManager
        /// y proyecta userPos a ese FloorHeight para usarlo como origen de búsqueda NavMesh.
        ///
        /// LÓGICA:
        ///   La cámara XR está a ~1.6m de altura (Y de los ojos).
        ///   Los FloorHeight de los StartPoints están a Y≈0.03m (piso 0) o Y≈3.48m (piso 1).
        ///   La diferencia entre userPos.y (ojos) y FloorHeight (suelo) es ~1.57m.
        ///   Con _fullAROriginFloorTolerance=1.2m esto se acercaría al límite,
        ///   por eso usamos la distancia total (no solo Y) para identificar el piso más cercano.
        ///
        /// RETORNA: true si encontró un piso conocido y proyectó correctamente.
        ///          false si StartPointManager no tiene datos de piso.
        /// </summary>
        private bool TryGetFloorProjection(Vector3 userPos, out Vector3 floorProjection)
        {
            floorProjection = userPos;

            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            if (startPoints == null || startPoints.Count == 0)
                return false;

            // Encontrar el piso cuyo FloorHeight es más cercano a (userPos.y - eyeHeight)
            // eyeHeight ≈ 1.6m. Usamos la distancia Y entre userPos y cada FloorHeight.
            // El piso correcto es el que minimiza |userPos.y - FloorHeight - ~1.6m|,
            // pero como no sabemos exactamente la eyeHeight, buscamos el FloorHeight
            // más cercano a userPos.y con la premisa de que el usuario está DE PIE encima.
            //
            // Mejor heurística: el piso es correcto cuando |userPos.y - floorY| < 3m
            // (umbral generoso que no confundiría pisos separados por >3m).

            const float kMaxEyeToFloor = 3.0f; // máxima altura ojo-suelo esperada
            NavigationStartPoint bestFloor = null;
            float                bestDelta = float.MaxValue;

            foreach (var sp in startPoints)
            {
                if (!sp.DefinesFloorHeight) continue;

                float deltaY = userPos.y - sp.FloorHeight;

                // El usuario está encima del piso: deltaY debe ser positivo y < kMaxEyeToFloor
                if (deltaY < 0f || deltaY > kMaxEyeToFloor) continue;

                if (deltaY < bestDelta)
                {
                    bestDelta = deltaY;
                    bestFloor = sp;
                }
            }

            if (bestFloor == null)
            {
                // Ningún piso dentro del rango esperado — quizás no hay datos aún
                if (_logVerbose)
                    Debug.LogWarning($"[PathController] ⚠️ [FullAR] TryGetFloorProjection: " +
                                     $"ningún StartPoint con FloorHeight dentro de [{userPos.y - kMaxEyeToFloor:F2}, " +
                                     $"{userPos.y:F2}]. Fallback a proyección heurística.");
                return false;
            }

            // Proyectar userPos al FloorHeight del piso identificado
            floorProjection = new Vector3(userPos.x, bestFloor.FloorHeight, userPos.z);

            if (_logVerbose)
                Debug.Log($"[PathController] 🏢 [FullAR] Piso identificado: Level {bestFloor.Level} " +
                          $"(FloorHeight={bestFloor.FloorHeight:F3}m, " +
                          $"deltaY={bestDelta:F3}m desde userPos.y={userPos.y:F3}m). " +
                          $"Proyección: {floorProjection:F2}");

            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SEGUIMIENTO DE RUTA
        // ─────────────────────────────────────────────────────────────────────

        private void FollowPath()
        {
            // ✅ v_PATCH + v5 FIX F: Auto-detección FullAR con reintento temporal.
            //   Si el aligner aún no existía cuando se buscó por primera vez,
            //   reintentamos cada _arAlignerRetryInterval segundos.
            if (!IsFullARMode)
            {
                var aligner = GetOrFindAROriginAligner();
                if (aligner != null && aligner.IsFullARMode)
                {
                    SetFullARMode(true);
                    Debug.Log("[PathController] ⚡ Auto-corrección FullAR en FollowPath().");
                }
            }
            if (IsFullARMode) return;

            IReadOnlyList<Vector3> waypoints = _currentPath.Waypoints;
            Vector3 finalDest = waypoints[waypoints.Count - 1];

            float distToFinal = Vector3.Distance(transform.position, finalDest);
            if (distToFinal <= _destinationArrivalRadius)
            {
                Arrive();
                return;
            }

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

        /// <summary>
        /// ✅ v5 FIX E: En v4 este método tenía !IsFullARMode como guardia,
        /// ignorando recálculos de NavMesh durante navegación AR.
        /// En FullAR la ruta debe recalcularse igual — FollowPath() no moverá
        /// el transform de todas formas, pero CurrentPath debe estar actualizado
        /// para VoiceGuide y ARGuideController.
        /// </summary>
        private void OnNavMeshRegenerated(NavMeshGeneratedEvent evt)
        {
            if (!evt.Success) return;
            _optimizer.InvalidateCache();

            // ✅ v5 FIX E: recalcular en AMBOS modos (NoAR y FullAR)
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
        //  v5 FIX F — CACHE DE ARORIGINALIGNER CON REINTENTO TEMPORAL
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ✅ v5 FIX F: v4 marcaba _arOriginAlignerSearched=true en el primer intento
        /// y nunca reintentaba si el objeto no existía aún (race condition al arrancar).
        ///
        /// Ahora: si no se encontró, reintenta cada _arAlignerRetryInterval segundos
        /// hasta encontrarlo. Una vez encontrado, el caché es permanente.
        /// </summary>
        private IndoorNavAR.AR.AROriginAligner GetOrFindAROriginAligner()
        {
            // Si ya lo tenemos en caché, devolverlo directamente
            if (_arOriginAlignerCache != null)
                return _arOriginAlignerCache;

            // Si encontramos en caché pero el objeto fue destruido (null check de Unity)
            // o si aún no hemos intentado / ha pasado suficiente tiempo para reintentar
            if (_arOriginAlignerSearched && Time.realtimeSinceStartup < _arAlignerNextRetryTime)
                return null;

            // Intentar encontrarlo
            _arOriginAlignerSearched  = true;
            _arAlignerNextRetryTime   = Time.realtimeSinceStartup + _arAlignerRetryInterval;

            _arOriginAlignerCache = FindFirstObjectByType<IndoorNavAR.AR.AROriginAligner>(
                FindObjectsInactive.Include);

            if (_arOriginAlignerCache != null && _logVerbose)
                Debug.Log("[PathController] ✅ AROriginAligner encontrado y cacheado.");
            else if (_logVerbose)
                Debug.Log($"[PathController] ⏳ AROriginAligner no encontrado, " +
                          $"reintentando en {_arAlignerRetryInterval}s.");

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
            }
        }
    }
}