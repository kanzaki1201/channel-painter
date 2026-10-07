using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Malloc.ChannelPainter.Editor
{
    public sealed class ChannelPainterWindow : EditorWindow
    {
        enum PaintTool { Brush, FillIsland }

        static readonly int[] Sizes = { 512, 1024, 2048, 4096 };
        static readonly string[] SizeLabels = { "512", "1024", "2048", "4096" };
        static readonly string[] OutputLabels = { "Map", "Vertex color" };
        static readonly string[] MaskSourceLabels = { "Canvas", "Vertex Color" };
        static readonly string[] ViewLabels = { "RGBA", "R", "G", "B", "A" };
        static readonly string[] ToolLabels = { "Brush", "Fill Island" };
        const float MinRadius = 0.001f;
        const float MaxRadius = 1f;
        const string ReloadPath = "Temp/ChannelPainter/canvas.raw";
        static readonly MethodInfo IntersectRayMesh = typeof(HandleUtility).GetMethod(
            "IntersectRayMesh", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(Ray), typeof(Mesh), typeof(Matrix4x4), typeof(RaycastHit).MakeByRefType() }, null);
        static bool reportedRayError;

        [SerializeField] Renderer target;
        [SerializeField] string propertyName;
        [SerializeField] int slot;
        [SerializeField] int sizeIndex = 1;
        [SerializeField] int canvasSize;
        [SerializeField] Color fill = Color.white;
        [SerializeField] float value = 1;
        [SerializeField] float radius = 0.1f;
        [SerializeField] float hardness = 0.5f;
        [SerializeField] float strength = 1;
        [SerializeField] BrushBlend blend;
        [SerializeField] bool pressureStrength = true;
        [SerializeField] bool pressureSize;
        [SerializeField] bool red = true;
        [SerializeField] bool green = true;
        [SerializeField] bool blue = true;
        [SerializeField] bool alpha = true;
        [SerializeField] bool paint;
        [SerializeField] PaintTool tool;
        [SerializeField] bool showMask;
        [SerializeField] bool maskVertexColor;
        [SerializeField] bool dirtyFlag;
        [SerializeField] int viewChannel;
        [SerializeField] PaintOutput output;
        [SerializeField] Vector4 reloadChannels;
        int pickerId;

        PaintCanvas canvas;
        PaintSession session;
        Material viewMaterial;
        Mesh posedMesh;
        Mesh cachedHitMesh;
        int cachedHitSlot = -1;
        int[] cachedTriangles;
        Vector2[] cachedUV;
        Mesh islandMesh;
        int islandSlot = -1;
        int[] islandIds;
        bool hasHit;
        bool hasHoverUV;
        bool strokeSnapshotTaken;
        bool reloading;
        double lastVertexUpdate;
        Vector3 hitPoint;
        Vector3 hitNormal;
        Vector2 hoverUV;

        internal static ChannelPainterWindow Active { get; private set; }
        internal PaintCanvas Canvas => canvas;
        internal Mesh TargetMesh => target == null ? null : session != null ? session.OriginalMesh : SharedMesh();
        internal int TargetSlot => slot;
        internal bool IsPainting => paint;
        internal Color BlendColor => blend == BrushBlend.Add ? new Color(0.35f, 1f, 0.45f)
            : blend == BrushBlend.Subtract ? new Color(1f, 0.4f, 0.4f) : Color.white;
        internal string StatusText => "Channel Painter  |  " + (tool == PaintTool.FillIsland ? "Fill Island" : "Brush") +
            "  |  " + blend + "  |  " + ChannelText + "  (A add, S subtract, R replace)";
        string ChannelText => (red ? "R" : "") + (green ? "G" : "") + (blue ? "B" : "") + (alpha ? "A" : "");

        // Active only while painting, so A, S and R override the scene view tool shortcuts only then.
        sealed class PaintShortcutContext : IShortcutContext
        {
            public bool active => Active != null && Active.paint;
        }

        static readonly PaintShortcutContext ShortcutContext = new PaintShortcutContext();

        [Shortcut("Channel Painter/Blend Add", typeof(PaintShortcutContext), KeyCode.A)]
        static void ShortcutAdd() => Active?.SetBlend(BrushBlend.Add);

        [Shortcut("Channel Painter/Blend Subtract", typeof(PaintShortcutContext), KeyCode.S)]
        static void ShortcutSubtract() => Active?.SetBlend(BrushBlend.Subtract);

        [Shortcut("Channel Painter/Blend Replace", typeof(PaintShortcutContext), KeyCode.R)]
        static void ShortcutReplace() => Active?.SetBlend(BrushBlend.Replace);

        void SetBlend(BrushBlend next)
        {
            blend = next;
            Repaint();
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
        }

        void DrawSceneStatus(SceneView view)
        {
            Handles.BeginGUI();
            var content = new GUIContent(StatusText);
            var style = new GUIStyle(EditorStyles.helpBox) { fontSize = 12, normal = { textColor = BlendColor } };
            Vector2 size = style.CalcSize(content);
            GUI.Box(new Rect(10, view.position.height - size.y - 34, size.x + 6, size.y + 4), content, style);
            Handles.EndGUI();
        }
        internal bool IsFillIsland => tool == PaintTool.FillIsland;
        internal bool HasHoverUV => hasHoverUV;
        internal Vector2 HoverUV => hoverUV;
        internal Material ViewMaterial => viewMaterial;
        internal int ViewChannel
        {
            get => viewChannel;
            set
            {
                if (viewChannel == value)
                    return;
                viewChannel = value;
                Repaint();
                SceneView.RepaintAll();
                ChannelPainterUVWindow.Active?.Repaint();
            }
        }

        Vector4 ChannelMask => new Vector4(red ? 1 : 0, green ? 1 : 0,
            blue ? 1 : 0, alpha ? 1 : 0);
        bool IsDirty => session != null ? session.Dirty : dirtyFlag;
        // Only Map output swaps the canvas into a texture property.
        bool HasOutputProperty => output == PaintOutput.VertexColor || !string.IsNullOrEmpty(propertyName);
        Texture PropertyTexture => string.IsNullOrEmpty(propertyName) ? null : CurrentMaterial()?.GetTexture(propertyName);

        [MenuItem("Tools/Channel Painter/Channel Painter")]
        static void Open()
        {
            GetWindow<ChannelPainterWindow>("Channel Painter");
        }

        void OnEnable()
        {
            Active = this;
            SceneView.duringSceneGui += OnSceneGUI;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
            ShortcutManager.RegisterContext(ShortcutContext);
            Shader shader = Shader.Find("Hidden/ChannelPainter/View");
            if (shader != null)
                viewMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (target == null && Selection.activeGameObject != null)
            {
                GameObject selected = Selection.activeGameObject;
                if (selected.TryGetComponent(out SkinnedMeshRenderer skinned))
                    SetTarget(skinned);
                else if (selected.TryGetComponent(out MeshRenderer meshRenderer))
                    SetTarget(meshRenderer);
            }
            RestoreAfterReload();
            UpdateUnsavedState();
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            ShortcutManager.UnregisterContext(ShortcutContext);
            if (!reloading && IsDirty)
                WriteCanvasFile();
            EndSession();
            ReleaseCanvas();
            if (posedMesh != null)
                DestroyImmediate(posedMesh);
            posedMesh = null;
            if (viewMaterial != null)
                DestroyImmediate(viewMaterial);
            viewMaterial = null;
            if (Active == this)
                Active = null;
        }

        void OnDestroy()
        {
            if (!reloading && !IsDirty && File.Exists(ReloadPath))
                File.Delete(ReloadPath);
        }

        public override void SaveChanges()
        {
            if (SaveCurrentOutput())
                base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            ClearDirty();
            if (File.Exists(ReloadPath))
                File.Delete(ReloadPath);
            base.DiscardChanges();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);
            Renderer chosen = (Renderer)EditorGUILayout.ObjectField("Target", target, typeof(Renderer), true);
            if (chosen != target)
                ChangeSelection(() => SetTarget(chosen));
            if (target == null)
            {
                EditorGUILayout.HelpBox("Choose a MeshRenderer with a MeshFilter or a SkinnedMeshRenderer.", MessageType.Info);
                return;
            }

            DrawTargetSettings();
            Mesh mesh = TargetMesh;
            if (mesh == null || mesh.uv.Length != mesh.vertexCount ||
                CurrentMaterial() == null || !HasOutputProperty)
                return;

            DrawCanvasSettings();
            DrawPaintSettings();
            DrawViewSettings();
            DrawSaveSettings();
            if (session != null && session.IsVertexColor)
                EditorGUILayout.HelpBox("End the Vertex color session before Apply Overrides or dragging this object into the Project window.", MessageType.Warning);
        }

        void ChangeSelection(Action change)
        {
            if (ConfirmSessionEnd())
                change();
            GUIUtility.ExitGUI();
        }

        bool ConfirmSessionEnd()
        {
            if (!IsDirty)
                return true;
            int choice = EditorUtility.DisplayDialogComplex("Unsaved Channel Painter paint",
                "Save the current paint before ending this session?", "Save", "Cancel", "Discard");
            if (choice == 1)
                return false;
            if (choice == 0 && !SaveCurrentOutput())
                return false;
            if (choice == 2)
                ClearDirty();
            return true;
        }

        void SetTarget(Renderer chosen)
        {
            EndSession();
            ReleaseCanvas();
            ReleasePose();
            target = chosen is SkinnedMeshRenderer ||
                (chosen is MeshRenderer && chosen.GetComponent<MeshFilter>() != null)
                ? chosen : null;
            slot = 0;
            propertyName = null;
            paint = false;
            hasHit = false;
            hasHoverUV = false;
            ClearIslands();
            ClearDirty();
            Repaint();
        }

        Mesh SharedMesh()
        {
            if (target is SkinnedMeshRenderer skinned)
                return skinned.sharedMesh;
            if (target is MeshRenderer renderer)
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                return filter != null ? filter.sharedMesh : null;
            }
            return null;
        }

        Material CurrentMaterial()
        {
            if (target == null)
                return null;
            Material[] materials = target.sharedMaterials;
            return slot >= 0 && slot < materials.Length ? materials[slot] : null;
        }

        void DrawTargetSettings()
        {
            Mesh mesh = TargetMesh;
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
                ChangeSelection(() =>
                {
                    EndSession();
                    ReleaseCanvas();
                    slot = chosen;
                    propertyName = null;
                    paint = false;
                    hasHit = false;
                    hasHoverUV = false;
                    ClearIslands();
                    ClearDirty();
                });

            Material material = CurrentMaterial();
            if (material == null)
            {
                EditorGUILayout.HelpBox("Choose a material for this slot.", MessageType.Error);
                return;
            }

            PaintOutput chosenOutput = (PaintOutput)EditorGUILayout.Popup("Output", (int)output, OutputLabels);
            if (chosenOutput != output)
                ChangeSelection(() => SwitchOutput(chosenOutput));
            if (output == PaintOutput.VertexColor)
            {
                if (mesh.uv.Length != mesh.vertexCount)
                    EditorGUILayout.HelpBox("The target mesh has no UV0.", MessageType.Error);
                return;
            }

            string[] properties = TextureProperties(material.shader);
            if (properties.Length == 0)
            {
                EditorGUILayout.HelpBox("This shader has no texture properties.", MessageType.Error);
                return;
            }
            int index = Array.IndexOf(properties, propertyName);
            int next = EditorGUILayout.Popup("Texture Property", Mathf.Max(index, 0), properties);
            string chosenProperty = properties[next];
            if (chosenProperty != propertyName)
                ChangeSelection(() =>
                {
                    EndSession();
                    ReleaseCanvas();
                    propertyName = chosenProperty;
                    paint = false;
                    ClearDirty();
                });

            string keyword = KeywordFor(material.shader, propertyName);
            if (keyword != null && !material.IsKeywordEnabled(keyword))
            {
                EditorGUILayout.HelpBox(keyword + " is off, so the shader ignores " + propertyName + ".", MessageType.Warning);
                if (GUILayout.Button("Enable " + keyword))
                {
                    EnableKeyword(material, keyword);
                    GUIUtility.ExitGUI();
                }
            }
            else if (keyword == null && material.GetTexture(propertyName) == null)
                EditorGUILayout.HelpBox("This property has no texture. Assign one in the material inspector if the shader needs a keyword to use it.", MessageType.Warning);
            if (mesh.uv.Length != mesh.vertexCount)
                EditorGUILayout.HelpBox("The target mesh has no UV0.", MessageType.Error);
        }

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

        void DrawPaintSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Paint", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button(paint ? "Stop Painting" : "Start Painting", GUILayout.Height(28)))
                {
                    if (!paint && canvas == null)
                        CreateStartCanvas();
                    paint = !paint && canvas != null;
                    hasHit = false;
                    if (!paint)
                        EndStroke();
                    SceneView.RepaintAll();
                    ChannelPainterUVWindow.Active?.Repaint();
                    GUIUtility.ExitGUI();
                }
            PaintTool chosenTool = (PaintTool)EditorGUILayout.Popup("Tool", (int)tool, ToolLabels);
            if (chosenTool != tool)
            {
                tool = chosenTool;
                hasHit = false;
                SceneView.RepaintAll();
                ChannelPainterUVWindow.Active?.Repaint();
            }
            EditorGUI.BeginChangeCheck();
            blend = (BrushBlend)EditorGUILayout.EnumPopup(
                new GUIContent("Blend", "Replace sets the value. Add and Subtract change the current value by Value."), blend);
            value = EditorGUILayout.Slider("Value", value, 0, 1);
            if (tool == PaintTool.Brush)
            {
                radius = EditorGUILayout.Slider(new GUIContent("Radius (world units)", "[ and ] in the scene view."),
                    radius, MinRadius, MaxRadius);
                hardness = EditorGUILayout.Slider("Hardness", hardness, 0, 1);
                strength = EditorGUILayout.Slider("Strength", strength, 0, 1);
                pressureStrength = EditorGUILayout.Toggle("Pen Pressure → Strength", pressureStrength);
                pressureSize = EditorGUILayout.Toggle("Pen Pressure → Size", pressureSize);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Paint On Channels");
                red = GUILayout.Toggle(red, "R", EditorStyles.miniButtonLeft, GUILayout.Width(28));
                green = GUILayout.Toggle(green, "G", EditorStyles.miniButtonMid, GUILayout.Width(28));
                blue = GUILayout.Toggle(blue, "B", EditorStyles.miniButtonMid, GUILayout.Width(28));
                alpha = GUILayout.Toggle(alpha, "A", EditorStyles.miniButtonRight, GUILayout.Width(28));
                GUILayout.FlexibleSpace();
            }
            if (EditorGUI.EndChangeCheck())
                SceneView.RepaintAll();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(canvas == null || session == null ||
                    EditorApplication.isPlayingOrWillChangePlaymode))
                    if (GUILayout.Button(new GUIContent("Fill Canvas", "Apply Value to every texel of the target submesh.")))
                    {
                        FillTriangles(null);
                        GUIUtility.ExitGUI();
                    }
                using (new EditorGUI.DisabledScope(canvas == null || !canvas.CanUndo))
                    if (GUILayout.Button("Undo"))
                    {
                        canvas.Undo();
                        MarkDirty();
                        UpdateVertexColors(true);
                        SceneView.RepaintAll();
                        ChannelPainterUVWindow.Active?.Repaint();
                    }
            }
        }

        void DrawCanvasSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Canvas", EditorStyles.boldLabel);
            sizeIndex = EditorGUILayout.Popup("Size", sizeIndex, SizeLabels);
            if (output == PaintOutput.Map)
            {
                fill = EditorGUILayout.ColorField("Fill Color", fill);
                string newTooltip = PropertyTexture == null
                    ? "Start a canvas filled with Fill Color. The material has no " + propertyName + ", so this also saves the canvas as a new PNG and assigns it."
                    : "Start a canvas filled with Fill Color.";
                if (GUILayout.Button(new GUIContent("New Canvas", newTooltip)))
                    ChangeSelection(NewCanvas);
                using (new EditorGUI.DisabledScope(PropertyTexture == null))
                    if (GUILayout.Button(new GUIContent("Load " + propertyName + " From Material",
                        "Replace the canvas with the map that the material holds in " + propertyName + ".")))
                    {
                        Load(PropertyTexture);
                        GUIUtility.ExitGUI();
                    }
            }
            else if (GUILayout.Button(new GUIContent("Reset To Mesh Colors",
                "Replace the canvas with the vertex colors of the original mesh.")))
                ChangeSelection(NewCanvas);

            if (GUILayout.Button(new GUIContent("Load Texture...", "Pick any texture and copy it into the canvas.")))
            {
                pickerId = GUIUtility.GetControlID(FocusType.Passive);
                EditorGUIUtility.ShowObjectPicker<Texture>(null, false, "", pickerId);
                GUIUtility.ExitGUI();
            }
            Event evt = Event.current;
            if (evt.type == EventType.ExecuteCommand && evt.commandName == "ObjectSelectorClosed" &&
                EditorGUIUtility.GetObjectPickerControlID() == pickerId)
            {
                pickerId = 0;
                if (EditorGUIUtility.GetObjectPickerObject() is Texture picked)
                    Load(picked);
                GUIUtility.ExitGUI();
            }
        }

        void NewCanvas()
        {
            EndSession();
            ReleaseCanvas();
            canvas = new PaintCanvas(Sizes[sizeIndex], fill);
            if (output == PaintOutput.VertexColor)
                canvas.LoadVertexColors(SharedMesh(), slot);
            SyncCanvasState();
            ClearDirty();
            StartSession();
            if (output == PaintOutput.Map && PropertyTexture == null)
                SaveMap(saveAs: true);
            UpdateVertexColors(true);
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
        }

        // A domain reload drops a canvas without unsaved paint, so Start Painting rebuilds it from the target.
        void CreateStartCanvas()
        {
            Texture assigned = PropertyTexture;
            if (output == PaintOutput.Map && assigned != null)
            {
                EndSession();
                canvas = new PaintCanvas(Sizes[sizeIndex], fill);
                canvas.Load(assigned);
                SyncCanvasState();
                ClearDirty();
                StartSession();
            }
            else
                NewCanvas();
        }

        void Load(Texture texture)
        {
            if (canvas == null)
            {
                canvas = new PaintCanvas(Sizes[sizeIndex], fill);
            }
            canvas.Load(texture);
            SyncCanvasState();
            StartSession();
            MarkDirty();
            UpdateVertexColors(true);
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
        }

        void DrawViewSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("View", EditorStyles.boldLabel);
            ViewChannel = EditorGUILayout.Popup("Show Channel", viewChannel, ViewLabels);
            bool nextShowMask = EditorGUILayout.Toggle("Show Mask In Scene View", showMask);
            if (nextShowMask != showMask)
            {
                showMask = nextShowMask;
                SceneView.RepaintAll();
            }
            // In Vertex color output the canvas already drives the vertex colors.
            if (output == PaintOutput.Map)
            {
                int nextSource = EditorGUILayout.Popup(new GUIContent("Mask Source",
                    "Canvas shows the paint. Vertex Color shows the mesh's current vertex colors, with or without a canvas."),
                    maskVertexColor ? 1 : 0, MaskSourceLabels);
                if ((nextSource == 1) != maskVertexColor)
                {
                    maskVertexColor = nextSource == 1;
                    SceneView.RepaintAll();
                }
            }
            if (GUILayout.Button("Open UV Window"))
            {
                ChannelPainterUVWindow.Open();
                GUIUtility.ExitGUI();
            }
        }

        void DrawSaveSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Save", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(canvas == null))
            {
                if (output == PaintOutput.Map)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(new GUIContent("Save Map", "Overwrite the PNG assigned to the material, or ask for a path when none is assigned.")))
                        {
                            SaveMap();
                            GUIUtility.ExitGUI();
                        }
                        if (GUILayout.Button(new GUIContent("Save Map As...", "Save to a new PNG and assign it.")))
                        {
                            SaveMap(true);
                            GUIUtility.ExitGUI();
                        }
                    }
                }
                else if (GUILayout.Button("Bake To Vertex Color"))
                {
                    BakeVertexColor();
                    GUIUtility.ExitGUI();
                }
            }
        }

        void SwitchOutput(PaintOutput chosen)
        {
            EndSession();
            output = chosen;
            ClearDirty();
            if (output == PaintOutput.VertexColor)
            {
                if (canvas == null)
                    canvas = new PaintCanvas(Sizes[sizeIndex], fill);
                canvas.LoadVertexColors(SharedMesh(), slot);
            }
            SyncCanvasState();
            StartSession();
            UpdateVertexColors(true);
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
        }

        void StartSession()
        {
            if (session != null || canvas == null || target == null || !HasOutputProperty ||
                EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            Mesh mesh = SharedMesh();
            if (mesh == null)
                return;
            RefreshPreview();
            session = new PaintSession(target, slot, propertyName, output, canvas);
            if (dirtyFlag)
                session.MarkDirty();
        }

        void EndSession()
        {
            session?.End();
            session = null;
        }

        void ReleaseCanvas()
        {
            canvas?.Dispose();
            canvas = null;
        }

        void ReleasePose()
        {
            if (posedMesh != null)
                DestroyImmediate(posedMesh);
            posedMesh = null;
            cachedHitMesh = null;
            cachedHitSlot = -1;
            cachedTriangles = null;
            cachedUV = null;
        }

        void ClearIslands()
        {
            islandMesh = null;
            islandSlot = -1;
            islandIds = null;
        }

        int[] GetIslands(Mesh mesh)
        {
            if (islandMesh != mesh || islandSlot != slot)
            {
                islandMesh = mesh;
                islandSlot = slot;
                islandIds = UVIslands.Islands(mesh, slot);
            }
            return islandIds;
        }

        void RefreshPreview()
        {
            Mesh mesh = SharedMesh();
            if (canvas != null && mesh != null)
                canvas.RefreshPreview(mesh, slot);
        }

        void MarkDirty()
        {
            RefreshPreview();
            SyncCanvasState();
            dirtyFlag = true;
            session?.MarkDirty();
            UpdateUnsavedState();
        }

        void ClearDirty()
        {
            dirtyFlag = false;
            session?.ClearDirty();
            UpdateUnsavedState();
        }

        void UpdateUnsavedState()
        {
            hasUnsavedChanges = IsDirty;
            saveChangesMessage = "Save the Channel Painter paint before closing?";
        }

        void SyncCanvasState()
        {
            if (canvas == null)
                return;
            canvasSize = canvas.Texture.width;
            reloadChannels = canvas.PaintedChannels;
        }

        void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
                EndSession();
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                StartSession();
                UpdateVertexColors(true);
            }
        }

        void BeforeAssemblyReload()
        {
            reloading = true;
            try
            {
                WriteCanvasFile();
            }
            finally
            {
                EndSession();
            }
        }

        // Also runs on a plain disable (maximize, layout change), so unsaved paint survives any window rebuild.
        void WriteCanvasFile()
        {
            if (canvas == null || target == null)
                return;
            SyncCanvasState();
            var raw = new Texture2D(canvasSize, canvasSize, TextureFormat.RGBAHalf, false, true);
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = canvas.Texture;
                raw.ReadPixels(new Rect(0, 0, canvasSize, canvasSize), 0, 0, false);
                raw.Apply(false, false);
                Directory.CreateDirectory(Path.GetDirectoryName(ReloadPath));
                File.WriteAllBytes(ReloadPath, raw.GetRawTextureData<byte>().ToArray());
            }
            finally
            {
                RenderTexture.active = previous;
                DestroyImmediate(raw);
            }
        }

        void OnUndoRedo()
        {
            if (session != null && session.AdoptMeshAfterUndo())
                UpdateVertexColors(true);
        }

        void RestoreAfterReload()
        {
            if (!File.Exists(ReloadPath) || target == null || canvasSize <= 0)
                return;
            try
            {
                byte[] bytes = File.ReadAllBytes(ReloadPath);
                if (bytes.Length != canvasSize * canvasSize * 8)
                    return;
                var raw = new Texture2D(canvasSize, canvasSize, TextureFormat.RGBAHalf, false, true);
                try
                {
                    raw.LoadRawTextureData(bytes);
                    raw.Apply(false, false);
                    canvas = new PaintCanvas(canvasSize, fill);
                    canvas.RestoreRaw(raw);
                    canvas.RestorePaintedChannels(reloadChannels);
                }
                finally
                {
                    DestroyImmediate(raw);
                }
                StartSession();
                UpdateVertexColors(true);
            }
            finally
            {
                File.Delete(ReloadPath);
            }
        }

        bool PaintMesh(out Mesh mesh, out Matrix4x4 matrix)
        {
            if (target == null)
            {
                mesh = null;
                matrix = Matrix4x4.identity;
                return false;
            }
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
            if (!paint || target == null || canvas == null || session == null)
                return;
            if (Event.current.type == EventType.Repaint)
                DrawSceneStatus(view);
            if (tool == PaintTool.FillIsland)
            {
                HandleFillSceneGUI(view);
                return;
            }
            HandleBrushSceneGUI(view);
        }

        void HandleFillSceneGUI(SceneView view)
        {
            Event evt = Event.current;
            if (evt.alt)
                return;
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (evt.type == EventType.MouseMove ||
                (evt.type == EventType.MouseDown && evt.button == 0))
            {
                if (UpdateSceneHit(evt, out _, out _, out RaycastHit hit) &&
                    evt.type == EventType.MouseDown && hasHit)
                    FillSceneTriangle(hit.triangleIndex);
                view.Repaint();
            }
            if (evt.button == 0 && (evt.type == EventType.MouseDown ||
                evt.type == EventType.MouseDrag || evt.type == EventType.MouseUp))
                evt.Use();
        }

        void HandleBrushSceneGUI(SceneView view)
        {
            Event evt = Event.current;
            if (HandleSceneMouseUp(evt))
                return;
            if (evt.alt)
                return;
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (HandleSceneKey(evt, view))
                return;
            if (evt.type == EventType.Repaint && hasHit)
            {
                Handles.color = BlendColor;
                Handles.DrawWireDisc(hitPoint, hitNormal, radius);
                Handles.color = Color.white;
            }
            if (evt.type != EventType.MouseMove && evt.type != EventType.MouseDown &&
                evt.type != EventType.MouseDrag)
                return;
            if (!UpdateSceneHit(evt, out Mesh mesh, out Matrix4x4 matrix, out RaycastHit hit))
                return;
            if (evt.type == EventType.MouseMove)
            {
                view.Repaint();
                return;
            }
            ApplyStroke(evt, hasHit, hit, mesh, matrix);
        }

        bool HandleSceneMouseUp(Event evt)
        {
            if (evt.type != EventType.MouseUp || evt.button != 0 || !strokeSnapshotTaken)
                return false;
            EndStroke();
            evt.Use();
            return true;
        }

        bool HandleSceneKey(Event evt, SceneView view)
        {
            if (evt.type != EventType.KeyDown ||
                (evt.keyCode != KeyCode.LeftBracket && evt.keyCode != KeyCode.RightBracket))
                return false;
            float step = evt.keyCode == KeyCode.RightBracket ? 1.15f : 1 / 1.15f;
            radius = Mathf.Clamp(radius * step, MinRadius, MaxRadius);
            evt.Use();
            Repaint();
            view.Repaint();
            return true;
        }

        bool UpdateSceneHit(Event evt, out Mesh mesh, out Matrix4x4 matrix, out RaycastHit hit)
        {
            hit = default;
            if (!PaintMesh(out mesh, out matrix))
            {
                hasHit = false;
                hasHoverUV = false;
                return false;
            }
            hasHit = RayHit(HandleUtility.GUIPointToWorldRay(evt.mousePosition), mesh, matrix, out hit);
            if (hasHit)
            {
                hitPoint = hit.point;
                hitNormal = hit.normal;
            }
            UpdateHoverUV(hasHit, hit);
            return true;
        }

        void UpdateHoverUV(bool found, RaycastHit hit)
        {
            hasHoverUV = false;
            Mesh mesh = TargetMesh;
            if (found && mesh != null && slot < mesh.subMeshCount)
            {
                if (cachedHitMesh != mesh || cachedHitSlot != slot)
                {
                    cachedHitMesh = mesh;
                    cachedHitSlot = slot;
                    cachedTriangles = mesh.GetTriangles(slot);
                    cachedUV = mesh.uv;
                }
                int triangle = SubmeshTriangle(mesh, hit.triangleIndex);
                if (triangle >= 0)
                {
                    int first = triangle * 3;
                    Vector3 bary = hit.barycentricCoordinate;
                    hoverUV = cachedUV[cachedTriangles[first]] * bary.x +
                        cachedUV[cachedTriangles[first + 1]] * bary.y +
                        cachedUV[cachedTriangles[first + 2]] * bary.z;
                    hasHoverUV = true;
                }
            }
            ChannelPainterUVWindow.Active?.Repaint();
        }

        int SubmeshTriangle(Mesh mesh, int wholeTriangle)
        {
            int first = 0;
            for (int i = 0; i < slot; i++)
                first += mesh.GetSubMesh(i).indexCount / 3;
            int local = wholeTriangle - first;
            return local >= 0 && local < mesh.GetSubMesh(slot).indexCount / 3 ? local : -1;
        }

        void FillSceneTriangle(int wholeTriangle)
        {
            Mesh mesh = TargetMesh;
            if (mesh == null)
                return;
            int triangle = SubmeshTriangle(mesh, wholeTriangle);
            if (triangle >= 0)
                FillIsland(triangle);
        }

        internal void FillIslandAtUV(Vector2 point)
        {
            if (!paint || tool != PaintTool.FillIsland || canvas == null || session == null)
                return;
            Mesh mesh = TargetMesh;
            if (mesh == null)
                return;
            Vector2[] uv = mesh.uv;
            int[] triangles = mesh.GetTriangles(slot);
            for (int triangle = 0; triangle < triangles.Length / 3; triangle++)
            {
                int first = triangle * 3;
                Vector2 a = uv[triangles[first]];
                Vector2 b = uv[triangles[first + 1]];
                Vector2 c = uv[triangles[first + 2]];
                float area = Cross(b - a, c - a);
                if (Mathf.Abs(area) < 1e-10f)
                    continue;
                float u = Cross(b - point, c - point) / area;
                float v = Cross(c - point, a - point) / area;
                float w = 1f - u - v;
                if (u >= -1e-6f && v >= -1e-6f && w >= -1e-6f)
                {
                    FillIsland(triangle);
                    return;
                }
            }
        }

        static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        void FillIsland(int triangle)
        {
            Mesh mesh = TargetMesh;
            int[] islands = GetIslands(mesh);
            FillTriangles(UVIslands.Triangles(mesh, slot, islands, islands[triangle]));
        }

        void FillTriangles(int[] triangles)
        {
            if (canvas == null || session == null || TargetMesh == null)
                return;
            canvas.PushUndo();
            canvas.BlendMode = blend;
            canvas.Fill(TargetMesh, slot, triangles, value, ChannelMask);
            MarkDirty();
            UpdateVertexColors(true);
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
            Repaint();
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
                Vector2 pressured = Pressured(strength, radius, PenPressure(evt), pressureStrength, pressureSize);
                canvas.BlendMode = blend;
                canvas.Paint(mesh, matrix, slot, hit.point, pressured.y, hardness, pressured.x, value, ChannelMask);
                MarkDirty();
                UpdateVertexColors(false);
                SceneView.RepaintAll();
                ChannelPainterUVWindow.Active?.Repaint();
            }
            evt.Use();
        }

        // A mouse reports no pressure, so only a pen scales the brush.
        public static float PenPressure(Event evt)
        {
            return evt.pointerType == PointerType.Pen ? Mathf.Clamp01(evt.pressure) : 1;
        }

        // x = strength, y = radius.
        public static Vector2 Pressured(float strength, float radius, float pressure, bool toStrength, bool toSize)
        {
            return new Vector2(toStrength ? strength * pressure : strength, toSize ? radius * pressure : radius);
        }

        internal void PaintUV(Vector2 uv, float radiusPixels, bool beginStroke, float pressure)
        {
            if (canvas == null || session == null || TargetMesh == null)
                return;
            if (beginStroke)
                strokeSnapshotTaken = false;
            if (!strokeSnapshotTaken)
            {
                canvas.PushUndo();
                strokeSnapshotTaken = true;
            }
            Vector2 pressured = Pressured(strength, radiusPixels, pressure, pressureStrength, pressureSize);
            canvas.BlendMode = blend;
            canvas.PaintUV(TargetMesh, slot, uv, pressured.y, hardness, pressured.x, value, ChannelMask);
            MarkDirty();
            UpdateVertexColors(false);
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
        }

        internal void EndUVStroke()
        {
            EndStroke();
        }

        void EndStroke()
        {
            if (strokeSnapshotTaken)
                UpdateVertexColors(true);
            strokeSnapshotTaken = false;
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
        }

        void UpdateVertexColors(bool force)
        {
            if (session == null || !session.IsVertexColor || canvas == null)
                return;
            double now = EditorApplication.timeSinceStartup;
            if (!force && now - lastVertexUpdate < 0.1)
                return;
            if (!PaintMesh(out Mesh coverageMesh, out _))
                return;
            session.SetColors(canvas.SampleVertices(session.OriginalMesh, coverageMesh, slot));
            lastVertexUpdate = now;
        }

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!showMask || camera.cameraType != CameraType.SceneView || viewMaterial == null || target == null)
                return;
            if (!maskVertexColor && (canvas == null || session == null))
                return;
            if (!PaintMesh(out Mesh mesh, out Matrix4x4 matrix))
                return;
            if (canvas != null)
                viewMaterial.SetTexture("_MainTex", canvas.Preview);
            viewMaterial.SetInt("_ViewChannel", viewChannel);
            viewMaterial.SetFloat("_UseVertexColor", maskVertexColor ? 1 : 0);
            viewMaterial.SetFloat("_HasVertexColor", mesh.HasVertexAttribute(VertexAttribute.Color) ? 1 : 0);
            viewMaterial.SetMatrix("_MaskMatrix", matrix);
            Graphics.DrawMesh(mesh, matrix, viewMaterial, 0, camera, slot);
        }

        bool SaveCurrentOutput()
        {
            return output == PaintOutput.Map ? SaveMap() : BakeVertexColor();
        }

        bool SaveMap(bool saveAs = false)
        {
            if (canvas == null || !PaintMesh(out Mesh mesh, out _))
                return false;
            Texture2D assigned = PropertyTexture as Texture2D;
            string path = assigned == null ? null : AssetDatabase.GetAssetPath(assigned);
            if (saveAs || string.IsNullOrEmpty(path) ||
                !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                path = EditorUtility.SaveFilePanelInProject("Save painted map",
                    CurrentMaterial().name + propertyName, "png", "Save the painted control map.");
            if (string.IsNullOrEmpty(path))
                return false;

            Texture2D painted = canvas.ReadbackDilated(mesh, slot);
            var png = new Texture2D(painted.width, painted.height, TextureFormat.RGBA32, false, true);
            try
            {
                png.SetPixels(painted.GetPixels());
                png.Apply(false, false);
                File.WriteAllBytes(path, png.EncodeToPNG());
            }
            finally
            {
                DestroyImmediate(png);
                DestroyImmediate(painted);
            }
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = Mathf.Max(importer.maxTextureSize, canvas.Texture.width);
            importer.SaveAndReimport();

            if (!string.IsNullOrEmpty(propertyName))
            {
                Material material = CurrentMaterial();
                Undo.RecordObject(material, "Assign Channel Painter Map");
                material.SetTexture(propertyName, AssetDatabase.LoadAssetAtPath<Texture2D>(path));
                string keyword = KeywordFor(material.shader, propertyName);
                if (keyword != null)
                    material.EnableKeyword(keyword);
                EditorUtility.SetDirty(material);
            }
            if (output == PaintOutput.Map)
                ClearDirty();
            return true;
        }

        bool BakeVertexColor()
        {
            if (canvas == null || !PaintMesh(out Mesh mesh, out _))
                return false;
            StartSession();
            if (session == null)
                return false;
            Color[] colors = canvas.SampleVertices(session.OriginalMesh, mesh, slot);
            Mesh baked = VertexColorBake.Bake(session.OriginalMesh, colors);
            if (baked == null)
                return false;
            session.AssignBakedMesh(baked);
            if (output == PaintOutput.VertexColor)
                ClearDirty();
            SceneView.RepaintAll();
            return true;
        }
    }
}
