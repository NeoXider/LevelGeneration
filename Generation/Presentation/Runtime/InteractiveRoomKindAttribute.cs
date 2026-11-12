using UnityEngine;

namespace Neoxider.LevelGeneration.Generation.Presentation.Runtime
{
    /// <summary>
    /// Marks a string field that holds an interactive room kind (<see cref="RoomTemplate"/>). The package knows
    /// no concrete kind: the host game's editor draws the field as a choice of its registered room types and
    /// warns about a value that is not one of them.
    /// </summary>
    public sealed class InteractiveRoomKindAttribute : PropertyAttribute
    {
    }
}
