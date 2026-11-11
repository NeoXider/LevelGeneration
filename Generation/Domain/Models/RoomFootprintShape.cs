using System;
using System.Collections.Generic;

namespace RedoSchool.LevelGeneration.Generation.Domain.Models
{
    /// <summary>
    /// Occupancy shape for rooms whose usable footprint is not a full rectangle.
    /// Coordinates are expressed in room-local footprint cells.
    /// </summary>
    public readonly struct RoomFootprintShape
    {
        private readonly IReadOnlyList<RoomFootprintCell> _cells;

        public RoomFootprintShape(RoomFootprint bounds, IReadOnlyList<RoomFootprintCell> cells)
        {
            Bounds = bounds;
            _cells = NormalizeCells(bounds, cells);
        }

        public RoomFootprint Bounds { get; }
        public IReadOnlyList<RoomFootprintCell> Cells => _cells ?? RectangleCells(Bounds);
        public bool IsRectangle => Cells.Count == Bounds.WidthCells * Bounds.DepthCells;

        public static RoomFootprintShape Rectangle(RoomFootprint footprint) => new(footprint, RectangleCells(footprint));

        private static IReadOnlyList<RoomFootprintCell> NormalizeCells(
            RoomFootprint bounds,
            IReadOnlyList<RoomFootprintCell> cells)
        {
            if (cells == null || cells.Count == 0)
            {
                return RectangleCells(bounds);
            }

            HashSet<RoomFootprintCell> unique = new();
            List<RoomFootprintCell> result = new(cells.Count);
            for (int i = 0; i < cells.Count; i++)
            {
                RoomFootprintCell cell = cells[i];
                if (cell.X < 0 || cell.Z < 0 || cell.X >= bounds.WidthCells || cell.Z >= bounds.DepthCells)
                {
                    continue;
                }

                if (!unique.Add(cell))
                {
                    continue;
                }

                result.Add(cell);
            }

            result.Sort((a, b) =>
            {
                int z = a.Z.CompareTo(b.Z);
                return z != 0 ? z : a.X.CompareTo(b.X);
            });

            return result.Count > 0 ? result : RectangleCells(bounds);
        }

        private static IReadOnlyList<RoomFootprintCell> RectangleCells(RoomFootprint footprint)
        {
            List<RoomFootprintCell> cells = new(Math.Max(1, footprint.WidthCells * footprint.DepthCells));
            for (int z = 0; z < footprint.DepthCells; z++)
            {
                for (int x = 0; x < footprint.WidthCells; x++)
                {
                    cells.Add(new RoomFootprintCell(x, z));
                }
            }

            return cells;
        }
    }
}
