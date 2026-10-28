using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Infrastructure.Data;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace RedoSchool.Features.LevelGeneration.Generation.Infrastructure
{
    /// <summary>
    /// <see cref="ITilesetCatalog"/> backed by <c>Resources.LoadAll&lt;TilesetSO&gt;</c>.
    /// </summary>
    public sealed class TilesetCatalog : ITilesetCatalog
    {
        private readonly Dictionary<string, TilesetSO> _byId;

        public TilesetCatalog(IEnumerable<TilesetSO> tilesets)
        {
            _byId = new Dictionary<string, TilesetSO>();
            if (tilesets == null)
            {
                return;
            }

            foreach (TilesetSO t in tilesets)
            {
                if (t == null || string.IsNullOrEmpty(t.TilesetId))
                {
                    continue;
                }

                _byId[t.TilesetId] = t;
            }
        }

        public static TilesetCatalog LoadFromResources(string resourcesFolder = "Rooms")
        {
            TilesetSO[] loaded = Resources.LoadAll<TilesetSO>(resourcesFolder);
            if (loaded == null || loaded.Length == 0)
            {
                TilesetSO[] allTypedResources = Resources.LoadAll<TilesetSO>(string.Empty);
                if (allTypedResources != null && allTypedResources.Length > 0)
                {
                    loaded = allTypedResources;
                }
            }

#if UNITY_EDITOR
            TilesetSO[] editorLoaded = LoadEditorResources(resourcesFolder);
            loaded = Merge(loaded, editorLoaded);
#endif

            return new TilesetCatalog(loaded);
        }

#if UNITY_EDITOR
        private static TilesetSO[] Merge(TilesetSO[] first, TilesetSO[] second)
        {
            List<TilesetSO> merged = new();
            AddUnique(merged, first);
            AddUnique(merged, second);
            return merged.ToArray();
        }

        private static void AddUnique(List<TilesetSO> target, TilesetSO[] source)
        {
            if (source == null)
            {
                return;
            }

            for (int i = 0; i < source.Length; i++)
            {
                TilesetSO item = source[i];
                if (item != null && !target.Contains(item))
                {
                    target.Add(item);
                }
            }
        }

        private static TilesetSO[] LoadEditorResources(string resourcesFolder)
        {
            string normalizedFolder = (resourcesFolder ?? string.Empty).Trim().Trim('/');
            // По всему проекту, а не по списку папок: раскладку Resources задаёт проект, а поиск по типу дёшев.
            string[] guids = AssetDatabase.FindAssets("t:TilesetSO");
            List<TilesetSO> tilesets = new();
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]).Replace('\\', '/');
                if (!IsInResourcesFolder(path, normalizedFolder))
                {
                    continue;
                }

                TilesetSO tileset = AssetDatabase.LoadAssetAtPath<TilesetSO>(path);
                if (tileset != null)
                {
                    tilesets.Add(tileset);
                }
            }

            return tilesets.ToArray();
        }

        private static bool IsInResourcesFolder(string assetPath, string resourcesFolder)
        {
            string marker = string.IsNullOrEmpty(resourcesFolder)
                ? "/Resources/"
                : "/Resources/" + resourcesFolder + "/";
            return assetPath.Contains(marker);
        }
#endif

        public ITilesetView FindTileset(string tilesetId)
        {
            if (string.IsNullOrEmpty(tilesetId))
            {
                return null;
            }

            return _byId.TryGetValue(tilesetId, out TilesetSO t) ? t : null;
        }
    }
}
