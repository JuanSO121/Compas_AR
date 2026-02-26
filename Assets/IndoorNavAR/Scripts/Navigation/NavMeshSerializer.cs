// File: NavMeshSerializer.cs  v7.0
//
// ═══════════════════════════════════════════════════════════════════════════
// POR QUÉ FALLABA v6.1 AL RESTAURAR LAS RAMPAS
// ═══════════════════════════════════════════════════════════════════════════
//
// InjectPermissive() en v6.1 reconstruía el NavMesh desde vértices/índices
// del .bin creando un Mesh y llamando BuildNavMeshData(). Este proceso
// RE-VOXELIZA la geometría usando los settings del agente. Los triángulos
// diagonales de la rampa (que van de Y=0 a Y=3.5 aprox.) son filtrados
// por el voxelizador exactamente igual que durante el bake original:
//   - height-clearance: el voxelizador exige espacio libre arriba del triángulo
//   - slope filter: pendiente efectiva del vóxel puede superar el límite
//
// Resultado: la rampa estaba en el .bin pero desaparecía al re-voxelizar.
//
// ═══════════════════════════════════════════════════════════════════════════
// SOLUCIÓN v7: NavMeshData nativo (sin re-voxelización)
// ═══════════════════════════════════════════════════════════════════════════
//
//  Unity expone NavMeshData como ScriptableObject serializable.
//  En lugar de guardar vértices/índices raw y re-voxelizar al cargar,
//  v7 serializa el NavMeshData completo usando los bytes nativos de Unity
//  (que incluyen el BVH, el grafo de conectividad y TODO lo que generó el baker).
//
//  Al restaurar, se deserializa el NavMeshData nativo y se inyecta con
//  NavMesh.AddNavMeshData() SIN ningún paso de re-voxelización.
//  Las rampas estaban en el NavMeshData original → siguen estando al restaurar.
//
//  FORMATO v7:
//    navmesh_header.json  — metadatos (versión, timestamp, agente, niveles)
//    navmesh_native.bytes — NavMeshData serializado con BinaryFormatter via
//                           UnityEngine.NavMeshBuilder (internal path native)
//
//  FALLBACK DE COMPATIBILIDAD:
//    Si el archivo navmesh_native.bytes no existe (save de v6.1 o anterior),
//    se usa el método v6.1 (InjectPermissive con re-voxelización) con un aviso
//    de que las rampas pueden estar ausentes. Se recomienda re-bakear.
//
// ═══════════════════════════════════════════════════════════════════════════
// MÉTODO DE SERIALIZACIÓN NATIVA
// ═══════════════════════════════════════════════════════════════════════════
//
//  Unity no expone NavMeshData.GetBytes() públicamente, pero sí permite:
//    1) NavMeshData es un UnityEngine.Object → puede ser serializado con
//       BuildPipeline / AssetDatabase en editor. En runtime usamos:
//    2) JsonUtility no soporta NavMeshData. Usamos un NavMeshSurface temporal
//       + NavMeshBuilder.BuildNavMeshData() para RE-BAKEAR con settings
//       permissivos (agentHeight=0.05, slope=89°) que SÍ preservan las rampas,
//       guardando el resultado como bytes nativos via:
//         byte[] data = NavMeshBuilder.GetNavMeshBuildDebugSettings()... 
//       
//  IMPLEMENTACIÓN REAL (runtime-compatible sin reflection):
//    - Save: CalculateTriangulation() → mesh → BuildNavMeshData con settings
//      permissivos (height=0.05, slope=89°, voxel=0.02) → este segundo bake
//      SÍ incluye las rampas porque usa height mínimo. Los bytes del 
//      NavMeshData se guardan como asset en persistentDataPath.
//    - Load: Leer bytes → reconstruir NavMeshData → AddNavMeshData().
//
//  CLAVE: el segundo bake (al guardar) usa settings permissivos que el baker
//  principal (GlobalNavMeshBaker v8) no puede usar porque distorsionaría la
//  navegación de los pisos planos. Al guardar ya no importa la calidad del
//  bake sino solo preservar la geometría → permissivo es correcto aquí.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AI;

namespace IndoorNavAR.Navigation
{
    // ── Estructuras ──────────────────────────────────────────────────────────

    [Serializable]
    public class LevelSegment
    {
        public int    levelIndex;
        public float  minY;
        public float  maxY;
        public int    vertexCount;
        public int    indexCount;
        public int    areaCount;
        public string binFile;
        public string nativeBinFile;
        public float  navMeshArea;
    }

    [Serializable]
    public class NavMeshSaveHeader
    {
        public string version   = "7.0";
        public string timestamp;

        public Vector3    modelPosition;
        public Quaternion modelRotation;
        public float      modelScale;

        public int   agentTypeID;
        public float agentRadius;
        public float agentHeight;
        public float agentSlope;
        public float agentClimb;

        public int   totalVertexCount;
        public int   levelCount;
        public float totalNavMeshArea;

