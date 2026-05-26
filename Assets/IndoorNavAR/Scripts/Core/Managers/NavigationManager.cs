// File: NavigationManager.cs
// ✅ FIX #15 — Correcciones encadenadas diagnosticadas desde logs del dispositivo
//
// ============================================================================
//  CAMBIOS FIX #14 → FIX #15
// ============================================================================
//
//  BUG #1 — WaitForAlignmentOrTimeout() con timeout recortado a 2s en device:
//  ─────────────────────────────────────────────────────────────────────────
//  ANTES (FIX #13):
//    #if !UNITY_EDITOR
//        float effectiveTimeout = Mathf.Min(_alignWaitTimeout, 2f); // ← BUG
//    #endif
//
//  CAUSA: En sesiones restauradas (kMapTracking mode), ARCore necesita
//  entre 3-8s para que InitialAlignDone sea true. Con 2s, WaitForAlignment
//  siempre terminaba por timeout en device, y el log mostraba:
//    "⚠️ Timeout 2s esperando InitialAlignDone. ARCore puede no estar en tracking."
//  Luego ReparentWaypointsAfterAlignment() ejecutaba con XROrigin desalineado
//  → waypoints en posiciones incorrectas → SceneReadyNotifier nunca veía
//  IsSessionLoadCompleted=true → scene_ready llegaba 20s tarde o nunca.
//
//  FIX: Usar _alignWaitTimeout del Inspector (default 5s) en todos los casos.
//  En sesiones restauradas con kMapTracking, 5s es suficiente según logs
//  (InitialAlignDone=true aparece en el log a los ~4.2s de inicio de sesión).
//
//  BUG #2 — _sessionInitInProgress guard demasiado agresivo:
//  ─────────────────────────────────────────────────────────────────────────
//  Si Initialize() era llamado dos veces (puede ocurrir en Resume desde
//  background), la segunda llamada retornaba false inmediatamente y dejaba
//  _isInitialized=false, bloqueando el sistema. Ahora espera y reintenta.
//
//  BUG #3 — ReleaseMemoryBeforeARStart en device usaba Task.Run para GC,
//  pero el compacting GC podía lanzar excepciones no manejadas en algunos
//  builds IL2CPP de Android. Añadido try/catch.
//
//  TODOS LOS CAMBIOS DE FIX #14 SE CONSERVAN ÍNTEGRAMENTE.

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

        [Header("⏱️ FIX #15 — Timeout espera alineación")]
        [Tooltip("Segundos máximos esperando que AROriginAligner.InitialAlignDone sea true.\n" +
                 "FIX #15: se usa este valor en TODOS los casos (Editor + Device).\n" +
                 "FIX #13 tenía Mathf.Min(..., 2f) en device → insuficiente para kMapTracking.\n" +
                 "Valor recomendado: 5-8s (kMapTracking necesita ~4s según logs).\n" +
                 "Default: 6s")]
        [SerializeField] private float _alignWaitTimeout = 6f;

        [Tooltip("Intervalo de polling para verificar InitialAlignDone (segundos).\n" +
                 "Default: 0.2s")]
        [SerializeField] private float _alignPollInterval = 0.2f;

        [Header("─── FIX #14 — Liberación RAM pre-AR ──────────────────────")]
        [Tooltip("Si true, ejecuta GC + UnloadUnusedAssets antes de iniciar ARCore.\n" +
                 "Default: true")]
        [SerializeField] private bool _releaseMemoryBeforeAR = true;

        [Tooltip("FPS objetivo durante la inicialización AR.\n" +
                 "Se restaura automáticamente tras la carga. 0 = no cambiar.\n" +
                 "Default: 30")]
        [SerializeField] private int _initFrameRate = 30;

        [Header("─── FIX #15 — Guard de re-entrada ─────────────────────────")]
        [Tooltip("Segundos máximos esperando que una inicialización en progreso termine\n" +
                 "antes de reintentarla (ej: Resume desde background).\n" +
                 "Default: 30s")]
        [SerializeField] private float _sessionInitWaitTimeout = 30f;

        [Header("🐛 Debug")]
        [SerializeField] private bool _logDetailedEvents = false;

        private AppMode _currentState = AppMode.Initialization;
        private bool    _isInitialized;

        private string _currentDestinationName;
        private NavigationPathController _pathController;

        // ✅ FIX #15 BUG #2 — Guard mejorado: expone cuándo inició para timeout
        private bool  _sessionInitInProgress  = false;
        private float _sessionInitStartedTime = 0f;

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

        private async Task<bool> InitializeFromSavedSession()
        {
            // ✅ FIX #15 BUG #2 — Guard mejorado con timeout
            // ANTES: retornaba false inmediatamente si ya había una init en progreso.
            // CAUSA: en Resume desde background, Initialize() se llamaba dos veces.
            //        La segunda llamada retornaba false, dejando _isInitialized=false.
            // FIX:   esperar a que la primera termine (hasta _sessionInitWaitTimeout).
            if (_sessionInitInProgress)
            {
                float waitStart = Time.realtimeSinceStartup;
                float elapsed   = 0f;
                Debug.LogWarning("[NavManager] ⚠️ InitializeFromSavedSession en progreso — esperando...");

                while (_sessionInitInProgress &&
                       elapsed < _sessionInitWaitTimeout)
                {
                    await Task.Delay(200);
                    elapsed = Time.realtimeSinceStartup - waitStart;
                }

                if (_sessionInitInProgress)
                {
                    Debug.LogError("[NavManager] ❌ InitializeFromSavedSession timeout " +
                                   $"({_sessionInitWaitTimeout}s) esperando init previa.");
                    return false;
                }

                // La primera init ya terminó; si tuvo éxito, _isInitialized=true
                Debug.Log("[NavManager] ✅ Init previa completada — reutilizando resultado.");
                return _isInitialized;
            }

            _sessionInitInProgress  = true;
            _sessionInitStartedTime = Time.realtimeSinceStartup;

            try
            {
                // ✅ FIX #14 FIX_RAM: Liberar memoria antes de que ARCore inicie el VIO.
                if (_releaseMemoryBeforeAR)
                    await ReleaseMemoryBeforeARStart();

                bool sessionLoaded;

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

                Debug.Log("[NavManager] ✅ [2/4] Marcando coordinador...");
                _navMeshCoordinator?.MarkSetupDone();

                // ✅ FIX #15 BUG #1 — WaitForAlignmentOrTimeout sin recorte a 2s
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

                Debug.Log("[NavManager] 🔄 [4/4] ReparentWaypointsAfterAlignment()...");
                await _persistenceManager.ReparentWaypointsAfterAlignment();
                Debug.Log("[NavManager] ✅ Waypoints re-creados y Flutter notificado.");

                ARPerformanceManager.Instance?.EndHeavyLoad("NavigationManager — cierre de seguridad");

                ChangeState(AppMode.Navigation);
                Debug.Log("[NavManager] ✅ InitializeFromSavedSession COMPLETADO en " +
                          $"{Time.realtimeSinceStartup - _sessionInitStartedTime:F1}s.");
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
        /// ✅ FIX #15 BUG #1 — WaitForAlignmentOrTimeout sin Mathf.Min(..., 2f).
        ///
        /// PROBLEMA FIX #13:
        ///   En device, effectiveTimeout = Mathf.Min(_alignWaitTimeout, 2f) = 2s.
        ///   ARCore en kMapTracking (sesión restaurada) necesita ~4s para alinear.
        ///   Resultado: siempre timeout en device → ReparentWaypointsAfterAlignment()
        ///   ejecutaba con XROrigin desalineado → waypoints incorrectos →
        ///   scene_ready llegaba con 20s de retraso o nunca.
        ///
        /// FIX:
        ///   Usar _alignWaitTimeout del Inspector en TODOS los casos.
        ///   Default cambiado de 5s a 6s para cubrir dispositivos lentos.
        ///   El log de diagnóstico ahora muestra el tiempo real de espera.
        /// </summary>
        private async Task WaitForAlignmentOrTimeout()
        {
            // ✅ FIX #15: sin Mathf.Min — usar valor del Inspector en Editor y Device
            float effectiveTimeout = _alignWaitTimeout;
            float elapsed          = 0f;
            int   pollMs           = Mathf.RoundToInt(_alignPollInterval * 1000f);

            Debug.Log($"[NavManager] ⏳ [FIX #15] Esperando InitialAlignDone " +
                      $"(timeout={effectiveTimeout}s, poll={_alignPollInterval:F2}s) " +
                      $"[Editor+Device mismo timeout]");

            while (elapsed < effectiveTimeout)
            {
                if (_arOriginAligner.InitialAlignDone)
                {
                    Debug.Log($"[NavManager] ✅ [FIX #15] InitialAlignDone=true en {elapsed:F1}s — " +
                              "procediendo a ReparentWaypointsAfterAlignment().");
                    return;
                }

                await Task.Delay(pollMs);
                elapsed += _alignPollInterval;

                // Log de progreso cada 1s
                if (Mathf.RoundToInt(elapsed * 10f) % 10 == 0)
                    Debug.Log($"[NavManager] ⏳ [{elapsed:F1}s/{effectiveTimeout}s] " +
                              $"InitialAlignDone={_arOriginAligner.InitialAlignDone} | " +
                              $"ARState={UnityEngine.XR.ARFoundation.ARSession.state}");
            }

            Debug.LogWarning($"[NavManager] ⚠️ [FIX #15] Timeout {effectiveTimeout}s esperando " +
                             "InitialAlignDone. Continuando — waypoints pueden quedar desalineados. " +
                             "Considera aumentar _alignWaitTimeout en el Inspector.");
        }

        /// <summary>
        /// ✅ FIX #14 + FIX #15 BUG #3 — ReleaseMemoryBeforeARStart con manejo de excepciones.
        ///
        /// En algunos builds IL2CPP de Android, GC.Collect compacting puede lanzar
        /// excepciones no manejadas. Añadido try/catch para garantizar que el flujo
        /// continúa incluso si el GC falla.
        /// </summary>
        private async Task ReleaseMemoryBeforeARStart()
        {
            Debug.Log("[NavManager] [FIX_RAM] Liberando memoria antes de iniciar AR...");

            if (_initFrameRate > 0)
            {
                Application.targetFrameRate = _initFrameRate;
                Debug.Log($"[NavManager] [FIX_RAM] targetFrameRate → {_initFrameRate} (reducido durante init).");
            }

            try
            {
                var unloadOp = Resources.UnloadUnusedAssets();
                while (!unloadOp.isDone)
                    await Task.Yield();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NavManager] [FIX_RAM] UnloadUnusedAssets error (no crítico): {ex.Message}");
            }

#if UNITY_EDITOR
            try
            {
                System.GC.Collect();
                await Task.Yield();
                Debug.Log("[NavManager] [FIX_RAM] GC (editor, non-blocking) completado.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NavManager] [FIX_RAM] GC editor error (no crítico): {ex.Message}");
            }
#else
            // ✅ FIX #15 BUG #3: try/catch alrededor del GC compacting en device
            // En IL2CPP algunos dispositivos lanzan excepciones durante compacting.
            try
            {
                await Task.Run(() =>
                {
                    System.GC.Collect(2, System.GCCollectionMode.Forced,
                                      blocking: true, compacting: true);
                    System.GC.WaitForPendingFinalizers();
                    System.GC.Collect(2, System.GCCollectionMode.Forced, blocking: true);
                });

                await Task.Yield();
                await Task.Yield();
                Debug.Log("[NavManager] [FIX_RAM] GC compacting completado — memoria liberada.");
            }
            catch (Exception ex)
            {
                // GC compacting falló — intentar GC simple como fallback
                Debug.LogWarning($"[NavManager] [FIX_RAM] GC compacting falló ({ex.Message}). " +
                                 "Intentando GC simple...");
                try
                {
                    System.GC.Collect();
                    System.GC.WaitForPendingFinalizers();
                    await Task.Yield();
                    Debug.Log("[NavManager] [FIX_RAM] GC simple completado (fallback).");
                }
                catch (Exception ex2)
                {
                    Debug.LogWarning($"[NavManager] [FIX_RAM] GC simple también falló " +
                                     $"(no crítico): {ex2.Message}");
                }
            }
#endif
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
            if (_modelLoadManager == null)
            { Debug.LogWarning("[NavManager] ⚠️ ModelLoadManager no disponible"); return false; }
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
                    Debug.Log($"[NavManager] ✅ [FullAR] Ruta calculada a '{waypoint.WaypointName}'.");
                else
                    Debug.LogError($"[NavManager] ❌ [FullAR] Sin ruta a '{waypoint.WaypointName}'.");

                NavigationVoiceGuide.Instance?.TriggerFromWaypoint(waypoint);
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
        private void Log(string msg)      => Debug.Log($"[NavManager] {msg}");

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
                      $"FPS: {Application.targetFrameRate} | " +
                      $"SessionInitInProgress: {_sessionInitInProgress}");
            if (_pathController != null)
                Debug.Log($"[NavManager] PathController: IsFullARMode={_pathController.IsFullARMode} | " +
                          $"IsNavigating={_pathController.IsNavigating}");
        }

        [ContextMenu("📦 Load Model")]
        private void DebugLoadModel()  => _ = LoadModelOnLargestPlane();

        [ContextMenu("🔄 Reset")]
        private void DebugReset()      => ResetSystem();

        [ContextMenu("🚀 Force Initialize")]
        private void DebugForceInit()
        {
            _isInitialized         = false;
            _sessionInitInProgress = false;
            _ = Initialize();
        }

        [ContextMenu("🧹 Force GC Now")]
        private void DebugForceGC()    => _ = ReleaseMemoryBeforeARStart();

        [ContextMenu("⏱️ Debug Align Wait")]
        private void DebugAlignWait()
        {
            Debug.Log($"[NavManager] _alignWaitTimeout={_alignWaitTimeout}s | " +
                      $"InitialAlignDone={_arOriginAligner?.InitialAlignDone} | " +
                      $"ARState={UnityEngine.XR.ARFoundation.ARSession.state}");
        }

        #endregion
    }
}