using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Navigation;

namespace IndoorNavAR.UI.Panels
{
    /// <summary>
    /// Panel para seleccionar destino y controlar la navegación.
    /// Muestra lista de waypoints disponibles y progreso de navegación.
    /// </summary>
    public class NavigationPanel : MonoBehaviour
    {
        [Header("Lista de Waypoints")]
        [SerializeField] private Transform _waypointListContent;
        [SerializeField] private GameObject _waypointItemPrefab;
        [SerializeField] private ScrollRect _scrollRect;

        [Header("Búsqueda y Filtros")]
        [SerializeField] private TMP_InputField _searchInputField;
        [SerializeField] private TMP_Dropdown _filterTypeDropdown;
        [SerializeField] private Toggle _showOnlyNavigableToggle;

        [Header("Progreso de Navegación")]
        [SerializeField] private GameObject _navigationProgressPanel;
        [SerializeField] private TextMeshProUGUI _destinationNameText;
        [SerializeField] private TextMeshProUGUI _distanceRemainingText;
        [SerializeField] private Slider _progressBar;
        [SerializeField] private Button _cancelNavigationButton;

        [Header("Botones")]
        [SerializeField] private Button _closeButton;
        [SerializeField] private Button _refreshButton;

        [Header("Mensajes")]
        [SerializeField] private GameObject _emptyStatePanel;
        [SerializeField] private TextMeshProUGUI _emptyStateText;

        private WaypointManager _waypointManager;
        private NavigationAgent _navigationAgent;
        private List<WaypointItemUI> _waypointItems = new List<WaypointItemUI>();
        private bool _isNavigating;

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateDependencies();
            SetupListeners();
        }

        private void OnEnable()
        {
            SubscribeToEvents();
            RefreshWaypointList();
        }

        private void OnDisable()
        {
            UnsubscribeFromEvents();
        }

        private void Update()
        {
            if (_isNavigating)
            {
                UpdateNavigationProgress();
            }
        }

        #endregion

        #region Initialization

        private void ValidateDependencies()
        {
            _waypointManager = FindFirstObjectByType<WaypointManager>();
            _navigationAgent = FindFirstObjectByType<NavigationAgent>();

            if (_waypointListContent == null)
                Debug.LogError("[NavigationPanel] Waypoint List Content no asignado.");

            if (_waypointItemPrefab == null)
                Debug.LogError("[NavigationPanel] Waypoint Item Prefab no asignado.");

            if (_navigationProgressPanel != null)
                _navigationProgressPanel.SetActive(false);
        }

        private void SetupListeners()
        {
            if (_closeButton != null)
                _closeButton.onClick.AddListener(OnCloseClicked);

            if (_refreshButton != null)
                _refreshButton.onClick.AddListener(OnRefreshClicked);

            if (_cancelNavigationButton != null)
                _cancelNavigationButton.onClick.AddListener(OnCancelNavigationClicked);

            if (_searchInputField != null)
                _searchInputField.onValueChanged.AddListener(OnSearchChanged);

            if (_filterTypeDropdown != null)
                _filterTypeDropdown.onValueChanged.AddListener(OnFilterChanged);

            if (_showOnlyNavigableToggle != null)
                _showOnlyNavigableToggle.onValueChanged.AddListener(OnNavigableFilterChanged);

            InitializeFilterDropdown();
        }

        private void InitializeFilterDropdown()
        {
            if (_filterTypeDropdown == null)
                return;

            _filterTypeDropdown.ClearOptions();

            var options = new List<string> { "Todos" };

            foreach (WaypointType type in System.Enum.GetValues(typeof(WaypointType)))
            {
                if (type != WaypointType.Generic)
                {
                    options.Add(GetTypeDisplayName(type));
                }
            }

            _filterTypeDropdown.AddOptions(options);
        }

        #endregion

        #region Event Subscriptions

