// File: NavMeshAgentCoordinator.cs
// ✅ FIX v3 — Modo "solo renderizado": en mobile con sesión restaurada,
//             nunca genera obstáculos ni bake. Solo carga el NavMesh desde disco.
//
//  CAMBIO PRINCIPAL:
//    _renderOnlyMode (bool, default true en builds mobile):
//      - Si true Y _setupAlreadyDone: OnModelLoaded ignora completamente el evento.
//      - Si true en ExecuteFullSetup: solo carga NavMesh desde disco, nunca bake.
//      - El flag se activa automáticamente en builds non-Editor.
//
//    _autoFindProxyMeshes queda como false por defecto en runtime:
//      Los tags NavMeshObstacle no deben procesarse en mobile, ya están en el NavMesh bakeado.

using System.Threading.Tasks;
using UnityEngine;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Core;

namespace IndoorNavAR.Navigation
{
    public class NavMeshAgentCoordinator : MonoBehaviour
    {
        [Header("📦 Componentes")]
        [SerializeField] private MultiLevelNavMeshGenerator _multiLevelGenerator;
        [SerializeField] private NavigationAgent            _navigationAgent;
        [SerializeField] private PersistenceManager         _persistenceManager;

        [Header("⚙️ Configuración")]
        [SerializeField] private bool  _autoExecuteOnModelLoad = true;
        [SerializeField] private float _modelLoadDelay         = 1.0f;

        [Header("📱 Modo Mobile")]
        [Tooltip("En builds de dispositivo, nunca regenera obstáculos ni hace bake.\n" +
                 "Solo carga el NavMesh guardado. Se activa automáticamente fuera del Editor.")]
        [SerializeField] private bool _renderOnlyMode = true;

        [Header("🔧 Timeouts")]
        [SerializeField] private float _generationTimeout = 30f;
        [SerializeField] private int   _maxRetryAttempts  = 2;

        [Header("🐛 Debug")]
        [SerializeField] private bool _logCoordinationSteps = true;

        private bool       _isExecuting;
        private bool       _isInitialized;
        private bool       _setupAlreadyDone;
        private GameObject _lastLoadedModel;

        #region Lifecycle

        private void Awake()
        {
            FindComponents();

#if !UNITY_EDITOR
            // Fuera del Editor, forzar render-only: nunca generar obstáculos en device.
            _renderOnlyMode = true;
            Log("📱 Build de dispositivo detectado → _renderOnlyMode = true");
#endif
        }

        private void OnEnable()  => EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
        private void OnDisable() => EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);

        #endregion

        #region Component Discovery

        private void FindComponents()
        {
            _multiLevelGenerator ??= FindFirstObjectByType<MultiLevelNavMeshGenerator>();
            _navigationAgent     ??= FindFirstObjectByType<NavigationAgent>();
            _persistenceManager  ??= FindFirstObjectByType<PersistenceManager>();
            ValidateComponents();
        }

        private void ValidateComponents()
        {
            bool valid = true;
            if (_multiLevelGenerator == null)
            { Debug.LogError("[Coordinator] ❌ MultiLevelNavMeshGenerator faltante"); valid = false; }
            if (_navigationAgent == null)
            { Debug.LogError("[Coordinator] ❌ NavigationAgent faltante"); valid = false; }
            if (_persistenceManager == null)
                Debug.LogWarning("[Coordinator] ⚠️ PersistenceManager no encontrado");

            if (!valid) { enabled = false; return; }
            Debug.Log("[Coordinator] ✅ Componentes validados");
            _isInitialized = true;
        }

        #endregion

        #region Event Handlers

        private async void OnModelLoaded(ModelLoadedEvent evt)
        {
            // FIX v3: En modo render-only con setup ya completo, ignorar completamente.
            // El modelo se renderiza pero no se toca el NavMesh ni los obstáculos.
            if (_renderOnlyMode && _setupAlreadyDone)
            {
                Log("📱 ModelLoadedEvent ignorado — render-only mode, NavMesh ya activo.");
                return;
            }

            if (_setupAlreadyDone)
            {
                Log("📦 ModelLoadedEvent ignorado — setup ya completado (sesión restaurada).");
                return;
            }

            if (!_autoExecuteOnModelLoad) return;

            Log($"📦 Modelo cargado: {evt.ModelName} — ejecutando setup en {_modelLoadDelay}s...");
            _lastLoadedModel = evt.ModelInstance;
            await Task.Delay((int)(_modelLoadDelay * 1000));

            if (_setupAlreadyDone) { Log("📦 Setup completado durante el delay — ignorando."); return; }

            await ExecuteFullSetup();
        }

