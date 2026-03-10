// File: ARPathVisualizer.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Navigation/AR/
//
// ============================================================================
//  PROPÓSITO
// ============================================================================
//
//  Dibuja una línea guía estilo Google Maps AR sobre la cámara real.
//  Diseñado para el demo de tesis: los jurados ven la línea AR sobre el
//  pasillo real mientras el usuario (ciego) recibe instrucciones auditivas.
//
//  PIPELINE:
//    NavigationPathController.CurrentPath.Waypoints
//         ↓  (world space, ya alineados por AROriginAligner)
//    ARPathVisualizer
//         ↓
//    LineRenderer 3D   → línea continua en el suelo
//    Flechas (Quads)   → en cada waypoint de giro
//    Quad de destino   → marcador pulsante al llegar
//
//  REQUISITOS:
//    - Un Material Unlit con ZWrite Off asignado en _lineMaterial
//    - NavigationPathController en escena
//    - NavigationVoiceGuide en escena (para leer los eventos de giro)
//    - UserPositionBridge en escena (para recortar la línea desde el usuario)
//
// ============================================================================
//  MODO SIN MODELO (demo en edificio real)
// ============================================================================
//
//  El modelo 3D puede estar cargado pero invisible (todos los Renderers off).
//  El NavMesh existe en memoria (cargado desde navmesh_unified.bin por
//  NavMeshAgentCoordinator en _renderOnlyMode=true).
//  AROriginAligner sincroniza el origen del NavMesh al XROrigin → los
//  waypoints de CurrentPath están en el mismo world space que la cámara AR.
//  Este componente simplemente lee esos waypoints y los renderiza.
//
// ============================================================================
//  INSTRUCCIONES DE USO
// ============================================================================
//
//  1. Crear un GameObject vacío "ARPathVisualizer" en la escena.
//  2. Agregar este componente.
//  3. Asignar en Inspector:
//       _lineMaterial  → Material Unlit (ver sección MATERIAL más abajo)
//       _arrowMaterial → mismo material u otro con flecha en texture
//  4. Ajustar colores y grosor al gusto.
//  5. El componente se activa/desactiva automáticamente al escuchar
//     NavigationStartedEvent / NavigationCompletedEvent / NavigationCancelledEvent.
//
// ============================================================================
//  MATERIAL RECOMENDADO (crear en Unity)
// ============================================================================
//
//  Nombre: "ARPathLine"
//  Shader: Universal Render Pipeline/Unlit   (o "Unlit/Color" en Built-in)
//  Surface Type: Transparent
//  Blend Mode: Alpha
//  ZWrite: Off
//  Render Face: Both
//  Color: #0088FF con Alpha ~200/255
//
//  Para el efecto de "textura que avanza" (animación de flechas en la línea):
//  Shader: Universal Render Pipeline/Lit  con emisión, o shader custom.
//  (Opcional — la línea funciona con color sólido para el demo.)

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Navigation;
using IndoorNavAR.Navigation.Voice;
using IndoorNavAR.Core;

namespace IndoorNavAR.AR
{
    [RequireComponent(typeof(LineRenderer))]
    public sealed class ARPathVisualizer : MonoBehaviour
    {
        // ─────────────────────────────────────────────────────────────────────
        //  INSPECTOR
        // ─────────────────────────────────────────────────────────────────────

        [Header("─── Referencias ──────────────────────────────────────────────")]
        [SerializeField] private NavigationPathController _pathController;
        [SerializeField] private NavigationVoiceGuide     _voiceGuide;
        [SerializeField] private UserPositionBridge       _userBridge;

        [Header("─── Materiales ──────────────────────────────────────────────")]
        [Tooltip("Material Unlit/Transparent. Shader: URP Unlit o Built-in Unlit/Color. " +
                 "ZWrite Off, Blend SrcAlpha OneMinusSrcAlpha.")]
        [SerializeField] private Material _lineMaterial;

        [Tooltip("Material para las flechas direccionales (puede ser el mismo con textura de flecha).")]
        [SerializeField] private Material _arrowMaterial;

        [Header("─── Línea ───────────────────────────────────────────────────")]
        [Tooltip("Color principal de la línea guía.")]
        [SerializeField] private Color _lineColor       = new(0.0f, 0.53f, 1.0f, 0.85f);

