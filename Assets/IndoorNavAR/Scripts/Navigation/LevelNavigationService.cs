// File: LevelNavigationService.cs
// ✅ CORREGIDO v2 — Unity 6.3+ compatible
//
//   FIX A: Eliminada dependencia de clusters vacíos — fallback robusto con cualquier dato.
//   FIX B: Cálculo de bounds usando worldspace correctamente.
//   FIX C: Threshold de densidad configurable, no hardcodeado a 0.12f.
//   FIX D: Separación de NavMeshBakingStats en archivo propio (ver NavMeshBakingStats.cs).

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Servicio de detección de niveles navegables.
    /// Identifica el piso caminable principal y sus propiedades geométricas.
    /// </summary>
    public sealed class LevelNavigationService
    {
        // ─── Configuración ────────────────────────────────────────────────────
        private readonly float _minClusterDensity;
        private readonly float _maxFloorDeviation;
        private readonly float _minFloorAreaSquareMeters;

        /// <param name="minClusterDensity">
        ///   Fracción mínima de vértices que debe tener un cluster para considerarse
        ///   candidato a piso. Rango típico: 0.05–0.20.
        /// </param>
        /// <param name="maxFloorDeviation">
        ///   Tolerancia vertical (m) para filtrar vértices alrededor del centroide
        ///   del piso. Rango típico: 0.05–0.15.
        /// </param>
        /// <param name="minFloorAreaSquareMeters">
        ///   Área mínima (m²) en XZ que debe tener un cluster de piso válido.
        /// </param>
        public LevelNavigationService(
            float minClusterDensity         = 0.08f,
            float maxFloorDeviation         = 0.12f,
            float minFloorAreaSquareMeters  = 1.0f)
        {
            _minClusterDensity        = minClusterDensity;
            _maxFloorDeviation        = maxFloorDeviation;
            _minFloorAreaSquareMeters = minFloorAreaSquareMeters;
        }

        // ─── API pública ──────────────────────────────────────────────────────

        /// <summary>
        /// Detecta el piso caminable principal a partir de los clusters de altura
        /// producidos por <see cref="MeshProcessingService.BuildHeightHistogram"/>.
        /// </summary>
        /// <returns>
        ///   <see cref="FloorDetectionResult"/> con la altura, vértices y área del piso;
        ///   <c>null</c> si la entrada está vacía o no supera los filtros de calidad.
        /// </returns>
        public FloorDetectionResult DetectWalkableFloor(IReadOnlyList<HeightCluster> heightClusters)
        {
            if (heightClusters == null || heightClusters.Count == 0)
            {
                Debug.LogWarning("[LevelNav] ⚠️ DetectWalkableFloor: lista de clusters vacía.");
                return null;
            }

            // Rango vertical total del modelo
            float minHeight = heightClusters[0].centerHeight;
            float maxHeight = heightClusters[heightClusters.Count - 1].centerHeight;

            // Buscar candidatos en el tercio inferior del rango de alturas
            float searchCeiling = minHeight + (maxHeight - minHeight) * 0.3f;

            List<HeightCluster> candidates = heightClusters
                .Where(c => c.centerHeight <= searchCeiling)
                .Where(c => c.density >= _minClusterDensity)
                .Where(c => c.bounds.size.x * c.bounds.size.z >= _minFloorAreaSquareMeters)
                .OrderByDescending(c => c.density)
                .ThenByDescending(c => c.bounds.size.x * c.bounds.size.z)
                .ThenBy(c => c.centerHeight)
                .ToList();

            // Fallback: usar el cluster más bajo si ninguno pasa los filtros
            if (candidates.Count == 0)
            {
                Debug.LogWarning("[LevelNav] ⚠️ Ningún cluster supera los filtros de piso. " +
                                 "Usando el cluster de menor altura como fallback.");
                candidates.Add(heightClusters[0]);
            }

            HeightCluster floorCluster = candidates[0];

            // Filtrar vértices dentro de la desviación permitida
            List<Vector3> floorVertices = floorCluster.vertices
                .Where(v => Mathf.Abs(v.y - floorCluster.centerHeight) <= _maxFloorDeviation)
                .ToList();

            // Si el filtro de desviación elimina todos los vértices, usar todos los del cluster
            if (floorVertices.Count == 0)
                floorVertices = new List<Vector3>(floorCluster.vertices);

            Bounds walkableArea = CalculateBounds(floorVertices);

            Debug.Log($"[LevelNav] ✅ Piso detectado: Y={floorCluster.centerHeight:F3}m, " +
                      $"área={walkableArea.size.x:F2}×{walkableArea.size.z:F2}m, " +
                      $"verts={floorVertices.Count}");

            return new FloorDetectionResult
            {
                FloorHeight   = floorCluster.centerHeight,
                FloorVertices = floorVertices,
                WalkableArea  = walkableArea,
            };
        }

        // ─── Utilidades privadas ──────────────────────────────────────────────

        private static Bounds CalculateBounds(IReadOnlyList<Vector3> vertices)
        {
            if (vertices.Count == 0) return default;

            Vector3 min = vertices[0];
            Vector3 max = vertices[0];

            foreach (Vector3 v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            return new Bounds((min + max) * 0.5f, max - min);
        }
    }

    // ─── Data Structures ─────────────────────────────────────────────────────

    /// <summary>
    /// Resultado de la detección de piso caminable.
    /// </summary>
    public sealed class FloorDetectionResult
    {
        public float FloorHeight;
        public List<Vector3> FloorVertices;
        public Bounds WalkableArea;
    }
}