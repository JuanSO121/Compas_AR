using UnityEngine;
using IndoorNavAR.Core;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Navigation;
using IndoorNavAR.Core.Controllers;

namespace IndoorNavAR.Testing
{
    /// <summary>
    /// Helper para testing rápido del sistema sin UI.
    /// Incluye métodos con [ContextMenu] para probar funcionalidades desde el Inspector.
    /// </summary>
    public class TestingHelper : MonoBehaviour
    {
        [Header("Referencias Automáticas")]
        [SerializeField] private NavigationManager _navigationManager;
        [SerializeField] private WaypointManager _waypointManager;
        [SerializeField] private NavMeshGenerator _navMeshGenerator;
        [SerializeField] private NavigationAgent _navigationAgent;
        [SerializeField] private PlacementController _placementController;

        [Header("Configuración de Testing")]
        [SerializeField] private bool _autoInitialize = true;
        [SerializeField] private Vector3[] _testWaypointPositions = new Vector3[]
        {
            new Vector3(0, 0.5f, 0),
            new Vector3(2, 0.5f, 0),
            new Vector3(0, 0.5f, 2),
            new Vector3(2, 0.5f, 2)
        };

        private void Awake()
        {
            FindComponents();
        }

        private void Start()
        {
            if (_autoInitialize)
            {
                Debug.Log("[TestingHelper] Auto-inicializando en 2 segundos...");
                Invoke(nameof(AutoInitialize), 2f);
            }
        }

        private void FindComponents()
        {
            if (_navigationManager == null)
                _navigationManager = FindFirstObjectByType<NavigationManager>();

            if (_waypointManager == null)
                _waypointManager = FindFirstObjectByType<WaypointManager>();

            if (_navMeshGenerator == null)
                _navMeshGenerator = FindFirstObjectByType<NavMeshGenerator>();

            if (_navigationAgent == null)
                _navigationAgent = FindFirstObjectByType<NavigationAgent>();

            if (_placementController == null)
                _placementController = FindFirstObjectByType<PlacementController>();

            Debug.Log($"[TestingHelper] Componentes encontrados: " +
                      $"NavManager={_navigationManager != null}, " +
                      $"Waypoints={_waypointManager != null}, " +
                      $"NavMesh={_navMeshGenerator != null}, " +
                      $"Agent={_navigationAgent != null}");
        }

        #region Auto Initialize

        private void AutoInitialize()
        {
            Debug.Log("[TestingHelper] Ejecutando auto-inicialización...");
            
            // Simular detección de planos (para testing en Editor)
            SimulatePlaneDetection();
        }

        #endregion

        #region Context Menu - Waypoints

        [ContextMenu("1. Crear Waypoint en Origen (0,0,0)")]
        private void Test_CreateWaypointAtOrigin()
        {
            if (_waypointManager == null)
            {
                Debug.LogError("[TestingHelper] WaypointManager no encontrado.");
                return;
            }

            var waypoint = _waypointManager.CreateWaypoint(Vector3.up * 0.5f, Quaternion.identity);
            
            if (waypoint != null)
            {
                Debug.Log($"[TestingHelper] ✅ Waypoint creado en origen: {waypoint.WaypointId}");
            }
        }

        [ContextMenu("2. Crear 4 Waypoints de Prueba")]
        private void Test_CreateTestWaypoints()
        {
            if (_waypointManager == null)
            {
                Debug.LogError("[TestingHelper] WaypointManager no encontrado.");
                return;
            }

            Debug.Log("[TestingHelper] Creando waypoints de prueba...");

            for (int i = 0; i < _testWaypointPositions.Length; i++)
            {
                var waypoint = _waypointManager.CreateWaypoint(
                    _testWaypointPositions[i], 
                    Quaternion.identity
                );

                if (waypoint != null)
                {
                    waypoint.WaypointName = $"TestPoint_{i + 1}";
                    waypoint.UpdateVisuals();
                }
            }

            Debug.Log($"[TestingHelper] ✅ {_testWaypointPositions.Length} waypoints creados.");
        }

        [ContextMenu("3. Listar Todos los Waypoints")]
        private void Test_ListAllWaypoints()
        {
            if (_waypointManager == null)
            {
                Debug.LogError("[TestingHelper] WaypointManager no encontrado.");
                return;
            }

            Debug.Log($"[TestingHelper] === WAYPOINTS ({_waypointManager.WaypointCount}) ===");
            
            foreach (var waypoint in _waypointManager.Waypoints)
            {
                Debug.Log($"  - {waypoint.WaypointName} at {waypoint.Position}");
            }
        }

