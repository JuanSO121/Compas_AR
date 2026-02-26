// NavMeshSerializer.cs  v6.1
//
// ═══════════════════════════════════════════════════════════════════════════
// QUÉ CAMBIA EN v6.1  (respecto a v6.0)
// ═══════════════════════════════════════════════════════════════════════════
//
// PROBLEMA 1 — ESCALERA DESAPARECE AL RESTAURAR
//   La escalera es un plano diagonal cuyos vértices tienen Y entre piso 0 y
//   piso 1. En v6.0 SplitTriangulationByLevel() asignaba cada triángulo al
//   nivel según el centroide Y. Los triángulos diagonales caían en la "zona
//   muerta" entre niveles → se perdían → no había camino continuo entre pisos.
//
//   SOLUCIÓN: Guardar TODO el NavMesh en un único archivo .bin sin dividir
//   por niveles. Al restaurar, se inyecta una sola NavMeshDataInstance con
//   toda la geometría (planos horizontales + rampa diagonal incluidos).
//
// PROBLEMA 2 — EL AGENTE EMPEZABA EN EL NIVEL INCORRECTO
//   v6.0 inyectaba los niveles en orden descendente. El primer SamplePosition
//   encontraba Level 1 en lugar de Level 0, y el agente aparecía en el piso
//   equivocado. Con una única instancia, NavigationStartPoint.TeleportToLevel()
//   controla correctamente el spawn en Level 0.
//
// ═══════════════════════════════════════════════════════════════════════════
// ARQUITECTURA v6.1
// ═══════════════════════════════════════════════════════════════════════════
//   • Save()      → escribe navmesh_unified.bin  (TODA la geometría junta)
//   • LoadMulti() → lee el .bin unificado, inyecta UNA NavMeshDataInstance
//   • InjectPermissive: agentHeight=0.001, voxelSize=0.005
//     → el voxelizador no filtra ningún triángulo (ni planos ni rampas)
//   • Compatibilidad de lectura con v5.x / v6.0 (combina los .bin por nivel
//     en memoria antes de inyectar, en lugar de inyectarlos por separado)
//   • LevelSegment se sigue guardando en el header pero solo para diagnóstico
// ═══════════════════════════════════════════════════════════════════════════

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
        public string version   = "6.1";
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

        // v6.1: archivo único unificado
        public string unifiedBinFile;
        public int    unifiedVertexCount;
        public int    unifiedIndexCount;
        public int    unifiedAreaCount;

        // Info por nivel (solo diagnóstico, no se usa en inyección v6.1)
        public List<LevelSegment> levels = new List<LevelSegment>();

        // Campos legacy (v4.x / v5.x / v6.0)
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
        private const string UNIFIED_BIN   = "navmesh_unified.bin";     // v6.1
        private const string LEVEL_BIN_FMT = "navmesh_level_{0}.bin";   // v5.x / v6.0
        private const string LEGACY_BIN    = "navmesh_data.bin";        // v4.x

        private const int   PostBakeSettleMs  = 300;
        private const float LevelClusterGapY  = 0.8f;

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
        // GUARDAR — toda la geometría en un único .bin
        // ═════════════════════════════════════════════════════════════════

        public static async Task<bool> Save(
            Transform modelTransform,
            int       agentTypeID = 0,
            int       levelCount  = 1)
        {
            LastSaveWasSuccessful = false;
            try
            {
                Debug.Log($"[NavMeshSerializer v6.1] ⏳ Esperando {PostBakeSettleMs}ms post-bake...");
                await Task.Delay(PostBakeSettleMs);

                string basePath   = Application.persistentDataPath;
                string headerPath = Path.Combine(basePath, HEADER_FILE);

                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                if (tri.vertices == null || tri.vertices.Length == 0)
                {
                    Debug.LogError("[NavMeshSerializer v6.1] ❌ NavMesh vacío, nada que guardar.");
                    return false;
                }

                CalculateYRange(tri.vertices, out float globalMinY, out float globalMaxY);
                float totalArea = CalculateArea(tri.vertices, tri.indices);

                Debug.Log($"[NavMeshSerializer v6.1] 📐 NavMesh completo: " +
                          $"{tri.vertices.Length} verts, {tri.indices.Length / 3} tris, " +
                          $"Y=[{globalMinY:F2},{globalMaxY:F2}], área={totalArea:F1}m²");

                // Guardar archivo unificado
                string unifiedPath = Path.Combine(basePath, UNIFIED_BIN);
                Vector3[] allV = tri.vertices;
                int[]     allI = tri.indices;
                int[]     allA = tri.areas;

                await Task.Run(() =>
                {
                    using var fs = new FileStream(unifiedPath, FileMode.Create, FileAccess.Write);
                    using var bw = new BinaryWriter(fs);
                    foreach (Vector3 v in allV) { bw.Write(v.x); bw.Write(v.y); bw.Write(v.z); }
                    foreach (int    i in allI)    bw.Write(i);
                    foreach (int    a in allA)    bw.Write(a);
                });

                Debug.Log($"[NavMeshSerializer v6.1] 💾 Guardado: navmesh_unified.bin " +
                          $"({allV.Length} verts, {allI.Length / 3} tris)");

                // Info por nivel (solo diagnóstico)
                List<LevelSegment> levelSegs =
                    BuildLevelSegmentsForDiagnostics(tri, levelCount, globalMinY, globalMaxY);

                NavMeshBuildSettings agentCfg = NavMesh.GetSettingsByID(agentTypeID);

                var header = new NavMeshSaveHeader
                {
                    version            = "6.1",
                    timestamp          = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    agentTypeID        = agentTypeID,
                    agentRadius        = agentCfg.agentRadius,
                    agentHeight        = agentCfg.agentHeight,
                    agentSlope         = agentCfg.agentSlope,
                    agentClimb         = agentCfg.agentClimb,
                    totalVertexCount   = allV.Length,
                    levelCount         = levelCount,
                    totalNavMeshArea   = totalArea,
                    navMeshMinY        = globalMinY,
                    navMeshMaxY        = globalMaxY,
                    unifiedBinFile     = UNIFIED_BIN,
                    unifiedVertexCount = allV.Length,
                    unifiedIndexCount  = allI.Length,
                    unifiedAreaCount   = allA.Length,
                    modelPosition      = modelTransform != null ? modelTransform.position      : Vector3.zero,
                    modelRotation      = modelTransform != null ? modelTransform.rotation      : Quaternion.identity,
                    modelScale         = modelTransform != null ? modelTransform.localScale.x  : 1f,
                    levels             = levelSegs,
                    // Campos legacy para compatibilidad de lectura
                    vertexCount        = allV.Length,
                    indexCount         = allI.Length,
                    areaCount          = allA.Length,
                    navMeshArea        = totalArea,
                };

                await Task.Run(() =>
                    File.WriteAllText(headerPath, JsonUtility.ToJson(header, true)));

                // Limpiar archivos multi-nivel obsoletos
                CleanLegacyFiles(basePath);

                Debug.Log($"[NavMeshSerializer v6.1] ✅ Guardado correctamente. " +
                          $"Escaleras incluidas en el .bin unificado.");
                LastSaveWasSuccessful = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[NavMeshSerializer v6.1] ❌ Error guardando: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // CARGAR
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Restaura el NavMesh guardado. Devuelve una única NavMeshDataInstance
        /// que contiene toda la geometría (Level 0 + escalera + Level 1).
        /// El agente debe hacer spawn en Level 0 vía NavigationStartPoint.
        /// </summary>
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
                    Debug.LogWarning("[NavMeshSerializer v6.1] ⚠️ No hay NavMesh guardado.");
                    return failed;
                }

                if (!File.Exists(headerPath))
                    return await LoadLegacy(currentModelTransform, basePath, legacyBinPath, headerPath);

                string json   = await Task.Run(() => File.ReadAllText(headerPath));
                var    header = JsonUtility.FromJson<NavMeshSaveHeader>(json);

                if (header == null)
                {
                    Debug.LogError("[NavMeshSerializer v6.1] ❌ Header corrupto.");
                    return failed;
                }

                Debug.Log($"[NavMeshSerializer v6.1] 📂 Header v{header.version}: " +
                          $"{header.totalVertexCount} verts, {header.levelCount} nivel(es)");

                // Remap de transformación (si el modelo se movió)
                Matrix4x4? remap = BuildRemap(header, currentModelTransform);

                bool isV61 = header.version == "6.1" &&
                             !string.IsNullOrEmpty(header.unifiedBinFile);

                return isV61
                    ? await LoadUnified(header, basePath, remap)
                    : await LoadMultiLevelCompat(header, basePath, remap);
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[NavMeshSerializer v6.1] ❌ Error cargando: {ex.Message}\n{ex.StackTrace}");
                return (false, default, new List<NavMeshDataInstance>());
            }
        }

        /// <summary>Wrapper de compatibilidad (un solo Instance).</summary>
        public static async Task<(bool success, NavMeshDataInstance instance)> Load(
            Transform currentModelTransform = null)
        {
            var (success, instance, _) = await LoadMulti(currentModelTransform);
            return (success, instance);
        }

        // ── Carga v6.1 — archivo unificado ──────────────────────────────────

        private static async Task<(bool success, NavMeshDataInstance instance,
            List<NavMeshDataInstance> instances)> LoadUnified(
            NavMeshSaveHeader header, string basePath, Matrix4x4? remap)
        {
            var failed = (false, default(NavMeshDataInstance), new List<NavMeshDataInstance>());

            string unifiedPath = Path.Combine(basePath, header.unifiedBinFile);
            if (!File.Exists(unifiedPath))
            {
                Debug.LogError(
                    $"[NavMeshSerializer v6.1] ❌ Archivo unificado no encontrado: {unifiedPath}");
                return failed;
            }

            int vCount = header.unifiedVertexCount;
            int iCount = header.unifiedIndexCount;
            int aCount = header.unifiedAreaCount;

            Debug.Log($"[NavMeshSerializer v6.1] 📂 Leyendo navmesh_unified.bin: " +
                      $"{vCount} verts, {iCount / 3} tris");

            var (verts, idxs, areas) = await ReadBin(unifiedPath, vCount, iCount, aCount);

            if (remap.HasValue) ApplyRemap(remap.Value, verts);

            CalculateYRange(verts, out float minY, out float maxY);
            Debug.Log($"[NavMeshSerializer v6.1] 📐 Datos leídos: Y=[{minY:F2},{maxY:F2}]");

            var inst = await InjectPermissive(
                verts, idxs, areas,
                header.agentTypeID,
                header.navMeshMinY,
                header.navMeshMaxY);

            if (!inst.valid) { Debug.LogError("[NavMeshSerializer v6.1] ❌ Inyección fallida."); return failed; }

            await Task.Yield(); await Task.Yield();

            LogVerification(header.levels);
            return (true, inst, new List<NavMeshDataInstance> { inst });
        }

        // ── Carga v5.x / v6.0 — combina niveles antes de inyectar ──────────
        // (arregla el problema de escaleras entre niveles incluso en saves viejos)

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

            // COMBINAR todos los niveles en un único mesh (evita zona muerta de escaleras)
            var allV = new List<Vector3>();
            var allI = new List<int>();
            var allA = new List<int>();
            float combinedMinY = float.MaxValue, combinedMaxY = float.MinValue;
            bool  anyLoaded    = false;

            // Cargar Level 0 primero, luego los demás en orden ascendente
            foreach (var seg in header.levels.OrderBy(s => s.levelIndex))
            {
                string binPath = Path.Combine(basePath,
                    string.IsNullOrEmpty(seg.binFile)
                        ? string.Format(LEVEL_BIN_FMT, seg.levelIndex)
                        : seg.binFile);

                if (!File.Exists(binPath))
                {
                    Debug.LogWarning($"[NavMeshSerializer v6.1] ⚠️ No encontrado: {binPath}");
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

                Debug.Log($"[NavMeshSerializer v6.1] 📂 Level {seg.levelIndex}: " +
                          $"{verts.Length} verts, Y=[{lMin:F2},{lMax:F2}]");
            }

            if (!anyLoaded) { Debug.LogError("[NavMeshSerializer v6.1] ❌ Sin datos."); return failed; }

            Debug.Log($"[NavMeshSerializer v6.1] 🔗 Combinado: {allV.Count} verts, " +
                      $"Y=[{combinedMinY:F2},{combinedMaxY:F2}]");

            var inst = await InjectPermissive(
                allV.ToArray(), allI.ToArray(), allA.ToArray(),
                header.agentTypeID,
                combinedMinY, combinedMaxY);

            if (!inst.valid) return failed;

            await Task.Yield(); await Task.Yield();

            LogVerification(header.levels);
            return (true, inst, new List<NavMeshDataInstance> { inst });
        }

        // ── Carga legacy v4.x ────────────────────────────────────────────────

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

                Debug.Log($"[NavMeshSerializer v6.1] ✅ Legacy: {verts.Length} verts.");
                return (true, inst, new List<NavMeshDataInstance> { inst });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavMeshSerializer v6.1] ❌ Error legacy: {ex.Message}");
                return (false, default, new List<NavMeshDataInstance>());
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // INYECCIÓN PERMISSIVA
        // ═════════════════════════════════════════════════════════════════
        //
        // El truco: usamos agentHeight = 0.001f
        // El voxelizador de Unity exige que haya `agentHeight` metros de
        // espacio LIBRE encima de cada triángulo para marcarlo walkable.
        // Con agentHeight normal (1.8m), los triángulos de Level 0 y los
        // planos diagonales de la escalera quedan bajo el techo → filtrados.
        // Con agentHeight=0.001, virtualmente ningún triángulo se descarta.
        //
        // El NavMeshAgent aplica sus propias restricciones de movimiento
        // en tiempo real de forma independiente al NavMeshData.

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

            NavMeshBuildSettings s = NavMesh.GetSettingsByID(agentTypeID);
            s.agentHeight       = 0.001f;   // ← no filtrar por altura libre
            s.agentRadius       = 0.001f;   // ← sin erosión de bordes
            s.agentSlope        = 89.9f;    // ← acepta cualquier pendiente (escalera diagonal)
            s.agentClimb        = 99f;      // ← acepta cualquier escalón
            s.overrideVoxelSize = true;
            // voxelSize pequeño = más vóxeles = más detalle = la rampa diagonal queda dentro de al menos un vóxel
            // 0.01f es suficientemente pequeño y 5x más rápido que 0.005f en móvil
            s.voxelSize         = 0.01f;
            s.minRegionArea     = 0f;       // no descartar regiones pequeñas (incluye rampas estrechas)
            s.overrideTileSize  = false;

            // Margen Y generoso: la escalera puede tener vértices en Y=0 (piso 0) hasta Y=3.5 (piso 1)
            // El bounds debe cubrir TODO ese rango + margen para que el voxelizador lo "vea" completo
            float yMargin = 2.0f;   // 2m de margen arriba y abajo → cubre cualquier escalera multi-piso
            float yMin    = dataMinY - yMargin;
            float yMax    = dataMaxY + yMargin;
            float yH      = yMax - yMin;
            float xzPad   = 4f;

            var bounds = new Bounds(
                new Vector3(mb.center.x, (yMin + yMax) * 0.5f, mb.center.z),
                new Vector3(mb.size.x + xzPad, yH, mb.size.z + xzPad));

            Debug.Log($"[NavMeshSerializer v6.1] 🔧 InjectPermissive: " +
                      $"{vertices.Length} verts, {indices.Length / 3} tris, " +
                      $"bounds Y=[{yMin:F2},{yMax:F2}], voxel={s.voxelSize}");

            await Task.Yield();

            NavMeshData data = NavMeshBuilder.BuildNavMeshData(
                s,
                new List<NavMeshBuildSource> { src },
                bounds,
                Vector3.zero,
                Quaternion.identity);

            if (data == null)
            {
                Debug.LogError("[NavMeshSerializer v6.1] ❌ BuildNavMeshData devolvió null.");
                return default;
            }

            await Task.Yield();

            NavMeshDataInstance inst = NavMesh.AddNavMeshData(data);
            if (!inst.valid)
                Debug.LogError("[NavMeshSerializer v6.1] ❌ AddNavMeshData inválido.");
            else
                Debug.Log("[NavMeshSerializer v6.1] ✅ NavMeshDataInstance válida.");

            return inst;
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

            Debug.Log("[NavMeshSerializer v6.1] 🔄 Remap de transformación activo.");
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

            // Clustering Y para info diagnóstica
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

            // Si la escalera crea un Y continuo entre pisos, el clustering detecta
            // menos grupos que pisos. Esto es solo diagnóstico — el .bin unificado
            // siempre guarda TODA la geometría independientemente.
            if (groups.Count < levelCount)
            {
                Debug.LogWarning($"[NavMeshSerializer v6.1] ⚠️ Clustering Y: {groups.Count} grupo(s) " +
                                 $"para {levelCount} nivel(es) — escalera crea rango Y continuo. " +
                                 $"Normal con rampas entre pisos. El .bin unificado está completo.");
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
            Debug.Log($"[NavMeshSerializer v6.1] ✅ Verificación final: " +
                      $"{verify.vertices.Length} verts, Y=[{vMinY:F2},{vMaxY:F2}]");

            if (segments == null || segments.Count <= 1) return;

            foreach (var seg in segments.OrderBy(s => s.levelIndex))
            {
                bool has = verify.vertices.Any(v =>
                    v.y >= seg.minY - 0.5f && v.y <= seg.maxY + 0.5f);
                if (has)
                    Debug.Log($"  ✅ Level {seg.levelIndex}: Y=[{seg.minY:F2},{seg.maxY:F2}]");
                else
                    Debug.LogError(
                        $"  ❌ Level {seg.levelIndex}: SIN geometría en Y=[{seg.minY:F2},{seg.maxY:F2}]");
            }
        }

        private static void CleanLegacyFiles(string basePath)
        {
            for (int i = 0; i < 10; i++)
            {
                string p = Path.Combine(basePath, string.Format(LEVEL_BIN_FMT, i));
                if (File.Exists(p)) { File.Delete(p); Debug.Log($"[NavMeshSerializer v6.1] 🗑️ Eliminado: navmesh_level_{i}.bin"); }
            }
            string legacy = Path.Combine(basePath, LEGACY_BIN);
            if (File.Exists(legacy)) File.Delete(legacy);
        }

        // ─── Utilidades públicas ──────────────────────────────────────────────

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

            foreach (string f in Directory.GetFiles(basePath, "navmesh_*.bin"))   File.Delete(f);
            foreach (string f in Directory.GetFiles(basePath, "navmesh_*.navbin")) File.Delete(f);
            string leg = Path.Combine(basePath, LEGACY_BIN);
            if (File.Exists(leg)) File.Delete(leg);

            Debug.Log("[NavMeshSerializer v6.1] 🗑️ Archivos NavMesh eliminados.");
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
                var h     = JsonUtility.FromJson<NavMeshSaveHeader>(File.ReadAllText(hPath));
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
                sb.AppendLine($"Agente: r={h.agentRadius:F3}m · h={h.agentHeight:F2}m");
                sb.AppendLine($"Formato: {(h.version == "6.1" ? "unificado ✅" : "multi-nivel (compat)")}");
                if (h.levels != null)
                    foreach (var s in h.levels.OrderBy(x => x.levelIndex))
                        sb.AppendLine($"  Level {s.levelIndex} (diag): Y=[{s.minY:F2},{s.maxY:F2}]");
                sb.Append($"Disco: {bytes / 1024} KB");
                return sb.ToString();
            }
            catch { return "Error leyendo info del NavMesh guardado"; }
        }
    }
}