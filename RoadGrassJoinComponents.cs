using System;
using Colossal.Serialization.Entities;
using Unity.Entities;

namespace RoadGrass
{
    /// <summary>
    /// Version-one endpoint-only record. It remains readable so simple saved
    /// joins can be migrated to a median-piece link without guessing.
    /// </summary>
    [InternalBufferCapacity(2)]
    public struct RoadGrassMedianJoin : IBufferElementData, ISerializable
    {
        internal Entity PairedEdge;
        internal bool LocalEnd;
        internal bool PairedEnd;

        internal RoadGrassMedianJoin(Entity pairedEdge, bool localEnd, bool pairedEnd)
        {
            PairedEdge = pairedEdge;
            LocalEnd = localEnd;
            PairedEnd = pairedEnd;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(1);
            writer.Write(PairedEdge);
            writer.Write(LocalEnd);
            writer.Write(PairedEnd);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int _);
            reader.Read(out PairedEdge);
            reader.Read(out LocalEnd);
            reader.Read(out PairedEnd);
        }
    }

    /// <summary>
    /// Persistent identity for one logical median section at one road end.
    /// Prefab references are preferred during rematching; ordinal and geometry
    /// allow generated RoadBuilder compositions to survive reconstruction.
    /// </summary>
    public struct RoadGrassMedianAnchorData : ISerializable, IEquatable<RoadGrassMedianAnchorData>
    {
        internal bool End;
        internal int Ordinal;
        internal Entity RootSection;
        internal Entity RepresentativePiece;
        internal float NormalizedCenter;
        internal float Width;
        internal bool IsValid => !float.IsNaN(NormalizedCenter) && !float.IsInfinity(NormalizedCenter) &&
            !float.IsNaN(Width) && !float.IsInfinity(Width) && Width > .01f &&
            (Ordinal >= 0 || Ordinal <= int.MinValue / 2);

        internal RoadGrassMedianAnchorData(
            bool end,
            int ordinal,
            Entity rootSection,
            Entity representativePiece,
            float normalizedCenter,
            float width)
        {
            End = end;
            Ordinal = ordinal;
            RootSection = rootSection;
            RepresentativePiece = representativePiece;
            NormalizedCenter = normalizedCenter;
            Width = width;
        }

        public bool Equals(RoadGrassMedianAnchorData other)
        {
            return End == other.End && Ordinal == other.Ordinal &&
                RootSection == other.RootSection &&
                RepresentativePiece == other.RepresentativePiece &&
                Math.Abs(NormalizedCenter - other.NormalizedCenter) < 0.0001f &&
                Math.Abs(Width - other.Width) < 0.001f;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(End);
            writer.Write(Ordinal);
            writer.Write(RootSection);
            writer.Write(RepresentativePiece);
            writer.Write(NormalizedCenter);
            writer.Write(Width);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out End);
            reader.Read(out Ordinal);
            reader.Read(out RootSection);
            reader.Read(out RepresentativePiece);
            reader.Read(out NormalizedCenter);
            reader.Read(out Width);
        }
    }

    /// <summary>
    /// Mirrored on the two participating road edges. The renderer processes
    /// the pair by anchor identity, so one road end can host multiple medians.
    /// </summary>
    [InternalBufferCapacity(2)]
    public struct RoadGrassMedianPieceLink : IBufferElementData, ISerializable
    {
        internal ulong PairId;
        internal Entity PairedEdge;
        internal RoadGrassMedianAnchorData Local;
        internal RoadGrassMedianAnchorData Paired;

        internal RoadGrassMedianPieceLink(
            ulong pairId,
            Entity pairedEdge,
            RoadGrassMedianAnchorData local,
            RoadGrassMedianAnchorData paired)
        {
            PairId = pairId;
            PairedEdge = pairedEdge;
            Local = local;
            Paired = paired;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(3);
            writer.Write(PairId);
            writer.Write(PairedEdge);
            Local.Serialize(writer);
            Paired.Serialize(writer);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            if (version >= 3)
            {
                reader.Read(out PairId);
            }
            else
            {
                PairId = 0;
            }
            reader.Read(out PairedEdge);
            Local.Deserialize(reader);
            Paired.Deserialize(reader);
        }
    }

    /// <summary>
    /// Canonical save-game record for a complete joined-median pair.  These
    /// records live on the serialized city singleton.  The per-edge
    /// RoadGrassMedianPieceLink buffers are runtime mirrors used by selection
    /// and rendering and can therefore be rebuilt after loading.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct RoadGrassSavedMedianPair : IBufferElementData, ISerializable
    {
        internal ulong PairId;
        internal Entity EdgeA;
        internal RoadGrassMedianAnchorData AnchorA;
        internal Entity EdgeB;
        internal RoadGrassMedianAnchorData AnchorB;
        internal bool CrosswalkGaps;

        internal RoadGrassSavedMedianPair(
            ulong pairId,
            Entity edgeA,
            RoadGrassMedianAnchorData anchorA,
            Entity edgeB,
            RoadGrassMedianAnchorData anchorB,
            bool crosswalkGaps = false)
        {
            PairId = pairId;
            EdgeA = edgeA;
            AnchorA = anchorA;
            EdgeB = edgeB;
            AnchorB = anchorB;
            CrosswalkGaps = crosswalkGaps;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(CrosswalkGaps ? 2 : 1);
            writer.Write(PairId);
            writer.Write(EdgeA);
            AnchorA.Serialize(writer);
            writer.Write(EdgeB);
            AnchorB.Serialize(writer);
            if (CrosswalkGaps) writer.Write(CrosswalkGaps);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out PairId);
            reader.Read(out EdgeA);
            AnchorA.Deserialize(reader);
            reader.Read(out EdgeB);
            AnchorB.Deserialize(reader);
            CrosswalkGaps = false;
            if (version >= 2) reader.Read(out CrosswalkGaps);
        }
    }
}
