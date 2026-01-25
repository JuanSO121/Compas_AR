using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Sistema robusto de generación de superficie navegable para entornos AR escaneados.
    /// ✅ Detecta y bloquea SOLO muros perimetrales
    /// ✅ Ignora muebles y objetos internos
    /// </summary>
    public class RobustWalkableSurfaceGenerator : MonoBehaviour
    {
        [Header("🔍 Análisis de Geometría")]
        [SerializeField] private float _heightBinSize = 0.05f;
        [SerializeField] [Range(0.05f, 0.5f)] private float _minClusterDensity = 0.12f;
        [SerializeField] private float _maxFloorDeviation = 0.12f;
        
        [Header("🏗️ Reconstrucción de Superficie")]
        [SerializeField] private float _surfacePadding = 0.5f;
        [SerializeField] private float _surfaceThickness = 0.01f;
        [SerializeField] private float _surfaceOffsetBelow = 0.02f;
        
        [Header("🧱 Detección de Paredes")]
        [Tooltip("Ángulo MÍNIMO con horizontal para considerar pared")]
        [SerializeField] private float _wallNormalAngleMin = 30f;
        
        [Tooltip("Ángulo MÁXIMO con horizontal")]
        [SerializeField] private float _wallNormalAngleMax = 150f;
        
        [Tooltip("Altura mínima de triángulo pared")]
        [SerializeField] private float _minWallHeight = 0.05f;
        
        [Tooltip("Expandir volúmenes de pared para cerrar gaps")]
        [SerializeField] private float _wallVolumeExpansion = 0.2f;
        
        [Tooltip("Altura máxima sobre piso")]
        [SerializeField] private float _maxWallHeightAboveFloor = 10f;
        
        [Tooltip("Usar carving para obstáculos")]
        [SerializeField] private bool _useCarving = true;
        
        [Tooltip("Ignorar filtro de altura mínima")]
        [SerializeField] private bool _ignoreHeightFilter = true;
        
        [Tooltip("Ignorar filtro de distancia al piso")]
        [SerializeField] private bool _ignoreFloorDistanceFilter = true;
        
        [Tooltip("Agrupar triángulos cercanos")]
        [SerializeField] private float _wallClusterDistance = 0.3f;
        
        [Tooltip("Área mínima para considerar pared real (m²)")]
        [SerializeField] private float _minWallArea = 0.5f;
        
        [Tooltip("Mínimo de triángulos para considerar pared real")]
        [SerializeField] private int _minWallTriangles = 5;
        
        [Header("🚫 Obstáculos Interiores")]
        [Tooltip("Ignorar muebles/objetos (SOLO bloquear muros)")]
        [SerializeField] private bool _ignoreElevatedObstacles = true;
        
        [SerializeField] private float _elevatedThreshold = 0.25f;
        [SerializeField] private float _exclusionExpansion = 0.15f;
        
        [Header("⚙️ Configuración NavMesh")]
        [SerializeField] private int _navMeshLayer = 0;
        [SerializeField] private int _navMeshAreaNotWalkable = 1;
        [SerializeField] private float _voxelSize = 0.08f;
        [SerializeField] private int _minRegionArea = 2;
        
        [Header("🎯 Agente NavMesh")]
        [Tooltip("Radio del agente")]
        [SerializeField] private float _agentRadius = 0.15f;
        
        [Tooltip("Altura del agente")]
        [SerializeField] private float _agentHeight = 1.8f;
        
        [Header("🎯 Referencias")]
        [SerializeField] private MeshFilter _scannedMeshFilter;
        
        [Header("🐛 Debug")]
        [SerializeField] private bool _showDebugVisualization = true;
        [SerializeField] private bool _logDetailedAnalysis = true;
        [SerializeField] private bool _debugDrawWallNormals = true;
        
        // Estado interno
        private float _detectedFloorHeight;
        private Bounds _walkableArea;
        private GameObject _walkableSurface;
        private NavMeshSurface _navMeshSurface;
        private List<GameObject> _exclusionVolumes = new List<GameObject>();
        private List<NavMeshObstacle> _wallObstacles = new List<NavMeshObstacle>();
        
        private List<Vector3> _floorVertices = new List<Vector3>();
        private List<Vector3> _elevatedVertices = new List<Vector3>();
        private List<HeightCluster> _heightClusters;
        private List<WallSegment> _detectedWalls = new List<WallSegment>();
        private List<WallTriangle> _debugWallTriangles = new List<WallTriangle>();

        #region Unity Lifecycle

        private void OnEnable()
        {
            Core.Events.EventBus.Instance?.Subscribe<Core.Events.ModelLoadedEvent>(OnModelLoaded);
        }

        private void OnDisable()
        {
            Core.Events.EventBus.Instance?.Unsubscribe<Core.Events.ModelLoadedEvent>(OnModelLoaded);
        }

        #endregion

        #region Event Handlers

        private void OnModelLoaded(Core.Events.ModelLoadedEvent evt)
        {
            Debug.Log($"[RobustWalkableSurface] 📦 Modelo cargado: {evt.ModelName}");
            
            MeshFilter[] meshFilters = evt.ModelInstance.GetComponentsInChildren<MeshFilter>();
            
            if (meshFilters.Length == 0)
            {
                Debug.LogWarning("[RobustWalkableSurface] ⚠️ Modelo sin MeshFilter");
                return;
            }
            
            MeshFilter largestMesh = meshFilters
                .OrderByDescending(mf => mf.sharedMesh != null ? mf.sharedMesh.vertexCount : 0)
                .FirstOrDefault();
            
            if (largestMesh != null && largestMesh.sharedMesh != null)
            {
                SetScannedMesh(largestMesh);
                _ = GenerateWalkableSurfaceAsync();
            }
        }

        #endregion

        #region Public API

        public bool GenerateWalkableSurface()
        {
            Debug.Log("[RobustWalkableSurface] 🚀 Iniciando generación...");
            
            try
            {
                if (!ValidateInputMesh()) return false;
                if (!AnalyzeScannedGeometry()) return false;
                if (!DetectWalkableFloor()) return false;
                if (!ReconstructCleanFloorPlane()) return false;
                
                DetectWallsFromMesh();
                FilterRealWalls();
                CreateWallObstacles();
                
                if (!_ignoreElevatedObstacles)
                {
                    MarkElevatedSurfacesAsObstacles();
                }
                
                if (!BakeNavMesh()) return false;
                
                Debug.Log("[RobustWalkableSurface] ✅ Generación exitosa");
                LogResults();
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RobustWalkableSurface] ❌ Error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        public async System.Threading.Tasks.Task<bool> GenerateWalkableSurfaceAsync()
        {
            await System.Threading.Tasks.Task.Delay(500);
            return GenerateWalkableSurface();
        }

        public void Clear()
        {
            if (_walkableSurface != null) Destroy(_walkableSurface);
            foreach (var vol in _exclusionVolumes) if (vol != null) Destroy(vol);
            foreach (var obstacle in _wallObstacles) if (obstacle != null) Destroy(obstacle.gameObject);
            
            _exclusionVolumes.Clear();
            _wallObstacles.Clear();
            _floorVertices.Clear();
            _elevatedVertices.Clear();
            _detectedWalls.Clear();
            _debugWallTriangles.Clear();
            
            Debug.Log("[RobustWalkableSurface] 🧹 Limpiado");
        }

        public void SetScannedMesh(MeshFilter meshFilter)
        {
            _scannedMeshFilter = meshFilter;
            Debug.Log($"[RobustWalkableSurface] ✅ Mesh: {meshFilter.name} ({meshFilter.sharedMesh.vertexCount} vértices)");
        }

        #endregion

        #region Validation

        private bool ValidateInputMesh()
        {
            if (_scannedMeshFilter == null || _scannedMeshFilter.sharedMesh == null)
            {
                Debug.LogError("[RobustWalkableSurface] ❌ No hay mesh");
                return false;
            }
            
            Mesh mesh = _scannedMeshFilter.sharedMesh;
            if (mesh.vertices.Length == 0)
            {
                Debug.LogError("[RobustWalkableSurface] ❌ Mesh vacío");
                return false;
            }
            
            Debug.Log($"[RobustWalkableSurface] ✅ Mesh validado: {mesh.vertices.Length} vértices, {mesh.triangles.Length / 3} triángulos");
            return true;
        }

        #endregion

        #region Geometry Analysis

        private bool AnalyzeScannedGeometry()
        {
            Mesh mesh = _scannedMeshFilter.sharedMesh;
            Transform meshTransform = _scannedMeshFilter.transform;
            
            List<Vector3> worldVertices = new List<Vector3>();
            foreach (Vector3 v in mesh.vertices)
                worldVertices.Add(meshTransform.TransformPoint(v));
            
            if (worldVertices.Count == 0) return false;
            
            _heightClusters = BuildHeightHistogram(worldVertices);
            
            if (_logDetailedAnalysis)
                LogHistogramDetails(_heightClusters);
            
            Debug.Log($"[RobustWalkableSurface] ✅ Análisis: {_heightClusters.Count} clusters");
            return true;
        }

        private List<HeightCluster> BuildHeightHistogram(List<Vector3> vertices)
        {
            float minY = vertices.Min(v => v.y);
            float maxY = vertices.Max(v => v.y);
            
            int numBins = Mathf.CeilToInt((maxY - minY) / _heightBinSize);
            numBins = Mathf.Max(1, numBins);
            
            Dictionary<int, HeightCluster> bins = new Dictionary<int, HeightCluster>();
            
            foreach (Vector3 vertex in vertices)
            {
                int binIndex = Mathf.FloorToInt((vertex.y - minY) / _heightBinSize);
                binIndex = Mathf.Clamp(binIndex, 0, numBins - 1);
                
                if (!bins.ContainsKey(binIndex))
                {
                    bins[binIndex] = new HeightCluster
                    {
                        binIndex = binIndex,
                        minHeight = minY + binIndex * _heightBinSize,
                        maxHeight = minY + (binIndex + 1) * _heightBinSize,
                        vertices = new List<Vector3>()
                    };
                }
                
                bins[binIndex].vertices.Add(vertex);
            }
            
            int totalVertices = vertices.Count;
            foreach (var cluster in bins.Values)
            {
                cluster.centerHeight = (cluster.minHeight + cluster.maxHeight) / 2f;
                cluster.vertexCount = cluster.vertices.Count;
                cluster.density = cluster.vertexCount / (float)totalVertices;
                cluster.bounds = CalculateBounds(cluster.vertices);
            }
            
            return bins.Values.OrderBy(c => c.centerHeight).ToList();
        }

        #endregion

        #region Floor Detection

        private bool DetectWalkableFloor()
        {
            if (_heightClusters == null || _heightClusters.Count == 0) return false;
            
            float minHeight = _heightClusters[0].centerHeight;
            float maxHeight = _heightClusters[_heightClusters.Count - 1].centerHeight;
            float searchThreshold = minHeight + ((maxHeight - minHeight) * 0.3f);
            
            var candidates = _heightClusters
                .Where(c => c.centerHeight <= searchThreshold)
                .Where(c => c.density >= _minClusterDensity)
                .Where(c => c.bounds.size.x * c.bounds.size.z > 1f)
                .OrderByDescending(c => c.density)
                .ThenByDescending(c => c.bounds.size.x * c.bounds.size.z)
                .ThenBy(c => c.centerHeight)
                .ToList();
            
            if (candidates.Count == 0)
                candidates = new List<HeightCluster> { _heightClusters[0] };
            
            HeightCluster floorCluster = candidates[0];
            
            _detectedFloorHeight = floorCluster.centerHeight;
            _floorVertices = floorCluster.vertices
                .Where(v => Mathf.Abs(v.y - floorCluster.centerHeight) <= _maxFloorDeviation)
                .ToList();
            
            _walkableArea = CalculateBounds(_floorVertices);
            
            _elevatedVertices = _heightClusters
                .Where(c => c.centerHeight > _detectedFloorHeight + _elevatedThreshold)
                .SelectMany(c => c.vertices)
                .ToList();
            
            Debug.Log($"[RobustWalkableSurface] ✅ Piso: Y={_detectedFloorHeight:F3}m, {_floorVertices.Count} vértices, {_walkableArea.size.x:F2}×{_walkableArea.size.z:F2}m");
            return true;
        }

        #endregion

        #region Floor Reconstruction

        private bool ReconstructCleanFloorPlane()
        {
            if (_walkableSurface != null) Destroy(_walkableSurface);
            
            _walkableSurface = new GameObject("WalkableSurface");
            _walkableSurface.layer = _navMeshLayer;
            _walkableSurface.isStatic = true;
            
            Vector3 planeCenter = _walkableArea.center;
            planeCenter.y = _detectedFloorHeight - _surfaceOffsetBelow;
            _walkableSurface.transform.position = planeCenter;
            
            float width = _walkableArea.size.x + (_surfacePadding * 2f);
            float depth = _walkableArea.size.z + (_surfacePadding * 2f);
            
            Mesh planeMesh = CreatePlaneMesh(width, depth);
            
            MeshFilter meshFilter = _walkableSurface.AddComponent<MeshFilter>();
            meshFilter.mesh = planeMesh;
            
            MeshRenderer meshRenderer = _walkableSurface.AddComponent<MeshRenderer>();
            meshRenderer.material = CreateFloorMaterial();
            
            MeshCollider meshCollider = _walkableSurface.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = planeMesh;
            meshCollider.convex = false;
            
            _navMeshSurface = _walkableSurface.AddComponent<NavMeshSurface>();
            ConfigureNavMeshSurface();
            
            Debug.Log($"[RobustWalkableSurface] ✅ Plano: {width:F2}×{depth:F2}m");
            return true;
        }

        private Mesh CreatePlaneMesh(float width, float depth)
        {
            Mesh mesh = new Mesh { name = "WalkablePlaneMesh" };
            
            float halfW = width / 2f;
            float halfD = depth / 2f;
            float halfT = _surfaceThickness / 2f;
            
            Vector3[] vertices = new Vector3[]
            {
                new Vector3(-halfW, halfT, -halfD), new Vector3(halfW, halfT, -halfD),
                new Vector3(halfW, halfT, halfD), new Vector3(-halfW, halfT, halfD),
                new Vector3(-halfW, -halfT, -halfD), new Vector3(halfW, -halfT, -halfD),
                new Vector3(halfW, -halfT, halfD), new Vector3(-halfW, -halfT, halfD)
            };
            
            int[] triangles = new int[]
            {
                0,2,1, 0,3,2, 4,5,6, 4,6,7,
                0,1,5, 0,5,4, 3,7,6, 3,6,2,
                0,4,7, 0,7,3, 1,2,6, 1,6,5
            };
            
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            
            return mesh;
        }

        private Material CreateFloorMaterial()
        {
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = _showDebugVisualization 
                ? new Color(0.2f, 0.8f, 0.2f, 0.4f) 
                : new Color(0.5f, 0.5f, 0.5f, 0f);
            return mat;
        }

        private void ConfigureNavMeshSurface()
        {
            _navMeshSurface.agentTypeID = 0;
            _navMeshSurface.collectObjects = CollectObjects.Volume;
            _navMeshSurface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            _navMeshSurface.layerMask = 1 << _navMeshLayer;
            _navMeshSurface.overrideVoxelSize = true;
            _navMeshSurface.voxelSize = _voxelSize;
            _navMeshSurface.minRegionArea = _minRegionArea;
            
            var settings = NavMesh.GetSettingsByID(0);
            if (settings.agentRadius != _agentRadius || settings.agentHeight != _agentHeight)
            {
                NavMeshBuildSettings customSettings = settings;
                customSettings.agentRadius = _agentRadius;
                customSettings.agentHeight = _agentHeight;
            }
            
            Vector3 volumeSize = _walkableArea.size;
            volumeSize.x += _surfacePadding * 4f;
            volumeSize.z += _surfacePadding * 4f;
            volumeSize.y = Mathf.Max(volumeSize.y * 2f, 3f);
            
            _navMeshSurface.size = volumeSize;
            _navMeshSurface.center = new Vector3(0, volumeSize.y / 2f, 0);
        }

        #endregion

        #region Wall Detection

        private void DetectWallsFromMesh()
        {
            Debug.Log("[RobustWalkableSurface] 🧱 === DETECCIÓN DE PAREDES ===");
            
            _detectedWalls.Clear();
            _debugWallTriangles.Clear();
            
            if (_scannedMeshFilter == null || _scannedMeshFilter.sharedMesh == null)
            {
                Debug.LogWarning("[RobustWalkableSurface] ⚠️ No hay mesh");
                return;
            }
            
            Mesh mesh = _scannedMeshFilter.sharedMesh;
            Transform meshTransform = _scannedMeshFilter.transform;
            
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            Vector3[] normals = mesh.normals;
            
            if (normals == null || normals.Length == 0)
            {
                mesh.RecalculateNormals();
                normals = mesh.normals;
            }
            
            List<WallTriangle> wallTriangles = new List<WallTriangle>();
            
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i];
                int i1 = triangles[i + 1];
                int i2 = triangles[i + 2];
                
                Vector3 v0 = meshTransform.TransformPoint(vertices[i0]);
                Vector3 v1 = meshTransform.TransformPoint(vertices[i1]);
                Vector3 v2 = meshTransform.TransformPoint(vertices[i2]);
                
                Vector3 n0 = meshTransform.TransformDirection(normals[i0]);
                Vector3 n1 = meshTransform.TransformDirection(normals[i1]);
                Vector3 n2 = meshTransform.TransformDirection(normals[i2]);
                
                Vector3 triNormal = (n0 + n1 + n2).normalized;
                
                float angleWithVertical = Vector3.Angle(triNormal, Vector3.up);
                bool isVerticalByAngle = angleWithVertical >= _wallNormalAngleMin && 
                                        angleWithVertical <= _wallNormalAngleMax;
                
                float minY = Mathf.Min(v0.y, v1.y, v2.y);
                float maxY = Mathf.Max(v0.y, v1.y, v2.y);
                float triHeight = maxY - minY;
                
                bool hasMinHeight = _ignoreHeightFilter || triHeight >= _minWallHeight;
                
                float heightAboveFloor = minY - _detectedFloorHeight;
                bool isNearFloor = _ignoreFloorDistanceFilter || 
                                  (heightAboveFloor <= _maxWallHeightAboveFloor && 
                                   heightAboveFloor >= -1.0f);
                
                if (isVerticalByAngle && hasMinHeight && isNearFloor)
                {
                    wallTriangles.Add(new WallTriangle
                    {
                        v0 = v0, v1 = v1, v2 = v2,
                        normal = triNormal,
                        center = (v0 + v1 + v2) / 3f,
                        minY = minY, maxY = maxY,
                        angleWithVertical = angleWithVertical
                    });
                }
            }
            
            _debugWallTriangles = wallTriangles;
            _detectedWalls = ClusterWallTriangles(wallTriangles);
            
            Debug.Log($"[RobustWalkableSurface] ✅ Triángulos: {wallTriangles.Count}, Segmentos: {_detectedWalls.Count}");
        }

        private void FilterRealWalls()
        {
            if (_detectedWalls.Count == 0) return;
            
            int initialCount = _detectedWalls.Count;
            
            _detectedWalls = _detectedWalls
                .Where(wall => wall.triangles.Count >= _minWallTriangles)
                .Where(wall => CalculateWallArea(wall) >= _minWallArea)
                .ToList();
            
            Debug.Log($"[RobustWalkableSurface] 🔍 Filtrado: {initialCount} → {_detectedWalls.Count} paredes reales");
        }

        private float CalculateWallArea(WallSegment segment)
        {
            float totalArea = 0f;
            foreach (var tri in segment.triangles)
            {
                Vector3 side1 = tri.v1 - tri.v0;
                Vector3 side2 = tri.v2 - tri.v0;
                totalArea += Vector3.Cross(side1, side2).magnitude * 0.5f;
            }
            return totalArea;
        }

        private List<WallSegment> ClusterWallTriangles(List<WallTriangle> triangles)
        {
            List<WallSegment> segments = new List<WallSegment>();
            if (triangles.Count == 0) return segments;
            
            Dictionary<Vector3Int, List<WallTriangle>> grid = new Dictionary<Vector3Int, List<WallTriangle>>();
            
            foreach (var tri in triangles)
            {
                Vector3Int cell = new Vector3Int(
                    Mathf.FloorToInt(tri.center.x / _wallClusterDistance),
                    Mathf.FloorToInt(tri.center.y / _wallClusterDistance),
                    Mathf.FloorToInt(tri.center.z / _wallClusterDistance)
                );
                
                if (!grid.ContainsKey(cell))
                    grid[cell] = new List<WallTriangle>();
                
                grid[cell].Add(tri);
            }
            
            foreach (var cellGroup in grid.Values)
            {
                if (cellGroup.Count < 2) continue;
                
                List<Vector3> allVertices = new List<Vector3>();
                foreach (var tri in cellGroup)
                {
                    allVertices.Add(tri.v0);
                    allVertices.Add(tri.v1);
                    allVertices.Add(tri.v2);
                }
                
                Bounds segmentBounds = CalculateBounds(allVertices);
                
                Vector3 avgNormal = Vector3.zero;
                foreach (var tri in cellGroup)
                    avgNormal += tri.normal;
                avgNormal = (avgNormal / cellGroup.Count).normalized;
                
                segments.Add(new WallSegment
                {
                    bounds = segmentBounds,
                    triangles = cellGroup,
                    normal = avgNormal
                });
            }
            
            return segments;
        }

        #endregion

        #region Elevated Surfaces

        private void MarkElevatedSurfacesAsObstacles()
        {
            if (_elevatedVertices.Count == 0) return;
            
            foreach (var vol in _exclusionVolumes) if (vol != null) Destroy(vol);
            _exclusionVolumes.Clear();
            
            List<List<Vector3>> spatialClusters = ClusterVerticesSpatially(_elevatedVertices, 0.5f);
            
            foreach (var cluster in spatialClusters)
            {
                if (cluster.Count < 10) continue;
                CreateExclusionVolume(cluster);
            }
            
            Debug.Log($"[RobustWalkableSurface] 🚫 Exclusión: {_exclusionVolumes.Count} volúmenes");
        }

        private List<List<Vector3>> ClusterVerticesSpatially(List<Vector3> vertices, float cellSize)
        {
            Dictionary<Vector3Int, List<Vector3>> grid = new Dictionary<Vector3Int, List<Vector3>>();
            
            foreach (Vector3 v in vertices)
            {
                Vector3Int cell = new Vector3Int(
                    Mathf.FloorToInt(v.x / cellSize),
                    Mathf.FloorToInt(v.y / cellSize),
                    Mathf.FloorToInt(v.z / cellSize)
                );
                
                if (!grid.ContainsKey(cell))
                    grid[cell] = new List<Vector3>();
                
                grid[cell].Add(v);
            }
            
            return grid.Values.ToList();
        }

        private void CreateExclusionVolume(List<Vector3> clusterVertices)
        {
            Bounds clusterBounds = CalculateBounds(clusterVertices);
            clusterBounds.Expand(_exclusionExpansion * 2f);
            
            GameObject excludeVolume = new GameObject($"ExclusionVolume_{_exclusionVolumes.Count}");
            excludeVolume.transform.position = clusterBounds.center;
            excludeVolume.layer = _navMeshLayer;
            excludeVolume.isStatic = true;
            
            NavMeshModifierVolume modifier = excludeVolume.AddComponent<NavMeshModifierVolume>();
            modifier.size = clusterBounds.size;
            modifier.area = _navMeshAreaNotWalkable;
            
            _exclusionVolumes.Add(excludeVolume);
        }

        #endregion

        #region Wall Obstacles

        private void CreateWallObstacles()
        {
            foreach (var obstacle in _wallObstacles)
                if (obstacle != null) Destroy(obstacle.gameObject);
            _wallObstacles.Clear();
            
            if (_detectedWalls.Count == 0)
            {
                Debug.LogWarning("[RobustWalkableSurface] ⚠️ No hay paredes detectadas");
                return;
            }
            
            foreach (var wallSegment in _detectedWalls)
            {
                CreateWallObstacle(wallSegment);
            }
            
            Debug.Log($"[RobustWalkableSurface] ✅ {_wallObstacles.Count} obstáculos de pared creados");
        }

        private void CreateWallObstacle(WallSegment segment)
        {
            Bounds expandedBounds = segment.bounds;
            expandedBounds.Expand(_wallVolumeExpansion * 2f);
            
            if (expandedBounds.size.y < 2f)
            {
                Vector3 newSize = expandedBounds.size;
                newSize.y = 2f;
                expandedBounds.size = newSize;
            }
            
            GameObject obstacleObj = new GameObject($"WallObstacle_{_wallObstacles.Count}");
            obstacleObj.transform.position = expandedBounds.center;
            obstacleObj.layer = _navMeshLayer;
            obstacleObj.isStatic = true;
            
            BoxCollider wallCollider = obstacleObj.AddComponent<BoxCollider>();
            wallCollider.size = expandedBounds.size;
            wallCollider.center = Vector3.zero;
            wallCollider.isTrigger = false;
            
            NavMeshModifierVolume modifier = obstacleObj.AddComponent<NavMeshModifierVolume>();
            modifier.size = expandedBounds.size;
            modifier.center = Vector3.zero;
            modifier.area = _navMeshAreaNotWalkable;
            
            if (_useCarving)
            {
                NavMeshObstacle obstacle = obstacleObj.AddComponent<NavMeshObstacle>();
                obstacle.carving = true;
                obstacle.carveOnlyStationary = false;
                obstacle.shape = NavMeshObstacleShape.Box;
                obstacle.size = expandedBounds.size;
                obstacle.center = Vector3.zero;
                _wallObstacles.Add(obstacle);
            }
            
            if (_showDebugVisualization)
            {
                MeshFilter mf = obstacleObj.AddComponent<MeshFilter>();
                mf.mesh = CreateBoxMesh(expandedBounds.size);
                
                MeshRenderer mr = obstacleObj.AddComponent<MeshRenderer>();
                Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.color = new Color(1f, 0f, 0f, 0.3f);
                mr.material = mat;
            }
        }

        private Mesh CreateBoxMesh(Vector3 size)
        {
            Mesh mesh = new Mesh();
            float x = size.x / 2f, y = size.y / 2f, z = size.z / 2f;
            
            Vector3[] vertices = new Vector3[]
            {
                new Vector3(-x,-y,-z), new Vector3(x,-y,-z), new Vector3(x,y,-z), new Vector3(-x,y,-z),
                new Vector3(-x,-y,z), new Vector3(x,-y,z), new Vector3(x,y,z), new Vector3(-x,y,z)
            };
            
            int[] triangles = new int[]
            {
                0,2,1, 0,3,2, 4,5,6, 4,6,7,
                0,1,5, 0,5,4, 1,2,6, 1,6,5,
                2,3,7, 2,7,6, 3,0,4, 3,4,7
            };
            
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            return mesh;
        }

        #endregion

        #region NavMesh Baking

        private bool BakeNavMesh()
        {
            if (_navMeshSurface == null) return false;
            
            try
            {
                _navMeshSurface.BuildNavMesh();
                
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                
                if (tri.vertices.Length == 0)
                {
                    Debug.LogError("[RobustWalkableSurface] ❌ NavMesh sin geometría");
                    return false;
                }
                
                float area = CalculateNavMeshArea(tri);
                Debug.Log($"[RobustWalkableSurface] ✅ NavMesh: {tri.vertices.Length} verts, {area:F2}m²");
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RobustWalkableSurface] ❌ Error: {ex.Message}");
                return false;
            }
        }

        private float CalculateNavMeshArea(NavMeshTriangulation tri)
        {
            float totalArea = 0f;
            for (int i = 0; i < tri.indices.Length; i += 3)
            {
                Vector3 v1 = tri.vertices[tri.indices[i]];
                Vector3 v2 = tri.vertices[tri.indices[i + 1]];
                Vector3 v3 = tri.vertices[tri.indices[i + 2]];
                totalArea += Vector3.Cross(v2 - v1, v3 - v1).magnitude * 0.5f;
            }
            return totalArea;
        }

        #endregion

        #region Utilities

        private Bounds CalculateBounds(List<Vector3> vertices)
        {
            if (vertices.Count == 0) return new Bounds();
            
            Vector3 min = vertices[0], max = vertices[0];
            foreach (Vector3 v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            
            return new Bounds((min + max) / 2f, max - min);
        }

        private void LogResults()
        {
            Debug.Log("========== RESUMEN FINAL ==========");
            Debug.Log($"✅ Piso: Y={_detectedFloorHeight:F3}m");
            Debug.Log($"✅ Área: {_walkableArea.size.x:F2}×{_walkableArea.size.z:F2}m");
            Debug.Log($"✅ Paredes bloqueadas: {_detectedWalls.Count}");
            Debug.Log($"✅ Obstáculos pared: {_wallObstacles.Count}");
            
            if (_ignoreElevatedObstacles)
                Debug.Log($"⏭️ Muebles IGNORADOS (atravesables)");
            else
                Debug.Log($"🚫 Muebles bloqueados: {_exclusionVolumes.Count}");
            
            Debug.Log("===================================");
        }

        private void LogHistogramDetails(List<HeightCluster> clusters)
        {
            Debug.Log("=== HISTOGRAMA ===");
            foreach (var c in clusters.Take(15))
            {
                float pct = c.density * 100f;
                string bar = new string('█', Mathf.RoundToInt(pct / 2f));
                Debug.Log($"Y={c.centerHeight:F2}m ({pct:F1}%) {bar}");
            }
        }

        #endregion

        #region Debug

        private void OnDrawGizmos()
        {
            if (!_showDebugVisualization) return;
            
            if (_walkableArea.size != Vector3.zero)
            {
                Gizmos.color = new Color(0f, 1f, 0f, 0.2f);
                Gizmos.DrawCube(_walkableArea.center, _walkableArea.size);
            }
            
            foreach (var wall in _detectedWalls)
            {
                Gizmos.color = new Color(1f, 0f, 0f, 0.3f);
                Gizmos.DrawCube(wall.bounds.center, wall.bounds.size);
            }
            
            if (_debugDrawWallNormals)
            {
                Gizmos.color = Color.yellow;
                foreach (var tri in _debugWallTriangles)
                    Gizmos.DrawRay(tri.center, tri.normal * 0.3f);
            }
        }

        #endregion

        #region Data Structures

        [Serializable]
        private class HeightCluster
        {
            public int binIndex;
            public float minHeight, maxHeight, centerHeight;
            public int vertexCount;
            public float density;
            public List<Vector3> vertices;
            public Bounds bounds;
        }

        private struct WallTriangle
        {
            public Vector3 v0, v1, v2, normal, center;
            public float minY, maxY, angleWithVertical;
        }

        private class WallSegment
        {
            public Bounds bounds;
            public List<WallTriangle> triangles;
            public Vector3 normal;
        }

        #endregion

        #region Context Menu

        [ContextMenu("Generate")]
        private void ContextGenerate() => GenerateWalkableSurface();

        [ContextMenu("Clear")]
        private void ContextClear() => Clear();

        #endregion
    }
}