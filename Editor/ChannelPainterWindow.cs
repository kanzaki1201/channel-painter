using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;
using PointerType = UnityEngine.PointerType;

namespace Malloc.ChannelPainter.Editor
{
    public sealed class ChannelPainterWindow : EditorWindow
    {
        enum PaintTool { Brush, FillIsland }
        enum BrushSpace { World, Screen }
        enum ValueMode { ChannelValue, Color }

        static readonly int[] Sizes = { 512, 1024, 2048, 4096 };
        static readonly string[] SizeLabels = { "512", "1024", "2048", "4096" };
        static readonly string[] OutputLabels = { "Map", "Vertex color" };
        static readonly string[] MaskSourceLabels = { "Canvas", "Vertex Color" };
        static readonly string[] ChannelNames = { "R", "G", "B", "A" };
        static readonly string[] ViewLabels = { "RGBA", "R", "G", "B", "A" };
        static readonly string[] BrushSpaceLabels = { "World", "Screen" };
        static readonly string[] ValueModeLabels = { "Channel Value", "Color" };
        static readonly string[] BlendLabels = { "Replace", "Add", "Subtract" };
        const int SwatchCount = 8;
        const int PickerId = 0x43504E54;
        const string StylePath = "Packages/com.malloc.channel-painter/Editor/ChannelPainterWindow.uss";
        const float MinRadius = 0.001f;
        const float MaxRadius = 1f;
        const float MinScreenRadius = 2f;
        const float MaxScreenRadius = 512f;
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
        [SerializeField] float screenRadius = 40f;
        [SerializeField] BrushSpace brushSpace = BrushSpace.Screen;
        [SerializeField] float hardness = 0.5f;
        [SerializeField] float strength = 1;
        [SerializeField] BrushBlend blend;
        [SerializeField] bool pressureStrength = true;
        [SerializeField] bool pressureSize;
        [SerializeField] bool hideCursor = true;
        static Texture2D hiddenCursor;
        [SerializeField] bool red = true;
        [SerializeField] bool green = true;
        [SerializeField] bool blue = true;
        [SerializeField] bool alpha = true;
        [SerializeField] bool paint;
        [SerializeField] PaintTool tool;
        [SerializeField] bool showMask;
        [SerializeField] bool maskVertexColor;
        [SerializeField] int copyFrom;
        [SerializeField] int copyTo = 1;
        [SerializeField] bool dirtyFlag;
        [SerializeField] int viewChannel;
        [SerializeField] PaintOutput output;
        [SerializeField] Vector4 reloadChannels;
        [SerializeField] ValueMode valueMode;
        [SerializeField] Color brushColor = Color.white;
        [SerializeField] List<Color> swatches = new List<Color>();
        [SerializeField] bool useRange;
        [SerializeField] float rangeMin = -1;
        [SerializeField] float rangeMax = 1;
        bool eyedropperArmed;
        bool swallowUntilMouseUp;

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
        StrokeStamper stamper;
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
            : blend == BrushBlend.Subtract ? new Color(1f, 0.4f, 0.4f) : new Color(0.35f, 0.75f, 1f);
        internal string StatusText => "Channel Painter  |  " + (tool == PaintTool.FillIsland ? "Fill Island" : "Brush") +
            "  |  " + blend + "  |  " + ChannelText + "  (A add, S subtract, R replace, 1-4 toggle RGBA)";
        string ChannelText
        {
            get
            {
                string text = (red ? "R" : "") + (green ? "G" : "") + (blue ? "B" : "") + (alpha ? "A" : "");
                return text.Length == 0 ? "no channels" : text;
            }
        }

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

        [Shortcut("Channel Painter/Toggle Red", typeof(PaintShortcutContext), KeyCode.Alpha1)]
        static void ShortcutRed() => Active?.ToggleChannel(0);

        [Shortcut("Channel Painter/Toggle Green", typeof(PaintShortcutContext), KeyCode.Alpha2)]
        static void ShortcutGreen() => Active?.ToggleChannel(1);

        [Shortcut("Channel Painter/Toggle Blue", typeof(PaintShortcutContext), KeyCode.Alpha3)]
        static void ShortcutBlue() => Active?.ToggleChannel(2);

        [Shortcut("Channel Painter/Toggle Alpha", typeof(PaintShortcutContext), KeyCode.Alpha4)]
        static void ShortcutAlpha() => Active?.ToggleChannel(3);

        void ToggleChannel(int channel)
        {
            if (channel == 0) red = !red;
            else if (channel == 1) green = !green;
            else if (channel == 2) blue = !blue;
            else alpha = !alpha;
            RefreshPanel();
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
        }

        // A dark outline under the colored circle keeps it visible on light and dark backgrounds.
        internal static void DrawBrushCircle(Vector3 center, Vector3 normal, float radius, Color color)
        {
            const int Segments = 64;
            Vector3 tangent = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.99f ? Vector3.up : Vector3.right).normalized;
            Vector3 bitangent = Vector3.Cross(normal, tangent);
            var points = new Vector3[Segments + 1];
            for (int i = 0; i <= Segments; i++)
            {
                float angle = i * Mathf.PI * 2 / Segments;
                points[i] = center + (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * radius;
            }
            Handles.color = new Color(0, 0, 0, 0.85f);
            Handles.DrawAAPolyLine(4f, points);
            Handles.color = color;
            Handles.DrawAAPolyLine(2f, points);
            Handles.color = Color.white;
        }

        // Only the Brush tool hides the cursor, because Fill Island draws no circle to aim with.
        internal bool HidesCursor => paint && hideCursor && tool == PaintTool.Brush && !eyedropperArmed;

        internal void ApplyPaintCursor(Rect rect)
        {
            if (HidesCursor)
            {
                if (hiddenCursor == null)
                {
                    hiddenCursor = new Texture2D(16, 16, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                    hiddenCursor.SetPixels32(new Color32[16 * 16]);
                    hiddenCursor.Apply();
                }
                Cursor.SetCursor(hiddenCursor, Vector2.zero, CursorMode.Auto);
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.CustomCursor);
            }
            else
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Arrow);
        }

