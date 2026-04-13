// File: WaypointData.cs
// ✅ v7-fix — No sobreescribir transform cuando hasLocalSpace=true en LoadFromSaveData().
//
// CAMBIO RESPECTO A v7:
//   LoadFromSaveData() ya NO asigna transform.position/rotation cuando
//   data.hasLocalSpace=true, porque CreateWaypointLocal() ya los asignó
//   en espacio local antes de llamar a este método.
//   Sobreescribir con data.position (world legacy) destruiría el local space.

using System;
using UnityEngine;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Core.Data
{
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
            if (string.IsNullOrEmpty(_waypointId))
                _waypointId = Guid.NewGuid().ToString();

            _meshRenderer = GetComponent<MeshRenderer>();
            _propertyBlock = new MaterialPropertyBlock();

            UpdateVisuals();
            UpdateScale();
        }

        #endregion

        #region Configuration Methods

        public void Configure(string name, WaypointType type, Color color, string description = "")
        {
            WaypointName = name;
            Type = type;
            Description = description;
            Color = color;

            EventBus.Instance.Publish(new WaypointConfiguredEvent
            {
                WaypointId   = _waypointId,
                WaypointName = name,
                Type         = type,
                Color        = color
            });
        }

        public void UpdateVisuals()
        {
            if (_meshRenderer == null || _propertyBlock == null) return;
            _meshRenderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetColor("_BaseColor", _color);
            _propertyBlock.SetColor("_Color", _color);
            _meshRenderer.SetPropertyBlock(_propertyBlock);
        }

        private void UpdateScale()
        {
            transform.localScale = new Vector3(_radius * 2f, _height, _radius * 2f);
        }

        public static Color GetDefaultColorForType(WaypointType type)
        {
            return type switch
            {
                WaypointType.Entrance   => new Color(0f, 1f, 0f, 0.8f),
                WaypointType.Exit       => new Color(1f, 0f, 0f, 0.8f),
                WaypointType.Kitchen    => new Color(1f, 0.65f, 0f, 0.8f),
                WaypointType.Bathroom   => new Color(0f, 0.75f, 1f, 0.8f),
                WaypointType.Bedroom    => new Color(0.8f, 0.4f, 0.8f, 0.8f),
                WaypointType.LivingRoom => new Color(1f, 1f, 0f, 0.8f),
                WaypointType.DiningRoom => new Color(1f, 0.5f, 0f, 0.8f),
                WaypointType.Office     => new Color(0f, 0f, 1f, 0.8f),
                WaypointType.Hallway    => new Color(0.7f, 0.7f, 0.7f, 0.8f),
                WaypointType.Stairs     => new Color(0.6f, 0.3f, 0f, 0.8f),
                WaypointType.Elevator   => new Color(0.5f, 0.5f, 0.5f, 0.8f),
                _                       => new Color(0f, 1f, 1f, 0.8f)
            };
        }

        #endregion

        #region Serialization Support

        public WaypointSaveData ToSaveData()
        {
            return new WaypointSaveData
            {
                id          = _waypointId,
                name        = _waypointName,
                type        = _type,
                position    = transform.position,
                rotation    = transform.rotation,
                color       = _color,
                height      = _height,
                radius      = _radius,
                description = _description,
                isNavigable = _isNavigable
            };
        }

        /// <summary>
        /// ✅ v7-fix — Carga configuración desde datos serializados.
        ///
        /// IMPORTANTE: Cuando data.hasLocalSpace=true, CreateWaypointLocal() ya asignó
        /// transform.localPosition/localRotation antes de llamar aquí.
        /// NO sobreescribir con data.position/rotation (world space de sesión anterior)
        /// porque destruiría el posicionamiento correcto en local space.
        ///
        /// Cuando data.hasLocalSpace=false (sesiones pre-v7), usar data.position como antes.
        /// </summary>
        public void LoadFromSaveData(WaypointSaveData data)
        {
            _waypointId  = data.id;
            _waypointName = data.name;
            _type        = data.type;
            _color       = data.color;
            _height      = data.height;
            _radius      = data.radius;
            _description = data.description;
            _isNavigable = data.isNavigable;

            // ✅ FIX: Solo asignar posición world si NO viene de local space.
            // Si hasLocalSpace=true, la posición ya fue asignada correctamente
            // por CreateWaypointLocal() — tocar transform aquí sería un bug.
            if (!data.hasLocalSpace)
            {
                transform.position = data.position;
                transform.rotation = data.rotation;
            }

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
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * _height);
        }

        #endregion
    }

    /// <summary>
    /// Estructura serializable para guardar/cargar waypoints.
    /// ✅ v7: Incluye localPosition/localRotation/hasLocalSpace para local space nativo.
    /// </summary>
    [Serializable]
    public class WaypointSaveData
    {
        public string      id;
        public string      name;
        public WaypointType type;
        public Vector3     position;    // world space — legacy, se conserva
        public Quaternion  rotation;    // world space — legacy, se conserva
        public Color       color;
        public float       height;
        public float       radius;
        public string      description;
        public bool        isNavigable;

        // ✅ v7: Espacio local del modelo — elimina drift entre sesiones VIO
        public Vector3    localPosition; // posición relativa al modelo
        public Quaternion localRotation; // rotación relativa al modelo
        public bool       hasLocalSpace; // true = usar local, false = usar world (legacy)
    }
}