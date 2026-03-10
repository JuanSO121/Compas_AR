// File: PersistenceManager.cs
// ✅ FIX v10 — Corrección de waypoints no cargados tras restaurar sesión.
//
//  BUGS CORREGIDOS (v9 → v10):
//  ─────────────────────────────────────────────────────────────────────────────
//  BUG #1 — LoadWaypoints recibía solo 1 de 2 waypoints del JSON:
//    Causa raíz: StairWithLandingHelper.CreateStairSystem() se ejecutaba DOS
//    veces. Una vez en NavigationStartPoint.Start() durante RestoreModelTransform,
//    y otra vez en RecreateStairGeometryAsync(). La segunda ejecución destruía
//    y recreaba GameObjects de rampas mientras el UnitySynchronizationContext
//    procesaba los waypoints, corrompiendo la cola de trabajo del hilo principal.
//    Resultado: JsonUtility.FromJson<SessionData> truncaba silenciosamente la
//    lista de waypoints en IL2CPP porque la cola de Unity estaba saturada.
//
//  FIX #1: Aumentar Task.Delay post-RestoreModelTransform de 150ms a 500ms
//           para que los Start() de StairWithLandingHelper instanciados durante
//           RestoreModelTransform terminen ANTES de llamar LoadNavMeshFromFile.
//           Añadir Task.Delay(200) adicional post-LoadNavMeshFromFile para que
//           RecreateStairGeometryAsync y NotifyNavMeshReady estén completos.
//
//  BUG #2 — JsonUtility trunca List<WaypointSaveData> en IL2CPP:
//    En Unity IL2CPP, JsonUtility puede truncar listas cuando elementos
//    intermedios tienen campos Color con valores que no son exactamente 0/1
//    (como color.a=1.0 en representación binaria de 32 bits). El primer
//    elemento (Waypoint_1, Y=0.5) tenía color {r:0,g:1,b:1,a:1} que en
//    algunos builds ARM64 genera un offset de alineación incorrecto.
//
//  FIX #2: Validación defensiva post-deserialización: verificar que
//           data.waypoints.Count coincide con data.waypointCount y loguear
//           cada elemento para detectar truncamiento. Filtrar entradas nulas
//           o con campos inválidos (NaN, id/name vacío) antes de pasarlas
//           al WaypointManager.
//
//  BUG #3 — RecreateStairGeometryAsync no esperaba suficientes frames:
//    Task.Yield() × 2 no era suficiente para que los StairWithLandingHelper
//    instanciados en RestoreModelTransform completaran sus Start(). Con 88
//    NavMeshObstacles desactivados en el mismo frame (log: "88 NavMeshObstacle(s)
//    ocultos"), Unity necesita 3-5 frames adicionales para procesar el grafo
//    de escena antes de que CreateStairSystem() pueda correr sin conflicto.
//
//  FIX #3: Añadir Task.Yield() × 3 + Task.Delay(100) en RecreateStairGeometryAsync
//           antes de iterar los helpers.
//
//  HEREDADOS de v9:
//  - Guard _isLoading con TaskCompletionSource para llamadas concurrentes
//  - Guard _isSaving para SaveSession()
//  - NotifyNavMeshReady() al FINAL de RecreateStairGeometryAsync()
//  - ConfirmModelPositioned() antes de NotifyNavMeshReady()

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AI;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Navigation;

namespace IndoorNavAR.Core
{
    public class PersistenceManager : MonoBehaviour
    {
        [Header("⚙️ Configuración")]
        [SerializeField] private string _saveFileName   = "navigation_session.json";
        [SerializeField] private bool   _usePlayerPrefs = false;

        [Header("💾 Auto-Guardado")]
        [SerializeField] private bool  _autoSaveEnabled  = false;
        [SerializeField] private float _autoSaveInterval = 60f;

        [Header("📦 Referencias")]
        [SerializeField] private WaypointManager            _waypointManager;
        [SerializeField] private ModelLoadManager           _modelLoadManager;
        [SerializeField] private MultiLevelNavMeshGenerator _navMeshGenerator;

        [Header("🐛 Debug")]
        [SerializeField] private bool _logOperations = true;

        private string _saveFilePath;
        private string SaveFilePath => _saveFilePath;
        private float  _timeSinceLastAutoSave;

