using System;
using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    // Read compatibility for the unreleased free-polygon prototype. There is no
    // GameObject renderer. These records cannot safely infer a native source profile.
    [InternalBufferCapacity(0)]
    public struct RoadGrassSavedPolygonVertex : IBufferElementData, ISerializable
    {
        internal int Id;
        internal Entity Node;
        internal float3 Offset;
        internal float Padding, Elevation, Radius, Curb;
        internal bool Grass;
        public void Serialize<T>(T writer) where T : IWriter
        {
            writer.Write(1); writer.Write(Id); writer.Write(Node);
            writer.Write(Offset.x); writer.Write(Offset.y); writer.Write(Offset.z);
            writer.Write(Padding); writer.Write(Elevation); writer.Write(Radius); writer.Write(Curb); writer.Write(Grass);
        }
        public void Deserialize<T>(T reader) where T : IReader
        {
            reader.Read(out int version);
            if (version != 1) throw new InvalidOperationException("Unsupported polygon version");
            reader.Read(out Id); reader.Read(out Node);
            reader.Read(out Offset.x); reader.Read(out Offset.y); reader.Read(out Offset.z);
            reader.Read(out Padding); reader.Read(out Elevation); reader.Read(out Radius); reader.Read(out Curb); reader.Read(out Grass);
        }
    }
}
