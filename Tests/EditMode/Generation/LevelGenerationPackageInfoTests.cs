using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration;
using UnityEngine;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class LevelGenerationPackageInfoTests
    {
        [Test]
        public void PackageInfo_MatchesPackageJsonAndChangelog()
        {
            string packageRoot = LevelGenerationPackageRoot();
            string packageJsonPath = Path.Combine(packageRoot, "package.json");
            string changelogPath = Path.Combine(packageRoot, "CHANGELOG.md");

            Assert.IsTrue(File.Exists(packageJsonPath), packageJsonPath);
            Assert.IsTrue(File.Exists(changelogPath), changelogPath);

            JObject packageJson = JObject.Parse(File.ReadAllText(packageJsonPath));
            Assert.AreEqual(packageJson.Value<string>("name"), LevelGenerationPackageInfo.PackageName);
            Assert.AreEqual(packageJson.Value<string>("displayName"), LevelGenerationPackageInfo.DisplayName);
            Assert.AreEqual(packageJson.Value<string>("version"), LevelGenerationPackageInfo.Version);
            Assert.AreEqual(packageJson["author"]?.Value<string>("name"), LevelGenerationPackageInfo.Author);

            string changelog = File.ReadAllText(changelogPath);
            StringAssert.Contains($"## {LevelGenerationPackageInfo.Version}", changelog);
        }

        [Test]
        public void PackageInfo_BoundaryPathsExist()
        {
            string packageRoot = LevelGenerationPackageRoot();

            Assert.IsTrue(
                Directory.Exists(Path.Combine(packageRoot, LevelGenerationPackageInfo.GeneratorBoundary)),
                LevelGenerationPackageInfo.GeneratorBoundary);
            Assert.IsTrue(
                Directory.Exists(Path.Combine(packageRoot, LevelGenerationPackageInfo.MinimapBoundary)),
                LevelGenerationPackageInfo.MinimapBoundary);
            Assert.IsTrue(
                Directory.Exists(Path.Combine(packageRoot, LevelGenerationPackageInfo.RuntimeBoundary)),
                LevelGenerationPackageInfo.RuntimeBoundary);
            Assert.IsTrue(LevelGenerationPackageInfo.MinimapIsPartOfLevelGeneration);
            Assert.IsFalse(LevelGenerationPackageInfo.RoomsAreOptionalGameplayIntegration);
        }

        [Test]
        public void CoreGeneratorAssemblies_DoNotReferenceGameOrUnityDependencies()
        {
            string packageRoot = LevelGenerationPackageRoot();

            AssertCoreAsmdef(
                Path.Combine(packageRoot, "Generation", "Domain", "RedoSchool.LevelGeneration.Domain.asmdef"),
                new string[0]);
            AssertCoreAsmdef(
                Path.Combine(packageRoot, "Generation", "Application", "RedoSchool.LevelGeneration.asmdef"),
                new[] { "RedoSchool.LevelGeneration.Domain" });

            string[] forbiddenUsingPrefixes = new[] { "using UnityEngine", "using UnityEditor", "using Cysharp", "using MessagePipe", "using VContainer", "using RedoSchool.Features.MissionMap", "using RedoSchool.Features.Quiz", "using RedoSchool.Features.Cheats", "using RedoSchool.Features.ChatUI", "using RedoSchool.Features.GameplayIntegration", "using RedoSchool.Features.LevelGeneration.Rooms", "using RedoSchool.Features.LevelGeneration.Runtime" };

            AssertNoForbiddenUsings(Path.Combine(packageRoot, "Generation", "Domain"), forbiddenUsingPrefixes);
            AssertNoForbiddenUsings(Path.Combine(packageRoot, "Generation", "Application"), forbiddenUsingPrefixes);
        }

        /// <summary>
        /// Корень пакета — по asmdef <c>RedoSchool.LevelGeneration.Unity</c> (лежит в <c>Generation/</c>), а не строкой:
        /// пакет лежит и в <c>Assets</c>, и установленным из git в <c>Packages</c> (<c>Library/PackageCache</c>).
        /// </summary>
        private static string LevelGenerationPackageRoot()
        {
            string asmdef = UnityEditor.Compilation.CompilationPipeline
                .GetAssemblyDefinitionFilePathFromAssemblyName("RedoSchool.LevelGeneration.Unity");
            Assert.IsFalse(string.IsNullOrEmpty(asmdef), "asmdef RedoSchool.LevelGeneration.Unity не найден.");
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssetPath(asmdef);
            return package != null
                ? package.resolvedPath
                : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(asmdef)));
        }

        private static void AssertCoreAsmdef(string path, string[] allowedReferences)
        {
            Assert.IsTrue(File.Exists(path), path);

            JObject asmdef = JObject.Parse(File.ReadAllText(path));
            Assert.IsTrue(asmdef.Value<bool>("noEngineReferences"), path);

            string[] references = asmdef["references"]?
                .Values<string>()
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .OrderBy(x => x)
                .ToArray() ?? new string[0];
            string[] expected = allowedReferences.OrderBy(x => x).ToArray();
            CollectionAssert.AreEqual(expected, references, path);
        }

        private static void AssertNoForbiddenUsings(string folder, string[] forbiddenUsingPrefixes)
        {
            Assert.IsTrue(Directory.Exists(folder), folder);

            foreach (string file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].TrimStart();
                    if (!line.StartsWith("using "))
                    {
                        continue;
                    }

                    for (int f = 0; f < forbiddenUsingPrefixes.Length; f++)
                    {
                        if (line.StartsWith(forbiddenUsingPrefixes[f]))
                        {
                            Assert.Fail($"{file}:{i + 1} uses forbidden dependency '{line}'.");
                        }
                    }
                }
            }
        }
    }
}