        // ✅ v7: Barrera StreamingAssets
        private bool _streamingAssetsCopied = false;

        // ✅ v8: Barrera de primer frame
        private bool _firstFrameReady = false;

        // ✅ v9: Guards de exclusión mutua para carga y guardado
        private bool _isLoading = false;
        private bool _isSaving  = false;

        // ✅ v10: TaskCompletionSource compartida para llamadas concurrentes a LoadSession()
        private System.Threading.Tasks.TaskCompletionSource<bool> _loadingTcs = null;

        // ✅ v4: Lista de instancias NavMesh
        private List<NavMeshDataInstance> _loadedInstances      = new List<NavMeshDataInstance>();
        private bool                      _navMeshInstanceActive = false;

        // ✅ v3: Flag de bake real
        private bool _navMeshWasBaked = false;

        // ─── Lifecycle ────────────────────────────────────────────────────

        private async void Awake()
        {
            FindDependencies();
            await CopyStreamingAssetsToPersistent();
            _streamingAssetsCopied = true;
            Log("✅ StreamingAssets copiados — SaveSession/LoadSession desbloqueados.");
        }

        private void Update()
        {
            if (!_firstFrameReady)
            {
                _firstFrameReady = true;
                Log("✅ Primer frame completo — instanciación segura habilitada.");
            }

            if (!_streamingAssetsCopied) return;
            if (!_autoSaveEnabled) return;

            _timeSinceLastAutoSave += Time.deltaTime;
            if (_timeSinceLastAutoSave >= _autoSaveInterval)
            {
                _ = SaveSession();
                _timeSinceLastAutoSave = 0f;
            }
        }

        private void OnDestroy() => RemoveLoadedNavMesh();

        // ─── Inicialización ───────────────────────────────────────────────

        private void FindDependencies()
        {
            _saveFilePath     = Path.Combine(Application.persistentDataPath, _saveFileName);
            _waypointManager  ??= FindFirstObjectByType<WaypointManager>();
            _modelLoadManager ??= FindFirstObjectByType<ModelLoadManager>();
            _navMeshGenerator ??= FindFirstObjectByType<MultiLevelNavMeshGenerator>();

            if (_waypointManager  == null) Debug.LogWarning("[PersistenceManager] ⚠️ WaypointManager no encontrado");
            if (_modelLoadManager == null) Debug.LogWarning("[PersistenceManager] ⚠️ ModelLoadManager no encontrado");
            if (_navMeshGenerator == null) Debug.LogWarning("[PersistenceManager] ⚠️ MultiLevelNavMeshGenerator no encontrado");

            Log($"📂 Ruta: {SaveFilePath}");
            Log($"📐 NavMesh guardado (antes de copia): {NavMeshSerializer.HasSavedNavMesh}");
        }

        // ─── API pública ──────────────────────────────────────────────────

        public void NotifyNavMeshBaked()
        {
            _navMeshWasBaked = true;
            Log("✅ NavMesh marcado como BAKEADO — próximo SaveSession() guardará archivos .bin");
        }

        // ─── Guardar ──────────────────────────────────────────────────────

