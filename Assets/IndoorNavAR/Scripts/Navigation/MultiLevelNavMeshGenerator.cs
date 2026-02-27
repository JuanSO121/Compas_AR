// File: MultiLevelNavMeshGenerator.cs
// ✅ FIX v3 — Modo render-only: en builds de dispositivo no crea ni muestra
//             obstáculos (cuadros rojos), no procesa proxy meshes/tags,
//             y oculta los GameObjects de debug.
//
//   CAMBIOS:
//     - _renderOnlyMode (bool): si true, GenerateMultiLevelNavMeshAsync() omite
//       la creación de WallObstacles, FurnitureObstacles y ProxyObstacles.
//       Los GameObjects de superficie sí se crean (el NavMesh los necesita),
//       pero los obstáculos de debug no aparecen en pantalla.
//     - _autoFindProxyMeshes = false por defecto en builds (no escanea tags).
//     - _showDebugVisualization = false por defecto en builds.
//     - Se activa automáticamente fuera del Editor con #if !UNITY_EDITOR.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;
using IndoorNavAR.Core.Events;


namespace IndoorNavAR.Navigation
{
    public class MultiLevelNavMeshGenerator : MonoBehaviour
    {
        #region Configuration — Análisis Multi-Nivel

        [Header("🔍 Análisis Multi-Nivel")]
        [SerializeField] private float _heightBinSize         = 0.05f;
        [SerializeField] [Range(0.05f, 0.5f)] private float _minClusterDensity = 0.12f;
        [SerializeField] private float _maxFloorDeviation     = 0.12f;
        [SerializeField] [Tooltip("Altura mínima entre pisos")]
        private float _levelSeparationHeight = 2.0f;

        #endregion

        #region Configuration — Superficie

        [Header("🏗️ Reconstrucción de Superficie")]
        [SerializeField] private float _surfacePadding     = 0.25f;
        [SerializeField] private float _surfaceThickness   = 0.01f;
        [SerializeField] private float _surfaceOffsetBelow = 0.02f;
        [SerializeField] [Tooltip("Margen vertical del volumen de baking")]
        private float _levelHeightMargin = 2.5f;

        #endregion

        #region Configuration — Paredes

        [Header("🧱 Detección de Paredes")]
        [SerializeField] private float _wallAngleMin          = 30f;
        [SerializeField] private float _wallAngleMax          = 150f;
        [SerializeField] private float _minWallHeight         = 0.8f;
        [SerializeField] private float _minWallArea           = 0.3f;
        [SerializeField] private float _planeNormalTolerance  = 0.15f;
        [SerializeField] private float _planeDistanceTolerance = 0.3f;

        #endregion

        #region Configuration — Aperturas

        [Header("🚪 Validación de Aperturas")]
        [SerializeField] private bool  _detectOpenings              = true;
        [SerializeField] private float _minOpeningWidth             = 0.7f;
        [SerializeField] private float _minFullWallHeight           = 2.0f;
        [SerializeField] private int   _openingRaycastGridSize      = 3;
        [SerializeField] private float _openingRaycastDistance      = 2.0f;
        [SerializeField] [Range(0f, 1f)] private float _openingPassThroughThreshold = 0.6f;
        [SerializeField] private LayerMask _openingDetectionLayers  = ~0;
        [SerializeField] private bool _debugDrawOpeningRaycasts     = false;

        #endregion

        #region Configuration — Muebles

        [Header("📦 Detección de Muebles")]
        [SerializeField] private bool  _detectFurnitureInScan  = true;
        [SerializeField] private float _furnitureVoxelSize     = 0.15f;
        [SerializeField] [Range(0.1f, 1f)] private float _minFurnitureDensity = 0.3f;
        [SerializeField] private float _minFurnitureHeight = 0.3f;
        [SerializeField] private float _maxFurnitureHeight = 2.8f;
        [SerializeField] private float _minFurnitureArea   = 0.1f;
        [SerializeField] private float _minFurnitureVolume = 0.05f;

        #endregion

        #region Configuration — Escaleras

        [Header("🔗 Escaleras")]
        [SerializeField] private bool   _autoDetectStairHelpers = true;
        [SerializeField] private string _stairHelperTag         = "";
        [SerializeField] private bool   _autoRegenerateStairs   = true;

