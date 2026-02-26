// File: NavigationManager.cs
// ✅ FIX #1 — Exclusión mutua estricta: si hay sesión guardada y se restaura con éxito,
//             NUNCA ejecuta el flujo completo ni _autoLoadModel.
// ✅ FIX #2 — Llama ConfirmModelPositioned() en todos los StartPoints después de
//             RestoreModelTransform y ANTES de cargar el NavMesh.

using System;
using System.Threading.Tasks;
using UnityEngine;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Core.Controllers;
using IndoorNavAR.AR;
using IndoorNavAR.Navigation;

namespace IndoorNavAR.Core
{
    public class NavigationManager : MonoBehaviour
    {
        [Header("📦 Managers")]
        [SerializeField] private ARSessionManager      _arSessionManager;
        [SerializeField] private WaypointManager       _waypointManager;
        [SerializeField] private ModelLoadManager      _modelLoadManager;
        [SerializeField] private PlacementController   _placementController;
        [SerializeField] private PersistenceManager    _persistenceManager;

        [Header("🧭 Sistema de Navegación")]
        [SerializeField] private MultiLevelNavMeshGenerator _walkableSurfaceGenerator;
        [SerializeField] private NavigationAgent             _navigationAgent;
        [SerializeField] private NavMeshAgentCoordinator    _navMeshCoordinator;

        [Header("⚙️ Configuración")]
        [SerializeField] private bool _autoInitialize  = true;
        [SerializeField] private bool _autoLoadModel   = true;
        // _loadPreviousSession ya no se usa — la detección es automática.

        [Header("🐛 Debug")]
        [SerializeField] private bool _logDetailedEvents = false;

        private AppMode _currentState = AppMode.Initialization;
        private bool    _isInitialized;

        #region Properties

        public bool       IsInitialized     => _isInitialized;
        public AppMode    CurrentState       => _currentState;
        public ARSessionManager  ARSession  => _arSessionManager;
        public WaypointManager   Waypoints  => _waypointManager;
        public ModelLoadManager  Models     => _modelLoadManager;
        public PlacementController Placement => _placementController;
        public MultiLevelNavMeshGenerator WalkableSurface => _walkableSurfaceGenerator;
        public NavigationAgent   Agent      => _navigationAgent;
        public NavMeshAgentCoordinator NavMeshCoordinator => _navMeshCoordinator;

        #endregion

        #region Unity Lifecycle

        private void Awake()     => FindComponents();
        private void OnEnable()  => SubscribeEvents();
        private void OnDisable() => UnsubscribeEvents();

        private void Start()
        {
            if (_autoInitialize)
                _ = Initialize();
        }

        #endregion

        #region Component Discovery

        private void FindComponents()
        {
            Log("🔍 Buscando componentes del sistema...");

            _arSessionManager       ??= FindFirstObjectByType<ARSessionManager>();
            _waypointManager        ??= FindFirstObjectByType<WaypointManager>();
            _modelLoadManager       ??= FindFirstObjectByType<ModelLoadManager>();
            _placementController    ??= FindFirstObjectByType<PlacementController>();
            _persistenceManager     ??= FindFirstObjectByType<PersistenceManager>();
            _walkableSurfaceGenerator ??= FindFirstObjectByType<MultiLevelNavMeshGenerator>();
            _navigationAgent        ??= FindFirstObjectByType<NavigationAgent>();
            _navMeshCoordinator     ??= FindFirstObjectByType<NavMeshAgentCoordinator>();

            ValidateComponents();
        }

