using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Core.Managers
{
    /// <summary>
    /// Gestor de carga dinámica de modelos 3D personalizados.
    /// Soporta carga desde Resources, AssetBundles y anclaje en AR.
    /// ✅ MODIFICADO: Ahora acepta prefabs directamente desde el Inspector.
    /// </summary>
    public class ModelLoadManager : MonoBehaviour
    {
        [Header("Modelo Predeterminado")]
        [Tooltip("Arrastra el prefab del modelo directamente aquí")]
        [SerializeField] private GameObject _defaultModelPrefab;
        
        [Header("Configuración Alternativa (Resources)")]
        [SerializeField] private string _resourcesFolder = "RoomModels";
        [SerializeField] private bool _useResourcesAsBackup = true;
        
        [Header("Configuración General")]
        [SerializeField] private Transform _modelParent;
        [SerializeField] private float _defaultModelScale = 1f;
        
        [Header("AR Anchoring")]
        [SerializeField] private bool _useARAnchors = true;

        private GameObject _currentModel;
        private ARAnchor _currentAnchor;
        private string _currentModelName;
        private bool _isModelLoaded;

        #region Properties

        public bool IsModelLoaded => _isModelLoaded;
        public GameObject CurrentModel => _currentModel;
        public string CurrentModelName => _currentModelName;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            CreateModelParent();
            ValidateDefaultPrefab();
        }

        #endregion

        #region Initialization

        private void CreateModelParent()
        {
            if (_modelParent == null)
            {
                GameObject parent = new GameObject("[3D Models]");
                _modelParent = parent.transform;
                Debug.Log("[ModelLoadManager] Contenedor de modelos creado.");
            }
        }

        private void ValidateDefaultPrefab()
        {
            if (_defaultModelPrefab == null)
            {
                Debug.LogWarning("[ModelLoadManager] No hay modelo predeterminado asignado. Usa el Inspector para asignar uno.");
            }
            else
            {
                Debug.Log($"[ModelLoadManager] Modelo predeterminado cargado: {_defaultModelPrefab.name}");
            }
        }

        #endregion

        #region Model Loading - Direct Prefab

        /// <summary>
        /// Carga el modelo predeterminado desde el prefab asignado en el Inspector.
        /// </summary>
        public async Task<bool> LoadDefaultModel(Vector3 position, Quaternion rotation)
        {
            if (_defaultModelPrefab == null)
            {
                Debug.LogError("[ModelLoadManager] No hay modelo predeterminado asignado.");
                
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "No hay modelo predeterminado configurado.",
                    Type = MessageType.Error,
                    Duration = 3f
                });

                return false;
            }

            return await LoadModelFromPrefab(_defaultModelPrefab, position, rotation);
        }

        /// <summary>
        /// Carga un modelo desde un prefab específico.
        /// </summary>
        public async Task<bool> LoadModelFromPrefab(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (prefab == null)
            {
                Debug.LogError("[ModelLoadManager] Prefab es null.");
                return false;
            }

            try
            {
                string modelName = prefab.name;
                Debug.Log($"[ModelLoadManager] Cargando modelo desde prefab: {modelName}");

                // Mostrar mensaje al usuario
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = $"Cargando modelo {modelName}...",
                    Type = MessageType.Info,
                    Duration = 2f
                });

                // Limpiar modelo anterior
                if (_currentModel != null)
                {
                    UnloadCurrentModel();
                }

                await Task.Yield();

                // Instanciar modelo
                _currentModel = Instantiate(prefab, position, rotation, _modelParent);
                _currentModel.name = $"Model_{modelName}";
                _currentModelName = modelName;

                // Aplicar escala
                _currentModel.transform.localScale = Vector3.one * _defaultModelScale;

                // Optimizar modelo
                OptimizeModel(_currentModel);

                // Crear ancla AR si está habilitado
                if (_useARAnchors)
                {
                    await CreateARAnchorForModel(position, rotation);
                }

                _isModelLoaded = true;

                // Publicar evento de éxito
                EventBus.Instance.Publish(new ModelLoadedEvent
                {
                    ModelInstance = _currentModel,
                    ModelName = modelName,
                    Position = position
                });

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = $"Modelo {modelName} cargado exitosamente.",
                    Type = MessageType.Success,
                    Duration = 3f
                });

                Debug.Log($"[ModelLoadManager] Modelo cargado exitosamente: {modelName}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModelLoadManager] Error cargando modelo: {ex.Message}");
                
                EventBus.Instance.Publish(new ModelLoadFailedEvent
                {
                    ModelName = prefab.name,
                    ErrorMessage = ex.Message
                });

                return false;
            }
        }

        #endregion

        #region Model Loading - Resources (Backup)

        /// <summary>
        /// Carga un modelo 3D desde la carpeta Resources de forma asíncrona.
        /// </summary>
        public async Task<bool> LoadModelFromResources(string modelName, Vector3 position, Quaternion rotation)
        {
            try
            {
                Debug.Log($"[ModelLoadManager] Cargando modelo desde Resources: {modelName}");

                // Mostrar mensaje al usuario
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = $"Cargando modelo {modelName}...",
                    Type = MessageType.Info,
                    Duration = 2f
                });

                // Limpiar modelo anterior
                if (_currentModel != null)
                {
                    UnloadCurrentModel();
                }

                await Task.Yield();

                string path = string.IsNullOrEmpty(_resourcesFolder) 
                    ? modelName 
                    : $"{_resourcesFolder}/{modelName}";
                
                GameObject modelPrefab = Resources.Load<GameObject>(path);

                if (modelPrefab == null)
                {
                    Debug.LogError($"[ModelLoadManager] Modelo no encontrado en Resources: {path}");
                    
                    EventBus.Instance.Publish(new ModelLoadFailedEvent
                    {
                        ModelName = modelName,
                        ErrorMessage = "Modelo no encontrado en Resources"
                    });

                    return false;
                }

                // Usar el método de prefab
                return await LoadModelFromPrefab(modelPrefab, position, rotation);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModelLoadManager] Error cargando modelo: {ex.Message}");
                
                EventBus.Instance.Publish(new ModelLoadFailedEvent
                {
                    ModelName = modelName,
                    ErrorMessage = ex.Message
                });

                return false;
            }
        }

        /// <summary>
        /// Carga el modelo predeterminado en la posición del plano AR más grande.
        /// </summary>
        public async Task<bool> LoadDefaultModelOnLargestPlane()
        {
            // Buscar el ARSessionManager
            var arSessionManager = FindFirstObjectByType<AR.ARSessionManager>();

            if (arSessionManager == null)
            {
                Debug.LogError("[ModelLoadManager] ARSessionManager no encontrado.");
                return false;
            }

            // Obtener plano más grande
            ARPlane largestPlane = arSessionManager.GetLargestPlane();

            if (largestPlane == null)
            {
                Debug.LogWarning("[ModelLoadManager] No hay planos AR detectados. Cargando en origen.");
                
                // Cargar en origen si no hay planos
                return await LoadDefaultModel(Vector3.zero, Quaternion.identity);
            }

            // Cargar en el centro del plano
            Vector3 position = largestPlane.center;
            Quaternion rotation = Quaternion.identity;

            return await LoadDefaultModel(position, rotation);
        }

        /// <summary>
        /// Carga un modelo desde Resources en la posición del plano AR más grande.
        /// </summary>
        public async Task<bool> LoadModelOnLargestPlane(string modelName)
        {
            // Buscar el ARSessionManager
            var arSessionManager = FindFirstObjectByType<AR.ARSessionManager>();

            if (arSessionManager == null)
            {
                Debug.LogError("[ModelLoadManager] ARSessionManager no encontrado.");
                return false;
            }

            // Obtener plano más grande
            ARPlane largestPlane = arSessionManager.GetLargestPlane();

            if (largestPlane == null)
            {
                Debug.LogWarning("[ModelLoadManager] No hay planos AR detectados.");
                
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "No se detectaron superficies. Busca una superficie plana.",
                    Type = MessageType.Warning,
                    Duration = 3f
                });

                return false;
            }

            // Cargar en el centro del plano
            Vector3 position = largestPlane.center;
            Quaternion rotation = Quaternion.identity;

            return await LoadModelFromResources(modelName, position, rotation);
        }

        #endregion

        #region Model Optimization

        /// <summary>
        /// Optimiza el modelo para mejor rendimiento en AR.
        /// </summary>
        private void OptimizeModel(GameObject model)
        {
            // Desactivar colliders innecesarios (el modelo es solo visual)
            Collider[] colliders = model.GetComponentsInChildren<Collider>();
            foreach (var collider in colliders)
            {
                collider.enabled = false;
            }

            // Opcional: Desactivar sombras para mejor performance
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            // Configurar capa (útil para raycasting selectivo)
            SetLayerRecursively(model, LayerMask.NameToLayer("Default"));

            Debug.Log($"[ModelLoadManager] Modelo optimizado: {colliders.Length} colliders desactivados, {renderers.Length} renderers configurados.");
        }

        private void SetLayerRecursively(GameObject obj, int layer)
        {
            obj.layer = layer;

            foreach (Transform child in obj.transform)
            {
                SetLayerRecursively(child.gameObject, layer);
            }
        }

        #endregion

        #region AR Anchoring

        /// <summary>
        /// Crea un ancla AR para el modelo (persistencia de posición).
        /// ✅ Compatible con AR Foundation 6 - Requiere ARPlane + Pose
        /// </summary>
        private async Task CreateARAnchorForModel(Vector3 position, Quaternion rotation)
        {
            try
            {
                // Buscar ARAnchorManager
                var anchorManager = FindFirstObjectByType<ARAnchorManager>();

                if (anchorManager == null)
                {
                    Debug.LogWarning("[ModelLoadManager] ARAnchorManager no disponible. Ancla no creada.");
                    return;
                }

                // Buscar ARSessionManager para obtener planos
                var arSessionManager = FindFirstObjectByType<AR.ARSessionManager>();

                if (arSessionManager == null || arSessionManager.DetectedPlaneCount == 0)
                {
                    Debug.LogWarning("[ModelLoadManager] No hay planos AR disponibles. Ancla no creada.");
                    return;
                }

                await Task.Yield();

                // Buscar el plano más cercano a la posición del modelo
                UnityEngine.XR.ARFoundation.ARPlane closestPlane = FindClosestPlane(arSessionManager, position);

                if (closestPlane == null)
                {
                    Debug.LogWarning("[ModelLoadManager] No se encontró plano cercano. Ancla no creada.");
                    return;
                }

                // ✅ AR Foundation 6: AttachAnchor requiere (ARPlane, Pose)
                Pose pose = new Pose(position, rotation);
                _currentAnchor = anchorManager.AttachAnchor(closestPlane, pose);

                if (_currentAnchor != null)
                {
                    // Hacer el modelo hijo del ancla para seguimiento
                    if (_currentModel != null)
                    {
                        _currentModel.transform.SetParent(_currentAnchor.transform);
                    }

                    Debug.Log($"[ModelLoadManager] Ancla AR creada para modelo: {_currentAnchor.trackableId}");
                }
                else
                {
                    Debug.LogWarning("[ModelLoadManager] AttachAnchor devolvió null. El modelo no estará anclado.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModelLoadManager] Error creando ancla AR: {ex.Message}");
            }
        }

        /// <summary>
        /// Encuentra el plano AR más cercano a una posición.
        /// </summary>
        private UnityEngine.XR.ARFoundation.ARPlane FindClosestPlane(AR.ARSessionManager arSessionManager, Vector3 position)
        {
            UnityEngine.XR.ARFoundation.ARPlane closestPlane = null;
            float minDistance = float.MaxValue;

            foreach (var kvp in arSessionManager.DetectedPlanes)
            {
                var plane = kvp.Value;
                
                if (plane == null)
                    continue;

                float distance = Vector3.Distance(position, plane.center);
                
                if (distance < minDistance)
                {
                    minDistance = distance;
                    closestPlane = plane;
                }
            }

            return closestPlane;
        }

        #endregion

        #region Model Manipulation

        /// <summary>
        /// Actualiza la posición del modelo actual.
        /// </summary>
        public void UpdateModelPosition(Vector3 newPosition)
        {
            if (_currentModel == null)
            {
                Debug.LogWarning("[ModelLoadManager] No hay modelo cargado.");
                return;
            }

            _currentModel.transform.position = newPosition;
            Debug.Log($"[ModelLoadManager] Posición del modelo actualizada: {newPosition}");
        }

        /// <summary>
        /// Actualiza la rotación del modelo actual.
        /// </summary>
        public void UpdateModelRotation(Quaternion newRotation)
        {
            if (_currentModel == null)
            {
                Debug.LogWarning("[ModelLoadManager] No hay modelo cargado.");
                return;
            }

            _currentModel.transform.rotation = newRotation;
            Debug.Log($"[ModelLoadManager] Rotación del modelo actualizada.");
        }

        /// <summary>
        /// Actualiza la escala del modelo actual.
        /// </summary>
        public void UpdateModelScale(float scale)
        {
            if (_currentModel == null)
            {
                Debug.LogWarning("[ModelLoadManager] No hay modelo cargado.");
                return;
            }

            _currentModel.transform.localScale = Vector3.one * scale;
            Debug.Log($"[ModelLoadManager] Escala del modelo actualizada: {scale}");
        }

        /// <summary>
        /// Rota el modelo en el eje Y (útil para ajustar orientación).
        /// </summary>
        public void RotateModelY(float degrees)
        {
            if (_currentModel == null)
                return;

            _currentModel.transform.Rotate(Vector3.up, degrees, Space.World);
        }

        #endregion

        #region Model Unloading

        /// <summary>
        /// Descarga el modelo actual.
        /// </summary>
        public void UnloadCurrentModel()
        {
            if (_currentModel != null)
            {
                // ✅ Destruir ancla si existe - AR Foundation 6
                if (_currentAnchor != null)
                {
                    var anchorManager = FindFirstObjectByType<ARAnchorManager>();
                    if (anchorManager != null)
                    {
                        // Usar TryRemoveAnchor en lugar de RemoveAnchor
                        if (!anchorManager.TryRemoveAnchor(_currentAnchor))
                        {
                            Debug.LogWarning("[ModelLoadManager] No se pudo remover ancla.");
                        }
                    }
                    _currentAnchor = null;
                }

                // Destruir modelo
                Destroy(_currentModel);
                _currentModel = null;
                _currentModelName = null;
                _isModelLoaded = false;

                Debug.Log("[ModelLoadManager] Modelo descargado.");

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Modelo descargado.",
                    Type = MessageType.Info,
                    Duration = 2f
                });
            }
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Verifica si hay un modelo predeterminado asignado.
        /// </summary>
        public bool HasDefaultModel()
        {
            return _defaultModelPrefab != null;
        }

        /// <summary>
        /// Verifica si un modelo existe en Resources.
        /// </summary>
        public bool ModelExists(string modelName)
        {
            string path = string.IsNullOrEmpty(_resourcesFolder) 
                ? modelName 
                : $"{_resourcesFolder}/{modelName}";
            
            GameObject modelPrefab = Resources.Load<GameObject>(path);
            return modelPrefab != null;
        }

        #endregion

        #region Debug

        [ContextMenu("Debug: Load Default Model")]
        private void DebugLoadDefaultModel()
        {
            _ = LoadDefaultModel(Vector3.zero, Quaternion.identity);
        }

        [ContextMenu("Debug: Load Default Model On Largest Plane")]
        private void DebugLoadDefaultModelOnPlane()
        {
            _ = LoadDefaultModelOnLargestPlane();
        }

        [ContextMenu("Debug: Unload Current Model")]
        private void DebugUnloadModel()
        {
            UnloadCurrentModel();
        }

        #endregion
    }
}