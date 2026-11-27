using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Game.Prefabs;
using Unity.Entities;

namespace RoadGrass
{
    // Renderer resource only. Shape and ownership remain in the existing city/ECS records.
    internal static class RoadGrassPolygonPrototypeMesh
    {
        internal struct Profile
        {
            internal float2 Insets, CurbU;
            internal float CurbWidth, Height, GrassHeight, BaseHeight, TextureScale, TopInset, CurbV, AttachmentWidth;
            internal List<CurbVertex> Section;
            internal float2 PavementU, PavementScale;
            internal float TerrainScale;
            internal float InteriorTiling;
            internal Entity PavementPiece, CurbPiece;
            internal int PavementMaterial, CurbMaterial;
        }

        internal struct CurbVertex
        {
            internal float Depth, Height;
            internal float2 UV;
        }

        internal static Profile WithPavementFill(Profile curb, Profile fill)
        {
            curb.PavementU = fill.PavementU; curb.PavementScale = fill.PavementScale;
            curb.PavementPiece = fill.PavementPiece; curb.PavementMaterial = fill.PavementMaterial;
            return curb;
        }

        internal static bool HasPhysicalPieces(RoadGrassResolvedMedianAnchor source) => source.Pieces.Exists(IsPhysicalPiece);
        private static bool IsPhysicalPiece(NetCompositionPiece piece) =>
            (piece.m_PieceFlags & NetPieceFlags.HasMesh) != 0 &&
            (piece.m_PieceFlags & (NetPieceFlags.PreserveShape | NetPieceFlags.BlockTraffic | NetPieceFlags.BlockCrosswalk)) != 0;
        internal static bool IncludePiece(NetCompositionPiece piece, bool physical) =>
            (!physical || IsPhysicalPiece(piece)) && (piece.m_PieceFlags & NetPieceFlags.Bottom) == 0 &&
            (piece.m_SectionFlags & (NetSectionFlags.Hidden | NetSectionFlags.Underground | NetSectionFlags.Overhead)) == 0;

        private struct TopFace
        {
            internal float Low, High, Height, OuterU, InnerU, VScale;
            internal Entity Piece;
            internal int Material;
        }

