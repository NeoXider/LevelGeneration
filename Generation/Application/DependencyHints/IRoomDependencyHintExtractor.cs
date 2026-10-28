using System.Collections.Generic;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.DependencyHints
{
    /// <summary>
    /// Extracts generator constraints from room-template metadata, for example KeyPickup -> DoorUnlock.
    /// </summary>
    public interface IRoomDependencyHintExtractor
    {
        public IReadOnlyList<RoomDependencyHint> Extract(IReadOnlyList<IRoomTemplateDescriptor> rooms, ITilesetView tileset);
    }
}
