using System;
using System.Collections.Generic;
using UnityEngine;

namespace Malloc.ChannelPainter.Editor
{
    public static class UVIslands
    {
        public static int[] Islands(Mesh mesh, int submesh)
        {
            int[] triangles = mesh.GetTriangles(submesh);
            Vector2[] uv = mesh.uv;
            int count = triangles.Length / 3;
            var parent = new int[count];
            var first = new Dictionary<(int, int), int>();
            for (int triangle = 0; triangle < count; triangle++)
            {
                parent[triangle] = triangle;
                for (int corner = 0; corner < 3; corner++)
                {
                    Vector2 position = uv[triangles[triangle * 3 + corner]];
                    var key = (Mathf.RoundToInt(position.x * 100000f),
                        Mathf.RoundToInt(position.y * 100000f));
                    if (first.TryGetValue(key, out int other))
                        Union(parent, triangle, other);
                    else
                        first.Add(key, triangle);
                }
            }

            var ids = new int[count];
            var roots = new Dictionary<int, int>();
            for (int triangle = 0; triangle < count; triangle++)
            {
                int root = Find(parent, triangle);
                if (!roots.TryGetValue(root, out int id))
                {
                    id = roots.Count;
                    roots.Add(root, id);
                }
                ids[triangle] = id;
            }
            return ids;
        }

        public static int[] Triangles(Mesh mesh, int submesh, int[] islands, int island)
        {
            int[] source = mesh.GetTriangles(submesh);
            if (islands.Length != source.Length / 3)
                throw new ArgumentException("Island count does not match the submesh.", nameof(islands));
            var selected = new List<int>();
            for (int triangle = 0; triangle < islands.Length; triangle++)
            {
                if (islands[triangle] != island)
                    continue;
                selected.Add(source[triangle * 3]);
                selected.Add(source[triangle * 3 + 1]);
                selected.Add(source[triangle * 3 + 2]);
            }
            return selected.ToArray();
        }

        static int Find(int[] parent, int triangle)
        {
            while (parent[triangle] != triangle)
            {
                parent[triangle] = parent[parent[triangle]];
                triangle = parent[triangle];
            }
            return triangle;
        }

        static void Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);
            if (rootA != rootB)
                parent[rootB] = rootA;
        }
    }
}
