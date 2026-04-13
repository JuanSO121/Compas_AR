// File: DefaultWaypointSeeder.cs
//
// Crea waypoints por defecto en cada NavigationStartPoint cuando el
// sistema arranca por primera vez (sin waypoints guardados) o cuando
// los waypoints cargados de sesión no incluyen ninguno en esas posiciones.
//
// DISEÑO:
//   - Se ejecuta después de que WaypointsBatchLoadedEvent se publica,
//     tanto en carga de sesión guardada como en arranque sin sesión.
//   - También se suscribe a NavMesh ready para el flujo de primer uso
//     (sin sesión guardada), donde los waypoints deben crearse una vez
//     que el agente está posicionado.
//   - Para cada nivel configurado en _levelConfigs, verifica si ya existe
//     un waypoint dentro de _searchRadius alrededor del StartPoint.
//     Si no existe, lo crea con el nombre e ícono configurados.
//   - Si WaypointManager ya tiene waypoints (sesión cargada), omite la
//     creación de los que ya estén cubiertos por posición próxima.
//
// CONFIGURACIÓN EN INSPECTOR:
//   Añadir este componente a cualquier GO persistente (ej. NavigationManager).
//   Configurar _levelConfigs con un entry por piso:
//     Level 0 → "Entrada / Salida"   tipo Entrance
//     Level 1 → "Habitación 2° piso" tipo Bedroom (o el tipo que prefieras)
//
// NOMBRE DE LOS WAYPOINTS POR DEFECTO:
//   Piso 0 (nivel de entrada): "Entrada / Salida"
//   Piso 1 (nivel superior):   "Habitación 2° Piso"

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Navigation;

namespace IndoorNavAR.Core
{
    public class DefaultWaypointSeeder : MonoBehaviour
    {
        // ─── Config por nivel ─────────────────────────────────────────────

        [Serializable]
        public class LevelWaypointConfig
        {
            [Tooltip("Nivel del NavigationStartPoint donde crear el waypoint.")]
            public int level = 0;

            [Tooltip("Nombre visible del waypoint.")]
            public string waypointName = "Entrada / Salida";

            [Tooltip("Tipo de waypoint (afecta color por defecto).")]
            public WaypointType waypointType = WaypointType.Entrance;

            [Tooltip("Si true, usa el color por defecto del tipo. Si false, usa customColor.")]
            public bool useTypeDefaultColor = true;

            [Tooltip("Color personalizado (solo si useTypeDefaultColor=false).")]
            public Color customColor = Color.cyan;
        }

        [Header("⚙️ Configuración de Waypoints por Defecto")]
        [SerializeField]
        private List<LevelWaypointConfig> _levelConfigs = new List<LevelWaypointConfig>
        {
            new LevelWaypointConfig
            {
                level           = 0,
                waypointName    = "Entrada / Salida",
                waypointType    = WaypointType.Entrance,
                useTypeDefaultColor = true,
            },
            new LevelWaypointConfig
            {
                level           = 1,
                waypointName    = "Habitación 2° Piso",
                waypointType    = WaypointType.Bedroom,
                useTypeDefaultColor = true,
            }
        };

        [Tooltip("Radio (metros) alrededor del StartPoint para considerar que ya existe un waypoint ahí.")]
        [SerializeField] private float _searchRadius = 1.5f;

        [Tooltip("Frames de espera después del evento antes de intentar crear waypoints.")]
        [SerializeField] private int _delayFrames = 3;

        [Header("📦 Referencias")]
        [SerializeField] private WaypointManager _waypointManager;

        [Header("🐛 Debug")]
        [SerializeField] private bool _logSeeding = true;

        private bool _seeded = false;

        // ─── Lifecycle ────────────────────────────────────────────────────

        private void Awake()
        {
            _waypointManager ??= FindFirstObjectByType<WaypointManager>();
        }

        private void OnEnable()
        {
            // Escuchar fin de carga batch (sesión guardada con waypoints)
            EventBus.Instance?.Subscribe<WaypointsBatchLoadedEvent>(OnWaypointsBatchLoaded);
        }

        private void OnDisable()
        {
            EventBus.Instance?.Unsubscribe<WaypointsBatchLoadedEvent>(OnWaypointsBatchLoaded);
        }

        // ─── Evento de carga batch ────────────────────────────────────────

