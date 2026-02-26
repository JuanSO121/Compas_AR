// File: GlobalNavMeshBaker.cs
// ✅ v6 — CollectStairBuildSources usa ProceduralRoot para garantizar
//         que SOLO se recolectan meshes procedurales, nunca los del GLB.
//
//  CAMBIO PRINCIPAL (v5 → v6):
//
//  ANTES (v5): CollectStairBuildSources() llamaba
//    helper.GetComponentsInChildren<MeshFilter>(true)
//    → Aunque filtraba por isReadable, el recorrido empezaba DENTRO del
//      árbol del GLB (helper es hijo de Stairs > StairLeft), lo que
//      causaba que Unity también recorriera GOs del GLB con nombres
//      similares a los procedurales.
//    → El ProceduralRoot no existía → los GOs procedurales no tenían
//      un contenedor propio fuera del GLB.
//
//  AHORA (v6): helper.ProceduralRoot es un GO en la RAÍZ de escena,
//    completamente separado del GLB. CollectStairBuildSources() accede
//    directamente a helper.ProceduralRoot.GetComponentsInChildren<MeshFilter>()
//    → 100% procedural, 0% GLB.
//    → No se necesita filtrar por isReadable (todos son procedurales),
//      pero se mantiene el check como defensa en profundidad.
//
//  ComputeGlobalVolume() también usa ProceduralRoot para encapsular
//  bounds de las rampas, garantizando que el volumen de bake las cubra.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

namespace IndoorNavAR.Navigation
{
    public sealed class GlobalNavMeshBaker : MonoBehaviour
    {
        // ─── Estado ───────────────────────────────────────────────────────────
        private NavMeshSurface _globalSurface;
        private readonly List<Collider> _temporarilyDisabled = new();

        // ─── API pública ──────────────────────────────────────────────────────

        public async Task<(bool success, NavMeshBakingStats stats)> BakeGlobalAsync(
            IReadOnlyList<NavigableLevel> levels,
            IReadOnlyList<GameObject>     allObstacles,
            int   navMeshLayer,
            int   agentTypeID,
            float voxelSize,
            int   minRegionArea)
        {
            var stats = new NavMeshBakingStats();

            if (levels == null || levels.Count == 0)
            {
                Debug.LogError("[GlobalBaker] ❌ Lista de niveles vacía.");
                return (false, stats);
            }

            EnsureGlobalSurface(navMeshLayer);

            Bounds globalVolume = ComputeGlobalVolume(levels);
            LogVolumeInfo(globalVolume, levels);

            ConfigureSurface(_globalSurface, globalVolume, navMeshLayer, agentTypeID, voxelSize, minRegionArea);
            DisableNonStairObstaclesForGlobalBake(allObstacles);

            bool success = false;
            try
            {
                Debug.Log($"[GlobalBaker] 🔥 Bake híbrido v6 — volumen: " +
                          $"[{globalVolume.min:F2} → {globalVolume.max:F2}]");

#if UNITY_EDITOR
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
                Canvas.ForceUpdateCanvases();

                float t0 = Time.realtimeSinceStartup;

                await BakeHybridAsync(_globalSurface, globalVolume, navMeshLayer, agentTypeID,
                                      voxelSize, minRegionArea);

                stats.BakingTime = (Time.realtimeSinceStartup - t0) * 1000f;

                await Task.Yield();
                await Task.Yield();

                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                stats.VertexCount   = tri.vertices.Length;
                stats.TriangleCount = tri.indices.Length / 3;
                stats.Area          = CalculateArea(tri);

                if (!stats.IsValid)
                {
                    Debug.LogError("[GlobalBaker] ❌ NavMesh vacío. " +
                                   "Verificar que WalkableSurfaces están en layer NavMesh y " +
                                   "que ProceduralRoot existe en los StairWithLandingHelper.");
                    return (false, stats);
                }

                LogTriangulationCoverage(tri, levels);
                Debug.Log($"[GlobalBaker] ✅ Bake global: {stats}");
                success = true;

                VerifyInterLevelConnectivity(levels);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GlobalBaker] ❌ Excepción durante bake: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                RestoreDisabledColliders();
            }

