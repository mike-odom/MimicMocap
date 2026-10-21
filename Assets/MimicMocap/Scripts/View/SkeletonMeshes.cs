using System.Collections.Generic;
using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// Procedural meshes for the anatomical skeleton look: a long bone with flared ends, a curved rib,
    /// and a vertebra. All are unit-sized and scaled per use. Built once and cached.
    /// </summary>
    public static class SkeletonMeshes
    {
        static Mesh s_Bone, s_SlimBone, s_Rib, s_Vertebra;

        /// <summary>Long bone along +Y from -0.5 to 0.5, shaft radius 0.5, knobbed ends radius ~0.85.</summary>
        public static Mesh Bone => s_Bone != null ? s_Bone : (s_Bone = BuildLathe("SkeletonBone", BoneProfile(0.86f), 14));

        /// <summary>Same bone with gentle ends, for short bones such as phalanges and toes where big knobs would merge.</summary>
        public static Mesh SlimBone => s_SlimBone != null ? s_SlimBone : (s_SlimBone = BuildLathe("SkeletonSlimBone", BoneProfile(0.62f), 12));

        /// <summary>
        /// One right-side rib: a tube following a semi-ellipse in the XZ plane from the back (-Z) round the right side
        /// (+X) toward the front, sloping down. Unit radii; scale X/Z for width/depth. Mirror with scale.x = -1 for the left.
        /// </summary>
        public static Mesh Rib => s_Rib != null ? s_Rib : (s_Rib = BuildRib("SkeletonRib", 0.06f, 20, 10));

        /// <summary>A short drum with a rounded rim: one vertebra, unit diameter, height 1 along Y.</summary>
        public static Mesh Vertebra => s_Vertebra != null ? s_Vertebra : (s_Vertebra = BuildLathe("SkeletonVertebra", VertebraProfile(), 12));

        static List<Vector2> BoneProfile(float knob)
        {
            // (radius, y) pairs from bottom to top. Knobs at both ends, thinner shaft.
            var p = new List<Vector2>();
            void Add(float r, float y) => p.Add(new Vector2(r, y));
            var k1 = Mathf.Lerp(0.5f, knob, 0.85f);
            var k2 = Mathf.Lerp(0.5f, knob, 0.7f);
            Add(0.00f, -0.50f);
            Add(0.55f, -0.49f);
            Add(k1, -0.45f);
            Add(knob, -0.40f);
            Add(k2, -0.34f);
            Add(0.58f, -0.28f);
            Add(0.50f, -0.20f);
            Add(0.47f, 0.00f);
            Add(0.50f, 0.20f);
            Add(0.58f, 0.28f);
            Add(k2, 0.34f);
            Add(knob, 0.40f);
            Add(k1, 0.45f);
            Add(0.55f, 0.49f);
            Add(0.00f, 0.50f);
            return p;
        }

        static List<Vector2> VertebraProfile()
        {
            var p = new List<Vector2>();
            void Add(float r, float y) => p.Add(new Vector2(r, y));
            Add(0.00f, -0.50f);
            Add(0.42f, -0.48f);
            Add(0.50f, -0.38f);
            Add(0.46f, 0.00f);
            Add(0.50f, 0.38f);
            Add(0.42f, 0.48f);
            Add(0.00f, 0.50f);
            return p;
        }

        /// <summary>Revolve a (radius, y) profile around the Y axis.</summary>
        static Mesh BuildLathe(string name, List<Vector2> profile, int segments)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            var rings = profile.Count;
            for (var i = 0; i < rings; i++)
            {
                for (var s = 0; s <= segments; s++)
                {
                    var a = s / (float)segments * Mathf.PI * 2f;
                    var r = profile[i].x;
                    verts.Add(new Vector3(Mathf.Cos(a) * r, profile[i].y, Mathf.Sin(a) * r));
                    uvs.Add(new Vector2(s / (float)segments, i / (float)(rings - 1)));
                }
            }
            var stride = segments + 1;
            for (var i = 0; i < rings - 1; i++)
            {
                for (var s = 0; s < segments; s++)
                {
                    var a = i * stride + s;
                    var b = a + 1;
                    var c = a + stride;
                    var d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }
            return Finish(name, verts, uvs, tris);
        }

        /// <summary>Tube along a semi-elliptical arc with a gentle downward slope, closed with small caps.</summary>
        static Mesh BuildRib(string name, float tubeRadius, int pathSegments, int ringSegments)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            // Path: angle 0 at the back centre (-Z), sweeping round the right side toward the front, stopping short of the sternum.
            const float sweep = 150f * Mathf.Deg2Rad;
            var path = new List<Vector3>();
            for (var i = 0; i <= pathSegments; i++)
            {
                var t = i / (float)pathSegments;
                var phi = t * sweep;
                var x = Mathf.Sin(phi);
                var z = -Mathf.Cos(phi);
                var y = -0.22f * t; // ribs slope downward toward the front
                path.Add(new Vector3(x, y, z));
            }

            for (var i = 0; i < path.Count; i++)
            {
                var p = path[i];
                var tangent = (i < path.Count - 1 ? path[i + 1] - p : p - path[i - 1]).normalized;
                var side = Vector3.Cross(Vector3.up, tangent).normalized;
                var up = Vector3.Cross(tangent, side).normalized;
                // Slightly flattened tube: wider along the body surface than radially.
                var taper = Mathf.Lerp(1f, 0.7f, i / (float)(path.Count - 1));
                for (var s = 0; s <= ringSegments; s++)
                {
                    var a = s / (float)ringSegments * Mathf.PI * 2f;
                    var offset = (side * (Mathf.Cos(a) * 0.8f) + up * Mathf.Sin(a)) * (tubeRadius * taper);
                    verts.Add(p + offset);
                    uvs.Add(new Vector2(s / (float)ringSegments, i / (float)(path.Count - 1)));
                }
            }
            var stride = ringSegments + 1;
            for (var i = 0; i < path.Count - 1; i++)
            {
                for (var s = 0; s < ringSegments; s++)
                {
                    var a = i * stride + s;
                    var b = a + 1;
                    var c = a + stride;
                    var d = c + 1;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(b); tris.Add(d); tris.Add(c);
                }
            }
            // Caps
            AddCap(verts, tris, 0, stride, path[0], false);
            AddCap(verts, tris, (path.Count - 1) * stride, stride, path[path.Count - 1], true);
            return Finish(name, verts, uvs, tris);
        }

        static void AddCap(List<Vector3> verts, List<int> tris, int ringStart, int stride, Vector3 centre, bool flip)
        {
            var centreIndex = verts.Count;
            verts.Add(centre);
            for (var s = 0; s < stride - 1; s++)
            {
                var a = ringStart + s;
                var b = ringStart + s + 1;
                if (flip) { tris.Add(centreIndex); tris.Add(a); tris.Add(b); }
                else { tris.Add(centreIndex); tris.Add(b); tris.Add(a); }
            }
        }

        static Mesh Finish(string name, List<Vector3> verts, List<Vector2> uvs, List<int> tris)
        {
            var m = new Mesh { name = name };
            m.SetVertices(verts);
            while (uvs.Count < verts.Count) uvs.Add(Vector2.zero);
            m.SetUVs(0, uvs);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
