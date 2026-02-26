// File: PersistenceManager.cs
// ✅ FIX v6 — Recrea geometría física de escaleras al restaurar sesión.
//
//  BUG CORREGIDO (v5 → v6):
//    Al cargar sesión desde disco, LoadNavMeshFromFile() inyectaba el NavMesh
//    correctamente (los vértices de rampa están en el .bin unificado), pero
//    los GameObjects físicos de StairWithLandingHelper (MeshCollider, MeshFilter)
//    NO existían en escena porque _createOnStart = false.
//    Resultado: el NavMesh tenía la geometría correcta pero el agente no podía
//    atravesar la escalera físicamente (sin colliders, caía o se bloqueaba).
//
//  SOLUCIÓN v6:
//    LoadNavMeshFromFile() ahora llama RecreateStairGeometryAsync() justo antes
//    de NotifyNavMeshReady(). Esto garantiza:
//      1) Los NavigationStartPoints ya están registrados (2x Task.Yield)
//         → SnapYToFloorLevel() calcula la Y correcta de cada extremo.
//      2) Los MeshCollider de rampa existen ANTES de que el agente se teleporte.
//      3) Task.Delay(150ms) da tiempo a Unity para procesar los nuevos colliders.
//
//  TODOS los fixes anteriores se mantienen:
//    v5: hasNavMesh doble escritura + auto-corrección discrepancia session.json
//    v4: _loadedInstances (lista para múltiples NavMeshDataInstance)
//    v3: _navMeshWasBaked (previene ciclo de degradación)

