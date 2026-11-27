#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Reads LayaAir 0301/0400/0401 and 05-family mesh blocks and creates a Unity Mesh asset.
/// Morph target payloads and multiple vertex buffers are rejected because they cannot be
/// represented by this single-buffer conversion path without additional reconstruction.
/// </summary>
public class MeshConvertor : MonoBehaviour
{
    [HideInInspector]
    public string LayaMesh路径;

    [HideInInspector, Obsolete("顶点数现在从 .lm 文件自动读取。")]
    public ushort 顶点数量;

    public void SaveAsset()
    {
        try
        {
            Mesh mesh = ToUnityMesh(LayaMesh路径);
            string assetPath = AssetDatabase.GenerateUniqueAssetPath($"Assets/LayaMeshConvertor_{SanitizeFileName(mesh.name)}.asset");
            AssetDatabase.CreateAsset(mesh, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Mesh 转换成功：{assetPath}", this);
        }
        catch (Exception e)
        {
            Debug.LogError($"Mesh 转换失败：{e.Message}\n{e}", this);
        }
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "ConvertedMesh" : value;
    }

    public static Mesh ToUnityMesh(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请填写 .lm 文件路径。", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 .lm 文件。", path);
        return ConvertModel(File.ReadAllBytes(path));
    }

    public static string[] ReadBoneNames(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        ModelData model = ReadDirectory(data);
        Block meshBlock = model.Blocks.Find(b => b.Name == "MESH");
        if (meshBlock == null) throw new InvalidDataException("模型区块表中没有 MESH 区块。");
        int p = meshBlock.Start + 2;
        int limit = meshBlock.Start + meshBlock.Length;
        ReadUInt16(data, ref p, limit); // mesh name
        int vbCount = ReadInt16(data, ref p, limit);
        for (int i = 0; i < vbCount; i++)
        {
            ReadUInt32(data, ref p, limit); // vertex buffer offset
            if (IsV05(model.Version)) ReadUInt32(data, ref p, limit); // vertex count
            else ReadUInt32(data, ref p, limit); // vertex buffer length
            ReadUInt16(data, ref p, limit); // vertex declaration string index
        }
        ReadUInt32(data, ref p, limit); // index buffer offset
        ReadUInt32(data, ref p, limit); // index buffer length
        if (IsV05(model.Version) && (model.Version == "LAYAMODEL:0501" || model.Version == "LAYAMODEL:COMPRESSION_0501" || model.Version == "LAYAMODEL:0502"))
            for (int i = 0; i < 6; i++) ReadSingle(data, ref p, limit);
        int boneCount = ReadUInt16(data, ref p, limit);
        var names = new string[boneCount];
        for (int i = 0; i < boneCount; i++) names[i] = GetString(model, ReadUInt16(data, ref p, limit));
        return names;
    }

    // Retained for source and serialized API compatibility. Vertex count is read from the file.
    public static Mesh ToUnityMesh(string path, ushort ignoredVertexCount) => ToUnityMesh(path);

    private sealed class Block
    {
        public string Name;
        public int Start;
        public int Length;
    }

    private sealed class DrawRange
    {
        public int Start;
        public int Count;
        public int[] BonePalette;
    }

    private sealed class SubMeshData
    {
        public int IndexStart;
        public int IndexCount;
        public readonly List<DrawRange> Draws = new List<DrawRange>();
    }

    private sealed class ModelData
    {
        public string Version;
        public int DataOffset;
        public string[] Strings;
        public readonly List<Block> Blocks = new List<Block>();
        public string Name;
        public string[] Attributes;
        public int VertexCount;
        public int Stride;
        public int InputStride;
        public int VertexOffset;
        public int IndexOffset;
        public int IndexLength;
        public int IndexSize;
        public string[] BoneNames;
        public Matrix4x4[] BindPoses;
        public readonly List<SubMeshData> SubMeshes = new List<SubMeshData>();
        public bool Compressed;

        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector4[] Tangents;
        public Color[] Colors;
        public Vector2[] Uv0;
        public Vector2[] Uv1;
        public Vector4[] Weights;
        public int[][] BoneIndices;
        public uint[] Indices;
    }