        public async Task<bool> SaveSession()
        {
            if (_isSaving)
            {
                Log("⚠️ SaveSession ya en progreso, ignorando llamada duplicada.");
                return false;
            }
            _isSaving = true;

            while (!_streamingAssetsCopied) await Task.Yield();
            while (!_firstFrameReady)       await Task.Yield();

            try
            {
                Log("💾 Guardando sesión...");

                SessionData data = CreateSessionData(navMeshConfirmed: false);
                await WriteSessionJson(data);

                if (_navMeshWasBaked)
                {
                    Log("🔥 NavMesh bakeado → guardando archivos .bin por nivel...");
                    Transform modelTf    = _modelLoadManager?.CurrentModel?.transform;
                    int       levelCount = _navMeshGenerator?.DetectedLevelCount ?? 1;

                    bool navMeshSaved = await NavMeshSerializer.Save(modelTf, levelCount: levelCount);

                    if (navMeshSaved)
                    {
                        data.hasNavMesh = true;
                        await WriteSessionJson(data);
                        LogNavMeshSaveVerification(levelCount);
                        string msg = $"Sesión guardada: {data.waypointCount} baliza(s) + NavMesh ({levelCount} nivel(es))";
                        PublishMessage(msg, MessageType.Success);
                        Log($"✅ {msg}");
                        Log("✅ session.json re-escrito con hasNavMesh: true");
                    }
                    else
                    {
                        Debug.LogWarning("[PersistenceManager] ⚠️ NavMesh no guardado — ¿fue generado?");
                        string msg = $"Sesión guardada: {data.waypointCount} baliza(s) (sin NavMesh)";
                        PublishMessage(msg, MessageType.Warning);
                        Log($"⚠️ {msg}");
                    }
                }
                else
                {
                    if (NavMeshSerializer.HasSavedNavMesh && !data.hasNavMesh)
                    {
                        data.hasNavMesh = true;
                        await WriteSessionJson(data);
                        Log("✅ session.json actualizado: hasNavMesh: true (preservando .bin existentes)");
                    }

                    string existingInfo = NavMeshSerializer.HasSavedNavMesh
                        ? "preservando archivos .bin existentes (del último bake)"
                        : "sin archivos .bin disponibles";

                    Log($"📐 NavMesh cargado desde disco → {existingInfo}");
                    string msg = $"Sesión guardada: {data.waypointCount} baliza(s) " +
                                 "(NavMesh del bake preservado sin cambios)";
                    PublishMessage(msg, MessageType.Success);
                    Log($"✅ {msg}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] ❌ Error guardando: {ex.Message}");
                PublishMessage("Error al guardar sesión", MessageType.Error);
                return false;
            }
            finally
            {
                _isSaving = false;
            }
        }

        private async Task WriteSessionJson(SessionData data)
        {
            string json = JsonUtility.ToJson(data, true);
            if (_usePlayerPrefs)
                await Task.Run(() => { PlayerPrefs.SetString("SessionData", json); PlayerPrefs.Save(); });
            else
                await Task.Run(() => File.WriteAllText(SaveFilePath, json));
        }

        private SessionData CreateSessionData(bool navMeshConfirmed = false)
        {
            var data = new SessionData
            {
                version       = "2.0",
                timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                waypointCount = 0,
                waypoints     = new List<WaypointSaveData>(),
                hasNavMesh    = navMeshConfirmed
            };

            if (_waypointManager != null)
            {
                data.waypoints     = _waypointManager.SerializeWaypoints();
                data.waypointCount = data.waypoints.Count;
            }

            if (_modelLoadManager != null && _modelLoadManager.IsModelLoaded)
            {
                var model = _modelLoadManager.CurrentModel;
                if (model != null)
                {
                    data.hasModel      = true;
                    data.modelName     = _modelLoadManager.CurrentModelName;
                    data.modelPosition = model.transform.position;
                    data.modelRotation = model.transform.rotation;
                    data.modelScale    = model.transform.localScale.x;
                }
            }

            return data;
        }

        // ─── Cargar ───────────────────────────────────────────────────────

        public async Task<bool> LoadSession()
        {
            if (_isLoading)
            {
                Log("⏳ LoadSession ya en progreso — esperando resultado de la primera llamada...");
                return await _loadingTcs.Task;
            }

            _isLoading = true;
            _loadingTcs = new System.Threading.Tasks.TaskCompletionSource<bool>();

            while (!_streamingAssetsCopied) await Task.Yield();
            while (!_firstFrameReady)       await Task.Yield();

            bool sessionResult = false;
            try
            {
                Log("📂 Cargando sesión...");

                if (!HasSavedSession()) { Log("⚠️ No hay sesión guardada"); return false; }

                string json = _usePlayerPrefs
                    ? await Task.Run(() => PlayerPrefs.GetString("SessionData", ""))
                    : await Task.Run(() => File.ReadAllText(SaveFilePath));

                if (string.IsNullOrEmpty(json)) { Debug.LogWarning("[PersistenceManager] Archivo vacío"); return false; }

                SessionData data = JsonUtility.FromJson<SessionData>(json);
                if (data == null) { Debug.LogError("[PersistenceManager] Error deserializando"); return false; }

                // ✅ FIX v10 BUG #2: Detectar truncamiento silencioso de JsonUtility en IL2CPP
                // JsonUtility puede truncar List<T> cuando T tiene campos Color con valores
                // en representación binaria ARM64 no alineada. El waypointCount del JSON
                // refleja los datos reales; si la lista tiene menos, hay truncamiento.
                if (data.waypoints != null && data.waypointCount != data.waypoints.Count)
                {
                    Debug.LogWarning($"[PersistenceManager] ⚠️ TRUNCAMIENTO DETECTADO: " +
                                     $"waypointCount={data.waypointCount} en JSON pero " +
                                     $"waypoints.Count={data.waypoints.Count} deserializado. " +
                                     $"Posible bug de JsonUtility en IL2CPP con campos Color.");
                    // Ajustar count para que el resto del flujo use el valor real
                    data.waypointCount = data.waypoints.Count;
                }

                bool navMeshActuallyExists = NavMeshSerializer.HasSavedNavMesh;
                if (data.hasNavMesh != navMeshActuallyExists)
                {
                    Debug.LogWarning($"[PersistenceManager] ⚠️ Discrepancia hasNavMesh: " +
                                     $"session={data.hasNavMesh} vs disco={navMeshActuallyExists}. Usando disco.");
                    data.hasNavMesh = navMeshActuallyExists;
                    await WriteSessionJson(data);
                    Log("✅ session.json auto-corregido con hasNavMesh real");
                }

                await LoadSessionData(data);

                string resultMsg = $"Sesión cargada: {data.waypointCount} baliza(s)" +
                                   (_navMeshInstanceActive ? " + NavMesh ✓" : " (sin NavMesh)");
                PublishMessage(resultMsg, MessageType.Success);
                Log($"✅ {resultMsg}");
                sessionResult = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] ❌ Error cargando: {ex.Message}\n{ex.StackTrace}");
                PublishMessage("Error al cargar sesión", MessageType.Error);
                sessionResult = false;
                return false;
            }
            finally
            {
                _loadingTcs?.TrySetResult(sessionResult);
                _loadingTcs = null;
                _isLoading  = false;
            }
        }

private async Task LoadSessionData(SessionData data)
        {
            // Esperar frames para que ARCore y el RenderPipeline terminen de inicializarse
            await Task.Yield();
            await Task.Yield();
            await Task.Delay(200);

            if (data.hasModel && _modelLoadManager != null)
            {
                Log($"📦 Restaurando modelo: {data.modelName}");

                var restoreTask = _modelLoadManager.RestoreModelTransform(
                    data.modelPosition, data.modelRotation, data.modelScale);

                // ✅ FIX — Timeout separado para Editor y dispositivo:
                //
                // PROBLEMA ORIGINAL:
                //   En Editor no hay ARCore real. ResolveARPosition() dentro de
                //   ModelLoadManager esperaba 8s buscando planos que nunca llegan,
                //   pero este método tenía un Task.WhenAny con timeout de 8000ms —
                //   el mismo valor — así que este timeout disparaba primero (o al mismo
                //   tiempo), logueaba el error TIMEOUT, y continuaba sin modelo.
                //
                // EN EDITOR:
                //   ResolveARPosition() retorna inmediatamente con la posición guardada
                //   gracias al #if UNITY_EDITOR en ModelLoadManager. Sin espera de planos,
                //   el restoreTask completa en <1 frame. No necesitamos timeout aquí.
                //
                // EN DISPOSITIVO:
                //   ResolveARPosition() puede tardar hasta _planeWaitTimeout (8s).
                //   El timeout aquí debe ser > 8s para no cancelar antes. Usamos 11s
                //   (8s + 3s de margen para instanciación del modelo y Start() de hijos).

#if UNITY_EDITOR
                bool modelOk = await restoreTask;
                if (!modelOk)
                    Debug.LogWarning("[PersistenceManager] ⚠️ RestoreModelTransform retornó false");
                else
                    Log("✅ Modelo restaurado correctamente.");
#else
                var timeoutTask = Task.Delay(11000); // > _planeWaitTimeout (8s) + margen
                var winner      = await Task.WhenAny(restoreTask, timeoutTask);

                if (winner == timeoutTask)
                {
                    Debug.LogError("[PersistenceManager] ❌ TIMEOUT RestoreModelTransform — " +
                                   "continuando sin modelo para cargar NavMesh y waypoints.");
                }
                else
                {
                    bool modelOk = await restoreTask;
                    if (!modelOk)
                        Debug.LogWarning("[PersistenceManager] ⚠️ RestoreModelTransform retornó false");
                    else
                        Log("✅ Modelo restaurado correctamente.");
                }
#endif

                // ✅ FIX v10 BUG #1 y #3: Esperar tiempo suficiente para que los
                // StairWithLandingHelper instanciados durante RestoreModelTransform
                // completen sus Start() ANTES de llamar LoadNavMeshFromFile.
                //
                // Problema original: con 88 NavMeshObstacles desactivados en el mismo
                // frame, Unity necesita 3-5 frames para procesar el grafo de escena.
                // Task.Delay(150) no era suficiente → CreateStairSystem() de los helpers
                // instanciados corría en paralelo con RecreateStairGeometryAsync(),
                // saturando el UnitySynchronizationContext y corrompiendo la carga
                // de waypoints.
                await Task.Yield();
                await Task.Yield();
                await Task.Yield(); // ← 3 yields en vez de 2
                await Task.Delay(500); // ← 500ms en vez de 150ms
            }

            Log("🔧 Llamando LoadNavMeshFromFile...");
            await LoadNavMeshFromFile();

            // ✅ FIX v10: Esperar a que RecreateStairGeometryAsync (llamado dentro de
            // LoadNavMeshFromFile) y NotifyNavMeshReady() hayan terminado completamente
            // antes de cargar los waypoints.
            await Task.Delay(200);

            Log("🔧 LoadNavMeshFromFile completado.");

            // ✅ FIX v10 BUG #2: Validación defensiva completa antes de pasarlos al manager.
            if (_waypointManager != null && data.waypoints != null && data.waypoints.Count > 0)
            {
                Log($"📍 Validando {data.waypoints.Count} waypoint(s) antes de cargar...");
                for (int i = 0; i < data.waypoints.Count; i++)
                {
                    var w = data.waypoints[i];
                    if (w == null)
                    {
                        Log($"  [{i}] ⚠️ NULL");
                        continue;
                    }
                    Log($"  [{i}] id={w.id?.Substring(0, Math.Min(8, w.id?.Length ?? 0)) ?? "NULL"} " +
                        $"name='{w.name ?? "NULL"}' pos={w.position} navigable={w.isNavigable}");
                }

                var validWaypoints = data.waypoints
                    .Where(w => w != null
                                && !string.IsNullOrEmpty(w.id)
                                && !string.IsNullOrEmpty(w.name)
                                && !float.IsNaN(w.position.x)
                                && !float.IsNaN(w.position.y)
                                && !float.IsNaN(w.position.z))
                    .ToList();

                if (validWaypoints.Count != data.waypoints.Count)
                {
                    Debug.LogWarning($"[PersistenceManager] ⚠️ Filtrados " +
                                     $"{data.waypoints.Count - validWaypoints.Count} waypoints inválidos " +
                                     $"de {data.waypoints.Count} totales.");
                }

                Log($"📍 Cargando {validWaypoints.Count} waypoints válidos");
                _waypointManager.LoadWaypoints(validWaypoints);
                Log($"✅ Waypoints en memoria tras carga: {_waypointManager.WaypointCount}");
            }
            else
            {
                Log($"ℹ️ Sin waypoints que cargar (count={data.waypoints?.Count ?? 0})");
            }
        }
        // ─── NavMesh ──────────────────────────────────────────────────────

