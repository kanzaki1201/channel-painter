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
            Texture preview)
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
                block.SetTexture(property, preview);
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

            if (Detached)
            {
                OriginalMesh = baked;
                return;
            }

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

        // Undo of a bake puts the pre-bake mesh back on the component; adopt it as the original
        // and put the temporary mesh back, so the session and its save hooks stay consistent.
        internal bool AdoptMeshAfterUndo()
        {
            if (!IsVertexColor || ended || renderer == null)
                return false;
            Mesh current = skinned != null ? skinned.sharedMesh : filter.sharedMesh;
            if (current == null || current == temporaryMesh)
                return false;
            OriginalMesh = current;
            SetMesh(temporaryMesh);
            return true;
        }

        Mesh CurrentMesh => skinned != null ? skinned.sharedMesh : filter != null ? filter.sharedMesh : null;

        // Another tool replaced the temporary mesh on the renderer; the session must not put its original back over that.
        internal bool Detached => IsVertexColor && !ended && !saving && renderer != null && CurrentMesh != temporaryMesh;

        void RestoreOriginalForSave()
        {
            if (CurrentMesh != temporaryMesh)
                return;
            SetMesh(OriginalMesh);
            Object component = skinned != null ? (Object)skinned : filter;
            if (PrefabUtility.IsPartOfPrefabInstance(component))
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            saving = true;
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
                RestoreOriginalForSave();
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
                RestoreOriginalForSave();
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
                if (renderer != null && CurrentMesh == temporaryMesh)
                    SetMesh(OriginalMesh);
                if (temporaryMesh != null)
                    Object.DestroyImmediate(temporaryMesh);
            }
            else if (renderer != null)
                renderer.SetPropertyBlock(originalBlock.isEmpty ? null : originalBlock, slot);
        }
    }
}
