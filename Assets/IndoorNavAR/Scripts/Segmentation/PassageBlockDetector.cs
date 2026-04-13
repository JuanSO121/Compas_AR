// File: PassageBlockDetector.cs
// Assets/IndoorNavAR/Scripts/Segmentation/
// ✅ v1.0 — Detector de paso bloqueado (pared / obstáculo) para personas con discapacidad visual
//
// ════════════════════════════════════════════════════════════════════════════
// PROPÓSITO
// ════════════════════════════════════════════════════════════════════════════
//
//  Detecta dos condiciones críticas de "paso bloqueado" usando los ratios
//  de segmentación del ObstacleSegmentationWorker:
//
//  1. WALL_BLOCK  — Pared enfrente
//     Condición: wallRatio   >= _wallBlockThreshold   (default 0.55)
//              Y floorRatio  <= _floorSuppressThreshold (default 0.08)
//     Mensaje:  "Cuidado, pared al frente."
//
//  2. OBJECT_BLOCK — Obstáculo bloqueando el paso
//     Condición: obstacleRatio >= _objectBlockThreshold  (default 0.55)
//              Y floorRatio    <= _floorSuppressThreshold (default 0.08)
//     Mensaje:  "Cuidado, obstáculo al frente."
//
//  Los umbrales son deliberadamente altos (≥ 55% del frame) porque queremos
//  alertar solo cuando el bloqueo es inminente — no ante cualquier pared
//  o caja visible en la escena. A esa cobertura el usuario está a <1m de
//  colisionar con el obstáculo.
//
// ════════════════════════════════════════════════════════════════════════════
// DISEÑO TÉCNICO
// ════════════════════════════════════════════════════════════════════════════
//
//  • RESPONSABILIDAD ÚNICA: este componente solo detecta y emite TTS.
//    No toca SegmentationController, ObstacleRerouteMediator ni VoiceGuide.
//
//  • CANAL TTS: publica TTSRequestEvent al EventBus (mismo bus que usa
//    NavigationVoiceGuide). Flutter recibe tts_request y ejecuta el TTS.
//    — priority = 2 + interrupt = false → se encola detrás de lo activo.
//    — NUNCA usa priority=3 (reservado para emergencias de Unity: UTurn,
//      ObstacleWarning real).
//
//  • SIN DOBLE ALERTA: cuando ObstacleRerouteMediator.IsActive == true,
//    el mediador ya está procesando el obstáculo con priority=3. Este
//    detector suprime su alerta para evitar saturar el TTS del usuario.
//
//  • CONFIRMACIÓN POR FRAMES: requiere N frames consecutivos en condición
//    antes de disparar (_confirmationFrames, default 4). Filtra falsos
//    positivos por jitter de segmentación.
//
//  • COOLDOWN INDEPENDIENTE por tipo: WallBlock y ObjectBlock tienen
//    timers separados. Si hay pared larga, no repite cada segundo —
//    espera _alertCooldown (default 12s) antes de volver a hablar.
//
//  • REQUIERE WallRatio EN EL WORKER: aplica el patch de
//    ObstacleSegmentationWorker_WallRatioPatch.cs antes de usar este
//    componente.
//
// ════════════════════════════════════════════════════════════════════════════
// INTEGRACIÓN
// ════════════════════════════════════════════════════════════════════════════
//
//  1. Aplica el patch al worker (agrega WallRatio — ver archivo patch).
//  2. Añade este script como componente en el mismo GameObject que tiene
//     SegmentationController (o en uno nuevo dedicado).
//  3. No requiere referencias manuales en el Inspector — auto-detecta todo.
//  4. Funciona tanto con navegación activa como en modo standalone.

using UnityEngine;
using IndoorNavAR.Core.Events;
using IndoorNavAR.Navigation; // Para ObstacleRerouteMediator.IsActive

namespace IndoorNavAR.Segmentation
{
    public sealed class PassageBlockDetector : MonoBehaviour
    {
        // ─── Tipo de bloqueo detectado ────────────────────────────────────────

        private enum BlockType { None, Wall, Object }

        // ─── Inspector — Umbrales ─────────────────────────────────────────────

        [Header("─── Umbrales de detección ───────────────────────────────────")]