    private static Mesh ConvertModel(byte[] data)
    {
        ModelData model = ReadDirectory(data);
        if (model.Blocks.Exists(b => b.Name == "MORPH"))
            throw new NotSupportedException("文件包含 MORPH 形变目标区块；当前转换器只还原基础 Mesh，不能静默丢弃形变数据。");

        Block meshBlock = model.Blocks.Find(b => b.Name == "MESH");
        if (meshBlock == null) throw new InvalidDataException("模型区块表中没有 MESH 区块。");
        ReadMeshBlock(data, model, meshBlock);
        foreach (Block block in model.Blocks)
        {
            if (block.Name == "SUBMESH") ReadSubMeshBlock(data, model, block);
            else if (block.Name != "MESH" && block.Name != "UVSIZE")
                throw new NotSupportedException($"模型包含尚未支持的 {block.Name} 区块；为避免丢失网格数据，已停止转换。");
        }

        if (model.SubMeshes.Count == 0)
            model.SubMeshes.Add(new SubMeshData { IndexStart = 0, IndexCount = model.Indices.Length });
        return BuildUnityMesh(model);
    }

    private static ModelData ReadDirectory(byte[] data)
    {
        int p = 0;
        string version = ReadUtfString(data, ref p, data.Length);
        bool v04 = version == "LAYAMODEL:0301" || version == "LAYAMODEL:0400" || version == "LAYAMODEL:0401";
        bool v05 = version == "LAYAMODEL:05" || version == "LAYAMODEL:COMPRESSION_05" ||
                   version == "LAYAMODEL:0501" || version == "LAYAMODEL:COMPRESSION_0501" || version == "LAYAMODEL:0502";
        if (!v04 && !v05) throw new NotSupportedException($"不支持的 Laya 模型版本：{version}。");

        int dataOffset = ToInt(ReadUInt32(data, ref p), "DATA 区偏移");
        ReadUInt32(data, ref p); // DATA size is informational in the Laya readers and is not a file span.
        EnsureRange(data, dataOffset, 0, "DATA 区起点");
        int blockCount = ReadUInt16(data, ref p);
        var starts = new int[blockCount];
        var lengths = new int[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            starts[i] = ToInt(ReadUInt32(data, ref p), "区块偏移");
            lengths[i] = ToInt(ReadUInt32(data, ref p), "区块长度");
            EnsureRange(data, starts[i], lengths[i], "模型区块");
        }

        int stringOffset = ToInt(ReadUInt32(data, ref p), "字符串表偏移");
        int stringCount = ReadUInt16(data, ref p);
        int stringPosition = checked(dataOffset + stringOffset);
        EnsureRange(data, stringPosition, 0, "字符串表");
        var strings = new string[stringCount];
        for (int i = 0; i < strings.Length; i++) strings[i] = ReadUtfString(data, ref stringPosition, data.Length);

        var model = new ModelData {
            Version = version,
            DataOffset = dataOffset,
            Strings = strings,
            Compressed = version == "LAYAMODEL:COMPRESSION_05" || version == "LAYAMODEL:COMPRESSION_0501"
        };
        for (int i = 0; i < blockCount; i++)
        {
            int blockStart = starts[i];
            int blockPosition = blockStart;
            int blockNameIndex = ReadUInt16(data, ref blockPosition, blockStart + lengths[i]);
            if (blockNameIndex >= strings.Length) throw new InvalidDataException("区块名字符串索引越界。");
            model.Blocks.Add(new Block { Name = strings[blockNameIndex], Start = blockStart, Length = lengths[i] });
        }
        return model;
    }

