using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using IndoorNavAR.Core.Events;
using IndoorNavAR.UI.Panels;

namespace IndoorNavAR.UI
{
    /// <summary>
    /// Gestor principal de la interfaz de usuario.
    /// Coordina paneles, mensajes y estados visuales de la aplicación.
    /// </summary>
    public class UIManager : MonoBehaviour
    {
        [Header("Paneles Principales")]
        [SerializeField] private GameObject _mainMenuPanel;
        [SerializeField] private WaypointConfigPanel _waypointConfigPanel;
        [SerializeField] private NavigationPanel _navigationPanel;
        [SerializeField] private GameObject _settingsPanel;

        [Header("HUD")]
        [SerializeField] private TextMeshProUGUI _statusText;
        [SerializeField] private TextMeshProUGUI _planeCountText;
        [SerializeField] private TextMeshProUGUI _waypointCountText;
        [SerializeField] private TextMeshProUGUI _navMeshStatusText;

        [Header("Botones Principales")]
        [SerializeField] private Button _placementModeButton;
        [SerializeField] private Button _generateNavMeshButton;
        [SerializeField] private Button _navigationButton;
        [SerializeField] private Button _clearAllButton;
        [SerializeField] private Button _settingsButton;

        [Header("Sistema de Mensajes")]
        [SerializeField] private GameObject _messagePanel;
        [SerializeField] private TextMeshProUGUI _messageText;
        [SerializeField] private Image _messageBackground;
        [SerializeField] private float _messageFadeDuration = 0.3f;

        [Header("Colores de Mensaje")]
        [SerializeField] private Color _infoColor = new Color(0.2f, 0.6f, 1f);
        [SerializeField] private Color _successColor = new Color(0.2f, 0.8f, 0.2f);
        [SerializeField] private Color _warningColor = new Color(1f, 0.8f, 0.2f);
        [SerializeField] private Color _errorColor = new Color(1f, 0.2f, 0.2f);

        private AppMode _currentMode = AppMode.Initialization;
        private Coroutine _messageCoroutine;
        private int _detectedPlaneCount;
        private int _waypointCount;
        private bool _isNavMeshGenerated;

        #region Properties

        public AppMode CurrentMode => _currentMode;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateDependencies();
            InitializeUI();
        }

        private void OnEnable()
        {
            SubscribeToEvents();
            SetupButtonListeners();
        }

        private void OnDisable()
        {
            UnsubscribeFromEvents();
            RemoveButtonListeners();
        }

        private void Start()
        {
            SetAppMode(AppMode.PlaneDetection);
            HideAllPanels();
            
            if (_messagePanel != null)
            {
                _messagePanel.SetActive(false);
            }
        }

        #endregion

        #region Initialization

        private void ValidateDependencies()
        {
            if (_statusText == null)
                Debug.LogWarning("[UIManager] Status Text no asignado.");

            if (_waypointConfigPanel == null)
                Debug.LogWarning("[UIManager] WaypointConfigPanel no asignado.");

            if (_navigationPanel == null)
                Debug.LogWarning("[UIManager] NavigationPanel no asignado.");

            if (_messagePanel == null)
                Debug.LogWarning("[UIManager] Message Panel no asignado.");
        }

        private void InitializeUI()
        {
            UpdateHUD();
            
            // Configurar estado inicial de botones
            if (_generateNavMeshButton != null)
                _generateNavMeshButton.interactable = false;

            if (_navigationButton != null)
                _navigationButton.interactable = false;
        }

        #endregion

        #region Event Subscriptions

        private void SubscribeToEvents()
        {
            EventBus.Instance.Subscribe<PlaneDetectedEvent>(OnPlaneDetected);
            EventBus.Instance.Subscribe<PlaneRemovedEvent>(OnPlaneRemoved);
            EventBus.Instance.Subscribe<WaypointPlacedEvent>(OnWaypointPlaced);
            EventBus.Instance.Subscribe<WaypointRemovedEvent>(OnWaypointRemoved);
            EventBus.Instance.Subscribe<NavMeshGeneratedEvent>(OnNavMeshGenerated);
            EventBus.Instance.Subscribe<NavigationStartedEvent>(OnNavigationStarted);
            EventBus.Instance.Subscribe<NavigationCompletedEvent>(OnNavigationCompleted);
            EventBus.Instance.Subscribe<ShowMessageEvent>(OnShowMessage);
            EventBus.Instance.Subscribe<AppModeChangedEvent>(OnAppModeChanged);
        }