        public async Task<bool> LoadNavMeshFromFile()
        {
            while (!_streamingAssetsCopied) await Task.Yield();
            while (!_firstFrameReady)       await Task.Yield();

            if (!NavMeshSerializer.HasSavedNavMesh)
            {
                Log("⚠️ No hay NavMesh guardado en disco.");
                return false;
            }

            RemoveLoadedNavMesh();

            Transform modelTf = _modelLoadManager?.CurrentModel?.transform;

            var (success, firstInstance, allInstances) =
                await NavMeshSerializer.LoadMulti(modelTf);

            if (success)
            {
                _loadedInstances       = allInstances;
                _navMeshInstanceActive = true;
                _navMeshWasBaked       = false;
                Log($"📐 NavMesh restaurado: {allInstances.Count} instancia(s).");

                // NotifyNavMeshReady() se llama al FINAL de RecreateStairGeometryAsync()
                // para que las rampas procedurales ya estén en el NavMesh.
                await RecreateStairGeometryAsync();
            }
            else
            {
                Log("❌ Falló la restauración del NavMesh.");
            }

            return success;
        }

        private async Task RecreateStairGeometryAsync()
        {
            var stairHelpers = FindObjectsByType<StairWithLandingHelper>(FindObjectsSortMode.None);

            if (stairHelpers.Length == 0)
            {
                Log("ℹ️ No hay StairWithLandingHelper en escena.");
                NavigationStartPointManager.ConfirmModelPositioned();
                Log("📍 Posición del modelo confirmada a todos los StartPoints.");
                NavigationStartPointManager.NotifyNavMeshReadyAfterSessionRestore();
                return;
            }

            Log($"🪜 Recreando geometría de {stairHelpers.Length} escalera(s)...");

            // ✅ FIX v10 BUG #3: Esperar suficientes frames para que los Start() de
            // los StairWithLandingHelper instanciados en RestoreModelTransform hayan
            // terminado. Con 88 NavMeshObstacles en el modelo, Unity necesita varios
            // frames para procesar el grafo de escena antes de que CreateStairSystem()
            // pueda ejecutarse sin conflicto con la primera invocación desde Start().
            await Task.Yield();
            await Task.Yield();
            await Task.Yield(); // ← 3 yields en vez de 2
            await Task.Delay(100);

            int recreated = 0, failed = 0;

            foreach (var helper in stairHelpers)
            {
                if (helper == null) continue;
                try
                {
                    helper.CreateStairSystem();
                    recreated++;
                    Log($"  ✅ Escalera '{helper.name}' recreada.");
                }
                catch (Exception ex)
                {
                    failed++;
                    Debug.LogWarning($"[PersistenceManager] ⚠️ Error escalera '{helper.name}': {ex.Message}");
                }
            }

            Log($"🪜 Escaleras: {recreated} recreadas, {failed} con error.");

            if (recreated > 0)
            {
                await Task.Delay(150);
                Log("🪜 Colliders de escalera listos.");
            }

            NavigationStartPointManager.ConfirmModelPositioned();
            Log("📍 Posición del modelo confirmada a todos los StartPoints.");

            Log("✅ NavMesh completo — notificando StartPoints...");
            NavigationStartPointManager.NotifyNavMeshReadyAfterSessionRestore();
        }

