using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Malloc.ChannelPainter.Editor
{
    public static class VertexColorBake
    {
        public static Color Merge(Color existing, Color painted, Vector4 mask)
        {
            return new Color(
                mask.x > 0.5f ? painted.r : existing.r,
                mask.y > 0.5f ? painted.g : existing.g,
                mask.z > 0.5f ? painted.b : existing.b,
                mask.w > 0.5f ? painted.a : existing.a);
        }

        public static Mesh Bake(Mesh original, Color[] mergedColors)
        {
            if (original == null || mergedColors == null || mergedColors.Length != original.vertexCount)
                return null;

            string path = AssetDatabase.GetAssetPath(original) ?? string.Empty;
            bool writableCopy = path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)
                && original.name.EndsWith("_ChannelPainter", StringComparison.Ordinal);
            if (writableCopy)
            {
                Undo.RecordObject(original, "Bake Channel Painter Vertex Color");
                original.colors = mergedColors;
                EditorUtility.SetDirty(original);
                return original;
            }

            string output = EditorUtility.SaveFilePanelInProject("Save painted mesh",
                original.name + "_ChannelPainter", "asset", "Save the painted mesh copy.");
            if (string.IsNullOrEmpty(output))
                return null;
            if (File.Exists(output))
            {
                Debug.LogError("Channel Painter: choose a new mesh asset path.");
                return null;
            }

            Mesh copy = UnityEngine.Object.Instantiate(original);
            copy.name = original.name + "_ChannelPainter";
            copy.colors = mergedColors;
            try
            {
                AssetDatabase.CreateAsset(copy, output);
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(copy);
                throw;
            }
            return copy;
        }
    }
}
