using System;
using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    [InternalBufferCapacity(0)]
    public struct RoadGrassSavedManualMedian : IBufferElementData, ISerializable
    {
        internal int Id;
        internal Entity Node, Edge;
        internal RoadGrassMedianAnchorData Anchor;
        internal MedianSavedSurface Surface;
        internal float4 Shape; // line/median offset, line/median radius
        internal RoadGrassMedianAnchorKey Key => new RoadGrassMedianAnchorKey(Edge, Anchor.End, Anchor.Ordinal);
        public void Serialize<T>(T writer) where T : IWriter
        {
            writer.Write(2); writer.Write(Id); writer.Write(Node); writer.Write(Edge);
            Anchor.Serialize(writer); writer.Write((int)Surface);
            writer.Write(Shape.x); writer.Write(Shape.y); writer.Write(Shape.z); writer.Write(Shape.w);
        }
        public void Deserialize<T>(T reader) where T : IReader
        {
            reader.Read(out int version);
            if (version < 1 || version > 2) throw new InvalidOperationException("Unsupported manual median version");
            reader.Read(out Id); reader.Read(out Node); reader.Read(out Edge);
            Anchor.Deserialize(reader); reader.Read(out int surface);
            Surface = surface >= 0 && surface <= 2 ? (MedianSavedSurface)surface : MedianSavedSurface.None;
            Shape = default;
            if (version >= 2) { reader.Read(out Shape.x); reader.Read(out Shape.y); reader.Read(out Shape.z); reader.Read(out Shape.w); }
            if (!math.all(math.isfinite(Shape))) Shape = default;
            Shape = math.clamp(Shape, 0, new float4(100, 100, 10, 10));
        }
    }

    [InternalBufferCapacity(0)]
    public struct RoadGrassSavedManualMedianVertex : IBufferElementData, ISerializable
    {
        internal int Id;
        internal float3 Offset;
        internal int Kind; // 0 free, 1/2 source opening left/right, 3 guide point, 4 crossing
        internal RoadGrassPolygonGuideReference RefA, RefB;
        public void Serialize<T>(T writer) where T : IWriter
        {
            writer.Write(2); writer.Write(Id); writer.Write(Offset.x); writer.Write(Offset.y); writer.Write(Offset.z);
            writer.Write(Kind); RefA.Serialize(writer); RefB.Serialize(writer);
        }
        public void Deserialize<T>(T reader) where T : IReader
        {
            reader.Read(out int version);
            if (version < 1 || version > 2) throw new InvalidOperationException("Unsupported manual median vertex version");
            reader.Read(out Id); reader.Read(out Offset.x); reader.Read(out Offset.y); reader.Read(out Offset.z);
            Kind = 0; RefA = default; RefB = default;
            if (version >= 2) { reader.Read(out Kind); RefA.Deserialize(reader); RefB.Deserialize(reader); }
            if (Kind < 0 || Kind > 4) throw new InvalidOperationException("Unsupported polygon reference kind");
        }
    }

    internal struct RoadGrassPolygonGuideReference : ISerializable
    {
        internal Entity Owner, Prefab;
        internal int Ordinal, Side;
        internal float T;
        internal float3 Start, End;
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(Owner); writer.Write(Prefab); writer.Write(Ordinal); writer.Write(Side); writer.Write(T);
            writer.Write(Start.x); writer.Write(Start.y); writer.Write(Start.z);
            writer.Write(End.x); writer.Write(End.y); writer.Write(End.z);
        }
        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Owner); reader.Read(out Prefab); reader.Read(out Ordinal); reader.Read(out Side); reader.Read(out T);
            reader.Read(out Start.x); reader.Read(out Start.y); reader.Read(out Start.z);
            reader.Read(out End.x); reader.Read(out End.y); reader.Read(out End.z);
        }
    }
}