    private static void ReadMeshBlock(byte[] data, ModelData model, Block block)
    {
        int p = block.Start + 2;
        int limit = block.Start + block.Length;
        model.Name = GetString(model, ReadUInt16(data, ref p, limit));
        int vertexBufferCount = ReadInt16(data, ref p, limit);
        if (vertexBufferCount != 1)
            throw new NotSupportedException($"文件包含 {vertexBufferCount} 个顶点缓冲；当前只支持 Laya 官方常见的单顶点缓冲布局。");

        uint vertexRelativeOffset = ReadUInt32(data, ref p, limit);
        int vertexStart = ToInt(checked((uint)model.DataOffset + vertexRelativeOffset), "顶点缓冲偏移");
        bool v05 = IsV05(model.Version);
        int vertexCount;
        if (v05) vertexCount = ToInt(ReadUInt32(data, ref p, limit), "顶点数");
        else
        {
            int vertexLength = ToInt(ReadUInt32(data, ref p, limit), "顶点缓冲长度");
            model.VertexOffset = vertexStart;
            model.Attributes = GetString(model, ReadUInt16(data, ref p, limit)).Split(',');
            model.Stride = GetStride(model.Attributes, IsLegacyCompatible(model.Version), false);
            model.InputStride = model.Stride;
            if (vertexLength % model.Stride != 0) throw new InvalidDataException("顶点缓冲长度不能被顶点步长整除。");
            vertexCount = vertexLength / model.Stride;
        }

        if (v05)
        {
            model.Attributes = GetString(model, ReadUInt16(data, ref p, limit)).Split(',');
            model.Stride = GetStride(model.Attributes, false, false);
            model.InputStride = GetStride(model.Attributes, false, model.Compressed);
            model.VertexOffset = vertexStart;
        }
        model.VertexCount = vertexCount;
        EnsureRange(data, model.VertexOffset, checked(vertexCount * model.InputStride), "顶点缓冲");

        uint indexRelativeOffset = ReadUInt32(data, ref p, limit);
        model.IndexLength = ToInt(ReadUInt32(data, ref p, limit), "索引缓冲长度");
        model.IndexOffset = ToInt(checked((uint)model.DataOffset + indexRelativeOffset), "索引缓冲偏移");
        model.IndexSize = v05 && vertexCount > 65535 ? 4 : 2;
        EnsureRange(data, model.IndexOffset, model.IndexLength, "索引缓冲");
        if (model.IndexLength % model.IndexSize != 0) throw new InvalidDataException("索引缓冲长度与索引格式不匹配。");

        if (v05 && (model.Version == "LAYAMODEL:0501" || model.Version == "LAYAMODEL:COMPRESSION_0501" || model.Version == "LAYAMODEL:0502"))
        {
            for (int i = 0; i < 6; i++) ReadSingle(data, ref p, limit); // serialized mesh bounds
        }

        int boneCount = ReadUInt16(data, ref p, limit);
        model.BoneNames = new string[boneCount];
        for (int i = 0; i < boneCount; i++) model.BoneNames[i] = GetString(model, ReadUInt16(data, ref p, limit));
        if (!v05) p = checked(p + 8); // 0301/040x mesh bounds
        uint bindPoseRelativeOffset = ReadUInt32(data, ref p, limit);
        int bindPoseLength = ToInt(ReadUInt32(data, ref p, limit), "BindPose 长度");
        int bindPoseOffset = ToInt(checked((uint)model.DataOffset + bindPoseRelativeOffset), "BindPose 偏移");
        EnsureRange(data, bindPoseOffset, bindPoseLength, "BindPose 数据");
        if (bindPoseLength % 64 != 0) throw new InvalidDataException("BindPose 数据长度不是 4x4 float 矩阵的倍数。");
        int bindPoseCount = bindPoseLength / 64;
        model.BindPoses = new Matrix4x4[bindPoseCount];
        int bindPoseEnd = bindPoseOffset + bindPoseLength;
        for (int i = 0; i < bindPoseCount; i++)
        {
            float[] m = new float[16];
            for (int j = 0; j < 16; j++) m[j] = ReadSingle(data, ref bindPoseOffset, bindPoseEnd);
            model.BindPoses[i] = new Matrix4x4(
                new Vector4(m[0], m[1], m[2], m[3]), new Vector4(m[4], m[5], m[6], m[7]),
                new Vector4(m[8], m[9], m[10], m[11]), new Vector4(m[12], m[13], m[14], m[15]));
        }
        ReadVertexData(data, model);
        ReadIndexData(data, model);
    }

    private static void ReadSubMeshBlock(byte[] data, ModelData model, Block block)
    {
        int p = block.Start + 2;
        int limit = block.Start + block.Length;
        ReadInt16(data, ref p, limit); // vertex buffer index (single buffer)
        if (!IsV05(model.Version))
        {
            ReadUInt32(data, ref p, limit); // vertex start
            ReadUInt32(data, ref p, limit); // vertex length
        }
        var sub = new SubMeshData {
            IndexStart = ToInt(ReadUInt32(data, ref p, limit), "子网格索引起点"),
            IndexCount = ToInt(ReadUInt32(data, ref p, limit), "子网格索引数量")
        };
        int drawCount = ReadUInt16(data, ref p, limit);
        for (int i = 0; i < drawCount; i++)
        {
            var draw = new DrawRange {
                Start = ToInt(ReadUInt32(data, ref p, limit), "绘制索引起点"),
                Count = ToInt(ReadUInt32(data, ref p, limit), "绘制索引数量")
            };
            uint dictionaryRelativeOffset = ReadUInt32(data, ref p, limit);
            int dictionaryByteLength = ToInt(ReadUInt32(data, ref p, limit), "骨骼字典长度");
            int dictionaryOffset = ToInt(checked((uint)model.DataOffset + dictionaryRelativeOffset), "骨骼字典偏移");
            EnsureRange(data, dictionaryOffset, dictionaryByteLength, "骨骼字典");
            draw.BonePalette = ReadBonePalette(data, dictionaryOffset, dictionaryByteLength, model);
            sub.Draws.Add(draw);
        }
        if (sub.IndexStart < 0 || sub.IndexCount < 0 || (long)sub.IndexStart + sub.IndexCount > model.Indices.Length)
            throw new InvalidDataException("子网格索引范围超出索引缓冲。");
        model.SubMeshes.Add(sub);
    }

