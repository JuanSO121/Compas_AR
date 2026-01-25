using System.Collections.Generic;
using UnityEngine;

namespace IndoorNavAR.Utils
{
    /// <summary>
    /// Pool genérico de objetos para optimización de performance.
    /// Reduce allocaciones evitando Instantiate/Destroy repetidos.
    /// </summary>
    /// <typeparam name="T">Tipo de componente a poolear</typeparam>
    public class ObjectPool<T> where T : Component
    {
        private readonly T _prefab;
        private readonly Transform _parent;
        private readonly Queue<T> _availableObjects = new Queue<T>();
        private readonly HashSet<T> _allObjects = new HashSet<T>();
        private readonly int _initialSize;
        private readonly int _maxSize;
        private readonly bool _expandable;

        #region Properties

        public int TotalCount => _allObjects.Count;
        public int AvailableCount => _availableObjects.Count;
        public int ActiveCount => TotalCount - AvailableCount;

        #endregion

        #region Constructor

        /// <summary>
        /// Crea un nuevo pool de objetos.
        /// </summary>
        /// <param name="prefab">Prefab a instanciar</param>
        /// <param name="parent">Transform padre para organización</param>
        /// <param name="initialSize">Tamaño inicial del pool</param>
        /// <param name="maxSize">Tamaño máximo (0 = ilimitado)</param>
        /// <param name="expandable">Si puede crecer más allá del tamaño inicial</param>
        public ObjectPool(T prefab, Transform parent = null, int initialSize = 10, int maxSize = 0, bool expandable = true)
        {
            _prefab = prefab;
            _parent = parent;
            _initialSize = initialSize;
            _maxSize = maxSize;
            _expandable = expandable;

            // Pre-instanciar objetos
            Prewarm();
        }

        #endregion

        #region Initialization

        private void Prewarm()
        {
            for (int i = 0; i < _initialSize; i++)
            {
                CreateNewObject();
            }
        }

        private T CreateNewObject()
        {
            T newObj = Object.Instantiate(_prefab, _parent);
            newObj.gameObject.SetActive(false);
            
            _allObjects.Add(newObj);
            _availableObjects.Enqueue(newObj);

            return newObj;
        }

        #endregion

        #region Get/Return

        /// <summary>
        /// Obtiene un objeto del pool.
        /// </summary>
        public T Get()
        {
            T obj;

            if (_availableObjects.Count > 0)
            {
                obj = _availableObjects.Dequeue();
            }
            else if (_expandable && (_maxSize == 0 || _allObjects.Count < _maxSize))
            {
                obj = CreateNewObject();
                Debug.Log($"[ObjectPool] Pool expandido: {_allObjects.Count} objetos.");
            }
            else
            {
                Debug.LogWarning($"[ObjectPool] Pool agotado. Máximo: {_maxSize}");
                return null;
            }

            obj.gameObject.SetActive(true);
            return obj;
        }

        /// <summary>
        /// Obtiene un objeto y lo posiciona.
        /// </summary>
        public T Get(Vector3 position, Quaternion rotation)
        {
            T obj = Get();

            if (obj != null)
            {
                obj.transform.position = position;
                obj.transform.rotation = rotation;
            }

            return obj;
        }

        /// <summary>
        /// Devuelve un objeto al pool.
        /// </summary>
        public void Return(T obj)
        {
            if (obj == null)
                return;

            if (!_allObjects.Contains(obj))
            {
                Debug.LogWarning("[ObjectPool] Intento de devolver objeto que no pertenece al pool.");
                return;
            }

            obj.gameObject.SetActive(false);
            obj.transform.SetParent(_parent);

            if (!_availableObjects.Contains(obj))
            {
                _availableObjects.Enqueue(obj);
            }
        }

        /// <summary>
        /// Devuelve todos los objetos activos al pool.
        /// </summary>
        public void ReturnAll()
        {
            foreach (T obj in _allObjects)
            {
                if (obj != null && obj.gameObject.activeSelf)
                {
                    Return(obj);
                }
            }
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Destruye todos los objetos del pool.
        /// </summary>
        public void Clear()
        {
            foreach (T obj in _allObjects)
            {
                if (obj != null)
                {
                    Object.Destroy(obj.gameObject);
                }
            }

            _allObjects.Clear();
            _availableObjects.Clear();
        }

        #endregion
    }

