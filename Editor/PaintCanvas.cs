using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Malloc.ChannelPainter.Editor
{
    public sealed class PaintCanvas : IDisposable
    {
        const int UndoLimit = 10;
        const int DilationPasses = 8;

        readonly List<RenderTexture> snapshots = new List<RenderTexture>();
        readonly List<Vector4> snapshotMasks = new List<Vector4>();
        readonly Material brushMaterial;
        readonly RenderTexture source;
        readonly RenderTexture coverage;
        readonly RenderTexture coverageNext;
        readonly RenderTexture dilated;
        readonly RenderTexture dilationNext;

        public RenderTexture Texture { get; }
        public Vector4 PaintedChannels { get; private set; }
        public bool CanUndo => snapshots.Count > 0;

        public PaintCanvas(int size, Color fill)
        {
            Shader shader = Shader.Find("Hidden/ChannelPainter/Brush");
            if (shader == null)
                throw new InvalidOperationException("Channel Painter brush shader is missing.");

            brushMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            Texture = CreateTexture(size, RenderTextureFormat.ARGBHalf);
            source = CreateTexture(size, RenderTextureFormat.ARGBHalf);
            coverage = CreateTexture(size, RenderTextureFormat.R8);
            coverageNext = CreateTexture(size, RenderTextureFormat.R8);
            dilated = CreateTexture(size, RenderTextureFormat.ARGBHalf);
            dilationNext = CreateTexture(size, RenderTextureFormat.ARGBHalf);
            coverage.filterMode = FilterMode.Point;
            coverageNext.filterMode = FilterMode.Point;
            Clear(Texture, fill);
        }

        static RenderTexture CreateTexture(int size, RenderTextureFormat format)
        {
            var texture = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear)
            {
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.Create();
            return texture;
        }

        static void Clear(RenderTexture target, Color color)
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                GL.Clear(false, true, color);
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        public void Load(Texture texture)
        {
            Graphics.Blit(texture, Texture);
            PaintedChannels = Vector4.zero;
            ClearUndo();
        }

        public void RestoreRaw(Texture2D raw)
        {
            Graphics.Blit(raw, Texture);
            ClearUndo();
        }

        public void RestorePaintedChannels(Vector4 channels)
        {
            PaintedChannels = channels;
        }

        public void LoadVertexColors(Mesh mesh, int submesh)
        {
            Clear(Texture, Color.white);
            var properties = new MaterialPropertyBlock();
            properties.SetFloat("_HasVertexColor", mesh.HasVertexAttribute(VertexAttribute.Color) ? 1 : 0);
            var commands = new CommandBuffer { name = "Channel Painter Vertex Colors" };
            try
            {
                commands.SetRenderTarget(Texture);
                commands.SetViewport(new Rect(0, 0, Texture.width, Texture.height));
                commands.DrawMesh(mesh, Matrix4x4.identity, brushMaterial, submesh, 2, properties);
                Graphics.ExecuteCommandBuffer(commands);
            }
            finally
            {
                commands.Release();
            }
            PaintedChannels = Vector4.zero;
            ClearUndo();
        }

        public void PushUndo()
        {
            RenderTexture snapshot = CreateTexture(Texture.width, RenderTextureFormat.ARGBHalf);
            Graphics.Blit(Texture, snapshot);
            snapshots.Add(snapshot);
            snapshotMasks.Add(PaintedChannels);
            if (snapshots.Count <= UndoLimit)
                return;

            Release(snapshots[0]);
            snapshots.RemoveAt(0);
            snapshotMasks.RemoveAt(0);
        }

        public void Undo()
        {
            if (!CanUndo)
                return;

            int index = snapshots.Count - 1;
            Graphics.Blit(snapshots[index], Texture);
            PaintedChannels = snapshotMasks[index];
            Release(snapshots[index]);
            snapshots.RemoveAt(index);
            snapshotMasks.RemoveAt(index);
        }

        public void Paint(Mesh mesh, Matrix4x4 matrix, int submesh, Vector3 center,
            float radius, float hardness, float strength, float value, Vector4 channelMask)
        {
            var properties = new MaterialPropertyBlock();
            properties.SetMatrix("_BrushMatrix", matrix);
            properties.SetVector("_BrushCenter", center);
            properties.SetFloat("_BrushSpace", 0);
            Paint(mesh, submesh, radius, hardness, strength, value, channelMask, properties);
        }

        public void PaintUV(Mesh mesh, int submesh, Vector2 centerUV, float radiusPixels,
            float hardness, float strength, float value, Vector4 channelMask)
        {
            var properties = new MaterialPropertyBlock();
            properties.SetFloat("_BrushSpace", 1);
            properties.SetVector("_BrushCenterUV", centerUV);
            properties.SetVector("_CanvasSize", new Vector4(Texture.width, Texture.height, 0, 0));
            Paint(mesh, submesh, radiusPixels, hardness, strength, value, channelMask, properties);
        }

        void Paint(Mesh mesh, int submesh, float radius, float hardness, float strength,
            float value, Vector4 channelMask, MaterialPropertyBlock properties)
        {
            Graphics.Blit(Texture, source);
            properties.SetTexture("_Source", source);
            properties.SetFloat("_BrushRadius", radius);
            properties.SetFloat("_BrushHardness", hardness);
            properties.SetFloat("_BrushStrength", strength);
            properties.SetFloat("_BrushValue", value);
            properties.SetVector("_ChannelMask", channelMask);

            var commands = new CommandBuffer { name = "Channel Painter Brush" };
            try
            {
                commands.SetRenderTarget(Texture);
                commands.SetViewport(new Rect(0, 0, Texture.width, Texture.height));
                commands.DrawMesh(mesh, Matrix4x4.identity, brushMaterial, submesh, 0, properties);
                Graphics.ExecuteCommandBuffer(commands);
            }
            finally
            {
                commands.Release();
            }

            if (strength > 0 && channelMask != Vector4.zero)
                PaintedChannels = Vector4.Max(PaintedChannels, channelMask);
        }

        void DrawCoverage(Mesh mesh, int submesh)
        {
            var commands = new CommandBuffer { name = "Channel Painter Coverage" };
            try
            {
                commands.SetRenderTarget(coverage);
                commands.ClearRenderTarget(false, true, Color.clear);
                commands.SetViewport(new Rect(0, 0, coverage.width, coverage.height));
                commands.DrawMesh(mesh, Matrix4x4.identity, brushMaterial, submesh, 1);
                Graphics.ExecuteCommandBuffer(commands);
            }
            finally
            {
                commands.Release();
            }
        }

        RenderTexture Dilate(Mesh mesh, int submesh)
        {
            DrawCoverage(mesh, submesh);
            Graphics.Blit(Texture, dilated);
            RenderTexture colorRead = dilated;
            RenderTexture colorWrite = dilationNext;
            RenderTexture maskRead = coverage;
            RenderTexture maskWrite = coverageNext;
            for (int pass = 0; pass < DilationPasses; pass++)
            {
                brushMaterial.SetTexture("_Coverage", maskRead);
                Graphics.Blit(colorRead, colorWrite, brushMaterial, 3);
                Graphics.Blit(maskRead, maskWrite, brushMaterial, 4);
                (colorRead, colorWrite) = (colorWrite, colorRead);
                (maskRead, maskWrite) = (maskWrite, maskRead);
            }
            return colorRead;
        }

        public Texture2D ReadbackDilated(Mesh mesh, int submesh)
        {
            return Readback(Dilate(mesh, submesh), TextureFormat.RGBAFloat);
        }

        public Color[] SampleVertices(Mesh original, Mesh coverageMesh, int submesh)
        {
            int vertexCount = original.vertexCount;
            Vector2[] uv = original.uv;
            if (uv == null || uv.Length != vertexCount)
                throw new ArgumentException("The target mesh has no UV0.", nameof(original));

            if (vertexCount == 0)
                return Array.Empty<Color>();

            int size = Mathf.CeilToInt(Mathf.Sqrt(vertexCount));
            var uvTexture = new Texture2D(size, size, TextureFormat.RGFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            RenderTexture samples = null;
            try
            {
                samples = CreateTexture(size, RenderTextureFormat.ARGBHalf);
                var positions = new Color[size * size];
                for (int i = 0; i < vertexCount; i++)
                    positions[i] = new Color(uv[i].x, uv[i].y, 0, 0);
                uvTexture.SetPixels(positions);
                uvTexture.Apply(false, false);

                brushMaterial.SetTexture("_VertexUV", uvTexture);
                Graphics.Blit(Dilate(coverageMesh, submesh), samples, brushMaterial, 5);
                Texture2D readback = Readback(samples, TextureFormat.RGBAHalf);
                try
                {
                    Color[] painted = readback.GetPixels();
                    Color[] existing = original.colors;
                    var merged = new Color[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                        merged[i] = existing.Length == vertexCount ? existing[i] : Color.white;
                    foreach (int index in original.GetTriangles(submesh))
                        merged[index] = VertexColorBake.Merge(merged[index], painted[index], PaintedChannels);
                    return merged;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(readback);
                }
            }
            finally
            {
                brushMaterial.SetTexture("_VertexUV", null);
                if (samples != null)
                    Release(samples);
                UnityEngine.Object.DestroyImmediate(uvTexture);
            }
        }

        static Texture2D Readback(RenderTexture target, TextureFormat format)
        {
            var result = new Texture2D(target.width, target.height, format, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                result.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                result.Apply(false, false);
                return result;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(result);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        void ClearUndo()
        {
            foreach (RenderTexture snapshot in snapshots)
                Release(snapshot);
            snapshots.Clear();
            snapshotMasks.Clear();
        }

        static void Release(RenderTexture texture)
        {
            texture.Release();
            UnityEngine.Object.DestroyImmediate(texture);
        }

        public void Dispose()
        {
            ClearUndo();
            Release(Texture);
            Release(source);
            Release(coverage);
            Release(coverageNext);
            Release(dilated);
            Release(dilationNext);
            UnityEngine.Object.DestroyImmediate(brushMaterial);
        }
    }
}
