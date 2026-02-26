// File: WaypointManager.cs
// ✅ FIX v3 — Añadido WaypointsBatchLoadedEvent al final de LoadWaypoints()
//    para que MobileNavigationUI pueda refrescar la lista UNA SOLA VEZ
//    en lugar de N veces (una por cada WaypointPlacedEvent individual).

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Core.Managers
{
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

        private void OnEnable()  => SubscribeToEvents();
        private void OnDisable() => UnsubscribeFromEvents();

        #endregion

        #region Initialization

        private void ValidateDependencies()
        {
            if (_waypointPrefab == null)
            {
                Debug.LogError("[WaypointManager] Waypoint Prefab no asignado. Creando prefab básico...");
                _waypointPrefab = CreateDefaultWaypointPrefab();
            }

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
            GameObject prefab = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            prefab.name = "Waypoint";
            prefab.AddComponent<WaypointData>();
            Renderer renderer = prefab.GetComponent<Renderer>();
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = new Color(0f, 1f, 1f, 0.7f);
            renderer.material = mat;
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
            => Debug.Log($"[WaypointManager] Waypoint colocado: {evt.WaypointId} en {evt.Position}");

        private void OnWaypointRemoved(WaypointRemovedEvent evt)
            => Debug.Log($"[WaypointManager] Waypoint removido: {evt.WaypointId}");

        #endregion

        #region Waypoint Creation

        public WaypointData CreateWaypoint(Vector3 position, Quaternion rotation)
        {
            if (_waypoints.Count >= _maxWaypoints)
            {
                Debug.LogWarning($"[WaypointManager] Límite máximo de waypoints alcanzado ({_maxWaypoints}).");
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message  = $"Límite máximo de {_maxWaypoints} waypoints alcanzado.",
                    Type     = MessageType.Warning,
                    Duration = 3f
                });
                return null;
            }

            try
            {
                GameObject waypointObj = Instantiate(_waypointPrefab, position, rotation, _waypointsParent);
                waypointObj.SetActive(true);

                WaypointData waypointData = waypointObj.GetComponent<WaypointData>();
                if (waypointData == null)
                {
                    Debug.LogError("[WaypointManager] El prefab no tiene componente WaypointData.");
                    Destroy(waypointObj);
                    return null;
                }

                string waypointId = Guid.NewGuid().ToString();
                waypointData.WaypointId   = waypointId;
                waypointData.WaypointName = $"Waypoint_{_waypoints.Count + 1}";

                _waypoints[waypointId] = waypointData;
                _waypointsList.Add(waypointData);

                EventBus.Instance.Publish(new WaypointPlacedEvent
                {
                    WaypointId = waypointId,
                    Position   = position,
                    Rotation   = rotation
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

        public WaypointData CreateConfiguredWaypoint(
            Vector3 position, Quaternion rotation,
            string name, WaypointType type, Color? color = null)
        {
            WaypointData waypoint = CreateWaypoint(position, rotation);
            if (waypoint != null)
                waypoint.Configure(name, type, color ?? WaypointData.GetDefaultColorForType(type));
            return waypoint;
        }

        #endregion

        #region Waypoint Management

        public WaypointData GetWaypoint(string waypointId)
        {
            _waypoints.TryGetValue(waypointId, out WaypointData waypoint);
            return waypoint;
        }

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

        public bool RemoveWaypoint(string waypointId)
        {
            if (!_waypoints.TryGetValue(waypointId, out WaypointData waypoint))
            {
                Debug.LogWarning($"[WaypointManager] Waypoint no encontrado: {waypointId}");
                return false;
            }

            _waypoints.Remove(waypointId);
            _waypointsList.Remove(waypoint);

            if (waypoint != null && waypoint.gameObject != null)
                Destroy(waypoint.gameObject);

            EventBus.Instance.Publish(new WaypointRemovedEvent { WaypointId = waypointId });
            Debug.Log($"[WaypointManager] Waypoint eliminado: {waypointId}");
            return true;
        }

        public void ClearAllWaypoints()
        {
            foreach (var waypoint in _waypointsList)
                if (waypoint != null && waypoint.gameObject != null)
                    Destroy(waypoint.gameObject);

            _waypoints.Clear();
            _waypointsList.Clear();

            Debug.Log("[WaypointManager] Todos los waypoints eliminados.");
            EventBus.Instance.Publish(new ShowMessageEvent
            {
                Message  = "Todos los waypoints eliminados.",
                Type     = MessageType.Info,
                Duration = 2f
            });
        }

        #endregion

        #region Queries

        public List<WaypointData> GetWaypointsByType(WaypointType type)
            => _waypointsList.Where(w => w.Type == type).ToList();

        public WaypointData FindNearestWaypoint(Vector3 position, float maxDistance = float.MaxValue)
        {
            WaypointData nearest  = null;
            float        minDist  = maxDistance;
            foreach (var wp in _waypointsList)
            {
                float d = Vector3.Distance(position, wp.Position);
                if (d < minDist && wp.IsNavigable) { minDist = d; nearest = wp; }
            }
            return nearest;
        }

        public List<WaypointData> FindWaypointsInRadius(Vector3 center, float radius)
            => _waypointsList.Where(w => Vector3.Distance(center, w.Position) <= radius).ToList();

        public List<WaypointData> SearchWaypointsByName(string searchTerm)
        {
            if (string.IsNullOrEmpty(searchTerm)) return new List<WaypointData>();
            return _waypointsList
                .Where(w => w.WaypointName.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        #endregion

        #region Persistence

        public List<WaypointSaveData> SerializeWaypoints()
        {
            var saveData = new List<WaypointSaveData>();
            foreach (var wp in _waypointsList)
                saveData.Add(wp.ToSaveData());
            return saveData;
        }

        /// <summary>
        /// Carga waypoints desde datos serializados.
        /// ✅ FIX v3: Publica WaypointsBatchLoadedEvent al FINALIZAR la carga
        ///    para que la UI refresque la lista una sola vez en lugar de N veces.
        /// </summary>
        public void LoadWaypoints(List<WaypointSaveData> saveData)
        {
            // 1. Limpiar existentes (genera WaypointRemovedEvents — la UI los ignora con debounce)
            ClearAllWaypoints();

            // 2. Crear cada waypoint (genera WaypointPlacedEvents — la UI los ignora con debounce)
            foreach (var data in saveData)
            {
                WaypointData waypoint = CreateWaypoint(data.position, data.rotation);
                if (waypoint != null)
                    waypoint.LoadFromSaveData(data);
            }

            Debug.Log($"[WaypointManager] {saveData.Count} waypoints cargados.");

            // 3. Notificar que la carga en LOTE terminó
            // ✅ FIX v3: Este evento cancela el debounce en MobileNavigationUI y
            //    dispara RefreshWaypointList() UNA SOLA VEZ con todos los datos listos.
            EventBus.Instance.Publish(new WaypointsBatchLoadedEvent { Count = saveData.Count });

            // 4. Mensaje informativo (ShowMessageEvent — ya manejado por la UI vía toast)
            EventBus.Instance.Publish(new ShowMessageEvent
            {
                Message  = $"{saveData.Count} waypoints cargados exitosamente.",
                Type     = MessageType.Success,
                Duration = 3f
            });
        }

        #endregion

        #region Debug

        [ContextMenu("Debug: List All Waypoints")]
        public void DebugListWaypoints()
        {
            Debug.Log($"[WaypointManager] Total waypoints: {_waypoints.Count}");
            foreach (var wp in _waypointsList)
                Debug.Log($"  - {wp.WaypointName} ({wp.Type}) at {wp.Position}");
        }

        #endregion
    }
}