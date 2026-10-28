using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Runtime.Presentation;
using UnityEngine;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    [Category("LevelGeneration")]
    public sealed class RouteHintFloorPrefabGuardTests
    {
        [Test]
        public void ProductionSettings_FloorTrailPrefab_HasReferencedStarMaterialAndTexture()
        {
            LevelMinimapSettings settings = Resources.Load<LevelMinimapSettings>(LevelMinimapSettings.ResourcesPath);
            Assert.NotNull(settings, "Production minimap settings asset is missing.");

            RouteHintFloorView prefab = settings.RouteHintFloorViewPrefab;

            Assert.NotNull(prefab, "The floor star prefab must be assigned in production settings.");
            Assert.NotNull(prefab.Sparkles, "The floor path needs its Shuriken stars.");
            ParticleSystemRenderer renderer = prefab.Sparkles.GetComponent<ParticleSystemRenderer>();
            Assert.NotNull(renderer);
            Material material = renderer.sharedMaterial;
            Assert.NotNull(material, "The star material must be referenced so it survives build stripping.");
            Assert.NotNull(material.shader);
            Assert.AreEqual(RouteHintFloorView.StarsShaderName, material.shader.name,
                "Stars need the additive trail shader rather than the old chevron ribbon shader.");
            Assert.NotNull(material.GetTexture("_BaseMap"), "The referenced material must supply the star texture.");
        }
    }
}
