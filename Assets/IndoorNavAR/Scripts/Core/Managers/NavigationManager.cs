// File: NavigationManager.cs
// ✅ FIX #7 — Integración NavigationVoiceGuide.TriggerFromWaypoint()
//             en NavigateToWaypoint() para activar el guía de voz.
//
//  TODOS LOS FIXES ANTERIORES SE MANTIENEN:
//  ✅ FIX #1 — Exclusión mutua estricta con sesión guardada.
//  ✅ FIX #2 — ConfirmModelPositioned() antes de cargar NavMesh.
//  ✅ FIX #3 — ForceRealign() eliminado de InitializeFromSavedSession().
//  ✅ FIX #4 — Initialize() espera al primer frame antes de arrancar.
//  ✅ FIX #5 — ConfirmModelPositionedToAllStartPoints() y ReteleportAgent()
//              eliminados de InitializeFromSavedSession().
//  ✅ FIX #6 — AROriginAligner.NotifySessionRestored() al final de
//              InitializeFromSavedSession().
//  ✅ FIX #7 — NavigationVoiceGuide.Instance?.TriggerFromWaypoint(waypoint)
//              llamado en NavigateToWaypoint() para que el VoiceGuide
//              arranque correctamente. Sin este llamado el VoiceGuide
//              nunca recibía el nombre del waypoint destino.

