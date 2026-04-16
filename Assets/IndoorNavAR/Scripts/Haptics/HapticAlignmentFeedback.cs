// File: HapticAlignmentFeedback.cs
// ✅ v1.0 — Retroalimentación háptica progresiva cuando el usuario mira
//           en la dirección correcta estando detenido o a baja velocidad.
//
// ════════════════════════════════════════════════════════════════════════════
// PROPÓSITO
// ════════════════════════════════════════════════════════════════════════════
//
//  Mejora de seguridad para usuarios con discapacidad visual:
//  El dispositivo vibra con una secuencia progresiva (suave → fuerte → pulso
//  de confirmación) cuando el usuario:
//    1. Está parado o caminando muy despacio (< _minSpeedToEvaluate)
//    2. Tiene el teléfono apuntando hacia el próximo waypoint (< _alignAngleFull)
//
//  PATRÓN DE PULSOS (3 niveles de proximidad angular):
//    • Zona exterior  (_alignAngleFull → _alignAnglePartial):
//        Pulso corto cada _pulseCooldownPartial segundos. "Estás cerca."
//    • Zona media     (< _alignAnglePartial, > _alignAngleFull):
//        Pulso medio cada _pulseCooldownMedium segundos. "Vas bien."
//    • Zona central   (< _alignAngleFull):
//        Doble pulso de confirmación, luego silencio por _confirmCooldown.
//        El usuario sabe que está perfectamente alineado — puede avanzar.
//
//  PROTECCIÓN ANTI-SPAM:
//    • Después del pulso de confirmación, _confirmCooldown (default 4s)
//      bloquea nuevas vibraciones aunque el usuario gire y vuelva.
//    • Si el usuario empieza a caminar (speed > _minSpeedToEvaluate),
//      el sistema se inhibe completamente (no interferir con la marcha).
//    • _globalCooldown (default 0.8s) impide que pulsos consecutivos
//      se solapen aunque el ángulo oscile en el límite de zona.
//
// ════════════════════════════════════════════════════════════════════════════
// INTEGRACIÓN CON EL SISTEMA EXISTENTE
// ════════════════════════════════════════════════════════════════════════════
//
//  Lee de NavigationVoiceGuide:
//    • _nextIdx  → índice del próximo evento, para obtener su WorldPosition
//    • IsGuiding → solo activo si hay navegación en curso
//
//  Lee de UserPositionBridge:
//    • UserForward  → dirección de la cámara proyectada en XZ
//    • UserSpeed    → velocidad suavizada (EMA, anti-spike)
//    • UserPosition → posición actual para calcular distancia al waypoint
//
//  Suscripciones al EventBus:
//    • NavigationStartedEvent   → activar sistema
//    • NavigationCompletedEvent → desactivar y resetear
//    • NavigationCancelledEvent → desactivar y resetear
//    • FloorTransitionEvent     → resetear cooldowns (nuevo piso = nueva orientación)
//
//  NO modifica ningún archivo existente — se añade como componente independiente
//  en el mismo GameObject que NavigationVoiceGuide o en uno propio.
//
// ════════════════════════════════════════════════════════════════════════════
// CONFIGURACIÓN ANDROID
// ════════════════════════════════════════════════════════════════════════════
//
//  Requiere permiso VIBRATE en AndroidManifest.xml:
//    <uses-permission android:name="android.permission.VIBRATE" />
//
//  En Unity Player Settings → Android → Vibration debe estar habilitado.
//  En iOS el sistema usa UIFeedbackGenerator (Taptic Engine) — no necesita
//  permiso explícito pero requiere iPhone 7 o superior.

using System.Collections;
using UnityEngine;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Navigation.Voice;

