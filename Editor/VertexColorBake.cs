using System;
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

        public static bool Bake(Renderer renderer, int submesh, ChannelCanvas canvas, Vector4 mask,
            Mesh coverageMesh)
        {
            Mesh source = renderer is SkinnedMeshRenderer skinned
                ? skinned.sharedMesh
                : renderer.GetComponent<MeshFilter>().sharedMesh;
            Vector2[] uv = source.uv;
            if (uv == null || uv.Length != source.vertexCount)
            {
                Debug.LogError("Channel Painter: the target mesh has no UV0.");
                return false;
            }

            Texture2D painted = canvas.ReadbackDilated(coverageMesh, submesh);
            Color[] existing = source.colors;
            var colors = new Color[source.vertexCount];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = existing.Length == colors.Length ? existing[i] : Color.white;

            foreach (int index in source.GetTriangles(submesh))
                colors[index] = Merge(colors[index], painted.GetPixelBilinear(uv[index].x, uv[index].y), mask);
            UnityEngine.Object.DestroyImmediate(painted);

            string path = AssetDatabase.GetAssetPath(source);
            bool writableCopy = path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)
                && source.name.EndsWith("_ChannelPainter", StringComparison.Ordinal);
            if (writableCopy)
            {
                Undo.RecordObject(source, "Bake Channel Painter Vertex Color");
                source.colors = colors;
                EditorUtility.SetDirty(source);
                return true;
            }

            string output = EditorUtility.SaveFilePanelInProject("Save painted mesh",
                source.name + "_ChannelPainter", "asset", "Save the painted mesh copy.");
            if (string.IsNullOrEmpty(output))
                return false;

            Mesh copy = UnityEngine.Object.Instantiate(source);
            copy.name = source.name + "_ChannelPainter";
            copy.colors = colors;
            AssetDatabase.CreateAsset(copy, output);
            if (renderer is SkinnedMeshRenderer targetSkinned)
            {
                Undo.RecordObject(targetSkinned, "Assign Channel Painter Mesh");
                targetSkinned.sharedMesh = copy;
                EditorUtility.SetDirty(targetSkinned);
            }
            else
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Undo.RecordObject(filter, "Assign Channel Painter Mesh");
                filter.sharedMesh = copy;
                EditorUtility.SetDirty(filter);
            }
            return true;
        }
    }
}
