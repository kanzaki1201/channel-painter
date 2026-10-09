# Channel Painter

Paint the RGBA control channels that toon shaders read, directly in the Unity Editor.

Paint on the mesh in the Scene view or on the UV layout in a 2D window.

Bake the result to a mask map (PNG) or to vertex color.

The real shader shows the result live while you paint.

Requires Unity 6 or later.

## Install

Add the package to `Packages/manifest.json`:

```json
"com.malloc.channel-painter": "https://github.com/kanzaki1201/channel-painter.git"
```

Or clone the repository into your project's `Packages/` folder.

## Open

- **Tools > Channel Painter > Channel Painter**: the main panel.
- **Tools > Channel Painter > Channel Painter UV**: the 2D UV window. Dock it beside the Scene view.

## Quick start: paint a mask map

1. Select a `MeshRenderer` or `SkinnedMeshRenderer`. It becomes the **Target**.
2. Pick the **Material Slot**, set **Output** to **Map**, and pick the **Texture Property**, for example `_OutlineMap`.
3. If a warning says a keyword is off, click **Enable `<keyword>`**. Some shaders ignore a map until its keyword is on.
4. Click **Start Painting**. The canvas loads from the assigned map. When none is assigned, you choose a path, and a new map is created and assigned.
5. Paint in the Scene view. The material shows the result live.
6. Click **Save Map** to write the assigned PNG, or **Save Map As...** to write a new one.

## Quick start: paint vertex color

1. Pick the Target and Material Slot, and set **Output** to **Vertex color**. No texture property is needed.
2. Click **Start Painting**. The canvas loads from the mesh's current vertex colors.
3. Paint. A temporary copy of the mesh shows the colors live.
4. Click **Bake To Vertex Color**.
   - A mesh asset under `Assets/` is written in place.
   - A mesh inside an FBX or a package is saved as a new `<name>_ChannelPainter` mesh asset and assigned.

## Panel

The panel has six sections in workflow order: Target, Canvas, Paint, Channel Tools, View, and Save.
Each section folds.
A section shows only the controls that the current output and tool use.

### Target

| Setting | Use |
|---|---|
| Target | The renderer to paint |
| Material Slot | The submesh and material to paint |
| Output | **Map** or **Vertex color** buttons |
| Texture Property | Map output only. The texture slot that shows the canvas. |

### Canvas

| Control | Use |
|---|---|
| Size | Canvas resolution: 512, 1024, 2048, or 4096 |
| Fill Color | Map output only. The color of a new canvas. |
| New Canvas | Map output only. Starts a canvas filled with Fill Color. |
| Import `<property>` From Material | Map output only. Replaces the canvas with the assigned map. |
| Reset To Mesh Colors | Vertex color output only. Replaces the canvas with the mesh's vertex colors. |
| Import Texture... | Opens the object picker and copies the picked texture into the canvas |

### Paint

| Setting | Use |
|---|---|
| Start Painting / Stop Painting | Turns painting on in the Scene view and the UV window. The button is highlighted while painting. |
| Tool | **Brush** paints strokes. **Fill Island** fills the UV island you click. |
| Fill Canvas | Applies Value or Color to the whole submesh |
| Undo | Undoes the last stroke or fill (10 steps) |
| Value Mode | **Channel Value** paints one value into the enabled channels. **Color** paints an RGBA color. |
| Blend | **Replace** sets the value. **Add** and **Subtract** change the current value. |
| Value | Channel Value mode. The target value in Replace, the amount in Add and Subtract. |
| Color | Color mode. Opens Unity's color picker. In Add and Subtract, each channel of the color is the amount for that channel. |
| Eyedropper | Picks from the canvas with the next left click (see below) |
| Recent Colors | Color mode. The last 8 colors that a stroke or fill used. Click one to paint with it. |
| Brush Space | Brush tool. **World**: a sphere around the point under the cursor. **Screen** (default): a circle on screen that paints only the front-most surface of the target. |
| Radius (world units) / Screen Radius (px) | Brush tool. Brush size. |
| Hardness | Brush tool. How much of the radius paints at full strength before the edge falloff. |
| Strength | Brush tool. How strongly each brush step applies. |
| Pen Pressure → Strength / Size | Brush tool. Scales Strength or the radius with pen pressure. A mouse is unaffected. |
| Paint On Channels | The R, G, B, A channels a stroke changes. In Color mode they mask the color. |

