// File: NavMeshBakingStats.cs
using System;
using UnityEngine;

namespace IndoorNavAR.Navigation
{
    [Serializable]
    public sealed class NavMeshBakingStats
    {
        public float BakingTime;
        public int   VertexCount;
        public int   TriangleCount;
        public float Area;

        public bool IsValid => VertexCount > 0 && Area > 0f;

        public override string ToString() =>
            $"BakingStats(t={BakingTime:F0}ms, verts={VertexCount}, tris={TriangleCount}, area={Area:F2}m²)";
    }
}