#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace IndoorNavAR.Navigation.Haptics
{
    /// <summary>
    /// Niveles de alineación del usuario con respecto al próximo waypoint.
    /// </summary>
    public enum AlignmentZone
    {
        /// Fuera de cualquier zona de guía háptica.
        None,
        /// Ángulo entre _alignAnglePartial y _alignAngleFull — vibración suave.
        Partial,
        /// Ángulo entre _alignAngleFull y _alignAngleCenter — vibración media.
        Medium,
        /// Dentro de _alignAngleCenter — pulso de confirmación doble.
        Full,
    }

    public sealed class HapticAlignmentFeedback : MonoBehaviour
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Inspector
        // ─────────────────────────────────────────────────────────────────────

        [Header("─── Referencias ─────────────────────────────────────────────")]
        [Tooltip("Se auto-detecta si queda vacío.")]
        [SerializeField] private UserPositionBridge _userBridge;

        [Tooltip("Se auto-detecta si queda vacío.")]
        [SerializeField] private NavigationVoiceGuide _voiceGuide;

        [Header("─── Velocidad de activación ─────────────────────────────────")]
        [Tooltip("Velocidad máxima (m/s) para que el sistema evalúe la orientación.\n" +
                 "Si el usuario camina más rápido, la háptica se inhibe.\n" +
                 "Default 0.4 m/s — caminar lento sigue activo, trotar no.")]
        [SerializeField, Range(0f, 1.5f)]
        private float _minSpeedToEvaluate = 10f;

        [Tooltip("Tiempo que el usuario debe estar quieto/lento antes de\n" +
                 "activar la evaluación de orientación. Evita activaciones\n" +
                 "al decelerar entre pasos.\n" +
                 "Default: 0.6s.")]
        [SerializeField, Range(0.1f, 3f)]
        private float _stillnessConfirmTime = 0.6f;

        [Header("─── Ángulos de alineación ──────────────────────────────────")]
        [Tooltip("Ángulo externo (grados): inicio de la zona Partial.\n" +
                 "Fuera de este ángulo no hay vibración.\n" +
                 "Default: 45°.")]
        [SerializeField, Range(20f, 90f)]
        private float _alignAnglePartial = 45f;

        [Tooltip("Ángulo medio (grados): inicio de la zona Medium.\n" +
                 "Default: 20°.")]
        [SerializeField, Range(5f, 45f)]
        private float _alignAngleMedium = 20f;

        [Tooltip("Ángulo central (grados): inicio de la zona Full (confirmación).\n" +
                 "Default: 8°.")]
        [SerializeField, Range(1f, 20f)]
        private float _alignAngleFull = 8f;

        [Header("─── Duración de pulsos (ms) ─────────────────────────────────")]
        [Tooltip("Duración del pulso corto — zona Partial.\n" +
                 "Default: 30ms.")]
        [SerializeField, Range(10, 200)]
        private int _pulseShortMs = 30;

        [Tooltip("Duración del pulso medio — zona Medium.\n" +
                 "Default: 60ms.")]
        [SerializeField, Range(20, 300)]
        private int _pulseMediumMs = 60;

        [Tooltip("Duración del primer pulso del doble de confirmación — zona Full.\n" +
                 "Default: 80ms.")]
        [SerializeField, Range(30, 400)]
        private int _pulseConfirmAMs = 80;

        [Tooltip("Duración del segundo pulso del doble de confirmación.\n" +
                 "Default: 120ms (más fuerte para señal inequívoca).")]
        [SerializeField, Range(30, 400)]
        private int _pulseConfirmBMs = 120;

        [Tooltip("Pausa entre el primer y segundo pulso del doble de confirmación.\n" +
                 "Default: 80ms.")]
        [SerializeField, Range(20, 300)]
        private int _pulseConfirmGapMs = 80;

        [Header("─── Cooldowns (anti-spam) ──────────────────────────────────")]
        [Tooltip("Cooldown entre pulsos en zona Partial (s).\n" +
                 "Default: 2.5s — ritmo lento que no desorienta.")]
        [SerializeField, Range(0.5f, 10f)]
        private float _pulseCooldownPartial = 2.5f;

        [Tooltip("Cooldown entre pulsos en zona Medium (s).\n" +
                 "Default: 1.5s — ritmo más frecuente para guiar activamente.")]
        [SerializeField, Range(0.3f, 8f)]
        private float _pulseCooldownMedium = 1.5f;

        [Tooltip("Cooldown GLOBAL entre cualquier pulso (s).\n" +
                 "Evita solapamiento si el ángulo oscila entre zonas.\n" +
                 "Default: 0.8s.")]
        [SerializeField, Range(0.1f, 3f)]
        private float _globalCooldown = 0.8f;

        [Tooltip("Tiempo de silencio después del pulso de confirmación (s).\n" +
                 "El usuario ya sabe que está alineado — dejar que procese.\n" +
                 "Default: 4s.")]
        [SerializeField, Range(1f, 15f)]
        private float _confirmCooldown = 4f;

        [Tooltip("Ángulo mínimo de salida de zona Full antes de resetear\n" +
                 "el cooldown de confirmación. Evita re-disparar si el usuario\n" +
                 "oscila 1° alrededor de la dirección correcta.\n" +
                 "Default: 15°.")]
        [SerializeField, Range(5f, 40f)]
        private float _confirmResetAngle = 15f;

        [Header("─── Distancia mínima al waypoint ───────────────────────────")]
        [Tooltip("Distancia mínima (m) al próximo waypoint para activar la háptica.\n" +
                 "Cerca del waypoint la háptica se inhibe (la voz ya lo indica).\n" +
                 "Default: 1.5m.")]
        [SerializeField, Range(0.5f, 5f)]
        private float _minWaypointDist = 1.5f;

        [Header("─── Intervalo de evaluación ────────────────────────────────")]
        [SerializeField, Range(0.05f, 0.5f)]
        private float _evalInterval = 0.12f;

        [Header("─── Debug ────────────────────────────────────────────────────")]
        [SerializeField] private bool _logHaptics = true;

        // ─────────────────────────────────────────────────────────────────────
        //  Estado interno
        // ─────────────────────────────────────────────────────────────────────

        private bool  _isActive           = false;
        private float _evalAccum          = 0f;
        private float _stillnessTimer     = 0f;
        private bool  _isStill            = false;

        private float _lastGlobalPulse    = -999f;
        private float _lastPartialPulse   = -999f;
        private float _lastMediumPulse    = -999f;
        private float _lastConfirmTime    = -999f;
        private bool  _confirmFired       = false;  // true hasta que salga de zona Full

        private AlignmentZone _lastZone   = AlignmentZone.None;

        private Coroutine _doubleVibCoroutine = null;

        // ─────────────────────────────────────────────────────────────────────
        //  iOS native (Taptic Engine)
        // ─────────────────────────────────────────────────────────────────────

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void _TapticLight();

        [DllImport("__Internal")]
        private static extern void _TapticMedium();

        [DllImport("__Internal")]
        private static extern void _TapticHeavy();
#endif

        // ─────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        private void Start()
        {
            if (_userBridge == null)
                _userBridge = FindFirstObjectByType<UserPositionBridge>(FindObjectsInactive.Include);

            if (_voiceGuide == null)
                _voiceGuide = FindFirstObjectByType<NavigationVoiceGuide>(FindObjectsInactive.Include);

            SubscribeEvents();
            Debug.Log("[HapticFeedback] ✅ v1.0 iniciado.");
        }

        private void OnEnable()  => SubscribeEvents();
        private void OnDisable() => UnsubscribeEvents();

        private void OnDestroy() => UnsubscribeEvents();

        private void SubscribeEvents()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Subscribe<NavigationStartedEvent>(OnNavStarted);
            bus.Subscribe<NavigationCompletedEvent>(OnNavCompleted);
            bus.Subscribe<NavigationCancelledEvent>(OnNavCancelled);
            bus.Subscribe<FloorTransitionEvent>(OnFloorTransition);
        }

        private void UnsubscribeEvents()
        {
            var bus = EventBus.Instance;
            if (bus == null) return;
            bus.Unsubscribe<NavigationStartedEvent>(OnNavStarted);
            bus.Unsubscribe<NavigationCompletedEvent>(OnNavCompleted);
            bus.Unsubscribe<NavigationCancelledEvent>(OnNavCancelled);
            bus.Unsubscribe<FloorTransitionEvent>(OnFloorTransition);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Update
        // ─────────────────────────────────────────────────────────────────────

        private void Update()
        {
            if (!_isActive || _userBridge == null || _voiceGuide == null) return;

            float dt = Time.deltaTime;

            // ── Detector de quietud ───────────────────────────────────────────
            if (_userBridge.UserSpeed < _minSpeedToEvaluate)
            {
                _stillnessTimer += dt;
                if (_stillnessTimer >= _stillnessConfirmTime)
                    _isStill = true;
            }
            else
            {
                // Usuario caminando: resetear estado de quietud y suprimir háptica
                _stillnessTimer = 0f;
                if (_isStill)
                {
                    _isStill  = false;
                    _lastZone = AlignmentZone.None;
                    // No cancelar _confirmFired — se mantiene hasta que gire
                }
                return; // No evaluar mientras camina
            }

            if (!_isStill) return;

            // ── Evaluación periódica ─────────────────────────────────────────
            _evalAccum += dt;
            if (_evalAccum < _evalInterval) return;
            _evalAccum = 0f;

            EvaluateAlignment();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Lógica de alineación
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateAlignment()
        {
            // Obtener posición del próximo evento de navegación
            var events = _voiceGuide.InstructionEvents;
            if (events == null || events.Count == 0) return;

            // Buscar el primer evento no disparado
            Vector3 targetPos = Vector3.zero;
            bool    found     = false;

            for (int i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                if (!ev.HasFired)
                {
                    // Ignorar eventos de llegada si estamos muy cerca
                    float dist = Vector3.Distance(_userBridge.UserPosition, ev.WorldPosition);
                    if (dist < _minWaypointDist) continue;

                    targetPos = ev.WorldPosition;
                    found     = true;
                    break;
                }
            }

            if (!found) return;

            // ── Calcular ángulo entre UserForward y dirección al waypoint ────
            Vector3 toTarget = targetPos - _userBridge.UserPosition;
            toTarget.y = 0f;

            if (toTarget.sqrMagnitude < 0.01f) return; // demasiado cerca

            toTarget.Normalize();

            Vector3 userFwd = _userBridge.UserForward; // ya normalizado y plano (v3)
            float   absAngle = Vector3.Angle(userFwd, toTarget);

            // ── Clasificar zona ──────────────────────────────────────────────
            AlignmentZone zone = ClassifyZone(absAngle);

            // ── Resetear confirmFired si el usuario giró suficientemente ────
            if (_confirmFired && absAngle > _alignAngleFull + _confirmResetAngle)
                _confirmFired = false;

            // ── Actualizar zona y disparar vibración si corresponde ──────────
            HandleZone(zone, absAngle);

            _lastZone = zone;
        }

        private AlignmentZone ClassifyZone(float absAngle)
        {
            if (absAngle <= _alignAngleFull)    return AlignmentZone.Full;
            if (absAngle <= _alignAngleMedium)  return AlignmentZone.Medium;
            if (absAngle <= _alignAnglePartial) return AlignmentZone.Partial;
            return AlignmentZone.None;
        }

        private void HandleZone(AlignmentZone zone, float absAngle)
        {
            float now = Time.time;

            // ── Zona Full — doble pulso de confirmación ──────────────────────
            if (zone == AlignmentZone.Full)
            {
                // No repetir si ya se confirmó recientemente
                if (_confirmFired) return;
                if (now - _lastConfirmTime < _confirmCooldown) return;
                if (now - _lastGlobalPulse < _globalCooldown)  return;

                _confirmFired    = true;
                _lastConfirmTime = now;
                _lastGlobalPulse = now;

                if (_logHaptics)
                    Debug.Log($"[HapticFeedback] ✅ CONFIRMACIÓN alineación total ({absAngle:F1}°)");

                if (_doubleVibCoroutine != null) StopCoroutine(_doubleVibCoroutine);
                _doubleVibCoroutine = StartCoroutine(DoubleVibrate(
                    _pulseConfirmAMs, _pulseConfirmGapMs, _pulseConfirmBMs));
                return;
            }

            // ── Zona Medium — pulso medio ────────────────────────────────────
            if (zone == AlignmentZone.Medium)
            {
                if (now - _lastMediumPulse  < _pulseCooldownMedium) return;
                if (now - _lastGlobalPulse  < _globalCooldown)       return;

                _lastMediumPulse = now;
                _lastGlobalPulse = now;

                if (_logHaptics)
                    Debug.Log($"[HapticFeedback] 〰 MEDIO ({absAngle:F1}°)");

                Vibrate(_pulseMediumMs, HapticStrength.Medium);
                return;
            }

            // ── Zona Partial — pulso suave ───────────────────────────────────
            if (zone == AlignmentZone.Partial)
            {
                if (now - _lastPartialPulse < _pulseCooldownPartial) return;
                if (now - _lastGlobalPulse  < _globalCooldown)       return;

                _lastPartialPulse = now;
                _lastGlobalPulse  = now;

                if (_logHaptics)
                    Debug.Log($"[HapticFeedback] · LEVE ({absAngle:F1}°)");

                Vibrate(_pulseShortMs, HapticStrength.Light);
                return;
            }

            // Zone.None — sin acción
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Vibración — abstracción multiplataforma
        // ─────────────────────────────────────────────────────────────────────

        private enum HapticStrength { Light, Medium, Heavy }

        private void Vibrate(int durationMs, HapticStrength strength)
        {
#if UNITY_EDITOR
            // En el editor solo logueamos — no hay vibración real
            Debug.Log($"[HapticFeedback][EDITOR] Vibrar {durationMs}ms ({strength})");

#elif UNITY_ANDROID
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity    = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var vibrator    = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");

                if (vibrator == null) return;

                // Android 8+ (API 26): usar VibrationEffect para control de amplitud
                int sdkInt = GetAndroidSdkVersion();
                if (sdkInt >= 26)
                {
                    int amplitude = strength switch
                    {
                        HapticStrength.Light  => 80,
                        HapticStrength.Medium => 160,
                        HapticStrength.Heavy  => 255,
                        _ => 128
                    };

                    using var vibrationEffect = new AndroidJavaClass(
                        "android.os.VibrationEffect");
                    using var effect = vibrationEffect.CallStatic<AndroidJavaObject>(
                        "createOneShot", (long)durationMs, amplitude);

                    vibrator.Call("vibrate", effect);
                }
                else
                {
                    // Android < 8: vibración simple sin control de amplitud
                    vibrator.Call("vibrate", (long)durationMs);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[HapticFeedback] Error Android vibrate: {ex.Message}");
            }

#elif UNITY_IOS
            // iOS: Taptic Engine — ignora durationMs (lo controla el sistema)
            switch (strength)
            {
                case HapticStrength.Light:  _TapticLight();  break;
                case HapticStrength.Medium: _TapticMedium(); break;
                case HapticStrength.Heavy:  _TapticHeavy();  break;
            }
#endif
        }

        /// <summary>
        /// Doble pulso para confirmación de alineación total.
        /// firstMs → gap → secondMs.
        /// </summary>
        private IEnumerator DoubleVibrate(int firstMs, int gapMs, int secondMs)
        {
            Vibrate(firstMs, HapticStrength.Medium);
            yield return new WaitForSecondsRealtime((firstMs + gapMs) / 1000f);
            Vibrate(secondMs, HapticStrength.Heavy);
            _doubleVibCoroutine = null;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static int _cachedSdkVersion = -1;
        private static int GetAndroidSdkVersion()
        {
            if (_cachedSdkVersion > 0) return _cachedSdkVersion;
            try
            {
                using var versionClass = new AndroidJavaClass("android.os.Build$VERSION");
                _cachedSdkVersion = versionClass.GetStatic<int>("SDK_INT");
            }
            catch { _cachedSdkVersion = 21; } // fallback conservador
            return _cachedSdkVersion;
        }
#else
        private static int GetAndroidSdkVersion() => 26;
#endif

        // ─────────────────────────────────────────────────────────────────────
        //  Eventos del bus
        // ─────────────────────────────────────────────────────────────────────

        private void OnNavStarted(NavigationStartedEvent _)
        {
            ResetState();
            _isActive = true;
            if (_logHaptics) Debug.Log("[HapticFeedback] ▶ Activo.");
        }

        private void OnNavCompleted(NavigationCompletedEvent _)
        {
            ResetState();
            _isActive = false;
            if (_logHaptics) Debug.Log("[HapticFeedback] ■ Detenido (llegó).");
        }

        private void OnNavCancelled(NavigationCancelledEvent _)
        {
            ResetState();
            _isActive = false;
            if (_logHaptics) Debug.Log("[HapticFeedback] ■ Detenido (cancelado).");
        }

        private void OnFloorTransition(FloorTransitionEvent evt)
        {
            // Al cambiar de piso, resetear cooldowns — nueva orientación, nuevo contexto
            ResetCooldowns();
            if (_logHaptics)
                Debug.Log($"[HapticFeedback] 🔄 FloorTransition {evt.FromLevel}→{evt.ToLevel}: cooldowns reset.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Reset
        // ─────────────────────────────────────────────────────────────────────

        private void ResetState()
        {
            _stillnessTimer = 0f;
            _isStill        = false;
            _evalAccum      = 0f;
            _lastZone       = AlignmentZone.None;
            if (_doubleVibCoroutine != null)
            {
                StopCoroutine(_doubleVibCoroutine);
                _doubleVibCoroutine = null;
            }
            ResetCooldowns();
        }

        private void ResetCooldowns()
        {
            _lastGlobalPulse  = -999f;
            _lastPartialPulse = -999f;
            _lastMediumPulse  = -999f;
            _lastConfirmTime  = -999f;
            _confirmFired     = false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  API pública
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Activa o desactiva el sistema desde código externo (ej. configuración de usuario).
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            if (!enabled) ResetState();
            _isActive = enabled && _voiceGuide != null && _voiceGuide.IsGuiding;
        }

        /// <summary>
        /// Dispara un pulso de prueba inmediato. Útil para que el usuario
        /// calibre la intensidad en Settings.
        /// </summary>
        public void TestPulse()
        {
            if (_doubleVibCoroutine != null) StopCoroutine(_doubleVibCoroutine);
            _doubleVibCoroutine = StartCoroutine(
                DoubleVibrate(_pulseConfirmAMs, _pulseConfirmGapMs, _pulseConfirmBMs));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ContextMenu debug
        // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
        [ContextMenu("🔔 Test: Pulso de confirmación")]
        private void DbgConfirmPulse() => TestPulse();

        [ContextMenu("🔔 Test: Pulso medio")]
        private void DbgMediumPulse() => Vibrate(_pulseMediumMs, HapticStrength.Medium);

        [ContextMenu("🔔 Test: Pulso leve")]
        private void DbgLightPulse() => Vibrate(_pulseShortMs, HapticStrength.Light);

        [ContextMenu("▶ Simular NavigationStarted")]
        private void DbgNavStart() => OnNavStarted(default);

        [ContextMenu("■ Simular NavigationCancelled")]
        private void DbgNavCancel() => OnNavCancelled(default);

        [ContextMenu("ℹ️ Estado actual")]
        private void DbgStatus()
        {
            Debug.Log(
                $"[HapticFeedback] v1.0\n" +
                $"  Activo:          {_isActive}\n" +
                $"  isStill:         {_isStill} (timer={_stillnessTimer:F2}s)\n" +
                $"  Zona actual:     {_lastZone}\n" +
                $"  confirmFired:    {_confirmFired}\n" +
                $"  UserSpeed:       {(_userBridge != null ? _userBridge.UserSpeed.ToString("F2") : "n/a")} m/s\n" +
                $"  lastGlobal:      {Time.time - _lastGlobalPulse:F1}s atrás\n" +
                $"  lastConfirm:     {Time.time - _lastConfirmTime:F1}s atrás"
            );
        }
#endif
    }
}