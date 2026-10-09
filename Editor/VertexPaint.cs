using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Malloc.ChannelPainter.Editor
{
    public sealed class VertexPaint : IDisposable
    {
        readonly int submesh;
        readonly int[] triangles;
        readonly int[] vertices;
        readonly int[][] adjacency;
        readonly Vector2[] uv;
        readonly List<Color[]> snapshots = new List<Color[]>();
        readonly List<Vector4> snapshotMasks = new List<Vector4>();
        Material triangleMaterial;
        RenderTexture triangleTarget;
        Texture2D triangleReadback;

        public Color[] Colors { get; private set; }
        public Vector4 PaintedChannels { get; private set; }
        public BrushBlend BlendMode { get; set; }
        public bool CanUndo => snapshots.Count > 0;

        public VertexPaint(Mesh mesh, int submesh)
        {
            this.submesh = submesh;
            triangles = mesh.GetTriangles(submesh);
            var unique = new HashSet<int>(triangles);
            vertices = new int[unique.Count];
            unique.CopyTo(vertices);
            adjacency = BuildAdjacency(mesh.vertexCount, triangles);
            uv = mesh.uv;
            Color[] source = mesh.colors;
            Colors = new Color[mesh.vertexCount];
            for (int i = 0; i < Colors.Length; i++)
                Colors[i] = Clamp(source.Length == Colors.Length ? source[i] : Color.white);
        }

        static Color Clamp(Color color) => new Color(Mathf.Clamp01(color.r), Mathf.Clamp01(color.g),
            Mathf.Clamp01(color.b), Mathf.Clamp01(color.a));

        public static float Weight(float distance, float radius, float hardness, float strength)
        {
            if (radius <= 0 || distance > radius)
                return 0;
            float falloff = hardness >= 1 ? 1 :
                1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(hardness * radius, radius, distance));
            return Mathf.Clamp01(strength * falloff);
        }

        public static float WorldWeight(Vector3 position, Vector3 center, float radius, float hardness,
            float strength) => Weight(Vector3.Distance(position, center), radius, hardness, strength);

        public static float ScreenWeight(Vector2 position, Vector2 center, float radius, float hardness,
            float strength) => Weight(Vector2.Distance(position, center), radius, hardness, strength);

        public static Color Apply(Color source, Vector4 value, Vector4 mask, float weight, BrushBlend blend)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                if (mask[channel] <= 0)
                    continue;
                float amount = weight * mask[channel];
                float result = blend == BrushBlend.Replace ? Mathf.Lerp(source[channel], value[channel], amount) :
                    source[channel] + value[channel] * amount * (blend == BrushBlend.Add ? 1 : -1);
                source[channel] = Mathf.Clamp01(result);
            }
            return source;
        }

        public static int[][] BuildAdjacency(int vertexCount, int[] triangleIndices)
        {
            var lists = new List<int>[vertexCount];
            for (int i = 0; i < triangleIndices.Length; i++)
            {
                int vertex = triangleIndices[i];
                if (lists[vertex] == null)
                    lists[vertex] = new List<int>();
                lists[vertex].Add(i / 3);
            }
            var result = new int[vertexCount][];
            for (int i = 0; i < result.Length; i++)
                result[i] = lists[i]?.ToArray() ?? Array.Empty<int>();
            return result;
        }

        public static bool InReach(int[] adjacentTriangles, ISet<int> visible)
        {
            foreach (int triangle in adjacentTriangles)
                if (visible.Contains(triangle))
                    return true;
            return false;
        }

        public void PaintWorld(Mesh posedMesh, Matrix4x4 objectToWorld, Vector3 center, float radius,
            float hardness, float strength, Vector4 value, Vector4 mask)
        {
            Vector3[] positions = posedMesh.vertices;
            foreach (int index in vertices)
            {
                float weight = WorldWeight(objectToWorld.MultiplyPoint3x4(positions[index]), center,
                    radius, hardness, strength);
                Colors[index] = Apply(Colors[index], value, mask, weight, BlendMode);
            }
            TrackChannels(strength, mask);
        }

        public void PaintScreen(Mesh posedMesh, Matrix4x4 objectToWorld, Camera camera, Vector2 screenPixel,
            float radius, float hardness, float strength, Vector4 value, Vector4 mask)
        {
            if (triangleTarget == null)
                return;
            HashSet<int> visible = ReadVisibleTriangles(screenPixel - camera.pixelRect.position, radius);
            Vector3[] positions = posedMesh.vertices;
            foreach (int index in vertices)
            {
                if (!InReach(adjacency[index], visible))
                    continue;
                Vector3 projected = camera.WorldToScreenPoint(objectToWorld.MultiplyPoint3x4(positions[index]));
                if (projected.z <= 0)
                    continue;
                float weight = ScreenWeight(projected, screenPixel, radius, hardness, strength);
                Colors[index] = Apply(Colors[index], value, mask, weight, BlendMode);
            }
            TrackChannels(strength, mask);
        }

        void TrackChannels(float strength, Vector4 mask)
        {
            if (strength > 0)
                PaintedChannels = Vector4.Max(PaintedChannels, mask);
        }

        public void Fill(int[] triangleIndices, Vector4 value, Vector4 mask)
        {
            IEnumerable<int> selected = triangleIndices == null ? vertices : new HashSet<int>(triangleIndices);
            foreach (int index in selected)
                Colors[index] = Apply(Colors[index], value, mask, 1, BlendMode);
            TrackChannels(1, mask);
        }

        public void CopyChannel(int from, int to)
        {
            foreach (int index in vertices)
                Colors[index][to] = Colors[index][from];
            Vector4 mask = Vector4.zero;
            mask[to] = 1;
            TrackChannels(1, mask);
        }

        public Color Sample(int triangleIndex, Vector3 barycentric)
        {
            int first = triangleIndex * 3;
            return Colors[triangles[first]] * barycentric.x + Colors[triangles[first + 1]] * barycentric.y +
                Colors[triangles[first + 2]] * barycentric.z;
        }

        public void ImportTexture(Texture texture)
        {
            if (uv.Length != Colors.Length)
                throw new InvalidOperationException("The target mesh has no UV0.");
            var target = RenderTexture.GetTemporary(texture.width, texture.height, 0,
                RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(texture.width, texture.height, TextureFormat.RGBAHalf, false, true)
                { wrapMode = TextureWrapMode.Clamp };
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                readback.Apply(false, false);
                foreach (int index in vertices)
                    Colors[index] = Clamp(readback.GetPixelBilinear(uv[index].x, uv[index].y));
                PaintedChannels = Vector4.one;
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(readback);
                RenderTexture.ReleaseTemporary(target);
            }
        }

        public void PushUndo()
        {
            snapshots.Add((Color[])Colors.Clone());
            snapshotMasks.Add(PaintedChannels);
            if (snapshots.Count <= 10)
                return;
            snapshots.RemoveAt(0);
            snapshotMasks.RemoveAt(0);
        }

        public void Undo()
        {
            if (!CanUndo)
                return;
            int last = snapshots.Count - 1;
            Colors = snapshots[last];
            PaintedChannels = snapshotMasks[last];
            snapshots.RemoveAt(last);
            snapshotMasks.RemoveAt(last);
        }

        public byte[] ToBytes()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            foreach (Color color in Colors)
                for (int channel = 0; channel < 4; channel++)
                    writer.Write(color[channel]);
            return stream.ToArray();
        }

        public void RestoreBytes(byte[] bytes, Vector4 paintedChannels)
        {
            if (bytes == null || bytes.Length != (long)Colors.Length * 16)
                throw new ArgumentException("Vertex color data does not match the mesh.", nameof(bytes));
            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream);
            var colors = new Color[Colors.Length];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Colors = colors;
            PaintedChannels = paintedChannels;
            snapshots.Clear();
            snapshotMasks.Clear();
        }

        public void RenderScreenTriangles(Mesh posedMesh, Matrix4x4 objectToWorld, Camera camera)
        {
            int width = camera.pixelWidth;
            int height = camera.pixelHeight;
            if (width <= 0 || height <= 0)
                return;
            EnsureTriangleTarget(width, height);
            var commands = new CommandBuffer { name = "Channel Painter Screen Triangles" };
            try
            {
                commands.SetRenderTarget(triangleTarget);
                commands.ClearRenderTarget(true, true, Color.clear, SystemInfo.usesReversedZBuffer ? 0 : 1);
                commands.SetViewport(new Rect(0, 0, width, height));
                for (int i = 0; i < posedMesh.subMeshCount; i++)
                {
                    var properties = new MaterialPropertyBlock();
                    properties.SetMatrix("_BrushMatrix", objectToWorld);
                    properties.SetMatrix("_DepthViewProj", GL.GetGPUProjectionMatrix(camera.projectionMatrix, true)
                        * camera.worldToCameraMatrix);
                    properties.SetFloat("_TargetSubmesh", i == submesh ? 1 : 0);
                    commands.DrawMesh(posedMesh, Matrix4x4.identity, triangleMaterial, i, 6, properties);
                }
                Graphics.ExecuteCommandBuffer(commands);
            }
            finally
            {
                commands.Release();
            }
        }

        void EnsureTriangleTarget(int width, int height)
        {
            if (triangleMaterial == null)
            {
                Shader shader = Shader.Find("Hidden/ChannelPainter/Brush");
                if (shader == null)
                    throw new InvalidOperationException("Channel Painter brush shader is missing.");
                triangleMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (triangleTarget != null && triangleTarget.width == width && triangleTarget.height == height)
                return;
            ReleaseTriangleTarget();
            triangleTarget = new RenderTexture(width, height, 24, RenderTextureFormat.RFloat,
                RenderTextureReadWrite.Linear)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            triangleTarget.Create();
        }

        HashSet<int> ReadVisibleTriangles(Vector2 center, float radius)
        {
            var visible = new HashSet<int>();
            int x = Mathf.Clamp(Mathf.FloorToInt(center.x - radius), 0, triangleTarget.width);
            int y = Mathf.Clamp(Mathf.FloorToInt(center.y - radius), 0, triangleTarget.height);
            int width = Mathf.Clamp(Mathf.CeilToInt(center.x + radius), 0, triangleTarget.width) - x;
            int height = Mathf.Clamp(Mathf.CeilToInt(center.y + radius), 0, triangleTarget.height) - y;
            if (width <= 0 || height <= 0)
                return visible;
            if (triangleReadback == null || triangleReadback.width != width || triangleReadback.height != height)
            {
                UnityEngine.Object.DestroyImmediate(triangleReadback);
                triangleReadback = new Texture2D(width, height, TextureFormat.RFloat, false, true)
                    { hideFlags = HideFlags.HideAndDontSave };
            }
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = triangleTarget;
                triangleReadback.ReadPixels(new Rect(x, y, width, height), 0, 0, false);
                var pixels = triangleReadback.GetRawTextureData<float>();
                for (int row = 0; row < height; row++)
                for (int col = 0; col < width; col++)
                {
                    if ((new Vector2(x + col + 0.5f, y + row + 0.5f) - center).sqrMagnitude > radius * radius)
                        continue;
                    int triangle = Mathf.RoundToInt(pixels[row * width + col]) - 1;
                    if (triangle >= 0 && triangle < triangles.Length / 3)
                        visible.Add(triangle);
                }
            }
            finally
            {
                RenderTexture.active = previous;
            }
            return visible;
        }

        void ReleaseTriangleTarget()
        {
            if (triangleTarget == null)
                return;
            if (RenderTexture.active == triangleTarget)
                RenderTexture.active = null;
            triangleTarget.Release();
            UnityEngine.Object.DestroyImmediate(triangleTarget);
        }

        public void Dispose()
        {
            ReleaseTriangleTarget();
            UnityEngine.Object.DestroyImmediate(triangleReadback);
            UnityEngine.Object.DestroyImmediate(triangleMaterial);
        }
    }
}