    private static void ReadVertexData(byte[] data, ModelData model)
    {
        if (!Array.Exists(model.Attributes, a => a.Trim() == "POSITION")) throw new InvalidDataException("顶点声明中没有 POSITION。");
        bool compressed = model.Compressed;
        if (compressed && Array.Exists(model.Attributes, a => a.Trim() == "NORMAL_BYTE"))
            throw new NotSupportedException("压缩布局中的 NORMAL_BYTE 没有对应的 LayaAir 解码分支。");
        int count = model.VertexCount;
        if (Has(model, "NORMAL") || Has(model, "NORMAL_BYTE")) model.Normals = new Vector3[count];
        if (Has(model, "TANGENT")) model.Tangents = new Vector4[count];
        if (Has(model, "COLOR")) model.Colors = new Color[count];
        if (Has(model, "UV")) model.Uv0 = new Vector2[count];
        if (Has(model, "UV1")) model.Uv1 = new Vector2[count];
        if (Has(model, "BLENDWEIGHT")) model.Weights = new Vector4[count];
        if (Has(model, "BLENDINDICES")) model.BoneIndices = new int[count][];
        model.Positions = new Vector3[count];

        int p = model.VertexOffset;
        for (int vertex = 0; vertex < count; vertex++)
        {
            int recordStart = p;
            foreach (string raw in model.Attributes)
            {
                string attr = raw.Trim();
                switch (attr)
                {
                    case "POSITION":
                    {
                        Vector3 v = compressed ? new Vector3(ReadHalf(data, ref p), ReadHalf(data, ref p), ReadHalf(data, ref p)) : ReadVector3(data, ref p);
                        model.Positions[vertex] = new Vector3(-v.x, v.y, v.z);
                        break;
                    }
                    case "NORMAL":
                        model.Normals[vertex] = compressed ? ReadPackedNormal(data, ref p) : ReadVector3(data, ref p);
                        if (!compressed) { Vector3 n = model.Normals[vertex]; n.x = -n.x; model.Normals[vertex] = n; }
                        else { Vector3 n = model.Normals[vertex]; n.x = -n.x; model.Normals[vertex] = n; }
                        break;
                    case "NORMAL_BYTE":
                    {
                        Vector3 n = ReadPackedNormal(data, ref p); p++; n.x = -n.x; model.Normals[vertex] = n;
                        break;
                    }
                    case "TANGENT":
                    {
                        Vector4 t = compressed ? ReadPackedTangent(data, ref p) : ReadVector4(data, ref p);
                        t.x = -t.x; model.Tangents[vertex] = t;
                        break;
                    }
                    case "COLOR":
                        model.Colors[vertex] = compressed ? ReadPackedColor(data, ref p) : ReadColor(data, ref p);
                        break;
                    case "UV":
                        model.Uv0[vertex] = compressed ? ReadHalfVector2Flipped(data, ref p) : ReadVector2Flipped(data, ref p);
                        break;
                    case "UV1":
                        model.Uv1[vertex] = compressed ? ReadHalfVector2Flipped(data, ref p) : ReadVector2Flipped(data, ref p);
                        break;
                    case "BLENDWEIGHT":
                        model.Weights[vertex] = compressed ? ReadPackedWeights(data, ref p) : ReadVector4(data, ref p);
                        break;
                    case "BLENDINDICES":
                        model.BoneIndices[vertex] = ReadBoneIndices(data, ref p, IsLegacyCompatible(model.Version));
                        break;
                    default:
                        throw new NotSupportedException($"顶点属性 {attr} 暂不支持。");
                }
            }
            if (p - recordStart != model.InputStride)
                throw new InvalidDataException("顶点属性大小与声明的 stride 不一致。");
        }
    }

    private static void ReadIndexData(byte[] data, ModelData model)
    {
        int count = model.IndexLength / model.IndexSize;
        model.Indices = new uint[count];
        int p = model.IndexOffset;
        for (int i = 0; i < count; i++)
        {
            model.Indices[i] = model.IndexSize == 2 ? ReadUInt16(data, ref p) : ReadUInt32(data, ref p);
            if (model.Indices[i] >= model.VertexCount) throw new InvalidDataException($"索引 {model.Indices[i]} 超出顶点数 {model.VertexCount}。");
        }
    }

