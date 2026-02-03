using System;
using System.Threading.Tasks;
using UnityEngine;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Core.Controllers;
using IndoorNavAR.AR;
using IndoorNavAR.Navigation;

namespace IndoorNavAR.Core
{
    /// <summary>
    /// Gestor principal del sistema de navegación AR
    /// ✅ Compatible con Unity 6 y AR Foundation 6
    /// ✅ Integración completa con sistema optimizado
    /// ✅ Flujo mejorado de inicialización
    /// </summary>
    public class NavigationManager : MonoBehaviour
    {
        [Header("📦 Managers")]
        [SerializeField] private ARSessionManager _arSessionManager;
        [SerializeField] private WaypointManager _waypointManager;
        [SerializeField] private ModelLoadManager _modelLoadManager;
        [SerializeField] private PlacementController _placementController;
        [SerializeField] private PersistenceManager _persistenceManager;

        [Header("🧭 Sistema de Navegación")]
        [SerializeField] private MultiMeshWalkableSurfaceGenerator _walkableSurfaceGenerator;
        [SerializeField] private NavigationAgent _navigationAgent;
        [SerializeField] private NavMeshAgentCoordinator _navMeshCoordinator;
        [SerializeField] private AgentFloorPlacement _agentFloorPlacement;

        [Header("⚙️ Configuración")]
        [SerializeField] private bool _autoInitialize = true;
        [SerializeField] private bool _loadPreviousSession = false;
        [SerializeField] private bool _autoLoadModel = true;

        [Header("🐛 Debug")]
        [SerializeField] private bool _logDetailedEvents = false;

        private AppMode _currentState = AppMode.Initialization;
        private bool _isInitialized;

        #region Properties

        public bool IsInitialized => _isInitialized;
        public AppMode CurrentState => _currentState;
        public ARSessionManager ARSession => _arSessionManager;
        public WaypointManager Waypoints => _waypointManager;
        public ModelLoadManager Models => _modelLoadManager;
        public PlacementController Placement => _placementController;
        public MultiMeshWalkableSurfaceGenerator WalkableSurface => _walkableSurfaceGenerator;
        public NavigationAgent Agent => _navigationAgent;
        public NavMeshAgentCoordinator NavMeshCoordinator => _navMeshCoordinator;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            FindComponents();
        }

        private void OnEnable()
        {
            SubscribeEvents();
        }

        private void OnDisable()
        {
            UnsubscribeEvents();
        }

        private void Start()
        {
            if (_autoInitialize)
            {
                _ = Initialize();
            }
        }

        #endregion

        #region Component Discovery

        private void FindComponents()
        {
            Log("🔍 Buscando componentes del sistema...");

            // Managers básicos
            _arSessionManager ??= FindFirstObjectByType<ARSessionManager>();
            _waypointManager ??= FindFirstObjectByType<WaypointManager>();
            _modelLoadManager ??= FindFirstObjectByType<ModelLoadManager>();
            _placementController ??= FindFirstObjectByType<PlacementController>();
            _persistenceManager ??= FindFirstObjectByType<PersistenceManager>();

            // Sistema navegación
            _walkableSurfaceGenerator ??= FindFirstObjectByType<MultiMeshWalkableSurfaceGenerator>();
            _navigationAgent ??= FindFirstObjectByType<NavigationAgent>();
            _navMeshCoordinator ??= FindFirstObjectByType<NavMeshAgentCoordinator>();
            _agentFloorPlacement ??= FindFirstObjectByType<AgentFloorPlacement>();

            ValidateComponents();
        }

        private void ValidateComponents()
        {
            bool hasErrors = false;

            // Críticos
            if (_arSessionManager == null)
            {
                Debug.LogError("[NavManager] ❌ ARSessionManager faltante");
                hasErrors = true;
            }

            if (_waypointManager == null)
            {
                Debug.LogError("[NavManager] ❌ WaypointManager faltante");
                hasErrors = true;
            }

            if (_walkableSurfaceGenerator == null)
            {
                Debug.LogError("[NavManager] ❌ MultiMeshWalkableSurfaceGenerator faltante");
                hasErrors = true;
            }

            if (_navigationAgent == null)
            {
                Debug.LogError("[NavManager] ❌ NavigationAgent faltante");
                hasErrors = true;
            }

            // Opcionales
            if (_modelLoadManager == null)
            {
                Debug.LogWarning("[NavManager] ⚠️ ModelLoadManager no encontrado");
            }

            if (_navMeshCoordinator == null)
            {
                Debug.LogWarning("[NavManager] ⚠️ NavMeshCoordinator no encontrado");
            }

            if (hasErrors)
            {
                Debug.LogError("[NavManager] ❌ Sistema deshabilitado");
                enabled = false;
            }
            else
            {
                Debug.Log("[NavManager] ✅ Componentes validados");
            }
        }

