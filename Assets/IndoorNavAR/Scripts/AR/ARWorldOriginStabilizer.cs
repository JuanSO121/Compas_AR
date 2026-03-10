// File: ARWorldOriginStabilizer.cs
// ✅ v1.1 — Eliminar suscripción propia a ARSession.stateChanged.
//
// ============================================================================
//  CAMBIOS v1.0 → v1.1
// ============================================================================
//
//  BUG CORREGIDO: Condición de carrera en VIO recovery.
//
//  PROBLEMA en v1.0:
//    Tanto ARWorldOriginStabilizer como AROriginAligner estaban suscritos a
//    ARSession.stateChanged. Al recuperarse el tracking (→ SessionTracking):
//
//    Orden de ejecución NO determinista entre los dos callbacks:
//
//    Caso A (ARWorldOriginStabilizer primero):
//      1. Stabilizer detecta SessionTracking → llama ScheduleAnchorRecapture()
//         → _anchorCaptured=false, _isStabilizing=false, inicia coroutine.
//      2. AROriginAligner detecta VIO recovery → llama DisableStabilization()
//         → _isStabilizing=false (ya estaba false, sin efecto).
//      3. AROriginAligner termina RealignAfterVIORecovery() → llama
//         ScheduleAnchorRecapture() OTRA VEZ → segunda coroutine en paralelo.
//      Resultado: dos coroutines RecaptureAfterDelay() corriendo en paralelo.
//      La segunda sobreescribe el anchor de la primera → anchor incorrecto.
//
//    Caso B (AROriginAligner primero):
//      1. AROriginAligner detecta VIO → DisableStabilization() → inicia
//         RealignAfterVIORecovery() con delay de _vioRecoveryDelay (0.8s).
//      2. Stabilizer detecta SessionTracking → ScheduleAnchorRecapture()
//         inmediatamente sin esperar el delay de AROriginAligner.
//      Resultado: recaptura antes de que AlignXROriginOnce() haya terminado
//      → anchor capturado con XR Origin en posición pre-realineación.
//
//  FIX v1.1:
//    Eliminar OnEnable/OnDisable/OnARSessionStateChanged de ARWorldOriginStabilizer.
//    AROriginAligner v8.4+ coordina el ciclo VIO completo:
//      1. DisableStabilization() — pausa el stabilizer
//      2. RealignAfterVIORecovery() + AlignXROriginOnce() — mueve XR Origin
//      3. ScheduleAnchorRecapture() con delay 0.5s — recaptura con cámara estable
//    Esta coordinación garantiza el orden correcto sin condición de carrera.
//
// ============================================================================
//  v1.0 — Funcionalidad original (conservada íntegramente)
// ============================================================================
//
// PROBLEMA QUE RESUELVE:
//   ARCore puede desplazar el world origin durante:
//     - VIO reset / relocalización
//     - Reinicio de ARSession
//     - Primer frame tras SessionInitializing → SessionTracking
//
//   Cuando esto ocurre, la cámara se desplaza respecto al modelo 3D,
//   rompiendo la alineación NavMesh ↔ usuario.
//
// SOLUCIÓN:
//   1. Al posicionar el modelo, se guarda el offset (modelo - cámara).
//   2. Cada frame se verifica si el XR Origin se desplazó.
//   3. Si hay desplazamiento > umbral, se reposiciona el modelo
//      usando el offset guardado + posición actual de la cámara.
//
// INTEGRACIÓN:
//   - Llamar CaptureModelAnchor() después de ResolveARPosition()
//   - Llamar EnableStabilization() cuando el modelo esté posicionado
//   - Suscribirse a OnOriginDrifted para notificar al NavMesh

using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;
using IndoorNavAR.Core.Events;

namespace IndoorNavAR.AR
{
    public class ARWorldOriginStabilizer : MonoBehaviour
    {
        public static ARWorldOriginStabilizer Instance { get; private set; }

        [Header("─── Referencias ──────────────────────────────────────────")]
        [SerializeField] private XROrigin   _xrOrigin;
        [SerializeField] private ARSession  _arSession;
        [SerializeField] private Transform  _modelRoot; // Biblioteca_F_V2

        [Header("─── Umbrales ────────────────────────────────────────────")]
        [Tooltip("Desplazamiento mínimo del XR Origin (m) para considerar drift.")]
        [SerializeField] private float _driftThresholdPosition = 0.05f;

        [Tooltip("Rotación mínima del XR Origin (°) para considerar drift.")]
        [SerializeField] private float _driftThresholdRotation = 1.0f;

        [Tooltip("Frames de espera tras VIO recovery antes de recapturar anchor.")]
        [SerializeField] private int _stabilizationFrames = 15;

        [Tooltip("Si true, reposiciona el modelo automáticamente cuando hay drift.")]
        [SerializeField] private bool _autoCorrect = true;

