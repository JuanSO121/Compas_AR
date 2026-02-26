// File: MeshProcessingService.cs
// ✅ VERSIÓN CORREGIDA - Filtrado vertical estricto por nivel

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// ✅ CORREGIDO: Filtrado vertical estricto para evitar proyección entre niveles
    /// </summary>
    public class MeshProcessingService
    {
        private readonly float _heightBinSize;
        private readonly float _minClusterDensity;
        private readonly float _maxFloorDeviation;
        private readonly float _levelSeparationHeight;
        
        private readonly float _wallAngleMin;
        private readonly float _wallAngleMax;
        private readonly float _minWallHeight;
        private readonly float _minWallArea;
        private readonly float _planeNormalTolerance;
        private readonly float _planeDistanceTolerance;
        
        private readonly float _furnitureVoxelSize;
        private readonly float _minFurnitureDensity;
        private readonly float _minFurnitureHeight;
        private readonly float _maxFurnitureHeight;
        private readonly float _minFurnitureArea;
        private readonly float _minFurnitureVolume;
        
        private readonly bool _logDetailedAnalysis;

        public MeshProcessingService(
            float heightBinSize,
            float minClusterDensity,
            float maxFloorDeviation,
            float levelSeparationHeight,
            float wallAngleMin,
            float wallAngleMax,
            float minWallHeight,
            float minWallArea,
            float planeNormalTolerance,
            float planeDistanceTolerance,
            float furnitureVoxelSize,
            float minFurnitureDensity,
            float minFurnitureHeight,
            float maxFurnitureHeight,
            float minFurnitureArea,
            float minFurnitureVolume,
            bool logDetailedAnalysis)
        {
            _heightBinSize = heightBinSize;
            _minClusterDensity = minClusterDensity;
            _maxFloorDeviation = maxFloorDeviation;
            _levelSeparationHeight = levelSeparationHeight;
            
            _wallAngleMin = wallAngleMin;
            _wallAngleMax = wallAngleMax;
            _minWallHeight = minWallHeight;
            _minWallArea = minWallArea;
            _planeNormalTolerance = planeNormalTolerance;
            _planeDistanceTolerance = planeDistanceTolerance;
            
            _furnitureVoxelSize = furnitureVoxelSize;
            _minFurnitureDensity = minFurnitureDensity;
            _minFurnitureHeight = minFurnitureHeight;
            _maxFurnitureHeight = maxFurnitureHeight;
            _minFurnitureArea = minFurnitureArea;
            _minFurnitureVolume = minFurnitureVolume;
            
            _logDetailedAnalysis = logDetailedAnalysis;
        }

        #region Primary Mesh Selection

        public MeshFilter FindPrimaryMesh(MeshFilter[] meshFilters)
        {
            return meshFilters
                .OrderByDescending(mf => mf.sharedMesh != null ? mf.sharedMesh.vertexCount : 0)
                .FirstOrDefault();
        }

        #endregion

        #region Geometry Analysis

        public GeometryAnalysisResult AnalyzeScannedGeometry(MeshFilter primaryMesh)
        {
            if (primaryMesh == null || primaryMesh.sharedMesh == null)
                return null;

            Mesh mesh = primaryMesh.sharedMesh;
            Transform meshTransform = primaryMesh.transform;
            
            List<Vector3> worldVertices = new List<Vector3>(mesh.vertices.Length);
            foreach (Vector3 v in mesh.vertices)
                worldVertices.Add(meshTransform.TransformPoint(v));
            
            if (worldVertices.Count == 0) return null;
            
            var heightClusters = BuildHeightHistogram(worldVertices);
            
            if (_logDetailedAnalysis)
                LogHistogramDetails(heightClusters);
            
            Debug.Log($"[NavAR] ✅ Análisis: {heightClusters.Count} clusters de altura");
            
            return new GeometryAnalysisResult
            {
                HeightClusters = heightClusters,
                MeshVertexCount = mesh.vertices.Length,
                MeshTriangleCount = mesh.triangles.Length / 3
            };
        }

        private List<HeightCluster> BuildHeightHistogram(List<Vector3> vertices)
        {
            float minY = float.MaxValue;
            float maxY = float.MinValue;
            
            foreach (var v in vertices)
            {
                if (v.y < minY) minY = v.y;
                if (v.y > maxY) maxY = v.y;
            }
            
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

        #region ✅ ULTRA-ROBUSTO: Multi-Level Detection

        public List<NavigableLevel> DetectNavigableLevels(List<HeightCluster> heightClusters)
        {
            if (heightClusters == null || heightClusters.Count == 0)
            {
                Debug.LogWarning("[NavAR] ⚠️ No hay clusters de altura");
                return new List<NavigableLevel>();
            }

            Debug.Log($"[NavAR] 🔍 INICIANDO DETECCIÓN MULTI-NIVEL de {heightClusters.Count} clusters...");

            // ✅ PASO 0: Verificar si hay NavigationStartPoints definidos
            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            bool useStartPointHeights = startPoints.Count > 0;
            
            if (useStartPointHeights)
            {
                Debug.Log($"[NavAR] 🎯 Usando alturas de {startPoints.Count} NavigationStartPoints");
                return DetectLevelsFromStartPoints(startPoints, heightClusters);
            }

            // FALLBACK: Detección automática (método anterior)
            Debug.Log($"[NavAR] 🔍 No hay NavigationStartPoints. Usando detección automática...");
            
            float absoluteMinY = heightClusters.Min(c => c.minHeight);
            float absoluteMaxY = heightClusters.Max(c => c.maxHeight);
            float totalRange = absoluteMaxY - absoluteMinY;

            Debug.Log($"[NavAR] 📏 Rango vertical total: [{absoluteMinY:F2}, {absoluteMaxY:F2}]m = {totalRange:F2}m");

            List<NavigableLevel> levels = new List<NavigableLevel>();

            // MÉTODO 1: Primer piso FORZADO
            var groundLevel = CreateGroundFloorLevel(heightClusters, absoluteMinY);
            if (groundLevel != null)
            {
                levels.Add(groundLevel);
                Debug.Log($"[NavAR] ✅ PRIMER PISO: Y={groundLevel.FloorHeight:F2}m");
            }

            // MÉTODO 2: Niveles adicionales por separación
            var additionalLevels = DetectAdditionalLevelsBySeparation(heightClusters, absoluteMinY, totalRange);
            levels.AddRange(additionalLevels);

            // MÉTODO 3: Forzar segundo nivel si hay altura suficiente
            if (levels.Count == 1 && totalRange > _levelSeparationHeight * 1.5f)
            {
                var upperLevel = CreateUpperFloorLevel(heightClusters, absoluteMinY, totalRange);
                if (upperLevel != null)
                {
                    levels.Add(upperLevel);
                    Debug.Log($"[NavAR] ✅ SEGUNDO PISO FORZADO: Y={upperLevel.FloorHeight:F2}m");
                }
            }

            levels = levels.OrderBy(l => l.FloorHeight).ToList();

            for (int i = 0; i < levels.Count; i++)
            {
                levels[i].LevelIndex = i;
                Debug.Log($"[NavAR] 🏢 NIVEL {i}: Y={levels[i].FloorHeight:F2}m, " +
                         $"rango=[{levels[i].MinY:F2}, {levels[i].MaxY:F2}]m");
            }

            return levels;
        }

        /// <summary>
        /// ✅ NUEVO: Detecta niveles usando las alturas exactas de NavigationStartPoints
        /// </summary>
        private List<NavigableLevel> DetectLevelsFromStartPoints(
            List<NavigationStartPoint> startPoints, 
            List<HeightCluster> heightClusters)
        {
            List<NavigableLevel> levels = new List<NavigableLevel>();
            
            foreach (var startPoint in startPoints)
            {
                if (startPoint == null || !startPoint.DefinesFloorHeight)
                    continue;
                
                float floorHeight = startPoint.FloorHeight;
                int levelIndex = startPoint.Level;
                
                Debug.Log($"[NavAR] 🎯 Creando nivel {levelIndex} en altura exacta Y={floorHeight:F2}m");
                
                // Buscar clusters cerca de esta altura
                float searchRange = _maxFloorDeviation * 3f; // Búsqueda más amplia
                
                var nearbyClusters = heightClusters
                    .Where(c => Mathf.Abs(c.centerHeight - floorHeight) <= searchRange)
                    .ToList();
                
                if (nearbyClusters.Count == 0)
                {
                    // Si no hay clusters cerca, usar todos los vertices del modelo
                    Debug.LogWarning($"[NavAR] ⚠️ No hay clusters cerca de Y={floorHeight:F2}m. Usando todos los vértices.");
                    nearbyClusters = heightClusters;
                }
                
                // Fusionar clusters
                var fusedClusters = FuseNearbyClusters(nearbyClusters, _heightBinSize * 10f);
                
                // Tomar el más cercano a la altura del startPoint
                var mainCluster = fusedClusters
                    .OrderBy(c => Mathf.Abs(c.centerHeight - floorHeight))
                    .FirstOrDefault();
                
                if (mainCluster == null)
                {
                    Debug.LogWarning($"[NavAR] ⚠️ No se pudo crear nivel {levelIndex}");
                    continue;
                }
                
                List<Vector3> levelVertices = mainCluster.vertices;
                Bounds levelBounds = CalculateBounds(levelVertices);
                
                // ✅ USAR LA ALTURA EXACTA DEL START POINT
                float minY = floorHeight - _maxFloorDeviation;
                float maxY = floorHeight + _levelSeparationHeight;
                
                var level = new NavigableLevel
                {
                    LevelIndex = levelIndex,
                    FloorHeight = floorHeight, // ✅ ALTURA EXACTA
                    MinY = minY,
                    MaxY = maxY,
                    HorizontalBounds = new Bounds(
                        new Vector3(levelBounds.center.x, floorHeight, levelBounds.center.z),
                        new Vector3(levelBounds.size.x, maxY - minY, levelBounds.size.z)
                    ),
                    Vertices = levelVertices,
                    Clusters = new List<HeightCluster> { mainCluster }
                };
                
                levels.Add(level);
                
                Debug.Log($"[NavAR] ✅ Nivel {levelIndex} creado: Y={floorHeight:F2}m, " +
                         $"rango=[{minY:F2}, {maxY:F2}]m, verts={levelVertices.Count}");
            }
            
            // Ordenar por altura
            levels = levels.OrderBy(l => l.FloorHeight).ToList();
            
            // Reasignar índices por si están desordenados
            for (int i = 0; i < levels.Count; i++)
            {
                levels[i].LevelIndex = i;
            }
            
            return levels;
        }

        private NavigableLevel CreateGroundFloorLevel(List<HeightCluster> heightClusters, float absoluteMinY)
        {
            float searchRange = absoluteMinY + 1.0f;

            var groundClusters = heightClusters
                .Where(c => c.centerHeight <= searchRange)
                .OrderBy(c => c.centerHeight)
                .ToList();

            if (groundClusters.Count == 0)
            {
                groundClusters.Add(heightClusters.OrderBy(c => c.centerHeight).First());
            }

            var fusedGround = FuseNearbyClusters(groundClusters, _heightBinSize * 5f);
            var groundCluster = fusedGround.OrderBy(c => c.centerHeight).First();

            List<Vector3> levelVertices = groundCluster.vertices;
            Bounds levelBounds = CalculateBounds(levelVertices);

            float floorHeight = groundCluster.centerHeight;
            float minY = floorHeight - _maxFloorDeviation * 2f;
            float maxY = floorHeight + _levelSeparationHeight;

            return new NavigableLevel
            {
                LevelIndex = 0,
                FloorHeight = floorHeight,
                MinY = minY,
                MaxY = maxY,
                HorizontalBounds = new Bounds(
                    new Vector3(levelBounds.center.x, floorHeight, levelBounds.center.z),
                    new Vector3(levelBounds.size.x, maxY - minY, levelBounds.size.z)
                ),
                Vertices = levelVertices,
                Clusters = new List<HeightCluster> { groundCluster }
            };
        }

        private List<NavigableLevel> DetectAdditionalLevelsBySeparation(
            List<HeightCluster> heightClusters, 
            float absoluteMinY, 
            float totalRange)
        {
            List<NavigableLevel> additionalLevels = new List<NavigableLevel>();

            var sortedClusters = heightClusters.OrderBy(c => c.centerHeight).ToList();

            float lastHeight = absoluteMinY;
            List<HeightCluster> currentGroup = new List<HeightCluster>();

            for (int i = 0; i < sortedClusters.Count; i++)
            {
                var cluster = sortedClusters[i];

                if (cluster.centerHeight < absoluteMinY + 1.0f)
                    continue;

                float gap = cluster.centerHeight - lastHeight;

                if (gap > _levelSeparationHeight * 0.8f)
                {
                    if (currentGroup.Count > 0)
                    {
                        var level = CreateLevelFromClusters(currentGroup, additionalLevels.Count + 1);
                        if (level != null)
                        {
                            additionalLevels.Add(level);
                            Debug.Log($"[NavAR] 🆕 Nivel adicional: Y={level.FloorHeight:F2}m (gap={gap:F2}m)");
                        }
                        currentGroup.Clear();
                    }
                }

                currentGroup.Add(cluster);
                lastHeight = cluster.centerHeight;
            }

            if (currentGroup.Count > 0)
            {
                var level = CreateLevelFromClusters(currentGroup, additionalLevels.Count + 1);
                if (level != null)
                {
                    additionalLevels.Add(level);
                }
            }

            return additionalLevels;
        }

        private NavigableLevel CreateUpperFloorLevel(
            List<HeightCluster> heightClusters, 
            float absoluteMinY, 
            float totalRange)
        {
            float midPoint = absoluteMinY + (totalRange / 2f);

            var upperClusters = heightClusters
                .Where(c => c.centerHeight >= midPoint)
                .OrderBy(c => c.centerHeight)
                .ToList();

            if (upperClusters.Count == 0)
                return null;

            var fusedUpper = FuseNearbyClusters(upperClusters, _heightBinSize * 5f);

            var upperCluster = fusedUpper
                .OrderByDescending(c => c.bounds.size.x * c.bounds.size.z)
                .First();

            List<Vector3> levelVertices = upperCluster.vertices;
            Bounds levelBounds = CalculateBounds(levelVertices);

            float floorHeight = upperCluster.centerHeight;
            float minY = floorHeight - _maxFloorDeviation * 2f;
            float maxY = floorHeight + 3.0f;

            return new NavigableLevel
            {
                LevelIndex = 1,
                FloorHeight = floorHeight,
                MinY = minY,
                MaxY = maxY,
                HorizontalBounds = new Bounds(
                    new Vector3(levelBounds.center.x, floorHeight, levelBounds.center.z),
                    new Vector3(levelBounds.size.x, maxY - minY, levelBounds.size.z)
                ),
                Vertices = levelVertices,
                Clusters = new List<HeightCluster> { upperCluster }
            };
        }

        private NavigableLevel CreateLevelFromClusters(List<HeightCluster> clusters, int levelIndex)
        {
            if (clusters.Count == 0) return null;

            var fusedClusters = FuseNearbyClusters(clusters, _heightBinSize * 3f);
            var mainCluster = fusedClusters.OrderByDescending(c => c.bounds.size.x * c.bounds.size.z).First();

            List<Vector3> levelVertices = mainCluster.vertices;
            Bounds levelBounds = CalculateBounds(levelVertices);

            float floorHeight = mainCluster.centerHeight;
            float minY = floorHeight - _maxFloorDeviation * 2f;
            float maxY = floorHeight + _levelSeparationHeight;

            return new NavigableLevel
            {
                LevelIndex = levelIndex,
                FloorHeight = floorHeight,
                MinY = minY,
                MaxY = maxY,
                HorizontalBounds = new Bounds(
                    new Vector3(levelBounds.center.x, floorHeight, levelBounds.center.z),
                    new Vector3(levelBounds.size.x, maxY - minY, levelBounds.size.z)
                ),
                Vertices = levelVertices,
                Clusters = new List<HeightCluster> { mainCluster }
            };
        }

        private List<HeightCluster> FuseNearbyClusters(List<HeightCluster> clusters, float maxGap)
        {
            if (clusters.Count <= 1) return new List<HeightCluster>(clusters);

            var sorted = clusters.OrderBy(c => c.minHeight).ToList();
            List<HeightCluster> fused = new List<HeightCluster>();
            HeightCluster current = sorted[0];

            for (int i = 1; i < sorted.Count; i++)
            {
                HeightCluster next = sorted[i];
                float gap = next.minHeight - current.maxHeight;

                if (gap <= maxGap)
                {
                    current.vertices.AddRange(next.vertices);
                    current.maxHeight = next.maxHeight;
                    current.centerHeight = (current.minHeight + current.maxHeight) / 2f;
                    current.vertexCount += next.vertexCount;
                    current.density = (current.density + next.density) / 2f;
                    current.bounds.Encapsulate(next.bounds);
                }
                else
                {
                    fused.Add(current);
                    current = next;
                }
            }

            fused.Add(current);
            return fused;
        }

        #endregion

        #region Wall Detection

        public List<WallPlane> DetectWallPlanesForLevel(
            MeshFilter primaryMesh, 
            NavigableLevel level)
        {
            Debug.Log($"[NavAR] 🧱 Detectando paredes para nivel {level.LevelIndex} (Y=[{level.MinY:F2}, {level.MaxY:F2}]m)...");
            
            if (primaryMesh == null || primaryMesh.sharedMesh == null)
                return new List<WallPlane>();
            
            Mesh mesh = primaryMesh.sharedMesh;
            Transform meshTransform = primaryMesh.transform;
            
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            Vector3[] normals = mesh.normals;
            
            if (normals == null || normals.Length == 0)
            {
                mesh.RecalculateNormals();
                normals = mesh.normals;
            }
            
            List<WallTriangle> verticalTriangles = new List<WallTriangle>();
            
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i];
                int i1 = triangles[i + 1];
                int i2 = triangles[i + 2];
                
                Vector3 v0 = meshTransform.TransformPoint(vertices[i0]);
                Vector3 v1 = meshTransform.TransformPoint(vertices[i1]);
                Vector3 v2 = meshTransform.TransformPoint(vertices[i2]);
                
                float triMinY = Mathf.Min(v0.y, v1.y, v2.y);
                float triMaxY = Mathf.Max(v0.y, v1.y, v2.y);
                float triCenterY = (v0.y + v1.y + v2.y) / 3f;
                
                // ✅ FILTRADO VERTICAL ESTRICTO
                if (triMaxY < level.MinY || triMinY > level.MaxY)
                    continue;
                
                if (triCenterY < level.MinY || triCenterY > level.MaxY)
                    continue;
                
                Vector3 n0 = meshTransform.TransformDirection(normals[i0]);
                Vector3 n1 = meshTransform.TransformDirection(normals[i1]);
                Vector3 n2 = meshTransform.TransformDirection(normals[i2]);
                Vector3 triNormal = (n0 + n1 + n2).normalized;
                
                float angleWithVertical = Vector3.Angle(triNormal, Vector3.up);
                if (angleWithVertical < _wallAngleMin || angleWithVertical > _wallAngleMax)
                    continue;
                
                float triHeight = triMaxY - triMinY;
                
                if (triHeight < _minWallHeight * 0.3f)
                    continue;
                
                Vector3 side1 = v1 - v0;
                Vector3 side2 = v2 - v0;
                float area = Vector3.Cross(side1, side2).magnitude * 0.5f;
                
                if (area < 0.01f)
                    continue;
                
                verticalTriangles.Add(new WallTriangle
                {
                    v0 = v0, v1 = v1, v2 = v2,
                    normal = triNormal,
                    center = (v0 + v1 + v2) / 3f,
                    minY = triMinY, maxY = triMaxY,
                    area = area,
                    levelIndex = level.LevelIndex
                });
            }
            
            var wallPlanes = ClusterTrianglesByPlane(verticalTriangles);
            
            wallPlanes = wallPlanes
                .Where(plane => plane.totalArea >= _minWallArea)
                .Where(plane => plane.triangles.Count >= 3)
                .ToList();
            
            Debug.Log($"[NavAR] ✅ Paredes nivel {level.LevelIndex}: {wallPlanes.Count} detectadas");
            
            return wallPlanes;
        }

        private List<WallPlane> ClusterTrianglesByPlane(List<WallTriangle> triangles)
        {
            List<WallPlane> planes = new List<WallPlane>();
            
            triangles = triangles.OrderBy(t => t.center.x)
                                .ThenBy(t => t.center.z)
                                .ToList();
            
            bool[] assigned = new bool[triangles.Count];
            
            for (int i = 0; i < triangles.Count; i++)
            {
                if (assigned[i]) continue;
                
                WallTriangle seed = triangles[i];
                List<WallTriangle> planeTriangles = new List<WallTriangle> { seed };
                assigned[i] = true;
                
                Queue<int> toExpand = new Queue<int>();
                toExpand.Enqueue(i);
                
                while (toExpand.Count > 0)
                {
                    int currentIdx = toExpand.Dequeue();
                    WallTriangle current = triangles[currentIdx];
                    
                    for (int j = i + 1; j < triangles.Count; j++)
                    {
                        if (assigned[j]) continue;
                        
                        WallTriangle candidate = triangles[j];
                        
                        if (Mathf.Abs(candidate.center.x - current.center.x) > _planeDistanceTolerance * 3f)
                            break;
                        
                        if (AreTrianglesOnSamePlane(current, candidate))
                        {
                            planeTriangles.Add(candidate);
                            assigned[j] = true;
                            toExpand.Enqueue(j);
                        }
                    }
                }
                
                if (planeTriangles.Count >= 2)
                {
                    WallPlane plane = CreateWallPlaneFromTriangles(planeTriangles);
                    planes.Add(plane);
                }
            }
            
            return planes;
        }

        private bool AreTrianglesOnSamePlane(WallTriangle t1, WallTriangle t2)
        {
            float normalDot = Vector3.Dot(t1.normal, t2.normal);
            if (Mathf.Abs(normalDot) < (1f - _planeNormalTolerance))
                return false;
            
            float distance = Vector3.Distance(t1.center, t2.center);
            if (distance > _planeDistanceTolerance * 2f)
                return false;
            
            Vector3 toOther = t2.center - t1.center;
            float distanceToPlane = Mathf.Abs(Vector3.Dot(toOther, t1.normal));
            
            return distanceToPlane < _planeDistanceTolerance;
        }

        private WallPlane CreateWallPlaneFromTriangles(List<WallTriangle> triangles)
        {
            Vector3 avgNormal = Vector3.zero;
            foreach (var tri in triangles)
                avgNormal += tri.normal;
            avgNormal = (avgNormal / triangles.Count).normalized;
            
            float totalArea = 0f;
            foreach (var tri in triangles)
                totalArea += tri.area;
            
            List<Vector3> allVertices = new List<Vector3>(triangles.Count * 3);
            foreach (var tri in triangles)
            {
                allVertices.Add(tri.v0);
                allVertices.Add(tri.v1);
                allVertices.Add(tri.v2);
            }
            
            Bounds bounds = CalculateBounds(allVertices);
            
            return new WallPlane
            {
                planeNormal = avgNormal,
                bounds = bounds,
                triangles = triangles,
                totalArea = totalArea,
                levelIndex = triangles[0].levelIndex
            };
        }

        #endregion

        #region Furniture Detection

        public List<FurnitureCluster> DetectFurnitureForLevel(
            MeshFilter primaryMesh, 
            NavigableLevel level)
        {
            Debug.Log($"[NavAR] 🪑 Detectando muebles para nivel {level.LevelIndex}...");
            
            if (primaryMesh == null || primaryMesh.sharedMesh == null)
                return new List<FurnitureCluster>();
            
            Mesh mesh = primaryMesh.sharedMesh;
            Transform meshTransform = primaryMesh.transform;
            
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            Vector3[] normals = mesh.normals;
            
            if (normals == null || normals.Length == 0)
            {
                mesh.RecalculateNormals();
                normals = mesh.normals;
            }
            
            List<FurnitureTriangle> candidateTriangles = new List<FurnitureTriangle>();
            
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i];
                int i1 = triangles[i + 1];
                int i2 = triangles[i + 2];
                
                Vector3 v0 = meshTransform.TransformPoint(vertices[i0]);
                Vector3 v1 = meshTransform.TransformPoint(vertices[i1]);
                Vector3 v2 = meshTransform.TransformPoint(vertices[i2]);
                
                Vector3 center = (v0 + v1 + v2) / 3f;
                
                // ✅ FILTRADO VERTICAL ESTRICTO
                if (center.y < level.MinY || center.y > level.MaxY)
                    continue;
                
                float heightAboveFloor = center.y - level.FloorHeight;
                
                if (heightAboveFloor < _minFurnitureHeight * 0.5f || 
                    heightAboveFloor > _maxFurnitureHeight)
                    continue;
                
                Vector3 n0 = meshTransform.TransformDirection(normals[i0]);
                Vector3 n1 = meshTransform.TransformDirection(normals[i1]);
                Vector3 n2 = meshTransform.TransformDirection(normals[i2]);
                Vector3 triNormal = (n0 + n1 + n2).normalized;
                
                float angleWithVertical = Vector3.Angle(triNormal, Vector3.up);
                
                if (angleWithVertical < 30f || angleWithVertical > 150f)
                    continue;
                
                candidateTriangles.Add(new FurnitureTriangle
                {
                    v0 = v0, v1 = v1, v2 = v2,
                    center = center,
                    normal = triNormal
                });
            }
            
            Debug.Log($"[NavAR] 📐 Triángulos candidatos (nivel {level.LevelIndex}): {candidateTriangles.Count}");
            
            if (candidateTriangles.Count == 0)
                return new List<FurnitureCluster>();
            
            var voxelGrid = VoxelizeTriangles(candidateTriangles);
            Debug.Log($"[NavAR] 🧊 Voxels ocupados: {voxelGrid.Count}");
            
            var furnitureClusters = ClusterVoxels3D(voxelGrid);
            
            furnitureClusters = furnitureClusters
                .Where(f => f.bounds.size.y >= _minFurnitureHeight)
                .Where(f => f.bounds.size.y <= _maxFurnitureHeight)
                .Where(f => (f.bounds.size.x * f.bounds.size.z) >= _minFurnitureArea)
                .Where(f => f.volume >= _minFurnitureVolume)
                .Where(f => f.density >= _minFurnitureDensity)
                .ToList();
            
            Debug.Log($"[NavAR] ✅ Muebles nivel {level.LevelIndex}: {furnitureClusters.Count}");
            
            return furnitureClusters;
        }

        private Dictionary<Vector3Int, List<FurnitureTriangle>> VoxelizeTriangles(List<FurnitureTriangle> triangles)
        {
            Dictionary<Vector3Int, List<FurnitureTriangle>> grid = new Dictionary<Vector3Int, List<FurnitureTriangle>>();
            
            foreach (var tri in triangles)
            {
                Vector3Int voxelPos = new Vector3Int(
                    Mathf.FloorToInt(tri.center.x / _furnitureVoxelSize),
                    Mathf.FloorToInt(tri.center.y / _furnitureVoxelSize),
                    Mathf.FloorToInt(tri.center.z / _furnitureVoxelSize)
                );
                
                if (!grid.ContainsKey(voxelPos))
                {
                    grid[voxelPos] = new List<FurnitureTriangle>();
                }
                
                grid[voxelPos].Add(tri);
            }
            
            return grid;
        }

        private List<FurnitureCluster> ClusterVoxels3D(Dictionary<Vector3Int, List<FurnitureTriangle>> voxelGrid)
        {
            HashSet<Vector3Int> visited = new HashSet<Vector3Int>();
            List<FurnitureCluster> clusters = new List<FurnitureCluster>();
            
            Vector3Int[] neighbors3D = new Vector3Int[]
            {
                Vector3Int.up, Vector3Int.down,
                Vector3Int.forward, Vector3Int.back,
                Vector3Int.left, Vector3Int.right
            };
            
            foreach (var kvp in voxelGrid)
            {
                Vector3Int start = kvp.Key;
                
                if (visited.Contains(start))
                    continue;
                
                List<FurnitureTriangle> clusterTriangles = new List<FurnitureTriangle>();
                Queue<Vector3Int> toExplore = new Queue<Vector3Int>();
                toExplore.Enqueue(start);
                visited.Add(start);
                
                int voxelCount = 0;
                
                while (toExplore.Count > 0)
                {
                    Vector3Int current = toExplore.Dequeue();
                    voxelCount++;
                    
                    if (voxelGrid.TryGetValue(current, out List<FurnitureTriangle> tris))
                    {
                        clusterTriangles.AddRange(tris);
                    }
                    
                    foreach (var dir in neighbors3D)
                    {
                        Vector3Int neighbor = current + dir;
                        
                        if (!visited.Contains(neighbor) && voxelGrid.ContainsKey(neighbor))
                        {
                            visited.Add(neighbor);
                            toExplore.Enqueue(neighbor);
                        }
                    }
                }
                
                if (clusterTriangles.Count > 0)
                {
                    List<Vector3> allVerts = new List<Vector3>();
                    foreach (var tri in clusterTriangles)
                    {
                        allVerts.Add(tri.v0);
                        allVerts.Add(tri.v1);
                        allVerts.Add(tri.v2);
                    }
                    
                    Bounds bounds = CalculateBounds(allVerts);
                    float volume = bounds.size.x * bounds.size.y * bounds.size.z;
                    float voxelVolume = voxelCount * Mathf.Pow(_furnitureVoxelSize, 3);
                    float density = volume > 0 ? voxelVolume / volume : 0f;
                    
                    clusters.Add(new FurnitureCluster
                    {
                        triangles = clusterTriangles,
                        bounds = bounds,
                        volume = volume,
                        density = density,
                        voxelCount = voxelCount
                    });
                }
            }
            
            return clusters;
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

        private void LogHistogramDetails(List<HeightCluster> clusters)
        {
            Debug.Log("=== HISTOGRAMA DE ALTURAS ===");
            foreach (var c in clusters.Take(20))
            {
                float pct = c.density * 100f;
                string bar = new string('█', Mathf.RoundToInt(pct / 2f));
                Debug.Log($"Y={c.centerHeight:F2}m ({pct:F1}%) {bar} verts={c.vertexCount}");
            }
        }

        #endregion
    }

    #region Data Structures

    [Serializable]
    public class HeightCluster
    {
        public int binIndex;
        public float minHeight, maxHeight, centerHeight;
        public int vertexCount;
        public float density;
        public List<Vector3> vertices;
        public Bounds bounds;
    }

    [Serializable]
    public class NavigableLevel
    {
        public int LevelIndex;
        public float FloorHeight;
        public float MinY;
        public float MaxY;
        public Bounds HorizontalBounds;
        public List<Vector3> Vertices;
        public List<HeightCluster> Clusters;
    }

    public struct WallTriangle
    {
        public Vector3 v0, v1, v2;
        public Vector3 normal;
        public Vector3 center;
        public float minY, maxY;
        public float area;
        public int levelIndex;
    }

    public class WallPlane
    {
        public Vector3 planeNormal;
        public Bounds bounds;
        public List<WallTriangle> triangles;
        public float totalArea;
        public int levelIndex;
    }

    public struct FurnitureTriangle
    {
        public Vector3 v0, v1, v2;
        public Vector3 center;
        public Vector3 normal;
    }

    public class FurnitureCluster
    {
        public List<FurnitureTriangle> triangles;
        public Bounds bounds;
        public float volume;
        public float density;
        public int voxelCount;
    }

    public class GeometryAnalysisResult
    {
        public List<HeightCluster> HeightClusters;
        public int MeshVertexCount;
        public int MeshTriangleCount;
    }

    #endregion
}