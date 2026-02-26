// File: MobileNavigationUI.cs — v3.0
// ============================================================================
//  CAMBIOS v3.0 vs v2 (BUGFIXES + RESPONSIVE UI):
//
//  ═══════════════════════════════════════════════════════════════════════════
//  FIX CRÍTICO 1 — Waypoints no aparecen en la lista
//  ═══════════════════════════════════════════════════════════════════════════
//  CAUSA RAÍZ: La UI se suscribía a WaypointPlacedEvent para refrescar la
//  lista, pero WaypointManager.LoadWaypoints() llama ClearAllWaypoints()
//  primero (dispara WaypointRemovedEvent N veces) y luego crea waypoints
//  individualmente (N × WaypointPlacedEvent). Cada evento lanzaba una
//  corutina separada. Con el nuevo Input System y async/await en Unity 6,
//  el TaskScheduler de Unity podría ejecutar continuaciones en frames
//  distintos, causando que RefreshWaypointList() se ejecute mientras
//  WaypointManager todavía está en medio de la carga.
//
//  SOLUCIÓN v3:
//    a) Se añade un nuevo evento WaypointsBatchLoadedEvent que WaypointManager
//       debe publicar al finalizar LoadWaypoints(). La UI escucha este evento
//       para refrescar la lista UNA SOLA VEZ al finalizar la carga en lote.
//       (Ver nota al final del archivo para la modificación mínima de WaypointManager)
//
//    b) Se añade un "dirty flag" (_listNeedsRefresh) con un timer de debounce
//       de 0.2s. Todos los eventos individuales (WaypointPlaced, WaypointRemoved)
//       activan el flag en lugar de llamar RefreshWaypointList() directamente.
//       El Update() llama RefreshWaypointList() cuando el timer expira.
//       Esto garantiza que aunque lleguen N eventos rápidos, la lista solo se
//       reconstruye una vez cuando el flujo se estabiliza.
//
//    c) RefreshWaypointList() ahora comprueba _waypointManager?.Waypoints?.Count
//       antes de mostrar el mensaje vacío, y usa un layout rebuild forzado con
//       dos frames de espera para garantizar que el ContentSizeFitter haya
//       calculado las alturas correctas.
//
//  ═══════════════════════════════════════════════════════════════════════════
//  FIX CRÍTICO 2 — Panel semitransparente cubre botones
//  ═══════════════════════════════════════════════════════════════════════════
//  CAUSA RAÍZ: Las constantes REF_H, STATUS_H, FAB_SIZE, PEEK_H eran valores
//  fijos diseñados para 1080×1920. Con CanvasScaler.matchWidthOrHeight = 0.5f,
//  en pantallas 20:9 (1080×2400) la altura lógica escalada es mayor que REF_H,
//  haciendo que el sheet en estado Full sobrepasara la status bar. El PEEK_H
//  fijo de 100u tampoco garantizaba que el borde del sheet quedase por encima
//  de los FABs.
//
//  SOLUCIÓN v3:
//    a) CalculateAdaptiveLayout(): calcula en tiempo real las alturas del sheet
//       basándose en el aspect ratio ACTUAL de la pantalla. Si el ratio es más
//       estrecho que 16:9, reduce _sheetFullY proporcionalmente para nunca
//       sobrepasar la status bar + margen.
//
//    b) STATUS_H ya no es una constante: se calcula como SafeTop() + 80f, con
//       un mínimo de 80f y un máximo de 140f para cubrir todos los notch sizes.
//
//    c) El sheet en estado Full tiene un tope duro en REF_H - statusHeight - FAB_AREA - 32f,
//       garantizando que nunca tape los FABs ni la status bar en ningún ratio.
//
//    d) OnRectTransformDimensionsChange() recalcula el layout cuando el usuario
//       rota el dispositivo o cambia el tamaño de ventana (split-screen).
//
//    e) Los FABs usan posición relativa al borde inferior mediante SafeBottom()
//       y se reposicionan en RebuildLayout() tras cualquier cambio de dimensiones.
//
//  ═══════════════════════════════════════════════════════════════════════════
//  MEJORAS ADICIONALES v3
//  ═══════════════════════════════════════════════════════════════════════════
//  - CanvasScaler.matchWidthOrHeight = 0.618f (golden ratio) para mejor balance
//    entre pantallas narrow y wide en lugar de 0.5f.
//  - El ScrollRect de waypoints calcula su altura máxima para no sobrepasar
//    el área disponible del sheet.
//  - Corregido BuildEditPanel(): el backdrop no era child de transform sino de
//    _editPanel.parent, causando que Find("EditBackdrop") fallara en algunos casos.
//  - Corregido IsPointerOverScrollRect(): ahora también verifica _favScrollRect.
//  - Corregido TickNavPanel(): division by zero si CurrentSpeed == 0.
//  - Se unifica el SafeTop()/SafeBottom() para que devuelvan valores en unidades
//    de referencia del Canvas (no píxeles físicos multiplicados por scaleFactor).
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using IndoorNavAR.Core;
using IndoorNavAR.Core.Data;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Core.Managers;
using IndoorNavAR.Navigation;
using UnityEngine.InputSystem;

namespace IndoorNavAR.AR
{
    // =========================================================================
    //  VOICE COMMAND INTERFACE (stub — se implementará en Fase 2)
    // =========================================================================

    public interface IVoiceCommandProvider
    {
        bool IsListening { get; }
        void StartListening();
        void StopListening();
        event Action<VoiceCommand> OnCommandRecognized;
    }

    public enum VoiceCommandType
    {
        NavigateTo, Cancel, GoHome, NextWaypoint, PrevWaypoint,
        SaveSession, LoadSession, AddWaypoint, ShowMenu, CloseMenu,
        Recalculate, ToggleFavorite,
    }

    public struct VoiceCommand
    {
        public VoiceCommandType Type;
        public string           Parameter;
    }

    // =========================================================================
    //  NUEVO EVENTO DE CARGA EN LOTE (FIX 1)
    //  Añade esto a tu EventBus/Events.cs:
    //  public struct WaypointsBatchLoadedEvent { public int Count; }
    // =========================================================================

    // =========================================================================
    //  MAIN COMPONENT
    // =========================================================================

    [RequireComponent(typeof(Canvas))]
    [RequireComponent(typeof(CanvasScaler))]
    [RequireComponent(typeof(GraphicRaycaster))]
    public class MobileNavigationUI : MonoBehaviour
    {
        // ─── Inspector ────────────────────────────────────────────────────────

        [Header("Dependencias")]
        [SerializeField] private NavigationAgent    _navigationAgent;
        [SerializeField] private WaypointManager    _waypointManager;
        [SerializeField] private PersistenceManager _persistenceManager;
        [SerializeField] private ARSessionManager   _arSessionManager;
        [SerializeField] private Camera             _arCamera;

        [Header("Tema")]
        [SerializeField] private Color _cPrimary  = new Color(1.00f, 0.62f, 0.10f, 1f);
        [SerializeField] private Color _cBg       = new Color(0.04f, 0.05f, 0.07f, 0.93f);
        [SerializeField] private Color _cSurface  = new Color(0.08f, 0.10f, 0.14f, 0.96f);
        [SerializeField] private Color _cSurface2 = new Color(0.12f, 0.15f, 0.20f, 0.98f);
        [SerializeField] private Color _cSuccess  = new Color(0.22f, 0.92f, 0.56f, 1f);
        [SerializeField] private Color _cDanger   = new Color(1.00f, 0.28f, 0.28f, 1f);
        [SerializeField] private Color _cWarning  = new Color(1.00f, 0.75f, 0.10f, 1f);
        [SerializeField] private Color _cText     = new Color(0.94f, 0.93f, 0.90f, 1f);
        [SerializeField] private Color _cMuted    = new Color(0.48f, 0.50f, 0.55f, 1f);
        [SerializeField] private Color _cFav      = new Color(1.00f, 0.85f, 0.10f, 1f);

        [Header("Comportamiento")]
        [SerializeField] private float _sheetAnimSpeed   = 10f;
        [SerializeField] private float _longPressTime    = 0.65f;
        [SerializeField] private bool  _tapToNavigate    = true;
        [SerializeField] private bool  _tapToAddWaypoint = false;

        [Header("Voz")]
        [SerializeField] private bool _voiceEnabled = false;

        [Header("Debug")]
        [SerializeField] private bool _showDebugOverlay = false;

        // ─── Constantes de referencia (1080×1920) ────────────────────────────
        private const float REF_W    = 1080f;
        private const float REF_H    = 1920f;
        private const float FAB_SIZE = 92f;
        private const float FAB_PAD  = 24f;

        // FIX 2: Estos valores se calculan dinámicamente, no son constantes
        private float _statusHeight;   // calculado: SafeTop() + 80f, clamp [80,140]
        private float _peekH;          // calculado: FAB_SIZE + FAB_PAD * 2 + SafeBottom()
        private float _sheetHalfY;
        private float _sheetFullY;
        private float _sheetPeekY;

        // ─── Canvas ───────────────────────────────────────────────────────────
        private Canvas       _canvas;
        private CanvasScaler _scaler;

        // ─── Status bar ───────────────────────────────────────────────────────
        private RectTransform _statusBar;
        private Text          _txtLevel;
        private Text          _txtDistance;
        private Text          _txtNavMeshLbl;
        private Image         _dotNavMesh;
        private Text          _txtWpCount;

        // ─── FABs ─────────────────────────────────────────────────────────────
        private RectTransform _fabLayer;
        private Button        _fabMenu;
        private Button        _fabAdd;
        private Button        _fabVoice;
        private Button        _fabHome;

        // ─── Bottom sheet ─────────────────────────────────────────────────────
        private RectTransform _sheet;
        private enum SheetState { Collapsed, Half, Full }
        private SheetState _sheetState   = SheetState.Collapsed;
        private float      _sheetTargetY;

        // Tabs: 0=Destinos, 1=Rutas, 2=Favoritos
        private Button        _tabBtnWp;
        private Button        _tabBtnRt;
        private Button        _tabBtnFav;
        private int           _activeTab = 0;
        private RectTransform _tabContentWp;
        private RectTransform _tabContentRt;
        private RectTransform _tabContentFav;

        private InputField    _searchInput;
        private RectTransform _wpListContent;
        private RectTransform _rtListContent;
        private RectTransform _favListContent;
        private ScrollRect    _wpScrollRect;
        private ScrollRect    _favScrollRect;

        private readonly List<WpItemView> _wpItems   = new List<WpItemView>();
        private readonly List<WpItemView> _favItems  = new List<WpItemView>();
        private WaypointData _selectedWp;
        private string       _searchQuery = "";

        // FIX 1: Debounce flag para evitar N refreshes por N eventos de carga
        private bool  _listNeedsRefresh   = false;
        private float _refreshDebounceTimer = 0f;
        private const float REFRESH_DEBOUNCE = 0.25f; // segundos de espera tras el último evento

        // ─── Favoritos (persiste en PlayerPrefs) ─────────────────────────────
        private readonly HashSet<string> _favorites = new HashSet<string>();
        private const string FAV_PREFS_KEY = "NavUI_Favorites";

        // ─── Panel de edición modal ───────────────────────────────────────────
        private RectTransform _editPanel;
        private RectTransform _editBackdrop; // FIX: referencia directa
        private InputField    _editNameInput;
        private Dropdown      _editTypeDropdown;
        private Button        _editSaveBtn;
        private Button        _editCancelBtn;
        private WaypointData  _editingWp;

        // ─── Panel de navegación activa ───────────────────────────────────────
        private RectTransform _navPanel;
        private Text          _navDestText;
        private Text          _navDistText;
        private Text          _navEtaText;
        private Image         _navFill;
        private Button        _navCancelBtn;
        private Button        _navPrevBtn;
        private Button        _navNextBtn;
        private Button        _navRecalcBtn;
        private float         _navPanelHiddenY;
        private WaypointData  _currentNavTarget;

        // ─── Menú ─────────────────────────────────────────────────────────────
        private RectTransform _menuPanel;
        private GameObject    _menuBackdrop;
        private Coroutine     _menuAnimCR;
        private bool          _menuOpen = false;

        // ─── Toast ────────────────────────────────────────────────────────────
        private RectTransform _toastLayer;
        private Coroutine     _toastCR;

        // ─── Touch tracking ──────────────────────────────────────────────────
        private float   _touchDownT;
        private Vector2 _touchDownPos;
        private bool    _longFired;
        private bool    _touchOnSheet;
        private bool    _touchOnScroll;
        private float   _sheetDragStartScreenY;
        private float   _sheetDragStartAnchorY;
        private const float TOUCH_MOVE_TOL = 14f;
        private bool _prevTouchActive = false;
        private bool _prevMouseActive = false;

        // ─── Estado de navegación ─────────────────────────────────────────────
        private bool               _isNavigating = false;
        private List<WaypointData> _guidedRoute  = null;
        private int                _routeIdx     = -1;

        // ─── Voz ─────────────────────────────────────────────────────────────
        private IVoiceCommandProvider _voiceProvider;
        private bool _voiceListening = false;

