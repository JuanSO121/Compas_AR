using System;
using UnityEngine;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Core.Data
{
    /// <summary>
    /// Componente que almacena y gestiona los datos de un waypoint individual.
    /// Se adjunta al GameObject del waypoint para mantener su configuración.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer))]
    public class WaypointData : MonoBehaviour
    {
        [Header("Identificación")]
        [SerializeField] private string _waypointId;
        [SerializeField] private string _waypointName = "Waypoint";
        [SerializeField] private WaypointType _type = WaypointType.Generic;

        [Header("Visualización")]
        [SerializeField] private Color _color = Color.cyan;
        [SerializeField] private float _height = 0.5f;
        [SerializeField] private float _radius = 0.25f;

        [Header("Configuración")]
        [SerializeField] private string _description;
        [SerializeField] private bool _isNavigable = true;
        
        private MeshRenderer _meshRenderer;
        private MaterialPropertyBlock _propertyBlock;

        #region Properties

        public string WaypointId
        {
            get => _waypointId;
            set => _waypointId = value;
        }

        public string WaypointName
        {
            get => _waypointName;
            set
            {
                _waypointName = value;
                gameObject.name = $"Waypoint_{_waypointName}";
            }
        }

        public WaypointType Type
        {
            get => _type;
            set => _type = value;
        }

        public Color Color
        {
            get => _color;
            set
            {
                _color = value;
                UpdateVisuals();
            }
        }

        public float Height
        {
            get => _height;
            set
            {
                _height = Mathf.Max(0.1f, value);
                UpdateScale();
            }
        }

        public float Radius
        {
            get => _radius;
            set
            {
                _radius = Mathf.Max(0.1f, value);
                UpdateScale();
            }
        }

        public string Description
        {
            get => _description;
            set => _description = value;
        }

        public bool IsNavigable
        {
            get => _isNavigable;
            set => _isNavigable = value;
        }

        public Vector3 Position => transform.position;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            // Generar ID único si no existe
            if (string.IsNullOrEmpty(_waypointId))
            {
                _waypointId = Guid.NewGuid().ToString();
            }

            // Inicializar componentes
            _meshRenderer = GetComponent<MeshRenderer>();
            _propertyBlock = new MaterialPropertyBlock();

            // Aplicar configuración inicial
            UpdateVisuals();
            UpdateScale();
        }

        #endregion

        #region Configuration Methods

        /// <summary>
        /// Configura el waypoint con todos los parámetros de una vez.
        /// </summary>
        public void Configure(string name, WaypointType type, Color color, string description = "")
        {
            WaypointName = name;
            Type = type;
            Description = description;
            Color = color;

            // Publicar evento de configuración
            EventBus.Instance.Publish(new WaypointConfiguredEvent
            {
                WaypointId = _waypointId,
                WaypointName = name,
                Type = type,
                Color = color
            });
        }

        /// <summary>
        /// Actualiza las propiedades visuales del waypoint.
        /// </summary>
        public void UpdateVisuals()
        {
            if (_meshRenderer == null || _propertyBlock == null)
                return;

            // Usar MaterialPropertyBlock para eficiencia (no crea instancias de material)
            _meshRenderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetColor("_BaseColor", _color);
            _propertyBlock.SetColor("_Color", _color); // Fallback para shaders legacy
            _meshRenderer.SetPropertyBlock(_propertyBlock);
        }

        /// <summary>
        /// Actualiza la escala basada en height y radius.
        /// </summary>
        private void UpdateScale()
        {
            transform.localScale = new Vector3(_radius * 2f, _height, _radius * 2f);
        }

        /// <summary>
        /// Obtiene el color predeterminado según el tipo de waypoint.
        /// </summary>
        public static Color GetDefaultColorForType(WaypointType type)
        {
            return type switch
            {
                WaypointType.Entrance => new Color(0f, 1f, 0f, 0.8f),      // Verde
                WaypointType.Exit => new Color(1f, 0f, 0f, 0.8f),          // Rojo
                WaypointType.Kitchen => new Color(1f, 0.65f, 0f, 0.8f),    // Naranja
                WaypointType.Bathroom => new Color(0f, 0.75f, 1f, 0.8f),   // Azul claro
                WaypointType.Bedroom => new Color(0.8f, 0.4f, 0.8f, 0.8f), // Púrpura
                WaypointType.LivingRoom => new Color(1f, 1f, 0f, 0.8f),    // Amarillo
                WaypointType.DiningRoom => new Color(1f, 0.5f, 0f, 0.8f),  // Naranja oscuro
                WaypointType.Office => new Color(0f, 0f, 1f, 0.8f),        // Azul
                WaypointType.Hallway => new Color(0.7f, 0.7f, 0.7f, 0.8f), // Gris
                WaypointType.Stairs => new Color(0.6f, 0.3f, 0f, 0.8f),    // Marrón
                WaypointType.Elevator => new Color(0.5f, 0.5f, 0.5f, 0.8f),// Gris oscuro
                _ => new Color(0f, 1f, 1f, 0.8f)                           // Cyan (Generic/Custom)
            };
        }

        #endregion

        #region Serialization Support

        /// <summary>
        /// Serializa el waypoint a formato guardable.
        /// </summary>
        public WaypointSaveData ToSaveData()
        {
            return new WaypointSaveData
            {
                id = _waypointId,
                name = _waypointName,
                type = _type,
                position = transform.position,
                rotation = transform.rotation,
                color = _color,
                height = _height,
                radius = _radius,
                description = _description,
                isNavigable = _isNavigable
            };
        }

        /// <summary>
        /// Carga configuración desde datos serializados.
        /// </summary>
        public void LoadFromSaveData(WaypointSaveData data)
        {
            _waypointId = data.id;
            _waypointName = data.name;
            _type = data.type;
            transform.position = data.position;
            transform.rotation = data.rotation;
            _color = data.color;
            _height = data.height;
            _radius = data.radius;
            _description = data.description;
            _isNavigable = data.isNavigable;

            gameObject.name = $"Waypoint_{_waypointName}";
            UpdateVisuals();
            UpdateScale();
        }

        #endregion

        #region Debug

        private void OnDrawGizmos()
        {
            Gizmos.color = _color;
            Gizmos.DrawWireSphere(transform.position, _radius);
            
            // Dibuja línea vertical para indicar altura
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * _height);
        }

        #endregion
    }

    /// <summary>
    /// Estructura serializable para guardar/cargar waypoints.
    /// </summary>
    [Serializable]
    public struct WaypointSaveData
    {
        public string id;
        public string name;
        public WaypointType type;
        public Vector3 position;
        public Quaternion rotation;
        public Color color;
        public float height;
        public float radius;
        public string description;
        public bool isNavigable;
    }
}