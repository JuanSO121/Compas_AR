// File: MeshProcessingService.cs

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Servicio de procesamiento de geometría: análisis de meshes, detección de pisos, paredes y muebles
    /// </summary>
    public class MeshProcessingService
    {
        private readonly float _heightBinSize;
        private readonly float _minClusterDensity;
        private readonly float _maxFloorDeviation;
        
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

        #region Wall Detection

        public List<WallPlane> DetectWallPlanesRobust(MeshFilter primaryMesh, float floorHeight)
        {
            Debug.Log("[NavAR] 🧱 Detectando paredes (método robusto)...");
            
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
                
                Vector3 n0 = meshTransform.TransformDirection(normals[i0]);
                Vector3 n1 = meshTransform.TransformDirection(normals[i1]);
                Vector3 n2 = meshTransform.TransformDirection(normals[i2]);
                Vector3 triNormal = (n0 + n1 + n2).normalized;
                
                float angleWithVertical = Vector3.Angle(triNormal, Vector3.up);
                if (angleWithVertical < _wallAngleMin || angleWithVertical > _wallAngleMax)
                    continue;
                
                float minY = Mathf.Min(v0.y, v1.y, v2.y);
                float maxY = Mathf.Max(v0.y, v1.y, v2.y);
                float triHeight = maxY - minY;
                
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
                    minY = minY, maxY = maxY,
                    area = area
                });
            }
            
            var wallPlanes = ClusterTrianglesByPlane(verticalTriangles);
            
            wallPlanes = wallPlanes
                .Where(plane => plane.totalArea >= _minWallArea)
                .Where(plane => plane.triangles.Count >= 3)
                .ToList();
            
            Debug.Log($"[NavAR] ✅ Paredes detectadas: {wallPlanes.Count}");
            
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
                totalArea = totalArea
            };
        }

        #endregion

        #region Furniture Detection

        public List<FurnitureCluster> DetectFurnitureVolumetric(MeshFilter primaryMesh, float floorHeight)
        {
            Debug.Log("[NavAR] 🪑 Detectando muebles (método volumétrico)...");
            
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
                float heightAboveFloor = center.y - floorHeight;
                
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
            
            Debug.Log($"[NavAR] 📐 Triángulos candidatos: {candidateTriangles.Count}");
            
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
            
            Debug.Log($"[NavAR] ✅ Muebles detectados: {furnitureClusters.Count}");
            
            if (_logDetailedAnalysis)
            {
                foreach (var furniture in furnitureClusters)
                {
                    Debug.Log($"  🪑 Mueble: pos={furniture.bounds.center}, size={furniture.bounds.size}, vol={furniture.volume:F3}m³, dens={furniture.density:F2}");
                }
            }
            
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
                    float density = voxelVolume / volume;
                    
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
            foreach (var c in clusters.Take(15))
            {
                float pct = c.density * 100f;
                string bar = new string('█', Mathf.RoundToInt(pct / 2f));
                Debug.Log($"Y={c.centerHeight:F2}m ({pct:F1}%) {bar}");
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

    public struct WallTriangle
    {
        public Vector3 v0, v1, v2;
        public Vector3 normal;
        public Vector3 center;
        public float minY, maxY;
        public float area;
    }

    public class WallPlane
    {
        public Vector3 planeNormal;
        public Bounds bounds;
        public List<WallTriangle> triangles;
        public float totalArea;
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