using System;
using System.Threading.Tasks;
using UnityEngine;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Core.Controllers;
using IndoorNavAR.AR;
using IndoorNavAR.Navigation;
using IndoorNavAR.Navigation.Voice; // ✅ FIX #7: namespace del VoiceGuide

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
        [SerializeField] private AROriginAligner       _arOriginAligner;

        [Header("🧭 Sistema de Navegación")]
        [SerializeField] private MultiLevelNavMeshGenerator _walkableSurfaceGenerator;
        [SerializeField] private NavigationAgent             _navigationAgent;
        [SerializeField] private NavMeshAgentCoordinator    _navMeshCoordinator;

        [Header("⚙️ Configuración")]
        [SerializeField] private bool _autoInitialize = true;
        [SerializeField] private bool _autoLoadModel  = true;

        [Header("🐛 Debug")]
        [SerializeField] private bool _logDetailedEvents = false;

        private AppMode _currentState = AppMode.Initialization;
        private bool    _isInitialized;

        #region Properties

        public bool       IsInitialized    => _isInitialized;
        public AppMode    CurrentState      => _currentState;
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
                StartCoroutine(InitializeAfterFirstFrame());
        }

        private System.Collections.IEnumerator InitializeAfterFirstFrame()
        {
            yield return null;
            _ = Initialize();
        }

        #endregion

        #region Component Discovery

        private void FindComponents()
        {
            Log("🔍 Buscando componentes del sistema...");

            _arSessionManager         ??= FindFirstObjectByType<ARSessionManager>();
            _waypointManager          ??= FindFirstObjectByType<WaypointManager>();
            _modelLoadManager         ??= FindFirstObjectByType<ModelLoadManager>();
            _placementController      ??= FindFirstObjectByType<PlacementController>();
            _persistenceManager       ??= FindFirstObjectByType<PersistenceManager>();
            _walkableSurfaceGenerator ??= FindFirstObjectByType<MultiLevelNavMeshGenerator>();
            _navigationAgent          ??= FindFirstObjectByType<NavigationAgent>();
            _navMeshCoordinator       ??= FindFirstObjectByType<NavMeshAgentCoordinator>();
            _arOriginAligner          ??= FindFirstObjectByType<AROriginAligner>();

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

            if (_arOriginAligner != null)
            {
                _arOriginAligner.AlignToStartPoint();
                Debug.Log("[NavManager] 🎯 Solicitando alineación XR Origin al StartPoint...");
            }
            else
            {
                StartCoroutine(TeleportAgentNextFrame());
            }
        }

        private System.Collections.IEnumerator TeleportAgentNextFrame()
        {
            yield return null;
            yield return null;
            var sp = NavigationStartPointManager.GetStartPointForLevel(0);
            if (sp != null)
            {
                sp.ConfirmModelPositioned();
                sp.ReteleportAgent();
                Debug.Log("[NavManager] 📍 Agente teleportado al StartPoint (fallback).");
            }
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

                bool hasSavedSession = _persistenceManager != null && _persistenceManager.HasSavedSession();
                bool hasSavedNavMesh = _persistenceManager != null && _persistenceManager.HasSavedNavMesh;

                Debug.Log($"[NavManager] 🔍 hasSavedSession={hasSavedSession} | hasSavedNavMesh={hasSavedNavMesh}");

                if (hasSavedSession && hasSavedNavMesh)
                {
                    Debug.Log("[NavManager] 💾 Sesión guardada detectada → carga rápida.");
                    bool ok = await InitializeFromSavedSession();

                    if (ok)
                    {
                        _isInitialized = true;
                        PublishMessage("Sesión restaurada", MessageType.Success);
                        Debug.Log("[NavManager] ✅ RESTAURADO DESDE SESIÓN GUARDADA — FIN");
                        return true;
                    }

                    Debug.LogWarning("[NavManager] ⚠️ Falló carga rápida → flujo completo.");
                    _modelLoadManager?.UnloadCurrentModel();
                }
                else
                {
                    Debug.Log("[NavManager] ℹ️ Sin sesión guardada completa → flujo completo.");
                }

                Debug.Log("[NavManager] 📡 Iniciando AR...");
                await InitializeAR();
                Debug.Log("[NavManager] ✅ AR lista.");

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

        private async Task<bool> InitializeFromSavedSession()
        {
            try
            {
                Debug.Log("[NavManager] 📂 [1/3] Llamando LoadSession...");
                bool sessionLoaded = await _persistenceManager.LoadSession();
                Debug.Log($"[NavManager] 📂 LoadSession resultado: {sessionLoaded}");

                if (!sessionLoaded)
                {
                    Debug.LogWarning("[NavManager] ⚠️ LoadSession falló.");
                    return false;
                }

                Debug.Log("[NavManager] ✅ [2/3] Marcando coordinador...");
                _navMeshCoordinator?.MarkSetupDone();

                if (_arOriginAligner != null)
                {
                    Debug.Log("[NavManager] 🎯 [3/3] Notificando AROriginAligner de sesión restaurada...");
                    _arOriginAligner.NotifySessionRestored();
                }
                else
                {
                    Debug.LogWarning("[NavManager] ⚠️ AROriginAligner no disponible — " +
                                     "alineación de cámara omitida.");
                }

                ChangeState(AppMode.Navigation);
                Debug.Log("[NavManager] ✅ InitializeFromSavedSession COMPLETADO.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavManager] ❌ InitializeFromSavedSession: {ex.Message}\n{ex.StackTrace}");
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
                throw new Exception("AR Session timeout");

            Debug.Log("[NavManager] ✅ AR Session lista");
        }

        #endregion

        #region State Management

        public void ChangeState(AppMode newState)
        {
            var prevState = _currentState;
            _currentState = newState;
            EventBus.Instance?.Publish(new AppModeChangedEvent { PreviousMode = prevState, NewMode = newState });
            LogEvent($"🔄 Estado: {prevState} → {newState}");
        }

        #endregion

        #region Model Management

        public async Task<bool> LoadModelOnLargestPlane()
        {
            if (_modelLoadManager == null) { Debug.LogWarning("[NavManager] ⚠️ ModelLoadManager no disponible"); return false; }
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

        /// <summary>
        /// ✅ FIX #7: Llama NavigationVoiceGuide.TriggerFromWaypoint() después de
        /// iniciar la navegación para que el guía de voz arranque con el nombre
        /// correcto del waypoint destino y espere la ruta antes de hablar.
        /// </summary>
        public bool NavigateToWaypoint(WaypointData waypoint)
        {
            if (_navigationAgent == null || waypoint == null) return false;

            bool success = _navigationAgent.NavigateToWaypoint(waypoint);

            if (success)
            {
                Debug.Log($"[NavManager] 🧭 Navegando a: {waypoint.WaypointName}");

                // ✅ FIX #7: Activar el guía de voz con el waypoint real.
                // NavigationVoiceGuide.TriggerFromWaypoint() espera la ruta antes
                // de generar instrucciones — no habla hasta tener el path listo.
                NavigationVoiceGuide.Instance?.TriggerFromWaypoint(waypoint);
            }

            return success;
        }

        public void StopNavigation()
        {
            _navigationAgent?.StopNavigation("Usuario canceló");
            NavigationVoiceGuide.Instance?.StopVoiceGuide();
        }

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
            Debug.Log($"Estado: {_currentState} | Init: {_isInitialized} | " +
                      $"AR: {_arSessionManager?.IsSessionReady ?? false} | " +
                      $"Modelo: {_modelLoadManager?.CurrentModelName ?? "None"} | " +
                      $"Waypoints: {_waypointManager?.WaypointCount ?? 0}");
        }

        [ContextMenu("📦 Load Model")]       private void DebugLoadModel()  => _ = LoadModelOnLargestPlane();
        [ContextMenu("🔄 Reset")]             private void DebugReset()      => ResetSystem();
        [ContextMenu("🚀 Force Initialize")]  private void DebugForceInit()  { _isInitialized = false; _ = Initialize(); }

        #endregion
    }
}