        [Header("─── Debug ────────────────────────────────────────────────")]
        [SerializeField] private bool _logDrift = true;

        // ─── Estado ───────────────────────────────────────────────────────

        private Vector3    _modelOffset_cameraLocal = Vector3.zero;
        private Quaternion _modelRotation_world      = Quaternion.identity;

        private Vector3    _xrOriginPos_atCapture = Vector3.zero;
        private Quaternion _xrOriginRot_atCapture = Quaternion.identity;
        private Vector3    _cameraPos_atCapture   = Vector3.zero;

        private bool _isStabilizing          = false;
        private bool _anchorCaptured         = false;
        private int  _stabilizationFrameCount = 0;

        // ─── Eventos ──────────────────────────────────────────────────────

        /// <summary>
        /// Disparado cuando se detecta drift del XR Origin y se corrige la posición.
        /// NavMeshOriginCompensator se suscribe a este evento para warpear el agente.
        /// </summary>
        public event Action<Vector3, Vector3> OnOriginDrifted; // (oldModelPos, newModelPos)

        /// <summary>
        /// Disparado cuando el anchor se captura correctamente.
        /// </summary>
        public event Action OnAnchorCaptured;

        // ─── Propiedades ──────────────────────────────────────────────────

        public bool IsStabilizing  => _isStabilizing;
        public bool AnchorCaptured => _anchorCaptured;

        private Camera ARCamera => _xrOrigin?.Camera;

        // ─── Lifecycle ────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            if (_xrOrigin  == null) _xrOrigin  = FindFirstObjectByType<XROrigin>();
            if (_arSession == null) _arSession  = FindFirstObjectByType<ARSession>();
        }

        // ✅ v1.1: OnEnable/OnDisable/OnARSessionStateChanged ELIMINADOS.
        // AROriginAligner v8.4+ coordina el ciclo VIO completo llamando
        // DisableStabilization() y ScheduleAnchorRecapture() en el orden correcto.
        // Si el stabilizer se suscribiera a stateChanged, habría condición de carrera
        // con el callback de AROriginAligner (ver cambios v1.0 → v1.1 arriba).

        private void LateUpdate()
        {
            if (!_isStabilizing || !_anchorCaptured) return;
            if (ARCamera == null || _modelRoot == null) return;

            if (_stabilizationFrameCount > 0)
            {
                _stabilizationFrameCount--;
                return;
            }

            DetectAndCorrectDrift();
        }

        // ─── API Pública ──────────────────────────────────────────────────

        /// <summary>
        /// Captura el anchor actual: offset entre la cámara y el modelo.
        /// LLAMAR después de que ResolveARPosition() posicione el modelo.
        /// </summary>
        public void CaptureModelAnchor(Transform modelRoot = null)
        {
            if (modelRoot != null) _modelRoot = modelRoot;
            if (_modelRoot == null || ARCamera == null)
            {
                Debug.LogWarning("[Stabilizer] ⚠️ ModelRoot o ARCamera nulos. Anchor no capturado.");
                return;
            }

            Vector3 cameraPos = ARCamera.transform.position;
            Vector3 modelPos  = _modelRoot.position;

            _modelOffset_cameraLocal = ARCamera.transform.InverseTransformPoint(modelPos);
            _modelRotation_world     = _modelRoot.rotation;

            _xrOriginPos_atCapture = _xrOrigin.transform.position;
            _xrOriginRot_atCapture = _xrOrigin.transform.rotation;
            _cameraPos_atCapture   = cameraPos;

            _anchorCaptured = true;

            Log($"✅ Anchor capturado:\n" +
                $"  ModelPos:      {modelPos:F3}\n" +
                $"  CameraPos:     {cameraPos:F3}\n" +
                $"  Offset local:  {_modelOffset_cameraLocal:F3}\n" +
                $"  XROrigin:      {_xrOriginPos_atCapture:F3}");

            OnAnchorCaptured?.Invoke();
        }

        /// <summary>
        /// Activa la estabilización continua.
        /// LLAMAR después de CaptureModelAnchor().
        /// </summary>
        public void EnableStabilization()
        {
            if (!_anchorCaptured)
            {
                Debug.LogWarning("[Stabilizer] ⚠️ Activa el anchor antes de EnableStabilization().");
                return;
            }
            _isStabilizing = true;
            Log("✅ Estabilización activa.");
        }

        /// <summary>
        /// Detiene la estabilización.
        /// Llamado por AROriginAligner antes de RealignAfterVIORecovery().
        /// </summary>
        public void DisableStabilization()
        {
            _isStabilizing = false;
            Log("⏸️ Estabilización detenida.");
        }

        /// <summary>
        /// Actualiza el modelo de referencia (ej. después de restaurar sesión).
        /// </summary>
        public void SetModelRoot(Transform modelRoot) => _modelRoot = modelRoot;