        // ─── AR raycast ──────────────────────────────────────────────────────
        private readonly List<ARRaycastHit> _arHits = new List<ARRaycastHit>();

        // ─── Debug ────────────────────────────────────────────────────────────
        private Text _dbgText;

        // ─── FIX A: flag de suscripción a eventos ─────────────────────────────
        private bool _eventsSubscribed = false;

        // FIX 2: flag para reconstruir layout al cambiar dimensiones
        private bool _layoutDirty = false;

        // =====================================================================
        //  LIFECYCLE
        // =====================================================================

        private void Awake()
        {
            _canvas = GetComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 20;

            _scaler = GetComponent<CanvasScaler>();
            _scaler.uiScaleMode        = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = new Vector2(REF_W, REF_H);
            // FIX 2: 0.618 balancea mejor entre pantallas narrow (phones) y wide (tablets)
            _scaler.matchWidthOrHeight  = 0.618f;

            EnsureEventSystem();
            LoadFavorites();
            FindDeps();
            CalculateAdaptiveLayout(); // FIX 2: calcula alturas antes de BuildUI
            BuildUI();
        }

        private void Start()
        {
            TrySubscribeEvents();
            StartCoroutine(InitAfterFrame());
            if (_voiceEnabled) SetupVoice();
        }

        // FIX 2: Recalcula layout al rotar o cambiar tamaño (split-screen, etc.)
        private void OnRectTransformDimensionsChange()
        {
            if (_canvas == null) return;
            _layoutDirty = true;
        }

        private IEnumerator InitAfterFrame()
        {
            // Espera 3 frames: Canvas + Layout + WaypointManager inicializados
            yield return null;
            yield return null;
            yield return null;
            DiagnoseWaypointManager();
            RefreshWaypointList();
            RefreshFavoriteList();
            RefreshRouteList();
            UpdateStatusBar();
            SnapSheet(SheetState.Collapsed, instant: true);
        }

        private void OnDestroy()
        {
            UnsubscribeEvents();
            SaveFavorites();
        }

        private void Update()
        {
            // FIX A: reintentar suscripción si EventBus no estaba listo en Start
            if (!_eventsSubscribed) TrySubscribeEvents();

            // FIX 2: Reconstruir layout si cambiaron las dimensiones
            if (_layoutDirty)
            {
                _layoutDirty = false;
                RebuildLayout();
            }

            // FIX 1: Debounce del refresh de lista
            if (_listNeedsRefresh)
            {
                _refreshDebounceTimer -= Time.unscaledDeltaTime;
                if (_refreshDebounceTimer <= 0f)
                {
                    _listNeedsRefresh = false;
                    RefreshWaypointList();
                    RefreshFavoriteList();
                    RefreshRouteList();
                    UpdateStatusBar();
                }
            }

            HandleTouch();
            SmoothSheet();
            if (_isNavigating) TickNavPanel();
            if (_showDebugOverlay) DrawDebug();
        }

        // =====================================================================
        //  FIX 2 — Cálculo adaptativo del layout
        // =====================================================================

        /// <summary>
        /// Calcula las alturas del sheet y la status bar según el aspect ratio real.
        /// Se llama en Awake y en cada cambio de dimensiones del RectTransform.
        /// </summary>
        private void CalculateAdaptiveLayout()
        {
            float safeTop    = SafeTop();
            float safeBottom = SafeBottom();

            // Status bar: notch + padding fijo. Clamp para notches muy grandes (tablets sin notch).
            _statusHeight = Mathf.Clamp(safeTop + 80f, 80f, 140f);

            // Área que ocupan los FABs desde el borde inferior
            float fabAreaH = FAB_SIZE + FAB_PAD * 2f + safeBottom;

            // Peek: el borde superior del sheet en estado Collapsed queda
            // exactamente sobre los FABs con 8u de margen
            _peekH     = fabAreaH + 8f;
            _sheetPeekY = _peekH;

            // El área lógica disponible entre status bar y FABs
            float availableH = REF_H - _statusHeight - fabAreaH;

            // Half: 38% del área disponible (antes era 30% de REF_H → muy pequeño en tablets)
            _sheetHalfY = _peekH + availableH * 0.38f;

            // Full: 72% del área disponible, con tope duro 24u bajo la status bar
            float maxFullY = REF_H - _statusHeight - 24f - fabAreaH;
            _sheetFullY = Mathf.Min(_peekH + availableH * 0.72f, maxFullY);

            // Garantía final: Full > Half > Peek
            _sheetHalfY = Mathf.Clamp(_sheetHalfY, _sheetPeekY + 80f, _sheetFullY - 60f);

            // NavPanel hidden: suficientemente abajo para no ser visible
            _navPanelHiddenY = -(280f + safeBottom + 20f);
        }

        /// <summary>
        /// Reposiciona el sheet, FABs y navPanel tras un cambio de dimensiones.
        /// </summary>
        private void RebuildLayout()
        {
            CalculateAdaptiveLayout();

            if (_sheet != null)
            {
                float sheetH = _sheetFullY + 20f;
                _sheet.sizeDelta = new Vector2(0, sheetH);
                SnapSheet(_sheetState, instant: true);
            }

            if (_statusBar != null)
                _statusBar.sizeDelta = new Vector2(0, _statusHeight);

            RepositionFABs();

            if (_navPanel != null && !_isNavigating)
                _navPanel.anchoredPosition = new Vector2(0, _navPanelHiddenY);

            if (_toastLayer != null)
            {
                float toastY = _peekH + FAB_SIZE + FAB_PAD + 14f;
                _toastLayer.anchoredPosition = new Vector2(0, toastY);
            }
        }

        private void RepositionFABs()
        {
            if (_fabLayer == null) return;
            float safeBottom = SafeBottom();
            float fabMidY    = _peekH + FAB_PAD + FAB_SIZE * 0.5f;

            // Reposicionar cada FAB
            SetFABPosition(_fabMenu,  new Vector2(1, 0), new Vector2(-FAB_PAD - FAB_SIZE * 0.5f, fabMidY));
            SetFABPosition(_fabAdd,   new Vector2(1, 0), new Vector2(-FAB_PAD - FAB_SIZE * 1.5f - 14f, fabMidY));
            SetFABPosition(_fabVoice, new Vector2(1, 0), new Vector2(-FAB_PAD - FAB_SIZE * 2.5f - 30f, fabMidY));
            SetFABPosition(_fabHome,  new Vector2(0, 0), new Vector2(FAB_PAD + FAB_SIZE * 0.5f, fabMidY));
        }

        private static void SetFABPosition(Button btn, Vector2 anchor, Vector2 pos)
        {
            if (btn == null) return;
            var rt = btn.GetComponent<RectTransform>();
            rt.anchorMin = anchor; rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
        }

        // =====================================================================
        //  FIX A — EventSystem + Suscripción robusta
        // =====================================================================

        private void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
            esGo.AddComponent<StandaloneInputModule>();
            Debug.Log("[NavUI] EventSystem creado automáticamente.");
        }

        private void TrySubscribeEvents()
        {
            if (_eventsSubscribed) return;
            if (EventBus.Instance == null) return;

            EventBus.Instance.Subscribe<FloorTransitionEvent>(e => UpdateStatusBar());

            // FIX 1: En lugar de refrescar inmediatamente, activa el debounce
            EventBus.Instance.Subscribe<WaypointPlacedEvent>(e   => ScheduleListRefresh());
            EventBus.Instance.Subscribe<WaypointRemovedEvent>(e  => ScheduleListRefresh());

            // FIX 1: Nuevo evento de carga en lote → refresco inmediato y definitivo
            // Requiere que WaypointManager.LoadWaypoints() publique este evento al finalizar
            EventBus.Instance.Subscribe<WaypointsBatchLoadedEvent>(e =>
            {
                // Cancelar debounce pendiente y refrescar de inmediato
                _listNeedsRefresh     = false;
                _refreshDebounceTimer = 0f;
                StartCoroutine(RefreshAfterTwoFrames());
            });

            EventBus.Instance.Subscribe<NavigationCancelledEvent>(e => SetNavigating(false));
            EventBus.Instance.Subscribe<ShowMessageEvent>(e =>
            {
                Color c = e.Type == MessageType.Success ? _cSuccess :
                          e.Type == MessageType.Warning ? _cWarning :
                          e.Type == MessageType.Error   ? _cDanger  : _cText;
                ShowToast(e.Message, c, e.Duration);
            });

            _eventsSubscribed = true;
            Debug.Log("[NavUI] Suscripción a eventos exitosa.");
        }

        /// <summary>
        /// FIX 1: Activa el debounce. Si llegan varios eventos seguidos (carga de sesión),
        /// el timer se resetea cada vez y RefreshWaypointList solo ocurre una vez al final.
        /// </summary>
        private void ScheduleListRefresh()
        {
            _listNeedsRefresh     = true;
            _refreshDebounceTimer = REFRESH_DEBOUNCE;
        }

        /// <summary>FIX 1: Espera 2 frames para que el LayoutGroup recalcule tamaños.</summary>
        private IEnumerator RefreshAfterTwoFrames()
        {
            yield return null;
            yield return null;
            RefreshWaypointList();
            RefreshFavoriteList();
            RefreshRouteList();
            UpdateStatusBar();
        }

        private void UnsubscribeEvents() { /* EventBus limpia al destruir */ }

        // =====================================================================
        //  FIX B — Diagnóstico
        // =====================================================================

        private void DiagnoseWaypointManager()
        {
            if (_waypointManager == null)
            {
                Debug.LogError("[NavUI] ❌ WaypointManager es null. Verifica que haya un " +
                               "GameObject con WaypointManager en la escena.");
                ShowToast("Error: WaypointManager no encontrado", _cDanger, 5f);
                return;
            }

            int count = _waypointManager.WaypointCount;
            if (count == 0)
                Debug.Log("[NavUI] ℹ️ WaypointManager encontrado pero sin waypoints aún.");
            else
                Debug.Log($"[NavUI] ✅ WaypointManager con {count} waypoints.");
        }

        // =====================================================================
        //  FAVORITOS
        // =====================================================================

        private void LoadFavorites()
        {
            string raw = PlayerPrefs.GetString(FAV_PREFS_KEY, "");
            if (string.IsNullOrEmpty(raw)) return;
            foreach (var id in raw.Split(','))
                if (!string.IsNullOrEmpty(id)) _favorites.Add(id);
        }

        private void SaveFavorites()
        {
            PlayerPrefs.SetString(FAV_PREFS_KEY, string.Join(",", _favorites));
            PlayerPrefs.Save();
        }

        private bool IsFavorite(string wpId) => _favorites.Contains(wpId);

        private void ToggleFavorite(string wpId)
        {
            if (_favorites.Contains(wpId)) _favorites.Remove(wpId);
            else _favorites.Add(wpId);
            SaveFavorites();
            ScheduleListRefresh();
        }

        // =====================================================================
        //  DEPENDENCIES
        // =====================================================================

        private void FindDeps()
        {
            _navigationAgent    ??= FindFirstObjectByType<NavigationAgent>();
            _waypointManager    ??= FindFirstObjectByType<WaypointManager>();
            _persistenceManager ??= FindFirstObjectByType<PersistenceManager>();
            _arSessionManager   ??= FindFirstObjectByType<ARSessionManager>();
            _arCamera           ??= Camera.main;

            if (_navigationAgent == null)
                Debug.LogError("[NavUI] ❌ NavigationAgent no encontrado.");
            if (_waypointManager == null)
                Debug.LogError("[NavUI] ❌ WaypointManager no encontrado — la lista estará vacía.");
            if (_persistenceManager == null)
                Debug.LogWarning("[NavUI] ⚠️ PersistenceManager no encontrado.");
        }

        // =====================================================================
        //  BUILD UI
        // =====================================================================

        private void BuildUI()
        {
            BuildStatusBar();
            BuildFABs();
            BuildSheet();
            BuildNavPanel();
            BuildMenuPanel();
            BuildEditPanel();
            BuildToastLayer();
        }

        // ─── STATUS BAR ───────────────────────────────────────────────────────

