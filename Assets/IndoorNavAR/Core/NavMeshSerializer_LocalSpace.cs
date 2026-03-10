// File: NavMeshSerializer_LocalSpace.cs
// ✅ Extensión del NavMeshSerializer para guardar en espacio LOCAL del modelo.
//
// ============================================================================
//  PROBLEMA QUE RESUELVE
// ============================================================================
//
//  El NavMeshSerializer original guarda vértices en WORLD SPACE.
//  Si Biblioteca_F_V2 se carga en una posición/rotación/escala diferente
//  entre sesiones (lo cual puede ocurrir en AR si el usuario reposiciona
//  el modelo), el NavMesh restaurado queda DESALINEADO respecto al modelo.
//
//  SÍNTOMA: El agente navega correctamente sobre el NavMesh, pero el NavMesh
//  no coincide con la geometría visual del edificio — el agente "flota" o
//  aparece dentro de las paredes.
//
// ============================================================================
//  SOLUCIÓN
// ============================================================================
//
//  Al guardar:
//    1. Capturar el transform del modelo en el momento del bake.
//    2. Convertir todos los vértices de world space a model-local space.
//    3. Guardar los vértices locales + snapshot del transform.
//
//  Al cargar:
//    1. Leer los vértices locales guardados.
//    2. Aplicar el transform ACTUAL del modelo para convertir a world space.
//    3. Construir el NavMeshData con los vértices en world space correcto.
//
//  Si el modelo está en la misma posición → resultado idéntico al original.
//  Si el modelo se movió → remap automático correcto.
//
// ============================================================================
//  INTEGRACIÓN CON TU PROYECTO
// ============================================================================
//
//  Este archivo es una EXTENSIÓN, no reemplaza NavMeshSerializer.
//  Agrega la clase NavMeshSerializerLocalSpace a tu proyecto.
//  En PersistenceManager, usa NavMeshSerializerLocalSpace.SaveLocal() y
//  NavMeshSerializerLocalSpace.LoadLocal() en lugar de los métodos originales
//  cuando el modelo pueda cambiar de transform entre sesiones.
//
//  Si Biblioteca_F_V2 SIEMPRE se carga en la misma posición, el remap
//  resulta en la identidad — sin costo extra, sin cambio de comportamiento.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AI;

namespace IndoorNavAR.Core
{
    /// <summary>
    /// Datos de transform del modelo en el momento del bake.
    /// Se guardan junto al NavMesh para permitir remap al cargar.
    /// </summary>
    [Serializable]
    public class NavMeshBakeTransformSnapshot
    {
        public Vector3    position;
        public Quaternion rotation;
        public Vector3    scale;
        public string     modelName; // Para verificación al cargar

        public Matrix4x4 WorldToLocal =>
            Matrix4x4.TRS(position, rotation, scale).inverse;

        public Matrix4x4 LocalToWorld =>
            Matrix4x4.TRS(position, rotation, scale);

        public static NavMeshBakeTransformSnapshot FromTransform(Transform t, string name = "")
        {
            return new NavMeshBakeTransformSnapshot
            {
                position  = t.position,
                rotation  = t.rotation,
                scale     = t.lossyScale,
                modelName = string.IsNullOrEmpty(name) ? t.name : name
            };
        }
    }

    /// <summary>
    /// Datos del NavMesh guardados en espacio local del modelo.
    /// </summary>
    [Serializable]
    public class NavMeshLocalSpaceData
    {
        public string                      version   = "local_v1";
        public NavMeshBakeTransformSnapshot snapshot;
        public Vector3[]                   vertices;
        public int[]                       indices;
        public int[]                       areas;
        public Bounds                      localBounds;
        public int                         levelCount = 1;
    }

    /// <summary>
    /// Extensión del sistema de serialización del NavMesh con soporte
    /// para guardar/cargar en espacio local del modelo.
    /// </summary>
    public static class NavMeshSerializerLocalSpace
    {
        private static readonly string FileName      = "navmesh_local.json";
        private static readonly string BinFileName   = "navmesh_local.bin";

        private static string FilePath =>
            Path.Combine(Application.persistentDataPath, FileName);

        public static bool HasSavedLocalNavMesh =>
            File.Exists(FilePath);

        // ─── GUARDAR ──────────────────────────────────────────────────────

