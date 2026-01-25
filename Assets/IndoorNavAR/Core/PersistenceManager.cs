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
    /// Serializa waypoints, configuración y estado del modelo.
    /// </summary>
    public class PersistenceManager : MonoBehaviour
    {
        [Header("Configuración")]
        [SerializeField] private string _saveFileName = "navigation_session.json";
        [SerializeField] private bool _usePlayerPrefs = false;
        [SerializeField] private bool _autoSaveEnabled = true;
        [SerializeField] private float _autoSaveInterval = 60f; // segundos

        [Header("Referencias")]
        [SerializeField] private WaypointManager _waypointManager;
        [SerializeField] private ModelLoadManager _modelLoadManager;

        private string SaveFilePath => Path.Combine(Application.persistentDataPath, _saveFileName);
        private float _timeSinceLastAutoSave;

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateDependencies();
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

        private void ValidateDependencies()
        {
            if (_waypointManager == null)
                _waypointManager = FindFirstObjectByType<WaypointManager>();

            if (_modelLoadManager == null)
                _modelLoadManager = FindFirstObjectByType<ModelLoadManager>();

            if (_waypointManager == null)
            {
                Debug.LogWarning("[PersistenceManager] WaypointManager no encontrado.");
            }

            Debug.Log($"[PersistenceManager] Ruta de guardado: {SaveFilePath}");
        }

        #endregion

        #region Save Session

        /// <summary>
        /// Guarda la sesión actual de forma asíncrona.
        /// </summary>
        public async Task<bool> SaveSession()
        {
            try
            {
                Debug.Log("[PersistenceManager] Guardando sesión...");

                // Crear datos de sesión
                SessionData sessionData = CreateSessionData();

                // Serializar a JSON
                string json = JsonUtility.ToJson(sessionData, true);

                if (_usePlayerPrefs)
                {
                    // Guardar en PlayerPrefs
                    await Task.Run(() => PlayerPrefs.SetString("SessionData", json));
                    PlayerPrefs.Save();
                }
                else
                {
                    // Guardar en archivo
                    await Task.Run(() => File.WriteAllText(SaveFilePath, json));
                }

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Sesión guardada correctamente.",
                    Type = MessageType.Success,
                    Duration = 2f
                });

                Debug.Log($"[PersistenceManager] Sesión guardada: {sessionData.waypointCount} waypoints.");

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] Error guardando sesión: {ex.Message}");

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Error al guardar sesión.",
                    Type = MessageType.Error,
                    Duration = 3f
                });

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

            // Datos del modelo
            if (_modelLoadManager != null && _modelLoadManager.IsModelLoaded)
            {
                data.hasModel = true;
                data.modelName = _modelLoadManager.CurrentModelName;
                data.modelPosition = _modelLoadManager.CurrentModel.transform.position;
                data.modelRotation = _modelLoadManager.CurrentModel.transform.rotation;
                data.modelScale = _modelLoadManager.CurrentModel.transform.localScale.x;
            }

            return data;
        }

        #endregion

        #region Load Session

        /// <summary>
        /// Carga una sesión guardada de forma asíncrona.
        /// </summary>
        public async Task<bool> LoadSession()
        {
            try
            {
                Debug.Log("[PersistenceManager] Cargando sesión...");

                // Verificar si existe guardado
                if (!HasSavedSession())
                {
                    Debug.Log("[PersistenceManager] No hay sesión guardada.");
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
                    Debug.LogWarning("[PersistenceManager] Archivo de guardado vacío.");
                    return false;
                }

                // Deserializar
                SessionData sessionData = JsonUtility.FromJson<SessionData>(json);

                if (sessionData == null)
                {
                    Debug.LogError("[PersistenceManager] Error deserializando datos.");
                    return false;
                }

                // Cargar datos
                await LoadSessionData(sessionData);

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = $"Sesión cargada: {sessionData.waypointCount} waypoints.",
                    Type = MessageType.Success,
                    Duration = 3f
                });

                Debug.Log($"[PersistenceManager] Sesión cargada: {sessionData.waypointCount} waypoints.");

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] Error cargando sesión: {ex.Message}");

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Error al cargar sesión.",
                    Type = MessageType.Error,
                    Duration = 3f
                });

                return false;
            }
        }

        private async Task LoadSessionData(SessionData data)
        {
            // Cargar modelo si existe
            if (data.hasModel && _modelLoadManager != null)
            {
                bool modelLoaded = await _modelLoadManager.LoadModelFromResources(
                    data.modelName, 
                    data.modelPosition, 
                    data.modelRotation
                );

                if (modelLoaded)
                {
                    _modelLoadManager.UpdateModelScale(data.modelScale);
                }
            }

            // Pequeño delay para asegurar que el modelo esté colocado
            await Task.Delay(500);

            // Cargar waypoints
            if (_waypointManager != null && data.waypoints != null && data.waypoints.Count > 0)
            {
                _waypointManager.LoadWaypoints(data.waypoints);
            }
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Verifica si existe una sesión guardada.
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
        /// Elimina los datos guardados.
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

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Datos guardados eliminados.",
                    Type = MessageType.Info,
                    Duration = 2f
                });

                Debug.Log("[PersistenceManager] Datos guardados eliminados.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] Error eliminando datos: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtiene información de la última sesión guardada.
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

                return $"Guardado: {data.timestamp}\nWaypoints: {data.waypointCount}";
            }
            catch
            {
                return "Error leyendo guardado";
            }
        }

        #endregion

        #region Debug

        [ContextMenu("Debug: Save Current Session")]
        private void DebugSaveSession()
        {
            _ = SaveSession();
        }

        [ContextMenu("Debug: Load Saved Session")]
        private void DebugLoadSession()
        {
            _ = LoadSession();
        }

        [ContextMenu("Debug: Clear Saved Data")]
        private void DebugClearData()
        {
            ClearSavedData();
        }

        [ContextMenu("Debug: Show Save Info")]
        private void DebugShowSaveInfo()
        {
            Debug.Log($"[PersistenceManager] {GetLastSaveInfo()}");
        }

        #endregion
    }

    #region Session Data Structure

    /// <summary>
    /// Estructura de datos para serializar sesiones completas.
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

    #endregion
}