        [Tooltip("Color del segmento ya recorrido (parte detrás del usuario). " +
                 "Se pone semitransparente para énfasis en lo que falta.")]
        [SerializeField] private Color _lineColorPassed = new(0.0f, 0.53f, 1.0f, 0.25f);

        [Tooltip("Grosor de la línea en metros. 0.12 = 12cm, visible en pasillo sin ser intrusivo.")]
        [SerializeField, Range(0.05f, 1.0f)]
        private float _lineWidth = 0.45f;

        [Tooltip("Altura sobre el suelo NavMesh (metros). " +
                 "0.04m = 4cm, flota sobre el suelo sin z-fighting.")]
        [SerializeField, Range(0.01f, 0.15f)]
        private float _lineYOffset = 0.08f;

        [Tooltip("Número de puntos interpolados entre cada par de waypoints originales. " +
                 "Mayor = línea más suave en curvas. 0 = sin interpolación (waypoints crudos).")]
        [SerializeField, Range(0, 8)]
        private int _smoothingSteps = 3;

        [Header("─── Flechas ──────────────────────────────────────────────────")]
        [Tooltip("Mostrar quads de flecha en los waypoints de giro.")]
        [SerializeField] private bool _showArrows = true;

        [Tooltip("Tamaño del quad de flecha en metros.")]
        [SerializeField, Range(0.1f, 0.6f)]
        private float _arrowSize = 0.25f;

        [Tooltip("Altura de las flechas sobre el suelo (metros).")]
        [SerializeField, Range(0.02f, 0.2f)]
        private float _arrowYOffset = 0.06f;

        [Tooltip("Color de las flechas direccionales.")]
        [SerializeField] private Color _arrowColor = new(1.0f, 0.85f, 0.0f, 0.9f);

        [Header("─── Marcador de destino ─────────────────────────────────────")]
        [Tooltip("Mostrar un quad pulsante en el destino final.")]
        [SerializeField] private bool  _showDestinationMarker = true;

        [Tooltip("Radio del marcador de destino en metros.")]
        [SerializeField, Range(0.1f, 1.0f)]
        private float _destMarkerRadius = 0.4f;

        [Tooltip("Velocidad del pulso del marcador de destino.")]
        [SerializeField, Range(0.5f, 4.0f)]
        private float _destPulseSpeed = 1.8f;

        [SerializeField] private Color _destColor = new(0.0f, 1.0f, 0.4f, 0.8f);

        [Header("─── Animación de flujo ──────────────────────────────────────")]
        [Tooltip("Animar la textura UV de la línea para dar sensación de movimiento hacia adelante.")]
        [SerializeField] private bool  _animateFlow = true;

        [Tooltip("Velocidad del desplazamiento UV (unidades/segundo). " +
                 "Positivo = fluye hacia adelante (hacia el destino).")]
        [SerializeField, Range(0.1f, 3.0f)]
        private float _flowSpeed = 0.8f;

        [Header("─── Actualización ───────────────────────────────────────────")]
        [Tooltip("Intervalo de re-muestreo de la ruta (segundos). " +
                 "0.1s = 10 veces/seg, suficiente para línea fluida sin overhead.")]
        [SerializeField, Range(0.05f, 0.5f)]
        private float _updateInterval = 0.1f;

        [Tooltip("El segmento de línea antes del usuario se recorta en tiempo real, " +
                 "proyectando la posición del usuario sobre la polilínea.")]
        [SerializeField] private bool _trimBehindUser = true;

        [Header("─── Debug ────────────────────────────────────────────────────")]
        [SerializeField] private bool _logEvents = true;

        // ─────────────────────────────────────────────────────────────────────
        //  COMPONENTES PRIVADOS
        // ─────────────────────────────────────────────────────────────────────

        private LineRenderer _lineRenderer;

        // Pool de flechas (Quads) — se reutilizan entre updates
        private readonly List<GameObject> _arrowPool   = new(8);
        private          GameObject       _destMarker;
        private          GameObject       _destRing;

        // Estado
        private bool    _isActive       = false;
        private float   _updateAccum    = 0f;
        private float   _flowOffset     = 0f;
        private Vector3 _destPos        = Vector3.zero;

