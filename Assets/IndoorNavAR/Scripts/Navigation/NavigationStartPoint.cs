// File: NavigationStartPoint.cs
// ✅ FIX v5 — Solo el nivel 0 teleporta automáticamente al iniciar.
//
//  PROBLEMA CORREGIDO (v4 → v5):
//    Todos los NavigationStartPoint (piso 0, piso 1, etc.) llamaban
//    StartCoroutine(TeleportAgentWhenReady()) en Start(). Cuando
//    NotifyNavMeshReady() llegaba, TODAS las corrutinas avanzaban en
//    paralelo y ejecutaban TeleportToCorrectFloor(). La del piso 1
//    terminaba última y dejaba al agente en el piso incorrecto.
//
//  FIX v5:
//    - Nueva propiedad serializable _autoTeleportOnStart (default true).
//    - En Start(), solo se lanza la corrutina si _autoTeleportOnStart == true.
//    - Los StartPoints de niveles superiores (piso 1, piso 2, …) deben
//      tener _autoTeleportOnStart = false en el Inspector; solo sirven
//      para definir la altura de piso y recibir teleports manuales vía
//      ReteleportAgent().
//    - Se añade propiedad pública AutoTeleportOnStart por si se necesita
//      configurar en runtime (p.ej. desde DefaultWaypointSeeder).
//
//  NOTA SOBRE EL FLUJO NORMAL (sesión guardada):
//    PersistenceManager.RecreateStairGeometryAsync() llama:
//      ConfirmModelPositioned() → NotifyNavMeshReady()
//    Solo el StartPoint nivel 0 (con _autoTeleportOnStart=true) avanza
//    su corrutina hasta TeleportToCorrectFloor(). Los demás están dormidos.
//
//  ReteleportAgent() sigue siendo útil para:
//    - Reposicionamiento manual (botón de reset)
//    - Flujo de nuevo modelo (OnModelLoaded)
//    - Debug desde ContextMenu en el editor

using System.Collections;
using UnityEngine;
using UnityEngine.AI;

namespace IndoorNavAR.Navigation
{
    public class NavigationStartPoint : MonoBehaviour
    {
        [Header("🎯 Nivel y Altura")]
        [SerializeField] private int  _level                = 0;
        [SerializeField] private bool _useThisAsFloorHeight = true;

        [Header("⚙️ Teleport")]
        [SerializeField] private bool  _waitForNavMesh      = true;
        [SerializeField] private float _initialDelay        = 0.5f;
        [SerializeField] private float _navMeshTimeout      = 30f;

        // ✅ FIX v5: Solo el StartPoint del nivel de entrada debe teleportar
        // automáticamente. Los niveles superiores ponen este flag en false.
        [Tooltip("Si true, el agente se teleporta aquí automáticamente al iniciar la escena.\n" +
                 "Debe estar activo SOLO en el nivel de entrada (normalmente nivel 0).\n" +
                 "Desactívalo en los niveles superiores para evitar teleports no deseados.")]
        [SerializeField] private bool _autoTeleportOnStart = true;

        [Header("📐 Búsqueda de NavMesh")]
        [Tooltip("Desviación vertical máxima permitida al buscar NavMesh. " +
                 "Debe ser menor que la mitad de la separación entre pisos.")]
        [SerializeField] private float _maxVerticalDeviation = 1.0f;
        [Tooltip("Radio horizontal máximo para buscar NavMesh en el piso correcto.")]
        [SerializeField] private float _maxHorizontalRadius  = 5.0f;

        [Header("🐛 Debug")]
        [SerializeField] private bool  _showGizmo   = true;
        [SerializeField] private Color _gizmoColor  = Color.green;
        [SerializeField] private float _gizmoRadius = 0.3f;

        private NavigationAgent _agent;
        private bool _hasTeleported           = false;
        private bool _navMeshSignaled         = false;
        private bool _modelPositionConfirmed  = false;

        // FIX v4: Referencia a la corrutina activa para poder cancelarla
        private Coroutine _teleportCoroutine = null;

        // ─── Properties ──────────────────────────────────────────────────

        public int     Level             => _level;
        public float   FloorHeight       => transform.position.y;
        public Vector3 Position          => transform.position;
        public bool    DefinesFloorHeight => _useThisAsFloorHeight;

        // ✅ FIX v5: Exponer para configuración en runtime si fuera necesario
        public bool AutoTeleportOnStart
        {
            get => _autoTeleportOnStart;
            set => _autoTeleportOnStart = value;
        }

        // ─── Lifecycle ────────────────────────────────────────────────────

