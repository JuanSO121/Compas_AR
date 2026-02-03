// File: ObstacleService.cs

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.AI.Navigation;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Servicio de gestión de obstáculos: paredes, muebles, proxies y validación de aperturas
    /// </summary>
    public class ObstacleService
    {
        private readonly float _obstaclePadding;
        private readonly float _obstacleHeightPadding;
        private readonly float _agentHeight;
        private readonly int _navMeshLayer;
        private readonly int _navMeshAreaNotWalkable;
        
        private readonly bool _detectOpenings;
        private readonly float _minOpeningWidth;
        private readonly float _minFullWallHeight;
        private readonly int _openingRaycastGridSize;
        private readonly float _openingRaycastDistance;
        private readonly float _openingPassThroughThreshold;
        private readonly LayerMask _openingDetectionLayers;
        private readonly bool _debugDrawOpeningRaycasts;
        private readonly bool _logDetailedAnalysis;
        
        private readonly bool _useLODForDistantObstacles;
        private readonly float _lodDistanceThreshold;
        private readonly int _maxObstaclesPerFrame;
        private readonly bool _showDebugVisualization;

        public ObstacleService(
            float obstaclePadding,
            float obstacleHeightPadding,
            float agentHeight,
            int navMeshLayer,
            int navMeshAreaNotWalkable,
            bool detectOpenings,
            float minOpeningWidth,
            float minFullWallHeight,
            int openingRaycastGridSize,
            float openingRaycastDistance,
            float openingPassThroughThreshold,
            LayerMask openingDetectionLayers,
            bool debugDrawOpeningRaycasts,
            bool logDetailedAnalysis,
            bool useLODForDistantObstacles,
            float lodDistanceThreshold,
            int maxObstaclesPerFrame,
            bool showDebugVisualization)
        {
            _obstaclePadding = obstaclePadding;
            _obstacleHeightPadding = obstacleHeightPadding;
            _agentHeight = agentHeight;
            _navMeshLayer = navMeshLayer;
            _navMeshAreaNotWalkable = navMeshAreaNotWalkable;
            
            _detectOpenings = detectOpenings;
            _minOpeningWidth = minOpeningWidth;
            _minFullWallHeight = minFullWallHeight;
            _openingRaycastGridSize = openingRaycastGridSize;
            _openingRaycastDistance = openingRaycastDistance;
            _openingPassThroughThreshold = openingPassThroughThreshold;
            _openingDetectionLayers = openingDetectionLayers;
            _debugDrawOpeningRaycasts = debugDrawOpeningRaycasts;
            _logDetailedAnalysis = logDetailedAnalysis;
            
            _useLODForDistantObstacles = useLODForDistantObstacles;
            _lodDistanceThreshold = lodDistanceThreshold;
            _maxObstaclesPerFrame = maxObstaclesPerFrame;
            _showDebugVisualization = showDebugVisualization;
        }

        #region Wall Obstacles

        public WallObstacleResult CreateWallObstaclesWithValidation(List<WallPlane> wallPlanes, float floorHeight, float agentHeight)
        {
            List<GameObject> obstacles = new List<GameObject>();
            int validWalls = 0;
            int openings = 0;
            
            foreach (var plane in wallPlanes)
            {
                string reason;
                bool isWall = IsActualWallOrOpening(plane, floorHeight, out reason);
                
                if (isWall)
                {
                    GameObject obstacle = CreateWallObstacle(plane, floorHeight, obstacles.Count);
                    obstacles.Add(obstacle);
                    validWalls++;
                    
                    if (_logDetailedAnalysis)
                    {
                        Debug.Log($"[NavAR] 🧱 Pared: {plane.bounds.center} → {reason}");
                    }
                }
                else
                {
                    openings++;
                    
                    if (_logDetailedAnalysis)
                    {
                        Debug.Log($"[NavAR] 🚪 Apertura: {plane.bounds.center} → {reason}");
                    }
                }
            }
            
            Debug.Log($"[NavAR] ✅ Obstáculos de pared: {validWalls} creados, {openings} aperturas ignoradas");
            
            return new WallObstacleResult
            {
                Obstacles = obstacles,
                ValidWalls = validWalls,
                Openings = openings
            };
        }

        private bool IsActualWallOrOpening(WallPlane plane, float floorHeight, out string reason)
        {
            reason = "";
            
            if (!_detectOpenings)
            {
                reason = "detección desactivada";
                return true;
            }
            
            float wallHeight = plane.bounds.max.y - floorHeight;
            
            if (wallHeight >= _minFullWallHeight)
            {
                reason = $"pared completa (h={wallHeight:F2}m)";
                return true;
            }
            
            float wallWidth = Mathf.Max(plane.bounds.size.x, plane.bounds.size.z);
            
            if (wallWidth < _minOpeningWidth)
            {
                reason = $"muy estrecha (w={wallWidth:F2}m)";
                return true;
            }
            
            Vector3 gridCenter = plane.bounds.center;
            gridCenter.y = floorHeight + (_agentHeight / 2f);
            
            Vector3 rayDirection = plane.planeNormal;
            
            float gridSizeX = Mathf.Min(plane.bounds.size.x, _minOpeningWidth * 1.5f);
            float gridSizeY = Mathf.Min(wallHeight, _agentHeight);
            
            int passedCount = 0;
            int totalRays = _openingRaycastGridSize * _openingRaycastGridSize;
            
            for (int y = 0; y < _openingRaycastGridSize; y++)
            {
                for (int x = 0; x < _openingRaycastGridSize; x++)
                {
                    float tx = (x / (float)(_openingRaycastGridSize - 1)) - 0.5f;
                    float ty = (y / (float)(_openingRaycastGridSize - 1)) - 0.5f;
                    
                    Vector3 offset = plane.bounds.size.x > plane.bounds.size.z
                        ? new Vector3(tx * gridSizeX, ty * gridSizeY, 0f)
                        : new Vector3(0f, ty * gridSizeY, tx * gridSizeX);
                    
                    Vector3 rayOrigin = gridCenter + offset;
                    
                    bool hitForward = Physics.Raycast(rayOrigin, rayDirection, _openingRaycastDistance, _openingDetectionLayers);
                    bool hitBackward = Physics.Raycast(rayOrigin, -rayDirection, _openingRaycastDistance, _openingDetectionLayers);
                    
                    if (!hitForward && !hitBackward)
                    {
                        passedCount++;
                    }
                    
                    if (_debugDrawOpeningRaycasts)
                    {
                        Color color = (!hitForward && !hitBackward) ? Color.green : Color.red;
                        Debug.DrawRay(rayOrigin, rayDirection * _openingRaycastDistance, color, 5f);
                        Debug.DrawRay(rayOrigin, -rayDirection * _openingRaycastDistance, color, 5f);
                    }
                }
            }
            
            float passThroughRatio = passedCount / (float)totalRays;
            
            if (passThroughRatio >= _openingPassThroughThreshold)
            {
                reason = $"apertura ({passThroughRatio * 100f:F0}% atravesado)";
                return false;
            }
            else
            {
                reason = $"pared ({passThroughRatio * 100f:F0}% atravesado)";
                return true;
            }
        }

        private GameObject CreateWallObstacle(WallPlane plane, float floorHeight, int index)
        {
            Bounds expandedBounds = plane.bounds;
            
            Vector3 padding = new Vector3(_obstaclePadding, _obstacleHeightPadding, _obstaclePadding);
            expandedBounds.Expand(padding * 2f);
            
            if (expandedBounds.size.y < _agentHeight)
            {
                Vector3 newSize = expandedBounds.size;
                newSize.y = _agentHeight;
                expandedBounds.size = newSize;
                
                Vector3 newCenter = expandedBounds.center;
                newCenter.y = floorHeight + (_agentHeight / 2f);
                expandedBounds.center = newCenter;
            }
            
            GameObject obstacleObj = new GameObject($"WallObstacle_{index}");
            obstacleObj.transform.position = expandedBounds.center;
            obstacleObj.layer = _navMeshLayer;
            obstacleObj.isStatic = true;
            
            MeshFilter mf = obstacleObj.AddComponent<MeshFilter>();
            mf.mesh = CreateBoxMesh(expandedBounds.size);
            
            MeshCollider wallCollider = obstacleObj.AddComponent<MeshCollider>();
            wallCollider.sharedMesh = mf.mesh;
            wallCollider.convex = false;
            
            NavMeshModifier modifier = obstacleObj.AddComponent<NavMeshModifier>();
            modifier.overrideArea = true;
            modifier.area = _navMeshAreaNotWalkable;
            modifier.ignoreFromBuild = false;
            
            if (_showDebugVisualization)
            {
                MeshRenderer mr = obstacleObj.AddComponent<MeshRenderer>();
                Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.color = new Color(1f, 0f, 0f, 0.25f);
                mr.material = mat;
            }
            
            return obstacleObj;
        }

        #endregion

        #region Furniture Obstacles

        public List<GameObject> CreateFurnitureObstacles(List<FurnitureCluster> furnitureClusters, float floorHeight)
        {
            List<GameObject> obstacles = new List<GameObject>();
            
            int index = 0;
            foreach (var furniture in furnitureClusters)
            {
                GameObject obstacle = CreateFurnitureObstacle(furniture, floorHeight, index++);
                obstacles.Add(obstacle);
            }
            
            Debug.Log($"[NavAR] ✅ Obstáculos de muebles creados: {obstacles.Count}");
            
            return obstacles;
        }

        private GameObject CreateFurnitureObstacle(FurnitureCluster furniture, float floorHeight, int index)
        {
            Bounds expandedBounds = furniture.bounds;
            expandedBounds.Expand(new Vector3(_obstaclePadding, _obstacleHeightPadding, _obstaclePadding) * 2f);
            
            Vector3 center = expandedBounds.center;
            center.y = floorHeight + (expandedBounds.size.y / 2f);
            
            GameObject obstacleObj = new GameObject($"FurnitureObstacle_{index}");
            obstacleObj.transform.position = center;
            obstacleObj.layer = _navMeshLayer;
            obstacleObj.isStatic = true;
            
            MeshFilter mf = obstacleObj.AddComponent<MeshFilter>();
            mf.mesh = CreateBoxMesh(expandedBounds.size);
            
            MeshCollider mc = obstacleObj.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.mesh;
            mc.convex = false;
            
            NavMeshModifier modifier = obstacleObj.AddComponent<NavMeshModifier>();
            modifier.overrideArea = true;
            modifier.area = _navMeshAreaNotWalkable;
            modifier.ignoreFromBuild = false;
            
            if (_showDebugVisualization)
            {
                MeshRenderer mr = obstacleObj.AddComponent<MeshRenderer>();
                Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.color = new Color(1f, 0.5f, 0f, 0.3f);
                mr.material = mat;
            }
            
            return obstacleObj;
        }

        #endregion

        #region Proxy Mesh Obstacles

        public ProxyObstacleResult ProcessProxyMeshesUnified(List<MeshFilter> proxyMeshes)
        {
            if (proxyMeshes == null || proxyMeshes.Count == 0)
            {
                Debug.Log("[NavAR] ℹ️ No hay meshes proxy");
                return new ProxyObstacleResult { Obstacles = new List<GameObject>(), ProcessedCount = 0 };
            }
            
            Debug.Log($"[NavAR] 📦 Procesando {proxyMeshes.Count} proxies (método unificado)...");
            
            List<GameObject> obstacles = new List<GameObject>();
            Vector3 cameraPos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            
            int processedCount = 0;
            int lodSkippedCount = 0;
            
            foreach (var proxyMesh in proxyMeshes)
            {
                if (proxyMesh == null || proxyMesh.gameObject == null)
                    continue;
                
                GameObject obj = proxyMesh.gameObject;
                
                if (_useLODForDistantObstacles)
                {
                    float distance = Vector3.Distance(obj.transform.position, cameraPos);
                    if (distance > _lodDistanceThreshold)
                    {
                        lodSkippedCount++;
                        continue;
                    }
                }
                
                if (proxyMesh.sharedMesh == null || proxyMesh.sharedMesh.vertexCount == 0)
                {
                    Debug.LogWarning($"[NavAR] ⚠️ Proxy sin mesh válido: {obj.name}");
                    continue;
                }
                
                obj.isStatic = true;
                obj.layer = _navMeshLayer;
                
                Bounds worldBounds;
                Renderer renderer = obj.GetComponent<Renderer>();
                
                if (renderer != null)
                {
                    worldBounds = renderer.bounds;
                }
                else
                {
                    worldBounds = CalculateMeshBounds(proxyMesh);
                }
                
                if (worldBounds.size.magnitude < 0.01f || worldBounds.size.magnitude > 100f)
                {
                    Debug.LogWarning($"[NavAR] ⚠️ Bounds inválidos en proxy: {obj.name}");
                    continue;
                }
                
                worldBounds.Expand(new Vector3(_obstaclePadding, _obstacleHeightPadding, _obstaclePadding) * 2f);
                
                Collider existingCollider = obj.GetComponent<Collider>();
                
                if (existingCollider == null)
                {
                    MeshCollider mc = obj.AddComponent<MeshCollider>();
                    mc.sharedMesh = proxyMesh.sharedMesh;
                    mc.convex = false;
                }
                else if (existingCollider is BoxCollider bc)
                {
                    Vector3 localCenter = obj.transform.InverseTransformPoint(worldBounds.center);
                    Vector3 localSize = new Vector3(
                        worldBounds.size.x / obj.transform.lossyScale.x,
                        worldBounds.size.y / obj.transform.lossyScale.y,
                        worldBounds.size.z / obj.transform.lossyScale.z
                    );
                    
                    bc.center = localCenter;
                    bc.size = localSize;
                }
                
                NavMeshModifier modifier = obj.GetComponent<NavMeshModifier>();
                if (modifier == null)
                {
                    modifier = obj.AddComponent<NavMeshModifier>();
                }
                
                modifier.overrideArea = true;
                modifier.area = _navMeshAreaNotWalkable;
                modifier.ignoreFromBuild = false;
                
                obstacles.Add(obj);
                processedCount++;
                
                if (_maxObstaclesPerFrame > 0 && processedCount >= _maxObstaclesPerFrame)
                {
                    break;
                }
            }
            
            Debug.Log($"[NavAR] ✅ Proxies procesados: {processedCount}, LOD skipped: {lodSkippedCount}");
            
            return new ProxyObstacleResult
            {
                Obstacles = obstacles,
                ProcessedCount = processedCount
            };
        }

        private Bounds CalculateMeshBounds(MeshFilter meshFilter)
        {
            Mesh mesh = meshFilter.sharedMesh;
            Transform transform = meshFilter.transform;
            
            Vector3[] vertices = mesh.vertices;
            if (vertices.Length == 0)
                return new Bounds(transform.position, Vector3.one * 0.5f);
            
            Vector3 min = transform.TransformPoint(vertices[0]);
            Vector3 max = min;
            
            foreach (Vector3 v in vertices)
            {
                Vector3 worldV = transform.TransformPoint(v);
                min = Vector3.Min(min, worldV);
                max = Vector3.Max(max, worldV);
            }
            
            return new Bounds((min + max) / 2f, max - min);
        }

        #endregion

        #region Utilities

        private Mesh CreateBoxMesh(Vector3 size)
        {
            Mesh mesh = new Mesh { name = "BoxObstacleMesh" };
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
            mesh.RecalculateBounds();
            
            return mesh;
        }

        #endregion
    }

    #region Data Structures

    public class WallObstacleResult
    {
        public List<GameObject> Obstacles;
        public int ValidWalls;
        public int Openings;
    }

    public class ProxyObstacleResult
    {
        public List<GameObject> Obstacles;
        public int ProcessedCount;
    }

    #endregion
}