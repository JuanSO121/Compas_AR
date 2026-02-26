// File: NavigationPathOptimizer.cs
// ============================================================================
//  SISTEMA DE OPTIMIZACIÓN DE RUTAS — IndoorNavAR  v2
// ============================================================================
//
//  CAMBIOS RESPECTO A v1:
//
//  PROBLEMA RAÍZ ELIMINADO:
//    v1 usaba ClearanceField con raycasts radiales, y luego SamplePosition
//    durante el movimiento revertía ese desplazamiento. Ciclo de corrección
//    que se anulaba a sí mismo → agente pegado a paredes → atasco.
//
//  NUEVO ENFOQUE:
//    1. Clearance con NavMesh.FindClosestEdge() — distancia REAL al borde
//       del NavMesh, O(1) por punto, sin raycasts ni estimaciones.
//    2. Center Pull predictivo — busca el punto con mayor clearance en
//       perpendicular y tira el waypoint hacia allí ANTES de entregar la ruta.
//    3. Funnel conservador — solo elimina waypoints casi colineales cuyo
//       reemplazo tiene clearance equivalente. No destruye giros necesarios.
//    4. MinClearance expuesto en OptimizedPath para que el controller
//       tome decisiones antes de que el agente entre en una zona problemática.
//
//  COMPLEJIDAD:
//    ComputeOptimized: O(W * S) donde W = waypoints, S = CenterSamples (8)
//    FindClosestEdge: O(1) interno de Unity
//    Costo móvil estimado: ~0.3ms por cálculo de ruta

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Resultado de optimización. Inmutable tras construcción.
    /// </summary>
    public sealed class OptimizedPath
    {
        public IReadOnlyList<Vector3> Waypoints       { get; }
        public float                  TotalLength      { get; }
        public NavMeshPathStatus      Status           { get; }
        public int                    RawWaypointCount { get; }
        public float                  ComputeTimeMs    { get; }

        /// <summary>
        /// Clearance mínima en todo el path (distancia al borde del NavMesh).
        /// Si < AgentRadius * SafetyFactor el agente puede rozar paredes.
        /// </summary>
        public float MinClearance { get; }

        public bool IsValid => Status == NavMeshPathStatus.PathComplete && Waypoints.Count > 0;

        internal OptimizedPath(
            List<Vector3>     waypoints,
            NavMeshPathStatus status,
            int               rawCount,
            float             computeMs,
            float             minClearance = float.MaxValue)
        {
            Waypoints        = waypoints.AsReadOnly();
            Status           = status;
            RawWaypointCount = rawCount;
            ComputeTimeMs    = computeMs;
            MinClearance     = minClearance;

            float len = 0f;
            for (int i = 1; i < waypoints.Count; i++)
                len += Vector3.Distance(waypoints[i - 1], waypoints[i]);
            TotalLength = len;
        }
    }

    /// <summary>
    /// Datos de clearance de un segmento del path. Usado por el controller
    /// para detectar zonas problemáticas antes de entrar en ellas.
    /// </summary>
    public struct CorridorSegment
    {
        public Vector3 Start;
        public Vector3 End;
        public float   MinClearance;
        public bool    IsPassable;
    }

    /// <summary>
    /// Motor de optimización de rutas NavMesh para navegación indoor en AR.
    /// </summary>
    public sealed class NavigationPathOptimizer
    {
        // ─── Agente ───────────────────────────────────────────────────────────
        /// <summary>Radio del agente (m).</summary>
        public float AgentRadius    { get; set; } = 0.10f;

        /// <summary>
        /// Factor de seguridad. ClearanceMínima = AgentRadius * SafetyFactor.
        /// 1.8 = el agente quiere estar 1.8× su radio del borde → margen cómodo.
        /// </summary>
        public float SafetyFactor   { get; set; } = 1.8f;

        // ─── Center Pull ──────────────────────────────────────────────────────
        /// <summary>Intensidad del pull hacia el centro del corredor [0–1].</summary>
        public float CenterPullStrength { get; set; } = 0.65f;

        /// <summary>Desplazamiento lateral máximo por waypoint (m).</summary>
        public float MaxLateralShift    { get; set; } = 0.45f;

        /// <summary>Radio de búsqueda del centro del corredor (m).</summary>
        public float CenterSearchRadius { get; set; } = 1.2f;

        /// <summary>Muestras perpendiculares para encontrar el centro del corredor.</summary>
        public int   CenterSamples      { get; set; } = 8;

        // ─── Funnel ───────────────────────────────────────────────────────────
        /// <summary>
        /// Ángulo máximo entre segmentos para considerarlos redundantes.
        /// 20° = conservador. No elimina giros cerrados de pasillos.
        /// </summary>
        public float FunnelAngleThreshold { get; set; } = 20f;

        // ─── Look-Ahead ───────────────────────────────────────────────────────
        public int   LookAheadMaxSkip  { get; set; } = 2;

        // ─── Cache ────────────────────────────────────────────────────────────
        public float MaxPathAgeSeconds     { get; set; } = 30f;
        public float DeviationThresholdM   { get; set; } = 1.0f;
        public float DestinationThresholdM { get; set; } = 0.35f;
        public float HashCellSize          { get; set; } = 0.50f;

        // ─── NavMesh ──────────────────────────────────────────────────────────
        public int   AreaMask          { get; set; } = NavMesh.AllAreas;
        public float NavMeshSnapRadius { get; set; } = 2.0f;

        // ─── Estado ───────────────────────────────────────────────────────────
        private readonly NavMeshPath  _path             = new();
        private OptimizedPath         _cachedPath;
        private long                  _cachedOriginHash;
        private long                  _cachedDestHash;
        private float                 _cacheTimestamp;

        private int _lookAheadCacheFrame = -1;
        private int _lookAheadResult     = -1;
        private int _lookAheadFromIndex  = -1;

        private readonly List<Vector3> _workingWaypoints = new(64);

        // ─────────────────────────────────────────────────────────────────────

        public float MinAcceptableClearance => AgentRadius * SafetyFactor;

        // ─────────────────────────────────────────────────────────────────────
        //  API PÚBLICA
        // ─────────────────────────────────────────────────────────────────────

        public OptimizedPath ComputeOptimized(Vector3 origin, Vector3 destination)
        {
            float t0 = Time.realtimeSinceStartup;

            if (!SnapToNavMesh(origin,      out Vector3 snappedOrigin))      return InvalidPath();
            if (!SnapToNavMesh(destination, out Vector3 snappedDestination)) return InvalidPath();

            long  originHash = ComputeHash(snappedOrigin);
            long  destHash   = ComputeHash(snappedDestination);
            float elapsed    = Time.realtimeSinceStartup - _cacheTimestamp;

            if (_cachedPath        != null
             && _cachedPath.IsValid
             && originHash == _cachedOriginHash
             && destHash   == _cachedDestHash
             && elapsed    <  MaxPathAgeSeconds
             && ComputeDeviationFromPath(origin, _cachedPath.Waypoints) < DeviationThresholdM)
            {
                return _cachedPath;
            }

            bool pathFound = NavMesh.CalculatePath(snappedOrigin, snappedDestination, AreaMask, _path);

            if (!pathFound || _path.corners.Length < 2)
            {
                return new OptimizedPath(
                    new List<Vector3> { snappedOrigin, snappedDestination },
                    _path.status, _path.corners.Length,
                    (Time.realtimeSinceStartup - t0) * 1000f);
            }

            int rawCount = _path.corners.Length;

            _workingWaypoints.Clear();
            foreach (Vector3 c in _path.corners)
                _workingWaypoints.Add(c);

            // PASO 1: Center Pull predictivo usando FindClosestEdge
            float minClearance = ApplyCenterPull(_workingWaypoints);

            // PASO 2: Funnel conservador
            ApplyConservativeFunnel(_workingWaypoints);

            float computeMs = (Time.realtimeSinceStartup - t0) * 1000f;

            _cachedPath       = new OptimizedPath(_workingWaypoints, _path.status,
                                                  rawCount, computeMs, minClearance);
            _cachedOriginHash = originHash;
            _cachedDestHash   = destHash;
            _cacheTimestamp   = Time.realtimeSinceStartup;

            if (minClearance < MinAcceptableClearance)
                Debug.LogWarning($"[PathOptimizer] ⚠️ Clearance mínima {minClearance:F3}m " +
                                 $"< requerida {MinAcceptableClearance:F3}m");

            return _cachedPath;
        }

        /// <summary>
        /// Valida el ancho del corredor en cada segmento.
        /// Permite al controller anticipar zonas problemáticas.
        /// </summary>
        public List<CorridorSegment> ValidateCorridor(IReadOnlyList<Vector3> waypoints)
        {
            var result      = new List<CorridorSegment>(waypoints.Count);
            float minReq    = MinAcceptableClearance;

            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                Vector3 mid       = (waypoints[i] + waypoints[i + 1]) * 0.5f;
                float   clearance = MeasureClearance(mid);

                result.Add(new CorridorSegment
                {
                    Start        = waypoints[i],
                    End          = waypoints[i + 1],
                    MinClearance = clearance,
                    IsPassable   = clearance >= minReq
                });
            }

            return result;
        }

        public int GetLookAheadTarget(
            Vector3                agentPosition,
            IReadOnlyList<Vector3> waypoints,
            int                    currentIndex)
        {
            if (waypoints == null || currentIndex >= waypoints.Count - 1)
                return currentIndex;

            if (Time.frameCount == _lookAheadCacheFrame &&
                currentIndex    == _lookAheadFromIndex)
                return _lookAheadResult;

            int bestTarget = currentIndex;
            int maxTarget  = Mathf.Min(currentIndex + LookAheadMaxSkip, waypoints.Count - 1);

            for (int target = maxTarget; target > currentIndex; target--)
            {
                if (!NavMesh.Raycast(agentPosition, waypoints[target], out _, AreaMask))
                {
                    bestTarget = target;
                    break;
                }
            }

            _lookAheadCacheFrame = Time.frameCount;
            _lookAheadFromIndex  = currentIndex;
            _lookAheadResult     = bestTarget;

            return bestTarget;
        }

        public void InvalidateCache()
        {
            _cachedPath     = null;
            _cacheTimestamp = float.MinValue;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CENTER PULL — NÚCLEO DEL REDISEÑO
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Para cada waypoint intermedio:
        ///   1. Mide distancia real al borde del NavMesh (FindClosestEdge, O(1)).
        ///   2. Si está demasiado cerca de un borde, busca el punto con mayor
        ///      clearance en perpendicular mediante muestras discretas.
        ///   3. Desplaza el waypoint hacia ese punto con intensidad CenterPullStrength.
        ///
        /// Retorna clearance mínima del path resultante.
        /// </summary>
        private float ApplyCenterPull(List<Vector3> waypoints)
        {
            float globalMin  = float.MaxValue;
            float minReq     = MinAcceptableClearance;

            for (int i = 1; i < waypoints.Count - 1; i++)
            {
                Vector3 wp        = waypoints[i];
                float   clearance = MeasureClearance(wp);

                globalMin = Mathf.Min(globalMin, clearance);

                // Solo actuar si estamos cerca del borde
                if (clearance >= minReq * 1.2f) continue;

                // Dirección del segmento (suavizada entre anterior y siguiente)
                Vector3 segDir = (waypoints[i + 1] - waypoints[i - 1]);
                segDir.y = 0f;
                if (segDir.sqrMagnitude < 0.001f) continue;
                segDir.Normalize();

                // Perpendicular en plano XZ
                Vector3 perp = new Vector3(-segDir.z, 0f, segDir.x);

                Vector3 bestShift = Vector3.zero;
                float   bestClear = clearance;

                float stepSize = CenterSearchRadius / CenterSamples;

                for (int s = 1; s <= CenterSamples; s++)
                {
                    float dist = s * stepSize;

                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        Vector3 candidate = wp + perp * (dist * sign);

                        if (!NavMesh.SamplePosition(candidate, out NavMeshHit snap, 0.25f, AreaMask))
                            continue;

                        float cClear = MeasureClearance(snap.position);
                        if (cClear > bestClear)
                        {
                            bestClear = cClear;
                            bestShift = snap.position - wp;
                        }
                    }
                }

                if (bestShift.sqrMagnitude < 0.0001f) continue;

                if (bestShift.magnitude > MaxLateralShift)
                    bestShift = bestShift.normalized * MaxLateralShift;

                Vector3 newPos = wp + bestShift * CenterPullStrength;

                if (NavMesh.SamplePosition(newPos, out NavMeshHit finalHit, 0.20f, AreaMask))
                {
                    waypoints[i] = finalHit.position;
                    globalMin    = Mathf.Min(globalMin, MeasureClearance(finalHit.position));
                }
            }

            return globalMin < float.MaxValue ? globalMin : 0f;
        }

        /// <summary>
        /// Distancia real al borde del NavMesh usando FindClosestEdge.
        /// Más preciso y más rápido que raycasts radiales.
        /// </summary>
        private float MeasureClearance(Vector3 point)
        {
            if (NavMesh.FindClosestEdge(point, out NavMeshHit hit, AreaMask))
                return hit.distance;
            return 0f;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  FUNNEL CONSERVADOR
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Elimina waypoints casi colineales solo si:
        ///   - La línea directa a→c no cruza bordes del NavMesh.
        ///   - El punto medio a→c tiene clearance comparable al waypoint eliminado.
        ///
        /// Esto preserva waypoints necesarios en giros y zonas estrechas.
        /// </summary>
        private void ApplyConservativeFunnel(List<Vector3> waypoints)
        {
            if (waypoints.Count <= 2) return;

            int i = 0;
            while (i < waypoints.Count - 2)
            {
                Vector3 a  = waypoints[i];
                Vector3 b  = waypoints[i + 1];
                Vector3 c  = waypoints[i + 2];
                Vector3 ab = b - a; ab.y = 0f;
                Vector3 bc = c - b; bc.y = 0f;

                float angle = ab.sqrMagnitude > 0.001f && bc.sqrMagnitude > 0.001f
                    ? Vector3.Angle(ab.normalized, bc.normalized)
                    : 180f;

                if (angle <= FunnelAngleThreshold && !NavMesh.Raycast(a, c, out _, AreaMask))
                {
                    float clearB   = MeasureClearance(b);
                    float clearMid = MeasureClearance((a + c) * 0.5f);

                    // Solo eliminar si no perdemos clearance significativa
                    if (clearMid >= clearB * 0.8f)
                    {
                        waypoints.RemoveAt(i + 1);
                        continue;
                    }
                }
                i++;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  UTILIDADES
        // ─────────────────────────────────────────────────────────────────────

        private bool SnapToNavMesh(Vector3 position, out Vector3 snapped)
        {
            if (NavMesh.SamplePosition(position, out NavMeshHit hit, NavMeshSnapRadius, AreaMask))
            {
                snapped = hit.position;
                return true;
            }
            snapped = position;
            return false;
        }

        private float ComputeDeviationFromPath(Vector3 agentPos, IReadOnlyList<Vector3> waypoints)
        {
            if (waypoints == null || waypoints.Count < 2) return float.MaxValue;
            float minDist = float.MaxValue;
            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                float d = PointToSegDistXZ(agentPos, waypoints[i], waypoints[i + 1]);
                if (d < minDist) minDist = d;
            }
            return minDist < float.MaxValue ? minDist : 0f;
        }

        private static float PointToSegDistXZ(Vector3 pt, Vector3 a, Vector3 b)
        {
            Vector2 p   = new(pt.x, pt.z);
            Vector2 p1  = new(a.x, a.z);
            Vector2 p2  = new(b.x, b.z);
            Vector2 seg = p2 - p1;
            float lenSq = seg.sqrMagnitude;
            if (lenSq < 0.0001f) return Vector2.Distance(p, p1);
            float t = Mathf.Clamp01(Vector2.Dot(p - p1, seg) / lenSq);
            return Vector2.Distance(p, p1 + t * seg);
        }

        private long ComputeHash(Vector3 pos)
        {
            int xi = Mathf.RoundToInt(pos.x / HashCellSize);
            int yi = Mathf.RoundToInt(pos.y / HashCellSize);
            int zi = Mathf.RoundToInt(pos.z / HashCellSize);
            unchecked
            {
                long hash = -3750763034362895579L;
                hash = (hash ^ xi) * 1099511628211L;
                hash = (hash ^ yi) * 1099511628211L;
                hash = (hash ^ zi) * 1099511628211L;
                return hash;
            }
        }

        private OptimizedPath InvalidPath() =>
            new OptimizedPath(new List<Vector3>(), NavMeshPathStatus.PathInvalid, 0, 0f);
    }
}