using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Infrastructure.Data;
using RedoSchool.Features.LevelGeneration.Generation.Presentation.Runtime;
using UnityEngine;

#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
#endif

namespace RedoSchool.Features.LevelGeneration.Generation.Infrastructure
{
    /// <summary>Room catalog backed by prefab root <see cref="RoomTemplate"/> components.</summary>
    public sealed class RoomDatabase : IRoomDatabase
    {
        private readonly List<IRoomTemplateDescriptor> _all;
        private readonly Dictionary<string, List<IRoomTemplateDescriptor>> _byTileset;

        public RoomDatabase(IEnumerable<IRoomTemplateDescriptor> rooms)
        {
            _all = new List<IRoomTemplateDescriptor>();
            _byTileset = new Dictionary<string, List<IRoomTemplateDescriptor>>();
            if (rooms == null)
            {
                return;
            }

            foreach (IRoomTemplateDescriptor r in rooms)
            {
                if (IsInvalidRoom(r))
                {
                    continue;
                }

                AddOrReplace(r);
            }

            // Stable ordering so selection (including fallback paths) is deterministic regardless of the
            // source enumeration order (Resources.LoadAll / AssetDatabase.FindAssets ordering can differ
            // between machines and platforms). Sort the master list by RoomId and rebuild the per-tileset
            // index once so each tileset list is also ordered.
            SortByRoomId();
            RebuildTilesetIndex();
        }

        private void SortByRoomId()
        {
            _all.Sort(static (a, b) =>
            {
                string left = a?.RoomId ?? string.Empty;
                string right = b?.RoomId ?? string.Empty;
                return string.CompareOrdinal(left, right);
            });
        }

        private void AddOrReplace(IRoomTemplateDescriptor room)
        {
            if (IsInvalidRoom(room))
            {
                return;
            }

            string roomId = room.RoomId;
            for (int i = 0; i < _all.Count; i++)
            {
                if (IsInvalidRoom(_all[i]))
                {
                    continue;
                }

                if (!string.Equals(_all[i].RoomId, roomId, System.StringComparison.Ordinal))
                {
                    continue;
                }

                _all[i] = PreferPrefabRoom(_all[i], room);
                RebuildTilesetIndex();
                return;
            }

            _all.Add(room);
            string tilesetId = room.TilesetId;
            if (string.IsNullOrEmpty(tilesetId))
            {
                return;
            }

            if (!_byTileset.TryGetValue(tilesetId, out List<IRoomTemplateDescriptor> list))
            {
                list = new List<IRoomTemplateDescriptor>();
                _byTileset[tilesetId] = list;
            }

            list.Add(room);
        }

        private static IRoomTemplateDescriptor PreferPrefabRoom(IRoomTemplateDescriptor current,
            IRoomTemplateDescriptor incoming) =>
            incoming is RoomTemplate || current is not RoomTemplate ? incoming : current;

        private void RebuildTilesetIndex()
        {
            _byTileset.Clear();
            for (int i = 0; i < _all.Count; i++)
            {
                IRoomTemplateDescriptor room = _all[i];
                if (IsInvalidRoom(room))
                {
                    continue;
                }

                string tilesetId = room.TilesetId;
                if (string.IsNullOrEmpty(tilesetId))
                {
                    continue;
                }

                if (!_byTileset.TryGetValue(tilesetId, out List<IRoomTemplateDescriptor> list))
                {
                    list = new List<IRoomTemplateDescriptor>();
                    _byTileset[tilesetId] = list;
                }

                list.Add(room);
            }
        }

        public static RoomDatabase LoadFromResources(string resourcesFolder = "Rooms")
        {
            List<IRoomTemplateDescriptor> loaded = new();
            AddUnique(loaded, Resources.LoadAll<RoomTemplate>(resourcesFolder));
            AddUnique(loaded, LoadTilesetPrefabRooms(resourcesFolder));

#if UNITY_EDITOR
            IRoomTemplateDescriptor[] editorLoaded = LoadEditorResources(resourcesFolder);
            AddUnique(loaded, editorLoaded);
#endif

            return new RoomDatabase(loaded);
        }

        private static IEnumerable<IRoomTemplateDescriptor> LoadTilesetPrefabRooms(string resourcesFolder)
        {
            TilesetSO[] tilesets = Resources.LoadAll<TilesetSO>(resourcesFolder);
            if (tilesets == null)
            {
                yield break;
            }

            for (int t = 0; t < tilesets.Length; t++)
            {
                IReadOnlyList<GameObject> prefabs = tilesets[t] != null ? tilesets[t].RoomPrefabs : null;
                if (prefabs == null)
                {
                    continue;
                }

                for (int i = 0; i < prefabs.Count; i++)
                {
                    if (prefabs[i] != null && prefabs[i].TryGetComponent<RoomTemplate>(out RoomTemplate room))
                    {
                        yield return room;
                    }
                }
            }
        }

#if UNITY_EDITOR
        private static IRoomTemplateDescriptor[] LoadEditorResources(string resourcesFolder)
        {
            List<IRoomTemplateDescriptor> result = new();
            AddPrefabRooms(result);
            return result.ToArray();
        }

        private static void AddPrefabRooms(List<IRoomTemplateDescriptor> rooms)
        {
            string[] roots = (LevelGenerationProjectPaths.RoomPrefabRoots ?? System.Array.Empty<string>())
                .Where(root => !string.IsNullOrEmpty(root) && AssetDatabase.IsValidFolder(root))
                .ToArray();
            if (roots.Length == 0)
            {
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:Prefab", roots);
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]).Replace('\\', '/');
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || !prefab.TryGetComponent<RoomTemplate>(out RoomTemplate room))
                {
                    continue;
                }

                AddUnique(rooms, room);
            }
        }

#endif

        public IReadOnlyList<IRoomTemplateDescriptor> AllRooms => _all;

        public IReadOnlyList<IRoomTemplateDescriptor> GetRoomsForTileset(string tilesetId)
        {
            if (string.IsNullOrEmpty(tilesetId))
            {
                return System.Array.Empty<IRoomTemplateDescriptor>();
            }

            if (!_byTileset.TryGetValue(tilesetId, out List<IRoomTemplateDescriptor> list))
            {
                return System.Array.Empty<IRoomTemplateDescriptor>();
            }

            return list;
        }

        private static void AddUnique(List<IRoomTemplateDescriptor> target, IEnumerable<IRoomTemplateDescriptor> source)
        {
            if (source == null)
            {
                return;
            }

            foreach (IRoomTemplateDescriptor item in source)
            {
                AddUnique(target, item);
            }
        }

        private static void AddUnique(List<IRoomTemplateDescriptor> target, IRoomTemplateDescriptor item)
        {
            if (IsInvalidRoom(item))
            {
                return;
            }

            for (int i = 0; i < target.Count; i++)
            {
                if (IsInvalidRoom(target[i]))
                {
                    continue;
                }

                if (!string.Equals(target[i].RoomId, item.RoomId, System.StringComparison.Ordinal))
                {
                    continue;
                }

                target[i] = PreferPrefabRoom(target[i], item);
                return;
            }

            target.Add(item);
        }

        private static bool IsInvalidRoom(IRoomTemplateDescriptor room)
        {
            if (room == null)
            {
                return true;
            }

            return room is Object unityObject && unityObject == null;
        }
    }
}