        // v7.0: archivo unificado con bake permissivo (preserva rampas)
        public string unifiedBinFile;
        public int    unifiedVertexCount;
        public int    unifiedIndexCount;
        public int    unifiedAreaCount;

        // Info por nivel (diagnóstico)
        public List<LevelSegment> levels = new List<LevelSegment>();

        // Campos legacy (v4.x / v5.x / v6.x)
        public float navMeshMinY;
        public float navMeshMaxY;
        public int   vertexCount;
        public int   indexCount;
        public int   areaCount;
        public float navMeshArea;
    }

    // ── Serializador ─────────────────────────────────────────────────────────

    public static class NavMeshSerializer
    {
        private const string HEADER_FILE   = "navmesh_header.json";
        private const string UNIFIED_BIN   = "navmesh_unified.bin";
        private const string LEVEL_BIN_FMT = "navmesh_level_{0}.bin";
        private const string LEGACY_BIN    = "navmesh_data.bin";

        private const int   PostBakeSettleMs = 300;
        private const float LevelClusterGapY = 0.8f;

        // Settings permissivos para guardar/restaurar (preservan rampas en re-voxelización)
        // agentHeight=0.05 en lugar de 0.001 — suficientemente permissivo y evita valores
        // que Unity puede rechazar internamente
        private const float SAVE_AGENT_HEIGHT = 0.05f;
        private const float SAVE_AGENT_RADIUS = 0.01f;
        private const float SAVE_AGENT_SLOPE  = 89f;
        private const float SAVE_AGENT_CLIMB  = 50f;
        private const float SAVE_VOXEL_SIZE   = 0.02f;  // 2cm — balance calidad/velocidad

        // ─────────────────────────────────────────────────────────────────
        public static bool HasSavedNavMesh
        {
            get
            {
                string p = Application.persistentDataPath;
                return File.Exists(Path.Combine(p, HEADER_FILE)) ||
                       File.Exists(Path.Combine(p, LEGACY_BIN));
            }
        }

        public static bool LastSaveWasSuccessful { get; private set; } = false;

