// File: ARSessionReadyEvent.cs
// ✅ v1.1 — struct (no class) para compatibilidad con EventBus<T where T : struct>
//
// CUÁNDO SE PUBLICA:
//   ARSessionManager.WaitForStableTracking() lo publica después de
//   _initialStableFrames frames consecutivos de SessionTracking.
//
// QUIÉN LO ESCUCHA:
//   Sistemas que necesiten esperar tracking estable antes de operar.
//   AROriginAligner también usa IsFullyStable (polling) directamente.

namespace IndoorNavAR.Core.Events
{
    public struct ARSessionReadyEvent
    {
        // Sin campos — la presencia del evento es la señal.
    }
}