        internal static Profile ReadProfile(EntityManager manager, PrefabSystem prefabs, RoadGrassResolvedMedianAnchor source)
        {
            float2 borders = RoadGrassTransitionMesh.ReadBorderWidths(manager, source);
            var result = new Profile { CurbWidth = math.max(.03f, math.csum(borders) * .5f),
                Height = .15f, GrassHeight = .15f, BaseHeight = -.15f, CurbU = new float2(0, 1), TextureScale = 1 };
            if (math.csum(borders) < .001f) result.CurbWidth = .12f;
            var samples = new List<float4>(); // lateral position, height, source U, longitudinal position
            var mapping = new List<float2>(); // longitudinal position, source V
            var sectionPoints = new List<float4>(); // lateral position, height, longitudinal position, lateral normal
            var sectionUV = new List<float2>();
            var surfaceLevels = new List<float>();
            var faces = new List<TopFace>();
            bool physical = HasPhysicalPieces(source);
            float low = float.MaxValue, high = float.MinValue, top = float.MinValue;
            float grassLow = float.MaxValue, grassHigh = float.MinValue;
            float grassTop = float.MinValue;
            foreach (var piece in source.Pieces)
            {
                if (!IncludePiece(piece, physical)) continue;
                if (manager.HasComponent<NetPieceData>(piece.m_Piece))
                {
                    float height = manager.GetComponentData<NetPieceData>(piece.m_Piece).m_HeightRange.max + piece.m_Offset.y;
                    if (height >= .01f && height <= 2) result.Height = math.max(result.Height, height);
                }
                if (!prefabs.TryGetPrefab<RenderPrefab>(piece.m_Piece, out var prefab) || prefab.geometryAsset == null) continue;
                bool inverted = (piece.m_SectionFlags & NetSectionFlags.Invert) != 0;
                // Source assets survive removal of the temporary ECS mesh buffers.
                var meshes = prefab.geometryAsset.ObtainMeshes(true);
                try
                {
                    int material = 0;
                    foreach (var mesh in meshes)
                    {
                        var vertices = mesh.vertices;
                        var uv = new List<Vector4>(); mesh.GetUVs(0, uv);
                        var normals = mesh.normals;
                        for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++, material++)
                        {
                            var surface = prefab.GetSurfaceAsset(material);
                            if (surface == null) continue;
                            bool isGrass = surface.name.IndexOf("grass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                surface.name.IndexOf("lawn", StringComparison.OrdinalIgnoreCase) >= 0;
                            var indices = mesh.GetIndices(subMesh);
                            if (!isGrass)
                            for (int t = 0; t + 2 < indices.Length; t += 3)
                            {
                                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                                if ((uint)a >= uv.Count || (uint)b >= uv.Count || (uint)c >= uv.Count ||
                                    (uint)a >= normals.Length || (uint)b >= normals.Length || (uint)c >= normals.Length ||
                                    normals[a].y < .9f || normals[b].y < .9f || normals[c].y < .9f) continue;
                                float3 pa = vertices[a], pb = vertices[b], pc = vertices[c];
                                if (math.abs(pa.y - pb.y) > .001f || math.abs(pa.y - pc.y) > .001f) continue;
                                float X(float3 p) => (inverted ? p.x : -p.x) + piece.m_Offset.x;
                                float xa = X(pa), xb = X(pb), xc = X(pc);
                                float lo = math.min(xa, math.min(xb, xc)), hi = math.max(xa, math.max(xb, xc));
                                if (hi - lo < .001f || lo < -source.Width * .5f - .5f || hi > source.Width * .5f + .5f) continue;
                                int left = xa <= xb && xa <= xc ? a : xb <= xc ? b : c;
                                int right = xa >= xb && xa >= xc ? a : xb >= xc ? b : c;
                                float zSpan = math.max(pa.z, math.max(pb.z, pc.z)) - math.min(pa.z, math.min(pb.z, pc.z));
                                float vSpan = math.max(uv[a].y, math.max(uv[b].y, uv[c].y)) - math.min(uv[a].y, math.min(uv[b].y, uv[c].y));
                                faces.Add(new TopFace { Low = lo, High = hi, Height = pa.y + piece.m_Offset.y,
                                    OuterU = uv[left].x, InnerU = uv[right].x, VScale = zSpan > .001f ? vSpan / zSpan : 0,
                                    Piece = piece.m_Piece, Material = material });
                            }
                            var visited = new HashSet<int>();
                            foreach (int index in indices)
                            {
                                if ((uint)index >= vertices.Length || (uint)index >= uv.Count || !visited.Add(index)) continue;
                                float3 p = vertices[index];
                                float y = p.y + piece.m_Offset.y;
                                // Native road surfaces can be below zero; a zero-height crown is valid.
                                float x = (inverted ? p.x : -p.x) + piece.m_Offset.x;
                                if (!math.isfinite(x) || math.abs(x) > source.Width * .5f + .5f || y < -4 || y > 2) continue;
                                if (isGrass)
                                {
                                    grassLow = math.min(grassLow, x); grassHigh = math.max(grassHigh, x);
                                    grassTop = math.max(grassTop, y);
                                    continue;
                                }
                                float normalX = index < normals.Length ? normals[index].x : 0;
                                if (index < normals.Length && normals[index].y > .9f) surfaceLevels.Add(y);
                                float z = ((piece.m_SectionFlags & NetSectionFlags.FlipMesh) != 0 ? p.z : -p.z) + piece.m_Offset.z;
                                sectionPoints.Add(new float4(x, y, z, inverted ? normalX : -normalX));
                                sectionUV.Add(new float2(uv[index].x, uv[index].y));
                                top = math.max(top, y);
                                samples.Add(new float4(x, y, uv[index].x, z));
                                mapping.Add(new float2(z, uv[index].y));
                            }
                        }
                    }
                }
                finally { prefab.geometryAsset.ReleaseMeshes(); }
            }
            if (samples.Count == 0) return result;
            foreach (var sample in samples)
                if (sample.y >= top - .01f) { low = math.min(low, sample.x); high = math.max(high, sample.x); }
            if (high - low < .01f) return result;
            float floor = math.min(top - .01f, source.RoadComposition.m_SurfaceHeight.min);
            // The network projection uses the native composition datum, not the road/apron foot.
            // Retain its authored offset separately from the crown-to-foot thickness.
            float roadLevel = float.MinValue;
            foreach (float level in surfaceLevels)
                if (level < top - .02f && level >= top - .5f) roadLevel = math.max(roadLevel, level);
            if (roadLevel != float.MinValue) floor = roadLevel;
            result.Insets = math.max(0, new float2(low + source.Width * .5f, source.Width * .5f - high));
            if (math.csum(borders) >= .001f)
                result.CurbWidth = math.max(.03f, math.csum(math.max(0, borders - result.Insets)) * .5f);
            if (grassHigh > grassLow && grassLow > low && grassHigh < high)
                result.CurbWidth = math.max(.03f, ((grassLow - low) + (high - grassHigh)) * .5f);
            result.Height = top - floor;
            result.BaseHeight = floor;
            result.GrassHeight = grassTop >= floor && grassTop <= top ? grassTop - floor : result.Height;
            float outerError = float.MaxValue, innerError = float.MaxValue;
            foreach (var sample in samples)
            {
                if (sample.y < top - .01f) continue;
                float outer = math.abs(sample.x - low), inner = math.abs(sample.x - low - result.CurbWidth);
                if (outer < outerError) { outerError = outer; result.CurbU.x = sample.z; }
                if (inner < innerError) { innerError = inner; result.CurbU.y = sample.z; }
            }
            int first = -1, last = -1;
            for (int i = 0; i < mapping.Count; i++)
            {
                if (samples[i].y < top - .01f || math.abs(samples[i].x - low) > outerError + .001f) continue;
                if (first < 0 || mapping[i].x < mapping[first].x) first = i;
                if (last < 0 || mapping[i].x > mapping[last].x) last = i;
            }
            if (first >= 0 && last >= 0)
            {
                float distance = mapping[last].x - mapping[first].x;
                if (distance > .001f) result.TextureScale = (mapping[last].y - mapping[first].y) / distance;
            }
            float station = float.MaxValue;
            foreach (var p in sectionPoints)
                if (p.x >= low - .1f && p.x <= low + result.CurbWidth + .001f && p.y >= top - .01f)
                    station = math.min(station, p.z);
            var section = new List<CurbVertex>();
            for (int i = 0; i < sectionPoints.Count; i++)
            {
                var p = sectionPoints[i];
                if (math.abs(p.z - station) > .001f || p.x < low - .1f || p.x > low + result.CurbWidth + .001f ||
                    p.y < floor - .001f || (p.w >= -.001f && p.y < top - .01f)) continue;
                section.Add(new CurbVertex { Depth = p.x, Height = p.y, UV = sectionUV[i] });
            }
            section.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : a.Height.CompareTo(b.Height));
            if (section.Count >= 2)
            {
                float foot = section[0].Depth;
                var profile = new List<CurbVertex>();
                foreach (var vertex in section)
                {
                    var p = vertex; p.Depth -= foot;
                    p.Height -= floor;
                    if (profile.Count > 0)
                    {
                        var previous = profile[profile.Count - 1];
                        if (math.abs(p.Depth - previous.Depth) < .00001f && math.abs(p.Height - previous.Height) < .00001f) continue;
                    }
                    profile.Add(p);
                    if (p.Height >= result.Height - .001f) break;
                }
                if (profile.Count >= 2 && profile[profile.Count - 1].Height >= result.Height - .001f)
                {
                    var shoulder = profile[profile.Count - 1];
                    result.Section = profile;
                    result.TopInset = shoulder.Depth;
                    result.CurbU.x = shoulder.UV.x;
                    result.CurbV = shoulder.UV.y;
                    float bevel = math.max(0, low - foot);
                    float rightFoot = high;
                    foreach (var p in sectionPoints)
                        if (p.w > .001f && p.y >= floor - .001f && p.x >= high - result.CurbWidth && p.x <= high + .1f)
                            rightFoot = math.max(rightFoot, p.x);
                    result.Insets = math.max(0, new float2(foot + source.Width * .5f, source.Width * .5f - rightFoot));
                    float nativeWidth = grassHigh > grassLow ? result.CurbWidth + bevel : math.csum(borders) < .001f ? result.CurbWidth + bevel :
                        math.csum(math.max(0, borders - result.Insets)) * .5f;
                    result.CurbWidth = math.max(shoulder.Depth + .01f, nativeWidth);
                }
            }
            return SelectSurfaceFaces(result, faces, low, top);
        }

