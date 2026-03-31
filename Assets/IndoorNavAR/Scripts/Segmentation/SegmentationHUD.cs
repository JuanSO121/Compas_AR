// File: SegmentationHUD.cs
// HUD superpuesto a la cámara AR: leyenda de colores + porcentajes en tiempo real
// + barra de alerta de obstáculo.
//
// ============================================================================
//  SETUP EN UNITY
// ============================================================================
//
//  1. Crea un Canvas (Screen Space — Overlay, o Camera).
//  2. Agrega este componente al Canvas o a un GameObject hijo.
//  3. Asigna en Inspector:
//       _worker       → ObstacleSegmentationWorker (del SegmentationSystem)
//       _font         → cualquier TMP_FontAsset (o deja null para el default)
//  4. El HUD se construye automáticamente en Start().
//
//  No necesita referencias adicionales — se auto-construye.

using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace IndoorNavAR.Segmentation
{
    public class SegmentationHUD : MonoBehaviour
    {
        [Header("Referencias")]
        [SerializeField] private ObstacleSegmentationWorker _worker;
        [Tooltip("Opcional. Si null usa el font default de TMP.")]
        [SerializeField] private TMP_FontAsset _font;

        [Header("Posición")]
        [Tooltip("Esquina donde aparece la leyenda.")]
        [SerializeField] private Corner _legendCorner = Corner.BottomRight;
        [Tooltip("Posición de la barra de obstáculo.")]
        [SerializeField] private Corner _alertBarPosition = Corner.TopCenter;

        [Header("Estilo")]
        [SerializeField] private float _legendFontSize  = 22f;
        [SerializeField] private float _alertFontSize   = 26f;
        [SerializeField] private Color _panelBgColor    = new Color(0f, 0f, 0f, 0.55f);

        [Header("Update")]
        [SerializeField, Range(0.05f, 0.5f)]
        private float _refreshRate = 0.1f;

        public enum Corner { TopLeft, TopCenter, TopRight, BottomLeft, BottomRight }

        // ── Clases y colores (espejo de SegmentationOverlayRenderer) ─────
        private static readonly (string label, Color color)[] CLASS_INFO =
        {
            ("Fondo",      new Color(0f,    0f,    0f,    0f)),
            ("Piso",       new Color(0f,    0.78f, 0f,    1f)),
            ("Obstáculo",  new Color(1f,    0.2f,  0.2f,  1f)),
            ("Pared",      new Color(0.31f, 0.31f, 0.86f, 1f)),
        };

        // ── UI interno ────────────────────────────────────────────────────
        private Canvas     _canvas;
        private RectTransform _legendPanel;
        private RectTransform _alertPanel;

        // Textos de porcentaje por clase (índices 1,2,3)
        private TextMeshProUGUI[] _pctLabels = new TextMeshProUGUI[4];

        // Barra de progreso de obstáculo
        private Image           _alertBar;
        private TextMeshProUGUI _alertLabel;
        private Image           _alertPanelBg;

        // Animación de pulso para alerta crítica
        private Coroutine _pulseCoroutine;
        private bool      _wasCritical = false;

        private static readonly Color COLOR_SAFE     = new Color(0.2f,  0.9f,  0.3f,  1f);
        private static readonly Color COLOR_MEDIUM   = new Color(1f,    0.75f, 0.1f,  1f);
        private static readonly Color COLOR_CRITICAL = new Color(1f,    0.15f, 0.15f, 1f);

        // ─────────────────────────────────────────────────────────────────

        private void Start()
        {
            _worker = ObstacleSegmentationWorker.Instance;

            if (_worker == null)
            {
                Debug.LogWarning("[SegHUD] Worker aún no creado.");
                return;
            }

            _canvas = GetComponentInParent<Canvas>();
            if (_canvas == null)
                _canvas = GetComponent<Canvas>();

            BuildHUD();
            StartCoroutine(RefreshLoop());
        }
        // ─────────────────────────────────────────────────────────────────
        //  CONSTRUCCIÓN DEL HUD
        // ─────────────────────────────────────────────────────────────────

        private void BuildHUD()
        {
            BuildLegendPanel();
            BuildAlertBar();
        }

        /// <summary>
        /// Panel de leyenda: cuadro de color + nombre de clase + porcentaje.
        /// Clases mostradas: Floor, Obstacle, Wall (Background se omite).
        /// </summary>
        private void BuildLegendPanel()
        {
            _legendPanel = CreatePanel("LegendPanel", _legendCorner,
                new Vector2(210f, 116f), new Vector2(-14f, 14f));

            float y = -10f;

            for (int i = 1; i <= 3; i++)  // omitir índice 0 (Background)
            {
                var (label, color) = CLASS_INFO[i];

                // Cuadradito de color
                var swatch = CreateSwatchRect(_legendPanel, color,
                    new Vector2(14f, y - 6f), new Vector2(16f, 16f));

                // Texto "Piso: 0%"
                var txt = CreateTMP(_legendPanel, $"Pct_{i}",
                    new Vector2(36f, y),
                    new Vector2(170f, 26f),
                    $"{label}: —",
                    _legendFontSize);

                _pctLabels[i] = txt;
                y -= 34f;
            }
        }

        /// <summary>
        /// Barra de alerta de obstáculo en la parte superior (o donde elijas).
        /// Muestra fill proporcional al ObstacleRatio con color semáforo.
        /// </summary>
        private void BuildAlertBar()
        {
            _alertPanel = CreatePanel("AlertPanel", _alertBarPosition,
                new Vector2(320f, 52f), new Vector2(0f, -14f));

            // Fondo de la barra de progreso
            var barBg = CreateRectChild(_alertPanel, "BarBg",
                new Vector2(10f, 10f), new Vector2(300f, 20f));
            var barBgImg = barBg.gameObject.AddComponent<Image>();
            barBgImg.color = new Color(1f, 1f, 1f, 0.12f);

            // Fill de la barra
            var barFill = CreateRectChild(barBg, "BarFill",
                Vector2.zero, new Vector2(0f, 20f));
            barFill.anchorMin = Vector2.zero;
            barFill.anchorMax = new Vector2(0f, 1f);
            barFill.sizeDelta = new Vector2(0f, 0f);
            barFill.offsetMin = Vector2.zero;
            barFill.offsetMax = Vector2.zero;

            _alertBar = barFill.gameObject.AddComponent<Image>();
            _alertBar.color = COLOR_SAFE;

            // Texto de porcentaje de obstáculo
            _alertLabel = CreateTMP(_alertPanel, "AlertLabel",
                new Vector2(0f, -30f),
                new Vector2(320f, 24f),
                "Obstáculos: —",
                _alertFontSize,
                TextAlignmentOptions.Center);

            _alertPanelBg = _alertPanel.GetComponent<Image>();
        }

        // ─────────────────────────────────────────────────────────────────
        //  LOOP DE ACTUALIZACIÓN
        // ─────────────────────────────────────────────────────────────────

        private IEnumerator RefreshLoop()
        {
            var wait = new WaitForSeconds(_refreshRate);

            while (true)
            {
                yield return wait;

                if (_worker == null) continue;

                float obs   = _worker.ObstacleRatio;
                float floor = _worker.FloorRatio;

                // Porcentajes de clase en la leyenda
                UpdateLegend(floor, obs);

                // Barra de obstáculo
                UpdateAlertBar(obs);
            }
        }

        private void UpdateLegend(float floor, float obs)
        {
            if (_pctLabels[1] != null)
                _pctLabels[1].text = $"Piso:       {floor:P0}";

            if (_pctLabels[2] != null)
            {
                _pctLabels[2].text = $"Obstáculo: {obs:P0}";
                _pctLabels[2].color = obs > 0.25f ? COLOR_CRITICAL
                                    : obs > 0.12f ? COLOR_MEDIUM
                                    : Color.white;
            }

            // Wall ratio no lo calcula el worker aún — mostramos guión
            if (_pctLabels[3] != null)
                _pctLabels[3].text = "Pared:      —";
        }

        private void UpdateAlertBar(float obs)
        {
            if (_alertBar == null || _alertLabel == null) return;

            // Ancho del fill proporcional (0-300px)
            float barW = Mathf.Lerp(0f, 300f, Mathf.Clamp01(obs / 0.5f));
            var barRect = _alertBar.rectTransform;
            barRect.sizeDelta = new Vector2(barW, 0f);

            // Color semáforo
            Color barColor = obs > 0.25f ? COLOR_CRITICAL
                           : obs > 0.12f ? COLOR_MEDIUM
                           : COLOR_SAFE;
            _alertBar.color = barColor;

            // Texto
            string statusText = obs > 0.25f ? "⚠ OBSTÁCULO CERCA"
                               : obs > 0.12f ? "⚠ Obstáculo detectado"
                               : "✓ Camino libre";

            _alertLabel.text  = $"{statusText}  {obs:P0}";
            _alertLabel.color = barColor;

            // Pulso en estado crítico
            bool isCritical = obs > 0.25f;
            if (isCritical && !_wasCritical)
            {
                if (_pulseCoroutine != null) StopCoroutine(_pulseCoroutine);
                _pulseCoroutine = StartCoroutine(PulsePanel());
            }
            else if (!isCritical && _wasCritical)
            {
                if (_pulseCoroutine != null) StopCoroutine(_pulseCoroutine);
                _pulseCoroutine = null;
                if (_alertPanelBg != null) _alertPanelBg.color = _panelBgColor;
            }
            _wasCritical = isCritical;
        }

        // Pulso de fondo rojo en alerta crítica
        private IEnumerator PulsePanel()
        {
            var critBg = new Color(0.5f, 0f, 0f, 0.75f);
            while (true)
            {
                float t = 0f;
                while (t < 1f)
                {
                    t += Time.deltaTime * 3f;
                    if (_alertPanelBg != null)
                        _alertPanelBg.color = Color.Lerp(_panelBgColor, critBg, t);
                    yield return null;
                }
                t = 0f;
                while (t < 1f)
                {
                    t += Time.deltaTime * 3f;
                    if (_alertPanelBg != null)
                        _alertPanelBg.color = Color.Lerp(critBg, _panelBgColor, t);
                    yield return null;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  HELPERS DE CONSTRUCCIÓN UI
        // ─────────────────────────────────────────────────────────────────

        private RectTransform CreatePanel(string name, Corner corner,
                                          Vector2 size, Vector2 margin)
        {
            var go   = new GameObject(name);
            go.transform.SetParent(_canvas.transform, false);
            var rect = go.AddComponent<RectTransform>();
            var img  = go.AddComponent<Image>();
            img.color = _panelBgColor;

            // Esquinas
            switch (corner)
            {
                case Corner.TopLeft:
                    rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                    rect.pivot     = new Vector2(0f, 1f);
                    rect.anchoredPosition = new Vector2(-margin.x, margin.y);
                    break;
                case Corner.TopCenter:
                    rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
                    rect.pivot     = new Vector2(0.5f, 1f);
                    rect.anchoredPosition = new Vector2(margin.x, margin.y);
                    break;
                case Corner.TopRight:
                    rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
                    rect.pivot     = new Vector2(1f, 1f);
                    rect.anchoredPosition = new Vector2(margin.x, margin.y);
                    break;
                case Corner.BottomLeft:
                    rect.anchorMin = rect.anchorMax = new Vector2(0f, 0f);
                    rect.pivot     = new Vector2(0f, 0f);
                    rect.anchoredPosition = new Vector2(-margin.x, -margin.y);
                    break;
                case Corner.BottomRight:
                default:
                    rect.anchorMin = rect.anchorMax = new Vector2(1f, 0f);
                    rect.pivot     = new Vector2(1f, 0f);
                    rect.anchoredPosition = new Vector2(margin.x, -margin.y);
                    break;
            }

            rect.sizeDelta = size;

            // Esquinas redondeadas via outline component (URP no tiene masked image built-in)
            // Usamos padding manual via Layout
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 6, 6);
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            return rect;
        }

        private RectTransform CreateRectChild(RectTransform parent, string name,
                                               Vector2 anchoredPos, Vector2 size)
        {
            var go   = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta        = size;
            return rect;
        }

        private RectTransform CreateSwatchRect(RectTransform parent, Color color,
                                                Vector2 pos, Vector2 size)
        {
            var go   = new GameObject("Swatch");
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin        = new Vector2(0f, 1f);
            rect.anchorMax        = new Vector2(0f, 1f);
            rect.pivot            = new Vector2(0f, 1f);
            rect.anchoredPosition = pos;
            rect.sizeDelta        = size;
            var img  = go.AddComponent<Image>();
            img.color = color;
            return rect;
        }

        private TextMeshProUGUI CreateTMP(RectTransform parent, string name,
                                          Vector2 anchoredPos, Vector2 size,
                                          string defaultText, float fontSize,
                                          TextAlignmentOptions alignment = TextAlignmentOptions.Left)
        {
            var go   = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin        = new Vector2(0f, 1f);
            rect.anchorMax        = new Vector2(0f, 1f);
            rect.pivot            = new Vector2(0f, 1f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta        = size;

            var tmp       = go.AddComponent<TextMeshProUGUI>();
            tmp.text      = defaultText;
            tmp.fontSize  = fontSize;
            tmp.color     = Color.white;
            tmp.alignment = alignment;

            if (_font != null) tmp.font = _font;

            // Outline para legibilidad sobre la cámara AR
            tmp.fontMaterial.EnableKeyword("OUTLINE_ON");
            tmp.outlineWidth = 0.15f;
            tmp.outlineColor = new Color32(0, 0, 0, 200);

            return tmp;
        }
    }
}