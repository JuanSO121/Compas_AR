// File: ObstacleService.cs
// ✅ VERSIÓN COMPLETA CORREGIDA
// ✅ Padding que REALMENTE preserva espacios navegables en interiores

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.AI.Navigation;

namespace IndoorNavAR.Navigation
{
    public class ObstacleService
    {
        private readonly float _wallPadding;
        private readonly float _furniturePadding;
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
        
        private readonly bool _mergeNearbyObstacles;
        private readonly float _mergeDistance;
        
        private readonly bool _indoorMode;
        private readonly float _minPassageWidth;
        private readonly bool _smartPaddingReduction;

        public ObstacleService(
            float obstaclePadding,
            float obstacleHeightPadding,
            float furniturePadding,
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
            bool showDebugVisualization,
            bool mergeNearbyObstacles,
            float mergeDistance,
            bool indoorMode = true,
            float minPassageWidth = 0.6f,
            bool smartPaddingReduction = true)
        {
            _wallPadding = obstaclePadding;
            _furniturePadding = furniturePadding;
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
            
            _mergeNearbyObstacles = mergeNearbyObstacles;
            _mergeDistance = mergeDistance;
            
            _indoorMode = indoorMode;
            _minPassageWidth = minPassageWidth;
            _smartPaddingReduction = smartPaddingReduction;
            
            Debug.Log($"[Obstacle] 🏠 Modo Interior: {(_indoorMode ? "ACTIVADO" : "off")} | " +
                     $"WallPadding={_wallPadding*1000:F1}mm | MinPassage={_minPassageWidth*100:F0}cm");
        }

        #region Wall Obstacles

        public WallObstacleResult CreateWallObstaclesWithValidation(
            List<WallPlane> wallPlanes, 
            float floorHeight, 
            float agentHeight)
        {
            List<GameObject> obstacles = new List<GameObject>();
            int validWalls = 0;
            int openings = 0;
            int excluded = 0;
            int segmented = 0;
            
            foreach (var plane in wallPlanes)
            {
                var segments = SegmentWallPlaneByExclusion(plane);
                
                if (segments.Count == 0)
                {
                    excluded++;
                    if (_logDetailedAnalysis)
                        Debug.Log($"[Obstacle] 🚫 Pared excluida: {plane.bounds.center}");
                    continue;
                }
                
                if (segments.Count > 1)
                {
                    segmented++;
                    if (_logDetailedAnalysis)
                        Debug.Log($"[Obstacle] ✂️ Pared segmentada en {segments.Count}");
                }
                
                foreach (var segment in segments)
                {
                    string reason;
                    bool isWall = IsActualWallOrOpening(segment, floorHeight, out reason);
                    
                    if (isWall)
                    {
                        GameObject obstacle = CreateWallObstacle(segment, floorHeight, obstacles.Count, obstacles);
                        
                        if (obstacle != null)
                        {
                            obstacles.Add(obstacle);
                            validWalls++;
                            
                            if (_logDetailedAnalysis)
                                Debug.Log($"[Obstacle] 🧱 Pared: {segment.bounds.center} → {reason}");
                        }
                    }
                    else
                    {
                        openings++;
                        if (_logDetailedAnalysis)
                            Debug.Log($"[Obstacle] 🚪 Apertura: {segment.bounds.center} → {reason}");
                    }
                }
            }
            
            Debug.Log($"[Obstacle] ✅ Paredes: {validWalls} creadas, {openings} aperturas, " +
                     $"{excluded} excluidas, {segmented} segmentadas");
            
            return new WallObstacleResult
            {
                Obstacles = obstacles,
                ValidWalls = validWalls,
                Openings = openings,
                ExcludedByZone = excluded,
                SegmentedWalls = segmented
            };
        }

