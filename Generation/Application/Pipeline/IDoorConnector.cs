using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Step 4 of generation: wire door sockets between adjacent placed rooms and mark unused doors as walls.
    /// </summary>
    public interface IDoorConnector
    {
        public LevelLayout Connect(
            LevelLayout layout,
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections);
    }
}
