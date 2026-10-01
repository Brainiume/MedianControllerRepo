using System;
using Colossal.Serialization.Entities;
using Unity.Entities;

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
        internal (Entity, int) Key => (Edge, Median.Ordinal);

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(1);
            writer.Write(Edge);
            // Normalize the legacy anchor-shaped identity to segment space.
            var identity = Median;
            identity.End = false;
            identity.Serialize(writer);
            writer.Write((int)Surface);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            if (version != 1) throw new InvalidOperationException("Unsupported Median Controller segment surface version");
            reader.Read(out Edge);
            Median.Deserialize(reader);
            Median.End = false;
            reader.Read(out int surface);
            Surface = surface >= 1 && surface <= 2 ? (MedianSavedSurface)surface : MedianSavedSurface.None;
        }
    }
}
