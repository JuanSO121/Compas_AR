// File: NavMeshSurfaceService.cs

using System;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Servicio de gestión de superficie NavMesh: reconstrucción de plano y baking optimizado
    /// </summary>
    public class NavMeshSurfaceService
    {
        private readonly float _surfacePadding;
        private readonly float _surfaceThickness;
        private readonly float _surfaceOffsetBelow;
        private readonly int _navMeshLayer;
        private readonly float _voxelSize;
        private readonly int _minRegionArea;
        private readonly bool _showDebugVisualization;

        public NavMeshSurfaceService(
            float surfacePadding,
            float surfaceThickness,
            float surfaceOffsetBelow,
            int navMeshLayer,
            float voxelSize,
            int minRegionArea,
            bool showDebugVisualization)
        {
            _surfacePadding = surfacePadding;
            _surfaceThickness = surfaceThickness;
            _surfaceOffsetBelow = surfaceOffsetBelow;
            _navMeshLayer = navMeshLayer;
            _voxelSize = voxelSize;
            _minRegionArea = minRegionArea;
            _showDebugVisualization = showDebugVisualization;
        }

        #region Floor Reconstruction

        public GameObject ReconstructCleanFloorPlane(Bounds walkableArea, float floorHeight, out NavMeshSurface navMeshSurface)
        {
            GameObject walkableSurface = new GameObject("WalkableSurface");
            walkableSurface.layer = _navMeshLayer;
            walkableSurface.isStatic = true;
            
            Vector3 planeCenter = walkableArea.center;
            planeCenter.y = floorHeight - _surfaceOffsetBelow;
            walkableSurface.transform.position = planeCenter;
            
            float width = walkableArea.size.x + (_surfacePadding * 2f);
            float depth = walkableArea.size.z + (_surfacePadding * 2f);
            
            Mesh planeMesh = CreatePlaneMesh(width, depth);
            
            MeshFilter meshFilter = walkableSurface.AddComponent<MeshFilter>();
            meshFilter.mesh = planeMesh;
            
            MeshRenderer meshRenderer = walkableSurface.AddComponent<MeshRenderer>();
            meshRenderer.material = CreateFloorMaterial();
            
            MeshCollider meshCollider = walkableSurface.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = planeMesh;
            meshCollider.convex = false;
            
            navMeshSurface = walkableSurface.AddComponent<NavMeshSurface>();
            ConfigureNavMeshSurface(navMeshSurface, walkableArea);
            
            Debug.Log($"[NavAR] ✅ Plano creado: {width:F2}×{depth:F2}m");
            return walkableSurface;
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

        private void ConfigureNavMeshSurface(NavMeshSurface navMeshSurface, Bounds walkableArea)
        {
            navMeshSurface.agentTypeID = 0;
            navMeshSurface.collectObjects = CollectObjects.Volume;
            navMeshSurface.useGeometry = NavMeshCollectGeometry.RenderMeshes;
            navMeshSurface.layerMask = 1 << _navMeshLayer;
            navMeshSurface.overrideVoxelSize = true;
            navMeshSurface.voxelSize = _voxelSize;
            navMeshSurface.minRegionArea = _minRegionArea;
            navMeshSurface.defaultArea = 0;
            navMeshSurface.overrideTileSize = false;
            
            Vector3 volumeSize = walkableArea.size;
            volumeSize.x += _surfacePadding * 4f;
            volumeSize.z += _surfacePadding * 4f;
            volumeSize.y = Mathf.Max(volumeSize.y * 2f, 3f);
            
            navMeshSurface.size = volumeSize;
            navMeshSurface.center = Vector3.zero;
        }

        #endregion

        #region NavMesh Baking

        public bool BakeNavMeshOptimized(NavMeshSurface navMeshSurface, int navMeshLayer, out NavMeshBakingStats stats)
        {
            stats = new NavMeshBakingStats();
            
            if (navMeshSurface == null)
            {
                Debug.LogError("[NavAR] ❌ NavMeshSurface no inicializado");
                return false;
            }
            
            try
            {
                Debug.Log("[NavAR] 🔥 Baking NavMesh (optimizado para móvil)...");
                
                var bakingStart = Time.realtimeSinceStartup;
                
                Canvas.ForceUpdateCanvases();
                
                #if UNITY_EDITOR
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                #endif
                
                navMeshSurface.BuildNavMesh();
                
                var bakingTime = (Time.realtimeSinceStartup - bakingStart) * 1000f;
                stats.BakingTime = bakingTime;
                
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                
                if (tri.vertices.Length == 0)
                {
                    Debug.LogError("[NavAR] ❌ NavMesh resultante está vacío");
                    LogNavMeshDiagnostics(navMeshSurface, navMeshLayer);
                    return false;
                }
                
                float area = CalculateNavMeshArea(tri);
                stats.VertexCount = tri.vertices.Length;
                stats.TriangleCount = tri.indices.Length / 3;
                stats.Area = area;
                
                Debug.Log($"[NavAR] ✅ NavMesh baked en {bakingTime:F0}ms: {tri.vertices.Length} verts, {area:F2}m²");
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavAR] ❌ Error en baking: {ex.Message}");
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

        private void LogNavMeshDiagnostics(NavMeshSurface navMeshSurface, int navMeshLayer)
        {
            Debug.Log("========== 🔍 DIAGNÓSTICO NAVMESH ==========");
            
            NavMeshModifier[] modifiers = UnityEngine.Object.FindObjectsByType<NavMeshModifier>(FindObjectsSortMode.None);
            Debug.Log($"NavMeshModifiers encontrados: {modifiers.Length}");
            
            int validCount = 0;
            foreach (var mod in modifiers)
            {
                bool hasValidMesh = false;
                bool hasCollider = mod.GetComponent<Collider>() != null;
                bool isStatic = mod.gameObject.isStatic;
                bool correctLayer = mod.gameObject.layer == navMeshLayer;
                
                MeshFilter mf = mod.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount > 0)
                {
                    hasValidMesh = true;
                }
                
                bool isValid = hasValidMesh && hasCollider && isStatic && correctLayer;
                if (isValid) validCount++;
                
                string status = isValid ? "✅" : "❌";
                Debug.Log($"{status} {mod.gameObject.name}: mesh={hasValidMesh}, col={hasCollider}, static={isStatic}, layer={correctLayer}");
            }
            
            Debug.Log($"Modifiers válidos: {validCount}/{modifiers.Length}");
            
            if (navMeshSurface != null)
            {
                Debug.Log($"NavMeshSurface: voxel={navMeshSurface.voxelSize}, minArea={navMeshSurface.minRegionArea}");
                Debug.Log($"  Volume: size={navMeshSurface.size}, center={navMeshSurface.center}");
                Debug.Log($"  Layers: {LayerMask.LayerToName(navMeshLayer)}");
            }
            
            Debug.Log("==========================================");
        }

        #endregion
    }
}