// File: KeyboardTestingController.cs

using UnityEngine;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Navigation;
using IndoorNavAR.Core.Controllers;
using UnityEngine.InputSystem;

namespace IndoorNavAR.Testing
{
    /// <summary>
    /// Controlador de testing mediante teclado para probar el sistema sin UI.
    /// Permite crear waypoints, generar NavMesh y navegar usando teclas.
    /// ✅ ACTUALIZADO: Usa MultiLevelNavMeshGenerator
    /// </summary>
    public class KeyboardTestingController : MonoBehaviour
    {
        [Header("Referencias")]
        [SerializeField] private WaypointManager _waypointManager;
        [SerializeField] private MultiLevelNavMeshGenerator _navMeshGenerator;
        [SerializeField] private NavigationAgent _navigationAgent;
        [SerializeField] private PlacementController _placementController;

        [Header("Configuración")]
        [SerializeField] private bool _showInstructions = true;
        private int _currentWaypointIndex = 0;
        private bool _instructionsShown = false;

        private void Awake()
        {
            FindComponents();
        }

        private void Start()
        {
            if (_showInstructions)
            {
                ShowKeyboardInstructions();
            }
        }

        private void Update()
        {
            HandleKeyboardInput();
        }

        private void FindComponents()
        {
            if (_waypointManager == null)
                _waypointManager = FindFirstObjectByType<WaypointManager>();

            if (_navMeshGenerator == null)
                _navMeshGenerator = FindFirstObjectByType<MultiLevelNavMeshGenerator>();

            if (_navigationAgent == null)
                _navigationAgent = FindFirstObjectByType<NavigationAgent>();

            if (_placementController == null)
                _placementController = FindFirstObjectByType<PlacementController>();
        }

private void HandleKeyboardInput()
{
    if (Keyboard.current == null)
        return;

    if (Keyboard.current.wKey.wasPressedThisFrame)
        CreateWaypointAtRandomPosition();

    if (Keyboard.current.nKey.wasPressedThisFrame)
        GenerateNavMesh();

    if (Keyboard.current.spaceKey.wasPressedThisFrame)
        NavigateToNextWaypoint();

    if (Keyboard.current.sKey.wasPressedThisFrame)
        StopNavigation();

    if (Keyboard.current.cKey.wasPressedThisFrame)
        ClearWaypoints();

    if (Keyboard.current.digit1Key.wasPressedThisFrame)
        CreateTestGrid();

    if (Keyboard.current.tKey.wasPressedThisFrame)
        TeleportAgentToOrigin();

    if (Keyboard.current.hKey.wasPressedThisFrame)
        ShowKeyboardInstructions();

    if (Keyboard.current.pKey.wasPressedThisFrame)
        TogglePlacementMode();

    if (Keyboard.current.iKey.wasPressedThisFrame)
        ShowSystemInfo();

    if (Keyboard.current.xKey.wasPressedThisFrame)
        ClearNavMesh();
}
        #region Keyboard Actions

        private void CreateWaypointAtRandomPosition()
        {
            if (_waypointManager == null)
            {
                Debug.LogError("[KeyboardTesting] WaypointManager no encontrado.");
                return;
            }

            // Crear waypoint en posición aleatoria dentro de un radio
            Vector3 randomPos = new Vector3(
                Random.Range(-3f, 3f),
                0.5f,
                Random.Range(-3f, 3f)
            );

            var waypoint = _waypointManager.CreateWaypoint(randomPos, Quaternion.identity);

            if (waypoint != null)
            {
                waypoint.WaypointName = $"Waypoint_{_waypointManager.WaypointCount}";
                waypoint.UpdateVisuals();
                Debug.Log($"✅ [W] Waypoint creado: {waypoint.WaypointName} at {randomPos}");
            }
        }

        private async void GenerateNavMesh()
        {
            if (_navMeshGenerator == null)
            {
                Debug.LogError("[KeyboardTesting] MultiLevelNavMeshGenerator no encontrado.");
                return;
            }

            Debug.Log("🔧 [N] Generando NavMesh Multi-Nivel...");
            bool success = await _navMeshGenerator.GenerateMultiLevelNavMeshAsync();

            if (success)
            {
                Debug.Log("✅ [N] NavMesh multi-nivel generado exitosamente!");
            }
            else
            {
                Debug.LogWarning("⚠️ [N] Error generando NavMesh.");
            }
        }

        private void ClearNavMesh()
        {
            if (_navMeshGenerator == null)
            {
                Debug.LogError("[KeyboardTesting] MultiLevelNavMeshGenerator no encontrado.");
                return;
            }

            _navMeshGenerator.Clear();
            Debug.Log("🧹 [X] NavMesh limpiado.");
        }

        private void NavigateToNextWaypoint()
        {
            if (_navigationAgent == null)
            {
                Debug.LogError("[KeyboardTesting] NavigationAgent no encontrado.");
                return;
            }

            if (_waypointManager == null || _waypointManager.WaypointCount == 0)
            {
                Debug.LogWarning("⚠️ [Space] No hay waypoints. Presiona [W] para crear uno.");
                return;
            }

            // Navegar al siguiente waypoint (circular)
            _currentWaypointIndex = (_currentWaypointIndex + 1) % _waypointManager.WaypointCount;
            var targetWaypoint = _waypointManager.Waypoints[_currentWaypointIndex];

            bool success = _navigationAgent.NavigateToWaypoint(targetWaypoint);

            if (success)
            {
                Debug.Log($"🚀 [Space] Navegando a: {targetWaypoint.WaypointName} ({_currentWaypointIndex + 1}/{_waypointManager.WaypointCount})");
            }
            else
            {
                Debug.LogWarning("⚠️ [Space] No se pudo iniciar navegación. ¿NavMesh generado?");
            }
        }