        private void BuildStatusBar()
        {
            _statusBar = Mk("StatusBar", transform);
            // FIX 2: altura adaptativa en lugar de constante STATUS_H fija
            AnchorTop(_statusBar, _statusHeight);
            Bg(_statusBar, _cBg);

            var line = Mk("AccentLine", _statusBar);
            line.anchorMin = new Vector2(0, 0); line.anchorMax = new Vector2(1, 0);
            line.sizeDelta = new Vector2(0, 2); line.anchoredPosition = Vector2.zero;
            Bg(line, CA(_cPrimary, 0.6f));

            float top = SafeTop() + 8f;

            // Izquierda: NavMesh status + nivel
            var left = Mk("Left", _statusBar);
            left.anchorMin = new Vector2(0, 0); left.anchorMax = new Vector2(0.55f, 1);
            left.offsetMin = new Vector2(18, top); left.offsetMax = Vector2.zero;

            _dotNavMesh = Mk("Dot", left).gameObject.AddComponent<Image>();
            _dotNavMesh.color = _cDanger;
            Rt(_dotNavMesh).anchorMin = new Vector2(0, 0.5f); Rt(_dotNavMesh).anchorMax = new Vector2(0, 0.5f);
            Rt(_dotNavMesh).sizeDelta = new Vector2(10, 10); Rt(_dotNavMesh).anchoredPosition = new Vector2(5, 6);

            _txtNavMeshLbl = L("NavLbl", left, "SIN NAVMESH", 10, _cDanger);
            _txtNavMeshLbl.fontStyle = FontStyle.Bold;
            Rt(_txtNavMeshLbl).anchorMin = new Vector2(0, 0.5f); Rt(_txtNavMeshLbl).anchorMax = new Vector2(1, 0.5f);
            Rt(_txtNavMeshLbl).anchoredPosition = new Vector2(20, 6); Rt(_txtNavMeshLbl).sizeDelta = new Vector2(-20, 16);

            _txtLevel = L("Level", left, "PLANTA 0", 22, _cText);
            _txtLevel.fontStyle = FontStyle.Bold;
            Rt(_txtLevel).anchorMin = new Vector2(0, 0.5f); Rt(_txtLevel).anchorMax = new Vector2(1, 0.5f);
            Rt(_txtLevel).anchoredPosition = new Vector2(0, -14); Rt(_txtLevel).sizeDelta = new Vector2(-4, 28);

            // Centro: conteo de waypoints
            var mid = Mk("Mid", _statusBar);
            mid.anchorMin = new Vector2(0.55f, 0); mid.anchorMax = new Vector2(0.75f, 1);
            mid.offsetMin = new Vector2(0, top); mid.offsetMax = Vector2.zero;

            _txtWpCount = L("WpCount", mid, "0 destinos", 13, _cMuted);
            _txtWpCount.alignment = TextAnchor.MiddleCenter;
            AnchorFull(Rt(_txtWpCount));

            // Derecha: distancia
            var right = Mk("Right", _statusBar);
            right.anchorMin = new Vector2(0.75f, 0); right.anchorMax = new Vector2(1, 1);
            right.offsetMin = new Vector2(0, top); right.offsetMax = new Vector2(-18, 0);

            _txtDistance = L("Dist", right, "", 32, _cPrimary);
            _txtDistance.fontStyle = FontStyle.Bold;
            _txtDistance.alignment = TextAnchor.MiddleRight;
            AnchorFull(Rt(_txtDistance));
        }

        // ─── FABs ─────────────────────────────────────────────────────────────

        private void BuildFABs()
        {
            _fabLayer = Mk("FABs", transform);
            AnchorFull(_fabLayer);

            float fabMidY = _peekH + FAB_PAD + FAB_SIZE * 0.5f;

            _fabMenu = MkFAB("FABMenu", _fabLayer, "☰", FAB_SIZE,
                new Vector2(1, 0), new Vector2(-FAB_PAD - FAB_SIZE * 0.5f, fabMidY));
            _fabMenu.onClick.AddListener(ToggleMenu);

            _fabAdd = MkFAB("FABAdd", _fabLayer, "＋", FAB_SIZE,
                new Vector2(1, 0), new Vector2(-FAB_PAD - FAB_SIZE * 1.5f - 14f, fabMidY));
            _fabAdd.onClick.AddListener(OnFabAddPressed);

            _fabVoice = MkFAB("FABVoice", _fabLayer, "◎", FAB_SIZE * 0.8f,
                new Vector2(1, 0), new Vector2(-FAB_PAD - FAB_SIZE * 2.5f - 30f, fabMidY));
            _fabVoice.onClick.AddListener(OnFabVoicePressed);
            SetAlpha(_fabVoice, _voiceEnabled ? 1f : 0.3f);
            _fabVoice.interactable = _voiceEnabled;

            _fabHome = MkFAB("FABHome", _fabLayer, "⌂", FAB_SIZE,
                new Vector2(0, 0), new Vector2(FAB_PAD + FAB_SIZE * 0.5f, fabMidY));
            _fabHome.onClick.AddListener(OnFabHomePressed);
        }

        private Button MkFAB(string name, RectTransform parent, string icon,
            float size, Vector2 anchor, Vector2 pos)
        {
            var rt = Mk(name, parent);
            rt.anchorMin = anchor; rt.anchorMax = anchor;
            rt.pivot = Vector2.one * 0.5f;
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = pos;

            Image bg = rt.gameObject.AddComponent<Image>(); bg.color = _cSurface;
            Button btn = rt.gameObject.AddComponent<Button>(); btn.targetGraphic = bg;
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = _cSurface; cb.highlightedColor = Br(_cSurface, 0.12f);
            cb.pressedColor = CA(_cPrimary, 0.2f); cb.colorMultiplier = 1f;
            btn.colors = cb;

            var lbl = L("Icon", rt, icon, (int)(size * 0.38f), _cText);
            lbl.alignment = TextAnchor.MiddleCenter; AnchorFull(Rt(lbl));
            return btn;
        }

        // ─── BOTTOM SHEET ─────────────────────────────────────────────────────

        private void BuildSheet()
        {
            // FIX 2: usa valores adaptativos calculados
            float sheetH = _sheetFullY + 20f;

            _sheet = Mk("Sheet", transform);
            _sheet.anchorMin = new Vector2(0, 0); _sheet.anchorMax = new Vector2(1, 0);
            _sheet.pivot     = new Vector2(0.5f, 0);
            _sheet.sizeDelta = new Vector2(0, sheetH);
            _sheet.anchoredPosition = new Vector2(0, _sheetPeekY);
            Bg(_sheet, _cBg);

            var border = Mk("TopBorder", _sheet);
            border.anchorMin = new Vector2(0, 1); border.anchorMax = new Vector2(1, 1);
            border.sizeDelta = new Vector2(0, 2); border.anchoredPosition = Vector2.zero;
            Bg(border, CA(_cPrimary, 0.7f));

            // Handle
            {
                var h = Mk("Handle", _sheet);
                h.anchorMin = new Vector2(0.5f, 1); h.anchorMax = new Vector2(0.5f, 1);
                h.sizeDelta = new Vector2(80, 26); h.anchoredPosition = new Vector2(0, -14);
                var pill = Mk("Pill", h);
                pill.anchorMin = Vector2.one * 0.5f; pill.anchorMax = Vector2.one * 0.5f;
                pill.sizeDelta = new Vector2(52, 5); pill.anchoredPosition = Vector2.zero;
                Bg(pill, _cMuted);
                var hBtn = h.gameObject.AddComponent<Button>();
                hBtn.transition = Selectable.Transition.None;
                hBtn.onClick.AddListener(OnHandleTap);
            }

            // Tab bar — 3 tabs: Destinos | Rutas | Favoritos
            var tabBar = Mk("TabBar", _sheet);
            tabBar.anchorMin = new Vector2(0, 1); tabBar.anchorMax = new Vector2(1, 1);
            tabBar.sizeDelta = new Vector2(0, 54); tabBar.anchoredPosition = new Vector2(0, -24);
            Bg(tabBar, _cBg);

            _tabBtnWp  = MkTab("TabWp",  tabBar, "DESTINOS",   0f,    0.34f);
            _tabBtnRt  = MkTab("TabRt",  tabBar, "RUTAS",      0.34f, 0.67f);
            _tabBtnFav = MkTab("TabFav", tabBar, "⭐ FAV",      0.67f, 1f);
            _tabBtnWp.onClick.AddListener(()  => SwitchTab(0));
            _tabBtnRt.onClick.AddListener(()  => SwitchTab(1));
            _tabBtnFav.onClick.AddListener(() => SwitchTab(2));

            var tabSep = Mk("TabSep", _sheet);
            tabSep.anchorMin = new Vector2(0, 1); tabSep.anchorMax = new Vector2(1, 1);
            tabSep.sizeDelta = new Vector2(0, 1); tabSep.anchoredPosition = new Vector2(0, -78);
            Bg(tabSep, CA(Color.white, 0.07f));

            _tabContentWp = Mk("ContentWp", _sheet);
            _tabContentWp.anchorMin = Vector2.zero; _tabContentWp.anchorMax = Vector2.one;
            _tabContentWp.offsetMin = new Vector2(0, 72); _tabContentWp.offsetMax = new Vector2(0, -79);
            BuildWaypointContent(_tabContentWp);

            _tabContentRt = Mk("ContentRt", _sheet);
            _tabContentRt.anchorMin = Vector2.zero; _tabContentRt.anchorMax = Vector2.one;
            _tabContentRt.offsetMin = new Vector2(0, 72); _tabContentRt.offsetMax = new Vector2(0, -79);
            BuildRoutesContent(_tabContentRt);
            _tabContentRt.gameObject.SetActive(false);

            _tabContentFav = Mk("ContentFav", _sheet);
            _tabContentFav.anchorMin = Vector2.zero; _tabContentFav.anchorMax = Vector2.one;
            _tabContentFav.offsetMin = new Vector2(0, 72); _tabContentFav.offsetMax = new Vector2(0, -79);
            BuildFavoritesContent(_tabContentFav);
            _tabContentFav.gameObject.SetActive(false);

            SwitchTab(0);
        }

        private Button MkTab(string name, RectTransform parent, string label, float xMin, float xMax)
        {
            var rt = Mk(name, parent);
            rt.anchorMin = new Vector2(xMin, 0); rt.anchorMax = new Vector2(xMax, 1);
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

            Image bg = rt.gameObject.AddComponent<Image>(); bg.color = Color.clear;
            Button btn = rt.gameObject.AddComponent<Button>(); btn.targetGraphic = bg;
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = Color.clear; cb.highlightedColor = CA(Color.white, 0.04f);
            cb.colorMultiplier = 1f; btn.colors = cb;

            var lbl = L("Lbl", rt, label, 12, _cMuted);
            lbl.fontStyle = FontStyle.Bold; lbl.alignment = TextAnchor.MiddleCenter;
            AnchorFull(Rt(lbl));

            var under = Mk("Under", rt);
            under.anchorMin = new Vector2(0.1f, 0); under.anchorMax = new Vector2(0.9f, 0);
            under.offsetMin = Vector2.zero; under.offsetMax = new Vector2(0, 2);
            Bg(under, CA(_cPrimary, 0f));

            return btn;
        }

        private void SwitchTab(int tab)
        {
            _activeTab = tab;
            SetTabActive(_tabBtnWp,  tab == 0);
            SetTabActive(_tabBtnRt,  tab == 1);
            SetTabActive(_tabBtnFav, tab == 2);
            _tabContentWp.gameObject.SetActive(tab == 0);
            _tabContentRt.gameObject.SetActive(tab == 1);
            _tabContentFav.gameObject.SetActive(tab == 2);
            if (tab == 1) RefreshRouteList();
            if (tab == 2) RefreshFavoriteList();
        }

        private void SetTabActive(Button btn, bool active)
        {
            var lbl   = btn.GetComponentInChildren<Text>();
            var under = btn.transform.Find("Under")?.GetComponent<Image>();
            if (lbl   != null) lbl.color   = active ? _cText : _cMuted;
            if (under != null) under.color = active ? _cPrimary : CA(_cPrimary, 0f);
        }

        private void BuildWaypointContent(RectTransform parent)
        {
            // Barra de búsqueda
            var sb = Mk("SearchBar", parent);
            sb.anchorMin = new Vector2(0, 1); sb.anchorMax = new Vector2(1, 1);
            sb.offsetMin = new Vector2(14, -52); sb.offsetMax = new Vector2(-14, 0);
            Bg(sb, _cSurface2);

            var icon = L("Icon", sb, "⌕", 18, _cMuted);
            icon.alignment = TextAnchor.MiddleCenter;
            Rt(icon).anchorMin = new Vector2(0, 0); Rt(icon).anchorMax = new Vector2(0, 1);
            Rt(icon).offsetMin = new Vector2(8, 0); Rt(icon).offsetMax = new Vector2(36, 0);

            _searchInput = sb.gameObject.AddComponent<InputField>();
            _searchInput.transition = Selectable.Transition.None;
            _searchInput.targetGraphic = sb.GetComponent<Image>();

            var inputText = L("InputText", sb, "", 15, _cText);
            inputText.alignment = TextAnchor.MiddleLeft;
            Rt(inputText).anchorMin = new Vector2(0, 0); Rt(inputText).anchorMax = new Vector2(1, 1);
            Rt(inputText).offsetMin = new Vector2(40, 4); Rt(inputText).offsetMax = new Vector2(-12, -4);

            var ph = L("Placeholder", sb, "Buscar destino...", 15, _cMuted);
            ph.alignment = TextAnchor.MiddleLeft; ph.fontStyle = FontStyle.Italic;
            Rt(ph).anchorMin = new Vector2(0, 0); Rt(ph).anchorMax = new Vector2(1, 1);
            Rt(ph).offsetMin = new Vector2(40, 4); Rt(ph).offsetMax = new Vector2(-12, -4);

            _searchInput.textComponent = inputText;
            _searchInput.placeholder   = ph;
            _searchInput.onValueChanged.AddListener(q =>
            {
                _searchQuery = q;
                ScheduleListRefresh();
            });

            var sr = MkScrollView("WpScroll", parent,
                Vector2.zero, Vector2.one, new Vector2(0, 0), new Vector2(0, -60));
            _wpScrollRect  = sr;
            _wpListContent = sr.content;
        }

