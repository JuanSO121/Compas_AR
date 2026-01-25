using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Sistema profesional de detección de piso real mediante análisis estadístico de alturas.
    /// Resuelve el problema de NavMesh en geometría escaneada con ruido.
    /// 
    /// ESTRATEGIA:
    /// 1. Analiza distribución de alturas (Y) de TODA la geometría
    /// 2. Detecta el cluster de altura más bajo y denso → PISO REAL
    /// 3. Marca superficies elevadas como NO navegables
    /// 4. Opcionalmente reconstruye piso continuo si hay huecos
    /// </summary>
    public class RobustFloorDetector : MonoBehaviour
    {
        [Header("Configuración de Análisis")]
        [Tooltip("Resolución del histograma de alturas (metros). Menor = más preciso")]
        [SerializeField] private float _heightBinSize = 0.05f; // 5cm
        
        [Tooltip("Mínima densidad de samples para considerar un cluster válido")]
        [SerializeField] private float _minClusterDensity = 0.15f; // 15% del total
        
        [Tooltip("Altura máxima sobre el piso para considerar navegable")]
        [SerializeField] private float _maxFloorDeviation = 0.15f; // 15cm
        
        [Header("Exclusión de Superficies Elevadas")]
        [Tooltip("Altura mínima sobre el piso para marcar como obstáculo")]
        [SerializeField] private float _elevatedSurfaceThreshold = 0.30f; // 30cm
        
        [Tooltip("Expandir volúmenes de exclusión (evitar bordes)")]
        [SerializeField] private float _exclusionVolumeExpansion = 0.1f;
        
        [Header("Reconstrucción de Piso (Opcional)")]
        [Tooltip("Activar reconstrucción de piso continuo si hay huecos")]
        [SerializeField] private bool _reconstructFloorPlane = true;
        
        [Tooltip("Padding del plano de piso reconstruido")]
        [SerializeField] private float _floorPlanePadding = 1f;
        
        [Header("Configuración NavMesh")]
        [SerializeField] private LayerMask _geometryLayers = ~0;
        [SerializeField] private int _navMeshLayer = 0; // Layer para NavMesh
        [SerializeField] private int _excludedArea = 1; // NavMesh Area "Not Walkable"
        
        [Header("Debug")]
        [SerializeField] private bool _showDebugVisualization = true;
        [SerializeField] private bool _logDetailedAnalysis = false;
        
        // Estado del análisis
        private float _detectedFloorHeight;
        private Bounds _floorBounds;
        private List<HeightCluster> _heightClusters;
        private List<GameObject> _exclusionVolumes;
        private GameObject _reconstructedFloor;
        
        // Visualización debug
        private List<Vector3> _floorSamples;
        private List<Vector3> _elevatedSamples;

        #region Public API

        /// <summary>
        /// Ejecuta análisis completo y prepara geometría para NavMesh.
        /// DEBE llamarse ANTES de BuildNavMesh().
        /// </summary>
        public bool AnalyzeAndPrepareGeometry()
        {
            Debug.Log("[RobustFloorDetector] 🔍 Iniciando análisis de geometría...");
            
            try
            {
                // 1. Recolectar samples de altura de toda la geometría
                List<HeightSample> samples = CollectHeightSamples();
                
                if (samples.Count == 0)
                {
                    Debug.LogError("[RobustFloorDetector] ❌ No se encontró geometría para analizar");
                    return false;
                }
                
                Debug.Log($"[RobustFloorDetector] 📊 Recolectados {samples.Count} samples de altura");
                
                // 2. Construir histograma de alturas
                _heightClusters = BuildHeightHistogram(samples);
                
                if (_logDetailedAnalysis)
                {
                    LogHistogramAnalysis(_heightClusters);
                }
                
                // 3. Detectar cluster de piso (más bajo y denso)
                HeightCluster floorCluster = DetectFloorCluster(_heightClusters);
                
                if (floorCluster == null)
                {
                    Debug.LogError("[RobustFloorDetector] ❌ No se pudo detectar el piso real");
                    return false;
                }
                
                _detectedFloorHeight = floorCluster.centerHeight;
                Debug.Log($"[RobustFloorDetector] ✅ Piso detectado a Y = {_detectedFloorHeight:F3}m");
                
                // 4. Calcular bounds del área de piso
                _floorBounds = CalculateFloorBounds(samples, floorCluster);
                Debug.Log($"[RobustFloorDetector] 📐 Área de piso: {_floorBounds.size.x:F2}x{_floorBounds.size.z:F2}m");
                
                // 5. Marcar superficies elevadas como no navegables
                int excluded = MarkElevatedSurfacesAsNonWalkable(samples);
                Debug.Log($"[RobustFloorDetector] 🚫 {excluded} superficies elevadas excluidas");
                
                // 6. Reconstruir piso continuo si está habilitado
                if (_reconstructFloorPlane)
                {
                    ReconstructContinuousFloor();
                    Debug.Log("[RobustFloorDetector] 🏗️ Piso continuo reconstruido");
                }
                
                Debug.Log("[RobustFloorDetector] ✅ Geometría preparada para NavMesh");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RobustFloorDetector] ❌ Error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Limpia volúmenes de exclusión y geometría reconstruida.
        /// </summary>
        public void Cleanup()
        {
            if (_exclusionVolumes != null)
            {
                foreach (var vol in _exclusionVolumes)
                {
                    if (vol != null) Destroy(vol);
                }
                _exclusionVolumes.Clear();
            }
            
            if (_reconstructedFloor != null)
            {
                Destroy(_reconstructedFloor);
                _reconstructedFloor = null;
            }
        }

        #endregion

        #region Step 1: Recolección de Samples

        /// <summary>
        /// Recolecta samples de altura de TODA la geometría visible.
        /// Estrategia: Samplear vértices de meshes para obtener distribución de alturas.
        /// </summary>
        private List<HeightSample> CollectHeightSamples()
        {
            List<HeightSample> samples = new List<HeightSample>();
            _floorSamples = new List<Vector3>();
            _elevatedSamples = new List<Vector3>();
            
            // Obtener todos los renderers en las capas especificadas
            Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            
            foreach (Renderer renderer in renderers)
            {
                // Filtrar por layer
                if ((_geometryLayers.value & (1 << renderer.gameObject.layer)) == 0)
                    continue;
                
                MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null)
                    continue;
                
                // Samplear vértices del mesh
                Mesh mesh = meshFilter.sharedMesh;
                Vector3[] vertices = mesh.vertices;
                Transform meshTransform = renderer.transform;
                
                // Estrategia: No todos los vértices (optimización)
                // Samplear cada N vértices para mantener performance
                int step = Mathf.Max(1, vertices.Length / 1000);
                
                for (int i = 0; i < vertices.Length; i += step)
                {
                    Vector3 worldPos = meshTransform.TransformPoint(vertices[i]);
                    
                    samples.Add(new HeightSample
                    {
                        position = worldPos,
                        height = worldPos.y,
                        renderer = renderer
                    });
                }
            }
            
            return samples;
        }

        #endregion

        #region Step 2: Construcción de Histograma

        /// <summary>
        /// Construye histograma de alturas usando bins de altura fija.
        /// Retorna clusters ordenados por altura.
        /// </summary>
        private List<HeightCluster> BuildHeightHistogram(List<HeightSample> samples)
        {
            if (samples.Count == 0)
                return new List<HeightCluster>();
            
            // Encontrar rango de alturas
            float minHeight = samples.Min(s => s.height);
            float maxHeight = samples.Max(s => s.height);
            
            // Calcular número de bins
            int numBins = Mathf.CeilToInt((maxHeight - minHeight) / _heightBinSize);
            numBins = Mathf.Max(1, numBins);
            
            // Inicializar bins
            Dictionary<int, HeightCluster> bins = new Dictionary<int, HeightCluster>();
            
            // Asignar samples a bins
            foreach (var sample in samples)
            {
                int binIndex = Mathf.FloorToInt((sample.height - minHeight) / _heightBinSize);
                binIndex = Mathf.Clamp(binIndex, 0, numBins - 1);
                
                if (!bins.ContainsKey(binIndex))
                {
                    bins[binIndex] = new HeightCluster
                    {
                        binIndex = binIndex,
                        minHeight = minHeight + binIndex * _heightBinSize,
                        maxHeight = minHeight + (binIndex + 1) * _heightBinSize,
                        samples = new List<HeightSample>()
                    };
                }
                
                bins[binIndex].samples.Add(sample);
            }
            
            // Calcular propiedades de cada cluster
            foreach (var cluster in bins.Values)
            {
                cluster.centerHeight = (cluster.minHeight + cluster.maxHeight) / 2f;
                cluster.sampleCount = cluster.samples.Count;
                cluster.density = cluster.sampleCount / (float)samples.Count;
            }
            
            // Retornar clusters ordenados por altura
            return bins.Values.OrderBy(c => c.centerHeight).ToList();
        }

        #endregion

        #region Step 3: Detección de Piso

        /// <summary>
        /// Detecta el cluster que representa el piso real.
        /// Criterios:
        /// 1. Debe estar en la parte inferior (primeros 30% de altura)
        /// 2. Debe tener densidad suficiente (área significativa)
        /// 3. En caso de empate, el más bajo gana
        /// </summary>
        private HeightCluster DetectFloorCluster(List<HeightCluster> clusters)
        {
            if (clusters.Count == 0)
                return null;
            
            // Calcular rango de búsqueda (30% inferior)
            float minHeight = clusters[0].centerHeight;
            float maxHeight = clusters[clusters.Count - 1].centerHeight;
            float searchRange = (maxHeight - minHeight) * 0.3f;
            float searchMaxHeight = minHeight + searchRange;
            
            // Filtrar clusters candidatos (en rango inferior)
            var candidates = clusters
                .Where(c => c.centerHeight <= searchMaxHeight)
                .Where(c => c.density >= _minClusterDensity)
                .OrderByDescending(c => c.density) // Priorizar densidad
                .ThenBy(c => c.centerHeight)        // Luego altura
                .ToList();
            
            if (candidates.Count > 0)
            {
                return candidates[0];
            }
            
            // Fallback: Si no hay candidatos densos, tomar el más bajo
            Debug.LogWarning("[RobustFloorDetector] ⚠️ No se encontró cluster denso, usando el más bajo");
            return clusters[0];
        }

        #endregion

        #region Step 4: Cálculo de Bounds

        /// <summary>
        /// Calcula bounds del área de piso basado en samples cercanos al nivel detectado.
        /// </summary>
        private Bounds CalculateFloorBounds(List<HeightSample> samples, HeightCluster floorCluster)
        {
            // Filtrar samples que están cerca del nivel de piso
            var floorSamples = samples
                .Where(s => Mathf.Abs(s.height - floorCluster.centerHeight) <= _maxFloorDeviation)
                .Select(s => s.position)
                .ToList();
            
            if (floorSamples.Count == 0)
            {
                // Fallback: usar samples del cluster
                floorSamples = floorCluster.samples.Select(s => s.position).ToList();
            }
            
            _floorSamples = floorSamples; // Para debug
            
            // Calcular bounds
            Vector3 min = floorSamples[0];
            Vector3 max = floorSamples[0];
            
            foreach (var pos in floorSamples)
            {
                min = Vector3.Min(min, pos);
                max = Vector3.Max(max, pos);
            }
            
            // Crear bounds centrado en el nivel de piso
            Vector3 center = (min + max) / 2f;
            center.y = floorCluster.centerHeight;
            
            Vector3 size = max - min;
            size.y = _maxFloorDeviation * 2f; // Altura del bounds del piso
            
            return new Bounds(center, size);
        }

        #endregion

        #region Step 5: Exclusión de Superficies Elevadas

        /// <summary>
        /// Marca superficies elevadas como no navegables usando NavMeshModifierVolume.
        /// Estrategia: Crear volúmenes de exclusión para geometría sobre el umbral.
        /// </summary>
        private int MarkElevatedSurfacesAsNonWalkable(List<HeightSample> samples)
        {
            // Limpiar volúmenes previos
            if (_exclusionVolumes == null)
                _exclusionVolumes = new List<GameObject>();
            else
                Cleanup();
            
            _exclusionVolumes = new List<GameObject>();
            
            // Agrupar samples elevados por renderer
            var elevatedByRenderer = samples
                .Where(s => s.height > _detectedFloorHeight + _elevatedSurfaceThreshold)
                .GroupBy(s => s.renderer)
                .ToList();
            
            _elevatedSamples = elevatedByRenderer
                .SelectMany(g => g.Select(s => s.position))
                .ToList();
            
            int excludedCount = 0;
            
            foreach (var group in elevatedByRenderer)
            {
                Renderer renderer = group.Key;
                if (renderer == null)
                    continue;
                
                // Calcular bounds del grupo de samples
                Bounds groupBounds = CalculateSampleBounds(group.ToList());
                
                // Expandir bounds
                groupBounds.Expand(_exclusionVolumeExpansion * 2f);
                
                // Crear volumen de exclusión
                GameObject excludeVolume = new GameObject($"ExcludeVolume_{renderer.name}");
                excludeVolume.transform.position = groupBounds.center;
                excludeVolume.layer = _navMeshLayer;
                
                // Agregar NavMeshModifierVolume
                NavMeshModifierVolume modifier = excludeVolume.AddComponent<NavMeshModifierVolume>();
                modifier.size = groupBounds.size;
                modifier.area = _excludedArea; // "Not Walkable"
                
                // Opcional: Agregar collider visual para debug
                if (_showDebugVisualization)
                {
                    BoxCollider debugBox = excludeVolume.AddComponent<BoxCollider>();
                    debugBox.size = groupBounds.size;
                    debugBox.isTrigger = true;
                }
                
                _exclusionVolumes.Add(excludeVolume);
                excludedCount++;
            }
            
            return excludedCount;
        }

        private Bounds CalculateSampleBounds(List<HeightSample> samples)
        {
            if (samples.Count == 0)
                return new Bounds();
            
            Vector3 min = samples[0].position;
            Vector3 max = samples[0].position;
            
            foreach (var sample in samples)
            {
                min = Vector3.Min(min, sample.position);
                max = Vector3.Max(max, sample.position);
            }
            
            Vector3 center = (min + max) / 2f;
            Vector3 size = max - min;
            
            return new Bounds(center, size);
        }

        #endregion

        #region Step 6: Reconstrucción de Piso

        /// <summary>
        /// Crea un plano continuo a nivel de piso para rellenar huecos.
        /// Esto asegura que NavMesh pueda generar rutas incluso con geometría fragmentada.
        /// </summary>
        private void ReconstructContinuousFloor()
        {
            // Limpiar piso anterior
            if (_reconstructedFloor != null)
            {
                Destroy(_reconstructedFloor);
            }
            
            // Crear GameObject para el piso
            _reconstructedFloor = new GameObject("ReconstructedFloor");
            _reconstructedFloor.layer = _navMeshLayer;
            
            // Posicionar ligeramente bajo el nivel detectado
            Vector3 floorPosition = _floorBounds.center;
            floorPosition.y = _detectedFloorHeight - 0.02f; // 2cm bajo para evitar z-fighting
            _reconstructedFloor.transform.position = floorPosition;
            
            // Crear mesh del plano
            MeshFilter meshFilter = _reconstructedFloor.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = _reconstructedFloor.AddComponent<MeshRenderer>();
            
            // Calcular tamaño del plano con padding
            float width = _floorBounds.size.x + _floorPlanePadding * 2f;
            float depth = _floorBounds.size.z + _floorPlanePadding * 2f;
            
            Mesh planeMesh = CreatePlaneMesh(width, depth);
            meshFilter.mesh = planeMesh;
            
            // Material simple (invisible o debug)
            Material floorMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            floorMat.color = new Color(0.5f, 0.5f, 0.5f, _showDebugVisualization ? 0.3f : 0f);
            meshRenderer.material = floorMat;
            
            // Agregar collider para NavMesh
            MeshCollider meshCollider = _reconstructedFloor.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = planeMesh;
            meshCollider.convex = false;
            
            // Marcar como estático para NavMesh
            _reconstructedFloor.isStatic = true;
        }

        private Mesh CreatePlaneMesh(float width, float depth)
        {
            Mesh mesh = new Mesh();
            mesh.name = "FloorPlane";
            
            // Vértices del plano (XZ)
            Vector3[] vertices = new Vector3[4]
            {
                new Vector3(-width/2f, 0f, -depth/2f),
                new Vector3( width/2f, 0f, -depth/2f),
                new Vector3(-width/2f, 0f,  depth/2f),
                new Vector3( width/2f, 0f,  depth/2f)
            };
            
            // Triángulos
            int[] triangles = new int[6]
            {
                0, 2, 1,
                2, 3, 1
            };
            
            // Normales
            Vector3[] normals = new Vector3[4]
            {
                Vector3.up,
                Vector3.up,
                Vector3.up,
                Vector3.up
            };
            
            // UVs
            Vector2[] uv = new Vector2[4]
            {
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(0, 1),
                new Vector2(1, 1)
            };
            
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.normals = normals;
            mesh.uv = uv;
            
            return mesh;
        }

        #endregion

        #region Debug & Logging

        private void LogHistogramAnalysis(List<HeightCluster> clusters)
        {
            Debug.Log("[RobustFloorDetector] === ANÁLISIS DE HISTOGRAMA ===");
            
            foreach (var cluster in clusters.Take(10)) // Solo primeros 10
            {
                float percentage = cluster.density * 100f;
                string bar = new string('█', Mathf.RoundToInt(percentage / 2f));
                
                Debug.Log($"  Y={cluster.centerHeight,6:F2}m [{cluster.minHeight:F2}-{cluster.maxHeight:F2}] " +
                         $"Count={cluster.sampleCount,5} ({percentage,5:F1}%) {bar}");
            }
            
            Debug.Log("==========================================");
        }

        private void OnDrawGizmos()
        {
            if (!_showDebugVisualization)
                return;
            
            // Dibujar bounds de piso
            if (_floorBounds.size != Vector3.zero)
            {
                Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
                Gizmos.DrawCube(_floorBounds.center, _floorBounds.size);
                
                Gizmos.color = Color.green;
                Gizmos.DrawWireCube(_floorBounds.center, _floorBounds.size);
            }
            
            // Dibujar samples de piso
            if (_floorSamples != null)
            {
                Gizmos.color = Color.green;
                foreach (var pos in _floorSamples)
                {
                    Gizmos.DrawSphere(pos, 0.02f);
                }
            }
            
            // Dibujar samples elevados
            if (_elevatedSamples != null)
            {
                Gizmos.color = Color.red;
                foreach (var pos in _elevatedSamples)
                {
                    Gizmos.DrawSphere(pos, 0.03f);
                }
            }
            
            // Dibujar volúmenes de exclusión
            if (_exclusionVolumes != null)
            {
                Gizmos.color = new Color(1f, 0f, 0f, 0.2f);
                foreach (var vol in _exclusionVolumes)
                {
                    if (vol != null)
                    {
                        NavMeshModifierVolume modifier = vol.GetComponent<NavMeshModifierVolume>();
                        if (modifier != null)
                        {
                            Gizmos.matrix = vol.transform.localToWorldMatrix;
                            Gizmos.DrawCube(Vector3.zero, modifier.size);
                            
                            Gizmos.color = Color.red;
                            Gizmos.DrawWireCube(Vector3.zero, modifier.size);
                            Gizmos.color = new Color(1f, 0f, 0f, 0.2f);
                        }
                    }
                }
                Gizmos.matrix = Matrix4x4.identity;
            }
        }

        #endregion

        #region Data Structures

        [Serializable]
        private class HeightSample
        {
            public Vector3 position;
            public float height;
            public Renderer renderer;
        }

        [Serializable]
        private class HeightCluster
        {
            public int binIndex;
            public float minHeight;
            public float maxHeight;
            public float centerHeight;
            public int sampleCount;
            public float density;
            public List<HeightSample> samples;
        }

        #endregion
    }
}