using UnityEngine;
using UnityEngine.UI;

namespace IndoorNavAR.Segmentation
{
    [RequireComponent(typeof(RawImage))]
    public class SegmentationOverlayRenderer : MonoBehaviour
    {
        // ── Colores por clase ─────────────────────────────────────────────
        private static readonly Color32[] CLASS_COLORS =
        {
            new Color32(0,0,0,0),
            new Color32(0,200,0,80),
            new Color32(255,50,50,180),
            new Color32(80,80,220,100),
        };

        [SerializeField, Range(0f, 1f)]
        private float _alpha = 0.45f;

        public enum FlipMode
        {
            None,
            UVFlipY,
            ScaleFlipY,
            RotateX180
        }

        [SerializeField]
        private FlipMode _flipMode = FlipMode.None;

        private RawImage _rawImage;
        private Texture2D _maskTexture;
        private Color32[] _pixels;

        private int _maskWidth;
        private int _maskHeight;
        
        // ✅ NUEVO: Flag para controlar si el overlay está activo
        private bool _isVisible = true;

        private void Awake()
        {
            _rawImage = GetComponent<RawImage>();
        }

        public void Initialize(int width, int height)
        {
            _maskWidth  = width;
            _maskHeight = height;

            _maskTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            _maskTexture.filterMode = FilterMode.Bilinear;

            _pixels = new Color32[width * height];
            _rawImage.texture = _maskTexture;

            // FULL SCREEN
            var rect = _rawImage.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            ApplyFlipCorrection();
            ApplyLetterboxCrop();
        }

        public void SetFlipMode(FlipMode mode)
        {
            _flipMode = mode;
            ApplyFlipCorrection();
        }

        private void ApplyFlipCorrection()
        {
            _rawImage.transform.localScale = Vector3.one;
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
            float screenAspect = (float)Screen.width / Screen.height;

            if (screenAspect < 1f) // portrait celular
            {
                float fitW = _maskWidth * screenAspect;
                float padX = (_maskWidth - fitW) * 0.5f;

                float x = padX / _maskWidth;
                float w = fitW / _maskWidth;

                _rawImage.uvRect = new Rect(x, 0f, w, 1f);
            }
            else
            {
                float fitH = _maskHeight / screenAspect;
                float padY = (_maskHeight - fitH) * 0.5f;

                float y = padY / _maskHeight;
                float h = fitH / _maskHeight;

                _rawImage.uvRect = new Rect(0f, y, 1f, h);
            }
        }

        public void UpdateMask(int[] maskData)
        {
            // ✅ FIX: No actualizar textura si el overlay no está visible
            if (!_isVisible || _maskTexture == null) return;

            for (int i = 0; i < maskData.Length; i++)
            {
                int cls = maskData[i];
                var c = CLASS_COLORS[cls < CLASS_COLORS.Length ? cls : 0];
                c.a = (byte)(c.a * _alpha);
                _pixels[i] = c;
            }

            _maskTexture.SetPixels32(_pixels);
            _maskTexture.Apply(false);
        }

        public void SetVisible(bool visible)
        {
            _isVisible = visible;
            _rawImage.enabled = visible;
            
            // ✅ NUEVO: Limpiar textura cuando se oculta para liberar GPU
            if (!visible && _maskTexture != null)
            {
                // Llenar con transparente
                for (int i = 0; i < _pixels.Length; i++)
                {
                    _pixels[i] = new Color32(0, 0, 0, 0);
                }
                _maskTexture.SetPixels32(_pixels);
                _maskTexture.Apply(false);
            }
        }
        
        // ✅ NUEVO: Propiedad pública para verificar visibilidad
        public bool IsVisible => _isVisible;

        private void OnDestroy()
        {
            if (_maskTexture != null)
                Destroy(_maskTexture);
        }
    }
}