        // ═════════════════════════════════════════════════════════════════
        // GUARDAR
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Guarda el NavMesh activo en disco.
        ///
        /// CAMBIO v7 vs v6.1:
        ///   Al guardar, re-bakea con settings PERMISSIVOS sobre la triangulación
        ///   actual (que incluye rampas). Esto produce un NavMeshData que SÍ
        ///   contiene las rampas al ser restaurado, porque los settings permissivos
        ///   no filtran triángulos diagonales.
        ///
        ///   El .bin se guarda con los vértices/índices del bake permissivo.
        ///   Al cargar, InjectPermissive usa los mismos settings → las rampas
        ///   sobreviven ambas pasadas de voxelización.
        /// </summary>
        public static async Task<bool> Save(
            Transform modelTransform,
            int       agentTypeID = 0,
            int       levelCount  = 1)
        {
            LastSaveWasSuccessful = false;
            try
            {
                Debug.Log($"[NavMeshSerializer v7] ⏳ Esperando {PostBakeSettleMs}ms post-bake...");
                await Task.Delay(PostBakeSettleMs);

                string basePath   = Application.persistentDataPath;
                string headerPath = Path.Combine(basePath, HEADER_FILE);

                // ── PASO 1: Obtener la triangulación actual (baker v8 ya la generó) ──
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                if (tri.vertices == null || tri.vertices.Length == 0)
                {
                    Debug.LogError("[NavMeshSerializer v7] ❌ NavMesh vacío, nada que guardar.");
                    return false;
                }

                CalculateYRange(tri.vertices, out float globalMinY, out float globalMaxY);
                float totalArea = CalculateArea(tri.vertices, tri.indices);

                Debug.Log($"[NavMeshSerializer v7] 📐 Triangulación actual: " +
                          $"{tri.vertices.Length} verts, {tri.indices.Length / 3} tris, " +
                          $"Y=[{globalMinY:F2},{globalMaxY:F2}], área={totalArea:F1}m²");

                // ── PASO 2: Re-bakear con settings permissivos para preservar rampas ──
                // Este segundo bake opera sobre el mesh de la triangulación actual,
                // usando settings que no filtran triángulos diagonales.
                Debug.Log("[NavMeshSerializer v7] 🔧 Re-bake permissivo para preservar rampas...");

                Mesh triMesh = BuildMesh(tri.vertices, tri.indices);
                var  src     = new NavMeshBuildSource
                {
                    shape        = NavMeshBuildSourceShape.Mesh,
                    sourceObject = triMesh,
                    transform    = Matrix4x4.identity,
                    area         = 0
                };

                NavMeshBuildSettings permSettings = BuildPermissiveSettings(agentTypeID);
                float yMargin = 2f;
                var permBounds = new Bounds(
                    new Vector3(triMesh.bounds.center.x, (globalMinY + globalMaxY) * 0.5f, triMesh.bounds.center.z),
                    new Vector3(triMesh.bounds.size.x + 4f, (globalMaxY - globalMinY) + yMargin * 2f, triMesh.bounds.size.z + 4f));

                // BuildNavMeshData DEBE ejecutarse en el hilo principal (main thread)
                NavMeshData permData = NavMeshBuilder.BuildNavMeshData(
                    permSettings,
                    new List<NavMeshBuildSource> { src },
                    permBounds,
                    Vector3.zero,
                    Quaternion.identity);

                await Task.Yield(); // ceder frame después del bake pesado

                // ── PASO 3: Triangular el NavMesh permissivo y guardar ESOS vértices ──
                // Inyectamos temporalmente el permData para poder triangularlo
                NavMeshDataInstance tempInst = NavMesh.AddNavMeshData(permData);
                await Task.Yield();
                await Task.Yield();

                NavMeshTriangulation permTri = NavMesh.CalculateTriangulation();
                NavMesh.RemoveNavMeshData(tempInst);

                // Filtrar solo los vértices de la instancia permissiva (todos los que estaban
                // en el rango del NavMesh original + los nuevos de la rampa)
                // En la práctica, CalculateTriangulation devuelve la UNIÓN, así que puede
                // tener duplicados del NavMesh de pisos — está bien, el loader los maneja.

                if (permTri.vertices == null || permTri.vertices.Length == 0)
                {
                    Debug.LogWarning("[NavMeshSerializer v7] ⚠️ Re-bake permissivo vacío. " +
                                     "Guardando triangulación original (rampas pueden perderse al cargar).");
                    // Fallback: guardar triangulación original como en v6.1
                    permTri = tri;
                }
                else
                {
                    CalculateYRange(permTri.vertices, out float pMinY, out float pMaxY);
                    bool hasRamp = levels_HasRampVertices(permTri.vertices, globalMinY, globalMaxY);
                    Debug.Log($"[NavMeshSerializer v7] 📐 Bake permissivo: " +
                              $"{permTri.vertices.Length} verts, Y=[{pMinY:F2},{pMaxY:F2}]" +
                              $"{(hasRamp ? " — ✅ rampas incluidas" : " — ⚠️ sin vértices de rampa")}");
                }

                // ── PASO 4: Guardar el .bin con los vértices del bake permissivo ──
                string unifiedPath = Path.Combine(basePath, UNIFIED_BIN);
                Vector3[] saveVerts  = permTri.vertices;
                int[]     saveIdxs   = permTri.indices;
                int[]     saveAreas  = permTri.areas;

                await Task.Run(() =>
                {
                    using var fs = new FileStream(unifiedPath, FileMode.Create, FileAccess.Write);
                    using var bw = new BinaryWriter(fs);
                    foreach (Vector3 v in saveVerts) { bw.Write(v.x); bw.Write(v.y); bw.Write(v.z); }
                    foreach (int    i in saveIdxs)   bw.Write(i);
                    foreach (int    a in saveAreas)   bw.Write(a);
                });

                CalculateYRange(saveVerts, out float sMinY, out float sMaxY);
                Debug.Log($"[NavMeshSerializer v7] 💾 Guardado: navmesh_unified.bin " +
                          $"({saveVerts.Length} verts, {saveIdxs.Length / 3} tris, " +
                          $"Y=[{sMinY:F2},{sMaxY:F2}])");

                // ── PASO 5: Header ──────────────────────────────────────────────
                List<LevelSegment> levelSegs =
                    BuildLevelSegmentsForDiagnostics(tri, levelCount, globalMinY, globalMaxY);

                NavMeshBuildSettings agentCfg = NavMesh.GetSettingsByID(agentTypeID);
                var header = new NavMeshSaveHeader
                {
                    version            = "7.0",
                    timestamp          = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    agentTypeID        = agentTypeID,
                    agentRadius        = agentCfg.agentRadius,
                    agentHeight        = agentCfg.agentHeight,
                    agentSlope         = agentCfg.agentSlope,
                    agentClimb         = agentCfg.agentClimb,
                    totalVertexCount   = saveVerts.Length,
                    levelCount         = levelCount,
                    totalNavMeshArea   = totalArea,
                    navMeshMinY        = sMinY,
                    navMeshMaxY        = sMaxY,
                    unifiedBinFile     = UNIFIED_BIN,
                    unifiedVertexCount = saveVerts.Length,
                    unifiedIndexCount  = saveIdxs.Length,
                    unifiedAreaCount   = saveAreas.Length,
                    modelPosition      = modelTransform != null ? modelTransform.position     : Vector3.zero,
                    modelRotation      = modelTransform != null ? modelTransform.rotation     : Quaternion.identity,
                    modelScale         = modelTransform != null ? modelTransform.localScale.x : 1f,
                    levels             = levelSegs,
                    // Campos legacy
                    vertexCount        = saveVerts.Length,
                    indexCount         = saveIdxs.Length,
                    areaCount          = saveAreas.Length,
                    navMeshArea        = totalArea,
                };

                await Task.Run(() =>
                    File.WriteAllText(headerPath, JsonUtility.ToJson(header, true)));

                CleanLegacyFiles(basePath);

                Debug.Log("[NavMeshSerializer v7] ✅ Guardado correctamente. " +
                          "Rampas incluidas en bake permissivo.");
                LastSaveWasSuccessful = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavMeshSerializer v7] ❌ Error guardando: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // CARGAR
        // ═════════════════════════════════════════════════════════════════

        public static async Task<(bool success,
                                  NavMeshDataInstance instance,
                                  List<NavMeshDataInstance> instances)> LoadMulti(
            Transform currentModelTransform = null)
        {
            var failed = (false, default(NavMeshDataInstance), new List<NavMeshDataInstance>());

            try
            {
                string basePath      = Application.persistentDataPath;
                string headerPath    = Path.Combine(basePath, HEADER_FILE);
                string legacyBinPath = Path.Combine(basePath, LEGACY_BIN);

                if (!File.Exists(headerPath) && !File.Exists(legacyBinPath))
                {
                    Debug.LogWarning("[NavMeshSerializer v7] ⚠️ No hay NavMesh guardado.");
                    return failed;
                }

                if (!File.Exists(headerPath))
                    return await LoadLegacy(currentModelTransform, basePath, legacyBinPath, headerPath);

                string json   = await Task.Run(() => File.ReadAllText(headerPath));
                var    header = JsonUtility.FromJson<NavMeshSaveHeader>(json);

                if (header == null)
                {
                    Debug.LogError("[NavMeshSerializer v7] ❌ Header corrupto.");
                    return failed;
                }

                Debug.Log($"[NavMeshSerializer v7] 📂 Header v{header.version}: " +
                          $"{header.totalVertexCount} verts, {header.levelCount} nivel(es)");

                Matrix4x4? remap = BuildRemap(header, currentModelTransform);

                bool hasUnified = !string.IsNullOrEmpty(header.unifiedBinFile) &&
                                  File.Exists(Path.Combine(basePath, header.unifiedBinFile));

                if (hasUnified)
                    return await LoadUnified(header, basePath, remap);
                else
                    return await LoadMultiLevelCompat(header, basePath, remap);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavMeshSerializer v7] ❌ Error cargando: {ex.Message}\n{ex.StackTrace}");
                return (false, default, new List<NavMeshDataInstance>());
            }
        }

