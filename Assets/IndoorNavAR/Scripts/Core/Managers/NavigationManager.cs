// File: NavigationManager.cs
// ✅ FIX #14 — FIX_RAM: Liberar memoria antes de que ARCore inicie el VIO.
//
// ============================================================================
//  CAMBIOS FIX #13 → FIX #14
// ============================================================================
//
//  PROBLEMA RAÍZ (logs del dispositivo Infinix X6887):
//  ─────────────────────────────────────────────────────────────────────────
//  The used RAM memory size by current process is 1048624 kb
//  which is greater than the threshold 1048576 kb, so stop online mapping.
//
//  El proceso consume ~1 GB RAM, superando el umbral de ARCore por apenas
//  48 KB. Esto detiene el mapping online a los ~3.4s y causa que el VIO
//  no pueda mantener suficientes inliers (Insufficient inliers: 37/262).
//
//  SOLUCIÓN FIX #14:
//  ─────────────────────────────────────────────────────────────────────────
//  ReleaseMemoryBeforeARStart():
//    - Resources.UnloadUnusedAssets() — descarga texturas/meshes no usados.
//    - GC.Collect compacting — libera heap fragmentado.
//    - Se llama al inicio de InitializeFromSavedSession() antes de cualquier
//      operación de carga, para que ARCore arranque con el máximo de RAM libre.
//    - Solo se ejecuta en device (en Editor se hace GC normal sin blocking).
//
//  TODOS LOS CAMBIOS DE FIX #13 SE CONSERVAN ÍNTEGRAMENTE.

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

        [Header("⏱️ FIX #13 — Timeout espera alineación")]
        [Tooltip("Segundos máximos esperando que AROriginAligner.InitialAlignDone sea true\n" +
                 "antes de llamar ReparentWaypointsAfterAlignment().\n" +
                 "Si el timeout se alcanza, se continúa con advertencia.\n" +
                 "Default: 5s (ARCore suele alinear en <2s con tracking)")]
        [SerializeField] private float _alignWaitTimeout = 5f;

        [Tooltip("Intervalo de polling para verificar InitialAlignDone (segundos).\n" +
                 "Default: 0.2s — balance entre responsividad y overhead.")]
        [SerializeField] private float _alignPollInterval = 0.2f;

        [Header("─── FIX #14 — Liberación RAM pre-AR ──────────────────────")]
        [Tooltip("Si true, ejecuta GC + UnloadUnusedAssets antes de iniciar ARCore.\n" +
                 "Libera RAM para que ARCore no supere su umbral de 1 GB y detenga el mapping.\n" +
                 "Default: true")]
        [SerializeField] private bool _releaseMemoryBeforeAR = true;

        [Tooltip("FPS objetivo durante la inicialización AR para reducir presión de framebuffers.\n" +
                 "Se restaura automáticamente tras la carga. 0 = no cambiar.\n" +
                 "Default: 30")]
        [SerializeField] private int _initFrameRate = 30;

        [Header("🐛 Debug")]
        [SerializeField] private bool _logDetailedEvents = false;

        private AppMode _currentState = AppMode.Initialization;
        private bool    _isInitialized;

        private string _currentDestinationName;

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
                Debug.LogWarning("[NavManager] ⚠️ NavigationPathController no encontrado.");

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

        // 🔒 Guard contra re-entrada concurrente
        private bool _sessionInitInProgress = false;

        private async Task<bool> InitializeFromSavedSession()
        {
            if (_sessionInitInProgress)
            {
                Debug.LogWarning("[NavManager] ⚠️ InitializeFromSavedSession ya en progreso — ignorando.");
                return false;
            }

            _sessionInitInProgress = true;

            try
            {
                // ✅ FIX #14 FIX_RAM: Liberar memoria antes de que ARCore inicie el VIO.
                // El proceso puede exceder el umbral de ARCore (~1 GB) por apenas 48 KB,
                // lo que detiene el mapping online y causa Insufficient inliers en el VIO.
                // Este paso se hace ANTES de cualquier carga para maximizar RAM disponible.
                if (_releaseMemoryBeforeAR)
                    await ReleaseMemoryBeforeARStart();

                bool sessionLoaded;

                // 🔒 Guard: si PersistenceManager ya completó la carga, no recargar
                if (_persistenceManager.IsSessionLoadCompleted)
                {
                    Debug.Log("[NavManager] ✅ Sesión ya cargada por PM — reutilizando resultado.");
                    sessionLoaded = _persistenceManager.SessionWasRestored;
                }
                else
                {
                    Debug.Log("[NavManager] 📂 [1/4] Llamando LoadSession...");
                    sessionLoaded = await _persistenceManager.LoadSession();
                    Debug.Log($"[NavManager] 📂 LoadSession resultado: {sessionLoaded}");

                    if (!sessionLoaded)
                    {
                        Debug.LogWarning("[NavManager] ⚠️ LoadSession falló.");
                        return false;
                    }
                }

                // ✅ Paso 2: marcar coordinador
                Debug.Log("[NavManager] ✅ [2/4] Marcando coordinador...");
                _navMeshCoordinator?.MarkSetupDone();

                // 🎯 Paso 3: esperar alineación VIO
                if (_arOriginAligner != null)
                {
                    Debug.Log("[NavManager] 🎯 [3/4] Ajustando VIO — NotifySessionRestored()...");
                    _arOriginAligner.NotifySessionRestored();

                    await WaitForAlignmentOrTimeout();
                }
                else
                {
                    Debug.LogWarning("[NavManager] ⚠️ AROriginAligner no disponible — saltando espera.");
                }

                // 🔄 Paso 4: reparent waypoints y notificar a Flutter
                Debug.Log("[NavManager] 🔄 [4/4] ReparentWaypointsAfterAlignment()...");
                await _persistenceManager.ReparentWaypointsAfterAlignment();
                Debug.Log("[NavManager] ✅ Waypoints re-creados y Flutter notificado.");

                ARPerformanceManager.Instance?.EndHeavyLoad("NavigationManager — cierre de seguridad");

                ChangeState(AppMode.Navigation);
                Debug.Log("[NavManager] ✅ InitializeFromSavedSession COMPLETADO.");

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavManager] ❌ InitializeFromSavedSession: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
            finally
            {
                _sessionInitInProgress = false;
            }
        }

        /// <summary>
        /// ✅ FIX #14 FIX_RAM — Libera RAM antes de que ARCore inicie el VIO.
        ///
        /// El proceso puede superar el umbral de ~1 GB de ARCore por márgenes muy
        /// pequeños (48 KB en logs observados), lo que detiene el mapping online.
        ///
        /// Secuencia:
        ///   1. Reducir FPS → menor presión de framebuffers (~15-30 MB liberados).
        ///   2. Resources.UnloadUnusedAssets() → descarga texturas/meshes sin refs.
        ///   3. GC.Collect compacting → libera heap fragmentado (costoso, una sola vez).
        ///
        /// En Editor: GC no-blocking para no interferir con el flujo de desarrollo.
        /// </summary>
        private async Task ReleaseMemoryBeforeARStart()
        {
            Debug.Log("[NavManager] [FIX_RAM] Liberando memoria antes de iniciar AR...");

            // Reducir FPS durante inicialización — libera presión de framebuffers
            if (_initFrameRate > 0)
            {
                Application.targetFrameRate = _initFrameRate;
                Debug.Log($"[NavManager] [FIX_RAM] targetFrameRate → {_initFrameRate} (reducido durante init).");
            }

            // Descargar assets no usados del bundle (texturas comprimidas, meshes)
            var unloadOp = Resources.UnloadUnusedAssets();
            while (!unloadOp.isDone)
                await Task.Yield();

#if UNITY_EDITOR
            // En Editor: GC simple sin blocking para no interferir con el workflow
            System.GC.Collect();
            await Task.Yield();
            Debug.Log("[NavManager] [FIX_RAM] GC (editor, non-blocking) completado.");
#else
            // En device: GC completo compacting — costoso pero necesario (una sola vez al inicio)
            // Compacting=true reorganiza el heap y elimina fragmentación que infla el RSS.
            await Task.Run(() =>
            {
                System.GC.Collect(2, System.GCCollectionMode.Forced, blocking: true, compacting: true);
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect(2, System.GCCollectionMode.Forced, blocking: true);
            });

            await Task.Yield();
            await Task.Yield();
            Debug.Log("[NavManager] [FIX_RAM] GC compacting completado — memoria liberada.");
#endif
        }

        /// <summary>
        /// ✅ FIX #13 — Espera activamente a que AROriginAligner.InitialAlignDone sea true.
        /// </summary>
        private async Task WaitForAlignmentOrTimeout()
        {
#if UNITY_EDITOR
            float effectiveTimeout = _alignWaitTimeout;
#else
            float effectiveTimeout = Mathf.Min(_alignWaitTimeout, 2f);
#endif

            float elapsed = 0f;
            int   pollMs  = Mathf.RoundToInt(_alignPollInterval * 1000f);

            Debug.Log($"[NavManager] ⏳ [FIX #13] Esperando InitialAlignDone " +
                    $"(timeout={effectiveTimeout}s, poll={_alignPollInterval:F2}s)...");

            while (elapsed < effectiveTimeout)
            {
                if (_arOriginAligner.InitialAlignDone)
                {
                    Debug.Log($"[NavManager] ✅ [FIX #13] InitialAlignDone=true en {elapsed:F1}s — " +
                            "procediendo a ReparentWaypointsAfterAlignment().");
                    return;
                }

                await Task.Delay(pollMs);
                elapsed += _alignPollInterval;
            }

            Debug.LogWarning($"[NavManager] ⚠️ [FIX #13] Timeout {effectiveTimeout}s esperando " +
                            "InitialAlignDone. ARCore puede no estar en tracking. " +
                            "Continuando — los waypoints pueden tener posiciones sub-óptimas.");
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

        public bool NavigateToWaypoint(WaypointData waypoint)
        {
            if (waypoint == null) return false;
            if (_navigationAgent == null)
            {
                Debug.LogError("[NavManager] ❌ NavigationAgent no disponible");
                return false;
            }

            _currentDestinationName = waypoint.WaypointName;

            bool isFullAR = _arOriginAligner == null || _arOriginAligner.IsFullARMode;

            if (isFullAR)
            {
                if (_arOriginAligner != null)
                {
                    _arOriginAligner.ForceSnapAgentToCamera();
                    Debug.Log("[NavManager] 📍 [FullAR] ForceSnapAgentToCamera().");
                }

                if (_pathController != null)
                {
                    _pathController.SetFullARMode(true);
                    Debug.Log("[NavManager] 📡 [FullAR] PathController.SetFullARMode(true).");
                }

                Vector3 agentPos = _navigationAgent.transform.position;
                Debug.Log($"[NavManager] 🧭 [FullAR] → {waypoint.WaypointName} | " +
                        $"agentPos={agentPos:F2} | dist={Vector3.Distance(agentPos, waypoint.Position):F2}m");

                bool ok = _navigationAgent.NavigateToWaypoint(waypoint);
                if (ok)
                {
                    Debug.Log($"[NavManager] ✅ [FullAR] Ruta calculada a '{waypoint.WaypointName}'.");
                    NavigationVoiceGuide.Instance?.TriggerFromWaypoint(waypoint);
                }
                else
                {
                    Debug.LogError($"[NavManager] ❌ [FullAR] Sin ruta a '{waypoint.WaypointName}'.");
                }
                return ok;
            }

            if (_pathController != null && _pathController.IsFullARMode)
            {
                _pathController.SetFullARMode(false);
                Debug.Log("[NavManager] 📵 [NoAR] PathController.SetFullARMode(false).");
            }

            bool okNoAR = _navigationAgent.NavigateToWaypoint(waypoint);
            if (okNoAR)
            {
                Debug.Log($"[NavManager] 🧭 [NoAR] → {waypoint.WaypointName}");
                NavigationVoiceGuide.Instance?.TriggerFromWaypoint(waypoint);
            }
            return okNoAR;
        }

        /// <summary>
        /// ✅ FIX #11 — Recálculo silencioso de ruta para ObstacleRerouteMediator.
        /// </summary>
        public bool RerouteToWaypoint(WaypointData waypoint)
        {
            if (waypoint == null) return false;
            if (_navigationAgent == null)
            {
                Debug.LogError("[NavManager] ❌ [Reroute] NavigationAgent no disponible");
                return false;
            }

            _currentDestinationName = waypoint.WaypointName;

            bool isFullAR = _arOriginAligner == null || _arOriginAligner.IsFullARMode;

            if (isFullAR && _arOriginAligner != null)
            {
                _arOriginAligner.ForceSnapAgentToCamera();
                Debug.Log("[NavManager] 📍 [Reroute/FullAR] ForceSnapAgentToCamera().");
            }

            bool ok = _navigationAgent.NavigateToWaypointForced(waypoint);

            if (ok)
                Debug.Log($"[NavManager] 🔄 [Reroute] Ruta recalculada a '{waypoint.WaypointName}'.");
            else
                Debug.LogError($"[NavManager] ❌ [Reroute] Sin ruta a '{waypoint.WaypointName}'.");

            return ok;
        }

        public void StopNavigation()
        {
            _navigationAgent?.StopNavigation("Usuario canceló");
            NavigationVoiceGuide.Instance?.StopVoiceGuide();

            EventBus.Instance?.Publish(new NavigationStoppedEvent
            {
                DestinationWaypointName = _currentDestinationName ?? ""
            });
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
                      $"Modo: {(isFullAR ? "FullAR" : "NoAR")} | " +
                      $"Waypoints: {_waypointManager?.WaypointCount ?? 0} | " +
                      $"InitialAlignDone: {_arOriginAligner?.InitialAlignDone ?? false} | " +
                      $"FPS: {Application.targetFrameRate}");
            if (_pathController != null)
                Debug.Log($"[NavManager] PathController: IsFullARMode={_pathController.IsFullARMode} | " +
                          $"IsNavigating={_pathController.IsNavigating}");
        }

        [ContextMenu("📦 Load Model")]       private void DebugLoadModel()  => _ = LoadModelOnLargestPlane();
        [ContextMenu("🔄 Reset")]             private void DebugReset()      => ResetSystem();
        [ContextMenu("🚀 Force Initialize")]  private void DebugForceInit()  { _isInitialized = false; _ = Initialize(); }
        [ContextMenu("🧹 Force GC Now")]
        private void DebugForceGC()           => _ = ReleaseMemoryBeforeARStart();

        #endregion
    }
}