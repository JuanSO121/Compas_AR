// File: TTSRequestEvent.cs
// Carpeta: Assets/IndoorNavAR/Scripts/Core/Events/
// ✅ v1.0 — Evento para solicitar TTS en Flutter desde Unity
//
// Este evento reemplaza el uso de GuideAnnouncementEvent como canal TTS.
// NavigationVoiceGuide lo publica → VoiceCommandAPI lo escucha y
// envía action="tts_request" al bridge de Flutter.
//
// GuideAnnouncementEvent sigue existiendo para consumidores de estado/UI
// (ARGuideController, overlays) pero ya NO es responsable de disparar TTS.

namespace IndoorNavAR.Core.Events
{
    /// <summary>
    /// Solicitud de texto a voz dirigida a Flutter.
    /// Flutter es el único dueño del engine TTS — Unity solo produce texto.
    /// </summary>
    public struct TTSRequestEvent
    {
        /// <summary>Texto a leer en voz alta.</summary>
        public string Text;

        /// <summary>
        /// Prioridad de la instrucción:
        ///   3 = urgente  (obstáculo, UTurn)           — interrupt=true obligatorio
        ///   2 = navegación (giros, escaleras, llegada) — interrupt si hay algo de menor prioridad
        ///   1 = informativo (inicio, ruta recalculada) — encolar detrás del actual
        ///   0 = bajo       (recto, progreso, parada)   — descartar si TTS ocupado
        /// </summary>
        public int Priority;

        /// <summary>
        /// Si true, Flutter interrumpe cualquier TTS en curso inmediatamente
        /// y vacía la cola antes de hablar este texto.
        /// Debe ser true solo para Priority >= 3.
        /// </summary>
        public bool Interrupt;
    }
}