        #endregion

        #region Configuration — Agente NavMesh

        [Header("🎯 Agente NavMesh")]
        [SerializeField] private float _agentRadius     = 0.10f;
        [SerializeField] private float _agentHeight     = 1.8f;
        [SerializeField] [Tooltip("Pendiente máxima (°)")]
        private float _agentMaxSlope   = 50f;
        [SerializeField] [Tooltip("Escalón máximo (m)")]
        private float _agentStepHeight = 0.05f;

        #endregion

        #region Configuration — Meshes

        [Header("📦 Meshes")]
        [SerializeField] private MeshFilter       _primaryMesh;
        [SerializeField] private List<MeshFilter> _proxyMeshes        = new List<MeshFilter>();
        [SerializeField] private bool             _autoFindProxyMeshes = true;
        [SerializeField] private string           _proxyMeshTag       = "NavMeshObstacle";

        #endregion

        #region Configuration — Obstáculos

        [Header("🛠️ Configuración de Obstáculos")]
        [SerializeField] [Range(0.01f, 0.15f)] private float _obstaclePadding       = 0.02f;
        [SerializeField] private float _obstacleHeightPadding = 0.05f;
        [SerializeField] [Range(0.05f, 0.25f)] private float _furniturePadding      = 0.10f;

        #endregion

        #region Configuration — Modo Interior

        [Header("🏠 Modo Espacios Interiores")]
        [SerializeField] private bool  _indoorMode           = true;
        [SerializeField] [Range(0.4f, 1.2f)] private float _minPassageWidth = 0.65f;
        [SerializeField] private bool  _smartPaddingReduction = true;

        #endregion

        #region Configuration — NavMesh

        [Header("⚙️ Configuración NavMesh")]
        [SerializeField] private int   _navMeshLayer          = 0;
        [SerializeField] private int   _navMeshAreaNotWalkable = 1;
        [SerializeField] private float _voxelSize             = 0.06f;
        [SerializeField] private int   _minRegionArea         = 1;
        [SerializeField] private int   _agentTypeID           = 0;

        #endregion

        #region Configuration — Debug / Mobile

        [Header("🐛 Debug")]
        [SerializeField] private bool _showDebugVisualization   = true;
        [SerializeField] private bool _logDetailedAnalysis      = false;
        [SerializeField] private bool _enableRuntimeDiagnostics = true;

        [Header("📱 Modo Mobile")]
        [Tooltip("Si true: no crea obstáculos visuales (cuadros rojos) ni procesa proxy tags.\n" +
                 "El NavMesh sigue funcionando: los obstáculos existen en el bake guardado.\n" +
                 "Se activa automáticamente en builds fuera del Editor.")]
        [SerializeField] private bool _renderOnlyMode = false;

        #endregion

        #region Servicios

        private MeshProcessingService       _meshProcessor;
        private MultiLevelSurfaceService    _surfaceService;
        private ObstacleService             _obstacleService;
        private SecondFloorOpeningGenerator _openingGenerator;
        private GlobalNavMeshBaker          _globalBaker;

        #endregion

        #region Estado

        private List<LevelData> _levels = new List<LevelData>();
        private List<StairWithLandingHelper> _detectedStairs = new List<StairWithLandingHelper>();
        private DiagnosticData _diagnostics = new();

        public int DetectedLevelCount => _levels?.Count ?? 0;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
#if !UNITY_EDITOR
            // FIX: Forzar flags ANTES de InitializeServices() para que los servicios
            // se construyan ya con showDebug=false. Si se hacía después, _surfaceService
            // y _obstacleService se creaban con _showDebugVisualization=true (valor del
            // Inspector/prefab) y podían instanciar MeshRenderers de debug en runtime.
            _renderOnlyMode           = true;
            _showDebugVisualization   = false;
            _autoFindProxyMeshes      = false;
            _debugDrawOpeningRaycasts = false;
            Debug.Log("[MultiLevel] 📱 Build de dispositivo → renderOnlyMode=true, debug visual=false");
#endif
            InitializeServices();
            _openingGenerator = GetComponent<SecondFloorOpeningGenerator>()
                             ?? gameObject.AddComponent<SecondFloorOpeningGenerator>();
            _globalBaker = GetComponent<GlobalNavMeshBaker>()
                        ?? gameObject.AddComponent<GlobalNavMeshBaker>();
        }

