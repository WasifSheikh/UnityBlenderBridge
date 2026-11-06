using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UMesh = UnityEngine.Mesh;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>
    /// Meshes for Blender in a small binary format (read by aa_bridge/aamesh.py), already in Blender space.
    /// AAM2: "AAM2", int verts, int flags (1 normals, 2 uv0, 4 uv1, 8 colours), int submeshes, int indexCount per
    /// submesh, float3 positions, [float3 normals], [float2 uv0], [float2 uv1], [byte4 colours], int indices.
    /// Winding is flipped (the axis change is a reflection). Non-triangle submeshes are written empty.
    /// </summary>
    internal static class BBMeshWriter
    {
        public const int FNormals = 1, FUv0 = 2, FUv1 = 4, FColors = 8;

        public static void Write(UMesh mesh, string file)
        {
            using (var arr = UMesh.AcquireReadOnlyMeshData(mesh))
            {
                var d = arr[0];
                int n = d.vertexCount;
                int flags = (d.HasVertexAttribute(VertexAttribute.Normal) ? FNormals : 0) | (d.HasVertexAttribute(VertexAttribute.TexCoord0) ? FUv0 : 0)
                          | (d.HasVertexAttribute(VertexAttribute.TexCoord1) ? FUv1 : 0) | (d.HasVertexAttribute(VertexAttribute.Color) ? FColors : 0);
                var subs = new List<int[]>(d.subMeshCount);
                for (int s = 0; s < d.subMeshCount; s++)
                {
                    var sm = d.GetSubMesh(s);
                    if (sm.topology != MeshTopology.Triangles) { subs.Add(Array.Empty<int>()); continue; }
                    using (var ia = new NativeArray<int>(sm.indexCount, Allocator.Temp))
                    {
                        d.GetIndices(ia, s, true);
                        var a = ia.ToArray();
                        for (int i = 0; i + 2 < a.Length; i += 3) { int t = a[i + 1]; a[i + 1] = a[i + 2]; a[i + 2] = t; }
                        subs.Add(a);
                    }
                }

                string tmp = file + ".tmp";
                using (var w = new BinaryWriter(File.Create(tmp)))
                {
                    w.Write(Encoding.ASCII.GetBytes("AAM2"));
                    w.Write(n);
                    w.Write(flags);
                    w.Write(subs.Count);
                    foreach (var a in subs) w.Write(a.Length);
                    using (var v3 = new NativeArray<Vector3>(n, Allocator.Temp))
                    {
                        d.GetVertices(v3);
                        WriteV3(w, v3);
                        if ((flags & FNormals) != 0) { d.GetNormals(v3); WriteV3(w, v3); }
                    }
                    if ((flags & FUv0) != 0) WriteUv(w, d, 0, n);
                    if ((flags & FUv1) != 0) WriteUv(w, d, 1, n);
                    if ((flags & FColors) != 0)
                        using (var c = new NativeArray<Color32>(n, Allocator.Temp))
                        {
                            d.GetColors(c);
                            var b = new byte[n * 4];
                            for (int i = 0; i < n; i++) { var k = c[i]; b[i * 4] = k.r; b[i * 4 + 1] = k.g; b[i * 4 + 2] = k.b; b[i * 4 + 3] = k.a; }
                            w.Write(b);
                        }
                    foreach (var a in subs) WriteRaw(w, a);
                }
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
        }

        /// <summary>Hash of a mesh's geometry, for meshes that are not assets (generated / ProBuilder): two different meshes
        /// with the same name and size no longer share a cache file, and an unchanged one is not converted again.</summary>
        public static string ContentHash(UMesh mesh)
        {
            var h = new Hash128();
            h.Append(mesh.name ?? "");
            using (var arr = UMesh.AcquireReadOnlyMeshData(mesh))
            {
                var d = arr[0];
                using (var v3 = new NativeArray<Vector3>(d.vertexCount, Allocator.Temp))
                {
                    d.GetVertices(v3);
                    h.Append(v3.ToArray());
                }
                for (int s = 0; s < d.subMeshCount; s++)
                    using (var ia = new NativeArray<int>(d.GetSubMesh(s).indexCount, Allocator.Temp))
                    {
                        d.GetIndices(ia, s, true);
                        h.Append(ia.ToArray());
                    }
            }
            return h.ToString();
        }

        static void WriteUv(BinaryWriter w, UMesh.MeshData d, int channel, int n)
        {
            using (var uv = new NativeArray<Vector2>(n, Allocator.Temp))
            {
                d.GetUVs(channel, uv);
                var f = new float[n * 2];
                for (int i = 0; i < n; i++) { f[i * 2] = uv[i].x; f[i * 2 + 1] = uv[i].y; }
                WriteRaw(w, f);
            }
        }

        static void WriteV3(BinaryWriter w, NativeArray<Vector3> v)
        {
            var f = new float[v.Length * 3];
            for (int i = 0; i < v.Length; i++) { f[i * 3] = -v[i].x; f[i * 3 + 1] = -v[i].z; f[i * 3 + 2] = v[i].y; }
            WriteRaw(w, f);
        }

        static void WriteRaw(BinaryWriter w, Array a)
        {
            var b = new byte[Buffer.ByteLength(a)];
            Buffer.BlockCopy(a, 0, b, 0, b.Length);
            w.Write(b);
        }
    }
}
