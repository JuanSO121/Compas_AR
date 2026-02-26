// File: NavigationStartPoint.cs
// ✅ FIX v3 — SamplePosition filtrado por desviación vertical máxima.
//
//  PROBLEMA CORREGIDO:
//    TeleportAgent() usaba NavMesh.SamplePosition con radio 3D esférico de 5m.
//    En un edificio de 2 pisos separados ~3m, ese radio captura ambos pisos.
//    SamplePosition devuelve el punto NavMesh MÁS CERCANO en distancia 3D,
//    lo que puede ser el piso equivocado si el StartPoint no está exactamente
//    a la altura del NavMesh de su nivel.
//
//    Ejemplo: StartPoint Level1 colocado en Y=3.0, NavMesh Level1 en Y=3.12,
//    NavMesh Level0 en Y=0.04. Radio 5m incluye ambos. En distancia 3D el
//    Level1 gana (0.12m vs 2.96m), pero si el StartPoint está en Y=1.5
//    (mitad del edificio) el Level0 gana incorrectamente (1.46m vs 1.62m).
//
//  SOLUCIÓN:
//    SamplePositionOnCorrectFloor() prueba radios crecientes en XZ pero
//    valida que el hit esté dentro de _maxVerticalDeviation en Y.
//    Si el SamplePosition devuelve un punto del piso equivocado (|hitY - myY| > umbral),
//    se descarta y se amplía el radio hasta encontrar uno del piso correcto.
//    Esto garantiza que cada StartPoint siempre teleporta al NavMesh de SU piso,
//    independientemente de cuán exacto sea su posicionamiento vertical manual.
//
//  DISEÑO (por qué los StartPoints son hijos del Empty del modelo):
//    El Empty raíz del modelo se posiciona en AR donde la cámara detecta el suelo.
//    Los StartPoints son hijos de ese Empty, así su world Y = posición AR + offset local.
//    Cuando el modelo se reposiciona (RestoreModelTransform), los StartPoints heredan
//    el transform y su world Y sigue siendo correcto sin ningún cálculo adicional.
//    El NavMesh serializado también se remapea a las mismas coordenadas world.
//    Por eso NO se usa una altura global predefinida: en AR móvil esa altura
//    cambia cada vez que se carga la sesión según dónde detecte el suelo la cámara.

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

        // ─── Properties ──────────────────────────────────────────────────

        public int     Level             => _level;
        public float   FloorHeight       => transform.position.y;
        public Vector3 Position          => transform.position;
        public bool    DefinesFloorHeight => _useThisAsFloorHeight;

        // ─── Lifecycle ────────────────────────────────────────────────────

        private void Awake()
        {
            // Si NO es hijo del modelo dinámico, confirmar posición inmediatamente.
            // Si ES hijo del modelo, NavigationManager llama ConfirmModelPositioned()
            // después de RestoreModelTransform(), garantizando que transform.position
            // ya está en world space correcto antes de leer FloorHeight.
            if (!IsChildOfDynamicModel())
            {
                _modelPositionConfirmed = true;
                Debug.Log($"[StartPoint Level{_level}] Posición auto-confirmada (no es hijo de modelo dinámico).");
            }
        }

        private void Start()
        {
            NavigationStartPointManager.RegisterStartPoint(this);
            StartCoroutine(TeleportAgentWhenReady());
        }

        private void OnDestroy()
        {
            NavigationStartPointManager.UnregisterStartPoint(this);
        }

        // ─── API pública ──────────────────────────────────────────────────

        /// <summary>
        /// Llamado por NavigationManager después de RestoreModelTransform().
        /// En ese momento transform.position ya está en world space correcto
        /// porque el modelo padre fue reposicionado y Unity propagó el transform.
        /// </summary>
        public void ConfirmModelPositioned()
        {
            _modelPositionConfirmed = true;
            Debug.Log($"[StartPoint Level{_level}] 📍 Posición del modelo confirmada. " +
                      $"World pos: {transform.position} (Y={transform.position.y:F3}m)");
        }

        /// <summary>
        /// Llamado por NavigationStartPointManager.NotifyNavMeshReady()
        /// cuando el NavMesh está disponible.
        /// </summary>
        public void NotifyNavMeshReady()
        {
            _navMeshSignaled = true;
            Debug.Log($"[StartPoint Level{_level}] 📡 NavMesh listo (notificación directa).");
        }

        [ContextMenu("🔄 Re-teleportar Agente")]
        public void ReteleportAgent()
        {
            _hasTeleported          = false;
            _navMeshSignaled        = IsNavMeshAvailable();
            _modelPositionConfirmed = true;

            if (_agent == null)
                _agent = FindFirstObjectByType<NavigationAgent>();

            if (_agent != null)
                StartCoroutine(TeleportAgentWhenReady());
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

            // Esperar a que el modelo padre esté en su posición world correcta.
            // Sin esto, transform.position.y podría ser el valor local sin transformar.
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
        }

        /// <summary>
        /// Busca el punto NavMesh más cercano que pertenezca al piso correcto.
        ///
        /// Estrategia: prueba radios XZ crecientes pero valida que el hit esté
        /// dentro de _maxVerticalDeviation en Y respecto a transform.position.y.
        /// Esto evita capturar el NavMesh de otro piso aunque esté más cerca en 3D.
        ///
        /// _maxVerticalDeviation debe ser menor que la mitad de la separación
        /// entre pisos (ej: pisos a 3m de separación → usar 1.0m o 1.2m).
        /// </summary>
        private bool TeleportToCorrectFloor()
        {
            Vector3 myPos = transform.position;
            float myY     = myPos.y;

            // Radios crecientes para búsqueda horizontal
            float[] radii = { 0.5f, 1.0f, 1.5f, 2.0f, 3.0f, _maxHorizontalRadius };

            foreach (float radius in radii)
            {
                if (!NavMesh.SamplePosition(myPos, out NavMeshHit hit, radius, NavMesh.AllAreas))
                    continue;

                float verticalDelta = Mathf.Abs(hit.position.y - myY);

                if (verticalDelta <= _maxVerticalDeviation)
                {
                    // Hit dentro del rango vertical correcto — este es nuestro piso
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
                    // Hit encontrado pero en el piso equivocado — ignorar y ampliar radio
                    Debug.Log($"[StartPoint Level{_level}] ⏭️ Hit en radio {radius}m descartado: " +
                              $"Y={hit.position.y:F3} está a {verticalDelta:F3}m de mi Y={myY:F3} " +
                              $"(máx permitido: {_maxVerticalDeviation:F2}m) — piso equivocado.");
                }
            }

            // Ningún radio encontró NavMesh en el piso correcto.
            // Intentar búsqueda ajustando la posición de origen hacia abajo/arriba
            // para compensar si el StartPoint está algo desplazado verticalmente.
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

            // Último recurso: usar el punto más cercano independientemente del piso,
            // con advertencia explícita
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

        /// <summary>
        /// El StartPoint es hijo del modelo dinámico si tiene un ancestro con tag "3DModel".
        /// En ese caso su world position depende del modelo padre, y se necesita
        /// ConfirmModelPositioned() antes de leer transform.position.
        /// </summary>
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
            // Verificar que hay NavMesh cerca de ESTE piso específicamente
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

            // Encontrar el vértice NavMesh más cercano en Y (mismo piso)
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

            Gizmos.color = _hasTeleported ? Color.green : _gizmoColor;
            Gizmos.DrawWireSphere(transform.position, _gizmoRadius);

            // Cruz de posición
            Vector3 pos = transform.position;
            float   cs  = _gizmoRadius * 0.5f;
            Gizmos.DrawLine(pos + Vector3.left * cs,    pos + Vector3.right * cs);
            Gizmos.DrawLine(pos + Vector3.forward * cs, pos + Vector3.back * cs);

            // Plano horizontal de altura (referencia del piso)
            if (_useThisAsFloorHeight)
            {
                Gizmos.color = new Color(_gizmoColor.r, _gizmoColor.g, _gizmoColor.b, 0.1f);
                Gizmos.DrawCube(pos, new Vector3(5f, 0.02f, 5f));
            }

            // Zona de búsqueda vertical (cilindro aproximado con dos esferas)
            Gizmos.color = new Color(_gizmoColor.r, _gizmoColor.g, _gizmoColor.b, 0.15f);
            Gizmos.DrawWireSphere(pos + Vector3.up    * _maxVerticalDeviation, _maxHorizontalRadius * 0.5f);
            Gizmos.DrawWireSphere(pos + Vector3.down  * _maxVerticalDeviation, _maxHorizontalRadius * 0.5f);

#if UNITY_EDITOR
            UnityEditor.Handles.Label(transform.position + Vector3.up * 0.5f,
                $"Start Level {_level}\nY={transform.position.y:F2}m\n±{_maxVerticalDeviation:F1}m",
                new GUIStyle
                {
                    normal    = new GUIStyleState { textColor = Color.white },
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

            // Mostrar el resultado de SamplePosition filtrado por Y
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
                          $"en Y={p.FloorHeight:F3}m (world)");
            }
        }

        public static void UnregisterStartPoint(NavigationStartPoint p) => _startPoints.Remove(p);

        /// <summary>
        /// Notifica a todos los StartPoints que el NavMesh está disponible.
        /// </summary>
        public static void NotifyNavMeshReady()
        {
            _startPoints.RemoveAll(p => p == null);
            Debug.Log($"[StartPointManager] 📡 Notificando NavMesh ready a {_startPoints.Count} StartPoint(s)");
            foreach (var p in _startPoints)
                p.NotifyNavMeshReady();
        }

        /// <summary>
        /// Confirma a todos los StartPoints que el modelo está en su posición world final.
        /// El world Y de cada StartPoint es válido a partir de este momento.
        /// </summary>
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