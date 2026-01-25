using UnityEngine;
using System;
using System.IO;
using System.Collections;

namespace IndoorNavAR.Core.Managers
{
    /// <summary>
    /// Gestor de carga de modelos 3D (.fbx, .obj) para mapas indoor
    /// Compatible con Unity 6.x
    /// </summary>
    public class ModelLoaderManager2 : MonoBehaviour
    {
        [Header("Model Settings")]
        [SerializeField] private Transform modelParent;
        [SerializeField] private Material defaultModelMaterial;
        [SerializeField] private float defaultScale = 1f;

        [Header("Debug")]
        [SerializeField] private bool debugMode = true;

        // Events
        public event Action<GameObject> OnModelLoaded;
        public event Action<string> OnModelLoadError;
        public event Action OnModelLoadStart;

        private GameObject currentLoadedModel;
        private bool isLoading = false;

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateDependencies();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Carga un modelo desde una ruta del sistema de archivos
        /// </summary>
        public void LoadModelFromPath(string filePath)
        {
            if (isLoading)
            {
                LogWarning("Ya hay un modelo cargándose. Espera a que termine.");
                return;
            }

            if (string.IsNullOrEmpty(filePath))
            {
                OnModelLoadError?.Invoke("La ruta del archivo está vacía");
                return;
            }

            if (!File.Exists(filePath))
            {
                OnModelLoadError?.Invoke($"El archivo no existe: {filePath}");
                return;
            }

            string extension = Path.GetExtension(filePath).ToLower();
            
            if (extension != ".fbx" && extension != ".obj")
            {
                OnModelLoadError?.Invoke($"Formato no soportado: {extension}. Use .fbx o .obj");
                return;
            }

            StartCoroutine(LoadModelCoroutine(filePath, extension));
        }

        /// <summary>
        /// Carga un modelo desde Resources (para testing)
        /// </summary>
        public void LoadModelFromResources(string resourcePath)
        {
            if (isLoading)
            {
                LogWarning("Ya hay un modelo cargándose.");
                return;
            }

            StartCoroutine(LoadFromResourcesCoroutine(resourcePath));
        }

        /// <summary>
        /// Limpia el modelo actual
        /// </summary>
        public void ClearCurrentModel()
        {
            if (currentLoadedModel != null)
            {
                Destroy(currentLoadedModel);
                currentLoadedModel = null;
                Log("Modelo eliminado");
            }
        }

        /// <summary>
        /// Obtiene el modelo actualmente cargado
        /// </summary>
        public GameObject GetCurrentModel()
        {
            return currentLoadedModel;
        }

        #endregion

        #region Private Methods - Loading

        private IEnumerator LoadModelCoroutine(string filePath, string extension)
        {
            isLoading = true;
            OnModelLoadStart?.Invoke();

            Log($"Iniciando carga de modelo: {Path.GetFileName(filePath)}");

            // Limpia el modelo anterior
            ClearCurrentModel();

            yield return new WaitForSeconds(0.1f);

            // En Unity, los modelos .fbx/.obj deben estar en Assets para ser cargados
            // Para runtime, necesitarías un plugin como TriLib o similar
            // Por ahora, simulamos la carga desde Resources o StreamingAssets
            
            string modelName = Path.GetFileNameWithoutExtension(filePath);
            
            // Intenta cargar desde StreamingAssets
            string streamingPath = Path.Combine(Application.streamingAssetsPath, "Models", modelName);
            
            if (extension == ".obj")
            {
                // Para .obj necesitarías un parser runtime
                OnModelLoadError?.Invoke("La carga runtime de .obj requiere un plugin adicional (TriLib, RuntimeOBJImporter)");
                isLoading = false;
                yield break;
            }

            // Para desarrollo: carga desde Resources
            GameObject prefab = Resources.Load<GameObject>($"Models/{modelName}");
            
            if (prefab == null)
            {
                OnModelLoadError?.Invoke($"No se encontró el modelo en Resources/Models/{modelName}");
                isLoading = false;
                yield break;
            }

            // Instancia el modelo
            currentLoadedModel = Instantiate(prefab, modelParent);
            currentLoadedModel.name = modelName;
            currentLoadedModel.transform.localPosition = Vector3.zero;
            currentLoadedModel.transform.localRotation = Quaternion.identity;
            currentLoadedModel.transform.localScale = Vector3.one * defaultScale;

            // Aplica material si no tiene
            ApplyDefaultMaterialIfNeeded(currentLoadedModel);

            // Añade layer para minimapa
            SetLayerRecursively(currentLoadedModel, LayerMask.NameToLayer("Default"));

            Log($"Modelo cargado exitosamente: {modelName}");
            OnModelLoaded?.Invoke(currentLoadedModel);

            isLoading = false;
        }

        private IEnumerator LoadFromResourcesCoroutine(string resourcePath)
        {
            isLoading = true;
            OnModelLoadStart?.Invoke();

            Log($"Cargando desde Resources: {resourcePath}");

            ClearCurrentModel();

            yield return new WaitForSeconds(0.1f);

            GameObject prefab = Resources.Load<GameObject>(resourcePath);

            if (prefab == null)
            {
                OnModelLoadError?.Invoke($"No se encontró el recurso: {resourcePath}");
                isLoading = false;
                yield break;
            }

            currentLoadedModel = Instantiate(prefab, modelParent);
            currentLoadedModel.name = Path.GetFileName(resourcePath);
            currentLoadedModel.transform.localPosition = Vector3.zero;
            currentLoadedModel.transform.localRotation = Quaternion.identity;
            currentLoadedModel.transform.localScale = Vector3.one * defaultScale;

            ApplyDefaultMaterialIfNeeded(currentLoadedModel);
            SetLayerRecursively(currentLoadedModel, LayerMask.NameToLayer("Default"));

            Log($"Modelo cargado desde Resources: {resourcePath}");
            OnModelLoaded?.Invoke(currentLoadedModel);

            isLoading = false;
        }

        #endregion

        #region Private Methods - Utilities

        private void ApplyDefaultMaterialIfNeeded(GameObject model)
        {
            if (defaultModelMaterial == null) return;

            MeshRenderer[] renderers = model.GetComponentsInChildren<MeshRenderer>();
            
            foreach (MeshRenderer renderer in renderers)
            {
                if (renderer.sharedMaterial == null)
                {
                    renderer.sharedMaterial = defaultModelMaterial;
                }
            }

            Log($"Material aplicado a {renderers.Length} renderers");
        }

        private void SetLayerRecursively(GameObject obj, int layer)
        {
            obj.layer = layer;

            foreach (Transform child in obj.transform)
            {
                SetLayerRecursively(child.gameObject, layer);
            }
        }

        private void ValidateDependencies()
        {
            if (modelParent == null)
            {
                GameObject parentObj = new GameObject("ModelContainer");
                modelParent = parentObj.transform;
                LogWarning("No se asignó ModelParent, se creó automáticamente");
            }
        }

        #endregion

        #region Debug Logging

        private void Log(string message)
        {
            if (debugMode)
            {
                Debug.Log($"[ModelLoaderManager2] {message}");
            }
        }

        private void LogWarning(string message)
        {
            if (debugMode)
            {
                Debug.LogWarning($"[ModelLoaderManager2] {message}");
            }
        }

        #endregion
    }
}