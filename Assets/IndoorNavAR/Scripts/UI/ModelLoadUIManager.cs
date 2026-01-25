using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using System.IO;

namespace IndoorNavAR.UI
{
    /// <summary>
    /// Manager de UI para carga de modelos y configuración del minimapa
    /// Compatible con Unity 6.x
    /// </summary>
    public class ModelLoadUIManager : MonoBehaviour
    {
        [Header("Manager References")]
        [SerializeField] private Core.Managers.ModelLoaderManager2 modelLoader;
        [SerializeField] private Core.Managers.MinimapManager minimapManager;

        [Header("Model Loading UI")]
        [SerializeField] private GameObject loadModelPanel;
        [SerializeField] private Button btnLoadFromFile;
        [SerializeField] private Button btnLoadFromResources;
        [SerializeField] private TMP_InputField inputFilePath;
        [SerializeField] private TMP_InputField inputResourcePath;
        [SerializeField] private TextMeshProUGUI txtLoadStatus;

        [Header("Minimap Configuration UI")]
        [SerializeField] private GameObject minimapConfigPanel;
        [SerializeField] private RawImage minimapDisplay;
        [SerializeField] private Slider sliderCameraHeight;
        [SerializeField] private TextMeshProUGUI txtHeightValue;
        [SerializeField] private Button btnRecenterCamera;
        [SerializeField] private Button btnConfirmMinimap;
        [SerializeField] private Toggle toggleMinimapActive;

        [Header("Loading Animation")]
        [SerializeField] private GameObject loadingIndicator;

        [Header("Settings")]
        [SerializeField] private bool autoShowMinimapOnLoad = true;

        [Header("Debug")]
        [SerializeField] private bool debugMode = true;

        // Events
        public event Action OnMinimapConfigured;

        private bool isModelLoaded = false;

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateDependencies();
            InitializeUI();
        }

        private void OnEnable()
        {
            SubscribeToEvents();
        }

        private void OnDisable()
        {
            UnsubscribeFromEvents();
        }

        private void Start()
        {
            ShowLoadModelPanel();
        }

        #endregion

        #region Initialization

        private void ValidateDependencies()
        {
            if (modelLoader == null)
            {
                modelLoader = FindFirstObjectByType<Core.Managers.ModelLoaderManager2>();
                if (modelLoader == null)
                {
                    Debug.LogError("[ModelLoadUIManager] No se encontró ModelLoaderManager2 en la escena");
                    enabled = false;
                    return;
                }
            }

            if (minimapManager == null)
            {
                minimapManager = FindFirstObjectByType<Core.Managers.MinimapManager>();
                if (minimapManager == null)
                {
                    Debug.LogError("[ModelLoadUIManager] No se encontró MinimapManager en la escena");
                    enabled = false;
                    return;
                }
            }
        }

        private void InitializeUI()
        {
            // Configura botones
            if (btnLoadFromFile != null)
                btnLoadFromFile.onClick.AddListener(OnLoadFromFileClicked);

            if (btnLoadFromResources != null)
                btnLoadFromResources.onClick.AddListener(OnLoadFromResourcesClicked);

            if (btnRecenterCamera != null)
                btnRecenterCamera.onClick.AddListener(OnRecenterCameraClicked);

            if (btnConfirmMinimap != null)
                btnConfirmMinimap.onClick.AddListener(OnConfirmMinimapClicked);

            // Configura slider
            if (sliderCameraHeight != null)
            {
                sliderCameraHeight.minValue = 0f;
                sliderCameraHeight.maxValue = 1f;
                sliderCameraHeight.value = 0.5f;
                sliderCameraHeight.onValueChanged.AddListener(OnCameraHeightChanged);
            }

            // Configura toggle
            if (toggleMinimapActive != null)
            {
                toggleMinimapActive.isOn = true;
                toggleMinimapActive.onValueChanged.AddListener(OnMinimapToggleChanged);
            }

            // Asigna RenderTexture al display
            if (minimapDisplay != null && minimapManager != null)
            {
                minimapDisplay.texture = minimapManager.MinimapTexture;
            }

            // Estado inicial
            if (loadingIndicator != null)
                loadingIndicator.SetActive(false);

            Log("UI inicializada correctamente");
        }

        private void SubscribeToEvents()
        {
            if (modelLoader != null)
            {
                modelLoader.OnModelLoaded += HandleModelLoaded;
                modelLoader.OnModelLoadError += HandleModelLoadError;
                modelLoader.OnModelLoadStart += HandleModelLoadStart;
            }

            if (minimapManager != null)
            {
                minimapManager.OnCameraHeightChanged += HandleCameraHeightChanged;
            }
        }

        private void UnsubscribeFromEvents()
        {
            if (modelLoader != null)
            {
                modelLoader.OnModelLoaded -= HandleModelLoaded;
                modelLoader.OnModelLoadError -= HandleModelLoadError;
                modelLoader.OnModelLoadStart -= HandleModelLoadStart;
            }

            if (minimapManager != null)
            {
                minimapManager.OnCameraHeightChanged -= HandleCameraHeightChanged;
            }
        }

        #endregion

        #region UI Button Callbacks

