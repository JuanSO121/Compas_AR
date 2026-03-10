// File: NavigationManager.cs
// ✅ FIX #10 — FullAR: ForceSnapAgentToCamera() antes de calcular ruta.
//
// ============================================================================
//  PROBLEMA CORREGIDO (FIX #9 → FIX #10)
// ============================================================================
//
//  SÍNTOMA (confirmado por log):
//    [PathController] Ruta: 1 raw → 2 optimizados, 0,0m  ← ruta de CERO metros
//    [ARGuideController] initialDist=3,1m                 ← usuario lejos del destino
//
//  CAUSA RAÍZ — Race condition entre NavigationStartPoint Level 1 y AROriginAligner:
//
//    1. NavigationStartPoint Level 1 tiene autoTeleportOnStart=true.
//       En Start(), lanza TeleportAgentWhenReady().
//       Esto mueve el agente a Y≈3.36 (piso 1, donde está el destino "Habitación 2° Piso").
//
//    2. AROriginAligner.SyncAgentToCameraFullAR() debería corregir esto
//       warpando el agente de vuelta a la cámara XR (piso 0).
//       Pero hay una ventana de tiempo (hasta 2s por _stableFramesRequired y
//       _syncFailThreshold) en que el agente puede seguir en piso 1.
//
//    3. Cuando el usuario dice "navegar a Habitación 2° Piso",
//       NavigationManager.NavigateToWaypoint() confía en que AROriginAligner
//       ya posicionó el agente correctamente (FIX #9).
//       Pero si la race condition ganó, el agente está en Y≈3.36 (= posición del
//       destino) → PathController computa ruta de 0m → VoiceGuide no genera
//       instrucciones → ARGuideController cree que ya se llegó.
//
//  FIX #10:
//    En NavigateToWaypoint() en modo FullAR, ANTES de llamar NavigateToWaypoint()
//    en el agente, llamar explícitamente AROriginAligner.ForceSnapAgentToCamera().
//    Este método hace un warp inmediato al NavMesh más cercano a la cámara XR,
//    sin depender del ciclo de Update de AROriginAligner.
//
//    Esto garantiza que el agente esté en la posición del usuario (cámara XR)
//    en el momento exacto en que se calcula la ruta, eliminando la race condition.
//
//  FIX COMPLEMENTARIO (Inspector, no código):
//    NavigationStartPoint Level 1 → desactivar _autoTeleportOnStart en el Inspector.
//    Solo el StartPoint del nivel de entrada (Level 0) debe teleportar al agente.
//    Los StartPoints de niveles superiores existen para definir FloorHeight,
//    no para teleportar. Ver nota en NavigationStartPoint.cs sobre _autoTeleportOnStart.
//
// ============================================================================
//  TODOS LOS FIXES ANTERIORES SE MANTIENEN:
//  ✅ FIX #1 — Exclusión mutua estricta con sesión guardada.
//  ✅ FIX #2 — ConfirmModelPositioned() antes de cargar NavMesh.
//  ✅ FIX #3 — ForceRealign() eliminado de InitializeFromSavedSession().
//  ✅ FIX #4 — Initialize() espera al primer frame antes de arrancar.
//  ✅ FIX #5 — ConfirmModelPositionedToAllStartPoints() eliminado.
//  ✅ FIX #6 — AROriginAligner.NotifySessionRestored() al final.
//  ✅ FIX #7 — VoiceGuide.TriggerFromWaypoint() conservado.
//  ✅ FIX #8 — Navegación pasa por PathController (conservado, sin TeleportTo).
//  ✅ FIX #9 — SetFullARMode(true) antes de navegar.
//  ✅ FIX #10 — ForceSnapAgentToCamera() antes de calcular ruta en FullAR.

