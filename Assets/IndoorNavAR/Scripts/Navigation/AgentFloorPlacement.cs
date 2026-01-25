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
    /// ✅ ARREGLADO: Espera a que el NavMesh esté listo antes de posicionar
    /// ✅ ARREGLADO: Reacciona automáticamente a carga de modelos
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
        [SerializeField] private float _checkInterval = 1f;

        [Header("🔧 Debug")]
        [SerializeField] private bool _debugVisualization = true;
        [SerializeField] private bool _verboseLogs = true;

        private NavMeshAgent _agent;
        private float _lastCheck;
        private Vector3 _detectedFloorPosition;
        private bool _isPlaced;
        private List<SurfaceInfo> _allSurfaces = new List<SurfaceInfo>();
        private Bounds _modelBounds;
        private bool _waitingForNavMesh;

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
            Floor,      // Piso real (más bajo)
            Furniture,  // Mesas, sillas (intermedio)
            Ceiling,    // Techo (más alto)
            Unknown
        }

        #region Unity Lifecycle

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            
            if (_agent == null)
            {
                Debug.LogError("[AgentFloorPlacement] NavMeshAgent no encontrado.");
                enabled = false;
            }
        }

        private void OnEnable()
        {
            // ✅ NUEVO: Suscribirse a eventos del sistema
            EventBus.Instance?.Subscribe<NavMeshGeneratedEvent>(OnNavMeshGenerated);
            EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        private void OnDisable()
        {
            // ✅ NUEVO: Desuscribirse para evitar memory leaks
            EventBus.Instance?.Unsubscribe<NavMeshGeneratedEvent>(OnNavMeshGenerated);
            EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        private void Start()
        {
            if (_placeOnStart)
            {
                // Intentar solo si NavMesh ya existe
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

        /// <summary>
        /// ✅ NUEVO: Se ejecuta cuando se genera/actualiza el NavMesh
        /// </summary>
        private void OnNavMeshGenerated(NavMeshGeneratedEvent evt)
        {
            if (!evt.Success)
            {
                if (_verboseLogs)
                {
                    Debug.LogWarning("[AgentFloorPlacement] ⚠️ NavMesh falló, no se puede posicionar agente");
                }
                return;
            }

            if (_verboseLogs)
            {
                Debug.Log("[AgentFloorPlacement] 📡 Evento NavMeshGenerated recibido");
            }

            // Esperar 1 frame para que el NavMesh se estabilice
            StartCoroutine(PlaceAgentNextFrame());
        }

        /// <summary>
        /// ✅ NUEVO: Se ejecuta cuando se carga un modelo
        /// </summary>
        private void OnModelLoaded(ModelLoadedEvent evt)
        {
            if (_verboseLogs)
            {
                Debug.Log($"[AgentFloorPlacement] 📦 Modelo cargado: {evt.ModelName}");
            }

            // Resetear estado
            _isPlaced = false;
            _allSurfaces.Clear();

            // NO posicionar inmediatamente - esperar a que se genere el NavMesh
            _waitingForNavMesh = true;
        }

        /// <summary>
        /// ✅ NUEVO: Coloca el agente en el siguiente frame
        /// </summary>
        private IEnumerator PlaceAgentNextFrame()
        {
            // Esperar 2 frames para total estabilidad
            yield return null;
            yield return null;

            if (IsNavMeshReady())
            {
                PlaceAgentOnFloor();
                _waitingForNavMesh = false;
            }
            else
            {
                if (_verboseLogs)
                {
                    Debug.LogWarning("[AgentFloorPlacement] ⚠️ NavMesh aún no listo después de esperar");
                }
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Posiciona el agente en el piso REAL (más bajo).
        /// ✅ ARREGLADO: Valida que NavMesh esté listo antes de intentar
        /// </summary>
        public bool PlaceAgentOnFloor()
        {
            if (_verboseLogs)
            {
                Debug.Log("==========================================");
                Debug.Log("[AgentFloorPlacement] 🎯 Detectando PISO REAL...");
            }

            // ✅ NUEVO: Verificar que NavMesh esté listo
            if (!IsNavMeshReady())
            {
                Debug.LogWarning("[AgentFloorPlacement] ⚠️ NavMesh no está listo. Espera a NavMeshGeneratedEvent.");
                return false;
            }

            // Analizar toda la geometría del modelo
            AnalyzeModelGeometry();

            if (_allSurfaces.Count == 0)
            {
                Debug.LogError("[AgentFloorPlacement] ❌ No se encontró geometría en el modelo.");
                Debug.LogError($"  LayerMask configurado: {LayerMaskToString(_modelLayers)}");
                return false;
            }

            // Detectar el piso real
            Vector3? floorPosition = DetectRealFloor();

            if (!floorPosition.HasValue)
            {
                Debug.LogError("[AgentFloorPlacement] ❌ No se pudo detectar el piso real.");
                return false;
            }

            _detectedFloorPosition = floorPosition.Value;
            Vector3 agentPosition = _detectedFloorPosition + Vector3.up * _heightOffset;

            // Posicionar agente
            bool success = PositionAgent(agentPosition);

            if (success)
            {
                _isPlaced = true;
                
                if (_verboseLogs)
                {
                    Debug.Log($"[AgentFloorPlacement] ✅ Agente posicionado en PISO REAL");
                    Debug.Log($"  Piso detectado: Y={_detectedFloorPosition.y:F3}m");
                    Debug.Log($"  Agente final: Y={transform.position.y:F3}m");
                    Debug.Log($"  Bounds modelo: Min Y={_modelBounds.min.y:F3}m, Max Y={_modelBounds.max.y:F3}m");
                    Debug.Log("==========================================");
                }
            }

            return success;
        }

        public void RepositionAgent()
        {
            _isPlaced = false;
            PlaceAgentOnFloor();
        }

        public float GetFloorHeight() => _detectedFloorPosition.y;

        #endregion

        #region NavMesh Validation

        /// <summary>
        /// ✅ NUEVO: Verifica si hay un NavMesh válido disponible
        /// </summary>
        private bool IsNavMeshReady()
        {
            // Verificar si hay triangulación NavMesh
            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
            
            if (triangulation.vertices == null || triangulation.vertices.Length == 0)
            {
                return false;
            }

            // Verificar que el agente pueda usar NavMesh
            if (_agent != null && !_agent.isOnNavMesh)
            {
                // Intentar samplear posición actual
                NavMeshHit hit;
                if (!NavMesh.SamplePosition(transform.position, out hit, 10f, NavMesh.AllAreas))
                {
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region Geometry Analysis

        /// <summary>
        /// Analiza toda la geometría del modelo.
        /// </summary>
        private void AnalyzeModelGeometry()
        {
            _allSurfaces.Clear();

            Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            List<Renderer> validRenderers = new List<Renderer>();

            // Filtrar por capa
            foreach (Renderer r in renderers)
            {
                if ((_modelLayers.value & (1 << r.gameObject.layer)) != 0)
                {
                    validRenderers.Add(r);
                }
            }

            if (validRenderers.Count == 0)
            {
                Debug.LogError("[AgentFloorPlacement] No hay geometría en las capas configuradas.");
                return;
            }

            // Calcular bounds completo del modelo
            _modelBounds = validRenderers[0].bounds;
            for (int i = 1; i < validRenderers.Count; i++)
            {
                _modelBounds.Encapsulate(validRenderers[i].bounds);
            }

            if (_verboseLogs)
            {
                Debug.Log($"[AgentFloorPlacement] 📊 Analizando modelo...");
                Debug.Log($"  Total renderers: {validRenderers.Count}");
                Debug.Log($"  Bounds: Y min={_modelBounds.min.y:F3}m, max={_modelBounds.max.y:F3}m");
                Debug.Log($"  Altura total: {_modelBounds.size.y:F3}m");
            }

            // Analizar cada superficie
            foreach (Renderer r in validRenderers)
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                    continue;

                bool isHorizontal = IsHorizontalSurface(mf, r.transform);
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

            if (_verboseLogs)
            {
                Debug.Log($"  Superficies detectadas: {_allSurfaces.Count}");
                Debug.Log($"  Horizontales: {_allSurfaces.Count(s => s.isHorizontal)}");
            }
        }

        /// <summary>
        /// Detecta el piso REAL (más bajo, ignorando mesas y techo).
        /// </summary>
        private Vector3? DetectRealFloor()
        {
            if (_allSurfaces.Count == 0)
                return null;

            // Clasificar superficies
            ClassifySurfaces();

            // ESTRATEGIA 1: Usar el punto más bajo del modelo (SIEMPRE CORRECTO)
            if (_useAbsoluteLowest)
            {
                float absoluteLowest = _modelBounds.min.y;
                
                if (_verboseLogs)
                {
                    Debug.Log($"[AgentFloorPlacement] ✅ Usando punto MÁS BAJO del modelo");
                    Debug.Log($"  Altura: Y={absoluteLowest:F3}m");
                }

                return new Vector3(transform.position.x, absoluteLowest, transform.position.z);
            }

            // ESTRATEGIA 2: Buscar superficie horizontal más baja y grande
            var floorCandidates = _allSurfaces
                .Where(s => s.type == SurfaceType.Floor && s.isHorizontal && s.area >= _minFloorArea)
                .OrderBy(s => s.height)
                .ToList();

            if (floorCandidates.Count > 0)
            {
                var floor = floorCandidates.First();
                
                if (_verboseLogs)
                {
                    Debug.Log($"[AgentFloorPlacement] ✅ Piso detectado por análisis");
                    Debug.Log($"  Superficie: {floor.name}");
                    Debug.Log($"  Altura: Y={floor.height:F3}m");
                    Debug.Log($"  Área: {floor.area:F2}m²");
                }

                return new Vector3(transform.position.x, floor.height, transform.position.z);
            }

            // FALLBACK: Usar bounds mínimo
            if (_verboseLogs)
            {
                Debug.Log($"[AgentFloorPlacement] ⚠️ Usando fallback: bounds mínimo");
            }

            return new Vector3(transform.position.x, _modelBounds.min.y, transform.position.z);
        }

        /// <summary>
        /// Clasifica superficies en: Piso, Muebles, Techo.
        /// </summary>
        private void ClassifySurfaces()
        {
            float minY = _modelBounds.min.y;
            float maxY = _modelBounds.max.y;
            float totalHeight = maxY - minY;

            // Definir zonas
            float ceilingThreshold = maxY - (totalHeight * _ignoreTopPercent);
            float floorZone = minY + (totalHeight * 0.2f); // 20% inferior = zona de piso

            if (_verboseLogs)
            {
                Debug.Log($"[AgentFloorPlacement] 🔍 Clasificando superficies...");
                Debug.Log($"  Zona piso: Y < {floorZone:F3}m");
                Debug.Log($"  Zona techo: Y > {ceilingThreshold:F3}m");
            }

            foreach (var surface in _allSurfaces)
            {
                // Clasificar por altura
                if (surface.height >= ceilingThreshold)
                {
                    surface.type = SurfaceType.Ceiling;
                }
                else if (surface.height <= floorZone)
                {
                    surface.type = SurfaceType.Floor;
                }
                else
                {
                    surface.type = SurfaceType.Furniture;
                }

                if (_verboseLogs && surface.isHorizontal)
                {
                    string icon = surface.type switch
                    {
                        SurfaceType.Floor => "🟢 PISO",
                        SurfaceType.Furniture => "🟡 MUEBLE",
                        SurfaceType.Ceiling => "🔴 TECHO",
                        _ => "⚪ DESCONOCIDO"
                    };

                    Debug.Log($"  {icon} - {surface.name} (Y={surface.height:F3}m, área={surface.area:F2}m²)");
                }
            }

            int floors = _allSurfaces.Count(s => s.type == SurfaceType.Floor);
            int furniture = _allSurfaces.Count(s => s.type == SurfaceType.Furniture);
            int ceilings = _allSurfaces.Count(s => s.type == SurfaceType.Ceiling);

            if (_verboseLogs)
            {
                Debug.Log($"[AgentFloorPlacement] 📊 Clasificación:");
                Debug.Log($"  Pisos: {floors}");
                Debug.Log($"  Muebles: {furniture}");
                Debug.Log($"  Techos: {ceilings}");
            }
        }

        /// <summary>
        /// Verifica si una superficie es horizontal.
        /// </summary>
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
            return angle < 30f; // Casi horizontal
        }

        #endregion

        #region Agent Positioning

        /// <summary>
        /// ✅ MEJORADO: Posiciona el agente validando NavMesh
        /// </summary>
        private bool PositionAgent(Vector3 targetPosition)
        {
            // Si el agente ya está en NavMesh, usar Warp
            if (_agent.isOnNavMesh)
            {
                NavMeshHit hit;
                if (NavMesh.SamplePosition(targetPosition, out hit, 5f, NavMesh.AllAreas))
                {
                    _agent.Warp(hit.position);
                    
                    if (_verboseLogs)
                    {
                        Debug.Log($"[AgentFloorPlacement] ✅ Agente warped a NavMesh: {hit.position}");
                    }
                    
                    return true;
                }
                else
                {
                    Debug.LogWarning("[AgentFloorPlacement] ⚠️ No se pudo samplear posición en NavMesh");
                }
            }

            // Fallback: posicionar directamente (puede causar flotación temporal)
            transform.position = targetPosition;
            
            if (_verboseLogs)
            {
                Debug.Log($"[AgentFloorPlacement] ⚠️ Agente posicionado directamente (sin NavMesh)");
            }
            
            return true;
        }

        #endregion

        #region Floating Correction

        /// <summary>
        /// ✅ MEJORADO: Solo corrige si NavMesh está listo
        /// </summary>
        private void CheckAndCorrectFloating()
        {
            if (!IsNavMeshReady())
                return;

            // Verificar si el agente está muy por encima del piso detectado
            float distanceToFloor = transform.position.y - _detectedFloorPosition.y;

            if (distanceToFloor > 0.5f)
            {
                if (_verboseLogs)
                {
                    Debug.LogWarning($"[AgentFloorPlacement] ⚠️ Flotación detectada ({distanceToFloor:F3}m). Corrigiendo...");
                }
                
                PlaceAgentOnFloor();
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

        [ContextMenu("🔍 Analyze Model Geometry")]
        private void DebugAnalyze()
        {
            AnalyzeModelGeometry();
            
            if (_allSurfaces.Count > 0)
            {
                ClassifySurfaces();
                
                Debug.Log("========== RESUMEN ==========");
                Debug.Log($"Punto más bajo: Y={_modelBounds.min.y:F3}m");
                Debug.Log($"Punto más alto: Y={_modelBounds.max.y:F3}m");
                Debug.Log($"Total superficies: {_allSurfaces.Count}");
                
                var floors = _allSurfaces.Where(s => s.type == SurfaceType.Floor).ToList();
                if (floors.Count > 0)
                {
                    Debug.Log($"\nPISOS DETECTADOS ({floors.Count}):");
                    foreach (var f in floors.OrderBy(s => s.height))
                    {
                        Debug.Log($"  - {f.name}: Y={f.height:F3}m, área={f.area:F2}m²");
                    }
                }
                
                Debug.Log("============================");
            }
        }

        [ContextMenu("✅ Check NavMesh Status")]
        private void DebugCheckNavMesh()
        {
            Debug.Log("========== NAVMESH STATUS ==========");
            Debug.Log($"NavMesh Ready: {IsNavMeshReady()}");
            
            NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
            Debug.Log($"NavMesh Vertices: {tri.vertices?.Length ?? 0}");
            Debug.Log($"NavMesh Triangles: {(tri.indices?.Length ?? 0) / 3}");
            
            if (_agent != null)
            {
                Debug.Log($"Agent on NavMesh: {_agent.isOnNavMesh}");
                Debug.Log($"Agent Position: {transform.position}");
            }
            
            Debug.Log("===================================");
        }

        [ContextMenu("📊 Show Configuration")]
        private void DebugShowConfig()
        {
            Debug.Log("========== CONFIGURACIÓN ==========");
            Debug.Log($"Use Absolute Lowest: {_useAbsoluteLowest} ⭐");
            Debug.Log($"Ignore Top Percent: {_ignoreTopPercent * 100}%");
            Debug.Log($"Min Floor Area: {_minFloorArea}m²");
            Debug.Log($"Model Layers: {LayerMaskToString(_modelLayers)}");
            Debug.Log($"Height Offset: {_heightOffset}m");
            Debug.Log($"Place On Start: {_placeOnStart}");
            Debug.Log($"Auto Correct Floating: {_autoCorrectFloating}");
            Debug.Log("==================================");
        }

        private void OnDrawGizmos()
        {
            if (!_debugVisualization)
                return;

            // Bounds del modelo
            if (_modelBounds.size != Vector3.zero)
            {
                Gizmos.color = new Color(1f, 1f, 0f, 0.15f);
                Gizmos.DrawCube(_modelBounds.center, _modelBounds.size);
                
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireCube(_modelBounds.center, _modelBounds.size);

                // Línea del piso real (más bajo)
                Gizmos.color = new Color(0f, 1f, 0f, 0.7f);
                Vector3 floorLineStart = _modelBounds.min;
                Vector3 floorLineEnd = _modelBounds.max;
                floorLineStart.y = _modelBounds.min.y;
                floorLineEnd.y = _modelBounds.min.y;
                Gizmos.DrawLine(floorLineStart, floorLineEnd);
            }

            // Superficies clasificadas
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

            // Piso detectado y agente
            if (_isPlaced && _detectedFloorPosition != Vector3.zero)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawSphere(_detectedFloorPosition, 0.2f);
                
                Gizmos.color = new Color(0f, 1f, 0f, 0.5f);
                Gizmos.DrawLine(transform.position, _detectedFloorPosition);

                // Plano visual del piso
                Vector3 planeCenter = _modelBounds.center;
                planeCenter.y = _detectedFloorPosition.y;
                Vector3 planeSize = new Vector3(_modelBounds.size.x, 0.02f, _modelBounds.size.z);
                
                Gizmos.color = new Color(0f, 1f, 0f, 0.4f);
                Gizmos.DrawCube(planeCenter, planeSize);
            }

            // ✅ NUEVO: Indicador de estado
            if (_waitingForNavMesh)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(transform.position + Vector3.up * 2f, 0.3f);
            }
        }

        #endregion
    }
}