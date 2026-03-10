using System;

namespace IndoorNavAR.Core.Events
{
    /// <summary>
    /// Evento para solicitar reproducción de texto a voz en Flutter.
    /// Unity publica este evento y VoiceCommandAPI lo envía al bridge.
    /// </summary>
    public struct TTSRequestEvent
    {
        public string Text;

        /// prioridad opcional
        public int Priority;

        /// si debe interrumpir lo que se está hablando
        public bool Interrupt;
    }
}