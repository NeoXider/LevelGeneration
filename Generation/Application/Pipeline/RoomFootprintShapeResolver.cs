using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline
{
    internal static class RoomFootprintShapeResolver
    {
        public static RoomFootprintShape ShapeFor(IRoomTemplateDescriptor template)
        {
            if (template is IRoomFootprintShapeDescriptor shaped)
            {
                return shaped.FootprintShape;
            }

            return RoomFootprintShape.Rectangle(template?.Footprint ?? new RoomFootprint(1, 1));
        }
    }
}