        private void OnEnable()
        {
            EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        private void OnDisable()
        {
            EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        #endregion

        #region Inicialización

        private void InitializeServices()
        {
            _meshProcessor = new MeshProcessingService(
                _heightBinSize, _minClusterDensity, _maxFloorDeviation, _levelSeparationHeight,
                _wallAngleMin, _wallAngleMax, _minWallHeight, _minWallArea,
                _planeNormalTolerance, _planeDistanceTolerance,
                _furnitureVoxelSize, _minFurnitureDensity, _minFurnitureHeight, _maxFurnitureHeight,
                _minFurnitureArea, _minFurnitureVolume, _logDetailedAnalysis);

            _surfaceService = new MultiLevelSurfaceService(
                _surfacePadding, _surfaceThickness, _surfaceOffsetBelow, _levelHeightMargin,
                _navMeshLayer, _voxelSize, _minRegionArea, _showDebugVisualization);

            _obstacleService = new ObstacleService(
                _obstaclePadding, _obstacleHeightPadding, _furniturePadding, _agentHeight,
                _navMeshLayer, _navMeshAreaNotWalkable,
                _detectOpenings, _minOpeningWidth, _minFullWallHeight,
                _openingRaycastGridSize, _openingRaycastDistance, _openingPassThroughThreshold,
                _openingDetectionLayers, _debugDrawOpeningRaycasts, _logDetailedAnalysis,
                false, 10f, 0, _showDebugVisualization, true, 0.25f,
                _indoorMode, _minPassageWidth, _smartPaddingReduction);
        }

        #endregion

        #region Eventos

        private void OnModelLoaded(ModelLoadedEvent evt)
        {
            Debug.Log($"[MultiLevel] 📦 Modelo cargado: {evt.ModelName}");

            MeshFilter[] meshFilters = evt.ModelInstance.GetComponentsInChildren<MeshFilter>();

            if (meshFilters.Length == 0)
            {
                Debug.LogWarning("[MultiLevel] ⚠️ Modelo sin MeshFilter");
                return;
            }

            _primaryMesh = _meshProcessor.FindPrimaryMesh(meshFilters);

            // FIX v3: En render-only no buscar proxies — evita procesar tag NavMeshObstacle
            if (_autoFindProxyMeshes && !_renderOnlyMode)
                FindProxyMeshes();
        }

        #endregion

        #region Proxy Meshes

        private void FindProxyMeshes()
        {
            _proxyMeshes.Clear();
            if (string.IsNullOrEmpty(_proxyMeshTag)) return;
            foreach (GameObject obj in GameObject.FindGameObjectsWithTag(_proxyMeshTag))
            {
                MeshFilter mf = obj.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                    _proxyMeshes.Add(mf);
            }
        }

        #endregion

        #region Gestión de Escaleras

        private void DetectStairHelpers()
        {
            _detectedStairs.Clear();
            if (!_autoDetectStairHelpers) return;

            StairWithLandingHelper[] all = string.IsNullOrEmpty(_stairHelperTag)
                ? FindObjectsByType<StairWithLandingHelper>(FindObjectsSortMode.None)
                : GameObject.FindGameObjectsWithTag(_stairHelperTag)
                            .Select(o => o.GetComponent<StairWithLandingHelper>())
                            .Where(s => s != null)
                            .ToArray();

            _detectedStairs.AddRange(all);
            Debug.Log($"[MultiLevel] ✅ Escaleras detectadas: {_detectedStairs.Count}");

            if (_autoRegenerateStairs)
                RegenerateDetectedStairs();
        }

        private void RegenerateDetectedStairs()
        {
            foreach (StairWithLandingHelper stair in _detectedStairs)
            {
                if (stair == null) continue;
                try { stair.CreateStairSystem(); }
                catch (Exception ex)
                {
                    Debug.LogError($"[MultiLevel] ❌ Error regenerando escalera: {ex.Message}");
                }
            }
        }

        private void PostBakeCleanup()
        {
            int total = 0;
            foreach (StairWithLandingHelper stair in _detectedStairs)
            {
                if (stair == null) continue;
                try { stair.PostBakeCleanup(); total++; }
                catch (Exception ex)
                {
                    Debug.LogError($"[MultiLevel] ❌ Error en PostBakeCleanup: {ex.Message}");
                }
            }
            Debug.Log($"[MultiLevel] 🧹 PostBakeCleanup completado en {total} escaleras");
        }

        private List<NavMeshLink> GetStairNavMeshLinks() =>
            _detectedStairs
                .Where(s => s != null)
                .SelectMany(s => s.GetComponentsInChildren<NavMeshLink>())
                .ToList();

        #endregion

        #region Configuración del Agente

        private void ConfigureNavMeshAgentSettings()
        {
            NavigationAgent agent = FindFirstObjectByType<NavigationAgent>();
            if (agent == null) return;

            NavMeshAgent navAgent = agent.GetComponent<NavMeshAgent>();
            if (navAgent == null) return;

            navAgent.height = _agentHeight;
            navAgent.radius = _agentRadius;

            Debug.Log($"[MultiLevel] 🎯 NavMeshAgent: height={_agentHeight}m, radius={_agentRadius}m");
        }

        #endregion

        #region ✅ Pipeline Principal de Generación

        public async Task<bool> GenerateMultiLevelNavMeshAsync()
        {
            // FIX: En render-only con NavMesh guardado en disco, no generar nada.
            // El Coordinator ya no debería llamar este método, pero por seguridad
            // lo bloqueamos aquí también para no instanciar obstáculos accidentalmente.
            if (_renderOnlyMode && NavMeshSerializer.HasSavedNavMesh)
            {
                Debug.LogWarning("[MultiLevel] ⚠️ GenerateMultiLevelNavMeshAsync bloqueado: " +
                                 "render-only mode con NavMesh en disco. Usa LoadNavMeshFromFile().");
                return false;
            }

            Debug.Log("[MultiLevel] 🚀 Iniciando generación MULTI-NIVEL async (Unity 6.3+)...");
            float startTime = Time.realtimeSinceStartup;
            _diagnostics = new DiagnosticData { startTime = startTime };

            try
            {
                if (!ValidateInputMesh()) return false;

                // FIX v3: En render-only no procesar proxies
                if (_autoFindProxyMeshes && !_renderOnlyMode) FindProxyMeshes();

                ConfigureNavMeshAgentSettings();
                DetectStairHelpers();

                GeometryAnalysisResult analysis = _meshProcessor.AnalyzeScannedGeometry(_primaryMesh);
                if (analysis == null) return false;

                _diagnostics.meshVertexCount   = analysis.MeshVertexCount;
                _diagnostics.meshTriangleCount = analysis.MeshTriangleCount;

                List<NavigableLevel> navigableLevels = _meshProcessor.DetectNavigableLevels(analysis.HeightClusters);
                if (navigableLevels.Count == 0)
                {
                    Debug.LogError("[MultiLevel] ❌ No se detectaron niveles navegables");
                    return false;
                }

                _diagnostics.levelsDetected = navigableLevels.Count;

                _levels.Clear();
                foreach (NavigableLevel navLevel in navigableLevels)
                {
                    LevelData levelData = ProcessLevel(navLevel);
                    if (levelData != null)
                        _levels.Add(levelData);
                }

                if (_levels.Count > 1 && _detectedStairs.Count > 0)
                {
                    Debug.Log("[MultiLevel] ✂️ Generando huecos en pisos superiores...");
                    _openingGenerator.GenerateOpeningsForAllStairs();
                }

                // FIX v3: En render-only la lista de obstáculos está vacía —
                // no se crean GameObjects rojos, pero el bake del NavMesh guardado
                // ya los tiene incorporados como área no-walkable.
                List<GameObject> allObstacles = _renderOnlyMode
                    ? new List<GameObject>()
                    : _levels
                        .SelectMany(l => l.WallObstacles.Concat(l.FurnitureObstacles).Concat(l.ProxyObstacles))
                        .ToList();

                Debug.Log($"[MultiLevel] 🔧 Bake GLOBAL ASYNC: {_levels.Count} niveles, " +
                          $"{allObstacles.Count} obstáculos{(_renderOnlyMode ? " (render-only: sin obstáculos)" : "")}, " +
                          $"{_detectedStairs.Count} escaleras");

                var (bakeOk, bakeStats) = await _globalBaker.BakeGlobalAsync(
                    navigableLevels,
                    allObstacles,
                    _navMeshLayer,
                    _agentTypeID,
                    _voxelSize,
                    _minRegionArea);

                if (!bakeOk)
                {
                    Debug.LogError("[MultiLevel] ❌ Bake global async fallido");
                    return false;
                }

                _diagnostics.bakingTime      = bakeStats.BakingTime;
                _diagnostics.navMeshVertices = bakeStats.VertexCount;
                _diagnostics.navMeshArea     = bakeStats.Area;

                PostBakeCleanup();

                if (_detectedStairs.Count > 0)
                {
                    _globalBaker.ValidateStairLinks(functionalRadiusM: 1.5f);
                    _diagnostics.stairLinksDetected = GetStairNavMeshLinks().Count;
                }

                _diagnostics.totalTime = (Time.realtimeSinceStartup - startTime) * 1000f;

                LogResults();
                if (_enableRuntimeDiagnostics) LogDiagnostics();

                EventBus.Instance?.Publish(new NavMeshGeneratedEvent { Success = true });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MultiLevel] ❌ {ex.Message}\n{ex.StackTrace}");
                _diagnostics.errors.Add(ex.Message);
                EventBus.Instance?.Publish(new NavMeshGeneratedEvent { Success = false });
                return false;
            }
        }

        public bool GenerateMultiLevelNavMesh()
        {
            Debug.Log("[MultiLevel] 🚀 Iniciando generación MULTI-NIVEL (sync, Unity 6.3+)...");
            float startTime = Time.realtimeSinceStartup;
            _diagnostics = new DiagnosticData { startTime = startTime };

            try
            {
                if (!ValidateInputMesh()) return false;
                if (_autoFindProxyMeshes && !_renderOnlyMode) FindProxyMeshes();

                ConfigureNavMeshAgentSettings();
                DetectStairHelpers();

                GeometryAnalysisResult analysis = _meshProcessor.AnalyzeScannedGeometry(_primaryMesh);
                if (analysis == null) return false;

                _diagnostics.meshVertexCount   = analysis.MeshVertexCount;
                _diagnostics.meshTriangleCount = analysis.MeshTriangleCount;

                List<NavigableLevel> navigableLevels = _meshProcessor.DetectNavigableLevels(analysis.HeightClusters);
                if (navigableLevels.Count == 0)
                {
                    Debug.LogError("[MultiLevel] ❌ No se detectaron niveles navegables");
                    return false;
                }

                _diagnostics.levelsDetected = navigableLevels.Count;

                _levels.Clear();
                foreach (NavigableLevel navLevel in navigableLevels)
                {
                    LevelData levelData = ProcessLevel(navLevel);
                    if (levelData != null)
                        _levels.Add(levelData);
                }

                if (_levels.Count > 1 && _detectedStairs.Count > 0)
                {
                    Debug.Log("[MultiLevel] ✂️ Generando huecos en pisos superiores...");
                    _openingGenerator.GenerateOpeningsForAllStairs();
                }

                List<GameObject> allObstacles = _renderOnlyMode
                    ? new List<GameObject>()
                    : _levels
                        .SelectMany(l => l.WallObstacles.Concat(l.FurnitureObstacles).Concat(l.ProxyObstacles))
                        .ToList();

                Debug.Log($"[MultiLevel] 🔧 Bake GLOBAL SYNC: {_levels.Count} niveles, " +
                          $"{allObstacles.Count} obstáculos");

                bool bakeOk = _globalBaker.BakeGlobal(
                    navigableLevels,
                    allObstacles,
                    _navMeshLayer,
                    _agentTypeID,
                    _voxelSize,
                    _minRegionArea,
                    out NavMeshBakingStats bakeStats);

                if (!bakeOk)
                {
                    Debug.LogError("[MultiLevel] ❌ Bake global sync fallido");
                    return false;
                }

                _diagnostics.bakingTime      = bakeStats.BakingTime;
                _diagnostics.navMeshVertices = bakeStats.VertexCount;
                _diagnostics.navMeshArea     = bakeStats.Area;

                PostBakeCleanup();

                if (_detectedStairs.Count > 0)
                {
                    _globalBaker.ValidateStairLinks(functionalRadiusM: 1.5f);
                    _diagnostics.stairLinksDetected = GetStairNavMeshLinks().Count;
                }

                _diagnostics.totalTime = (Time.realtimeSinceStartup - startTime) * 1000f;

                LogResults();
                if (_enableRuntimeDiagnostics) LogDiagnostics();

                EventBus.Instance?.Publish(new NavMeshGeneratedEvent { Success = true });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MultiLevel] ❌ {ex.Message}\n{ex.StackTrace}");
                _diagnostics.errors.Add(ex.Message);
                EventBus.Instance?.Publish(new NavMeshGeneratedEvent { Success = false });
                return false;
            }
        }

