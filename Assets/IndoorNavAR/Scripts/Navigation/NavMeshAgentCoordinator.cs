using System.Threading.Tasks;
using UnityEngine;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Coordinador del sistema NavMesh con validación mejorada
    /// ✅ Compatible con Unity 6 y AR Foundation 6
    /// ✅ Validación correcta de modelo cargado
    /// ✅ Gestión inteligente de eventos
    /// </summary>
    public class NavMeshAgentCoordinator : MonoBehaviour
    {
        [Header("📦 Componentes")]
        [SerializeField] private MultiMeshWalkableSurfaceGenerator _walkableSurfaceGenerator;
        [SerializeField] private NavigationAgent _navigationAgent;
        [SerializeField] private AgentFloorPlacement _agentFloorPlacement;

        [Header("⚙️ Configuración")]
        [SerializeField] private bool _autoExecuteOnModelLoad = true;
        [SerializeField] private float _modelLoadDelay = 1.0f;
        [SerializeField] private bool _placeAgentBeforeNavMesh = true;
        [SerializeField] private bool _repositionAfterNavMesh = true;

        [Header("🔧 Timeouts")]
        [SerializeField] private float _generationTimeout = 30f;
        [SerializeField] private int _maxRetryAttempts = 2;

        [Header("🐛 Debug")]
        [SerializeField] private bool _logCoordinationSteps = true;

        private bool _isExecuting;
        private bool _isInitialized;
        private GameObject _lastLoadedModel;

        #region Unity Lifecycle

        private void Awake()
        {
            FindComponents();
        }

        private void OnEnable()
        {
            EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        private void OnDisable()
        {
            EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        #endregion

        #region Component Discovery

        private void FindComponents()
        {
            Log("🔍 Buscando componentes...");

            if (_walkableSurfaceGenerator == null)
            {
                _walkableSurfaceGenerator = FindFirstObjectByType<MultiMeshWalkableSurfaceGenerator>();
            }

            if (_navigationAgent == null)
            {
                _navigationAgent = FindFirstObjectByType<NavigationAgent>();
            }

            if (_agentFloorPlacement == null)
            {
                _agentFloorPlacement = FindFirstObjectByType<AgentFloorPlacement>();
                
                // Auto-crear si no existe
                if (_agentFloorPlacement == null && _navigationAgent != null)
                {
                    _agentFloorPlacement = _navigationAgent.gameObject.AddComponent<AgentFloorPlacement>();
                    Log("📍 AgentFloorPlacement creado automáticamente");
                }
            }

            ValidateComponents();
        }

        private void ValidateComponents()
        {
            bool valid = true;

            if (_walkableSurfaceGenerator == null)
            {
                Debug.LogError("[Coordinator] ❌ MultiMeshWalkableSurfaceGenerator faltante");
                valid = false;
            }

            if (_navigationAgent == null)
            {
                Debug.LogError("[Coordinator] ❌ NavigationAgent faltante");
                valid = false;
            }

            if (!valid)
            {
                Debug.LogError("[Coordinator] ❌ Sistema deshabilitado - componentes críticos faltantes");
                enabled = false;
                return;
            }

            Debug.Log("[Coordinator] ✅ Componentes validados");
            _isInitialized = true;
        }

        #endregion

        #region Event Handlers

        private async void OnModelLoaded(ModelLoadedEvent evt)
        {
            if (!_autoExecuteOnModelLoad)
            {
                Log($"📦 Modelo cargado: {evt.ModelName} (auto-execute OFF)");
                return;
            }

            Log($"📦 Modelo cargado: {evt.ModelName}");
            _lastLoadedModel = evt.ModelInstance;

            // Esperar estabilización
            Log($"⏳ Esperando {_modelLoadDelay}s...");
            await Task.Delay((int)(_modelLoadDelay * 1000));

            // Ejecutar setup
            await ExecuteFullSetup();
        }

        #endregion

        #region Main Flow

        public async Task<bool> ExecuteFullSetup()
        {
            if (!_isInitialized)
            {
                Debug.LogError("[Coordinator] ❌ No inicializado");
                return false;
            }

            if (_isExecuting)
            {
                Debug.LogWarning("[Coordinator] ⚠️ Ya ejecutando");
                return false;
            }

            // Validar modelo
            if (!IsModelLoaded())
            {
                Debug.LogError("[Coordinator] ❌ No hay modelo cargado");
                PublishMessage("Carga un modelo 3D primero", MessageType.Warning);
                return false;
            }

            _isExecuting = true;

            try
            {
                Log("═══════════════════════════════");
                Log("🚀 INICIANDO SETUP NAVEGACIÓN");
                Log("═══════════════════════════════");

                // PASO 1: Posicionar agente (pre-NavMesh)
                if (_placeAgentBeforeNavMesh && _agentFloorPlacement != null)
                {
                    Log("📍 [1/3] Posicionando agente...");
                    _agentFloorPlacement.PlaceAgentOnFloor();
                    await Task.Delay(100);
                }

                // PASO 2: Generar NavMesh
                Log("🔨 [2/3] Generando NavMesh...");
                bool success = await GenerateNavMeshWithRetry();

                if (!success)
                {
                    Debug.LogError("[Coordinator] ❌ Falló generación NavMesh");
                    PublishMessage("Error generando navegación", MessageType.Error);
                    return false;
                }

                // PASO 3: Ajustar agente (post-NavMesh)
                if (_repositionAfterNavMesh && _agentFloorPlacement != null)
                {
                    Log("🎯 [3/3] Ajustando agente en NavMesh...");
                    await Task.Delay(200);
                    _agentFloorPlacement.RepositionAgent();
                }

                Log("═══════════════════════════════");
                Log("✅ SETUP COMPLETADO");
                Log("═══════════════════════════════");

                PublishMessage("Sistema de navegación listo", MessageType.Success);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Coordinator] ❌ Error: {ex.Message}");
                PublishMessage("Error en setup", MessageType.Error);
                return false;
            }
            finally
            {
                _isExecuting = false;
            }
        }

        #endregion

        #region NavMesh Generation

        private async Task<bool> GenerateNavMeshWithRetry()
        {
            for (int i = 0; i < _maxRetryAttempts; i++)
            {
                if (i > 0)
                {
                    Log($"⚠️ Reintento {i + 1}/{_maxRetryAttempts}");
                    await Task.Delay(1000);
                }

                bool success = await GenerateWithTimeout();
                if (success) return true;
            }

            return false;
        }

        private async Task<bool> GenerateWithTimeout()
        {
            var genTask = _walkableSurfaceGenerator.GenerateWalkableSurfaceAsync();
            var timeoutTask = Task.Delay((int)(_generationTimeout * 1000));

            var completed = await Task.WhenAny(genTask, timeoutTask);

            if (completed == timeoutTask)
            {
                Debug.LogError("[Coordinator] ⏰ Timeout");
                return false;
            }

            return await genTask;
        }

        #endregion

        #region Validation

        private bool IsModelLoaded()
        {
            // Verificar si hay modelo en escena
            if (_lastLoadedModel != null && _lastLoadedModel.activeInHierarchy)
            {
                return true;
            }

            // Buscar ModelLoadManager
            var modelManager = FindFirstObjectByType<Core.Managers.ModelLoadManager>();
            if (modelManager != null && modelManager.IsModelLoaded)
            {
                _lastLoadedModel = modelManager.CurrentModel;
                return true;
            }

            return false;
        }

        #endregion

        #region Public API

        public async Task<bool> RegenerateAll()
        {
            Log("🔄 Regenerando todo...");

            if (_walkableSurfaceGenerator != null)
            {
                _walkableSurfaceGenerator.Clear();
            }

            await Task.Delay(100);
            return await ExecuteFullSetup();
        }

        public bool RepositionAgentOnly()
        {
            if (_agentFloorPlacement == null) return false;
            
            Log("📍 Reposicionando agente...");
            return _agentFloorPlacement.PlaceAgentOnFloor();
        }

        public async Task<bool> RegenerateNavMeshOnly()
        {
            if (_walkableSurfaceGenerator == null) return false;
            
            Log("🔨 Regenerando NavMesh...");
            return await GenerateNavMeshWithRetry();
        }

        public bool IsSystemReady()
        {
            return _isInitialized && 
                   !_isExecuting && 
                   _walkableSurfaceGenerator != null && 
                   _navigationAgent != null;
        }

        #endregion

        #region Utilities

        private void Log(string msg)
        {
            if (_logCoordinationSteps)
            {
                Debug.Log($"[Coordinator] {msg}");
            }
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

        [ContextMenu("🚀 Execute Setup")]
        private void DebugExecute() => _ = ExecuteFullSetup();

        [ContextMenu("🔄 Regenerate All")]
        private void DebugRegen() => _ = RegenerateAll();

        [ContextMenu("ℹ️ Status")]
        private void DebugStatus()
        {
            Debug.Log("========== STATUS ==========");
            Debug.Log($"Inicializado: {_isInitialized}");
            Debug.Log($"Ejecutando: {_isExecuting}");
            Debug.Log($"Modelo cargado: {IsModelLoaded()}");
            Debug.Log($"Sistema listo: {IsSystemReady()}");
            Debug.Log("============================");
        }

        #endregion
    }
}