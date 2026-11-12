#if UNITY_EDITOR
using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Application;
using Neoxider.LevelGeneration.Generation.Application.Pipeline;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Generation.Infrastructure;
using UnityEditor;
using UnityEngine;

namespace Neoxider.LevelGeneration.Editor
{
    public sealed class RoomCompatibilityReportWindow : EditorWindow
    {
        /// <summary>
        /// Из <see cref="LevelGenerationProjectPaths.RoomResourcesPath"/> при первом <see cref="OnGUI"/>: окно,
        /// восстановленное после перекомпиляции, создаётся раньше, чем проект назначит свои пути.
        /// </summary>
        private string _resourcesPath;
        private int _rotationStepDegrees = 45;
        private Vector2 _scroll;
        private readonly List<string> _lines = new();

        [MenuItem("Neoxider/LevelGeneration/Room Compatibility Report")]
        public static void Open() => GetWindow<RoomCompatibilityReportWindow>("Room Compatibility");

        private void OnGUI()
        {
            _resourcesPath ??= LevelGenerationProjectPaths.RoomResourcesPath;
            _resourcesPath = EditorGUILayout.TextField("Resources path", _resourcesPath);
            _rotationStepDegrees = EditorGUILayout.IntSlider("Rotation step", _rotationStepDegrees, 15, 90);

            if (GUILayout.Button("Scan Room Prefabs"))
            {
                Scan();
            }

            EditorGUILayout.Space();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < _lines.Count; i++)
            {
                EditorGUILayout.HelpBox(_lines[i], MessageType.None);
            }

            EditorGUILayout.EndScrollView();
        }

        private void Scan()
        {
            _lines.Clear();
            RoomDatabase database = RoomDatabase.LoadFromResources(_resourcesPath);
            IReadOnlyList<IRoomTemplateDescriptor> descriptors = database.AllRooms;
            if (descriptors == null || descriptors.Count == 0)
            {
                _lines.Add($"No room prefabs found for Resources/{_resourcesPath} or the editor room roots " +
                           $"({string.Join(", ", LevelGenerationProjectPaths.RoomPrefabRoots ?? System.Array.Empty<string>())}).");
                return;
            }

            int isolatedRooms = 0;
            for (int i = 0; i < descriptors.Count; i++)
            {
                IRoomTemplateDescriptor room = descriptors[i];
                int compatibleDoorCount = CountCompatibleDoors(room, descriptors);
                if (compatibleDoorCount > 0)
                {
                    _lines.Add($"OK: {room.RoomId} has {compatibleDoorCount}/{room.Doors.Count} compatible doors.");
                    continue;
                }

                isolatedRooms++;
                _lines.Add($"NO MATCH: {room.RoomId} has no compatible door pair in current room set.");
            }

            _lines.Insert(0,
                $"Scanned {descriptors.Count} rooms. Isolated rooms: {isolatedRooms}. Rotation step: {_rotationStepDegrees} degrees.");
        }

        private int CountCompatibleDoors(IRoomTemplateDescriptor room, IReadOnlyList<IRoomTemplateDescriptor> allRooms)
        {
            if (room.Doors == null || room.Doors.Count == 0)
            {
                return 0;
            }

            int compatible = 0;
            for (int d = 0; d < room.Doors.Count; d++)
            {
                if (HasCompatiblePair(room, room.Doors[d], allRooms))
                {
                    compatible++;
                }
            }

            return compatible;
        }

        private bool HasCompatiblePair(
            IRoomTemplateDescriptor source,
            IDoorSocketDescriptor sourceDoor,
            IReadOnlyList<IRoomTemplateDescriptor> allRooms)
        {
            GridPose sourcePose = new(0f, 0f, 0);
            (float x, float z) sourcePoint = RoomPlacer.DoorWorldPoint(sourcePose, source, sourceDoor);
            float sourceYaw = Normalize(sourceDoor.LocalYawDegrees);

            for (int r = 0; r < allRooms.Count; r++)
            {
                IRoomTemplateDescriptor target = allRooms[r];
                if (target.Doors == null)
                {
                    continue;
                }

                for (int td = 0; td < target.Doors.Count; td++)
                {
                    IDoorSocketDescriptor targetDoor = target.Doors[td];
                    if (sourceDoor.Size != targetDoor.Size)
                    {
                        continue;
                    }

                    for (int rot = 0; rot < 360; rot += _rotationStepDegrees)
                    {
                        float targetYaw = Normalize(rot + targetDoor.LocalYawDegrees);
                        if (!Opposes(sourceYaw, targetYaw))
                        {
                            continue;
                        }

                        (float x, float z) targetLocalPoint =
                            Rotate((targetDoor.ConnectionLocalXDoorSteps(), targetDoor.ConnectionLocalZDoorSteps()),
                                rot);
                        GridPose targetPose = new(sourcePoint.x - targetLocalPoint.x,
                            sourcePoint.z - targetLocalPoint.z, rot);
                        if (!RoomPlacer.Overlaps(sourcePose, source.Footprint, targetPose, target.Footprint))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static (float x, float z) Rotate((float x, float z) point, float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);
            return (point.x * cos + point.z * sin, -point.x * sin + point.z * cos);
        }

        private static bool Opposes(float a, float b)
        {
            float diff = Mathf.Abs(Mathf.DeltaAngle(a, b));
            return Mathf.Abs(diff - 180f) <= 0.1f;
        }

        private static float Normalize(float yaw)
        {
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }
    }
}
#endif
