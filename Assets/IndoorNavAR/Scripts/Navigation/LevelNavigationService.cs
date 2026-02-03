// File: LevelNavigationService.cs

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace IndoorNavAR.Navigation
{
    /// <summary>
    /// Servicio de detección de niveles navegables: identificación de piso caminable
    /// </summary>
    public class LevelNavigationService
    {
        #region Floor Detection

        public FloorDetectionResult DetectWalkableFloor(List<HeightCluster> heightClusters)
        {
            if (heightClusters == null || heightClusters.Count == 0)
                return null;
            
            float minHeight = heightClusters[0].centerHeight;
            float maxHeight = heightClusters[heightClusters.Count - 1].centerHeight;
            float searchThreshold = minHeight + ((maxHeight - minHeight) * 0.3f);
            
            var candidates = heightClusters
                .Where(c => c.centerHeight <= searchThreshold)
                .Where(c => c.density >= 0.12f)
                .Where(c => c.bounds.size.x * c.bounds.size.z > 1f)
                .OrderByDescending(c => c.density)
                .ThenByDescending(c => c.bounds.size.x * c.bounds.size.z)
                .ThenBy(c => c.centerHeight)
                .ToList();
            
            if (candidates.Count == 0)
                candidates = new List<HeightCluster> { heightClusters[0] };
            
            HeightCluster floorCluster = candidates[0];
            
            float detectedFloorHeight = floorCluster.centerHeight;
            
            List<Vector3> floorVertices = floorCluster.vertices
                .Where(v => Mathf.Abs(v.y - floorCluster.centerHeight) <= 0.12f)
                .ToList();
            
            Bounds walkableArea = CalculateBounds(floorVertices);
            
            Debug.Log($"[NavAR] ✅ Piso detectado: Y={detectedFloorHeight:F3}m, área={walkableArea.size.x:F2}x{walkableArea.size.z:F2}m");
            
            return new FloorDetectionResult
            {
                FloorHeight = detectedFloorHeight,
                FloorVertices = floorVertices,
                WalkableArea = walkableArea
            };
        }

        private Bounds CalculateBounds(List<Vector3> vertices)
        {
            if (vertices.Count == 0) return new Bounds();
            
            Vector3 min = vertices[0];
            Vector3 max = vertices[0];
            
            foreach (Vector3 v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            
            return new Bounds((min + max) / 2f, max - min);
        }

        #endregion
    }

    #region Data Structures

    public class FloorDetectionResult
    {
        public float FloorHeight;
        public List<Vector3> FloorVertices;
        public Bounds WalkableArea;
    }

    #endregion
}