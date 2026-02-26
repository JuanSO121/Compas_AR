// File: NavMeshWallExclusionZone.cs
// ✅ Marca zonas donde NO se deben generar wall obstacles
// Útil para aperturas problemáticas, vidrios, etc.

using UnityEngine;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Zona de exclusión para wall obstacles.
    /// Las paredes detectadas dentro de esta zona NO se convertirán en obstáculos.
    /// Los furniture obstacles NO se ven afectados.
    /// </summary>
    [ExecuteInEditMode]
    public class NavMeshWallExclusionZone : MonoBehaviour
    {
        [Header("🚫 Configuración de Exclusión")]
        [Tooltip("Tamaño del volumen de exclusión")]
        [SerializeField] private Vector3 _zoneSize = new Vector3(2f, 2.5f, 0.5f);
        
        [Tooltip("Offset del centro de la zona")]
        [SerializeField] private Vector3 _zoneOffset = Vector3.zero;
        
        [Tooltip("Tipo de zona de exclusión")]
        [SerializeField] private ExclusionType _exclusionType = ExclusionType.WallsOnly;
        
        [Header("🎨 Visualización")]
        [SerializeField] private bool _showGizmo = true;
        [SerializeField] private Color _gizmoColor = new Color(1f, 0f, 0f, 0.3f);
        [SerializeField] private bool _showWireframe = true;

        private BoxCollider _trigger;

        public enum ExclusionType
        {
            WallsOnly,          // Solo ignora walls (default)
            FurnitureOnly,      // Solo ignora furniture
            AllObstacles        // Ignora walls Y furniture
        }

        #region Unity Lifecycle

        private void OnEnable()
        {
            // Asegurar que tenga BoxCollider como trigger
            EnsureTriggerCollider();
            
            // Registrar en el sistema global
            NavMeshWallExclusionManager.RegisterZone(this);
        }

        private void OnDisable()
        {
            // Desregistrar del sistema global
            NavMeshWallExclusionManager.UnregisterZone(this);
        }

        private void OnValidate()
        {
            // Actualizar collider cuando se cambian valores en Inspector
            EnsureTriggerCollider();
        }

        #endregion

        #region Trigger Setup

        private void EnsureTriggerCollider()
        {
            if (_trigger == null)
            {
                _trigger = GetComponent<BoxCollider>();
                
                if (_trigger == null)
                {
                    _trigger = gameObject.AddComponent<BoxCollider>();
                }
            }

            // Configurar como trigger
            _trigger.isTrigger = true;
            _trigger.size = _zoneSize;
            _trigger.center = _zoneOffset;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Verifica si un punto está dentro de la zona de exclusión
        /// </summary>
        public bool ContainsPoint(Vector3 worldPoint)
        {
            // Convertir punto a espacio local
            Vector3 localPoint = transform.InverseTransformPoint(worldPoint);
            localPoint -= _zoneOffset;

            // Verificar si está dentro del box
            return Mathf.Abs(localPoint.x) <= _zoneSize.x / 2f &&
                   Mathf.Abs(localPoint.y) <= _zoneSize.y / 2f &&
                   Mathf.Abs(localPoint.z) <= _zoneSize.z / 2f;
        }

        /// <summary>
        /// Verifica si un bounds intersecta con la zona de exclusión
        /// </summary>
        public bool IntersectsBounds(Bounds worldBounds)
        {
            // Crear bounds de la zona en world space
            Bounds zoneBounds = new Bounds(
                transform.TransformPoint(_zoneOffset),
                _zoneSize
            );

            // Verificar intersección
            return zoneBounds.Intersects(worldBounds);
        }

        /// <summary>
        /// Verifica si debe excluir walls
        /// </summary>
        public bool ShouldExcludeWalls()
        {
            return _exclusionType == ExclusionType.WallsOnly || 
                   _exclusionType == ExclusionType.AllObstacles;
        }

        /// <summary>
        /// Verifica si debe excluir furniture
        /// </summary>
        public bool ShouldExcludeFurniture()
        {
            return _exclusionType == ExclusionType.FurnitureOnly || 
                   _exclusionType == ExclusionType.AllObstacles;
        }

        /// <summary>
        /// Obtener bounds en world space
        /// </summary>
        public Bounds GetWorldBounds()
        {
            Vector3 worldCenter = transform.TransformPoint(_zoneOffset);
            Vector3 worldSize = Vector3.Scale(_zoneSize, transform.lossyScale);
            
            return new Bounds(worldCenter, worldSize);
        }

        #endregion

        #region Helper Methods

        [ContextMenu("📏 Ajustar a Renderer")]
        private void FitToRenderer()
        {
            Renderer renderer = GetComponent<Renderer>();
            
            if (renderer != null)
            {
                _zoneSize = renderer.bounds.size;
                _zoneOffset = transform.InverseTransformPoint(renderer.bounds.center);
                
                EnsureTriggerCollider();
                
                Debug.Log($"[ExclusionZone] ✅ Ajustado a renderer: size={_zoneSize}");
            }
            else
            {
                Debug.LogWarning("[ExclusionZone] ⚠️ No hay Renderer en este GameObject");
            }
        }

        [ContextMenu("📐 Crear desde Selección")]
        private void CreateFromSelection()
        {
            #if UNITY_EDITOR
            // Obtener bounds combinados de todos los objetos seleccionados
            GameObject[] selected = UnityEditor.Selection.gameObjects;
            
            if (selected.Length == 0)
            {
                Debug.LogWarning("[ExclusionZone] ⚠️ No hay objetos seleccionados");
                return;
            }

            Bounds combinedBounds = new Bounds(selected[0].transform.position, Vector3.zero);
            
            foreach (GameObject obj in selected)
            {
                Renderer r = obj.GetComponent<Renderer>();
                if (r != null)
                {
                    combinedBounds.Encapsulate(r.bounds);
                }
            }

            _zoneSize = combinedBounds.size;
            transform.position = combinedBounds.center;
            _zoneOffset = Vector3.zero;
            
            EnsureTriggerCollider();
            
            Debug.Log($"[ExclusionZone] ✅ Creado desde {selected.Length} objetos: size={_zoneSize}");
            #endif
        }

        #endregion

        #region Gizmos

        private void OnDrawGizmos()
        {
            if (!_showGizmo) return;

            DrawZoneGizmo(false);
        }

        private void OnDrawGizmosSelected()
        {
            if (!_showGizmo) return;

            DrawZoneGizmo(true);
        }

        private void DrawZoneGizmo(bool selected)
        {
            Vector3 worldCenter = transform.TransformPoint(_zoneOffset);
            
            // Color base
            Color color = selected ? Color.red : _gizmoColor;
            
            // Box sólido
            Gizmos.color = color;
            Gizmos.matrix = Matrix4x4.TRS(worldCenter, transform.rotation, Vector3.one);
            Gizmos.DrawCube(Vector3.zero, _zoneSize);
            Gizmos.matrix = Matrix4x4.identity;
            
            // Wireframe
            if (_showWireframe)
            {
                Gizmos.color = new Color(color.r, color.g, color.b, 1f);
                Gizmos.matrix = Matrix4x4.TRS(worldCenter, transform.rotation, Vector3.one);
                Gizmos.DrawWireCube(Vector3.zero, _zoneSize);
                Gizmos.matrix = Matrix4x4.identity;
            }

            #if UNITY_EDITOR
            if (selected)
            {
                // Etiqueta con tipo de exclusión
                string label = _exclusionType switch
                {
                    ExclusionType.WallsOnly => "🚫 No Walls",
                    ExclusionType.FurnitureOnly => "🚫 No Furniture",
                    ExclusionType.AllObstacles => "🚫 No Obstacles",
                    _ => "🚫 Exclusion"
                };

                UnityEditor.Handles.Label(
                    worldCenter + Vector3.up * (_zoneSize.y / 2f + 0.2f),
                    label,
                    new GUIStyle()
                    {
                        normal = new GUIStyleState() { textColor = Color.white },
                        fontSize = 12,
                        fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleCenter
                    }
                );
                
                // Mostrar dimensiones
                UnityEditor.Handles.Label(
                    worldCenter - Vector3.up * (_zoneSize.y / 2f + 0.2f),
                    $"{_zoneSize.x:F1} x {_zoneSize.y:F1} x {_zoneSize.z:F1}m",
                    new GUIStyle()
                    {
                        normal = new GUIStyleState() { textColor = Color.yellow },
                        fontSize = 10
                    }
                );
            }
            #endif
        }

        #endregion
    }

    /// <summary>
    /// Manager global que mantiene registro de todas las zonas de exclusión activas
    /// </summary>
    public static class NavMeshWallExclusionManager
    {
        private static System.Collections.Generic.List<NavMeshWallExclusionZone> _activeZones = 
            new System.Collections.Generic.List<NavMeshWallExclusionZone>();

        public static void RegisterZone(NavMeshWallExclusionZone zone)
        {
            if (!_activeZones.Contains(zone))
            {
                _activeZones.Add(zone);
                Debug.Log($"[ExclusionManager] ✅ Zona registrada: {zone.name} (total: {_activeZones.Count})");
            }
        }

        public static void UnregisterZone(NavMeshWallExclusionZone zone)
        {
            if (_activeZones.Contains(zone))
            {
                _activeZones.Remove(zone);
                Debug.Log($"[ExclusionManager] 🗑️ Zona desregistrada: {zone.name} (total: {_activeZones.Count})");
            }
        }

        /// <summary>
        /// Verifica si un punto está en alguna zona de exclusión de walls
        /// </summary>
        public static bool IsPointInWallExclusionZone(Vector3 worldPoint)
        {
            foreach (var zone in _activeZones)
            {
                if (zone != null && zone.ShouldExcludeWalls() && zone.ContainsPoint(worldPoint))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Verifica si un bounds está en alguna zona de exclusión de walls
        /// </summary>
        public static bool IsBoundsInWallExclusionZone(Bounds worldBounds)
        {
            foreach (var zone in _activeZones)
            {
                if (zone != null && zone.ShouldExcludeWalls() && zone.IntersectsBounds(worldBounds))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Verifica si un punto está en alguna zona de exclusión de furniture
        /// </summary>
        public static bool IsPointInFurnitureExclusionZone(Vector3 worldPoint)
        {
            foreach (var zone in _activeZones)
            {
                if (zone != null && zone.ShouldExcludeFurniture() && zone.ContainsPoint(worldPoint))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Verifica si un bounds está en alguna zona de exclusión de furniture
        /// </summary>
        public static bool IsBoundsInFurnitureExclusionZone(Bounds worldBounds)
        {
            foreach (var zone in _activeZones)
            {
                if (zone != null && zone.ShouldExcludeFurniture() && zone.IntersectsBounds(worldBounds))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Obtener total de zonas activas
        /// </summary>
        public static int GetActiveZoneCount()
        {
            // Limpiar referencias nulas
            _activeZones.RemoveAll(z => z == null);
            return _activeZones.Count;
        }

        /// <summary>
        /// Limpiar todas las zonas
        /// </summary>
        public static void ClearAll()
        {
            _activeZones.Clear();
            Debug.Log("[ExclusionManager] 🧹 Todas las zonas limpiadas");
        }
    }
}