using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Sistema de navegación avanzado para interiores con técnicas profesionales
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class NavigationAgent : MonoBehaviour
    {
        [Header("Agente")]
        [SerializeField] private NavMeshAgent _navMeshAgent;
        
        [Header("Visualización de Ruta")]
        [SerializeField] private LineRenderer _pathLineRenderer;
        [SerializeField] private Color _pathColor = Color.yellow;
        [SerializeField] private float _pathWidth = 0.1f;
        [SerializeField] private float _pathHeightOffset = 0.1f;

        [Header("Smoothing Avanzado")]
        [SerializeField] private bool _enablePathSmoothing = true;
        [SerializeField] private int _smoothingIterations = 3;
        [SerializeField] private float _smoothingStrength = 0.5f;
        [SerializeField] private float _cornerCutDistance = 0.3f;

        [Header("Margen Dinámico de Obstáculos")]
        [SerializeField] private bool _enableDynamicMargin = true;

        [Header("Margen Dinámico de Obstáculos - SISTEMA ADAPTATIVO")]
        [Tooltip("Margen base mínimo para navegación. 0.08m = seguridad sin bloquear puertas")]
        [SerializeField] private float _baseObstacleMargin = 0.08f;

        [Tooltip("Margen máximo en espacios abiertos. 0.25m = conservador sin ser restrictivo")]
        [SerializeField] private float _maxObstacleMargin = 0.25f;

        [Tooltip("Velocidad de incremento del margen. 0.05m/s = cambio gradual")]
        [SerializeField] private float _marginIncreaseRate = 0.05f;

        [Header("Detección Inteligente de Espacios Estrechos")]
        [Tooltip("Activa reducción automática de margen en puertas/pasillos")]
        [SerializeField] private bool _enableNarrowSpaceDetection = true;

        [Tooltip("Ancho máximo considerado 'espacio estrecho'. 1.2m = pasillo típico")]
        [SerializeField] private float _narrowSpaceThreshold = 1.2f;

        [Tooltip("Factor de reducción de margen en espacios estrechos. 0.3 = reducir a 30%")]
        [SerializeField] private float _narrowSpaceMarginMultiplier = 0.3f;

        [Tooltip("Frecuencia de detección de espacios estrechos (segundos)")]
        [SerializeField] private float _narrowSpaceCheckInterval = 0.2f;

        [Header("Predicción y Anticipación")]
        [SerializeField] private bool _enableTrajectoryPrediction = true;
        
        [SerializeField] private float _predictionDistance = 2f;
        [SerializeField] private int _predictionSamples = 5;
        [SerializeField] private LayerMask _obstacleDetectionMask;

        [Header("Corrección Inteligente")]
        [SerializeField] private bool _enableSmartCorrection = true;
        [SerializeField] private float _correctionCheckInterval = 0.3f;
        [SerializeField] private float _stuckDetectionTime = 2f;
        [SerializeField] private float _stuckDistanceThreshold = 0.1f;

        [Header("Navegación con Waypoints")]
        [SerializeField] private bool _useIntermediateWaypoints = true;
        [SerializeField] private float _waypointSpacing = 3f;

        [Header("Configuración")]
        [SerializeField] private float _arrivalThreshold = 0.5f;
        [SerializeField] private float _updatePathInterval = 0.5f;

        [Header("Animación")]
        [SerializeField] private Animator _animator;
        [SerializeField] private string _walkAnimParam = "IsWalking";

        private WaypointData _currentDestination;
        private bool _isNavigating;
        private float _navigationStartTime;
        private float _totalDistance;
        private float _lastPathUpdateTime;
        private Vector3 _startPosition;
        private List<Vector3> _smoothedPath = new List<Vector3>();
        private List<Vector3> _intermediateWaypoints = new List<Vector3>();
        private int _currentWaypointIndex = 0;
        private float _currentObstacleMargin;
        private Vector3 _lastPositionCheck;
        private float _lastMovementTime;
        private float _distanceTraveledSinceCheck;
        private float _lastCorrectionCheck;
        private Queue<Vector3> _recentPositions = new Queue<Vector3>(10);

        // Variables privadas del sistema adaptativo
        private float _lastNarrowSpaceCheck;
        private bool _isInNarrowSpace;

        public bool IsNavigating => _isNavigating;
        public WaypointData CurrentDestination => _currentDestination;
        public float DistanceToDestination => _navMeshAgent.remainingDistance;
        public float ProgressPercent
        {
            get
            {
                if (!_isNavigating || _totalDistance <= 0) return 0f;
                return Mathf.Clamp01((_totalDistance - _navMeshAgent.remainingDistance) / _totalDistance);
            }
        }

        private void Awake()
        {
            ValidateDependencies();
            SetupPathVisualization();
            InitializeAdvancedSystems();
        }

        private void Update()
        {
            if (_isNavigating)
            {
                UpdateAdvancedNavigation();
                UpdatePathVisualization();
                UpdateAnimations();
            }
        }

        private void ValidateDependencies()
        {
            if (_navMeshAgent == null) _navMeshAgent = GetComponent<NavMeshAgent>();
            if (_animator == null) _animator = GetComponent<Animator>();
            
            if (_navMeshAgent == null)
            {
                Debug.LogError("[NavigationAgent] NavMeshAgent no encontrado.");
                enabled = false;
            }
        }

        private void SetupPathVisualization()
        {
            if (_pathLineRenderer == null)
                _pathLineRenderer = gameObject.AddComponent<LineRenderer>();

            _pathLineRenderer.startWidth = _pathWidth;
            _pathLineRenderer.endWidth = _pathWidth;
            _pathLineRenderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _pathLineRenderer.startColor = _pathColor;
            _pathLineRenderer.endColor = _pathColor;
            _pathLineRenderer.positionCount = 0;
            _pathLineRenderer.enabled = false;
        }

        private void InitializeAdvancedSystems()
        {
            // Inicializar margen en valor base
            _currentObstacleMargin = _baseObstacleMargin;
            _lastPositionCheck = transform.position;
            _lastMovementTime = Time.time;
            _lastNarrowSpaceCheck = 0f; // Forzar check inmediato
            _isInNarrowSpace = false;
            
            if (_obstacleDetectionMask == 0)
                _obstacleDetectionMask = LayerMask.GetMask("Default");
            
            Debug.Log("[NavigationAgent] Sistema adaptativo de márgenes inicializado.");
            Debug.Log($"  • Margen base: {_baseObstacleMargin}m");
            Debug.Log($"  • Margen máximo: {_maxObstacleMargin}m");
            Debug.Log($"  • Reducción en pasillos: {_narrowSpaceMarginMultiplier * 100}%");
        }

        public bool NavigateToWaypoint(WaypointData waypoint)
        {
            if (waypoint == null || !waypoint.IsNavigable) return false;
            if (!_navMeshAgent.isOnNavMesh) return false;
            return NavigateToPosition(waypoint.Position, waypoint);
        }

        public bool NavigateToPosition(Vector3 destination, WaypointData waypointData = null)
        {
            if (!NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                return false;

            NavMeshPath path = new NavMeshPath();
            if (!_navMeshAgent.CalculatePath(hit.position, path))
                return false;

            ProcessAdvancedPath(path, hit.position);
            StartNavigationSequence(hit.position, waypointData, path);
            return true;
        }

        private void ProcessAdvancedPath(NavMeshPath path, Vector3 finalDestination)
        {
            List<Vector3> rawCorners = path.corners.ToList();

            if (_useIntermediateWaypoints)
                _intermediateWaypoints = GenerateIntermediateWaypoints(rawCorners, finalDestination);

            if (_enablePathSmoothing)
                _smoothedPath = ApplyPathSmoothing(rawCorners);
            else
                _smoothedPath = new List<Vector3>(rawCorners);

            _smoothedPath = OptimizeCorners(_smoothedPath);
        }

        private List<Vector3> GenerateIntermediateWaypoints(List<Vector3> corners, Vector3 finalDest)
        {
            List<Vector3> waypoints = new List<Vector3>();
            if (corners.Count < 2) { waypoints.Add(finalDest); return waypoints; }

            float totalDistance = 0f;
            for (int i = 0; i < corners.Count - 1; i++)
                totalDistance += Vector3.Distance(corners[i], corners[i + 1]);

            if (totalDistance < _waypointSpacing * 2) { waypoints.Add(finalDest); return waypoints; }

            float accumulatedDistance = 0f;
            for (int i = 0; i < corners.Count - 1; i++)
            {
                Vector3 start = corners[i];
                Vector3 end = corners[i + 1];
                float segmentLength = Vector3.Distance(start, end);
                
                while (accumulatedDistance + segmentLength >= _waypointSpacing * (waypoints.Count + 1))
                {
                    float targetDistance = _waypointSpacing * (waypoints.Count + 1);
                    float distanceInSegment = targetDistance - accumulatedDistance;
                    float t = distanceInSegment / segmentLength;
                    waypoints.Add(Vector3.Lerp(start, end, t));
                }
                accumulatedDistance += segmentLength;
            }

            waypoints.Add(finalDest);
            return waypoints;
        }

        private List<Vector3> ApplyPathSmoothing(List<Vector3> corners)
        {
            if (corners.Count < 3) return corners;

            List<Vector3> smoothed = new List<Vector3> { corners[0] };

            for (int i = 0; i < corners.Count - 1; i++)
            {
                Vector3 p0 = i > 0 ? corners[i - 1] : corners[i];
                Vector3 p1 = corners[i];
                Vector3 p2 = corners[i + 1];
                Vector3 p3 = (i + 2 < corners.Count) ? corners[i + 2] : p2;

                int segments = Mathf.Max(3, Mathf.CeilToInt(Vector3.Distance(p1, p2) / 0.5f));
                
                for (int s = 1; s <= segments; s++)
                {
                    float t = s / (float)segments;
                    Vector3 point = CatmullRom(p0, p1, p2, p3, t);
                    
                    if (NavMesh.SamplePosition(point, out NavMeshHit hit, 0.5f, NavMesh.AllAreas))
                        smoothed.Add(hit.position);
                    else
                        smoothed.Add(point);
                }
            }
            return smoothed;
        }

        private Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * ((2f * p1) + (-p0 + p2) * t + 
                   (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + 
                   (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private List<Vector3> OptimizeCorners(List<Vector3> path)
        {
            if (path.Count < 3 || _cornerCutDistance <= 0) return path;

            List<Vector3> optimized = new List<Vector3> { path[0] };

            for (int i = 1; i < path.Count - 1; i++)
            {
                Vector3 prev = path[i - 1];
                Vector3 current = path[i];
                Vector3 next = path[i + 1];

                Vector3 dirIn = (current - prev).normalized;
                Vector3 dirOut = (next - current).normalized;
                float angle = Vector3.Angle(dirIn, dirOut);

                if (angle > 30f && angle < 150f)
                {
                    float cutDist = Mathf.Min(_cornerCutDistance, 
                        Vector3.Distance(prev, current) * 0.3f,
                        Vector3.Distance(current, next) * 0.3f);

                    Vector3 cutPoint = current - dirIn * cutDist + dirOut * cutDist;
                    optimized.Add(cutPoint);
                }
                else
                {
                    optimized.Add(current);
                }
            }

            optimized.Add(path[path.Count - 1]);
            return optimized;
        }

        private void StartNavigationSequence(Vector3 destination, WaypointData waypointData, NavMeshPath originalPath)
        {
            _currentDestination = waypointData;
            _isNavigating = true;
            _navigationStartTime = Time.time;
            _startPosition = transform.position;
            _totalDistance = CalculatePathLength(originalPath);
            _lastPathUpdateTime = Time.time;
            _lastCorrectionCheck = Time.time;
            _lastMovementTime = Time.time;
            _lastPositionCheck = transform.position;
            _distanceTraveledSinceCheck = 0f;
            _recentPositions.Clear();
            _navMeshAgent.radius = _currentObstacleMargin;

            if (_useIntermediateWaypoints && _intermediateWaypoints.Count > 1)
            {
                _currentWaypointIndex = 0;
                _navMeshAgent.SetDestination(_intermediateWaypoints[0]);
            }
            else
            {
                _navMeshAgent.SetDestination(destination);
            }

            if (_pathLineRenderer != null) _pathLineRenderer.enabled = true;

            EventBus.Instance.Publish(new NavigationStartedEvent
            {
                DestinationWaypointId = waypointData?.WaypointId ?? "",
                StartPosition = _startPosition,
                DestinationPosition = destination,
                EstimatedDistance = _totalDistance
            });
        }

        public void StopNavigation(string reason = "Usuario canceló navegación")
        {
            if (!_isNavigating) return;

            _navMeshAgent.ResetPath();
            _isNavigating = false;
            _currentDestination = null;
            _smoothedPath.Clear();
            _intermediateWaypoints.Clear();
            _currentWaypointIndex = 0;

            if (_pathLineRenderer != null) _pathLineRenderer.enabled = false;
            UpdateAnimations();
            EventBus.Instance.Publish(new NavigationCancelledEvent { Reason = reason });
        }

        private void UpdateAdvancedNavigation()
        {
            _recentPositions.Enqueue(transform.position);
            if (_recentPositions.Count > 10) _recentPositions.Dequeue();

            if (!_navMeshAgent.pathPending && _navMeshAgent.remainingDistance <= _arrivalThreshold)
            {
                HandleArrival();
                return;
            }

            if (_enableSmartCorrection) CheckStuckState();
            if (_enableDynamicMargin) UpdateDynamicMargin();
            if (_enableTrajectoryPrediction) PredictAndCorrectTrajectory();

            if (Time.time - _lastCorrectionCheck >= _correctionCheckInterval)
            {
                CorrectPathIfNeeded();
                _lastCorrectionCheck = Time.time;
            }

            if (Time.time - _lastPathUpdateTime >= _updatePathInterval)
            {
                _lastPathUpdateTime = Time.time;
                EventBus.Instance.Publish(new NavigationProgressEvent
                {
                    DistanceRemaining = _navMeshAgent.remainingDistance,
                    ProgressPercent = ProgressPercent,
                    CurrentPosition = transform.position
                });
            }
        }

        private void HandleArrival()
        {
            if (_useIntermediateWaypoints && _currentWaypointIndex < _intermediateWaypoints.Count - 1)
            {
                _currentWaypointIndex++;
                _navMeshAgent.SetDestination(_intermediateWaypoints[_currentWaypointIndex]);
            }
            else
            {
                OnArrivalAtDestination();
            }
        }

        private void CheckStuckState()
        {
            float distanceMoved = Vector3.Distance(transform.position, _lastPositionCheck);
            _distanceTraveledSinceCheck += distanceMoved;
            _lastPositionCheck = transform.position;

            if (_navMeshAgent.velocity.magnitude < 0.1f)
            {
                float timeSinceMovement = Time.time - _lastMovementTime;
                if (timeSinceMovement > _stuckDetectionTime && _distanceTraveledSinceCheck < _stuckDistanceThreshold)
                {
                    RecoverFromStuck();
                }
            }
            else
            {
                _lastMovementTime = Time.time;
                _distanceTraveledSinceCheck = 0f;
            }
        }

        private void RecoverFromStuck()
        {
            _currentObstacleMargin = Mathf.Min(_currentObstacleMargin + _marginIncreaseRate, _maxObstacleMargin);
            _navMeshAgent.radius = _currentObstacleMargin;

            Vector3 targetPos = _useIntermediateWaypoints && _currentWaypointIndex < _intermediateWaypoints.Count
                ? _intermediateWaypoints[_currentWaypointIndex]
                : _navMeshAgent.destination;

            NavMeshPath newPath = new NavMeshPath();
            if (_navMeshAgent.CalculatePath(targetPos, newPath))
                _navMeshAgent.SetPath(newPath);

            _lastMovementTime = Time.time;
            _distanceTraveledSinceCheck = 0f;
        }

        private void PredictAndCorrectTrajectory()
        {
            if (_navMeshAgent.velocity.magnitude < 0.1f) return;

            Vector3 predictedPos = transform.position + _navMeshAgent.velocity.normalized * _predictionDistance;

            for (int i = 0; i < _predictionSamples; i++)
            {
                float t = (i + 1) / (float)_predictionSamples;
                Vector3 samplePos = Vector3.Lerp(transform.position, predictedPos, t);

                if (Physics.CheckSphere(samplePos, _navMeshAgent.radius * 0.8f, _obstacleDetectionMask))
                {
                    _navMeshAgent.ResetPath();
                    Vector3 targetPos = _useIntermediateWaypoints && _currentWaypointIndex < _intermediateWaypoints.Count
                        ? _intermediateWaypoints[_currentWaypointIndex]
                        : _navMeshAgent.destination;
                    _navMeshAgent.SetDestination(targetPos);
                    break;
                }
            }
        }

        private void CorrectPathIfNeeded()
        {
            if (!_enableSmartCorrection) return;

            Vector3 targetPos = _useIntermediateWaypoints && _currentWaypointIndex < _intermediateWaypoints.Count
                ? _intermediateWaypoints[_currentWaypointIndex]
                : _navMeshAgent.destination;

            NavMeshPath candidatePath = new NavMeshPath();
            if (_navMeshAgent.CalculatePath(targetPos, candidatePath))
            {
                float candidateLength = CalculatePathLength(candidatePath);
                float currentLength = _navMeshAgent.remainingDistance;

                if (candidateLength < currentLength * 0.9f)
                    _navMeshAgent.SetPath(candidatePath);
            }
        }

        private void OnArrivalAtDestination()
        {
            float totalTime = Time.time - _navigationStartTime;
            
            EventBus.Instance.Publish(new NavigationCompletedEvent
            {
                DestinationWaypointId = _currentDestination?.WaypointId ?? "",
                TotalDistance = _totalDistance,
                TotalTime = totalTime
            });

            _isNavigating = false;
            _currentDestination = null;
            _smoothedPath.Clear();
            _intermediateWaypoints.Clear();

            if (_pathLineRenderer != null) _pathLineRenderer.enabled = false;
            UpdateAnimations();
        }

        private void UpdatePathVisualization()
        {
            if (_pathLineRenderer == null || !_pathLineRenderer.enabled) return;

            List<Vector3> pathToShow = _smoothedPath.Count > 0 ? _smoothedPath : _navMeshAgent.path.corners.ToList();
            if (pathToShow.Count < 2) { _pathLineRenderer.positionCount = 0; return; }

            _pathLineRenderer.positionCount = pathToShow.Count;
            for (int i = 0; i < pathToShow.Count; i++)
            {
                Vector3 pos = pathToShow[i];
                pos.y += _pathHeightOffset;
                _pathLineRenderer.SetPosition(i, pos);
            }
        }

        private void UpdateAnimations()
        {
            if (_animator == null || string.IsNullOrEmpty(_walkAnimParam)) return;
            bool isWalking = _isNavigating && _navMeshAgent.velocity.magnitude > 0.1f;
            _animator.SetBool(_walkAnimParam, isWalking);
        }

        private float CalculatePathLength(NavMeshPath path)
        {
            if (path.corners.Length < 2) return 0f;
            float length = 0f;
            for (int i = 0; i < path.corners.Length - 1; i++)
                length += Vector3.Distance(path.corners[i], path.corners[i + 1]);
            return length;
        }

        public bool TeleportTo(Vector3 position)
        {
            if (NavMesh.SamplePosition(position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                _navMeshAgent.Warp(hit.position);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Detecta si el agente está en un espacio estrecho (puerta/pasillo).
        /// Usa raycasts laterales para medir ancho disponible.
        /// </summary>
        private bool IsInNarrowSpace()
        {
            if (!_enableNarrowSpaceDetection) 
                return false;
            
            // Optimización: Verificar solo cada X segundos
            if (Time.time - _lastNarrowSpaceCheck < _narrowSpaceCheckInterval)
                return _isInNarrowSpace;
            
            _lastNarrowSpaceCheck = Time.time;
            
            // Posición de origen para raycasts (altura del centro del agente)
            Vector3 origin = transform.position + Vector3.up * (_navMeshAgent.height * 0.5f);
            
            // Distancia a pared izquierda
            float leftDist = GetDistanceToWall(origin, -transform.right);
            
            // Distancia a pared derecha
            float rightDist = GetDistanceToWall(origin, transform.right);
            
            // Ancho total disponible
            float totalWidth = leftDist + rightDist;
            
            // Guardar estado
            _isInNarrowSpace = totalWidth < _narrowSpaceThreshold;
            
            // Debug visual
            if (_isInNarrowSpace)
            {
                Debug.DrawRay(origin, -transform.right * leftDist, Color.yellow, _narrowSpaceCheckInterval);
                Debug.DrawRay(origin, transform.right * rightDist, Color.yellow, _narrowSpaceCheckInterval);
            }
            
            return _isInNarrowSpace;
        }

        /// <summary>
        /// Calcula distancia a la pared más cercana en una dirección.
        /// </summary>
        private float GetDistanceToWall(Vector3 origin, Vector3 direction)
        {
            RaycastHit hit;
            float maxDistance = 2f; // Máximo a chequear (optimización)
            
            if (Physics.Raycast(origin, direction, out hit, maxDistance, _obstacleDetectionMask))
            {
                return hit.distance;
            }
            
            // No hay pared en rango = espacio abierto
            return maxDistance;
        }

        /// <summary>
        /// Actualiza el margen de seguridad dinámicamente según el contexto.
        /// LÓGICA CLAVE: Reduce margen en espacios estrechos, aumenta en abiertos.
        /// </summary>
        private void UpdateDynamicMargin()
        {
            // Detectar si hay obstáculos cercanos
            bool nearObstacle = Physics.CheckSphere(
                transform.position, 
                _currentObstacleMargin + 0.15f, 
                _obstacleDetectionMask
            );
            
            // Detectar si estamos en espacio estrecho
            bool inNarrowSpace = IsInNarrowSpace();
            
            // ✅ LÓGICA ADAPTATIVA:
            // En espacio estrecho → usar margen mínimo (priorizar navegabilidad)
            // En espacio abierto → permitir margen mayor (priorizar seguridad)
            
            float targetMargin;
            float maxAllowedMargin;
            
            if (inNarrowSpace)
            {
                // MODO PASILLO/PUERTA: Margen ultra-reducido
                targetMargin = _baseObstacleMargin * _narrowSpaceMarginMultiplier;
                maxAllowedMargin = _baseObstacleMargin; // No puede crecer
                
                // Log académico para debugging
                if (Time.frameCount % 100 == 0) // Cada ~1.6s a 60fps
                {
                    Debug.Log($"[NavigationAgent] 🚪 Modo espacio estrecho: margen={targetMargin:F2}m");
                }
            }
            else
            {
                // MODO NORMAL: Margen dinámico según obstáculos
                targetMargin = _baseObstacleMargin;
                maxAllowedMargin = _maxObstacleMargin;
            }
            
            // Ajustar margen gradualmente
            if (nearObstacle && !inNarrowSpace)
            {
                // Incrementar margen (solo en espacios abiertos)
                _currentObstacleMargin = Mathf.Min(
                    _currentObstacleMargin + _marginIncreaseRate * Time.deltaTime,
                    maxAllowedMargin
                );
            }
            else
            {
                // Decrementar margen hacia objetivo
                _currentObstacleMargin = Mathf.Max(
                    _currentObstacleMargin - _marginIncreaseRate * Time.deltaTime * 0.5f,
                    targetMargin
                );
            }
            
            // Aplicar margen al agente NavMesh
            _navMeshAgent.radius = _currentObstacleMargin;
        }

        private void OnDrawGizmos()
        {
            if (!_isNavigating || _navMeshAgent == null) return;

            // 🔹 PATH ORIGINAL DEL NAVMESH
            Gizmos.color = Color.yellow;
            NavMeshPath path = _navMeshAgent.path;
            if (path != null && path.corners.Length > 1)
            {
                for (int i = 0; i < path.corners.Length - 1; i++)
                    Gizmos.DrawLine(path.corners[i], path.corners[i + 1]);
            }

            // 🔹 PATH SUAVIZADO
            if (_smoothedPath.Count > 1)
            {
                Gizmos.color = Color.cyan;
                for (int i = 0; i < _smoothedPath.Count - 1; i++)
                    Gizmos.DrawLine(_smoothedPath[i], _smoothedPath[i + 1]);
            }

            // 🔹 WAYPOINTS INTERMEDIOS
            if (_intermediateWaypoints.Count > 0)
            {
                Gizmos.color = Color.green;
                foreach (Vector3 wp in _intermediateWaypoints)
                    Gizmos.DrawWireSphere(wp, 0.3f);

                if (_currentWaypointIndex < _intermediateWaypoints.Count)
                {
                    Gizmos.color = Color.red;
                    Gizmos.DrawWireSphere(_intermediateWaypoints[_currentWaypointIndex], 0.4f);
                }
            }

            // 🔹 DESTINO FINAL
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(_navMeshAgent.destination, 0.3f);

            // =========================================================
            // ✅ VISUALIZACIÓN DEL SISTEMA ADAPTATIVO DE MÁRGENES
            // =========================================================

            Color marginColor = _isInNarrowSpace
                ? new Color(0f, 1f, 0f, 0.3f)     // 🟢 Verde → espacio estrecho
                : new Color(1f, 0.5f, 0f, 0.3f);  // 🟠 Naranja → espacio normal

            Gizmos.color = marginColor;
            Gizmos.DrawWireSphere(transform.position, _currentObstacleMargin);

            #if UNITY_EDITOR
            UnityEditor.Handles.Label(
                transform.position + Vector3.up * 2f,
                _isInNarrowSpace
                    ? $"MODO PASILLO\nMargen: {_currentObstacleMargin:F2} m"
                    : $"Margen: {_currentObstacleMargin:F2} m"
            );
            #endif

            // 🔹 PREDICCIÓN DE TRAYECTORIA
            if (_enableTrajectoryPrediction && _navMeshAgent.velocity.magnitude > 0.1f)
            {
                Gizmos.color = Color.magenta;
                Vector3 predictedPos = transform.position + 
                                    _navMeshAgent.velocity.normalized * _predictionDistance;

                Gizmos.DrawLine(transform.position, predictedPos);

                for (int i = 0; i < _predictionSamples; i++)
                {
                    float t = (i + 1) / (float)_predictionSamples;
                    Vector3 samplePos = Vector3.Lerp(transform.position, predictedPos, t);
                    Gizmos.DrawWireSphere(samplePos, _navMeshAgent.radius * 0.5f);
                }
            }

            // 🔹 HISTORIAL DE POSICIONES
            if (_recentPositions.Count > 1)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.5f);
                Vector3[] posArray = _recentPositions.ToArray();
                for (int i = 0; i < posArray.Length - 1; i++)
                    Gizmos.DrawLine(posArray[i], posArray[i + 1]);
            }
        }

    }
}