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
                using (var canvas = new ChannelCanvas(64, Color.white))
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
    }
}
