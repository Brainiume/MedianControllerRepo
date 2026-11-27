using System;
namespace RoadGrass
{
    internal enum MedianControllerMode { Connect, Edit, Reset, Polygon }
    internal enum MedianCapChoice { Rounded, Straight }
    internal enum MedianSurfaceChoice { Grass, Plain, TerrainGrass = 11, TerrainDirt, TerrainRock, Dry, Lush, Dirt, Sand }
    [Flags]
    internal enum MedianResetFilters
    {
        None = 0, Connection = 1, EndCap = 2, Surface = 4,
        All = Connection | EndCap | Surface
    }
}
