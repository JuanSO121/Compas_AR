using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;

namespace IndoorNavAR.Core
{
    /// <summary>
    /// Gestor de persistencia para guardar y cargar sesiones.
    /// ✅ Compatible con Unity 6 y ModelLoadManager simplificado
    /// ✅ Serializa waypoints y estado del modelo
    /// ✅ Auto-guardado opcional
    /// </summary>
    public class PersistenceManager : MonoBehaviour
    {
        [Header("⚙️ Configuración")]
        [SerializeField] private string _saveFileName = "navigation_session.json";
        [SerializeField] private bool _usePlayerPrefs = false;
        
        [Header("💾 Auto-Guardado")]
        [SerializeField] private bool _autoSaveEnabled = false;
        [SerializeField] private float _autoSaveInterval = 60f;

        [Header("📦 Referencias")]
        [SerializeField] private WaypointManager _waypointManager;
        [SerializeField] private ModelLoadManager _modelLoadManager;

        [Header("🐛 Debug")]
        [SerializeField] private bool _logOperations = true;

        private string SaveFilePath => Path.Combine(Application.persistentDataPath, _saveFileName);
        private float _timeSinceLastAutoSave;

        #region Unity Lifecycle

        private void Awake()
        {
            FindDependencies();
        }

        private void Update()
        {
            if (_autoSaveEnabled)
            {
                _timeSinceLastAutoSave += Time.deltaTime;

                if (_timeSinceLastAutoSave >= _autoSaveInterval)
                {
                    _ = SaveSession();
                    _timeSinceLastAutoSave = 0f;
                }
            }
        }

        #endregion

        #region Initialization

        private void FindDependencies()
        {
            if (_waypointManager == null)
            {
                _waypointManager = FindFirstObjectByType<WaypointManager>();
            }

            if (_modelLoadManager == null)
            {
                _modelLoadManager = FindFirstObjectByType<ModelLoadManager>();
            }

            // Validación
            if (_waypointManager == null)
            {
                Debug.LogWarning("[PersistenceManager] ⚠️ WaypointManager no encontrado");
            }

            if (_modelLoadManager == null)
            {
                Debug.LogWarning("[PersistenceManager] ⚠️ ModelLoadManager no encontrado");
            }

            Log($"📂 Ruta de guardado: {SaveFilePath}");
        }

        #endregion

        #region Save Session

        /// <summary>
        /// Guarda la sesión actual de forma asíncrona
        /// </summary>
        public async Task<bool> SaveSession()
        {
            try
            {
                Log("💾 Guardando sesión...");

                // Crear datos de sesión
                SessionData sessionData = CreateSessionData();

                // Serializar a JSON
                string json = JsonUtility.ToJson(sessionData, true);

                // Guardar según configuración
                if (_usePlayerPrefs)
                {
                    await Task.Run(() => PlayerPrefs.SetString("SessionData", json));
                    PlayerPrefs.Save();
                }
                else
                {
                    await Task.Run(() => File.WriteAllText(SaveFilePath, json));
                }

                PublishMessage($"Sesión guardada: {sessionData.waypointCount} waypoints", MessageType.Success);
                Log($"✅ Sesión guardada exitosamente");

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] ❌ Error guardando: {ex.Message}");
                PublishMessage("Error al guardar sesión", MessageType.Error);
                return false;
            }
        }

        private SessionData CreateSessionData()
        {
            SessionData data = new SessionData
            {
                version = "1.0",
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                waypointCount = 0,
                waypoints = new List<WaypointSaveData>()
            };

            // Serializar waypoints
            if (_waypointManager != null)
            {
                data.waypoints = _waypointManager.SerializeWaypoints();
                data.waypointCount = data.waypoints.Count;
            }

            // Datos del modelo (solo si está cargado)
            if (_modelLoadManager != null && _modelLoadManager.IsModelLoaded)
            {
                var model = _modelLoadManager.CurrentModel;
                
                if (model != null)
                {
                    data.hasModel = true;
                    data.modelName = _modelLoadManager.CurrentModelName;
                    data.modelPosition = model.transform.position;
                    data.modelRotation = model.transform.rotation;
                    data.modelScale = model.transform.localScale.x;
                }
            }

            return data;
        }

        #endregion

        #region Load Session

