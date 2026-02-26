// StairWithLandingHelper.cs
//
// ═══════════════════════════════════════════════════════════════════════
// HISTORIAL DE FIXES
// ═══════════════════════════════════════════════════════════════════════
// [FIX 1-5 anteriores se mantienen — ver historial en versión previa]
//
// FIX 6 — RAÍZ PROCEDURAL FUERA DEL GLB + ProceduralRoot público
//   PROBLEMA: CreateRamp() creaba GOs como hijos del helper, que es hijo
//   del modelo GLB (Stairs > StairLeft > StairWithLandingHelper).
//   Aunque el mesh sea procedural (isReadable=true), el GO vive dentro
//   del árbol del GLB → jerarquía confusa, posibles conflictos de nombres
//   con GOs del GLB, y Clear() con Destroy() asíncrono causaba
//   coexistencia temporal.
//   SOLUCIÓN: Todos los GOs procedurales viven en un contenedor raíz
//   separado (_proceduralRoot = "NavRamps_{name}") en la raíz de escena.
//   ProceduralRoot expone ese contenedor para que GlobalNavMeshBaker
//   pueda accederlo directamente sin buscar por tipo.
//
// FIX 7 — DestroyImmediate en Clear() para evitar coexistencia de GOs
//   Destroy() es asíncrono → al llamar CreateStairSystem() en el mismo
//   frame, los GOs anteriores siguen vivos. DestroyImmediate() garantiza
//   limpieza síncrona. Solo se usa para el _proceduralRoot (contenedor
//   separado del GLB, seguro para DestroyImmediate en runtime).
//
// ═══════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

namespace IndoorNavAR.Navigation
{
    public class StairWithLandingHelper : MonoBehaviour
    {
        [Header("Anclaje al Modelo")]
        [SerializeField] private Transform _modelRoot;
        [SerializeField] private bool _autoFindRoot  = true;
        [SerializeField] private bool _createOnStart = false;

        [Header("Nivel inferior y superior")]
        [Tooltip("Índice del NavigationStartPoint que define el piso inferior (normalmente 0)")]
        [SerializeField] private int _lowerLevel = 0;
        [Tooltip("Índice del NavigationStartPoint que define el piso superior (normalmente 1)")]
        [SerializeField] private int _upperLevel = 1;
        [Tooltip("Offset Y aplicado al extremo de la rampa respecto al FloorHeight del StartPoint.\n" +
                 "Negativo = ligeramente por debajo del piso (recomendado: -0.01 a 0).")]
        [SerializeField] private float _rampFloorOffset = 0;

        [Header("Tramo 1 — inferior (coordenadas LOCALES al modelo)")]
        [SerializeField] private Vector3 _t1Start = new(0, 0, 0);
        [SerializeField] private Vector3 _t1End   = new(0, 1.5f, 3f);

        [Header("Descansillo (coordenadas LOCALES al modelo)")]
        [SerializeField] private Vector3 _landingCenter = new(0, 1.5f, 4f);
        [SerializeField] private Vector3 _landingSize   = new(2.5f, 0.05f, 2.5f);
        [SerializeField] private float   _landingRotY   = 0f;

        [Header("Tramo 2 — superior (coordenadas LOCALES al modelo)")]
        [SerializeField] private Vector3 _t2Start = new(0, 1.5f, 5.5f);
        [SerializeField] private Vector3 _t2End   = new(0, 3f, 2f);

        [Header("Geometría")]
        [SerializeField] private float _rampWidth = 1.2f;
        [SerializeField] private bool  _createLanding = true;

        [Header("Subdivisión del Mesh")]
        [SerializeField] [Range(4, 32)] private int _rampSubdivisions = 10;
        [SerializeField] [Range(2, 16)] private int _rampWidthSegments = 4;

        [Header("NavMesh")]
        [SerializeField] private int    _navMeshLayer     = 0;
        [SerializeField] private string _navMeshLayerName = "NavMesh";

        [Header("NavMeshLink (respaldo)")]
        [SerializeField] private bool  _createLinks = true;
        [SerializeField] private float _linkCostMod = 1f;
        [SerializeField] private bool  _linkBidir   = true;

        [Header("Visualización")]
        [SerializeField] private bool  _gizmos       = true;
        [SerializeField] private Color _color1       = Color.yellow;
        [SerializeField] private Color _color2       = Color.cyan;
        [SerializeField] private Color _colorLanding = Color.green;

        // ✅ FIX 6: Raíz procedural separada del GLB
        private GameObject _proceduralRoot;

        // Referencias internas (dentro de _proceduralRoot)
        private GameObject _ramp1;
        private GameObject _ramp2;
        private GameObject _landing;
        private GameObject _link1;
        private GameObject _link2;

