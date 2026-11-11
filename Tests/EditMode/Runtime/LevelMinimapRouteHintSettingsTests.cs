using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Runtime.Application;
using RedoSchool.LevelGeneration.Runtime.Presentation;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    /// <summary>
    /// What the owner tuned lives in the route hint ScriptableObject (class defaults and the shipped asset): golden
    /// stars, a slow 0.5 m/s drift, small-skewed sizes, long twinkles for a minority, scatter that fits inside the
    /// wall clearance, a start 0.75 m ahead of the student, a hint that does not go out on approach.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class LevelMinimapRouteHintSettingsTests
    {
        private readonly List<Object> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _created)
            {
                if (created != null)
                {
                    Object.DestroyImmediate(created);
                }
            }

            _created.Clear();
        }

        private LevelMinimapSettings NewSettings()
        {
            LevelMinimapSettings settings = ScriptableObject.CreateInstance<LevelMinimapSettings>();
            _created.Add(settings);
            return settings;
        }

        private static LevelMinimapSettings ProductionSettings()
        {
            LevelMinimapSettings settings = Resources.Load<LevelMinimapSettings>(LevelMinimapSettings.ResourcesPath);
            Assert.NotNull(settings, "Production minimap settings asset is missing.");
            return settings;
        }

        [Test]
        public void ClassDefaults_StarsAreGoldenYellow_NotWhite()
        {
            AssertGolden(NewSettings().RouteHintFloorSparkleColor);
        }

        [Test]
        public void ProductionAsset_StarsAreGoldenYellow_AndTheMaterialMatches()
        {
            LevelMinimapSettings settings = ProductionSettings();

            AssertGolden(settings.RouteHintFloorSparkleColor);
            Material material = settings.RouteHintFloorViewPrefab.Sparkles.GetComponent<ParticleSystemRenderer>()
                .sharedMaterial;
            AssertGolden(material.GetColor("_Tint"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Motion_DriftIsHalfAMetrePerSecond_WithSwayAndSkewedSizes(bool production)
        {
            LevelMinimapSettings settings = production ? ProductionSettings() : NewSettings();

            Assert.AreEqual(0.5f, settings.RouteHintFloorSparkleSpeedMetersPerSecond, 0.0001f,
                "Mean drift is 0.5 m/s, slower than the first 0.8 m/s tuning.");
            Assert.Greater(settings.RouteHintFloorSparkleAlongWobbleMeters, 0f, "Each star has its own speed jitter.");
            Assert.Greater(settings.RouteHintFloorSparkleLateralWobbleMeters, 0f, "Each star sways gently.");
            Assert.AreEqual(0.5f, settings.RouteHintFloorSparkleSizeMinScale, 0.0001f);
            Assert.AreEqual(2f, settings.RouteHintFloorSparkleSizeMaxScale, 0.0001f);
            Assert.Greater(settings.RouteHintFloorSparkleSizeSkew, 1f, "Most stars stay small, a few grow big.");
            Assert.Greater(settings.RouteHintFloorSparkleLongTwinkleFraction, 0f);
            Assert.Less(settings.RouteHintFloorSparkleLongTwinkleFraction, 0.5f, "Long twinkles are a minority.");
            Assert.Greater(settings.RouteHintFloorSparkleLongTwinkleSeconds,
                1f / settings.RouteHintFloorSparkleTwinkleHertz * 3f, "Long twinkles are much slower than the fast ones.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Scatter_FitsInsideTheWallClearance_AndStaysUnderThreeDecimetres(bool production)
        {
            LevelMinimapSettings settings = production ? ProductionSettings() : NewSettings();
            float scatter = settings.RouteHintFloorSparkleLateralJitterMeters
                            + settings.RouteHintFloorSparkleLateralWobbleMeters;

            Assert.LessOrEqual(scatter, 0.3f);
            Assert.GreaterOrEqual(settings.RouteHintPathWallClearanceMeters, 0.8f,
                "The route keeps at least 0.8 m from the walls.");
            Assert.LessOrEqual(settings.RouteHintPathWallClearanceMeters, 1.2f);
            Assert.GreaterOrEqual(settings.RouteHintPathWallClearanceMeters - scatter
                                  - settings.RouteHintFloorSparkleWallMarginMeters, 0.2f,
                "Clearance minus scatter minus the wall margin still leaves free room around every star.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Hint_DoesNotGoOutWhenTheStudentWalksTowardTheTarget(bool production)
        {
            LevelMinimapSettings settings = production ? ProductionSettings() : NewSettings();

            Assert.AreEqual(0f, settings.RouteHintFloorApproachMeters,
                "The hint stays while the student moves along it; progress hides it.");
            Assert.AreEqual(0f, settings.CreateRouteHintRevealTiming().ApproachMeters);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Path_StartsAboutThreeQuartersOfAMetreAhead_AndReplansWithThresholdAndThrottle(bool production)
        {
            LevelMinimapSettings settings = production ? ProductionSettings() : NewSettings();

            Assert.AreEqual(0.75f, settings.RouteHintPathStartOffsetMeters, 0.0001f);
            Assert.GreaterOrEqual(settings.RouteHintReplanDeviationMeters, 0.5f);
            Assert.GreaterOrEqual(settings.RouteHintReplanIntervalSeconds, 0.1f,
                "The re-plan throttle keeps planning far away from every frame.");
        }

        [Test]
        public void StarStyle_MirrorsTheAssetValues()
        {
            LevelMinimapSettings settings = NewSettings();
            SerializedObject serialized = new(settings);
            serialized.FindProperty("routeHintFloorSparkleSizeMinScale").floatValue = 0.25f;
            serialized.FindProperty("routeHintFloorSparkleSizeMaxScale").floatValue = 3f;
            serialized.FindProperty("routeHintFloorSparkleSizeSkew").floatValue = 2f;
            serialized.FindProperty("routeHintFloorSparkleSpeedMetersPerSecond").floatValue = 0.9f;
            serialized.FindProperty("routeHintFloorSparkleLongTwinkleFraction").floatValue = 0.4f;
            serialized.FindProperty("routeHintFloorSparkleLongTwinkleSeconds").floatValue = 6f;
            serialized.FindProperty("routeHintFloorSparkleAlongWobbleMeters").floatValue = 0.2f;
            serialized.FindProperty("routeHintFloorSparkleLateralWobbleMeters").floatValue = 0.05f;
            serialized.FindProperty("routeHintFloorSparkleWallMarginMeters").floatValue = 0.45f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            RouteHintStarStyle style = settings.CreateRouteHintStarStyle();

            Assert.AreEqual(0.25f, style.SizeMinScale, 0.0001f);
            Assert.AreEqual(3f, style.SizeMaxScale, 0.0001f);
            Assert.AreEqual(2f, style.SizeSkew, 0.0001f);
            Assert.AreEqual(0.9f, style.SpeedMetersPerSecond, 0.0001f);
            Assert.AreEqual(0.4f, style.LongTwinkleFraction, 0.0001f);
            Assert.AreEqual(6f, style.LongTwinkleSeconds, 0.0001f);
            Assert.AreEqual(0.2f, style.AlongWobbleMeters, 0.0001f);
            Assert.AreEqual(0.05f, style.LateralWobbleMeters, 0.0001f);
            Assert.AreEqual(0.45f, style.WallMarginMeters, 0.0001f);
            Assert.AreEqual(settings.RouteHintFloorMaxSparkles, style.MaxStars);
        }

        [Test]
        public void PathOptions_AddTheCornerDeviationToTheClearance_ZeroClearanceIsTheLegacyPlanner()
        {
            LevelMinimapSettings settings = NewSettings();

            RouteHintPathOptions options = settings.CreateRouteHintPathOptions();

            Assert.IsTrue(options.UsesClearance);
            Assert.AreEqual(settings.RouteHintPathWallClearanceMeters + settings.RouteHintFloorCornerMaxDeviationMeters,
                options.ClearanceMeters, 0.0001f, "Rounding a corner may not eat the wall clearance.");
            Assert.AreEqual(settings.RouteHintPathDoorApproachMeters, options.DoorApproachMeters, 0.0001f);

            SerializedObject serialized = new(settings);
            serialized.FindProperty("routeHintPathWallClearanceMeters").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.IsFalse(settings.CreateRouteHintPathOptions().UsesClearance);
        }

        private static void AssertGolden(Color color)
        {
            float peak = Mathf.Max(color.r, color.g, color.b);
            Assert.GreaterOrEqual(peak, 1.5f, "HDR: the stars glow.");
            Color.RGBToHSV(new Color(color.r / peak, color.g / peak, color.b / peak), out float hue, out float saturation,
                out _);
            Assert.That(hue * 360f, Is.InRange(40f, 56f), "Golden yellow, not orange and not white.");
            Assert.GreaterOrEqual(saturation, 0.8f, "Saturated gold: a white star would not read as golden.");
            Assert.Less(color.b, color.g * 0.3f, "Little blue: warm gold.");
        }
    }
}