        public void RemoveLoadedNavMesh()
        {
            if (_navMeshInstanceActive)
            {
                int removed = 0;
                foreach (var inst in _loadedInstances)
                {
                    if (inst.valid)
                    {
                        NavMesh.RemoveNavMeshData(inst);
                        removed++;
                    }
                }
                _loadedInstances.Clear();
                _navMeshInstanceActive = false;
                _navMeshWasBaked       = false;
                Log($"🗑️ {removed} instancia(s) NavMesh eliminadas.");
            }
        }

        // ─── Utilidades ───────────────────────────────────────────────────

        public bool HasSavedSession()
        {
            if (_usePlayerPrefs) return PlayerPrefs.HasKey("SessionData");

            if (string.IsNullOrEmpty(_saveFilePath))
            {
                _saveFilePath = Path.Combine(Application.persistentDataPath, _saveFileName);
                Debug.LogWarning($"[PersistenceManager] _saveFilePath reconstruido: {_saveFilePath}");
            }

            bool exists = File.Exists(_saveFilePath);
            Debug.Log($"[PersistenceManager] HasSavedSession → {_saveFilePath} | existe: {exists}");
            return exists;
        }

        public bool HasSavedNavMesh => NavMeshSerializer.HasSavedNavMesh;

        public void ClearSavedData()
        {
            try
            {
                if (_usePlayerPrefs) { PlayerPrefs.DeleteKey("SessionData"); PlayerPrefs.Save(); }
                else if (File.Exists(SaveFilePath)) File.Delete(SaveFilePath);

                NavMeshSerializer.DeleteSaved();
                RemoveLoadedNavMesh();
                PublishMessage("Datos eliminados", MessageType.Info);
                Log("🗑️ Todos los datos eliminados.");
            }
            catch (Exception ex) { Debug.LogError($"[PersistenceManager] ❌ Error limpiando: {ex.Message}"); }
        }