        #endregion

        #region Procesamiento por nivel

        private LevelData ProcessLevel(NavigableLevel navLevel)
        {
            Debug.Log($"[MultiLevel] 📐 Procesando nivel {navLevel.LevelIndex}...");

            LevelData levelData = new LevelData
            {
                Level              = navLevel,
                WallObstacles      = new List<GameObject>(),
                FurnitureObstacles = new List<GameObject>(),
                ProxyObstacles     = new List<GameObject>()
            };

            GameObject surface = _surfaceService.CreateLevelSurface(navLevel, out NavMeshSurface navMeshSurface);
            if (surface == null)
            {
                Debug.LogError($"[MultiLevel] ❌ No se pudo crear superficie nivel {navLevel.LevelIndex}");
                return null;
            }
            levelData.WalkableSurface = surface;
            levelData.NavMeshSurface  = navMeshSurface;

            // FIX v3: En render-only, no crear ningún obstáculo visual.
            // Los obstáculos ya están bakeados en el NavMesh guardado en disco.
            if (_renderOnlyMode)
            {
                Debug.Log($"[MultiLevel] 📱 Nivel {navLevel.LevelIndex} — render-only: sin obstáculos.");
                return levelData;
            }

            List<WallPlane> walls = _meshProcessor.DetectWallPlanesForLevel(_primaryMesh, navLevel);
            _diagnostics.wallPlanesDetected += walls.Count;

            WallObstacleResult wallResult = _obstacleService.CreateWallObstaclesWithValidation(
                walls, navLevel.FloorHeight, _agentHeight);
            levelData.WallObstacles = wallResult.Obstacles;
            _diagnostics.wallObstaclesCreated += wallResult.ValidWalls;
            _diagnostics.openingsDetected     += wallResult.Openings;

            if (_detectFurnitureInScan)
            {
                List<FurnitureCluster> furniture = _meshProcessor.DetectFurnitureForLevel(_primaryMesh, navLevel);
                _diagnostics.furnitureDetected += furniture.Count;
                levelData.FurnitureObstacles = _obstacleService.CreateFurnitureObstacles(furniture, navLevel.FloorHeight);
                _diagnostics.furnitureObstaclesCreated += levelData.FurnitureObstacles.Count;
            }

            List<MeshFilter> relevantProxies = _proxyMeshes
                .Where(pm => IsProxyInLevel(pm, navLevel))
                .ToList();

            if (relevantProxies.Count > 0)
            {
                ProxyObstacleResult proxyResult = _obstacleService.ProcessProxyMeshesUnified(relevantProxies);
                levelData.ProxyObstacles = proxyResult.Obstacles;
                _diagnostics.proxyObstaclesCreated += proxyResult.ProcessedCount;
            }

            return levelData;
        }