        [ContextMenu("4. Limpiar Todos los Waypoints")]
        private void Test_ClearAllWaypoints()
        {
            if (_waypointManager == null)
            {
                Debug.LogError("[TestingHelper] WaypointManager no encontrado.");
                return;
            }

            _waypointManager.ClearAllWaypoints();
            Debug.Log("[TestingHelper] ✅ Waypoints limpiados.");
        }

        #endregion

        #region Context Menu - NavMesh

        [ContextMenu("5. Generar NavMesh (Forzar)")]
        private async void Test_GenerateNavMesh()
        {
            if (_navMeshGenerator == null)
            {
                Debug.LogError("[TestingHelper] NavMeshGenerator no encontrado.");
                return;
            }

            Debug.Log("[TestingHelper] Generando NavMesh...");
            bool success = await _navMeshGenerator.RegenerateNavMesh();

            if (success)
            {
                Debug.Log("[TestingHelper] ✅ NavMesh generado exitosamente.");
            }
            else
            {
                Debug.LogWarning("[TestingHelper] ⚠️ Error generando NavMesh.");
            }
        }

        [ContextMenu("6. Simular Planos AR (Para Editor)")]
        private void Test_SimulatePlaneDetection()
        {
            SimulatePlaneDetection();
        }

        /// <summary>
        /// Simula planos AR para testing en Editor (sin dispositivo real).
        /// Crea planos invisibles para que el NavMeshGenerator pueda trabajar.
        /// </summary>
        private void SimulatePlaneDetection()
        {
            Debug.Log("[TestingHelper] Simulando detección de planos AR...");

            // Crear plano simulado de 5x5 metros
            GameObject simulatedPlane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            simulatedPlane.name = "[Simulated AR Plane]";
            simulatedPlane.transform.position = Vector3.zero;
            simulatedPlane.transform.localScale = new Vector3(0.5f, 1f, 0.5f); // 5x5 metros

            // Hacer invisible (solo para NavMesh)
            var renderer = simulatedPlane.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.enabled = false; // Ocultar visualmente
            }

            // Asegurar que tenga collider para NavMesh
            var collider = simulatedPlane.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = true;
            }