        private void OnWaypointsBatchLoaded(WaypointsBatchLoadedEvent evt)
        {
            // Este evento llega tanto si se cargaron 0 waypoints (primer uso)
            // como si se cargaron N waypoints de sesión guardada.
            // En ambos casos, verificamos si faltan los waypoints por defecto.
            Log($"📦 WaypointsBatchLoaded recibido (count={evt.Count}) — verificando waypoints por defecto...");
            StartCoroutine(SeedAfterFrames());
        }

        // ─── API pública ──────────────────────────────────────────────────

        /// <summary>
        /// Fuerza la siembra de waypoints por defecto independientemente del estado actual.
        /// Útil para llamar después de borrar todos los waypoints manualmente.
        /// </summary>
        [ContextMenu("🌱 Forzar waypoints por defecto")]
        public void ForceSeed()
        {
            _seeded = false;
            StartCoroutine(SeedAfterFrames());
        }

        /// <summary>
        /// Llama este método desde código externo (ej. PersistenceManager o NavigationManager)
        /// si el flujo no pasa por WaypointsBatchLoadedEvent (por ejemplo, en primer uso sin
        /// sesión donde el NavMesh aún no estaba listo cuando se procesaron los waypoints).
        /// </summary>
        public void TriggerSeed()
        {
            if (!_seeded)
                StartCoroutine(SeedAfterFrames());
        }

        // ─── Lógica principal ─────────────────────────────────────────────

        private IEnumerator SeedAfterFrames()
        {
            for (int i = 0; i < _delayFrames; i++)
                yield return null;

            SeedDefaultWaypoints();
        }

        private void SeedDefaultWaypoints()
        {
            if (_waypointManager == null)
            {
                Debug.LogWarning("[DefaultWaypointSeeder] ⚠️ WaypointManager no encontrado.");
                return;
            }

            int created = 0;
            int skipped = 0;

            foreach (var config in _levelConfigs)
            {
                var startPoint = NavigationStartPointManager.GetStartPointForLevel(config.level);

                if (startPoint == null)
                {
                    Log($"  ⚠️ Level {config.level}: NavigationStartPoint no encontrado — omitiendo.");
                    skipped++;
                    continue;
                }

                Vector3 targetPos = startPoint.Position;

                // Verificar si ya existe un waypoint cerca de este StartPoint
                if (WaypointExistsNear(targetPos))
                {
                    Log($"  ✅ Level {config.level}: ya existe un waypoint cerca de {targetPos:F2} — omitiendo '{config.waypointName}'.");
                    skipped++;
                    continue;
                }

                // Crear el waypoint
                Color color = config.useTypeDefaultColor
                    ? WaypointData.GetDefaultColorForType(config.waypointType)
                    : config.customColor;

                WaypointData wp = _waypointManager.CreateConfiguredWaypoint(
                    targetPos,
                    Quaternion.identity,
                    config.waypointName,
                    config.waypointType,
                    color);

                if (wp != null)
                {
                    created++;
                    Log($"  🌱 Level {config.level}: creado waypoint '{config.waypointName}' en {targetPos:F2} (tipo={config.waypointType}).");
                }
                else
                {
                    skipped++;
                    Log($"  ⚠️ Level {config.level}: CreateConfiguredWaypoint retornó null para '{config.waypointName}'.");
                }
            }

            Log($"🌱 Siembra completada: {created} creados, {skipped} omitidos.");

            if (created > 0)
                _seeded = true;
        }

        /// <summary>
        /// Devuelve true si hay al menos un waypoint dentro de _searchRadius de worldPos.
        /// </summary>
        private bool WaypointExistsNear(Vector3 worldPos)
        {
            if (_waypointManager == null) return false;

            foreach (var wp in _waypointManager.Waypoints)
            {
                if (wp == null) continue;
                if (Vector3.Distance(wp.Position, worldPos) <= _searchRadius)
                    return true;
            }
            return false;
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private void Log(string msg)
        {
            if (_logSeeding)
                Debug.Log($"[DefaultWaypointSeeder] {msg}");
        }
    }

    // ─── Evento auxiliar (si no existe en el proyecto) ────────────────────
    // WaypointsBatchLoadedEvent ya debe existir en IndoorNavAR.Core.Events.
    // Si no está declarado, agrégalo aquí o en el archivo de eventos correspondiente.
    // Ejemplo:
    //   public class WaypointsBatchLoadedEvent { public int Count; }
}