        private void Awake()
        {
            if (!IsChildOfDynamicModel())
            {
                _modelPositionConfirmed = true;
                Debug.Log($"[StartPoint Level{_level}] Posición auto-confirmada (no es hijo de modelo dinámico).");
            }
        }

        private void Start()
        {
            NavigationStartPointManager.RegisterStartPoint(this);

            // ✅ FIX v5: Solo lanzar corrutina de teleport automático si este
            // StartPoint es el punto de entrada. Los niveles superiores NO deben
            // teleportar al agente automáticamente — solo definen alturas de piso.
            if (_autoTeleportOnStart)
            {
                _teleportCoroutine = StartCoroutine(TeleportAgentWhenReady());
                Debug.Log($"[StartPoint Level{_level}] 🚀 Corrutina de teleport automático iniciada.");
            }
            else
            {
                Debug.Log($"[StartPoint Level{_level}] ⏸ autoTeleportOnStart=false — " +
                          $"este StartPoint no teleporta al agente automáticamente.");
            }
        }

        private void OnDestroy()
        {
            NavigationStartPointManager.UnregisterStartPoint(this);
        }

        // ─── API pública ──────────────────────────────────────────────────

        public void ConfirmModelPositioned()
        {
            _modelPositionConfirmed = true;
            Debug.Log($"[StartPoint Level{_level}] 📍 Posición del modelo confirmada. " +
                      $"World pos: {transform.position} (Y={transform.position.y:F3}m)");
        }

        public void NotifyNavMeshReady()
        {
            _navMeshSignaled = true;
            Debug.Log($"[StartPoint Level{_level}] 📡 NavMesh listo (notificación directa).");
        }

        /// <summary>
        /// Re-teleporta el agente. Cancela la corrutina anterior antes de lanzar una nueva.
        /// Usar solo cuando el NavMesh ya esté disponible (flujo manual / reset / nuevo modelo).
        /// En el flujo de sesión guardada, NavigationManager ya no llama este método —
        /// la corrutina de Start() se resuelve sola vía NotifyNavMeshReady().
        /// </summary>
        [ContextMenu("🔄 Re-teleportar Agente")]
        public void ReteleportAgent()
        {
            // FIX v4: Cancelar corrutina anterior para evitar doble teleport
            if (_teleportCoroutine != null)
            {
                StopCoroutine(_teleportCoroutine);
                _teleportCoroutine = null;
                Debug.Log($"[StartPoint Level{_level}] 🛑 Corrutina anterior cancelada.");
            }

            _hasTeleported          = false;
            _navMeshSignaled        = IsNavMeshAvailable();
            _modelPositionConfirmed = true;

            if (_agent == null)
                _agent = FindFirstObjectByType<NavigationAgent>();

            if (_agent != null)
                _teleportCoroutine = StartCoroutine(TeleportAgentWhenReady());
            else
                Debug.LogWarning($"[StartPoint Level{_level}] ⚠️ ReteleportAgent: NavigationAgent no encontrado.");
        }

        public NavigationStartPointInfo GetInfo() => new NavigationStartPointInfo
        {
            Position           = transform.position,
            Level              = _level,
            FloorHeight        = FloorHeight,
            HasTeleported      = _hasTeleported,
            IsNavMeshReady     = IsNavMeshAvailable(),
            DefinesFloorHeight = _useThisAsFloorHeight
        };

        // ─── Teleport Logic ───────────────────────────────────────────────

