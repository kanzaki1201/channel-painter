using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Malloc.ChannelPainter.Editor
{
    public sealed class ChannelCanvas : IDisposable
    {
        const int UndoLimit = 10;
        const int DilationPasses = 8;

        readonly List<RenderTexture> snapshots = new List<RenderTexture>();
        readonly Material brushMaterial;
        readonly RenderTexture source;
        readonly RenderTexture coverage;

        public RenderTexture Texture { get; }
        public bool CanUndo => snapshots.Count > 0;

        public ChannelCanvas(int size, Color fill)
        {
            Shader shader = Shader.Find("Hidden/ChannelPainter/Brush");
            if (shader == null)
                throw new InvalidOperationException("Channel Painter brush shader is missing.");

            brushMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            Texture = CreateTexture(size, RenderTextureFormat.ARGBHalf);
            source = CreateTexture(size, RenderTextureFormat.ARGBHalf);
            coverage = CreateTexture(size, RenderTextureFormat.R8);
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
            RenderTexture.active = target;
            GL.Clear(false, true, color);
            RenderTexture.active = previous;
        }

        public void Load(Texture texture)
        {
            Graphics.Blit(texture, Texture);
            ClearUndo();
        }

        public void PushUndo()
        {
            RenderTexture snapshot = CreateTexture(Texture.width, RenderTextureFormat.ARGBHalf);
            Graphics.Blit(Texture, snapshot);
            snapshots.Add(snapshot);
            if (snapshots.Count <= UndoLimit)
                return;

            snapshots[0].Release();
            UnityEngine.Object.DestroyImmediate(snapshots[0]);
            snapshots.RemoveAt(0);
        }

        public void Undo()
        {
            if (!CanUndo)
                return;

            int index = snapshots.Count - 1;
            Graphics.Blit(snapshots[index], Texture);
            snapshots[index].Release();
            UnityEngine.Object.DestroyImmediate(snapshots[index]);
            snapshots.RemoveAt(index);
        }

        public void Paint(Mesh mesh, Matrix4x4 matrix, int submesh, Vector3 center,
            float radius, float hardness, float strength, float value, Vector4 channelMask)
        {
            Graphics.Blit(Texture, source);
            var properties = new MaterialPropertyBlock();
            properties.SetTexture("_Source", source);
            properties.SetMatrix("_BrushMatrix", matrix);
            properties.SetVector("_BrushCenter", center);
            properties.SetFloat("_BrushRadius", radius);
            properties.SetFloat("_BrushHardness", hardness);
            properties.SetFloat("_BrushStrength", strength);
            properties.SetFloat("_BrushValue", value);
            properties.SetVector("_ChannelMask", channelMask);

            var commands = new CommandBuffer { name = "Channel Painter Brush" };
            commands.SetRenderTarget(Texture);
            commands.SetViewport(new Rect(0, 0, Texture.width, Texture.height));
            commands.DrawMesh(mesh, Matrix4x4.identity, brushMaterial, submesh, 0, properties);
            Graphics.ExecuteCommandBuffer(commands);
            commands.Release();
        }

        void DrawCoverage(Mesh mesh, int submesh)
        {
            var commands = new CommandBuffer { name = "Channel Painter Coverage" };
            commands.SetRenderTarget(coverage);
            commands.ClearRenderTarget(false, true, Color.clear);
            commands.SetViewport(new Rect(0, 0, coverage.width, coverage.height));
            commands.DrawMesh(mesh, Matrix4x4.identity, brushMaterial, submesh, 1);
            Graphics.ExecuteCommandBuffer(commands);
            commands.Release();
        }

        public Texture2D ReadbackDilated(Mesh mesh, int submesh)
        {
            DrawCoverage(mesh, submesh);
            Texture2D painted = Readback(Texture, TextureFormat.RGBAFloat);
            Texture2D mask = Readback(coverage, TextureFormat.R8);
            Dilate(painted, mask);
            UnityEngine.Object.DestroyImmediate(mask);
            return painted;
        }

        static Texture2D Readback(RenderTexture target, TextureFormat format)
        {
            var result = new Texture2D(target.width, target.height, format, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            result.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
            result.Apply(false, false);
            RenderTexture.active = previous;
            return result;
        }

        static void Dilate(Texture2D painted, Texture2D mask)
        {
            int size = painted.width;
            Color[] pixels = painted.GetPixels();
            Color32[] maskPixels = mask.GetPixels32();
            var covered = new bool[pixels.Length];
            var added = new bool[pixels.Length];
            for (int i = 0; i < covered.Length; i++)
                covered[i] = maskPixels[i].r != 0;

            for (int pass = 0; pass < DilationPasses; pass++)
            {
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int index = y * size + x;
                    if (covered[index])
                        continue;

                    Color sum = Color.clear;
                    int count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx < 0 || nx >= size || ny < 0 || ny >= size || !covered[ny * size + nx])
                            continue;
                        sum += pixels[ny * size + nx];
                        count++;
                    }
                    if (count == 0)
                        continue;
                    pixels[index] = sum / count;
                    added[index] = true;
                }

                for (int i = 0; i < covered.Length; i++)
                {
                    covered[i] |= added[i];
                    added[i] = false;
                }
            }

            painted.SetPixels(pixels);
            painted.Apply(false, false);
        }

        void ClearUndo()
        {
            foreach (RenderTexture snapshot in snapshots)
            {
                snapshot.Release();
                UnityEngine.Object.DestroyImmediate(snapshot);
            }
            snapshots.Clear();
        }

        public void Dispose()
        {
            ClearUndo();
            Texture.Release();
            source.Release();
            coverage.Release();
            UnityEngine.Object.DestroyImmediate(Texture);
            UnityEngine.Object.DestroyImmediate(source);
            UnityEngine.Object.DestroyImmediate(coverage);
            UnityEngine.Object.DestroyImmediate(brushMaterial);
        }
    }
}