            return (success, stats);
        }

        public bool BakeGlobal(
            IReadOnlyList<NavigableLevel> levels,
            IReadOnlyList<GameObject>     allObstacles,
            int   navMeshLayer,
            int   agentTypeID,
            float voxelSize,
            int   minRegionArea,
            out NavMeshBakingStats stats)
        {
            stats = new NavMeshBakingStats();

            if (levels == null || levels.Count == 0)
            {
                Debug.LogError("[GlobalBaker] ❌ Lista de niveles vacía.");
                return false;
            }

            EnsureGlobalSurface(navMeshLayer);
            Bounds globalVolume = ComputeGlobalVolume(levels);
            LogVolumeInfo(globalVolume, levels);
            ConfigureSurface(_globalSurface, globalVolume, navMeshLayer, agentTypeID, voxelSize, minRegionArea);
            DisableNonStairObstaclesForGlobalBake(allObstacles);

            bool success = false;
            try
            {
                Debug.Log($"[GlobalBaker] 🔥 Bake híbrido v6 (sync) — volumen: " +
                          $"[{globalVolume.min:F2} → {globalVolume.max:F2}]");

#if UNITY_EDITOR
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
                Canvas.ForceUpdateCanvases();

                float t0 = Time.realtimeSinceStartup;
                BakeHybridSync(_globalSurface, globalVolume, navMeshLayer, agentTypeID,
                               voxelSize, minRegionArea);
                stats.BakingTime = (Time.realtimeSinceStartup - t0) * 1000f;

                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                stats.VertexCount   = tri.vertices.Length;
                stats.TriangleCount = tri.indices.Length / 3;
                stats.Area          = CalculateArea(tri);

                if (!stats.IsValid)
                {
                    Debug.LogError("[GlobalBaker] ❌ NavMesh vacío (sync).");
                    return false;
                }

                LogTriangulationCoverage(tri, levels);
                Debug.Log($"[GlobalBaker] ✅ Bake global (sync): {stats}");
                success = true;

                VerifyInterLevelConnectivity(levels);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GlobalBaker] ❌ Excepción durante bake: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                RestoreDisabledColliders();
            }

