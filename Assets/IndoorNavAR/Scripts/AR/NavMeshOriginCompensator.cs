// File: NavMeshOriginCompensator.cs
// ✅ v1.1 — Fix doble suscripción a OnOriginDrifted.
//
// ============================================================================
//  CAMBIOS v1.0 → v1.1
// ============================================================================
//
//  BUG CORREGIDO: OnOriginDrifted se ejecutaba dos veces por cada drift.
//
//  CAUSA en v1.0:
//    El componente se suscribía en DOS lugares:
//      - OnEnable()  → se ejecuta antes de Start()
//      - Start()     → se ejecuta justo después de OnEnable() en el primer frame
//
//    Si ARWorldOriginStabilizer ya existía cuando este componente se activaba
//    (caso normal — ambos están en la escena desde el inicio), OnEnable() se
//    suscribía correctamente, y luego Start() añadía UNA SEGUNDA suscripción
//    al mismo evento. C# events permiten múltiples suscriptores del mismo
//    método — no se deduplicam automáticamente.
//
//    Resultado: cada drift llamaba OnOriginDrifted() dos veces →
//    el agente era warpeado dos veces con el mismo delta → posición incorrecta.
//
//  FIX v1.1:
//    - Eliminar Start() completamente.
//    - En OnEnable(): suscribir con -= antes de += (patron idempotente).
//      Garantiza que aunque OnEnable() se llame múltiples veces (ej. el
//      GameObject se desactiva y reactiva), solo hay UNA suscripción activa.
//    - En OnDisable(): desuscribir con -= como antes.
//
// ============================================================================
//  v1.0 — Funcionalidad original (conservada íntegramente)
// ============================================================================
//
//  Reacciona al drift del AR world origin para mantener
//  la navegación alineada tras correcciones del modelo.
//
//  Flujo:
//    ARWorldOriginStabilizer.DetectAndCorrectDrift()
//      → corrige posición del modelo
//      → dispara OnOriginDrifted(oldPos, newPos)
//    NavMeshOriginCompensator.OnOriginDrifted()
//      → calcula delta de desplazamiento
//      → warpea el NavMeshAgent para seguir al modelo

using UnityEngine;
using UnityEngine.AI;
using IndoorNavAR.Navigation;

namespace IndoorNavAR.AR
{
    public class NavMeshOriginCompensator : MonoBehaviour
    {
        [SerializeField] private NavigationAgent _navigationAgent;
        [SerializeField] private bool            _logCompensation = true;

        private NavMeshAgent _rawAgent;

        private void Awake()
        {
            _navigationAgent ??= FindFirstObjectByType<NavigationAgent>();
            if (_navigationAgent != null)
                _rawAgent = _navigationAgent.GetComponent<NavMeshAgent>();
        }

        private void OnEnable()
        {
            var stabilizer = ARWorldOriginStabilizer.Instance;
            if (stabilizer == null) return;

            // ✅ v1.1: Patrón idempotente — desuscribir antes de suscribir.
            // Evita doble suscripción si OnEnable() se llama más de una vez
            // (ej. el GameObject se desactiva y reactiva).
            stabilizer.OnOriginDrifted -= OnOriginDrifted;
            stabilizer.OnOriginDrifted += OnOriginDrifted;
        }

        private void OnDisable()
        {
            var stabilizer = ARWorldOriginStabilizer.Instance;
            if (stabilizer != null)
                stabilizer.OnOriginDrifted -= OnOriginDrifted;
        }

        // ✅ v1.1: Start() ELIMINADO.
        // En v1.0 tenía una segunda suscripción aquí que causaba que
        // OnOriginDrifted() se ejecutara dos veces por drift. Ver changelog arriba.
        //
        // Si ARWorldOriginStabilizer se instancia DESPUÉS de este componente
        // (caso poco probable dado que ambos son singletons en la escena),
        // el stabilizer no estará disponible en OnEnable(). En ese escenario,
        // la suscripción simplemente no ocurre — el compensador no actuará
        // hasta que el GameObject sea desactivado y reactivado.
        // En la arquitectura actual esto no ocurre porque ambos están en la
        // escena desde el inicio.

        private void OnOriginDrifted(Vector3 oldModelPos, Vector3 newModelPos)
        {
            Vector3 delta = newModelPos - oldModelPos;

            if (delta.magnitude < 0.001f) return;

            Log($"🔄 Compensando drift: Δ={delta:F3} ({delta.magnitude:F3}m)");

            if (_rawAgent != null && _rawAgent.enabled && _rawAgent.isOnNavMesh)
            {
                Vector3 newAgentPos = _rawAgent.transform.position + delta;

                if (NavMesh.SamplePosition(newAgentPos, out NavMeshHit hit, 1.0f, NavMesh.AllAreas))
                {
                    _rawAgent.Warp(hit.position);
                    Log($"✅ NavMeshAgent warpeado a {hit.position:F3}");
                }
                else
                {
                    Log($"⚠️ Sin NavMesh en {newAgentPos:F3} — agente no warpeado.");
                }
            }
        }

        private void Log(string msg)
        {
            if (_logCompensation) Debug.Log($"[NavMeshOriginCompensator] {msg}");
        }
    }
}