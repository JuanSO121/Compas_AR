// File: SegmentationDiagnostic.cs
// Herramienta de diagnóstico — botones OnGUI para calibrar rotación y flip en runtime.
//
// USO:
//   1. Agregar este componente a cualquier GameObject en la escena AR
//   2. Ejecutar en dispositivo — aparecen botones en la esquina derecha
//   3. Probar Rot 90 → 270 → 180 → 0 hasta que la máscara coincida visualmente
//   4. Una vez calibrado, anotar el valor y eliminar este componente del build final

using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using IndoorNavAR.Segmentation;

namespace IndoorNavAR.Diagnostics
{
    public class SegmentationDiagnostic : MonoBehaviour
    {
        [Header("Opcional — asignar para ver preview del frame")]
        [SerializeField] private RawImage _rawCameraView;

        [Header("Config")]
        [SerializeField] private bool _showDiagnosticUI = true;

        private ObstacleSegmentationWorker _worker;
        private SegmentationController _controller;
        private SegmentationOverlayRenderer _overlay;

        private void Start()
        {
            _worker = ObstacleSegmentationWorker.Instance;
            _controller = FindFirstObjectByType<SegmentationController>(FindObjectsInactive.Include);
            _overlay = FindFirstObjectByType<SegmentationOverlayRenderer>(FindObjectsInactive.Include);
        }

        // ── UI en pantalla ────────────────────────────────────────────────

        private GUIStyle _btnStyle;
        private GUIStyle _labelStyle;

        private void OnGUI()
        {
            if (!_showDiagnosticUI) return;

            if (_btnStyle == null)
            {
                _btnStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 28,
                    fixedWidth = 160f,
                    fixedHeight = 65f
                };
                _labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 24,
                    normal = { textColor = Color.yellow }
                };
            }

            float x = Screen.width - 180f;
            float y = 20f;

            // ── Rotación ──────────────────────────────────────────────────
            GUI.Label(new Rect(x - 10f, y, 200f, 36f), "-- Rotacion --", _labelStyle);
            y += 42f;

            foreach (int deg in new[] { 0, 90, 180, 270 })
            {
                if (GUI.Button(new Rect(x, y, 160f, 60f), "Rot " + deg, _btnStyle))
                    ApplyRotation(deg);
                y += 68f;
            }

            y += 16f;

            // ── Flip ──────────────────────────────────────────────────────
            GUI.Label(new Rect(x - 10f, y, 200f, 36f), "-- Flip --", _labelStyle);
            y += 42f;

            if (GUI.Button(new Rect(x, y, 160f, 60f), "Flip None", _btnStyle))
                ApplyFlip(SegmentationOverlayRenderer.FlipMode.None);
            y += 68f;

            if (GUI.Button(new Rect(x, y, 160f, 60f), "Flip UV Y", _btnStyle))
                ApplyFlip(SegmentationOverlayRenderer.FlipMode.UVFlipY);
            y += 68f;

            if (GUI.Button(new Rect(x, y, 160f, 60f), "Flip Scale", _btnStyle))
                ApplyFlip(SegmentationOverlayRenderer.FlipMode.ScaleFlipY);

            // ── Stats ─────────────────────────────────────────────────────
            if (_worker == null) return;

            GUI.Label(
                new Rect(20f, Screen.height - 110f, 600f, 36f),
                "Obstacle: " + _worker.ObstacleRatio.ToString("P1") +
                "   Floor: " + _worker.FloorRatio.ToString("P1"),
                _labelStyle);

            int[] maskData = _worker.MaskData;
            if (maskData == null || maskData.Length == 0) return;

            int c0 = 0, c1 = 0, c2 = 0, c3 = 0;
            for (int i = 0; i < maskData.Length; i++)
            {
                int v = maskData[i];
                if (v == 0) c0++;
                else if (v == 1) c1++;
                else if (v == 2) c2++;
                else if (v == 3) c3++;
            }
            int total = maskData.Length;

            GUI.Label(
                new Rect(20f, Screen.height - 68f, 800f, 36f),
                "BG:" + (c0 * 100 / total) + "%  " +
                "Floor:" + (c1 * 100 / total) + "%  " +
                "Obs:" + (c2 * 100 / total) + "%  " +
                "Wall:" + (c3 * 100 / total) + "%",
                _labelStyle);
        }

        // ── Acciones ──────────────────────────────────────────────────────

        private void ApplyRotation(int deg)
        {
            // ApplyRotation es privado en SegmentationController (decorado con ContextMenu).
            // Lo invocamos via reflexión para no tener que modificar el controller.
            if (_controller != null)
            {
                MethodInfo method = _controller.GetType().GetMethod(
                    "ApplyRotation",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (method != null)
                    method.Invoke(_controller, new object[] { deg });
                else
                    UnityEngine.Debug.LogWarning(
                        "[Diag] No se encontro ApplyRotation en SegmentationController. " +
                        "Verifica que el metodo exista con ese nombre exacto.");
            }

            UnityEngine.Debug.Log("[Diag] Rotacion aplicada: " + deg);
        }

        private void ApplyFlip(SegmentationOverlayRenderer.FlipMode mode)
        {
            if (_overlay != null)
                _overlay.SetFlipMode(mode);

            UnityEngine.Debug.Log("[Diag] FlipMode: " + mode);
        }
    }
}