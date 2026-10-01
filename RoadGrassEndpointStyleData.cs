using System;
using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    internal enum MedianSavedCap { None, Rounded, Straight }
    internal enum MedianSavedSurface { None, Grass, Plain }

    // Only the city buffer is serialized. The edge marker schedules invalidation.
    internal struct RoadGrassStyledEdge : IComponentData { }

    [InternalBufferCapacity(0)]
    public struct RoadGrassSavedEndpointStyle : IBufferElementData, ISerializable
    {
        internal Entity Edge;
        internal Entity Node;
        internal RoadGrassMedianAnchorData Anchor;
        internal MedianSavedCap Cap;
        internal MedianSavedSurface Surface;
        internal bool ExtendIntoIntersection;
        internal bool HasTaperEnd;
        internal float3 TaperEndOffset;
        internal RoadGrassMedianAnchorKey Key => new RoadGrassMedianAnchorKey(Edge, Anchor.End, Anchor.Ordinal);

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(3);
            writer.Write(Edge);
            writer.Write(Node);
            Anchor.Serialize(writer);
            writer.Write((int)Cap);
            writer.Write((int)Surface);
            writer.Write(ExtendIntoIntersection);
            writer.Write(HasTaperEnd);
            writer.Write(TaperEndOffset.x);
            writer.Write(TaperEndOffset.y);
            writer.Write(TaperEndOffset.z);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            if (version < 1 || version > 3) throw new InvalidOperationException("Unsupported Median Controller endpoint style version");
            reader.Read(out Edge);
            reader.Read(out Node);
            Anchor.Deserialize(reader);
            reader.Read(out int cap);
            reader.Read(out int surface);
            Cap = cap >= 0 && cap <= 2 ? (MedianSavedCap)cap : MedianSavedCap.None;
            Surface = surface >= 0 && surface <= 2 ? (MedianSavedSurface)surface : MedianSavedSurface.None;
            ExtendIntoIntersection = false;
            if (version >= 2) reader.Read(out ExtendIntoIntersection);
            HasTaperEnd = false;
            TaperEndOffset = default;
            if (version >= 3)
            {
                reader.Read(out HasTaperEnd);
                reader.Read(out TaperEndOffset.x);
                reader.Read(out TaperEndOffset.y);
                reader.Read(out TaperEndOffset.z);
                if (!math.all(math.isfinite(TaperEndOffset))) HasTaperEnd = false;
            }
        }
    }
}