        private void UnsubscribeFromEvents()
        {
            EventBus.Instance.Unsubscribe<PlaneDetectedEvent>(OnPlaneDetected);
            EventBus.Instance.Unsubscribe<PlaneRemovedEvent>(OnPlaneRemoved);
            EventBus.Instance.Unsubscribe<WaypointPlacedEvent>(OnWaypointPlaced);
            EventBus.Instance.Unsubscribe<WaypointRemovedEvent>(OnWaypointRemoved);
            EventBus.Instance.Unsubscribe<NavMeshGeneratedEvent>(OnNavMeshGenerated);
            EventBus.Instance.Unsubscribe<NavigationStartedEvent>(OnNavigationStarted);
            EventBus.Instance.Unsubscribe<NavigationCompletedEvent>(OnNavigationCompleted);
            EventBus.Instance.Unsubscribe<ShowMessageEvent>(OnShowMessage);
            EventBus.Instance.Unsubscribe<AppModeChangedEvent>(OnAppModeChanged);
        }

        #endregion

        #region Event Handlers

        private void OnPlaneDetected(PlaneDetectedEvent evt)
        {
            _detectedPlaneCount++;
            UpdateHUD();

            // Habilitar generación de NavMesh cuando hay al menos 1 plano
            if (_generateNavMeshButton != null)
                _generateNavMeshButton.interactable = true;
        }

        private void OnPlaneRemoved(PlaneRemovedEvent evt)
        {
            _detectedPlaneCount = Mathf.Max(0, _detectedPlaneCount - 1);
            UpdateHUD();
        }

        private void OnWaypointPlaced(WaypointPlacedEvent evt)
        {
            _waypointCount++;
            UpdateHUD();
        }

        private void OnWaypointRemoved(WaypointRemovedEvent evt)
        {
            _waypointCount = Mathf.Max(0, _waypointCount - 1);
            UpdateHUD();
        }

        private void OnNavMeshGenerated(NavMeshGeneratedEvent evt)
        {
            _isNavMeshGenerated = evt.Success;
            UpdateHUD();

            // Habilitar navegación si hay waypoints y NavMesh
            if (_navigationButton != null)
                _navigationButton.interactable = _waypointCount > 0 && _isNavMeshGenerated;
        }

        private void OnNavigationStarted(NavigationStartedEvent evt)
        {
            SetAppMode(AppMode.Navigation);
        }

        private void OnNavigationCompleted(NavigationCompletedEvent evt)
        {
            SetAppMode(AppMode.WaypointPlacement);
        }

        private void OnShowMessage(ShowMessageEvent evt)
        {
            ShowMessage(evt.Message, evt.Type, evt.Duration);
        }

        private void OnAppModeChanged(AppModeChangedEvent evt)
        {
            UpdateModeVisuals();
        }

        #endregion

        #region Button Listeners

        private void SetupButtonListeners()
        {
            if (_placementModeButton != null)
                _placementModeButton.onClick.AddListener(OnPlacementModeClicked);

            if (_generateNavMeshButton != null)
                _generateNavMeshButton.onClick.AddListener(OnGenerateNavMeshClicked);

            if (_navigationButton != null)
                _navigationButton.onClick.AddListener(OnNavigationClicked);

            if (_clearAllButton != null)
                _clearAllButton.onClick.AddListener(OnClearAllClicked);

            if (_settingsButton != null)
                _settingsButton.onClick.AddListener(OnSettingsClicked);
        }

        private void RemoveButtonListeners()
        {
            if (_placementModeButton != null)
                _placementModeButton.onClick.RemoveListener(OnPlacementModeClicked);

            if (_generateNavMeshButton != null)
                _generateNavMeshButton.onClick.RemoveListener(OnGenerateNavMeshClicked);

            if (_navigationButton != null)
                _navigationButton.onClick.RemoveListener(OnNavigationClicked);

            if (_clearAllButton != null)
                _clearAllButton.onClick.RemoveListener(OnClearAllClicked);

            if (_settingsButton != null)
                _settingsButton.onClick.RemoveListener(OnSettingsClicked);
        }