            Debug.Log("[TestingHelper] ✅ Plano simulado creado. Ahora genera NavMesh.");
        }

        #endregion

        #region Context Menu - Navigation

        [ContextMenu("7. Navegar al Primer Waypoint")]
        private void Test_NavigateToFirstWaypoint()
        {
            if (_navigationAgent == null)
            {
                Debug.LogError("[TestingHelper] NavigationAgent no encontrado.");
                return;
            }

            if (_waypointManager == null || _waypointManager.WaypointCount == 0)
            {
                Debug.LogWarning("[TestingHelper] No hay waypoints disponibles. Crea waypoints primero.");
                return;
            }

            var firstWaypoint = _waypointManager.Waypoints[0];
            bool success = _navigationAgent.NavigateToWaypoint(firstWaypoint);

            if (success)
            {
                Debug.Log($"[TestingHelper] ✅ Navegando a: {firstWaypoint.WaypointName}");
            }
            else
            {
                Debug.LogWarning("[TestingHelper] ⚠️ No se pudo iniciar navegación.");
            }
        }

        [ContextMenu("8. Navegar al Último Waypoint")]
        private void Test_NavigateToLastWaypoint()
        {
            if (_navigationAgent == null)
            {
                Debug.LogError("[TestingHelper] NavigationAgent no encontrado.");
                return;
            }

            if (_waypointManager == null || _waypointManager.WaypointCount == 0)
            {
                Debug.LogWarning("[TestingHelper] No hay waypoints disponibles.");
                return;
            }

            var lastWaypoint = _waypointManager.Waypoints[_waypointManager.WaypointCount - 1];
            bool success = _navigationAgent.NavigateToWaypoint(lastWaypoint);

            if (success)
            {
                Debug.Log($"[TestingHelper] ✅ Navegando a: {lastWaypoint.WaypointName}");
            }
        }

        [ContextMenu("9. Detener Navegación")]
        private void Test_StopNavigation()
        {
            if (_navigationAgent == null)
            {
                Debug.LogError("[TestingHelper] NavigationAgent no encontrado.");
                return;
            }

            _navigationAgent.StopNavigation("Testing - Usuario detuvo navegación");
            Debug.Log("[TestingHelper] ✅ Navegación detenida.");
        }

        [ContextMenu("10. Teleportar Agente al Origen")]
        private void Test_TeleportAgentToOrigin()
        {
            if (_navigationAgent == null)
            {
                Debug.LogError("[TestingHelper] NavigationAgent no encontrado.");
                return;
            }

            bool success = _navigationAgent.TeleportTo(Vector3.up * 0.5f);

            if (success)
            {
                Debug.Log("[TestingHelper] ✅ Agente teleportado al origen.");
            }
            else
            {
                Debug.LogWarning("[TestingHelper] ⚠️ No se pudo teleportar (posición no válida en NavMesh).");
            }
        }

        #endregion

        #region Context Menu - Placement

        [ContextMenu("11. Activar Modo Colocación")]
        private void Test_EnablePlacementMode()
        {
            if (_placementController == null)
            {
                Debug.LogError("[TestingHelper] PlacementController no encontrado.");
                return;
            }

            _placementController.TogglePlacementMode(true);
            Debug.Log("[TestingHelper] ✅ Modo colocación ACTIVADO. Toca la pantalla para colocar waypoints.");
        }

        [ContextMenu("12. Desactivar Modo Colocación")]
        private void Test_DisablePlacementMode()
        {
            if (_placementController == null)
            {
                Debug.LogError("[TestingHelper] PlacementController no encontrado.");
                return;
            }

            _placementController.TogglePlacementMode(false);
            Debug.Log("[TestingHelper] ✅ Modo colocación DESACTIVADO.");
        }

        #endregion

        #region Context Menu - System

        [ContextMenu("13. Estado del Sistema")]
        private void Test_SystemStatus()
        {
            Debug.Log("========== ESTADO DEL SISTEMA ==========");
            Debug.Log($"NavigationManager: {(_navigationManager != null ? "✅" : "❌")}");
            Debug.Log($"WaypointManager: {(_waypointManager != null ? "✅" : "❌")}");
            Debug.Log($"  - Waypoints: {_waypointManager?.WaypointCount ?? 0}");
            Debug.Log($"NavMeshGenerator: {(_navMeshGenerator != null ? "✅" : "❌")}");
            Debug.Log($"  - Generando: {_navMeshGenerator?.IsGenerating ?? false}");
            Debug.Log($"NavigationAgent: {(_navigationAgent != null ? "✅" : "❌")}");
            Debug.Log($"  - Navegando: {_navigationAgent?.IsNavigating ?? false}");
            Debug.Log($"PlacementController: {(_placementController != null ? "✅" : "❌")}");
            Debug.Log($"  - Activo: {_placementController?.IsPlacementActive ?? false}");
            Debug.Log("========================================");
        }

        [ContextMenu("14. Reset Sistema Completo")]
        private void Test_ResetSystem()
        {
            if (_navigationManager != null)
            {
                _navigationManager.ResetSystem();
                Debug.Log("[TestingHelper] ✅ Sistema reseteado completamente.");
            }
            else
            {
                Debug.LogError("[TestingHelper] NavigationManager no encontrado.");
            }
        }

        [ContextMenu("15. Quick Setup (Completo)")]
        private async void Test_QuickSetup()
        {
            Debug.Log("[TestingHelper] ========== QUICK SETUP ==========");
            
            // 1. Simular planos
            Debug.Log("[TestingHelper] 1/4 - Simulando planos AR...");
            SimulatePlaneDetection();
            await System.Threading.Tasks.Task.Delay(500);

            // 2. Crear waypoints
            Debug.Log("[TestingHelper] 2/4 - Creando waypoints de prueba...");
            Test_CreateTestWaypoints();
            await System.Threading.Tasks.Task.Delay(500);

            // 3. Generar NavMesh
            Debug.Log("[TestingHelper] 3/4 - Generando NavMesh...");
            await Test_GenerateNavMeshAsync();
            await System.Threading.Tasks.Task.Delay(1000);

            // 4. Teleportar agente
            Debug.Log("[TestingHelper] 4/4 - Posicionando agente...");
            if (_navigationAgent != null)
            {
                _navigationAgent.TeleportTo(new Vector3(-1, 0.5f, -1));
            }

            Debug.Log("[TestingHelper] ========== SETUP COMPLETO ==========");
            Debug.Log("[TestingHelper] ✅ Usa 'Context Menu → 7. Navegar al Primer Waypoint'");
        }

        private async System.Threading.Tasks.Task Test_GenerateNavMeshAsync()
        {
            if (_navMeshGenerator != null)
            {
                await _navMeshGenerator.RegenerateNavMesh();
            }
        }

        #endregion

        #region Debug Gizmos

        private void OnDrawGizmos()
        {
            // Dibujar posiciones de waypoints de prueba
            Gizmos.color = Color.yellow;
            foreach (var pos in _testWaypointPositions)
            {
                Gizmos.DrawWireSphere(pos, 0.2f);
            }
        }

        #endregion
    }
}