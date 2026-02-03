using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Posiciona el agente en el PISO REAL (más bajo), ignorando mesas y techos.
    /// ✅ FIXED: Loop infinito de flotación corregido
    /// ✅ FIXED: Tolerancia aumentada para evitar reposicionamientos constantes
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class AgentFloorPlacement : MonoBehaviour
    {
        [Header("🎯 Detección del Piso Real")]
        [Tooltip("Usar siempre el punto MÁS BAJO del modelo como piso")]
        [SerializeField] private bool _useAbsoluteLowest = true;

        [Tooltip("Ignorar superficies por encima de este porcentaje (0-1). 0.3 = ignorar 30% superior")]
        [Range(0f, 0.9f)]
        [SerializeField] private float _ignoreTopPercent = 0.5f;

        [Tooltip("Área mínima para considerar como piso real (m²). Descarta mesas pequeñas")]
        [SerializeField] private float _minFloorArea = 2f;

        [Tooltip("Capas del modelo")]
        [SerializeField] private LayerMask _modelLayers = -1;

        [Header("⚙️ Configuración")]
        [Tooltip("Offset desde el piso (metros)")]
        [SerializeField] private float _heightOffset = 0.05f;

        [Tooltip("Ejecutar al iniciar (solo si NavMesh ya existe)")]
        [SerializeField] private bool _placeOnStart = false;

        [Tooltip("Reposicionar automáticamente si está flotando")]
        [SerializeField] private bool _autoCorrectFloating = true;

        [Tooltip("Frecuencia de verificación (segundos)")]
        [SerializeField] private float _checkInterval = 2f; // ✅ Aumentado de 1s a 2s

        [Header("🔧 NavMesh Waiting")]
        [Tooltip("Intentos máximos de espera por NavMesh")]
        [SerializeField] private int _maxNavMeshWaitAttempts = 10;

        [Tooltip("Delay entre intentos (segundos)")]
        [SerializeField] private float _navMeshCheckDelay = 0.5f;

        [Tooltip("Radio de búsqueda para SamplePosition")]
        [SerializeField] private float _navMeshSampleRadius = 10f;

        [Header("🛡️ Tolerancia de Flotación")]
        [Tooltip("Distancia máxima al piso antes de considerar flotación (metros)")]
        [SerializeField] private float _floatingTolerance = 1.0f; // ✅ NUEVO: Era hardcoded 0.5m

        [Tooltip("Umbral mínimo de movimiento para evitar correcciones innecesarias")]
        [SerializeField] private float _minMovementThreshold = 0.05f; // ✅ NUEVO

        [Header("🔧 Debug")]
        [SerializeField] private bool _debugVisualization = true;
        [SerializeField] private bool _verboseLogs = false; // ✅ Cambiado a false por defecto

        private NavMeshAgent _agent;
        private float _lastCheck;
        private Vector3 _detectedFloorPosition;
        private bool _isPlaced;
        private List<SurfaceInfo> _allSurfaces = new List<SurfaceInfo>();
        private Bounds _modelBounds;
        private bool _waitingForNavMesh;
        private Coroutine _navMeshWaitCoroutine;
        
        // ✅ NUEVO: Prevenir loops
        private int _consecutiveCorrections = 0;
        private const int MAX_CONSECUTIVE_CORRECTIONS = 3;
        private float _lastCorrectionTime;
        private const float MIN_TIME_BETWEEN_CORRECTIONS = 3f;

        private class SurfaceInfo
        {
            public string name;
            public float height;
            public float area;
            public Bounds bounds;
            public bool isHorizontal;
            public SurfaceType type;
        }

        private enum SurfaceType
        {
            Floor,
            Furniture,
            Ceiling,
            Unknown
        }

        #region Unity Lifecycle

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            
            if (_agent == null)
            {
                Debug.LogError("[AgentFloorPlacement] ❌ NavMeshAgent no encontrado.");
                enabled = false;
                return;
            }

            _agent.updatePosition = true;
            _agent.updateRotation = true;
        }

        private void OnEnable()
        {
            EventBus.Instance?.Subscribe<NavMeshGeneratedEvent>(OnNavMeshGenerated);
            EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        private void OnDisable()
        {
            EventBus.Instance?.Unsubscribe<NavMeshGeneratedEvent>(OnNavMeshGenerated);
            EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);

            if (_navMeshWaitCoroutine != null)
            {
                StopCoroutine(_navMeshWaitCoroutine);
                _navMeshWaitCoroutine = null;
            }
        }

        private void Start()
        {
            if (_placeOnStart)
            {
                if (IsNavMeshReady())
                {
                    PlaceAgentOnFloor();
                }
                else
                {
                    if (_verboseLogs)
                    {
                        Debug.Log("[AgentFloorPlacement] ⏳ NavMesh no listo. Esperando evento...");
                    }
                    _waitingForNavMesh = true;
                }
            }
        }

        private void Update()
        {
            if (_autoCorrectFloating && _isPlaced && Time.time - _lastCheck >= _checkInterval)
            {
                _lastCheck = Time.time;
                CheckAndCorrectFloating();
            }
        }

        #endregion

        #region Event Handlers

        private void OnNavMeshGenerated(NavMeshGeneratedEvent evt)
        {
            if (!evt.Success)
            {
                if (_verboseLogs)
                {
                    Debug.LogWarning("[AgentFloorPlacement] ⚠️ NavMesh falló, no se puede posicionar agente");
                }
                _waitingForNavMesh = false;
                return;
            }

            if (_verboseLogs)
            {
                Debug.Log("[AgentFloorPlacement] 📡 Evento NavMeshGenerated recibido");
            }

            if (_navMeshWaitCoroutine != null)
            {
                StopCoroutine(_navMeshWaitCoroutine);
            }

            _navMeshWaitCoroutine = StartCoroutine(WaitForNavMeshAndPlace());
        }

        private void OnModelLoaded(ModelLoadedEvent evt)
        {
            if (_verboseLogs)
            {
                Debug.Log($"[AgentFloorPlacement] 📦 Modelo cargado: {evt.ModelName}");
            }

            _isPlaced = false;
            _allSurfaces.Clear();
            _modelBounds = default;
            _consecutiveCorrections = 0; // ✅ Reset contador
            _waitingForNavMesh = true;
        }

        private IEnumerator WaitForNavMeshAndPlace()
        {
            if (_verboseLogs)
            {
                Debug.Log("[AgentFloorPlacement] ⏳ Esperando consolidación de NavMesh...");
            }

            int attempts = 0;
            bool navMeshReady = false;

            yield return null;
            yield return null;

            while (attempts < _maxNavMeshWaitAttempts && !navMeshReady)
            {
                attempts++;

                if (IsNavMeshReady())
                {
                    navMeshReady = true;
                    
                    if (_verboseLogs)
                    {
                        Debug.Log($"[AgentFloorPlacement] ✅ NavMesh listo después de {attempts} intentos");
                    }
                    
                    break;
                }

                if (_verboseLogs && attempts % 3 == 0)
                {
                    Debug.Log($"[AgentFloorPlacement] ⏳ Esperando NavMesh... (intento {attempts}/{_maxNavMeshWaitAttempts})");
                }

                yield return new WaitForSeconds(_navMeshCheckDelay);
            }

            if (navMeshReady)
            {
                yield return null;

                bool success = PlaceAgentOnFloor();
                
                if (success)
                {
                    _waitingForNavMesh = false;
                    _consecutiveCorrections = 0; // ✅ Reset
                }
                else
                {
                    Debug.LogWarning("[AgentFloorPlacement] ⚠️ Posicionamiento falló a pesar de NavMesh listo");
                }
            }
            else
            {
                Debug.LogError($"[AgentFloorPlacement] ❌ NavMesh no estuvo listo después de {attempts} intentos ({attempts * _navMeshCheckDelay}s)");
                _waitingForNavMesh = false;
            }

            _navMeshWaitCoroutine = null;
        }

        #endregion

        #region Public API

        public bool PlaceAgentOnFloor()
        {
            if (_verboseLogs)
            {
                Debug.Log("==========================================");
                Debug.Log("[AgentFloorPlacement] 🎯 INICIANDO POSICIONAMIENTO EN PISO REAL...");
            }

            if (_agent == null)
            {
                Debug.LogError("[AgentFloorPlacement] ❌ NavMeshAgent no disponible");
                return false;
            }

            if (!IsNavMeshReady())
            {
                Debug.LogWarning("[AgentFloorPlacement] ⚠️ NavMesh no está listo.");
                
                if (_navMeshWaitCoroutine == null)
                {
                    _waitingForNavMesh = true;
                    _navMeshWaitCoroutine = StartCoroutine(WaitForNavMeshAndPlace());
                }
                
                return false;
            }

            AnalyzeModelGeometry();

            if (_allSurfaces.Count == 0)
            {
                Debug.LogError("[AgentFloorPlacement] ❌ No se encontró geometría en el modelo.");
                Debug.LogError($"  LayerMask configurado: {LayerMaskToString(_modelLayers)}");
                return false;
            }

            Vector3? floorPosition = DetectRealFloor();

            if (!floorPosition.HasValue)
            {
                Debug.LogError("[AgentFloorPlacement] ❌ No se pudo detectar el piso real.");
                return false;
            }

            _detectedFloorPosition = floorPosition.Value;
            Vector3 agentTargetPosition = _detectedFloorPosition + Vector3.up * _heightOffset;

            bool success = PositionAgent(agentTargetPosition);

            if (success)
            {
                _isPlaced = true;
                _waitingForNavMesh = false;
                _consecutiveCorrections = 0; // ✅ Reset en éxito
                
                if (_verboseLogs)
                {
                    Debug.Log($"[AgentFloorPlacement] ✅ AGENTE POSICIONADO EXITOSAMENTE");
                    Debug.Log($"  Piso detectado: Y={_detectedFloorPosition.y:F3}m");
                    Debug.Log($"  Agente final: Y={transform.position.y:F3}m");
                    Debug.Log($"  Agente on NavMesh: {_agent.isOnNavMesh}");
                    Debug.Log("==========================================");
                }
            }
            else
            {
                Debug.LogError("[AgentFloorPlacement] ❌ Error posicionando agente");
            }

            return success;
        }

        public void RepositionAgent()
        {
            _isPlaced = false;
            _consecutiveCorrections = 0; // ✅ Reset
            PlaceAgentOnFloor();
        }

        public float GetFloorHeight() => _detectedFloorPosition.y;

        public bool IsCorrectlyPlaced()
        {
            if (!_isPlaced || _agent == null)
                return false;

            if (!_agent.isOnNavMesh)
                return false;

            float distanceToFloor = Mathf.Abs(transform.position.y - _detectedFloorPosition.y);
            return distanceToFloor < _floatingTolerance;
        }

        #endregion

        #region NavMesh Validation

        private bool IsNavMeshReady()
        {
            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
            
            if (triangulation.vertices == null || triangulation.vertices.Length == 0)
                return false;

            if (triangulation.indices == null || triangulation.indices.Length == 0)
                return false;

            if (triangulation.areas == null || triangulation.areas.Length == 0)
                return false;

            NavMeshHit hit;
            Vector3 samplePosition = transform.position;
            
            if (!NavMesh.SamplePosition(samplePosition, out hit, _navMeshSampleRadius, NavMesh.AllAreas))
            {
                if (!NavMesh.SamplePosition(Vector3.zero, out hit, _navMeshSampleRadius * 2f, NavMesh.AllAreas))
                {
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region Geometry Analysis

        private void AnalyzeModelGeometry()
        {
            _allSurfaces.Clear();

            Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            List<Renderer> validRenderers = new List<Renderer>();

            foreach (Renderer r in renderers)
            {
                if ((_modelLayers.value & (1 << r.gameObject.layer)) != 0)
                {
                    validRenderers.Add(r);
                }
            }

            if (validRenderers.Count == 0)
            {
                Debug.LogError("[AgentFloorPlacement] ❌ No hay geometría en las capas configuradas.");
                return;
            }

            _modelBounds = validRenderers[0].bounds;
            for (int i = 1; i < validRenderers.Count; i++)
            {
                _modelBounds.Encapsulate(validRenderers[i].bounds);
            }

            if (_verboseLogs)
            {
                Debug.Log($"[AgentFloorPlacement] 📊 Análisis: {validRenderers.Count} renderers");
            }

            int horizontalCount = 0;
            foreach (Renderer r in validRenderers)
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                    continue;

                bool isHorizontal = IsHorizontalSurface(mf, r.transform);
                if (isHorizontal) horizontalCount++;

                Bounds bounds = r.bounds;
                float area = bounds.size.x * bounds.size.z;

                _allSurfaces.Add(new SurfaceInfo
                {
                    name = r.name,
                    height = bounds.min.y,
                    area = area,
                    bounds = bounds,
                    isHorizontal = isHorizontal,
                    type = SurfaceType.Unknown
                });
            }
        }

        private Vector3? DetectRealFloor()
        {
            if (_allSurfaces.Count == 0)
                return null;

            ClassifySurfaces();

            if (_useAbsoluteLowest)
            {
                float absoluteLowest = _modelBounds.min.y;
                
                if (_verboseLogs)
                {
                    Debug.Log($"[AgentFloorPlacement] ✅ Usando punto más bajo: Y={absoluteLowest:F3}m");
                }

                return new Vector3(_modelBounds.center.x, absoluteLowest, _modelBounds.center.z);
            }

            var floorCandidates = _allSurfaces
                .Where(s => s.type == SurfaceType.Floor && s.isHorizontal && s.area >= _minFloorArea)
                .OrderBy(s => s.height)
                .ToList();

            if (floorCandidates.Count > 0)
            {
                var floor = floorCandidates.First();
                return new Vector3(_modelBounds.center.x, floor.height, _modelBounds.center.z);
            }

            return new Vector3(_modelBounds.center.x, _modelBounds.min.y, _modelBounds.center.z);
        }

        private void ClassifySurfaces()
        {
            float minY = _modelBounds.min.y;
            float maxY = _modelBounds.max.y;
            float totalHeight = maxY - minY;

            float ceilingThreshold = maxY - (totalHeight * _ignoreTopPercent);
            float floorZone = minY + (totalHeight * 0.2f);

            foreach (var surface in _allSurfaces)
            {
                if (surface.height >= ceilingThreshold)
                    surface.type = SurfaceType.Ceiling;
                else if (surface.height <= floorZone)
                    surface.type = SurfaceType.Floor;
                else
                    surface.type = SurfaceType.Furniture;
            }
        }

        private bool IsHorizontalSurface(MeshFilter mf, Transform t)
        {
            Mesh mesh = mf.sharedMesh;
            Vector3[] normals = mesh.normals;

            if (normals == null || normals.Length == 0)
                return false;

            Vector3 avgNormal = Vector3.zero;
            foreach (Vector3 n in normals)
            {
                avgNormal += t.TransformDirection(n);
            }
            avgNormal = (avgNormal / normals.Length).normalized;

            float angle = Vector3.Angle(avgNormal, Vector3.up);
            return angle < 30f;
        }

        #endregion

        #region Agent Positioning

        private bool PositionAgent(Vector3 targetPosition)
        {
            if (_agent == null)
                return false;

            // ESTRATEGIA 1: Warp si ya está en NavMesh
            if (_agent.isOnNavMesh)
            {
                NavMeshHit hit;
                if (NavMesh.SamplePosition(targetPosition, out hit, _navMeshSampleRadius, NavMesh.AllAreas))
                {
                    _agent.Warp(hit.position);
                    
                    if (_verboseLogs)
                    {
                        Debug.Log($"[AgentFloorPlacement] ✅ Agente warped: {hit.position}");
                    }
                    
                    return true;
                }
            }

            // ESTRATEGIA 2: Reposicionamiento manual
            bool wasEnabled = _agent.enabled;
            _agent.enabled = false;
            
            transform.position = targetPosition;
            
            _agent.enabled = wasEnabled;

            if (_agent.isOnNavMesh)
            {
                if (_verboseLogs)
                {
                    Debug.Log($"[AgentFloorPlacement] ✅ Agente en NavMesh después de reactivación");
                }
                return true;
            }

            // ESTRATEGIA 3: Posición cercana válida
            NavMeshHit nearbyHit;
            if (NavMesh.SamplePosition(targetPosition, out nearbyHit, _navMeshSampleRadius, NavMesh.AllAreas))
            {
                _agent.enabled = false;
                transform.position = nearbyHit.position;
                _agent.enabled = wasEnabled;

                if (_agent.isOnNavMesh)
                {
                    return true;
                }
            }

            return true;
        }

        #endregion

        #region Floating Correction

        /// <summary>
        /// ✅ FIXED: Previene loops infinitos con múltiples salvaguardas
        /// </summary>
        private void CheckAndCorrectFloating()
        {
            if (!_isPlaced || _detectedFloorPosition == Vector3.zero)
                return;

            if (!IsNavMeshReady())
                return;

            // ✅ SALVAGUARDA 1: Limitar frecuencia de correcciones
            if (Time.time - _lastCorrectionTime < MIN_TIME_BETWEEN_CORRECTIONS)
                return;

            // ✅ SALVAGUARDA 2: Limitar correcciones consecutivas
            if (_consecutiveCorrections >= MAX_CONSECUTIVE_CORRECTIONS)
            {
                if (_verboseLogs)
                {
                    Debug.LogWarning($"[AgentFloorPlacement] ⚠️ Máximo de correcciones consecutivas alcanzado ({MAX_CONSECUTIVE_CORRECTIONS}). Deshabilitando auto-corrección.");
                }
                _autoCorrectFloating = false;
                return;
            }

            float distanceToFloor = transform.position.y - _detectedFloorPosition.y;

            // ✅ SALVAGUARDA 3: Tolerancia aumentada
            if (distanceToFloor > _floatingTolerance)
            {
                // ✅ SALVAGUARDA 4: Verificar que realmente necesita corrección
                if (distanceToFloor < _floatingTolerance + _minMovementThreshold)
                {
                    // Diferencia muy pequeña, ignorar
                    return;
                }

                Debug.LogWarning($"[AgentFloorPlacement] ⚠️ FLOTACIÓN DETECTADA: {distanceToFloor:F3}m");
                
                _consecutiveCorrections++;
                _lastCorrectionTime = Time.time;
                
                PlaceAgentOnFloor();
            }
            else if (_agent != null && !_agent.isOnNavMesh)
            {
                Debug.LogWarning($"[AgentFloorPlacement] ⚠️ Agente NO en NavMesh. Reposicionando...");
                
                _consecutiveCorrections++;
                _lastCorrectionTime = Time.time;
                
                PlaceAgentOnFloor();
            }
            else
            {
                // ✅ Todo OK, resetear contador
                if (_consecutiveCorrections > 0)
                {
                    _consecutiveCorrections = 0;
                }
            }
        }

        #endregion

        #region Helpers

        private string LayerMaskToString(LayerMask mask)
        {
            if (mask.value == -1)
                return "Everything";

            List<string> layers = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((mask.value & (1 << i)) != 0)
                {
                    string layerName = LayerMask.LayerToName(i);
                    layers.Add(string.IsNullOrEmpty(layerName) ? $"Layer {i}" : layerName);
                }
            }
            return layers.Count > 0 ? string.Join(", ", layers) : "None";
        }

        #endregion

        #region Debug

        [ContextMenu("🎯 Place Agent on Real Floor")]
        private void DebugPlace()
        {
            PlaceAgentOnFloor();
        }

        [ContextMenu("🔄 Force Reposition")]
        private void DebugForceReposition()
        {
            RepositionAgent();
        }

        [ContextMenu("✅ Check NavMesh Status")]
        private void DebugCheckNavMesh()
        {
            Debug.Log("========== NAVMESH STATUS ==========");
            Debug.Log($"NavMesh Ready: {IsNavMeshReady()}");
            
            if (_agent != null)
            {
                Debug.Log($"Agent on NavMesh: {_agent.isOnNavMesh}");
                Debug.Log($"Agent Position: {transform.position}");
            }
            
            Debug.Log($"Is Placed: {_isPlaced}");
            Debug.Log($"Consecutive Corrections: {_consecutiveCorrections}/{MAX_CONSECUTIVE_CORRECTIONS}");
            Debug.Log($"Auto Correct Enabled: {_autoCorrectFloating}");
            Debug.Log("===================================");
        }

        private void OnDrawGizmos()
        {
            if (!_debugVisualization)
                return;

            if (_modelBounds.size != Vector3.zero)
            {
                Gizmos.color = new Color(1f, 1f, 0f, 0.15f);
                Gizmos.DrawCube(_modelBounds.center, _modelBounds.size);
                
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireCube(_modelBounds.center, _modelBounds.size);

                Gizmos.color = new Color(0f, 1f, 0f, 0.7f);
                Vector3 floorLineStart = _modelBounds.min;
                Vector3 floorLineEnd = _modelBounds.max;
                floorLineStart.y = _modelBounds.min.y;
                floorLineEnd.y = _modelBounds.min.y;
                Gizmos.DrawLine(floorLineStart, floorLineEnd);
            }

            foreach (var surface in _allSurfaces)
            {
                if (!surface.isHorizontal)
                    continue;

                Color color = surface.type switch
                {
                    SurfaceType.Floor => new Color(0f, 1f, 0f, 0.3f),
                    SurfaceType.Furniture => new Color(1f, 1f, 0f, 0.3f),
                    SurfaceType.Ceiling => new Color(1f, 0f, 0f, 0.3f),
                    _ => new Color(0.5f, 0.5f, 0.5f, 0.3f)
                };

                Gizmos.color = color;
                Gizmos.DrawCube(surface.bounds.center, surface.bounds.size);
            }

            if (_isPlaced && _detectedFloorPosition != Vector3.zero)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawSphere(_detectedFloorPosition, 0.2f);
                
                Gizmos.color = new Color(0f, 1f, 0f, 0.5f);
                Gizmos.DrawLine(transform.position, _detectedFloorPosition);

                Vector3 planeCenter = _modelBounds.center;
                planeCenter.y = _detectedFloorPosition.y;
                Vector3 planeSize = new Vector3(_modelBounds.size.x, 0.02f, _modelBounds.size.z);
                
                Gizmos.color = new Color(0f, 1f, 0f, 0.4f);
                Gizmos.DrawCube(planeCenter, planeSize);
            }

            if (_waitingForNavMesh)
            {
                Gizmos.color = Color.yellow;
                float pulseSize = 0.3f + Mathf.PingPong(Time.time * 2f, 0.2f);
                Gizmos.DrawWireSphere(transform.position + Vector3.up * 2f, pulseSize);
            }
            else if (_isPlaced && IsCorrectlyPlaced())
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(transform.position + Vector3.up * 2f, 0.3f);
            }
            else if (_isPlaced && !IsCorrectlyPlaced())
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(transform.position + Vector3.up * 2f, 0.3f);
            }
        }

        #endregion
    }
}