        private static bool IsProxyInLevel(MeshFilter pm, NavigableLevel level)
        {
            if (pm == null || pm.sharedMesh == null) return false;
            Renderer r = pm.GetComponent<Renderer>();
            if (r == null) return false;
            float cy = r.bounds.center.y;
            return cy >= level.MinY && cy <= level.MaxY;
        }

        #endregion

        #region API pública

        public void Clear()
        {
            _openingGenerator?.ClearPreviousOpenings();

            foreach (LevelData level in _levels)
            {
                if (level.WalkableSurface != null) Destroy(level.WalkableSurface);
                foreach (GameObject obj in level.WallObstacles)      { if (obj) Destroy(obj); }
                foreach (GameObject obj in level.FurnitureObstacles) { if (obj) Destroy(obj); }
                foreach (GameObject obj in level.ProxyObstacles)     { if (obj) Destroy(obj); }
            }

            _levels.Clear();
            _detectedStairs.Clear();

            NavigationStartPointManager.ClearAll();

            Debug.Log("[MultiLevel] 🧹 Limpiado (incluyendo NavigationStartPointManager)");
        }

        /// <summary>
        /// Activa/desactiva el modo render-only en tiempo de ejecución.
        /// </summary>
        public void SetRenderOnlyMode(bool renderOnly)
        {
            _renderOnlyMode = renderOnly;
            if (renderOnly)
            {
                _showDebugVisualization   = false;
                _autoFindProxyMeshes      = false;
                _debugDrawOpeningRaycasts = false;
            }
            Debug.Log($"[MultiLevel] 📱 renderOnlyMode = {renderOnly}");
        }