        private readonly List<GameObject> _buildOnly = new();

        private float _resolvedLowerY = float.MinValue;
        private float _resolvedUpperY = float.MinValue;

        // ✅ FIX 6: Expone el contenedor procedural para GlobalNavMeshBaker
        /// <summary>
        /// Contenedor raíz de todos los GOs procedurales (rampas, descansillo, links).
        /// Vive en la raíz de escena, NO dentro del árbol del GLB.
        /// GlobalNavMeshBaker debe acceder a este root para recolectar fuentes de bake.
        /// NULL si CreateStairSystem() no ha sido llamado aún.
        /// </summary>
        public GameObject ProceduralRoot => _proceduralRoot;

        // ✅ FIX 5: Layer resuelto en runtime
        private int ResolvedNavMeshLayer
        {
            get
            {
                if (!string.IsNullOrEmpty(_navMeshLayerName))
                {
                    int layerByName = LayerMask.NameToLayer(_navMeshLayerName);
                    if (layerByName >= 0)
                    {
                        if (layerByName != _navMeshLayer)
                            Debug.Log($"[StairHelper] '{name}' Layer resuelto por nombre '{_navMeshLayerName}': " +
                                      $"{layerByName} (Inspector tenía {_navMeshLayer} — usando {layerByName})");
                        return layerByName;
                    }
                }
                return _navMeshLayer;
            }
        }

        public Transform ModelRoot
        {
            get
            {
                if (_modelRoot == null && _autoFindRoot) TryFindRoot();
                return _modelRoot != null ? _modelRoot : transform;
            }
            set => _modelRoot = value;
        }

        private void Awake() { if (_autoFindRoot) TryFindRoot(); }
        private void Start() { if (_createOnStart) CreateStairSystem(); }

        private void TryFindRoot()
        {
            var mgr = FindFirstObjectByType<IndoorNavAR.Core.Managers.ModelLoadManager>();
            if (mgr?.CurrentModel != null) { _modelRoot = mgr.CurrentModel.transform; return; }

            Transform t = transform.parent;
            while (t != null)
            {
                if (t.CompareTag("3DModel") || t.name.StartsWith("Model_"))
                { _modelRoot = t; return; }
                t = t.parent;
            }

            if (transform.parent != null) _modelRoot = transform.parent;
        }

        public void ConnectToModel(Transform model)
        {
            _modelRoot = model;
            if (_ramp1 != null || _ramp2 != null) CreateStairSystem();
        }

        // ─────────────────────────────────────────────────────────────────────
        #region CREAR SISTEMA

