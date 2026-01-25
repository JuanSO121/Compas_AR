using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Filtra un mesh unificado (piso+paredes+muebles) para generar geometría SOLO del piso.
    /// TÉCNICA: Análisis de triángulos por altura (world Y) + orientación (normal).
    /// </summary>
    public class UnifiedMeshFloorFilter : MonoBehaviour
    {
        [Header("Configuración de Filtrado")]
        [Tooltip("Referencia de detector AR (opcional, mejora precisión)")]
        [SerializeField] private ARFloorReferenceDetector _arFloorDetector;
        
        [Tooltip("Altura mínima del piso (fallback si no hay AR)")]
        [SerializeField] private float _fallbackFloorHeight = 0f;
        
        [Tooltip("Altura máxima del piso (fallback si no hay AR)")]
        [SerializeField] private float _fallbackFloorMaxHeight = 0.25f;
        
        [Tooltip("Tolerancia de altura si se usa AR Reference")]
        [SerializeField] private float _arFloorTolerance = 0.15f;
        
        [Header("Filtrado por Normal")]
        [Tooltip("Ángulo máximo con vertical para considerar 'horizontal' (grados)")]
        [SerializeField] private float _maxFloorAngle = 15f;
        
        [Tooltip("Rechazar triángulos que apuntan hacia abajo")]
        [SerializeField] private bool _rejectDownwardFacing = true;
        
        [Header("Optimización")]
        [Tooltip("Simplificar mesh resultante (reduce triángulos redundantes)")]
        [SerializeField] private bool _simplifyResultMesh = true;
        
        [Tooltip("Distancia mínima entre vértices para fusionar (metros)")]
        [SerializeField] private float _vertexWeldDistance = 0.01f;
        
        [Header("Output")]
        [Tooltip("Layer para el mesh de piso generado")]
        [SerializeField] private int _floorMeshLayer = 0;
        
        [Tooltip("Crear collider en el mesh de piso")]
        [SerializeField] private bool _addMeshCollider = true;
        
        [Header("Debug")]
        [SerializeField] private bool _showDebugVisualization = true;
        [SerializeField] private bool _logFilteringStats = true;
        
        // Estado
        private GameObject _generatedFloorMesh;
        private Mesh _filteredFloorMesh;
        private int _totalTriangles;
        private int _acceptedTriangles;
        private int _rejectedByHeight;
        private int _rejectedByNormal;

        #region Public API

        /// <summary>
        /// Filtra un mesh unificado y genera geometría SOLO del piso.
        /// </summary>
        public GameObject FilterAndGenerateFloorMesh(MeshFilter sourceMeshFilter)
        {
            if (sourceMeshFilter == null || sourceMeshFilter.sharedMesh == null)
            {
                Debug.LogError("[UnifiedMeshFilter] MeshFilter inválido.");
                return null;
            }
            
            Debug.Log("[UnifiedMeshFilter] 🔍 Iniciando filtrado de mesh unificado...");
            
            // Limpiar mesh anterior
            CleanupPreviousMesh();
            
            // Obtener altura de referencia del piso
            float floorMinY, floorMaxY;
            if (!GetFloorHeightRange(out floorMinY, out floorMaxY))
            {
                Debug.LogError("[UnifiedMeshFilter] ❌ No se pudo determinar altura del piso.");
                return null;
            }
            
            Debug.Log($"[UnifiedMeshFilter] 📏 Rango de piso: Y=[{floorMinY:F3}, {floorMaxY:F3}]m");
            
            // Extraer triángulos del piso
            Mesh sourceMesh = sourceMeshFilter.sharedMesh;
            Transform sourceTransform = sourceMeshFilter.transform;
            
            FilteredMeshData floorData = ExtractFloorTriangles(
                sourceMesh, 
                sourceTransform, 
                floorMinY, 
                floorMaxY
            );
            
            if (floorData.vertices.Count == 0)
            {
                Debug.LogError("[UnifiedMeshFilter] ❌ No se encontraron triángulos de piso.");
                return null;
            }
            
            // Crear mesh filtrado
            _filteredFloorMesh = CreateMeshFromFilteredData(floorData);
            
            // Generar GameObject con el mesh de piso
            _generatedFloorMesh = CreateFloorMeshObject(_filteredFloorMesh);
            
            LogFilteringResults();
            
            return _generatedFloorMesh;
        }

        /// <summary>
        /// Filtra TODOS los MeshFilters en la escena y genera mesh de piso unificado.
        /// </summary>
        public GameObject FilterAllSceneMeshes(LayerMask meshLayers)
        {
            MeshFilter[] allMeshFilters = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
            List<MeshFilter> validFilters = new List<MeshFilter>();
            
            foreach (MeshFilter mf in allMeshFilters)
            {
                if ((meshLayers.value & (1 << mf.gameObject.layer)) != 0)
                {
                    validFilters.Add(mf);
                }
            }
            
            if (validFilters.Count == 0)
            {
                Debug.LogError("[UnifiedMeshFilter] No se encontraron meshes en las capas especificadas.");
                return null;
            }
            
            Debug.Log($"[UnifiedMeshFilter] 🔍 Filtrando {validFilters.Count} meshes...");
            
            // Combinar todos los datos filtrados
            List<FilteredMeshData> allFloorData = new List<FilteredMeshData>();
            
            float floorMinY, floorMaxY;
            if (!GetFloorHeightRange(out floorMinY, out floorMaxY))
            {
                Debug.LogError("[UnifiedMeshFilter] ❌ No se pudo determinar altura del piso.");
                return null;
            }
            
            foreach (MeshFilter mf in validFilters)
            {
                FilteredMeshData data = ExtractFloorTriangles(
                    mf.sharedMesh, 
                    mf.transform, 
                    floorMinY, 
                    floorMaxY
                );
                
                if (data.vertices.Count > 0)
                {
                    allFloorData.Add(data);
                }
            }
            
            // Combinar todos los datos
            FilteredMeshData combinedData = CombineFilteredData(allFloorData);
            
            // Crear mesh final
            _filteredFloorMesh = CreateMeshFromFilteredData(combinedData);
            _generatedFloorMesh = CreateFloorMeshObject(_filteredFloorMesh);
            
            LogFilteringResults();
            
            return _generatedFloorMesh;
        }

        /// <summary>
        /// Limpia mesh de piso generado previamente.
        /// </summary>
        public void CleanupPreviousMesh()
        {
            if (_generatedFloorMesh != null)
            {
                Destroy(_generatedFloorMesh);
                _generatedFloorMesh = null;
            }
            
            _filteredFloorMesh = null;
            ResetStats();
        }

        #endregion

        #region Floor Height Detection

        /// <summary>
        /// Obtiene rango de altura válido para el piso.
        /// PRIORIDAD: AR Reference > Fallback manual
        /// </summary>
        private bool GetFloorHeightRange(out float minY, out float maxY)
        {
            // Intentar usar AR Reference si está disponible
            if (_arFloorDetector != null && _arFloorDetector.IsReady)
            {
                if (_arFloorDetector.GetFloorHeightRange(out minY, out maxY))
                {
                    Debug.Log($"[UnifiedMeshFilter] ✅ Usando altura AR: Y=[{minY:F3}, {maxY:F3}]m");
                    return true;
                }
            }
            
            // Fallback: usar valores manuales
            minY = _fallbackFloorHeight;
            maxY = _fallbackFloorMaxHeight;
            
            Debug.LogWarning($"[UnifiedMeshFilter] ⚠️ Usando altura FALLBACK: Y=[{minY:F3}, {maxY:F3}]m");
            return true;
        }

        #endregion

        #region Triangle Filtering

        /// <summary>
        /// Extrae triángulos que corresponden al piso del mesh fuente.
        /// </summary>
        private FilteredMeshData ExtractFloorTriangles(
            Mesh sourceMesh, 
            Transform sourceTransform,
            float floorMinY,
            float floorMaxY)
        {
            FilteredMeshData result = new FilteredMeshData();
            
            Vector3[] vertices = sourceMesh.vertices;
            Vector3[] normals = sourceMesh.normals;
            int[] triangles = sourceMesh.triangles;
            Vector2[] uvs = sourceMesh.uv;
            
            bool hasNormals = normals != null && normals.Length == vertices.Length;
            bool hasUVs = uvs != null && uvs.Length == vertices.Length;
            
            _totalTriangles += triangles.Length / 3;
            
            // Procesar cada triángulo
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i];
                int i1 = triangles[i + 1];
                int i2 = triangles[i + 2];
                
                // Transformar vértices a world space
                Vector3 v0World = sourceTransform.TransformPoint(vertices[i0]);
                Vector3 v1World = sourceTransform.TransformPoint(vertices[i1]);
                Vector3 v2World = sourceTransform.TransformPoint(vertices[i2]);
                
                // Calcular altura promedio del triángulo
                float avgY = (v0World.y + v1World.y + v2World.y) / 3f;
                
                // FILTRO 1: Altura
                if (avgY < floorMinY || avgY > floorMaxY)
                {
                    _rejectedByHeight++;
                    continue;
                }
                
                // FILTRO 2: Orientación (normal)
                Vector3 triangleNormal;
                
                if (hasNormals)
                {
                    // Usar normal promedio de vértices
                    Vector3 n0 = sourceTransform.TransformDirection(normals[i0]);
                    Vector3 n1 = sourceTransform.TransformDirection(normals[i1]);
                    Vector3 n2 = sourceTransform.TransformDirection(normals[i2]);
                    triangleNormal = ((n0 + n1 + n2) / 3f).normalized;
                }
                else
                {
                    // Calcular normal del triángulo
                    Vector3 edge1 = v1World - v0World;
                    Vector3 edge2 = v2World - v0World;
                    triangleNormal = Vector3.Cross(edge1, edge2).normalized;
                }
                
                // Verificar si es suficientemente horizontal
                float angleWithUp = Vector3.Angle(triangleNormal, Vector3.up);
                
                if (angleWithUp > _maxFloorAngle)
                {
                    _rejectedByNormal++;
                    continue;
                }
                
                // Rechazar si apunta hacia abajo
                if (_rejectDownwardFacing && triangleNormal.y < 0)
                {
                    _rejectedByNormal++;
                    continue;
                }
                
                // ✅ TRIÁNGULO ACEPTADO - Agregarlo al resultado
                int newI0 = result.vertices.Count;
                int newI1 = newI0 + 1;
                int newI2 = newI0 + 2;
                
                result.vertices.Add(v0World);
                result.vertices.Add(v1World);
                result.vertices.Add(v2World);
                
                result.normals.Add(triangleNormal);
                result.normals.Add(triangleNormal);
                result.normals.Add(triangleNormal);
                
                result.triangles.Add(newI0);
                result.triangles.Add(newI1);
                result.triangles.Add(newI2);
                
                if (hasUVs)
                {
                    result.uvs.Add(uvs[i0]);
                    result.uvs.Add(uvs[i1]);
                    result.uvs.Add(uvs[i2]);
                }
                
                _acceptedTriangles++;
            }
            
            return result;
        }

        #endregion

        #region Mesh Creation

        /// <summary>
        /// Crea Mesh de Unity a partir de datos filtrados.
        /// </summary>
        private Mesh CreateMeshFromFilteredData(FilteredMeshData data)
        {
            if (data.vertices.Count == 0)
                return null;
            
            // Optimizar vértices si está habilitado
            if (_simplifyResultMesh)
            {
                data = WeldVertices(data);
            }
            
            Mesh mesh = new Mesh();
            mesh.name = "FilteredFloorMesh";
            
            mesh.SetVertices(data.vertices);
            mesh.SetTriangles(data.triangles, 0);
            mesh.SetNormals(data.normals);
            
            if (data.uvs.Count == data.vertices.Count)
            {
                mesh.SetUVs(0, data.uvs);
            }
            
            mesh.RecalculateBounds();
            
            return mesh;
        }

        /// <summary>
        /// Fusiona vértices muy cercanos para optimizar el mesh.
        /// </summary>
        private FilteredMeshData WeldVertices(FilteredMeshData input)
        {
            // Implementación simplificada - en producción usar spatial hashing
            Dictionary<Vector3, int> vertexMap = new Dictionary<Vector3, int>();
            List<int> newIndices = new List<int>();
            
            FilteredMeshData output = new FilteredMeshData();
            
            for (int i = 0; i < input.vertices.Count; i++)
            {
                Vector3 v = input.vertices[i];
                
                // Buscar vértice similar
                int existingIndex = -1;
                foreach (var kvp in vertexMap)
                {
                    if (Vector3.Distance(kvp.Key, v) < _vertexWeldDistance)
                    {
                        existingIndex = kvp.Value;
                        break;
                    }
                }
                
                if (existingIndex >= 0)
                {
                    // Reusar vértice existente
                    newIndices.Add(existingIndex);
                }
                else
                {
                    // Agregar nuevo vértice
                    int newIndex = output.vertices.Count;
                    output.vertices.Add(v);
                    output.normals.Add(input.normals[i]);
                    if (i < input.uvs.Count)
                        output.uvs.Add(input.uvs[i]);
                    
                    vertexMap[v] = newIndex;
                    newIndices.Add(newIndex);
                }
            }
            
            // Reconstruir triángulos
            for (int i = 0; i < input.triangles.Count; i++)
            {
                output.triangles.Add(newIndices[input.triangles[i]]);
            }
            
            return output;
        }

        /// <summary>
        /// Crea GameObject con el mesh de piso filtrado.
        /// </summary>
        private GameObject CreateFloorMeshObject(Mesh floorMesh)
        {
            if (floorMesh == null)
                return null;
            
            GameObject floorObj = new GameObject("[Filtered Floor Mesh]");
            floorObj.layer = _floorMeshLayer;
            floorObj.transform.position = Vector3.zero;
            floorObj.transform.rotation = Quaternion.identity;
            
            // MeshFilter
            MeshFilter meshFilter = floorObj.AddComponent<MeshFilter>();
            meshFilter.mesh = floorMesh;
            
            // MeshRenderer (opcional, para visualización)
            MeshRenderer meshRenderer = floorObj.AddComponent<MeshRenderer>();
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = new Color(0.2f, 0.8f, 0.2f, _showDebugVisualization ? 0.5f : 0f);
            meshRenderer.material = mat;
            
            // MeshCollider (para NavMesh)
            if (_addMeshCollider)
            {
                MeshCollider meshCollider = floorObj.AddComponent<MeshCollider>();
                meshCollider.sharedMesh = floorMesh;
                meshCollider.convex = false;
            }
            
            // Marcar como estático para NavMesh
            floorObj.isStatic = true;
            
            Debug.Log($"[UnifiedMeshFilter] ✅ Mesh de piso generado: {floorMesh.vertexCount} vértices, {floorMesh.triangles.Length / 3} triángulos");
            
            return floorObj;
        }

        /// <summary>
        /// Combina múltiples FilteredMeshData en uno solo.
        /// </summary>
        private FilteredMeshData CombineFilteredData(List<FilteredMeshData> dataList)
        {
            FilteredMeshData combined = new FilteredMeshData();
            
            foreach (FilteredMeshData data in dataList)
            {
                int vertexOffset = combined.vertices.Count;
                
                combined.vertices.AddRange(data.vertices);
                combined.normals.AddRange(data.normals);
                combined.uvs.AddRange(data.uvs);
                
                // Ajustar índices de triángulos
                foreach (int index in data.triangles)
                {
                    combined.triangles.Add(index + vertexOffset);
                }
            }
            
            return combined;
        }

        #endregion

        #region Stats & Logging

        private void ResetStats()
        {
            _totalTriangles = 0;
            _acceptedTriangles = 0;
            _rejectedByHeight = 0;
            _rejectedByNormal = 0;
        }

        private void LogFilteringResults()
        {
            if (!_logFilteringStats)
                return;
            
            Debug.Log("[UnifiedMeshFilter] ========== RESULTADOS ==========");
            Debug.Log($"  📊 Total triángulos analizados: {_totalTriangles}");
            Debug.Log($"  ✅ Aceptados (piso): {_acceptedTriangles}");
            Debug.Log($"  ❌ Rechazados por altura: {_rejectedByHeight}");
            Debug.Log($"  ❌ Rechazados por normal: {_rejectedByNormal}");
            
            if (_totalTriangles > 0)
            {
                float acceptRate = (_acceptedTriangles / (float)_totalTriangles) * 100f;
                Debug.Log($"  📈 Tasa de aceptación: {acceptRate:F1}%");
            }
            
            if (_filteredFloorMesh != null)
            {
                Debug.Log($"  🏗️ Mesh final: {_filteredFloorMesh.vertexCount} vértices");
            }
            
            Debug.Log("==========================================");
        }

        #endregion

        #region Gizmos

        private void OnDrawGizmos()
        {
            if (!_showDebugVisualization || _filteredFloorMesh == null)
                return;
            
            // Dibujar wireframe del mesh de piso
            Gizmos.color = Color.green;
            Gizmos.DrawWireMesh(_filteredFloorMesh, Vector3.zero, Quaternion.identity);
        }

        #endregion

        #region Data Structures

        /// <summary>
        /// Estructura para almacenar datos de mesh filtrado.
        /// </summary>
        private class FilteredMeshData
        {
            public List<Vector3> vertices = new List<Vector3>();
            public List<Vector3> normals = new List<Vector3>();
            public List<int> triangles = new List<int>();
            public List<Vector2> uvs = new List<Vector2>();
        }

        #endregion

        