        private IEnumerator TeleportAgentWhenReady()
        {
            if (_initialDelay > 0)
                yield return new WaitForSeconds(_initialDelay);

            _agent = FindFirstObjectByType<NavigationAgent>();
            if (_agent == null)
            {
                Debug.LogError($"[StartPoint Level{_level}] ❌ NavigationAgent no encontrado.");
                yield break;
            }

            // Esperar confirmación de posición del modelo
            if (!_modelPositionConfirmed)
            {
                Debug.Log($"[StartPoint Level{_level}] ⏳ Esperando confirmación de posición del modelo...");
                float waited = 0f;
                while (!_modelPositionConfirmed && waited < 10f)
                {
                    yield return new WaitForSeconds(0.2f);
                    waited += 0.2f;
                }

                if (!_modelPositionConfirmed)
                    Debug.LogWarning($"[StartPoint Level{_level}] ⚠️ Timeout esperando posición del modelo. " +
                                     $"Usando transform.position actual: Y={transform.position.y:F3}m");
            }

            // Esperar NavMesh disponible
            if (_waitForNavMesh)
            {
                float elapsed = 0f;
                while (!_navMeshSignaled && !IsNavMeshAvailable() && elapsed < _navMeshTimeout)
                {
                    yield return new WaitForSeconds(0.5f);
                    elapsed += 0.5f;
                    if (elapsed % 3f < 0.6f)
                        Debug.Log($"[StartPoint Level{_level}] ⏳ Esperando NavMesh... {elapsed:F0}s");
                }

                string source = _navMeshSignaled ? "señal directa"
                              : (elapsed >= _navMeshTimeout ? "timeout" : "polling");
                Debug.Log($"[StartPoint Level{_level}] NavMesh detectado vía: {source}");
            }

            // Frame extra para propagación
            yield return new WaitForSeconds(0.3f);

            LogDiagnostics();

            bool success = TeleportToCorrectFloor();
            if (success)
            {
                _hasTeleported = true;
                Debug.Log($"[StartPoint Level{_level}] ✅ Agente teleportado exitosamente.");
            }
            else
            {
                Debug.LogError($"[StartPoint Level{_level}] ❌ Falló teleport. " +
                               $"StartPoint Y={transform.position.y:F3}m, " +
                               $"maxVerticalDeviation={_maxVerticalDeviation}m");
            }

            _teleportCoroutine = null; // limpiar referencia al terminar
        }

