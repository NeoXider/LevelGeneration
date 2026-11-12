using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Neoxider.LevelGeneration;
using UnityEngine;

namespace Neoxider.LevelGeneration.Tests.Generation
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
                Path.Combine(packageRoot, "Generation", "Domain", "Neoxider.LevelGeneration.Domain.asmdef"),
                new string[0]);
            AssertCoreAsmdef(
                Path.Combine(packageRoot, "Generation", "Application", "Neoxider.LevelGeneration.asmdef"),
                new[] { "Neoxider.LevelGeneration.Domain" });

            // Allow-list, not a deny-list of a particular host: the pure layers may use only .NET and the
            // generation layers of this package — never the engine, a DI/async library or any game.
            string[] allowedUsingPrefixes = new[] { "using System", "using Neoxider.LevelGeneration.Generation." };

            AssertOnlyAllowedUsings(Path.Combine(packageRoot, "Generation", "Domain"), allowedUsingPrefixes);
            AssertOnlyAllowedUsings(Path.Combine(packageRoot, "Generation", "Application"), allowedUsingPrefixes);
        }

        /// <summary>
        /// Корень пакета — по asmdef <c>Neoxider.LevelGeneration.Unity</c> (лежит в <c>Generation/</c>), а не строкой:
        /// пакет лежит и в <c>Assets</c>, и установленным из git в <c>Packages</c> (<c>Library/PackageCache</c>).
        /// </summary>
        private static string LevelGenerationPackageRoot()
        {
            string asmdef = UnityEditor.Compilation.CompilationPipeline
                .GetAssemblyDefinitionFilePathFromAssemblyName("Neoxider.LevelGeneration.Unity");
            Assert.IsFalse(string.IsNullOrEmpty(asmdef), "asmdef Neoxider.LevelGeneration.Unity не найден.");
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

        private static void AssertOnlyAllowedUsings(string folder, string[] allowedUsingPrefixes)
        {
            Assert.IsTrue(Directory.Exists(folder), folder);

            foreach (string file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].TrimStart();
                    if (line.StartsWith("namespace "))
                    {
                        break;
                    }

                    if (!line.StartsWith("using "))
                    {
                        continue;
                    }

                    bool allowed = false;
                    for (int a = 0; a < allowedUsingPrefixes.Length && !allowed; a++)
                    {
                        allowed = line.StartsWith(allowedUsingPrefixes[a]);
                    }

                    if (!allowed)
                    {
                        Assert.Fail($"{file}:{i + 1} uses a dependency outside .NET and the generation layers: '{line}'.");
                    }
                }
            }
        }
    }
}