        [ContextMenu("🏗️ Crear Escalera")]
        public void CreateStairSystem()
        {
            // ✅ FIX 7: Clear síncrono — usa DestroyImmediate en el contenedor separado
            Clear();

            Transform root = ModelRoot;

            int resolvedLayer = ResolvedNavMeshLayer;
            Debug.Log($"[StairHelper] '{name}' Usando layer {resolvedLayer} " +
                      $"('{LayerMask.LayerToName(resolvedLayer)}') para rampas procedurales.");

            Vector3 t1S = root.TransformPoint(_t1Start);
            Vector3 t1E = root.TransformPoint(_t1End);
            Vector3 lc  = root.TransformPoint(_landingCenter);
            Vector3 t2S = root.TransformPoint(_t2Start);
            Vector3 t2E = root.TransformPoint(_t2End);

            // FIX 1: Y de extremos anclado al NavigationStartPoint de cada nivel
            t1S = SnapYToFloorLevel(t1S, _lowerLevel, "Tramo1 inicio");
            t2E = SnapYToFloorLevel(t2E, _upperLevel, "Tramo2 fin");

            _resolvedLowerY = t1S.y;
            _resolvedUpperY = t2E.y;

            ValidateDescansillo(t1E, lc, t2S);

            // ✅ FIX 6: Crear contenedor raíz FUERA del GLB
            _proceduralRoot = new GameObject($"NavRamps_{name}");
            _proceduralRoot.transform.SetParent(null);   // raíz de escena
            _proceduralRoot.transform.position   = Vector3.zero;
            _proceduralRoot.transform.rotation   = Quaternion.identity;
            _proceduralRoot.transform.localScale = Vector3.one;
            _proceduralRoot.layer = resolvedLayer;

            Debug.Log($"[StairHelper] '{name}' Raíz procedural creada: '{_proceduralRoot.name}' " +
                      $"en raíz de escena (layer={resolvedLayer})");

            _ramp1   = CreateRamp("NavRamp_T1_" + name, t1S, t1E, resolvedLayer);
            _ramp2   = CreateRamp("NavRamp_T2_" + name, t2S, t2E, resolvedLayer);
            if (_createLanding) _landing = CreateLanding("NavLanding_" + name, lc, resolvedLayer);
            if (_createLinks)
            {
                _link1 = CreateLink("NavLink_1_" + name, t1S, t1E);
                _link2 = CreateLink("NavLink_2_" + name, t2S, t2E);
            }

            float s1 = Slope(t1S, t1E);
            float s2 = Slope(t2S, t2E);
            float ps = NavMesh.GetSettingsByIndex(0).agentSlope;

            Debug.Log($"[StairHelper] '{name}' creado:\n" +
                      $"  Tramo1: {t1S:F3} → {t1E:F3} ({s1:F1}°)\n" +
                      $"  Tramo2: {t2S:F3} → {t2E:F3} ({s2:F1}°)\n" +
                      $"  FloorY L{_lowerLevel}={_resolvedLowerY:F3}m  L{_upperLevel}={_resolvedUpperY:F3}m\n" +
                      $"  Layer rampas: {resolvedLayer} ('{LayerMask.LayerToName(resolvedLayer)}')\n" +
                      $"  Subdivisiones: {_rampSubdivisions}×{_rampWidthSegments} " +
                      $"= {_rampSubdivisions * _rampWidthSegments * 2} tris/cara\n" +
                      $"  ProceduralRoot: '{_proceduralRoot.name}' en escena raíz\n" +
                      $"  {(ps >= s1 && ps >= s2 ? "✅ Slope OK" : $"❌ Slope insuficiente — subir a >{Mathf.Max(s1,s2):F0}° en ProjectSettings")}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FIX 1 — Anclar Y al NavigationStartPoint
        private Vector3 SnapYToFloorLevel(Vector3 worldPos, int level, string label)
        {
            if (NavigationStartPointManager.TryGetFloorHeight(level, out float floorY))
            {
                float sy = floorY + _rampFloorOffset;
                Debug.Log($"[StairHelper] '{name}' {label}: Y {worldPos.y:F4}→{sy:F4}m (Manager)");
                return new Vector3(worldPos.x, sy, worldPos.z);
            }

            foreach (var pt in FindObjectsByType<NavigationStartPoint>(FindObjectsSortMode.None))
            {
                if (pt == null || pt.Level != level) continue;
                float sy = pt.transform.position.y + _rampFloorOffset;
                Debug.Log($"[StairHelper] '{name}' {label}: Y {worldPos.y:F4}→{sy:F4}m (FindObjects)");
                return new Vector3(worldPos.x, sy, worldPos.z);
            }

            Debug.LogWarning($"[StairHelper] ⚠️ '{name}' {label}: StartPoint Level{level} no encontrado. Usando Y={worldPos.y:F4}m del inspector.");
            return worldPos;
        }

        private void ValidateDescansillo(Vector3 t1E, Vector3 lc, Vector3 t2S)
        {
            const float maxGap = 0.05f;
            float g1 = Mathf.Abs(t1E.y - lc.y);
            float g2 = Mathf.Abs(lc.y  - t2S.y);
            Debug.Log(g1 > maxGap
                ? $"[StairHelper] ⚠️ Gap T1→Landing: {g1:F4}m — ajustar _t1End.y o _landingCenter.y"
                : $"[StairHelper] ✅ Gap T1→Landing: {g1:F4}m");
            Debug.Log(g2 > maxGap
                ? $"[StairHelper] ⚠️ Gap Landing→T2: {g2:F4}m — ajustar _landingCenter.y o _t2Start.y"
                : $"[StairHelper] ✅ Gap Landing→T2: {g2:F4}m");
        }

        // ✅ FIX 6: Padre es _proceduralRoot (raíz de escena), no el helper del GLB
        // ✅ FIX 3: isStatic = false
        // ✅ FIX 5: layer pasado como parámetro
        private GameObject CreateRamp(string rampName, Vector3 worldStart, Vector3 worldEnd, int layer)
        {
            var go = new GameObject(rampName);
            // ✅ FIX 6: padre = contenedor en raíz de escena, NO dentro del GLB
            go.transform.SetParent(_proceduralRoot.transform);
            go.transform.position = worldStart;
            go.transform.rotation = Quaternion.identity;

            go.layer    = layer;
            go.isStatic = false; // FIX 3

            Mesh mesh = BuildRampMesh(worldStart, worldEnd, _rampWidth, _rampSubdivisions, _rampWidthSegments);

            go.AddComponent<MeshFilter>().sharedMesh   = mesh;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = DebugMat(new Color(0.9f, 0.6f, 0.1f, 0.5f));

            var mod = go.AddComponent<NavMeshModifier>();
            mod.overrideArea    = true;
            mod.area            = 0;
            mod.ignoreFromBuild = false;

            Debug.Log($"[StairHelper] Rampa '{rampName}': {worldStart:F3} → {worldEnd:F3} " +
                      $"parent='{_proceduralRoot.name}', layer={layer}, mesh={mesh.vertexCount}v/{mesh.triangles.Length/3}t, isReadable={mesh.isReadable}");

            return go;
        }

        // FIX 2 + FIX 4: Mesh subdividido con normal correcta (sin cambios)
        private Mesh BuildRampMesh(
            Vector3 worldStart, Vector3 worldEnd,
            float width, int longSteps, int widthSteps)
        {
            Vector3 localEnd = worldEnd - worldStart;
            Vector3 dir      = localEnd.normalized;
            float   length   = localEnd.magnitude;

            Vector3 right = Vector3.Cross(dir, Vector3.up);
            if (right.sqrMagnitude < 0.001f)
                right = Vector3.Cross(dir, Vector3.forward);
            right = right.normalized;

            // FIX 2: normal perpendicular al plano de la rampa
            Vector3 surfNormal = Vector3.Cross(right, dir).normalized;
            if (Vector3.Dot(surfNormal, Vector3.up) < 0f) surfNormal = -surfNormal;

            const float halfT = 0.025f;

            int vertsPerRow = (widthSteps + 1);
            int totalRows   = (longSteps  + 1);
            int topCount    = totalRows * vertsPerRow;

            var verts = new List<Vector3>(topCount * 2);
            var uvs   = new List<Vector2>(topCount * 2);

            for (int li = 0; li <= longSteps; li++)
            {
                float lt = li / (float)longSteps;
                Vector3 basePoint = dir * (lt * length);

                for (int wi = 0; wi <= widthSteps; wi++)
                {
                    float wt   = wi / (float)widthSteps - 0.5f;
                    Vector3 wr = right * (wt * width);

                    verts.Add(basePoint + wr + surfNormal * halfT);
                    uvs.Add(new Vector2(lt, wt + 0.5f));
                }
            }
            for (int li = 0; li <= longSteps; li++)
            {
                float lt = li / (float)longSteps;
                Vector3 basePoint = dir * (lt * length);

                for (int wi = 0; wi <= widthSteps; wi++)
                {
                    float wt   = wi / (float)widthSteps - 0.5f;
                    Vector3 wr = right * (wt * width);

                    verts.Add(basePoint + wr - surfNormal * halfT);
                    uvs.Add(new Vector2(lt, wt + 0.5f));
                }
            }

            var tris = new List<int>();

            for (int li = 0; li < longSteps; li++)
            {
                for (int wi = 0; wi < widthSteps; wi++)
                {
                    int bl = li * vertsPerRow + wi;
                    int br = bl + 1;
                    int tl = bl + vertsPerRow;
                    int tr = tl + 1;
                    tris.Add(bl); tris.Add(tr); tris.Add(br);
                    tris.Add(bl); tris.Add(tl); tris.Add(tr);
                }
            }

            int botOffset = topCount;
            for (int li = 0; li < longSteps; li++)
            {
                for (int wi = 0; wi < widthSteps; wi++)
                {
                    int bl = botOffset + li * vertsPerRow + wi;
                    int br = bl + 1;
                    int tl = bl + vertsPerRow;
                    int tr = tl + 1;
                    tris.Add(bl); tris.Add(br); tris.Add(tr);
                    tris.Add(bl); tris.Add(tr); tris.Add(tl);
                }
            }

            for (int wi = 0; wi < widthSteps; wi++)
            {
                int t0 = wi, t1 = wi + 1;
                int b0 = botOffset + wi, b1 = botOffset + wi + 1;
                tris.Add(t0); tris.Add(b0); tris.Add(b1);
                tris.Add(t0); tris.Add(b1); tris.Add(t1);
            }
            int lastRow = longSteps * vertsPerRow;
            for (int wi = 0; wi < widthSteps; wi++)
            {
                int t0 = lastRow + wi, t1 = lastRow + wi + 1;
                int b0 = botOffset + lastRow + wi, b1 = botOffset + lastRow + wi + 1;
                tris.Add(t0); tris.Add(b1); tris.Add(b0);
                tris.Add(t0); tris.Add(t1); tris.Add(b1);
            }
            for (int li = 0; li < longSteps; li++)
            {
                int t0 = li * vertsPerRow, t1 = (li+1) * vertsPerRow;
                int b0 = botOffset + li * vertsPerRow, b1 = botOffset + (li+1) * vertsPerRow;
                tris.Add(t0); tris.Add(b1); tris.Add(b0);
                tris.Add(t0); tris.Add(t1); tris.Add(b1);
            }
            for (int li = 0; li < longSteps; li++)
            {
                int t0 = li * vertsPerRow + widthSteps, t1 = (li+1) * vertsPerRow + widthSteps;
                int b0 = botOffset + li * vertsPerRow + widthSteps;
                int b1 = botOffset + (li+1) * vertsPerRow + widthSteps;
                tris.Add(t0); tris.Add(b0); tris.Add(b1);
                tris.Add(t0); tris.Add(b1); tris.Add(t1);
            }

            var mesh = new Mesh { name = $"{name}_RampMesh_{System.Guid.NewGuid().ToString("N").Substring(0, 6)}" };
            mesh.vertices  = verts.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.uv        = uvs.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            // Verificación crítica: el mesh procedural DEBE ser readable
            Debug.Assert(mesh.isReadable, $"[StairHelper] ❌ Mesh procedural '{mesh.name}' no es readable!");

            return mesh;
        }

        // ✅ FIX 6: CreateLanding también usa _proceduralRoot como padre
        private GameObject CreateLanding(string landingName, Vector3 worldCenter, int layer)
        {
            var go = new GameObject(landingName);
            go.transform.SetParent(_proceduralRoot.transform);
            go.transform.position = worldCenter;
            go.transform.rotation = ModelRoot.rotation * Quaternion.Euler(0, _landingRotY, 0);
            go.layer    = layer;
            go.isStatic = true;

            Mesh mesh = BuildBoxMesh(_landingSize.x, _landingSize.y, _landingSize.z);
            go.AddComponent<MeshFilter>().sharedMesh   = mesh;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = DebugMat(new Color(0.2f, 0.9f, 0.2f, 0.5f));

            var mod = go.AddComponent<NavMeshModifier>();
            mod.overrideArea    = true;
            mod.area            = 0;
            mod.ignoreFromBuild = false;

            return go;
        }

        // ✅ FIX 6: Links también bajo _proceduralRoot
        private GameObject CreateLink(string linkName, Vector3 worldStart, Vector3 worldEnd)
        {
            var go = new GameObject(linkName);
            go.transform.SetParent(_proceduralRoot.transform);
            go.transform.position = worldStart;

            var link = go.AddComponent<NavMeshLink>();
            link.startPoint    = Vector3.zero;
            link.endPoint      = go.transform.InverseTransformPoint(worldEnd);
            link.width         = _rampWidth;
            link.costModifier  = _linkCostMod;
            link.bidirectional = _linkBidir;
            link.autoUpdate    = true;
            link.area          = 0;

            return go;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Post-Bake

        public void PostBakeCleanup()
        {
            if (_buildOnly.Count == 0) return;
            int n = 0;
            foreach (var go in _buildOnly)
            {
                if (go == null || go == _ramp1 || go == _ramp2 || go == _landing) continue;
                foreach (var col in go.GetComponentsInChildren<Collider>()) { col.enabled = false; n++; }
                foreach (var ren in go.GetComponentsInChildren<Renderer>()) ren.enabled = false;
            }
            if (n > 0) Debug.Log($"[StairHelper] PostBakeCleanup: {n} colliders desactivados");
        }

        public void RegisterBuildOnlyGeometry(GameObject go)
        {
            if (go == null || go == _ramp1 || go == _ramp2 || go == _landing) return;
            if (!_buildOnly.Contains(go)) _buildOnly.Add(go);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region API pública

        public void SetPositions(
            Vector3 t1Start, Vector3 t1End,
            Vector3 landingCenter, Vector3 landingSize,
            Vector3 t2Start, Vector3 t2End,
            float landingRotY = 0f)
        {
            _t1Start       = t1Start;
            _t1End         = t1End;
            _landingCenter = landingCenter;
            _landingSize   = landingSize;
            _t2Start       = t2Start;
            _t2End         = t2End;
            _landingRotY   = landingRotY;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Cleanup

        [ContextMenu("🧹 Limpiar")]
        public void Clear()
        {
            // ✅ FIX 7: DestroyImmediate en el contenedor separado para limpieza síncrona.
            // Es seguro porque _proceduralRoot NO pertenece al GLB importado.
            if (_proceduralRoot != null)
            {
                DestroyImmediate(_proceduralRoot);
                _proceduralRoot = null;
            }

            // Las referencias individuales son nulas después de destruir el padre,
            // pero las limpiamos explícitamente para seguridad.
            _ramp1 = _ramp2 = _landing = _link1 = _link2 = null;

            // _buildOnly puede tener GOs externos — destruir con Destroy normal
            foreach (var g in _buildOnly)
                if (g != null) Destroy(g);
            _buildOnly.Clear();

            _resolvedLowerY = _resolvedUpperY = float.MinValue;
        }

        private void OnDestroy() => Clear();

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Diagnóstico

        [ContextMenu("🔍 Diagnosticar Rampas")]
        public void DiagnoseRamps()
        {
            int resolvedLayer = ResolvedNavMeshLayer;
            Debug.Log($"══ DIAGNÓSTICO '{name}' ══");
            Debug.Log($"  ProceduralRoot: {(_proceduralRoot != null ? $"✅ '{_proceduralRoot.name}' en escena raíz" : "❌ NULL — ejecutar Crear Escalera")}");
            Debug.Log($"  Layer resuelto: {resolvedLayer} ('{LayerMask.LayerToName(resolvedLayer)}') " +
                      $"[Inspector={_navMeshLayer}, Nombre='{_navMeshLayerName}']");

            void Check(GameObject go, string label)
            {
                if (go == null) { Debug.Log($"  {label}: NULL"); return; }

                if (go.GetComponent<NavMeshObstacle>() != null)
                    Debug.LogError($"  ❌ {label}: tiene NavMeshObstacle — eliminar componente");

                if (go.isStatic)
                    Debug.LogWarning($"  ⚠️ {label}: isStatic=true — debería ser false");

                if (go.layer != resolvedLayer)
                    Debug.LogWarning($"  ⚠️ {label}: layer={go.layer} ('{LayerMask.LayerToName(go.layer)}') " +
                                     $"— debería ser {resolvedLayer}");
                else
                    Debug.Log($"  ✅ {label}: layer OK ({go.layer})");

                // Verificar que el parent ES el proceduralRoot (no el GLB)
                if (go.transform.parent != null && _proceduralRoot != null &&
                    go.transform.parent.gameObject != _proceduralRoot)
                    Debug.LogWarning($"  ⚠️ {label}: parent='{go.transform.parent.name}' — esperado '{_proceduralRoot.name}'");
                else
                    Debug.Log($"  ✅ {label}: parent correcto ('{(go.transform.parent != null ? go.transform.parent.name : "root")}')");

                var mf = go.GetComponent<MeshFilter>();
                if (mf?.sharedMesh != null)
                {
                    bool readable = mf.sharedMesh.isReadable;
                    Debug.Log($"  {label}: mesh='{mf.sharedMesh.name}' {mf.sharedMesh.vertexCount}v/" +
                              $"{mf.sharedMesh.triangles.Length/3}t, " +
                              $"isReadable={readable}{(readable ? " ✅" : " ❌ NO READABLE — mesh del GLB infiltrado!")}");
                }

                var col = go.GetComponent<Collider>();
                if (col == null) { Debug.LogError($"  {label}: SIN COLLIDER"); return; }

                Vector3 top = col.bounds.center + Vector3.up * (col.bounds.extents.y + 0.05f);
                bool hit05  = NavMesh.SamplePosition(top, out var h05, 0.5f, NavMesh.AllAreas);
                bool hit3   = NavMesh.SamplePosition(top, out var h3,  3.0f, NavMesh.AllAreas);

                if      (hit05) Debug.Log($"  ✅ {label}: NavMesh a {Vector3.Distance(top, h05.position):F3}m");
                else if (hit3)  Debug.LogError($"  ❌ {label}: NavMesh LEJOS ({Vector3.Distance(top, h3.position):F2}m)");
                else            Debug.LogError($"  ❌ {label}: SIN NavMesh en 3m");
            }

            Check(_ramp1,   "NavRamp_T1");
            Check(_ramp2,   "NavRamp_T2");
            Check(_landing, "NavLanding");

            bool hasL = NavigationStartPointManager.TryGetFloorHeight(_lowerLevel, out float lY);
            bool hasU = NavigationStartPointManager.TryGetFloorHeight(_upperLevel, out float uY);
            Debug.Log($"  StartPoint L{_lowerLevel}: {(hasL ? $"✅ Y={lY:F4}m" : "❌ NO ENCONTRADO")}");
            Debug.Log($"  StartPoint L{_upperLevel}: {(hasU ? $"✅ Y={uY:F4}m" : "❌ NO ENCONTRADO")}");

            Transform r = ModelRoot;
            float s1 = Slope(r.TransformPoint(_t1Start), r.TransformPoint(_t1End));
            float s2 = Slope(r.TransformPoint(_t2Start), r.TransformPoint(_t2End));
            float ps = NavMesh.GetSettingsByIndex(0).agentSlope;
            Debug.Log($"  Pendientes: T1={s1:F1}° T2={s2:F1}° Max={ps}°");
            Debug.Log(ps < s1 || ps < s2
                ? $"  ❌ Slope insuficiente — subir a >{Mathf.Max(s1,s2):F0}° en ProjectSettings"
                : "  ✅ Slope OK");

            Debug.Log("══════════════════════════");
        }

        [ContextMenu("📐 Diagnosticar Endpoints")]
        public void DiagnoseEndpoints()
        {
            Transform r = ModelRoot;
            Vector3 t1S = r.TransformPoint(_t1Start);
            Vector3 t1E = r.TransformPoint(_t1End);
            Vector3 lc  = r.TransformPoint(_landingCenter);
            Vector3 t2S = r.TransformPoint(_t2Start);
            Vector3 t2E = r.TransformPoint(_t2End);

            bool hasL = NavigationStartPointManager.TryGetFloorHeight(_lowerLevel, out float lY);
            bool hasU = NavigationStartPointManager.TryGetFloorHeight(_upperLevel, out float uY);

            if (!hasL || !hasU)
            {
                foreach (var pt in FindObjectsByType<NavigationStartPoint>(FindObjectsSortMode.None))
                {
                    if (pt.Level == _lowerLevel && !hasL) { lY = pt.transform.position.y; hasL = true; }
                    if (pt.Level == _upperLevel && !hasU) { uY = pt.transform.position.y; hasU = true; }
                }
            }

            int resolvedLayer = ResolvedNavMeshLayer;
            Debug.Log($"══ ENDPOINTS '{name}' ══");
            Debug.Log($"  Layer rampas: {resolvedLayer} ('{LayerMask.LayerToName(resolvedLayer)}')");
            Debug.Log($"  SP L{_lowerLevel}: {(hasL ? $"✅ Y={lY:F4}m" : "❌")}  SP L{_upperLevel}: {(hasU ? $"✅ Y={uY:F4}m" : "❌")}");
            Debug.Log($"  Offset: {_rampFloorOffset:F3}m");
            Debug.Log($"  T1 inicio Y real: {(hasL ? lY+_rampFloorOffset : t1S.y):F4}m  (X={t1S.x:F3} Z={t1S.z:F3})");
            Debug.Log($"  T1 fin:   {t1E:F4}");
            Debug.Log($"  Landing:  {lc:F4}");
            Debug.Log($"  T2 inicio:{t2S:F4}");
            Debug.Log($"  T2 fin Y real:  {(hasU ? uY+_rampFloorOffset : t2E.y):F4}m  (X={t2E.x:F3} Z={t2E.z:F3})");
            Debug.Log($"  Gap T1→Land: {Mathf.Abs(t1E.y-lc.y):F4}m   Gap Land→T2: {Mathf.Abs(lc.y-t2S.y):F4}m");
            Debug.Log($"  Pendiente T1: {Slope(t1S,t1E):F1}°  T2: {Slope(t2S,t2E):F1}°");
            Debug.Log($"  Subdivisiones: {_rampSubdivisions}×{_rampWidthSegments}");
            Debug.Log($"  ProceduralRoot: {(_proceduralRoot != null ? _proceduralRoot.name : "NULL")}");
            Debug.Log("══════════════════════════");
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Gizmos

        private void OnDrawGizmos()
        {
            if (!_gizmos) return;
            Transform r = ModelRoot;
            Vector3 t1S = r.TransformPoint(_t1Start);
            Vector3 t1E = r.TransformPoint(_t1End);
            Vector3 lc  = r.TransformPoint(_landingCenter);
            Vector3 t2S = r.TransformPoint(_t2Start);
            Vector3 t2E = r.TransformPoint(_t2End);

            if (_resolvedLowerY > float.MinValue) t1S.y = _resolvedLowerY;
            if (_resolvedUpperY > float.MinValue) t2E.y = _resolvedUpperY;

            Gizmos.color = _color1; DrawArrow(t1S, t1E);
            Gizmos.DrawWireSphere(t1S, 0.1f); Gizmos.DrawWireSphere(t1E, 0.1f);

            Gizmos.color  = _colorLanding;
            Gizmos.matrix = Matrix4x4.TRS(lc, r.rotation * Quaternion.Euler(0, _landingRotY, 0), Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, _landingSize);
            Gizmos.matrix = Matrix4x4.identity;

            Gizmos.color = _color2; DrawArrow(t2S, t2E);
            Gizmos.DrawWireSphere(t2S, 0.1f); Gizmos.DrawWireSphere(t2E, 0.1f);

            Gizmos.color = Color.gray;
            Gizmos.DrawLine(t1E, lc); Gizmos.DrawLine(lc, t2S);

            if (NavigationStartPointManager.TryGetFloorHeight(_lowerLevel, out float lF))
            {
                Gizmos.color = new Color(_color1.r, _color1.g, _color1.b, 0.4f);
                float snapY = lF + _rampFloorOffset;
                Gizmos.DrawLine(new Vector3(t1S.x-1, snapY, t1S.z), new Vector3(t1S.x+1, snapY, t1S.z));
            }
            if (NavigationStartPointManager.TryGetFloorHeight(_upperLevel, out float uF))
            {
                Gizmos.color = new Color(_color2.r, _color2.g, _color2.b, 0.4f);
                float snapY = uF + _rampFloorOffset;
                Gizmos.DrawLine(new Vector3(t2E.x-1, snapY, t2E.z), new Vector3(t2E.x+1, snapY, t2E.z));
            }
        }

        private void OnDrawGizmosSelected()
        {
#if UNITY_EDITOR
            Transform r = ModelRoot;
            void Handle(ref Vector3 local, Color col)
            {
                UnityEditor.Handles.color = col;
                Vector3 w = r.TransformPoint(local);
                Vector3 nw = UnityEditor.Handles.PositionHandle(w, Quaternion.identity);
                if (nw != w) local = r.InverseTransformPoint(nw);
            }
            Handle(ref _t1Start, _color1); Handle(ref _t1End, _color1);
            Handle(ref _landingCenter, _colorLanding);
            Handle(ref _t2Start, _color2); Handle(ref _t2End, _color2);
#endif
        }

        private void DrawArrow(Vector3 from, Vector3 to)
        {
            Gizmos.DrawLine(from, to);
            Vector3 d    = (to - from).normalized;
            Vector3 perp = Vector3.Cross(Vector3.up, d).normalized;
            if (perp.sqrMagnitude < 0.01f) perp = Vector3.Cross(Vector3.forward, d).normalized;
            const float s = 0.15f;
            Gizmos.DrawLine(to, to - d*s + perp*s*0.5f);
            Gizmos.DrawLine(to, to - d*s - perp*s*0.5f);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Utilidades

        private static float Slope(Vector3 from, Vector3 to)
        {
            float h = Mathf.Abs(to.y - from.y);
            float d = Vector3.Distance(new Vector3(from.x,0,from.z), new Vector3(to.x,0,to.z));
            return d > 0.001f ? Mathf.Atan2(h, d) * Mathf.Rad2Deg : 90f;
        }

        private static Mesh BuildBoxMesh(float w, float h, float d)
        {
            float x=w/2f, y=h/2f, z=d/2f;
            var m = new Mesh { name = "LandingBox" };
            m.vertices  = new Vector3[]
            {
                new(-x,y,-z),new(x,y,-z),new(x,y,z),new(-x,y,z),
                new(-x,-y,-z),new(x,-y,-z),new(x,-y,z),new(-x,-y,z),
            };
            m.triangles = new int[]
            {
                0,2,1,0,3,2, 4,5,6,4,6,7,
                0,1,5,0,5,4, 2,3,7,2,7,6,
                0,4,7,0,7,3, 1,2,6,1,6,5,
            };
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        private static Material DebugMat(Color color)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.color = color;
            return m;
        }

        #endregion

        #region ContextMenu extra
        [ContextMenu("🔄 Recrear")]   private void CmRecreate()  => CreateStairSystem();
        [ContextMenu("📐 Endpoints")] private void CmEndpoints() => DiagnoseEndpoints();
        [ContextMenu("🔍 Layer Info")]
        private void CmLayerInfo()
        {
            int resolved = ResolvedNavMeshLayer;
            Debug.Log($"[StairHelper] '{name}' Layer Info:\n" +
                      $"  _navMeshLayer (Inspector): {_navMeshLayer} ('{LayerMask.LayerToName(_navMeshLayer)}')\n" +
                      $"  _navMeshLayerName: '{_navMeshLayerName}'\n" +
                      $"  LayerMask.NameToLayer('{_navMeshLayerName}'): {LayerMask.NameToLayer(_navMeshLayerName)}\n" +
                      $"  ResolvedNavMeshLayer: {resolved} ('{LayerMask.LayerToName(resolved)}')\n" +
                      $"  ProceduralRoot: {(_proceduralRoot != null ? _proceduralRoot.name : "NULL")}\n" +
                      $"  Ramp1 layer: {(_ramp1 != null ? $"{_ramp1.layer} ('{LayerMask.LayerToName(_ramp1.layer)}')" : "NULL")}\n" +
                      $"  Ramp2 layer: {(_ramp2 != null ? $"{_ramp2.layer} ('{LayerMask.LayerToName(_ramp2.layer)}')" : "NULL")}");
        }
        #endregion
    }

    public class NavMeshLinkGizmoHelper : MonoBehaviour
    {
        public Color LinkColor = Color.yellow;
        private void OnDrawGizmos()
        {
            var link = GetComponent<NavMeshLink>();
            if (link == null) return;
            Gizmos.color = LinkColor;
            Vector3 s = transform.TransformPoint(link.startPoint);
            Vector3 e = transform.TransformPoint(link.endPoint);
            Gizmos.DrawLine(s, e);
            Gizmos.DrawWireSphere(s, 0.08f);
            Gizmos.DrawWireSphere(e, 0.08f);
        }
    }
}