        private bool TeleportToCorrectFloor()
        {
            Vector3 myPos = transform.position;
            float myY     = myPos.y;

            float[] radii = { 0.5f, 1.0f, 1.5f, 2.0f, 3.0f, _maxHorizontalRadius };

            foreach (float radius in radii)
            {
                if (!NavMesh.SamplePosition(myPos, out NavMeshHit hit, radius, NavMesh.AllAreas))
                    continue;

                float verticalDelta = Mathf.Abs(hit.position.y - myY);

                if (verticalDelta <= _maxVerticalDeviation)
                {
                    float horizontalDist = Vector2.Distance(
                        new Vector2(myPos.x, myPos.z),
                        new Vector2(hit.position.x, hit.position.z));

                    Debug.Log($"[StartPoint Level{_level}] 📍 NavMesh encontrado con radio {radius}m: " +
                              $"hit={hit.position:F3} " +
                              $"(ΔY={verticalDelta:F3}m, ΔXZ={horizontalDist:F3}m)");

                    bool ok = _agent.TeleportTo(hit.position);
                    if (!ok)
                        Debug.LogWarning($"[StartPoint Level{_level}] ⚠️ TeleportTo rechazado en {hit.position:F3}");
                    return ok;
                }
                else
                {
                    Debug.Log($"[StartPoint Level{_level}] ⏭️ Hit en radio {radius}m descartado: " +
                              $"Y={hit.position.y:F3} está a {verticalDelta:F3}m de mi Y={myY:F3} " +
                              $"(máx permitido: {_maxVerticalDeviation:F2}m) — piso equivocado.");
                }
            }

            Debug.LogWarning($"[StartPoint Level{_level}] 🔍 Búsqueda estándar fallida. " +
                             $"Intentando búsqueda con offsets verticales...");

            float[] verticalOffsets = { -0.1f, 0.1f, -0.3f, 0.3f, -0.5f, 0.5f };
            foreach (float yOffset in verticalOffsets)
            {
                Vector3 adjustedPos = myPos + Vector3.up * yOffset;
                if (NavMesh.SamplePosition(adjustedPos, out NavMeshHit hit2, 2.0f, NavMesh.AllAreas))
                {
                    float verticalDelta = Mathf.Abs(hit2.position.y - myY);
                    if (verticalDelta <= _maxVerticalDeviation)
                    {
                        Debug.Log($"[StartPoint Level{_level}] 📍 Encontrado con offset Y={yOffset:+0.0;-0.0}m: " +
                                  $"hit={hit2.position:F3}");
                        return _agent.TeleportTo(hit2.position);
                    }
                }
            }

            if (NavMesh.SamplePosition(myPos, out NavMeshHit fallback, _maxHorizontalRadius * 2f, NavMesh.AllAreas))
            {
                float verticalDelta = Mathf.Abs(fallback.position.y - myY);
                Debug.LogWarning($"[StartPoint Level{_level}] ⚠️ FALLBACK: usando NavMesh en Y={fallback.position.y:F3} " +
                                 $"(ΔY={verticalDelta:F3}m — puede ser el piso equivocado). " +
                                 $"Ajusta la posición Y del StartPoint Level{_level} para que esté " +
                                 $"más cerca del piso ({myY:F3}m ± {_maxVerticalDeviation:F2}m).");
                return _agent.TeleportTo(fallback.position);
            }

            Debug.LogError($"[StartPoint Level{_level}] ❌ Sin NavMesh accesible desde Y={myY:F3}m " +
                           $"con radio máximo {_maxHorizontalRadius * 2f}m. " +
                           $"Verifica que el NavMesh fue bakeado correctamente para este nivel.");
            return false;
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private bool IsChildOfDynamicModel()
        {
            Transform current = transform.parent;
            while (current != null)
            {
                if (current.CompareTag("3DModel"))
                    return true;
                current = current.parent;
            }
            return false;
        }

        private bool IsNavMeshAvailable()
        {
            NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
            if (tri.vertices.Length == 0) return false;
            return NavMesh.SamplePosition(transform.position, out _, _maxHorizontalRadius, NavMesh.AllAreas);
        }

        // ─── Diagnóstico ──────────────────────────────────────────────────

        private void LogDiagnostics()
        {
            Vector3 myPos = transform.position;
            NavMeshTriangulation tri = NavMesh.CalculateTriangulation();

            if (tri.vertices.Length == 0)
            {
                Debug.LogWarning($"[StartPoint Level{_level}] ⚠️ NavMesh vacío — 0 vértices");
                return;
            }

            float minYDist  = float.MaxValue;
            float minDist3D = float.MaxValue;
            Vector3 closestSameFloor = Vector3.zero;
            Vector3 closest3D        = Vector3.zero;

            foreach (var v in tri.vertices)
            {
                float yDist  = Mathf.Abs(v.y - myPos.y);
                float dist3D = Vector3.Distance(v, myPos);

                if (yDist < minYDist) { minYDist = yDist; closestSameFloor = v; }
                if (dist3D < minDist3D) { minDist3D = dist3D; closest3D = v; }
            }

            Debug.Log($"[StartPoint Level{_level}] 📊 Diagnóstico:" +
                      $"\n  Mi posición: {myPos:F3} (Y={myPos.y:F3}m)" +
                      $"\n  NavMesh total: {tri.vertices.Length} vértices" +
                      $"\n  Más cercano en Y: {closestSameFloor:F3} (ΔY={minYDist:F3}m)" +
                      $"\n  Más cercano 3D:   {closest3D:F3} (dist3D={minDist3D:F3}m)" +
                      $"\n  maxVerticalDeviation: {_maxVerticalDeviation:F2}m" +
                      $"\n  {(minYDist <= _maxVerticalDeviation ? "✅ NavMesh de este piso alcanzable" : "❌ NavMesh más cercano en Y está fuera del umbral")}");
        }

        // ─── Gizmos ───────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (!_showGizmo) return;

            // ✅ FIX v5: Color diferente si autoTeleport está desactivado
            Gizmos.color = _hasTeleported ? Color.green
                         : _autoTeleportOnStart ? _gizmoColor
                         : new Color(_gizmoColor.r, _gizmoColor.g, _gizmoColor.b, 0.4f);

            Gizmos.DrawWireSphere(transform.position, _gizmoRadius);

            Vector3 pos = transform.position;
            float   cs  = _gizmoRadius * 0.5f;
            Gizmos.DrawLine(pos + Vector3.left * cs,    pos + Vector3.right * cs);
            Gizmos.DrawLine(pos + Vector3.forward * cs, pos + Vector3.back * cs);

            if (_useThisAsFloorHeight)
            {
                Gizmos.color = new Color(_gizmoColor.r, _gizmoColor.g, _gizmoColor.b, 0.1f);
                Gizmos.DrawCube(pos, new Vector3(5f, 0.02f, 5f));
            }

            Gizmos.color = new Color(_gizmoColor.r, _gizmoColor.g, _gizmoColor.b, 0.15f);
            Gizmos.DrawWireSphere(pos + Vector3.up    * _maxVerticalDeviation, _maxHorizontalRadius * 0.5f);
            Gizmos.DrawWireSphere(pos + Vector3.down  * _maxVerticalDeviation, _maxHorizontalRadius * 0.5f);

#if UNITY_EDITOR
            UnityEditor.Handles.Label(transform.position + Vector3.up * 0.5f,
                $"Start Level {_level}\nY={transform.position.y:F2}m\n±{_maxVerticalDeviation:F1}m" +
                $"\n{(_autoTeleportOnStart ? "AUTO ✅" : "manual only")}",
                new GUIStyle
                {
                    normal    = new GUIStyleState { textColor = _autoTeleportOnStart ? Color.white : Color.gray },
                    fontSize  = 11,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter
                });
#endif
        }