        private List<WallPlane> SegmentWallPlaneByExclusion(WallPlane originalPlane)
        {
            List<WallPlane> segments = new List<WallPlane>();
            
            if (!NavMeshWallExclusionManager.IsBoundsInWallExclusionZone(originalPlane.bounds))
            {
                segments.Add(originalPlane);
                return segments;
            }
            
            List<List<WallTriangle>> triangleGroups = new List<List<WallTriangle>>();
            List<WallTriangle> currentGroup = new List<WallTriangle>();
            
            foreach (var triangle in originalPlane.triangles)
            {
                Vector3 triCenter = (triangle.v0 + triangle.v1 + triangle.v2) / 3f;
                bool inExclusion = NavMeshWallExclusionManager.IsPointInWallExclusionZone(triCenter);
                
                if (!inExclusion)
                {
                    currentGroup.Add(triangle);
                }
                else
                {
                    if (currentGroup.Count > 0)
                    {
                        triangleGroups.Add(currentGroup);
                        currentGroup = new List<WallTriangle>();
                    }
                }
            }
            
            if (currentGroup.Count > 0)
            {
                triangleGroups.Add(currentGroup);
            }
            
            foreach (var group in triangleGroups)
            {
                if (group.Count < 2) continue;
                
                Vector3 avgNormal = Vector3.zero;
                foreach (var tri in group)
                    avgNormal += tri.normal;
                avgNormal = (avgNormal / group.Count).normalized;
                
                float totalArea = 0f;
                foreach (var tri in group)
                    totalArea += tri.area;
                
                List<Vector3> allVertices = new List<Vector3>(group.Count * 3);
                foreach (var tri in group)
                {
                    allVertices.Add(tri.v0);
                    allVertices.Add(tri.v1);
                    allVertices.Add(tri.v2);
                }
                
                Bounds bounds = CalculateBounds(allVertices);
                
                segments.Add(new WallPlane
                {
                    planeNormal = avgNormal,
                    bounds = bounds,
                    triangles = group,
                    totalArea = totalArea,
                    levelIndex = originalPlane.levelIndex
                });
            }
            
            return segments;
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

        private GameObject CreateWallObstacle(
            WallPlane plane, 
            float floorHeight, 
            int index,
            List<GameObject> existingObstacles)
        {
            Bounds originalBounds = plane.bounds;
            
            // ✅ ANÁLISIS CRÍTICO: Detectar gaps REALES
            GapAnalysisResult gapAnalysis = AnalyzeGapsAroundWall(
                originalBounds, 
                plane.planeNormal, 
                existingObstacles
            );
            
            // ✅ Calcular padding que PRESERVA gaps
            Vector3 adaptivePadding = CalculateAdaptivePaddingFromGaps(
                originalBounds,
                plane.planeNormal,
                gapAnalysis
            );
            
            Bounds expandedBounds = originalBounds;
            
            // ✅ Expandir SOLO en direcciones seguras
            if (gapAnalysis.HasGapInFront)
            {
                Vector3 expansion = adaptivePadding;
                expansion = RemoveComponentInDirection(expansion, plane.planeNormal);
                expandedBounds.Expand(expansion * 2f);
            }
            else
            {
                expandedBounds.Expand(adaptivePadding * 2f);
            }
            
            // Asegurar altura mínima
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
            
            if (_logDetailedAnalysis)
            {
                Debug.Log($"[Obstacle] 🧱 Pared {index}: " +
                         $"gap={gapAnalysis.NearestGapDistance:F3}m, " +
                         $"padding=[{adaptivePadding.x*1000:F0}mm, {adaptivePadding.z*1000:F0}mm]");
            }
            
            return obstacleObj;
        }

        #endregion

        #region Gap Analysis - CRITICAL

        private GapAnalysisResult AnalyzeGapsAroundWall(
            Bounds bounds, 
            Vector3 normal, 
            List<GameObject> existingObstacles)
        {
            var result = new GapAnalysisResult
            {
                NearestGapDistance = float.MaxValue,
                HasGapInFront = false,
                HasGapBehind = false,
                HasGapLeft = false,
                HasGapRight = false
            };
            
            if (!_indoorMode || !_smartPaddingReduction)
            {
                return result;
            }
            
            Vector3 horizontalNormal = new Vector3(normal.x, 0, normal.z).normalized;
            
            if (horizontalNormal.magnitude < 0.1f)
            {
                return result;
            }
            
            Vector3 forward = horizontalNormal;
            Vector3 backward = -horizontalNormal;
            Vector3 right = new Vector3(-forward.z, 0, forward.x).normalized;
            Vector3 left = -right;
            
            float checkDistance = _minPassageWidth * 1.2f;
            
            result.HasGapInFront = !HasObstacleInDirection(bounds.center, forward, checkDistance, existingObstacles);
            result.HasGapBehind = !HasObstacleInDirection(bounds.center, backward, checkDistance, existingObstacles);
            result.HasGapLeft = !HasObstacleInDirection(bounds.center, left, checkDistance, existingObstacles);
            result.HasGapRight = !HasObstacleInDirection(bounds.center, right, checkDistance, existingObstacles);
            
            if (result.HasGapInFront)
            {
                float dist = MeasureDistanceToNextObstacle(bounds.center, forward, checkDistance * 2f, existingObstacles);
                result.NearestGapDistance = Mathf.Min(result.NearestGapDistance, dist);
            }
            
            return result;
        }

        private bool HasObstacleInDirection(
            Vector3 origin, 
            Vector3 direction, 
            float distance, 
            List<GameObject> obstacles)
        {
            foreach (var obs in obstacles)
            {
                if (obs == null) continue;
                
                Renderer renderer = obs.GetComponent<Renderer>();
                if (renderer == null) continue;
                
                Bounds obsBounds = renderer.bounds;
                
                Vector3 toObstacle = obsBounds.center - origin;
                float projection = Vector3.Dot(toObstacle, direction);
                
                if (projection > 0 && projection < distance)
                {
                    Vector3 perpendicular = toObstacle - direction * projection;
                    
                    if (perpendicular.magnitude < (obsBounds.size.magnitude / 2f + 0.5f))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }

        private float MeasureDistanceToNextObstacle(
            Vector3 origin, 
            Vector3 direction, 
            float maxDistance, 
            List<GameObject> obstacles)
        {
            float nearestDistance = maxDistance;
            
            foreach (var obs in obstacles)
            {
                if (obs == null) continue;
                
                Renderer renderer = obs.GetComponent<Renderer>();
                if (renderer == null) continue;
                
                Bounds obsBounds = renderer.bounds;
                
                Vector3 toObstacle = obsBounds.center - origin;
                float projection = Vector3.Dot(toObstacle, direction);
                
                if (projection > 0 && projection < nearestDistance)
                {
                    Vector3 perpendicular = toObstacle - direction * projection;
                    
                    if (perpendicular.magnitude < (obsBounds.size.magnitude / 2f + 0.5f))
                    {
                        nearestDistance = projection;
                    }
                }
            }
            
            return nearestDistance;
        }

        private Vector3 CalculateAdaptivePaddingFromGaps(
            Bounds bounds,
            Vector3 normal,
            GapAnalysisResult gapAnalysis)
        {
            Vector3 basePadding = new Vector3(
                _wallPadding, 
                _obstacleHeightPadding, 
                _wallPadding
            );
            
            if (!_indoorMode || !_smartPaddingReduction)
            {
                return basePadding;
            }
            
            if (gapAnalysis.NearestGapDistance < _minPassageWidth)
            {
                float gapWidth = gapAnalysis.NearestGapDistance;
                
                float maxAllowedPadding = Mathf.Max(
                    0.01f,
                    (gapWidth - _minPassageWidth) / 2f
                );
                
                float adjustedPadding = Mathf.Min(_wallPadding, maxAllowedPadding);
                
                Vector3 horizontalNormal = new Vector3(normal.x, 0, normal.z).normalized;
                Vector3 adaptivePadding = basePadding;
                
                if (Mathf.Abs(horizontalNormal.x) > Mathf.Abs(horizontalNormal.z))
                {
                    adaptivePadding.x = adjustedPadding;
                }
                else
                {
                    adaptivePadding.z = adjustedPadding;
                }
                
                if (_logDetailedAnalysis)
                {
                    Debug.Log($"[Obstacle] 🎯 Padding REDUCIDO: {_wallPadding*1000:F1}mm → {adjustedPadding*1000:F1}mm " +
                             $"(gap={gapWidth*100:F0}cm, objetivo={_minPassageWidth*100:F0}cm)");
                }
                
                return adaptivePadding;
            }
            
            return basePadding;
        }

        private Vector3 RemoveComponentInDirection(Vector3 vec, Vector3 direction)
        {
            Vector3 normalized = direction.normalized;
            float component = Vector3.Dot(vec, normalized);
            return vec - normalized * component;
        }

        #endregion

        #region Furniture Obstacles

        public List<GameObject> CreateFurnitureObstacles(List<FurnitureCluster> furnitureClusters, float floorHeight)
        {
            List<GameObject> obstacles = new List<GameObject>();
            int excluded = 0;
            
            int index = 0;
            foreach (var furniture in furnitureClusters)
            {
                GameObject obstacle = CreateFurnitureObstacle(furniture, floorHeight, index++);
                
                if (obstacle != null)
                {
                    obstacles.Add(obstacle);
                }
                else
                {
                    excluded++;
                }
            }
            
            if (excluded > 0)
            {
                Debug.Log($"[Obstacle] ✅ Muebles: {obstacles.Count} creados, {excluded} excluidos");
            }
            else
            {
                Debug.Log($"[Obstacle] ✅ Obstáculos de muebles: {obstacles.Count}");
            }
            
            return obstacles;
        }

        private GameObject CreateFurnitureObstacle(FurnitureCluster furniture, float floorHeight, int index)
        {
            if (NavMeshWallExclusionManager.IsBoundsInFurnitureExclusionZone(furniture.bounds))
            {
                if (_logDetailedAnalysis)
                    Debug.Log($"[Obstacle] 🚫 Mueble {index} en zona de exclusión");
                return null;
            }
            
            Bounds expandedBounds = furniture.bounds;
            
            Vector3 furniturePaddingVec = new Vector3(
                _furniturePadding, 
                _obstacleHeightPadding, 
                _furniturePadding
            );
            expandedBounds.Expand(furniturePaddingVec * 2f);
            
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
                Debug.Log("[Obstacle] ℹ️ No hay meshes proxy");
                return new ProxyObstacleResult { Obstacles = new List<GameObject>(), ProcessedCount = 0 };
            }
            
            Debug.Log($"[Obstacle] 📦 Procesando {proxyMeshes.Count} proxies...");
            
            List<GameObject> obstacles = new List<GameObject>();
            int processedCount = 0;
            
            foreach (var proxyMesh in proxyMeshes)
            {
                if (proxyMesh == null || proxyMesh.gameObject == null)
                    continue;
                
                GameObject obj = proxyMesh.gameObject;
                
                if (proxyMesh.sharedMesh == null || proxyMesh.sharedMesh.vertexCount == 0)
                {
                    Debug.LogWarning($"[Obstacle] ⚠️ Proxy sin mesh: {obj.name}");
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
                    Debug.LogWarning($"[Obstacle] ⚠️ Bounds inválidos: {obj.name}");
                    continue;
                }
                
                worldBounds.Expand(new Vector3(_wallPadding, _obstacleHeightPadding, _wallPadding) * 2f);
                
                Collider existingCollider = obj.GetComponent<Collider>();
                
                if (existingCollider == null)
                {
                    MeshCollider mc = obj.AddComponent<MeshCollider>();
                    mc.sharedMesh = proxyMesh.sharedMesh;
                    mc.convex = false;
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
            }
            
            Debug.Log($"[Obstacle] ✅ Proxies procesados: {processedCount}");
            
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

        #region Merge Nearby Obstacles

        public List<GameObject> MergeNearbyObstacles(List<GameObject> obstacles, float mergeDistance)
        {
            if (!_mergeNearbyObstacles || obstacles.Count < 2)
                return obstacles;
            
            Debug.Log($"[Obstacle] 🔗 Fusionando obstáculos (dist={mergeDistance:F2}m)...");
            
            List<GameObject> merged = new List<GameObject>();
            bool[] processed = new bool[obstacles.Count];
            int fusionCount = 0;
            
            for (int i = 0; i < obstacles.Count; i++)
            {
                if (processed[i]) continue;
                
                GameObject current = obstacles[i];
                Renderer currentRenderer = current.GetComponent<Renderer>();
                if (currentRenderer == null)
                {
                    merged.Add(current);
                    continue;
                }
                
                Bounds combinedBounds = currentRenderer.bounds;
                List<int> clusteredIndices = new List<int> { i };
                processed[i] = true;
                
                for (int j = i + 1; j < obstacles.Count; j++)
                {
                    if (processed[j]) continue;
                    
                    GameObject other = obstacles[j];
                    Renderer otherRenderer = other.GetComponent<Renderer>();
                    if (otherRenderer == null) continue;
                    
                    float distance = Vector3.Distance(combinedBounds.center, otherRenderer.bounds.center);
                    float combinedSize = (combinedBounds.size.magnitude + otherRenderer.bounds.size.magnitude) / 2f;
                    
                    if (distance < combinedSize + mergeDistance)
                    {
                        combinedBounds.Encapsulate(otherRenderer.bounds);
                        clusteredIndices.Add(j);
                        processed[j] = true;
                    }
                }
                
                if (clusteredIndices.Count > 1)
                {
                    GameObject mergedObstacle = CreateMergedObstacle(combinedBounds, fusionCount);
                    merged.Add(mergedObstacle);
                    fusionCount++;
                    
                    foreach (int idx in clusteredIndices)
                    {
                        if (obstacles[idx] != null)
                        {
                            if (Application.isPlaying)
                                UnityEngine.Object.Destroy(obstacles[idx]);
                            else
                                UnityEngine.Object.DestroyImmediate(obstacles[idx]);
                        }
                    }
                }
                else
                {
                    merged.Add(current);
                }
            }
            
            Debug.Log($"[Obstacle] ✅ Fusión: {obstacles.Count} → {merged.Count} ({fusionCount} fusiones)");
            
            return merged;
        }

        private GameObject CreateMergedObstacle(Bounds bounds, int index)
        {
            GameObject mergedObj = new GameObject($"MergedObstacle_{index}");
            mergedObj.transform.position = bounds.center;
            mergedObj.layer = _navMeshLayer;
            mergedObj.isStatic = true;
            
            Vector3 minPadding = new Vector3(0.01f, _obstacleHeightPadding, 0.01f);
            bounds.Expand(minPadding * 2f);
            
            MeshFilter mf = mergedObj.AddComponent<MeshFilter>();
            mf.mesh = CreateBoxMesh(bounds.size);
            
            MeshCollider mc = mergedObj.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.mesh;
            mc.convex = false;
            
            NavMeshModifier modifier = mergedObj.AddComponent<NavMeshModifier>();
            modifier.overrideArea = true;
            modifier.area = _navMeshAreaNotWalkable;
            modifier.ignoreFromBuild = false;
            
            if (_showDebugVisualization)
            {
                MeshRenderer mr = mergedObj.AddComponent<MeshRenderer>();
                Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.color = new Color(0.8f, 0f, 0.8f, 0.3f);
                mr.material = mat;
            }
            
            return mergedObj;
        }

        #endregion

        #region Utilities

        private Bounds CalculateBounds(List<Vector3> vertices)
        {
            if (vertices.Count == 0) return new Bounds();
            
            Vector3 min = vertices[0];
            Vector3 max = vertices[0];
            
            foreach (Vector3 v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            
            return new Bounds((min + max) / 2f, max - min);
        }

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

        #region Data Structures
        
        private class GapAnalysisResult
        {
            public float NearestGapDistance;
            public bool HasGapInFront;
            public bool HasGapBehind;
            public bool HasGapLeft;
            public bool HasGapRight;
        }

        #endregion
    }

    #region Public Data Structures

    public class WallObstacleResult
    {
        public List<GameObject> Obstacles;
        public int ValidWalls;
        public int Openings;
        public int ExcludedByZone;
        public int SegmentedWalls;
    }

    public class ProxyObstacleResult
    {
        public List<GameObject> Obstacles;
        public int ProcessedCount;
    }

    #endregion
}