        private void ValidateComponents()
        {
            bool hasErrors = false;

            if (_arSessionManager == null)
            { Debug.LogError("[NavManager] ❌ ARSessionManager faltante"); hasErrors = true; }
            if (_waypointManager == null)
            { Debug.LogError("[NavManager] ❌ WaypointManager faltante"); hasErrors = true; }
            if (_walkableSurfaceGenerator == null)
            { Debug.LogError("[NavManager] ❌ MultiLevelNavMeshGenerator faltante"); hasErrors = true; }
            if (_navigationAgent == null)
            { Debug.LogError("[NavManager] ❌ NavigationAgent faltante"); hasErrors = true; }
            if (_modelLoadManager == null)
                Debug.LogWarning("[NavManager] ⚠️ ModelLoadManager no encontrado");
            if (_navMeshCoordinator == null)
                Debug.LogWarning("[NavManager] ⚠️ NavMeshCoordinator no encontrado");

            if (hasErrors)
            { Debug.LogError("[NavManager] ❌ Sistema deshabilitado"); enabled = false; }
            else
                Debug.Log("[NavManager] ✅ Componentes validados");
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
            if (_isInitialized) { Debug.LogWarning("[NavManager] ⚠️ Ya inicializado"); return true; }

            try
            {
                Debug.Log("[NavManager] 🚀 INICIANDO SISTEMA AR");
                ChangeState(AppMode.Initialization);

                // ── Verificar si existe sesión + NavMesh guardados ────────────────
                bool hasSavedSession = _persistenceManager != null && _persistenceManager.HasSavedSession();
                bool hasSavedNavMesh = _persistenceManager != null && _persistenceManager.HasSavedNavMesh;

                Debug.Log($"[NavManager] 🔍 hasSavedSession={hasSavedSession} | hasSavedNavMesh={hasSavedNavMesh}");

                if (hasSavedSession && hasSavedNavMesh)
                {
                    Debug.Log("[NavManager] 💾 Sesión guardada detectada → carga rápida.");
                    bool ok = await InitializeFromSavedSession();

                    if (ok)
                    {
                        // ✅ FIX #1: return INMEDIATO. Nunca continúa al flujo completo.
                        _isInitialized = true;
                        PublishMessage("Sesión restaurada", MessageType.Success);
                        Debug.Log("[NavManager] ✅ RESTAURADO DESDE SESIÓN GUARDADA — FIN");
                        return true;
                    }

                    // La carga rápida falló → limpiar cualquier modelo parcial antes de continuar
                    Debug.LogWarning("[NavManager] ⚠️ Falló carga rápida — limpiando y continuando con flujo completo.");
                    _modelLoadManager?.UnloadCurrentModel();
                }
                else
                {
                    Debug.Log("[NavManager] ℹ️ Sin sesión guardada completa → flujo completo.");
                }

                // ── Flujo completo (solo llega aquí si NO hay sesión válida guardada) ──
                Debug.Log("[NavManager] 📡 Iniciando AR...");
                await InitializeAR();
                Debug.Log("[NavManager] ✅ AR lista.");

                // ✅ FIX #1: _autoLoadModel solo se evalúa en el flujo completo.
                if (_autoLoadModel && _modelLoadManager != null)
                {
                    Debug.Log("[NavManager] 📦 Cargando modelo automáticamente...");
                    await Task.Delay(1000);
                    await _modelLoadManager.LoadModelOnLargestPlaneAsync();
                    Debug.Log("[NavManager] ✅ Modelo cargado.");
                }

                ChangeState(AppMode.PlaneDetection);
                _isInitialized = true;
                PublishMessage("Sistema iniciado", MessageType.Success);
                Debug.Log("[NavManager] ✅ SISTEMA LISTO — FIN");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavManager] ❌ Error en Initialize: {ex.Message}\n{ex.StackTrace}");
                PublishMessage("Error inicializando sistema", MessageType.Error);
                return false;
            }
        }