        static void ResetCursor()
        {
            Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
        }

        void SetBlend(BrushBlend next)
        {
            blend = next;
            RefreshPanel();
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
                RefreshPanel();
                SceneView.RepaintAll();
                ChannelPainterUVWindow.Active?.Repaint();
            }
        }

        Vector4 ChannelMask => new Vector4(red ? 1 : 0, green ? 1 : 0,
            blue ? 1 : 0, alpha ? 1 : 0);
        Vector4 BrushValue => valueMode == ValueMode.Color ? (Vector4)brushColor : new Vector4(value, value, value, value);
        internal bool EyedropperArmed => eyedropperArmed;
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
            ResetCursor();
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

        ObjectField targetField;
        HelpBox noTargetBox;
        HelpBox noSlotBox;
        HelpBox noMaterialBox;
        HelpBox noPropertiesBox;
        HelpBox keywordBox;
        HelpBox noTextureBox;
        HelpBox noUVBox;
        HelpBox rangeWarning;
        HelpBox detachedBox;
        HelpBox vertexSessionBox;
        DropdownField slotField;
        VisualElement outputRow;
        Button[] outputButtons;
        DropdownField propertyField;
        DropdownField sizeField;
        DropdownField copyFromField;
        DropdownField copyToField;
        DropdownField viewChannelField;
        DropdownField maskSourceField;
        Button enableKeywordButton;
        Button newCanvasButton;
        Button loadPropertyButton;
        Button resetMeshButton;
        Button copyButton;
        Button startButton;
        Button fillCanvasButton;
        Button undoButton;
        Button eyedropperButton;
        Button restartButton;
        Button bakeButton;
        Button[] toolButtons;
        Button[] valueModeButtons;
        Button[] blendButtons;
        Button[] brushSpaceButtons;
        Button[] channelButtons;
        Button[] swatchButtons;
        ColorField fillField;
        ColorField colorField;
        Slider valueSlider;
        Slider radiusSlider;
        Slider screenRadiusSlider;
        Slider hardnessSlider;
        Slider strengthSlider;
        Toggle pressureStrengthToggle;
        Toggle pressureSizeToggle;
        Toggle showMaskToggle;
        Toggle hideCursorToggle;
        Toggle rangeToggle;
        FloatField rangeMinField;
        FloatField rangeMaxField;
        Foldout canvasSection;
        Foldout paintSection;
        Foldout viewSection;
        Foldout saveSection;
        VisualElement brushSpaceRow;
        VisualElement swatchRow;
        VisualElement saveMapRow;
        IMGUIContainer pickerCatcher;
        static Texture[] toolIcons;
        static Texture eyedropperIcon;

        void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.Clear();
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (sheet != null)
                root.styleSheets.Add(sheet);
            root.AddToClassList("cp-root");
            if (toolIcons == null)
            {
                toolIcons = new[]
                {
                    EditorGUIUtility.IconContent("Grid.PaintTool").image,
                    EditorGUIUtility.IconContent("Grid.FillTool").image
                };
                eyedropperIcon = EditorGUIUtility.IconContent("eyeDropper.Large").image;
            }

            // The object picker sends ObjectSelectorClosed to the focused IMGUIContainer, so it stays outside every Foldout.
            pickerCatcher = new IMGUIContainer(OnPickerGUI) { focusable = true };
            root.Add(pickerCatcher);
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            root.Add(scroll);

            Foldout targetSection = Section(scroll, "Target");
            targetField = new ObjectField("Target") { objectType = typeof(Renderer), allowSceneObjects = true };
            targetField.RegisterValueChangedCallback(evt =>
            {
                var chosen = evt.newValue as Renderer;
                if (chosen != target)
                    ChangeSelection(() => SetTarget(chosen));
            });
            targetSection.Add(targetField);
            noTargetBox = Box(targetSection, "Choose a MeshRenderer with a MeshFilter or a SkinnedMeshRenderer.", HelpBoxMessageType.Info);
            noSlotBox = Box(targetSection, "The target needs a mesh and a material slot.", HelpBoxMessageType.Error);
            slotField = Dropdown(targetSection, "Material Slot", null, index =>
            {
                if (index != slot)
                    ChangeSelection(() =>
                    {
                        ResetSession();
                        slot = index;
                        propertyName = null;
                    });
            });
            noMaterialBox = Box(targetSection, "Choose a material for this slot.", HelpBoxMessageType.Error);
            outputRow = Segmented(targetSection, "Output", OutputLabels,
                "Map paints into a texture property. Vertex color paints the mesh colors.", index =>
            {
                var chosen = (PaintOutput)index;
                if (chosen != output)
                    ChangeSelection(() => SwitchOutput(chosen));
            }, out outputButtons);
            noPropertiesBox = Box(targetSection, "This shader has no texture properties.", HelpBoxMessageType.Error);
            propertyField = Dropdown(targetSection, "Texture Property", null, index =>
            {
                string chosen = propertyField.value;
                if (chosen != propertyName)
                    ChangeSelection(() =>
                    {
                        ResetSession();
                        propertyName = chosen;
                    });
            });
            keywordBox = Box(targetSection, "", HelpBoxMessageType.Warning);
            enableKeywordButton = AddButton(targetSection, "", null, () =>
            {
                Material material = CurrentMaterial();
                string keyword = material == null || string.IsNullOrEmpty(propertyName) ? null : KeywordFor(material.shader, propertyName);
                if (keyword != null)
                    EnableKeyword(material, keyword);
                RefreshPanel();
            });
            noTextureBox = Box(targetSection, "This property has no texture. Assign one in the material inspector if the shader needs a keyword to use it.", HelpBoxMessageType.Warning);
            noUVBox = Box(targetSection, "The target mesh has no UV0.", HelpBoxMessageType.Error);

            canvasSection = Section(scroll, "Canvas");
            sizeField = Dropdown(canvasSection, "Size", SizeLabels, index => sizeIndex = index);
            fillField = new ColorField("Fill Color") { showAlpha = true, hdr = false };
            fillField.RegisterValueChangedCallback(evt => fill = evt.newValue);
            canvasSection.Add(fillField);
            newCanvasButton = AddButton(canvasSection, "New Canvas", null, () => ChangeSelection(NewCanvas));
            loadPropertyButton = AddButton(canvasSection, "", null, () =>
            {
                Texture assigned = PropertyTexture;
                if (assigned != null)
                    Load(assigned);
                RefreshPanel();
            });
            resetMeshButton = AddButton(canvasSection, "Reset To Mesh Colors",
                "Replace the canvas with the vertex colors of the original mesh.", () => ChangeSelection(NewCanvas));
            AddButton(canvasSection, "Import Texture...", "Pick any texture and import it into the canvas.", () =>
            {
                pickerCatcher.Focus();
                EditorGUIUtility.ShowObjectPicker<Texture>(null, false, "", PickerId);
            });

            paintSection = Section(scroll, "Paint");
            startButton = AddButton(paintSection, "Start Painting", null, ToggleStartPainting);
            startButton.AddToClassList("cp-primary");
            var toolRow = new VisualElement();
            toolRow.AddToClassList("cp-tool-row");
            paintSection.Add(toolRow);
            toolButtons = new[]
            {
                ToolButton(toolRow, toolIcons[0], "Brush", "Paint strokes.", () => SetTool(PaintTool.Brush)),
                ToolButton(toolRow, toolIcons[1], "Fill Island", "Fill the UV island you click.", () => SetTool(PaintTool.FillIsland))
            };
            var toolSpacer = new VisualElement();
            toolSpacer.AddToClassList("cp-spacer");
            toolRow.Add(toolSpacer);
            fillCanvasButton = AddButton(toolRow, "Fill Canvas", "Apply Value or Color to every texel of the target submesh.", () => FillTriangles(null));
            undoButton = AddButton(toolRow, "Undo", "Undo the last stroke or fill.", UndoCanvas);
            Segmented(paintSection, "Value Mode", ValueModeLabels,
                "Channel Value paints one value into the enabled channels. Color paints an RGBA color, masked by the enabled channels.",
                index =>
                {
                    valueMode = (ValueMode)index;
                    SceneView.RepaintAll();
                    RefreshPanel();
                }, out valueModeButtons);
            Segmented(paintSection, "Blend", BlendLabels,
                "Replace sets the value. Add and Subtract change the current value by Value.",
                index => SetBlend((BrushBlend)index), out blendButtons);
            var valueRow = new VisualElement();
            valueRow.AddToClassList("cp-value-row");
            paintSection.Add(valueRow);
            valueSlider = AddSlider(valueRow, "Value", 0, 1, display =>
            {
                Vector2 range = ValueDisplayRange();
                value = RemapFromDisplay(display, range.x, range.y);
                SceneView.RepaintAll();
            });
            colorField = new ColorField("Color") { showAlpha = true, hdr = false };
            colorField.RegisterValueChangedCallback(evt =>
            {
                brushColor = evt.newValue;
                SceneView.RepaintAll();
            });
            valueRow.Add(colorField);
            eyedropperButton = new Button(ToggleEyedropper)
            {
                tooltip = "Pick from the canvas: click the mesh in the Scene view or the canvas in the UV window."
            };
            eyedropperButton.AddToClassList("cp-eyedropper");
            if (eyedropperIcon != null)
                eyedropperButton.Add(new Image { image = eyedropperIcon });
            else
                eyedropperButton.text = "Pick";
            valueRow.Add(eyedropperButton);
            swatchRow = Row(paintSection, "Recent Colors", "Click a swatch to paint with it.");
            swatchRow.AddToClassList("cp-swatch-row");
            swatchButtons = new Button[SwatchCount];
            for (int i = 0; i < SwatchCount; i++)
            {
                int index = i;
                swatchButtons[i] = new Button(() => PickSwatch(index));
                swatchButtons[i].AddToClassList("cp-swatch");
                swatchRow.Add(swatchButtons[i]);
            }
            brushSpaceRow = Segmented(paintSection, "Brush Space", BrushSpaceLabels,
                "World: a sphere around the point under the cursor. Screen: a circle on screen that paints only the front-most surface of the target.",
                index =>
                {
                    brushSpace = (BrushSpace)index;
                    hasHit = false;
                    hasHoverUV = false;
                    SceneView.RepaintAll();
                    ChannelPainterUVWindow.Active?.Repaint();
                    RefreshPanel();
                }, out brushSpaceButtons);
            radiusSlider = AddSlider(paintSection, "Radius (world units)", MinRadius, MaxRadius, next =>
            {
                radius = next;
                SceneView.RepaintAll();
            });
            radiusSlider.tooltip = "[ and ] in the scene view.";
            screenRadiusSlider = AddSlider(paintSection, "Screen Radius (px)", MinScreenRadius, MaxScreenRadius, next =>
            {
                screenRadius = next;
                SceneView.RepaintAll();
            });
            hardnessSlider = AddSlider(paintSection, "Hardness", 0, 1, next => hardness = next);
            strengthSlider = AddSlider(paintSection, "Strength", 0, 1, next => strength = next);
            pressureStrengthToggle = AddToggle(paintSection, "Pen Pressure → Strength", null, next => pressureStrength = next);
            pressureSizeToggle = AddToggle(paintSection, "Pen Pressure → Size", null, next => pressureSize = next);
            VisualElement channelRow = Row(paintSection, "Paint On Channels", null);
            channelButtons = new Button[ChannelNames.Length];
            for (int i = 0; i < ChannelNames.Length; i++)
            {
                int channel = i;
                channelButtons[i] = new Button(() => ToggleChannel(channel)) { text = ChannelNames[i] };
                channelButtons[i].AddToClassList("cp-channel");
                channelButtons[i].AddToClassList("cp-channel-" + ChannelNames[i].ToLowerInvariant());
                channelRow.Add(channelButtons[i]);
            }

            // One-off channel operations sit apart from painting, collapsed by default.
            Foldout toolsSection = Section(scroll, "Channel Tools");
            toolsSection.value = false;
            VisualElement copyRow = Row(toolsSection, "Copy Channel", "Copy one channel of the canvas into another.");
            copyFromField = Dropdown(copyRow, null, ChannelNames, index =>
            {
                copyFrom = index;
                RefreshPanel();
            });
            var arrow = new Label("→");
            arrow.AddToClassList("cp-arrow");
            copyRow.Add(arrow);
            copyToField = Dropdown(copyRow, null, ChannelNames, index =>
            {
                copyTo = index;
                RefreshPanel();
            });
            copyButton = AddButton(copyRow, "Copy", null, () =>
            {
                if (canvas == null || copyFrom == copyTo)
                    return;
                canvas.PushUndo();
                canvas.CopyChannel(copyFrom, copyTo);
                MarkDirty();
                UpdateVertexColors(true);
                SceneView.RepaintAll();
                ChannelPainterUVWindow.Active?.Repaint();
                RefreshPanel();
            });

            viewSection = Section(scroll, "View");
            viewChannelField = Dropdown(viewSection, "Show Channel", ViewLabels, index => ViewChannel = index);
            showMaskToggle = AddToggle(viewSection, "Show Mask In Scene View", null, next =>
            {
                showMask = next;
                SceneView.RepaintAll();
            });
            maskSourceField = Dropdown(viewSection, "Mask Source", MaskSourceLabels, index =>
            {
                maskVertexColor = index == 1;
                SceneView.RepaintAll();
            });
            maskSourceField.tooltip = "Canvas shows the paint. Vertex Color shows the mesh's current vertex colors, with or without a canvas.";
            hideCursorToggle = AddToggle(viewSection, "Hide Mouse Cursor While Painting",
                "Show only the brush circle while the Brush tool paints.", next =>
                {
                    hideCursor = next;
                    ResetCursor();
                    SceneView.RepaintAll();
                });
            rangeToggle = AddToggle(viewSection, "Value Range",
                "Show Value in shader units. Replace shows lerp(Min, Max, Value). Add and Subtract show Value × (Max − Min). The stored value stays 0 to 1.",
                next =>
                {
                    useRange = next;
                    RefreshPanel();
                });
            rangeMinField = AddFloat(viewSection, "Min", next =>
            {
                rangeMin = next;
                RefreshPanel();
            });
            rangeMaxField = AddFloat(viewSection, "Max", next =>
            {
                rangeMax = next;
                RefreshPanel();
            });
            rangeWarning = Box(viewSection, "Max must be greater than Min, so Value shows 0 to 1.", HelpBoxMessageType.Warning);
            AddButton(viewSection, "Open UV Window", null, ChannelPainterUVWindow.Open);

            saveSection = Section(scroll, "Save");
            saveMapRow = new VisualElement();
            saveMapRow.AddToClassList("cp-button-row");
            saveSection.Add(saveMapRow);
            AddButton(saveMapRow, "Save Map", "Overwrite the PNG assigned to the material, or ask for a path when none is assigned.", () =>
            {
                SaveMap();
                RefreshPanel();
            });
            AddButton(saveMapRow, "Save Map As...", "Save to a new PNG and assign it.", () =>
            {
                SaveMap(true);
                RefreshPanel();
            });
            bakeButton = AddButton(saveSection, "Bake To Vertex Color", null, () =>
            {
                BakeVertexColor();
                RefreshPanel();
            });

            detachedBox = Box(scroll, "Another tool replaced the mesh on this renderer, so the live preview no longer shows. Restart the session on the new mesh.", HelpBoxMessageType.Warning);
            restartButton = AddButton(scroll, "Restart Session On Current Mesh", null, () => ChangeSelection(NewCanvas));
            vertexSessionBox = Box(scroll, "End the Vertex color session before Apply Overrides or dragging this object into the Project window.", HelpBoxMessageType.Warning);

            RefreshPanel();
            // Inspector edits, keyword changes and play mode changes have no callback here.
            root.schedule.Execute(RefreshPanel).Every(250);
        }

        // Pushes every field into the panel. Shortcuts and scene events change fields outside the panel.
        internal void RefreshPanel()
        {
            if (targetField == null)
                return;
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            bool map = output == PaintOutput.Map;
            Sync(targetField, (UnityEngine.Object)target);
            Mesh mesh = TargetMesh;
            Material[] materials = target == null ? null : target.sharedMaterials;
            int count = mesh == null ? 0 : Mathf.Min(mesh.subMeshCount, materials.Length);
            Material material = count == 0 ? null : CurrentMaterial();
            bool hasUV = mesh != null && mesh.HasVertexAttribute(VertexAttribute.TexCoord0);
            string[] properties = material != null && map ? TextureProperties(material.shader) : Array.Empty<string>();
            // Auto-select the first texture property; with unsaved paint the user picks one instead.
            if (properties.Length > 0 && Array.IndexOf(properties, propertyName) < 0 && !IsDirty)
            {
                ResetSession();
                propertyName = properties[0];
            }
            bool hasProperty = Array.IndexOf(properties, propertyName) >= 0;

            Show(noTargetBox, target == null);
            Show(noSlotBox, target != null && count == 0);
            Show(slotField, count > 0);
            if (count > 0)
            {
                var labels = new List<string>(count);
                for (int i = 0; i < count; i++)
                    labels.Add(i + ": " + (materials[i] == null ? "None" : materials[i].name));
                SetChoices(slotField, labels);
                SyncIndex(slotField, Mathf.Clamp(slot, 0, count - 1));
            }
            Show(noMaterialBox, count > 0 && material == null);
            Show(outputRow, material != null);
            SetActive(outputButtons, (int)output);
            Show(noPropertiesBox, material != null && map && properties.Length == 0);
            Show(propertyField, properties.Length > 0);
            if (properties.Length > 0)
            {
                SetChoices(propertyField, properties.ToList());
                Sync(propertyField, propertyName);
            }
            string keyword = hasProperty ? KeywordFor(material.shader, propertyName) : null;
            bool keywordOff = keyword != null && !material.IsKeywordEnabled(keyword);
            Show(keywordBox, keywordOff);
            Show(enableKeywordButton, keywordOff);
            if (keywordOff)
            {
                keywordBox.text = keyword + " is off, so the shader ignores " + propertyName + ".";
                enableKeywordButton.text = "Enable " + keyword;
            }
            Show(noTextureBox, hasProperty && keyword == null && material.GetTexture(propertyName) == null);
            Show(noUVBox, material != null && mesh != null && !hasUV);

            bool ready = target != null && hasUV && material != null && HasOutputProperty;
            Show(canvasSection, ready);
            Show(paintSection, ready);
            Show(viewSection, ready);
            Show(saveSection, ready);
            bool detached = ready && session != null && session.Detached;
            Show(detachedBox, detached);
            Show(restartButton, detached);
            Show(vertexSessionBox, ready && session != null && session.IsVertexColor);
            if (!ready)
                return;

            SyncIndex(sizeField, sizeIndex);
            Show(fillField, map);
            Sync(fillField, fill);
            Show(newCanvasButton, map);
            Texture assigned = map ? PropertyTexture : null;
            newCanvasButton.tooltip = assigned == null
                ? "Start a canvas filled with Fill Color. The material has no " + propertyName + ", so this also saves the canvas as a new PNG and assigns it."
                : "Start a canvas filled with Fill Color.";
            Show(loadPropertyButton, map);
            loadPropertyButton.text = "Import " + propertyName + " From Material";
            loadPropertyButton.tooltip = "Replace the canvas with the map that the material holds in " + propertyName + ".";
            loadPropertyButton.SetEnabled(assigned != null);
            Show(resetMeshButton, !map);
            SyncIndex(copyFromField, copyFrom);
            SyncIndex(copyToField, copyTo);
            copyButton.SetEnabled(canvas != null && copyFrom != copyTo);

            startButton.text = paint ? "Stop Painting" : "Start Painting";
            startButton.EnableInClassList("cp-painting", paint);
            startButton.SetEnabled(!playing);
            SetActive(toolButtons, (int)tool);
            fillCanvasButton.SetEnabled(canvas != null && session != null && !playing);
            undoButton.SetEnabled(canvas != null && canvas.CanUndo);
            SetActive(valueModeButtons, (int)valueMode);
            SetActive(blendButtons, (int)blend);
            bool colorMode = valueMode == ValueMode.Color;
            Show(valueSlider, !colorMode);
            Vector2 range = ValueDisplayRange();
            SetSliderRange(valueSlider, range.x, range.y, RemapToDisplay(value, range.x, range.y));
            Show(colorField, colorMode);
            Sync(colorField, brushColor);
            eyedropperButton.EnableInClassList("cp-active", eyedropperArmed);
            eyedropperButton.SetEnabled(canvas != null);
            Show(swatchRow, colorMode);
            for (int i = 0; i < SwatchCount; i++)
            {
                bool has = i < swatches.Count;
                swatchButtons[i].SetEnabled(has);
                swatchButtons[i].EnableInClassList("cp-empty", !has);
                swatchButtons[i].style.backgroundColor = has ? new StyleColor(swatches[i]) : new StyleColor(StyleKeyword.Null);
            }
            bool brush = tool == PaintTool.Brush;
            Show(brushSpaceRow, brush);
            SetActive(brushSpaceButtons, (int)brushSpace);
            Show(radiusSlider, brush && brushSpace == BrushSpace.World);
            Sync(radiusSlider, radius);
            Show(screenRadiusSlider, brush && brushSpace == BrushSpace.Screen);
            Sync(screenRadiusSlider, screenRadius);
            Show(hardnessSlider, brush);
            Sync(hardnessSlider, hardness);
            Show(strengthSlider, brush);
            Sync(strengthSlider, strength);
            Show(pressureStrengthToggle, brush);
            Sync(pressureStrengthToggle, pressureStrength);
            Show(pressureSizeToggle, brush);
            Sync(pressureSizeToggle, pressureSize);
            Vector4 mask = ChannelMask;
            for (int i = 0; i < channelButtons.Length; i++)
                channelButtons[i].EnableInClassList("cp-on", mask[i] > 0);

            SyncIndex(viewChannelField, viewChannel);
            Sync(showMaskToggle, showMask);
            Show(maskSourceField, map);
            SyncIndex(maskSourceField, maskVertexColor ? 1 : 0);
            Sync(hideCursorToggle, hideCursor);
            Sync(rangeToggle, useRange);
            Show(rangeMinField, useRange);
            Sync(rangeMinField, rangeMin);
            Show(rangeMaxField, useRange);
            Sync(rangeMaxField, rangeMax);
            Show(rangeWarning, useRange && rangeMax <= rangeMin);

            Show(saveMapRow, map);
            saveMapRow.SetEnabled(canvas != null);
            Show(bakeButton, !map);
            bakeButton.SetEnabled(canvas != null);
        }

        void OnPickerGUI()
        {
            Event evt = Event.current;
            if (evt.type != EventType.ExecuteCommand || evt.commandName != "ObjectSelectorClosed" ||
                EditorGUIUtility.GetObjectPickerControlID() != PickerId)
                return;
            if (EditorGUIUtility.GetObjectPickerObject() is Texture picked)
                Load(picked);
            evt.Use();
            RefreshPanel();
        }

        // On Cancel nothing changes, and RefreshPanel puts the old value back into the control.
        void ChangeSelection(Action change)
        {
            if (ConfirmSessionEnd())
                change();
            RefreshPanel();
        }

        void ToggleStartPainting()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (!paint && canvas == null)
                CreateStartCanvas();
            paint = !paint && canvas != null;
            hasHit = false;
            if (!paint)
            {
                EndStroke();
                ResetCursor();
                eyedropperArmed = false;
            }
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
            RefreshPanel();
        }

        void SetTool(PaintTool next)
        {
            tool = next;
            hasHit = false;
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
            RefreshPanel();
        }

        void UndoCanvas()
        {
            if (canvas == null || !canvas.CanUndo)
                return;
            canvas.Undo();
            MarkDirty();
            UpdateVertexColors(true);
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
            RefreshPanel();
        }

        void ToggleEyedropper()
        {
            eyedropperArmed = !eyedropperArmed && canvas != null;
            ResetCursor();
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
            RefreshPanel();
        }

        // Samples the stored canvas, so it returns the value the shader reads, not the lit screen.
        internal void PickAt(Vector2 uv)
        {
            if (canvas == null)
                return;
            Color picked = canvas.Sample(uv);
            if (valueMode == ValueMode.Color)
                brushColor = picked;
            else
                value = Mathf.Clamp01(picked[Mathf.Max(Array.IndexOf(new[] { red, green, blue, alpha }, true), 0)]);
            eyedropperArmed = false;
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
            RefreshPanel();
        }

        void PickSwatch(int index)
        {
            if (index >= swatches.Count)
                return;
            brushColor = swatches[index];
            RefreshPanel();
        }

        void PushSwatch()
        {
            if (valueMode != ValueMode.Color)
                return;
            swatches.Remove(brushColor);
            swatches.Insert(0, brushColor);
            if (swatches.Count > SwatchCount)
                swatches.RemoveRange(SwatchCount, swatches.Count - SwatchCount);
        }

        // Replace shows Value inside [min, max]. Add and Subtract show it as an amount of that span.
        Vector2 ValueDisplayRange()
        {
            if (!useRange || rangeMax <= rangeMin)
                return new Vector2(0, 1);
            return blend == BrushBlend.Replace ? new Vector2(rangeMin, rangeMax) : new Vector2(0, rangeMax - rangeMin);
        }

        public static float RemapToDisplay(float value, float min, float max)
        {
            return Mathf.LerpUnclamped(min, max, value);
        }

        public static float RemapFromDisplay(float display, float min, float max)
        {
            return max > min ? Mathf.Clamp01((display - min) / (max - min)) : 0f;
        }

        // Widening first keeps the current value inside the range, so no clamp sends a change event.
        static void SetSliderRange(Slider slider, float low, float high, float display)
        {
            slider.lowValue = Mathf.Min(slider.lowValue, low);
            slider.highValue = Mathf.Max(slider.highValue, high);
            if (Mathf.Abs(slider.value - display) > Mathf.Abs(high - low) * 1e-6f)
                slider.SetValueWithoutNotify(display);
            slider.lowValue = low;
            slider.highValue = high;
        }

        // Skips equal values, so a 250 ms refresh does not rewrite a field the user is typing in.
        static void Sync<T>(BaseField<T> field, T next)
        {
            if (!EqualityComparer<T>.Default.Equals(field.value, next))
                field.SetValueWithoutNotify(next);
        }

        static void SyncIndex(DropdownField field, int index)
        {
            Sync(field, index >= 0 && index < field.choices.Count ? field.choices[index] : null);
        }

        // Reassigning equal choices would close an open dropdown on every refresh.
        static void SetChoices(DropdownField field, List<string> choices)
        {
            if (field.choices == null || !field.choices.SequenceEqual(choices))
                field.choices = choices;
        }

        static void Show(VisualElement element, bool visible)
        {
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        static void SetActive(Button[] buttons, int active)
        {
            for (int i = 0; i < buttons.Length; i++)
                buttons[i].EnableInClassList("cp-active", i == active);
        }

        static Foldout Section(VisualElement parent, string title)
        {
            var section = new Foldout { text = title, viewDataKey = "channel-painter-" + title };
            section.AddToClassList("cp-section");
            parent.Add(section);
            return section;
        }

        static HelpBox Box(VisualElement parent, string text, HelpBoxMessageType type)
        {
            var box = new HelpBox(text, type);
            parent.Add(box);
            return box;
        }

        static Button AddButton(VisualElement parent, string text, string tooltip, Action click)
        {
            var button = new Button(click) { text = text, tooltip = tooltip };
            parent.Add(button);
            return button;
        }

        static Button ToolButton(VisualElement parent, Texture icon, string text, string tooltip, Action click)
        {
            var button = new Button(click) { tooltip = tooltip };
            button.AddToClassList("cp-tool");
            if (icon != null)
                button.Add(new Image { image = icon });
            button.Add(new Label(text));
            parent.Add(button);
            return button;
        }

        static VisualElement Row(VisualElement parent, string label, string tooltip)
        {
            var row = new VisualElement { tooltip = tooltip };
            row.AddToClassList("cp-row");
            var caption = new Label(label);
            caption.AddToClassList("cp-row-label");
            row.Add(caption);
            parent.Add(row);
            return row;
        }

        // Plain buttons with an active class: a ToolbarToggle flips itself, which breaks cancel-revert.
        static VisualElement Segmented(VisualElement parent, string label, string[] labels, string tooltip,
            Action<int> click, out Button[] buttons)
        {
            VisualElement row = Row(parent, label, tooltip);
            var segment = new VisualElement();
            segment.AddToClassList("cp-segment");
            row.Add(segment);
            buttons = new Button[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                buttons[i] = new Button(() => click(index)) { text = labels[i] };
                buttons[i].AddToClassList("cp-segment-button");
                segment.Add(buttons[i]);
            }
            return row;
        }

        static DropdownField Dropdown(VisualElement parent, string label, string[] choices, Action<int> changed)
        {
            var field = new DropdownField(label) { choices = choices == null ? new List<string>() : choices.ToList() };
            field.RegisterValueChangedCallback(evt => changed(field.index));
            parent.Add(field);
            return field;
        }

        static Slider AddSlider(VisualElement parent, string label, float low, float high, Action<float> changed)
        {
            var slider = new Slider(label, low, high) { showInputField = true };
            slider.RegisterValueChangedCallback(evt => changed(evt.newValue));
            parent.Add(slider);
            return slider;
        }

        static Toggle AddToggle(VisualElement parent, string label, string tooltip, Action<bool> changed)
        {
            var toggle = new Toggle(label) { tooltip = tooltip };
            toggle.RegisterValueChangedCallback(evt => changed(evt.newValue));
            parent.Add(toggle);
            return toggle;
        }

        static FloatField AddFloat(VisualElement parent, string label, Action<float> changed)
        {
            var field = new FloatField(label);
            field.RegisterValueChangedCallback(evt => changed(evt.newValue));
            parent.Add(field);
            return field;
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
            ResetSession();
            ReleasePose();
            target = chosen is SkinnedMeshRenderer ||
                (chosen is MeshRenderer && chosen.GetComponent<MeshFilter>() != null)
                ? chosen : null;
            slot = 0;
            propertyName = null;
            RefreshPanel();
        }

        // Ends the session for a target, slot or property change.
        void ResetSession()
        {
            EndSession();
            ReleaseCanvas();
            paint = false;
            eyedropperArmed = false;
            hasHit = false;
            hasHoverUV = false;
            ClearIslands();
            ClearDirty();
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
            else
            {
                // The vertex color canvas must never reach the map property, so Map starts from the assigned map.
                ReleaseCanvas();
                Texture assigned = PropertyTexture;
                if (assigned != null)
                {
                    canvas = new PaintCanvas(Sizes[sizeIndex], fill);
                    canvas.Load(assigned);
                }
                else
                    paint = false;
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
                // BakeMesh with useScale keeps the renderer's local space, so the full localToWorldMatrix places it.
                skinned.BakeMesh(posedMesh, true);
                mesh = posedMesh;
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
            Event current = Event.current;
            if (swallowUntilMouseUp && current.button == 0 && (current.type == EventType.MouseDrag ||
                current.type == EventType.MouseUp))
            {
                if (current.type == EventType.MouseUp)
                    swallowUntilMouseUp = false;
                current.Use();
                return;
            }
            if (eyedropperArmed && target != null && canvas != null)
            {
                HandleEyedropperSceneGUI(view);
                return;
            }
            if (!paint || target == null || canvas == null || session == null)
                return;
            Handles.BeginGUI();
            ApplyPaintCursor(new Rect(0, 0, view.position.width, view.position.height));
            Handles.EndGUI();
            if (Event.current.type == EventType.Repaint)
                DrawSceneStatus(view);
            if (tool == PaintTool.FillIsland)
            {
                HandleFillSceneGUI(view);
                return;
            }
            HandleBrushSceneGUI(view);
        }

        // The armed eyedropper keeps the cursor, paints nothing, and ray-hits the mesh in either brush space.
        void HandleEyedropperSceneGUI(SceneView view)
        {
            Event evt = Event.current;
            if (evt.alt)
                return;
            Handles.BeginGUI();
            EditorGUIUtility.AddCursorRect(new Rect(0, 0, view.position.width, view.position.height), MouseCursor.Arrow);
            Handles.EndGUI();
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (evt.type == EventType.MouseDown && evt.button == 0 &&
                UpdateSceneHit(evt, out _, out _, out _) && hasHoverUV)
            {
                PickAt(hoverUV);
                swallowUntilMouseUp = true;
            }
            if (evt.button == 0 && (evt.type == EventType.MouseDown ||
                evt.type == EventType.MouseDrag || evt.type == EventType.MouseUp))
                evt.Use();
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
            if (brushSpace == BrushSpace.Screen)
            {
                HandleScreenBrushSceneGUI(view, evt);
                return;
            }
            if (evt.type == EventType.Repaint && hasHit)
            {
                DrawBrushCircle(hitPoint, hitNormal, radius, BlendColor);
            }
            if (evt.type != EventType.MouseMove && evt.type != EventType.MouseDown &&
                evt.type != EventType.MouseDrag)
                return;
            if (!UpdateSceneHit(evt, out Mesh mesh, out Matrix4x4 matrix, out RaycastHit hit, true))
                return;
            if (evt.type == EventType.MouseMove)
            {
                view.Repaint();
                return;
            }
            ApplyStroke(evt, hasHit, hit, mesh, matrix);
        }

        void HandleScreenBrushSceneGUI(SceneView view, Event evt)
        {
            if (evt.type == EventType.Repaint)
            {
                Handles.BeginGUI();
                DrawBrushCircle(evt.mousePosition, Vector3.forward,
                    screenRadius / EditorGUIUtility.pixelsPerPoint, BlendColor);
                Handles.EndGUI();
                return;
            }
            if (evt.type == EventType.MouseMove)
            {
                view.Repaint();
                return;
            }
            if ((evt.type != EventType.MouseDown && evt.type != EventType.MouseDrag) || evt.button != 0)
                return;
            if (!PaintMesh(out Mesh mesh, out Matrix4x4 matrix))
                return;
            if (evt.type == EventType.MouseDown)
                strokeSnapshotTaken = false;
            if (!strokeSnapshotTaken)
            {
                canvas.PushUndo();
                canvas.RenderScreenDepth(mesh, matrix, view.camera);
                stamper.Begin();
                strokeSnapshotTaken = true;
            }
            Vector2 screenPixel = HandleUtility.GUIPointToScreenPixelCoordinate(evt.mousePosition);
            canvas.BlendMode = blend;
            stamper.Stamp(screenPixel, PenPressure(evt), screenRadius * StampSpacing, (position, pressure) =>
            {
                Vector2 pressured = Pressured(strength, screenRadius, pressure, pressureStrength, pressureSize);
                canvas.PaintScreen(mesh, matrix, slot, view.camera, PaintCanvas.CursorViewport(view.camera, position),
                    pressured.y, hardness, pressured.x, BrushValue, ChannelMask);
            });
            MarkDirty();
            UpdateVertexColors(false);
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
            evt.Use();
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
            if (brushSpace == BrushSpace.Screen)
                screenRadius = Mathf.Clamp(screenRadius * step, MinScreenRadius, MaxScreenRadius);
            else
                radius = Mathf.Clamp(radius * step, MinRadius, MaxRadius);
            evt.Use();
            RefreshPanel();
            view.Repaint();
            return true;
        }

        bool UpdateSceneHit(Event evt, out Mesh mesh, out Matrix4x4 matrix, out RaycastHit hit, bool allowNearMiss = false)
        {
            hit = default;
            if (!PaintMesh(out mesh, out matrix))
            {
                hasHit = false;
                hasHoverUV = false;
                return false;
            }
            Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
            bool onMesh = RayHit(ray, mesh, matrix, out hit);
            hasHit = onMesh;
            if (onMesh)
            {
                hitPoint = hit.point;
                hitNormal = hit.normal;
            }
            else if (allowNearMiss && NearestOnRay(ray, mesh, matrix, radius, out Vector3 center))
            {
                hasHit = true;
                hitPoint = center;
                hitNormal = -ray.direction;
            }
            UpdateHoverUV(onMesh, hit);
            return true;
        }

        // Off the mesh, the brush centers on the ray at the depth of the vertex nearest to it,
        // so a brush over the silhouette still reaches the border texels.
        public static bool NearestOnRay(Ray ray, Mesh mesh, Matrix4x4 matrix, float radius, out Vector3 center)
        {
            center = default;
            float best = radius * radius;
            bool found = false;
            foreach (Vector3 vertex in mesh.vertices)
            {
                Vector3 world = matrix.MultiplyPoint3x4(vertex);
                float along = Vector3.Dot(world - ray.origin, ray.direction);
                if (along < 0)
                    continue;
                Vector3 onRay = ray.origin + ray.direction * along;
                float distance = (world - onRay).sqrMagnitude;
                if (distance > best)
                    continue;
                best = distance;
                center = onRay;
                found = true;
            }
            return found;
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
            canvas.Fill(TargetMesh, slot, triangles, BrushValue, ChannelMask);
            PushSwatch();
            MarkDirty();
            UpdateVertexColors(true);
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
            RefreshPanel();
        }

        void ApplyStroke(Event evt, bool found, RaycastHit hit, Mesh mesh, Matrix4x4 matrix)
        {
            if (evt.button != 0)
                return;
            if (evt.type == EventType.MouseDown)
                strokeSnapshotTaken = false;
            // Off the mesh the stroke lifts, so it never bridges a gap through the air.
            if (evt.type == EventType.MouseDown || !found)
                stamper.Begin();
            if (found)
            {
                if (!strokeSnapshotTaken)
                {
                    canvas.PushUndo();
                    strokeSnapshotTaken = true;
                }
                canvas.BlendMode = blend;
                stamper.Stamp(hitPoint, PenPressure(evt), radius * StampSpacing, (position, pressure) =>
                {
                    Vector2 pressured = Pressured(strength, radius, pressure, pressureStrength, pressureSize);
                    canvas.Paint(mesh, matrix, slot, position, pressured.y, hardness, pressured.x, BrushValue, ChannelMask);
                });
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

        // Stamp distance as a fraction of the brush radius.
        const float StampSpacing = 0.25f;

        // Mouse events arrive far apart on a fast drag, so stamps fill the path between them at even spacing.
        public struct StrokeStamper
        {
            Vector3 last;
            float lastPressure;
            bool started;

            public void Begin()
            {
                started = false;
            }

            public void Stamp(Vector3 position, float pressure, float spacing, Action<Vector3, float> dab)
            {
                if (!started)
                {
                    dab(position, pressure);
                    last = position;
                    lastPressure = pressure;
                    started = true;
                    return;
                }
                float distance = Vector3.Distance(last, position);
                int count = Mathf.FloorToInt(distance / Mathf.Max(spacing, 1e-6f));
                if (count == 0)
                    return;
                for (int i = 1; i <= count; i++)
                {
                    float t = i * spacing / distance;
                    dab(Vector3.Lerp(last, position, t), Mathf.Lerp(lastPressure, pressure, t));
                }
                float travelled = count * spacing / distance;
                last = Vector3.Lerp(last, position, travelled);
                lastPressure = Mathf.Lerp(lastPressure, pressure, travelled);
            }
        }

        internal void PaintUV(Vector2 uv, float radiusPixels, bool beginStroke, float pressure)
        {
            if (canvas == null || session == null || TargetMesh == null)
                return;
            if (beginStroke)
            {
                strokeSnapshotTaken = false;
                stamper.Begin();
            }
            if (!strokeSnapshotTaken)
            {
                canvas.PushUndo();
                strokeSnapshotTaken = true;
            }
            canvas.BlendMode = blend;
            float size = canvas.Texture.width;
            stamper.Stamp(uv * size, pressure, radiusPixels * StampSpacing, (position, stampPressure) =>
            {
                Vector2 pressured = Pressured(strength, radiusPixels, stampPressure, pressureStrength, pressureSize);
                canvas.PaintUV(TargetMesh, slot, (Vector2)position / size, pressured.y, hardness, pressured.x, BrushValue, ChannelMask);
            });
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
            {
                UpdateVertexColors(true);
                PushSwatch();
            }
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