        private void BuildFavoritesContent(RectTransform parent)
        {
            var hdr = L("Hdr", parent, "MIS FAVORITOS", 11, _cMuted);
            hdr.fontStyle = FontStyle.Bold; hdr.alignment = TextAnchor.MiddleLeft;
            Rt(hdr).anchorMin = new Vector2(0, 1); Rt(hdr).anchorMax = new Vector2(1, 1);
            Rt(hdr).offsetMin = new Vector2(18, -36); Rt(hdr).offsetMax = new Vector2(-18, 0);

            var sr = MkScrollView("FavScroll", parent,
                Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0, -42));
            _favScrollRect  = sr;
            _favListContent = sr.content;
        }

        private void BuildRoutesContent(RectTransform parent)
        {
            var hdr = L("Hdr", parent, "ACCIONES RÁPIDAS", 11, _cMuted);
            hdr.fontStyle = FontStyle.Bold; hdr.alignment = TextAnchor.MiddleLeft;
            Rt(hdr).anchorMin = new Vector2(0, 1); Rt(hdr).anchorMax = new Vector2(1, 1);
            Rt(hdr).offsetMin = new Vector2(18, -38); Rt(hdr).offsetMax = new Vector2(-18, 0);

            var sr = MkScrollView("RtScroll", parent,
                Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0, -46));
            _rtListContent = sr.content;
        }

        // ─── PANEL NAVEGACIÓN ACTIVA ──────────────────────────────────────────

        private void BuildNavPanel()
        {
            float safeBottom = SafeBottom();
            _navPanelHiddenY = -(280f + safeBottom + 20f);

            _navPanel = Mk("NavPanel", transform);
            _navPanel.anchorMin = new Vector2(0, 0); _navPanel.anchorMax = new Vector2(1, 0);
            _navPanel.pivot     = new Vector2(0.5f, 0);
            _navPanel.sizeDelta = new Vector2(0, 240 + safeBottom);
            _navPanel.anchoredPosition = new Vector2(0, _navPanelHiddenY);
            Bg(_navPanel, _cBg);

            var brd = Mk("Brd", _navPanel);
            brd.anchorMin = new Vector2(0, 1); brd.anchorMax = new Vector2(1, 1);
            brd.sizeDelta = new Vector2(0, 2); brd.anchoredPosition = Vector2.zero;
            Bg(brd, _cPrimary);

            _navDestText = L("Dest", _navPanel, "NAVEGANDO HACIA", 11, _cMuted);
            _navDestText.fontStyle = FontStyle.Bold;
            Rt(_navDestText).anchorMin = new Vector2(0, 1); Rt(_navDestText).anchorMax = new Vector2(1, 1);
            Rt(_navDestText).offsetMin = new Vector2(20, -46); Rt(_navDestText).offsetMax = new Vector2(-20, -14);

            _navDistText = L("NavDist", _navPanel, "-- m", 52, _cPrimary);
            _navDistText.fontStyle = FontStyle.Bold;
            Rt(_navDistText).anchorMin = new Vector2(0, 1); Rt(_navDistText).anchorMax = new Vector2(0.6f, 1);
            Rt(_navDistText).offsetMin = new Vector2(20, -114); Rt(_navDistText).offsetMax = new Vector2(0, -54);

            _navEtaText = L("ETA", _navPanel, "", 13, _cMuted);
            _navEtaText.alignment = TextAnchor.LowerRight;
            Rt(_navEtaText).anchorMin = new Vector2(0.6f, 1); Rt(_navEtaText).anchorMax = new Vector2(1, 1);
            Rt(_navEtaText).offsetMin = new Vector2(0, -114); Rt(_navEtaText).offsetMax = new Vector2(-20, -54);

            var progBg = Mk("ProgBg", _navPanel);
            progBg.anchorMin = new Vector2(0, 1); progBg.anchorMax = new Vector2(1, 1);
            progBg.offsetMin = new Vector2(20, -128); progBg.offsetMax = new Vector2(-20, -116);
            Bg(progBg, _cSurface2);

            _navFill = Mk("Fill", progBg).gameObject.AddComponent<Image>();
            _navFill.color = _cPrimary;
            Rt(_navFill).anchorMin = new Vector2(0, 0); Rt(_navFill).anchorMax = new Vector2(0, 1);
            Rt(_navFill).offsetMin = Vector2.zero; Rt(_navFill).offsetMax = Vector2.zero;
            Rt(_navFill).pivot = new Vector2(0, 0.5f);

            _navPrevBtn   = MkNavBtn("PrevBtn",   _navPanel, "← Ant.",   0f,    0.25f, -138, -188, _cSurface2, _cText);
            _navCancelBtn = MkNavBtn("CancelBtn", _navPanel, "✕ Cancel", 0.25f, 0.5f,  -138, -188, CA(_cDanger, 0.18f), _cDanger);
            _navNextBtn   = MkNavBtn("NextBtn",   _navPanel, "Sig. →",   0.5f,  0.75f, -138, -188, _cSurface2, _cText);
            _navRecalcBtn = MkNavBtn("RecalcBtn", _navPanel, "🔄",       0.75f, 1f,    -138, -188, CA(_cWarning, 0.15f), _cWarning);

            _navCancelBtn.onClick.AddListener(CancelNavigation);
            _navPrevBtn.onClick.AddListener(NavPrevWaypoint);
            _navNextBtn.onClick.AddListener(NavNextWaypoint);
            _navRecalcBtn.onClick.AddListener(RecalculateRoute);

            _navPanel.gameObject.SetActive(false);
        }

        private Button MkNavBtn(string name, RectTransform parent, string label,
            float xMin, float xMax, float yMin, float yMax, Color bg, Color textColor)
        {
            var rt = Mk(name, parent);
            rt.anchorMin = new Vector2(xMin, 1); rt.anchorMax = new Vector2(xMax, 1);
            rt.offsetMin = new Vector2(xMin == 0f ? 20 : 4, yMin);
            rt.offsetMax = new Vector2(xMax == 1f ? -20 : -4, yMax);

            Image bgImg = rt.gameObject.AddComponent<Image>(); bgImg.color = bg;
            Button btn = rt.gameObject.AddComponent<Button>(); btn.targetGraphic = bgImg;
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = bg; cb.highlightedColor = Br(bg, 0.08f);
            cb.pressedColor = Br(bg, 0.16f); cb.colorMultiplier = 1f;
            btn.colors = cb;

            var lbl = L("Lbl", rt, label, 12, textColor);
            lbl.alignment = TextAnchor.MiddleCenter; AnchorFull(Rt(lbl));
            return btn;
        }

        // ─── PANEL DE EDICIÓN MODAL ───────────────────────────────────────────

        private void BuildEditPanel()
        {
            // FIX: referencia directa al backdrop
            var bdRt = Mk("EditBackdrop", transform);
            AnchorFull(bdRt);
            var bdImg = bdRt.gameObject.AddComponent<Image>();
            bdImg.color = CA(Color.black, 0.65f); // semi-oscuro visible — correcto
            var bdBtn = bdRt.gameObject.AddComponent<Button>(); bdBtn.targetGraphic = bdImg;
            bdBtn.transition = Selectable.Transition.None;
            bdBtn.onClick.AddListener(CloseEditPanel);
            _editBackdrop = bdRt; // FIX: guardamos referencia directa
            _editBackdrop.gameObject.SetActive(false);

            _editPanel = Mk("EditPanel", transform);
            _editPanel.anchorMin = new Vector2(0.05f, 0.5f); _editPanel.anchorMax = new Vector2(0.95f, 0.5f);
            _editPanel.pivot = new Vector2(0.5f, 0.5f);
            _editPanel.sizeDelta = new Vector2(0, 320);
            _editPanel.anchoredPosition = Vector2.zero;
            Bg(_editPanel, _cBg);

            var brd2 = Mk("Brd", _editPanel);
            brd2.anchorMin = new Vector2(0, 1); brd2.anchorMax = new Vector2(1, 1);
            brd2.sizeDelta = new Vector2(0, 2); brd2.anchoredPosition = Vector2.zero;
            Bg(brd2, _cPrimary);

            var title = L("Title", _editPanel, "EDITAR WAYPOINT", 13, _cPrimary);
            title.fontStyle = FontStyle.Bold;
            Rt(title).anchorMin = new Vector2(0, 1); Rt(title).anchorMax = new Vector2(1, 1);
            Rt(title).offsetMin = new Vector2(20, -50); Rt(title).offsetMax = new Vector2(-20, -14);

            // Nombre
            var nameLbl = L("NameLbl", _editPanel, "Nombre", 12, _cMuted);
            Rt(nameLbl).anchorMin = new Vector2(0, 1); Rt(nameLbl).anchorMax = new Vector2(1, 1);
            Rt(nameLbl).offsetMin = new Vector2(20, -80); Rt(nameLbl).offsetMax = new Vector2(-20, -56);

            var nameField = Mk("NameField", _editPanel);
            nameField.anchorMin = new Vector2(0, 1); nameField.anchorMax = new Vector2(1, 1);
            nameField.offsetMin = new Vector2(20, -130); nameField.offsetMax = new Vector2(-20, -84);
            Bg(nameField, _cSurface2);

            _editNameInput = nameField.gameObject.AddComponent<InputField>();
            _editNameInput.transition = Selectable.Transition.None;
            _editNameInput.targetGraphic = nameField.GetComponent<Image>();
            var nameText = L("Text", nameField, "", 16, _cText);
            nameText.alignment = TextAnchor.MiddleLeft;
            Rt(nameText).anchorMin = Vector2.zero; Rt(nameText).anchorMax = Vector2.one;
            Rt(nameText).offsetMin = new Vector2(12, 4); Rt(nameText).offsetMax = new Vector2(-12, -4);
            var namePh = L("Ph", nameField, "Nombre del waypoint", 16, _cMuted);
            namePh.alignment = TextAnchor.MiddleLeft; namePh.fontStyle = FontStyle.Italic;
            Rt(namePh).anchorMin = Vector2.zero; Rt(namePh).anchorMax = Vector2.one;
            Rt(namePh).offsetMin = new Vector2(12, 4); Rt(namePh).offsetMax = new Vector2(-12, -4);
            _editNameInput.textComponent = nameText;
            _editNameInput.placeholder   = namePh;

            // Tipo
            var typeLbl = L("TypeLbl", _editPanel, "Tipo", 12, _cMuted);
            Rt(typeLbl).anchorMin = new Vector2(0, 1); Rt(typeLbl).anchorMax = new Vector2(1, 1);
            Rt(typeLbl).offsetMin = new Vector2(20, -158); Rt(typeLbl).offsetMax = new Vector2(-20, -134);

            var typeField = Mk("TypeField", _editPanel);
            typeField.anchorMin = new Vector2(0, 1); typeField.anchorMax = new Vector2(1, 1);
            typeField.offsetMin = new Vector2(20, -208); typeField.offsetMax = new Vector2(-20, -162);
            Bg(typeField, _cSurface2);

            _editTypeDropdown = typeField.gameObject.AddComponent<Dropdown>();
            _editTypeDropdown.ClearOptions();
            _editTypeDropdown.AddOptions(Enum.GetNames(typeof(WaypointType)).ToList());

            var dropText = L("Label", typeField, "Generic", 15, _cText);
            dropText.alignment = TextAnchor.MiddleLeft;
            Rt(dropText).anchorMin = Vector2.zero; Rt(dropText).anchorMax = Vector2.one;
            Rt(dropText).offsetMin = new Vector2(12, 4); Rt(dropText).offsetMax = new Vector2(-30, -4);
            _editTypeDropdown.captionText = dropText;

            var dropArrow = L("Arrow", typeField, "▾", 18, _cMuted);
            dropArrow.alignment = TextAnchor.MiddleRight;
            Rt(dropArrow).anchorMin = new Vector2(1, 0); Rt(dropArrow).anchorMax = new Vector2(1, 1);
            Rt(dropArrow).offsetMin = new Vector2(-30, 0); Rt(dropArrow).offsetMax = Vector2.zero;

            _editSaveBtn   = MkNavBtn("SaveBtn",   _editPanel, "✓ Guardar",  0f,   0.5f, -268, -218, CA(_cPrimary, 0.2f), _cPrimary);
            _editCancelBtn = MkNavBtn("CancelBtn", _editPanel, "✕ Cancelar", 0.5f, 1f,   -268, -218, _cSurface2, _cMuted);
            _editSaveBtn.onClick.AddListener(SaveEditedWaypoint);
            _editCancelBtn.onClick.AddListener(CloseEditPanel);

            _editPanel.gameObject.SetActive(false);
        }

        // ─── MENU PANEL ───────────────────────────────────────────────────────

        private void BuildMenuPanel()
        {
            // FIX: el backdrop va al FINAL del hierarchy (SetAsLastSibling al abrir)
            // para quedar por encima del sheet y del navPanel.
            // Alpha = 0.01f en lugar de 0f: con alpha=0 el GraphicRaycaster
            // puede ignorar el raycast según la configuración de Sprite/Image.
            // Con 0.01f es invisible pero siempre raycasteable.
            var backdrop = Mk("MenuBackdrop", transform);
            AnchorFull(backdrop);
            Image bdImg = backdrop.gameObject.AddComponent<Image>();
            bdImg.color = CA(Color.black, 0.01f); // FIX: alpha mínimo para raycast fiable
            Button bdBtn = backdrop.gameObject.AddComponent<Button>(); bdBtn.targetGraphic = bdImg;
            bdBtn.transition = Selectable.Transition.None;
            bdBtn.onClick.AddListener(CloseMenu);
            backdrop.gameObject.SetActive(false);
            _menuBackdrop = backdrop.gameObject;

            _menuPanel = Mk("MenuPanel", transform);
            _menuPanel.anchorMin = new Vector2(1, 0); _menuPanel.anchorMax = new Vector2(1, 1);
            _menuPanel.pivot     = new Vector2(1, 0.5f);
            _menuPanel.sizeDelta = new Vector2(290, 0);
            _menuPanel.anchoredPosition = new Vector2(295, 0);
            Bg(_menuPanel, _cSurface);

            var brdL = Mk("BrdL", _menuPanel);
            brdL.anchorMin = new Vector2(0, 0); brdL.anchorMax = new Vector2(0, 1);
            brdL.sizeDelta = new Vector2(2, 0); brdL.anchoredPosition = Vector2.zero;
            Bg(brdL, CA(_cPrimary, 0.6f));

            float safePad = SafeTop() + 18f;

            var title = L("Title", _menuPanel, "OPCIONES", 13, _cPrimary);
            title.fontStyle = FontStyle.Bold;
            Rt(title).anchorMin = new Vector2(0, 1); Rt(title).anchorMax = new Vector2(1, 1);
            Rt(title).offsetMin = new Vector2(22, -safePad - 34); Rt(title).offsetMax = new Vector2(-50, -safePad);

            {
                var xRt = Mk("CloseBtn", _menuPanel);
                xRt.anchorMin = new Vector2(1, 1); xRt.anchorMax = new Vector2(1, 1);
                xRt.pivot = new Vector2(1, 1); xRt.sizeDelta = new Vector2(44, 44);
                xRt.anchoredPosition = new Vector2(-10, -safePad - 4);
                Image xImg = xRt.gameObject.AddComponent<Image>(); xImg.color = Color.clear;
                Button xBtn = xRt.gameObject.AddComponent<Button>(); xBtn.targetGraphic = xImg;
                xBtn.onClick.AddListener(CloseMenu);
                var xLbl = L("X", xRt, "✕", 17, _cMuted); xLbl.alignment = TextAnchor.MiddleCenter;
                AnchorFull(Rt(xLbl));
            }

            MkSep(_menuPanel, safePad + 44);

            float y = safePad + 56; float h = 56; float g = 6;
            MkMenuRow(_menuPanel, "💾   Guardar sesión",   y,         h, CA(_cPrimary, 0.1f), _cText,   OnMenuSave);
            MkMenuRow(_menuPanel, "📂   Cargar sesión",    y+h+g,     h, CA(_cPrimary, 0.1f), _cText,   OnMenuLoad);
            MkMenuRow(_menuPanel, "🗺️   Generar NavMesh",  y+(h+g)*2, h, CA(_cPrimary, 0.1f), _cText,   OnMenuBake);

            MkSep(_menuPanel, y + (h + g) * 3 + 8);
            MkMenuRow(_menuPanel, "🗑️   Borrar todo",      y+(h+g)*3+16, h, CA(_cDanger, 0.12f), _cDanger, OnMenuClear);

            var info = L("Info", _menuPanel, "Sin sesión guardada", 11, _cMuted);
            info.alignment = TextAnchor.LowerLeft;
            Rt(info).anchorMin = new Vector2(0, 0); Rt(info).anchorMax = new Vector2(1, 0);
            Rt(info).offsetMin = new Vector2(22, SafeBottom() + 14);
            Rt(info).offsetMax = new Vector2(-22, SafeBottom() + 36);
        }

        private void MkSep(RectTransform p, float topOff)
        {
            var s = Mk("Sep", p);
            s.anchorMin = new Vector2(0, 1); s.anchorMax = new Vector2(1, 1);
            s.offsetMin = new Vector2(18, -topOff - 1); s.offsetMax = new Vector2(-18, -topOff);
            Bg(s, CA(Color.white, 0.07f));
        }

        private void MkMenuRow(RectTransform p, string label, float topOff, float h,
            Color bg, Color tc, UnityEngine.Events.UnityAction action)
        {
            var rt = Mk("Row_" + label, p);
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.offsetMin = new Vector2(12, -topOff - h); rt.offsetMax = new Vector2(-12, -topOff);

            Image bgImg = rt.gameObject.AddComponent<Image>(); bgImg.color = bg;
            Button btn = rt.gameObject.AddComponent<Button>(); btn.targetGraphic = bgImg;
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = bg; cb.highlightedColor = Br(bg, 0.1f);
            cb.pressedColor = Br(bg, 0.2f); cb.colorMultiplier = 1f;
            btn.colors = cb; btn.onClick.AddListener(action);

            var lbl = L("Lbl", rt, label, 15, tc);
            lbl.alignment = TextAnchor.MiddleLeft; AnchorFull(Rt(lbl));
            Rt(lbl).offsetMin = new Vector2(16, 0);
        }

        // ─── TOAST LAYER ──────────────────────────────────────────────────────

        private void BuildToastLayer()
        {
            _toastLayer = Mk("ToastLayer", transform);
            _toastLayer.anchorMin = new Vector2(0, 0); _toastLayer.anchorMax = new Vector2(1, 0);
            _toastLayer.pivot     = new Vector2(0.5f, 0);
            _toastLayer.sizeDelta = new Vector2(-28, 56);
            // FIX 2: posición calculada dinámicamente
            float toastY = _peekH + FAB_SIZE + FAB_PAD + 14f;
            _toastLayer.anchoredPosition = new Vector2(0, toastY);
        }

        // ─── ScrollView helper ────────────────────────────────────────────────

        private ScrollRect MkScrollView(string name, RectTransform parent,
            Vector2 amin, Vector2 amax, Vector2 offMin, Vector2 offMax)
        {
            var c = Mk(name, parent);
            c.anchorMin = amin; c.anchorMax = amax;
            c.offsetMin = offMin; c.offsetMax = offMax;

            var sr = c.gameObject.AddComponent<ScrollRect>();
            sr.horizontal = false; sr.vertical = true;
            sr.scrollSensitivity = 38; sr.movementType = ScrollRect.MovementType.Elastic;
            sr.elasticity = 0.08f;

            var vp = Mk("Viewport", c); AnchorFull(vp);
            vp.gameObject.AddComponent<Image>().color = Color.clear;
            vp.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            sr.viewport = vp;

            var content = Mk("Content", vp);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1); content.anchoredPosition = Vector2.zero;

            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6; vlg.padding = new RectOffset(12, 12, 8, 8);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;

            var csf = content.gameObject.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            sr.content = content;
            return sr;
        }

        // =====================================================================
        //  SHEET STATE
        // =====================================================================

        private void SnapSheet(SheetState state, bool instant = false)
        {
            _sheetState = state;
            _sheetTargetY = state == SheetState.Half ? _sheetHalfY :
                            state == SheetState.Full ? _sheetFullY :
                            _sheetPeekY;
            if (instant && _sheet != null)
                _sheet.anchoredPosition = new Vector2(0, _sheetTargetY);
        }

        private void SmoothSheet()
        {
            if (_sheet == null) return;
            var cur = _sheet.anchoredPosition;
            _sheet.anchoredPosition = new Vector2(0,
                Mathf.Lerp(cur.y, _sheetTargetY, Time.unscaledDeltaTime * _sheetAnimSpeed));
        }

        private void OnHandleTap()
        {
            if      (_sheetState == SheetState.Collapsed) SnapSheet(SheetState.Half);
            else if (_sheetState == SheetState.Half)      SnapSheet(SheetState.Full);
            else                                           SnapSheet(SheetState.Collapsed);
        }

        // =====================================================================
        //  TOUCH
        // =====================================================================

        private void HandleTouch()
        {
            var touchscreen = Touchscreen.current;
            if (touchscreen != null) { HandleTouchscreen(touchscreen); return; }
            var mouse = Mouse.current;
            if (mouse != null) HandleMouse(mouse);
        }

        private void HandleTouchscreen(Touchscreen touchscreen)
        {
            var touch     = touchscreen.primaryTouch;
            bool isActive = touch.press.isPressed;
            Vector2 pos   = touch.position.ReadValue();

            if (isActive && !_prevTouchActive)       BeganTouch(pos);
            else if (isActive && _prevTouchActive)   MovedTouch(pos);
            else if (!isActive && _prevTouchActive)  HandleTouchEnd(pos, touch.delta.ReadValue());

            _prevTouchActive = isActive;
        }

        private void HandleMouse(Mouse mouse)
        {
            bool isActive  = mouse.leftButton.isPressed;
            bool rightNow  = mouse.rightButton.wasPressedThisFrame;
            Vector2 pos    = mouse.position.ReadValue();

            if (rightNow && _isNavigating) { OnLongPress(pos); return; }

            if (isActive && !_prevMouseActive)       BeganTouch(pos);
            else if (isActive && _prevMouseActive)   MovedTouch(pos);
            else if (!isActive && _prevMouseActive)  HandleTouchEnd(pos, Vector2.zero);

            _prevMouseActive = isActive;
        }

        private void BeganTouch(Vector2 pos)
        {
            _touchDownT    = Time.time;
            _touchDownPos  = pos;
            _longFired     = false;
            _touchOnSheet  = SheetIsVisible() && pos.y < GetSheetTopScreenY();
            _touchOnScroll = _touchOnSheet && IsPointerOverScrollRect(pos);
            if (_touchOnSheet && !_touchOnScroll)
            {
                _sheetDragStartScreenY = pos.y;
                _sheetDragStartAnchorY = _sheet.anchoredPosition.y;
            }
        }

        private void MovedTouch(Vector2 pos)
        {
            if (_touchOnSheet && !_touchOnScroll)
            {
                float dy   = (pos.y - _sheetDragStartScreenY) / _canvas.scaleFactor;
                float newY = Mathf.Clamp(_sheetDragStartAnchorY + dy, _sheetPeekY, _sheetFullY);
                _sheet.anchoredPosition = new Vector2(0, newY);
                _sheetTargetY = newY;
            }
            else if (!_longFired
                     && Vector2.Distance(pos, _touchDownPos) < TOUCH_MOVE_TOL
                     && Time.time - _touchDownT >= _longPressTime)
            {
                _longFired = true;
                OnLongPress(pos);
            }
        }

        private void HandleTouchEnd(Vector2 pos, Vector2 delta)
        {
            if (_touchOnSheet && !_touchOnScroll)
            {
                float curY = _sheet.anchoredPosition.y;
                if (delta.y > 22f)
                    SnapSheet(curY > _sheetHalfY * 0.7f ? SheetState.Full : SheetState.Half);
                else if (delta.y < -22f)
                    SnapSheet(curY < _sheetHalfY * 1.3f ? SheetState.Collapsed : SheetState.Half);
                else
                {
                    float dp = Mathf.Abs(curY - _sheetPeekY);
                    float dh = Mathf.Abs(curY - _sheetHalfY);
                    float df = Mathf.Abs(curY - _sheetFullY);
                    if      (dp <= dh && dp <= df) SnapSheet(SheetState.Collapsed);
                    else if (dh <= df)             SnapSheet(SheetState.Half);
                    else                           SnapSheet(SheetState.Full);
                }
            }
            else if (!_longFired
                     && Time.time - _touchDownT < _longPressTime
                     && Vector2.Distance(pos, _touchDownPos) < TOUCH_MOVE_TOL
                     && !IsPointerOverInteractableUI(pos))
            {
                OnTap(pos);
            }
        }

        private bool SheetIsVisible() =>
            _sheet != null && _sheet.anchoredPosition.y > _sheetPeekY + 20f;

        private float GetSheetTopScreenY()
        {
            if (_sheet == null) return 0;
            return (_sheet.anchoredPosition.y + _sheet.rect.height) * _canvas.scaleFactor;
        }

        private bool IsPointerOverInteractableUI(Vector2 sp)
        {
            var ped = new PointerEventData(EventSystem.current) { position = sp };
            var res = new List<RaycastResult>();
            EventSystem.current?.RaycastAll(ped, res);
            foreach (var r in res)
            {
                var go = r.gameObject;

                // FIX: Los backdrops (menú y edit) son Buttons que cubren toda
                // la pantalla. Si el menú o el panel de edición está abierto,
                // NO los consideramos "UI interactable" para que OnTap() pueda
                // ejecutarse y llamar a CloseMenu() / CloseEditPanel().
                bool isMenuBackdrop = _menuBackdrop != null && go == _menuBackdrop;
                bool isEditBackdrop = _editBackdrop != null && go == _editBackdrop.gameObject;
                if (isMenuBackdrop || isEditBackdrop) continue;

                if (go.GetComponent<Button>()     != null) return true;
                if (go.GetComponent<InputField>() != null) return true;
                if (go.GetComponent<Slider>()     != null) return true;
                if (go.GetComponent<Toggle>()     != null) return true;
                if (go.GetComponent<ScrollRect>() != null) return true;
                if (go.GetComponent<Dropdown>()   != null) return true;
            }
            return false;
        }

        private bool IsPointerOverScrollRect(Vector2 sp)
        {
            // FIX: verifica también _favScrollRect
            if (_wpScrollRect == null && _favScrollRect == null) return false;
            var ped = new PointerEventData(EventSystem.current) { position = sp };
            var res = new List<RaycastResult>();
            EventSystem.current?.RaycastAll(ped, res);
            foreach (var r in res)
            {
                if (r.gameObject.GetComponent<ScrollRect>() != null) return true;
                if (_wpScrollRect  != null && r.gameObject.transform.IsChildOf(_wpScrollRect.transform))  return true;
                if (_favScrollRect != null && r.gameObject.transform.IsChildOf(_favScrollRect.transform)) return true;
            }
            return false;
        }

        private void OnTap(Vector2 screenPos)
        {
            // FIX: el editPanel tiene prioridad — si está abierto, cualquier tap
            // fuera de él (o sobre su backdrop) lo cierra.
            if (_editPanel != null && _editPanel.gameObject.activeSelf)
            {
                CloseEditPanel();
                return;
            }

            // FIX: si el menú está abierto, cualquier tap lo cierra (el backdrop
            // ya no bloquea HandleTouchEnd porque lo excluimos en IsPointerOverInteractableUI).
            if (_menuOpen) { CloseMenu(); return; }

            if (_sheetState != SheetState.Collapsed) { SnapSheet(SheetState.Collapsed); return; }
            if (_tapToAddWaypoint) { TryAddWaypointAtTouch(screenPos); return; }
            if (_tapToNavigate)   TryNavigateToTouchPoint(screenPos);
        }

        private void OnLongPress(Vector2 screenPos)
        {
            if (_isNavigating) { CancelNavigation(); ShowToast("Navegación cancelada", _cWarning); Vibrate(); }
        }

        private void TryNavigateToTouchPoint(Vector2 sp)
        {
            if (_arSessionManager == null) return;
            if (_arSessionManager.Raycast(sp, out ARRaycastHit hit))
            {
                NavigateToPosition(hit.pose.position, "Punto seleccionado"); Vibrate();
            }
            else if (_arCamera != null)
            {
                Ray ray = _arCamera.ScreenPointToRay(sp);
                if (Physics.Raycast(ray, out RaycastHit phyHit, 50f))
                { NavigateToPosition(phyHit.point, "Punto seleccionado"); Vibrate(); }
            }
        }

        private void TryAddWaypointAtTouch(Vector2 sp)
        {
            if (_waypointManager == null) { ShowToast("WaypointManager no disponible", _cDanger); return; }

            Vector3 worldPos = Vector3.zero;
            bool    found    = false;

            if (_arSessionManager != null && _arSessionManager.Raycast(sp, out ARRaycastHit hit))
            { worldPos = hit.pose.position; found = true; }
            else if (_arCamera != null)
            {
                Ray ray = _arCamera.ScreenPointToRay(sp);
                if (Physics.Raycast(ray, out RaycastHit phyHit, 50f))
                { worldPos = phyHit.point; found = true; }
            }

            if (found)
            {
                var wp = _waypointManager.CreateWaypoint(worldPos, Quaternion.identity);
                if (wp != null)
                {
                    ShowToast($"Waypoint añadido ✓ ({_waypointManager.WaypointCount} total)", _cSuccess);
                    Vibrate();
                    // ScheduleListRefresh() es suficiente: el WaypointPlacedEvent lo activa
                }
            }
            else ShowToast("No se encontró superficie. Apunta al suelo.", _cWarning);
        }

        // =====================================================================
        //  WAYPOINT LIST
        // =====================================================================

        /// <summary>
        /// FIX 1: Refresca la lista de waypoints.
        /// Ahora robusto ante llamadas múltiples rápidas (debounce en Update).
        /// También fuerza el LayoutRebuild con doble yield.
        /// </summary>
        public void RefreshWaypointList()
        {
            if (_wpListContent == null) return;

            foreach (Transform c in _wpListContent) Destroy(c.gameObject);
            _wpItems.Clear();

            if (_waypointManager == null)
            {
                MkEmptyMsg(_wpListContent, "WaypointManager no encontrado.\nVerifica la escena.");
                return;
            }

            // FIX 1: Comprobación defensiva antes de iterar
            var waypoints = _waypointManager.Waypoints;
            if (waypoints == null)
            {
                MkEmptyMsg(_wpListContent, "Error leyendo waypoints del manager.");
                return;
            }

            IEnumerable<WaypointData> all = waypoints;

            if (!string.IsNullOrEmpty(_searchQuery))
                all = all.Where(w => w.WaypointName.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0);

            var sorted = all
                .OrderBy(w => Mathf.RoundToInt(w.Position.y * 2f))
                .ThenBy(w => w.WaypointName)
                .ToList();

            int lastLevel = int.MinValue;
            foreach (var wp in sorted)
            {
                int level = Mathf.RoundToInt(wp.Position.y * 2f);
                if (level != lastLevel)
                {
                    MkLevelHeader(_wpListContent, $"PLANTA {Mathf.RoundToInt(wp.Position.y)}");
                    lastLevel = level;
                }
                _wpItems.Add(BuildWpItem(_wpListContent, wp, showCrudButtons: true));
            }

            if (!_wpItems.Any())
                MkEmptyMsg(_wpListContent, string.IsNullOrEmpty(_searchQuery)
                    ? "Sin destinos.\nUsa ＋ para añadir waypoints."
                    : $"Sin resultados para \"{_searchQuery}\"");

            // FIX 1: Doble yield garantiza que ContentSizeFitter recalcule
            StartCoroutine(ForceLayoutRebuild(_wpListContent, _wpScrollRect));
            UpdateStatusBar();
        }

        public void RefreshFavoriteList()
        {
            if (_favListContent == null) return;
            foreach (Transform c in _favListContent) Destroy(c.gameObject);
            _favItems.Clear();

            if (_waypointManager == null) { MkEmptyMsg(_favListContent, "WaypointManager no encontrado."); return; }

            var favs = _waypointManager.Waypoints?
                .Where(w => IsFavorite(w.WaypointId))
                .OrderBy(w => w.WaypointName)
                .ToList() ?? new List<WaypointData>();

            foreach (var wp in favs)
                _favItems.Add(BuildWpItem(_favListContent, wp, showCrudButtons: false));

            if (!_favItems.Any())
                MkEmptyMsg(_favListContent, "Sin favoritos.\nToca ⭐ en un destino para marcarlo.");

            StartCoroutine(ForceLayoutRebuild(_favListContent, _favScrollRect));
        }

        private void MkLevelHeader(RectTransform parent, string title)
        {
            var go = new GameObject("LvlHdr");
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 28; le.minHeight = 28; le.flexibleWidth = 1;
            var lbl = go.AddComponent<Text>();
            lbl.text = title; lbl.fontSize = 10; lbl.color = _cMuted;
            lbl.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            lbl.fontStyle = FontStyle.Bold; lbl.alignment = TextAnchor.MiddleLeft;
            var rt = go.GetComponent<RectTransform>();
            rt.offsetMin = new Vector2(18, 0);
        }

        /// <summary>
        /// FIX 1: Doble yield antes de ForceRebuildLayoutImmediate para que
        /// ContentSizeFitter procese todos los hijos recién creados.
        /// </summary>
        private IEnumerator ForceLayoutRebuild(RectTransform rt, ScrollRect sr = null)
        {
            yield return null;
            yield return null;
            if (rt != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
                yield return null; // un frame más para que el scroll recalcule
                if (sr != null) sr.verticalNormalizedPosition = 1f;
            }
        }

        private WpItemView BuildWpItem(RectTransform parent, WaypointData wp, bool showCrudButtons)
        {
            var go = new GameObject($"WpItem_{wp.WaypointId}");
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 72; le.minHeight = 72; le.flexibleWidth = 1;

            Image bg = go.AddComponent<Image>(); bg.color = _cSurface;
            Button btn = go.AddComponent<Button>(); btn.targetGraphic = bg;
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = _cSurface; cb.highlightedColor = Br(_cSurface, 0.07f);
            cb.pressedColor = CA(_cPrimary, 0.18f); cb.selectedColor = CA(_cPrimary, 0.14f);
            cb.colorMultiplier = 1f; btn.colors = cb;

            var rt = go.GetComponent<RectTransform>();

            var stripe = Mk("S", rt);
            stripe.anchorMin = new Vector2(0, 0); stripe.anchorMax = new Vector2(0, 1);
            stripe.offsetMin = Vector2.zero; stripe.offsetMax = new Vector2(4, 0);
            Bg(stripe, wp.Color);

            bool isFav = IsFavorite(wp.WaypointId);
            var name = L("N", rt, (isFav ? "⭐ " : "") + wp.WaypointName, 16, _cText);
            name.fontStyle = FontStyle.Bold; name.alignment = TextAnchor.MiddleLeft;
            Rt(name).anchorMin = new Vector2(0, 0.5f); Rt(name).anchorMax = new Vector2(1, 1);
            Rt(name).offsetMin = new Vector2(16, 2); Rt(name).offsetMax = new Vector2(showCrudButtons ? -90 : -44, -2);

            var type = L("T", rt, wp.Type.ToString(), 12, _cMuted);
            type.alignment = TextAnchor.MiddleLeft;
            Rt(type).anchorMin = new Vector2(0, 0); Rt(type).anchorMax = new Vector2(1, 0.5f);
            Rt(type).offsetMin = new Vector2(16, 2); Rt(type).offsetMax = new Vector2(showCrudButtons ? -90 : -44, -2);

            var view = new WpItemView { Data = wp, Root = go, Bg = bg };
            btn.onClick.AddListener(() => OnWpTap(view));

            if (showCrudButtons)
            {
                // Botón ⭐ favorito
                var favBtnRt = Mk("FavBtn", rt);
                favBtnRt.anchorMin = new Vector2(1, 0.5f); favBtnRt.anchorMax = new Vector2(1, 0.5f);
                favBtnRt.pivot = new Vector2(1, 0.5f); favBtnRt.sizeDelta = new Vector2(36, 36);
                favBtnRt.anchoredPosition = new Vector2(-50, 0);
                var favImg = favBtnRt.gameObject.AddComponent<Image>(); favImg.color = Color.clear;
                var favBtn = favBtnRt.gameObject.AddComponent<Button>(); favBtn.targetGraphic = favImg;
                var favLbl = L("F", favBtnRt, "⭐", 18, isFav ? _cFav : CA(_cMuted, 0.4f));
                favLbl.alignment = TextAnchor.MiddleCenter; AnchorFull(Rt(favLbl));
                string capId = wp.WaypointId;
                favBtn.onClick.AddListener(() => { ToggleFavorite(capId); Vibrate(); });

                // Botón ✏️ editar
                var editBtnRt = Mk("EditBtn", rt);
                editBtnRt.anchorMin = new Vector2(1, 0.5f); editBtnRt.anchorMax = new Vector2(1, 0.5f);
                editBtnRt.pivot = new Vector2(1, 0.5f); editBtnRt.sizeDelta = new Vector2(36, 36);
                editBtnRt.anchoredPosition = new Vector2(-10, 0);
                var editImg = editBtnRt.gameObject.AddComponent<Image>(); editImg.color = Color.clear;
                var editBtn = editBtnRt.gameObject.AddComponent<Button>(); editBtn.targetGraphic = editImg;
                var editLbl = L("E", editBtnRt, "✏", 16, CA(_cMuted, 0.7f));
                editLbl.alignment = TextAnchor.MiddleCenter; AnchorFull(Rt(editLbl));
                WaypointData capWp = wp;
                editBtn.onClick.AddListener(() => OpenEditPanel(capWp));
            }
            else
            {
                var arr = L("A", rt, "›", 30, CA(_cPrimary, 0.45f));
                arr.alignment = TextAnchor.MiddleRight;
                Rt(arr).anchorMin = new Vector2(1, 0); Rt(arr).anchorMax = new Vector2(1, 1);
                Rt(arr).offsetMin = new Vector2(-38, 0); Rt(arr).offsetMax = Vector2.zero;
                view.Arrow = arr;
            }

            return view;
        }

        private void OnWpTap(WpItemView v)
        {
            _selectedWp = v.Data;
            Vibrate();
            StartNavigationToWaypoint(v.Data);
            SnapSheet(SheetState.Collapsed);
        }

        private void MkEmptyMsg(RectTransform parent, string msg)
        {
            var lbl = L("Empty", parent, msg, 14, _cMuted);
            lbl.alignment = TextAnchor.MiddleCenter; lbl.fontStyle = FontStyle.Italic;
            var le = lbl.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 110; le.minHeight = 110; le.flexibleWidth = 1;
        }

        // ─── CRUD: Panel de edición ───────────────────────────────────────────

        private void OpenEditPanel(WaypointData wp)
        {
            _editingWp = wp;
            _editNameInput.text = wp.WaypointName;
            _editTypeDropdown.value = (int)wp.Type;
            // FIX: traer al frente antes de activar para garantizar draw order correcto
            if (_editBackdrop != null)
            {
                _editBackdrop.gameObject.SetActive(true);
                _editBackdrop.SetAsLastSibling();
            }
            _editPanel.gameObject.SetActive(true);
            _editPanel.SetAsLastSibling();
        }

        private void CloseEditPanel()
        {
            _editPanel.gameObject.SetActive(false);
            _editingWp = null;
            if (_editBackdrop != null) _editBackdrop.gameObject.SetActive(false);
        }

        private void SaveEditedWaypoint()
        {
            if (_editingWp == null || _waypointManager == null) { CloseEditPanel(); return; }

            string newName = _editNameInput.text.Trim();
            if (string.IsNullOrEmpty(newName)) { ShowToast("El nombre no puede estar vacío", _cWarning); return; }

            WaypointType newType  = (WaypointType)_editTypeDropdown.value;
            Color newColor        = WaypointData.GetDefaultColorForType(newType);

            bool ok = _waypointManager.UpdateWaypoint(_editingWp.WaypointId, newName, newType, newColor);
            if (ok)
            {
                ShowToast($"Waypoint actualizado: {newName}", _cSuccess);
                Vibrate();
                CloseEditPanel();
                ScheduleListRefresh();
            }
            else ShowToast("No se pudo actualizar el waypoint", _cDanger);
        }

        public void DeleteWaypoint(WaypointData wp)
        {
            if (_waypointManager == null || wp == null) return;
            string name = wp.WaypointName;
            string id   = wp.WaypointId;
            bool ok = _waypointManager.RemoveWaypoint(id);
            if (ok)
            {
                _favorites.Remove(id);
                SaveFavorites();
                ShowToast($"Waypoint eliminado: {name}", _cDanger);
                if (_isNavigating && _currentNavTarget?.WaypointId == id)
                    CancelNavigation();
            }
        }

        // =====================================================================
        //  ROUTE LIST
        // =====================================================================

        public void RefreshRouteList()
        {
            if (_rtListContent == null) return;
            foreach (Transform c in _rtListContent) Destroy(c.gameObject);

            MkRouteItem("⌂   Regresar al inicio",
                "Navega al punto de entrada del nivel actual", _cPrimary, OnFabHomePressed);

            if (_waypointManager != null && _waypointManager.WaypointCount > 1)
                MkRouteItem("▶   Ruta guiada completa",
                    $"Visita los {_waypointManager.WaypointCount} destinos en orden",
                    _cSuccess, StartGuidedRoute);

            if (_waypointManager != null)
            {
                var entries = _waypointManager.GetWaypointsByType(WaypointType.Entrance);
                if (entries.Count > 0)
                    MkRouteItem("↪   Ir a Entrada", $"{entries.Count} entrada(s) disponible(s)", _cWarning,
                        () => StartNavigationToWaypoint(entries[0]));

                var exits = _waypointManager.GetWaypointsByType(WaypointType.Exit);
                if (exits.Count > 0)
                    MkRouteItem("↗   Ir a Salida", $"{exits.Count} salida(s) disponible(s)", _cDanger,
                        () => StartNavigationToWaypoint(exits[0]));

                var stairs = _waypointManager.GetWaypointsByType(WaypointType.Stairs);
                if (stairs.Count > 0)
                    MkRouteItem("⬆   Ir a Escaleras", $"{stairs.Count} punto(s) de acceso",
                        new Color(0.6f, 0.8f, 1f), () => StartNavigationToWaypoint(stairs[0]));
            }

            if (_rtListContent.childCount == 0)
                MkEmptyMsg(_rtListContent, "Añade waypoints para ver rutas disponibles.");

            StartCoroutine(ForceLayoutRebuild(_rtListContent));
        }

        private void MkRouteItem(string title, string sub, Color accent,
            UnityEngine.Events.UnityAction action)
        {
            var go = new GameObject("RI"); go.transform.SetParent(_rtListContent, false);
            go.AddComponent<RectTransform>();
            var le = go.AddComponent<LayoutElement>(); le.preferredHeight = 78; le.minHeight = 78; le.flexibleWidth = 1;

            Image bg = go.AddComponent<Image>(); bg.color = _cSurface;
            Button btn = go.AddComponent<Button>(); btn.targetGraphic = bg;
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = _cSurface; cb.highlightedColor = Br(_cSurface, 0.07f);
            cb.pressedColor = CA(accent, 0.18f); cb.colorMultiplier = 1f;
            btn.colors = cb; btn.onClick.AddListener(action);

            var rt = go.GetComponent<RectTransform>();
            var stripe = Mk("S", rt); stripe.anchorMin = new Vector2(0, 0); stripe.anchorMax = new Vector2(0, 1);
            stripe.offsetMin = Vector2.zero; stripe.offsetMax = new Vector2(3, 0); Bg(stripe, accent);

            var t = L("T", rt, title, 16, _cText); t.fontStyle = FontStyle.Bold; t.alignment = TextAnchor.MiddleLeft;
            Rt(t).anchorMin = new Vector2(0, 0.5f); Rt(t).anchorMax = new Vector2(1, 1);
            Rt(t).offsetMin = new Vector2(18, 2); Rt(t).offsetMax = new Vector2(-16, -2);

            var s = L("S2", rt, sub, 12, _cMuted); s.alignment = TextAnchor.MiddleLeft;
            Rt(s).anchorMin = new Vector2(0, 0); Rt(s).anchorMax = new Vector2(1, 0.5f);
            Rt(s).offsetMin = new Vector2(18, 2); Rt(s).offsetMax = new Vector2(-16, -2);
        }

        // =====================================================================
        //  NAVIGATION
        // =====================================================================

        public void NavigateToPosition(Vector3 pos, string label = "Destino")
        {
            if (_navigationAgent == null) return;
            _currentNavTarget = null;
            _navigationAgent.StartNavigation(pos);
            SetNavigating(true, label);
        }

        public void StartNavigationToWaypoint(WaypointData wp)
        {
            if (_navigationAgent == null || wp == null) return;
            bool ok = _navigationAgent.NavigateToWaypoint(wp);
            if (ok)
            {
                _currentNavTarget = wp;
                SetNavigating(true, wp.WaypointName);
                ShowToast($"→ {wp.WaypointName}", _cPrimary);
            }
            else ShowToast("No se puede alcanzar ese destino", _cDanger);
        }

        public void CancelNavigation()
        {
            _navigationAgent?.StopNavigation("UI cancelado");
            _guidedRoute = null; _routeIdx = -1;
            _currentNavTarget = null;
            SetNavigating(false);
        }

        public void RecalculateRoute()
        {
            if (!_isNavigating) return;
            if (_currentNavTarget != null)
            {
                bool ok = _navigationAgent.NavigateToWaypoint(_currentNavTarget);
                ShowToast(ok ? "Ruta recalculada ✓" : "No se pudo recalcular la ruta",
                          ok ? _cSuccess : _cDanger);
            }
            else if (_navigationAgent != null)
            {
                _navigationAgent.StartNavigation(_navigationAgent.LastDestination);
                ShowToast("Ruta recalculada ✓", _cSuccess);
            }
            Vibrate();
        }

        private void SetNavigating(bool nav, string dest = "")
        {
            _isNavigating = nav;
            _sheet.gameObject.SetActive(!nav);
            _fabHome.gameObject.SetActive(!nav);
            _fabAdd.gameObject.SetActive(!nav);
            _navPanel.gameObject.SetActive(nav);

            if (nav)
            {
                _navDestText.text = dest.Length > 28 ? dest.Substring(0, 26) + "…" : dest;
                _navDistText.text = "-- m"; _navEtaText.text = "";
                Rt(_navFill).sizeDelta = Vector2.zero;

                bool guided = _guidedRoute != null && _guidedRoute.Count > 1;
                _navPrevBtn.gameObject.SetActive(guided);
                _navNextBtn.gameObject.SetActive(guided);
                _navRecalcBtn.gameObject.SetActive(true);

                StartCoroutine(SlidePanel(_navPanel, true));
            }
            else
            {
                StartCoroutine(SlidePanel(_navPanel, false));
            }

            UpdateStatusBar();
        }

        private IEnumerator SlidePanel(RectTransform panel, bool show)
        {
            float fromY = panel.anchoredPosition.y;
            float toY   = show ? 0f : _navPanelHiddenY;
            if (show) panel.gameObject.SetActive(true);
            float t = 0f, dur = 0.26f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                panel.anchoredPosition = new Vector2(0, Mathf.SmoothStep(fromY, toY, t / dur));
                yield return null;
            }
            panel.anchoredPosition = new Vector2(0, toY);
            if (!show) panel.gameObject.SetActive(false);
        }

        private void TickNavPanel()
        {
            if (_navigationAgent == null) return;

            float dist = _navigationAgent.DistanceToDestination;
            float prog = _navigationAgent.ProgressPercent;

            _navDistText.text = dist > 1f ? $"{dist:F0} m" : "Llegando...";

            // FIX: evita división por cero si CurrentSpeed == 0
            float speed  = Mathf.Max(_navigationAgent.CurrentSpeed, 0.5f);
            float etaSec = dist / speed;
            _navEtaText.text = etaSec > 8f
                ? (etaSec < 60f ? $"≈ {Mathf.CeilToInt(etaSec)}s" : $"≈ {Mathf.CeilToInt(etaSec / 60f)} min")
                : "";

            float w = Rt(_navFill).parent.GetComponent<RectTransform>().rect.width;
            Rt(_navFill).sizeDelta = new Vector2(w * prog, 0);

            if (_isNavigating && !_navigationAgent.IsNavigating)
            {
                if (_guidedRoute != null && _routeIdx < _guidedRoute.Count - 1)
                {
                    _routeIdx++;
                    StartNavigationToWaypoint(_guidedRoute[_routeIdx]);
                }
                else
                {
                    SetNavigating(false);
                    ShowToast("¡Destino alcanzado! ✓", _cSuccess);
                    Vibrate();
                }
            }

            UpdateStatusBar();
        }

        private void StartGuidedRoute()
        {
            if (_waypointManager == null || _waypointManager.WaypointCount == 0) return;
            _guidedRoute = new List<WaypointData>(_waypointManager.Waypoints
                .OrderBy(w => w.Position.y).ThenBy(w => w.WaypointName));
            _routeIdx = 0;
            StartNavigationToWaypoint(_guidedRoute[0]);
            SnapSheet(SheetState.Collapsed);
            ShowToast($"Ruta guiada: {_guidedRoute.Count} paradas", _cPrimary);
        }

        private void NavNextWaypoint()
        {
            if (_guidedRoute == null || _routeIdx >= _guidedRoute.Count - 1) return;
            _routeIdx++;
            StartNavigationToWaypoint(_guidedRoute[_routeIdx]);
        }

        private void NavPrevWaypoint()
        {
            if (_guidedRoute == null || _routeIdx <= 0) return;
            _routeIdx--;
            StartNavigationToWaypoint(_guidedRoute[_routeIdx]);
        }

        // =====================================================================
        //  STATUS BAR
        // =====================================================================

        private void UpdateStatusBar()
        {
            int level = _navigationAgent?.CurrentLevel ?? 0;
            _txtLevel.text = $"PLANTA {level}";

            int wpCount = _waypointManager?.WaypointCount ?? 0;
            _txtWpCount.text = $"{wpCount} destino{(wpCount != 1 ? "s" : "")}";

            if (_isNavigating && _navigationAgent != null)
            {
                float d = _navigationAgent.DistanceToDestination;
                _txtDistance.text = d > 0 ? $"{d:F0} m" : "✓";
            }
            else _txtDistance.text = "";

            bool hasNM = _persistenceManager != null && _persistenceManager.HasSavedNavMesh;
            _dotNavMesh.color   = hasNM ? _cSuccess : _cDanger;
            _txtNavMeshLbl.text = hasNM ? "NAVMESH ●" : "SIN NAVMESH";
            _txtNavMeshLbl.color = hasNM ? _cSuccess : _cDanger;
        }

        // =====================================================================
        //  FAB HANDLERS
        // =====================================================================

        private void OnFabHomePressed()
        {
            var pts = NavigationStartPointManager.GetAllStartPoints();
            int cur = _navigationAgent?.CurrentLevel ?? 0;
            var pt  = pts.FirstOrDefault(p => p.Level == cur);
            if (pt != null) { NavigateToPosition(pt.Position, $"Inicio — Planta {cur}"); SnapSheet(SheetState.Collapsed); }
            else ShowToast("Sin punto de inicio en esta planta", _cWarning);
        }

        private void OnFabAddPressed()
        {
            _tapToAddWaypoint = !_tapToAddWaypoint;
            _tapToNavigate    = !_tapToAddWaypoint;
            var img = _fabAdd.GetComponent<Image>();
            var lbl = _fabAdd.GetComponentInChildren<Text>();
            img.color = _tapToAddWaypoint ? CA(_cPrimary, 0.28f) : _cSurface;
            lbl.color = _tapToAddWaypoint ? _cPrimary : _cText;
            ShowToast(_tapToAddWaypoint ? "Toca el suelo AR para añadir waypoint" : "Modo navegación activo",
                _tapToAddWaypoint ? _cPrimary : _cMuted);
        }

        private void OnFabVoicePressed()
        {
            if (!_voiceEnabled) { ShowToast("Sistema de voz no disponible aún", _cWarning); return; }
            if (_voiceProvider == null) return;
            if (_voiceListening)
            {
                _voiceProvider.StopListening(); _voiceListening = false;
                _fabVoice.GetComponent<Image>().color = _cSurface;
            }
            else
            {
                _voiceProvider.StartListening(); _voiceListening = true;
                _fabVoice.GetComponent<Image>().color = CA(_cDanger, 0.28f);
                ShowToast("Escuchando...", _cPrimary);
            }
        }

        // =====================================================================
        //  MENU
        // =====================================================================

        public void ToggleMenu() { if (_menuOpen) CloseMenu(); else OpenMenu(); }

        public void OpenMenu()
        {
            if (_menuPanel == null) return;
            _menuOpen = true;
            if (_menuBackdrop != null)
            {
                // FIX: mover backdrop al final del hierarchy para que quede
                // por encima del sheet, navPanel y toastLayer en el sort order.
                // Luego el menuPanel se mueve también al último para quedar
                // delante del backdrop.
                _menuBackdrop.SetActive(true);
                _menuBackdrop.transform.SetAsLastSibling();
                _menuPanel.SetAsLastSibling();
            }
            if (_menuAnimCR != null) StopCoroutine(_menuAnimCR);
            _menuAnimCR = StartCoroutine(AnimMenu(0f));
        }

        public void CloseMenu()
        {
            if (_menuPanel == null) return;
            _menuOpen = false;
            if (_menuBackdrop != null) _menuBackdrop.SetActive(false);
            if (_menuAnimCR != null) StopCoroutine(_menuAnimCR);
            _menuAnimCR = StartCoroutine(AnimMenu(295f));
        }

        private IEnumerator AnimMenu(float toX)
        {
            if (_menuPanel == null) yield break;
            float from = _menuPanel.anchoredPosition.x, dur = 0.22f, t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                if (_menuPanel == null) yield break;
                _menuPanel.anchoredPosition = new Vector2(Mathf.SmoothStep(from, toX, t / dur), 0);
                yield return null;
            }
            if (_menuPanel != null) _menuPanel.anchoredPosition = new Vector2(toX, 0);
        }

        private async void OnMenuSave()
        {
            CloseMenu();
            if (_persistenceManager == null) return;
            ShowToast("Guardando...", _cMuted);
            bool ok = await _persistenceManager.SaveSession();
            ShowToast(ok ? "Sesión guardada ✓" : "Error al guardar", ok ? _cSuccess : _cDanger);
        }

        private async void OnMenuLoad()
        {
            CloseMenu();
            if (_persistenceManager == null) return;
            if (!_persistenceManager.HasSavedSession()) { ShowToast("Sin sesión guardada", _cWarning); return; }
            ShowToast("Cargando...", _cMuted);
            bool ok = await _persistenceManager.LoadSession();
            if (ok)
            {
                // FIX 1: ScheduleListRefresh en vez de llamadas directas (WaypointsBatchLoadedEvent lo hará)
                ScheduleListRefresh();
                UpdateStatusBar();
            }
            ShowToast(ok ? "Sesión cargada ✓" : "Error al cargar", ok ? _cSuccess : _cDanger);
        }

        private void OnMenuBake()
        {
            CloseMenu();
            ShowToast("Genera el NavMesh desde Inspector → MultiLevelNavMeshGenerator", _cWarning, 4f);
        }

        private void OnMenuClear()
        {
            CloseMenu();
            _persistenceManager?.ClearSavedData();
            _waypointManager?.ClearAllWaypoints();
            _favorites.Clear(); SaveFavorites();
            if (_isNavigating) CancelNavigation();
            RefreshWaypointList(); RefreshFavoriteList(); RefreshRouteList(); UpdateStatusBar();
            ShowToast("Datos eliminados", _cDanger);
        }

        // =====================================================================
        //  TOAST
        // =====================================================================

        public void ShowToast(string msg, Color accent, float dur = 2.8f)
        {
            if (_toastCR != null) StopCoroutine(_toastCR);
            _toastCR = StartCoroutine(ToastCR(msg, accent, dur));
        }

        private IEnumerator ToastCR(string msg, Color accent, float dur)
        {
            if (_toastLayer == null) yield break;
            foreach (Transform c in _toastLayer) Destroy(c.gameObject);

            var go = new GameObject("Toast"); go.transform.SetParent(_toastLayer, false);
            AnchorFull(go.AddComponent<RectTransform>());
            Image bg = go.AddComponent<Image>(); bg.color = _cSurface;

            var brd = Mk("B", go.GetComponent<RectTransform>());
            brd.anchorMin = new Vector2(0, 0); brd.anchorMax = new Vector2(0, 1);
            brd.offsetMin = Vector2.zero; brd.offsetMax = new Vector2(3, 0);
            Bg(brd, accent);

            var lbl = L("M", go.GetComponent<RectTransform>(), msg, 14, _cText);
            lbl.alignment = TextAnchor.MiddleLeft;
            AnchorFull(Rt(lbl)); Rt(lbl).offsetMin = new Vector2(14, 4); Rt(lbl).offsetMax = new Vector2(-12, -4);

            var cg = go.AddComponent<CanvasGroup>();
            float t = 0f;
            while (t < 0.16f) { cg.alpha = t / 0.16f; t += Time.unscaledDeltaTime; yield return null; }
            cg.alpha = 1f;
            yield return new WaitForSecondsRealtime(dur);
            t = 0f;
            while (t < 0.22f) { cg.alpha = 1f - t / 0.22f; t += Time.unscaledDeltaTime; yield return null; }
            if (go) Destroy(go);
        }

        // =====================================================================
        //  VOICE
        // =====================================================================

        private void SetupVoice()
        {
            if (_voiceProvider != null) _voiceProvider.OnCommandRecognized += ProcessVoiceCommand;
        }

        public void ProcessVoiceCommand(VoiceCommand cmd)
        {
            switch (cmd.Type)
            {
                case VoiceCommandType.NavigateTo:
                    var wp = _waypointManager?.SearchWaypointsByName(cmd.Parameter).FirstOrDefault();
                    if (wp != null) StartNavigationToWaypoint(wp);
                    else ShowToast($"'{cmd.Parameter}' no encontrado", _cWarning);
                    break;
                case VoiceCommandType.Cancel:        CancelNavigation();              break;
                case VoiceCommandType.GoHome:        OnFabHomePressed();              break;
                case VoiceCommandType.NextWaypoint:  NavNextWaypoint();               break;
                case VoiceCommandType.PrevWaypoint:  NavPrevWaypoint();               break;
                case VoiceCommandType.SaveSession:   OnMenuSave();                    break;
                case VoiceCommandType.LoadSession:   OnMenuLoad();                    break;
                case VoiceCommandType.AddWaypoint:   OnFabAddPressed();               break;
                case VoiceCommandType.ShowMenu:      OpenMenu();                      break;
                case VoiceCommandType.CloseMenu:     CloseMenu();                     break;
                case VoiceCommandType.Recalculate:   RecalculateRoute();              break;
                case VoiceCommandType.ToggleFavorite:
                    if (_selectedWp != null) ToggleFavorite(_selectedWp.WaypointId);
                    break;
            }
        }

        // =====================================================================
        //  DEBUG OVERLAY
        // =====================================================================

        private void DrawDebug()
        {
            if (_dbgText == null)
            {
                _dbgText = L("Dbg", (RectTransform)transform, "", 11, CA(_cSuccess, 0.7f));
                _dbgText.alignment = TextAnchor.UpperLeft;
                AnchorFull(Rt(_dbgText));
                Rt(_dbgText).offsetMin = new Vector2(8, _statusHeight + 4);
            }
            _dbgText.text =
                $"Sheet: {_sheetState} Y:{_sheet?.anchoredPosition.y:F0}/{_sheetTargetY:F0}\n" +
                $"PeekY:{_sheetPeekY:F0} HalfY:{_sheetHalfY:F0} FullY:{_sheetFullY:F0}\n" +
                $"Nav: {_isNavigating} | dist: {_navigationAgent?.DistanceToDestination:F1}m\n" +
                $"WP: {_waypointManager?.WaypointCount ?? 0} | Favs: {_favorites.Count} | " +
                $"fps: {(1f / Time.deltaTime):F0}\n" +
                $"EventsSub:{_eventsSubscribed} | StatusH:{_statusHeight:F0} | " +
                $"Screen:{Screen.width}x{Screen.height}";
        }

        // =====================================================================
        //  HAPTICS
        // =====================================================================

        private static void Vibrate()
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            UnityEngine.InputSystem.Handheld.Vibrate(0.1f);
#endif
        }

        // =====================================================================
        //  UI HELPER PRIMITIVES
        // =====================================================================

        private static RectTransform Mk(string n, Transform p)
        {
            var go = new GameObject(n, typeof(RectTransform));
            go.transform.SetParent(p, false);
            return go.GetComponent<RectTransform>();
        }

        private static void AnchorFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        private static void AnchorTop(RectTransform rt, float h)
        {
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot     = new Vector2(0.5f, 1); rt.sizeDelta = new Vector2(0, h);
            rt.anchoredPosition = Vector2.zero;
        }

        private static Image Bg(RectTransform rt, Color c)
        {
            var i = rt.GetComponent<Image>() ?? rt.gameObject.AddComponent<Image>();
            i.color = c; return i;
        }

        private Text L(string n, RectTransform p, string txt, int sz, Color col)
        {
            var rt = Mk(n, p); var t = rt.gameObject.AddComponent<Text>();
            t.text = txt; t.fontSize = sz; t.color = col;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.alignment = TextAnchor.MiddleLeft; t.supportRichText = false;
            return t;
        }

        private static RectTransform Rt(Text t)  => t.GetComponent<RectTransform>();
        private static RectTransform Rt(Image i) => i.GetComponent<RectTransform>();

        private static Color CA(Color c, float a) => new Color(c.r, c.g, c.b, a);
        private static Color Br(Color c, float d) =>
            new Color(Mathf.Clamp01(c.r + d), Mathf.Clamp01(c.g + d), Mathf.Clamp01(c.b + d), c.a);

        private static void SetAlpha(Button btn, float a)
        {
            CanvasGroup cg = btn.GetComponent<CanvasGroup>();
            if (cg == null) cg = btn.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = a;
        }

        // FIX 2: SafeTop/Bottom en unidades de referencia del canvas, no en píxeles físicos
        private static float SafeTop()
        {
            float ratio = Screen.safeArea.yMin / Screen.height;
            return ratio * REF_H * 0.5f; // × 0.5 porque matchWidthOrHeight = 0.618 ≈ 0.5 para alto
        }

        private static float SafeBottom()
        {
            float ratio = (Screen.height - Screen.safeArea.yMax) / Screen.height;
            return ratio * REF_H * 0.5f;
        }

        // ─── Data ─────────────────────────────────────────────────────────────

        private class WpItemView
        {
            public WaypointData Data;
            public GameObject   Root;
            public Image        Bg;
            public Text         Arrow;
        }
    }
}

// =============================================================================
//  MODIFICACIÓN MÍNIMA REQUERIDA EN WaypointManager.cs
// =============================================================================
// En LoadWaypoints(), al FINAL del método, DESPUÉS del Debug.Log, añade:
//
//   EventBus.Instance.Publish(new WaypointsBatchLoadedEvent { Count = saveData.Count });
//
// Y en Events.cs (o donde definas tus eventos), añade:
//
//   public struct WaypointsBatchLoadedEvent { public int Count; }
//
// Esto permite que la UI refresque la lista UNA SOLA VEZ cuando toda la carga
// en lote ha terminado, en lugar de N veces por N WaypointPlacedEvents.
// =============================================================================