using System;
using System.Threading.Tasks;
using UnityEngine;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Core.Controllers;
using IndoorNavAR.AR;
using IndoorNavAR.Navigation;
using IndoorNavAR.UI;

namespace IndoorNavAR.Core
{
    /// <summary>
    /// Gestor principal que orquesta todo el sistema de navegación AR.
    /// Inicializa componentes, gestiona estados y provee API unificada.
    /// ✅ MEJORADO: Integración completa con detección de paredes y eventos
    /// </summary>
    public class NavigationManager : MonoBehaviour
    {
        [Header("Referencias de Managers")]
        [SerializeField] private ARSessionManager _arSessionManager;
        [SerializeField] private WaypointManager _waypointManager;
        [SerializeField] private ModelLoadManager _modelLoadManager;
        [SerializeField] private PlacementController _placementController;
        [SerializeField] private NavMeshGenerator _navMeshGenerator;
        [SerializeField] private NavigationAgent _navigationAgent;
        [SerializeField] private UIManager _uiManager;
        [SerializeField] private PersistenceManager _persistenceManager;

        [Header("🧱 Referencias de Sistema de Navegación (NUEVO)")]
        [Tooltip("Generador robusto de superficie navegable con detección de paredes")]
        [SerializeField] private RobustWalkableSurfaceGenerator _walkableSurfaceGenerator;
        
        [Tooltip("Posicionador de agente en piso real")]
        [SerializeField] private AgentFloorPlacement _agentFloorPlacement;

        [Header("Configuración de Inicio")]
        [SerializeField] private bool _autoInitialize = true;
        [SerializeField] private bool _loadPreviousSession = false; // ✅ Cambiado a false por defecto
        [SerializeField] private bool _loadDefaultModelOnStart = true;
        
        [Header("🔧 Pipeline de Navegación (NUEVO)")]
        [Tooltip("Generar NavMesh automáticamente después de cargar modelo")]
        [SerializeField] private bool _autoGenerateNavMesh = true;
        
        [Tooltip("Delay antes de generar NavMesh (segundos)")]
        [SerializeField] private float _navMeshGenerationDelay = 1f;

        private AppMode _currentState = AppMode.Initialization;
        private bool _isInitialized;
        private bool _isProcessingNavMesh; // ✅ NUEVO: Evitar generaciones múltiples

        #region Properties

        public bool IsInitialized => _isInitialized;
        public AppMode CurrentState => _currentState;

        // Acceso a managers individuales
        public ARSessionManager ARSession => _arSessionManager;
        public WaypointManager Waypoints => _waypointManager;
        public ModelLoadManager Models => _modelLoadManager;
        public PlacementController Placement => _placementController;
        public NavMeshGenerator NavMesh => _navMeshGenerator;
        public NavigationAgent Agent => _navigationAgent;
        public UIManager UI => _uiManager;
        
        // ✅ NUEVO: Acceso a componentes de navegación
        public RobustWalkableSurfaceGenerator WalkableSurface => _walkableSurfaceGenerator;
        public AgentFloorPlacement AgentPlacement => _agentFloorPlacement;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateAndFindComponents();
        }

        private void OnEnable()
        {
            // ✅ NUEVO: Suscribirse a eventos del sistema
            EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
            EventBus.Instance?.Subscribe<NavMeshGeneratedEvent>(OnNavMeshGenerated);
        }