        #endregion

        #region Validación

        private bool ValidateInputMesh()
        {
            if (_primaryMesh == null || _primaryMesh.sharedMesh == null)
            {
                Debug.LogError("[MultiLevel] ❌ No hay mesh principal");
                return false;
            }
            Mesh mesh = _primaryMesh.sharedMesh;
            if (mesh.vertices.Length == 0)
            {
                Debug.LogError("[MultiLevel] ❌ Mesh vacío");
                return false;
            }
            Debug.Log($"[MultiLevel] ✅ Mesh: {mesh.vertices.Length} verts, {mesh.triangles.Length / 3} tris");
            return true;
        }

        #endregion

        #region Diagnóstico

        [ContextMenu("🔍 Diagnose NavMesh Multi-Level Connectivity")]
        public void DiagnoseNavMeshConnectivity()
        {
            Debug.Log("========== 🔍 DIAGNÓSTICO CONECTIVIDAD MULTI-NIVEL ==========");

            var pts = NavigationStartPointManager.GetAllStartPoints();
            Debug.Log($"StartPoints: {pts.Count}");
            foreach (var pt in pts)
            {
                bool hasNavMesh = NavMesh.SamplePosition(pt.Position, out NavMeshHit h, 2f, NavMesh.AllAreas);
                Debug.Log($"  Level {pt.Level} Y={pt.FloorHeight:F2}m: NavMesh={hasNavMesh} " +
                          $"@ {(hasNavMesh ? h.position.ToString("F3") : "N/A")} " +
                          $"(dist={( hasNavMesh ? Vector3.Distance(pt.Position, h.position) : -1f):F3}m)");
            }

            if (pts.Count >= 2)
            {
                NavMesh.SamplePosition(pts[0].Position, out NavMeshHit h0, 2f, NavMesh.AllAreas);
                NavMesh.SamplePosition(pts[pts.Count - 1].Position, out NavMeshHit h1, 2f, NavMesh.AllAreas);

                NavMeshPath path = new NavMeshPath();
                NavMesh.CalculatePath(h0.position, h1.position, NavMesh.AllAreas, path);
                Debug.Log($"Path Floor0→Floor{pts.Count - 1}: status={path.status}, corners={path.corners.Length}");
            }

            var stairs = FindObjectsByType<StairWithLandingHelper>(FindObjectsSortMode.None);
            Debug.Log($"StairHelpers: {stairs.Length}");
            foreach (var s in stairs)
                s.DiagnoseRamps();

            _globalBaker?.ValidateStairLinks();

            Debug.Log("=============================================================");
        }