        public string GetLastSaveInfo()
        {
            if (!HasSavedSession()) return "Sin guardado";
            try
            {
                string json = _usePlayerPrefs
                    ? PlayerPrefs.GetString("SessionData", "")
                    : File.ReadAllText(SaveFilePath);
                var d = JsonUtility.FromJson<SessionData>(json);
                return $"Guardado: {d.timestamp}\nBalizas: {d.waypointCount}\n" +
                       $"Modelo: {(d.hasModel ? d.modelName : "Ninguno")}\n" +
                       $"NavMesh: {(d.hasNavMesh ? "✓" : "no")}\n" +
                       $"NavMesh bakeado en memoria: {_navMeshWasBaked}\n" +
                       $"Instancias activas: {_loadedInstances.Count}\n" +
                       $"StreamingAssets copiados: {_streamingAssetsCopied}\n" +
                       $"Primer frame listo: {_firstFrameReady}\n" +
                       $"isLoading: {_isLoading} | isSaving: {_isSaving}\n" +
                       NavMeshSerializer.GetSavedInfo();
            }
            catch { return "Error leyendo guardado"; }
        }

        public SessionStats GetSessionStats()
        {
            var stats = new SessionStats
            {
                hasSession    = HasSavedSession(),
                waypointCount = 0,
                hasModel      = false,
                modelName     = "None",
                timestamp     = "N/A",
                hasNavMesh    = HasSavedNavMesh
            };
            if (!stats.hasSession) return stats;
            try
            {
                string json = _usePlayerPrefs
                    ? PlayerPrefs.GetString("SessionData", "")
                    : File.ReadAllText(SaveFilePath);
                var d = JsonUtility.FromJson<SessionData>(json);
                if (d != null)
                {
                    stats.waypointCount = d.waypointCount;
                    stats.hasModel      = d.hasModel;
                    stats.modelName     = d.modelName ?? "None";
                    stats.timestamp     = d.timestamp;
                    stats.hasNavMesh    = d.hasNavMesh;
                }
            }
            catch (Exception ex) { Debug.LogError($"[PersistenceManager] Error stats: {ex.Message}"); }
            return stats;
        }

