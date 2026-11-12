using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// Пакет генерации уровней не зависит от игры, в которую встроен: его сборки ссылаются только на свои сборки,
    /// .NET и Unity. Ввод и журнал игра подключает сама — через <c>IMinimapGameplayContext</c> и
    /// <c>LevelGenerationRuntimeLog</c>. Правила проекта-хозяина папку пакета не сканируют, поэтому страж свой:
    /// до него карта уровня тянула за собой сборки игры-хозяина (ввод игрока и журнал),
    /// и пакет не собирался отдельно от игры.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class LevelGenerationIndependenceTests
    {
        private const string PackageAssemblyPrefix = "Neoxider.LevelGeneration";

        /// <summary>Сборки пакета — по имени, а не списком: новый asmdef попадает под стража сам.</summary>
        private static IEnumerable<string> PackageAssemblies() => CompilationPipeline
            .GetAssemblies(AssembliesType.Editor)
            .Select(assembly => assembly.name)
            .Where(name => IsPackageAssembly(name) && !name.Contains(".Tests"))
            .OrderBy(name => name, StringComparer.Ordinal);

        [Serializable]
        private sealed class AsmdefModel
        {
            public string name;
            public string[] references;
        }

        [Test]
        public void PackageAssemblies_AreFound()
        {
            CollectionAssert.Contains(PackageAssemblies().ToList(), PackageAssemblyPrefix + ".Runtime",
                "Страж не нашёл сборок пакета и молча ничего не проверяет.");
        }

        /// <summary>Объявленная ссылка ломает сборку пакета отдельно от игры, даже если код её не использует.</summary>
        [TestCaseSource(nameof(PackageAssemblies))]
        public void Asmdef_ReferencesOnlyPackageAndUnityAssemblies(string assemblyName)
        {
            AsmdefModel asmdef = ReadAsmdefAtAssetPath(
                CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName), assemblyName);
            string[] foreign = (asmdef.references ?? Array.Empty<string>())
                .Select(ResolveReferenceName)
                .Where(reference => !IsPackageAssembly(reference) && !IsUnityAssembly(reference))
                .ToArray();

            Assert.IsEmpty(foreign, $"{assemblyName}: ссылки на сборки вне пакета — " + string.Join(", ", foreign));
        }

        /// <summary>
        /// Скомпилированная сборка показывает, чьи типы код действительно использует. Белый список, а не запрет
        /// на имена конкретной игры: автоподключаемые библиотеки хозяина (Newtonsoft, DOTween, R3) тоже ломают сборку
        /// пакета отдельно от игры.
        /// </summary>
        [TestCaseSource(nameof(PackageAssemblies))]
        public void CompiledAssembly_UsesOnlyPackageDotNetAndUnity(string assemblyName)
        {
            System.Reflection.Assembly assembly = System.Reflection.Assembly.Load(assemblyName);
            string[] foreign = assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(name => !IsPackageAssembly(name) && !IsUnityAssembly(name) && !IsDotNetAssembly(name))
                .ToArray();

            Assert.IsEmpty(foreign, $"{assemblyName} использует сборки вне пакета: " + string.Join(", ", foreign));
        }

        private static bool IsPackageAssembly(string name) => HasPrefix(name, PackageAssemblyPrefix);

        private static bool IsUnityAssembly(string name) =>
            HasPrefix(name, "UnityEngine") || HasPrefix(name, "UnityEditor") || HasPrefix(name, "Unity");

        private static bool IsDotNetAssembly(string name) =>
            name == "mscorlib" || name == "netstandard" || HasPrefix(name, "System") || HasPrefix(name, "Microsoft.CSharp");

        /// <summary>Совпадение целиком или по границе точки: <c>Neoxider.LevelGenerationX</c> — чужая сборка.</summary>
        private static bool HasPrefix(string name, string prefix) =>
            name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal);

        private static AsmdefModel ReadAsmdefAtAssetPath(string assetPath, string what)
        {
            Assert.IsFalse(string.IsNullOrEmpty(assetPath), $"asmdef {what} не найден.");
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            string fullPath = package == null
                ? Path.GetFullPath(assetPath)
                : Path.Combine(package.resolvedPath, assetPath.Substring(package.assetPath.Length).TrimStart('/'));
            return JsonUtility.FromJson<AsmdefModel>(File.ReadAllText(fullPath));
        }

        private static string ResolveReferenceName(string reference)
        {
            const string guidPrefix = "GUID:";
            if (!reference.StartsWith(guidPrefix, StringComparison.Ordinal))
            {
                return reference;
            }

            string path = AssetDatabase.GUIDToAssetPath(reference.Substring(guidPrefix.Length));
            return string.IsNullOrEmpty(path) ? reference : ReadAsmdefAtAssetPath(path, reference).name;
        }
    }
}