        [Tooltip("Ratio mínimo de WALL para considerar que hay una pared bloqueando el paso.\n" +
                 "Rango recomendado: 0.45 – 0.65. Default 0.55.\n" +
                 "Un valor de 0.55 significa que el 55% del frame es pared → usuario a <1m.")]
        [SerializeField, Range(0.30f, 0.85f)]
        private float _wallBlockThreshold = 0.55f;

        [Tooltip("Ratio mínimo de OBSTACLE para considerar que un objeto bloquea el paso.\n" +
                 "Rango recomendado: 0.45 – 0.65. Default 0.55.")]
        [SerializeField, Range(0.30f, 0.85f)]
        private float _objectBlockThreshold = 0.55f;

        [Tooltip("Ratio MÁXIMO de FLOOR tolerable cuando hay bloqueo.\n" +
                 "Si hay mucho suelo visible, el paso no está completamente bloqueado — ignorar.\n" +
                 "Default 0.08 (8%). Sube si hay falsos negativos en suelos texturizados.")]
        [SerializeField, Range(0.02f, 0.25f)]
        private float _floorSuppressThreshold = 0.08f;

        // ─── Inspector — Confirmación ─────────────────────────────────────────

        [Header("─── Confirmación por frames ────────────────────────────────")]

        [Tooltip("Frames consecutivos con condición activa antes de disparar la alerta.\n" +
                 "Reduce falsos positivos por jitter de inferencia.\n" +
                 "Default 4 frames ≈ 0.4s a 10fps de segmentación.")]
        [SerializeField, Range(2, 10)]
        private int _confirmationFrames = 4;

        // ─── Inspector — Cooldown ─────────────────────────────────────────────

        [Header("─── Cooldown ────────────────────────────────────────────────")]

        [Tooltip("Segundos mínimos entre alertas del mismo tipo.\n" +
                 "Evita que el TTS repita 'Cuidado, pared al frente' cada pocos segundos\n" +
                 "mientras el usuario sigue parado frente a la pared.\n" +
                 "Default 12s.")]
        [SerializeField, Range(5f, 60f)]
        private float _alertCooldown = 12f;

        [Tooltip("Segundos mínimos entre cualquier alerta (de cualquier tipo).\n" +
                 "Evita que Wall + Object se disparen simultáneamente.\n" +
                 "Default 3s.")]
        [SerializeField, Range(1f, 10f)]
        private float _globalAlertCooldown = 3f;

        // ─── Inspector — Mensajes TTS ─────────────────────────────────────────

        [Header("─── Mensajes de alerta ──────────────────────────────────────")]

        [Tooltip("Texto TTS para bloqueo por pared.\n" +
                 "Debe ser corto y directo — se leerá en <2 segundos.")]
        [SerializeField]
        private string _wallBlockMessage = "Cuidado, pared al frente.";

        [Tooltip("Texto TTS para bloqueo por obstáculo.\n" +
                 "Debe ser corto y directo.")]
        [SerializeField]
        private string _objectBlockMessage = "Cuidado, obstáculo al frente.";

        // ─── Inspector — Prioridad TTS ────────────────────────────────────────

        [Header("─── Prioridad TTS ───────────────────────────────────────────")]

        [Tooltip("Prioridad TTS de las alertas.\n" +
                 "2 = encola detrás del TTS activo (recomendado).\n" +
                 "3 = interrumpe — usar solo si el equipo decide que es emergencia pura.\n" +
                 "NOTA: priority=3 + interrupt=true puede cortar instrucciones de giro.\n" +
                 "      Mantener en 2 salvo decisión explícita del equipo.")]
        [SerializeField, Range(1, 3)]
        private int _alertPriority = 2;

        [Tooltip("Si true, la alerta interrumpe el TTS activo (solo cuando priority=3).\n" +
                 "Con priority=2 este campo se ignora — siempre encola.")]
        [SerializeField]
        private bool _interruptOnAlert = false;

        // ─── Inspector — Debug ────────────────────────────────────────────────

        [Header("─── Debug ───────────────────────────────────────────────────")]

        [SerializeField] private bool _logDetection = true;

        // ─── Estado interno ───────────────────────────────────────────────────

        // Contadores de frames consecutivos por tipo
        private int _wallConsecutiveFrames   = 0;
        private int _objectConsecutiveFrames = 0;

        // Timestamps de última alerta por tipo (usando Time.unscaledTime)
        private float _lastWallAlertTime   = -999f;
        private float _lastObjectAlertTime = -999f;
        private float _lastAnyAlertTime    = -999f;