        /// <summary>
        /// Carga una sesión guardada de forma asíncrona
        /// </summary>
        public async Task<bool> LoadSession()
        {
            try
            {
                Log("📂 Cargando sesión...");

                // Verificar si existe guardado
                if (!HasSavedSession())
                {
                    Log("⚠️ No hay sesión guardada");
                    return false;
                }

                // Leer JSON
                string json;

                if (_usePlayerPrefs)
                {
                    json = await Task.Run(() => PlayerPrefs.GetString("SessionData", ""));
                }
                else
                {
                    json = await Task.Run(() => File.ReadAllText(SaveFilePath));
                }

                if (string.IsNullOrEmpty(json))
                {
                    Debug.LogWarning("[PersistenceManager] ⚠️ Archivo vacío");
                    return false;
                }

                // Deserializar
                SessionData sessionData = JsonUtility.FromJson<SessionData>(json);

                if (sessionData == null)
                {
                    Debug.LogError("[PersistenceManager] ❌ Error deserializando");
                    return false;
                }

                // Cargar datos
                await LoadSessionData(sessionData);

                PublishMessage($"Sesión cargada: {sessionData.waypointCount} waypoints", MessageType.Success);
                Log($"✅ Sesión cargada: {sessionData.waypointCount} waypoints");

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] ❌ Error cargando: {ex.Message}");
                PublishMessage("Error al cargar sesión", MessageType.Error);
                return false;
            }
        }

        private async Task LoadSessionData(SessionData data)
        {
            // PASO 1: Cargar modelo si existe
            if (data.hasModel && _modelLoadManager != null && !string.IsNullOrEmpty(data.modelName))
            {
                Log($"📦 Cargando modelo: {data.modelName}");
                
                // Cargar modelo en la posición guardada
                bool modelLoaded = await _modelLoadManager.LoadModel(
                    data.modelPosition,
                    data.modelRotation
                );

                if (modelLoaded)
                {
                    // Aplicar escala guardada
                    _modelLoadManager.UpdateModelScale(data.modelScale);
                    Log($"✅ Modelo cargado y configurado");
                }
                else
                {
                    Debug.LogWarning("[PersistenceManager] ⚠️ No se pudo cargar el modelo");
                }

                // Esperar estabilización del modelo
                await Task.Delay(500);
            }

            // PASO 2: Cargar waypoints
            if (_waypointManager != null && data.waypoints != null && data.waypoints.Count > 0)
            {
                Log($"📍 Cargando {data.waypoints.Count} waypoints");
                _waypointManager.LoadWaypoints(data.waypoints);
            }
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Verifica si existe una sesión guardada
        /// </summary>
        public bool HasSavedSession()
        {
            if (_usePlayerPrefs)
            {
                return PlayerPrefs.HasKey("SessionData");
            }
            else
            {
                return File.Exists(SaveFilePath);
            }
        }

        /// <summary>
        /// Elimina los datos guardados
        /// </summary>
        public void ClearSavedData()
        {
            try
            {
                if (_usePlayerPrefs)
                {
                    PlayerPrefs.DeleteKey("SessionData");
                    PlayerPrefs.Save();
                }
                else
                {
                    if (File.Exists(SaveFilePath))
                    {
                        File.Delete(SaveFilePath);
                    }
                }

                PublishMessage("Datos eliminados", MessageType.Info);
                Log("🗑️ Datos guardados eliminados");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] ❌ Error eliminando: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtiene información de la última sesión guardada
        /// </summary>
        public string GetLastSaveInfo()
        {
            if (!HasSavedSession())
                return "Sin guardado";

            try
            {
                string json;

                if (_usePlayerPrefs)
                {
                    json = PlayerPrefs.GetString("SessionData", "");
                }
                else
                {
                    json = File.ReadAllText(SaveFilePath);
                }

                SessionData data = JsonUtility.FromJson<SessionData>(json);

                return $"Guardado: {data.timestamp}\nWaypoints: {data.waypointCount}\nModelo: {(data.hasModel ? data.modelName : "Ninguno")}";
            }
            catch
            {
                return "Error leyendo guardado";
            }
        }

        /// <summary>
        /// Obtiene estadísticas de la sesión guardada
        /// </summary>
        public SessionStats GetSessionStats()
        {
            var stats = new SessionStats
            {
                hasSession = HasSavedSession(),
                waypointCount = 0,
                hasModel = false,
                modelName = "None",
                timestamp = "N/A"
            };

            if (!stats.hasSession)
                return stats;

            try
            {
                string json = _usePlayerPrefs 
                    ? PlayerPrefs.GetString("SessionData", "") 
                    : File.ReadAllText(SaveFilePath);

                SessionData data = JsonUtility.FromJson<SessionData>(json);

                if (data != null)
                {
                    stats.waypointCount = data.waypointCount;
                    stats.hasModel = data.hasModel;
                    stats.modelName = data.modelName ?? "None";
                    stats.timestamp = data.timestamp;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] Error obteniendo stats: {ex.Message}");
            }

            return stats;
        }

        #endregion

        #region Helpers

        private void Log(string message)
        {
            if (_logOperations)
            {
                Debug.Log($"[PersistenceManager] {message}");
            }
        }

        private void PublishMessage(string message, MessageType type)
        {
            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message = message,
                Type = type,
                Duration = type == MessageType.Error ? 5f : 3f
            });
        }

        #endregion

        #region Debug

        [ContextMenu("💾 Save Session")]
        private void DebugSaveSession()
        {
            _ = SaveSession();
        }

        [ContextMenu("📂 Load Session")]
        private void DebugLoadSession()
        {
            _ = LoadSession();
        }

        [ContextMenu("🗑️ Clear Data")]
        private void DebugClearData()
        {
            ClearSavedData();
        }

        [ContextMenu("ℹ️ Show Info")]
        private void DebugShowInfo()
        {
            Debug.Log("========== SESSION INFO ==========");
            Debug.Log(GetLastSaveInfo());
            Debug.Log("==================================");
        }

        [ContextMenu("📊 Show Stats")]
        private void DebugShowStats()
        {
            var stats = GetSessionStats();
            Debug.Log("========== SESSION STATS ==========");
            Debug.Log($"Tiene sesión: {stats.hasSession}");
            Debug.Log($"Waypoints: {stats.waypointCount}");
            Debug.Log($"Tiene modelo: {stats.hasModel}");
            Debug.Log($"Modelo: {stats.modelName}");
            Debug.Log($"Fecha: {stats.timestamp}");
            Debug.Log("===================================");
        }

        #endregion
    }

    #region Data Structures

    /// <summary>
    /// Estructura de datos para serializar sesiones completas
    /// </summary>
    [Serializable]
    public class SessionData
    {
        public string version;
        public string timestamp;

        // Waypoints
        public int waypointCount;
        public List<WaypointSaveData> waypoints;

        // Modelo
        public bool hasModel;
        public string modelName;
        public Vector3 modelPosition;
        public Quaternion modelRotation;
        public float modelScale;
    }

    /// <summary>
    /// Estadísticas de sesión guardada
    /// </summary>
    public struct SessionStats
    {
        public bool hasSession;
        public int waypointCount;
        public bool hasModel;
        public string modelName;
        public string timestamp;
    }

    #endregion
}