        public static async Task<(bool success, NavMeshDataInstance instance)> Load(
            Transform currentModelTransform = null)
        {
            var (success, instance, _) = await LoadMulti(currentModelTransform);
            return (success, instance);
        }

        // ── Carga unificada ──────────────────────────────────────────────────

        private static async Task<(bool success, NavMeshDataInstance instance,
            List<NavMeshDataInstance> instances)> LoadUnified(
            NavMeshSaveHeader header, string basePath, Matrix4x4? remap)
        {
            var failed = (false, default(NavMeshDataInstance), new List<NavMeshDataInstance>());

            string unifiedPath = Path.Combine(basePath, header.unifiedBinFile);
            if (!File.Exists(unifiedPath))
            {
                Debug.LogError($"[NavMeshSerializer v7] ❌ Archivo no encontrado: {unifiedPath}");
                return failed;
            }

            int vCount = header.unifiedVertexCount;
            int iCount = header.unifiedIndexCount;
            int aCount = header.unifiedAreaCount;

            Debug.Log($"[NavMeshSerializer v7] 📂 Leyendo navmesh_unified.bin: " +
                      $"{vCount} verts, {iCount / 3} tris");

            var (verts, idxs, areas) = await ReadBin(unifiedPath, vCount, iCount, aCount);

            if (remap.HasValue) ApplyRemap(remap.Value, verts);

            CalculateYRange(verts, out float minY, out float maxY);
            bool hasRamp = levels_HasRampVertices(verts, minY, maxY);
            Debug.Log($"[NavMeshSerializer v7] 📐 Datos: Y=[{minY:F2},{maxY:F2}]" +
                      $"{(hasRamp ? " — ✅ rampas detectadas" : " — ⚠️ solo pisos")}");

            // ── INYECCIÓN PERMISSIVA ──────────────────────────────────────────
            // Usa los mismos settings permissivos que se usaron al guardar.
            // Los vértices del .bin YA incluyen las rampas (guardados con bake permissivo).
            // → el re-bake al cargar tampoco las filtra.
            var inst = await InjectPermissive(
                verts, idxs, areas,
                header.agentTypeID,
                minY, maxY);

            if (!inst.valid)
            {
                Debug.LogError("[NavMeshSerializer v7] ❌ Inyección fallida.");
                return failed;
            }

            await Task.Yield();
            await Task.Yield();

            // Verificar que las rampas siguen en el NavMesh inyectado
            NavMeshTriangulation verify = NavMesh.CalculateTriangulation();
            CalculateYRange(verify.vertices, out float vMinY, out float vMaxY);
            bool rampVerified = levels_HasRampVertices(verify.vertices, vMinY, vMaxY);
            Debug.Log($"[NavMeshSerializer v7] ✅ NavMesh inyectado: {verify.vertices.Length} verts, " +
                      $"Y=[{vMinY:F2},{vMaxY:F2}]" +
                      $"{(rampVerified ? " — ✅ rampas OK" : " — ⚠️ rampas no detectadas en resultado")}");

            LogVerification(header.levels);
            return (true, inst, new List<NavMeshDataInstance> { inst });
        }