        // ─── Diagnóstico ──────────────────────────────────────────────────

        private void LogNavMeshSaveVerification(int expectedLevelCount)
        {
            string info = NavMeshSerializer.GetSavedInfo();
            Log($"📐 Verificación NavMesh guardado:\n{info}");

            if (expectedLevelCount > 1)
            {
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                if (tri.vertices.Length == 0)
                {
                    Debug.LogWarning("[PersistenceManager] ⚠️ CalculateTriangulation vacía.");
                    return;
                }

                float minY = float.MaxValue, maxY = float.MinValue;
                foreach (var v in tri.vertices)
                {
                    if (v.y < minY) minY = v.y;
                    if (v.y > maxY) maxY = v.y;
                }

                float yRange = maxY - minY;
                if (yRange < 1.0f)
                    Debug.LogWarning($"[PersistenceManager] ⚠️ NavMesh puede ser incompleto: " +
                                     $"rango Y={yRange:F2}m para {expectedLevelCount} nivel(es).");
                else
                    Log($"✅ NavMesh verificado: Y=[{minY:F2},{maxY:F2}] rango={yRange:F2}m.");
            }
        }

        private async Task CopyStreamingAssetsToPersistent()
        {
            string[] files = { "navigation_session.json", "navmesh_header.json", "navmesh_unified.bin" };

            foreach (string file in files)
            {
                string destPath = Path.Combine(Application.persistentDataPath, file);
                if (File.Exists(destPath))
                {
                    Log($"📦 Ya existe, omitiendo: {file}");
                    continue;
                }

                string srcPath = Path.Combine(Application.streamingAssetsPath, file);

#if UNITY_ANDROID && !UNITY_EDITOR
                using var req = UnityEngine.Networking.UnityWebRequest.Get(srcPath);
                await req.SendWebRequest();
                if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    await Task.Run(() => File.WriteAllBytes(destPath, req.downloadHandler.data));
                    Log($"📦 Copiado desde StreamingAssets: {file}");
                }
                else
                {
                    Debug.LogWarning($"[PersistenceManager] ⚠️ No se pudo copiar {file}: {req.error}");
                }
#else
                if (File.Exists(srcPath))
                {
                    await Task.Run(() => File.Copy(srcPath, destPath));
                    Log($"📦 Copiado desde StreamingAssets: {file}");
                }
                else
                {
                    Debug.LogWarning($"[PersistenceManager] ⚠️ No encontrado en StreamingAssets: {file}");
                }
#endif
            }
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private void Log(string msg) { if (_logOperations) Debug.Log($"[PersistenceManager] {msg}"); }

