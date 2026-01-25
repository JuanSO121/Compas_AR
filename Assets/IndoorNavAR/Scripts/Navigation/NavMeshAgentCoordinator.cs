using System.Threading.Tasks;
using UnityEngine;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Coordina la generación de NavMesh y el posicionamiento correcto del agente.
    /// ✅ GARANTIZA flujo correcto: detectar piso → generar NavMesh → posicionar agente.
    /// </summary>
    public class NavMeshAgentCoordinator : MonoBehaviour
    {
        [Header("Referencias")]
        [SerializeField] private NavMeshGenerator _navMeshGenerator;
        [SerializeField] private NavigationAgent _navigationAgent;
        [SerializeField] private AgentFloorPlacement _agentFloorPlacement;

        [Header("Configuración")]
        [Tooltip("Posicionar agente antes de generar NavMesh")]
        [SerializeField] private bool _placeAgentBeforeNavMesh = true;

        [Tooltip("Reposicionar agente después de generar NavMesh")]
        [SerializeField] private bool _repositionAfterNavMesh = true;

        [Tooltip("Ejecutar flujo completo al iniciar")]
        [SerializeField] private bool _autoExecuteOnStart = true;

        [Tooltip("Delay antes de ejecutar (segundos)")]
        [SerializeField] private float _startDelay = 2.5f;

        [Header("Debug")]
        [SerializeField] private bool _logCoordinationSteps = true;

        private bool _isExecuting;

        #region Unity Lifecycle

        private void Awake()
        {
            ValidateComponents();
        }

        private void Start()
        {
            if (_autoExecuteOnStart)
            {
                Invoke(nameof(ExecuteFullSetupAsync), _startDelay);
            }
        }

        #endregion

        #region Validation

        private void ValidateComponents()
        {
            bool hasErrors = false;

            if (_navMeshGenerator == null)
            {
                _navMeshGenerator = FindFirstObjectByType<NavMeshGenerator>();
                if (_navMeshGenerator == null)
                {
                    Debug.LogError("[NavMeshAgentCoordinator] NavMeshGenerator no encontrado");
                    hasErrors = true;
                }
            }

            if (_navigationAgent == null)
            {
                _navigationAgent = FindFirstObjectByType<NavigationAgent>();
                if (_navigationAgent == null)
                {
                    Debug.LogError("[NavMeshAgentCoordinator] NavigationAgent no encontrado");
                    hasErrors = true;
                }
            }

            if (_agentFloorPlacement == null)
            {
                // Intentar obtener del NavigationAgent
                if (_navigationAgent != null)
                {
                    _agentFloorPlacement = _navigationAgent.GetComponent<AgentFloorPlacement>();
                }

                // Si no existe, agregarlo automáticamente
                if (_agentFloorPlacement == null && _navigationAgent != null)
                {
                    Debug.Log("[NavMeshAgentCoordinator] Agregando AgentFloorPlacement al NavigationAgent...");
                    _agentFloorPlacement = _navigationAgent.gameObject.AddComponent<AgentFloorPlacement>();
                }

                if (_agentFloorPlacement == null)
                {
                    Debug.LogError("[NavMeshAgentCoordinator] No se pudo configurar AgentFloorPlacement");
                    hasErrors = true;
                }
            }

            if (hasErrors)
            {
                enabled = false;
            }
            else
            {
                Debug.Log("[NavMeshAgentCoordinator] ✅ Componentes validados correctamente");
            }
        }

        #endregion

        #region Full Setup Flow

        private async void ExecuteFullSetupAsync()
        {
            await ExecuteFullSetup();
        }

        /// <summary>
        /// Ejecuta el flujo completo de configuración:
        /// 1. Posicionar agente sobre el piso
        /// 2. Generar NavMesh
        /// 3. Reposicionar agente en NavMesh
        /// </summary>
        public async Task<bool> ExecuteFullSetup()
        {
            if (_isExecuting)
            {
                Debug.LogWarning("[NavMeshAgentCoordinator] Setup ya en ejecución");
                return false;
            }

            _isExecuting = true;

            try
            {
                if (_logCoordinationSteps)
                {
                    Debug.Log("[NavMeshAgentCoordinator] ==========================================");
                    Debug.Log("[NavMeshAgentCoordinator] 🚀 INICIANDO CONFIGURACIÓN COMPLETA");
                    Debug.Log("[NavMeshAgentCoordinator] ==========================================");
                }

                // PASO 1: Posicionar agente ANTES de generar NavMesh
                if (_placeAgentBeforeNavMesh)
                {
                    if (_logCoordinationSteps)
                    {
                        Debug.Log("[NavMeshAgentCoordinator] 📍 PASO 1: Posicionando agente sobre el piso...");
                    }

                    bool agentPlaced = _agentFloorPlacement.PlaceAgentOnFloor();
                    
                    if (!agentPlaced)
                    {
                        Debug.LogError("[NavMeshAgentCoordinator] ❌ Error posicionando agente");
                        return false;
                    }

                    await Task.Delay(100); // Pequeño delay para estabilización
                }

                // PASO 2: Generar NavMesh
                if (_logCoordinationSteps)
                {
                    Debug.Log("[NavMeshAgentCoordinator] 🔨 PASO 2: Generando NavMesh...");
                }

                bool navMeshGenerated = await _navMeshGenerator.GenerateAdvancedNavMesh();

                if (!navMeshGenerated)
                {
                    Debug.LogError("[NavMeshAgentCoordinator] ❌ Error generando NavMesh");
                    return false;
                }

                // PASO 3: Reposicionar agente DESPUÉS de NavMesh (ajuste fino)
                if (_repositionAfterNavMesh)
                {
                    if (_logCoordinationSteps)
                    {
                        Debug.Log("[NavMeshAgentCoordinator] 🎯 PASO 3: Ajustando posición del agente en NavMesh...");
                    }

                    await Task.Delay(200); // Esperar a que NavMesh se consolide

                    _agentFloorPlacement.RepositionAgent();
                }

                if (_logCoordinationSteps)
                {
                    Debug.Log("[NavMeshAgentCoordinator] ==========================================");
                    Debug.Log("[NavMeshAgentCoordinator] ✅ CONFIGURACIÓN COMPLETA EXITOSA");
                    Debug.Log("[NavMeshAgentCoordinator] ==========================================");
                }

                EventBus.Instance.Publish(new ShowMessageEvent
                {
                    Message = "Sistema de navegación listo",
                    Type = MessageType.Success,
                    Duration = 3f
                });

                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[NavMeshAgentCoordinator] ❌ Error en setup: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
            finally
            {
                _isExecuting = false;
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Regenera todo el sistema (útil al cambiar de mapa).
        /// </summary>
        public async Task<bool> RegenerateAll()
        {
            if (_logCoordinationSteps)
            {
                Debug.Log("[NavMeshAgentCoordinator] 🔄 Regenerando sistema completo...");
            }

            // Limpiar NavMesh anterior
            _navMeshGenerator.ClearNavMesh();

            await Task.Delay(100);

            // Ejecutar setup completo
            return await ExecuteFullSetup();
        }

        /// <summary>
        /// Solo posiciona el agente (sin regenerar NavMesh).
        /// </summary>
        public bool RepositionAgentOnly()
        {
            if (_logCoordinationSteps)
            {
                Debug.Log("[NavMeshAgentCoordinator] 📍 Reposicionando solo el agente...");
            }

            return _agentFloorPlacement.PlaceAgentOnFloor();
        }

        /// <summary>
        /// Solo regenera el NavMesh (sin mover el agente).
        /// </summary>
        public async Task<bool> RegenerateNavMeshOnly()
        {
            if (_logCoordinationSteps)
            {
                Debug.Log("[NavMeshAgentCoordinator] 🔨 Regenerando solo NavMesh...");
            }

            return await _navMeshGenerator.RegenerateNavMesh();
        }

        #endregion

        #region Event Subscriptions

        private void OnEnable()
        {
            // Suscribirse a eventos relevantes
            EventBus.Instance.Subscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        private void OnDisable()
        {
            EventBus.Instance.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        private async void OnModelLoaded(ModelLoadedEvent evt)
        {
            if (_logCoordinationSteps)
            {
                Debug.Log($"[NavMeshAgentCoordinator] 📦 Modelo cargado: {evt.ModelName}. Regenerando sistema...");
            }

            // Esperar un poco a que el modelo se estabilice
            await Task.Delay(500);

            // Regenerar todo
            await RegenerateAll();
        }

        #endregion

        #region Debug

        [ContextMenu("🚀 Execute Full Setup")]
        private void DebugExecuteSetup()
        {
            _ = ExecuteFullSetup();
        }

        [ContextMenu("🔄 Regenerate All")]
        private void DebugRegenerateAll()
        {
            _ = RegenerateAll();
        }

        [ContextMenu("📍 Reposition Agent Only")]
        private void DebugRepositionAgent()
        {
            RepositionAgentOnly();
        }

        [ContextMenu("🔨 Regenerate NavMesh Only")]
        private void DebugRegenerateNavMesh()
        {
            _ = RegenerateNavMeshOnly();
        }

        #endregion
    }
}