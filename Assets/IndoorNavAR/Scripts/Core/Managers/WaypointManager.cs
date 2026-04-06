// File: WaypointManager.cs
// ✅ v8 — FIX_LOCAL_SPACE_INSTANTIATE: Instanciar waypoints directamente en local space.
//
// ============================================================================
//  CAMBIOS v7 → v8
// ============================================================================
//
//  PROBLEMA RAÍZ (diagnosticado sobre v7):
//  ─────────────────────────────────────────────────────────────────────────
//  En v7, LoadWaypoints() convertía localPosition → world con TransformPoint()
//  y luego llamaba CreateWaypoint(worldPos, worldRot), que instancia con parent.
//  Unity entonces recalcula localPosition internamente.
//
//  Esto funciona SOLO si el modelo no se mueve entre la llamada a TransformPoint()
//  y la asignación del parent. Pero el sistema tiene:
//    - ARWorldOriginStabilizer corrigiendo drift en LateUpdate
//    - AROriginAligner moviendo XROrigin
//    - VIO corrections continuas de ARCore
//
//  Si el modelo se mueve en ese gap (aunque sea 1 frame), la conversión
//  local→world queda obsoleta antes de que Unity la procese como local.
//  Resultado: drift proporcional al movimiento del modelo durante la carga.
//
//  SOLUCIÓN v8:
//  ─────────────────────────────────────────────────────────────────────────
//  Nuevo método CreateWaypointLocal(localPos, localRot):
//    - Instantiate(_waypointPrefab, _waypointsParent) sin posición — queda en local (0,0,0).
//    - Asignar transform.localPosition = localPos y transform.localRotation = localRot.
//    - Unity mantiene la posición relativa al modelo independientemente de cuándo
//      o cuánto se mueva el modelo después.
//
//  LoadWaypoints():
//    - Cuando hasLocalSpace=true → llamar CreateWaypointLocal() directamente.
//    - NO convertir a world, NO llamar CreateWaypoint() (que asume world space).
//    - Continuar con `continue` para saltarse el CreateWaypoint() legacy.
//
//  LoadFromSaveData() en WaypointData:
//    - Cuando hasLocalSpace=true, NO sobreescribir transform.position/rotation.
//    - La posición ya fue asignada por CreateWaypointLocal() — sobreescribir
//      con world legacy destruiría el local space correcto.
//
//  Compatibilidad hacia atrás:
//    - Sesiones con hasLocalSpace=false siguen usando el camino legacy (world space).
//    - SerializeWaypoints() sin cambios — ya guarda localPosition correctamente.
//
//  TODOS LOS COMPORTAMIENTOS DE v7 SE CONSERVAN ÍNTEGRAMENTE.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Core.Managers
{
    public class WaypointManager : MonoBehaviour
    {
        [Header("Prefab")]
        [SerializeField] private GameObject _waypointPrefab;

        [Header("Configuración")]
        [SerializeField] private int _maxWaypoints = 50;

        [Header("─── FIX v6: Anclaje al modelo 3D ─────────────────────────────────")]
        [Tooltip("Asigna aquí el mismo Transform que usa ModelLoadManager como _modelParent " +
                 "('[3D_Models_Container]'). Si se deja vacío, WaypointManager lo busca " +
                 "automáticamente al cargar el modelo. RECOMENDADO: asignarlo en el Inspector.")]
        [SerializeField] private Transform _modelContainer;

        [Tooltip("Si true, [Waypoints] se crea como hijo de _modelContainer desde el inicio. " +
                 "Si false, se crea en la raíz y se re-parentea cuando el modelo esté listo.")]
        [SerializeField] private bool _parentToModelOnAwake = false;

        private Transform _waypointsParent;

        private readonly Dictionary<string, WaypointData> _waypoints     = new Dictionary<string, WaypointData>();
        private readonly List<WaypointData>               _waypointsList = new List<WaypointData>();

        private bool _isLoadingBatch      = false;
        private bool _waypointsReparented = false;

        #region Properties

        public int WaypointCount => _waypoints.Count;
        public IReadOnlyList<WaypointData> Waypoints => _waypointsList.AsReadOnly();

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateDependencies();
            CreateWaypointsParent();
        }

        private void OnEnable()
        {
            SubscribeToEvents();
            EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        private void OnDisable()
        {
            UnsubscribeFromEvents();
            EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        #endregion

        #region Initialization

        private void ValidateDependencies()
        {
            if (_waypointPrefab == null)
            {
                Debug.LogError("[WaypointManager] Waypoint Prefab no asignado. Creando prefab básico...");
                _waypointPrefab = CreateDefaultWaypointPrefab();
            }

            if (_waypointPrefab.GetComponent<WaypointData>() == null)
            {
                Debug.LogError("[WaypointManager] El prefab debe tener componente WaypointData.");
                enabled = false;
            }
        }

        private void CreateWaypointsParent()
        {
            GameObject parent = new GameObject("[Waypoints]");

            if (_modelContainer != null && _parentToModelOnAwake)
            {
                parent.transform.SetParent(_modelContainer, worldPositionStays: false);
                _waypointsReparented = true;
                Debug.Log("[WaypointManager] ✅ Contenedor [Waypoints] creado bajo _modelContainer " +
                          $"'{_modelContainer.name}' (Awake, _parentToModelOnAwake=true).");
            }
            else
            {
                Debug.Log("[WaypointManager] Contenedor [Waypoints] creado en raíz de escena. " +
                          "Se re-parenteará cuando el modelo 3D esté disponible.");
            }

            _waypointsParent = parent.transform;
        }

        private GameObject CreateDefaultWaypointPrefab()
        {
            GameObject prefab = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            prefab.name = "Waypoint";
            prefab.AddComponent<WaypointData>();
            Renderer renderer = prefab.GetComponent<Renderer>();
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = new Color(0f, 1f, 1f, 0.7f);
            renderer.material = mat;
            Destroy(prefab.GetComponent<Collider>());
            prefab.SetActive(false);
            return prefab;
        }

        #endregion

        #region ✅ v6/v7 — Anclaje al modelo 3D

        public void ForceReparentToModel(Transform modelRoot = null)
        {
            _waypointsReparented = false;
            ReparentToModel(modelRoot);
        }

        public void ReparentToModel(Transform modelRoot = null)
        {
            if (_waypointsReparented)
            {
                Debug.Log("[WaypointManager] ReparentToModel() — ya reparenteado, ignorando.");
                return;
            }

            Transform target = modelRoot
                            ?? _modelContainer
                            ?? FindModelContainer();

            if (target == null)
            {
                Debug.LogWarning("[WaypointManager] ⚠️ ReparentToModel: no se encontró transform " +
                                 "del modelo. [Waypoints] permanece en raíz de escena.");
                return;
            }

            Vector3    prevPos = _waypointsParent.position;
            Quaternion prevRot = _waypointsParent.rotation;

            _waypointsParent.SetParent(target, worldPositionStays: true);
            _waypointsReparented = true;

            Debug.Log($"[WaypointManager] ✅ [Waypoints] re-parenteado bajo '{target.name}'.\n" +
                      $"  Pos world antes: {prevPos:F3} | después: {_waypointsParent.position:F3}\n" +
                      $"  Rot world antes: {prevRot.eulerAngles:F1} | " +
                      $"después: {_waypointsParent.rotation.eulerAngles:F1}");
        }

        private void TryReparentToModel()
        {
            if (_waypointsReparented) return;
            Transform target = _modelContainer ?? FindModelContainer();
            if (target != null)
                ReparentToModel(target);
            else
                Debug.LogWarning("[WaypointManager] ⚠️ TryReparentToModel: modelo no disponible aún.");
        }

        private static Transform FindModelContainer()
        {
            GameObject byName = GameObject.Find("[3D_Models_Container]");
            if (byName != null) return byName.transform;

            var mlm = FindFirstObjectByType<ModelLoadManager>();
            if (mlm != null && mlm.CurrentModel != null)
            {
                Transform parent = mlm.CurrentModel.transform.parent;
                return parent != null ? parent : mlm.CurrentModel.transform;
            }

            GameObject byTag = GameObject.FindGameObjectWithTag("3DModel");
            if (byTag != null)
            {
                Transform parent = byTag.transform.parent;
                return parent != null ? parent : byTag.transform;
            }

            return null;
        }

        private void OnModelLoaded(ModelLoadedEvent evt)
        {
            if (_waypointsReparented) return;

            Transform target = null;
            if (evt.ModelInstance != null)
            {
                target = evt.ModelInstance.transform.parent != null
                    ? evt.ModelInstance.transform.parent
                    : evt.ModelInstance.transform;
            }

            Debug.Log($"[WaypointManager] 📦 ModelLoadedEvent — reparentando bajo '{(target?.name ?? "null")}'...");
            ReparentToModel(target);
        }

        private Transform GetModelRoot()
        {
            if (_modelContainer != null) return _modelContainer;
            if (_waypointsParent != null && _waypointsParent.parent != null)
                return _waypointsParent.parent;
            return FindModelContainer();
        }

        #endregion

        #region Event Subscriptions

        private void SubscribeToEvents()
        {
            EventBus.Instance.Subscribe<WaypointPlacedEvent>(OnWaypointPlaced);
            EventBus.Instance.Subscribe<WaypointRemovedEvent>(OnWaypointRemoved);
        }

        private void UnsubscribeFromEvents()
        {
            EventBus.Instance.Unsubscribe<WaypointPlacedEvent>(OnWaypointPlaced);
            EventBus.Instance.Unsubscribe<WaypointRemovedEvent>(OnWaypointRemoved);
        }

        private void OnWaypointPlaced(WaypointPlacedEvent evt)
            => Debug.Log($"[WaypointManager] Waypoint colocado: {evt.WaypointId} en {evt.Position}");

        private void OnWaypointRemoved(WaypointRemovedEvent evt)
            => Debug.Log($"[WaypointManager] Waypoint removido: {evt.WaypointId}");

        #endregion

        #region Waypoint Creation

        /// <summary>
        /// Crea un waypoint a partir de una posición WORLD SPACE.
        /// Usar para waypoints nuevos colocados por el usuario en la sesión actual.
        /// </summary>
        public WaypointData CreateWaypoint(Vector3 position, Quaternion rotation)
        {
            if (_waypoints.Count >= _maxWaypoints)
            {
                Debug.LogWarning($"[WaypointManager] Límite máximo de waypoints alcanzado ({_maxWaypoints}).");
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message  = $"Límite máximo de {_maxWaypoints} waypoints alcanzado.",
                    Type     = MessageType.Warning,
                    Duration = 3f
                });
                return null;
            }

            try
            {
                GameObject waypointObj = Instantiate(_waypointPrefab, position, rotation, _waypointsParent);
                waypointObj.SetActive(true);

                WaypointData waypointData = waypointObj.GetComponent<WaypointData>();
                if (waypointData == null)
                {
                    Debug.LogError("[WaypointManager] El prefab no tiene componente WaypointData.");
                    Destroy(waypointObj);
                    return null;
                }

                string waypointId = Guid.NewGuid().ToString();
                waypointData.WaypointId   = waypointId;
                waypointData.WaypointName = $"Waypoint_{_waypoints.Count + 1}";

                _waypoints[waypointId] = waypointData;
                _waypointsList.Add(waypointData);

                EventBus.Instance.Publish(new WaypointPlacedEvent
                {
                    WaypointId = waypointId,
                    Position   = position,
                    Rotation   = rotation
                });

                Debug.Log($"[WaypointManager] Waypoint creado (world): {waypointId} en {position}");
                return waypointData;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WaypointManager] Error creando waypoint: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ✅ v8 — Crea un waypoint a partir de una posición LOCAL SPACE del modelo.
        ///
        /// Usar exclusivamente al restaurar waypoints desde sesiones guardadas con v7+
        /// (data.hasLocalSpace=true). A diferencia de CreateWaypoint():
        ///   - Instancia el prefab como hijo de _waypointsParent SIN posición world.
        ///   - Asigna localPosition/localRotation directamente.
        ///   - El waypoint queda anclado al modelo independientemente de cualquier
        ///     movimiento posterior del modelo (VIO, stabilizer, XR Origin, etc.).
        ///
        /// Por qué es correcto:
        ///   Si se convirtiera local→world y luego se instanciara con parent (como en v7),
        ///   Unity recalcularía localPos partiendo del world convertido EN ESE INSTANTE.
        ///   Si el modelo se mueve 1 frame antes o después, la conversión es obsoleta.
        ///   Asignando localPosition directamente, no hay conversión que pueda quedar obsoleta.
        /// </summary>
        public WaypointData CreateWaypointLocal(Vector3 localPos, Quaternion localRot)
        {
            if (_waypoints.Count >= _maxWaypoints)
            {
                Debug.LogWarning($"[WaypointManager] Límite máximo de waypoints alcanzado ({_maxWaypoints}).");
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message  = $"Límite máximo de {_maxWaypoints} waypoints alcanzado.",
                    Type     = MessageType.Warning,
                    Duration = 3f
                });
                return null;
            }

            try
            {
                // Instanciar bajo el parent sin especificar posición world.
                // El prefab queda en localPosition=(0,0,0) del parent.
                GameObject waypointObj = Instantiate(_waypointPrefab, _waypointsParent);

                // Asignar local space directamente — no hay conversión intermedia.
                waypointObj.transform.localPosition = localPos;
                waypointObj.transform.localRotation = localRot;
                waypointObj.SetActive(true);

                WaypointData waypointData = waypointObj.GetComponent<WaypointData>();
                if (waypointData == null)
                {
                    Debug.LogError("[WaypointManager] El prefab no tiene componente WaypointData.");
                    Destroy(waypointObj);
                    return null;
                }

                string waypointId = Guid.NewGuid().ToString();
                waypointData.WaypointId   = waypointId;
                waypointData.WaypointName = $"Waypoint_{_waypoints.Count + 1}";

                _waypoints[waypointId] = waypointData;
                _waypointsList.Add(waypointData);

                // Publicar en world space real (Unity lo calcula automáticamente)
                EventBus.Instance.Publish(new WaypointPlacedEvent
                {
                    WaypointId = waypointId,
                    Position   = waypointObj.transform.position,
                    Rotation   = waypointObj.transform.rotation
                });

                Debug.Log($"[WaypointManager] [v8] Waypoint local creado: {waypointId} " +
                          $"localPos={localPos:F3} → worldPos={waypointObj.transform.position:F3}");
                return waypointData;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WaypointManager] Error en CreateWaypointLocal: {ex.Message}");
                return null;
            }
        }

        public WaypointData CreateConfiguredWaypoint(
            Vector3 position, Quaternion rotation,
            string name, WaypointType type, Color? color = null)
        {
            WaypointData waypoint = CreateWaypoint(position, rotation);
            if (waypoint != null)
                waypoint.Configure(name, type, color ?? WaypointData.GetDefaultColorForType(type));
            return waypoint;
        }

        #endregion

        #region Waypoint Management

        public WaypointData GetWaypoint(string waypointId)
        {
            _waypoints.TryGetValue(waypointId, out WaypointData waypoint);
            return waypoint;
        }

        public bool UpdateWaypoint(string waypointId, string name, WaypointType type, Color color, string description = "")
        {
            if (!_waypoints.TryGetValue(waypointId, out WaypointData waypoint))
            {
                Debug.LogWarning($"[WaypointManager] Waypoint no encontrado: {waypointId}");
                return false;
            }
            waypoint.Configure(name, type, color, description);
            return true;
        }

        public bool RemoveWaypoint(string waypointId)
        {
            if (!_waypoints.TryGetValue(waypointId, out WaypointData waypoint))
            {
                Debug.LogWarning($"[WaypointManager] Waypoint no encontrado: {waypointId}");
                return false;
            }

            _waypoints.Remove(waypointId);
            _waypointsList.Remove(waypoint);

            if (waypoint != null && waypoint.gameObject != null)
                Destroy(waypoint.gameObject);

            EventBus.Instance.Publish(new WaypointRemovedEvent { WaypointId = waypointId });
            Debug.Log($"[WaypointManager] Waypoint eliminado: {waypointId}");
            return true;
        }

        public void ClearAllWaypoints()
        {
            foreach (var waypoint in _waypointsList)
                if (waypoint != null && waypoint.gameObject != null)
                    Destroy(waypoint.gameObject);

            _waypoints.Clear();
            _waypointsList.Clear();

            Debug.Log("[WaypointManager] Todos los waypoints eliminados.");
            EventBus.Instance.Publish(new ShowMessageEvent
            {
                Message  = "Todos los waypoints eliminados.",
                Type     = MessageType.Info,
                Duration = 2f
            });
        }

        #endregion

        #region Queries

        public List<WaypointData> GetWaypointsByType(WaypointType type)
            => _waypointsList.Where(w => w.Type == type).ToList();

        public WaypointData FindNearestWaypoint(Vector3 position, float maxDistance = float.MaxValue)
        {
            WaypointData nearest = null;
            float        minDist = maxDistance;
            foreach (var wp in _waypointsList)
            {
                float d = Vector3.Distance(position, wp.Position);
                if (d < minDist && wp.IsNavigable) { minDist = d; nearest = wp; }
            }
            return nearest;
        }

        public List<WaypointData> FindWaypointsInRadius(Vector3 center, float radius)
            => _waypointsList.Where(w => Vector3.Distance(center, w.Position) <= radius).ToList();

        public List<WaypointData> SearchWaypointsByName(string searchTerm)
        {
            if (string.IsNullOrEmpty(searchTerm)) return new List<WaypointData>();
            return _waypointsList
                .Where(w => w.WaypointName.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        #endregion

        #region Persistence — ✅ v8 FIX_LOCAL_SPACE_INSTANTIATE

        /// <summary>
        /// Serializa waypoints guardando posición en espacio local del modelo.
        /// Sin cambios respecto a v7 — ya funciona correctamente.
        /// </summary>
        public List<WaypointSaveData> SerializeWaypoints()
        {
            var saveData  = new List<WaypointSaveData>();
            Transform modelRoot = GetModelRoot();

            bool hasModel = modelRoot != null;
            if (!hasModel)
                Debug.LogWarning("[WaypointManager] ⚠️ [v8] SerializeWaypoints: modelo no encontrado. " +
                                 "Guardando solo world space (sin local space). " +
                                 "Los waypoints pueden driftar en la próxima sesión.");
            else
                Debug.Log($"[WaypointManager] [v8] SerializeWaypoints con modelRoot='{modelRoot.name}' " +
                          $"pos={modelRoot.position:F3}");

            foreach (var wp in _waypointsList)
            {
                WaypointSaveData data = wp.ToSaveData();

                if (hasModel)
                {
                    data.localPosition = modelRoot.InverseTransformPoint(wp.transform.position);
                    data.localRotation = Quaternion.Inverse(modelRoot.rotation) * wp.transform.rotation;
                    data.hasLocalSpace = true;

                    Debug.Log($"[WaypointManager] [v8] WP '{wp.WaypointName}': " +
                              $"world={wp.transform.position:F3} → " +
                              $"local={data.localPosition:F3}");
                }
                else
                {
                    data.localPosition = Vector3.zero;
                    data.localRotation = Quaternion.identity;
                    data.hasLocalSpace = false;
                }

                saveData.Add(data);
            }

            return saveData;
        }

        /// <summary>
        /// ✅ v8 — Carga waypoints usando local space nativo sin conversión intermedia.
        ///
        /// CAMBIO RESPECTO A v7:
        ///   Cuando hasLocalSpace=true:
        ///     v7: TransformPoint(localPos) → world → CreateWaypoint(world) → Unity recalcula local
        ///     v8: CreateWaypointLocal(localPos) → asigna localPosition directamente
        ///
        ///   La v8 elimina la ventana de tiempo entre conversión y asignación de parent
        ///   donde cualquier movimiento del modelo producía drift residual.
        ///
        /// IMPORTANTE: Después de CreateWaypointLocal(), llamar LoadFromSaveData() que
        /// NO sobreescribe transform cuando hasLocalSpace=true (ver WaypointData.cs).
        /// </summary>
        public void LoadWaypoints(List<WaypointSaveData> saveData)
        {
            if (_isLoadingBatch)
            {
                Debug.LogWarning("[WaypointManager] ⚠️ LoadWaypoints ya en progreso — llamada ignorada.");
                return;
            }
            _isLoadingBatch = true;

            try
            {
                if (saveData == null || saveData.Count == 0)
                {
                    Debug.LogWarning("[WaypointManager] LoadWaypoints: saveData nulo o vacío.");
                    EventBus.Instance.Publish(new WaypointsBatchLoadedEvent { Count = 0 });
                    return;
                }

                // ─── Log diagnóstico PRE-carga ─────────────────────────────
                Debug.Log($"[WaypointManager] 📍 Recibidos {saveData.Count} elemento(s) para cargar:");
                for (int i = 0; i < saveData.Count; i++)
                {
                    var d = saveData[i];
                    if (d == null) { Debug.Log($"[WaypointManager]   [{i}] ⚠️ ELEMENTO NULL"); continue; }

                    bool hasValidId   = !string.IsNullOrEmpty(d.id);
                    bool hasValidName = !string.IsNullOrEmpty(d.name);
                    bool hasValidPos  = !float.IsNaN(d.position.x) && !float.IsNaN(d.position.y) && !float.IsNaN(d.position.z);

                    string idPreview = hasValidId ? d.id.Substring(0, Math.Min(8, d.id.Length)) : "VACÍO";

                    Debug.Log($"[WaypointManager]   [{i}] " +
                              $"id={idPreview} name='{(hasValidName ? d.name : "VACÍO")}' " +
                              $"pos={d.position} localPos={d.localPosition} " +
                              $"hasLocalSpace={d.hasLocalSpace} type={d.type} " +
                              $"→ {(hasValidId && hasValidName && hasValidPos ? "✅ VÁLIDO" : "❌ INVÁLIDO")}");
                }

                Debug.Log($"[WaypointManager] 📍 Iniciando carga de {saveData.Count} waypoint(s)...");

                // ─── Reparentar antes de instanciar ───────────────────────
                TryReparentToModel();

                // ─── v8: canUseLocalSpace = _waypointsParent está bajo el modelo ──
                // Con el fix v8, la conversión local→world ya no se necesita,
                // pero sí necesitamos que _waypointsParent sea hijo del modelo
                // para que localPosition sea relativa al modelo.
                bool canUseLocalSpace = _waypointsReparented && _waypointsParent.parent != null;

                if (canUseLocalSpace)
                    Debug.Log($"[WaypointManager] [v8] Local space disponible — " +
                              $"_waypointsParent bajo '{_waypointsParent.parent.name}'.");
                else
                    Debug.LogWarning("[WaypointManager] [v8] ⚠️ _waypointsParent no está bajo el modelo — " +
                                     "usando world space legacy para todos los waypoints.");

                Debug.Log($"[WaypointManager] 📦 _waypointsParent: '{_waypointsParent.name}' | " +
                          $"parent='{(_waypointsParent.parent?.name ?? "RAÍZ")}' | " +
                          $"worldPos={_waypointsParent.position:F3} | " +
                          $"reparented={_waypointsReparented}");

                ClearAllWaypoints();

                int received  = saveData.Count;
                int created   = 0;
                int skipped   = 0;
                int usedLocal = 0;
                int usedWorld = 0;

                foreach (var data in saveData)
                {
                    // ─── Validación ────────────────────────────────────────
                    if (data == null)
                    { Debug.LogWarning("[WaypointManager] ⚠️ WaypointSaveData null, omitiendo."); skipped++; continue; }

                    if (string.IsNullOrEmpty(data.id))
                    { Debug.LogWarning($"[WaypointManager] ⚠️ Waypoint '{data.name}' id vacío, omitiendo."); skipped++; continue; }

                    if (string.IsNullOrEmpty(data.name))
                    { Debug.LogWarning($"[WaypointManager] ⚠️ Waypoint id '{data.id.Substring(0, 8)}' name vacío, omitiendo."); skipped++; continue; }

                    if (float.IsNaN(data.position.x) || float.IsNaN(data.position.y) || float.IsNaN(data.position.z))
                    { Debug.LogWarning($"[WaypointManager] ⚠️ Waypoint '{data.name}' posición NaN, omitiendo."); skipped++; continue; }

                    // ─── v8: CAMINO PRINCIPAL — local space nativo ─────────
                    if (data.hasLocalSpace && canUseLocalSpace)
                    {
                        // ✅ FIX v8: Instanciar directamente en local space.
                        // NO convertir a world. Unity mantiene la posición relativa
                        // al modelo independientemente de cuándo se mueva.
                        WaypointData waypoint = CreateWaypointLocal(data.localPosition, data.localRotation);

                        if (waypoint != null)
                        {
                            // LoadFromSaveData NO sobreescribe transform cuando hasLocalSpace=true
                            waypoint.LoadFromSaveData(data);
                            created++;
                            usedLocal++;

                            Debug.Log($"[WaypointManager] [v8] ✅ '{data.name}': " +
                                      $"local={data.localPosition:F3} → " +
                                      $"world real={waypoint.transform.position:F3}");
                        }
                        else
                        {
                            Debug.LogWarning($"[WaypointManager] ⚠️ CreateWaypointLocal null para '{data.name}'.");
                            skipped++;
                        }

                        continue; // ← saltarse el camino legacy
                    }

                    // ─── CAMINO LEGACY — world space (sesiones pre-v7) ─────
                    {
                        Vector3    finalPos = data.position;
                        Quaternion finalRot = data.rotation;
                        usedWorld++;

                        if (data.hasLocalSpace && !canUseLocalSpace)
                            Debug.LogWarning($"[WaypointManager] [v8] ⚠️ '{data.name}': " +
                                             "hasLocalSpace=true pero _waypointsParent no bajo modelo — usando world legacy.");
                        else
                            Debug.Log($"[WaypointManager] [v8] '{data.name}': " +
                                      "hasLocalSpace=false — usando world legacy (sesión pre-v7).");

                        WaypointData waypoint = CreateWaypoint(finalPos, finalRot);
                        if (waypoint != null)
                        {
                            waypoint.LoadFromSaveData(data);
                            created++;
                        }
                        else
                        {
                            Debug.LogWarning($"[WaypointManager] ⚠️ CreateWaypoint null para '{data.name}'.");
                            skipped++;
                        }
                    }
                }

                Debug.Log($"[WaypointManager] ✅ LoadWaypoints COMPLETO: " +
                          $"recibidos={received}, creados={created}, omitidos={skipped}. " +
                          $"[v8] localSpace={usedLocal}, worldLegacy={usedWorld}. " +
                          $"En memoria: {_waypoints.Count} waypoints.");

                if (created < received)
                    Debug.LogWarning($"[WaypointManager] ⚠️ Solo se crearon {created} de {received} waypoints.");

                EventBus.Instance.Publish(new WaypointsBatchLoadedEvent { Count = created });
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message  = $"{created} waypoints cargados exitosamente.",
                    Type     = MessageType.Success,
                    Duration = 3f
                });
            }
            finally
            {
                _isLoadingBatch = false;
            }
        }

        #endregion

        #region Debug

        [ContextMenu("Debug: List All Waypoints")]
        public void DebugListWaypoints()
        {
            Debug.Log($"[WaypointManager] Total: _waypoints={_waypoints.Count}, _waypointsList={_waypointsList.Count}");
            Transform modelRoot = GetModelRoot();
            foreach (var wp in _waypointsList)
            {
                string localStr = modelRoot != null
                    ? $" | local={modelRoot.InverseTransformPoint(wp.transform.position):F3}"
                    : "";
                Debug.Log($"  - {wp?.WaypointName ?? "NULL"} ({wp?.Type}) world={wp?.Position}{localStr}");
            }
        }

        [ContextMenu("Debug: Verify Consistency")]
        public void DebugVerifyConsistency()
        {
            bool ok = _waypoints.Count == _waypointsList.Count;
            Debug.Log($"[WaypointManager] Consistencia: {_waypoints.Count} == {_waypointsList.Count} → {(ok ? "✅ OK" : "❌ INCONSISTENTE")}");
        }

        [ContextMenu("Debug: Parent Info")]
        public void DebugParentInfo()
        {
            Transform modelRoot = GetModelRoot();
            Debug.Log($"[WaypointManager] 📦 _waypointsParent: '{_waypointsParent?.name ?? "NULL"}'\n" +
                      $"  parent      = '{(_waypointsParent?.parent?.name ?? "RAÍZ")}'\n" +
                      $"  worldPos    = {_waypointsParent?.position.ToString("F3") ?? "N/A"}\n" +
                      $"  localPos    = {_waypointsParent?.localPosition.ToString("F3") ?? "N/A"}\n" +
                      $"  reparented  = {_waypointsReparented}\n" +
                      $"  modelRoot   = '{(modelRoot?.name ?? "no encontrado")}'\n" +
                      $"  _modelContainer (Inspector) = '{(_modelContainer?.name ?? "no asignado")}'");
        }

        [ContextMenu("Debug: Force Reparent Now")]
        public void DebugForceReparent()
        {
            _waypointsReparented = false;
            TryReparentToModel();
            DebugParentInfo();
        }

        [ContextMenu("Debug: Test Local Space Round-trip")]
        public void DebugTestLocalSpaceRoundtrip()
        {
            Transform modelRoot = GetModelRoot();
            if (modelRoot == null) { Debug.LogWarning("[WaypointManager] Sin modelRoot para test."); return; }

            foreach (var wp in _waypointsList)
            {
                Vector3 worldOriginal = wp.transform.position;
                Vector3 local         = modelRoot.InverseTransformPoint(worldOriginal);
                Vector3 worldRestored = modelRoot.TransformPoint(local);
                float   error         = Vector3.Distance(worldOriginal, worldRestored);

                Debug.Log($"[WaypointManager] Round-trip '{wp.WaypointName}': " +
                          $"world={worldOriginal:F3} → local={local:F3} → restored={worldRestored:F3} " +
                          $"| error={error:F6}m {(error < 0.001f ? "✅" : "❌")}");
            }
        }

        [ContextMenu("Debug: Verify Local Space Anchoring")]
        public void DebugVerifyLocalSpaceAnchoring()
        {
            // Verifica que los waypoints tengan localPosition estable
            // (no cambiar aunque el modelo se mueva en world space)
            Debug.Log($"[WaypointManager] [v8] Verificando anclaje local space:");
            foreach (var wp in _waypointsList)
            {
                if (wp == null) continue;
                Debug.Log($"  '{wp.WaypointName}': " +
                          $"localPos={wp.transform.localPosition:F3} | " +
                          $"worldPos={wp.transform.position:F3} | " +
                          $"parent='{wp.transform.parent?.name ?? "NULL"}'");
            }
        }

        #endregion
    }
}