        /// <summary>
        /// Flujo de restauración desde sesión guardada.
        /// Orden garantizado:
        ///   1) RestoreModelTransform  → modelo posicionado
        ///   2) ConfirmModelPositioned → StartPoints conocen su posición mundo real
        ///   3) LoadNavMeshFromFile    → NavMesh activo
        ///   4) NotifyNavMeshReady     → StartPoints pueden teleportar al agente
        ///   5) LoadWaypoints          → balizas recreadas
        /// </summary>
        private async Task<bool> InitializeFromSavedSession()
        {
            try
            {
                Debug.Log("[NavManager] 📂 [1/4] Llamando LoadSession...");
                bool sessionLoaded = await _persistenceManager.LoadSession();
                Debug.Log($"[NavManager] 📂 LoadSession resultado: {sessionLoaded}");

                if (!sessionLoaded)
                {
                    Debug.LogWarning("[NavManager] ⚠️ LoadSession falló.");
                    return false;
                }

                // ✅ FIX #2 — Paso 2: Confirmar posición del modelo a todos los StartPoints.
                // El modelo ya fue instanciado/reposicionado dentro de LoadSession →
                // RestoreModelTransform. Ahora sus hijos (StartPoints) tienen posición
                // mundo correcta. Se les notifica para que desbloqueen el teleport.
                Debug.Log("[NavManager] 📍 [2/4] Confirmando posición del modelo a StartPoints...");
                ConfirmModelPositionedToAllStartPoints();

                Debug.Log("[NavManager] ✅ [3/4] Sesión cargada — marcando coordinador...");
                _navMeshCoordinator?.MarkSetupDone();

                await Task.Delay(300);

                Debug.Log("[NavManager] 🧭 [4/4] Buscando NavigationStartPoint...");
                var startPoint = NavigationStartPointManager.GetStartPointForLevel(0);
                if (startPoint != null)
                {
                    Debug.Log($"[NavManager] ✅ StartPoint encontrado: {startPoint.gameObject.name}");
                    startPoint.ReteleportAgent();
                }
                else
                {
                    Debug.LogWarning("[NavManager] ⚠️ Sin NavigationStartPoint — agente no reposicionado.");
                }

                ChangeState(AppMode.Navigation);
                Debug.Log("[NavManager] ✅ InitializeFromSavedSession COMPLETADO.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavManager] ❌ InitializeFromSavedSession error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// ✅ FIX #2 — Notifica a todos los NavigationStartPoints que el modelo
        /// ya está en su posición final. Deben llamar a este método antes de
        /// leer transform.position si son hijos del modelo.
        /// </summary>
        private void ConfirmModelPositionedToAllStartPoints()
        {
            var startPoints = NavigationStartPointManager.GetAllStartPoints();
            Debug.Log($"[NavManager] 📍 Confirmando posición a {startPoints.Count} StartPoint(s)...");
            foreach (var sp in startPoints)
                sp?.ConfirmModelPositioned();
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
                throw new Exception("AR Session timeout");

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
                NewMode      = newState
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
            _walkableSurfaceGenerator?.Clear();
        }

        #endregion

        #region Navigation

        public bool NavigateToWaypoint(WaypointData waypoint)
        {
            if (_navigationAgent == null || waypoint == null) return false;
            bool success = _navigationAgent.NavigateToWaypoint(waypoint);
            if (success) Debug.Log($"[NavManager] 🧭 Navegando a: {waypoint.WaypointName}");
            return success;
        }

        public void StopNavigation() => _navigationAgent?.StopNavigation("Usuario canceló");

        #endregion

        #region Waypoints

        public void ToggleWaypointPlacement(bool enabled)
        {
            if (_placementController == null) return;
            _placementController.TogglePlacementMode(enabled);
            if (enabled) ChangeState(AppMode.WaypointPlacement);
        }

        public void ClearAllWaypoints() => _waypointManager?.ClearAllWaypoints();

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

        private void LogEvent(string msg) { if (_logDetailedEvents) Debug.Log($"[NavManager] {msg}"); }
        private void Log(string msg) => Debug.Log($"[NavManager] {msg}");

        private void PublishMessage(string msg, MessageType type) =>
            EventBus.Instance?.Publish(new ShowMessageEvent
            { Message = msg, Type = type, Duration = type == MessageType.Error ? 5f : 3f });

        #endregion

        #region Debug

        [ContextMenu("ℹ️ System Info")]
        private void DebugInfo()
        {
            Debug.Log("══════════════════════════════");
            Debug.Log("NAVIGATION SYSTEM INFO");
            Debug.Log("══════════════════════════════");
            Debug.Log($"Estado:      {_currentState}");
            Debug.Log($"Inicializado:{_isInitialized}");
            Debug.Log($"AR Ready:    {_arSessionManager?.IsSessionReady ?? false}");
            Debug.Log($"Modelo:      {_modelLoadManager?.CurrentModelName ?? "None"}");
            Debug.Log($"Waypoints:   {_waypointManager?.WaypointCount ?? 0}");
            Debug.Log($"Navegando:   {_navigationAgent?.IsNavigating ?? false}");
            Debug.Log("══════════════════════════════");
        }

        [ContextMenu("📦 Load Model")]
        private void DebugLoadModel() => _ = LoadModelOnLargestPlane();

        [ContextMenu("🔄 Reset")]
        private void DebugReset() => ResetSystem();

        #endregion
    }
}