        private void OnLoadFromFileClicked()
        {
            if (inputFilePath == null || string.IsNullOrEmpty(inputFilePath.text))
            {
                UpdateStatus("Por favor, ingresa una ruta de archivo", true);
                return;
            }

            modelLoader.LoadModelFromPath(inputFilePath.text);
        }

        private void OnLoadFromResourcesClicked()
        {
            if (inputResourcePath == null || string.IsNullOrEmpty(inputResourcePath.text))
            {
                UpdateStatus("Por favor, ingresa una ruta de Resources", true);
                return;
            }

            modelLoader.LoadModelFromResources(inputResourcePath.text);
        }

        private void OnRecenterCameraClicked()
        {
            minimapManager?.RecenterCamera();
            UpdateStatus("Cámara recentrada", false);
        }

        private void OnConfirmMinimapClicked()
        {
            if (!isModelLoaded)
            {
                UpdateStatus("No hay modelo cargado", true);
                return;
            }

            HideMinimapConfigPanel();
            OnMinimapConfigured?.Invoke();
            
            UpdateStatus("Minimapa configurado correctamente", false);
            Log("Usuario confirmó configuración del minimapa");
        }

        private void OnCameraHeightChanged(float normalizedValue)
        {
            minimapManager?.SetCameraHeightNormalized(normalizedValue);
        }

        private void OnMinimapToggleChanged(bool isActive)
        {
            minimapManager?.SetMinimapActive(isActive);
        }

        #endregion

        #region Event Handlers

        private void HandleModelLoaded(GameObject model)
        {
            isModelLoaded = true;
            
            if (loadingIndicator != null)
                loadingIndicator.SetActive(false);

            UpdateStatus($"Modelo cargado: {model.name}", false);

            // Configura el minimapa automáticamente
            minimapManager?.SetupMinimap(model);

            if (autoShowMinimapOnLoad)
            {
                ShowMinimapConfigPanel();
            }

            Log($"Modelo cargado exitosamente: {model.name}");
        }

        private void HandleModelLoadError(string errorMessage)
        {
            if (loadingIndicator != null)
                loadingIndicator.SetActive(false);

            UpdateStatus($"Error: {errorMessage}", true);
            Log($"Error al cargar modelo: {errorMessage}");
        }

        private void HandleModelLoadStart()
        {
            if (loadingIndicator != null)
                loadingIndicator.SetActive(true);

            UpdateStatus("Cargando modelo...", false);
        }

        private void HandleCameraHeightChanged(float height)
        {
            if (txtHeightValue != null)
            {
                txtHeightValue.text = $"{height:F2}m";
            }
        }

        #endregion

        #region Panel Management

        private void ShowLoadModelPanel()
        {
            if (loadModelPanel != null)
                loadModelPanel.SetActive(true);

            if (minimapConfigPanel != null)
                minimapConfigPanel.SetActive(false);
        }

        private void ShowMinimapConfigPanel()
        {
            if (loadModelPanel != null)
                loadModelPanel.SetActive(false);

            if (minimapConfigPanel != null)
                minimapConfigPanel.SetActive(true);

            // Actualiza el slider con el valor actual
            if (sliderCameraHeight != null && minimapManager != null)
            {
                sliderCameraHeight.value = minimapManager.GetNormalizedHeight();
            }
        }

        private void HideMinimapConfigPanel()
        {
            if (minimapConfigPanel != null)
                minimapConfigPanel.SetActive(false);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Muestra el panel de carga de modelo
        /// </summary>
        public void ShowModelLoadPanel()
        {
            ShowLoadModelPanel();
        }

        /// <summary>
        /// Muestra el panel de configuración del minimapa
        /// </summary>
        public void ShowMinimapPanel()
        {
            if (isModelLoaded)
            {
                ShowMinimapConfigPanel();
            }
            else
            {
                UpdateStatus("Primero debes cargar un modelo", true);
            }
        }

        /// <summary>
        /// Actualiza el texto de estado
        /// </summary>
        public void UpdateStatus(string message, bool isError = false)
        {
            if (txtLoadStatus != null)
            {
                txtLoadStatus.text = message;
                txtLoadStatus.color = isError ? Color.red : Color.white;
            }

            if (isError)
            {
                Log($"Status Error: {message}");
            }
        }

        /// <summary>
        /// Carga un modelo de prueba desde Resources
        /// </summary>
        public void LoadTestModel(string resourcePath = "Models/TestRoom")
        {
            if (inputResourcePath != null)
            {
                inputResourcePath.text = resourcePath;
            }
            
            OnLoadFromResourcesClicked();
        }

        #endregion

        #region Debug Logging

        private void Log(string message)
        {
            if (debugMode)
            {
                Debug.Log($"[ModelLoadUIManager] {message}");
            }
        }

        #endregion

        #region Editor Helpers

#if UNITY_EDITOR
        [ContextMenu("Setup Test UI References")]
        private void SetupTestReferences()
        {
            // Helper para autoasignar componentes en el editor
            if (modelLoader == null)
                modelLoader = FindFirstObjectByType<Core.Managers.ModelLoaderManager2>();

            if (minimapManager == null)
                minimapManager = FindFirstObjectByType<Core.Managers.MinimapManager>();

            Debug.Log("Referencias de prueba configuradas");
        }
#endif

        #endregion
    }
}