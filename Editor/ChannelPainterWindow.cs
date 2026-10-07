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
        static readonly string[] OutputLabels = { "Map", "Vertex color" };
        static readonly string[] ViewLabels = { "RGBA", "R", "G", "B", "A" };
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
        [SerializeField] bool red = true;
        [SerializeField] bool green = true;
        [SerializeField] bool blue = true;
        [SerializeField] bool alpha = true;
        [SerializeField] bool assignToMaterial = true;
        [SerializeField] bool paint;
        [SerializeField] bool showMask;
        [SerializeField] bool dirtyFlag;
        [SerializeField] int viewChannel;
        [SerializeField] PaintOutput output;
        [SerializeField] Vector4 reloadChannels;
        [SerializeField] Texture loadTexture;

        PaintCanvas canvas;
        PaintSession session;
        Material viewMaterial;
        Mesh posedMesh;
        Mesh cachedHitMesh;
        int[] cachedTriangles;
        Vector2[] cachedUV;
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

        [MenuItem("Tools/Channel Painter")]
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
                CurrentMaterial() == null || string.IsNullOrEmpty(propertyName))
                return;

            DrawBrushSettings();
            DrawCanvasSettings();
            DrawOutputSettings();
            using (new EditorGUI.DisabledScope(canvas == null || EditorApplication.isPlayingOrWillChangePlaymode))
                paint = EditorGUILayout.Toggle("Paint", paint);
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
                    ClearDirty();
                });

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
                    EnableKeyword(material, keyword);
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
                ChangeSelection(NewCanvas);
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
                    MarkDirty();
                    UpdateVertexColors(true);
                    SceneView.RepaintAll();
                    ChannelPainterUVWindow.Active?.Repaint();
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
            UpdateVertexColors(true);
            SceneView.RepaintAll();
            ChannelPainterUVWindow.Active?.Repaint();
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

        void DrawOutputSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            PaintOutput chosen = (PaintOutput)EditorGUILayout.Popup("Output", (int)output, OutputLabels);
            if (chosen != output)
                ChangeSelection(() => SwitchOutput(chosen));
            ViewChannel = EditorGUILayout.Popup("View", viewChannel, ViewLabels);
            bool nextShowMask = EditorGUILayout.Toggle("Show mask", showMask);
            if (nextShowMask != showMask)
            {
                showMask = nextShowMask;
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Open UV Window"))
                ChannelPainterUVWindow.Open();
            assignToMaterial = EditorGUILayout.Toggle("Assign to material", assignToMaterial);
            using (new EditorGUI.DisabledScope(canvas == null))
            {
                if (GUILayout.Button("Save Map"))
                    SaveMap();
                if (GUILayout.Button("Bake To Vertex Color"))
                    BakeVertexColor();
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
            if (session != null || canvas == null || target == null || string.IsNullOrEmpty(propertyName) ||
                EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            Mesh mesh = SharedMesh();
            if (mesh == null)
                return;
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
            cachedTriangles = null;
            cachedUV = null;
        }

        void MarkDirty()
        {
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
            Event evt = Event.current;
            if (HandleSceneMouseUp(evt))
                return;
            if (evt.alt)
                return;
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (HandleSceneKey(evt, view))
                return;
            if (evt.type == EventType.Repaint && hasHit)
                Handles.DrawWireDisc(hitPoint, hitNormal, radius);
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
                if (cachedHitMesh != mesh)
                {
                    cachedHitMesh = mesh;
                    cachedTriangles = mesh.triangles;
                    cachedUV = mesh.uv;
                }
                SubMeshDescriptor submesh = mesh.GetSubMesh(slot);
                int first = hit.triangleIndex * 3;
                if (first >= submesh.indexStart && first + 2 < submesh.indexStart + submesh.indexCount &&
                    first + 2 < cachedTriangles.Length)
                {
                    Vector3 bary = hit.barycentricCoordinate;
                    hoverUV = cachedUV[cachedTriangles[first]] * bary.x +
                        cachedUV[cachedTriangles[first + 1]] * bary.y +
                        cachedUV[cachedTriangles[first + 2]] * bary.z;
                    hasHoverUV = true;
                }
            }
            ChannelPainterUVWindow.Active?.Repaint();
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
                MarkDirty();
                UpdateVertexColors(false);
                SceneView.RepaintAll();
                ChannelPainterUVWindow.Active?.Repaint();
            }
            evt.Use();
        }

        internal void PaintUV(Vector2 uv, float radiusPixels, bool beginStroke)
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
            canvas.PaintUV(TargetMesh, slot, uv, radiusPixels, hardness, strength, value, ChannelMask);
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
            if (!showMask || camera.cameraType != CameraType.SceneView || viewMaterial == null ||
                canvas == null || session == null || target == null)
                return;
            if (!PaintMesh(out Mesh mesh, out Matrix4x4 matrix))
                return;
            viewMaterial.SetTexture("_MainTex", canvas.Texture);
            viewMaterial.SetInt("_ViewChannel", viewChannel);
            viewMaterial.SetFloat("_UseVertexColor", session.IsVertexColor ? 1 : 0);
            viewMaterial.SetMatrix("_MaskMatrix", matrix);
            Graphics.DrawMesh(mesh, matrix, viewMaterial, 0, camera, slot);
        }

        bool SaveCurrentOutput()
        {
            return output == PaintOutput.Map ? SaveMap() : BakeVertexColor();
        }

        bool SaveMap()
        {
            if (canvas == null || !PaintMesh(out Mesh mesh, out _))
                return false;
            string path = EditorUtility.SaveFilePanelInProject("Save painted map", "ChannelPainter",
                "png", "Save the painted control map.");
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

            if (assignToMaterial)
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