        // Cache de puntos suavizados (evita alloc cada frame)
        private readonly List<Vector3> _smoothedPoints = new(64);
        private readonly List<Vector3> _trimmedPoints  = new(64);

        // ─────────────────────────────────────────────────────────────────────
        //  LIFECYCLE
        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            _lineRenderer = GetComponent<LineRenderer>();
            ConfigureLineRenderer();
            SetupDestinationMarker();

            // Ocultar hasta que empiece la navegación
            SetVisible(false);
        }

        private void Start()
        {
            // Auto-buscar referencias si no están asignadas
            if (_pathController == null)
                _pathController = FindFirstObjectByType<NavigationPathController>(FindObjectsInactive.Include);
            if (_voiceGuide == null)
                _voiceGuide = FindFirstObjectByType<NavigationVoiceGuide>(FindObjectsInactive.Include);
            if (_userBridge == null)
                _userBridge = FindFirstObjectByType<UserPositionBridge>(FindObjectsInactive.Include);

            if (_pathController == null)
                Debug.LogError("[ARPathVisualizer] ❌ NavigationPathController no encontrado.");
        }

        private void OnEnable()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Subscribe<NavigationStartedEvent>  (OnNavStarted);
            bus.Subscribe<NavigationCompletedEvent>(OnNavCompleted);
            bus.Subscribe<NavigationCancelledEvent>(OnNavCancelled);
        }

        private void OnDisable()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Unsubscribe<NavigationStartedEvent>  (OnNavStarted);
            bus.Unsubscribe<NavigationCompletedEvent>(OnNavCompleted);
            bus.Unsubscribe<NavigationCancelledEvent>(OnNavCancelled);
        }

        private void Update()
        {
            if (!_isActive) return;

            // ── Animación de flujo UV ──────────────────────────────────────
            if (_animateFlow && _lineRenderer.positionCount > 0)
            {
                _flowOffset -= _flowSpeed * Time.deltaTime;
                _lineRenderer.material.SetTextureOffset("_MainTex",
                    new Vector2(_flowOffset, 0f));
            }

            // ── Pulso del marcador de destino ──────────────────────────────
            if (_showDestinationMarker && _destMarker != null && _destMarker.activeSelf)
            {
                float pulse = 0.7f + 0.3f * Mathf.Sin(Time.time * _destPulseSpeed * Mathf.PI);
                _destMarker.transform.localScale = Vector3.one * (_destMarkerRadius * 2f * pulse);

                // Pulso de alpha
                if (_destMarker.TryGetComponent<Renderer>(out var r))
                {
                    Color c = _destColor;
                    c.a = _destColor.a * pulse;
                    r.material.color = c;
                }

                // Ring exterior más lento (fase invertida para efecto "respiración")
                if (_destRing != null)
                {
                    float ringPulse = 0.5f + 0.5f * Mathf.Sin(Time.time * _destPulseSpeed * Mathf.PI * 0.6f);
                    _destRing.transform.localScale = Vector3.one * (_destMarkerRadius * 2.8f * ringPulse);
                    if (_destRing.TryGetComponent<Renderer>(out var rr))
                    {
                        Color rc = _destColor;
                        rc.a = _destColor.a * 0.4f * ringPulse;
                        rr.material.color = rc;
                    }
                }
            }

            // ── Re-muestreo de la ruta ─────────────────────────────────────
            _updateAccum += Time.deltaTime;
            if (_updateAccum >= _updateInterval)
            {
                _updateAccum = 0f;
                RefreshPath();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVENTOS DEL BUS
        // ─────────────────────────────────────────────────────────────────────

        private void OnNavStarted(NavigationStartedEvent evt)
        {
            _destPos = evt.DestinationPosition;
            SetVisible(true);
            _isActive = true;
            _flowOffset = 0f;

            if (_showDestinationMarker && _destMarker != null)
            {
                _destMarker.transform.position =
                    SnapToNavMeshSurface(_destPos) + Vector3.up * _arrowYOffset;
                _destMarker.SetActive(true);
                if (_destRing != null) _destRing.SetActive(true);
            }

            RefreshPath();

            if (_logEvents)
                Debug.Log($"[ARPathVisualizer] ▶️ Iniciando visualización → {evt.DestinationWaypointId}");
        }

        private void OnNavCompleted(NavigationCompletedEvent _)
        {
            // Breve delay visual antes de ocultar (el usuario oye "llegaste")
            Invoke(nameof(HideWithDelay), 2.5f);

            if (_logEvents)
                Debug.Log("[ARPathVisualizer] 🏁 Navegación completada — ocultando en 2.5s.");
        }

        private void OnNavCancelled(NavigationCancelledEvent _) => HideImmediate();

        // ─────────────────────────────────────────────────────────────────────
        //  LÓGICA DE ACTUALIZACIÓN DE LA LÍNEA
        // ─────────────────────────────────────────────────────────────────────

        private void RefreshPath()
        {
            var path = _pathController?.CurrentPath;
            if (path == null || !path.IsValid || path.Waypoints.Count < 2)
            {
                _lineRenderer.positionCount = 0;
                HideAllArrows();
                return;
            }

            var waypoints = path.Waypoints;

            // ── 1. Suavizar la polilínea ───────────────────────────────────
            BuildSmoothedPoints(waypoints, _smoothedPoints);

            // ── 2. Offset de altura (proyectar al suelo NavMesh + yOffset) ─
            for (int i = 0; i < _smoothedPoints.Count; i++)
            {
                Vector3 p = _smoothedPoints[i];
                p = SnapToNavMeshSurface(p);
                p.y += _lineYOffset;
                _smoothedPoints[i] = p;
            }

            // ── 3. Recortar la parte ya recorrida ─────────────────────────
            Vector3 userPos = GetUserPos();

            if (_trimBehindUser && _smoothedPoints.Count >= 2)
            {
                TrimBehindUser(userPos, _smoothedPoints, _trimmedPoints);
            }
            else
            {
                _trimmedPoints.Clear();
                _trimmedPoints.AddRange(_smoothedPoints);
            }

            // ── 4. Aplicar al LineRenderer ─────────────────────────────────
            if (_trimmedPoints.Count < 2)
            {
                _lineRenderer.positionCount = 0;
                HideAllArrows();
                return;
            }

            _lineRenderer.positionCount = _trimmedPoints.Count;
            for (int i = 0; i < _trimmedPoints.Count; i++)
                _lineRenderer.SetPosition(i, _trimmedPoints[i]);

            // ── 5. Actualizar flechas en waypoints de giro ─────────────────
            if (_showArrows)
                UpdateArrows(waypoints);

            // ── 6. Posicionar marcador de destino ──────────────────────────
            if (_showDestinationMarker && _destMarker != null && _destMarker.activeSelf)
            {
                Vector3 last = waypoints[waypoints.Count - 1];
                Vector3 destSnapped = SnapToNavMeshSurface(last) + Vector3.up * _arrowYOffset;
                _destMarker.transform.position = destSnapped;
                if (_destRing != null) _destRing.transform.position = destSnapped;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SUAVIZADO DE LA RUTA (Catmull-Rom simplificado)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Interpola puntos intermedios entre cada par de waypoints consecutivos.
        /// Usa interpolación lineal simple (suficiente para pasillos rectos con
        /// giros angulares). Para curvas más fluidas usar Catmull-Rom.
        /// </summary>
        private void BuildSmoothedPoints(IReadOnlyList<Vector3> wp, List<Vector3> result)
        {
            result.Clear();
            if (wp.Count == 0) return;

            result.Add(wp[0]);

            for (int i = 0; i < wp.Count - 1; i++)
            {
                Vector3 a = wp[i];
                Vector3 b = wp[i + 1];

                if (_smoothingSteps > 0)
                {
                    for (int s = 1; s <= _smoothingSteps; s++)
                    {
                        float t = s / (float)(_smoothingSteps + 1);
                        result.Add(Vector3.Lerp(a, b, t));
                    }
                }
                result.Add(b);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RECORTE DE LÍNEA DETRÁS DEL USUARIO
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Proyecta la posición del usuario sobre la polilínea suavizada y
        /// construye un nuevo array que empieza desde esa proyección.
        /// Esto hace que la línea siempre "salga de los pies del usuario"
        /// hacia adelante, eliminando la parte ya recorrida.
        ///
        /// Resultado visual: idéntico a Google Maps AR Navigation.
        /// </summary>
        private static void TrimBehindUser(
            Vector3        userPos,
            List<Vector3>  points,
            List<Vector3>  result)
        {
            result.Clear();

            // Buscar el segmento más cercano al usuario en XZ
            int   closestSeg   = 0;
            float closestDistSq = float.MaxValue;
            float closestT      = 0f;

            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector3 a  = points[i];
                Vector3 b  = points[i + 1];
                Vector3 ab = b - a;

                float lenSq = ab.x * ab.x + ab.z * ab.z; // XZ only
                float t     = 0f;

                if (lenSq > 0.0001f)
                {
                    Vector3 ap = userPos - a;
                    t = Mathf.Clamp01((ap.x * ab.x + ap.z * ab.z) / lenSq);
                }

                Vector3 proj = a + t * ab;
                float   dx   = userPos.x - proj.x;
                float   dz   = userPos.z - proj.z;
                float   dSq  = dx * dx + dz * dz;

                if (dSq < closestDistSq)
                {
                    closestDistSq = dSq;
                    closestSeg    = i;
                    closestT      = t;
                }
            }

            // Punto de inicio de la línea = proyección del usuario en el segmento
            Vector3 startPt = Vector3.Lerp(points[closestSeg], points[closestSeg + 1], closestT);

            // Preservar altura de la línea (el Y ya viene suavizado)
            startPt.y = Mathf.Lerp(points[closestSeg].y, points[closestSeg + 1].y, closestT);

            result.Add(startPt);

            // Añadir todos los puntos posteriores al punto de proyección
            for (int i = closestSeg + 1; i < points.Count; i++)
                result.Add(points[i]);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  FLECHAS DIRECCIONALES
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Coloca flechas (Quads planos) en los waypoints de giro.
        /// Lee los NavigationInstructionEvents de NavigationVoiceGuide para
        /// saber qué waypoints son giros y en qué dirección.
        ///
        /// Las flechas rotan para apuntar en la dirección del giro (dirOut).
        /// Se miran hacia arriba (face up) para verse proyectadas en el suelo.
        /// </summary>
        private void UpdateArrows(IReadOnlyList<Vector3> waypoints)
        {
            // Recolectar posiciones de giro y sus direcciones
            var turnData = new List<(Vector3 pos, Vector3 dir)>(4);

            if (_voiceGuide != null)
            {
                var events = _voiceGuide.InstructionEvents;
                for (int i = 0; i < events.Count; i++)
                {
                    var evt = events[i];
                    bool isTurn =
                        evt.Type == VoiceInstructionType.TurnLeft    ||
                        evt.Type == VoiceInstructionType.TurnRight   ||
                        evt.Type == VoiceInstructionType.SlightLeft  ||
                        evt.Type == VoiceInstructionType.SlightRight ||
                        evt.Type == VoiceInstructionType.UTurn;

                    if (!isTurn || evt.HasFired) continue;

                    // Dirección del giro: segmento de salida desde este waypoint
                    int wpIdx = evt.CornerIndex;
                    if (wpIdx < 0 || wpIdx + 1 >= waypoints.Count) continue;

                    Vector3 outDir = waypoints[wpIdx + 1] - waypoints[wpIdx];
                    outDir.y = 0f;
                    if (outDir.sqrMagnitude < 0.001f) continue;

                    Vector3 pos = SnapToNavMeshSurface(evt.WorldPosition) + Vector3.up * _arrowYOffset;
                    turnData.Add((pos, outDir.normalized));
                }
            }

            // Redimensionar pool si necesario
            while (_arrowPool.Count < turnData.Count)
                _arrowPool.Add(CreateArrowQuad());

            // Posicionar flechas activas
            for (int i = 0; i < turnData.Count; i++)
            {
                var arrow = _arrowPool[i];
                arrow.SetActive(true);
                arrow.transform.position = turnData[i].pos;

                // Rotar para apuntar en la dirección del giro (en plano XZ, tumbado hacia arriba)
                if (turnData[i].dir != Vector3.zero)
                    arrow.transform.rotation = Quaternion.LookRotation(
                        turnData[i].dir, Vector3.up) *
                        Quaternion.Euler(90f, 0f, 0f);
            }

            // Ocultar flechas sobrantes del pool
            for (int i = turnData.Count; i < _arrowPool.Count; i++)
                _arrowPool[i].SetActive(false);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SNAP AL SUELO (NavMesh)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Proyecta un punto al NavMesh más cercano para que la línea quede
        /// pegada al suelo real aunque los waypoints tengan Y inexacto.
        ///
        /// Fallback: si NavMesh.SamplePosition falla, devuelve el punto original
        /// (la línea flotará al _lineYOffset pero no se romperá).
        /// </summary>
        private static Vector3 SnapToNavMeshSurface(Vector3 point)
        {
            if (NavMesh.SamplePosition(point, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
                return hit.position;
            return point;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SETUP DE COMPONENTES
        // ─────────────────────────────────────────────────────────────────────

        private void ConfigureLineRenderer()
        {
            _lineRenderer.useWorldSpace       = true;
            _lineRenderer.loop                = false;
            _lineRenderer.numCapVertices      = 4;
            _lineRenderer.numCornerVertices   = 6;
            _lineRenderer.alignment           = LineAlignment.TransformZ;
            _lineRenderer.textureMode         = LineTextureMode.Tile;
            _lineRenderer.generateLightingData = false;
            _lineRenderer.receiveShadows      = false;
            _lineRenderer.shadowCastingMode   = UnityEngine.Rendering.ShadowCastingMode.Off;

            _lineRenderer.startWidth = _lineWidth;
            _lineRenderer.endWidth   = _lineWidth;

            // Gradiente de color: inicio azul opaco → final azul más transparente
            // (énfasis visual en lo que el usuario tiene justo adelante)
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] {
                    new GradientColorKey(_lineColor,       0.0f),
                    new GradientColorKey(_lineColor,       0.5f),
                    new GradientColorKey(_lineColor * 0.8f, 1.0f),
                },
                new[] {
                    new GradientAlphaKey(_lineColor.a,        0.0f),
                    new GradientAlphaKey(_lineColor.a,        0.7f),
                    new GradientAlphaKey(_lineColor.a * 0.5f, 1.0f),
                });
            _lineRenderer.colorGradient = gradient;

            if (_lineMaterial != null)
                _lineRenderer.material = _lineMaterial;
            else
            {
                // Fallback: material Unlit generado en runtime
                // Funciona pero sin transparencia ni animación UV
                _lineRenderer.material = new Material(Shader.Find("Unlit/Color"))
                {
                    color = _lineColor
                };
                Debug.LogWarning("[ARPathVisualizer] ⚠️ No se asignó _lineMaterial. " +
                                 "Usando Unlit/Color como fallback (sin transparencia).");
            }
        }

        private void SetupDestinationMarker()
        {
            if (!_showDestinationMarker) return;

            // Disco central pulsante
            _destMarker = CreateFlatQuad("DestMarker",
                _destMarkerRadius * 2f,
                _arrowMaterial ?? CreateFallbackMaterial(_destColor),
                _destColor);

            // Ring exterior (efecto "sonar")
            _destRing = CreateFlatQuad("DestRing",
                _destMarkerRadius * 2.8f,
                _arrowMaterial ?? CreateFallbackMaterial(_destColor),
                new Color(_destColor.r, _destColor.g, _destColor.b, _destColor.a * 0.35f));

            _destMarker.SetActive(false);
            _destRing.SetActive(false);
        }

        private GameObject CreateArrowQuad()
        {
            Material mat = _arrowMaterial ?? CreateFallbackMaterial(_arrowColor);
            return CreateFlatQuad($"Arrow_{_arrowPool.Count}", _arrowSize, mat, _arrowColor);
        }

        /// <summary>
        /// Crea un GameObject con un Quad (Plane rotado) plano sobre el suelo.
        /// Face-up: normal apuntando hacia +Y, visible desde arriba (cámara AR).
        ///
        /// NOTA: Unity's built-in Quad tiene normal apuntando a +Z.
        /// Para que se vea desde arriba, se rota 90° en X.
        /// El Renderer tiene CullMode=Off para que se vea desde ambos lados.
        /// </summary>
        private GameObject CreateFlatQuad(string name, float size, Material mat, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = $"[ARPathViz] {name}";
            go.transform.SetParent(transform);
            go.transform.localScale = Vector3.one * size;

            // Rotar para que quede tumbado (normal hacia arriba)
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            // Quitar collider (no necesario)
            if (go.TryGetComponent<Collider>(out var col))
                Destroy(col);

            var rend = go.GetComponent<Renderer>();
            rend.receiveShadows      = false;
            rend.shadowCastingMode   = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Instanciar material para poder cambiar color individualmente
            var instMat = new Material(mat) { color = color };
            rend.material = instMat;

            go.SetActive(false);
            return go;
        }

        private static Material CreateFallbackMaterial(Color color)
        {
            var mat = new Material(Shader.Find("Unlit/Color") ?? Shader.Find("Universal Render Pipeline/Unlit"))
                ?? new Material(Shader.Find("Hidden/InternalErrorShader"));
            mat.color = color;
            return mat;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  VISIBILIDAD
        // ─────────────────────────────────────────────────────────────────────

        private void SetVisible(bool visible)
        {
            _lineRenderer.enabled = visible;
            if (!visible) HideAllArrows();
        }

        private void HideAllArrows()
        {
            foreach (var a in _arrowPool) a.SetActive(false);
        }

        private void HideWithDelay()
        {
            _isActive = false;
            SetVisible(false);
            _lineRenderer.positionCount = 0;
            if (_destMarker != null) _destMarker.SetActive(false);
            if (_destRing   != null) _destRing.SetActive(false);
        }

        private void HideImmediate()
        {
            CancelInvoke(nameof(HideWithDelay));
            HideWithDelay();

            if (_logEvents)
                Debug.Log("[ARPathVisualizer] ⏹️ Visualización cancelada.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────────────────

        private Vector3 GetUserPos()
        {
            if (_userBridge != null) return _userBridge.UserPosition;
            if (Camera.main != null) return Camera.main.transform.position;
            return Vector3.zero;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GIZMOS (Editor — visualizar el offset de la línea)
        // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;
            if (_trimmedPoints.Count < 2) return;

            // Dibujar la línea suavizada en el Editor
            Gizmos.color = _lineColor;
            for (int i = 0; i < _trimmedPoints.Count - 1; i++)
                Gizmos.DrawLine(_trimmedPoints[i], _trimmedPoints[i + 1]);

            // Punto de inicio (donde empieza la línea desde el usuario)
            Gizmos.color = Color.white;
            Gizmos.DrawSphere(_trimmedPoints[0], 0.05f);
        }
#endif

        // ─────────────────────────────────────────────────────────────────────
        //  CONTEXT MENU (Debug en runtime)
        // ─────────────────────────────────────────────────────────────────────

        [ContextMenu("🔄 Refresh Path (debug)")]
        private void DebugRefresh() => RefreshPath();

        [ContextMenu("👁 Toggle Visible")]
        private void DebugToggle()
        {
            _isActive = !_isActive;
            SetVisible(_isActive);
        }

        [ContextMenu("ℹ️ Status")]
        private void DebugStatus()
        {
            var path = _pathController?.CurrentPath;
            Debug.Log(
                $"[ARPathVisualizer] Active={_isActive} | " +
                $"LinePoints={_lineRenderer.positionCount} | " +
                $"SmoothedPts={_smoothedPoints.Count} | " +
                $"TrimmedPts={_trimmedPoints.Count}\n" +
                $"Path valid={path?.IsValid} | Waypoints={path?.Waypoints.Count}\n" +
                $"Arrows in pool={_arrowPool.Count} | " +
                $"Arrows active={_arrowPool.FindAll(a => a.activeSelf).Count}\n" +
                $"UserPos={GetUserPos():F2} | DestPos={_destPos:F2}");
        }

        [ContextMenu("🎨 Apply Color Changes")]
        private void DebugApplyColors()
        {
            ConfigureLineRenderer();
            foreach (var arrow in _arrowPool)
                if (arrow.TryGetComponent<Renderer>(out var r))
                    r.material.color = _arrowColor;
            if (_destMarker != null && _destMarker.TryGetComponent<Renderer>(out var dm))
                dm.material.color = _destColor;
        }
    }
}