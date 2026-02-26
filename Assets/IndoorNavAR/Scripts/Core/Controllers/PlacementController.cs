using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace IndoorNavAR.Core.Controllers
{
    /// <summary>
    /// Controlador para colocación de waypoints mediante toques en pantalla.
    /// Usa Enhanced Touch para input moderno y AR Foundation para raycasting.
    /// </summary>
    public class PlacementController : MonoBehaviour
    {
        [Header("Referencias")]
        [SerializeField] private AR.ARSessionManager _arSessionManager;
        [SerializeField] private WaypointManager _waypointManager;

        [Header("Feedback Visual")]
        [SerializeField] private GameObject _placementIndicatorPrefab;
        [SerializeField] private float _indicatorHeight = 0.01f;
        
        [Header("Configuración")]
        [SerializeField] private float _maxRaycastDistance = 10f;

        private GameObject _placementIndicator;
        private bool _isPlacementActive;
        private Vector3 _lastValidPosition;
        private Quaternion _lastValidRotation;
        private bool _hasValidPlacement;

        #region Properties

        public bool IsPlacementActive
        {
            get => _isPlacementActive;
            set
            {
                _isPlacementActive = value;
                
                if (_placementIndicator != null)
                {
                    _placementIndicator.SetActive(value);
                }

                Debug.Log($"[PlacementController] Modo colocación: {value}");
            }
        }

        public bool HasValidPlacement => _hasValidPlacement;
        public Vector3 LastValidPosition => _lastValidPosition;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateDependencies();
            CreatePlacementIndicator();
        }

        private void OnEnable()
        {
            EnhancedTouchSupport.Enable();
            SubscribeToEvents();
        }

        private void OnDisable()
        {
            UnsubscribeFromEvents();  // ✅ PRIMERO desuscribir (mientras está habilitado)
            EnhancedTouchSupport.Disable();  // ✅ DESPUÉS deshabilitar
        }

        private void Update()
        {
            if (_isPlacementActive && _arSessionManager != null && _arSessionManager.IsSessionReady)
            {
                UpdatePlacementIndicator();
            }
        }

        #endregion

        #region Initialization

        private void ValidateDependencies()
        {
            // Auto-buscar componentes si no están asignados
            if (_arSessionManager == null)
            {
                _arSessionManager = FindFirstObjectByType<AR.ARSessionManager>();
            }

            if (_waypointManager == null)
            {
                _waypointManager = FindFirstObjectByType<WaypointManager>();
            }

            // Validar componentes críticos
            if (_arSessionManager == null)
            {
                Debug.LogError("[PlacementController] ARSessionManager no encontrado. Deshabilitando.");
                enabled = false;
                return;
            }

            if (_waypointManager == null)
            {
                Debug.LogWarning("[PlacementController] WaypointManager no encontrado. Funcionalidad limitada.");
            }

            Debug.Log("[PlacementController] Dependencias validadas.");
        }

        private void CreatePlacementIndicator()
        {
            if (_placementIndicatorPrefab != null)
            {
                _placementIndicator = Instantiate(_placementIndicatorPrefab);
                _placementIndicator.name = "[PlacementIndicator]";
                _placementIndicator.SetActive(false);
            }
            else
            {
                // Crear indicador básico
                _placementIndicator = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                _placementIndicator.name = "[PlacementIndicator]";
                _placementIndicator.transform.localScale = new Vector3(0.3f, 0.01f, 0.3f);

                // Material semi-transparente
                Renderer renderer = _placementIndicator.GetComponent<Renderer>();
                Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.color = new Color(0f, 1f, 0f, 0.5f);
                renderer.material = mat;

                // Remover collider
                Destroy(_placementIndicator.GetComponent<Collider>());

                _placementIndicator.SetActive(false);
            }

            Debug.Log("[PlacementController] Indicador de colocación creado.");
        }

        #endregion

        #region Event Subscriptions

        private void SubscribeToEvents()
        {
            Touch.onFingerDown += OnFingerDown;
        }

        private void UnsubscribeFromEvents()
        {
            Touch.onFingerDown -= OnFingerDown;
        }

        #endregion

        #region Touch Input

        /// <summary>
        /// Callback cuando se detecta un toque en pantalla.
        /// </summary>
        private void OnFingerDown(Finger finger)
        {
            if (!_isPlacementActive)
                return;

            // Ignorar toques sobre UI
            if (UnityEngine.EventSystems.EventSystem.current != null &&
                UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject(finger.index))
            {
                return;
            }

            Vector2 touchPosition = finger.screenPosition;
            TryPlaceWaypoint(touchPosition);
        }

        #endregion

        #region Placement Logic

        /// <summary>
        /// Actualiza la posición del indicador de colocación cada frame.
        /// </summary>
        private void UpdatePlacementIndicator()
        {
            if (_placementIndicator == null || !_placementIndicator.activeSelf)
                return;

            // Usar el centro de la pantalla para el indicador
            Vector2 screenCenter = new Vector2(Screen.width / 2f, Screen.height / 2f);

            // Realizar raycast
            if (_arSessionManager.Raycast(screenCenter, out ARRaycastHit hit))
            {
                // Posición válida encontrada
                _lastValidPosition = hit.pose.position;
                _lastValidRotation = hit.pose.rotation;
                _hasValidPlacement = true;

                // Actualizar indicador
                _placementIndicator.transform.position = _lastValidPosition + Vector3.up * _indicatorHeight;
                _placementIndicator.transform.rotation = _lastValidRotation;

                // Hacer el indicador verde (válido)
                UpdateIndicatorColor(Color.green);
            }
            else
            {
                _hasValidPlacement = false;
                
                // Hacer el indicador rojo (inválido)
                UpdateIndicatorColor(Color.red);
            }
        }

        /// <summary>
        /// Intenta colocar un waypoint en la posición del toque.
        /// </summary>
        private void TryPlaceWaypoint(Vector2 screenPosition)
        {
            // Realizar raycast
            if (!_arSessionManager.Raycast(screenPosition, out ARRaycastHit hit))
            {
                Debug.LogWarning("[PlacementController] No se detectó superficie válida.");
                
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Toca una superficie detectada para colocar un waypoint.",
                    Type = MessageType.Warning,
                    Duration = 2f
                });

                return;
            }

            // Validar distancia máxima
            Camera mainCamera = Camera.main;
            if (mainCamera != null)
            {
                float distance = Vector3.Distance(mainCamera.transform.position, hit.pose.position);
                
                if (distance > _maxRaycastDistance)
                {
                    Debug.LogWarning($"[PlacementController] Posición muy lejana: {distance:F2}m");
                    return;
                }
            }

            // Colocar waypoint
            PlaceWaypoint(hit.pose.position, hit.pose.rotation);
        }

        /// <summary>
        /// Coloca un waypoint en la posición especificada.
        /// </summary>
        private void PlaceWaypoint(Vector3 position, Quaternion rotation)
        {
            if (_waypointManager == null)
            {
                Debug.LogError("[PlacementController] WaypointManager no disponible.");
                return;
            }

            // Ajustar altura del waypoint (elevarlo ligeramente sobre el plano)
            Vector3 adjustedPosition = position + Vector3.up * 0.25f;

            // Crear waypoint a través del manager
            var waypoint = _waypointManager.CreateWaypoint(adjustedPosition, rotation);

            if (waypoint != null)
            {
                Debug.Log($"[PlacementController] Waypoint colocado en: {adjustedPosition}");

                // Feedback visual (pulso en el indicador)
                StartCoroutine(PlayPlacementFeedback());

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Waypoint colocado. Configúralo en el panel.",
                    Type = MessageType.Success,
                    Duration = 2f
                });
            }
            else
            {
                Debug.LogError("[PlacementController] Error al crear waypoint.");
            }
        }

        #endregion

        #region Visual Feedback

        /// <summary>
        /// Actualiza el color del indicador de colocación.
        /// </summary>
        private void UpdateIndicatorColor(Color color)
        {
            if (_placementIndicator == null)
                return;

            Renderer renderer = _placementIndicator.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
            {
                Color transparentColor = color;
                transparentColor.a = 0.5f;
                renderer.material.color = transparentColor;
            }
        }

        /// <summary>
        /// Feedback visual cuando se coloca un waypoint.
        /// </summary>
        private System.Collections.IEnumerator PlayPlacementFeedback()
        {
            if (_placementIndicator == null)
                yield break;

            Vector3 originalScale = _placementIndicator.transform.localScale;
            
            // Pulso: crecer y volver
            float duration = 0.3f;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;
                float scale = Mathf.Lerp(1f, 1.5f, Mathf.Sin(t * Mathf.PI));
                
                _placementIndicator.transform.localScale = originalScale * scale;
                
                yield return null;
            }

            _placementIndicator.transform.localScale = originalScale;
        }

        #endregion

        #region Public Controls

        /// <summary>
        /// Activa/desactiva el modo de colocación.
        /// </summary>
        public void TogglePlacementMode(bool active)
        {
            IsPlacementActive = active;

            if (active)
            {
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Modo colocación activado. Toca una superficie para colocar waypoint.",
                    Type = MessageType.Info,
                    Duration = 3f
                });
            }
        }

        /// <summary>
        /// Coloca un waypoint en la última posición válida.
        /// </summary>
        public void PlaceWaypointAtIndicator()
        {
            if (!_hasValidPlacement)
            {
                Debug.LogWarning("[PlacementController] No hay posición válida.");
                return;
            }

            PlaceWaypoint(_lastValidPosition, _lastValidRotation);
        }

        #endregion

        #region Debug

        private void OnDrawGizmos()
        {
            if (_hasValidPlacement)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(_lastValidPosition, 0.1f);
            }
        }

        #endregion
    }
}