            return success;
        }

        // ─── CORE: Bake híbrido ───────────────────────────────────────────────

        private async Task BakeHybridAsync(
            NavMeshSurface surface,
            Bounds globalVolume,
            int navMeshLayer,
            int agentTypeID,
            float voxelSize,
            int minRegionArea)
        {
            var settings = NavMesh.GetSettingsByID(agentTypeID);
            settings.overrideVoxelSize = true;
            settings.voxelSize         = voxelSize;
            settings.minRegionArea     = minRegionArea;

            var sources = new List<NavMeshBuildSource>();

            // Solo navMeshLayer (layer 6) para WalkableSurfaces procedurales.
            // El GLB en Default (0) queda excluido intencionalmente.
            int onlyNavMeshLayerMask = 1 << navMeshLayer;

            NavMeshBuilder.CollectSources(
                globalVolume,
                onlyNavMeshLayerMask,
                NavMeshCollectGeometry.PhysicsColliders,
                0,
                new List<NavMeshBuildMarkup>(),
                sources);

            Debug.Log($"[GlobalBaker] 📦 Fuentes WalkableSurface (layer {navMeshLayer}): {sources.Count}");

            // ✅ v6: CollectStairBuildSources usa ProceduralRoot
            var stairSources = CollectStairBuildSources(navMeshLayer);
            sources.AddRange(stairSources);

            Debug.Log($"[GlobalBaker] 📦 Total fuentes: {sources.Count} " +
                      $"({sources.Count - stairSources.Count} floor + {stairSources.Count} escalera)");

            surface.RemoveData();

            NavMeshData data = NavMeshBuilder.BuildNavMeshData(
                settings,
                sources,
                globalVolume,
                Vector3.zero,
                Quaternion.identity);

            surface.navMeshData = data;
            surface.AddData();

            await Task.Yield();
        }

        private void BakeHybridSync(
            NavMeshSurface surface,
            Bounds globalVolume,
            int navMeshLayer,
            int agentTypeID,
            float voxelSize,
            int minRegionArea)
        {
            var settings = NavMesh.GetSettingsByID(agentTypeID);
            settings.overrideVoxelSize = true;
            settings.voxelSize         = voxelSize;
            settings.minRegionArea     = minRegionArea;

            var sources = new List<NavMeshBuildSource>();

            int onlyNavMeshLayerMask = 1 << navMeshLayer;

            NavMeshBuilder.CollectSources(
                globalVolume,
                onlyNavMeshLayerMask,
                NavMeshCollectGeometry.PhysicsColliders,
                0,
                new List<NavMeshBuildMarkup>(),
                sources);

            Debug.Log($"[GlobalBaker] 📦 Fuentes WalkableSurface (sync, layer {navMeshLayer}): {sources.Count}");

            var stairSources = CollectStairBuildSources(navMeshLayer);
            sources.AddRange(stairSources);

            Debug.Log($"[GlobalBaker] 📦 Total fuentes (sync): {sources.Count}");

            surface.RemoveData();

            NavMeshData data = NavMeshBuilder.BuildNavMeshData(
                settings,
                sources,
                globalVolume,
                Vector3.zero,
                Quaternion.identity);

            surface.navMeshData = data;
            surface.AddData();
        }

        // ─── ✅ v6: Recolección de fuentes de escalera via ProceduralRoot ─────

        /// <summary>
        /// ✅ v6: Accede a helper.ProceduralRoot en lugar de recorrer el árbol del GLB.
        ///
        /// helper.ProceduralRoot es un GO en la RAÍZ de escena que contiene SOLO
        /// los GOs procedurales (NavRamp_T1_*, NavRamp_T2_*, NavLanding_*).
        /// Ningún mesh del GLB puede estar ahí → sin riesgo de meshes no-readables.
        ///
        /// Si ProceduralRoot es null (CreateStairSystem no fue llamado),
        /// se loguea un error claro y se omite esa escalera.
        /// </summary>
        private static List<NavMeshBuildSource> CollectStairBuildSources(int navMeshLayer)
        {
            var sources = new List<NavMeshBuildSource>();

            var stairHelpers = FindObjectsByType<StairWithLandingHelper>(FindObjectsSortMode.None);
            int meshCount       = 0;
            int skippedNoRoot   = 0;
            int layerCorrected  = 0;

            foreach (var helper in stairHelpers)
            {
                if (helper == null) continue;

                // ✅ v6: Usar ProceduralRoot en lugar de GetComponentsInChildren desde el GLB
                GameObject procRoot = helper.ProceduralRoot;

                if (procRoot == null)
                {
                    skippedNoRoot++;
                    Debug.LogWarning($"[GlobalBaker] ⚠️ '{helper.name}': ProceduralRoot es NULL. " +
                                     $"Ejecutar 'Crear Escalera' (ContextMenu) en el StairWithLandingHelper. " +
                                     $"Escalera omitida del bake.");
                    continue;
                }

                Debug.Log($"[GlobalBaker] 🪜 Procesando ProceduralRoot '{procRoot.name}' " +
                          $"de helper '{helper.name}'...");

                // Recolectar MeshFilters del contenedor procedural
                foreach (var mf in procRoot.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null) continue;

                    Mesh mesh = mf.sharedMesh;

                    // Defensa en profundidad: todos deberían ser readables (son procedurales)
                    if (!mesh.isReadable)
                    {
                        // Esto NO debería pasar con el nuevo flujo. Si ocurre, es un bug.
                        Debug.LogError($"[GlobalBaker] ❌ INESPERADO: mesh NO readable en ProceduralRoot: " +
                                       $"'{mesh.name}' en '{mf.gameObject.name}'. " +
                                       $"Revisar CreateRamp() — solo debe crear meshes con 'new Mesh()'.");
                        continue;
                    }

                    // Corregir layer si es necesario
                    if (mf.gameObject.layer != navMeshLayer)
                    {
                        mf.gameObject.layer = navMeshLayer;
                        layerCorrected++;
                    }

                    var source = new NavMeshBuildSource
                    {
                        shape        = NavMeshBuildSourceShape.Mesh,
                        sourceObject = mesh,
                        transform    = Matrix4x4.TRS(
                            mf.transform.position,
                            mf.transform.rotation,
                            mf.transform.lossyScale),
                        area         = 0, // Walkable
                        component    = mf
                    };

                    sources.Add(source);
                    meshCount++;

                    Debug.Log($"[GlobalBaker]   + Mesh '{mesh.name}': " +
                              $"{mesh.vertexCount}v/{mesh.triangles.Length/3}t, " +
                              $"pos={mf.transform.position:F3}, isReadable=✅");
                }

                // BoxColliders procedurales del ProceduralRoot
                foreach (var box in procRoot.GetComponentsInChildren<BoxCollider>(true))
                {
                    if (box == null) continue;

                    if (box.gameObject.layer != navMeshLayer)
                    {
                        box.gameObject.layer = navMeshLayer;
                        layerCorrected++;
                    }

                    var source = new NavMeshBuildSource
                    {
                        shape     = NavMeshBuildSourceShape.Box,
                        transform = Matrix4x4.TRS(
                            box.transform.TransformPoint(box.center),
                            box.transform.rotation,
                            box.transform.lossyScale),
                        size      = box.size,
                        area      = 0
                    };
                    sources.Add(source);
                    meshCount++;
                }
            }

            Debug.Log($"[GlobalBaker] 🪜 Fuentes de escalera v6: {meshCount} fuentes recolectadas, " +
                      $"{skippedNoRoot} helpers sin ProceduralRoot (ejecutar Crear Escalera), " +
                      $"{layerCorrected} GOs con layer corregido a {navMeshLayer}.");

            if (skippedNoRoot > 0)
                Debug.LogError($"[GlobalBaker] ❌ {skippedNoRoot} escalera(s) omitidas — el NavMesh NO tendrá geometría de rampa. " +
                               $"Solución: clic derecho en cada StairWithLandingHelper → '🏗️ Crear Escalera'");

            return sources;
        }

        // ─── Configuración del NavMeshSurface global ──────────────────────────

        private void EnsureGlobalSurface(int navMeshLayer)
        {
            if (_globalSurface != null)
            {
                _globalSurface.RemoveData();
                Destroy(_globalSurface.gameObject);
                _globalSurface = null;
            }

            GameObject go = new GameObject("GlobalNavMeshSurface");
            go.transform.SetParent(null);
            go.transform.position   = Vector3.zero;
            go.transform.rotation   = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            go.layer    = navMeshLayer;
            go.isStatic = true;

            _globalSurface = go.AddComponent<NavMeshSurface>();

            Debug.Log($"[GlobalBaker] 📦 GlobalNavMeshSurface creado (world 0,0,0). " +
                      $"Layer: {LayerMask.LayerToName(navMeshLayer)} ({navMeshLayer})");
        }

        private static void ConfigureSurface(
            NavMeshSurface surface,
            Bounds         volume,
            int            navMeshLayer,
            int            agentTypeID,
            float          voxelSize,
            int            minRegionArea)
        {
            surface.agentTypeID    = agentTypeID;
            surface.collectObjects = CollectObjects.Volume;
            surface.useGeometry    = NavMeshCollectGeometry.PhysicsColliders;

            // Solo navMeshLayer — sin Default para no recoger el GLB
            int layerMask = 1 << navMeshLayer;
            surface.layerMask = layerMask;

            surface.defaultArea    = 0;
            surface.overrideVoxelSize = true;
            surface.voxelSize         = voxelSize;
            surface.minRegionArea     = minRegionArea;
            surface.overrideTileSize  = false;

            surface.center = volume.center;
            surface.size   = volume.size;

            Debug.Log($"[GlobalBaker] 📐 Surface config v6: center={volume.center:F2}, size={volume.size:F2}, " +
                      $"layerMask={layerMask} (layer '{LayerMask.LayerToName(navMeshLayer)}' = {navMeshLayer}), " +
                      $"geometry=PhysicsColliders, voxel={voxelSize}");
        }

        // ─── ✅ v6: Volumen global incluye ProceduralRoot de cada escalera ────

        private static Bounds ComputeGlobalVolume(IReadOnlyList<NavigableLevel> levels)
        {
            Bounds combined = levels[0].HorizontalBounds;

            foreach (NavigableLevel level in levels)
            {
                combined.Encapsulate(level.HorizontalBounds);
                combined.Encapsulate(new Vector3(level.HorizontalBounds.min.x, level.MinY, level.HorizontalBounds.min.z));
                combined.Encapsulate(new Vector3(level.HorizontalBounds.max.x, level.MaxY, level.HorizontalBounds.max.z));
            }

            var stairHelpers = FindObjectsByType<StairWithLandingHelper>(FindObjectsSortMode.None);
            int stairBoundsAdded = 0;

            foreach (var helper in stairHelpers)
            {
                if (helper == null) continue;

                // ✅ v6: Priorizar ProceduralRoot para encapsular bounds de rampas procedurales
                if (helper.ProceduralRoot != null)
                {
                    foreach (var renderer in helper.ProceduralRoot.GetComponentsInChildren<Renderer>(true))
                    {
                        if (renderer == null) continue;
                        combined.Encapsulate(renderer.bounds);
                        stairBoundsAdded++;
                    }
                    foreach (var col in helper.ProceduralRoot.GetComponentsInChildren<Collider>(true))
                    {
                        if (col == null) continue;
                        combined.Encapsulate(col.bounds);
                        stairBoundsAdded++;
                    }
                }
                else
                {
                    // Fallback: usar colliders del helper mismo (puede incluir GLB)
                    Debug.LogWarning($"[GlobalBaker] ⚠️ '{helper.name}': ProceduralRoot null al calcular volumen. " +
                                     $"El volumen de bake puede no cubrir las rampas.");
                    foreach (var col in helper.GetComponentsInChildren<Collider>(true))
                    {
                        if (col == null) continue;
                        combined.Encapsulate(col.bounds);
                        stairBoundsAdded++;
                    }
                }
            }

            if (stairBoundsAdded > 0)
                Debug.Log($"[GlobalBaker] 📐 Volumen expandido con {stairBoundsAdded} bounds de escaleras");

            const float horizontalMargin = 2.0f;
            const float verticalMargin   = 0.5f;

            combined.Expand(new Vector3(
                horizontalMargin * 2f,
                verticalMargin   * 2f,
                horizontalMargin * 2f));

            return combined;
        }

        // ─── Obstáculos ───────────────────────────────────────────────────────

        private void DisableNonStairObstaclesForGlobalBake(IReadOnlyList<GameObject> allObstacles)
        {
            if (allObstacles == null) return;

            foreach (GameObject obs in allObstacles)
            {
                if (obs == null) continue;
                if (IsStairGeometry(obs)) continue;

                foreach (Collider col in obs.GetComponents<Collider>())
                {
                    if (col != null && !col.enabled)
                        col.enabled = true;
                }
            }

            Debug.Log("[GlobalBaker] 🔓 Colliders de obstáculos activados para bake global.");
        }

        private void RestoreDisabledColliders()
        {
            foreach (Collider col in _temporarilyDisabled)
            {
                if (col != null) col.enabled = true;
            }
            _temporarilyDisabled.Clear();
        }

        // ─── Verificación post-bake ───────────────────────────────────────────

        public void ValidateStairLinks(float functionalRadiusM = 1.5f)
        {
            const float strictRadiusM = 0.5f;

            var links = FindObjectsByType<NavMeshLink>(FindObjectsSortMode.None);
            int ok = 0, warn = 0, broken = 0;

            foreach (NavMeshLink link in links)
            {
                if (link == null) continue;

                Vector3 ws = link.transform.TransformPoint(link.startPoint);
                Vector3 we = link.transform.TransformPoint(link.endPoint);

                bool startStrict = NavMesh.SamplePosition(ws, out NavMeshHit hsStrict, strictRadiusM,    NavMesh.AllAreas);
                bool endStrict   = NavMesh.SamplePosition(we, out NavMeshHit heStrict, strictRadiusM,    NavMesh.AllAreas);
                bool startWide   = NavMesh.SamplePosition(ws, out NavMeshHit hsWide,   functionalRadiusM, NavMesh.AllAreas);
                bool endWide     = NavMesh.SamplePosition(we, out NavMeshHit heWide,   functionalRadiusM, NavMesh.AllAreas);

                if (startStrict && endStrict)
                {
                    ok++;
                    Debug.Log($"[GlobalBaker] ✅ Link '{link.gameObject.name}': OK " +
                              $"(start@{hsStrict.position:F3}, end@{heStrict.position:F3})");
                }
                else if (startWide && endWide)
                {
                    warn++;
                    float startDist = Vector3.Distance(ws, hsWide.position);
                    float endDist   = Vector3.Distance(we, heWide.position);
                    Debug.LogWarning(
                        $"[GlobalBaker] ⚠️ Link '{link.gameObject.name}': funcional pero endpoint desplazado.\n" +
                        $"  start: dist={startDist:F3}m{(startStrict ? "" : " ← ajustar")}\n" +
                        $"  end:   dist={endDist:F3}m{(endStrict   ? "" : " ← ajustar")}");
                }
                else
                {
                    broken++;
                    NavMesh.SamplePosition(ws, out NavMeshHit hsDiag, 5f, NavMesh.AllAreas);
                    NavMesh.SamplePosition(we, out NavMeshHit heDiag, 5f, NavMesh.AllAreas);
                    Debug.LogError(
                        $"[GlobalBaker] ❌ Link '{link.gameObject.name}' ROTO:\n" +
                        $"  start: pos={ws:F3} → NavMesh más cercano: {hsDiag.position:F3} " +
                        $"(dist={Vector3.Distance(ws, hsDiag.position):F3}m)\n" +
                        $"  end:   pos={we:F3} → NavMesh más cercano: {heDiag.position:F3} " +
                        $"(dist={Vector3.Distance(we, heDiag.position):F3}m)");
                }
            }

            Debug.Log($"[GlobalBaker] 🔗 NavMeshLinks: {ok} OK / {warn} funcionales / {broken} rotos");
        }

        private static void VerifyInterLevelConnectivity(IReadOnlyList<NavigableLevel> levels)
        {
            if (levels.Count < 2) return;

            var pts = NavigationStartPointManager.GetAllStartPoints();
            if (pts.Count < 2) return;

            NavMesh.SamplePosition(pts[0].Position, out NavMeshHit h0, 2f, NavMesh.AllAreas);
            NavMesh.SamplePosition(pts[pts.Count - 1].Position, out NavMeshHit h1, 2f, NavMesh.AllAreas);

            NavMeshPath path = new NavMeshPath();
            NavMesh.CalculatePath(h0.position, h1.position, NavMesh.AllAreas, path);

            switch (path.status)
            {
                case NavMeshPathStatus.PathComplete:
                    Debug.Log($"[GlobalBaker] ✅ Conectividad entre pisos: COMPLETA ({path.corners.Length} waypoints)");
                    break;
                case NavMeshPathStatus.PathPartial:
                    Debug.LogWarning("[GlobalBaker] ⚠️ Conectividad entre pisos: PARCIAL");
                    break;
                case NavMeshPathStatus.PathInvalid:
                    Debug.LogError("[GlobalBaker] ❌ Conectividad entre pisos: INVÁLIDA");
                    break;
            }
        }

        // ─── Logging ─────────────────────────────────────────────────────────

        private static void LogVolumeInfo(Bounds volume, IReadOnlyList<NavigableLevel> levels)
        {
            Debug.Log($"[GlobalBaker] 📊 Volumen global:");
            Debug.Log($"  min={volume.min:F2} max={volume.max:F2}");
            Debug.Log($"  Centro: {volume.center:F2}, Tamaño: {volume.size:F2}");
            for (int i = 0; i < levels.Count; i++)
            {
                var lvl = levels[i];
                bool coveredMin = lvl.MinY >= volume.min.y && lvl.MinY <= volume.max.y;
                bool coveredMax = lvl.MaxY >= volume.min.y && lvl.MaxY <= volume.max.y;
                string coverage = (coveredMin && coveredMax) ? "✅ CUBIERTO" : "❌ FUERA DEL VOLUMEN";
                Debug.Log($"  Level {lvl.LevelIndex}: Y=[{lvl.MinY:F2}, {lvl.MaxY:F2}] floor={lvl.FloorHeight:F2} → {coverage}");
            }
        }

        private static void LogTriangulationCoverage(NavMeshTriangulation tri, IReadOnlyList<NavigableLevel> levels)
        {
            if (tri.vertices.Length == 0) return;

            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var v in tri.vertices)
            {
                if (v.y < minY) minY = v.y;
                if (v.y > maxY) maxY = v.y;
            }

            Debug.Log($"[GlobalBaker] 📐 Triangulación: {tri.vertices.Length} verts, Y=[{minY:F2}, {maxY:F2}]");

            for (int i = 0; i < levels.Count; i++)
            {
                var lvl = levels[i];
                bool hasVerticesAtLevel = tri.vertices.Any(v =>
                    v.y >= lvl.FloorHeight - 0.5f && v.y <= lvl.FloorHeight + 0.5f);
                string status = hasVerticesAtLevel ? "✅ PRESENTE" : "❌ AUSENTE";
                Debug.Log($"  Level {lvl.LevelIndex} (Y={lvl.FloorHeight:F2}): {status} en NavMesh");
            }

            if (levels.Count >= 2)
            {
                float floor0 = levels[0].FloorHeight;
                float floor1 = levels[levels.Count - 1].FloorHeight;
                bool hasStairVertices = tri.vertices.Any(v =>
                    v.y > floor0 + 0.1f && v.y < floor1 - 0.1f);
                Debug.Log($"  Vértices de rampa (entre pisos): " +
                          $"{(hasStairVertices ? "✅ SÍ — rampas incluidas en NavMesh" : "⚠️ NO — solo NavMeshLinks disponibles")}");
            }
        }

        // ─── Utilidades ───────────────────────────────────────────────────────

        private static bool IsStairGeometry(GameObject go)
        {
            if (go == null) return false;

            string[] prefixes = { "NavRamp_", "NavLanding_", "NavLink_", "Ramp_", "Landing_", "StairLink", "OpeningBarrier" };
            foreach (string prefix in prefixes)
                if (go.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;

            if (go.CompareTag("StairGeometry")) return true;

            NavMeshModifier modifier = go.GetComponent<NavMeshModifier>();
            if (modifier != null && modifier.overrideArea && modifier.area == 0)
                return true;

            // ✅ v6: Verificar si pertenece al ProceduralRoot de algún helper
            if (go.GetComponentInParent<StairWithLandingHelper>() != null)
                return true;

            // Verificar si el GO o algún ancestro ES un ProceduralRoot
            Transform t = go.transform;
            while (t != null)
            {
                if (t.name.StartsWith("NavRamps_", StringComparison.OrdinalIgnoreCase))
                    return true;
                t = t.parent;
            }

            return false;
        }

        private static float CalculateArea(NavMeshTriangulation tri)
        {
            float area = 0f;
            for (int i = 0; i < tri.indices.Length; i += 3)
            {
                Vector3 a = tri.vertices[tri.indices[i]];
                Vector3 b = tri.vertices[tri.indices[i + 1]];
                Vector3 c = tri.vertices[tri.indices[i + 2]];
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            return area;
        }

        // ─── Limpieza ─────────────────────────────────────────────────────────

        private void OnDestroy()
        {
            if (_globalSurface != null)
                _globalSurface.RemoveData();
        }
    }
}