        #endregion

        #region Logs

        private void LogResults()
        {
            Debug.Log("======= ✅ RESUMEN MULTI-NIVEL (bake global async) =======");
            foreach (LevelData level in _levels)
                Debug.Log($"  Nivel {level.Level.LevelIndex}: Y={level.Level.FloorHeight:F2}m, " +
                          $"paredes={level.WallObstacles.Count}, muebles={level.FurnitureObstacles.Count}");
            Debug.Log($"  Escaleras: {_detectedStairs.Count} | Links: {_diagnostics.stairLinksDetected}");
            Debug.Log($"  NavMesh global: {_diagnostics.navMeshVertices} verts, {_diagnostics.navMeshArea:F1}m²");
            Debug.Log($"  Tiempo total: {_diagnostics.totalTime:F0}ms");
            Debug.Log("====================================================");
        }

        private void LogDiagnostics()
        {
            Debug.Log("======= 📊 DIAGNÓSTICOS =======");
            Debug.Log($"Niveles: {_diagnostics.levelsDetected} detectados, {_levels.Count} procesados");
            foreach (LevelData level in _levels)
                Debug.Log($"  [{level.Level.LevelIndex}] Y={level.Level.FloorHeight:F2}m " +
                          $"[{level.Level.MinY:F2},{level.Level.MaxY:F2}]");
            Debug.Log($"Agente: slope={_agentMaxSlope}°, step={_agentStepHeight}m");
            if (_diagnostics.errors.Count > 0)
                foreach (string e in _diagnostics.errors)
                    Debug.LogWarning($"  Error: {e}");
            Debug.Log("================================");
        }