        /// <summary>
        /// Fuerza una recaptura del anchor tras delay de estabilización.
        /// LLAMAR desde AROriginAligner después de RealignAfterVIORecovery().
        /// NO llamar desde otros lugares — AROriginAligner coordina el timing.
        /// </summary>
        public void ScheduleAnchorRecapture(int delayFrames = -1)
        {
            _anchorCaptured          = false;
            _isStabilizing           = false;
            _stabilizationFrameCount = delayFrames >= 0
                ? delayFrames
                : _stabilizationFrames;

            StartCoroutine(RecaptureAfterDelay());
        }

        // ─── Detección y corrección de drift ─────────────────────────────

        private void DetectAndCorrectDrift()
        {
            Vector3 xrOriginPosDelta =
                _xrOrigin.transform.position - _xrOriginPos_atCapture;
            float xrOriginRotDelta =
                Quaternion.Angle(_xrOrigin.transform.rotation, _xrOriginRot_atCapture);

            bool hasDrift =
                xrOriginPosDelta.magnitude > _driftThresholdPosition ||
                xrOriginRotDelta           > _driftThresholdRotation;

            if (!hasDrift) return;

            Vector3 oldModelPos = _modelRoot.position;

            if (_autoCorrect)
                CorrectModelPosition();

            Log($"⚠️ Drift detectado:\n" +
                $"  XROrigin Δpos={xrOriginPosDelta.magnitude:F3}m\n" +
                $"  XROrigin Δrot={xrOriginRotDelta:F1}°\n" +
                $"  Modelo: {oldModelPos:F3} → {_modelRoot.position:F3}");

            _xrOriginPos_atCapture = _xrOrigin.transform.position;
            _xrOriginRot_atCapture = _xrOrigin.transform.rotation;

            OnOriginDrifted?.Invoke(oldModelPos, _modelRoot.position);
        }

        private void CorrectModelPosition()
        {
            Vector3 newModelPos =
                ARCamera.transform.TransformPoint(_modelOffset_cameraLocal);

            _modelRoot.SetPositionAndRotation(newModelPos, _modelRotation_world);

            Log($"✅ Modelo reposicionado: {newModelPos:F3}");
        }

        // ─── Coroutines ───────────────────────────────────────────────────

        private System.Collections.IEnumerator RecaptureAfterDelay()
        {
            int frames = _stabilizationFrames;
            while (frames > 0)
            {
                frames--;
                yield return null;
            }

            if (ARCamera == null || _modelRoot == null)
            {
                Log("⚠️ RecaptureAfterDelay: refs nulas, abortando.");
                yield break;
            }

            if (ARSession.state != ARSessionState.SessionTracking)
            {
                Log("⚠️ RecaptureAfterDelay: tracking no estable, reintentando...");
                StartCoroutine(RecaptureAfterDelay());
                yield break;
            }

            CaptureModelAnchor();
            EnableStabilization();
            Log("✅ Anchor recapturado tras VIO recovery.");
        }

        // ─── Utilities ────────────────────────────────────────────────────

        private void Log(string msg)
        {
            if (_logDrift) Debug.Log($"[ARWorldOriginStabilizer] {msg}");
        }

        [ContextMenu("ℹ️ Estado actual")]
        private void DebugStatus()
        {
            Debug.Log(
                $"[ARWorldOriginStabilizer]\n" +
                $"  AnchorCaptured:   {_anchorCaptured}\n" +
                $"  IsStabilizing:    {_isStabilizing}\n" +
                $"  ModelRoot:        {(_modelRoot != null ? _modelRoot.name : "NULL")}\n" +
                $"  ModelPos:         {(_modelRoot != null ? _modelRoot.position.ToString("F3") : "N/A")}\n" +
                $"  CameraPos:        {(ARCamera != null ? ARCamera.transform.position.ToString("F3") : "N/A")}\n" +
                $"  Offset local cam: {_modelOffset_cameraLocal:F3}\n" +
                $"  XROrigin@capture: {_xrOriginPos_atCapture:F3}\n" +
                $"  XROrigin now:     {(_xrOrigin != null ? _xrOrigin.transform.position.ToString("F3") : "N/A")}\n" +
                $"  Drift actual:     " +
                $"{(_xrOrigin != null ? ((_xrOrigin.transform.position - _xrOriginPos_atCapture).magnitude).ToString("F3") + "m" : "N/A")}");
        }

        [ContextMenu("📸 Capturar anchor ahora")]
        private void DebugCapture() => CaptureModelAnchor();

        [ContextMenu("🔄 Simular VIO drift")]
        private void DebugSimulateDrift()
        {
            if (!_anchorCaptured) { Log("Sin anchor capturado."); return; }
            _xrOriginPos_atCapture += Vector3.right * 0.3f;
            Log("🧪 Drift simulado de 0.3m — próximo LateUpdate corregirá.");
        }
    }
}