        /// <summary>
        /// Guarda el NavMesh actual en espacio local del modelo.
        /// Llama este método después de un bake exitoso.
        /// </summary>
        /// <param name="modelTransform">Transform de Biblioteca_F_V2.</param>
        /// <param name="modelName">Nombre identificador del modelo.</param>
        public static async Task<bool> SaveLocal(Transform modelTransform, string modelName = "")
        {
            try
            {
                if (modelTransform == null)
                {
                    Debug.LogError("[NavMeshSerializerLocal] ❌ modelTransform es null.");
                    return false;
                }

                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();

                if (tri.vertices.Length == 0)
                {
                    Debug.LogError("[NavMeshSerializerLocal] ❌ NavMesh vacío — nada que guardar.");
                    return false;
                }

                // Capturar snapshot del transform del modelo
                var snapshot = NavMeshBakeTransformSnapshot.FromTransform(modelTransform, modelName);
                Matrix4x4 worldToLocal = snapshot.WorldToLocal;

                // Convertir vértices de world space a model-local space
                Vector3[] localVertices = new Vector3[tri.vertices.Length];
                for (int i = 0; i < tri.vertices.Length; i++)
                    localVertices[i] = worldToLocal.MultiplyPoint3x4(tri.vertices[i]);

                // Calcular bounds en espacio local
                Bounds localBounds = new Bounds(localVertices[0], Vector3.zero);
                foreach (var v in localVertices)
                    localBounds.Encapsulate(v);

                var data = new NavMeshLocalSpaceData
                {
                    snapshot    = snapshot,
                    vertices    = localVertices,
                    indices     = tri.indices,
                    areas       = tri.areas,
                    localBounds = localBounds
                };

                string json = JsonUtility.ToJson(data, true);
                await Task.Run(() => File.WriteAllText(FilePath, json));

                Debug.Log($"[NavMeshSerializerLocal] ✅ Guardado en espacio local del modelo '{modelName}'.\n" +
                          $"  Vértices: {localVertices.Length} | " +
                          $"  Snapshot: pos={snapshot.position:F3} rot={snapshot.rotation.eulerAngles:F1} " +
                          $"  scale={snapshot.scale:F3}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavMeshSerializerLocal] ❌ Error guardando: {ex.Message}");
                return false;
            }
        }

        // ─── CARGAR ───────────────────────────────────────────────────────