        #endregion

        #region Debug Gizmos

        private void OnDrawGizmos()
        {
            // FIX v3: En render-only no dibujar gizmos de debug
            if (!_showDebugVisualization || _renderOnlyMode) return;
            for (int i = 0; i < _levels.Count; i++)
            {
                LevelData level = _levels[i];
                Gizmos.color = Color.Lerp(Color.green, Color.blue, i / (float)Mathf.Max(1, _levels.Count - 1));
                Gizmos.DrawWireCube(level.Level.HorizontalBounds.center, level.Level.HorizontalBounds.size);
            }
            Gizmos.color = Color.yellow;
            foreach (StairWithLandingHelper stair in _detectedStairs)
                if (stair != null) Gizmos.DrawWireSphere(stair.transform.position, 0.3f);
        }

        #endregion

        #region Data Structures

        private sealed class LevelData
        {
            public NavigableLevel    Level;
            public GameObject        WalkableSurface;
            public NavMeshSurface    NavMeshSurface;
            public List<GameObject>  WallObstacles;
            public List<GameObject>  FurnitureObstacles;
            public List<GameObject>  ProxyObstacles;
        }

        private sealed class DiagnosticData
        {
            public float startTime, totalTime, bakingTime;
            public int meshVertexCount, meshTriangleCount, levelsDetected;
            public int wallPlanesDetected, wallObstaclesCreated, openingsDetected;
            public int furnitureDetected, furnitureObstaclesCreated, proxyObstaclesCreated;
            public int stairLinksDetected, navMeshVertices, navMeshTriangles;
            public float navMeshArea;
            public List<string> errors = new List<string>();
        }

        #endregion

        #region Context Menu

        [ContextMenu("🚀 Generate Multi-Level NavMesh")]
        private void ContextGenerate() => _ = GenerateMultiLevelNavMeshAsync();

        [ContextMenu("🧹 Clear All")]
        private void ContextClear() => Clear();

        [ContextMenu("🔍 Find Proxy Meshes")]
        private void ContextFindProxy() => FindProxyMeshes();

        [ContextMenu("🔗 Detect Stair Helpers")]
        private void ContextDetectStairs() => DetectStairHelpers();

        [ContextMenu("✅ Validate Stair Links")]
        private void ContextVerifyLinks() => _globalBaker?.ValidateStairLinks();

        [ContextMenu("✂️ Test Floor Openings")]
        private void ContextTestOpenings() => _openingGenerator?.GenerateOpeningsForAllStairs();

        [ContextMenu("🧹 Post Bake Cleanup")]
        private void ContextPostBakeCleanup() => PostBakeCleanup();

        [ContextMenu("📱 Toggle RenderOnly Mode")]
        private void ContextToggleRenderOnly() => SetRenderOnlyMode(!_renderOnlyMode);

        #endregion
    }
}