        private void OnDisable()
        {
            // ✅ NUEVO: Desuscribirse para evitar memory leaks
            EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);
            EventBus.Instance?.Unsubscribe<NavMeshGeneratedEvent>(OnNavMeshGenerated);
        }

        private void Start()
        {
            if (_autoInitialize)
            {
                _ = Initialize();
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// ✅ NUEVO: Reacciona cuando se carga un modelo
        /// </summary>
        private void OnModelLoaded(ModelLoadedEvent evt)
        {
            Debug.Log($"[NavigationManager] 📦 Modelo cargado: {evt.ModelName}");

            // Si está configurado, generar NavMesh automáticamente
            if (_autoGenerateNavMesh && !_isProcessingNavMesh)
            {
                _ = GenerateNavMeshDelayed();
            }
        }

        /// <summary>
        /// ✅ NUEVO: Reacciona cuando se genera el NavMesh
        /// </summary>
        private void OnNavMeshGenerated(NavMeshGeneratedEvent evt)
        {
            _isProcessingNavMesh = false;

            if (evt.Success)
            {
                Debug.Log($"[NavigationManager] ✅ NavMesh generado: {evt.SurfaceCount} superficies, {evt.TotalArea:F2}m²");
                
                // Cambiar al modo de colocación de waypoints
                ChangeState(AppMode.WaypointPlacement);
            }
            else
            {
                Debug.LogError("[NavigationManager] ❌ Generación de NavMesh falló");
            }
        }

        #endregion

        #region Initialization

        private void ValidateAndFindComponents()
        {
            // Auto-buscar componentes existentes
            if (_arSessionManager == null)
                _arSessionManager = FindFirstObjectByType<ARSessionManager>();

            if (_waypointManager == null)
                _waypointManager = FindFirstObjectByType<WaypointManager>();

            if (_modelLoadManager == null)
                _modelLoadManager = FindFirstObjectByType<ModelLoadManager>();

            if (_placementController == null)
                _placementController = FindFirstObjectByType<PlacementController>();

            if (_navMeshGenerator == null)
                _navMeshGenerator = FindFirstObjectByType<NavMeshGenerator>();

            if (_navigationAgent == null)
                _navigationAgent = FindFirstObjectByType<NavigationAgent>();

            if (_uiManager == null)
                _uiManager = FindFirstObjectByType<UIManager>();

            if (_persistenceManager == null)
                _persistenceManager = FindFirstObjectByType<PersistenceManager>();

            // ✅ NUEVO: Buscar componentes de navegación
            if (_walkableSurfaceGenerator == null)
                _walkableSurfaceGenerator = FindFirstObjectByType<RobustWalkableSurfaceGenerator>();

            if (_agentFloorPlacement == null)
                _agentFloorPlacement = FindFirstObjectByType<AgentFloorPlacement>();

            // Validar componentes críticos
            ValidateCriticalComponents();
        }

        private void ValidateCriticalComponents()
        {
            bool hasErrors = false;

            if (_arSessionManager == null)
            {
                Debug.LogError("[NavigationManager] ARSessionManager no encontrado.");
                hasErrors = true;
            }

            if (_waypointManager == null)
            {
                Debug.LogError("[NavigationManager] WaypointManager no encontrado.");
                hasErrors = true;
            }

            if (_navMeshGenerator == null && _walkableSurfaceGenerator == null)
            {
                Debug.LogError("[NavigationManager] Ningún generador de NavMesh encontrado.");
                hasErrors = true;
            }

            // ✅ NUEVO: Validar componentes de navegación
            if (_walkableSurfaceGenerator == null)
            {
                Debug.LogWarning("[NavigationManager] ⚠️ RobustWalkableSurfaceGenerator no encontrado. Usando NavMeshGenerator legacy.");
            }

            if (_agentFloorPlacement == null)
            {
                Debug.LogWarning("[NavigationManager] ⚠️ AgentFloorPlacement no encontrado. Posicionamiento automático deshabilitado.");
            }

            if (hasErrors)
            {
                Debug.LogError("[NavigationManager] Componentes críticos faltantes. Sistema no se iniciará.");
                enabled = false;
            }
            else
            {
                Debug.Log("[NavigationManager] ✅ Todos los componentes críticos validados.");
            }
        }

        /// <summary>
        /// Inicializa todo el sistema de forma secuencial.
        /// </summary>
        public async Task<bool> Initialize()
        {
            if (_isInitialized)
            {
                Debug.LogWarning("[NavigationManager] Sistema ya inicializado.");
                return true;
            }

            try
            {
                Debug.Log("[NavigationManager] 🚀 Iniciando sistema de navegación AR...");

                ChangeState(AppMode.Initialization);

                // Paso 1: Inicializar AR
                await InitializeAR();

                // Paso 2: Cargar sesión anterior si está habilitado (opcional)
                if (_loadPreviousSession && _persistenceManager != null)
                {
                    Debug.Log("[NavigationManager] Cargando sesión anterior...");
                    await _persistenceManager.LoadSession();
                }

                // Paso 3: Cargar modelo predeterminado si está configurado
                if (_loadDefaultModelOnStart && _modelLoadManager != null)
                {
                    Debug.Log("[NavigationManager] Cargando modelo predeterminado...");
                    bool modelLoaded = await LoadDefaultModel();
                    
                    if (!modelLoaded)
                    {
                        Debug.LogWarning("[NavigationManager] ⚠️ No se pudo cargar modelo predeterminado.");
                    }
                }
                else
                {
                    // Si no se carga modelo, ir a detección de planos
                    ChangeState(AppMode.PlaneDetection);
                }

                _isInitialized = true;

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Sistema iniciado correctamente.",
                    Type = MessageType.Success,
                    Duration = 3f
                });

                Debug.Log("[NavigationManager] ✅ Sistema inicializado exitosamente.");

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavigationManager] ❌ Error inicializando sistema: {ex.Message}");

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Error al inicializar sistema.",
                    Type = MessageType.Error,
                    Duration = 5f
                });

                return false;
            }
        }

        private async Task InitializeAR()
        {
            if (_arSessionManager == null)
            {
                Debug.LogWarning("[NavigationManager] ARSessionManager no disponible. Saltando inicialización AR.");
                return;
            }

            Debug.Log("[NavigationManager] Esperando AR Session...");

            // Esperar a que AR esté listo
            int timeout = 10; // 10 segundos
            while (!_arSessionManager.IsSessionReady && timeout > 0)
            {
                await Task.Delay(1000);
                timeout--;
            }

            if (!_arSessionManager.IsSessionReady)
            {
                throw new Exception("AR Session no se pudo inicializar en tiempo límite.");
            }

            Debug.Log("[NavigationManager] ✅ AR Session inicializada.");
        }

        #endregion

        #region State Management

        /// <summary>
        /// Cambia el estado de la aplicación.
        /// </summary>
        public void ChangeState(AppMode newState)
        {
            AppMode previousState = _currentState;
            _currentState = newState;

            EventBus.Instance.Publish(new AppModeChangedEvent
            {
                PreviousMode = previousState,
                NewMode = newState
            });

            // Actualizar UI
            if (_uiManager != null)
            {
                _uiManager.SetAppMode(newState);
            }

            Debug.Log($"[NavigationManager] Estado: {previousState} → {newState}");
        }

        #endregion

        #region Public API - Waypoints

        /// <summary>
        /// Activa/desactiva el modo de colocación de waypoints.
        /// </summary>
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

        /// <summary>
        /// Configura un waypoint específico.
        /// </summary>
        public void ConfigureWaypoint(Data.WaypointData waypoint)
        {
            if (_uiManager != null)
            {
                _uiManager.ShowWaypointConfigPanel(waypoint);
                ChangeState(AppMode.WaypointConfiguration);
            }
        }

        /// <summary>
        /// Elimina todos los waypoints.
        /// </summary>
        public void ClearAllWaypoints()
        {
            if (_waypointManager != null)
            {
                _waypointManager.ClearAllWaypoints();
            }
        }

        #endregion

        #region Public API - Models

        /// <summary>
        /// Carga el modelo predeterminado configurado en ModelLoadManager.
        /// ✅ MEJORADO: Maneja el pipeline completo con eventos
        /// </summary>
        public async Task<bool> LoadDefaultModel()
        {
            if (_modelLoadManager == null)
            {
                Debug.LogError("[NavigationManager] ModelLoadManager no disponible.");
                return false;
            }

            if (!_modelLoadManager.HasDefaultModel())
            {
                Debug.LogWarning("[NavigationManager] No hay modelo predeterminado configurado.");
                return false;
            }

            ChangeState(AppMode.ModelPlacement);

            // Intentar cargar en el plano más grande
            bool success = await _modelLoadManager.LoadDefaultModelOnLargestPlane();

            if (success)
            {
                Debug.Log("[NavigationManager] ✅ Modelo cargado exitosamente.");
                
                // El evento ModelLoadedEvent disparará la generación de NavMesh
                // si _autoGenerateNavMesh está habilitado
            }
            else
            {
                Debug.LogError("[NavigationManager] ❌ Error cargando modelo.");
                ChangeState(AppMode.PlaneDetection);
            }

            return success;
        }

        /// <summary>
        /// Carga un modelo desde Resources por nombre.
        /// </summary>
        public async Task<bool> LoadModel(string modelName)
        {
            if (_modelLoadManager == null)
                return false;

            ChangeState(AppMode.ModelPlacement);

            bool success = await _modelLoadManager.LoadModelOnLargestPlane(modelName);

            if (success)
            {
                Debug.Log($"[NavigationManager] ✅ Modelo '{modelName}' cargado.");
            }
            else
            {
                Debug.LogError($"[NavigationManager] ❌ Error cargando modelo '{modelName}'.");
                ChangeState(AppMode.PlaneDetection);
            }

            return success;
        }

        /// <summary>
        /// Descarga el modelo actual.
        /// </summary>
        public void UnloadModel()
        {
            if (_modelLoadManager != null)
            {
                _modelLoadManager.UnloadCurrentModel();
            }
            
            // Limpiar NavMesh también
            ClearNavMesh();
        }

        #endregion

        #region Public API - NavMesh

        /// <summary>
        /// ✅ NUEVO: Genera NavMesh con delay configurable
        /// </summary>
        private async Task GenerateNavMeshDelayed()
        {
            if (_isProcessingNavMesh)
            {
                Debug.LogWarning("[NavigationManager] ⚠️ Generación de NavMesh ya en progreso.");
                return;
            }

            _isProcessingNavMesh = true;

            Debug.Log($"[NavigationManager] Esperando {_navMeshGenerationDelay}s antes de generar NavMesh...");
            await Task.Delay((int)(_navMeshGenerationDelay * 1000));

            await GenerateNavMesh();
        }

        /// <summary>
        /// Genera o regenera el NavMesh.
        /// ✅ MEJORADO: Usa RobustWalkableSurfaceGenerator si está disponible
        /// </summary>
        public async Task<bool> GenerateNavMesh()
        {
            if (_isProcessingNavMesh)
            {
                Debug.LogWarning("[NavigationManager] ⚠️ Generación de NavMesh ya en progreso.");
                return false;
            }

            _isProcessingNavMesh = true;

            try
            {
                Debug.Log("[NavigationManager] 🔨 Generando NavMesh...");

                bool success = false;

                // ✅ PRIORIDAD: Usar RobustWalkableSurfaceGenerator (con detección de paredes)
                if (_walkableSurfaceGenerator != null)
                {
                    Debug.Log("[NavigationManager] Usando RobustWalkableSurfaceGenerator (con detección de paredes)");
                    success = await _walkableSurfaceGenerator.GenerateWalkableSurfaceAsync();
                }
                // FALLBACK: Usar NavMeshGenerator legacy
                else if (_navMeshGenerator != null)
                {
                    Debug.Log("[NavigationManager] Usando NavMeshGenerator legacy");
                    success = await _navMeshGenerator.RegenerateNavMesh();
                }
                else
                {
                    Debug.LogError("[NavigationManager] ❌ No hay generador de NavMesh disponible.");
                    _isProcessingNavMesh = false;
                    return false;
                }

                if (success)
                {
                    Debug.Log("[NavigationManager] ✅ NavMesh generado exitosamente.");

                    EventBus.Instance.Publish(new ShowMessageEvent
                    {
                        Message = "Navegación lista. Puedes seleccionar un destino.",
                        Type = MessageType.Success,
                        Duration = 3f
                    });
                }
                else
                {
                    Debug.LogError("[NavigationManager] ❌ Generación de NavMesh falló.");
                    _isProcessingNavMesh = false;
                }

                return success;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NavigationManager] ❌ Error generando NavMesh: {ex.Message}");
                _isProcessingNavMesh = false;
                return false;
            }
        }

        /// <summary>
        /// Limpia el NavMesh generado.
        /// </summary>
        public void ClearNavMesh()
        {
            // Limpiar con RobustWalkableSurfaceGenerator
            if (_walkableSurfaceGenerator != null)
            {
                _walkableSurfaceGenerator.Clear();
            }
            
            // Limpiar con NavMeshGenerator legacy
            if (_navMeshGenerator != null)
            {
                _navMeshGenerator.ClearNavMesh();
            }

            _isProcessingNavMesh = false;
        }

        #endregion

        #region Public API - Navigation

        /// <summary>
        /// Inicia navegación hacia un waypoint.
        /// </summary>
        public bool NavigateToWaypoint(Data.WaypointData waypoint)
        {
            if (_navigationAgent == null || waypoint == null)
                return false;

            bool success = _navigationAgent.NavigateToWaypoint(waypoint);

            if (success)
            {
                ChangeState(AppMode.Navigation);
                
                Debug.Log($"[NavigationManager] 🧭 Navegación iniciada hacia: {waypoint.WaypointName}");
            }
            else
            {
                Debug.LogError($"[NavigationManager] ❌ No se pudo iniciar navegación hacia: {waypoint.WaypointName}");
            }

            return success;
        }

        /// <summary>
        /// Detiene la navegación actual.
        /// </summary>
        public void StopNavigation()
        {
            if (_navigationAgent != null)
            {
                _navigationAgent.StopNavigation("Usuario detuvo navegación");
                ChangeState(AppMode.WaypointPlacement);
                
                Debug.Log("[NavigationManager] 🛑 Navegación detenida.");
            }
        }

        /// <summary>
        /// Abre el panel de navegación para seleccionar destino.
        /// </summary>
        public void OpenNavigationPanel()
        {
            if (_uiManager != null)
            {
                _uiManager.ShowNavigationPanel();
            }
        }

        #endregion

        #region Public API - Agent Placement

        /// <summary>
        /// ✅ NUEVO: Posiciona el agente manualmente en el piso real
        /// </summary>
        public void PlaceAgentOnFloor()
        {
            if (_agentFloorPlacement != null)
            {
                _agentFloorPlacement.PlaceAgentOnFloor();
            }
            else
            {
                Debug.LogWarning("[NavigationManager] AgentFloorPlacement no disponible.");
            }
        }

        #endregion

        #region Public API - Persistence

        /// <summary>
        /// Guarda la sesión actual.
        /// </summary>
        public async Task<bool> SaveSession()
        {
            if (_persistenceManager == null)
                return false;

            return await _persistenceManager.SaveSession();
        }

        /// <summary>
        /// Carga la sesión guardada.
        /// </summary>
        public async Task<bool> LoadSession()
        {
            if (_persistenceManager == null)
                return false;

            return await _persistenceManager.LoadSession();
        }

        /// <summary>
        /// Limpia la sesión guardada.
        /// </summary>
        public void ClearSavedSession()
        {
            if (_persistenceManager != null)
            {
                _persistenceManager.ClearSavedData();
            }
        }

        #endregion

        #region Public API - AR Controls

        /// <summary>
        /// Alterna visualización de planos AR.
        /// </summary>
        public void TogglePlaneVisualization(bool show)
        {
            if (_arSessionManager != null)
            {
                _arSessionManager.TogglePlaneVisualization(show);
            }
        }

        /// <summary>
        /// Limpia todos los planos AR detectados.
        /// </summary>
        public void ClearAllPlanes()
        {
            if (_arSessionManager != null)
            {
                _arSessionManager.ClearAllPlanes();
            }
        }

        #endregion

        #region Complete Reset

        /// <summary>
        /// Resetea todo el sistema al estado inicial.
        /// </summary>
        public void ResetSystem()
        {
            Debug.Log("[NavigationManager] 🔄 Reseteando sistema completo...");

            // Detener navegación
            StopNavigation();

            // Limpiar waypoints
            ClearAllWaypoints();

            // Limpiar NavMesh
            ClearNavMesh();

            // Descargar modelo
            UnloadModel();

            // Desactivar colocación
            ToggleWaypointPlacement(false);

            // Cambiar a estado inicial
            ChangeState(AppMode.PlaneDetection);

            EventBus.Instance.Publish(new ShowMessageEvent
            {
                Message = "Sistema reseteado.",
                Type = MessageType.Info,
                Duration = 2f
            });

            Debug.Log("[NavigationManager] ✅ Sistema reseteado completamente.");
        }

        #endregion

        #region Debug

        [ContextMenu("Debug: Complete System Info")]
        private void DebugSystemInfo()
        {
            Debug.Log("========== NAVIGATION SYSTEM INFO ==========");
            Debug.Log($"Estado actual: {_currentState}");
            Debug.Log($"Inicializado: {_isInitialized}");
            Debug.Log($"Procesando NavMesh: {_isProcessingNavMesh}");
            Debug.Log("");
            Debug.Log("--- Componentes AR ---");
            Debug.Log($"AR Session Ready: {_arSessionManager?.IsSessionReady ?? false}");
            Debug.Log($"Planos detectados: {_arSessionManager?.DetectedPlaneCount ?? 0}");
            Debug.Log("");
            Debug.Log("--- Modelo ---");
            Debug.Log($"Modelo cargado: {_modelLoadManager?.IsModelLoaded ?? false}");
            Debug.Log($"Nombre: {_modelLoadManager?.CurrentModelName ?? "Ninguno"}");
            Debug.Log("");
            Debug.Log("--- Navegación ---");
            Debug.Log($"NavMesh generado: {(_walkableSurfaceGenerator != null ? "Sí (Robust)" : (_navMeshGenerator != null ? "Sí (Legacy)" : "No"))}");
            Debug.Log($"Waypoints: {_waypointManager?.WaypointCount ?? 0}");
            Debug.Log($"Navegando: {_navigationAgent?.IsNavigating ?? false}");
            Debug.Log("");
            Debug.Log("--- Nuevo Sistema ---");
            Debug.Log($"RobustWalkableSurfaceGenerator: {(_walkableSurfaceGenerator != null ? "✅" : "❌")}");
            Debug.Log($"AgentFloorPlacement: {(_agentFloorPlacement != null ? "✅" : "❌")}");
            Debug.Log("===========================================");
        }

        [ContextMenu("Debug: Quick Setup")]
        private async void DebugQuickSetup()
        {
            Debug.Log("[NavigationManager] 🚀 Ejecutando Quick Setup...");

            // Esperar AR si es necesario
            if (_arSessionManager != null && !_arSessionManager.IsSessionReady)
            {
                Debug.Log("[NavigationManager] Esperando AR Session...");
                await Task.Delay(2000);
            }

            // Cargar modelo predeterminado
            Debug.Log("[NavigationManager] Cargando modelo...");
            bool modelLoaded = await LoadDefaultModel();

            if (!modelLoaded)
            {
                Debug.LogError("[NavigationManager] ❌ Quick Setup falló: No se pudo cargar modelo.");
                return;
            }

            // Esperar a que se genere NavMesh automáticamente
            Debug.Log("[NavigationManager] Esperando generación automática de NavMesh...");
            
            int timeout = 20; // 20 segundos máximo
            while (_isProcessingNavMesh && timeout > 0)
            {
                await Task.Delay(500);
                timeout--;
            }

            if (timeout == 0)
            {
                Debug.LogError("[NavigationManager] ⏰ Timeout esperando NavMesh.");
            }
            else
            {
                Debug.Log("[NavigationManager] ✅ Quick Setup completado exitosamente.");
            }
        }

        [ContextMenu("Debug: Force NavMesh Generation")]
        private async void DebugForceNavMesh()
        {
            Debug.Log("[NavigationManager] 🔨 Forzando generación de NavMesh...");
            await GenerateNavMesh();
        }

        [ContextMenu("Debug: Place Agent on Floor")]
        private void DebugPlaceAgent()
        {
            PlaceAgentOnFloor();
        }

        #endregion
    }
}