using System;
using System.Threading.Tasks;
using UnityEngine;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Core.Controllers;
using IndoorNavAR.AR;
using IndoorNavAR.Navigation;
using IndoorNavAR.Navigation.Voice;

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

        // Cache del PathController para SetFullARMode
        private NavigationPathController _pathController;

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

            // Cache del PathController para SetFullARMode
            if (_navigationAgent != null)
                _pathController = _navigationAgent.GetComponent<NavigationPathController>();

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
            if (_pathController == null)
                Debug.LogWarning("[NavManager] ⚠️ NavigationPathController no encontrado " +
                                 "— SetFullARMode no se aplicará.");

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
        /// ✅ FIX #10 — En FullAR:
        ///   0. (NUEVO) ForceSnapAgentToCamera() para warpear el agente a la posición
        ///      de la cámara XR en este frame exacto. Elimina la race condition donde
        ///      NavigationStartPoint Level 1 (autoTeleportOnStart=true) deja al agente
        ///      en Y≈3.36 (piso 1) y AROriginAligner aún no lo corrigió, causando
        ///      que PathController compute una ruta de 0m (agente ya está en el destino).
        ///   1. Activa SetFullARMode(true) en PathController ANTES de navegar.
        ///      Esto garantiza que FollowPath() no mueva el transform.
        ///   2. NO llama TeleportTo(userPos) — AROriginAligner (+ ForceSnap) ya
        ///      posicionó el agente en la posición del usuario.
        ///   3. Llama NavigateToWaypoint() para calcular el path.
        ///      PathController genera CurrentPath válido desde agentPos (= userPos).
        ///   4. VoiceGuide evalúa la ruta y genera instrucciones TTS.
        ///   5. El agente no se mueve — AROriginAligner sigue sincronizando.
        /// </summary>
        public bool NavigateToWaypoint(WaypointData waypoint)
        {
            if (waypoint == null) return false;
            if (_navigationAgent == null)
            {
                Debug.LogError("[NavManager] ❌ NavigationAgent no disponible");
                return false;
            }

            bool isFullAR = _arOriginAligner == null || _arOriginAligner.IsFullARMode;

            if (isFullAR)
            {
                // ✅ FIX #10 — Paso 0 (NUEVO): Warp inmediato del agente a la cámara XR.
                // CRÍTICO: debe hacerse ANTES de SetFullARMode y NavigateToWaypoint.
                // Elimina la race condition donde el agente está en piso incorrecto
                // porque NavigationStartPoint Level 1 lo teleportó ahí.
                if (_arOriginAligner != null)
                {
                    _arOriginAligner.ForceSnapAgentToCamera();
                    Debug.Log("[NavManager] 📍 [FullAR] ForceSnapAgentToCamera() — " +
                              "agente sincronizado con cámara XR antes de calcular ruta.");
                }
                else
                {
                    Debug.LogWarning("[NavManager] ⚠️ [FullAR] AROriginAligner no disponible. " +
                                     "El agente puede estar en posición incorrecta. " +
                                     "Verificar que AROriginAligner está en la escena.");
                }

                // ✅ FIX #9 — Paso 1: Activar modo FullAR en PathController.
                // CRÍTICO: debe hacerse ANTES de NavigateToWaypoint() para que
                // FollowPath() no mueva el transform cuando se active _isNavigating.
                if (_pathController != null)
                {
                    _pathController.SetFullARMode(true);
                    Debug.Log("[NavManager] 📡 [FullAR] PathController.SetFullARMode(true) — " +
                              "el agente no se moverá.");
                }
                else
                {
                    Debug.LogWarning("[NavManager] ⚠️ [FullAR] PathController no encontrado. " +
                                     "El agente PODRÍA moverse. Verificar que VirtualAssistant " +
                                     "tiene NavigationPathController.");
                }

                // ✅ FIX #9 — Paso 2: Navegar desde la posición actual del agente.
                // ForceSnapAgentToCamera() garantizó que el agente está en la posición
                // del usuario (cámara XR). La ruta se calculará correctamente.
                Vector3 agentPos = _navigationAgent.transform.position;
                Debug.Log($"[NavManager] 🧭 [FullAR] → {waypoint.WaypointName} | " +
                          $"agentPos={agentPos:F2} | dest={waypoint.Position:F2} | " +
                          $"dist={Vector3.Distance(agentPos, waypoint.Position):F2}m");

                bool ok = _navigationAgent.NavigateToWaypoint(waypoint);
                if (ok)
                {
                    Debug.Log($"[NavManager] ✅ [FullAR] Ruta calculada a '{waypoint.WaypointName}'. " +
                              "Agente estático — VoiceGuide generará instrucciones.");
                    // ✅ FIX #7 conservado
                    NavigationVoiceGuide.Instance?.TriggerFromWaypoint(waypoint);
                }
                else
                {
                    Debug.LogError($"[NavManager] ❌ [FullAR] Sin ruta a '{waypoint.WaypointName}' " +
                                   $"desde {agentPos:F2}. " +
                                   "¿El NavMesh cubre la posición del usuario? " +
                                   "ForceSnapAgentToCamera ya intentó posicionar el agente. " +
                                   "Verificar: (1) NavMesh bakeado cubre piso 0, " +
                                   "(2) XR tracking estable, " +
                                   "(3) AROriginAligner no está en deadlock de piso.");
                }
                return ok;
            }

            // ── Modo NoAR: el agente navega físicamente (sin cambios) ──────────
            // En NoAR, PathController.IsFullARMode es false → FollowPath() activo.
            if (_pathController != null && _pathController.IsFullARMode)
            {
                // Seguridad: si por algún motivo quedó en FullAR, revertir.
                _pathController.SetFullARMode(false);
                Debug.Log("[NavManager] 📵 [NoAR] PathController.SetFullARMode(false) — " +
                          "movimiento del agente habilitado.");
            }

            bool okNoAR = _navigationAgent.NavigateToWaypoint(waypoint);
            if (okNoAR)
            {
                Debug.Log($"[NavManager] 🧭 [NoAR] → {waypoint.WaypointName}");
                NavigationVoiceGuide.Instance?.TriggerFromWaypoint(waypoint);
            }
            return okNoAR;
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
            bool isFullAR = _arOriginAligner == null || _arOriginAligner.IsFullARMode;
            Debug.Log($"[NavManager] Estado: {_currentState} | Init: {_isInitialized} | " +
                      $"AR: {_arSessionManager?.IsSessionReady ?? false} | " +
                      $"Modo: {(isFullAR ? "FullAR" : "NoAR")} | " +
                      $"Modelo: {_modelLoadManager?.CurrentModelName ?? "None"} | " +
                      $"Waypoints: {_waypointManager?.WaypointCount ?? 0}");
            if (_pathController != null)
                Debug.Log($"[NavManager] PathController: IsFullARMode={_pathController.IsFullARMode} | " +
                          $"IsNavigating={_pathController.IsNavigating}");
            if (_navigationAgent != null)
                Debug.Log($"[NavManager] AgentPos: {_navigationAgent.transform.position:F2} | " +
                          $"FullARMode: {_navigationAgent.IsFullARMode}");
        }

        [ContextMenu("📦 Load Model")]       private void DebugLoadModel()  => _ = LoadModelOnLargestPlane();
        [ContextMenu("🔄 Reset")]             private void DebugReset()      => ResetSystem();
        [ContextMenu("🚀 Force Initialize")]  private void DebugForceInit()  { _isInitialized = false; _ = Initialize(); }

        #endregion
    }
}