        #endregion

        #region Events

        private void SubscribeEvents()
        {
            EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
            EventBus.Instance?.Subscribe<NavigationStartedEvent>(OnNavigationStarted);
            EventBus.Instance?.Subscribe<NavigationCompletedEvent>(OnNavigationCompleted);
            EventBus.Instance?.Subscribe<NavigationCancelledEvent>(OnNavigationCancelled);
        }

        private void UnsubscribeEvents()
        {
            EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);
            EventBus.Instance?.Unsubscribe<NavigationStartedEvent>(OnNavigationStarted);
            EventBus.Instance?.Unsubscribe<NavigationCompletedEvent>(OnNavigationCompleted);
            EventBus.Instance?.Unsubscribe<NavigationCancelledEvent>(OnNavigationCancelled);
        }

        private void OnModelLoaded(ModelLoadedEvent evt)
        {
            LogEvent($"📦 Modelo cargado: {evt.ModelName}");
            ChangeState(AppMode.ModelPlacement);
        }

        private void OnNavigationStarted(NavigationStartedEvent evt)
        {
            LogEvent($"🧭 Navegación iniciada: {evt.DestinationWaypointId}");
            ChangeState(AppMode.Navigation);
        }

        private void OnNavigationCompleted(NavigationCompletedEvent evt)
        {
            LogEvent($"✅ Navegación completada: {evt.TotalTime:F1}s");
            ChangeState(AppMode.WaypointPlacement);
        }

        private void OnNavigationCancelled(NavigationCancelledEvent evt)
        {
            LogEvent($"🛑 Navegación cancelada: {evt.Reason}");
            ChangeState(AppMode.WaypointPlacement);
        }

        #endregion

        #region Initialization

        public async Task<bool> Initialize()
        {
            if (_isInitialized)
            {
                Debug.LogWarning("[NavManager] ⚠️ Ya inicializado");
                return true;
            }

            try
            {
                Debug.Log("[NavManager] ═══════════════════════════");
                Debug.Log("[NavManager] 🚀 INICIANDO SISTEMA AR");
                Debug.Log("[NavManager] ═══════════════════════════");

                ChangeState(AppMode.Initialization);

                // PASO 1: AR Session
                await InitializeAR();

                // PASO 2: Cargar sesión previa (opcional)
                if (_loadPreviousSession && _persistenceManager != null)
                {
                    Debug.Log("[NavManager] 📂 Cargando sesión...");
                    await _persistenceManager.LoadSession();
                }

                // PASO 3: Cambiar a detección de planos
                ChangeState(AppMode.PlaneDetection);

                // PASO 4: Auto-cargar modelo (opcional)
                if (_autoLoadModel && _modelLoadManager != null)
                {
                    Debug.Log("[NavManager] 📦 Cargando modelo automáticamente...");
                    await Task.Delay(1000); // Esperar planos
                    await _modelLoadManager.LoadModelOnLargestPlaneAsync();
                }

                _isInitialized = true;

                PublishMessage("Sistema iniciado", MessageType.Success);

                Debug.Log("[NavManager] ═══════════════════════════");
                Debug.Log("[NavManager] ✅ SISTEMA LISTO");
                Debug.Log("[NavManager] ═══════════════════════════");

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavManager] ❌ Error: {ex.Message}");
                PublishMessage("Error inicializando sistema", MessageType.Error);
                return false;
            }
        }

        private async Task InitializeAR()
        {
            if (_arSessionManager == null)
            {
                Debug.LogWarning("[NavManager] ⚠️ ARSessionManager no disponible");
                return;
            }

            Debug.Log("[NavManager] 📡 Esperando AR Session...");

            int timeout = 10;
            while (!_arSessionManager.IsSessionReady && timeout > 0)
            {
                await Task.Delay(1000);
                timeout--;
            }

            if (!_arSessionManager.IsSessionReady)
            {
                throw new Exception("AR Session timeout");
            }

            Debug.Log("[NavManager] ✅ AR Session lista");
        }

        #endregion

        #region State Management

        public void ChangeState(AppMode newState)
        {
            var prevState = _currentState;
            _currentState = newState;

            EventBus.Instance?.Publish(new AppModeChangedEvent
            {
                PreviousMode = prevState,
                NewMode = newState
            });

            LogEvent($"🔄 Estado: {prevState} → {newState}");
        }

        #endregion

        #region Model Management

        public async Task<bool> LoadModelOnLargestPlane()
        {
            if (_modelLoadManager == null)
            {
                Debug.LogWarning("[NavManager] ⚠️ ModelLoadManager no disponible");
                return false;
            }

            ChangeState(AppMode.ModelPlacement);
            return await _modelLoadManager.LoadModelOnLargestPlaneAsync();
        }

        public void UnloadModel()
        {
            _modelLoadManager?.UnloadCurrentModel();
            
            if (_walkableSurfaceGenerator != null)
            {
                _walkableSurfaceGenerator.Clear();
            }
        }

        #endregion

        #region Navigation

        public bool NavigateToWaypoint(WaypointData waypoint)
        {
            if (_navigationAgent == null || waypoint == null)
                return false;

            bool success = _navigationAgent.NavigateToWaypoint(waypoint);

            if (success)
            {
                Debug.Log($"[NavManager] 🧭 Navegando a: {waypoint.WaypointName}");
            }

            return success;
        }

        public void StopNavigation()
        {
            _navigationAgent?.StopNavigation("Usuario canceló");
        }

        #endregion

        #region Waypoints

        public void ToggleWaypointPlacement(bool enabled)
        {
            if (_placementController != null)
            {
                _placementController.TogglePlacementMode(enabled);
                
                if (enabled)
                {
                    ChangeState(AppMode.WaypointPlacement);
                }
            }
        }

        public void ClearAllWaypoints()
        {
            _waypointManager?.ClearAllWaypoints();
        }

        #endregion

        #region System Control

        public void ResetSystem()
        {
            Debug.Log("[NavManager] 🔄 Reseteando sistema...");

            StopNavigation();
            ClearAllWaypoints();
            UnloadModel();
            ToggleWaypointPlacement(false);
            
            ChangeState(AppMode.PlaneDetection);

            PublishMessage("Sistema reseteado", MessageType.Info);
        }

        #endregion

        #region Utilities

        private void LogEvent(string msg)
        {
            if (_logDetailedEvents)
            {
                Debug.Log($"[NavManager] {msg}");
            }
        }

        private void Log(string msg)
        {
            Debug.Log($"[NavManager] {msg}");
        }

        private void PublishMessage(string msg, MessageType type)
        {
            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message = msg,
                Type = type,
                Duration = type == MessageType.Error ? 5f : 3f
            });
        }

        #endregion

        #region Debug

        [ContextMenu("ℹ️ System Info")]
        private void DebugInfo()
        {
            Debug.Log("══════════════════════════════");
            Debug.Log("NAVIGATION SYSTEM INFO");
            Debug.Log("══════════════════════════════");
            Debug.Log($"Estado: {_currentState}");
            Debug.Log($"Inicializado: {_isInitialized}");
            Debug.Log($"AR Ready: {_arSessionManager?.IsSessionReady ?? false}");
            Debug.Log($"Modelo: {_modelLoadManager?.CurrentModelName ?? "None"}");
            Debug.Log($"Waypoints: {_waypointManager?.WaypointCount ?? 0}");
            Debug.Log($"Navegando: {_navigationAgent?.IsNavigating ?? false}");
            Debug.Log("══════════════════════════════");
        }

        [ContextMenu("📦 Load Model")]
        private void DebugLoadModel()
        {
            _ = LoadModelOnLargestPlane();
        }

        [ContextMenu("🔄 Reset")]
        private void DebugReset()
        {
            ResetSystem();
        }

        #endregion
    }
}