        // ─── Lifecycle ────────────────────────────────────────────────────────

        private void Start()
        {
            // Validar que el worker existe — si no hay segmentación activa,
            // este componente simplemente no hace nada.
            if (ObstacleSegmentationWorker.Instance == null)
            {
                Debug.LogWarning("[PassageBlockDetector] ObstacleSegmentationWorker.Instance " +
                                 "es null en Start(). El detector estará inactivo hasta que " +
                                 "SegmentationController inicialice el worker.");
            }

            Debug.Log("[PassageBlockDetector] ✅ v1.0 inicializado.\n" +
                      $"  WallThreshold={_wallBlockThreshold:P0} " +
                      $"ObjectThreshold={_objectBlockThreshold:P0} " +
                      $"FloorSuppress={_floorSuppressThreshold:P0}\n" +
                      $"  ConfirmFrames={_confirmationFrames} " +
                      $"Cooldown={_alertCooldown}s " +
                      $"Priority={_alertPriority} " +
                      $"Interrupt={_interruptOnAlert}");
        }

        private void Update()
        {
            var worker = ObstacleSegmentationWorker.Instance;

            // Sin worker activo o sin inferencia reciente: resetear contadores
            if (worker == null || !worker.IsReady || worker.IsBusy)
            {
                _wallConsecutiveFrames   = 0;
                _objectConsecutiveFrames = 0;
                return;
            }

            // ── Leer ratios actuales ──────────────────────────────────────────
            float wallRatio     = worker.WallRatio;
            float obstacleRatio = worker.ObstacleRatio;
            float floorRatio    = worker.FloorRatio;

            // ── Evaluar condiciones de bloqueo ────────────────────────────────
            EvaluateWallBlock(wallRatio, floorRatio);
            EvaluateObjectBlock(obstacleRatio, floorRatio);
        }

        // ─── Evaluación — Pared ───────────────────────────────────────────────

        private void EvaluateWallBlock(float wallRatio, float floorRatio)
        {
            bool conditionMet = wallRatio   >= _wallBlockThreshold
                             && floorRatio  <= _floorSuppressThreshold;

            if (conditionMet)
            {
                _wallConsecutiveFrames++;

                if (_wallConsecutiveFrames >= _confirmationFrames)
                    TryFireAlert(BlockType.Wall, wallRatio, floorRatio);
            }
            else
            {
                // Decaimiento suave: si la condición desaparece, no reseteamos
                // inmediatamente a 0 — dejamos que baje 1 por frame.
                // Esto evita que un frame ruidoso cancele una detección legítima.
                _wallConsecutiveFrames = Mathf.Max(0, _wallConsecutiveFrames - 1);
            }
        }

        // ─── Evaluación — Obstáculo ───────────────────────────────────────────

        private void EvaluateObjectBlock(float obstacleRatio, float floorRatio)
        {
            bool conditionMet = obstacleRatio >= _objectBlockThreshold
                             && floorRatio    <= _floorSuppressThreshold;

            if (conditionMet)
            {
                _objectConsecutiveFrames++;

                if (_objectConsecutiveFrames >= _confirmationFrames)
                    TryFireAlert(BlockType.Object, obstacleRatio, floorRatio);
            }
            else
            {
                _objectConsecutiveFrames = Mathf.Max(0, _objectConsecutiveFrames - 1);
            }
        }

        // ─── Disparo de alerta ────────────────────────────────────────────────

