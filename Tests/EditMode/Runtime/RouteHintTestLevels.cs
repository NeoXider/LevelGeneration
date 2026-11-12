using System.Collections.Generic;
using Neoxider.LevelGeneration.Runtime.Application;
using UnityEngine;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// Small levels with real proportions for the route-hint tests: 5 m floor cells, 0.3 m walls, 2.1 m doorways
    /// (the default medium door frame). Walls are cut at the doorways exactly as the game does it
    /// (<see cref="RouteHintGeometry.AddDoor"/>).
    /// </summary>
    internal static class RouteHintTestLevels
    {
        public const float WallWidth = 0.3f;
        public const float DoorFrame = 2.1f;

        public static RouteHintWall[] Box(float x0, float y0, float x1, float y1) => new[]
        {
            new RouteHintWall(new Vector2(x0, y0), new Vector2(x1, y0), WallWidth),
            new RouteHintWall(new Vector2(x1, y0), new Vector2(x1, y1), WallWidth),
            new RouteHintWall(new Vector2(x1, y1), new Vector2(x0, y1), WallWidth),
            new RouteHintWall(new Vector2(x0, y1), new Vector2(x0, y0), WallWidth)
        };

        /// <summary>
        /// start (5x5) → east door → corridor that runs east and then turns north (three cells) → north door →
        /// end room (5x5). The corridor turn is where a shortest route hugs the inner corner.
        /// </summary>
        public static RouteHintGeometry CorridorWithTurn()
        {
            RouteHintGeometry geometry = new();
            geometry.AddRoom("start", Vector2.zero, 0f, new[] { new MinimapCellSnapshot(0f, 0f, 5f, 5f) },
                Box(0f, 0f, 5f, 5f));
            RouteHintWall[] corridor =
            {
                new(new Vector2(5f, 0f), new Vector2(15f, 0f), WallWidth),
                new(new Vector2(15f, 0f), new Vector2(15f, 10f), WallWidth),
                new(new Vector2(15f, 10f), new Vector2(10f, 10f), WallWidth),
                new(new Vector2(10f, 10f), new Vector2(10f, 5f), WallWidth),
                new(new Vector2(10f, 5f), new Vector2(5f, 5f), WallWidth),
                new(new Vector2(5f, 5f), new Vector2(5f, 0f), WallWidth)
            };
            geometry.AddRoom("corridor", Vector2.zero, 0f, new[]
            {
                new MinimapCellSnapshot(5f, 0f, 5f, 5f), new MinimapCellSnapshot(10f, 0f, 5f, 5f),
                new MinimapCellSnapshot(10f, 5f, 5f, 5f)
            }, corridor);
            geometry.AddRoom("end", Vector2.zero, 0f, new[] { new MinimapCellSnapshot(10f, 10f, 5f, 5f) },
                Box(10f, 10f, 15f, 15f));
            geometry.AddDoor("start", "east", "corridor", "west", new Vector2(5f, 2.5f), DoorFrame);
            geometry.AddDoor("corridor", "north", "end", "south", new Vector2(12.5f, 10f), DoorFrame);
            return geometry;
        }

        public static CriticalPathDoor CorridorExit() => new("corridor", "north", "end", "south");

        /// <summary>
        /// A 10x10 hall with a 7 m partition standing from the south wall; doors in the west and east walls.
        /// The way from door to door goes around the partition's free end.
        /// </summary>
        public static RouteHintGeometry HallWithPartition()
        {
            List<RouteHintWall> walls = new(Box(0f, 0f, 10f, 10f))
            {
                new RouteHintWall(new Vector2(5f, 0f), new Vector2(5f, 7f), WallWidth)
            };
            RouteHintGeometry geometry = new();
            geometry.AddRoom("hall", Vector2.zero, 0f, new[]
            {
                new MinimapCellSnapshot(0f, 0f, 5f, 5f), new MinimapCellSnapshot(5f, 0f, 5f, 5f),
                new MinimapCellSnapshot(0f, 5f, 5f, 5f), new MinimapCellSnapshot(5f, 5f, 5f, 5f)
            }, walls);
            geometry.AddRoom("west", new Vector2(-5f, 0f), 0f, new[] { new MinimapCellSnapshot(0f, 0f, 5f, 5f) });
            geometry.AddRoom("east", new Vector2(10f, 0f), 0f, new[] { new MinimapCellSnapshot(0f, 0f, 5f, 5f) });
            geometry.AddDoor("hall", "w", "west", "e", new Vector2(0f, 2.5f), DoorFrame);
            geometry.AddDoor("hall", "e", "east", "w", new Vector2(10f, 2.5f), DoorFrame);
            return geometry;
        }

        public static CriticalPathDoor HallEastDoor() => new("hall", "e", "east", "w");

        /// <summary>A plain 10x10 hall with a west and an east doorway and nothing inside (a doorway test bed).</summary>
        public static RouteHintGeometry EmptyHall(float doorFrameMeters = DoorFrame)
        {
            RouteHintGeometry geometry = new();
            geometry.AddRoom("hall", Vector2.zero, 0f, new[]
            {
                new MinimapCellSnapshot(0f, 0f, 5f, 5f), new MinimapCellSnapshot(5f, 0f, 5f, 5f),
                new MinimapCellSnapshot(0f, 5f, 5f, 5f), new MinimapCellSnapshot(5f, 5f, 5f, 5f)
            }, Box(0f, 0f, 10f, 10f));
            geometry.AddRoom("west", new Vector2(-5f, 0f), 0f, new[] { new MinimapCellSnapshot(0f, 0f, 5f, 5f) });
            geometry.AddRoom("east", new Vector2(10f, 0f), 0f, new[] { new MinimapCellSnapshot(0f, 0f, 5f, 5f) });
            geometry.AddDoor("hall", "w", "west", "e", new Vector2(0f, 5f), doorFrameMeters);
            geometry.AddDoor("hall", "e", "east", "w", new Vector2(10f, 5f), doorFrameMeters);
            return geometry;
        }

        public static CriticalPathDoor HallWestDoor() => new("hall", "w", "west", "e");

        /// <summary>
        /// Three 5x5 rooms in a row, doors a|b and b|c: a player next to the first door with the target beyond it.
        /// </summary>
        public static RouteHintGeometry ThreeRoomsInARow()
        {
            RouteHintGeometry geometry = new();
            geometry.AddRoom("a", Vector2.zero, 0f, new[] { new MinimapCellSnapshot(0f, 0f, 5f, 5f) },
                Box(0f, 0f, 5f, 5f));
            geometry.AddRoom("b", new Vector2(5f, 0f), 0f, new[] { new MinimapCellSnapshot(0f, 0f, 5f, 5f) },
                Box(5f, 0f, 10f, 5f));
            geometry.AddRoom("c", new Vector2(10f, 0f), 0f, new[] { new MinimapCellSnapshot(0f, 0f, 5f, 5f) },
                Box(10f, 0f, 15f, 5f));
            geometry.AddDoor("a", "e", "b", "w", new Vector2(5f, 2.5f), DoorFrame);
            geometry.AddDoor("b", "e", "c", "w", new Vector2(10f, 2.5f), DoorFrame);
            return geometry;
        }

        public static CriticalPathDoor SecondDoor() => new("b", "e", "c", "w");

        /// <summary>Distance from a point to the nearest wall surface across every room of the level.</summary>
        public static float WallClearance(RouteHintGeometry geometry, Vector2 point) =>
            geometry.MeasureWallClearance(point, 4f);
    }
}
