using UnityEngine;
using UnityEngine.UI;
using TMPro;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.UI.Panels
{
    /// <summary>
    /// Panel para configurar propiedades de waypoints individuales.
    /// Permite editar nombre, tipo, color y descripción.
    /// </summary>
    public class WaypointConfigPanel : MonoBehaviour
    {
        [Header("Input Fields")]
        [SerializeField] private TMP_InputField _nameInputField;
        [SerializeField] private TMP_Dropdown _typeDropdown;
        [SerializeField] private TMP_InputField _descriptionInputField;

        [Header("Color Picker")]
        [SerializeField] private FlexibleColorPicker _colorPicker;
        [SerializeField] private Image _colorPreview;
        [SerializeField] private Button _useDefaultColorButton;

        [Header("Preview")]
        [SerializeField] private TextMeshProUGUI _previewText;

        [Header("Buttons")]
        [SerializeField] private Button _saveButton;
        [SerializeField] private Button _cancelButton;
        [SerializeField] private Button _deleteButton;

        [Header("Toggle")]
        [SerializeField] private Toggle _navigableToggle;

        private WaypointData _currentWaypoint;
        private Color _selectedColor;

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateDependencies();
            SetupListeners();
            InitializeTypeDropdown();
        }

        private void OnEnable()
        {
            if (_currentWaypoint != null)
            {
                LoadWaypointData();
            }
        }

        #endregion

        #region Initialization

        private void ValidateDependencies()
        {
            if (_nameInputField == null)
                Debug.LogError("[WaypointConfigPanel] Name Input Field no asignado.");

            if (_typeDropdown == null)
                Debug.LogError("[WaypointConfigPanel] Type Dropdown no asignado.");

            if (_saveButton == null)
                Debug.LogError("[WaypointConfigPanel] Save Button no asignado.");
        }

        private void SetupListeners()
        {
            if (_saveButton != null)
                _saveButton.onClick.AddListener(OnSaveClicked);

            if (_cancelButton != null)
                _cancelButton.onClick.AddListener(OnCancelClicked);

            if (_deleteButton != null)
                _deleteButton.onClick.AddListener(OnDeleteClicked);

            if (_useDefaultColorButton != null)
                _useDefaultColorButton.onClick.AddListener(OnUseDefaultColorClicked);

            if (_typeDropdown != null)
                _typeDropdown.onValueChanged.AddListener(OnTypeChanged);

            if (_colorPicker != null)
                _colorPicker.onColorChanged += OnColorChanged;

            if (_nameInputField != null)
                _nameInputField.onValueChanged.AddListener(OnNameChanged);
        }

        private void InitializeTypeDropdown()
        {
            if (_typeDropdown == null)
                return;

            _typeDropdown.ClearOptions();

            var options = new System.Collections.Generic.List<string>();
            
            foreach (WaypointType type in System.Enum.GetValues(typeof(WaypointType)))
            {
                options.Add(GetTypeDisplayName(type));
            }

            _typeDropdown.AddOptions(options);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Muestra el panel para configurar un waypoint específico.
        /// </summary>
        public void ShowForWaypoint(WaypointData waypoint)
        {
            if (waypoint == null)
            {
                Debug.LogError("[WaypointConfigPanel] Waypoint es null.");
                return;
            }

            _currentWaypoint = waypoint;
            gameObject.SetActive(true);
            LoadWaypointData();

            Debug.Log($"[WaypointConfigPanel] Configurando waypoint: {waypoint.WaypointName}");
        }

        /// <summary>
        /// Cierra el panel.
        /// </summary>
        public void Close()
        {
            gameObject.SetActive(false);
            _currentWaypoint = null;
        }

        #endregion

        #region Data Loading

        private void LoadWaypointData()
        {
            if (_currentWaypoint == null)
                return;

            // Cargar nombre
            if (_nameInputField != null)
                _nameInputField.text = _currentWaypoint.WaypointName;

            // Cargar tipo
            if (_typeDropdown != null)
                _typeDropdown.value = (int)_currentWaypoint.Type;

            // Cargar descripción
            if (_descriptionInputField != null)
                _descriptionInputField.text = _currentWaypoint.Description;

            // Cargar color
            _selectedColor = _currentWaypoint.Color;
            UpdateColorPreview();

            if (_colorPicker != null)
                _colorPicker.color = _selectedColor;

            // Cargar navegable
            if (_navigableToggle != null)
                _navigableToggle.isOn = _currentWaypoint.IsNavigable;

            UpdatePreview();
        }

        #endregion

        #region Event Handlers

        private void OnSaveClicked()
        {
            if (_currentWaypoint == null)
                return;

            // Validar nombre
            string waypointName = _nameInputField != null ? _nameInputField.text.Trim() : "Waypoint";
            
            if (string.IsNullOrEmpty(waypointName))
            {
                ShowError("El nombre no puede estar vacío.");
                return;
            }

            // Obtener tipo
            WaypointType type = _typeDropdown != null 
                ? (WaypointType)_typeDropdown.value 
                : WaypointType.Generic;

            // Obtener descripción
            string description = _descriptionInputField != null 
                ? _descriptionInputField.text.Trim() 
                : "";

            // Obtener navegable
            bool isNavigable = _navigableToggle != null 
                ? _navigableToggle.isOn 
                : true;

            // Configurar waypoint
            _currentWaypoint.Configure(waypointName, type, _selectedColor, description);
            _currentWaypoint.IsNavigable = isNavigable;

            // Notificar guardado exitoso
            EventBus.Instance.Publish(new ShowMessageEvent
            {
                Message = $"Waypoint '{waypointName}' configurado correctamente.",
                Type = MessageType.Success,
                Duration = 2f
            });

            Debug.Log($"[WaypointConfigPanel] Waypoint guardado: {waypointName}");

            Close();
        }

        private void OnCancelClicked()
        {
            Close();
        }

        private void OnDeleteClicked()
        {
            if (_currentWaypoint == null)
                return;

            string waypointName = _currentWaypoint.WaypointName;
            string waypointId = _currentWaypoint.WaypointId;

            // Publicar evento de eliminación
            EventBus.Instance.Publish(new WaypointRemovedEvent
            {
                WaypointId = waypointId
            });

            // Buscar WaypointManager para eliminar
            var waypointManager = FindFirstObjectByType<Core.Managers.WaypointManager>();
            
            if (waypointManager != null)
            {
                waypointManager.RemoveWaypoint(waypointId);
            }

            EventBus.Instance.Publish(new ShowMessageEvent
            {
                Message = $"Waypoint '{waypointName}' eliminado.",
                Type = MessageType.Info,
                Duration = 2f
            });

            Close();
        }

        private void OnUseDefaultColorClicked()
        {
            if (_typeDropdown == null)
                return;

            WaypointType type = (WaypointType)_typeDropdown.value;
            _selectedColor = WaypointData.GetDefaultColorForType(type);

            UpdateColorPreview();

            if (_colorPicker != null)
                _colorPicker.color = _selectedColor;

            UpdatePreview();
        }

        private void OnTypeChanged(int typeIndex)
        {
            UpdatePreview();
        }

        private void OnColorChanged(Color color)
        {
            _selectedColor = color;
            UpdateColorPreview();
            UpdatePreview();
        }

        private void OnNameChanged(string newName)
        {
            UpdatePreview();
        }

        #endregion

        #region Visual Updates

        private void UpdateColorPreview()
        {
            if (_colorPreview != null)
            {
                _colorPreview.color = _selectedColor;
            }
        }

        private void UpdatePreview()
        {
            if (_previewText == null)
                return;

            string name = _nameInputField != null ? _nameInputField.text : "Waypoint";
            WaypointType type = _typeDropdown != null 
                ? (WaypointType)_typeDropdown.value 
                : WaypointType.Generic;

            _previewText.text = $"<b>{name}</b>\n<size=10>{GetTypeDisplayName(type)}</size>";
        }

        private void ShowError(string message)
        {
            EventBus.Instance.Publish(new ShowMessageEvent
            {
                Message = message,
                Type = MessageType.Error,
                Duration = 3f
            });
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
    /// Color Picker simple usando sliders RGB.
    /// Puedes reemplazar esto con un asset más completo si lo prefieres.
    /// </summary>
    public class FlexibleColorPicker : MonoBehaviour
    {
        [SerializeField] private Slider _redSlider;
        [SerializeField] private Slider _greenSlider;
        [SerializeField] private Slider _blueSlider;
        [SerializeField] private Image _colorPreview;

        public System.Action<Color> onColorChanged;

        private Color _currentColor = Color.white;

        public Color color
        {
            get => _currentColor;
            set
            {
                _currentColor = value;
                UpdateSlidersFromColor();
                UpdatePreview();
            }
        }

        private void Awake()
        {
            if (_redSlider != null)
                _redSlider.onValueChanged.AddListener(_ => OnSliderChanged());

            if (_greenSlider != null)
                _greenSlider.onValueChanged.AddListener(_ => OnSliderChanged());

            if (_blueSlider != null)
                _blueSlider.onValueChanged.AddListener(_ => OnSliderChanged());
        }

        private void OnSliderChanged()
        {
            float r = _redSlider != null ? _redSlider.value : 1f;
            float g = _greenSlider != null ? _greenSlider.value : 1f;
            float b = _blueSlider != null ? _blueSlider.value : 1f;

            _currentColor = new Color(r, g, b, 0.8f);
            UpdatePreview();

            onColorChanged?.Invoke(_currentColor);
        }

        private void UpdateSlidersFromColor()
        {
            if (_redSlider != null)
                _redSlider.value = _currentColor.r;

            if (_greenSlider != null)
                _greenSlider.value = _currentColor.g;

            if (_blueSlider != null)
                _blueSlider.value = _currentColor.b;
        }

        private void UpdatePreview()
        {
            if (_colorPreview != null)
            {
                _colorPreview.color = _currentColor;
            }
        }
    }
}