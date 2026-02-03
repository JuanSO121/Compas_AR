// File: MultiMeshWalkableSurfaceGenerator.cs

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// ✅ VERSIÓN 4.0 - REFACTORIZADA - Orquestador del sistema de navegación AR móvil
    /// </summary>
    public class MultiMeshWalkableSurfaceGenerator : MonoBehaviour
    {
        #region Configuration

        [Header("🔍 Análisis de Geometría")]
        [SerializeField] private float _heightBinSize = 0.05f;
        [SerializeField] [Range(0.05f, 0.5f)] private float _minClusterDensity = 0.12f;
        [SerializeField] private float _maxFloorDeviation = 0.12f;
        
        [Header("🏗️ Reconstrucción de Superficie")]
        [SerializeField] private float _surfacePadding = 0.25f;
        [SerializeField] private float _surfaceThickness = 0.01f;
        [SerializeField] private float _surfaceOffsetBelow = 0.02f;
        
        [Header("🧱 Detección de Paredes MEJORADA")]
        [SerializeField] private float _wallAngleMin = 30f;
        [SerializeField] private float _wallAngleMax = 150f;
        [SerializeField] private float _minWallHeight = 0.8f;
        [SerializeField] private float _minWallArea = 0.3f;
        [SerializeField] private float _planeNormalTolerance = 0.15f;
        [SerializeField] private float _planeDistanceTolerance = 0.3f;
        
        [Header("🚪 Validación de Aperturas ROBUSTA")]
        [SerializeField] private bool _detectOpenings = true;
        [SerializeField] private float _minOpeningWidth = 0.7f;
        [SerializeField] private float _minFullWallHeight = 2.0f;
        [SerializeField] private int _openingRaycastGridSize = 3;
        [SerializeField] private float _openingRaycastDistance = 2.0f;
        [SerializeField] [Range(0f, 1f)] private float _openingPassThroughThreshold = 0.6f;
        [SerializeField] private LayerMask _openingDetectionLayers = ~0;
        
        [Header("📦 DETECCIÓN DE MUEBLES")]
        [SerializeField] private bool _detectFurnitureInScan = true;
        [SerializeField] private float _furnitureVoxelSize = 0.15f;
        [SerializeField] [Range(0.1f, 1f)] private float _minFurnitureDensity = 0.3f;
        [SerializeField] private float _minFurnitureHeight = 0.3f;
        [SerializeField] private float _maxFurnitureHeight = 2.8f;
        [SerializeField] private float _minFurnitureArea = 0.1f;
        [SerializeField] private float _minFurnitureVolume = 0.05f;
        
        [Header("📦 Meshes Múltiples")]
        [SerializeField] private MeshFilter _primaryMesh;
        [SerializeField] private List<MeshFilter> _proxyMeshes = new List<MeshFilter>();
        [SerializeField] private bool _autoFindProxyMeshes = true;
        [SerializeField] private string _proxyMeshTag = "NavMeshObstacle";
        
        [Header("🛠️ Configuración de Obstáculos")]
        [SerializeField] private float _obstaclePadding = 0.15f;
        [SerializeField] private float _obstacleHeightPadding = 0.1f;
        
        [Header("🎯 Agente NavMesh")]
        [SerializeField] private float _agentRadius = 0.15f;
        [SerializeField] private float _agentHeight = 1.8f;
        
        [Header("⚙️ Configuración NavMesh")]
        [SerializeField] private int _navMeshLayer = 0;
        [SerializeField] private int _navMeshAreaNotWalkable = 1;
        [SerializeField] private float _voxelSize = 0.06f;
        [SerializeField] private int _minRegionArea = 1;
        
        [Header("⚡ Optimización Móvil")]
        [SerializeField] private bool _useAsyncBaking = true;
        [SerializeField] private int _maxObstaclesPerFrame = 10;
        [SerializeField] private bool _useLODForDistantObstacles = true;
        [SerializeField] private float _lodDistanceThreshold = 5f;
        
        [Header("🐛 Debug y Diagnóstico")]
        [SerializeField] private bool _showDebugVisualization = true;
        [SerializeField] private bool _logDetailedAnalysis = false;
        [SerializeField] private bool _enableRuntimeDiagnostics = true;
        [SerializeField] private bool _debugDrawOpeningRaycasts = false;

        #endregion

        #region Services

        private MeshProcessingService _meshProcessor;
        private NavMeshSurfaceService _surfaceService;
        private ObstacleService _obstacleService;
        private LevelNavigationService _levelService;

        #endregion

        #region Internal State

        private float _detectedFloorHeight;
        private Bounds _walkableArea;
        private GameObject _walkableSurface;
        private NavMeshSurface _navMeshSurface;
        
        private List<GameObject> _wallObstacles = new List<GameObject>();
        private List<GameObject> _proxyObstacles = new List<GameObject>();
        private List<GameObject> _furnitureObstacles = new List<GameObject>();
        
        private DiagnosticData _diagnostics = new DiagnosticData();

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            InitializeServices();
        }

        private void OnEnable()
        {
            Core.Events.EventBus.Instance?.Subscribe<Core.Events.ModelLoadedEvent>(OnModelLoaded);
        }

        private void OnDisable()
        {
            Core.Events.EventBus.Instance?.Unsubscribe<Core.Events.ModelLoadedEvent>(OnModelLoaded);
        }

        #endregion

        #region Initialization

        private void InitializeServices()
        {
            _meshProcessor = new MeshProcessingService(
                _heightBinSize,
                _minClusterDensity,
                _maxFloorDeviation,
                _wallAngleMin,
                _wallAngleMax,
                _minWallHeight,
                _minWallArea,
                _planeNormalTolerance,
                _planeDistanceTolerance,
                _furnitureVoxelSize,
                _minFurnitureDensity,
                _minFurnitureHeight,
                _maxFurnitureHeight,
                _minFurnitureArea,
                _minFurnitureVolume,
                _logDetailedAnalysis
            );

            _surfaceService = new NavMeshSurfaceService(
                _surfacePadding,
                _surfaceThickness,
                _surfaceOffsetBelow,
                _navMeshLayer,
                _voxelSize,
                _minRegionArea,
                _showDebugVisualization
            );

            _obstacleService = new ObstacleService(
                _obstaclePadding,
                _obstacleHeightPadding,
                _agentHeight,
                _navMeshLayer,
                _navMeshAreaNotWalkable,
                _detectOpenings,
                _minOpeningWidth,
                _minFullWallHeight,
                _openingRaycastGridSize,
                _openingRaycastDistance,
                _openingPassThroughThreshold,
                _openingDetectionLayers,
                _debugDrawOpeningRaycasts,
                _logDetailedAnalysis,
                _useLODForDistantObstacles,
                _lodDistanceThreshold,
                _maxObstaclesPerFrame,
                _showDebugVisualization
            );

            _levelService = new LevelNavigationService();
        }

        #endregion

        #region Event Handlers

        private void OnModelLoaded(Core.Events.ModelLoadedEvent evt)
        {
            Debug.Log($"[NavAR] 📦 Modelo cargado: {evt.ModelName}");
            
            MeshFilter[] meshFilters = evt.ModelInstance.GetComponentsInChildren<MeshFilter>();
            
            if (meshFilters.Length == 0)
            {
                Debug.LogWarning("[NavAR] ⚠️ Modelo sin MeshFilter");
                return;
            }
            
            _primaryMesh = _meshProcessor.FindPrimaryMesh(meshFilters);
            
            if (_autoFindProxyMeshes)
            {
                FindProxyMeshes();
            }
            
            if (_primaryMesh != null)
            {
                _ = GenerateWalkableSurfaceAsync();
            }
        }

        #endregion

        #region Proxy Mesh Management

        private void FindProxyMeshes()
        {
            _proxyMeshes.Clear();
            
            if (!string.IsNullOrEmpty(_proxyMeshTag))
            {
                GameObject[] taggedObjects = GameObject.FindGameObjectsWithTag(_proxyMeshTag);
                foreach (var obj in taggedObjects)
                {
                    MeshFilter mf = obj.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        _proxyMeshes.Add(mf);
                    }
                }
                
                Debug.Log($"[NavAR] 🔍 Encontrados {_proxyMeshes.Count} meshes proxy");
            }
        }

        public void AddProxyMesh(MeshFilter meshFilter)
        {
            if (meshFilter != null && !_proxyMeshes.Contains(meshFilter))
            {
                _proxyMeshes.Add(meshFilter);
            }
        }

        public void ClearProxyMeshes()
        {
            _proxyMeshes.Clear();
        }

        #endregion

        #region Public API

        public bool GenerateWalkableSurface()
        {
            Debug.Log("[NavAR] 🚀 Iniciando generación V4.0 (AR Móvil)...");
            
            var startTime = Time.realtimeSinceStartup;
            _diagnostics = new DiagnosticData { startTime = startTime };
            
            try
            {
                if (!ValidateInputMesh()) return false;
                
                if (_autoFindProxyMeshes)
                {
                    FindProxyMeshes();
                }
                
                // Pipeline principal
                var analysisResult = _meshProcessor.AnalyzeScannedGeometry(_primaryMesh);
                if (analysisResult == null) return false;
                
                _diagnostics.meshVertexCount = analysisResult.MeshVertexCount;
                _diagnostics.meshTriangleCount = analysisResult.MeshTriangleCount;
                
                var floorResult = _levelService.DetectWalkableFloor(analysisResult.HeightClusters);
                if (floorResult == null) return false;
                
                _detectedFloorHeight = floorResult.FloorHeight;
                _walkableArea = floorResult.WalkableArea;
                
                _diagnostics.floorHeight = _detectedFloorHeight;
                _diagnostics.walkableAreaSize = _walkableArea.size;
                
                _walkableSurface = _surfaceService.ReconstructCleanFloorPlane(_walkableArea, _detectedFloorHeight, out _navMeshSurface);
                if (_walkableSurface == null) return false;
                
                // Detección de obstáculos
                var wallPlanes = _meshProcessor.DetectWallPlanesRobust(_primaryMesh, _detectedFloorHeight);
                _diagnostics.wallPlanesDetected = wallPlanes.Count;
                
                var wallObstacleResult = _obstacleService.CreateWallObstaclesWithValidation(wallPlanes, _detectedFloorHeight, _agentHeight);
                _wallObstacles = wallObstacleResult.Obstacles;
                _diagnostics.wallObstaclesCreated = wallObstacleResult.ValidWalls;
                _diagnostics.openingsDetected = wallObstacleResult.Openings;
                
                if (_detectFurnitureInScan)
                {
                    var furnitureClusters = _meshProcessor.DetectFurnitureVolumetric(_primaryMesh, _detectedFloorHeight);
                    _diagnostics.furnitureDetected = furnitureClusters.Count;
                    
                    _furnitureObstacles = _obstacleService.CreateFurnitureObstacles(furnitureClusters, _detectedFloorHeight);
                    _diagnostics.furnitureObstaclesCreated = _furnitureObstacles.Count;
                }
                
                var proxyResult = _obstacleService.ProcessProxyMeshesUnified(_proxyMeshes);
                _proxyObstacles = proxyResult.Obstacles;
                _diagnostics.proxyMeshCount = _proxyMeshes.Count;
                _diagnostics.proxyObstaclesCreated = proxyResult.ProcessedCount;
                
                // Baking final
                if (!_surfaceService.BakeNavMeshOptimized(_navMeshSurface, _navMeshLayer, out var bakingStats)) return false;
                
                _diagnostics.bakingTime = bakingStats.BakingTime;
                _diagnostics.navMeshVertices = bakingStats.VertexCount;
                _diagnostics.navMeshTriangles = bakingStats.TriangleCount;
                _diagnostics.navMeshArea = bakingStats.Area;
                
                var elapsed = (Time.realtimeSinceStartup - startTime) * 1000f;
                _diagnostics.totalTime = elapsed;
                
                Debug.Log($"[NavAR] ✅ Generación exitosa en {elapsed:F0}ms");
                LogResults();
                
                if (_enableRuntimeDiagnostics)
                {
                    LogDiagnostics();
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavAR] ❌ Error: {ex.Message}\n{ex.StackTrace}");
                _diagnostics.errors.Add(ex.Message);
                return false;
            }
        }

        public async Task<bool> GenerateWalkableSurfaceAsync()
        {
            if (_useAsyncBaking)
            {
                await Task.Delay(500);
                return GenerateWalkableSurface();
            }
            else
            {
                return GenerateWalkableSurface();
            }
        }

        public void Clear()
        {
            if (_walkableSurface != null) Destroy(_walkableSurface);
            
            foreach (var obj in _wallObstacles) if (obj != null) Destroy(obj);
            foreach (var obj in _proxyObstacles) if (obj != null) Destroy(obj);
            foreach (var obj in _furnitureObstacles) if (obj != null) Destroy(obj);
            
            _wallObstacles.Clear();
            _proxyObstacles.Clear();
            _furnitureObstacles.Clear();
            
            Debug.Log("[NavAR] 🧹 Limpiado");
        }

        #endregion

        #region Validation

        private bool ValidateInputMesh()
        {
            if (_primaryMesh == null || _primaryMesh.sharedMesh == null)
            {
                Debug.LogError("[NavAR] ❌ No hay mesh principal");
                _diagnostics.errors.Add("No primary mesh");
                return false;
            }
            
            Mesh mesh = _primaryMesh.sharedMesh;
            if (mesh.vertices.Length == 0)
            {
                Debug.LogError("[NavAR] ❌ Mesh vacío");
                _diagnostics.errors.Add("Empty mesh");
                return false;
            }
            
            Debug.Log($"[NavAR] ✅ Mesh: {mesh.vertices.Length} verts, {mesh.triangles.Length/3} tris, {_proxyMeshes.Count} proxies");
            
            return true;
        }

        #endregion

        #region Logging

        private void LogResults()
        {
            Debug.Log("========== ✅ RESUMEN V4.0 AR MÓVIL ==========");
            Debug.Log($"Piso: Y={_detectedFloorHeight:F3}m");
            Debug.Log($"Área navegable: {_walkableArea.size.x:F2} × {_walkableArea.size.z:F2}m");
            Debug.Log($"Paredes: {_diagnostics.wallPlanesDetected} detectadas, {_diagnostics.wallObstaclesCreated} obstáculos");
            Debug.Log($"Aperturas: {_diagnostics.openingsDetected} detectadas");
            Debug.Log($"Muebles: {_diagnostics.furnitureDetected} detectados, {_diagnostics.furnitureObstaclesCreated} obstáculos");
            Debug.Log($"Proxies: {_diagnostics.proxyMeshCount} meshes, {_diagnostics.proxyObstaclesCreated} obstáculos");
            Debug.Log($"NavMesh: {_diagnostics.navMeshVertices} verts, {_diagnostics.navMeshArea:F2}m²");
            Debug.Log($"Tiempo total: {_diagnostics.totalTime:F0}ms (baking: {_diagnostics.bakingTime:F0}ms)");
            Debug.Log("==========================================");
        }

        private void LogDiagnostics()
        {
            Debug.Log("========== 📊 DIAGNÓSTICOS EN TIEMPO REAL ==========");
            Debug.Log($"Mesh principal: {_diagnostics.meshVertexCount} verts, {_diagnostics.meshTriangleCount} tris");
            Debug.Log($"Piso: {_diagnostics.floorHeight:F3}m, área: {_diagnostics.walkableAreaSize}");
            Debug.Log($"Paredes: {_diagnostics.wallPlanesDetected} planos → {_diagnostics.wallObstaclesCreated} obstáculos");
            Debug.Log($"Aperturas: {_diagnostics.openingsDetected}");
            Debug.Log($"Muebles: {_diagnostics.furnitureDetected} → {_diagnostics.furnitureObstaclesCreated} obstáculos");
            Debug.Log($"Proxies: {_diagnostics.proxyMeshCount} → {_diagnostics.proxyObstaclesCreated} obstáculos");
            Debug.Log($"NavMesh: {_diagnostics.navMeshVertices} verts, {_diagnostics.navMeshTriangles} tris, {_diagnostics.navMeshArea:F2}m²");
            Debug.Log($"Performance: total={_diagnostics.totalTime:F0}ms, baking={_diagnostics.bakingTime:F0}ms");
            
            if (_diagnostics.errors.Count > 0)
            {
                Debug.LogWarning($"Errores: {_diagnostics.errors.Count}");
                foreach (var error in _diagnostics.errors)
                {
                    Debug.LogWarning($"  - {error}");
                }
            }
            
            Debug.Log("==================================================");
        }

        #endregion

        #region Debug Visualization

        private void OnDrawGizmos()
        {
            if (!_showDebugVisualization) return;
            
            if (_walkableArea.size != Vector3.zero)
            {
                Gizmos.color = new Color(0f, 1f, 0f, 0.2f);
                Gizmos.DrawCube(_walkableArea.center, _walkableArea.size);
                
                Gizmos.color = Color.green;
                Gizmos.DrawWireCube(_walkableArea.center, _walkableArea.size);
            }
        }

        #endregion

        #region Data Structures

        private class DiagnosticData
        {
            public float startTime;
            public float totalTime;
            public float bakingTime;
            
            public int meshVertexCount;
            public int meshTriangleCount;
            public int proxyMeshCount;
            
            public float floorHeight;
            public Vector3 walkableAreaSize;
            
            public int wallPlanesDetected;
            public int wallObstaclesCreated;
            public int openingsDetected;
            
            public int furnitureDetected;
            public int furnitureObstaclesCreated;
            
            public int proxyObstaclesCreated;
            
            public int navMeshVertices;
            public int navMeshTriangles;
            public float navMeshArea;
            
            public List<string> errors = new List<string>();
        }

        #endregion

        #region Context Menu

        [ContextMenu("🚀 Generate NavMesh")]
        private void ContextGenerate() => GenerateWalkableSurface();

        [ContextMenu("🧹 Clear All")]
        private void ContextClear() => Clear();

        [ContextMenu("🔍 Find Proxy Meshes")]
        private void ContextFindProxy() => FindProxyMeshes();

        [ContextMenu("🐛 Toggle Debug Visualization")]
        private void ContextToggleDebug()
        {
            _showDebugVisualization = !_showDebugVisualization;
            Debug.Log($"[NavAR] Debug visualization: {(_showDebugVisualization ? "ON" : "OFF")}");
        }

        #endregion
    }
}