    private static Mesh BuildUnityMesh(ModelData model)
    {
        bool skinned = model.Weights != null || model.BoneIndices != null || model.BoneNames.Length > 0 || model.BindPoses.Length > 0;
        if ((model.Weights == null) != (model.BoneIndices == null)) throw new InvalidDataException("BLENDWEIGHT 与 BLENDINDICES 必须同时存在。");
        if (skinned && (model.Weights == null || model.BindPoses.Length == 0 || model.BoneNames.Length == 0))
            throw new NotSupportedException("蒙皮网格缺少 BLENDWEIGHT、BLENDINDICES、骨骼名或 BindPose 数据，不能还原为有效的 Unity 蒙皮 Mesh。");

        var positions = new List<Vector3>();
        var normals = model.Normals == null ? null : new List<Vector3>();
        var tangents = model.Tangents == null ? null : new List<Vector4>();
        var colors = model.Colors == null ? null : new List<Color>();
        var uv0 = model.Uv0 == null ? null : new List<Vector2>();
        var uv1 = model.Uv1 == null ? null : new List<Vector2>();
        var weights = skinned ? new List<BoneWeight>() : null;
        var trianglesBySubMesh = new List<int[]>();
        var vertexRemap = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int subIndex = 0; subIndex < model.SubMeshes.Count; subIndex++)
        {
            SubMeshData sub = model.SubMeshes[subIndex];
            var triangles = new List<int>();
            if (skinned && sub.Draws.Count > 0)
            {
                foreach (DrawRange draw in sub.Draws)
                {
                    int start = draw.Start - sub.IndexStart;
                    if (start < 0 || draw.Count < 0 || (long)start + draw.Count > sub.IndexCount)
                        throw new InvalidDataException("蒙皮绘制范围超出了子网格索引范围。");
                    if (draw.Count % 3 != 0) throw new InvalidDataException("蒙皮绘制索引数量不是三角形数量的倍数。");
                    for (int i = start; i < start + draw.Count; i++)
                    {
                        int sourceVertex = checked((int)model.Indices[sub.IndexStart + i]);
                        triangles.Add(GetMappedVertex(sourceVertex, draw.BonePalette, model, vertexRemap, positions, normals, tangents, colors, uv0, uv1, weights));
                    }
                }
            }
            else
            {
                if (sub.IndexCount % 3 != 0) throw new InvalidDataException("子网格索引数量不是三角形数量的倍数。");
                for (int i = 0; i < sub.IndexCount; i++)
                {
                    int sourceVertex = checked((int)model.Indices[sub.IndexStart + i]);
                    triangles.Add(GetMappedVertex(sourceVertex, null, model, vertexRemap, positions, normals, tangents, colors, uv0, uv1, weights));
                }
            }
            trianglesBySubMesh.Add(triangles.ToArray());
        }

        if (positions.Count > 65535 && !SystemInfo.supports32bitsIndexBuffer)
            throw new NotSupportedException("当前平台不支持 32 位 Mesh 索引。");
        var mesh = new Mesh { name = string.IsNullOrWhiteSpace(model.Name) ? "LayaMesh" : model.Name };
        if (positions.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(positions);
        if (normals != null) mesh.SetNormals(normals);
        if (tangents != null) mesh.SetTangents(tangents);
        if (colors != null) mesh.SetColors(colors);
        if (uv0 != null) mesh.SetUVs(0, uv0);
        if (uv1 != null) mesh.SetUVs(1, uv1);
        if (weights != null) mesh.boneWeights = weights.ToArray();
        if (skinned) mesh.bindposes = model.BindPoses;
        mesh.subMeshCount = trianglesBySubMesh.Count;
        for (int i = 0; i < trianglesBySubMesh.Count; i++) mesh.SetTriangles(trianglesBySubMesh[i], i, false);
        mesh.RecalculateBounds();
        return mesh;
    }

