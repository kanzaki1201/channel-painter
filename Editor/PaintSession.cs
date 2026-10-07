using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Malloc.ChannelPainter.Editor
{
    internal enum PaintOutput
    {
        Map,
        VertexColor
    }

    internal sealed class PaintSession
    {
        readonly Renderer renderer;
        readonly int slot;
        readonly PaintOutput output;
        readonly MaterialPropertyBlock originalBlock;
        readonly Mesh temporaryMesh;
        readonly MeshFilter filter;
        readonly SkinnedMeshRenderer skinned;
        bool saving;
        bool ended;

        internal Mesh OriginalMesh { get; private set; }
        internal bool Dirty { get; private set; }
        internal bool IsVertexColor => output == PaintOutput.VertexColor;

        internal PaintSession(Renderer renderer, int slot, string property, PaintOutput output,
            PaintCanvas canvas)
        {
            this.renderer = renderer;
            this.slot = slot;
            this.output = output;
            skinned = renderer as SkinnedMeshRenderer;
            filter = skinned == null ? renderer.GetComponent<MeshFilter>() : null;
            OriginalMesh = skinned != null ? skinned.sharedMesh : filter.sharedMesh;

            if (IsVertexColor)
            {
                temporaryMesh = Object.Instantiate(OriginalMesh);
                temporaryMesh.hideFlags = HideFlags.HideAndDontSave;
                SetMesh(temporaryMesh);
                EditorSceneManager.sceneSaving += OnSceneSaving;
                EditorSceneManager.sceneSaved += OnSceneSaved;
                PrefabStage.prefabSaving += OnPrefabSaving;
                PrefabStage.prefabSaved += OnPrefabSaved;
            }
            else
            {
                originalBlock = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(originalBlock, slot);
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block, slot);
                block.SetTexture(property, canvas.Texture);
                renderer.SetPropertyBlock(block, slot);
            }
        }

        internal void MarkDirty()
        {
            Dirty = true;
        }

        internal void ClearDirty()
        {
            Dirty = false;
        }

        internal void SetColors(Color[] colors)
        {
            if (temporaryMesh != null)
                temporaryMesh.colors = colors;
        }

        internal void AssignBakedMesh(Mesh baked)
        {
            if (baked == null || renderer == null)
                return;

            if (IsVertexColor)
                SetMesh(OriginalMesh);
            try
            {
                Object component = skinned != null ? (Object)skinned : filter;
                Undo.RecordObject(component, "Assign Channel Painter Mesh");
                SetMesh(baked);
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                EditorUtility.SetDirty(component);
                Undo.FlushUndoRecordObjects();
                OriginalMesh = baked;
            }
            finally
            {
                if (IsVertexColor)
                    SetMesh(temporaryMesh);
            }
        }

        void SetMesh(Mesh mesh)
        {
            if (skinned != null)
                skinned.sharedMesh = mesh;
            else if (filter != null)
                filter.sharedMesh = mesh;
        }

        void OnSceneSaving(Scene scene, string path)
        {
            if (!ended && renderer != null && renderer.gameObject.scene == scene)
            {
                SetMesh(OriginalMesh);
                saving = true;
            }
        }

        void OnSceneSaved(Scene scene)
        {
            if (!ended && saving && renderer != null && renderer.gameObject.scene == scene)
            {
                SetMesh(temporaryMesh);
                saving = false;
            }
        }

        void OnPrefabSaving(GameObject root)
        {
            if (!ended && renderer != null && root != null &&
                (renderer.gameObject == root || renderer.transform.IsChildOf(root.transform)))
            {
                SetMesh(OriginalMesh);
                saving = true;
            }
        }

        void OnPrefabSaved(GameObject root)
        {
            if (!ended && saving && renderer != null && root != null &&
                (renderer.gameObject == root || renderer.transform.IsChildOf(root.transform)))
            {
                SetMesh(temporaryMesh);
                saving = false;
            }
        }

        internal void End()
        {
            if (ended)
                return;
            ended = true;
            if (IsVertexColor)
            {
                EditorSceneManager.sceneSaving -= OnSceneSaving;
                EditorSceneManager.sceneSaved -= OnSceneSaved;
                PrefabStage.prefabSaving -= OnPrefabSaving;
                PrefabStage.prefabSaved -= OnPrefabSaved;
                if (renderer != null)
                    SetMesh(OriginalMesh);
                if (temporaryMesh != null)
                    Object.DestroyImmediate(temporaryMesh);
            }
            else if (renderer != null)
                renderer.SetPropertyBlock(originalBlock.isEmpty ? null : originalBlock, slot);
        }
    }
}