using System;
using System.Collections.Generic;
using System.IO;
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

        // ✅ FIX v4: Lista de instancias (una por nivel en NavMeshSerializer v5.0)
        private List<NavMeshDataInstance> _loadedInstances  = new List<NavMeshDataInstance>();
        private bool                      _navMeshInstanceActive = false;

        // ✅ FIX v3: Indica si el NavMesh activo proviene de un bake real.
        private bool _navMeshWasBaked = false;

        // ─── Lifecycle ────────────────────────────────────────────────────

        private void Awake()  => FindDependencies();
        private void Update()
        {
            if (!_autoSaveEnabled) return;
            _timeSinceLastAutoSave += Time.deltaTime;
            if (_timeSinceLastAutoSave >= _autoSaveInterval)
            { _ = SaveSession(); _timeSinceLastAutoSave = 0f; }
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
            Log($"📐 NavMesh guardado: {NavMeshSerializer.HasSavedNavMesh}");
        }

        // ─── API pública ──────────────────────────────────────────────────

        /// <summary>
        /// Llamado por NavMeshAgentCoordinator.GenerateAndSave() después de un bake exitoso.
        /// Activa el flag que permite a SaveSession() sobreescribir los archivos .bin.
        /// </summary>
        public void NotifyNavMeshBaked()
        {
            _navMeshWasBaked = true;
            Log("✅ NavMesh marcado como BAKEADO — próximo SaveSession() guardará archivos .bin");
        }

        // ─── Guardar ──────────────────────────────────────────────────────

        /// <summary>
        /// Guarda la sesión (JSON de waypoints) y opcionalmente los archivos NavMesh.
        ///
        /// ✅ FIX v5: session.json se re-escribe DESPUÉS de guardar el NavMesh
        ///   para que hasNavMesh refleje el estado real del .bin en disco.
        /// </summary>
        public async Task<bool> SaveSession()
        {
            try
            {
                Log("💾 Guardando sesión...");

                // ── Primera escritura del session.json (sin NavMesh aún) ──────
                // Se hace aquí para preservar waypoints/modelo aunque el NavMesh falle.
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
                        // ✅ FIX v5: Re-escribir session.json con hasNavMesh = true
                        // AHORA que los .bin están en disco y HasSavedNavMesh devuelve true.
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
                        // data.hasNavMesh ya es false desde CreateSessionData → no re-escribir
                        string msg = $"Sesión guardada: {data.waypointCount} baliza(s) (sin NavMesh)";
                        PublishMessage(msg, MessageType.Warning);
                        Log($"⚠️ {msg}");
                    }
                }
                else
                {
                    // No hubo bake → preservar .bin existentes.
                    // Si ya había NavMesh en disco, el session.json debe decir true.
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
        }

        /// <summary>
        /// Escribe el session.json de forma async. Separado para poder llamarlo dos veces.
        /// </summary>
        private async Task WriteSessionJson(SessionData data)
        {
            string json = JsonUtility.ToJson(data, true);
            if (_usePlayerPrefs)
                await Task.Run(() => { PlayerPrefs.SetString("SessionData", json); PlayerPrefs.Save(); });
            else
                await Task.Run(() => File.WriteAllText(SaveFilePath, json));
        }

        /// <summary>
        /// ✅ FIX v5: Parámetro navMeshConfirmed para separar el estado "pendiente" del "confirmado".
        /// HasSavedNavMesh puede devolver false si los .bin aún no existen (primera sesión).
        /// </summary>
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

        /// <summary>
        /// Carga sesión con flujo garantizado:
        ///   Paso 1 → RestoreModelTransform
        ///   Paso 2 → Task.Yield x2 + Delay(100ms)
        ///   Paso 3 → LoadNavMeshFromFile  ← ignora data.hasNavMesh, usa HasSavedNavMesh directamente
        ///   Paso 4 → RecreateStairGeometryAsync  ← ✅ FIX v6: recrea colliders de escalera
        ///   Paso 5 → NotifyNavMeshReady   ← desbloquea teleport de StartPoints
        ///   Paso 6 → LoadWaypoints
        /// </summary>
        public async Task<bool> LoadSession()
        {
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

                // ✅ FIX v5: Loguear si hay discrepancia entre session.json y la realidad
                bool navMeshActuallyExists = NavMeshSerializer.HasSavedNavMesh;
                if (data.hasNavMesh != navMeshActuallyExists)
                {
                    Debug.LogWarning($"[PersistenceManager] ⚠️ Discrepancia: session.json dice hasNavMesh={data.hasNavMesh} " +
                                     $"pero HasSavedNavMesh={navMeshActuallyExists}. " +
                                     $"Usando el estado real del disco.");
                    data.hasNavMesh = navMeshActuallyExists;
                    await WriteSessionJson(data);
                    Log("✅ session.json auto-corregido con hasNavMesh real");
                }

                await LoadSessionData(data);

                string resultMsg = $"Sesión cargada: {data.waypointCount} baliza(s)" +
                                   (_navMeshInstanceActive ? " + NavMesh ✓" : " (sin NavMesh)");
                PublishMessage(resultMsg, MessageType.Success);
                Log($"✅ {resultMsg}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PersistenceManager] ❌ Error cargando: {ex.Message}");
                PublishMessage("Error al cargar sesión", MessageType.Error);
                return false;
            }
        }

        private async Task LoadSessionData(SessionData data)
        {
            // ── PASO 1: Restaurar modelo ──────────────────────────────────────
            if (data.hasModel && _modelLoadManager != null)
            {
                Log($"📦 Restaurando modelo: {data.modelName}");
                bool modelOk = await _modelLoadManager.RestoreModelTransform(
                    data.modelPosition, data.modelRotation, data.modelScale);

                if (!modelOk)
                    Debug.LogWarning("[PersistenceManager] ⚠️ No se pudo restaurar modelo");

                await Task.Yield();
                await Task.Yield();
                await Task.Delay(100);
            }

            // ── PASO 2: NavMesh desde binario ─────────────────────────────────
            // ✅ FIX v5: Intentar cargar NavMesh si EXISTE en disco,
            // independientemente de lo que diga data.hasNavMesh.
            // ✅ FIX v6: RecreateStairGeometryAsync se llama dentro de LoadNavMeshFromFile()
            await LoadNavMeshFromFile();

            // ── PASO 3: Waypoints ─────────────────────────────────────────────
            if (_waypointManager != null && data.waypoints?.Count > 0)
            {
                Log($"📍 Cargando {data.waypoints.Count} waypoints");
                _waypointManager.LoadWaypoints(data.waypoints);
            }
        }

        // ─── NavMesh ──────────────────────────────────────────────────────

        /// <summary>
        /// Carga NavMesh desde disco y recrea la geometría física de escaleras.
        ///
        /// ✅ FIX v6: Llama RecreateStairGeometryAsync() ANTES de NotifyNavMeshReady()
        ///   para que los MeshCollider de rampa existan cuando el agente se teleporte.
        ///
        /// ✅ FIX v4: Usa NavMeshSerializer.LoadMulti() para obtener TODAS las
        ///   instancias (una por nivel). Las almacena en _loadedInstances.
        ///
        /// ✅ FIX v3: Desactiva _navMeshWasBaked al cargar desde disco.
        ///
        /// ✅ FIX v5: No depende de data.hasNavMesh — usa HasSavedNavMesh directamente.
        /// </summary>
        public async Task<bool> LoadNavMeshFromFile()
        {
            if (!NavMeshSerializer.HasSavedNavMesh)
            {
                Log("⚠️ No hay NavMesh guardado en disco.");
                return false;
            }

            RemoveLoadedNavMesh();

            Transform modelTf = _modelLoadManager?.CurrentModel?.transform;

            // ✅ FIX v4: LoadMulti devuelve todas las instancias
            var (success, firstInstance, allInstances) =
                await NavMeshSerializer.LoadMulti(modelTf);

            if (success)
            {
                _loadedInstances       = allInstances;
                _navMeshInstanceActive = true;

                // ✅ FIX v3: desactivar flag para no sobreescribir .bin con datos re-voxelizados
                _navMeshWasBaked = false;
                Log($"📐 NavMesh restaurado: {allInstances.Count} instancia(s). " +
                    "_navMeshWasBaked = false (SaveSession preservará los .bin del bake original)");

                // ✅ FIX v6: Recrear geometría física de escaleras ANTES del teleport del agente.
                // Sin esto, los MeshCollider de las rampas no existen → el agente no puede
                // atravesar la escalera aunque el NavMesh tenga la geometría correcta en el .bin.
                await RecreateStairGeometryAsync();

                NavigationStartPointManager.NotifyNavMeshReady();
            }
            else
            {
                Log("❌ Falló la restauración del NavMesh.");
            }

            return success;
        }

        /// <summary>
        /// ✅ FIX v6: Recrea la geometría física de todas las escaleras al cargar sesión.
        ///
        /// PROBLEMA: StairWithLandingHelper tiene _createOnStart = false para evitar
        ///   que se regeneren durante el bake (el baker ya los recolecta manualmente).
        ///   Al restaurar sesión, los GameObjects de rampa (MeshCollider, MeshFilter)
        ///   no existen → el agente no puede cruzar la escalera físicamente.
        ///
        /// SOLUCIÓN: Llamar CreateStairSystem() en cada StairWithLandingHelper.
        ///   El NavMesh ya fue inyectado desde el .bin (geometría correcta).
        ///   Solo necesitamos los colliders físicos para que el agente pueda moverse.
        ///
        /// ORDEN CRÍTICO:
        ///   1) Task.Yield x2 → NavigationStartPoints ya registrados en Manager
        ///      → SnapYToFloorLevel() calcula Y correctamente
        ///   2) CreateStairSystem() → crea MeshCollider, MeshFilter, NavMeshLink
        ///   3) Task.Delay(150ms) → Unity procesa colliders antes del teleport del agente
        /// </summary>
        private async Task RecreateStairGeometryAsync()
        {
            var stairHelpers = FindObjectsByType<StairWithLandingHelper>(FindObjectsSortMode.None);

            if (stairHelpers.Length == 0)
            {
                Log("ℹ️ No hay StairWithLandingHelper en escena — sin escaleras que recrear.");
                return;
            }

            Log($"🪜 Recreando geometría física de {stairHelpers.Length} escalera(s) (sesión restaurada)...");

            // Yield para asegurar que NavigationStartPointManager tiene todos los StartPoints
            // registrados antes de que SnapYToFloorLevel() intente leer su FloorHeight.
            // Sin esto, el Manager puede devolver false y las rampas usan la Y del inspector.
            await Task.Yield();
            await Task.Yield();

            int recreated = 0;
            int failed    = 0;

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
                    Debug.LogWarning($"[PersistenceManager] ⚠️ Error recreando escalera '{helper.name}': " +
                                     $"{ex.Message}");
                }
            }

            Log($"🪜 Escaleras: {recreated} recreadas, {failed} con error.");

            if (recreated > 0)
            {
                // Dar tiempo a Unity para procesar los nuevos colliders en el árbol de física
                // antes de que NotifyNavMeshReady() desencadene el teleport del agente.
                // Sin este delay, el agente puede caer o atravesar la rampa en el primer frame.
                await Task.Delay(150);
                Log("🪜 Colliders de escalera listos para el agente.");
            }
        }

        /// <summary>
        /// Elimina TODAS las instancias NavMesh cargadas desde disco.
        /// ✅ FIX v4: itera sobre _loadedInstances en lugar de una sola instancia.
        /// </summary>
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
                Log($"🗑️ {removed} instancia(s) NavMesh runtime eliminadas.");
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
                    Debug.LogWarning("[PersistenceManager] ⚠️ CalculateTriangulation vacía durante verificación.");
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
                    Debug.LogWarning(
                        $"[PersistenceManager] ⚠️ NavMesh guardado puede ser incompleto: " +
                        $"rango Y={yRange:F2}m para {expectedLevelCount} nivel(es). " +
                        $"Se esperan >2m. Regenera y guarda de nuevo.");
                else
                    Log($"✅ NavMesh verificado: Y=[{minY:F2}, {maxY:F2}] rango={yRange:F2}m — " +
                        $"cubre {expectedLevelCount} nivel(es).");
            }
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private void Log(string msg) { if (_logOperations) Debug.Log($"[PersistenceManager] {msg}"); }

        private void PublishMessage(string msg, MessageType type) =>
            EventBus.Instance?.Publish(new ShowMessageEvent
            { Message = msg, Type = type, Duration = type == MessageType.Error ? 5f : 3f });

        // ─── ContextMenu ──────────────────────────────────────────────────

        [ContextMenu("💾 Save Session")]       private void DbgSave()    => _ = SaveSession();
        [ContextMenu("📂 Load Session")]       private void DbgLoad()    => _ = LoadSession();
        [ContextMenu("🗺️ Load NavMesh Only")]  private void DbgNavMesh() => _ = LoadNavMeshFromFile();
        [ContextMenu("🗑️ Clear All Data")]     private void DbgClear()   => ClearSavedData();
        [ContextMenu("ℹ️ Show Info")]           private void DbgInfo()    => Debug.Log(GetLastSaveInfo());
        [ContextMenu("📐 NavMesh Info")]        private void DbgNavInfo() => Debug.Log(NavMeshSerializer.GetSavedInfo());
        [ContextMenu("🔍 Verify NavMesh Save")] private void DbgVerify()  => LogNavMeshSaveVerification(_navMeshGenerator?.DetectedLevelCount ?? 1);
        [ContextMenu("🔥 Force Baked Flag")]   private void DbgBakedFlag() { NotifyNavMeshBaked(); Log("🔥 _navMeshWasBaked forzado a true (usa solo para testing)"); }
        [ContextMenu("📊 Instance Count")]     private void DbgInstances() => Debug.Log($"[PersistenceManager] Instancias activas: {_loadedInstances.Count}");

        /// <summary>
        /// ✅ FIX v6: Recrea escaleras manualmente desde el ContextMenu (útil para debugging).
        /// </summary>
        [ContextMenu("🪜 Recrear Escaleras Manualmente")]
        private void DbgRecreateStairs() => _ = RecreateStairGeometryAsync();

        /// <summary>
        /// ✅ FIX v5: Auto-repara el session.json si hasNavMesh no coincide con la realidad.
        /// </summary>
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