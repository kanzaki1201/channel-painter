using NUnit.Framework;
using UnityEngine;

namespace Malloc.ChannelPainter.Editor
{
    public class ChannelPainterTests
    {
        [Test]
        public void StamperFillsTheDragAtEvenSpacing()
        {
            var stamps = new System.Collections.Generic.List<Vector3>();
            var stamper = new ChannelPainterWindow.StrokeStamper();
            stamper.Begin();
            stamper.Stamp(Vector3.zero, 1, 1, (position, pressure) => stamps.Add(position));
            stamper.Stamp(new Vector3(3.5f, 0, 0), 1, 1, (position, pressure) => stamps.Add(position));
            stamper.Stamp(new Vector3(4.2f, 0, 0), 1, 1, (position, pressure) => stamps.Add(position));

            Assert.That(stamps.Count, Is.EqualTo(5));
            for (int i = 0; i < stamps.Count; i++)
                Assert.That(stamps[i].x, Is.EqualTo(i).Within(1e-4f));
        }

        [Test]
        public void MergeChangesOnlyMaskedChannels()
        {
            Color result = VertexColorBake.Merge(
                new Color(0.1f, 0.2f, 0.3f, 0.4f),
                new Color(0.5f, 0.6f, 0.7f, 0.8f),
                new Vector4(1, 0, 1, 0));

            Assert.That(result.r, Is.EqualTo(0.5f));
            Assert.That(result.g, Is.EqualTo(0.2f));
            Assert.That(result.b, Is.EqualTo(0.7f));
            Assert.That(result.a, Is.EqualTo(0.4f));
        }

        [Test]
        public void BrushUsesTheSameYOrientationAsUvReadback()
        {
            var quad = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                    new Vector3(1, 1, 0), new Vector3(0, 1, 0)
                },
                uv = new[]
                {
                    new Vector2(0, 0), new Vector2(1, 0),
                    new Vector2(1, 1), new Vector2(0, 1)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };

            try
            {
                using (var canvas = new PaintCanvas(64, Color.white))
                {
                    canvas.Paint(quad, Matrix4x4.identity, 0, new Vector3(0.25f, 0.75f, 0),
                        0.15f, 0.5f, 1, Vector4.zero, new Vector4(1, 0, 0, 0));
                    Texture2D readback = canvas.ReadbackDilated(quad, 0);
                    try
                    {
                        Assert.That(readback.GetPixelBilinear(0.25f, 0.75f).r, Is.LessThan(0.5f));
                        Assert.That(readback.GetPixelBilinear(0.25f, 0.25f).r, Is.GreaterThan(0.95f));
                    }
                    finally
                    {
                        Object.DestroyImmediate(readback);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(quad);
            }
        }

        [Test]
        public void ScreenBrushPaintsOnlyTheVisibleSurface()
        {
            var cameraObject = new GameObject("Screen brush test camera");
            var camera = cameraObject.AddComponent<Camera>();
            var target = new RenderTexture(128, 128, 0);
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0, 0, 1), new Vector3(1, 0, 1),
                    new Vector3(1, 1, 1), new Vector3(0, 1, 1),
                    new Vector3(-1, -1, 2), new Vector3(1, -1, 2),
                    new Vector3(1, 1, 2), new Vector3(-1, 1, 2)
                },
                uv = new[]
                {
                    new Vector2(0, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 1), new Vector2(0, 1),
                    new Vector2(0.5f, 0), new Vector2(1, 0),
                    new Vector2(1, 1), new Vector2(0.5f, 1)
                },
                subMeshCount = 2
            };
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.SetTriangles(new[] { 4, 5, 6, 4, 6, 7 }, 1);
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 1;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 10;
            camera.targetTexture = target;
            camera.aspect = 1;