        private void SubscribeToEvents()
        {
            EventBus.Instance.Subscribe<NavigationStartedEvent>(OnNavigationStarted);
            EventBus.Instance.Subscribe<NavigationCompletedEvent>(OnNavigationCompleted);
            EventBus.Instance.Subscribe<NavigationCancelledEvent>(OnNavigationCancelled);
            EventBus.Instance.Subscribe<WaypointPlacedEvent>(OnWaypointPlaced);
            EventBus.Instance.Subscribe<WaypointRemovedEvent>(OnWaypointRemoved);
        }

        private void UnsubscribeFromEvents()
        {
            EventBus.Instance.Unsubscribe<NavigationStartedEvent>(OnNavigationStarted);
            EventBus.Instance.Unsubscribe<NavigationCompletedEvent>(OnNavigationCompleted);
            EventBus.Instance.Unsubscribe<NavigationCancelledEvent>(OnNavigationCancelled);
            EventBus.Instance.Unsubscribe<WaypointPlacedEvent>(OnWaypointPlaced);
            EventBus.Instance.Unsubscribe<WaypointRemovedEvent>(OnWaypointRemoved);
        }

        private void OnNavigationStarted(NavigationStartedEvent evt)
        {
            _isNavigating = true;
            
            if (_navigationProgressPanel != null)
            {
                _navigationProgressPanel.SetActive(true);
            }

            // Encontrar waypoint destino
            WaypointData destination = _waypointManager?.GetWaypoint(evt.DestinationWaypointId);
            
            if (_destinationNameText != null && destination != null)
            {
                _destinationNameText.text = $"Destino: {destination.WaypointName}";
            }
        }

        private void OnNavigationCompleted(NavigationCompletedEvent evt)
        {
            _isNavigating = false;
            
            if (_navigationProgressPanel != null)
            {
                _navigationProgressPanel.SetActive(false);
            }
        }

        private void OnNavigationCancelled(NavigationCancelledEvent evt)
        {
            _isNavigating = false;
            
            if (_navigationProgressPanel != null)
            {
                _navigationProgressPanel.SetActive(false);
            }
        }

        private void OnWaypointPlaced(WaypointPlacedEvent evt)
        {
            RefreshWaypointList();
        }

        private void OnWaypointRemoved(WaypointRemovedEvent evt)
        {
            RefreshWaypointList();
        }

        #endregion

        #region Waypoint List Management

        /// <summary>
        /// Refresca la lista completa de waypoints.
        /// </summary>
        public void RefreshWaypointList()
        {
            if (_waypointManager == null)
            {
                ShowEmptyState("WaypointManager no encontrado.");
                return;
            }

            // Limpiar items existentes
            ClearWaypointList();

            // Obtener waypoints
            var waypoints = GetFilteredWaypoints();

            if (waypoints.Count == 0)
            {
                ShowEmptyState("No hay waypoints disponibles.\nColoca waypoints primero.");
                return;
            }

            HideEmptyState();

            // Crear items UI
            foreach (var waypoint in waypoints)
            {
                CreateWaypointItem(waypoint);
            }

            Debug.Log($"[NavigationPanel] Lista actualizada: {waypoints.Count} waypoints.");
        }

        private void ClearWaypointList()
        {
            foreach (var item in _waypointItems)
            {
                if (item != null && item.gameObject != null)
                {
                    Destroy(item.gameObject);
                }
            }

            _waypointItems.Clear();
        }

        private void CreateWaypointItem(WaypointData waypoint)
        {
            if (_waypointItemPrefab == null || _waypointListContent == null)
                return;

            GameObject itemObj = Instantiate(_waypointItemPrefab, _waypointListContent);
            WaypointItemUI itemUI = itemObj.GetComponent<WaypointItemUI>();

            if (itemUI == null)
            {
                itemUI = itemObj.AddComponent<WaypointItemUI>();
            }

            itemUI.Setup(waypoint, this);
            _waypointItems.Add(itemUI);
        }