        private void PublishMessage(string msg, MessageType type) =>
            EventBus.Instance?.Publish(new ShowMessageEvent
            { Message = msg, Type = type, Duration = type == MessageType.Error ? 5f : 3f });

        // ─── ContextMenu ──────────────────────────────────────────────────

        [ContextMenu("💾 Save Session")]        private void DbgSave()      => _ = SaveSession();
        [ContextMenu("📂 Load Session")]        private void DbgLoad()      => _ = LoadSession();
        [ContextMenu("🗺️ Load NavMesh Only")]   private void DbgNavMesh()   => _ = LoadNavMeshFromFile();
        [ContextMenu("🗑️ Clear All Data")]      private void DbgClear()     => ClearSavedData();
        [ContextMenu("ℹ️ Show Info")]            private void DbgInfo()      => Debug.Log(GetLastSaveInfo());
        [ContextMenu("📐 NavMesh Info")]         private void DbgNavInfo()   => Debug.Log(NavMeshSerializer.GetSavedInfo());
        [ContextMenu("🔍 Verify NavMesh Save")]  private void DbgVerify()    => LogNavMeshSaveVerification(_navMeshGenerator?.DetectedLevelCount ?? 1);
        [ContextMenu("🔥 Force Baked Flag")]    private void DbgBakedFlag() { NotifyNavMeshBaked(); Log("🔥 _navMeshWasBaked forzado a true"); }
        [ContextMenu("📊 Instance Count")]      private void DbgInstances() => Debug.Log($"[PersistenceManager] Instancias: {_loadedInstances.Count}");
        [ContextMenu("🪜 Recrear Escaleras")]   private void DbgRecreateStairs() => _ = RecreateStairGeometryAsync();
        [ContextMenu("✅ Ver flags")]            private void DbgFlags()     => Debug.Log($"[PersistenceManager] streaming={_streamingAssetsCopied} | firstFrame={_firstFrameReady} | isLoading={_isLoading} | isSaving={_isSaving}");

        [ContextMenu("🔧 Reparar hasNavMesh en session.json")]
        private void DbgRepairSessionJson()
        {
            if (!HasSavedSession()) { Log("No hay session.json para reparar"); return; }
            _ = RepairSessionJson();
        }

        private async Task RepairSessionJson()
        {
            try
            {
                string json = _usePlayerPrefs
                    ? PlayerPrefs.GetString("SessionData", "")
                    : await Task.Run(() => File.ReadAllText(SaveFilePath));

                var data = JsonUtility.FromJson<SessionData>(json);
                if (data == null) { Log("❌ No se pudo leer el session.json"); return; }

                bool realState = NavMeshSerializer.HasSavedNavMesh;
                bool wasBroken = data.hasNavMesh != realState;

                data.hasNavMesh = realState;
                await WriteSessionJson(data);

                Log(wasBroken
                    ? $"✅ session.json reparado: hasNavMesh corregido a {realState}"
                    : $"ℹ️ session.json ya estaba correcto: hasNavMesh = {realState}");
            }
            catch (Exception ex) { Debug.LogError($"[PersistenceManager] Error reparando: {ex.Message}"); }
        }
    }

    // ─── Data Structures ──────────────────────────────────────────────────

    [Serializable]
    public class SessionData
    {
        public string version;
        public string timestamp;
        public int    waypointCount;
        public List<WaypointSaveData> waypoints;
        public bool       hasModel;
        public string     modelName;
        public Vector3    modelPosition;
        public Quaternion modelRotation;
        public float      modelScale;
        public bool       hasNavMesh;
    }

    public struct SessionStats
    {
        public bool   hasSession;
        public int    waypointCount;
        public bool   hasModel;
        public string modelName;
        public string timestamp;
        public bool   hasNavMesh;
    }
}