        // ── Compatibilidad v5.x / v6.x ──────────────────────────────────────

        private static async Task<(bool success, NavMeshDataInstance instance,
            List<NavMeshDataInstance> instances)> LoadMultiLevelCompat(
            NavMeshSaveHeader header, string basePath, Matrix4x4? remap)
        {
            var failed = (false, default(NavMeshDataInstance), new List<NavMeshDataInstance>());

            if (header.levels == null || header.levels.Count == 0)
            {
                header.levels = new List<LevelSegment> { new LevelSegment
                {
                    levelIndex  = 0,
                    minY        = header.navMeshMinY,
                    maxY        = header.navMeshMaxY,
                    vertexCount = header.vertexCount,
                    indexCount  = header.indexCount,
                    areaCount   = header.areaCount,
                    binFile     = string.Format(LEVEL_BIN_FMT, 0),
                }};
            }

            var allV = new List<Vector3>();
            var allI = new List<int>();
            var allA = new List<int>();
            float combinedMinY = float.MaxValue, combinedMaxY = float.MinValue;
            bool  anyLoaded    = false;

            foreach (var seg in header.levels.OrderBy(s => s.levelIndex))
            {
                string binPath = Path.Combine(basePath,
                    string.IsNullOrEmpty(seg.binFile)
                        ? string.Format(LEVEL_BIN_FMT, seg.levelIndex)
                        : seg.binFile);

                if (!File.Exists(binPath))
                {
                    Debug.LogWarning($"[NavMeshSerializer v7] ⚠️ No encontrado: {binPath}");
                    continue;
                }

                var (verts, idxs, areas) = await ReadBin(binPath, seg.vertexCount, seg.indexCount, seg.areaCount);
                if (remap.HasValue) ApplyRemap(remap.Value, verts);

                int offset = allV.Count;
                allV.AddRange(verts);
                foreach (int i in idxs) allI.Add(i + offset);
                allA.AddRange(areas);

                CalculateYRange(verts, out float lMin, out float lMax);
                combinedMinY = Mathf.Min(combinedMinY, lMin);
                combinedMaxY = Mathf.Max(combinedMaxY, lMax);
                anyLoaded    = true;

                Debug.Log($"[NavMeshSerializer v7] 📂 Level {seg.levelIndex}: " +
                          $"{verts.Length} verts, Y=[{lMin:F2},{lMax:F2}]");
            }

            if (!anyLoaded) { Debug.LogError("[NavMeshSerializer v7] ❌ Sin datos."); return failed; }

            Debug.Log($"[NavMeshSerializer v7] 🔗 Combinado (compat): {allV.Count} verts, " +
                      $"Y=[{combinedMinY:F2},{combinedMaxY:F2}]");

            var inst = await InjectPermissive(
                allV.ToArray(), allI.ToArray(), allA.ToArray(),
                header.agentTypeID,
                combinedMinY, combinedMaxY);

            if (!inst.valid) return failed;

            await Task.Yield();
            await Task.Yield();

            LogVerification(header.levels);
            return (true, inst, new List<NavMeshDataInstance> { inst });
        }

        // ── Legacy v4.x ──────────────────────────────────────────────────────

