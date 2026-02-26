// File: GlobalNavMeshBaker.cs
// ✅ v8 — Vuelve al enfoque v6 (UNA sola instancia con todo junto) que SÍ generaba
//         la rampa correctamente, con correcciones menores de robustez.
//
// ═══════════════════════════════════════════════════════════════════════════
// POR QUÉ SE REVIERTE v7 → v8
// ═══════════════════════════════════════════════════════════════════════════
//
//  v7 intentaba dos NavMeshDataInstances separadas: una para pisos (settings
//  normales) y otra para rampas (settings permissivos: agentHeight=0.001).
//
//  PROBLEMA 1: NavMesh.GetSettingsByID() + sobreescribir agentHeight=0.001
//    puede resultar en BuildNavMeshData devolviendo null o un NavMeshData
//    vacío porque los settings son físicamente incoherentes con el agentTypeID
//    registrado (Unity valida internamente mínimos por tipo de agente).
//
//  PROBLEMA 2: Aunque la segunda instancia se generara, el serializer
//    (NavMeshSerializer.InjectPermissive) RE-VOXELIZA al restaurar.
//    Los triángulos diagonales de la rampa se pierden en esa re-voxelización
//    igual que en el bake original con settings restrictivos — el bug persiste.
//
//  SOLUCIÓN v8 (= enfoque v6 que funcionaba):
//    - UN SOLO BuildNavMeshData con todas las fuentes juntas (pisos + rampas).
//    - Los settings del agente se usan para los pisos; las rampas son fuentes
//      Mesh directas (procedurales, isReadable=true) que el voxelizador acepta
//      porque son geometría explícita, no física de escena.
//    - CollectStairBuildSources() accede a helper.ProceduralRoot (v6).
//    - El NavMesh resultante incluye rampas → CalculateTriangulation() los
//      incluye → NavMeshSerializer.Save() los guarda en el .bin unificado.
//
// ═══════════════════════════════════════════════════════════════════════════
// PIPELINE DE BAKE (v8)
// ═══════════════════════════════════════════════════════════════════════════
//
//  1. CollectSources (layer NavMesh) → fuentes de pisos WalkableSurface
//  2. CollectStairBuildSources (ProceduralRoot) → fuentes Mesh de rampas
//  3. sources.AddRange(stairSources) → TODO en una lista
//  4. BuildNavMeshData con settings del agente (slope=75°, climb=0.1) y
//     voxelSize ajustado → genera NavMesh unificado
//  5. surface.AddData() → instancia única activa
//  6. CalculateTriangulation() → verifica presencia de vértices de rampa
//
// ═══════════════════════════════════════════════════════════════════════════

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
        private NavMeshSurface          _globalSurface;
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
                Debug.Log($"[GlobalBaker] 🔥 Bake v8 (fuentes unificadas) — volumen: " +
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
                Debug.Log($"[GlobalBaker] ✅ Bake v8 completado: {stats}");
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
                Debug.Log($"[GlobalBaker] 🔥 Bake v8 (sync) — volumen: " +
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
                Debug.Log($"[GlobalBaker] ✅ Bake v8 (sync): {stats}");
                success = true;

                VerifyInterLevelConnectivity(levels);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GlobalBaker] ❌ Excepción (sync): {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                RestoreDisabledColliders();
            }

            return success;
        }

        // ─── CORE: Bake híbrido (pisos + rampas en una sola pasada) ──────────

        private async Task BakeHybridAsync(
            NavMeshSurface surface,
            Bounds         globalVolume,
            int navMeshLayer, int agentTypeID, float voxelSize, int minRegionArea)
        {
            var sources = BuildUnifiedSources(globalVolume, navMeshLayer, out int stairCount);

            surface.RemoveData();
            NavMeshData data = BuildNavMeshData(agentTypeID, voxelSize, minRegionArea, sources, globalVolume);
            surface.navMeshData = data;
            surface.AddData();

            Debug.Log($"[GlobalBaker] 📦 Async — {sources.Count} fuentes " +
                      $"({sources.Count - stairCount} piso + {stairCount} escalera)");

            await Task.Yield();
        }

        private void BakeHybridSync(
            NavMeshSurface surface,
            Bounds         globalVolume,
            int navMeshLayer, int agentTypeID, float voxelSize, int minRegionArea)
        {
            var sources = BuildUnifiedSources(globalVolume, navMeshLayer, out int stairCount);

            surface.RemoveData();
            NavMeshData data = BuildNavMeshData(agentTypeID, voxelSize, minRegionArea, sources, globalVolume);
            surface.navMeshData = data;
            surface.AddData();

            Debug.Log($"[GlobalBaker] 📦 Sync — {sources.Count} fuentes " +
                      $"({sources.Count - stairCount} piso + {stairCount} escalera)");
        }

        /// <summary>
        /// Recolecta TODAS las fuentes: WalkableSurfaces (pisos) + ProceduralRoot (rampas).
        /// </summary>
        private static List<NavMeshBuildSource> BuildUnifiedSources(
            Bounds globalVolume,
            int    navMeshLayer,
            out int stairSourceCount)
        {
            // ── Fuentes de pisos (layer NavMesh) ──────────────────────────────
            var sources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(
                globalVolume,
                1 << navMeshLayer,
                NavMeshCollectGeometry.PhysicsColliders,
                0,
                new List<NavMeshBuildMarkup>(),
                sources);

            int floorCount = sources.Count;
            Debug.Log($"[GlobalBaker] 📦 Fuentes de piso (layer {navMeshLayer}): {floorCount}");

            // ── Fuentes de escalera (ProceduralRoot) ──────────────────────────
            var stairSources = CollectStairBuildSources(navMeshLayer);
            sources.AddRange(stairSources);
            stairSourceCount = stairSources.Count;

            return sources;
        }

        /// <summary>
        /// Construye NavMeshData con los settings del agente.
        /// Las rampas son fuentes Mesh directas → el voxelizador las acepta aunque
        /// estén inclinadas, porque no dependen de height-clearance de la escena.
        ///
        /// NOTA: Si las rampas siguen siendo filtradas, subir agentSlope en
        ///   Project Settings → Navigation → Agents o usar el campo Inspector
        ///   "Agent Max Slope" en el MultiLevelNavMeshGenerator.
        /// </summary>
        private static NavMeshData BuildNavMeshData(
            int   agentTypeID,
            float voxelSize,
            int   minRegionArea,
            List<NavMeshBuildSource> sources,
            Bounds volume)
        {
            NavMeshBuildSettings s = NavMesh.GetSettingsByID(agentTypeID);
            s.overrideVoxelSize = true;
            s.voxelSize         = voxelSize;
            s.minRegionArea     = minRegionArea;

            // Log de los settings reales para diagnóstico
            Debug.Log($"[GlobalBaker] 🔧 Settings agente: height={s.agentHeight:F2}, " +
                      $"radius={s.agentRadius:F2}, slope={s.agentSlope:F1}°, " +
                      $"climb={s.agentClimb:F3}, voxel={s.voxelSize}");

            return NavMeshBuilder.BuildNavMeshData(
                s, sources, volume, Vector3.zero, Quaternion.identity);
        }

        // ─── Recolección de fuentes de escalera via ProceduralRoot ───────────

        /// <summary>
        /// Accede a helper.ProceduralRoot (GO en raíz de escena, fuera del GLB).
        /// Recolecta MeshFilters procedurales (isReadable garantizado) y BoxColliders.
        /// Corrige el layer de los GOs si es necesario para que CollectSources
        /// los incluya en futuras llamadas.
        /// </summary>
        private static List<NavMeshBuildSource> CollectStairBuildSources(int navMeshLayer)
        {
            var sources = new List<NavMeshBuildSource>();
            var stairHelpers = FindObjectsByType<StairWithLandingHelper>(FindObjectsSortMode.None);

            int meshCount      = 0;
            int skippedNoRoot  = 0;
            int layerCorrected = 0;

            foreach (var helper in stairHelpers)
            {
                if (helper == null) continue;

                GameObject procRoot = helper.ProceduralRoot;

                if (procRoot == null)
                {
                    skippedNoRoot++;
                    Debug.LogWarning($"[GlobalBaker] ⚠️ '{helper.name}': ProceduralRoot NULL → " +
                                     "ejecutar '🏗️ Crear Escalera'. Escalera omitida.");
                    continue;
                }

                Debug.Log($"[GlobalBaker] 🪜 Procesando ProceduralRoot '{procRoot.name}'...");

                // ── MeshFilters procedurales ──────────────────────────────────
                foreach (var mf in procRoot.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null) continue;

                    Mesh mesh = mf.sharedMesh;

                    if (!mesh.isReadable)
                    {
                        // No debería ocurrir con meshes procedurales (new Mesh())
                        Debug.LogError($"[GlobalBaker] ❌ INESPERADO: mesh no readable: " +
                                       $"'{mesh.name}'. Revisar CreateRamp() en StairHelper.");
                        continue;
                    }

                    if (mf.gameObject.layer != navMeshLayer)
                    {
                        mf.gameObject.layer = navMeshLayer;
                        layerCorrected++;
                    }

                    sources.Add(new NavMeshBuildSource
                    {
                        shape        = NavMeshBuildSourceShape.Mesh,
                        sourceObject = mesh,
                        transform    = Matrix4x4.TRS(
                            mf.transform.position,
                            mf.transform.rotation,
                            mf.transform.lossyScale),
                        area         = 0, // Walkable
                        component    = mf
                    });
                    meshCount++;

                    Debug.Log($"[GlobalBaker]   + '{mesh.name}': {mesh.vertexCount}v/" +
                              $"{mesh.triangles.Length/3}t @ {mf.transform.position:F3} ✅readable");
                }

                // ── BoxColliders procedurales (descansillo) ───────────────────
                foreach (var box in procRoot.GetComponentsInChildren<BoxCollider>(true))
                {
                    if (box == null) continue;

                    if (box.gameObject.layer != navMeshLayer)
                    {
                        box.gameObject.layer = navMeshLayer;
                        layerCorrected++;
                    }

                    sources.Add(new NavMeshBuildSource
                    {
                        shape     = NavMeshBuildSourceShape.Box,
                        transform = Matrix4x4.TRS(
                            box.transform.TransformPoint(box.center),
                            box.transform.rotation,
                            box.transform.lossyScale),
                        size  = box.size,
                        area  = 0
                    });
                    meshCount++;
                }
            }

            Debug.Log($"[GlobalBaker] 🪜 Fuentes de escalera: {meshCount} recolectadas, " +
                      $"{skippedNoRoot} helpers sin ProceduralRoot, " +
                      $"{layerCorrected} GOs con layer corregido.");

            if (skippedNoRoot > 0)
                Debug.LogError($"[GlobalBaker] ❌ {skippedNoRoot} escalera(s) sin ProceduralRoot — " +
                               "el NavMesh NO tendrá geometría de rampa. " +
                               "Clic derecho en cada StairWithLandingHelper → '🏗️ Crear Escalera'");

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

            var go = new GameObject("GlobalNavMeshSurface");
            go.transform.SetParent(null);
            go.transform.position   = Vector3.zero;
            go.transform.rotation   = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            go.layer    = navMeshLayer;
            go.isStatic = true;

            _globalSurface = go.AddComponent<NavMeshSurface>();

            Debug.Log($"[GlobalBaker] 📦 GlobalNavMeshSurface creado. " +
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
            surface.agentTypeID       = agentTypeID;
            surface.collectObjects    = CollectObjects.Volume;
            surface.useGeometry       = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask         = 1 << navMeshLayer;
            surface.defaultArea       = 0;
            surface.overrideVoxelSize = true;
            surface.voxelSize         = voxelSize;
            surface.minRegionArea     = minRegionArea;
            surface.overrideTileSize  = false;
            surface.center            = volume.center;
            surface.size              = volume.size;

            Debug.Log($"[GlobalBaker] 📐 Surface: center={volume.center:F2}, " +
                      $"size={volume.size:F2}, layer={navMeshLayer}, voxel={voxelSize}");
        }

        // ─── Volumen global (incluye ProceduralRoot de cada escalera) ─────────

        private static Bounds ComputeGlobalVolume(IReadOnlyList<NavigableLevel> levels)
        {
            Bounds combined = levels[0].HorizontalBounds;

            foreach (NavigableLevel level in levels)
            {
                combined.Encapsulate(level.HorizontalBounds);
                combined.Encapsulate(new Vector3(level.HorizontalBounds.min.x, level.MinY, level.HorizontalBounds.min.z));
                combined.Encapsulate(new Vector3(level.HorizontalBounds.max.x, level.MaxY, level.HorizontalBounds.max.z));
            }

            var stairHelpers  = FindObjectsByType<StairWithLandingHelper>(FindObjectsSortMode.None);
            int boundsAdded   = 0;

            foreach (var helper in stairHelpers)
            {
                if (helper == null) continue;

                GameObject root = helper.ProceduralRoot;

                if (root != null)
                {
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                        if (r != null) { combined.Encapsulate(r.bounds); boundsAdded++; }
                    foreach (var c in root.GetComponentsInChildren<Collider>(true))
                        if (c != null) { combined.Encapsulate(c.bounds); boundsAdded++; }
                }
                else
                {
                    Debug.LogWarning($"[GlobalBaker] ⚠️ '{helper.name}': ProceduralRoot null → " +
                                     "volumen puede no cubrir las rampas.");
                    foreach (var c in helper.GetComponentsInChildren<Collider>(true))
                        if (c != null) { combined.Encapsulate(c.bounds); boundsAdded++; }
                }
            }

            if (boundsAdded > 0)
                Debug.Log($"[GlobalBaker] 📐 Volumen expandido con {boundsAdded} bounds de escaleras.");

            combined.Expand(new Vector3(4f, 1f, 4f));
            return combined;
        }

        // ─── Obstáculos ───────────────────────────────────────────────────────

        private void DisableNonStairObstaclesForGlobalBake(IReadOnlyList<GameObject> allObstacles)
        {
            if (allObstacles == null) return;
            foreach (GameObject obs in allObstacles)
            {
                if (obs == null || IsStairGeometry(obs)) continue;
                foreach (Collider col in obs.GetComponents<Collider>())
                    if (col != null && !col.enabled) col.enabled = true;
            }
            Debug.Log("[GlobalBaker] 🔓 Colliders de obstáculos activados.");
        }

        private void RestoreDisabledColliders()
        {
            foreach (Collider col in _temporarilyDisabled)
                if (col != null) col.enabled = true;
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

                bool startStrict = NavMesh.SamplePosition(ws, out NavMeshHit hsStrict, strictRadiusM,     NavMesh.AllAreas);
                bool endStrict   = NavMesh.SamplePosition(we, out NavMeshHit heStrict, strictRadiusM,     NavMesh.AllAreas);
                bool startWide   = NavMesh.SamplePosition(ws, out NavMeshHit hsWide,   functionalRadiusM, NavMesh.AllAreas);
                bool endWide     = NavMesh.SamplePosition(we, out NavMeshHit heWide,   functionalRadiusM, NavMesh.AllAreas);

                if (startStrict && endStrict)
                {
                    ok++;
                    Debug.Log($"[GlobalBaker] ✅ Link '{link.gameObject.name}': OK");
                }
                else if (startWide && endWide)
                {
                    warn++;
                    Debug.LogWarning($"[GlobalBaker] ⚠️ Link '{link.gameObject.name}': funcional " +
                                     $"(dist start={Vector3.Distance(ws, hsWide.position):F3}m, " +
                                     $"end={Vector3.Distance(we, heWide.position):F3}m)");
                }
                else
                {
                    broken++;
                    NavMesh.SamplePosition(ws, out NavMeshHit hsDiag, 5f, NavMesh.AllAreas);
                    NavMesh.SamplePosition(we, out NavMeshHit heDiag, 5f, NavMesh.AllAreas);
                    Debug.LogError(
                        $"[GlobalBaker] ❌ Link '{link.gameObject.name}' ROTO:\n" +
                        $"  start: {ws:F3} → más cercano NavMesh: {hsDiag.position:F3} " +
                        $"(dist={Vector3.Distance(ws, hsDiag.position):F3}m)\n" +
                        $"  end:   {we:F3} → más cercano NavMesh: {heDiag.position:F3} " +
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

            var path = new NavMeshPath();
            NavMesh.CalculatePath(h0.position, h1.position, NavMesh.AllAreas, path);

            switch (path.status)
            {
                case NavMeshPathStatus.PathComplete:
                    Debug.Log($"[GlobalBaker] ✅ Conectividad entre pisos: COMPLETA ({path.corners.Length} puntos)");
                    break;
                case NavMeshPathStatus.PathPartial:
                    Debug.LogWarning("[GlobalBaker] ⚠️ Conectividad PARCIAL — ajustar endpoints de rampa.");
                    break;
                case NavMeshPathStatus.PathInvalid:
                    Debug.LogError("[GlobalBaker] ❌ Sin conectividad entre pisos. " +
                                   "Verificar NavMeshLinks y que las rampas están en el NavMesh.");
                    break;
            }
        }

        // ─── Logging ─────────────────────────────────────────────────────────

        private static void LogVolumeInfo(Bounds volume, IReadOnlyList<NavigableLevel> levels)
        {
            Debug.Log($"[GlobalBaker] 📊 Volumen: min={volume.min:F2} max={volume.max:F2}");
            for (int i = 0; i < levels.Count; i++)
            {
                var lvl = levels[i];
                bool ok = lvl.MinY >= volume.min.y && lvl.MaxY <= volume.max.y;
                Debug.Log($"  Level {lvl.LevelIndex}: Y=[{lvl.MinY:F2},{lvl.MaxY:F2}] " +
                          $"floor={lvl.FloorHeight:F2} → {(ok ? "✅" : "❌ FUERA DEL VOLUMEN")}");
            }
        }

        private static void LogTriangulationCoverage(NavMeshTriangulation tri, IReadOnlyList<NavigableLevel> levels)
        {
            if (tri.vertices.Length == 0) return;

            float minY = tri.vertices.Min(v => v.y);
            float maxY = tri.vertices.Max(v => v.y);
            Debug.Log($"[GlobalBaker] 📐 NavMesh final: {tri.vertices.Length} verts, Y=[{minY:F2},{maxY:F2}]");

            for (int i = 0; i < levels.Count; i++)
            {
                var lvl = levels[i];
                bool present = tri.vertices.Any(v => v.y >= lvl.FloorHeight - 0.5f && v.y <= lvl.FloorHeight + 0.5f);
                Debug.Log($"  Level {lvl.LevelIndex} (Y={lvl.FloorHeight:F2}): " +
                          $"{(present ? "✅ presente" : "❌ ausente")}");
            }

            if (levels.Count >= 2)
            {
                float f0 = levels[0].FloorHeight;
                float f1 = levels[levels.Count - 1].FloorHeight;
                bool hasRamp = tri.vertices.Any(v => v.y > f0 + 0.1f && v.y < f1 - 0.1f);
                Debug.Log(hasRamp
                    ? "  ✅ Vértices de rampa presentes — rampas incluidas en NavMesh"
                    : "  ⚠️ Sin vértices de rampa — revisa slope del agente o ProceduralRoot");
            }
        }

        // ─── Utilidades ───────────────────────────────────────────────────────

        private static bool IsStairGeometry(GameObject go)
        {
            if (go == null) return false;

            string[] prefixes = { "NavRamp_", "NavLanding_", "NavLink_", "NavRamps_", "OpeningBarrier" };
            foreach (string p in prefixes)
                if (go.name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;

            if (go.CompareTag("StairGeometry")) return true;

            Transform t = go.transform;
            while (t != null)
            {
                if (t.name.StartsWith("NavRamps_", StringComparison.OrdinalIgnoreCase)) return true;
                t = t.parent;
            }

            return false;
        }

        private static float CalculateArea(NavMeshTriangulation tri)
        {
            float area = 0f;
            for (int i = 0; i + 2 < tri.indices.Length; i += 3)
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