        #endregion

        #region Main Flow

        public void MarkSetupDone()
        {
            _setupAlreadyDone = true;
            Log("✅ Setup marcado como completado (sesión restaurada).");
        }

        public async Task<bool> ExecuteFullSetup()
        {
            if (!_isInitialized)
            { Debug.LogError("[Coordinator] ❌ No inicializado"); return false; }
            if (_isExecuting)
            { Debug.LogWarning("[Coordinator] ⚠️ Ya ejecutando — ignorando"); return false; }
            if (_setupAlreadyDone)
            { Log("⚠️ Setup ya completado — ignorando"); return true; }
            if (!IsModelLoaded())
            { PublishMessage("Carga un modelo 3D primero", MessageType.Warning); return false; }

            _isExecuting = true;
            try
            {
                Log("═══════════════════════════════");
                Log(_renderOnlyMode ? "📱 SETUP — MODO RENDER ONLY" : "🚀 INICIANDO SETUP MULTI-NIVEL");
                Log("═══════════════════════════════");

                bool hasSaved = _persistenceManager != null && _persistenceManager.HasSavedNavMesh;

                if (_renderOnlyMode)
                {
                    // ── MODO RENDER-ONLY ──────────────────────────────────
                    // Solo carga el NavMesh desde disco. Jamás genera obstáculos.
                    if (hasSaved)
                    {
                        Log("💾 [render-only] Cargando NavMesh desde disco...");
                        bool loaded = await _persistenceManager.LoadNavMeshFromFile();
                        if (!loaded)
                        {
                            Debug.LogError("[Coordinator] ❌ Falló carga NavMesh (render-only)");
                            PublishMessage("Error cargando navegación guardada", MessageType.Error);
                            return false;
                        }
                        Log("✅ NavMesh cargado en modo render-only.");
                    }
                    else
                    {
                        // Sin NavMesh guardado en render-only: no podemos generar.
                        // Avisamos al usuario y retornamos false.
                        Debug.LogWarning("[Coordinator] ⚠️ render-only pero sin NavMesh guardado.");
                        PublishMessage("Sin NavMesh guardado. Escanea primero en Editor.", MessageType.Warning);
                        return false;
                    }
                }
                else
                {
                    // ── MODO COMPLETO (Editor / primera vez) ──────────────
                    bool navMeshOk;
                    if (hasSaved)
                    {
                        Log("💾 [1/2] Cargando NavMesh desde disco...");
                        navMeshOk = await _persistenceManager.LoadNavMeshFromFile();
                        if (!navMeshOk)
                        {
                            Log("⚠️ Fallo carga disco → generando NavMesh...");
                            navMeshOk = await GenerateAndSave();
                        }
                        else Log("✅ NavMesh restaurado desde archivo.");
                    }
                    else
                    {
                        Log("🔨 [1/2] Sin NavMesh guardado → generando...");
                        navMeshOk = await GenerateAndSave();
                    }

                    if (!navMeshOk)
                    {
                        Debug.LogError("[Coordinator] ❌ Falló NavMesh");
                        PublishMessage("Error generando navegación", MessageType.Error);
                        return false;
                    }
                }

                // ── Posicionar agente (común en ambos modos) ──────────────
                await Task.Delay(300);
                Log("📍 Posicionando agente...");
                var sp = NavigationStartPointManager.GetStartPointForLevel(0);
                if (sp != null) sp.ReteleportAgent();
                else Debug.LogWarning("[Coordinator] ⚠️ Sin NavigationStartPoint");

                _setupAlreadyDone = true;
                Log("✅ SETUP COMPLETADO");
                PublishMessage("Sistema de navegación listo", MessageType.Success);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Coordinator] ❌ Error: {ex.Message}");
                PublishMessage("Error en setup", MessageType.Error);
                return false;
            }
            finally { _isExecuting = false; }
        }

        /// <summary>
        /// Genera el NavMesh y lo guarda. Solo llamado en modo completo (no render-only).
        /// ✅ FIX v2: Llama NotifyNavMeshBaked() antes de SaveSession().
        /// </summary>
        private async Task<bool> GenerateAndSave()
        {
            bool ok = await GenerateNavMeshWithRetry();
            if (ok && _persistenceManager != null)
            {
                _persistenceManager.NotifyNavMeshBaked();
                Log("💾 Auto-guardando NavMesh bakeado...");
                await _persistenceManager.SaveSession();
            }
            return ok;
        }

        #endregion

        #region NavMesh Generation

        private async Task<bool> GenerateNavMeshWithRetry()
        {
            for (int i = 0; i < _maxRetryAttempts; i++)
            {
                if (i > 0) { Log($"⚠️ Reintento {i + 1}/{_maxRetryAttempts}"); await Task.Delay(1000); }
                if (await GenerateWithTimeout()) return true;
            }
            return false;
        }

        private async Task<bool> GenerateWithTimeout()
        {
            var gen     = _multiLevelGenerator.GenerateMultiLevelNavMeshAsync();
            var timeout = Task.Delay((int)(_generationTimeout * 1000));
            var done    = await Task.WhenAny(gen, timeout);
            if (done == timeout) { Debug.LogError("[Coordinator] ⏰ Timeout"); return false; }
            return await gen;
        }

        #endregion

        #region Validation

        private bool IsModelLoaded()
        {
            if (_lastLoadedModel != null && _lastLoadedModel.activeInHierarchy) return true;
            var mm = FindFirstObjectByType<ModelLoadManager>();
            if (mm != null && mm.IsModelLoaded) { _lastLoadedModel = mm.CurrentModel; return true; }
            return false;
        }

        #endregion

        #region Public API

        public async Task<bool> RegenerateAll()
        {
            Log("🔄 Regenerando todo (forzado)...");
            _setupAlreadyDone = false;
            _multiLevelGenerator?.Clear();
            _persistenceManager?.RemoveLoadedNavMesh();
            NavMeshSerializer.DeleteSaved();
            await Task.Delay(100);
            return await ExecuteFullSetup();
        }

        public bool RepositionAgentOnly()
        {
            var sp = NavigationStartPointManager.GetStartPointForLevel(0);
            if (sp == null) return false;
            sp.ReteleportAgent();
            return true;
        }

        public async Task<bool> RegenerateNavMeshOnly()
        {
            _setupAlreadyDone = false;
            bool ok = await GenerateNavMeshWithRetry();
            if (ok && _persistenceManager != null)
            {
                _persistenceManager.NotifyNavMeshBaked();
                await _persistenceManager.SaveSession();
            }
            return ok;
        }

        public bool IsSystemReady() =>
            _isInitialized && !_isExecuting && _multiLevelGenerator != null && _navigationAgent != null;

        /// <summary>
        /// Activa/desactiva el modo render-only en tiempo de ejecución.
        /// Útil para forzar un re-bake desde el Editor aunque _renderOnlyMode fuera true.
        /// </summary>
        public void SetRenderOnlyMode(bool renderOnly)
        {
            _renderOnlyMode = renderOnly;
            Log($"📱 renderOnlyMode = {renderOnly}");
        }

        #endregion

        #region Utilities

        private void Log(string m) { if (_logCoordinationSteps) Debug.Log($"[Coordinator] {m}"); }

        private void PublishMessage(string m, MessageType t) =>
            EventBus.Instance?.Publish(new ShowMessageEvent
            { Message = m, Type = t, Duration = t == MessageType.Error ? 5f : 3f });

        #endregion

        #region Debug

        [ContextMenu("🚀 Execute Setup")]
        private void DebugExecute() { _setupAlreadyDone = false; _ = ExecuteFullSetup(); }

        [ContextMenu("🔄 Regenerate All (borra guardado)")]
        private void DebugRegen() => _ = RegenerateAll();

        [ContextMenu("💾 NavMesh Info")]
        private void DebugNavInfo() => Debug.Log(NavMeshSerializer.GetSavedInfo());

        [ContextMenu("🗑️ Borrar NavMesh Guardado")]
        private void DebugDelete() { NavMeshSerializer.DeleteSaved(); _setupAlreadyDone = false; }

        [ContextMenu("ℹ️ Status")]
        private void DebugStatus()
        {
            Debug.Log($"[Coordinator] Inicializado={_isInitialized} | Ejecutando={_isExecuting} | " +
                      $"SetupDone={_setupAlreadyDone} | RenderOnly={_renderOnlyMode}");
            Debug.Log($"[Coordinator] Modelo={IsModelLoaded()} | Listo={IsSystemReady()}");
            Debug.Log($"[Coordinator] NavMesh guardado: {NavMeshSerializer.HasSavedNavMesh}");
        }

        [ContextMenu("📱 Toggle RenderOnly Mode")]
        private void DebugToggleRenderOnly() => SetRenderOnlyMode(!_renderOnlyMode);

        #endregion
    }
}