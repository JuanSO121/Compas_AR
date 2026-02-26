// File: AROriginAligner.cs
// ============================================================================
//  AR ORIGIN ALIGNER — IndoorNavAR
//
//  Alinea el XR Origin al NavigationStartPoint del modelo GLB.
//
//  EL PROBLEMA:
//    AR Foundation controla la posición del XR Origin a través del tracking
//    del dispositivo. No puedes hacer xrOrigin.position = X directamente
//    porque el subsistema AR lo sobreescribe cada frame.
//
//  LA SOLUCIÓN CORRECTA:
//    XR Origin expone XROrigin.MoveCameraToWorldLocation(Vector3) y
//    XROrigin.MatchOriginUpCameraForward(Vector3, Vector3) que calculan
//    el OFFSET necesario para que la cámara AR quede en la posición
//    deseada, moviendo el XR Origin de forma que AR Foundation lo respete.
//
//  RESULTADO:
//    - XR Origin queda desplazado de forma que la AR Camera esté
//      visualmente en el StartPoint del modelo.
//    - El NavigationAgent se teleporta también al StartPoint sobre NavMesh.
//    - El usuario "ve" desde el inicio del edificio.
//
//  USO:
//    1. Añadir este componente a cualquier GameObject en la escena.
//    2. Asignar XR Origin en el Inspector (o se encuentra automáticamente).
//    3. Se activa automáticamente via EventBus cuando el modelo se carga,
//       o puedes llamar AlignToStartPoint() manualmente.
//
// ============================================================================

using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Navigation;

using Unity.XR.CoreUtils;

namespace IndoorNavAR.AR
{
    public class AROriginAligner : MonoBehaviour
    {
        [Header("─── Referencias ────────────────────────────────────────")]
        [SerializeField] private XROrigin         _xrOrigin;
        [SerializeField] private NavigationAgent  _navigationAgent;

        [Header("─── Configuración ──────────────────────────────────────")]
        [Tooltip("Nivel del modelo al que buscar el StartPoint (normalmente 0).")]
        [SerializeField] private int   _targetLevel       = 0;

        [Tooltip("Altura adicional sobre el StartPoint para colocar la cámara AR (simula la altura del ojo del usuario).")]
        [SerializeField] private float _eyeHeightOffset   = 1.6f;

        [Tooltip("Si true, se realinea cada vez que se carga un modelo nuevo vía EventBus.")]
        [SerializeField] private bool  _autoAlignOnLoad   = true;

        [Tooltip("Si true, también teleporta el NavigationAgent al StartPoint.")]
        [SerializeField] private bool  _teleportAgent     = true;

        [Tooltip("Frames de espera después de que el modelo se carga antes de alinear (da tiempo a que los transforms se actualicen).")]
        [SerializeField] private int   _delayFrames       = 2;

        [Header("─── Debug ──────────────────────────────────────────────")]
        [SerializeField] private bool _logAlignment = true;

        // ─── Estado interno ────────────────────────────────────────────────
        private bool _aligned = false;

        #region Unity Lifecycle

        private void Awake()
        {
            FindComponents();
        }

