// File: NavMeshAgentCoordinator.cs
// ✅ FIX v2 — Prevención del ciclo de degradación del NavMesh.
//
//  CAMBIO PRINCIPAL:
//    GenerateAndSave() ahora llama _persistenceManager.NotifyNavMeshBaked()
//    ANTES de SaveSession(). Sin esta llamada, PersistenceManager v3 no
//    sobreescribe navmesh_data.bin (lo preserva del bake anterior).
//    Con esta llamada, PersistenceManager sabe que el NavMesh activo es
//    un bake real y puede guardarlo con confianza.
//
//    RegenerateNavMeshOnly() recibe el mismo tratamiento.
//
//  LO QUE NO CAMBIA:
//    Toda la lógica de orquestación, guards, timeouts y retries.
//    La llamada a LoadNavMeshFromFile() sigue sin activar el flag
//    (PersistenceManager lo desactiva en esa ruta, como es correcto).

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

        private void Awake()     => FindComponents();
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
            if (_setupAlreadyDone)
            {
                Log("📦 ModelLoadedEvent ignorado — setup ya completado (sesión restaurada).");
                return;
            }

            if (!_autoExecuteOnModelLoad) return;

            Log($"📦 Modelo cargado: {evt.ModelName} — ejecutando setup en {_modelLoadDelay}s...");
            _lastLoadedModel = evt.ModelInstance;
            await Task.Delay((int)(_modelLoadDelay * 1000));

            if (_setupAlreadyDone)
            {
                Log("📦 Setup completado durante el delay — ignorando.");
                return;
            }

            await ExecuteFullSetup();
        }

        #endregion

        #region Main Flow

        /// <summary>
        /// Llamado por NavigationManager cuando restaura desde sesión guardada.
        /// Bloquea OnModelLoaded para que no re-ejecute el setup.
        /// </summary>
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
                Log("🚀 INICIANDO SETUP MULTI-NIVEL");
                Log("═══════════════════════════════");

                // ── PASO 1: NavMesh ───────────────────────────────────────
                bool navMeshOk;
                bool hasSaved = _persistenceManager != null && _persistenceManager.HasSavedNavMesh;

                if (hasSaved)
                {
                    Log("💾 [1/2] Cargando NavMesh desde disco...");
                    navMeshOk = await _persistenceManager.LoadNavMeshFromFile();
                    // LoadNavMeshFromFile() pone _navMeshWasBaked = false internamente.
                    // Si falla, caemos a GenerateAndSave() que sí activa el flag.
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

                // ── PASO 2: Posicionar agente ─────────────────────────────
                await Task.Delay(300);
                Log("📍 [2/2] Posicionando agente...");
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
        /// Genera el NavMesh y lo guarda.
        /// ✅ FIX v2: Llama NotifyNavMeshBaked() antes de SaveSession() para que
        /// PersistenceManager sepa que puede sobreescribir navmesh_data.bin con
        /// datos frescos del bake (no con el NavMesh degradado de una carga previa).
        /// </summary>
        private async Task<bool> GenerateAndSave()
        {
            bool ok = await GenerateNavMeshWithRetry();

            if (ok && _persistenceManager != null)
            {
                // ✅ FIX: Activar flag ANTES de guardar.
                // Sin esta llamada, PersistenceManager v3 preserva el .bin anterior
                // en lugar de sobreescribirlo, lo cual es correcto para cargas pero
                // incorrecto para bakes frescos.
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

        /// <summary>
        /// ✅ FIX v2: También activa NotifyNavMeshBaked() aquí, por consistencia.
        /// </summary>
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
            Debug.Log($"[Coordinator] Inicializado={_isInitialized} | Ejecutando={_isExecuting} | SetupDone={_setupAlreadyDone}");
            Debug.Log($"[Coordinator] Modelo={IsModelLoaded()} | Listo={IsSystemReady()}");
            Debug.Log($"[Coordinator] NavMesh guardado: {NavMeshSerializer.HasSavedNavMesh}");
        }

        #endregion
    }
}