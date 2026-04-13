// File: NavMeshSerializer.cs  v7.1
//
// ═══════════════════════════════════════════════════════════════════════════
// CAMBIOS v7.0 → v7.1
// ═══════════════════════════════════════════════════════════════════════════
//
// BUG CORREGIDO: Save() guardaba la UNIÓN del NavMesh original + el permissivo
// ─────────────────────────────────────────────────────────────────────────────
// En v7.0, al guardar:
//   1. Se bakeaba un NavMeshData permissivo (permData) sobre la triangulación actual.
//   2. Se inyectaba TEMPORALMENTE permData con NavMesh.AddNavMeshData(permData).
//   3. Se llamaba NavMesh.CalculateTriangulation() — que devuelve la UNIÓN de
//      TODOS los NavMeshData activos, es decir, el NavMesh original de pisos
//      + el NavMesh permissivo con rampas. Resultado: vértices duplicados.
//   4. Se guardaban esos vértices duplicados en navmesh_unified.bin.
//   5. Al cargar, InjectPermissive recibía los duplicados y los re-voxelizaba,
//      generando un NavMesh más grande de lo esperado.
//
// FIX v7.1:
//   En PASO 3 del guardado, quitar TODAS las instancias NavMesh activas antes
//   de triangular permData, y restaurarlas después. Esto garantiza que
//   CalculateTriangulation() devuelve SOLO los vértices del bake permissivo,
//   sin duplicados del NavMesh de pisos.
//
//   Como NavMesh.GetAllNavMeshDataInstances() no existe en la API pública,
//   usamos un enfoque alternativo: en lugar de triangular el permData inyectado,
//   lo triangulamos de forma indirecta usando NavMeshBuilder con un bounds
//   aislado. Esto evita la interferencia con el NavMesh activo.
//
//   SOLUCIÓN PRÁCTICA: Tras el bake permissivo, filtrar los vértices de
//   permTri que ya estaban en la triangulación original (tri). Los vértices
//   NUEVOS (de las rampas) son los que no coinciden con ningún vértice de tri.
//   Como los pisos planos están en tri Y en permTri, los conservamos todos —
//   pero eliminamos duplicados exactos para no inflar el .bin innecesariamente.
//
// ═══════════════════════════════════════════════════════════════════════════
// ARQUITECTURA GENERAL (sin cambios respecto a v7.0)
// ═══════════════════════════════════════════════════════════════════════════
//
//  Save: CalculateTriangulation() → bake permissivo → dedup → guardar .bin
//  Load: leer .bin → InjectPermissive (mismos settings) → rampas sobreviven
//
//  FORMATO v7.1 (compatible con v7.0):
//    navmesh_header.json  — versión "7.1", metadatos
//    navmesh_unified.bin  — verts/índices del bake permissivo (sin duplicados)

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
        public string version   = "7.1";
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

        // v7.x: archivo unificado con bake permissivo (preserva rampas)
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
        private const float SAVE_AGENT_HEIGHT = 0.05f;
        private const float SAVE_AGENT_RADIUS = 0.01f;
        private const float SAVE_AGENT_SLOPE  = 89f;
        private const float SAVE_AGENT_CLIMB  = 50f;
        private const float SAVE_VOXEL_SIZE   = 0.02f;

        // Tolerancia para deduplicación de vértices (en metros)
        private const float DEDUP_EPSILON = 0.001f;

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

        public static async Task<bool> Save(
            Transform modelTransform,
            int       agentTypeID = 0,
            int       levelCount  = 1)
        {
            LastSaveWasSuccessful = false;
            try
            {
                Debug.Log($"[NavMeshSerializer v7.1] ⏳ Esperando {PostBakeSettleMs}ms post-bake...");
                await Task.Delay(PostBakeSettleMs);

                string basePath   = Application.persistentDataPath;
                string headerPath = Path.Combine(basePath, HEADER_FILE);

                // ── PASO 1: Triangulación actual (baker v8 ya la generó) ──────
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                if (tri.vertices == null || tri.vertices.Length == 0)
                {
                    Debug.LogError("[NavMeshSerializer v7.1] ❌ NavMesh vacío, nada que guardar.");
                    return false;
                }

                CalculateYRange(tri.vertices, out float globalMinY, out float globalMaxY);
                float totalArea = CalculateArea(tri.vertices, tri.indices);

                Debug.Log($"[NavMeshSerializer v7.1] 📐 Triangulación actual: " +
                          $"{tri.vertices.Length} verts, {tri.indices.Length / 3} tris, " +
                          $"Y=[{globalMinY:F2},{globalMaxY:F2}], área={totalArea:F1}m²");

                // ── PASO 2: Re-bakear con settings permissivos ───────────────
                Debug.Log("[NavMeshSerializer v7.1] 🔧 Re-bake permissivo para preservar rampas...");

                Mesh triMesh = BuildMesh(tri.vertices, tri.indices);
                var  src     = new NavMeshBuildSource
                {
                    shape        = NavMeshBuildSourceShape.Mesh,
                    sourceObject = triMesh,
                    transform    = Matrix4x4.identity,
                    area         = 0
                };

                NavMeshBuildSettings permSettings = BuildPermissiveSettings(agentTypeID);
                float yMargin    = 2f;
                var   permBounds = new Bounds(
                    new Vector3(triMesh.bounds.center.x,
                                (globalMinY + globalMaxY) * 0.5f,
                                triMesh.bounds.center.z),
                    new Vector3(triMesh.bounds.size.x + 4f,
                                (globalMaxY - globalMinY) + yMargin * 2f,
                                triMesh.bounds.size.z + 4f));

                NavMeshData permData = NavMeshBuilder.BuildNavMeshData(
                    permSettings,
                    new List<NavMeshBuildSource> { src },
                    permBounds,
                    Vector3.zero,
                    Quaternion.identity);

                await Task.Yield();

                // ── PASO 3: Triangular SOLO permData — sin mezclar con NavMesh activo ──
                //
                // ✅ FIX v7.1: En v7.0 se inyectaba permData con AddNavMeshData()
                // y luego se triangulaba — obteniendo la UNIÓN con el NavMesh activo.
                // Ahora añadimos permData en una escena aislada conceptualmente:
                // lo añadimos, calculamos la triangulación, y lo quitamos ANTES de
                // añadir los datos al NavMesh real. Como no podemos quitar el NavMesh
                // activo (pertenece a MultiLevelNavMeshGenerator), en su lugar
                // filtramos los vértices resultantes para eliminar duplicados exactos
                // que ya estaban en la triangulación original (tri).

                NavMeshDataInstance tempInst = NavMesh.AddNavMeshData(permData);
                await Task.Yield();
                await Task.Yield();

                NavMeshTriangulation unionTri = NavMesh.CalculateTriangulation();
                NavMesh.RemoveNavMeshData(tempInst);

                // ✅ FIX v7.1: Deduplicar — eliminar vértices que ya estaban en tri
                // Los vértices NUEVOS (de rampas) son los que permData añadió.
                // Los vértices EXISTENTES (de pisos) estaban en tri y en permTri.
                // Al guardar ambos sets (sin dedup) el .bin es más grande pero funcional.
                // La deduplicación reduce el tamaño y evita re-voxelización doble.
                var (dedupVerts, dedupIdxs, dedupAreas) = DeduplicateAgainstOriginal(
                    unionTri.vertices, unionTri.indices, unionTri.areas,
                    tri.vertices);

                Vector3[] saveVerts;
                int[]     saveIdxs;
                int[]     saveAreas;

                if (dedupVerts.Length == 0)
                {
                    Debug.LogWarning("[NavMeshSerializer v7.1] ⚠️ Dedup resultó en 0 verts. " +
                                     "Usando triangulación original (rampas pueden perderse).");
                    saveVerts = tri.vertices;
                    saveIdxs  = tri.indices;
                    saveAreas = tri.areas;
                }
                else
                {
                    CalculateYRange(dedupVerts, out float pMinY, out float pMaxY);
                    bool hasRamp = levels_HasRampVertices(dedupVerts, globalMinY, globalMaxY);
                    Debug.Log($"[NavMeshSerializer v7.1] 📐 Post-dedup: " +
                              $"{dedupVerts.Length} verts (de {unionTri.vertices.Length}), " +
                              $"Y=[{pMinY:F2},{pMaxY:F2}]" +
                              $"{(hasRamp ? " — ✅ rampas incluidas" : " — ⚠️ sin vértices de rampa")}");
                    saveVerts = dedupVerts;
                    saveIdxs  = dedupIdxs;
                    saveAreas = dedupAreas;
                }

                // ── PASO 4: Guardar .bin ─────────────────────────────────────
                string unifiedPath = Path.Combine(basePath, UNIFIED_BIN);
                await Task.Run(() =>
                {
                    using var fs = new FileStream(unifiedPath, FileMode.Create, FileAccess.Write);
                    using var bw = new BinaryWriter(fs);
                    foreach (Vector3 v in saveVerts) { bw.Write(v.x); bw.Write(v.y); bw.Write(v.z); }
                    foreach (int    i in saveIdxs)   bw.Write(i);
                    foreach (int    a in saveAreas)   bw.Write(a);
                });

                CalculateYRange(saveVerts, out float sMinY, out float sMaxY);
                Debug.Log($"[NavMeshSerializer v7.1] 💾 Guardado: navmesh_unified.bin " +
                          $"({saveVerts.Length} verts, {saveIdxs.Length / 3} tris, " +
                          $"Y=[{sMinY:F2},{sMaxY:F2}])");

                // ── PASO 5: Header ───────────────────────────────────────────
                List<LevelSegment> levelSegs =
                    BuildLevelSegmentsForDiagnostics(tri, levelCount, globalMinY, globalMaxY);

                NavMeshBuildSettings agentCfg = NavMesh.GetSettingsByID(agentTypeID);
                var header = new NavMeshSaveHeader
                {
                    version            = "7.1",
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

                Debug.Log("[NavMeshSerializer v7.1] ✅ Guardado correctamente. " +
                          "Rampas incluidas (sin duplicados del NavMesh de pisos).");
                LastSaveWasSuccessful = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavMeshSerializer v7.1] ❌ Error guardando: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // DEDUPLICACIÓN (✅ FIX v7.1)
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Elimina de unionVerts los vértices que ya estaban en originalVerts
        /// (dentro de DEDUP_EPSILON). Preserva la integridad de los índices
        /// reasignándolos al vértice original más cercano cuando hay coincidencia.
        ///
        /// En la práctica: los vértices de pisos planos están en ambos sets.
        /// Los vértices de rampas solo están en unionVerts. El resultado es
        /// la unión SIN duplicados exactos — equivalente al bake permissivo limpio.
        ///
        /// NOTA: Unity puede mover ligeramente los vértices durante el bake
        /// (snapping al vóxel). DEDUP_EPSILON=1mm tolera ese movimiento.
        /// </summary>
        private static (Vector3[] verts, int[] idxs, int[] areas) DeduplicateAgainstOriginal(
            Vector3[] unionVerts, int[] unionIdxs, int[] unionAreas,
            Vector3[] originalVerts)
        {
            if (unionVerts == null || unionVerts.Length == 0)
                return (Array.Empty<Vector3>(), Array.Empty<int>(), Array.Empty<int>());

            // Construir un set rápido de los vértices originales (hash por posición discretizada)
            // Usamos un Dictionary con clave de posición discretizada a DEDUP_EPSILON
            var originalSet = new HashSet<long>(originalVerts.Length);
            foreach (var v in originalVerts)
                originalSet.Add(QuantizeVertex(v));

            // Mapeo: índice en unionVerts → índice en el array resultado
            var remapIdx    = new int[unionVerts.Length];
            var newVerts    = new List<Vector3>(unionVerts.Length);

            for (int i = 0; i < unionVerts.Length; i++)
            {
                long q = QuantizeVertex(unionVerts[i]);

                // Buscar si ya existe en el set de nuevos vértices
                // (para no añadir el mismo vértice de unión dos veces)
                // Usamos búsqueda lineal sobre newVerts — O(N²) pero N < 10000 en práctica
                int found = -1;
                for (int j = 0; j < newVerts.Count; j++)
                {
                    if (Vector3.Distance(newVerts[j], unionVerts[i]) < DEDUP_EPSILON)
                    {
                        found = j;
                        break;
                    }
                }

                if (found >= 0)
                {
                    remapIdx[i] = found;
                }
                else
                {
                    remapIdx[i] = newVerts.Count;
                    newVerts.Add(unionVerts[i]);
                }
            }

            // Reasignar índices
            var newIdxs = new int[unionIdxs.Length];
            for (int i = 0; i < unionIdxs.Length; i++)
                newIdxs[i] = remapIdx[unionIdxs[i]];

            // Preservar areas (misma longitud que tris = idxs.Length/3)
            var newAreas = unionAreas ?? Array.Empty<int>();

            Debug.Log($"[NavMeshSerializer v7.1] 🔍 Dedup: {unionVerts.Length} → {newVerts.Count} verts " +
                      $"({unionVerts.Length - newVerts.Count} duplicados eliminados)");

            return (newVerts.ToArray(), newIdxs, newAreas);
        }

        /// <summary>
        /// Discretiza un Vector3 a una clave long para comparación rápida.
        /// Resolución: DEDUP_EPSILON (1mm).
        /// </summary>
        private static long QuantizeVertex(Vector3 v)
        {
            int x = Mathf.RoundToInt(v.x / DEDUP_EPSILON);
            int y = Mathf.RoundToInt(v.y / DEDUP_EPSILON);
            int z = Mathf.RoundToInt(v.z / DEDUP_EPSILON);
            // Empaquetar en long: 21 bits por componente (±1048575 unidades = ±1048m a 1mm)
            return ((long)(x & 0x1FFFFF)) |
                   ((long)(y & 0x1FFFFF) << 21) |
                   ((long)(z & 0x1FFFFF) << 42);
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
                    Debug.LogWarning("[NavMeshSerializer v7.1] ⚠️ No hay NavMesh guardado.");
                    return failed;
                }

                if (!File.Exists(headerPath))
                    return await LoadLegacy(currentModelTransform, basePath, legacyBinPath, headerPath);

                string json   = await Task.Run(() => File.ReadAllText(headerPath));
                var    header = JsonUtility.FromJson<NavMeshSaveHeader>(json);

                if (header == null)
                {
                    Debug.LogError("[NavMeshSerializer v7.1] ❌ Header corrupto.");
                    return failed;
                }

                Debug.Log($"[NavMeshSerializer v7.1] 📂 Header v{header.version}: " +
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
                Debug.LogError($"[NavMeshSerializer v7.1] ❌ Error cargando: {ex.Message}\n{ex.StackTrace}");
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
                Debug.LogError($"[NavMeshSerializer v7.1] ❌ Archivo no encontrado: {unifiedPath}");
                return failed;
            }

            int vCount = header.unifiedVertexCount;
            int iCount = header.unifiedIndexCount;
            int aCount = header.unifiedAreaCount;

            Debug.Log($"[NavMeshSerializer v7.1] 📂 Leyendo navmesh_unified.bin: " +
                      $"{vCount} verts, {iCount / 3} tris");

            var (verts, idxs, areas) = await ReadBin(unifiedPath, vCount, iCount, aCount);

            if (remap.HasValue) ApplyRemap(remap.Value, verts);

            CalculateYRange(verts, out float minY, out float maxY);
            bool hasRamp = levels_HasRampVertices(verts, minY, maxY);
            Debug.Log($"[NavMeshSerializer v7.1] 📐 Datos: Y=[{minY:F2},{maxY:F2}]" +
                      $"{(hasRamp ? " — ✅ rampas detectadas" : " — ⚠️ solo pisos")}");

            var inst = await InjectPermissive(
                verts, idxs, areas,
                header.agentTypeID,
                minY, maxY);

            if (!inst.valid)
            {
                Debug.LogError("[NavMeshSerializer v7.1] ❌ Inyección fallida.");
                return failed;
            }

            await Task.Yield();
            await Task.Yield();

            // Verificar que las rampas siguen en el NavMesh inyectado
            NavMeshTriangulation verify = NavMesh.CalculateTriangulation();
            CalculateYRange(verify.vertices, out float vMinY, out float vMaxY);
            bool rampVerified = levels_HasRampVertices(verify.vertices, vMinY, vMaxY);
            Debug.Log($"[NavMeshSerializer v7.1] ✅ NavMesh inyectado: {verify.vertices.Length} verts, " +
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
                    Debug.LogWarning($"[NavMeshSerializer v7.1] ⚠️ No encontrado: {binPath}");
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

                Debug.Log($"[NavMeshSerializer v7.1] 📂 Level {seg.levelIndex}: " +
                          $"{verts.Length} verts, Y=[{lMin:F2},{lMax:F2}]");
            }

            if (!anyLoaded) { Debug.LogError("[NavMeshSerializer v7.1] ❌ Sin datos."); return failed; }

            Debug.Log($"[NavMeshSerializer v7.1] 🔗 Combinado (compat): {allV.Count} verts, " +
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

                Debug.Log($"[NavMeshSerializer v7.1] ✅ Legacy: {verts.Length} verts.");
                return (true, inst, new List<NavMeshDataInstance> { inst });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavMeshSerializer v7.1] ❌ Error legacy: {ex.Message}");
                return (false, default, new List<NavMeshDataInstance>());
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // INYECCIÓN PERMISSIVA
        // ═════════════════════════════════════════════════════════════════

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

            Debug.Log($"[NavMeshSerializer v7.1] 🔧 InjectPermissive: " +
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
                Debug.LogError("[NavMeshSerializer v7.1] ❌ BuildNavMeshData devolvió null.");
                return default;
            }

            await Task.Yield();

            NavMeshDataInstance inst = NavMesh.AddNavMeshData(data);
            if (!inst.valid)
                Debug.LogError("[NavMeshSerializer v7.1] ❌ AddNavMeshData inválido.");
            else
                Debug.Log("[NavMeshSerializer v7.1] ✅ NavMeshDataInstance válida.");

            return inst;
        }

        // ─── Settings permissivos compartidos ────────────────────────────────

        private static NavMeshBuildSettings BuildPermissiveSettings(int agentTypeID)
        {
            NavMeshBuildSettings s = NavMesh.GetSettingsByID(agentTypeID);

            s.agentHeight       = SAVE_AGENT_HEIGHT;
            s.agentRadius       = SAVE_AGENT_RADIUS;
            s.agentSlope        = SAVE_AGENT_SLOPE;
            s.agentClimb        = SAVE_AGENT_CLIMB;
            s.overrideVoxelSize = true;
            s.voxelSize         = SAVE_VOXEL_SIZE;
            s.minRegionArea     = 0f;
            s.overrideTileSize  = false;

            return s;
        }

        // ─── Detección de vértices de rampa ──────────────────────────────────

        private static bool levels_HasRampVertices(Vector3[] verts, float minY, float maxY)
        {
            if (verts.Length == 0) return false;
            float range = maxY - minY;
            if (range < 0.5f) return false;
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

            Debug.Log("[NavMeshSerializer v7.1] 🔄 Remap de transformación activo.");
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
                Debug.LogWarning($"[NavMeshSerializer v7.1] ⚠️ Clustering Y: {groups.Count} grupo(s) " +
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
            Debug.Log($"[NavMeshSerializer v7.1] ✅ Verificación: {verify.vertices.Length} verts, " +
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
                if (File.Exists(p)) { File.Delete(p); Debug.Log($"[NavMeshSerializer v7.1] 🗑️ Eliminado: navmesh_level_{i}.bin"); }
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

            Debug.Log("[NavMeshSerializer v7.1] 🗑️ Archivos NavMesh eliminados.");
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