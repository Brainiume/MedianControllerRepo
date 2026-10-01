using System;
namespace RoadGrass
{
    internal enum MedianControllerMode { Connect, Edit, Reset, Polygon }
    internal enum MedianCapChoice { Rounded, Straight }
    internal enum MedianSurfaceChoice { Grass, Plain }
    [Flags]
    internal enum MedianResetFilters
    {
        None = 0, Connection = 1, EndCap = 2, Surface = 4,
        All = Connection | EndCap | Surface
    }
}
