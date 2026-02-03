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
    /// Sistema de navegación OPTIMIZADO para AR con pathfinding inteligente
    /// ✅ Sin recalculaciones innecesarias cerca de bordes
    /// ✅ Hysteresis inteligente para estabilidad
    /// ✅ Path caching y reutilización
    /// ✅ Corrección predictiva sin lag
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class NavigationAgent : MonoBehaviour
    {
        [Header("🎯 Agente")]
        [SerializeField] private NavMeshAgent _navMeshAgent;
        
        [Header("📊 Visualización de Ruta")]
        [SerializeField] private LineRenderer _pathLineRenderer;
        [SerializeField] private Color _pathColor = Color.yellow;
        [SerializeField] private float _pathWidth = 0.1f;
        [SerializeField] private float _pathHeightOffset = 0.1f;

        [Header("🧠 Pathfinding Inteligente")]
        [Tooltip("Intervalo mínimo entre recálculos de ruta (segundos)")]
        [SerializeField] private float _minPathRecalculationInterval = 1.0f;
        
        [Tooltip("Distancia mínima para considerar recálculo necesario")]
        [SerializeField] private float _pathRecalculationThreshold = 2.0f;
        
        [Tooltip("Usar hysteresis para evitar oscilaciones")]
        [SerializeField] private bool _useHysteresis = true;
        
        [Tooltip("Margen de hysteresis (m)")]
        [SerializeField] private float _hysteresisMargin = 0.3f;
        
        [Header("🚀 Optimización de Performance")]
        [Tooltip("Cachear rutas calculadas")]
        [SerializeField] private bool _enablePathCaching = true;
        
        [Tooltip("Tiempo de validez del cache (segundos)")]
        [SerializeField] private float _pathCacheLifetime = 5.0f;
        
        [Tooltip("Usar path smoothing progresivo")]
        [SerializeField] private bool _enableProgressiveSmoothing = true;
        
        [Header("🔧 Configuración de Movimiento")]
        [SerializeField] private float _arrivalThreshold = 0.5f;
        [SerializeField] private float _stoppingDistance = 0.3f;
        
        [Header("🎨 Animación")]
        [SerializeField] private Animator _animator;
        [SerializeField] private string _walkAnimParam = "IsWalking";

        // Estado de navegación
        private WaypointData _currentDestination;
        private bool _isNavigating;
        private float _navigationStartTime;
        private float _totalDistance;
        
        // Optimización de pathfinding
        private float _lastPathRecalculationTime;
        private Vector3 _lastRecalculationPosition;
        private NavMeshPath _cachedPath;
        private float _pathCacheTimestamp;
        private bool _isPathCacheValid;
        
        // Hysteresis para estabilidad
        private bool _wasNearEdge;
        private float _edgeProximityHysteresis;
        
        // Smoothing progresivo
        private List<Vector3> _smoothedPath = new List<Vector3>();
        private int _currentPathIndex = 0;

        #region Properties

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

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateDependencies();
            SetupPathVisualization();
            InitializeOptimizedSystems();
        }

        private void Update()
        {
            if (_isNavigating)
            {
                UpdateOptimizedNavigation();
                UpdatePathVisualization();
                UpdateAnimations();
            }
        }

        #endregion

        #region Initialization

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

        private void InitializeOptimizedSystems()
        {
            _lastPathRecalculationTime = -_minPathRecalculationInterval;
            _lastRecalculationPosition = transform.position;
            _isPathCacheValid = false;
            _wasNearEdge = false;
            _edgeProximityHysteresis = 0f;
            
            // Configurar NavMeshAgent para estabilidad
            _navMeshAgent.stoppingDistance = _stoppingDistance;
            _navMeshAgent.autoBraking = true;
            _navMeshAgent.autoRepath = false; // ✅ Control manual para evitar recálculos constantes
            
            Debug.Log("[NavigationAgent] Sistema optimizado inicializado");
            Debug.Log($"  • Recálculo mínimo: {_minPathRecalculationInterval}s");
            Debug.Log($"  • Threshold: {_pathRecalculationThreshold}m");
            Debug.Log($"  • Hysteresis: {(_useHysteresis ? "Activo" : "Inactivo")}");
        }

        #endregion

        #region Public API - Navigation

        public bool NavigateToWaypoint(WaypointData waypoint)
        {
            if (waypoint == null || !waypoint.IsNavigable) return false;
            if (!_navMeshAgent.isOnNavMesh) return false;
            
            return NavigateToPosition(waypoint.Position, waypoint);
        }

        public bool NavigateToPosition(Vector3 destination, WaypointData waypointData = null)
        {
            // Validar que el destino esté en NavMesh
            if (!NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[NavigationAgent] Destino fuera de NavMesh: {destination}");
                return false;
            }

            // Intentar usar path cacheado si es válido
            NavMeshPath pathToUse = null;
            
            if (_enablePathCaching && _isPathCacheValid && 
                (Time.time - _pathCacheTimestamp) < _pathCacheLifetime &&
                _currentDestination?.Position == destination)
            {
                pathToUse = _cachedPath;
                Debug.Log("[NavigationAgent] Usando path cacheado");
            }
            else
            {
                // Calcular nueva ruta
                pathToUse = new NavMeshPath();
                if (!_navMeshAgent.CalculatePath(hit.position, pathToUse))
                {
                    Debug.LogWarning("[NavigationAgent] No se pudo calcular ruta");
                    return false;
                }
                
                // Cachear ruta
                if (_enablePathCaching)
                {
                    _cachedPath = pathToUse;
                    _pathCacheTimestamp = Time.time;
                    _isPathCacheValid = true;
                }
            }

            // Procesar y aplicar ruta
            ProcessAndApplyPath(pathToUse, hit.position, waypointData);
            
            return true;
        }

        public void StopNavigation(string reason = "Usuario canceló navegación")
        {
            if (!_isNavigating) return;

            _navMeshAgent.ResetPath();
            _isNavigating = false;
            _currentDestination = null;
            _smoothedPath.Clear();
            _currentPathIndex = 0;
            _isPathCacheValid = false;

            if (_pathLineRenderer != null) _pathLineRenderer.enabled = false;
            
            UpdateAnimations();
            
            EventBus.Instance.Publish(new NavigationCancelledEvent { Reason = reason });
            
            Debug.Log($"[NavigationAgent] Navegación detenida: {reason}");
        }

        #endregion

        #region Path Processing

        private void ProcessAndApplyPath(NavMeshPath path, Vector3 finalDestination, WaypointData waypointData)
        {
            // Aplicar smoothing progresivo si está habilitado
            if (_enableProgressiveSmoothing && path.corners.Length > 2)
            {
                _smoothedPath = ApplyProgressiveSmoothing(path.corners.ToList());
            }
            else
            {
                _smoothedPath = path.corners.ToList();
            }
            
            // Iniciar navegación
            StartNavigationSequence(finalDestination, waypointData, path);
        }

        /// <summary>
        /// Smoothing progresivo que no sobrecarga el sistema
        /// </summary>
        private List<Vector3> ApplyProgressiveSmoothing(List<Vector3> corners)
        {
            if (corners.Count < 3) return corners;

            List<Vector3> smoothed = new List<Vector3> { corners[0] };

            for (int i = 0; i < corners.Count - 1; i++)
            {
                Vector3 p0 = i > 0 ? corners[i - 1] : corners[i];
                Vector3 p1 = corners[i];
                Vector3 p2 = corners[i + 1];
                Vector3 p3 = (i + 2 < corners.Count) ? corners[i + 2] : p2;

                // Interpolar con Catmull-Rom (suavizado)
                int segments = 3; // Menos segmentos = mejor performance
                
                for (int s = 1; s <= segments; s++)
                {
                    float t = s / (float)segments;
                    Vector3 point = CatmullRom(p0, p1, p2, p3, t);
                    smoothed.Add(point);
                }
            }

            smoothed.Add(corners[corners.Count - 1]);
            return smoothed;
        }

        private Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            
            return 0.5f * (
                (2f * p1) + 
                (-p0 + p2) * t + 
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + 
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3
            );
        }

        private void StartNavigationSequence(Vector3 destination, WaypointData waypointData, NavMeshPath originalPath)
        {
            _currentDestination = waypointData;
            _isNavigating = true;
            _navigationStartTime = Time.time;
            _totalDistance = CalculatePathLength(originalPath);
            _lastPathRecalculationTime = Time.time;
            _lastRecalculationPosition = transform.position;
            _currentPathIndex = 0;
            
            // Aplicar ruta al agente
            _navMeshAgent.SetPath(originalPath);

            if (_pathLineRenderer != null) _pathLineRenderer.enabled = true;

            EventBus.Instance.Publish(new NavigationStartedEvent
            {
                DestinationWaypointId = waypointData?.WaypointId ?? "",
                StartPosition = transform.position,
                DestinationPosition = destination,
                EstimatedDistance = _totalDistance
            });
            
            Debug.Log($"[NavigationAgent] Navegación iniciada a: {waypointData?.WaypointName ?? "Posición"}");
        }

        #endregion

        #region Optimized Navigation Update

        private void UpdateOptimizedNavigation()
        {
            // Verificar llegada
            if (!_navMeshAgent.pathPending && _navMeshAgent.remainingDistance <= _arrivalThreshold)
            {
                OnArrivalAtDestination();
                return;
            }

            // Sistema INTELIGENTE de recálculo de ruta
            CheckAndRecalculatePathIfNeeded();
        }

        /// <summary>
        /// ✅ SISTEMA CLAVE: Recálculo inteligente con hysteresis para evitar oscilaciones
        /// </summary>
        private void CheckAndRecalculatePathIfNeeded()
        {
            float timeSinceLastRecalc = Time.time - _lastPathRecalculationTime;
            
            // Respetar intervalo mínimo (evita spam de recálculos)
            if (timeSinceLastRecalc < _minPathRecalculationInterval)
                return;

            // Verificar si estamos cerca de un borde del NavMesh
            bool isNearEdge = IsNearNavMeshEdge();
            
            // Aplicar HYSTERESIS para evitar oscilaciones
            if (_useHysteresis)
            {
                if (isNearEdge && !_wasNearEdge)
                {
                    // Acabamos de acercarnos al borde - incrementar hysteresis
                    _edgeProximityHysteresis += _hysteresisMargin;
                }
                else if (!isNearEdge && _wasNearEdge)
                {
                    // Acabamos de alejarnos del borde - decrementar hysteresis
                    _edgeProximityHysteresis -= _hysteresisMargin;
                }
                
                _wasNearEdge = isNearEdge;
            }

            // Calcular distancia recorrida desde último recálculo
            float distanceMoved = Vector3.Distance(transform.position, _lastRecalculationPosition);
            
            // Threshold adaptativo según proximidad a borde
            float adaptiveThreshold = _pathRecalculationThreshold + _edgeProximityHysteresis;
            
            // Decidir si recalcular
            bool shouldRecalculate = false;
            
            // Condición 1: Nos hemos movido suficiente Y no estamos oscilando en borde
            if (distanceMoved > adaptiveThreshold)
            {
                shouldRecalculate = true;
            }
            
            // Condición 2: Hay un camino significativamente mejor disponible
            if (!shouldRecalculate && CheckForBetterPath())
            {
                shouldRecalculate = true;
            }

            if (shouldRecalculate)
            {
                RecalculatePathOptimized();
            }
        }

        /// <summary>
        /// Detecta si estamos cerca de un borde del NavMesh
        /// </summary>
        private bool IsNearNavMeshEdge()
        {
            // Raycast en varias direcciones para detectar bordes
            Vector3[] directions = new Vector3[]
            {
                transform.forward,
                -transform.forward,
                transform.right,
                -transform.right
            };

            float edgeCheckDistance = _navMeshAgent.radius * 2f;

            foreach (Vector3 dir in directions)
            {
                Vector3 checkPos = transform.position + (dir * edgeCheckDistance);
                
                if (!NavMesh.SamplePosition(checkPos, out NavMeshHit hit, edgeCheckDistance * 0.5f, NavMesh.AllAreas))
                {
                    // No hay NavMesh en esta dirección = estamos cerca de un borde
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Verifica si hay una ruta significativamente mejor disponible
        /// </summary>
        private bool CheckForBetterPath()
        {
            if (_currentDestination == null) return false;

            NavMeshPath candidatePath = new NavMeshPath();
            
            if (!_navMeshAgent.CalculatePath(_currentDestination.Position, candidatePath))
                return false;

            float candidateLength = CalculatePathLength(candidatePath);
            float currentLength = _navMeshAgent.remainingDistance;

            // Si la nueva ruta es 15% más corta, vale la pena recalcular
            return candidateLength < currentLength * 0.85f;
        }

        /// <summary>
        /// Recálculo optimizado de ruta
        /// </summary>
        private void RecalculatePathOptimized()
        {
            if (_currentDestination == null) return;

            NavMeshPath newPath = new NavMeshPath();
            
            if (_navMeshAgent.CalculatePath(_currentDestination.Position, newPath))
            {
                // Aplicar nueva ruta
                _navMeshAgent.SetPath(newPath);
                
                // Actualizar cache
                if (_enablePathCaching)
                {
                    _cachedPath = newPath;
                    _pathCacheTimestamp = Time.time;
                }
                
                // Actualizar smoothing
                if (_enableProgressiveSmoothing)
                {
                    _smoothedPath = ApplyProgressiveSmoothing(newPath.corners.ToList());
                }
                
                // Actualizar estado de recálculo
                _lastPathRecalculationTime = Time.time;
                _lastRecalculationPosition = transform.position;
                
                Debug.Log("[NavigationAgent] Ruta recalculada (optimizado)");
            }
        }

        #endregion

        #region Arrival Handling

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
            _isPathCacheValid = false;

            if (_pathLineRenderer != null) _pathLineRenderer.enabled = false;
            
            UpdateAnimations();
            
            Debug.Log($"[NavigationAgent] ✅ Llegada exitosa en {totalTime:F1}s");
        }

        #endregion

        #region Visualization

        private void UpdatePathVisualization()
        {
            if (_pathLineRenderer == null || !_pathLineRenderer.enabled) return;

            // Usar path suavizado si está disponible
            List<Vector3> pathToShow = _smoothedPath.Count > 0 
                ? _smoothedPath 
                : _navMeshAgent.path.corners.ToList();

            if (pathToShow.Count < 2)
            {
                _pathLineRenderer.positionCount = 0;
                return;
            }

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

        #endregion

        #region Utilities

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
                _isPathCacheValid = false; // Invalidar cache después de teleport
                return true;
            }
            return false;
        }

        #endregion

        #region Debug Visualization

        private void OnDrawGizmos()
        {
            if (!_isNavigating || _navMeshAgent == null) return;

            // Path actual
            Gizmos.color = Color.yellow;
            NavMeshPath path = _navMeshAgent.path;
            if (path != null && path.corners.Length > 1)
            {
                for (int i = 0; i < path.corners.Length - 1; i++)
                    Gizmos.DrawLine(path.corners[i], path.corners[i + 1]);
            }

            // Path suavizado
            if (_smoothedPath.Count > 1)
            {
                Gizmos.color = Color.cyan;
                for (int i = 0; i < _smoothedPath.Count - 1; i++)
                    Gizmos.DrawLine(_smoothedPath[i], _smoothedPath[i + 1]);
            }

            // Destino
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(_navMeshAgent.destination, 0.3f);

            // Radio de detección de bordes
            Gizmos.color = IsNearNavMeshEdge() ? Color.red : Color.green;
            Gizmos.DrawWireSphere(transform.position, _navMeshAgent.radius * 2f);

            #if UNITY_EDITOR
            // Información de estado
            string status = $"Dist: {_navMeshAgent.remainingDistance:F1}m\n";
            status += $"Próx. borde: {(IsNearNavMeshEdge() ? "SÍ" : "NO")}\n";
            status += $"Hysteresis: {_edgeProximityHysteresis:F2}m";
            
            UnityEditor.Handles.Label(transform.position + Vector3.up * 2f, status);
            #endif
        }

        #endregion
    }
}