        /// <summary>
        /// Carga el NavMesh y lo remap al transform ACTUAL del modelo.
        /// Si el modelo no cambió de posición, el resultado es idéntico
        /// al NavMesh original. Si cambió, se aplica remap automático.
        /// </summary>
        /// <param name="currentModelTransform">Transform actual de Biblioteca_F_V2.</param>
        public static async Task<(bool success, NavMeshDataInstance instance)>
            LoadLocal(Transform currentModelTransform)
        {
            try
            {
                if (!HasSavedLocalNavMesh)
                {
                    Debug.LogWarning("[NavMeshSerializerLocal] ⚠️ No hay NavMesh local guardado.");
                    return (false, default);
                }

                string json = await Task.Run(() => File.ReadAllText(FilePath));
                var data    = JsonUtility.FromJson<NavMeshLocalSpaceData>(json);

                if (data == null || data.vertices == null || data.vertices.Length == 0)
                {
                    Debug.LogError("[NavMeshSerializerLocal] ❌ Datos inválidos o vacíos.");
                    return (false, default);
                }

                // Calcular la matrix de remap:
                // local → world(bake time) → local → world(current)
                // = currentLocalToWorld * bakeWorldToLocal
                //   (si el modelo no cambió → resulta en identidad)
                Matrix4x4 currentLocalToWorld;
                Matrix4x4 remapMatrix;

                if (currentModelTransform != null)
                {
                    currentLocalToWorld = currentModelTransform.localToWorldMatrix;
                    remapMatrix         = currentLocalToWorld; // vértices ya están en local space
                }
                else
                {
                    // Sin modelo → asumir que world == local (identidad)
                    remapMatrix = Matrix4x4.identity;
                    Debug.LogWarning("[NavMeshSerializerLocal] ⚠️ currentModelTransform es null. " +
                                     "Usando vértices en world space original.");
                }

                // Calcular el desplazamiento si el modelo cambió de posición
                Vector3 positionDelta = currentModelTransform != null
                    ? currentModelTransform.position - data.snapshot.position
                    : Vector3.zero;

                bool modelMoved = positionDelta.magnitude > 0.01f ||
                                  Quaternion.Angle(currentModelTransform != null
                                      ? currentModelTransform.rotation
                                      : Quaternion.identity,
                                      data.snapshot.rotation) > 0.1f;

                if (modelMoved)
                {
                    Debug.Log($"[NavMeshSerializerLocal] 🔄 Modelo se movió desde el bake. " +
                              $"Aplicando remap:\n" +
                              $"  Bake pos: {data.snapshot.position:F3} → " +
                              $"  Current:  {currentModelTransform?.position.ToString("F3") ?? "N/A"}\n" +
                              $"  Delta: {positionDelta:F3} ({positionDelta.magnitude:F3}m)");
                }

                // Convertir vértices de local a world space actual
                Vector3[] worldVertices = new Vector3[data.vertices.Length];
                for (int i = 0; i < data.vertices.Length; i++)
                    worldVertices[i] = remapMatrix.MultiplyPoint3x4(data.vertices[i]);

                // Calcular bounds en world space para el NavMeshData
                Bounds worldBounds = new Bounds(worldVertices[0], Vector3.zero);
                foreach (var v in worldVertices)
                    worldBounds.Encapsulate(v);
                worldBounds.Expand(1f);

                // Construir sources desde los triángulos en world space
                var sources = BuildSourcesFromTriangulation(worldVertices, data.indices);

                if (sources.Count == 0)
                {
                    Debug.LogError("[NavMeshSerializerLocal] ❌ No se pudieron construir fuentes NavMesh.");
                    return (false, default);
                }

                // Settings permisivos para preservar rampas
                NavMeshBuildSettings settings = GetPermissiveSettings();

                NavMeshData navMeshData = NavMeshBuilder.BuildNavMeshData(
                    settings, sources, worldBounds,
                    Vector3.zero, Quaternion.identity);

                if (navMeshData == null)
                {
                    Debug.LogError("[NavMeshSerializerLocal] ❌ BuildNavMeshData retornó null.");
                    return (false, default);
                }

                var instance = NavMesh.AddNavMeshData(navMeshData);

                Debug.Log($"[NavMeshSerializerLocal] ✅ NavMesh restaurado.\n" +
                          $"  Vértices: {worldVertices.Length} | " +
                          $"  Fuentes: {sources.Count} | " +
                          $"  Remap aplicado: {modelMoved}");

                return (true, instance);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavMeshSerializerLocal] ❌ Error cargando: {ex.Message}\n{ex.StackTrace}");
                return (false, default);
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────

        private static List<NavMeshBuildSource> BuildSourcesFromTriangulation(
            Vector3[] vertices, int[] indices)
        {
            var sources = new List<NavMeshBuildSource>();

            if (vertices.Length == 0 || indices.Length == 0)
                return sources;

            // Crear un Mesh con los vértices y triángulos del NavMesh
            var mesh = new Mesh();
            mesh.name      = "RestoredNavMesh";
            mesh.indexFormat = vertices.Length > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.vertices  = vertices;
            mesh.triangles = indices;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();

            sources.Add(new NavMeshBuildSource
            {
                shape        = NavMeshBuildSourceShape.Mesh,
                sourceObject = mesh,
                transform    = Matrix4x4.identity,
                area         = 0 // Walkable
            });

            return sources;
        }

        private static NavMeshBuildSettings GetPermissiveSettings()
        {
            // Intentar obtener los settings del agente por defecto
            NavMeshBuildSettings s = NavMesh.GetSettingsByIndex(0);

            // Settings permisivos para preservar geometría de rampas
            s.agentHeight      = 0.5f;
            s.agentRadius      = 0.1f;
            s.agentSlope       = 75f;
            s.agentClimb       = 0.5f;
            s.minRegionArea    = 0.01f;
            s.overrideVoxelSize = true;
            s.voxelSize        = 0.05f;

            return s;
        }

        // ─── Verificación ────────────────────────────────────────────────

        /// <summary>
        /// Verifica si el modelo actual está en la misma posición que en el momento del bake.
        /// Útil para mostrar advertencias al usuario antes de cargar.
        /// </summary>
        public static bool IsModelAlignedWithBake(Transform currentModelTransform,
                                                   float posTolerance = 0.05f,
                                                   float rotTolerance = 1.0f)
        {
            if (!HasSavedLocalNavMesh || currentModelTransform == null)
                return false;

            try
            {
                string json = File.ReadAllText(FilePath);
                var data    = JsonUtility.FromJson<NavMeshLocalSpaceData>(json);

                if (data?.snapshot == null) return false;

                float posDelta = Vector3.Distance(currentModelTransform.position, data.snapshot.position);
                float rotDelta = Quaternion.Angle(currentModelTransform.rotation, data.snapshot.rotation);

                bool aligned = posDelta <= posTolerance && rotDelta <= rotTolerance;

                if (!aligned)
                    Debug.Log($"[NavMeshSerializerLocal] Modelo desalineado respecto al bake:\n" +
                              $"  ΔPos={posDelta:F3}m (tol={posTolerance}m)\n" +
                              $"  ΔRot={rotDelta:F1}° (tol={rotTolerance}°)");

                return aligned;
            }
            catch
            {
                return false;
            }
        }

        public static void DeleteSaved()
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
                Debug.Log("[NavMeshSerializerLocal] 🗑️ NavMesh local eliminado.");
            }
        }

        public static string GetSavedInfo()
        {
            if (!HasSavedLocalNavMesh) return "Sin NavMesh local guardado.";

            try
            {
                string json = File.ReadAllText(FilePath);
                var data    = JsonUtility.FromJson<NavMeshLocalSpaceData>(json);
                if (data == null) return "Archivo inválido.";

                return $"NavMesh local v{data.version}\n" +
                       $"  Modelo: {data.snapshot?.modelName ?? "?"}\n" +
                       $"  Vértices: {data.vertices?.Length ?? 0}\n" +
                       $"  Triángulos: {(data.indices?.Length ?? 0) / 3}\n" +
                       $"  Bake pos: {data.snapshot?.position.ToString("F3") ?? "?"}\n" +
                       $"  Bake rot: {data.snapshot?.rotation.eulerAngles.ToString("F1") ?? "?"}";
            }
            catch (Exception ex)
            {
                return $"Error leyendo info: {ex.Message}";
            }
        }
    }
}