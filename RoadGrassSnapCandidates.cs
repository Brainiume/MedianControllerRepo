using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace RoadGrass
{
    internal static class RoadGrassSnapCandidates
    {
        internal static IEnumerable<int2> Pairs(IReadOnlyList<Line3.Segment> lines, float cellSize = 8)
        {
            var cells = new Dictionary<int2, List<int>>();
            var lower = new int2[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                lower[i] = (int2)math.floor(math.min(lines[i].a.xz, lines[i].b.xz) / cellSize);
                int2 upper = (int2)math.floor(math.max(lines[i].a.xz, lines[i].b.xz) / cellSize);
                for (int x = lower[i].x; x <= upper.x; x++) for (int y = lower[i].y; y <= upper.y; y++)
                {
                    var key = new int2(x, y);
                    if (!cells.TryGetValue(key, out var members)) cells[key] = members = new List<int>();
                    members.Add(i);
                }
            }
            foreach (var cell in cells)
            {
                var members = cell.Value;
                for (int a = 0; a < members.Count; a++) for (int b = a + 1; b < members.Count; b++)
                {
                    int i = members[a], j = members[b];
                    // Emit in the first shared cell only; no pair set or duplicate work.
                    if (!math.all(math.max(lower[i], lower[j]) == cell.Key)) continue;
                    yield return new int2(i, j);
                }
            }
        }
    }
}