[ContextMenu("Debug: Analyze Scene Meshes")]
private void DebugAnalyzeSceneMeshes()
{
    Debug.Log("[UnifiedMeshFilter] ========== ANÁLISIS DE MESHES ==========");
    
    MeshFilter[] allMeshes = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
    Debug.Log($"Total meshes en escena: {allMeshes.Length}");
    
    float floorMinY, floorMaxY;
    if (!GetFloorHeightRange(out floorMinY, out floorMaxY))
    {
        Debug.LogWarning("No se pudo determinar altura del piso");
        floorMinY = _fallbackFloorHeight;
        floorMaxY = _fallbackFloorMaxHeight;
    }
    
    Debug.Log($"Rango de altura del piso: Y=[{floorMinY:F3}, {floorMaxY:F3}]m");
    Debug.Log("");
    
    int validMeshCount = 0;
    
    foreach (MeshFilter mf in allMeshes)
    {
        if (mf.sharedMesh == null)
            continue;
            
        int totalTris = mf.sharedMesh.triangles.Length / 3;
        int floorTris = 0;
        
        // Analizar triángulos
        Vector3[] vertices = mf.sharedMesh.vertices;
        int[] triangles = mf.sharedMesh.triangles;
        
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 v0 = mf.transform.TransformPoint(vertices[triangles[i]]);
            Vector3 v1 = mf.transform.TransformPoint(vertices[triangles[i + 1]]);
            Vector3 v2 = mf.transform.TransformPoint(vertices[triangles[i + 2]]);
            
            float avgY = (v0.y + v1.y + v2.y) / 3f;
            
            if (avgY >= floorMinY && avgY <= floorMaxY)
            {
                floorTris++;
            }
        }
        
        if (floorTris > 0)
        {
            validMeshCount++;
            Debug.Log($"✅ {mf.gameObject.name}:");
            Debug.Log($"   → Layer: {LayerMask.LayerToName(mf.gameObject.layer)}");
            Debug.Log($"   → Vértices: {mf.sharedMesh.vertexCount}");
            Debug.Log($"   → Triángulos totales: {totalTris}");
            Debug.Log($"   → Triángulos de piso: {floorTris}");
            Debug.Log($"   → Posición: {mf.transform.position}");
        }
        else
        {
            Debug.Log($"❌ {mf.gameObject.name}: Sin triángulos de piso en rango");
        }
    }
    
    Debug.Log("");
    Debug.Log($"Meshes con geometría de piso: {validMeshCount}/{allMeshes.Length}");
    Debug.Log("==========================================");
}

[ContextMenu("Debug: Test Filter Current Meshes")]
private void DebugTestFilter()
{
    MeshFilter[] allMeshes = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
    
    if (allMeshes.Length == 0)
    {
        Debug.LogError("No hay meshes en la escena");
        return;
    }
    
    Debug.Log($"[UnifiedMeshFilter] Probando filtrado con {allMeshes.Length} meshes...");
    
    // Probar con el primer mesh válido
    foreach (MeshFilter mf in allMeshes)
    {
        if (mf.sharedMesh != null && mf.sharedMesh.vertexCount > 0)
        {
            Debug.Log($"Probando con: {mf.gameObject.name}");
            GameObject result = FilterAndGenerateFloorMesh(mf);
            
            if (result != null)
            {
                Debug.Log("✅ Filtrado exitoso!");
            }
            else
            {
                Debug.LogError("❌ Filtrado falló");
            }
            
            return;
        }
    }
    
    Debug.LogError("No se encontró ningún mesh válido para probar");
}
    }
}