        private void OnPlacementModeClicked()
        {
            var placementController = FindFirstObjectByType<Core.Controllers.PlacementController>();
            
            if (placementController != null)
            {
                bool newState = !placementController.IsPlacementActive;
                placementController.TogglePlacementMode(newState);
                
                // Actualizar visual del botón
                UpdateButtonState(_placementModeButton, newState);
                
                if (newState)
                {
                    SetAppMode(AppMode.WaypointPlacement);
                }
            }
        }

        private async void OnGenerateNavMeshClicked()
        {
            var navMeshGenerator = FindFirstObjectByType<Navigation.NavMeshGenerator>();
            
            if (navMeshGenerator != null)
            {
                // Deshabilitar botón durante generación
                if (_generateNavMeshButton != null)
                    _generateNavMeshButton.interactable = false;

                await navMeshGenerator.RegenerateNavMesh();

                // Re-habilitar botón
                if (_generateNavMeshButton != null)
                    _generateNavMeshButton.interactable = true;
            }
        }

        private void OnNavigationClicked()
        {
            // Abrir panel de navegación
            if (_navigationPanel != null)
            {
                ShowPanel(_navigationPanel.gameObject);
            }
        }

        private void OnClearAllClicked()
        {
            // Confirmación antes de limpiar
            ShowConfirmationDialog(
                "¿Eliminar todos los waypoints y NavMesh?",
                () =>
                {
                    var waypointManager = FindFirstObjectByType<Core.Managers.WaypointManager>();
                    var navMeshGenerator = FindFirstObjectByType<Navigation.NavMeshGenerator>();

                    waypointManager?.ClearAllWaypoints();
                    navMeshGenerator?.ClearNavMesh();

                    _waypointCount = 0;
                    _isNavMeshGenerated = false;
                    UpdateHUD();

                    ShowMessage("Todo limpiado correctamente.", MessageType.Success, 2f);
                }
            );
        }

        private void OnSettingsClicked()
        {
            if (_settingsPanel != null)
            {
                ShowPanel(_settingsPanel);
            }
        }

        #endregion

        #region App Mode Management

        public void SetAppMode(AppMode newMode)
        {
            AppMode previousMode = _currentMode;
            _currentMode = newMode;

            EventBus.Instance.Publish(new AppModeChangedEvent
            {
                PreviousMode = previousMode,
                NewMode = newMode
            });

            UpdateModeVisuals();
            Debug.Log($"[UIManager] Modo cambiado: {previousMode} → {newMode}");
        }

        private void UpdateModeVisuals()
        {
            if (_statusText != null)
            {
                _statusText.text = GetModeDisplayText(_currentMode);
            }

            // Actualizar visibilidad de paneles según modo
            switch (_currentMode)
            {
                case AppMode.Initialization:
                    HideAllPanels();
                    break;

                case AppMode.PlaneDetection:
                    HideAllPanels();
                    ShowMessage("Busca superficies horizontales moviendo el dispositivo.", MessageType.Info, 3f);
                    break;

                case AppMode.WaypointPlacement:
                    HideAllPanels();
                    break;

                case AppMode.WaypointConfiguration:
                    ShowPanel(_waypointConfigPanel?.gameObject);
                    break;

                case AppMode.Navigation:
                    ShowPanel(_navigationPanel?.gameObject);
                    break;

                case AppMode.Settings:
                    ShowPanel(_settingsPanel);
                    break;
            }
        }

        private string GetModeDisplayText(AppMode mode)
        {
            return mode switch
            {
                AppMode.Initialization => "Inicializando...",
                AppMode.PlaneDetection => "Detectando Superficies",
                AppMode.ModelPlacement => "Colocando Modelo",
                AppMode.WaypointPlacement => "Modo Colocación de Waypoints",
                AppMode.WaypointConfiguration => "Configurando Waypoint",
                AppMode.Navigation => "Navegando",
                AppMode.Settings => "Configuración",
                _ => "Listo"
            };
        }

        #endregion

        #region Panel Management

