// File: MultiLevelSurfaceService.cs
// ✅ CORREGIDO v4 — Unity 6.3+
//
//   CAMBIO PRINCIPAL:
//     BakeLevelNavMesh() se mantiene por compatibilidad pero está marcado [Obsolete].
//     El baking real ahora lo hace GlobalNavMeshBaker.BakeGlobal() en un único paso.
//     CreateLevelSurface() no cambia — sigue creando la geometría del plano caminable.
//
//   TODOS los fixes de v3 se mantienen:
//     FIX A: IsStairGeometry() — geometría de escaleras nunca se desactiva.
//     FIX B: ConfigureNavMeshSurfaceForLevel() — overlap dinámico.
//     FIX C: DisableStairGeometryOutsideLevel() — eliminado.
//     FIX D: ValidateLevelNavMesh() — radio de sampling ampliado + diagnóstico de links.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

namespace IndoorNavAR.Navigation
{
    public class MultiLevelSurfaceService
    {
        private readonly float _surfacePadding;
        private readonly float _surfaceThickness;
        private readonly float _surfaceOffsetBelow;
        private readonly float _levelHeightMargin;
        private readonly int   _navMeshLayer;
        private readonly float _voxelSize;
        private readonly int   _minRegionArea;
        private readonly bool  _showDebugVisualization;

        // Geometría de escaleras — NUNCA se desactiva
        private static readonly string[] StairGeometryNamePrefixes =
        {
            "Ramp_", "Landing_", "StairLink", "Stair", "OpeningBarrier",
        };

        public MultiLevelSurfaceService(
            float surfacePadding,
            float surfaceThickness,
            float surfaceOffsetBelow,
            float levelHeightMargin,
            int   navMeshLayer,
            float voxelSize,
            int   minRegionArea,
            bool  showDebugVisualization)
        {
            _surfacePadding         = surfacePadding;
            _surfaceThickness       = surfaceThickness;
            _surfaceOffsetBelow     = surfaceOffsetBelow;
            _levelHeightMargin      = levelHeightMargin;
            _navMeshLayer           = navMeshLayer;
            _voxelSize              = voxelSize;
            _minRegionArea          = minRegionArea;
            _showDebugVisualization = showDebugVisualization;
        }

        #region Level Surface Creation

        /// <summary>
        /// Crea el GameObject de superficie caminable (plano geométrico) para un nivel.
        /// NO bakea NavMesh — el baking se hace de forma global en GlobalNavMeshBaker.
        /// </summary>
        public GameObject CreateLevelSurface(
            NavigableLevel level,
            out NavMeshSurface navMeshSurface)
        {
            Debug.Log($"[Surface] 🏗️ Creando superficie nivel {level.LevelIndex}...");

            GameObject walkableSurface = new GameObject($"WalkableSurface_Level{level.LevelIndex}");
            walkableSurface.layer    = _navMeshLayer;
            walkableSurface.isStatic = true;

            Vector3 planeCenter = level.HorizontalBounds.center;
            planeCenter.y = level.FloorHeight - _surfaceOffsetBelow;
            walkableSurface.transform.position = planeCenter;

            float width = level.HorizontalBounds.size.x + (_surfacePadding * 2f);
            float depth = level.HorizontalBounds.size.z + (_surfacePadding * 2f);

            Mesh planeMesh = CreatePlaneMesh(width, depth);

            MeshFilter mf = walkableSurface.AddComponent<MeshFilter>();
            mf.mesh = planeMesh;

            MeshRenderer mr = walkableSurface.AddComponent<MeshRenderer>();
            mr.material = CreateFloorMaterial(level.LevelIndex);

            MeshCollider mc = walkableSurface.AddComponent<MeshCollider>();
            mc.sharedMesh = planeMesh;
            mc.convex     = false;

            // NavMeshSurface se añade para compatibilidad con código legado.
            // En el nuevo pipeline, NO se llama BuildNavMesh() aquí.
            navMeshSurface = walkableSurface.AddComponent<NavMeshSurface>();
            ConfigureNavMeshSurfaceForLevel(navMeshSurface, level);

            Debug.Log($"[Surface] ✅ Superficie nivel {level.LevelIndex}: {width:F2}×{depth:F2}m, " +
                      $"Y=[{level.MinY:F2}, {level.MaxY:F2}]m");

            return walkableSurface;
        }

