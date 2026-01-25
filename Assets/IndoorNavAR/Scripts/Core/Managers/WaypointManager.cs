using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Core.Managers
{
    /// <summary>
    /// Gestor centralizado del ciclo de vida de waypoints.
    /// Maneja creación, configuración, almacenamiento y consultas de waypoints.
    /// </summary>
    public class WaypointManager : MonoBehaviour
    {
        [Header("Prefab")]
        [SerializeField] private GameObject _waypointPrefab;

        [Header("Configuración")]
        [SerializeField] private Transform _waypointsParent;
        [SerializeField] private int _maxWaypoints = 50;

        private readonly Dictionary<string, WaypointData> _waypoints = new Dictionary<string, WaypointData>();
        private readonly List<WaypointData> _waypointsList = new List<WaypointData>();

        #region Properties

        public int WaypointCount => _waypoints.Count;
        public IReadOnlyList<WaypointData> Waypoints => _waypointsList.AsReadOnly();

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateDependencies();
            CreateWaypointsParent();
        }

        private void OnEnable()
        {
            SubscribeToEvents();
        }

        private void OnDisable()
        {
            UnsubscribeFromEvents();
        }

        #endregion

        #region Initialization

        private void ValidateDependencies()
        {
            if (_waypointPrefab == null)
            {
                Debug.LogError("[WaypointManager] Waypoint Prefab no asignado. Creando prefab básico...");
                _waypointPrefab = CreateDefaultWaypointPrefab();
            }

            // Validar que el prefab tenga WaypointData
            if (_waypointPrefab.GetComponent<WaypointData>() == null)
            {
                Debug.LogError("[WaypointManager] El prefab debe tener componente WaypointData.");
                enabled = false;
            }
        }

        private void CreateWaypointsParent()
        {
            if (_waypointsParent == null)
            {
                GameObject parent = new GameObject("[Waypoints]");
                _waypointsParent = parent.transform;
                Debug.Log("[WaypointManager] Contenedor de waypoints creado.");
            }
        }

        private GameObject CreateDefaultWaypointPrefab()
        {
            // Crear prefab básico en runtime si no está asignado
            GameObject prefab = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            prefab.name = "Waypoint";
            
            // Agregar componente WaypointData
            prefab.AddComponent<WaypointData>();
            
            // Configurar material semi-transparente
            Renderer renderer = prefab.GetComponent<Renderer>();
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = new Color(0f, 1f, 1f, 0.7f);
            renderer.material = mat;

            // Remover collider innecesario
            Destroy(prefab.GetComponent<Collider>());

            prefab.SetActive(false);
            return prefab;
        }

        #endregion

        #region Event Subscriptions

        private void SubscribeToEvents()
        {
            EventBus.Instance.Subscribe<WaypointPlacedEvent>(OnWaypointPlaced);
            EventBus.Instance.Subscribe<WaypointRemovedEvent>(OnWaypointRemoved);
        }

        private void UnsubscribeFromEvents()
        {
            EventBus.Instance.Unsubscribe<WaypointPlacedEvent>(OnWaypointPlaced);
            EventBus.Instance.Unsubscribe<WaypointRemovedEvent>(OnWaypointRemoved);
        }

        private void OnWaypointPlaced(WaypointPlacedEvent evt)
        {
            Debug.Log($"[WaypointManager] Waypoint colocado: {evt.WaypointId} en {evt.Position}");
        }

        private void OnWaypointRemoved(WaypointRemovedEvent evt)
        {
            Debug.Log($"[WaypointManager] Waypoint removido: {evt.WaypointId}");
        }

        #endregion

        #region Waypoint Creation

        /// <summary>
        /// Crea un nuevo waypoint en la posición especificada.
        /// </summary>
        public WaypointData CreateWaypoint(Vector3 position, Quaternion rotation)
        {
            // Validar límite máximo
            if (_waypoints.Count >= _maxWaypoints)
            {
                Debug.LogWarning($"[WaypointManager] Límite máximo de waypoints alcanzado ({_maxWaypoints}).");
                
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = $"Límite máximo de {_maxWaypoints} waypoints alcanzado.",
                    Type = MessageType.Warning,
                    Duration = 3f
                });

                return null;
            }

            try
            {
                // Instanciar waypoint
                GameObject waypointObj = Instantiate(_waypointPrefab, position, rotation, _waypointsParent);
                waypointObj.SetActive(true);

                // Obtener componente WaypointData
                WaypointData waypointData = waypointObj.GetComponent<WaypointData>();
                
                if (waypointData == null)
                {
                    Debug.LogError("[WaypointManager] El prefab no tiene componente WaypointData.");
                    Destroy(waypointObj);
                    return null;
                }

                // Generar ID único
                string waypointId = Guid.NewGuid().ToString();
                waypointData.WaypointId = waypointId;
                waypointData.WaypointName = $"Waypoint_{_waypoints.Count + 1}";

                // Registrar waypoint
                _waypoints[waypointId] = waypointData;
                _waypointsList.Add(waypointData);

                // Publicar evento
                EventBus.Instance.Publish(new WaypointPlacedEvent
                {
                    WaypointId = waypointId,
                    Position = position,
                    Rotation = rotation
                });

                Debug.Log($"[WaypointManager] Waypoint creado: {waypointId} en {position}");

                return waypointData;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WaypointManager] Error creando waypoint: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Crea un waypoint con configuración completa.
        /// </summary>
        public WaypointData CreateConfiguredWaypoint(
            Vector3 position, 
            Quaternion rotation,
            string name,
            WaypointType type,
            Color? color = null)
        {
            WaypointData waypoint = CreateWaypoint(position, rotation);

            if (waypoint != null)
            {
                Color waypointColor = color ?? WaypointData.GetDefaultColorForType(type);
                waypoint.Configure(name, type, waypointColor);
            }

            return waypoint;
        }

        #endregion

        #region Waypoint Management

        /// <summary>
        /// Obtiene un waypoint por su ID.
        /// </summary>
        public WaypointData GetWaypoint(string waypointId)
        {
            _waypoints.TryGetValue(waypointId, out WaypointData waypoint);
            return waypoint;
        }

        /// <summary>
        /// Actualiza la configuración de un waypoint existente.
        /// </summary>
        public bool UpdateWaypoint(string waypointId, string name, WaypointType type, Color color, string description = "")
        {
            if (!_waypoints.TryGetValue(waypointId, out WaypointData waypoint))
            {
                Debug.LogWarning($"[WaypointManager] Waypoint no encontrado: {waypointId}");
                return false;
            }

            waypoint.Configure(name, type, color, description);
            return true;
        }

        /// <summary>
        /// Elimina un waypoint por su ID.
        /// </summary>
        public bool RemoveWaypoint(string waypointId)
        {
            if (!_waypoints.TryGetValue(waypointId, out WaypointData waypoint))
            {
                Debug.LogWarning($"[WaypointManager] Waypoint no encontrado: {waypointId}");
                return false;
            }

            // Remover de colecciones
            _waypoints.Remove(waypointId);
            _waypointsList.Remove(waypoint);

            // Destruir GameObject
            if (waypoint != null && waypoint.gameObject != null)
            {
                Destroy(waypoint.gameObject);
            }

            // Publicar evento
            EventBus.Instance.Publish(new WaypointRemovedEvent
            {
                WaypointId = waypointId
            });

            Debug.Log($"[WaypointManager] Waypoint eliminado: {waypointId}");
            return true;
        }

        /// <summary>
        /// Elimina todos los waypoints.
        /// </summary>
        public void ClearAllWaypoints()
        {
            foreach (var waypoint in _waypointsList)
            {
                if (waypoint != null && waypoint.gameObject != null)
                {
                    Destroy(waypoint.gameObject);
                }
            }

            _waypoints.Clear();
            _waypointsList.Clear();

            Debug.Log("[WaypointManager] Todos los waypoints eliminados.");

            EventBus.Instance.Publish(new ShowMessageEvent
            {
                Message = "Todos los waypoints eliminados.",
                Type = MessageType.Info,
                Duration = 2f
            });
        }

        #endregion

        #region Queries

        /// <summary>
        /// Obtiene todos los waypoints de un tipo específico.
        /// </summary>
        public List<WaypointData> GetWaypointsByType(WaypointType type)
        {
            return _waypointsList.Where(w => w.Type == type).ToList();
        }

        /// <summary>
        /// Encuentra el waypoint más cercano a una posición.
        /// </summary>
        public WaypointData FindNearestWaypoint(Vector3 position, float maxDistance = float.MaxValue)
        {
            WaypointData nearest = null;
            float minDistance = maxDistance;

            foreach (var waypoint in _waypointsList)
            {
                float distance = Vector3.Distance(position, waypoint.Position);
                
                if (distance < minDistance && waypoint.IsNavigable)
                {
                    minDistance = distance;
                    nearest = waypoint;
                }
            }

            return nearest;
        }

        /// <summary>
        /// Encuentra waypoints dentro de un radio.
        /// </summary>
        public List<WaypointData> FindWaypointsInRadius(Vector3 center, float radius)
        {
            return _waypointsList
                .Where(w => Vector3.Distance(center, w.Position) <= radius)
                .ToList();
        }

        /// <summary>
        /// Busca waypoints por nombre (búsqueda parcial).
        /// </summary>
        public List<WaypointData> SearchWaypointsByName(string searchTerm)
        {
            if (string.IsNullOrEmpty(searchTerm))
                return new List<WaypointData>();

            return _waypointsList
                .Where(w => w.WaypointName.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        #endregion

        #region Persistence

        /// <summary>
        /// Serializa todos los waypoints a formato guardable.
        /// </summary>
        public List<WaypointSaveData> SerializeWaypoints()
        {
            List<WaypointSaveData> saveData = new List<WaypointSaveData>();

            foreach (var waypoint in _waypointsList)
            {
                saveData.Add(waypoint.ToSaveData());
            }

            return saveData;
        }

        /// <summary>
        /// Carga waypoints desde datos serializados.
        /// </summary>
        public void LoadWaypoints(List<WaypointSaveData> saveData)
        {
            // Limpiar waypoints existentes
            ClearAllWaypoints();

            foreach (var data in saveData)
            {
                // Crear waypoint
                WaypointData waypoint = CreateWaypoint(data.position, data.rotation);

                if (waypoint != null)
                {
                    // Cargar configuración
                    waypoint.LoadFromSaveData(data);
                }
            }

            Debug.Log($"[WaypointManager] {saveData.Count} waypoints cargados.");

            EventBus.Instance.Publish(new ShowMessageEvent
            {
                Message = $"{saveData.Count} waypoints cargados exitosamente.",
                Type = MessageType.Success,
                Duration = 3f
            });
        }

        #endregion

        #region Debug

        /// <summary>
        /// Muestra información de todos los waypoints en consola.
        /// </summary>
        [ContextMenu("Debug: List All Waypoints")]
        public void DebugListWaypoints()
        {
            Debug.Log($"[WaypointManager] Total waypoints: {_waypoints.Count}");

            foreach (var waypoint in _waypointsList)
            {
                Debug.Log($"  - {waypoint.WaypointName} ({waypoint.Type}) at {waypoint.Position}");
            }
        }

        #endregion
    }
}