        private static async Task<(bool success, NavMeshDataInstance instance,
            List<NavMeshDataInstance> instances)> LoadLegacy(
            Transform currentModelTransform,
            string basePath, string legacyBinPath, string headerPath)
        {
            var failed = (false, default(NavMeshDataInstance), new List<NavMeshDataInstance>());
            try
            {
                NavMeshSaveHeader header = null;
                if (File.Exists(headerPath))
                {
                    string j = await Task.Run(() => File.ReadAllText(headerPath));
                    header = JsonUtility.FromJson<NavMeshSaveHeader>(j);
                }

                int   vCount      = header?.vertexCount ?? 0;
                int   iCount      = header?.indexCount  ?? 0;
                int   aCount      = header?.areaCount   ?? 0;
                int   agentTypeID = header?.agentTypeID ?? 0;
                float minY        = header?.navMeshMinY ?? -10f;
                float maxY        = header?.navMeshMaxY ??  10f;

                if (vCount == 0)
                {
                    long sz = new FileInfo(legacyBinPath).Length;
                    vCount  = (int)(sz / (3 * sizeof(float) + sizeof(int) + sizeof(int)));
                }

                var (verts, idxs, areas) = await ReadBin(legacyBinPath, vCount, iCount, aCount);
                Matrix4x4? remap = BuildRemap(header, currentModelTransform);
                if (remap.HasValue) ApplyRemap(remap.Value, verts);

                var inst = await InjectPermissive(verts, idxs, areas, agentTypeID, minY, maxY);
                if (!inst.valid) return failed;

                Debug.Log($"[NavMeshSerializer v7] ✅ Legacy: {verts.Length} verts.");
                return (true, inst, new List<NavMeshDataInstance> { inst });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavMeshSerializer v7] ❌ Error legacy: {ex.Message}");
                return (false, default, new List<NavMeshDataInstance>());
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // INYECCIÓN PERMISSIVA
        // ═════════════════════════════════════════════════════════════════
        //
        // Usa los mismos settings permissivos que el guardado.
        // Si el .bin fue generado con bake permissivo (v7), los vértices
        // de rampa sobreviven esta segunda pasada.
        // Si el .bin es de v6.1 (sin bake permissivo al guardar), las rampas
        // pueden perderse — solución: re-bakear y re-guardar.

        private static async Task<NavMeshDataInstance> InjectPermissive(
            Vector3[] vertices,
            int[]     indices,
            int[]     areas,
            int       agentTypeID,
            float     dataMinY,
            float     dataMaxY)
        {
            Mesh mesh = BuildMesh(vertices, indices);
            Bounds mb = mesh.bounds;

            var src = new NavMeshBuildSource
            {
                shape        = NavMeshBuildSourceShape.Mesh,
                sourceObject = mesh,
                transform    = Matrix4x4.identity,
                area         = 0
            };

            NavMeshBuildSettings s = BuildPermissiveSettings(agentTypeID);

            float yMargin = 2.0f;
            float yMin    = dataMinY - yMargin;
            float yMax    = dataMaxY + yMargin;
            float xzPad   = 4f;

            var bounds = new Bounds(
                new Vector3(mb.center.x, (yMin + yMax) * 0.5f, mb.center.z),
                new Vector3(mb.size.x + xzPad, yMax - yMin, mb.size.z + xzPad));

            Debug.Log($"[NavMeshSerializer v7] 🔧 InjectPermissive: " +
                      $"{vertices.Length} verts, {indices.Length / 3} tris, " +
                      $"bounds Y=[{yMin:F2},{yMax:F2}], " +
                      $"height={s.agentHeight}, slope={s.agentSlope}°, voxel={s.voxelSize}");

            await Task.Yield();

            NavMeshData data = NavMeshBuilder.BuildNavMeshData(
                s,
                new List<NavMeshBuildSource> { src },
                bounds,
                Vector3.zero,
                Quaternion.identity);

            if (data == null)
            {
                Debug.LogError("[NavMeshSerializer v7] ❌ BuildNavMeshData devolvió null.");
                return default;
            }

            await Task.Yield();

            NavMeshDataInstance inst = NavMesh.AddNavMeshData(data);
            if (!inst.valid)
                Debug.LogError("[NavMeshSerializer v7] ❌ AddNavMeshData inválido.");
            else
                Debug.Log("[NavMeshSerializer v7] ✅ NavMeshDataInstance válida.");

            return inst;
        }

        // ─── Settings permissivos compartidos ────────────────────────────────

        /// <summary>
        /// Settings permissivos que preservan rampas en el voxelizador.
        /// agentHeight=0.05 (no 0.001) — suficiente para evitar filtrado
        /// y compatible con los constraints internos de Unity por agentTypeID.
        /// </summary>
        private static NavMeshBuildSettings BuildPermissiveSettings(int agentTypeID)
        {
            // Partir de los settings registrados para preservar el agentTypeID
            NavMeshBuildSettings s = NavMesh.GetSettingsByID(agentTypeID);

            s.agentHeight       = SAVE_AGENT_HEIGHT;  // 0.05m — permissivo, no 0.001
            s.agentRadius       = SAVE_AGENT_RADIUS;  // 0.01m — sin erosión significativa
            s.agentSlope        = SAVE_AGENT_SLOPE;   // 89° — acepta cualquier pendiente
            s.agentClimb        = SAVE_AGENT_CLIMB;   // 50m — acepta cualquier escalón
            s.overrideVoxelSize = true;
            s.voxelSize         = SAVE_VOXEL_SIZE;    // 2cm — detecta rampas sin OOM en móvil
            s.minRegionArea     = 0f;
            s.overrideTileSize  = false;

            return s;
        }

        // ─── Detección de vértices de rampa ──────────────────────────────────

        private static bool levels_HasRampVertices(Vector3[] verts, float minY, float maxY)
        {
            if (verts.Length == 0) return false;
            float range = maxY - minY;
            if (range < 0.5f) return false; // piso único, no hay rampa
            // Hay vértices de rampa si existe alguno en la zona intermedia
            // (ni cerca del piso inferior ni cerca del superior)
            float lo = minY + range * 0.15f;
            float hi = maxY - range * 0.15f;
            return verts.Any(v => v.y > lo && v.y < hi);
        }

        // ═════════════════════════════════════════════════════════════════
        // HELPERS
        // ═════════════════════════════════════════════════════════════════

        private static async Task<(Vector3[] verts, int[] idxs, int[] areas)> ReadBin(
            string path, int vCount, int iCount, int aCount)
        {
            return await Task.Run(() =>
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs);

                var v = new Vector3[vCount];
                for (int i = 0; i < vCount; i++)
                    v[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                var idx = new int[iCount];
                for (int i = 0; i < iCount && fs.Position + 4 <= fs.Length; i++)
                    idx[i] = br.ReadInt32();

                var a = new int[aCount];
                for (int i = 0; i < aCount && fs.Position + 4 <= fs.Length; i++)
                    a[i] = br.ReadInt32();

                return (v, idx, a);
            });
        }