        private void OnDrawGizmosSelected()
        {
            if (!_showGizmo) return;

            Vector3 myPos = transform.position;

            bool foundCorrectFloor = false;
            float[] radii = { 0.5f, 1.0f, 1.5f, 2.0f, 3.0f, _maxHorizontalRadius };
            foreach (float r in radii)
            {
                if (NavMesh.SamplePosition(myPos, out NavMeshHit hit, r, NavMesh.AllAreas))
                {
                    float yDelta = Mathf.Abs(hit.position.y - myPos.y);
                    if (yDelta <= _maxVerticalDeviation)
                    {
                        Gizmos.color = Color.cyan;
                        Gizmos.DrawLine(myPos, hit.position);
                        Gizmos.DrawSphere(hit.position, 0.08f);
#if UNITY_EDITOR
                        UnityEditor.Handles.Label(hit.position + Vector3.up * 0.2f,
                            $"NavMesh Y={hit.position.y:F2}m\n(ΔY={yDelta:F3}m, r={r}m)",
                            new GUIStyle { normal = new GUIStyleState { textColor = Color.cyan }, fontSize = 10 });
#endif
                        foundCorrectFloor = true;
                        break;
                    }
                }
            }

            if (!foundCorrectFloor)
            {
#if UNITY_EDITOR
                UnityEditor.Handles.Label(myPos + Vector3.up * 0.7f,
                    $"⚠️ Sin NavMesh en Y={myPos.y:F2}m\n±{_maxVerticalDeviation:F1}m",
                    new GUIStyle
                    {
                        normal    = new GUIStyleState { textColor = Color.red },
                        fontSize  = 11,
                        fontStyle = FontStyle.Bold
                    });
#endif
            }
        }
    }

    // ─── Data Structures ──────────────────────────────────────────────────

    public struct NavigationStartPointInfo
    {
        public Vector3 Position;
        public int     Level;
        public float   FloorHeight;
        public bool    HasTeleported;
        public bool    IsNavMeshReady;
        public bool    DefinesFloorHeight;
    }

    // ─── Manager estático ─────────────────────────────────────────────────

    public static class NavigationStartPointManager
    {
        private static System.Collections.Generic.List<NavigationStartPoint> _startPoints =
            new System.Collections.Generic.List<NavigationStartPoint>();

        public static void RegisterStartPoint(NavigationStartPoint p)
        {
            if (!_startPoints.Contains(p))
            {
                _startPoints.Add(p);
                Debug.Log($"[StartPointManager] ✅ Registrado Level {p.Level} " +
                          $"en Y={p.FloorHeight:F3}m (world) | autoTeleport={p.AutoTeleportOnStart}");
            }
        }

        public static void UnregisterStartPoint(NavigationStartPoint p) => _startPoints.Remove(p);

        public static void NotifyNavMeshReady()
        {
            _startPoints.RemoveAll(p => p == null);
            Debug.Log($"[StartPointManager] 📡 Notificando NavMesh ready a {_startPoints.Count} StartPoint(s)");
            foreach (var p in _startPoints)
                p.NotifyNavMeshReady();
        }

        public static void ConfirmModelPositioned()
        {
            _startPoints.RemoveAll(p => p == null);
            Debug.Log($"[StartPointManager] 📍 Confirmando posición del modelo a {_startPoints.Count} StartPoint(s)");
            foreach (var p in _startPoints)
                p.ConfirmModelPositioned();
        }

        public static bool TryGetFloorHeight(int level, out float h)
        {
            h = 0f;
            foreach (var p in _startPoints)
                if (p != null && p.Level == level && p.DefinesFloorHeight)
                { h = p.FloorHeight; return true; }
            return false;
        }

        public static System.Collections.Generic.List<NavigationStartPoint> GetAllStartPoints()
        {
            _startPoints.RemoveAll(p => p == null);
            var s = new System.Collections.Generic.List<NavigationStartPoint>(_startPoints);
            s.Sort((a, b) => a.Level.CompareTo(b.Level));
            return s;
        }

        public static NavigationStartPoint GetStartPointForLevel(int level)
        {
            foreach (var p in _startPoints)
                if (p != null && p.Level == level) return p;
            return null;
        }

        public static int GetLevelCount()
        {
            _startPoints.RemoveAll(p => p == null);
            var u = new System.Collections.Generic.HashSet<int>();
            foreach (var p in _startPoints) u.Add(p.Level);
            return u.Count;
        }

        public static void ClearAll() => _startPoints.Clear();
    }
}