    private static int GetMappedVertex(int source, int[] palette, ModelData model, Dictionary<string, int> remap,
        List<Vector3> positions, List<Vector3> normals, List<Vector4> tangents, List<Color> colors,
        List<Vector2> uv0, List<Vector2> uv1, List<BoneWeight> weights)
    {
        int[] mappedBones = null;
        if (weights != null)
        {
            mappedBones = new int[4];
            int[] sourceBones = model.BoneIndices[source];
            Vector4 sourceWeights = model.Weights[source];
            for (int i = 0; i < 4; i++)
            {
                float weight = GetComponent(sourceWeights, i);
                if (weight <= 0f) continue;
                int localBone = sourceBones[i];
                int globalBone;
                if (palette == null || palette.Length == 0) globalBone = localBone;
                else
                {
                    if (localBone < 0 || localBone >= palette.Length) throw new InvalidDataException("顶点骨骼索引超出当前子网格的骨骼字典。");
                    globalBone = palette[localBone];
                }
                if (globalBone < 0 || globalBone >= model.BoneNames.Length || globalBone >= model.BindPoses.Length)
                    throw new InvalidDataException("骨骼字典索引超出骨骼名或 BindPose 数量。");
                mappedBones[i] = globalBone;
            }
        }

        string key = source.ToString() + ":" + (mappedBones == null ? "" : string.Join(",", mappedBones));
        if (remap.TryGetValue(key, out int existing)) return existing;
        int output = positions.Count;
        remap.Add(key, output);
        positions.Add(model.Positions[source]);
        if (normals != null) normals.Add(model.Normals[source]);
        if (tangents != null) tangents.Add(model.Tangents[source]);
        if (colors != null) colors.Add(model.Colors[source]);
        if (uv0 != null) uv0.Add(model.Uv0[source]);
        if (uv1 != null) uv1.Add(model.Uv1[source]);
        if (weights != null)
        {
            Vector4 w = model.Weights[source];
            weights.Add(new BoneWeight {
                boneIndex0 = mappedBones[0], boneIndex1 = mappedBones[1], boneIndex2 = mappedBones[2], boneIndex3 = mappedBones[3],
                weight0 = w.x, weight1 = w.y, weight2 = w.z, weight3 = w.w
            });
        }
        return output;
    }

    private static int GetStride(string[] attributes, bool legacyCompatible, bool compressed)
    {
        int stride = 0;
        foreach (string raw in attributes)
        {
            string attr = raw.Trim();
            if (compressed)
            {
                switch (attr)
                {
                    case "POSITION": stride += 6; break;
                    case "NORMAL": stride += 3; break;
                    case "COLOR": case "BLENDWEIGHT": case "BLENDINDICES": case "TANGENT": stride += 4; break;
                    case "UV": case "UV1": stride += 4; break;
                    default: throw new NotSupportedException($"压缩顶点属性 {attr} 未被 LayaAir reader 支持。");
                }
            }
            else
            {
                switch (attr)
                {
                    case "POSITION": case "NORMAL": stride += 12; break;
                    case "NORMAL_BYTE": stride += 4; break;
                    case "TANGENT": case "COLOR": case "BLENDWEIGHT": stride += 16; break;
                    case "UV": case "UV1": stride += 8; break;
                    case "BLENDINDICES": stride += legacyCompatible ? 16 : 4; break;
                    default: throw new NotSupportedException($"顶点属性 {attr} 暂不支持。");
                }
            }
        }
        if (stride <= 0) throw new InvalidDataException("顶点步长为零。");
        return stride;
    }

    private static int[] ReadBoneIndices(byte[] data, ref int p, bool legacyCompatible)
    {
        var result = new int[4];
        for (int i = 0; i < 4; i++) result[i] = legacyCompatible ? Mathf.RoundToInt(ReadSingle(data, ref p)) : ReadByte(data, ref p);
        return result;
    }

    private static int[] ReadBonePalette(byte[] data, int offset, int byteLength, ModelData model)
    {
        // Official 05 reader stores palettes as Uint16Array slices. Some 0301 files
        // (including the bundled legacy sample) store one byte per bone and report
        // that byte count, so retain that representation when it is the only valid map.
        bool byteMapValid = model.BoneNames.Length > 0;
        for (int i = 0; i < byteLength && byteMapValid; i++)
            byteMapValid = data[offset + i] < model.BoneNames.Length;

        bool wordMapValid = byteLength % 2 == 0;
        for (int i = 0; i < byteLength / 2 && wordMapValid; i++)
        {
            int bone = data[offset + i * 2] | data[offset + i * 2 + 1] << 8;
            wordMapValid = bone < model.BoneNames.Length;
        }

        bool useBytes = byteMapValid && (byteLength % 2 != 0 || !wordMapValid);
        int count = useBytes ? byteLength : byteLength / 2;
        var palette = new int[count];
        for (int i = 0; i < count; i++)
            palette[i] = useBytes ? data[offset + i] : data[offset + i * 2] | data[offset + i * 2 + 1] << 8;
        return palette;
    }

