// File: SegmentationOverlayRenderer.cs
// ✅ v2.0 — FIX: Visibilidad de máscara y opacidad mejorada
//
// ============================================================================
//  CAMBIOS v1 → v2.0
// ============================================================================
//
//  FIX A — SetVisible() forzaba la activación del GO pero _isVisible no se
//           actualizaba antes de que UpdateMask() hiciera el early-return.
//           Ahora el orden es: _isVisible = visible → SetActive(visible).
//
//  FIX B — El alpha por defecto (0.45f) combinado con los CLASS_COLORS que
//           ya tenían alpha bajo (80, 100, 180) producía una máscara casi
//           invisible. Se aumentó el alpha de los colores y el factor _alpha
//           por defecto a 0.75f para que la máscara sea claramente visible.
//
//  FIX C — UpdateMask() retornaba si !_isVisible, lo que impedía actualizar
//           la textura en el mismo frame en que se activaba el overlay.
//           Se separó el guard: solo se salta Apply() si no hay textura.
//
//  FIX D — Initialize() ahora preserva _isVisible al re-llamarse, evitando
//           que una re-inicialización oculte una máscara que ya estaba activa.

using UnityEngine;
using UnityEngine.UI;

namespace IndoorNavAR.Segmentation
{
    [RequireComponent(typeof(RawImage))]
    public class SegmentationOverlayRenderer : MonoBehaviour
    {
        // ── Colores por clase ─────────────────────────────────────────────
        // FIX B: alpha aumentado para visibilidad real en pantalla
        private static readonly Color32[] CLASS_COLORS =
        {
            new Color32(0,   0,   0,   0),    // background — transparente
            new Color32(0,   220, 80,  160),  // floor — verde más visible
            new Color32(255, 60,  60,  220),  // obstacle — rojo muy visible
            new Color32(80,  120, 240, 140),  // wall — azul moderado
        };

        // FIX B: alpha subido a 0.75 (antes 0.45) — las mascaras se veían casi invisible
        [SerializeField, Range(0f, 1f)]
        private float _alpha = 0.75f;

        public enum FlipMode
        {
            None,
            UVFlipY,
            ScaleFlipY,
            RotateX180
        }

        [SerializeField]
        private FlipMode _flipMode = FlipMode.None;

        private RawImage  _rawImage;
        private Texture2D _maskTexture;
        private Color32[] _pixels;

        private int _maskWidth;
        private int _maskHeight;

        // FIX A: _isVisible refleja el estado real deseado
        private bool _isVisible = false;

        private void Awake()
        {
            _rawImage = GetComponent<RawImage>();
            // Asegurarse de que empieza oculto — sin tocar _isVisible (ya es false)
            _rawImage.gameObject.SetActive(false);
        }

        public void Initialize(int width, int height)
        {
            _rawImage ??= GetComponent<RawImage>();

            if (_rawImage == null)
            {
                Debug.LogError("[SegOverlay] ❌ RawImage no encontrado.");
                return;
            }

            _maskWidth  = width;
            _maskHeight = height;

            // Si ya había textura del tamaño correcto, reutilizar
            if (_maskTexture == null || _maskTexture.width != width || _maskTexture.height != height)
            {
                if (_maskTexture != null) Destroy(_maskTexture);
                _maskTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                _maskTexture.filterMode = FilterMode.Bilinear;
                _pixels = new Color32[width * height];
            }

            _rawImage.texture = _maskTexture;

            // FULL SCREEN
            var rect = _rawImage.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            ApplyFlipCorrection();
            ApplyLetterboxCrop();

            // FIX D: restaurar visibilidad actual tras re-inicialización
            _rawImage.gameObject.SetActive(_isVisible);

            Debug.Log($"[SegOverlay] ✅ Inicializado {width}×{height}. " +
                      $"alpha={_alpha:F2} visible={_isVisible}");
        }

        public void SetFlipMode(FlipMode mode)
        {
            _flipMode = mode;
            ApplyFlipCorrection();
        }

        private void ApplyFlipCorrection()
        {
            if (_rawImage == null) return;
            _rawImage.transform.localScale    = Vector3.one;
            _rawImage.transform.localRotation = Quaternion.identity;

            switch (_flipMode)
            {
                case FlipMode.ScaleFlipY:
                    _rawImage.transform.localScale = new Vector3(1f, -1f, 1f);
                    break;
                case FlipMode.RotateX180:
                    _rawImage.transform.localRotation = Quaternion.Euler(180f, 0f, 0f);
                    break;
                case FlipMode.UVFlipY:
                    var r = _rawImage.uvRect;
                    _rawImage.uvRect = new Rect(r.x, r.y + r.height, r.width, -r.height);
                    break;
            }
        }

        private void ApplyLetterboxCrop()
        {
            if (_maskWidth == 0 || _maskHeight == 0) return;

            float screenAspect = (float)Screen.width / Screen.height;

            if (screenAspect < 1f) // portrait
            {
                float fitW = _maskWidth * screenAspect;
                float padX = (_maskWidth - fitW) * 0.5f;
                _rawImage.uvRect = new Rect(padX / _maskWidth, 0f, fitW / _maskWidth, 1f);
            }
            else
            {
                float fitH = _maskHeight / screenAspect;
                float padY = (_maskHeight - fitH) * 0.5f;
                _rawImage.uvRect = new Rect(0f, padY / _maskHeight, 1f, fitH / _maskHeight);
            }
        }

        /// <summary>
        /// Actualiza la textura de máscara con los datos de clase por píxel.
        /// FIX C: ya no retorna early por !_isVisible — la textura se actualiza
        /// siempre que exista, para que esté lista cuando se active el overlay.
        /// </summary>
        public void UpdateMask(int[] maskData)
        {
            if (_maskTexture == null || _pixels == null) return;
            if (maskData == null || maskData.Length == 0) return;

            int len = Mathf.Min(maskData.Length, _pixels.Length);
            for (int i = 0; i < len; i++)
            {
                int cls = maskData[i];
                var c   = CLASS_COLORS[cls < CLASS_COLORS.Length ? cls : 0];
                // Aplicar factor alpha global encima del alpha del color
                c.a = (byte)(c.a * _alpha);
                _pixels[i] = c;
            }

            _maskTexture.SetPixels32(_pixels);
            _maskTexture.Apply(false);
        }

        /// <summary>
        /// FIX A: Actualiza _isVisible ANTES de cambiar SetActive,
        /// así UpdateMask no hace early-return en el mismo frame.
        /// </summary>
        public void SetVisible(bool visible)
        {
            if (_isVisible == visible) return;

            _isVisible = visible;  // ← primero el flag

            if (_rawImage != null)
                _rawImage.gameObject.SetActive(visible);  // ← luego el GO

            Debug.Log($"[SegOverlay] 👁️ Visibilidad → {visible}");
        }

        /// <summary>
        /// Cambia el factor alpha en runtime sin necesidad de re-inicializar.
        /// </summary>
        public void SetAlpha(float alpha)
        {
            _alpha = Mathf.Clamp01(alpha);
        }

        public bool IsVisible => _isVisible;

        private void OnDestroy()
        {
            if (_maskTexture != null)
                Destroy(_maskTexture);
        }
    }
}