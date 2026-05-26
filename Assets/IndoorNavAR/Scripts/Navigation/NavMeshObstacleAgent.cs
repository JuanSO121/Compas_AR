// File: NavMeshObstacleAgent.cs
// Assets/IndoorNavAR/Scripts/Navigation/
// ✅ v1.0 — Obstáculo virtual estándar para recálculo de ruta
//
// Coloca un NavMeshObstacle (Unity built-in) en una posición del mundo.
// El obstáculo talla el NavMesh automáticamente, forzando a
// NavigationPathController a recalcular la ruta al detectar el cambio.
//
// Tamaño por defecto: 2.0m ancho × 1.8m alto × 1.4m profundidad
// (bloqueo amplio para forzar una ruta alternativa con más margen).

using System.Collections;
using UnityEngine;
using UnityEngine.AI;

namespace IndoorNavAR.Navigation
{
    [RequireComponent(typeof(NavMeshObstacle))]
    public class NavMeshObstacleAgent : MonoBehaviour
    {
        // ── Tamaño estándar "bloqueo amplio de paso" ───────────────────
        [Tooltip("Tamaño del obstáculo virtual tallado en el NavMesh (ancho, alto, profundidad).")]
        [SerializeField] private Vector3 _obstacleSize = new Vector3(2.0f, 1.8f, 1.4f);

        // Cuántos segundos permanece el obstáculo antes de auto-destruirse.
        // Permite que el usuario rodee el obstáculo y la ruta se normalice.
        [SerializeField] private float _lifetimeSeconds = 8f;

        // Offset delante de la cámara donde se coloca el obstáculo (metros).
        // 1.5m es suficiente para que el NavMesh tenga espacio para rodear.
        [SerializeField] private float _forwardOffset = 1.5f;

        private NavMeshObstacle _obstacle;
        private Coroutine       _lifetimeCoroutine;

        private void Awake()
        {
            _obstacle            = GetComponent<NavMeshObstacle>();
            _obstacle.carving    = true;   // activa el tallado dinámico del NavMesh
            _obstacle.shape      = NavMeshObstacleShape.Box;
            _obstacle.size       = _obstacleSize;
            _obstacle.center     = Vector3.zero;

            // Mover sólo si el obstáculo se desplaza > 0.1m — evita re-tallados
            // continuos por micro-jitter de ARCore.
            _obstacle.carvingMoveThreshold  = 0.1f;
            _obstacle.carvingTimeToStationary = 0.5f;
        }

        /// <summary>
        /// Coloca el obstáculo en worldPosition y activa el timer de vida.
        /// Llamar desde ObstacleRerouteMediator.
        /// </summary>
        public void PlaceAt(Vector3 worldPosition)
        {
            _obstacle.size = _obstacleSize;
            transform.position = worldPosition;
            gameObject.SetActive(true);

            if (_lifetimeCoroutine != null)
                StopCoroutine(_lifetimeCoroutine);

            _lifetimeCoroutine = StartCoroutine(AutoExpire());

            Debug.Log($"[NavMeshObstacleAgent] Obstáculo colocado en {worldPosition:F2}. " +
                      $"Tamaño: {_obstacleSize:F2}. Vida: {_lifetimeSeconds}s.");
        }

        private void OnValidate()
        {
            _obstacleSize.x = Mathf.Max(0.1f, _obstacleSize.x);
            _obstacleSize.y = Mathf.Max(0.1f, _obstacleSize.y);
            _obstacleSize.z = Mathf.Max(0.1f, _obstacleSize.z);

            if (_obstacle == null)
                _obstacle = GetComponent<NavMeshObstacle>();

            if (_obstacle != null)
                _obstacle.size = _obstacleSize;
        }

        /// <summary>
        /// Desactiva el obstáculo manualmente (ej: navegación cancelada).
        /// </summary>
        public void Remove()
        {
            if (_lifetimeCoroutine != null)
            {
                StopCoroutine(_lifetimeCoroutine);
                _lifetimeCoroutine = null;
            }
            gameObject.SetActive(false);
            Debug.Log("[NavMeshObstacleAgent] Obstáculo removido manualmente.");
        }

        private IEnumerator AutoExpire()
        {
            yield return new WaitForSeconds(_lifetimeSeconds);
            gameObject.SetActive(false);
            _lifetimeCoroutine = null;
            Debug.Log("[NavMeshObstacleAgent] Obstáculo expirado (lifetime).");
        }

        private void OnDestroy()
        {
            if (_lifetimeCoroutine != null)
                StopCoroutine(_lifetimeCoroutine);
        }

        /// <summary>
        /// Posición proyectada frente a la cámara, al nivel del suelo.
        /// Usa raycast hacia abajo para encontrar el NavMesh bajo el punto.
        /// </summary>
        public static bool TryGetPlacementPosition(
            Transform cameraTransform,
            float forwardOffset,
            out Vector3 result)
        {
            Vector3 flatForward = cameraTransform.forward;
            flatForward.y = 0f;

            if (flatForward.sqrMagnitude < 0.001f)
            {
                result = Vector3.zero;
                return false;
            }

            flatForward.Normalize();
            Vector3 candidate = cameraTransform.position + flatForward * forwardOffset;

            // Proyectar al NavMesh — busca hasta 3m abajo
            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 3f, NavMesh.AllAreas))
            {
                result = hit.position;
                return true;
            }

            result = Vector3.zero;
            return false;
        }
    }
}