    private static Vector3 ReadPackedNormal(byte[] data, ref int p)
    {
        return new Vector3(ReadByte(data, ref p) / 127.5f - 1f, ReadByte(data, ref p) / 127.5f - 1f, ReadByte(data, ref p) / 127.5f - 1f);
    }

    private static Vector4 ReadPackedTangent(byte[] data, ref int p)
    {
        return new Vector4(ReadByte(data, ref p) / 127.5f - 1f, ReadByte(data, ref p) / 127.5f - 1f,
            ReadByte(data, ref p) / 127.5f - 1f, ReadByte(data, ref p) / 127.5f - 1f);
    }

    private static Color ReadPackedColor(byte[] data, ref int p)
    {
        return new Color(ReadByte(data, ref p) / 255f, ReadByte(data, ref p) / 255f, ReadByte(data, ref p) / 255f, ReadByte(data, ref p) / 255f);
    }

    private static Vector4 ReadPackedWeights(byte[] data, ref int p)
    {
        return new Vector4(ReadByte(data, ref p) / 255f, ReadByte(data, ref p) / 255f, ReadByte(data, ref p) / 255f, ReadByte(data, ref p) / 255f);
    }

    private static Vector2 ReadHalfVector2Flipped(byte[] data, ref int p) => new Vector2(ReadHalf(data, ref p), 1f - ReadHalf(data, ref p));
    private static Vector2 ReadVector2Flipped(byte[] data, ref int p) => new Vector2(ReadSingle(data, ref p), 1f - ReadSingle(data, ref p));
    private static Vector3 ReadVector3(byte[] data, ref int p) => new Vector3(ReadSingle(data, ref p), ReadSingle(data, ref p), ReadSingle(data, ref p));
    private static Vector4 ReadVector4(byte[] data, ref int p) => new Vector4(ReadSingle(data, ref p), ReadSingle(data, ref p), ReadSingle(data, ref p), ReadSingle(data, ref p));
    private static Vector2 ReadVector2(byte[] data, ref int p) => new Vector2(ReadSingle(data, ref p), ReadSingle(data, ref p));
    private static Color ReadColor(byte[] data, ref int p) => new Color(ReadSingle(data, ref p), ReadSingle(data, ref p), ReadSingle(data, ref p), ReadSingle(data, ref p));

    private static float ReadHalf(byte[] data, ref int p)
    {
        ushort h = ReadUInt16(data, ref p);
        uint sign = (uint)(h >> 15) & 1;
        uint exponent = (uint)(h >> 10) & 0x1f;
        uint fraction = (uint)h & 0x3ff;
        uint bits;
        if (exponent == 0)
        {
            if (fraction == 0) bits = sign << 31;
            else
            {
                int e = -14;
                while ((fraction & 0x400) == 0) { fraction <<= 1; e--; }
                fraction &= 0x3ff;
                bits = (sign << 31) | ((uint)(e + 127) << 23) | (fraction << 13);
            }
        }
        else if (exponent == 31) bits = (sign << 31) | 0x7f800000 | (fraction << 13);
        else bits = (sign << 31) | ((exponent + 112) << 23) | (fraction << 13);
        return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
    }

    private static bool Has(ModelData model, string attribute) => Array.Exists(model.Attributes, a => a.Trim() == attribute);
    private static bool IsV05(string version) => version.StartsWith("LAYAMODEL:05", StringComparison.Ordinal) || version.StartsWith("LAYAMODEL:COMPRESSION_05", StringComparison.Ordinal);
    private static bool IsLegacyCompatible(string version) => version == "LAYAMODEL:0301" || version == "LAYAMODEL:0400";
    private static string GetString(ModelData model, int index)
    {
        if (index < 0 || index >= model.Strings.Length) throw new InvalidDataException($"字符串表索引 {index} 越界。");
        return model.Strings[index];
    }

    private static float GetComponent(Vector4 value, int index)
    {
        switch (index) { case 0: return value.x; case 1: return value.y; case 2: return value.z; default: return value.w; }
    }

    private static int ToInt(uint value, string what)
    {
        if (value > int.MaxValue) throw new InvalidDataException($"{what}超出支持范围。");
        return (int)value;
    }

    private static void EnsureRange(byte[] data, int offset, int length, string what)
    {
        if (offset < 0 || length < 0 || (long)offset + length > data.Length) throw new InvalidDataException($"{what}超出文件范围。");
    }

    private static string ReadUtfString(byte[] data, ref int p, int limit)
    {
        int length = ReadUInt16(data, ref p, limit);
        EnsureRange(data, p, length, "字符串");
        if ((long)p + length > limit) throw new InvalidDataException("字符串跨越数据区边界。");
        string value = Encoding.UTF8.GetString(data, p, length);
        p += length;
        return value;
    }