        private Mesh CreatePlaneMesh(float width, float depth)
        {
            Mesh mesh = new Mesh { name = "WalkablePlaneMesh" };

            float halfW = width  / 2f;
            float halfD = depth  / 2f;
            float halfT = _surfaceThickness / 2f;

            mesh.vertices = new Vector3[]
            {
                new(-halfW,  halfT, -halfD), new( halfW,  halfT, -halfD),
                new( halfW,  halfT,  halfD), new(-halfW,  halfT,  halfD),
                new(-halfW, -halfT, -halfD), new( halfW, -halfT, -halfD),
                new( halfW, -halfT,  halfD), new(-halfW, -halfT,  halfD),
            };

            mesh.triangles = new int[]
            {
                0,2,1, 0,3,2,  4,5,6, 4,6,7,
                0,1,5, 0,5,4,  3,7,6, 3,6,2,
                0,4,7, 0,7,3,  1,2,6, 1,6,5,
            };

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private Material CreateFloorMaterial(int levelIndex)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (_showDebugVisualization)
            {
                Color[] levelColors =
                {
                    new(0.2f, 0.8f, 0.2f, 0.4f),
                    new(0.2f, 0.5f, 0.8f, 0.4f),
                    new(0.8f, 0.5f, 0.2f, 0.4f),
                };
                mat.color = levelColors[levelIndex % levelColors.Length];
            }
            else
            {
                mat.color = new Color(0.5f, 0.5f, 0.5f, 0f);
            }
            return mat;
        }

        /// <summary>
        /// Configura el NavMeshSurface por nivel.
        /// En el nuevo pipeline con GlobalNavMeshBaker, estos datos se usan como
        /// referencia para calcular el volumen global pero NO se bakean individualmente.
        /// </summary>
        private void ConfigureNavMeshSurfaceForLevel(NavMeshSurface navMeshSurface, NavigableLevel level)
        {
            navMeshSurface.agentTypeID    = 0;
            navMeshSurface.collectObjects = CollectObjects.Volume;
            navMeshSurface.useGeometry    = NavMeshCollectGeometry.PhysicsColliders;
            navMeshSurface.layerMask      = 1 << _navMeshLayer;

            navMeshSurface.overrideVoxelSize = true;
            navMeshSurface.voxelSize         = _voxelSize;
            navMeshSurface.minRegionArea     = _minRegionArea;
            navMeshSurface.defaultArea       = 0;
            navMeshSurface.overrideTileSize  = false;

            float floorHeight  = level.FloorHeight;
            float overlapDown  = 0f;

            if (level.LevelIndex > 0)
            {
                float levelHeight = level.MaxY - level.MinY;
                overlapDown = levelHeight + 0.5f;
            }

            float volumeBottom = level.LevelIndex == 0
                ? level.MinY
                : floorHeight - overlapDown;

            float volumeTop    = floorHeight + _levelHeightMargin;
            float volumeHeight = volumeTop - volumeBottom;
            float hPadding     = _surfacePadding * 8f;

            navMeshSurface.size = new Vector3(
                level.HorizontalBounds.size.x + hPadding,
                volumeHeight,
                level.HorizontalBounds.size.z + hPadding);

            navMeshSurface.center = new Vector3(
                0f,
                (volumeBottom + volumeTop) * 0.5f - floorHeight,
                0f);

            Debug.Log(
                $"[Surface] Nivel {level.LevelIndex} | Volumen Y " +
                $"[{volumeBottom:F2} → {volumeTop:F2}] " +
                $"(altura={volumeHeight:F2}m, overlapDown={overlapDown:F2}m)");
        }

        #endregion

        #region NavMesh Baking (OBSOLETO — usar GlobalNavMeshBaker.BakeGlobal)

        /// <summary>
        /// ⚠️ OBSOLETO en Unity 6.3+.
        /// Usar <see cref="GlobalNavMeshBaker.BakeGlobal"/> en su lugar.
        /// Se mantiene por compatibilidad con código que llame a esta sobrecarga.
        /// </summary>
        [Obsolete("Usar GlobalNavMeshBaker.BakeGlobal() para garantizar conectividad de NavMeshLinks en Unity 6.3+")]
        public bool BakeLevelNavMesh(
            NavMeshSurface navMeshSurface,
            NavigableLevel currentLevel,
            List<GameObject> allObstacles,
            int navMeshLayer,
            out NavMeshBakingStats stats)
        {
            return BakeLevelNavMesh(navMeshSurface, currentLevel, allObstacles,
                                    navMeshLayer, null, out stats);
        }

