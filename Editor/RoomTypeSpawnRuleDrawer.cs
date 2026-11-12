#if UNITY_EDITOR
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Infrastructure.Data;
using UnityEditor;
using UnityEngine;

namespace Neoxider.LevelGeneration.Editor
{
    [CustomPropertyDrawer(typeof(RoomKindSpawnRule))]
    public sealed class RoomKindSpawnRuleDrawer : PropertyDrawer
    {
        private const float VerticalSpacing = 2f;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            int lineCount = 1;
            if (property.isExpanded)
            {
                lineCount += 2;
                lineCount += IsChanceRule(property) ? 2 : 2;
            }

            return EditorGUIUtility.singleLineHeight * lineCount + VerticalSpacing * (lineCount - 1);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            Rect line = NextLine(ref position);
            property.isExpanded = EditorGUI.Foldout(line, property.isExpanded, label, true);
            if (!property.isExpanded)
            {
                EditorGUI.EndProperty();
                return;
            }

            EditorGUI.indentLevel++;

            SerializedProperty kind = property.FindPropertyRelative("kind");
            SerializedProperty mode = property.FindPropertyRelative("mode");
            SerializedProperty overrideChance = property.FindPropertyRelative("overrideChance");
            SerializedProperty chance = property.FindPropertyRelative("chance");
            SerializedProperty minCount = property.FindPropertyRelative("minCount");
            SerializedProperty maxCount = property.FindPropertyRelative("maxCount");

            EditorGUI.PropertyField(NextLine(ref position), kind);
            EditorGUI.PropertyField(NextLine(ref position), mode);

            if (IsChanceMode(mode))
            {
                EditorGUI.PropertyField(NextLine(ref position), overrideChance, new GUIContent("Override Chance"));

                if (!overrideChance.boolValue)
                {
                    chance.floatValue = CalculateAutomaticChance(property);
                }

                using (new EditorGUI.DisabledScope(!overrideChance.boolValue))
                {
                    EditorGUI.Slider(NextLine(ref position), chance, 0f, 1f, new GUIContent("Chance"));
                }
            }
            else
            {
                EditorGUI.PropertyField(NextLine(ref position), minCount, new GUIContent("Min Count"));
                EditorGUI.PropertyField(NextLine(ref position), maxCount, new GUIContent("Max Count"));
            }

            EditorGUI.indentLevel--;
            EditorGUI.EndProperty();
        }

        private static bool IsChanceRule(SerializedProperty property)
        {
            SerializedProperty mode = property.FindPropertyRelative("mode");
            return IsChanceMode(mode);
        }

        private static bool IsChanceMode(SerializedProperty mode) => mode != null && mode.enumValueIndex == (int)RoomTypeSpawnMode.Chance;

        private static Rect NextLine(ref Rect position)
        {
            Rect line = new(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            position.y += EditorGUIUtility.singleLineHeight + VerticalSpacing;
            return line;
        }

        private static float CalculateAutomaticChance(SerializedProperty current)
        {
            SerializedProperty rules = current.serializedObject.FindProperty("roomKindSpawnRules");
            if (rules == null || !rules.isArray)
            {
                return 0f;
            }

            int autoRuleCount = 0;
            float overriddenChanceSum = 0f;
            for (int i = 0; i < rules.arraySize; i++)
            {
                SerializedProperty rule = rules.GetArrayElementAtIndex(i);
                SerializedProperty mode = rule.FindPropertyRelative("mode");
                if (!IsChanceMode(mode))
                {
                    continue;
                }

                SerializedProperty overrideChance = rule.FindPropertyRelative("overrideChance");
                SerializedProperty chance = rule.FindPropertyRelative("chance");
                if (overrideChance.boolValue)
                {
                    overriddenChanceSum += Mathf.Clamp01(chance.floatValue);
                }
                else
                {
                    autoRuleCount++;
                }
            }

            if (autoRuleCount <= 0)
            {
                return 0f;
            }

            return Mathf.Clamp01(1f - overriddenChanceSum) / autoRuleCount;
        }
    }
}
#endif
