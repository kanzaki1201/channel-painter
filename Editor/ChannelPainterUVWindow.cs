using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Malloc.ChannelPainter.Editor
{
    public sealed class ChannelPainterUVWindow : EditorWindow
    {
        const float ToolbarHeight = 22f;
        static readonly string[] ViewLabels = { "RGBA", "R", "G", "B", "A" };

        [SerializeField] float zoom = 1f;
        [SerializeField] Vector2 pan;
        [SerializeField] float brushRadius = 16f;
        [SerializeField] bool showUV = true;

        Mesh edgeMesh;
        int edgeSlot = -1;
        Vector2[] edgeUVs;
        bool panning;
        bool painting;

        internal static ChannelPainterUVWindow Active { get; private set; }

        [MenuItem("Tools/Channel Painter/Channel Painter UV")]
        internal static void Open()
        {
            GetWindow<ChannelPainterUVWindow>("Channel Painter UV");
        }

        void OnEnable()
        {
            Active = this;
            wantsMouseMove = true;
            wantsMouseEnterLeaveWindow = true;
        }

        void OnDisable()
        {
            if (painting && ChannelPainterWindow.Active?.IsVertexOutput != true)
                ChannelPainterWindow.Active?.EndUVStroke();
            if (Active == this)
                Active = null;
        }

        void OnGUI()
        {
            ChannelPainterWindow main = ChannelPainterWindow.Active;
            using (new EditorGUI.DisabledScope(main?.IsVertexOutput == true))
                DrawToolbar(main);
            if (main == null || main.IsVertexOutput || main.TargetMesh == null || main.Canvas == null)
            {
                if (painting || panning)
                    GUIUtility.hotControl = 0;
                painting = false;
                panning = false;
                EditorGUILayout.HelpBox("The UV window supports Map output. Open Channel Painter and create a canvas for a mesh target.", MessageType.Info);
                return;
            }
            if (main.ViewMaterial == null)
            {
                EditorGUILayout.HelpBox("The Channel Painter view shader is unavailable.", MessageType.Error);
                return;
            }

            Rect viewport = new Rect(0, ToolbarHeight, position.width,
                Mathf.Max(0, position.height - ToolbarHeight));
            Rect canvasRect = CanvasRect(viewport.size);
            if (main.IsPainting)
                main.ApplyPaintCursor(viewport);
            Event evt = Event.current;

            if (evt.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(viewport, new Color(0.13f, 0.13f, 0.13f));
                GUI.BeginGroup(viewport);
                main.ViewMaterial.SetFloat("_ViewChannel", main.ViewChannel);
                main.ViewMaterial.SetTexture("_MainTex", main.Canvas.Preview);
                // Graphics.DrawTexture with a custom material ignores the GUI clip, so clip by hand.
                Rect visible = Rect.MinMaxRect(
                    Mathf.Max(canvasRect.xMin, 0), Mathf.Max(canvasRect.yMin, 0),
                    Mathf.Min(canvasRect.xMax, viewport.width), Mathf.Min(canvasRect.yMax, viewport.height));
                if (visible.width > 0 && visible.height > 0)
                {
                    Rect source = Rect.MinMaxRect(
                        (visible.xMin - canvasRect.xMin) / canvasRect.width,
                        (canvasRect.yMax - visible.yMax) / canvasRect.height,
                        (visible.xMax - canvasRect.xMin) / canvasRect.width,
                        (canvasRect.yMax - visible.yMin) / canvasRect.height);
                    Graphics.DrawTexture(visible, main.Canvas.Preview, source,
                        0, 0, 0, 0, main.ViewMaterial, 1);
                }
                DrawOverlay(main, canvasRect, evt.mousePosition - viewport.position);
                GUI.EndGroup();
            }

            HandleInput(main, viewport, canvasRect, evt);
        }

        void DrawToolbar(ChannelPainterWindow main)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar, GUILayout.Height(ToolbarHeight)))
            {
                using (new EditorGUI.DisabledScope(main == null))
                {
                    int selected = EditorGUILayout.Popup(main == null ? 0 : main.ViewChannel,
                        ViewLabels, EditorStyles.toolbarPopup, GUILayout.Width(70));
                    if (main != null && selected != main.ViewChannel)
                    {
                        main.ViewChannel = selected;
                        main.RefreshPanel();
                        SceneView.RepaintAll();
                        Repaint();
                    }
                }
                showUV = GUILayout.Toggle(showUV, "UV Overlay", EditorStyles.toolbarButton);
                GUILayout.FlexibleSpace();
                if (main != null && main.IsPainting)
                {
                    var status = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = main.BlendColor } };
                    GUILayout.Label(main.StatusText, status);
                    GUILayout.FlexibleSpace();
                }
                GUILayout.Label("Radius");
                brushRadius = Mathf.Clamp(EditorGUILayout.FloatField(brushRadius, GUILayout.Width(48)), 1, 512);
                if (GUILayout.Button("Fit", EditorStyles.toolbarButton))
                    Fit();
            }
        }

        Rect CanvasRect(Vector2 viewSize)
        {
            float size = Mathf.Min(viewSize.x, viewSize.y) * zoom;
            return new Rect((viewSize.x - size) * 0.5f + pan.x,
                (viewSize.y - size) * 0.5f + pan.y, size, size);
        }

        void DrawOverlay(ChannelPainterWindow main, Rect canvasRect, Vector2 mouse)
        {
            Handles.BeginGUI();
            if (showUV)
            {
                CacheEdges(main.TargetMesh, main.TargetSlot);
                if (edgeUVs != null)
                {
                    var points = new Vector3[edgeUVs.Length];
                    for (int i = 0; i < points.Length; i++)
                        points[i] = UVToPoint(edgeUVs[i], canvasRect);
                    Handles.color = new Color(1, 1, 1, 0.7f);
                    Handles.DrawLines(points);
                }
            }

            if (main.HasHoverUV)
            {
                Vector3 point = UVToPoint(main.HoverUV, canvasRect);
                Handles.color = new Color(1f, 0.55f, 0.1f);
                Handles.DrawWireDisc(point, Vector3.forward, 5);
                Handles.DrawLine(point + Vector3.left * 8, point + Vector3.right * 8);
                Handles.DrawLine(point + Vector3.up * 8, point + Vector3.down * 8);
            }

            if (canvasRect.Contains(mouse) && !main.IsFillIsland)
            {
                float radius = brushRadius * canvasRect.width / main.Canvas.Texture.width;
                ChannelPainterWindow.DrawBrushCircle(mouse, Vector3.forward, radius, main.BlendColor);
            }
            Handles.color = Color.white;
            Handles.EndGUI();
        }

        static Vector3 UVToPoint(Vector2 uv, Rect rect)
        {
            return new Vector3(rect.x + uv.x * rect.width,
                rect.y + (1f - uv.y) * rect.height, 0);
        }

        static Vector2 PointToUV(Vector2 point, Rect rect)
        {
            return new Vector2((point.x - rect.x) / rect.width,
                1f - (point.y - rect.y) / rect.height);
        }

        void HandleInput(ChannelPainterWindow main, Rect viewport, Rect canvasRect, Event evt)
        {
            int control = GUIUtility.GetControlID(FocusType.Passive);
            Vector2 mouse = evt.mousePosition - viewport.position;
            switch (evt.type)
            {
                case EventType.ScrollWheel:
                    HandleScroll(viewport, mouse, evt);
                    break;
                case EventType.KeyDown:
                    HandleKey(evt);
                    break;
                case EventType.MouseDown:
                    HandleMouseDown(main, viewport, canvasRect, mouse, control, evt);
                    break;
                case EventType.MouseDrag:
                    HandleMouseDrag(main, canvasRect, mouse, evt);
                    break;
                case EventType.MouseUp:
                case EventType.MouseLeaveWindow:
                    EndDrag(main, evt);
                    break;
                case EventType.MouseMove:
                    Repaint();
                    break;
            }
        }

        void HandleScroll(Rect viewport, Vector2 mouse, Event evt)
        {
            if (!viewport.Contains(evt.mousePosition))
                return;
            float next = Mathf.Clamp(zoom * Mathf.Pow(1.15f, -evt.delta.y), 0.1f, 32f);
            float ratio = next / zoom;
            Vector2 center = viewport.size * 0.5f + pan;
            pan += (1f - ratio) * (mouse - center);
            zoom = next;
            evt.Use();
            Repaint();
        }

        void HandleKey(Event evt)
        {
            if (focusedWindow != this)
                return;
            if (evt.keyCode == KeyCode.F)
            {
                Fit();
                evt.Use();
                return;
            }
            if (evt.keyCode != KeyCode.LeftBracket && evt.keyCode != KeyCode.RightBracket)
                return;
            float scale = evt.keyCode == KeyCode.RightBracket ? 1.15f : 1f / 1.15f;
            brushRadius = Mathf.Clamp(brushRadius * scale, 1f, 512f);
            evt.Use();
            Repaint();
        }

        void HandleMouseDown(ChannelPainterWindow main, Rect viewport, Rect canvasRect,
            Vector2 mouse, int control, Event evt)
        {
            if (!viewport.Contains(evt.mousePosition))
                return;
            if (evt.button == 2 || (evt.button == 0 && evt.alt))
            {
                panning = true;
                GUIUtility.hotControl = control;
                Focus();
                evt.Use();
                return;
            }
            if (evt.button == 0 && main.EyedropperArmed && canvasRect.Contains(mouse))
            {
                main.PickAt(PointToUV(mouse, canvasRect));
                evt.Use();
                Repaint();
                return;
            }
            if (evt.button != 0 || !canvasRect.Contains(mouse) || !main.IsPainting)
                return;
            GUIUtility.hotControl = control;
            Focus();
            if (main.IsFillIsland)
            {
                main.FillIslandAtUV(PointToUV(mouse, canvasRect));
                GUIUtility.hotControl = 0;
            }
            else
            {
                painting = true;
                main.PaintUV(PointToUV(mouse, canvasRect), brushRadius, true, ChannelPainterWindow.PenPressure(evt));
            }
            evt.Use();
            Repaint();
        }

        void HandleMouseDrag(ChannelPainterWindow main, Rect canvasRect, Vector2 mouse, Event evt)
        {
            if (panning)
            {
                pan += evt.delta;
                evt.Use();
                Repaint();
                return;
            }
            if (!painting)
                return;
            if (canvasRect.Contains(mouse))
                main.PaintUV(PointToUV(mouse, canvasRect), brushRadius, false, ChannelPainterWindow.PenPressure(evt));
            evt.Use();
            Repaint();
        }

        void EndDrag(ChannelPainterWindow main, Event evt)
        {
            if (!panning && !painting)
                return;
            if (painting)
                main.EndUVStroke();
            painting = false;
            panning = false;
            GUIUtility.hotControl = 0;
            evt.Use();
            Repaint();
        }

        void Fit()
        {
            zoom = 1;
            pan = Vector2.zero;
            Repaint();
        }

        void CacheEdges(Mesh mesh, int slot)
        {
            if (edgeMesh == mesh && edgeSlot == slot)
                return;
            edgeMesh = mesh;
            edgeSlot = slot;
            edgeUVs = null;
            if (mesh == null || slot < 0 || slot >= mesh.subMeshCount)
                return;

            Vector2[] uv = mesh.uv;
            if (uv.Length != mesh.vertexCount)
                return;
            int[] triangles = mesh.GetTriangles(slot);
            var unique = new HashSet<ulong>();
            var edges = new List<Vector2>();
            for (int i = 0; i < triangles.Length; i += 3)
            {
                AddEdge(triangles[i], triangles[i + 1], uv, unique, edges);
                AddEdge(triangles[i + 1], triangles[i + 2], uv, unique, edges);
                AddEdge(triangles[i + 2], triangles[i], uv, unique, edges);
            }
            edgeUVs = edges.ToArray();
        }

        static void AddEdge(int a, int b, Vector2[] uv, HashSet<ulong> unique, List<Vector2> edges)
        {
            ulong key = ((ulong)(uint)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
            if (!unique.Add(key))
                return;
            edges.Add(uv[a]);
            edges.Add(uv[b]);
        }
    }
}