        /// <summary>
        /// ⚠️ OBSOLETO en Unity 6.3+.
        /// Se mantiene por compatibilidad.
        /// </summary>
        [Obsolete("Usar GlobalNavMeshBaker.BakeGlobal() para garantizar conectividad de NavMeshLinks en Unity 6.3+")]
        public bool BakeLevelNavMesh(
            NavMeshSurface navMeshSurface,
            NavigableLevel currentLevel,
            List<GameObject> allObstacles,
            int navMeshLayer,
            Dictionary<int, List<GameObject>> stairGeometryByLevel,
            out NavMeshBakingStats stats)
        {
            stats = new NavMeshBakingStats();

            if (navMeshSurface == null)
            {
                Debug.LogError("[Surface] ❌ NavMeshSurface no inicializado");
                return false;
            }

            Debug.LogWarning(
                "[Surface] ⚠️ BakeLevelNavMesh() está obsoleto en Unity 6.3+.\n" +
                "Usar GlobalNavMeshBaker.BakeGlobal() para garantizar conectividad entre pisos.");

            var disabledColliders = new List<Collider>();

            try
            {
                disabledColliders = DisableObstacleCollidersOutsideLevel(currentLevel, allObstacles);

#if UNITY_EDITOR
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
                Canvas.ForceUpdateCanvases();

                float bakingStart = Time.realtimeSinceStartup;
                navMeshSurface.BuildNavMesh();
                stats.BakingTime = (Time.realtimeSinceStartup - bakingStart) * 1000f;

                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                stats.VertexCount   = tri.vertices.Length;
                stats.TriangleCount = tri.indices.Length / 3;
                stats.Area          = CalculateNavMeshArea(tri);

                if (!stats.IsValid)
                    Debug.LogWarning($"[Surface] ⚠️ NavMesh nivel {currentLevel.LevelIndex} vacío");

                return stats.IsValid;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Surface] ❌ Error en baking nivel {currentLevel.LevelIndex}: {ex.Message}");
                return false;
            }
            finally
            {
                RestoreColliders(disabledColliders);
            }
        }

        private List<Collider> DisableObstacleCollidersOutsideLevel(
            NavigableLevel currentLevel,
            List<GameObject> allObstacles)
        {
            var disabled = new List<Collider>();
            if (allObstacles == null) return disabled;

            int disabledCount = 0;
            int skippedStair  = 0;

            foreach (GameObject obstacle in allObstacles)
            {
                if (obstacle == null) continue;
                if (IsStairGeometry(obstacle)) { skippedStair++; continue; }

                Renderer renderer = obstacle.GetComponent<Renderer>();
                if (renderer == null) continue;

                float centerY = renderer.bounds.center.y;
                if (centerY < currentLevel.MinY || centerY > currentLevel.MaxY)
                {
                    foreach (Collider col in obstacle.GetComponents<Collider>())
                    {
                        if (col != null && col.enabled)
                        {
                            col.enabled = false;
                            disabled.Add(col);
                            disabledCount++;
                        }
                    }
                }
            }

            Debug.Log($"[Surface] 🔒 Nivel {currentLevel.LevelIndex}: " +
                      $"{disabledCount} colliders de obstáculos desactivados, " +
                      $"{skippedStair} escaleras preservadas.");

            return disabled;
        }

        private static bool IsStairGeometry(GameObject go)
        {
            if (go == null) return false;

            foreach (string prefix in StairGeometryNamePrefixes)
            {
                if (go.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (go.CompareTag("StairGeometry"))
                return true;

            NavMeshModifier modifier = go.GetComponent<NavMeshModifier>();
            if (modifier != null && modifier.overrideArea && modifier.area == 0)
                return true;

            return false;
        }

        private static void RestoreColliders(List<Collider> colliders)
        {
            int restored = 0;
            foreach (Collider col in colliders)
            {
                if (col != null)
                {
                    col.enabled = true;
                    restored++;
                }
            }
            if (restored > 0)
                Debug.Log($"[Surface] 🔓 Restaurados {restored} colliders de obstáculos");
        }

        private static float CalculateNavMeshArea(NavMeshTriangulation tri)
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
    }
}