        private static Profile SelectSurfaceFaces(Profile result, List<TopFace> faces, float low, float top)
        {
            // Select connected crown faces, not coincident UV vertices from the pavement/apron.
            float fillWidth = 0, curbError = float.MaxValue;
            foreach (var face in faces)
            {
                if (face.Height < top - .01f) continue;
                float width = face.High - face.Low;
                if (width > result.CurbWidth * 1.5f && width > fillWidth)
                {
                    fillWidth = width;
                    result.PavementU = new float2(math.min(face.OuterU, face.InnerU), math.max(face.OuterU, face.InnerU));
                    result.PavementScale = new float2(1 / width, face.VScale);
                    result.PavementPiece = face.Piece; result.PavementMaterial = face.Material;
                }
                float error = math.abs(face.Low - low);
                if (width <= result.CurbWidth * 1.5f && error < curbError)
                {
                    curbError = error;
                    result.CurbU = new float2(face.OuterU, face.InnerU);
                    result.CurbPiece = face.Piece; result.CurbMaterial = face.Material;
                }
            }
            return result;
        }
        internal static float2 MapPavement(float3 point, float3 origin) => (point - origin).xz;

        internal static Mesh Build(IReadOnlyList<RoadGrassPolygonShape.Loop> loops, float3 origin,
            Profile profile, Func<float3, float3> project, bool useGrass)
        {
            float curbWidth = profile.CurbWidth, height = profile.Height, textureScale = profile.TextureScale;
            float2 curbU = profile.CurbU;
            var vertices = new List<Vector3>(); var uv = new List<Vector2>();
            var topNormals = new Dictionary<Vector3, float3>();
            var topVertices = new List<int>();
            var pavement = new List<int>(); var grass = new List<int>();
            IReadOnlyList<float3> boundary = null;
            foreach (var loop in loops)
            {
                boundary = loop.Points;
                var edgeDistances = new float[loop.Points.Count + 1];
                for (int i = 0; i < loop.Points.Count; i++)
                    edgeDistances[i + 1] = edgeDistances[i] + math.distance(loop.Points[i].xz,
                        loop.Points[(i + 1) % loop.Points.Count].xz);
                var wallLoops = new List<RoadGrassPolygonShape.Loop>();
                if (profile.Section != null)
                    foreach (var vertex in profile.Section)
                    {
                        var wall = RoadGrassPolygonShape.Offset(loop, vertex.Depth, 0);
                        PinSeam(wall, vertex.Depth);
                        wallLoops.Add(wall);
                    }
                var interiors = RoadGrassPolygonShape.Build(loop.Points, loop.Opening, new float4(curbWidth, 0, 0, 0));
                var tops = RoadGrassPolygonShape.Build(loop.Points, loop.Opening, new float4(profile.TopInset, 0, 0, 0));
                foreach (var inner in interiors) PinSeam(inner, curbWidth);
                foreach (var top in tops) PinSeam(top, profile.TopInset);
                void PinSeam(RoadGrassPolygonShape.Loop contour, float inset)
                {
                    if (profile.AttachmentWidth <= 0) return;
                    for (int i = 0; i < loop.Points.Count; i++)
                    {
                        if (!loop.Opening[i] || RoadGrassPolygonShape.IsBridge(loop,i)) continue;
                        RoadGrassPolygonShape.PinOpening(contour, loop.Points[i], loop.Points[(i + 1) % loop.Points.Count],
                            profile.AttachmentWidth, new float2(inset));
                        break;
                    }
                }
                if (interiors.Count == 0)
                {
                    foreach (var surface in tops) Fill(surface.Points, useGrass ? pavement : grass, 0, !useGrass);
                }
                else
                {
                    // Doubled bridges cancel under even-odd filling; the curb ring excludes the fill.
                    var ring = new List<float3>();
                    foreach (var surface in tops)
                    {
                        ring.AddRange(surface.Points);
                        ring.Add(surface.Points[0]);
                        ring.Add(loop.Points[0]);
                    }
                    foreach (var inner in interiors)
                    {
                        ring.AddRange(inner.Points);
                        ring.Add(inner.Points[0]);
                        ring.Add(loop.Points[0]);
                        Fill(inner.Points, grass,
                            useGrass ? profile.GrassHeight - height : 0, !useGrass);
                    }
                    if (tops.Count == 1 && interiors.Count == 1 &&
                        tops[0].Points.Count == loop.Points.Count && interiors[0].Points.Count == loop.Points.Count)
                    {
                        // Each curb edge owns its UV basis. Generic ring triangulation can span
                        // two nearest-edge UV bases, producing stretched/striped corner triangles.
                        var outer = tops[0].Points; var inner = interiors[0].Points;
                        for (int edge = 0; edge < loop.Points.Count; edge++)
                        {
                            if (loop.Opening[edge]) continue;
                            int next = (edge + 1) % loop.Points.Count;
                            int rows = math.max(Samples(outer[edge], outer[next]), Samples(inner[edge], inner[next]));
                            float2 tangent = math.normalizesafe(loop.Points[next].xz - loop.Points[edge].xz);
                            for (int row = 0; row < rows; row++)
                            {
                                float t0 = row / (float)rows, t1 = (row + 1f) / rows;
                                float3 a = math.lerp(outer[edge], outer[next], t0), b = math.lerp(outer[edge], outer[next], t1),
                                    c = math.lerp(inner[edge], inner[next], t1), d = math.lerp(inner[edge], inner[next], t0);
                                float3 lift = new float3(0, height, 0);
                                Quad(project(a) + lift, project(b) + lift, project(c) + lift, project(d) + lift,
                                    pavement, true, Map(a, curbU.x), Map(b, curbU.x), Map(c, curbU.y), Map(d, curbU.y));
                            }
                            float2 Map(float3 p, float u) => new float2(u, profile.CurbV +
                                (edgeDistances[edge] + math.dot(p.xz - loop.Points[edge].xz, tangent)) * textureScale);
                        }
                    }
                    else if (tops.Count > 0) Fill(ring, pavement, 0);
                }
                float signedArea = 0;
                for (int i = 0; i < loop.Points.Count; i++)
                {
                    float3 a = loop.Points[i] - loop.Points[0], b = loop.Points[(i + 1) % loop.Points.Count] - loop.Points[0];
                    signedArea += a.x * b.z - a.z * b.x;
                }
                for (int i = 0; i < loop.Points.Count; i++)
                {
                    if (loop.Opening[i]) continue;
                    float3 start = loop.Points[i], end = loop.Points[(i + 1) % loop.Points.Count];
                    int samples = Samples(start, end);
                    for (int j = 0; j < samples; j++)
                    {
                        float3 a = project(math.lerp(start, end, j / (float)samples)),
                            b = project(math.lerp(start, end, (j + 1f) / samples));
                        if (profile.Section == null)
                        {
                            float3 highA = a + new float3(0, height, 0), highB = b + new float3(0, height, 0);
                            if (signedArea >= 0) Quad(b, a, highA, highB, pavement, false);
                            else Quad(a, b, highB, highA, pavement, false);
                        }
                        else
                        {
                            float alongA = edgeDistances[i] + math.distance(start.xz, a.xz),
                                alongB = edgeDistances[i] + math.distance(start.xz, b.xz);
                            for (int section = 1; section < profile.Section.Count; section++)
                            {
                                var low = profile.Section[section - 1]; var high = profile.Section[section];
                                float3 lowA = AtSection(section - 1, j / (float)samples), lowB = AtSection(section - 1, (j + 1f) / samples);
                                float3 highA = AtSection(section, j / (float)samples), highB = AtSection(section, (j + 1f) / samples);
                                float2 lowUVa = low.UV + new float2(0, alongA * textureScale), lowUVb = low.UV + new float2(0, alongB * textureScale);
                                float2 highUVa = high.UV + new float2(0, alongA * textureScale), highUVb = high.UV + new float2(0, alongB * textureScale);
                                if (signedArea >= 0) Quad(lowB, lowA, highA, highB, pavement, false, lowUVb, lowUVa, highUVa, highUVb);
                                else Quad(lowA, lowB, highB, highA, pavement, false, lowUVa, lowUVb, highUVb, highUVa);

                                float3 AtSection(int profileIndex, float t)
                                {
                                    var inset = wallLoops[profileIndex];
                                    var p = math.lerp(inset.Points[i], inset.Points[(i + 1) % inset.Points.Count], t);
                                    return project(p) + new float3(0, profile.Section[profileIndex].Height, 0);
                                }
                            }
                        }
                    }
                }
            }
            if (vertices.Count == 0) return null;
            var mesh = new Mesh { name = "Median Controller polygon prototype", hideFlags = HideFlags.DontSave,
                indexFormat = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.subMeshCount = 2;
                mesh.SetTriangles(pavement, 0); mesh.SetTriangles(grass, 1);
                mesh.RecalculateNormals();
                var normals = mesh.normals;
                foreach (int i in topVertices)
                    if (topNormals.TryGetValue(vertices[i], out var normal)) normals[i] = (Vector3)math.normalizesafe(normal, new float3(0, 1, 0));
                mesh.SetNormals(normals); mesh.RecalculateTangents(); mesh.RecalculateBounds();
                return mesh;
            }
            catch { UnityEngine.Object.Destroy(mesh); throw; }

            void Fill(IReadOnlyList<float3> points, List<int> indices, float lift, bool planar = false)
            {
                if (!RoadGrassPolygonSections.TryBuild(points, out var sections, out var reason,
                    maxPoints: points.Count, maxSections: int.MaxValue))
                    throw new InvalidOperationException(reason);
                foreach (var section in sections)
                {
                    float3 elevation = new float3(0, height + lift, 0);
                    int rows = math.max(Samples(section.Left.a, section.Left.d), Samples(section.Right.a, section.Right.d));
                    int columns = math.max(Samples(section.Left.a, section.Right.a), Samples(section.Left.d, section.Right.d));
                    var grid = new float3[(rows + 1) * (columns + 1)];
                    for (int row = 0; row <= rows; row++)
                    for (int column = 0; column <= columns; column++)
                        grid[row * (columns + 1) + column] = project(math.lerp(
                            math.lerp(section.Left.a, section.Left.d, row / (float)rows),
                            math.lerp(section.Right.a, section.Right.d, row / (float)rows), column / (float)columns)) + elevation;
                    for (int row = 0; row < rows; row++)
                    for (int column = 0; column < columns; column++)
                    {
                        float3 At(int x, int y) => grid[y * (columns + 1) + x];
                        float3 a = At(column, row), b = At(column + 1, row), c = At(column + 1, row + 1), d = At(column, row + 1);
                        if (planar) Quad(a, b, c, d, indices, true, MapPavement(a, origin) * profile.PavementScale,
                            MapPavement(b, origin) * profile.PavementScale, MapPavement(c, origin) * profile.PavementScale,
                            MapPavement(d, origin) * profile.PavementScale);
                        else Quad(a, b, c, d, indices, true);
                    }
                }
            }
            // Sample the road grade throughout the island, including narrow tips, rather than flattening each large section.
            // ponytail: one-metre samples capped at 128 per span; raise the cap if very large islands need finer grade following.
            int Samples(float3 a, float3 b) => math.clamp((int)math.ceil(math.distance(a.xz, b.xz)), 1, 128);
            void Quad(float3 a, float3 b, float3 c, float3 d, List<int> indices, bool top,
                float2? uvA = null, float2? uvB = null, float2? uvC = null, float2? uvD = null)
            {
                int first = vertices.Count;
                Vertex(a, uvA); Vertex(b, uvB); Vertex(c, uvC); Vertex(d, uvD);
                Triangle(0,1,2); Triangle(0,2,3);
                void Vertex(float3 point, float2? mapped)
                {
                    if (!math.all(math.isfinite(point))) throw new InvalidOperationException("Polygon mesh contains a non-finite vertex");
                    vertices.Add((Vector3)(point - origin));
                    float tiling = profile.InteriorTiling > 0 ? profile.InteriorTiling : 1;
                    if (mapped.HasValue) uv.Add(mapped.Value * (indices != pavement ? tiling : 1));
                    else if (indices != pavement) uv.Add(new Vector2(point.x, point.z) * (profile.TerrainScale > 0 ? profile.TerrainScale : 1) * tiling);
                    else
                    {
                    float best = float.MaxValue, along = 0, perimeter = 0;
                        for (int edge = 0; edge < boundary.Count; edge++)
                        {
                            float2 start = boundary[edge].xz, delta = boundary[(edge + 1) % boundary.Count].xz - start;
                            float length = math.length(delta);
                            float t = math.saturate(math.dot(point.xz - start, delta) / math.max(.000001f, length * length));
                            float distance = math.distance(point.xz, start + t * delta);
                        if (distance < best) { best = distance; along = perimeter + t * length; }
                        perimeter += length;
                        }
                    uv.Add(new Vector2(math.lerp(curbU.x, curbU.y,
                        math.saturate((best - profile.TopInset) / math.max(.001f, curbWidth - profile.TopInset))),
                        profile.CurbV + along * textureScale));
                    }
                }
                void Triangle(int x, int y, int z)
                {
                    float3 normal = math.cross((float3)vertices[first+y] - (float3)vertices[first+x], (float3)vertices[first+z] - (float3)vertices[first+x]);
                    if (math.lengthsq(normal) < 1e-10f) return;
                    // At a pointed end one triangle can collapse. Its winding cannot determine the other triangle's front face.
                    bool reverse = top && normal.y < 0;
                    if (top)
                    {
                        if (reverse) normal = -normal;
                        AddNormal(first+x); AddNormal(first+y); AddNormal(first+z);
                        void AddNormal(int i)
                        {
                            topVertices.Add(i);
                            topNormals.TryGetValue(vertices[i], out var previous);
                            topNormals[vertices[i]] = previous + normal;
                        }
                    }
                    indices.Add(first+x); indices.Add(first+(reverse ? z : y)); indices.Add(first+(reverse ? y : z));
                }
            }
        }
    }
}
