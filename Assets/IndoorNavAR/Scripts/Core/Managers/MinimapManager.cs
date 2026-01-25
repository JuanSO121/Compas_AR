using UnityEngine;
using System;

namespace IndoorNavAR.Core.Managers
{
    /// <summary>
    /// Gestor del minimapa con vista superior ortográfica
    /// Permite ajustar altura de cámara para atravesar techos
    /// Compatible con Unity 6.x
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class MinimapManager : MonoBehaviour
    {
        [Header("Camera Settings")]
        [SerializeField] private float defaultHeight = 5f;
        [SerializeField] private float minHeight = 2f;
        [SerializeField] private float maxHeight = 20f;
        [SerializeField] private float orthographicSize = 10f;

        [Header("Render Texture")]
        [SerializeField] private RenderTexture minimapRenderTexture;
        [SerializeField] private int textureWidth = 512;
        [SerializeField] private int textureHeight = 512;

        [Header("References")]
        [SerializeField] private Transform targetModel;
        [SerializeField] private LayerMask minimapLayers = -1;

        [Header("Debug")]
        [SerializeField] private bool debugMode = true;

        // Events
        public event Action<float> OnCameraHeightChanged;

        private Camera minimapCamera;
        private bool isInitialized = false;
        private Vector3 modelCenter;

        #region Properties

        public Camera MinimapCamera => minimapCamera;
        public RenderTexture MinimapTexture => minimapRenderTexture;
        public float CurrentHeight => transform.position.y;
        public float MinHeight => minHeight;
        public float MaxHeight => maxHeight;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            InitializeCamera();
        }

        private void OnValidate()
        {
            // Clamp valores en el editor
            defaultHeight = Mathf.Clamp(defaultHeight, minHeight, maxHeight);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Configura el minimapa con un modelo target
        /// </summary>
        public void SetupMinimap(GameObject model)
        {
            if (model == null)
            {
                LogWarning("No se puede configurar minimapa con un modelo null");
                return;
            }

            targetModel = model.transform;
            CalculateModelCenter();
            PositionCameraAboveModel();
            
            isInitialized = true;
            Log($"Minimapa configurado para modelo: {model.name}");
        }

        /// <summary>
        /// Ajusta la altura de la cámara (0-1 normalizado)
        /// </summary>
        public void SetCameraHeightNormalized(float normalizedHeight)
        {
            if (!isInitialized)
            {
                LogWarning("Minimapa no inicializado");
                return;
            }

            float clampedValue = Mathf.Clamp01(normalizedHeight);
            float newHeight = Mathf.Lerp(minHeight, maxHeight, clampedValue);
            
            SetCameraHeight(newHeight);
        }

        /// <summary>
        /// Ajusta la altura de la cámara (valor absoluto)
        /// </summary>
        public void SetCameraHeight(float height)
        {
            float clampedHeight = Mathf.Clamp(height, minHeight, maxHeight);
            
            Vector3 newPosition = transform.position;
            newPosition.y = modelCenter.y + clampedHeight;
            transform.position = newPosition;

            OnCameraHeightChanged?.Invoke(clampedHeight);
            Log($"Altura de cámara ajustada a: {clampedHeight:F2}m");
        }

        /// <summary>
        /// Obtiene la altura normalizada (0-1)
        /// </summary>
        public float GetNormalizedHeight()
        {
            float currentRelativeHeight = transform.position.y - modelCenter.y;
            return Mathf.InverseLerp(minHeight, maxHeight, currentRelativeHeight);
        }

        /// <summary>
        /// Ajusta el tamaño ortográfico (zoom)
        /// </summary>
        public void SetOrthographicSize(float size)
        {
            if (minimapCamera != null)
            {
                minimapCamera.orthographicSize = Mathf.Max(0.1f, size);
                Log($"Tamaño ortográfico ajustado a: {size:F2}");
            }
        }

        /// <summary>
        /// Activa/desactiva la cámara del minimapa
        /// </summary>
        public void SetMinimapActive(bool active)
        {
            if (minimapCamera != null)
            {
                minimapCamera.enabled = active;
                gameObject.SetActive(active);
                Log($"Minimapa {(active ? "activado" : "desactivado")}");
            }
        }

        /// <summary>
        /// Recentra la cámara sobre el modelo
        /// </summary>
        public void RecenterCamera()
        {
            if (targetModel != null)
            {
                CalculateModelCenter();
                PositionCameraAboveModel();
                Log("Cámara recentrada");
            }
        }

        #endregion

        #region Private Methods

        private void InitializeCamera()
        {
            // Obtiene o configura la cámara
            if (!TryGetComponent<Camera>(out minimapCamera))
            {
                LogWarning("No se encontró componente Camera, esto no debería pasar con RequireComponent");
                return;
            }

            // Configuración de cámara ortográfica
            minimapCamera.orthographic = true;
            minimapCamera.orthographicSize = orthographicSize;
            minimapCamera.cullingMask = minimapLayers;
            minimapCamera.clearFlags = CameraClearFlags.SolidColor;
            minimapCamera.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 1f);
            minimapCamera.depth = -10; // Render antes que la cámara principal

            // Rotación para vista superior
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            // Crea render texture si no existe
            if (minimapRenderTexture == null)
            {
                CreateRenderTexture();
            }

            minimapCamera.targetTexture = minimapRenderTexture;

            Log("Cámara de minimapa inicializada");
        }

        private void CreateRenderTexture()
        {
            minimapRenderTexture = new RenderTexture(textureWidth, textureHeight, 16)
            {
                name = "MinimapRenderTexture",
                filterMode = FilterMode.Bilinear,
                autoGenerateMips = false
            };

            Log($"RenderTexture creada: {textureWidth}x{textureHeight}");
        }

        private void CalculateModelCenter()
        {
            if (targetModel == null) return;

            // Calcula el centro usando bounds de todos los renderers
            Renderer[] renderers = targetModel.GetComponentsInChildren<Renderer>();
            
            if (renderers.Length == 0)
            {
                modelCenter = targetModel.position;
                Log("No se encontraron renderers, usando posición del transform");
                return;
            }

            Bounds combinedBounds = renderers[0].bounds;
            
            for (int i = 1; i < renderers.Length; i++)
            {
                combinedBounds.Encapsulate(renderers[i].bounds);
            }

            modelCenter = combinedBounds.center;
            
            Log($"Centro del modelo calculado: {modelCenter}");
        }

        private void PositionCameraAboveModel()
        {
            Vector3 cameraPosition = modelCenter;
            cameraPosition.y += defaultHeight;
            
            transform.position = cameraPosition;
            
            Log($"Cámara posicionada en: {cameraPosition}");
        }

        #endregion

        #region Debug Logging

        private void Log(string message)
        {
            if (debugMode)
            {
                Debug.Log($"[MinimapManager] {message}");
            }
        }

        private void LogWarning(string message)
        {
            if (debugMode)
            {
                Debug.LogWarning($"[MinimapManager] {message}");
            }
        }

        #endregion

        #region Cleanup

        private void OnDestroy()
        {
            // Limpia el RenderTexture si fue creado dinámicamente
            if (minimapRenderTexture != null && minimapRenderTexture.name == "MinimapRenderTexture")
            {
                minimapRenderTexture.Release();
                Destroy(minimapRenderTexture);
            }
        }

        #endregion
    }
}