        private void StopNavigation()
        {
            if (_navigationAgent == null)
                return;

            _navigationAgent.StopNavigation("Usuario detuvo navegación (tecla S)");
            Debug.Log("🛑 [S] Navegación detenida.");
        }

        private void ClearWaypoints()
        {
            if (_waypointManager == null)
                return;

            int count = _waypointManager.WaypointCount;
            _waypointManager.ClearAllWaypoints();
            _currentWaypointIndex = 0;
            Debug.Log($"🗑️ [C] {count} waypoints eliminados.");
        }

        private void CreateTestGrid()
        {
            if (_waypointManager == null)
                return;

            Debug.Log("📐 [1] Creando grid 2x2 de waypoints...");

            Vector3[] positions = new Vector3[]
            {
                new Vector3(-1.5f, 0.5f, -1.5f),
                new Vector3(1.5f, 0.5f, -1.5f),
                new Vector3(-1.5f, 0.5f, 1.5f),
                new Vector3(1.5f, 0.5f, 1.5f)
            };

            for (int i = 0; i < positions.Length; i++)
            {
                var waypoint = _waypointManager.CreateWaypoint(positions[i], Quaternion.identity);
                if (waypoint != null)
                {
                    waypoint.WaypointName = $"GridPoint_{i + 1}";
                    waypoint.UpdateVisuals();
                }
            }

            Debug.Log($"✅ [1] 4 waypoints creados en grid.");
        }

        private void TeleportAgentToOrigin()
        {
            if (_navigationAgent == null)
                return;

            bool success = _navigationAgent.TeleportTo(new Vector3(0, 0.5f, 0));

            if (success)
            {
                Debug.Log("📍 [T] Agente teleportado al origen.");
            }
            else
            {
                Debug.LogWarning("⚠️ [T] No se pudo teleportar (posición no válida).");
            }
        }

        private void TogglePlacementMode()
        {
            if (_placementController == null)
                return;

            bool newState = !_placementController.IsPlacementActive;
            _placementController.TogglePlacementMode(newState);

            Debug.Log($"👆 [P] Modo Placement: {(newState ? "ACTIVADO" : "DESACTIVADO")}");
        }

        private void ShowSystemInfo()
        {
            Debug.Log("========== INFO DEL SISTEMA ==========");
            Debug.Log($"Waypoints: {_waypointManager?.WaypointCount ?? 0}");
            Debug.Log($"Agente Navegando: {_navigationAgent?.IsNavigating ?? false}");
            Debug.Log($"Placement Activo: {_placementController?.IsPlacementActive ?? false}");
            
            if (_navigationAgent != null && _navigationAgent.IsNavigating)
            {
                Debug.Log($"Distancia Restante: {_navigationAgent.DistanceToDestination:F2}m");
                Debug.Log($"Progreso: {_navigationAgent.ProgressPercent * 100:F0}%");
            }
            
            Debug.Log("====================================");
        }

        private void ShowKeyboardInstructions()
        {
            if (_instructionsShown && !Keyboard.current.hKey.isPressed)
                return;

            _instructionsShown = true;

            Debug.Log("╔═══════════════════════════════════════════════╗");
            Debug.Log("║   CONTROLES DE TECLADO - TESTING              ║");
            Debug.Log("╠═══════════════════════════════════════════════╣");
            Debug.Log("║  [W]       - Crear Waypoint aleatorio         ║");
            Debug.Log("║  [1]       - Crear grid 2x2 de waypoints      ║");
            Debug.Log("║  [N]       - Generar NavMesh Multi-Nivel      ║");
            Debug.Log("║  [X]       - Limpiar NavMesh                  ║");
            Debug.Log("║  [SPACE]   - Navegar al siguiente waypoint    ║");
            Debug.Log("║  [S]       - Detener navegación               ║");
            Debug.Log("║  [T]       - Teleportar agente al origen      ║");
            Debug.Log("║  [C]       - Limpiar todos los waypoints      ║");
            Debug.Log("║  [P]       - Toggle modo Placement            ║");
            Debug.Log("║  [I]       - Mostrar info del sistema         ║");
            Debug.Log("║  [H]       - Mostrar esta ayuda               ║");
            Debug.Log("╠═══════════════════════════════════════════════╣");
            Debug.Log("║  FLUJO RÁPIDO:                                ║");
            Debug.Log("║  1. Presiona [1] para crear waypoints         ║");
            Debug.Log("║  2. Presiona [N] para generar NavMesh         ║");
            Debug.Log("║  3. Presiona [SPACE] para navegar             ║");
            Debug.Log("╚═══════════════════════════════════════════════╝");
        }

        #endregion

        #region GUI Display (Opcional)

        private void OnGUI()
        {
            if (!_showInstructions)
                return;

            // Mostrar controles en pantalla
            GUIStyle style = new GUIStyle();
            style.fontSize = 14;
            style.normal.textColor = Color.white;
            style.padding = new RectOffset(10, 10, 10, 10);

            string info = "CONTROLES:\n" +
                         "[W] Waypoint | [1] Grid | [N] NavMesh Multi-Nivel | [X] Clear\n" +
                         "[SPACE] Navegar | [S] Stop | [I] Info | [H] Ayuda\n" +
                         "[C] Limpiar | [P] Placement\n\n";

            if (_waypointManager != null)
            {
                info += $"Waypoints: {_waypointManager.WaypointCount}\n";
            }

            if (_navigationAgent != null && _navigationAgent.IsNavigating)
            {
                info += $"Navegando... {_navigationAgent.DistanceToDestination:F1}m restantes\n";
            }

            GUI.Label(new Rect(10, 10, 500, 150), info, style);
        }

        #endregion
    }
}