    /// <summary>
    /// Manager de pools para gestionar múltiples pools.
    /// </summary>
    public class PoolManager : MonoBehaviour
    {
        private static PoolManager _instance;
        public static PoolManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("[Pool Manager]");
                    _instance = go.AddComponent<PoolManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private readonly Dictionary<string, object> _pools = new Dictionary<string, object>();

        /// <summary>
        /// Registra un nuevo pool.
        /// </summary>
        public void RegisterPool<T>(string poolName, ObjectPool<T> pool) where T : Component
        {
            if (_pools.ContainsKey(poolName))
            {
                Debug.LogWarning($"[PoolManager] Pool ya existe: {poolName}");
                return;
            }

            _pools[poolName] = pool;
            Debug.Log($"[PoolManager] Pool registrado: {poolName}");
        }

        /// <summary>
        /// Obtiene un pool registrado.
        /// </summary>
        public ObjectPool<T> GetPool<T>(string poolName) where T : Component
        {
            if (_pools.TryGetValue(poolName, out object pool))
            {
                return pool as ObjectPool<T>;
            }

            Debug.LogWarning($"[PoolManager] Pool no encontrado: {poolName}");
            return null;
        }

        /// <summary>
        /// Crea y registra un nuevo pool.
        /// </summary>
        public ObjectPool<T> CreatePool<T>(string poolName, T prefab, int initialSize = 10, int maxSize = 0) where T : Component
        {
            if (_pools.ContainsKey(poolName))
            {
                return GetPool<T>(poolName);
            }

            // Crear contenedor
            Transform parent = new GameObject($"[Pool] {poolName}").transform;
            parent.SetParent(transform);

            // Crear pool
            ObjectPool<T> pool = new ObjectPool<T>(prefab, parent, initialSize, maxSize);
            RegisterPool(poolName, pool);

            return pool;
        }

        /// <summary>
        /// Limpia todos los pools.
        /// </summary>
        public void ClearAllPools()
        {
            foreach (var poolEntry in _pools)
            {
                // Usar reflection para llamar Clear() en cada pool
                var poolType = poolEntry.Value.GetType();
                var clearMethod = poolType.GetMethod("Clear");
                clearMethod?.Invoke(poolEntry.Value, null);
            }

            _pools.Clear();
            Debug.Log("[PoolManager] Todos los pools limpiados.");
        }

        private void OnDestroy()
        {
            ClearAllPools();
        }
    }

    /// <summary>
    /// Extensión para facilitar uso de pools en WaypointManager.
    /// </summary>
    public static class WaypointPoolExtensions
    {
        private const string WAYPOINT_POOL_NAME = "WaypointPool";

        /// <summary>
        /// Inicializa el pool de waypoints.
        /// </summary>
        public static void InitializeWaypointPool(GameObject prefab, int initialSize = 20)
        {
            PoolManager.Instance.CreatePool(WAYPOINT_POOL_NAME, prefab.GetComponent<Core.Data.WaypointData>(), initialSize, 100);
        }

        /// <summary>
        /// Obtiene un waypoint del pool.
        /// </summary>
        public static Core.Data.WaypointData GetWaypointFromPool()
        {
            var pool = PoolManager.Instance.GetPool<Core.Data.WaypointData>(WAYPOINT_POOL_NAME);
            return pool?.Get();
        }

        /// <summary>
        /// Devuelve un waypoint al pool.
        /// </summary>
        public static void ReturnWaypointToPool(Core.Data.WaypointData waypoint)
        {
            var pool = PoolManager.Instance.GetPool<Core.Data.WaypointData>(WAYPOINT_POOL_NAME);
            pool?.Return(waypoint);
        }

        /// <summary>
        /// Devuelve todos los waypoints al pool.
        /// </summary>
        public static void ReturnAllWaypoints()
        {
            var pool = PoolManager.Instance.GetPool<Core.Data.WaypointData>(WAYPOINT_POOL_NAME);
            pool?.ReturnAll();
        }
    }
}