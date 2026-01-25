using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using IndoorNavAR.Core.Events;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace IndoorNavAR.Core.Loaders
{
    /// <summary>
    /// Importador universal de modelos 3D en runtime.
    /// Soporta: FBX, OBJ, GLB/GLTF (vía TriLib o runtime loaders).
    /// ✅ PROFESIONAL: Importación dinámica sin recompilar.
    /// </summary>
    public class RuntimeModelImporter : MonoBehaviour
    {
        [Header("Rutas de Importación")]
        [SerializeField] private string _modelsFolder = "Models";
        [Tooltip("Ruta relativa a Application.persistentDataPath o StreamingAssets")]
        [SerializeField] private bool _useStreamingAssets = true;
        [SerializeField] private bool _usePersistentData = false;

        [Header("Configuración de Importación")]
        [SerializeField] private float _defaultScale = 1f;
        [SerializeField] private bool _generateColliders = true;
        [SerializeField] private bool _optimizeMesh = true;
        [SerializeField] private bool _calculateNormals = true;

        [Header("Materiales")]
        [SerializeField] private Material _defaultMaterial;
        [SerializeField] private bool _createMaterialsFromTextures = true;

        [Header("Referencias")]
        [SerializeField] private Transform _modelsParent;

        private GameObject _currentModel;
        private string _currentModelPath;

        #region Properties

        public GameObject CurrentModel => _currentModel;
        public bool HasModelLoaded => _currentModel != null;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            Initialize();
        }

        #endregion

        #region Initialization

        private void Initialize()
        {
            if (_modelsParent == null)
            {
                GameObject parent = new GameObject("[Runtime Models]");
                _modelsParent = parent.transform;
            }

            if (_defaultMaterial == null)
            {
                _defaultMaterial = CreateDefaultMaterial();
            }

            Debug.Log("[RuntimeModelImporter] Sistema de importación inicializado.");
        }

        private Material CreateDefaultMaterial()
        {
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = new Color(0.8f, 0.8f, 0.8f);
            mat.name = "DefaultModelMaterial";
            return mat;
        }

        #endregion

        #region Public API - Model Loading

        /// <summary>
        /// Carga un modelo 3D desde archivo (.obj, .fbx, .gltf).
        /// </summary>
        /// <param name="fileName">Nombre del archivo (ej: "room.obj")</param>
        public async Task<GameObject> LoadModelFromFile(string fileName)
        {
            try
            {
                Debug.Log($"[RuntimeModelImporter] 📦 Cargando modelo: {fileName}");

                string fullPath = GetFullPath(fileName);

                if (!File.Exists(fullPath))
                {
                    Debug.LogError($"[RuntimeModelImporter] ❌ Archivo no encontrado: {fullPath}");
                    return null;
                }

                // Determinar formato
                string extension = Path.GetExtension(fileName).ToLower();

                GameObject model = null;

                switch (extension)
                {
                    case ".obj":
                        model = await LoadOBJModel(fullPath, fileName);
                        break;

                    case ".fbx":
                        model = await LoadFBXModel(fullPath, fileName);
                        break;

                    case ".gltf":
                    case ".glb":
                        model = await LoadGLTFModel(fullPath, fileName);
                        break;

                    default:
                        Debug.LogError($"[RuntimeModelImporter] ❌ Formato no soportado: {extension}");
                        return null;
                }

                if (model != null)
                {
                    PostProcessModel(model);
                    _currentModel = model;
                    _currentModelPath = fullPath;

                    Debug.Log($"[RuntimeModelImporter] ✅ Modelo cargado exitosamente: {fileName}");

                    EventBus.Instance.Publish(new ModelLoadedEvent
                    {
                        ModelInstance = model,
                        ModelName = fileName,
                        Position = model.transform.position
                    });
                }

                return model;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RuntimeModelImporter] ❌ Error cargando modelo: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Carga modelo desde bytes (útil para descarga web).
        /// </summary>
        public async Task<GameObject> LoadModelFromBytes(byte[] data, string fileName)
        {
            try
            {
                // Guardar temporalmente
                string tempPath = Path.Combine(Application.temporaryCachePath, fileName);
                File.WriteAllBytes(tempPath, data);

                GameObject model = await LoadModelFromFile(tempPath);

                // Limpiar archivo temporal
                File.Delete(tempPath);

                return model;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RuntimeModelImporter] Error cargando desde bytes: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region OBJ Loading

        /// <summary>
        /// Carga archivo OBJ usando parser nativo.
        /// </summary>
        private async Task<GameObject> LoadOBJModel(string path, string fileName)
        {
            Debug.Log($"[RuntimeModelImporter] 📄 Parseando OBJ: {fileName}");

            await Task.Yield();

            string objData = File.ReadAllText(path);
            OBJData parsedData = ParseOBJ(objData);

            if (parsedData == null || parsedData.vertices.Count == 0)
            {
                Debug.LogError("[RuntimeModelImporter] OBJ parsing falló.");
                return null;
            }

            // Crear GameObject
            GameObject model = new GameObject(Path.GetFileNameWithoutExtension(fileName));
            model.transform.SetParent(_modelsParent);
            model.transform.localScale = Vector3.one * _defaultScale;

            // Crear mesh
            Mesh mesh = CreateMeshFromOBJData(parsedData);
            
            // Agregar componentes
            MeshFilter meshFilter = model.AddComponent<MeshFilter>();
            meshFilter.mesh = mesh;

            MeshRenderer meshRenderer = model.AddComponent<MeshRenderer>();
            meshRenderer.material = _defaultMaterial;

            Debug.Log($"[RuntimeModelImporter] ✅ OBJ cargado: {parsedData.vertices.Count} vértices");

            return model;
        }

        /// <summary>
        /// Parser simple de archivos OBJ.
        /// </summary>
        private OBJData ParseOBJ(string objData)
        {
            OBJData data = new OBJData();
            
            string[] lines = objData.Split('\n');

            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                string[] parts = trimmed.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                
                if (parts.Length < 2)
                    continue;

                switch (parts[0])
                {
                    case "v": // Vertex
                        if (parts.Length >= 4)
                        {
                            float x = float.Parse(parts[1]);
                            float y = float.Parse(parts[2]);
                            float z = float.Parse(parts[3]);
                            data.vertices.Add(new Vector3(x, y, z));
                        }
                        break;

                    case "vt": // Texture coordinate
                        if (parts.Length >= 3)
                        {
                            float u = float.Parse(parts[1]);
                            float v = float.Parse(parts[2]);
                            data.uvs.Add(new Vector2(u, v));
                        }
                        break;

                    case "vn": // Normal
                        if (parts.Length >= 4)
                        {
                            float x = float.Parse(parts[1]);
                            float y = float.Parse(parts[2]);
                            float z = float.Parse(parts[3]);
                            data.normals.Add(new Vector3(x, y, z));
                        }
                        break;

                    case "f": // Face
                        ParseFace(parts, data);
                        break;
                }
            }

            return data;
        }

        private void ParseFace(string[] parts, OBJData data)
        {
            // OBJ face format: f v1/vt1/vn1 v2/vt2/vn2 v3/vt3/vn3
            if (parts.Length < 4)
                return;

            // Convertir a triángulos si es necesario
            int vertexCount = parts.Length - 1;

            for (int i = 2; i < vertexCount; i++)
            {
                ParseVertex(parts[1], data);
                ParseVertex(parts[i], data);
                ParseVertex(parts[i + 1], data);
            }
        }

        private void ParseVertex(string vertexString, OBJData data)
        {
            string[] indices = vertexString.Split('/');
            
            if (indices.Length > 0)
            {
                int vertexIndex = int.Parse(indices[0]) - 1; // OBJ is 1-indexed
                data.triangles.Add(vertexIndex);
            }
        }

        private Mesh CreateMeshFromOBJData(OBJData data)
        {
            Mesh mesh = new Mesh();
            mesh.name = "RuntimeOBJMesh";

            mesh.SetVertices(data.vertices);
            mesh.SetTriangles(data.triangles, 0);

            if (data.uvs.Count == data.vertices.Count)
            {
                mesh.SetUVs(0, data.uvs);
            }

            if (_calculateNormals || data.normals.Count != data.vertices.Count)
            {
                mesh.RecalculateNormals();
            }
            else
            {
                mesh.SetNormals(data.normals);
            }

            mesh.RecalculateBounds();

            if (_optimizeMesh)
            {
                mesh.Optimize();
            }

            return mesh;
        }

        #endregion

        #region FBX Loading

        /// <summary>
        /// Carga archivo FBX (requiere Assets en runtime o uso de AssetDatabase en Editor).
        /// </summary>
        private async Task<GameObject> LoadFBXModel(string path, string fileName)
        {
            Debug.Log($"[RuntimeModelImporter] 📦 Cargando FBX: {fileName}");

            #if UNITY_EDITOR
            // En Editor: Usar AssetDatabase
            return await LoadFBXInEditor(path, fileName);
            #else
            // En Runtime: Usar biblioteca externa o precargar como prefab
            Debug.LogWarning("[RuntimeModelImporter] ⚠️ FBX runtime loading requiere biblioteca externa (TriLib).");
            Debug.LogWarning("[RuntimeModelImporter] Alternativa: Importa FBX en Editor y guarda como prefab.");
            
            // Intentar cargar como prefab desde Resources
            string prefabName = Path.GetFileNameWithoutExtension(fileName);
            return await LoadFromResources(prefabName);
            #endif
        }

        #if UNITY_EDITOR
        private async Task<GameObject> LoadFBXInEditor(string path, string fileName)
        {
            await Task.Yield();

            // Copiar a Assets para que Unity lo importe
            string assetsPath = $"Assets/ImportedModels/{fileName}";
            
            if (!Directory.Exists("Assets/ImportedModels"))
            {
                Directory.CreateDirectory("Assets/ImportedModels");
            }

            File.Copy(path, assetsPath, true);
            AssetDatabase.Refresh();

            // Cargar el modelo importado
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetsPath);

            if (prefab == null)
            {
                Debug.LogError("[RuntimeModelImporter] No se pudo importar FBX.");
                return null;
            }

            // Instanciar
            GameObject instance = Instantiate(prefab, _modelsParent);
            instance.name = Path.GetFileNameWithoutExtension(fileName);
            instance.transform.localScale = Vector3.one * _defaultScale;

            return instance;
        }
        #endif

        #endregion

        #region GLTF Loading

        /// <summary>
        /// Carga archivo GLTF/GLB (requiere biblioteca como GLTFast).
        /// </summary>
        private async Task<GameObject> LoadGLTFModel(string path, string fileName)
        {
            Debug.LogWarning("[RuntimeModelImporter] ⚠️ GLTF loading requiere paquete 'glTFast' de Unity Package Manager.");
            Debug.LogWarning("[RuntimeModelImporter] Instala: Window → Package Manager → Add by name → com.unity.cloud.gltfast");
            
            // Si tienes glTFast instalado, descomentar:
            /*
            var gltf = gameObject.AddComponent<GLTFast.GltfAsset>();
            bool success = await gltf.Load(path);
            
            if (success)
            {
                GameObject model = gltf.gameObject;
                model.name = Path.GetFileNameWithoutExtension(fileName);
                return model;
            }
            */

            await Task.Yield();
            return null;
        }

        #endregion

        #region Resources Fallback

        /// <summary>
        /// Carga modelo desde Resources como fallback.
        /// </summary>
        private async Task<GameObject> LoadFromResources(string modelName)
        {
            await Task.Yield();

            GameObject prefab = Resources.Load<GameObject>($"{_modelsFolder}/{modelName}");

            if (prefab == null)
            {
                Debug.LogError($"[RuntimeModelImporter] No se encontró prefab en Resources: {_modelsFolder}/{modelName}");
                return null;
            }

            GameObject instance = Instantiate(prefab, _modelsParent);
            instance.transform.localScale = Vector3.one * _defaultScale;

            return instance;
        }

        #endregion

        #region Post-Processing

        /// <summary>
        /// Post-procesa el modelo después de cargarlo.
        /// </summary>
        private void PostProcessModel(GameObject model)
        {
            Debug.Log("[RuntimeModelImporter] 🔧 Post-procesando modelo...");

            // Generar colliders
            if (_generateColliders)
            {
                GenerateColliders(model);
            }

            // Configurar layer
            SetLayerRecursively(model, LayerMask.NameToLayer("Default"));

            // Centrar en origen
            CenterModel(model);
        }

        /// <summary>
        /// Genera colliders automáticamente en el modelo.
        /// </summary>
        private void GenerateColliders(GameObject model)
        {
            MeshFilter[] meshFilters = model.GetComponentsInChildren<MeshFilter>();

            foreach (MeshFilter meshFilter in meshFilters)
            {
                GameObject obj = meshFilter.gameObject;

                // Solo si no tiene collider
                if (obj.GetComponent<Collider>() == null)
                {
                    MeshCollider meshCollider = obj.AddComponent<MeshCollider>();
                    meshCollider.sharedMesh = meshFilter.sharedMesh;
                    meshCollider.convex = false;
                }
            }

            Debug.Log($"[RuntimeModelImporter] ✅ Colliders generados: {meshFilters.Length}");
        }

        /// <summary>
        /// Centra el modelo en el origen basado en sus bounds.
        /// </summary>
        private void CenterModel(GameObject model)
        {
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
            
            if (renderers.Length == 0)
                return;

            Bounds combinedBounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                combinedBounds.Encapsulate(renderers[i].bounds);
            }

            Vector3 offset = model.transform.position - combinedBounds.center;
            model.transform.position = offset;

            Debug.Log($"[RuntimeModelImporter] Modelo centrado en origen.");
        }

        private void SetLayerRecursively(GameObject obj, int layer)
        {
            obj.layer = layer;

            foreach (Transform child in obj.transform)
            {
                SetLayerRecursively(child.gameObject, layer);
            }
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Obtiene la ruta completa del archivo.
        /// </summary>
        private string GetFullPath(string fileName)
        {
            if (_useStreamingAssets)
            {
                return Path.Combine(Application.streamingAssetsPath, _modelsFolder, fileName);
            }
            else if (_usePersistentData)
            {
                return Path.Combine(Application.persistentDataPath, _modelsFolder, fileName);
            }
            else
            {
                // Ruta absoluta directa
                return Path.Combine(_modelsFolder, fileName);
            }
        }

        /// <summary>
        /// Descarga modelo desde URL.
        /// </summary>
        public async Task<GameObject> DownloadAndLoadModel(string url, string fileName)
        {
            try
            {
                Debug.Log($"[RuntimeModelImporter] 🌐 Descargando: {url}");

                using (var www = UnityEngine.Networking.UnityWebRequest.Get(url))
                {
                    var operation = www.SendWebRequest();

                    while (!operation.isDone)
                    {
                        await Task.Yield();
                    }

                    if (www.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"[RuntimeModelImporter] Error descargando: {www.error}");
                        return null;
                    }

                    byte[] data = www.downloadHandler.data;
                    return await LoadModelFromBytes(data, fileName);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RuntimeModelImporter] Error en descarga: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Descarga el modelo actual.
        /// </summary>
        public void UnloadCurrentModel()
        {
            if (_currentModel != null)
            {
                Destroy(_currentModel);
                _currentModel = null;
                _currentModelPath = null;
                Debug.Log("[RuntimeModelImporter] Modelo descargado.");
            }
        }

        #endregion

        #region Debug

        [ContextMenu("Test Load OBJ")]
        private void TestLoadOBJ()
        {
            _ = LoadModelFromFile("test.obj");
        }

        [ContextMenu("Test Load FBX")]
        private void TestLoadFBX()
        {
            _ = LoadModelFromFile("test.fbx");
        }

        #endregion

        #region Data Structures

        /// <summary>
        /// Datos parseados de archivo OBJ.
        /// </summary>
        private class OBJData
        {
            public System.Collections.Generic.List<Vector3> vertices = new System.Collections.Generic.List<Vector3>();
            public System.Collections.Generic.List<Vector2> uvs = new System.Collections.Generic.List<Vector2>();
            public System.Collections.Generic.List<Vector3> normals = new System.Collections.Generic.List<Vector3>();
            public System.Collections.Generic.List<int> triangles = new System.Collections.Generic.List<int>();
        }

        #endregion
    }
}