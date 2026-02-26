// File: ModelLoadManager.cs
// ✅ FIX #1 — RestoreModelTransform() incluye guard contra doble instanciación:
//             si ya existe un modelo cargado, lo destruye antes de instanciar uno nuevo.
// ✅ RestoreModelTransform NO publica ModelLoadedEvent — evita disparar NavMeshAgentCoordinator.
// ✅ LoadModel (flujo completo) sí publica ModelLoadedEvent.

using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Core.Managers
{
    public class ModelLoadManager : MonoBehaviour
    {
        [Header("📦 Modelo 3D")]
        [SerializeField] private GameObject _modelPrefab;

        [Header("⚙️ Configuración")]
        [SerializeField] private Transform _modelParent;
        [SerializeField] private float     _defaultScale = 1f;

        [Header("🎯 AR Configuration")]
        [SerializeField] private bool _useARAnchors           = true;
        [SerializeField] private bool _autoLoadOnLargestPlane = false;

        [Header("🔗 Integración de Navegación")]
        [SerializeField] private bool _autoConnectStairs = true;

        private GameObject _currentModel;
        private ARAnchor   _currentAnchor;
        private bool       _isModelLoaded;

        #region Properties

        public bool       IsModelLoaded    => _isModelLoaded && _currentModel != null;
        public GameObject CurrentModel     => _currentModel;
        public string     CurrentModelName => _modelPrefab != null ? _modelPrefab.name : "None";

        #endregion

        #region Lifecycle

        private void Awake()
        {
            InitializeModelParent();
            ValidateModelPrefab();
        }

        private void Start()
        {
            if (_autoLoadOnLargestPlane && _modelPrefab != null)
                _ = LoadModelOnLargestPlaneAsync();
        }

        #endregion

        #region Initialization

        private void InitializeModelParent()
        {
            if (_modelParent == null)
            {
                _modelParent = new GameObject("[3D_Models_Container]").transform;
                Debug.Log("[ModelLoadManager] ✅ Contenedor de modelos creado");
            }
        }

        private void ValidateModelPrefab()
        {
            if (_modelPrefab == null)
                Debug.LogWarning("[ModelLoadManager] ⚠️ No hay modelo asignado en el Inspector");
            else
                Debug.Log($"[ModelLoadManager] ✅ Modelo configurado: {_modelPrefab.name}");
        }

        #endregion

        #region Model Loading

        /// <summary>
        /// FLUJO LIGERO — solo para restaurar sesión guardada.
        ///
        /// ✅ FIX #1: Incluye guard contra doble instanciación.
        ///    Si ya hay un modelo cargado (_currentModel != null), lo destruye
        ///    antes de instanciar uno nuevo. Esto evita que la escena tenga
        ///    dos GameObjects del prefab del edificio simultáneamente.
        ///
        /// NO publica ModelLoadedEvent (evita disparar NavMeshAgentCoordinator).
        /// NO crea anclas AR, NO conecta escaleras, NO optimiza sombras.
        /// </summary>
        public async Task<bool> RestoreModelTransform(Vector3 position, Quaternion rotation, float scale = 1f)
        {
            try
            {
                // ✅ FIX #1 — Caso 1: Ya hay modelo en escena → solo reposicionarlo.
                // No se destruye ni se reinstancia. Esto es el caso ideal.
                if (_currentModel != null && _currentModel.activeInHierarchy)
                {
                    _currentModel.transform.SetPositionAndRotation(position, rotation);
                    _currentModel.transform.localScale = Vector3.one * scale;
                    _isModelLoaded = true;
                    Debug.Log($"[ModelLoadManager] 📍 Modelo reposicionado en {position}");
                    // ⚠️ NO publicar ModelLoadedEvent
                    return true;
                }

                // ✅ FIX #1 — Caso 2: El _currentModel es null pero puede haber
                // una instancia huérfana en escena (de una sesión anterior en Editor).
                // Buscarla por tag y destruirla antes de instanciar.
                DestroyOrphanModelInstances();

                // Caso 3: No hay modelo — instanciar mínimamente.
                if (_modelPrefab == null)
                {
                    Debug.LogError("[ModelLoadManager] ❌ No hay prefab para restaurar.");
                    return false;
                }

                Debug.Log($"[ModelLoadManager] 📦 Restaurando modelo: {_modelPrefab.name}");
                await Task.Yield();

                _currentModel = Instantiate(_modelPrefab, position, rotation, _modelParent);
                _currentModel.name = $"Model_{_modelPrefab.name}";
                _currentModel.transform.localScale = Vector3.one * scale;
                _currentModel.tag  = "3DModel";
                _isModelLoaded     = true;

                // Solo deshabilitar colliders — mínimo procesamiento
                foreach (var col in _currentModel.GetComponentsInChildren<Collider>())
                    col.enabled = false;

                // Yield extra para que Unity propague el transform a los hijos
                // (NavigationStartPoints necesitan transform.position correcto)
                await Task.Yield();

                Debug.Log($"[ModelLoadManager] ✅ Modelo restaurado en {position}");
                // ⚠️ NO publicar ModelLoadedEvent — evita disparar NavMeshAgentCoordinator
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModelLoadManager] ❌ RestoreModelTransform: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ✅ FIX #1 — Destruye instancias huérfanas del modelo en escena.
        /// Busca por tag "3DModel". Útil en el Editor donde el modelo puede
        /// haber sido arrastrado a la jerarquía manualmente durante desarrollo.
        /// </summary>
        private void DestroyOrphanModelInstances()
        {
            var orphans = GameObject.FindGameObjectsWithTag("3DModel");
            if (orphans.Length == 0) return;

            Debug.LogWarning($"[ModelLoadManager] ⚠️ Encontradas {orphans.Length} instancia(s) huérfana(s) del modelo. Destruyendo...");
            foreach (var orphan in orphans)
            {
                if (orphan != _currentModel)
                {
                    Debug.Log($"[ModelLoadManager] 🗑️ Destruyendo instancia huérfana: {orphan.name}");
                    Destroy(orphan);
                }
            }
        }

        /// <summary>
        /// FLUJO COMPLETO — para primera vez o cuando el usuario coloca el modelo en AR.
        /// Sí publica ModelLoadedEvent → NavMeshAgentCoordinator generará NavMesh.
        /// </summary>
        public async Task<bool> LoadModel(Vector3 position, Quaternion rotation)
        {
            if (_modelPrefab == null)
            {
                Debug.LogError("[ModelLoadManager] ❌ No hay modelo asignado");
                PublishMessage("No hay modelo configurado", MessageType.Error);
                return false;
            }

            try
            {
                Debug.Log($"[ModelLoadManager] 📦 Cargando modelo: {_modelPrefab.name}");
                PublishMessage($"Cargando {_modelPrefab.name}...", MessageType.Info);

                UnloadCurrentModel();
                await Task.Yield();

                _currentModel = Instantiate(_modelPrefab, position, rotation, _modelParent);
                _currentModel.name = $"Model_{_modelPrefab.name}";
                _currentModel.transform.localScale = Vector3.one * _defaultScale;
                _currentModel.tag  = "3DModel";

                OptimizeModel(_currentModel);

                if (_useARAnchors)
                    await CreateARAnchor(position, rotation);

                if (_autoConnectStairs)
                    ConnectNavigationSystems();

                _isModelLoaded = true;

                // ✅ Publicar evento — el coordinador generará NavMesh
                EventBus.Instance?.Publish(new ModelLoadedEvent
                {
                    ModelInstance = _currentModel,
                    ModelName     = _modelPrefab.name,
                    Position      = position
                });

                PublishMessage($"Modelo cargado: {_modelPrefab.name}", MessageType.Success);
                Debug.Log($"[ModelLoadManager] ✅ Modelo cargado en {position}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModelLoadManager] ❌ Error: {ex.Message}");
                EventBus.Instance?.Publish(new ModelLoadFailedEvent
                { ModelName = _modelPrefab?.name ?? "Unknown", ErrorMessage = ex.Message });
                PublishMessage("Error cargando modelo", MessageType.Error);
                return false;
            }
        }

        public async Task<bool> LoadModelOnLargestPlaneAsync()
        {
            if (_modelPrefab == null) { Debug.LogError("[ModelLoadManager] ❌ Sin prefab"); return false; }

            var arMgr = FindFirstObjectByType<AR.ARSessionManager>();
            if (arMgr == null) { Debug.LogError("[ModelLoadManager] ❌ ARSessionManager no encontrado"); return false; }

            int wait = 10;
            while (arMgr.DetectedPlaneCount == 0 && wait > 0)
            { Debug.Log("[ModelLoadManager] ⏳ Esperando planos..."); await Task.Delay(500); wait--; }

            ARPlane plane = arMgr.GetLargestPlane();
            if (plane == null)
            {
                Debug.LogWarning("[ModelLoadManager] ⚠️ Sin planos — cargando en origen");
                return await LoadModel(Vector3.zero, Quaternion.identity);
            }

            Debug.Log($"[ModelLoadManager] 🎯 Plano encontrado: {plane.size.x * plane.size.y:F2}m²");
            return await LoadModel(plane.center, Quaternion.identity);
        }

        #endregion

        #region Navigation Integration

        private void ConnectNavigationSystems()
        {
            if (_currentModel == null) return;
            int connected = 0;
            foreach (var sh in FindObjectsByType<Navigation.StairWithLandingHelper>(FindObjectsSortMode.None))
            {
                try { sh.ConnectToModel(_currentModel.transform); connected++; }
                catch (Exception ex) { Debug.LogError($"[ModelLoadManager] ❌ Escalera: {ex.Message}"); }
            }
            if (connected > 0) Debug.Log($"[ModelLoadManager] ✅ {connected} escalera(s) conectadas");
        }

        private void DisconnectNavigationSystems()
        {
            foreach (var sh in FindObjectsByType<Navigation.StairWithLandingHelper>(FindObjectsSortMode.None))
            {
                try { sh.Clear(); }
                catch (Exception ex) { Debug.LogError($"[ModelLoadManager] ❌ Clear escalera: {ex.Message}"); }
            }
        }

        #endregion

        #region Optimization

        private void OptimizeModel(GameObject model)
        {
            var cols  = model.GetComponentsInChildren<Collider>();
            var rends = model.GetComponentsInChildren<Renderer>();
            foreach (var c in cols)  c.enabled = false;
            foreach (var r in rends) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Debug.Log($"[ModelLoadManager] 🔧 {cols.Length} colliders, {rends.Length} renderers");
        }

        #endregion

        #region AR Anchoring

        private async Task CreateARAnchor(Vector3 position, Quaternion rotation)
        {
            try
            {
                var anchorMgr = FindFirstObjectByType<ARAnchorManager>();
                if (anchorMgr == null) { Debug.LogWarning("[ModelLoadManager] ⚠️ ARAnchorManager no encontrado"); return; }

                var arMgr = FindFirstObjectByType<AR.ARSessionManager>();
                if (arMgr == null || arMgr.DetectedPlaneCount == 0)
                { Debug.LogWarning("[ModelLoadManager] ⚠️ Sin planos para anclar"); return; }

                await Task.Yield();
                ARPlane closest = FindClosestPlane(arMgr, position);
                if (closest == null) return;

                _currentAnchor = anchorMgr.AttachAnchor(closest, new Pose(position, rotation));
                if (_currentAnchor != null)
                {
                    _currentModel.transform.SetParent(_currentAnchor.transform);
                    Debug.Log($"[ModelLoadManager] ⚓ Ancla: {_currentAnchor.trackableId}");
                }
            }
            catch (Exception ex) { Debug.LogError($"[ModelLoadManager] ❌ Ancla: {ex.Message}"); }
        }

        private ARPlane FindClosestPlane(AR.ARSessionManager arMgr, Vector3 pos)
        {
            ARPlane closest = null; float minD = float.MaxValue;
            foreach (var kvp in arMgr.DetectedPlanes)
            {
                if (kvp.Value == null) continue;
                float d = Vector3.Distance(pos, kvp.Value.center);
                if (d < minD) { minD = d; closest = kvp.Value; }
            }
            return closest;
        }

        #endregion

        #region Model Management

        public void UnloadCurrentModel()
        {
            if (_currentModel == null) return;
            if (_autoConnectStairs) DisconnectNavigationSystems();

            if (_currentAnchor != null)
            {
                FindFirstObjectByType<ARAnchorManager>()?.TryRemoveAnchor(_currentAnchor);
                _currentAnchor = null;
            }

            Destroy(_currentModel);
            _currentModel  = null;
            _isModelLoaded = false;
            Debug.Log("[ModelLoadManager] 🗑️ Modelo descargado");
            PublishMessage("Modelo descargado", MessageType.Info);
        }

        public void UpdateModelPosition(Vector3 p) { if (_currentModel != null) { _currentModel.transform.position = p; RefreshStairs(); } }
        public void UpdateModelRotation(Quaternion r) { if (_currentModel != null) { _currentModel.transform.rotation = r; RefreshStairs(); } }
        public void UpdateModelScale(float s) { if (_currentModel != null) { _currentModel.transform.localScale = Vector3.one * s; RefreshStairs(); } }

        private void RefreshStairs()
        {
            if (!_autoConnectStairs) return;
            foreach (var sh in FindObjectsByType<Navigation.StairWithLandingHelper>(FindObjectsSortMode.None))
            {
                try { sh.CreateStairSystem(); }
                catch (Exception ex) { Debug.LogError($"[ModelLoadManager] ❌ Refresh escalera: {ex.Message}"); }
            }
        }

        #endregion

        #region Utilities

        private void PublishMessage(string msg, MessageType type) =>
            EventBus.Instance?.Publish(new ShowMessageEvent
            { Message = msg, Type = type, Duration = type == MessageType.Error ? 5f : 3f });

        #endregion

        #region Debug

        [ContextMenu("🔨 Load on Largest Plane")] private void DbgLoad()   => _ = LoadModelOnLargestPlaneAsync();
        [ContextMenu("🗑️ Unload")]                 private void DbgUnload() => UnloadCurrentModel();
        [ContextMenu("🔗 Reconnect Stairs")]        private void DbgStairs() => ConnectNavigationSystems();
        [ContextMenu("ℹ️ Info")]
        private void DbgInfo()
        {
            Debug.Log($"Prefab: {(_modelPrefab ? _modelPrefab.name : "None")} | " +
                      $"Loaded: {_isModelLoaded} | " +
                      $"Pos: {(_currentModel ? _currentModel.transform.position.ToString() : "N/A")}");
        }

        #endregion
    }
}