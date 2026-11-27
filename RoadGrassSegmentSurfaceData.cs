using System;
using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    // One surface per placed edge and logical median ordinal. No endpoint or
    // generated mesh entity is part of the durable identity.
    [InternalBufferCapacity(0)]
    public struct RoadGrassSavedSegmentSurface : IBufferElementData, ISerializable
    {
        internal Entity Edge;
        internal RoadGrassMedianAnchorData Median;
        internal MedianSavedSurface Surface;
        internal PolygonTerrainLayer TerrainLayer;
        internal float TextureTiling; // Zero keeps legacy records at 1x.
        internal float EffectiveTextureTiling => TextureTiling > 0 ? TextureTiling : 1;
        internal (Entity, int) Key => (Edge, Median.Ordinal);

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            float tiling = TerrainLayer == PolygonTerrainLayer.None ? 1 : EffectiveTextureTiling;
            writer.Write(tiling != 1 ? 3 : TerrainLayer == PolygonTerrainLayer.None ? 1 : 2);
            writer.Write(Edge);
            // Normalize the legacy anchor-shaped identity to segment space.
            var identity = Median;
            identity.End = false;
            identity.Serialize(writer);
            writer.Write((int)Surface);
            if (TerrainLayer != PolygonTerrainLayer.None) writer.Write((int)TerrainLayer);
            if (tiling != 1) writer.Write(tiling);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            if (version < 1 || version > 3) throw new InvalidOperationException("Unsupported Median Controller segment surface version");
            reader.Read(out Edge);
            Median.Deserialize(reader);
            Median.End = false;
            reader.Read(out int surface);
            Surface = surface >= 1 && surface <= 2 ? (MedianSavedSurface)surface : MedianSavedSurface.None;
            TerrainLayer = PolygonTerrainLayer.None;
            if (version >= 2)
            {
                reader.Read(out int layer);
                if (Surface == MedianSavedSurface.Grass && layer != 0 && MedianTextureIdentity.IsValid(layer)) TerrainLayer = (PolygonTerrainLayer)layer;
            }
            TextureTiling = 0;
            if (version >= 3)
            {
                reader.Read(out float tiling);
                if (TerrainLayer != PolygonTerrainLayer.None && math.isfinite(tiling) && tiling > 0)
                    TextureTiling = math.clamp(tiling, .25f, 4);
            }
        }
    }
}