            try
            {
                Assert.That(camera.pixelWidth, Is.EqualTo(128));
                Assert.That(camera.pixelHeight, Is.EqualTo(128));
                using (var canvas = new PaintCanvas(128, Color.white))
                {
                    var mask = new Vector4(1, 0, 0, 0);
                    Vector2 upper = PaintCanvas.CursorViewport(camera, new Vector2(88, 96));
                    canvas.RenderScreenDepth(mesh, Matrix4x4.identity, camera);
                    canvas.PaintScreen(mesh, Matrix4x4.identity, 0, camera, upper, 8, 1, 1, Vector4.zero, mask);
                    Texture2D first = canvas.ReadbackDilated(mesh, 0);
                    try
                    {
                        Assert.That(first.GetPixelBilinear(0.1875f, 0.75f).r, Is.LessThan(0.1f));
                        Assert.That(first.GetPixelBilinear(0.84375f, 0.75f).r, Is.GreaterThan(0.9f));
                    }
                    finally
                    {
                        Object.DestroyImmediate(first);
                    }

                    Vector2 lower = PaintCanvas.CursorViewport(camera, new Vector2(40, 32));
                    canvas.RenderScreenDepth(mesh, Matrix4x4.identity, camera);
                    canvas.PaintScreen(mesh, Matrix4x4.identity, 1, camera, lower, 8, 1, 1, Vector4.zero, mask);
                    Texture2D second = canvas.ReadbackDilated(mesh, 1);
                    try
                    {
                        Assert.That(second.GetPixelBilinear(0.65625f, 0.25f).r, Is.LessThan(0.1f));
                    }
                    finally
                    {
                        Object.DestroyImmediate(second);
                    }
                }
            }
            finally
            {
                camera.targetTexture = null;
                target.Release();
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void GpuDilationExtendsPaintAtIslandEdge()
        {
            var quad = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                    new Vector3(1, 1, 0), new Vector3(0, 1, 0)
                },
                uv = new[]
                {
                    new Vector2(0, 0), new Vector2(0.5f, 0),
                    new Vector2(0.5f, 1), new Vector2(0, 1)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };

            try
            {
                using (var canvas = new PaintCanvas(64, Color.black))
                {
                    canvas.Paint(quad, Matrix4x4.identity, 0, new Vector3(0.5f, 0.5f, 0),
                        2, 1, 1, Vector4.one, new Vector4(1, 0, 0, 0));
                    Texture2D readback = canvas.ReadbackDilated(quad, 0);
                    try
                    {
                        Assert.That(readback.GetPixelBilinear(0.53f, 0.5f).r, Is.GreaterThan(0.9f));
                        Assert.That(readback.GetPixelBilinear(0.8f, 0.5f).r, Is.LessThan(0.1f));
                    }
                    finally
                    {
                        Object.DestroyImmediate(readback);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(quad);
            }
        }

        [Test]
        public void UvBrushUsesTexelRadius()
        {
            var quad = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                    new Vector3(1, 1, 0), new Vector3(0, 1, 0)
                },
                uv = new[]
                {
                    new Vector2(0, 0), new Vector2(1, 0),
                    new Vector2(1, 1), new Vector2(0, 1)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };

            try
            {
                using (var canvas = new PaintCanvas(64, Color.white))
                {
                    canvas.PaintUV(quad, 0, new Vector2(0.25f, 0.75f), 4, 1, 1, Vector4.zero,
                        new Vector4(1, 0, 0, 0));
                    Texture2D readback = canvas.ReadbackDilated(quad, 0);
                    try
                    {
                        Assert.That(readback.GetPixelBilinear(0.25f, 0.75f).r, Is.LessThan(0.1f));
                        Assert.That(readback.GetPixelBilinear(0.25f, 0.25f).r, Is.GreaterThan(0.9f));
                    }
                    finally
                    {
                        Object.DestroyImmediate(readback);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(quad);
            }
        }

        [Test]
        public void FillCoversSubtexelUvTriangle()
        {
            float low = 10.1f / 64f;
            float high = 10.4f / 64f;
            var sliver = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                uv = new[] { new Vector2(0.2f, low), new Vector2(0.8f, low),
                    new Vector2(0.5f, high) },
                triangles = new[] { 0, 1, 2 }
            };

            try
            {
                using (var canvas = new PaintCanvas(64, Color.black))
                {
                    canvas.Fill(sliver, 0, null, Vector4.one, new Vector4(1, 0, 0, 0));
                    Texture2D readback = canvas.ReadbackDilated(sliver, 0);
                    try
                    {
                        Assert.That(readback.GetPixel(32, 10).r, Is.GreaterThan(0.5f));
                    }
                    finally
                    {
                        Object.DestroyImmediate(readback);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(sliver);
            }
        }

        [Test]
        public void SeparateUvQuadsHaveTwoIslands()
        {
            var mesh = new Mesh
            {
                vertices = new Vector3[8],
                uv = new[]
                {
                    new Vector2(0, 0), new Vector2(0.4f, 0),
                    new Vector2(0.4f, 1), new Vector2(0, 1),
                    new Vector2(0.6f, 0), new Vector2(1, 0),
                    new Vector2(1, 1), new Vector2(0.6f, 1)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }
            };
            try
            {
                int[] islands = UVIslands.Islands(mesh, 0);
                Assert.That(islands[0], Is.EqualTo(islands[1]));
                Assert.That(islands[2], Is.EqualTo(islands[3]));
                Assert.That(islands[0], Is.Not.EqualTo(islands[2]));
                Assert.That(UVIslands.Triangles(mesh, 0, islands, islands[0]).Length, Is.EqualTo(6));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void QuadsWithMatchingUvEdgeShareAnIsland()
        {
            var mesh = new Mesh
            {
                vertices = new Vector3[8],
                uv = new[]
                {
                    new Vector2(0, 0), new Vector2(0.5f, 0),
                    new Vector2(0.5f, 1), new Vector2(0, 1),
                    new Vector2(0.5f, 0), new Vector2(1, 0),
                    new Vector2(1, 1), new Vector2(0.5f, 1)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }
            };
            try
            {
                int[] islands = UVIslands.Islands(mesh, 0);
                for (int i = 1; i < islands.Length; i++)
                    Assert.That(islands[i], Is.EqualTo(islands[0]));
                Assert.That(UVIslands.Triangles(mesh, 0, islands, islands[0]).Length, Is.EqualTo(12));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }
            [Test]
        public void PenPressureScalesOnlyTheChosenSettings()
        {
            Assert.That(ChannelPainterWindow.Pressured(0.8f, 0.2f, 0.5f, true, false), Is.EqualTo(new Vector2(0.4f, 0.2f)));
            Assert.That(ChannelPainterWindow.Pressured(0.8f, 0.2f, 0.5f, false, true), Is.EqualTo(new Vector2(0.8f, 0.1f)));
            Assert.That(ChannelPainterWindow.Pressured(0.8f, 0.2f, 0.5f, false, false), Is.EqualTo(new Vector2(0.8f, 0.2f)));
        }

        [Test]
        public void AddAndSubtractChangeTheCurrentValue()
        {
            var quad = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                    new Vector3(1, 1, 0), new Vector3(0, 1, 0)
                },
                uv = new[]
                {
                    new Vector2(0, 0), new Vector2(1, 0),
                    new Vector2(1, 1), new Vector2(0, 1)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };

            try
            {
                using (var canvas = new PaintCanvas(64, new Color(0.5f, 0.5f, 0.5f, 0.5f)))
                {
                    canvas.BlendMode = BrushBlend.Add;
                    canvas.Fill(quad, 0, null, Vector4.one * 0.25f, new Vector4(1, 0, 0, 0));
                    canvas.BlendMode = BrushBlend.Subtract;
                    canvas.Fill(quad, 0, null, Vector4.one * 0.125f, new Vector4(0, 1, 0, 0));
                    Texture2D readback = canvas.ReadbackDilated(quad, 0);
                    try
                    {
                        Color texel = readback.GetPixelBilinear(0.5f, 0.5f);
                        Assert.That(texel.r, Is.EqualTo(0.75f).Within(0.01f));
                        Assert.That(texel.g, Is.EqualTo(0.375f).Within(0.01f));
                        Assert.That(texel.b, Is.EqualTo(0.5f).Within(0.01f));
                    }
                    finally
                    {
                        Object.DestroyImmediate(readback);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(quad);
            }
        }

        [Test]
        public void BrushOffTheEdgeCentersNearTheNearestVertex()
        {
            var quad = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                    new Vector3(1, 1, 0), new Vector3(0, 1, 0)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            try
            {
                var ray = new Ray(new Vector3(1.2f, 1f, -5), Vector3.forward);
                Assert.That(ChannelPainterWindow.NearestOnRay(ray, quad, Matrix4x4.identity, 0.3f, out Vector3 center), Is.True);
                Assert.That(Vector3.Distance(center, new Vector3(1.2f, 1f, 0)), Is.LessThan(1e-4f));
                Assert.That(ChannelPainterWindow.NearestOnRay(ray, quad, Matrix4x4.identity, 0.1f, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(quad);
            }
        }

        [Test]
        public void CopyChannelCopiesOneChannelIntoAnother()
        {
            var quad = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                    new Vector3(1, 1, 0), new Vector3(0, 1, 0)
                },
                uv = new[]
                {
                    new Vector2(0, 0), new Vector2(1, 0),
                    new Vector2(1, 1), new Vector2(0, 1)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            try
            {
                using (var canvas = new PaintCanvas(64, new Color(0.2f, 0.4f, 0.6f, 0.8f)))
                {
                    canvas.CopyChannel(2, 0);
                    Texture2D readback = canvas.ReadbackDilated(quad, 0);
                    try
                    {
                        Color texel = readback.GetPixelBilinear(0.5f, 0.5f);
                        Assert.That(texel.r, Is.EqualTo(0.6f).Within(0.01f));
                        Assert.That(texel.g, Is.EqualTo(0.4f).Within(0.01f));
                        Assert.That(texel.b, Is.EqualTo(0.6f).Within(0.01f));
                        Assert.That(texel.a, Is.EqualTo(0.8f).Within(0.01f));
                        Assert.That(canvas.PaintedChannels, Is.EqualTo(new Vector4(1, 0, 0, 0)));
                    }
                    finally
                    {
                        Object.DestroyImmediate(readback);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(quad);
            }
        }

        [Test]
        public void MergeClampsChannelsToTheControlRange()
        {
            Color result = VertexColorBake.Merge(
                new Color(1.07f, -0.1f, 0.5f, 0.5f),
                new Color(-0.07f, 0.3f, 1.2f, 0.5f),
                new Vector4(1, 0, 1, 0));

            Assert.That(result.r, Is.EqualTo(0f));
            Assert.That(result.g, Is.EqualTo(0f));
            Assert.That(result.b, Is.EqualTo(1f));
            Assert.That(result.a, Is.EqualTo(0.5f));
        }

        [Test]
        public void ColorReplaceWritesOnlyMaskedChannels()
        {
            Mesh quad = Quad();
            try
            {
                using (var canvas = new PaintCanvas(64, Color.white))
                {
                    canvas.Fill(quad, 0, null, new Vector4(0.1f, 0.2f, 0.3f, 0.4f), new Vector4(1, 1, 1, 0));
                    Texture2D readback = canvas.ReadbackDilated(quad, 0);
                    try
                    {
                        Color texel = readback.GetPixelBilinear(0.5f, 0.5f);
                        Assert.That(texel.r, Is.EqualTo(0.1f).Within(0.01f));
                        Assert.That(texel.g, Is.EqualTo(0.2f).Within(0.01f));
                        Assert.That(texel.b, Is.EqualTo(0.3f).Within(0.01f));
                        Assert.That(texel.a, Is.EqualTo(1f).Within(0.01f));
                    }
                    finally
                    {
                        Object.DestroyImmediate(readback);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(quad);
            }
        }

        [Test]
        public void ValueRangeRemapRoundTrips()
        {
            Assert.That(ChannelPainterWindow.RemapToDisplay(0.5f, -0.02f, 0.02f), Is.EqualTo(0f).Within(1e-6f));
            foreach (float value in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
            {
                float display = ChannelPainterWindow.RemapToDisplay(value, -0.02f, 0.02f);
                Assert.That(ChannelPainterWindow.RemapFromDisplay(display, -0.02f, 0.02f), Is.EqualTo(value).Within(1e-5f));
            }
        }

        [Test]
        public void SampleReturnsTheFilledValue()
        {
            Mesh quad = Quad();
            try
            {
                using (var canvas = new PaintCanvas(64, Color.black))
                {
                    canvas.Fill(quad, 0, null, new Vector4(0.25f, 0.5f, 0.75f, 1f), Vector4.one);
                    Color texel = canvas.Sample(new Vector2(0.3f, 0.6f));
                    Assert.That(texel.r, Is.EqualTo(0.25f).Within(0.01f));
                    Assert.That(texel.g, Is.EqualTo(0.5f).Within(0.01f));
                    Assert.That(texel.b, Is.EqualTo(0.75f).Within(0.01f));
                    Assert.That(texel.a, Is.EqualTo(1f).Within(0.01f));

                    // A painted dot proves Sample uses the same UV orientation as painting.
                    canvas.PaintUV(quad, 0, new Vector2(0.25f, 0.75f), 4, 1, 1, Vector4.zero, new Vector4(1, 0, 0, 0));
                    Assert.That(canvas.Sample(new Vector2(0.25f, 0.75f)).r, Is.LessThan(0.1f));
                    Assert.That(canvas.Sample(new Vector2(0.25f, 0.25f)).r, Is.EqualTo(0.25f).Within(0.01f));
                }
            }
            finally
            {
                Object.DestroyImmediate(quad);
            }
        }

        [TestCase(0f, 0.5f, 1f)]
        [TestCase(0.5f, 0.5f, 1f)]
        [TestCase(0.75f, 0.5f, 0.5f)]
        [TestCase(1f, 0.5f, 0f)]
        [TestCase(1.1f, 0.5f, 0f)]
        [TestCase(0.5f, 0f, 0.5f)]
        [TestCase(1f, 1f, 1f)]
        public void VertexWeightsUseSmoothFalloffAndHardness(float distance, float hardness, float falloff)
        {
            var worldCenter = new Vector3(3, 4, 5);
            var screenCenter = new Vector2(120, 80);
            Assert.That(VertexPaint.WorldWeight(worldCenter + Vector3.forward * distance * 4,
                worldCenter, 4, hardness, 0.6f), Is.EqualTo(0.6f * falloff).Within(1e-6f));
            Assert.That(VertexPaint.ScreenWeight(screenCenter + Vector2.right * distance * 40,
                screenCenter, 40, hardness, 0.6f), Is.EqualTo(0.6f * falloff).Within(1e-6f));
        }

        [TestCase(false, BrushBlend.Replace, 0.5f, 0.5f)]
        [TestCase(false, BrushBlend.Add, 0.6f, 0.65f)]
        [TestCase(false, BrushBlend.Subtract, 0.2f, 0.55f)]
        [TestCase(true, BrushBlend.Replace, 0.5f, 0.5f)]
        [TestCase(true, BrushBlend.Add, 0.6f, 0.65f)]
        [TestCase(true, BrushBlend.Subtract, 0.2f, 0.55f)]
        public void VertexBlendUsesWeightAndChannelMask(bool screen, BrushBlend blend, float red, float alpha)
        {
            float weight = screen
                ? VertexPaint.ScreenWeight(new Vector2(30, 0), Vector2.zero, 40, 0.5f, 0.5f)
                : VertexPaint.WorldWeight(new Vector3(3, 0, 0), Vector3.zero, 4, 0.5f, 0.5f);
            Color result = VertexPaint.Apply(new Color(0.4f, 0.3f, 0.2f, 0.6f),
                new Vector4(0.8f, 0.7f, 0.6f, 0.2f), new Vector4(1, 0, 0, 1), weight, blend);
            Assert.That(result.r, Is.EqualTo(red).Within(1e-6f));
            Assert.That(result.g, Is.EqualTo(0.3f));
            Assert.That(result.b, Is.EqualTo(0.2f));
            Assert.That(result.a, Is.EqualTo(alpha).Within(1e-6f));
        }

        [TestCase(BrushBlend.Add, 1f)]
        [TestCase(BrushBlend.Subtract, 0f)]
        public void VertexBlendClampsAtTheControlRange(BrushBlend blend, float expected)
        {
            Color result = VertexPaint.Apply(new Color(0.5f, 0.5f, 0.5f, 0.5f),
                Vector4.one, Vector4.one, 1, blend);
            Assert.That(result, Is.EqualTo(new Color(expected, expected, expected, expected)));
        }

        [Test]
        public void VertexReachRequiresAnAdjacentVisibleTriangle()
        {
            int[][] adjacency = VertexPaint.BuildAdjacency(5, new[] { 0, 1, 2, 0, 2, 3 });
            var visible = new System.Collections.Generic.HashSet<int> { 1 };
            Assert.That(VertexPaint.InReach(adjacency[0], visible), Is.True);
            Assert.That(VertexPaint.InReach(adjacency[1], visible), Is.False);
            Assert.That(VertexPaint.InReach(adjacency[2], visible), Is.True);
            Assert.That(VertexPaint.InReach(adjacency[3], visible), Is.True);
            Assert.That(VertexPaint.InReach(adjacency[4], visible), Is.False);
            visible.Clear();
            Assert.That(VertexPaint.InReach(adjacency[0], visible), Is.False);
            visible.Add(0);
            Assert.That(VertexPaint.InReach(adjacency[1], visible), Is.True);
            Assert.That(VertexPaint.InReach(adjacency[3], visible), Is.False);
        }

        [Test]
        public void VertexWorldPaintUsesPosedPositionsAndFullTransform()
        {
            Mesh original = Quad();
            Mesh posed = Object.Instantiate(original);
            posed.vertices = new[] { new Vector3(5, 0, 0), new Vector3(6, 0, 0),
                new Vector3(6, 1, 0), new Vector3(5, 1, 0) };
            try
            {
                using (var paint = new VertexPaint(original, 0))
                {
                    paint.PaintWorld(posed, Matrix4x4.TRS(new Vector3(10, 0, 0), Quaternion.identity,
                        new Vector3(2, 3, 1)), new Vector3(20, 0, 0), 1, 1, 1,
                        Vector4.zero, new Vector4(1, 0, 0, 0));
                    Assert.That(paint.Colors[0], Is.EqualTo(new Color(0, 1, 1, 1)));
                    for (int i = 1; i < paint.Colors.Length; i++)
                        Assert.That(paint.Colors[i], Is.EqualTo(Color.white));
                }
            }
            finally
            {
                Object.DestroyImmediate(posed);
                Object.DestroyImmediate(original);
            }
        }

        [Test]
        public void VertexArrayRoundTripsThroughStorageAndMaskedBakeMerge()
        {
            Mesh mesh = Quad();
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 0, 2, 3 }, 1);
            var original = new Color(0.125f, 0.25f, 0.375f, 0.5f);
            mesh.colors = new[] { original, original, original, original };
            try
            {
                using (var paint = new VertexPaint(mesh, 0))
                using (var restored = new VertexPaint(mesh, 0))
                {
                    paint.Fill(null, new Vector4(0.75f, 0.625f, 0.5f, 0.875f), new Vector4(1, 0, 0, 1));
                    paint.CopyChannel(0, 2);
                    restored.RestoreBytes(paint.ToBytes(), paint.PaintedChannels);
                    CollectionAssert.AreEqual(paint.Colors, restored.Colors);
                    Assert.That(restored.PaintedChannels, Is.EqualTo(new Vector4(1, 0, 1, 1)));

                    var current = new Color(0.125f, 1.25f, 0.375f, 0.5f);
                    mesh.colors = new[] { current, current, current, current };
                    Color[] merged = VertexColorBake.Merge(mesh, restored.Colors, 0, restored.PaintedChannels);
                    for (int i = 0; i < 3; i++)
                        Assert.That(merged[i], Is.EqualTo(new Color(0.75f, 1.25f, 0.75f, 0.875f)));
                    Assert.That(merged[3], Is.EqualTo(current));
                    Assert.That(restored.Colors[3], Is.EqualTo(original));
                    CollectionAssert.AreEqual(new[] { current, current, current, current }, mesh.colors);
                    Assert.Throws<System.ArgumentException>(() => restored.RestoreBytes(new byte[1], Vector4.one));
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void VertexLoadClampsColorsAndUsesWhiteWhenMissing()
        {
            Mesh mesh = Quad();
            try
            {
                using (var paint = new VertexPaint(mesh, 0))
                {
                    CollectionAssert.AreEqual(new[] { Color.white, Color.white, Color.white, Color.white }, paint.Colors);
                }
                var source = new Color(-0.2f, 1.2f, 0.3f, 0.4f);
                mesh.colors = new[] { source, source, source, source };
                using (var paint = new VertexPaint(mesh, 0))
                {
                    Assert.That(paint.Colors[0], Is.EqualTo(new Color(0, 1, 0.3f, 0.4f)));
                    Assert.That(paint.PaintedChannels, Is.EqualTo(Vector4.zero));
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void VertexUndoKeepsTenSnapshotsAndRestoresTheChannelMask()
        {
            Mesh mesh = Quad();
            try
            {
                using (var paint = new VertexPaint(mesh, 0))
                {
                    for (int i = 0; i < 11; i++)
                    {
                        paint.PushUndo();
                        paint.Fill(null, Vector4.one * (i / 20f), new Vector4(1, 0, 0, 0));
                    }
                    for (int i = 0; i < 10; i++)
                        paint.Undo();
                    Assert.That(paint.CanUndo, Is.False);
                    Assert.That(paint.Colors[0].r, Is.EqualTo(0));
                    Assert.That(paint.PaintedChannels, Is.EqualTo(new Vector4(1, 0, 0, 0)));
                    paint.PushUndo();
                    paint.CopyChannel(0, 3);
                    paint.Undo();
                    Assert.That(paint.Colors[0].a, Is.EqualTo(1));
                    Assert.That(paint.PaintedChannels, Is.EqualTo(new Vector4(1, 0, 0, 0)));
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void VertexIslandFillTouchesEachVertexOnceAndSamplesBarycentrically()
        {
            Mesh mesh = Quad();
            mesh.colors = new[] { Color.clear, Color.clear, Color.clear, Color.clear };
            try
            {
                using (var paint = new VertexPaint(mesh, 0))
                {
                    paint.BlendMode = BrushBlend.Add;
                    paint.Fill(new[] { 0, 1, 2, 0, 1, 2 }, Vector4.one * 0.4f, Vector4.one);
                    Assert.That(paint.Colors[0], Is.EqualTo(new Color(0.4f, 0.4f, 0.4f, 0.4f)));
                    Assert.That(paint.Colors[3], Is.EqualTo(Color.clear));
                    Color sampled = paint.Sample(1, new Vector3(0.25f, 0.25f, 0.5f));
                    Assert.That(sampled.r, Is.EqualTo(0.2f).Within(1e-6f));
                    Assert.That(sampled.a, Is.EqualTo(0.2f).Within(1e-6f));
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        static Mesh Quad()
        {
            return new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                    new Vector3(1, 1, 0), new Vector3(0, 1, 0)
                },
                uv = new[]
                {
                    new Vector2(0, 0), new Vector2(1, 0),
                    new Vector2(1, 1), new Vector2(0, 1)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
        }
    }
}
