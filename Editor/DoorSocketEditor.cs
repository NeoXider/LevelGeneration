#if UNITY_EDITOR
using System;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Presentation.Runtime;
using UnityEditor;
using UnityEngine;

namespace RedoSchool.LevelGeneration.Editor
{
    [CustomEditor(typeof(DoorSocket))]
    [CanEditMultipleObjects]
    public sealed class DoorSocketEditor : UnityEditor.Editor
    {
        private SerializedProperty _localYawDegrees;
        private SerializedProperty _flowDirection;

        private void OnEnable()
        {
            _localYawDegrees = serializedObject.FindProperty("localYawDegrees");
            _flowDirection = serializedObject.FindProperty("flowDirection");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Door authoring helpers", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Flip Yaw 180"))
                {
                    ApplyToInspectorTargets(FlipYaw);
                }

                if (GUILayout.Button("Round Yaw 45"))
                {
                    ApplyToInspectorTargets(RoundYaw45);
                }
            }

            if (GUILayout.Button("Align Yaw/Center To Visual Door"))
            {
                ApplyToInspectorTargets(AlignYawToVisualDoor);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Entry"))
                {
                    SetFlow(DoorSocketFlowDirection.EntryOnly);
                }

                if (GUILayout.Button("Exit"))
                {
                    SetFlow(DoorSocketFlowDirection.ExitOnly);
                }

                if (GUILayout.Button("Both"))
                {
                    SetFlow(DoorSocketFlowDirection.Both);
                }

                if (GUILayout.Button("None"))
                {
                    SetFlow(DoorSocketFlowDirection.None);
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void ApplyToInspectorTargets(Action<SerializedObject> action)
        {
            foreach (UnityEngine.Object targetObject in targets)
            {
                if (targetObject is not DoorSocket)
                {
                    continue;
                }

                SerializedObject serialized = new(targetObject);
                serialized.Update();
                action(serialized);
                serialized.ApplyModifiedProperties();
                if (targetObject is DoorSocket door)
                {
                    door.RefreshAuthoringCenterCache();
                    EditorUtility.SetDirty(door);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(door);
                    RoomTemplate room = door.GetComponentInParent<RoomTemplate>(true);
                    if (room != null)
                    {
                        EditorUtility.SetDirty(room);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(room);
                    }
                }
                else
                {
                    EditorUtility.SetDirty(targetObject);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(targetObject);
                }
            }

            serializedObject.Update();
            SceneView.RepaintAll();
        }

        private static void FlipYaw(SerializedObject serialized)
        {
            SerializedProperty yaw = serialized.FindProperty("localYawDegrees");
            yaw.floatValue = NormalizeYaw(yaw.floatValue + 180f);
        }

        private static void RoundYaw45(SerializedObject serialized)
        {
            SerializedProperty yaw = serialized.FindProperty("localYawDegrees");
            yaw.floatValue = NormalizeYaw(Mathf.Round(yaw.floatValue / 45f) * 45f);
        }

        private static void AlignYawToVisualDoor(SerializedObject serialized)
        {
            if (serialized.targetObject is not DoorSocket door)
            {
                return;
            }

            RoomTemplate room = door.GetComponentInParent<RoomTemplate>(true);
            if (room == null || !TryCalculateDoorVisualNormalYaw(room.transform, door, out float yawDegrees))
            {
                return;
            }

            SerializedProperty yaw = serialized.FindProperty("localYawDegrees");
            yaw.floatValue = NormalizeYaw(yawDegrees);
        }

        private static bool TryCalculateDoorVisualNormalYaw(Transform roomRoot, DoorSocket door, out float yawDegrees)
        {
            yawDegrees = 0f;
            MeshFilter[] meshFilters = door.GetComponentsInChildren<MeshFilter>(true);
            if (meshFilters == null || meshFilters.Length == 0)
            {
                return false;
            }

            int count = 0;
            double sumX = 0d;
            double sumZ = 0d;
            for (int i = 0; i < meshFilters.Length; i++)
            {
                MeshFilter filter = meshFilters[i];
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                Vector3[] vertices = mesh.vertices;
                for (int v = 0; v < vertices.Length; v++)
                {
                    Vector3 world = filter.transform.TransformPoint(vertices[v]);
                    Vector3 local = roomRoot.InverseTransformPoint(world);
                    sumX += local.x;
                    sumZ += local.z;
                    count++;
                }
            }

            if (count < 3)
            {
                return false;
            }

            double meanX = sumX / count;
            double meanZ = sumZ / count;
            double covXX = 0d;
            double covXZ = 0d;
            double covZZ = 0d;
            for (int i = 0; i < meshFilters.Length; i++)
            {
                MeshFilter filter = meshFilters[i];
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                Vector3[] vertices = mesh.vertices;
                for (int v = 0; v < vertices.Length; v++)
                {
                    Vector3 world = filter.transform.TransformPoint(vertices[v]);
                    Vector3 local = roomRoot.InverseTransformPoint(world);
                    double dx = local.x - meanX;
                    double dz = local.z - meanZ;
                    covXX += dx * dx;
                    covXZ += dx * dz;
                    covZZ += dz * dz;
                }
            }

            double tangentAngle = 0.5d * Math.Atan2(2d * covXZ, covXX - covZZ);
            Vector2 tangent = new((float)Math.Cos(tangentAngle), (float)Math.Sin(tangentAngle));
            if (tangent.sqrMagnitude <= 0.0001f)
            {
                return false;
            }

            tangent.Normalize();
            Vector2 normalA = new(tangent.y, -tangent.x);
            Vector2 normalB = -normalA;
            Vector2 current = YawToLocalVector(door.LocalYawDegrees);
            Vector2 selected = Vector2.Dot(normalA, current) >= Vector2.Dot(normalB, current) ? normalA : normalB;
            yawDegrees = Mathf.Atan2(-selected.y, selected.x) * Mathf.Rad2Deg;
            return true;
        }

        private static Vector2 YawToLocalVector(float yawDegrees)
        {
            float radians = yawDegrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(radians), -Mathf.Sin(radians)).normalized;
        }

        private void SetFlow(DoorSocketFlowDirection flow) => _flowDirection.enumValueIndex = (int)flow;

        private static float NormalizeYaw(float yaw)
        {
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }
    }
}
#endif