        private List<WaypointData> GetFilteredWaypoints()
        {
            if (_waypointManager == null)
                return new List<WaypointData>();

            var waypoints = _waypointManager.Waypoints.ToList();

            // Filtro de búsqueda
            string searchTerm = _searchInputField != null ? _searchInputField.text.ToLower() : "";
            if (!string.IsNullOrEmpty(searchTerm))
            {
                waypoints = waypoints.Where(w => 
                    w.WaypointName.ToLower().Contains(searchTerm) ||
                    w.Description.ToLower().Contains(searchTerm)
                ).ToList();
            }

            // Filtro de tipo
            if (_filterTypeDropdown != null && _filterTypeDropdown.value > 0)
            {
                WaypointType filterType = (WaypointType)(_filterTypeDropdown.value - 1);
                waypoints = waypoints.Where(w => w.Type == filterType).ToList();
            }

            // Filtro de navegable
            if (_showOnlyNavigableToggle != null && _showOnlyNavigableToggle.isOn)
            {
                waypoints = waypoints.Where(w => w.IsNavigable).ToList();
            }

            return waypoints;
        }

        #endregion

        #region Navigation Control

        /// <summary>
        /// Inicia navegación hacia un waypoint específico.
        /// </summary>
        public void NavigateToWaypoint(WaypointData waypoint)
        {
            if (_navigationAgent == null)
            {
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Agente de navegación no encontrado.",
                    Type = MessageType.Error,
                    Duration = 3f
                });
                return;
            }

            bool success = _navigationAgent.NavigateToWaypoint(waypoint);

            if (!success)
            {
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "No se pudo iniciar navegación. Verifica el NavMesh.",
                    Type = MessageType.Warning,
                    Duration = 3f
                });
            }
        }

        private void OnCancelNavigationClicked()
        {
            if (_navigationAgent != null)
            {
                _navigationAgent.StopNavigation("Usuario canceló navegación");
            }

            _isNavigating = false;

            if (_navigationProgressPanel != null)
            {
                _navigationProgressPanel.SetActive(false);
            }
        }

        #endregion

        #region Navigation Progress

        private void UpdateNavigationProgress()
        {
            if (_navigationAgent == null)
                return;

            // Actualizar distancia restante
            if (_distanceRemainingText != null)
            {
                float distance = _navigationAgent.DistanceToDestination;
                _distanceRemainingText.text = $"{distance:F1}m restantes";
            }

            // Actualizar barra de progreso
            if (_progressBar != null)
            {
                _progressBar.value = _navigationAgent.ProgressPercent;
            }
        }

        #endregion

        #region Event Handlers

        private void OnCloseClicked()
        {
            gameObject.SetActive(false);
        }

        private void OnRefreshClicked()
        {
            RefreshWaypointList();
        }

        private void OnSearchChanged(string searchTerm)
        {
            RefreshWaypointList();
        }

        private void OnFilterChanged(int filterIndex)
        {
            RefreshWaypointList();
        }

        private void OnNavigableFilterChanged(bool showOnlyNavigable)
        {
            RefreshWaypointList();
        }

        #endregion

        #region Empty State

        private void ShowEmptyState(string message)
        {
            if (_emptyStatePanel != null)
            {
                _emptyStatePanel.SetActive(true);
            }

            if (_emptyStateText != null)
            {
                _emptyStateText.text = message;
            }

            if (_scrollRect != null)
            {
                _scrollRect.gameObject.SetActive(false);
            }
        }

        private void HideEmptyState()
        {
            if (_emptyStatePanel != null)
            {
                _emptyStatePanel.SetActive(false);
            }

            if (_scrollRect != null)
            {
                _scrollRect.gameObject.SetActive(true);
            }
        }

        #endregion

        #region Utilities

        private string GetTypeDisplayName(WaypointType type)
        {
            return type switch
            {
                WaypointType.Generic => "Genérico",
                WaypointType.Entrance => "Entrada",
                WaypointType.Exit => "Salida",
                WaypointType.Kitchen => "Cocina",
                WaypointType.Bathroom => "Baño",
                WaypointType.Bedroom => "Habitación",
                WaypointType.LivingRoom => "Sala",
                WaypointType.DiningRoom => "Comedor",
                WaypointType.Office => "Oficina",
                WaypointType.Hallway => "Pasillo",
                WaypointType.Stairs => "Escaleras",
                WaypointType.Elevator => "Ascensor",
                WaypointType.Custom => "Personalizado",
                _ => type.ToString()
            };
        }

        #endregion
    }

    /// <summary>
    /// Componente UI para un item individual de waypoint en la lista.
    /// </summary>
    public class WaypointItemUI : MonoBehaviour
    {
        [Header("UI Elements")]
        [SerializeField] private TextMeshProUGUI _nameText;
        [SerializeField] private TextMeshProUGUI _typeText;
        [SerializeField] private TextMeshProUGUI _distanceText;
        [SerializeField] private Image _colorIndicator;
        [SerializeField] private Button _navigateButton;
        [SerializeField] private Button _configButton;

        private WaypointData _waypoint;
        private NavigationPanel _parentPanel;

        public void Setup(WaypointData waypoint, NavigationPanel parentPanel)
        {
            _waypoint = waypoint;
            _parentPanel = parentPanel;

            UpdateUI();
            SetupButtons();
        }

        private void UpdateUI()
        {
            if (_waypoint == null)
                return;

            // Nombre
            if (_nameText != null)
                _nameText.text = _waypoint.WaypointName;

            // Tipo
            if (_typeText != null)
                _typeText.text = GetTypeDisplayName(_waypoint.Type);

            // Color
            if (_colorIndicator != null)
                _colorIndicator.color = _waypoint.Color;

            // Distancia (opcional - calcular desde cámara)
            if (_distanceText != null)
            {
                Camera mainCamera = Camera.main;
                if (mainCamera != null)
                {
                    float distance = Vector3.Distance(mainCamera.transform.position, _waypoint.Position);
                    _distanceText.text = $"{distance:F1}m";
                }
            }

            // Deshabilitar botón si no es navegable
            if (_navigateButton != null)
                _navigateButton.interactable = _waypoint.IsNavigable;
        }

        private void SetupButtons()
        {
            if (_navigateButton != null)
            {
                _navigateButton.onClick.RemoveAllListeners();
                _navigateButton.onClick.AddListener(OnNavigateClicked);
            }

            if (_configButton != null)
            {
                _configButton.onClick.RemoveAllListeners();
                _configButton.onClick.AddListener(OnConfigClicked);
            }
        }

        private void OnNavigateClicked()
        {
            if (_parentPanel != null && _waypoint != null)
            {
                _parentPanel.NavigateToWaypoint(_waypoint);
            }
        }

        private void OnConfigClicked()
        {
            var uiManager = FindFirstObjectByType<UIManager>();
            
            if (uiManager != null && _waypoint != null)
            {
                uiManager.ShowWaypointConfigPanel(_waypoint);
            }
        }

        private string GetTypeDisplayName(WaypointType type)
        {
            return type switch
            {
                WaypointType.Generic => "Genérico",
                WaypointType.Entrance => "Entrada",
                WaypointType.Exit => "Salida",
                WaypointType.Kitchen => "Cocina",
                WaypointType.Bathroom => "Baño",
                WaypointType.Bedroom => "Habitación",
                WaypointType.LivingRoom => "Sala",
                WaypointType.DiningRoom => "Comedor",
                WaypointType.Office => "Oficina",
                WaypointType.Hallway => "Pasillo",
                WaypointType.Stairs => "Escaleras",
                WaypointType.Elevator => "Ascensor",
                WaypointType.Custom => "Personalizado",
                _ => type.ToString()
            };
        }
    }
}