#### Color mode

The canvas stores the color exactly as the color field shows it, with no sRGB conversion.
For a control map, the picked value is the value the shader reads.
A new color painted into an albedo map displays lighter than picked, because the saved map is linear.

#### Eyedropper

Click the eyedropper button, then left-click the mesh in the Scene view or the canvas in the UV window.
It reads the canvas, not the lit screen.
In Color mode it sets Color.
In Channel Value mode it sets Value from the first enabled channel.
One click picks and turns the eyedropper off.
Click the button again to cancel.

### Channel Tools

Collapsed by default.

| Control | Use |
|---|---|
| Copy Channel | Copies one canvas channel into another, for example B → R. Undo reverts it. |

### View

| Setting | Use |
|---|---|
| Show Channel | Shows RGBA, or one channel as grayscale, in the UV window and the scene mask |
| Show Mask In Scene View | Draws the selected channel unlit on the mesh |
| Mask Source | Map output only. **Canvas** shows the paint. **Vertex Color** shows the mesh's existing vertex colors, with or without a canvas. |
| Hide Mouse Cursor While Painting | Shows only the brush circle while the Brush tool paints. On by default. |
| Value Range, Min, Max | Shows Value in shader units (see below). Off by default, Min -1, Max 1. |
| Open UV Window | Opens the 2D UV window |

#### Value Range

Value Range shows and edits Value in the units that the shader uses.
For example, Min -0.02 and Max 0.02 shows a depth map in metres.

- In Replace, Value shows as `lerp(Min, Max, Value)`.
- In Add and Subtract, Value is an amount, so it shows as `Value × (Max − Min)`.

The stored value stays 0 to 1.
When Max is not greater than Min, the range is ignored and a warning shows.

### Save

| Button | Use |
|---|---|
| Save Map | Overwrites the PNG assigned to the material, or asks for a path |
| Save Map As... | Saves a new PNG and assigns it |
| Bake To Vertex Color | Writes the colors to the mesh (see the vertex color quick start) |

Saved maps import with sRGB off and no compression, because they hold control values, not colors.

## UV window

| Input | Action |
|---|---|
| Left drag | Paint, with the radius in canvas texels |
| Left click with the eyedropper on | Pick from the canvas |
| Scroll | Zoom around the cursor |
| Middle drag, or Alt + left drag | Pan |
| F, or the Fit button | Fit the canvas to the window |
| `[`, `]`, or the Radius field | Smaller or larger 2D brush, 1 to 512 texels |
| Channel dropdown | Same as Show Channel in the panel |
| UV Overlay | Shows the UV wireframe of the target submesh |

A marker shows where the Scene view cursor sits on the UV layout.

## Shortcuts

These work only while painting.

| Key | Action |
|---|---|
| A | Blend: Add |
| S | Blend: Subtract |
| R | Blend: Replace |
| 1, 2, 3, 4 | Toggle the R, G, B, A channel |
| `[`, `]` | Smaller or larger brush in the Scene view |
| Alt + left drag | Orbit the Scene view camera (Unity default) |

Rebind A, S, R, and 1 to 4 under **Edit > Shortcuts > Channel Painter**.
The other keys are fixed.

While painting, a status box in the Scene view shows the tool, blend, and channels.

The brush circle is light blue for Replace, green for Add, and red for Subtract, with a dark outline so it shows on light backgrounds too.

## Unsaved paint

Paint stays in memory until you save.

- Closing the panel, or changing the target, slot, property, or output, asks to Save, Discard, or Cancel.
- A script reload keeps unsaved paint.
- Saving the scene never stores the temporary vertex color mesh.
- Entering play mode ends the paint session.

## Limits

- Mirrored or overlapping UVs share texels, so painting one side paints the other.
- A screen brush is blocked only by the target mesh, not by other renderers in front of it.
- A mesh baked in place loses its paint if another tool regenerates that mesh asset.
- Windows Ink "press and hold to right-click" can interrupt a pen stroke. Turn it off in Control Panel > Pen and Touch.

## License

MIT. See [LICENSE.md](LICENSE.md).
