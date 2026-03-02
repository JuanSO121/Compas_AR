// File: WaypointManager.cs
// ✅ FIX v5 — Diagnóstico completo de elementos recibidos + validación NaN
//
//  CAMBIOS (v4 → v5):
//  ─────────────────────────────────────────────────────────────────────────────
//  v4 tenía logs post-carga y guard _isLoadingBatch.
//  v5 añade:
//
//  1) Log diagnóstico PRE-carga: imprime CADA elemento recibido en saveData
//     con su índice, id (primeros 8 chars), name y posición. Esto permite
//     detectar en el logcat si el truncamiento ocurre ANTES de llegar a
//     LoadWaypoints (problema de JsonUtility/PersistenceManager) o DENTRO
//     (problema de la propia lógica de creación).
//
//  2) Validación de posición NaN: JsonUtility en IL2CPP puede generar
//     Vector3 con componentes NaN cuando el campo Color serializado tiene
//     valores en representación ARM64 no alineada (bug conocido en Unity
//     2021-2022 con structs anidados que mezclan float y bool). Si se
//     pasa una posición NaN a NavMesh.SamplePosition(), Unity lanza una
//     excepción no capturada que rompe el flujo de carga completo.
//
//  3) Validación de id y name vacíos: elementos deserializados con campos
//     string vacíos pueden causar colisiones de GUID en _waypoints Dictionary,
//     sobreescribiendo silenciosamente waypoints válidos.
//
//  4) Log de resumen mejorado: distingue entre elementos recibidos, válidos,
//     creados y omitidos para facilitar el diagnóstico.
//
//  HEREDADOS de v4:
//  - Guard _isLoadingBatch contra carga re-entrante
//  - WaypointsBatchLoadedEvent al finalizar
//  - Log de verificación post-carga

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

        private readonly Dictionary<string, WaypointData> _waypoints     = new Dictionary<string, WaypointData>();
        private readonly List<WaypointData>               _waypointsList = new List<WaypointData>();

        // ✅ v4: Guard contra carga batch re-entrante
        private bool _isLoadingBatch = false;

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
        ///
        /// ✅ FIX v5: Log diagnóstico PRE-carga de cada elemento recibido.
        ///            Validación de posición NaN, id vacío y name vacío.
        ///            Distingue entre recibidos, válidos, creados y omitidos.
        /// ✅ FIX v4: Guard _isLoadingBatch para evitar doble carga concurrente.
        /// ✅ FIX v3: Publica WaypointsBatchLoadedEvent al FINALIZAR la carga.
        /// </summary>
        public void LoadWaypoints(List<WaypointSaveData> saveData)
        {
            // ✅ v4: Guard contra carga batch re-entrante
            if (_isLoadingBatch)
            {
                Debug.LogWarning("[WaypointManager] ⚠️ LoadWaypoints ya en progreso — llamada ignorada.");
                return;
            }
            _isLoadingBatch = true;

            try
            {
                if (saveData == null || saveData.Count == 0)
                {
                    Debug.LogWarning("[WaypointManager] LoadWaypoints: saveData nulo o vacío.");
                    EventBus.Instance.Publish(new WaypointsBatchLoadedEvent { Count = 0 });
                    return;
                }

                // ✅ FIX v5: Log diagnóstico PRE-carga — imprime CADA elemento
                // recibido. Si aquí aparecen menos elementos que los guardados,
                // el problema está en JsonUtility/PersistenceManager (truncamiento).
                // Si aparecen todos pero alguno es inválido, el problema está aquí.
                Debug.Log($"[WaypointManager] 📍 Recibidos {saveData.Count} elemento(s) para cargar:");
                for (int i = 0; i < saveData.Count; i++)
                {
                    var d = saveData[i];
                    if (d == null)
                    {
                        Debug.Log($"[WaypointManager]   [{i}] ⚠️ ELEMENTO NULL");
                        continue;
                    }

                    bool hasValidId   = !string.IsNullOrEmpty(d.id);
                    bool hasValidName = !string.IsNullOrEmpty(d.name);
                    bool hasValidPos  = !float.IsNaN(d.position.x)
                                    && !float.IsNaN(d.position.y)
                                    && !float.IsNaN(d.position.z);

                    string idPreview = hasValidId
                        ? d.id.Substring(0, Math.Min(8, d.id.Length))
                        : "VACÍO";

                    Debug.Log($"[WaypointManager]   [{i}] " +
                              $"id={idPreview} " +
                              $"name='{(hasValidName ? d.name : "VACÍO")}' " +
                              $"pos={d.position} " +
                              $"type={d.type} " +
                              $"navigable={d.isNavigable} " +
                              $"→ {(hasValidId && hasValidName && hasValidPos ? "✅ VÁLIDO" : "❌ INVÁLIDO")}");
                }

                Debug.Log($"[WaypointManager] 📍 Iniciando carga de {saveData.Count} waypoint(s)...");

                // 1. Limpiar existentes
                ClearAllWaypoints();

                // 2. Crear cada waypoint con validación completa
                int received = saveData.Count;
                int created  = 0;
                int skipped  = 0;

                foreach (var data in saveData)
                {
                    // ✅ FIX v5: Validación defensiva completa
                    if (data == null)
                    {
                        Debug.LogWarning("[WaypointManager] ⚠️ WaypointSaveData null, omitiendo.");
                        skipped++;
                        continue;
                    }

                    if (string.IsNullOrEmpty(data.id))
                    {
                        Debug.LogWarning($"[WaypointManager] ⚠️ Waypoint '{data.name}' tiene id vacío, omitiendo.");
                        skipped++;
                        continue;
                    }

                    if (string.IsNullOrEmpty(data.name))
                    {
                        Debug.LogWarning($"[WaypointManager] ⚠️ Waypoint con id '{data.id.Substring(0, 8)}' tiene name vacío, omitiendo.");
                        skipped++;
                        continue;
                    }

                    // ✅ FIX v5: Detectar posiciones NaN que JsonUtility puede generar
                    // en IL2CPP cuando structs anidados (Color + Vector3) tienen alineación
                    // incorrecta en ARM64. Pasar NaN a NavMesh.SamplePosition() crashea Unity.
                    if (float.IsNaN(data.position.x) || float.IsNaN(data.position.y) || float.IsNaN(data.position.z))
                    {
                        Debug.LogWarning($"[WaypointManager] ⚠️ Waypoint '{data.name}' tiene posición NaN " +
                                         $"({data.position}), omitiendo. Posible bug de JsonUtility en IL2CPP.");
                        skipped++;
                        continue;
                    }

                    WaypointData waypoint = CreateWaypoint(data.position, data.rotation);
                    if (waypoint != null)
                    {
                        waypoint.LoadFromSaveData(data);
                        created++;
                    }
                    else
                    {
                        Debug.LogWarning($"[WaypointManager] ⚠️ CreateWaypoint retornó null para '{data.name}' " +
                                         $"(¿límite alcanzado? actual={_waypoints.Count}/{_maxWaypoints})");
                        skipped++;
                    }
                }

                // ✅ FIX v5: Log de resumen mejorado con distinción entre recibidos/válidos/creados
                Debug.Log($"[WaypointManager] ✅ LoadWaypoints COMPLETO: " +
                          $"recibidos={received}, creados={created}, omitidos={skipped}. " +
                          $"En memoria: _waypoints={_waypoints.Count}, _waypointsList={_waypointsList.Count}");

                if (created < received)
                {
                    Debug.LogWarning($"[WaypointManager] ⚠️ Solo se crearon {created} de {received} waypoints. " +
                                     $"Revisar logs anteriores para identificar los omitidos.");
                }

                // 3. Notificar que la carga en lote terminó
                EventBus.Instance.Publish(new WaypointsBatchLoadedEvent { Count = created });

                // 4. Mensaje informativo
                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message  = $"{created} waypoints cargados exitosamente.",
                    Type     = MessageType.Success,
                    Duration = 3f
                });
            }
            finally
            {
                // ✅ v4: Liberar siempre, incluso si hay excepción
                _isLoadingBatch = false;
            }
        }

        #endregion

        #region Debug

        [ContextMenu("Debug: List All Waypoints")]
        public void DebugListWaypoints()
        {
            Debug.Log($"[WaypointManager] Total waypoints: _waypoints={_waypoints.Count}, " +
                      $"_waypointsList={_waypointsList.Count}");
            foreach (var wp in _waypointsList)
                Debug.Log($"  - {wp?.WaypointName ?? "NULL"} ({wp?.Type}) at {wp?.Position}");
        }

        [ContextMenu("Debug: Verify Consistency")]
        public void DebugVerifyConsistency()
        {
            bool ok = _waypoints.Count == _waypointsList.Count;
            Debug.Log($"[WaypointManager] Consistencia: " +
                      $"_waypoints={_waypoints.Count}, _waypointsList={_waypointsList.Count} " +
                      $"→ {(ok ? "✅ OK" : "❌ INCONSISTENTE")}");
        }

        #endregion
    }
}