        private void TryFireAlert(BlockType type, float triggerRatio, float floorRatio)
        {
            float now = Time.unscaledTime;

            // ── Guard 1: cooldown global (evita Wall + Object simultáneos) ────
            if (now - _lastAnyAlertTime < _globalAlertCooldown)
            {
                if (_logDetection)
                    Debug.Log($"[PassageBlockDetector] 🔇 [{type}] Global cooldown " +
                              $"({now - _lastAnyAlertTime:F1}s < {_globalAlertCooldown}s)");
                return;
            }

            // ── Guard 2: cooldown por tipo ────────────────────────────────────
            float lastTypeAlert = type == BlockType.Wall ? _lastWallAlertTime : _lastObjectAlertTime;
            if (now - lastTypeAlert < _alertCooldown)
            {
                if (_logDetection)
                    Debug.Log($"[PassageBlockDetector] 🔇 [{type}] Cooldown por tipo " +
                              $"({now - lastTypeAlert:F1}s < {_alertCooldown}s)");
                return;
            }

            // ── Guard 3: mediador activo → priority=3 ya gestionado por Unity ──
            // Cuando ObstacleRerouteMediator está activo, NavigationVoiceGuide
            // ya habrá disparado "Obstáculo detectado. Buscando ruta alternativa."
            // con priority=3 + interrupt=true. No añadimos una segunda voz encima.
            //
            // NOTA: Para alertas de PARED no hay mediador — no se suprime.
            if (type == BlockType.Object && ObstacleRerouteMediator.IsActive)
            {
                if (_logDetection)
                    Debug.Log("[PassageBlockDetector] 🔇 [Object] ObstacleRerouteMediator " +
                              "activo — alerta suprimida (VoiceGuide ya habló p=3).");
                return;
            }

            // ── Componer el mensaje y publicar al EventBus ────────────────────
            string message = type == BlockType.Wall ? _wallBlockMessage : _objectBlockMessage;

            // Garantía: con priority=2, interrupt es siempre false.
            // Esto es intencional: queremos encolar detrás de la instrucción
            // de navegación activa, no cortarla.
            int  finalPriority  = Mathf.Clamp(_alertPriority, 1, 3);
            bool finalInterrupt = (finalPriority >= 3) && _interruptOnAlert;

            EventBus.Instance?.Publish(new TTSRequestEvent
            {
                Text      = message,
                Priority  = finalPriority,
                Interrupt = finalInterrupt,
            });

            // ── Actualizar timestamps ─────────────────────────────────────────
            _lastAnyAlertTime = now;
            if (type == BlockType.Wall)   _lastWallAlertTime   = now;
            else                          _lastObjectAlertTime = now;

            // ── Resetear contador de confirmación ─────────────────────────────
            // Obliga a re-confirmar antes de disparar de nuevo — evita ráfagas
            // si el usuario sigue frente al obstáculo.
            if (type == BlockType.Wall)   _wallConsecutiveFrames   = 0;
            else                          _objectConsecutiveFrames = 0;

            if (_logDetection)
                Debug.Log($"[PassageBlockDetector] 🚨 [{type}] " +
                          $"ratio={triggerRatio:P1} floor={floorRatio:P1} " +
                          $"p={finalPriority} interrupt={finalInterrupt} " +
                          $"\"{message}\"");
        }

        // ─── ContextMenu debug ────────────────────────────────────────────────

        [ContextMenu("📊 Log ratios actuales")]
        private void DbgLogRatios()
        {
            var worker = ObstacleSegmentationWorker.Instance;
            if (worker == null)
            {
                Debug.Log("[PassageBlockDetector] Worker no disponible.");
                return;
            }
            Debug.Log($"[PassageBlockDetector] Ratios: " +
                      $"wall={worker.WallRatio:P1} " +
                      $"obstacle={worker.ObstacleRatio:P1} " +
                      $"floor={worker.FloorRatio:P1}\n" +
                      $"  WallFrames={_wallConsecutiveFrames}/{_confirmationFrames} " +
                      $"ObjectFrames={_objectConsecutiveFrames}/{_confirmationFrames}\n" +
                      $"  MediatorActive={ObstacleRerouteMediator.IsActive}");
        }

        [ContextMenu("🧪 Simular alerta PARED")]
        private void DbgSimulateWall()
        {
            Debug.Log("[PassageBlockDetector] Simulando alerta de pared...");
            EventBus.Instance?.Publish(new TTSRequestEvent
            {
                Text      = _wallBlockMessage,
                Priority  = _alertPriority,
                Interrupt = false,
            });
        }

        [ContextMenu("🧪 Simular alerta OBSTÁCULO")]
        private void DbgSimulateObject()
        {
            Debug.Log("[PassageBlockDetector] Simulando alerta de obstáculo...");
            EventBus.Instance?.Publish(new TTSRequestEvent
            {
                Text      = _objectBlockMessage,
                Priority  = _alertPriority,
                Interrupt = false,
            });
        }

        [ContextMenu("🔄 Resetear cooldowns")]
        private void DbgResetCooldowns()
        {
            _lastWallAlertTime   = -999f;
            _lastObjectAlertTime = -999f;
            _lastAnyAlertTime    = -999f;
            _wallConsecutiveFrames   = 0;
            _objectConsecutiveFrames = 0;
            Debug.Log("[PassageBlockDetector] Cooldowns reseteados.");
        }
    }
}