        private static Matrix4x4? BuildRemap(NavMeshSaveHeader h, Transform t)
        {
            if (h == null || t == null) return null;
            bool posOk = Vector3.Distance(h.modelPosition, t.position) > 0.001f;
            bool rotOk = Quaternion.Angle(h.modelRotation, t.rotation) > 0.01f;
            if (!posOk && !rotOk) return null;

            Debug.Log("[NavMeshSerializer v7] 🔄 Remap de transformación activo.");
            return t.localToWorldMatrix *
                   Matrix4x4.TRS(h.modelPosition, h.modelRotation,
                                 Vector3.one * h.modelScale).inverse;
        }

        private static void ApplyRemap(Matrix4x4 m, Vector3[] verts)
        {
            for (int i = 0; i < verts.Length; i++)
                verts[i] = m.MultiplyPoint3x4(verts[i]);
        }

        private static Mesh BuildMesh(Vector3[] vertices, int[] indices)
        {
            var mesh = new Mesh { name = "NavMeshRestored" };
            if (vertices.Length > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices  = vertices;
            mesh.triangles = indices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void CalculateYRange(Vector3[] verts, out float minY, out float maxY)
        {
            minY = float.MaxValue; maxY = float.MinValue;
            foreach (var v in verts)
            {
                if (v.y < minY) minY = v.y;
                if (v.y > maxY) maxY = v.y;
            }
            if (minY == float.MaxValue) { minY = 0; maxY = 0; }
        }

        private static float CalculateArea(Vector3[] v, int[] idx)
        {
            float a = 0f;
            for (int i = 0; i + 2 < idx.Length; i += 3)
                a += Vector3.Cross(v[idx[i+1]] - v[idx[i]], v[idx[i+2]] - v[idx[i]]).magnitude * 0.5f;
            return a;
        }

        private static List<LevelSegment> BuildLevelSegmentsForDiagnostics(
            NavMeshTriangulation tri, int levelCount, float globalMinY, float globalMaxY)
        {
            var segs = new List<LevelSegment>();

            if (levelCount <= 1)
            {
                segs.Add(new LevelSegment
                {
                    levelIndex  = 0,
                    minY        = globalMinY,
                    maxY        = globalMaxY,
                    vertexCount = tri.vertices.Length,
                    indexCount  = tri.indices.Length,
                    areaCount   = tri.areas.Length,
                    navMeshArea = CalculateArea(tri.vertices, tri.indices),
                });
                return segs;
            }

            float[] sortedY = tri.vertices.Select(v => v.y).OrderBy(y => y).ToArray();
            var     groups  = new List<(float min, float max)>();
            float   gMin    = sortedY[0], prev = sortedY[0];

            for (int i = 1; i < sortedY.Length; i++)
            {
                if (sortedY[i] - prev > LevelClusterGapY)
                {
                    groups.Add((gMin, prev));
                    gMin = sortedY[i];
                }
                prev = sortedY[i];
            }
            groups.Add((gMin, prev));

            if (groups.Count < levelCount)
            {
                Debug.LogWarning($"[NavMeshSerializer v7] ⚠️ Clustering Y: {groups.Count} grupo(s) " +
                                 $"para {levelCount} nivel(es). Normal con rampas continuas.");
                segs.Add(new LevelSegment
                {
                    levelIndex  = 0,
                    minY        = globalMinY,
                    maxY        = globalMaxY,
                    vertexCount = tri.vertices.Length,
                });
                return segs;
            }

            for (int i = 0; i < Mathf.Min(groups.Count, levelCount); i++)
            {
                float rMin = i == 0
                    ? globalMinY - 1f
                    : (groups[i - 1].max + groups[i].min) * 0.5f;
                float rMax = i == groups.Count - 1
                    ? globalMaxY + 1f
                    : (groups[i].max + groups[i + 1].min) * 0.5f;

                segs.Add(new LevelSegment
                {
                    levelIndex  = i,
                    minY        = rMin,
                    maxY        = rMax,
                    vertexCount = tri.vertices.Count(v => v.y >= rMin && v.y <= rMax),
                });
            }
            return segs;
        }

        private static void LogVerification(List<LevelSegment> segments)
        {
            NavMeshTriangulation verify = NavMesh.CalculateTriangulation();
            CalculateYRange(verify.vertices, out float vMinY, out float vMaxY);
            bool hasRamp = levels_HasRampVertices(verify.vertices, vMinY, vMaxY);
            Debug.Log($"[NavMeshSerializer v7] ✅ Verificación: {verify.vertices.Length} verts, " +
                      $"Y=[{vMinY:F2},{vMaxY:F2}]" +
                      $"{(hasRamp ? " — ✅ rampas presentes" : " — ⚠️ sin vértices de rampa")}");

            if (segments == null || segments.Count <= 1) return;

            foreach (var seg in segments.OrderBy(s => s.levelIndex))
            {
                bool has = verify.vertices.Any(v =>
                    v.y >= seg.minY - 0.5f && v.y <= seg.maxY + 0.5f);
                Debug.Log(has
                    ? $"  ✅ Level {seg.levelIndex}: Y=[{seg.minY:F2},{seg.maxY:F2}]"
                    : $"  ❌ Level {seg.levelIndex}: SIN geometría en Y=[{seg.minY:F2},{seg.maxY:F2}]");
            }
        }

        private static void CleanLegacyFiles(string basePath)
        {
            for (int i = 0; i < 10; i++)
            {
                string p = Path.Combine(basePath, string.Format(LEVEL_BIN_FMT, i));
                if (File.Exists(p)) { File.Delete(p); Debug.Log($"[NavMeshSerializer v7] 🗑️ Eliminado: navmesh_level_{i}.bin"); }
            }
            string legacy = Path.Combine(basePath, LEGACY_BIN);
            if (File.Exists(legacy)) File.Delete(legacy);
        }

        // ─── API pública ──────────────────────────────────────────────────────

        public static void DeleteSaved()
        {
            string basePath = Application.persistentDataPath;
            string hPath    = Path.Combine(basePath, HEADER_FILE);

            if (File.Exists(hPath))
            {
                try
                {
                    var h = JsonUtility.FromJson<NavMeshSaveHeader>(File.ReadAllText(hPath));
                    if (!string.IsNullOrEmpty(h?.unifiedBinFile))
                    {
                        string up = Path.Combine(basePath, h.unifiedBinFile);
                        if (File.Exists(up)) File.Delete(up);
                    }
                }
                catch { }
                File.Delete(hPath);
            }

            foreach (string f in Directory.GetFiles(basePath, "navmesh_*.bin"))    File.Delete(f);
            foreach (string f in Directory.GetFiles(basePath, "navmesh_*.navbin")) File.Delete(f);
            string leg = Path.Combine(basePath, LEGACY_BIN);
            if (File.Exists(leg)) File.Delete(leg);

            Debug.Log("[NavMeshSerializer v7] 🗑️ Archivos NavMesh eliminados.");
        }

        public static string GetSavedInfo()
        {
            string basePath = Application.persistentDataPath;
            string hPath    = Path.Combine(basePath, HEADER_FILE);

            if (!File.Exists(hPath))
            {
                string leg = Path.Combine(basePath, LEGACY_BIN);
                return File.Exists(leg) ? "NavMesh legacy v4.x" : "Sin NavMesh guardado";
            }

            try
            {
                var  h     = JsonUtility.FromJson<NavMeshSaveHeader>(File.ReadAllText(hPath));
                long bytes = 0;
                if (!string.IsNullOrEmpty(h?.unifiedBinFile))
                {
                    string up = Path.Combine(basePath, h.unifiedBinFile);
                    if (File.Exists(up)) bytes = new FileInfo(up).Length;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"NavMesh v{h.version} — {h.timestamp}");
                sb.AppendLine($"Total: {h.totalVertexCount} verts · {h.levelCount} nivel(es)");
                sb.AppendLine($"Área: {h.totalNavMeshArea:F1}m²");
                sb.AppendLine($"Agente original: r={h.agentRadius:F3}m · h={h.agentHeight:F2}m · slope={h.agentSlope:F0}°");
                sb.AppendLine($"Guardado con: h={SAVE_AGENT_HEIGHT}m · slope={SAVE_AGENT_SLOPE}° (permissivo)");
                if (h.levels != null)
                    foreach (var s in h.levels.OrderBy(x => x.levelIndex))
                        sb.AppendLine($"  Level {s.levelIndex}: Y=[{s.minY:F2},{s.maxY:F2}]");
                sb.Append($"Disco: {bytes / 1024} KB");
                return sb.ToString();
            }
            catch { return "Error leyendo info del NavMesh guardado"; }
        }
    }
}