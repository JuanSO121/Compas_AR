// File: SecondFloorOpeningGenerator.cs
// ✅ SOLUCIÓN REAL: Corta el mesh del piso geométricamente para crear huecos navegables
// ✅ El enfoque anterior (NavMeshModifier Not Walkable) falla porque el WalkableSurface
//    del nivel bakeado tiene mayor prioridad en el pipeline de Unity NavMesh
//
// ✅ FIX v2 — CalculateStairSystemBounds usa ProceduralRoot
//   PROBLEMA: GetComponentsInChildren<Collider>() buscaba colliders dentro del helper,
//   que es hijo del GLB. Con StairWithLandingHelper FIX 6, los colliders de rampas
//   viven en ProceduralRoot (raíz de escena) → no son hijos del helper →
//   GetComponentsInChildren no los encontraba → caía al fallback 2×3×2m →
//   el hueco en el piso superior tenía tamaño incorrecto.
//   SOLUCIÓN: CalculateStairSystemBounds() accede a stair.ProceduralRoot para
//   obtener los Collider reales de las rampas procedurales.

using UnityEngine;
using Unity.AI.Navigation;
using System.Collections.Generic;
using System.Linq;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// ✅ SOLUCIÓN CORRECTA AL PROBLEMA PRINCIPAL:
    /// En lugar de marcar zonas como "Not Walkable" (que falla porque el WalkableSurface 
    /// tiene mayor prioridad), este generador DESTRUYE y RECONSTRUYE el mesh del piso
    /// superior con agujeros geométricos reales donde están las escaleras.
    /// 
    /// Pipeline correcto:
    ///   1. MultiLevelNavMeshGenerator genera los WalkableSurface planes
    ///   2. SecondFloorOpeningGenerator recorta esos planes ANTES del baking
    ///   3. MultiLevelSurfaceService bakea sobre los planes ya recortados
    /// </summary>
    public class SecondFloorOpeningGenerator : MonoBehaviour
    {
        [Header("🔍 Referencia al Generador")]
        [Tooltip("Referencia al generador de NavMesh multi-nivel")]
        [SerializeField] private MultiLevelNavMeshGenerator _multiLevelGenerator;

        [Header("📐 Configuración de Aberturas")]
        [Tooltip("Margen extra alrededor del bounds de la escalera (metros)")]
        [SerializeField] private float _openingMargin = -0.1f;

        [Tooltip("Resolución del mesh recortado (subdivisions por unidad de medida). Más alto = más preciso pero más vértices.")]
        [SerializeField] [Range(1, 8)] private int _meshResolution = 3;

        [Header("🔒 Colisión de Caída")]
        [Tooltip("Crear colisores invisibles en los bordes del hueco para que el agente no 'caiga' fuera del NavMesh")]
        [SerializeField] private bool _createEdgeBarriers = true;
        [SerializeField] private float _barrierHeight = 0.5f;

        [Header("🎨 Visualización")]
        [SerializeField] private bool _showGizmos = true;
        [SerializeField] private Color _openingColor = new Color(1f, 0.3f, 0f, 0.6f);

        [Header("🐛 Debug")]
        [SerializeField] private bool _logDetailedProcess = true;

        // Track de los floors superiores que hemos procesado
        private readonly List<ProcessedFloor> _processedFloors = new List<ProcessedFloor>();

        #region Estructuras internas

        private class ProcessedFloor
        {
            public GameObject OriginalSurface;
            public GameObject PatchedSurface;
            public List<OpeningBounds> Openings = new List<OpeningBounds>();
            public int LevelIndex;
        }

        private struct OpeningBounds
        {
            public Vector3 Center;
            public Vector3 Size;
            public string StairName;
        }

        #endregion

        #region Unity Lifecycle

        private void OnEnable()
        {
            if (_multiLevelGenerator == null)
                _multiLevelGenerator = GetComponent<MultiLevelNavMeshGenerator>();
        }

        #endregion

        #region Public API

        [ContextMenu("🔨 Generar Huecos en Pisos Superiores")]
        public void GenerateOpeningsForAllStairs()
        {
            Debug.Log("[FloorOpening] 🚀 Generando huecos geométricos en pisos superiores...");
            ClearPreviousOpenings();

            StairWithLandingHelper[] stairs = FindObjectsByType<StairWithLandingHelper>(FindObjectsSortMode.None);

            if (stairs.Length == 0)
            {
                Debug.LogWarning("[FloorOpening] ⚠️ No se encontraron StairWithLandingHelper.");
                return;
            }

            Debug.Log($"[FloorOpening] 🔍 {stairs.Length} escaleras encontradas");

            // ✅ FIX v2: Validar que cada escalera tiene ProceduralRoot antes de continuar
            foreach (var stair in stairs)
            {
                if (stair == null) continue;
                if (stair.ProceduralRoot == null)
                    Debug.LogWarning($"[FloorOpening] ⚠️ '{stair.name}': ProceduralRoot es NULL. " +
                                     $"Ejecutar '🏗️ Crear Escalera' en el StairWithLandingHelper antes de generar huecos.");
            }

            var openingsByFloor = CollectStairOpenings(stairs);

            int success = 0;
            foreach (var kvp in openingsByFloor)
            {
                GameObject floorSurface = kvp.Key;
                List<OpeningBounds> openings = kvp.Value;

                if (PatchFloorSurface(floorSurface, openings))
                {
                    success++;
                    Debug.Log($"[FloorOpening] ✅ Piso '{floorSurface.name}' recortado con {openings.Count} hueco(s)");
                }
            }

            Debug.Log($"[FloorOpening] ✅ {success}/{openingsByFloor.Count} pisos recortados correctamente");
        }

        [ContextMenu("🧹 Limpiar Huecos Generados")]
        public void ClearPreviousOpenings()
        {
            foreach (var pf in _processedFloors)
            {
                if (pf.OriginalSurface != null)
                    pf.OriginalSurface.SetActive(true);

                if (pf.PatchedSurface != null)
                {
                    if (Application.isPlaying) Destroy(pf.PatchedSurface);
                    else DestroyImmediate(pf.PatchedSurface);
                }
            }
            _processedFloors.Clear();
            Debug.Log("[FloorOpening] 🧹 Huecos limpiados, superficies restauradas");
        }

        #endregion

        #region Core Logic

        private Dictionary<GameObject, List<OpeningBounds>> CollectStairOpenings(StairWithLandingHelper[] stairs)
        {
            var result = new Dictionary<GameObject, List<OpeningBounds>>();

            GameObject[] surfaces = FindWalkableSurfaces();

            if (surfaces.Length == 0)
            {
                Debug.LogError("[FloorOpening] ❌ No se encontraron WalkableSurface_Level* en escena. " +
                               "Llama GenerateOpeningsForAllStairs() DESPUÉS de crear los surfaces.");
                return result;
            }

            if (_logDetailedProcess)
                Debug.Log($"[FloorOpening] 📐 WalkableSurfaces encontrados: " +
                          string.Join(", ", surfaces.Select(s => s.name)));

            foreach (StairWithLandingHelper stair in stairs)
            {
                if (stair == null) continue;

                // ✅ FIX v2: CalculateStairSystemBounds ahora usa ProceduralRoot
                Bounds stairBounds = CalculateStairSystemBounds(stair);
                if (stairBounds.size.magnitude < 0.1f)
                {
                    Debug.LogWarning($"[FloorOpening] ⚠️ Bounds inválidos para '{stair.gameObject.name}'");
                    continue;
                }

                float topY = stairBounds.max.y;

                GameObject targetFloor = FindFloorSurfaceAtHeight(surfaces, topY);

                if (targetFloor == null)
                {
                    Debug.LogWarning($"[FloorOpening] ⚠️ No se encontró WalkableSurface en Y≈{topY:F2}m " +
                                     $"para escalera '{stair.gameObject.name}'");
                    continue;
                }

                var opening = new OpeningBounds
                {
                    Center = new Vector3(stairBounds.center.x, topY, stairBounds.center.z),
                    Size   = new Vector3(
                        stairBounds.size.x + _openingMargin * 2f,
                        0.2f,
                        stairBounds.size.z + _openingMargin * 2f),
                    StairName = stair.gameObject.name
                };

                if (!result.ContainsKey(targetFloor))
                    result[targetFloor] = new List<OpeningBounds>();

                result[targetFloor].Add(opening);

                if (_logDetailedProcess)
                    Debug.Log($"[FloorOpening] 📍 Hueco '{stair.gameObject.name}': " +
                              $"center={opening.Center}, size={opening.Size.x:F2}×{opening.Size.z:F2}m " +
                              $"(bounds del ProceduralRoot: {stairBounds.min:F2}→{stairBounds.max:F2})");
            }

            return result;
        }

        private GameObject[] FindWalkableSurfaces()
        {
            return FindObjectsByType<GameObject>(FindObjectsSortMode.None)
                .Where(go => go.name.StartsWith("WalkableSurface_Level") && go.name != "WalkableSurface_Level0")
                .ToArray();
        }

        private GameObject FindFloorSurfaceAtHeight(GameObject[] surfaces, float targetHeight)
        {
            const float tolerance = 0.8f;
            GameObject best = null;
            float bestDiff = float.MaxValue;

            foreach (GameObject s in surfaces)
            {
                float diff = Mathf.Abs(s.transform.position.y - targetHeight);
                if (diff < tolerance && diff < bestDiff)
                {
                    bestDiff = diff;
                    best = s;
                }
            }

            return best;
        }

        private bool PatchFloorSurface(GameObject floorSurface, List<OpeningBounds> openings)
        {
            if (floorSurface == null) return false;

            MeshFilter mf = floorSurface.GetComponent<MeshFilter>();
            MeshCollider mc = floorSurface.GetComponent<MeshCollider>();

            if (mf == null)
            {
                Debug.LogError($"[FloorOpening] ❌ '{floorSurface.name}' no tiene MeshFilter");
                return false;
            }

            var processed = new ProcessedFloor
            {
                OriginalSurface = floorSurface,
                Openings = openings,
                LevelIndex = ExtractLevelIndex(floorSurface.name)
            };

            Bounds originalBounds = mf.sharedMesh != null
                ? mf.sharedMesh.bounds
                : new Bounds(Vector3.zero, new Vector3(20f, 0.01f, 20f));

            float width = originalBounds.size.x;
            float depth = originalBounds.size.z;
            Vector3 floorWorldPos = floorSurface.transform.position;

            Mesh patchedMesh = BuildPatchedMesh(width, depth, openings, floorWorldPos);

            if (patchedMesh == null || patchedMesh.vertexCount == 0)
            {
                Debug.LogError($"[FloorOpening] ❌ Falló construcción de mesh recortado para '{floorSurface.name}'");
                return false;
            }

            mf.sharedMesh = patchedMesh;

            if (mc != null)
                mc.sharedMesh = patchedMesh;

            if (_createEdgeBarriers)
                CreateOpeningBarriers(floorSurface, openings, floorWorldPos);

            _processedFloors.Add(processed);
            return true;
        }

        private Mesh BuildPatchedMesh(float width, float depth, List<OpeningBounds> openings, Vector3 floorWorldCenter)
        {
            int cellsX = Mathf.Max(4, Mathf.RoundToInt(width * _meshResolution));
            int cellsZ = Mathf.Max(4, Mathf.RoundToInt(depth * _meshResolution));

            float cellW = width / cellsX;
            float cellD = depth / cellsZ;

            float halfW = width * 0.5f;
            float halfD = depth * 0.5f;
            float halfT = 0.01f;

            var localOpenings = openings.Select(o => new Rect(
                o.Center.x - floorWorldCenter.x - o.Size.x * 0.5f,
                o.Center.z - floorWorldCenter.z - o.Size.z * 0.5f,
                o.Size.x,
                o.Size.z
            )).ToList();

            var vertices  = new List<Vector3>();
            var triangles = new List<int>();

            for (int iz = 0; iz < cellsZ; iz++)
            {
                for (int ix = 0; ix < cellsX; ix++)
                {
                    float x0 = -halfW + ix * cellW;
                    float x1 = x0 + cellW;
                    float z0 = -halfD + iz * cellD;
                    float z1 = z0 + cellD;

                    float cx = (x0 + x1) * 0.5f;
                    float cz = (z0 + z1) * 0.5f;

                    bool inOpening = localOpenings.Any(rect => rect.Contains(new Vector2(cx, cz)));
                    if (inOpening) continue;

                    int baseIdx = vertices.Count;

                    vertices.Add(new Vector3(x0,  halfT, z0));
                    vertices.Add(new Vector3(x1,  halfT, z0));
                    vertices.Add(new Vector3(x1,  halfT, z1));
                    vertices.Add(new Vector3(x0,  halfT, z1));

                    triangles.Add(baseIdx + 0); triangles.Add(baseIdx + 2); triangles.Add(baseIdx + 1);
                    triangles.Add(baseIdx + 0); triangles.Add(baseIdx + 3); triangles.Add(baseIdx + 2);

                    int baseIdx2 = vertices.Count;
                    vertices.Add(new Vector3(x0, -halfT, z0));
                    vertices.Add(new Vector3(x1, -halfT, z0));
                    vertices.Add(new Vector3(x1, -halfT, z1));
                    vertices.Add(new Vector3(x0, -halfT, z1));

                    triangles.Add(baseIdx2 + 0); triangles.Add(baseIdx2 + 1); triangles.Add(baseIdx2 + 2);
                    triangles.Add(baseIdx2 + 0); triangles.Add(baseIdx2 + 2); triangles.Add(baseIdx2 + 3);
                }
            }

            if (vertices.Count == 0)
            {
                Debug.LogError("[FloorOpening] ❌ El mesh resultante está vacío — el hueco cubre todo el piso.");
                return null;
            }

            Mesh mesh = new Mesh { name = "PatchedFloorMesh" };
            mesh.indexFormat = vertices.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            if (_logDetailedProcess)
                Debug.Log($"[FloorOpening] 📐 Mesh recortado: {vertices.Count} vértices, " +
                          $"{triangles.Count / 3} triángulos, grid {cellsX}×{cellsZ}");

            return mesh;
        }

        private void CreateOpeningBarriers(GameObject parent, List<OpeningBounds> openings, Vector3 floorWorldPos)
        {
            foreach (var opening in openings)
            {
                GameObject barrierRoot = new GameObject($"OpeningBarrier_{opening.StairName}");
                barrierRoot.transform.SetParent(parent.transform);
                barrierRoot.layer = parent.layer;

                CreateBarrierWall(barrierRoot,
                    new Vector3(opening.Center.x - floorWorldPos.x, 0, opening.Center.z - floorWorldPos.z + opening.Size.z * 0.5f),
                    new Vector3(opening.Size.x, _barrierHeight, 0.05f));
                CreateBarrierWall(barrierRoot,
                    new Vector3(opening.Center.x - floorWorldPos.x, 0, opening.Center.z - floorWorldPos.z - opening.Size.z * 0.5f),
                    new Vector3(opening.Size.x, _barrierHeight, 0.05f));
                CreateBarrierWall(barrierRoot,
                    new Vector3(opening.Center.x - floorWorldPos.x + opening.Size.x * 0.5f, 0, opening.Center.z - floorWorldPos.z),
                    new Vector3(0.05f, _barrierHeight, opening.Size.z));
                CreateBarrierWall(barrierRoot,
                    new Vector3(opening.Center.x - floorWorldPos.x - opening.Size.x * 0.5f, 0, opening.Center.z - floorWorldPos.z),
                    new Vector3(0.05f, _barrierHeight, opening.Size.z));
            }
        }

        private void CreateBarrierWall(GameObject parent, Vector3 localPos, Vector3 size)
        {
            GameObject wall = new GameObject("Barrier");
            wall.transform.SetParent(parent.transform);
            wall.transform.localPosition = localPos;
            wall.layer = parent.layer;
            wall.isStatic = true;

            BoxCollider bc = wall.AddComponent<BoxCollider>();
            bc.size = size;
            bc.isTrigger = false;

            var modifier = wall.AddComponent<NavMeshModifier>();
            modifier.overrideArea = true;
            modifier.area = 1; // Not Walkable
            modifier.ignoreFromBuild = false;
        }

        /// <summary>
        /// ✅ FIX v2: Calcula bounds del sistema de escalera usando ProceduralRoot.
        ///
        /// ANTES: GetComponentsInChildren buscaba dentro del helper (árbol del GLB).
        ///   Con FIX 6 de StairWithLandingHelper, los colliders de rampas viven en
        ///   ProceduralRoot (raíz de escena) — no son hijos del helper → no se encontraban.
        ///
        /// AHORA: Accede directamente a stair.ProceduralRoot para obtener los Collider
        ///   y Renderer de las rampas procedurales reales.
        ///
        /// Fallback si ProceduralRoot es null: usa GetComponentsInChildren como antes
        /// (compatibilidad con escaleras que aún no fueron recreadas con FIX 6).
        /// </summary>
        private Bounds CalculateStairSystemBounds(StairWithLandingHelper stair)
        {
            // ✅ FIX v2: Priorizar ProceduralRoot
            if (stair.ProceduralRoot != null)
            {
                Collider[] colliders = stair.ProceduralRoot.GetComponentsInChildren<Collider>(false);

                if (colliders.Length > 0)
                {
                    Bounds combined = colliders[0].bounds;
                    for (int i = 1; i < colliders.Length; i++)
                        combined.Encapsulate(colliders[i].bounds);

                    if (_logDetailedProcess)
                        Debug.Log($"[FloorOpening] 📐 Bounds '{stair.name}' desde ProceduralRoot: " +
                                  $"min={combined.min:F2} max={combined.max:F2} " +
                                  $"({colliders.Length} colliders)");

                    return combined;
                }

                Debug.LogWarning($"[FloorOpening] ⚠️ ProceduralRoot '{stair.ProceduralRoot.name}' " +
                                 $"no tiene Colliders — ¿CreateStairSystem() se ejecutó correctamente?");
            }
            else
            {
                Debug.LogWarning($"[FloorOpening] ⚠️ '{stair.name}': ProceduralRoot es NULL. " +
                                 $"Usando fallback GetComponentsInChildren (puede incluir geometría del GLB).");
            }

            // Fallback: búsqueda en hijos del helper (comportamiento anterior)
            Collider[] fallbackColliders = stair.GetComponentsInChildren<Collider>(false);

            if (fallbackColliders.Length > 0)
            {
                Bounds combined = fallbackColliders[0].bounds;
                for (int i = 1; i < fallbackColliders.Length; i++)
                    combined.Encapsulate(fallbackColliders[i].bounds);
                return combined;
            }

            // Último fallback: bounds estimado desde la posición del helper
            Debug.LogWarning($"[FloorOpening] ⚠️ Sin colliders para '{stair.name}'. " +
                             $"Usando bounds estimado 2×3×2m desde transform.position.");
            return new Bounds(stair.transform.position, new Vector3(2f, 3f, 2f));
        }

        private int ExtractLevelIndex(string surfaceName)
        {
            string suffix = surfaceName.Replace("WalkableSurface_Level", "");
            return int.TryParse(suffix, out int idx) ? idx : -1;
        }

        #endregion

        #region Gizmos

        private void OnDrawGizmos()
        {
            if (!_showGizmos) return;

            foreach (var pf in _processedFloors)
            {
                foreach (var opening in pf.Openings)
                {
                    Gizmos.color = _openingColor;
                    Gizmos.DrawWireCube(opening.Center, opening.Size);

                    Gizmos.color = new Color(1f, 1f, 0f, 0.3f);
                    Gizmos.DrawCube(opening.Center, opening.Size);

#if UNITY_EDITOR
                    UnityEditor.Handles.Label(
                        opening.Center + Vector3.up * 0.4f,
                        $"⬛ Hueco escalera\n{opening.StairName}\n{opening.Size.x:F1}×{opening.Size.z:F1}m",
                        new GUIStyle
                        {
                            normal    = new GUIStyleState { textColor = Color.white },
                            fontSize  = 10,
                            fontStyle = FontStyle.Bold,
                            alignment = TextAnchor.MiddleCenter
                        });
#endif
                }
            }
        }

        #endregion

        #region Context Menu

        [ContextMenu("ℹ️ Estado de Huecos")]
        private void ShowStatus()
        {
            Debug.Log($"[FloorOpening] Pisos procesados: {_processedFloors.Count}");
            foreach (var pf in _processedFloors)
            {
                Debug.Log($"  Nivel {pf.LevelIndex}: {pf.Openings.Count} hueco(s)");
                foreach (var o in pf.Openings)
                    Debug.Log($"    - {o.StairName}: {o.Size.x:F2}×{o.Size.z:F2}m @ {o.Center}");
            }
        }

        [ContextMenu("🔍 Buscar WalkableSurfaces")]
        private void DebugFindSurfaces()
        {
            var surfaces = FindWalkableSurfaces();
            Debug.Log($"[FloorOpening] WalkableSurfaces superiores: {surfaces.Length}");
            foreach (var s in surfaces)
                Debug.Log($"  - {s.name} @ Y={s.transform.position.y:F2}m");
        }

        [ContextMenu("🔍 Debug Bounds de Escaleras")]
        private void DebugStairBounds()
        {
            var stairs = FindObjectsByType<StairWithLandingHelper>(FindObjectsSortMode.None);
            Debug.Log($"[FloorOpening] Escaleras encontradas: {stairs.Length}");
            foreach (var stair in stairs)
            {
                Bounds b = CalculateStairSystemBounds(stair);
                Debug.Log($"  '{stair.name}': ProceduralRoot={stair.ProceduralRoot?.name ?? "NULL"}, " +
                          $"bounds={b.min:F2}→{b.max:F2}, size={b.size:F2}");
            }
        }

        #endregion
    }
}