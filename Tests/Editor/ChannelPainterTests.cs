using NUnit.Framework;
using UnityEngine;

namespace Malloc.ChannelPainter.Editor
{
    public class ChannelPainterTests
    {
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
                        0.15f, 0.5f, 1, 0, new Vector4(1, 0, 0, 0));
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
                        2, 1, 1, 1, new Vector4(1, 0, 0, 0));
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
                    canvas.PaintUV(quad, 0, new Vector2(0.25f, 0.75f), 4, 1, 1, 0,
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
                    canvas.Fill(sliver, 0, null, 1, new Vector4(1, 0, 0, 0));
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
                    canvas.Fill(quad, 0, null, 0.25f, new Vector4(1, 0, 0, 0));
                    canvas.BlendMode = BrushBlend.Subtract;
                    canvas.Fill(quad, 0, null, 0.125f, new Vector4(0, 1, 0, 0));
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
    }
}
