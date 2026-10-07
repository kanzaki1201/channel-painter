using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Malloc.ChannelPainter.Editor
{
    public sealed class ChannelPainterWindow : EditorWindow
    {
        static readonly int[] Sizes = { 512, 1024, 2048, 4096 };
        static readonly string[] SizeLabels = { "512", "1024", "2048", "4096" };
        const float MinRadius = 0.001f;
        const float MaxRadius = 1f;
        static readonly MethodInfo IntersectRayMesh = typeof(HandleUtility).GetMethod(
            "IntersectRayMesh", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(Ray), typeof(Mesh), typeof(Matrix4x4), typeof(RaycastHit).MakeByRefType() }, null);
        static bool reportedRayError;

        Renderer target;
        Renderer previewRenderer;
        MaterialPropertyBlock previousBlock;
        Mesh posedMesh;
        ChannelCanvas canvas;
        Texture loadTexture;
        string propertyName;
        int previewSlot;
        int slot;
        int sizeIndex = 1;
        Color fill = Color.white;
        float value = 1;
        float radius = 0.1f;
        float hardness = 0.5f;
        float strength = 1;
        bool red = true;
        bool green = true;
        bool blue = true;
        bool alpha = true;
        bool assignToMaterial = true;
        bool paint;
        bool hasHit;
        bool strokeSnapshotTaken;
        Vector3 hitPoint;
        Vector3 hitNormal;

        Vector4 ChannelMask => new Vector4(red ? 1 : 0, green ? 1 : 0,
            blue ? 1 : 0, alpha ? 1 : 0);

        [MenuItem("Tools/Channel Painter")]
        static void Open()
        {
            GetWindow<ChannelPainterWindow>("Channel Painter");
        }

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            if (target == null && Selection.activeGameObject != null)
            {
                GameObject selected = Selection.activeGameObject;
                if (selected.TryGetComponent(out SkinnedMeshRenderer skinned))
                    SetTarget(skinned);
                else if (selected.TryGetComponent(out MeshRenderer meshRenderer))
                    SetTarget(meshRenderer);
            }
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            ClearPreview();
            ReleaseCanvas();
            if (posedMesh != null)
                DestroyImmediate(posedMesh);
            posedMesh = null;
        }

        void OnGUI()
        {
            Renderer chosen = (Renderer)EditorGUILayout.ObjectField("Target", target, typeof(Renderer), true);
            if (chosen != target)
                SetTarget(chosen);
            if (target == null)
            {
                EditorGUILayout.HelpBox("Choose a MeshRenderer with a MeshFilter or a SkinnedMeshRenderer.", MessageType.Info);
                return;
            }

            DrawTargetSettings();
            Mesh mesh = SharedMesh();
            if (mesh == null || mesh.uv.Length != mesh.vertexCount ||
                CurrentMaterial() == null || string.IsNullOrEmpty(propertyName))
                return;

            DrawBrushSettings();
            DrawCanvasSettings();
            DrawOutputSettings();
            using (new EditorGUI.DisabledScope(canvas == null))
                paint = EditorGUILayout.Toggle("Paint", paint);
        }

        void SetTarget(Renderer chosen)
        {
            ClearPreview();
            ReleaseCanvas();
            if (posedMesh != null)
                DestroyImmediate(posedMesh);
            posedMesh = null;
            target = chosen is SkinnedMeshRenderer ||
                (chosen is MeshRenderer && chosen.GetComponent<MeshFilter>() != null)
                ? chosen : null;
            slot = 0;
            propertyName = null;
            paint = false;
            hasHit = false;
            Repaint();
        }

        Mesh SharedMesh()
        {
            return target is SkinnedMeshRenderer skinned
                ? skinned.sharedMesh : target.GetComponent<MeshFilter>().sharedMesh;
        }

        Material CurrentMaterial()
        {
            if (target == null)
                return null;
            Material[] materials = target.sharedMaterials;
            return slot < materials.Length ? materials[slot] : null;
        }

        void DrawTargetSettings()
        {
            Mesh mesh = SharedMesh();
            int count = mesh == null ? 0 : Mathf.Min(mesh.subMeshCount, target.sharedMaterials.Length);
            if (count == 0)
            {
                EditorGUILayout.HelpBox("The target needs a mesh and a material slot.", MessageType.Error);
                return;
            }

            var labels = new string[count];
            for (int i = 0; i < count; i++)
                labels[i] = i + ": " + (target.sharedMaterials[i] == null ? "None" : target.sharedMaterials[i].name);
            int chosen = EditorGUILayout.Popup("Material Slot", Mathf.Clamp(slot, 0, count - 1), labels);
            if (chosen != slot)
            {
                ClearPreview();
                ReleaseCanvas();
                slot = chosen;
                propertyName = null;
                paint = false;
                hasHit = false;
            }

            Material material = CurrentMaterial();
            if (material == null)
            {
                EditorGUILayout.HelpBox("Choose a material for this slot.", MessageType.Error);
                return;
            }

            string[] properties = TextureProperties(material.shader);
            if (properties.Length == 0)
            {
                EditorGUILayout.HelpBox("This shader has no texture properties.", MessageType.Error);
                ClearPreview();
                propertyName = null;
                return;
            }
            int index = Array.IndexOf(properties, propertyName);
            int next = EditorGUILayout.Popup("Texture Property", Mathf.Max(index, 0), properties);
            string chosenProperty = properties[next];
            if (chosenProperty != propertyName)
            {
                ClearPreview();
                propertyName = chosenProperty;
                ApplyPreview();
            }
            string keyword = KeywordFor(material.shader, propertyName);
            if (keyword != null && !material.IsKeywordEnabled(keyword))
            {
                EditorGUILayout.HelpBox(keyword + " is off, so the shader ignores " + propertyName + ".", MessageType.Warning);
                if (GUILayout.Button("Enable " + keyword))
                    EnableKeyword(material, keyword);
            }
            else if (keyword == null && material.GetTexture(propertyName) == null)
                EditorGUILayout.HelpBox("This property has no texture. Assign one in the material inspector if the shader needs a keyword to use it.", MessageType.Warning);
            if (mesh.uv == null || mesh.uv.Length != mesh.vertexCount)
                EditorGUILayout.HelpBox("The target mesh has no UV0.", MessageType.Error);
        }

        // ponytail: name match (_OutlineMap -> *_OUTLINE_MAP); add a per-shader table if a shader breaks the convention.
        static string KeywordFor(Shader shader, string property)
        {
            string snake = "_" + Regex.Replace(property.TrimStart('_'), "(?<=[a-z0-9])(?=[A-Z])", "_").ToUpperInvariant();
            foreach (string name in shader.keywordSpace.keywordNames)
                if (name.EndsWith(snake, StringComparison.Ordinal))
                    return name;
            return null;
        }

        static void EnableKeyword(Material material, string keyword)
        {
            Undo.RecordObject(material, "Enable " + keyword);
            material.EnableKeyword(keyword);
            EditorUtility.SetDirty(material);
        }

        static string[] TextureProperties(Shader shader)
        {
            var properties = new List<string>();
            for (int i = 0; i < shader.GetPropertyCount(); i++)
                if (shader.GetPropertyType(i) == ShaderPropertyType.Texture)
                    properties.Add(shader.GetPropertyName(i));
            return properties.ToArray();
        }

        void DrawBrushSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Brush", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            value = EditorGUILayout.Slider("Value", value, 0, 1);
            radius = EditorGUILayout.Slider(new GUIContent("Radius (world units)", "[ and ] in the scene view."),
                radius, MinRadius, MaxRadius);
            hardness = EditorGUILayout.Slider("Hardness", hardness, 0, 1);
            strength = EditorGUILayout.Slider("Strength", strength, 0, 1);
            using (new EditorGUILayout.HorizontalScope())
            {
                red = EditorGUILayout.ToggleLeft("R", red);
                green = EditorGUILayout.ToggleLeft("G", green);
                blue = EditorGUILayout.ToggleLeft("B", blue);
                alpha = EditorGUILayout.ToggleLeft("A", alpha);
            }
            if (EditorGUI.EndChangeCheck())
                SceneView.RepaintAll();
        }

        void DrawCanvasSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Canvas", EditorStyles.boldLabel);
            sizeIndex = EditorGUILayout.Popup("Size", sizeIndex, SizeLabels);
            fill = EditorGUILayout.ColorField("Fill Color", fill);
            if (GUILayout.Button("New"))
            {
                ClearPreview();
                ReleaseCanvas();
                canvas = new ChannelCanvas(Sizes[sizeIndex], fill);
                ApplyPreview();
            }
            using (new EditorGUI.DisabledScope(CurrentMaterial().GetTexture(propertyName) == null))
                if (GUILayout.Button("Load From Material"))
                    Load(CurrentMaterial().GetTexture(propertyName));
            loadTexture = (Texture)EditorGUILayout.ObjectField("Load Texture", loadTexture, typeof(Texture), false);
            using (new EditorGUI.DisabledScope(loadTexture == null))
                if (GUILayout.Button("Load"))
                    Load(loadTexture);
            using (new EditorGUI.DisabledScope(canvas == null || !canvas.CanUndo))
                if (GUILayout.Button("Undo"))
                {
                    canvas.Undo();
                    SceneView.RepaintAll();
                }
        }

        void Load(Texture texture)
        {
            if (canvas == null)
                canvas = new ChannelCanvas(Sizes[sizeIndex], fill);
            canvas.Load(texture);
            ApplyPreview();
            SceneView.RepaintAll();
        }

        void DrawOutputSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            assignToMaterial = EditorGUILayout.Toggle("Assign to material", assignToMaterial);
            using (new EditorGUI.DisabledScope(canvas == null))
            {
                if (GUILayout.Button("Save Map"))
                    SaveMap();
                if (GUILayout.Button("Bake To Vertex Color"))
                    BakeVertexColor();
            }
        }

        void ReleaseCanvas()
        {
            canvas?.Dispose();
            canvas = null;
        }

        void ClearPreview()
        {
            if (previewRenderer != null)
                previewRenderer.SetPropertyBlock(previousBlock != null && !previousBlock.isEmpty
                    ? previousBlock : null, previewSlot);
            previewRenderer = null;
            previousBlock = null;
        }

        void ApplyPreview()
        {
            if (canvas == null || target == null || string.IsNullOrEmpty(propertyName))
                return;
            if (previewRenderer == null)
            {
                previewRenderer = target;
                previewSlot = slot;
                previousBlock = new MaterialPropertyBlock();
                target.GetPropertyBlock(previousBlock, slot);
            }
            var block = new MaterialPropertyBlock();
            target.GetPropertyBlock(block, slot);
            block.SetTexture(propertyName, canvas.Texture);
            target.SetPropertyBlock(block, slot);
        }

        bool PaintMesh(out Mesh mesh, out Matrix4x4 matrix)
        {
            mesh = SharedMesh();
            matrix = target.localToWorldMatrix;
            if (mesh == null)
                return false;
            if (target is SkinnedMeshRenderer skinned)
            {
                if (posedMesh == null)
                    posedMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                skinned.BakeMesh(posedMesh, true);
                mesh = posedMesh;
                matrix = Matrix4x4.TRS(target.transform.position, target.transform.rotation, Vector3.one);
            }
            return mesh != null && mesh.uv.Length == mesh.vertexCount;
        }

        static bool RayHit(Ray ray, Mesh mesh, Matrix4x4 matrix, out RaycastHit hit)
        {
            hit = default;
            if (IntersectRayMesh == null)
            {
                ReportRayError(null);
                return false;
            }
            try
            {
                object[] args = { ray, mesh, matrix, null };
                bool found = (bool)IntersectRayMesh.Invoke(null, args);
                if (found)
                    hit = (RaycastHit)args[3];
                return found;
            }
            catch (Exception error)
            {
                ReportRayError(error);
                return false;
            }
        }

        static void ReportRayError(Exception error)
        {
            if (reportedRayError)
                return;
            reportedRayError = true;
            Debug.LogError("Channel Painter: scene ray intersection is unavailable. " + error);
        }

        void OnSceneGUI(SceneView view)
        {
            if (!paint || target == null || canvas == null)
                return;
            Event evt = Event.current;
            if (evt.alt)
                return;
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (evt.type == EventType.KeyDown &&
                (evt.keyCode == KeyCode.LeftBracket || evt.keyCode == KeyCode.RightBracket))
            {
                float step = evt.keyCode == KeyCode.RightBracket ? 1.15f : 1 / 1.15f;
                radius = Mathf.Clamp(radius * step, MinRadius, MaxRadius);
                evt.Use();
                Repaint();
                view.Repaint();
                return;
            }
            if (evt.type == EventType.Repaint && hasHit)
                Handles.DrawWireDisc(hitPoint, hitNormal, radius);
            if (evt.type != EventType.MouseMove && evt.type != EventType.MouseDown &&
                evt.type != EventType.MouseDrag)
                return;
            if (!PaintMesh(out Mesh mesh, out Matrix4x4 matrix))
            {
                hasHit = false;
                return;
            }

            bool found = RayHit(HandleUtility.GUIPointToWorldRay(evt.mousePosition), mesh, matrix,
                out RaycastHit hit);
            hasHit = found;
            if (found)
            {
                hitPoint = hit.point;
                hitNormal = hit.normal;
            }
            if (evt.type == EventType.MouseMove)
            {
                view.Repaint();
                return;
            }
            ApplyStroke(evt, found, hit, mesh, matrix);
        }

        void ApplyStroke(Event evt, bool found, RaycastHit hit, Mesh mesh, Matrix4x4 matrix)
        {
            if (evt.button != 0)
                return;
            if (evt.type == EventType.MouseDown)
                strokeSnapshotTaken = false;
            if (found)
            {
                if (!strokeSnapshotTaken)
                {
                    canvas.PushUndo();
                    strokeSnapshotTaken = true;
                }
                canvas.Paint(mesh, matrix, slot, hit.point, radius, hardness, strength, value, ChannelMask);
                SceneView.RepaintAll();
            }
            evt.Use();
        }

        void SaveMap()
        {
            string path = EditorUtility.SaveFilePanelInProject("Save painted map", "ChannelPainter",
                "png", "Save the painted control map.");
            if (string.IsNullOrEmpty(path) || !PaintMesh(out Mesh mesh, out _))
                return;

            Texture2D painted = canvas.ReadbackDilated(mesh, slot);
            var png = new Texture2D(painted.width, painted.height, TextureFormat.RGBA32, false, true);
            png.SetPixels(painted.GetPixels());
            png.Apply(false, false);
            File.WriteAllBytes(path, png.EncodeToPNG());
            DestroyImmediate(png);
            DestroyImmediate(painted);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = Mathf.Max(importer.maxTextureSize, canvas.Texture.width);
            importer.SaveAndReimport();

            if (!assignToMaterial)
                return;
            Material material = CurrentMaterial();
            Undo.RecordObject(material, "Assign Channel Painter Map");
            material.SetTexture(propertyName, AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            string keyword = KeywordFor(material.shader, propertyName);
            if (keyword != null)
                material.EnableKeyword(keyword);
            EditorUtility.SetDirty(material);
        }

        void BakeVertexColor()
        {
            if (PaintMesh(out Mesh mesh, out _))
                VertexColorBake.Bake(target, slot, canvas, ChannelMask, mesh);
        }
    }
}