    private static byte ReadByte(byte[] data, ref int p, int limit = int.MaxValue)
    {
        EnsureRange(data, p, 1, "Byte");
        if (p >= limit) throw new InvalidDataException("读取位置跨越区块边界。");
        return data[p++];
    }

    private static ushort ReadUInt16(byte[] data, ref int p, int limit = int.MaxValue)
    {
        if ((long)p + 2 > limit) throw new InvalidDataException("UInt16 读取跨越区块边界。");
        EnsureRange(data, p, 2, "UInt16");
        ushort value = (ushort)(data[p] | data[p + 1] << 8); p += 2; return value;
    }

    private static short ReadInt16(byte[] data, ref int p, int limit = int.MaxValue) => unchecked((short)ReadUInt16(data, ref p, limit));

    private static uint ReadUInt32(byte[] data, ref int p, int limit = int.MaxValue)
    {
        if ((long)p + 4 > limit) throw new InvalidDataException("UInt32 读取跨越区块边界。");
        EnsureRange(data, p, 4, "UInt32");
        uint value = (uint)(data[p] | data[p + 1] << 8 | data[p + 2] << 16 | data[p + 3] << 24); p += 4; return value;
    }

    private static float ReadSingle(byte[] data, ref int p, int limit = int.MaxValue)
    {
        if ((long)p + 4 > limit) throw new InvalidDataException("Float32 读取跨越区块边界。");
        EnsureRange(data, p, 4, "Float32");
        float value = BitConverter.ToSingle(data, p); p += 4; return value;
    }
}

[CustomEditor(typeof(MeshConvertor))]
public class MeshConvertorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        SerializedProperty pathProperty = serializedObject.FindProperty(nameof(MeshConvertor.LayaMesh路径));

        EditorGUILayout.LabelField("Laya 模型 => Unity Mesh", EditorStyles.boldLabel);
        Rect dropArea = GUILayoutUtility.GetRect(0f, 64f, GUILayout.ExpandWidth(true));
        GUI.Box(dropArea, "将 .lm 文件拖放到此处", EditorStyles.helpBox);
        HandleFileDrop(dropArea, pathProperty);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("当前文件", GUILayout.Width(60f));
        EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(pathProperty.stringValue) ? "未选择" : pathProperty.stringValue,
            EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        if (GUILayout.Button("浏览…", GUILayout.Width(64f)))
        {
            string current = pathProperty.stringValue;
            string directory = string.IsNullOrEmpty(current) ? Application.dataPath : Path.GetDirectoryName(current);
            string selected = EditorUtility.OpenFilePanel("选择 Laya .lm 模型", directory, "lm");
            if (!string.IsNullOrEmpty(selected)) pathProperty.stringValue = Path.GetFullPath(selected);
        }
        EditorGUILayout.EndHorizontal();

        serializedObject.ApplyModifiedProperties();
        EditorGUILayout.Space();
        if (GUILayout.Button("转换 Mesh")) ((MeshConvertor)target).SaveAsset();
    }

    private static void HandleFileDrop(Rect area, SerializedProperty pathProperty)
    {
        Event evt = Event.current;
        if (!area.Contains(evt.mousePosition) || (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)) return;

        string candidate = null;
        foreach (string path in DragAndDrop.paths)
        {
            if (IsLmFile(path)) { candidate = path; break; }
        }
        if (candidate == null)
        {
            foreach (UnityEngine.Object item in DragAndDrop.objectReferences)
            {
                string assetPath = AssetDatabase.GetAssetPath(item);
                if (IsLmFile(assetPath)) { candidate = assetPath; break; }
            }
        }

        DragAndDrop.visualMode = candidate == null ? DragAndDropVisualMode.Rejected : DragAndDropVisualMode.Copy;
        if (evt.type == EventType.DragPerform && candidate != null)
        {
            DragAndDrop.AcceptDrag();
            if (!Path.IsPathRooted(candidate))
                candidate = Path.Combine(Directory.GetParent(Application.dataPath).FullName, candidate);
            pathProperty.stringValue = Path.GetFullPath(candidate);
            evt.Use();
        }
        else if (evt.type == EventType.DragUpdated)
        {
            evt.Use();
        }
    }

    private static bool IsLmFile(string path)
    {
        return !string.IsNullOrEmpty(path) && string.Equals(Path.GetExtension(path), ".lm", StringComparison.OrdinalIgnoreCase);
    }
}
#endif
