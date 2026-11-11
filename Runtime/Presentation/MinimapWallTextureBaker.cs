using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Domain.Models;
using RedoSchool.LevelGeneration.Generation.Presentation.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace RedoSchool.LevelGeneration.Runtime.Presentation
{
    /// <summary>
    /// Runtime top-down raster bake for wall meshes assigned on RoomTemplate.
    /// </summary>
    public static class MinimapWallTextureBaker
    {
        private const int DefaultTextureSize = 256;
        private const int MinTextureSize = 32;
        private const int TemporaryRenderLayer = 31;
        private const float PaddingMeters = 0.3f;
        private const float CameraVerticalPadding = 10f;

        public static MinimapWallTexture Bake(RoomTemplate template)
        {
            if (template == null || !template.UseMinimapWallGeometry)
            {
                return null;
            }

            List<MinimapWallSegment> segments = ConvertSegments(RoomWallSegmentExtractor.Extract(template));
            if (segments.Count == 0)
            {
                return null;
            }

            return new MinimapWallTexture(null, CalculateSegmentRect(segments), segments);
        }

        private static List<MinimapWallSegment> ConvertSegments(IReadOnlyList<RoomWallSegment2D> source)
        {
            List<MinimapWallSegment> result = new(source?.Count ?? 0);
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Count; i++)
            {
                RoomWallSegment2D segment = source[i];
                result.Add(new MinimapWallSegment(
                    new Vector2(segment.Ax, segment.Az),
                    new Vector2(segment.Bx, segment.Bz),
                    segment.WidthMeters));
            }

            return result;
        }

        private static List<Transform> ResolveRoots(RoomTemplate template)
        {
            List<Transform> result = new(4);
            if (template.MinimapWallParent != null)
            {
                result.Add(template.MinimapWallParent.transform);
                return result;
            }

            IReadOnlyList<Transform> roots = template.MinimapWallRoots;
            if (roots == null)
            {
                return result;
            }

            for (int i = 0; i < roots.Count; i++)
            {
                if (roots[i] != null)
                {
                    result.Add(roots[i]);
                }
            }

            return result;
        }

        private static List<Renderer> CollectRenderers(IReadOnlyList<Transform> roots)
        {
            List<Renderer> result = new();
            for (int i = 0; i < roots.Count; i++)
            {
                Transform root = roots[i];
                if (root == null)
                {
                    continue;
                }

                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer renderer = renderers[r];
                    if (IsRenderable(renderer))
                    {
                        result.Add(renderer);
                    }
                }
            }

            return result;
        }

        private static Bounds? CalculateBounds(IReadOnlyList<Renderer> renderers)
        {
            Bounds bounds = default;
            bool hasBounds = false;
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds ? bounds : null;
        }

        private static Bounds ExpandXZ(Bounds bounds, float amount)
        {
            bounds.Expand(new Vector3(amount * 2f, 0f, amount * 2f));
            return bounds;
        }

        private static bool IsRenderable(Renderer renderer) => renderer != null && renderer.enabled && renderer.gameObject.activeSelf;

        private static List<MinimapWallSegment> BuildSegments(Transform roomRoot, IReadOnlyList<Renderer> renderers)
        {
            List<MinimapWallSegment> result = new(renderers.Count);
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (TryBuildMeshSegment(roomRoot, renderer, out MinimapWallSegment segment))
                {
                    result.Add(segment);
                }
            }

            return result;
        }

        private static bool TryBuildMeshSegment(Transform roomRoot, Renderer renderer, out MinimapWallSegment segment)
        {
            segment = default;
            if (roomRoot == null || renderer == null)
            {
                return false;
            }

            if (renderer.TryGetComponent<MeshFilter>(out MeshFilter filter)
                && filter.sharedMesh != null)
            {
                return TryBuildLocalBoundsSegment(roomRoot, filter.sharedMesh.bounds, renderer.transform, out segment);
            }

            return TryBuildWorldBoundsSegment(roomRoot, renderer.bounds, out segment);
        }

        private static bool TryBuildLocalBoundsSegment(Transform roomRoot, Bounds localBounds, Transform transform,
            out MinimapWallSegment segment)
        {
            segment = default;
            if (transform == null)
            {
                return false;
            }

            Vector3 extents = localBounds.extents;
            Vector3 center = localBounds.center;
            float xLength = extents.x * 2f;
            float zLength = extents.z * 2f;
            if (Mathf.Max(xLength, zLength) < 0.35f)
            {
                return false;
            }

            // Pick the LENGTH axis by WORLD size (mesh extent x that axis' own scale) and take the PERPENDICULAR
            // world extent as wall thickness. Modular wall tiles share a square mesh and encode the thin wall via
            // a non-uniform transform scale; averaging both scales produced fat/inconsistent minimap wall quads
            // (and placement-bounds overlay squares). Matches RoomWallSegmentExtractor so visual == collision.
            float scaleX = Mathf.Abs(transform.lossyScale.x);
            float scaleZ = Mathf.Abs(transform.lossyScale.z);
            float worldX = xLength * scaleX;
            float worldZ = zLength * scaleZ;

            Vector3 localA;
            Vector3 localB;
            float widthMeters;
            if (worldX >= worldZ)
            {
                localA = new Vector3(center.x - extents.x, center.y, center.z);
                localB = new Vector3(center.x + extents.x, center.y, center.z);
                widthMeters = Mathf.Max(0.08f, worldZ);
            }
            else
            {
                localA = new Vector3(center.x, center.y, center.z - extents.z);
                localB = new Vector3(center.x, center.y, center.z + extents.z);
                widthMeters = Mathf.Max(0.08f, worldX);
            }

            Vector3 worldA = transform.TransformPoint(localA);
            Vector3 worldB = transform.TransformPoint(localB);
            Vector3 roomLocalA = roomRoot.InverseTransformPoint(worldA);
            Vector3 roomLocalB = roomRoot.InverseTransformPoint(worldB);
            segment = new MinimapWallSegment(
                new Vector2(roomLocalA.x, roomLocalA.z),
                new Vector2(roomLocalB.x, roomLocalB.z),
                widthMeters);
            return true;
        }

        private static bool TryBuildWorldBoundsSegment(Transform roomRoot, Bounds bounds,
            out MinimapWallSegment segment)
        {
            segment = default;
            float xLength = bounds.size.x;
            float zLength = bounds.size.z;
            if (Mathf.Max(xLength, zLength) < 0.35f)
            {
                return false;
            }

            Vector2 center = new(bounds.center.x, bounds.center.z);
            Vector3 worldA = xLength >= zLength
                ? new Vector3(bounds.min.x, bounds.center.y, center.y)
                : new Vector3(center.x, bounds.center.y, bounds.min.z);
            Vector3 worldB = xLength >= zLength
                ? new Vector3(bounds.max.x, bounds.center.y, center.y)
                : new Vector3(center.x, bounds.center.y, bounds.max.z);
            Vector3 roomLocalA = roomRoot.InverseTransformPoint(worldA);
            Vector3 roomLocalB = roomRoot.InverseTransformPoint(worldB);
            segment = new MinimapWallSegment(
                new Vector2(roomLocalA.x, roomLocalA.z),
                new Vector2(roomLocalB.x, roomLocalB.z),
                Mathf.Max(0.08f, xLength >= zLength ? zLength : xLength));
            return true;
        }

        private static Rect CalculateSegmentRect(IReadOnlyList<MinimapWallSegment> segments)
        {
            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;

            for (int i = 0; i < segments.Count; i++)
            {
                MinimapWallSegment segment = segments[i];
                Include(segment.A);
                Include(segment.B);
            }

            if (float.IsInfinity(minX))
            {
                return Rect.zero;
            }

            return Rect.MinMaxRect(
                minX - PaddingMeters,
                minY - PaddingMeters,
                maxX + PaddingMeters,
                maxY + PaddingMeters);

            void Include(Vector2 point)
            {
                if (point.x < minX)
                {
                    minX = point.x;
                }

                if (point.y < minY)
                {
                    minY = point.y;
                }

                if (point.x > maxX)
                {
                    maxX = point.x;
                }

                if (point.y > maxY)
                {
                    maxY = point.y;
                }
            }
        }

        private static float AverageScale(float a, float b) => (Mathf.Abs(a) + Mathf.Abs(b)) * 0.5f;

        private static MinimapWallTexture RenderTopDown(IReadOnlyList<Renderer> renderers, Bounds bounds)
        {
            float worldWidth = Mathf.Max(0.1f, bounds.size.x);
            float worldDepth = Mathf.Max(0.1f, bounds.size.z);
            ResolveTextureSize(worldWidth, worldDepth, out int textureWidth, out int textureHeight);

            RenderTexture renderTexture = new(textureWidth, textureHeight, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1, useMipMap = false, autoGenerateMips = false };
            Texture2D texture = new(textureWidth, textureHeight, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            GameObject cameraObject = new("MinimapWallTextureBakeCamera") { hideFlags = HideFlags.HideAndDontSave };
            Camera camera = cameraObject.AddComponent<Camera>();
            Material maskMaterial = CreateMaskMaterial();
            RendererState[] states = CaptureAndApplyRenderState(renderers, maskMaterial);
            RenderTexture previousActive = RenderTexture.active;

            try
            {
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.orthographic = true;
                camera.orthographicSize = worldDepth * 0.5f;
                camera.aspect = textureWidth / (float)textureHeight;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = Mathf.Max(CameraVerticalPadding * 2f, bounds.size.y + CameraVerticalPadding * 2f);
                camera.cullingMask = 1 << TemporaryRenderLayer;
                camera.allowHDR = false;
                camera.allowMSAA = false;
                camera.targetTexture = renderTexture;
                camera.transform.SetPositionAndRotation(
                    new Vector3(bounds.center.x, bounds.max.y + CameraVerticalPadding, bounds.center.z),
                    Quaternion.Euler(90f, 0f, 0f));

                renderTexture.Create();
                camera.Render();

                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0f, 0f, textureWidth, textureHeight), 0, 0, false);
                texture.Apply(false, false);

                return new MinimapWallTexture(
                    texture,
                    new Rect(bounds.min.x, bounds.min.z, worldWidth, worldDepth));
            }
            catch (Exception)
            {
                DestroyUnityObject(texture);
                return null;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RestoreRenderState(states);
                camera.targetTexture = null;
                renderTexture.Release();
                DestroyUnityObject(renderTexture);
                DestroyUnityObject(cameraObject);
                DestroyUnityObject(maskMaterial);
            }
        }

        private static void ResolveTextureSize(float worldWidth, float worldDepth, out int textureWidth,
            out int textureHeight)
        {
            float max = Mathf.Max(worldWidth, worldDepth);
            textureWidth = Mathf.Clamp(Mathf.RoundToInt(DefaultTextureSize * (worldWidth / max)), MinTextureSize,
                DefaultTextureSize);
            textureHeight = Mathf.Clamp(Mathf.RoundToInt(DefaultTextureSize * (worldDepth / max)), MinTextureSize,
                DefaultTextureSize);
        }

        private static RendererState[] CaptureAndApplyRenderState(IReadOnlyList<Renderer> renderers,
            Material maskMaterial)
        {
            RendererState[] states = new RendererState[renderers.Count];
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                states[i] = new RendererState(renderer, renderer.gameObject.layer, renderer.sharedMaterials);
                renderer.gameObject.layer = TemporaryRenderLayer;
                int materialCount = Mathf.Max(1, renderer.sharedMaterials.Length);
                Material[] materials = new Material[materialCount];
                for (int m = 0; m < materials.Length; m++)
                {
                    materials[m] = maskMaterial;
                }

                renderer.sharedMaterials = materials;
            }

            return states;
        }

        private static void RestoreRenderState(RendererState[] states)
        {
            if (states == null)
            {
                return;
            }

            for (int i = 0; i < states.Length; i++)
            {
                RendererState state = states[i];
                if (state.Renderer == null)
                {
                    continue;
                }

                state.Renderer.gameObject.layer = state.Layer;
                state.Renderer.sharedMaterials = state.Materials;
            }
        }

        private static Material CreateMaskMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
                            ?? Shader.Find("Unlit/Color")
                            ?? Shader.Find("Sprites/Default")
                            ?? Shader.Find("Hidden/Internal-Colored");
            Material material = new(shader) { hideFlags = HideFlags.HideAndDontSave };

            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", Color.white);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", Color.white);
            }

            if (material.HasProperty("_Cull"))
            {
                material.SetInt("_Cull", (int)CullMode.Off);
            }

            return material;
        }

        internal static void DestroyUnityObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (UnityEngine.Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private readonly struct RendererState
        {
            public readonly Renderer Renderer;
            public readonly int Layer;
            public readonly Material[] Materials;

            public RendererState(Renderer renderer, int layer, Material[] materials)
            {
                Renderer = renderer;
                Layer = layer;
                Materials = materials;
            }
        }
    }

    public sealed class MinimapWallTexture : IDisposable
    {
        public MinimapWallTexture(Texture2D texture, Rect worldRect)
            : this(texture, worldRect, Array.Empty<MinimapWallSegment>())
        {
        }

        public MinimapWallTexture(Texture2D texture, Rect worldRect, IReadOnlyList<MinimapWallSegment> segments)
        {
            Texture = texture;
            WorldRect = worldRect;
            Segments = segments ?? Array.Empty<MinimapWallSegment>();
        }

        public Texture2D Texture { get; }
        public Rect WorldRect { get; }
        public IReadOnlyList<MinimapWallSegment> Segments { get; }

        public void Dispose()
        {
            if (Texture == null)
            {
                return;
            }

            MinimapWallTextureBaker.DestroyUnityObject(Texture);
        }
    }

    public readonly struct MinimapWallSegment
    {
        public readonly Vector2 A;
        public readonly Vector2 B;
        public readonly float WidthMeters;

        public MinimapWallSegment(Vector2 a, Vector2 b, float widthMeters)
        {
            A = a;
            B = b;
            WidthMeters = widthMeters;
        }
    }
}