        private void OnEnable()
        {
            if (_autoAlignOnLoad)
                EventBus.Instance?.Subscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        private void OnDisable()
        {
            EventBus.Instance?.Unsubscribe<ModelLoadedEvent>(OnModelLoaded);
        }

        #endregion

        #region Component Discovery

        private void FindComponents()
        {
            if (_xrOrigin == null)
                _xrOrigin = FindFirstObjectByType<XROrigin>();

            if (_navigationAgent == null)
                _navigationAgent = FindFirstObjectByType<NavigationAgent>();

            if (_xrOrigin == null)
                Debug.LogWarning("[AROriginAligner] ⚠️ XROrigin no encontrado. Asígnalo en el Inspector.");
        }

        #endregion

        #region Event Handlers

        private void OnModelLoaded(ModelLoadedEvent evt)
        {
            Log($"📦 Modelo cargado: {evt.ModelName} → alineando en {_delayFrames} frames...");
            StartCoroutine(AlignAfterFrames(_delayFrames));
        }

        #endregion

        #region Public API

        /// <summary>
        /// Alinea el XR Origin de forma que la AR Camera quede en el
        /// NavigationStartPoint del nivel especificado.
        /// Llámalo manualmente si no usas _autoAlignOnLoad.
        /// </summary>
        [ContextMenu("🎯 Alinear al StartPoint ahora")]
        public void AlignToStartPoint()
        {
            StartCoroutine(AlignAfterFrames(1));
        }

        /// <summary>
        /// Fuerza la realineación aunque ya se haya alineado antes.
        /// </summary>
        public void ForceRealign()
        {
            _aligned = false;
            AlignToStartPoint();
        }

        #endregion

        #region Core Alignment

        private IEnumerator AlignAfterFrames(int frames)
        {
            for (int i = 0; i < frames; i++)
                yield return null;

            PerformAlignment();
        }

        private void PerformAlignment()
        {
            // ── 1. Validar prerequisitos ───────────────────────────────────
            if (_xrOrigin == null)
            {
                Debug.LogError("[AROriginAligner] ❌ XROrigin es null. No se puede alinear.");
                return;
            }

            var startPoint = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);
            if (startPoint == null)
            {
                Debug.LogWarning($"[AROriginAligner] ⚠️ No hay NavigationStartPoint para nivel {_targetLevel}.");
                return;
            }

            // Confirmar que el modelo está posicionado (desbloquea el transform del StartPoint)
            startPoint.ConfirmModelPositioned();

            Vector3 startWorldPos = startPoint.transform.position;
            Log($"📍 StartPoint en mundo: {startWorldPos}");

            // ── 2. Mover XR Origin ─────────────────────────────────────────
            // XROrigin.MoveCameraToWorldLocation mueve el ORIGIN completo
            // de forma que la AR Camera quede exactamente en la posición dada.
            // Esto es el método oficial de AR Foundation para reposicionar.
            Vector3 targetCameraPos = startWorldPos + Vector3.up * _eyeHeightOffset;
            _xrOrigin.MoveCameraToWorldLocation(targetCameraPos);

            Log($"✅ XR Origin alineado → cámara en {targetCameraPos}");

            // ── 3. Teleportar NavigationAgent ──────────────────────────────
            if (_teleportAgent && _navigationAgent != null)
            {
                bool ok = _navigationAgent.TeleportTo(startWorldPos);
                if (ok)
                    Log($"✅ NavigationAgent teleportado a {startWorldPos}");
                else
                    Debug.LogWarning("[AROriginAligner] ⚠️ TeleportTo falló — ¿NavMesh activo en ese punto?");
            }

            // ── 4. Notificar al StartPoint ─────────────────────────────────
            startPoint.ReteleportAgent();

            _aligned = true;

            // ── 5. Publicar evento de confirmación ─────────────────────────
            EventBus.Instance?.Publish(new ShowMessageEvent
            {
                Message  = "Posicionado en el inicio del edificio",
                Type     = MessageType.Success,
                Duration = 3f
            });
        }

        #endregion

        #region Utilities

        private void Log(string msg)
        {
            if (_logAlignment)
                Debug.Log($"[AROriginAligner] {msg}");
        }

        #endregion

        #region Debug Info

        [ContextMenu("ℹ️ Info de alineación")]
        private void DebugInfo()
        {
            var startPoint = NavigationStartPointManager.GetStartPointForLevel(_targetLevel);

            Debug.Log("══════════════════════════════════════");
            Debug.Log("  AROriginAligner — Estado");
            Debug.Log("══════════════════════════════════════");
            Debug.Log($"  XR Origin:      {(_xrOrigin != null ? _xrOrigin.gameObject.name : "NULL")}");
            Debug.Log($"  AR Camera pos:  {(_xrOrigin != null ? _xrOrigin.Camera.transform.position.ToString() : "N/A")}");
            Debug.Log($"  StartPoint:     {(startPoint != null ? startPoint.gameObject.name : "No encontrado")}");
            Debug.Log($"  SP posición:    {(startPoint != null ? startPoint.transform.position.ToString() : "N/A")}");
            Debug.Log($"  Nivel target:   {_targetLevel}");
            Debug.Log($"  Alineado:       {_aligned}");
            Debug.Log($"  Eye offset:     {_eyeHeightOffset}m");
            Debug.Log("══════════════════════════════════════");
        }

        #endregion
    }
}