using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;

namespace RedoSchool.Features.LevelGeneration.Generation.Domain.Models
{
    /// <summary>
    /// Designer-time description of a door socket on a room prefab.
    /// Pure data — does not reference Unity transforms or scene objects.
    /// </summary>
    public sealed class DoorSocketSpec
    {
        public DoorSocketSpec(
            string socketId,
            DoorSize size,
            bool canBeMainPath = true,
            bool canBeSidePath = true,
            bool canBeLocked = true)
        {
            SocketId = socketId;
            Size = size;
            CanBeMainPath = canBeMainPath;
            CanBeSidePath = canBeSidePath;
            CanBeLocked = canBeLocked;
        }

        public string SocketId { get; }
        public DoorSize Size { get; }
        public bool CanBeMainPath { get; }
        public bool CanBeSidePath { get; }
        public bool CanBeLocked { get; }
    }
}