        private void HideAllPanels()
        {
            if (_mainMenuPanel != null)
                _mainMenuPanel.SetActive(false);

            if (_waypointConfigPanel != null)
                _waypointConfigPanel.gameObject.SetActive(false);

            if (_navigationPanel != null)
                _navigationPanel.gameObject.SetActive(false);

            if (_settingsPanel != null)
                _settingsPanel.SetActive(false);
        }

        private void ShowPanel(GameObject panel)
        {
            if (panel == null)
                return;

            HideAllPanels();
            panel.SetActive(true);
        }

        #endregion

        #region HUD Updates

        private void UpdateHUD()
        {
            if (_planeCountText != null)
                _planeCountText.text = $"Planos: {_detectedPlaneCount}";

            if (_waypointCountText != null)
                _waypointCountText.text = $"Waypoints: {_waypointCount}";

            if (_navMeshStatusText != null)
            {
                _navMeshStatusText.text = _isNavMeshGenerated 
                    ? "<color=green>NavMesh: ✓</color>" 
                    : "<color=red>NavMesh: ✗</color>";
            }
        }

        #endregion

        #region Message System

        public void ShowMessage(string message, MessageType type, float duration)
        {
            if (_messagePanel == null || _messageText == null)
                return;

            // Detener mensaje anterior si existe
            if (_messageCoroutine != null)
            {
                StopCoroutine(_messageCoroutine);
            }

            _messageCoroutine = StartCoroutine(ShowMessageCoroutine(message, type, duration));
        }

        private IEnumerator ShowMessageCoroutine(string message, MessageType type, float duration)
        {
            // Configurar color según tipo
            Color backgroundColor = GetColorForMessageType(type);

            if (_messageBackground != null)
            {
                _messageBackground.color = backgroundColor;
            }

            _messageText.text = message;

            // Fade In
            yield return FadeMessage(0f, 1f);

            // Mostrar mensaje
            yield return new WaitForSeconds(duration);

            // Fade Out
            yield return FadeMessage(1f, 0f);

            _messagePanel.SetActive(false);
        }

        private IEnumerator FadeMessage(float fromAlpha, float toAlpha)
        {
            _messagePanel.SetActive(true);

            CanvasGroup canvasGroup = _messagePanel.GetComponent<CanvasGroup>();
            
            if (canvasGroup == null)
            {
                canvasGroup = _messagePanel.AddComponent<CanvasGroup>();
            }

            float elapsed = 0f;

            while (elapsed < _messageFadeDuration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / _messageFadeDuration;
                canvasGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, t);
                yield return null;
            }

            canvasGroup.alpha = toAlpha;
        }

        private Color GetColorForMessageType(MessageType type)
        {
            return type switch
            {
                MessageType.Info => _infoColor,
                MessageType.Success => _successColor,
                MessageType.Warning => _warningColor,
                MessageType.Error => _errorColor,
                _ => _infoColor
            };
        }

        #endregion

        #region Confirmation Dialog

        private void ShowConfirmationDialog(string message, Action onConfirm)
        {
            // Implementación básica - puedes expandir con un panel dedicado
            ShowMessage($"{message} (Implementar diálogo)", MessageType.Warning, 3f);
            
            // Por ahora ejecuta directamente
            onConfirm?.Invoke();
        }

        #endregion

        #region Utilities

        private void UpdateButtonState(Button button, bool isActive)
        {
            if (button == null)
                return;

            ColorBlock colors = button.colors;
            colors.normalColor = isActive ? Color.green : Color.white;
            button.colors = colors;

            // Actualizar texto del botón si tiene
            TextMeshProUGUI buttonText = button.GetComponentInChildren<TextMeshProUGUI>();
            if (buttonText != null)
            {
                buttonText.text = isActive ? "Desactivar Colocación" : "Colocar Waypoint";
            }
        }

        #endregion

        #region Public API

        public void ShowWaypointConfigPanel(Core.Data.WaypointData waypoint)
        {
            if (_waypointConfigPanel != null)
            {
                _waypointConfigPanel.ShowForWaypoint(waypoint);
                SetAppMode(AppMode.WaypointConfiguration);
            }
        }

        public void ShowNavigationPanel()
        {
            if (_navigationPanel != null)
            {
                _navigationPanel.RefreshWaypointList();
                ShowPanel(_navigationPanel.gameObject);
                SetAppMode(AppMode.Navigation);
            }
        }

        #endregion
    }
}