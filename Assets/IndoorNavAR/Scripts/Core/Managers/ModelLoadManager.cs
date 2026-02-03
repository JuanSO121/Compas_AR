using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Core.Managers
{
    /// <summary>
    /// Gestor simplificado de carga de modelos 3D para AR.
    /// ✅ Compatible con Unity 6 y AR Foundation 6
    /// ✅ Asignación directa de prefab desde Inspector
    /// ✅ Integración optimizada con sistema de navegación
    /// </summary>
    public class ModelLoadManager : MonoBehaviour
    {
        [Header("📦 Modelo 3D")]
        [Tooltip("Arrastra aquí el prefab del modelo 3D")]
        [SerializeField] private GameObject _modelPrefab;
        
        [Header("⚙️ Configuración")]
        [SerializeField] private Transform _modelParent;
        [SerializeField] private float _defaultScale = 1f;
        
        [Header("🎯 AR Configuration")]
        [SerializeField] private bool _useARAnchors = true;
        [SerializeField] private bool _autoLoadOnLargestPlane = false;

        // Estado
        private GameObject _currentModel;
        private ARAnchor _currentAnchor;
        private bool _isModelLoaded;

        #region Properties

        public bool IsModelLoaded => _isModelLoaded && _currentModel != null;
        public GameObject CurrentModel => _currentModel;
        public string CurrentModelName => _modelPrefab != null ? _modelPrefab.name : "None";

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            InitializeModelParent();
            ValidateModelPrefab();
        }

        private void Start()
        {
            if (_autoLoadOnLargestPlane && _modelPrefab != null)
            {
                _ = LoadModelOnLargestPlaneAsync();
            }
        }

        #endregion

        #region Initialization

        private void InitializeModelParent()
        {
            if (_modelParent == null)
            {
                GameObject parent = new GameObject("[3D_Models_Container]");
                _modelParent = parent.transform;
                Debug.Log("[ModelLoadManager] ✅ Contenedor de modelos creado");
            }
        }

        private void ValidateModelPrefab()
        {
            if (_modelPrefab == null)
            {
                Debug.LogWarning("[ModelLoadManager] ⚠️ No hay modelo asignado en el Inspector");
            }
            else
            {
                Debug.Log($"[ModelLoadManager] ✅ Modelo configurado: {_modelPrefab.name}");
            }
        }

        #endregion

        #region Model Loading

        /// <summary>
        /// Carga el modelo en una posición específica
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

                // Limpiar modelo anterior
                UnloadCurrentModel();

                await Task.Yield();

                // Instanciar modelo
                _currentModel = Instantiate(_modelPrefab, position, rotation, _modelParent);
                _currentModel.name = $"Model_{_modelPrefab.name}";
                _currentModel.transform.localScale = Vector3.one * _defaultScale;

                // Optimizar
                OptimizeModel(_currentModel);

                // Anclar en AR si está habilitado
                if (_useARAnchors)
                {
                    await CreateARAnchor(position, rotation);
                }

                _isModelLoaded = true;

                // Publicar evento
                EventBus.Instance?.Publish(new ModelLoadedEvent
                {
                    ModelInstance = _currentModel,
                    ModelName = _modelPrefab.name,
                    Position = position
                });

                PublishMessage($"Modelo cargado: {_modelPrefab.name}", MessageType.Success);
                Debug.Log($"[ModelLoadManager] ✅ Modelo cargado exitosamente");

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModelLoadManager] ❌ Error: {ex.Message}");
                
                EventBus.Instance?.Publish(new ModelLoadFailedEvent
                {
                    ModelName = _modelPrefab.name,
                    ErrorMessage = ex.Message
                });

                PublishMessage("Error cargando modelo", MessageType.Error);
                return false;
            }
        }

        /// <summary>
        /// Carga el modelo en el plano AR más grande detectado
        /// </summary>
        public async Task<bool> LoadModelOnLargestPlaneAsync()
        {
            if (_modelPrefab == null)
            {
                Debug.LogError("[ModelLoadManager] ❌ No hay modelo asignado");
                return false;
            }

            var arSessionManager = FindFirstObjectByType<AR.ARSessionManager>();

            if (arSessionManager == null)
            {
                Debug.LogError("[ModelLoadManager] ❌ ARSessionManager no encontrado");
                return false;
            }

            // Esperar a que haya planos detectados
            int maxWait = 10;
            while (arSessionManager.DetectedPlaneCount == 0 && maxWait > 0)
            {
                Debug.Log("[ModelLoadManager] ⏳ Esperando detección de planos...");
                await Task.Delay(500);
                maxWait--;
            }

            ARPlane largestPlane = arSessionManager.GetLargestPlane();

            if (largestPlane == null)
            {
                Debug.LogWarning("[ModelLoadManager] ⚠️ No hay planos detectados, cargando en origen");
                PublishMessage("No se detectaron superficies, cargando en origen", MessageType.Warning);
                return await LoadModel(Vector3.zero, Quaternion.identity);
            }

            Vector3 position = largestPlane.center;
            Quaternion rotation = Quaternion.identity;

            Debug.Log($"[ModelLoadManager] 🎯 Cargando en plano: área={largestPlane.size.x * largestPlane.size.y:F2}m²");

            return await LoadModel(position, rotation);
        }

        #endregion

        #region Model Optimization

        private void OptimizeModel(GameObject model)
        {
            // Desactivar colliders (el modelo es solo visual)
            var colliders = model.GetComponentsInChildren<Collider>();
            foreach (var col in colliders)
            {
                col.enabled = false;
            }

            // Desactivar sombras para mejor rendimiento
            var renderers = model.GetComponentsInChildren<Renderer>();
            foreach (var rend in renderers)
            {
                rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            Debug.Log($"[ModelLoadManager] 🔧 Optimizado: {colliders.Length} colliders, {renderers.Length} renderers");
        }

        #endregion

        #region AR Anchoring

        private async Task CreateARAnchor(Vector3 position, Quaternion rotation)
        {
            try
            {
                var anchorManager = FindFirstObjectByType<ARAnchorManager>();
                if (anchorManager == null)
                {
                    Debug.LogWarning("[ModelLoadManager] ⚠️ ARAnchorManager no encontrado");
                    return;
                }

                var arSessionManager = FindFirstObjectByType<AR.ARSessionManager>();
                if (arSessionManager == null || arSessionManager.DetectedPlaneCount == 0)
                {
                    Debug.LogWarning("[ModelLoadManager] ⚠️ No hay planos para anclar");
                    return;
                }

                await Task.Yield();

                ARPlane closestPlane = FindClosestPlane(arSessionManager, position);
                if (closestPlane == null)
                {
                    Debug.LogWarning("[ModelLoadManager] ⚠️ No se encontró plano cercano");
                    return;
                }

                // AR Foundation 6: usar Pose
                Pose pose = new Pose(position, rotation);
                _currentAnchor = anchorManager.AttachAnchor(closestPlane, pose);

                if (_currentAnchor != null)
                {
                    _currentModel.transform.SetParent(_currentAnchor.transform);
                    Debug.Log($"[ModelLoadManager] ⚓ Ancla AR creada: {_currentAnchor.trackableId}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModelLoadManager] ❌ Error creando ancla: {ex.Message}");
            }
        }

        private ARPlane FindClosestPlane(AR.ARSessionManager arSessionManager, Vector3 position)
        {
            ARPlane closestPlane = null;
            float minDistance = float.MaxValue;

            foreach (var kvp in arSessionManager.DetectedPlanes)
            {
                if (kvp.Value == null) continue;

                float distance = Vector3.Distance(position, kvp.Value.center);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    closestPlane = kvp.Value;
                }
            }

            return closestPlane;
        }

        #endregion

        #region Model Management

        public void UnloadCurrentModel()
        {
            if (_currentModel == null) return;

            // Remover ancla AR
            if (_currentAnchor != null)
            {
                var anchorManager = FindFirstObjectByType<ARAnchorManager>();
                if (anchorManager != null)
                {
                    anchorManager.TryRemoveAnchor(_currentAnchor);
                }
                _currentAnchor = null;
            }

            // Destruir modelo
            Destroy(_currentModel);
            _currentModel = null;
            _isModelLoaded = false;

            Debug.Log("[ModelLoadManager] 🗑️ Modelo descargado");
            PublishMessage("Modelo descargado", MessageType.Info);
        }

        public void UpdateModelPosition(Vector3 newPosition)
        {
            if (_currentModel != null)
            {
                _currentModel.transform.position = newPosition;
            }
        }

        public void UpdateModelRotation(Quaternion newRotation)
        {
            if (_currentModel != null)
            {
                _currentModel.transform.rotation = newRotation;
            }
        }

        public void UpdateModelScale(float scale)
        {
            if (_currentModel != null)
            {
                _currentModel.transform.localScale = Vector3.one * scale;
            }
        }

        #endregion

        #region Utilities

        private void PublishMessage(string message, MessageType type)
        {
            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message = message,
                Type = type,
                Duration = type == MessageType.Error ? 5f : 3f
            });
        }

        #endregion

        #region Debug

        [ContextMenu("🔨 Load Model on Largest Plane")]
        private void DebugLoadModel()
        {
            _ = LoadModelOnLargestPlaneAsync();
        }

        [ContextMenu("🗑️ Unload Model")]
        private void DebugUnloadModel()
        {
            UnloadCurrentModel();
        }

        [ContextMenu("ℹ️ Model Info")]
        private void DebugInfo()
        {
            Debug.Log("========== MODEL INFO ==========");
            Debug.Log($"Prefab: {(_modelPrefab != null ? _modelPrefab.name : "None")}");
            Debug.Log($"Loaded: {_isModelLoaded}");
            Debug.Log($"Current: {(_currentModel != null ? _currentModel.name : "None")}");
            Debug.Log($"Anchored: {(_currentAnchor != null ? "Yes" : "No")}");
            Debug.Log("================================");
        }

        #endregion
    }
}