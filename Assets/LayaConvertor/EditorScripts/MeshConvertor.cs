#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using System;
using System.IO;
using System.Text;

public class MeshConvertor : MonoBehaviour
{
    [Header("Laya模型 => Unity模型 (Version:1.0)")]
    [Header("---")]
    [Header("作者：暖阳WarmSun QQ:1134478590")]
    [Header("本插件免费且开源，若遇收费请及时举报商家并反馈")]
    [Header("Github项目地址：https://github.com/wujiayang2007/LayaConvertor")]
    [Header("---")]
    [Header("第一步：输入lm模型路径(绝对路径)")]
    public string LayaMesh路径;
    [Header("第二步：输入顶点数量(可从Laya模型预览面板上获取)")]
    public ushort 顶点数量;
    public void SaveAsset()
    {
        Debug.Log("开始转换LayaMesh");
        try
        {
            var mesh = ToUnityMesh(LayaMesh路径, 顶点数量);
            if (mesh != null)
            {
                AssetDatabase.CreateAsset(mesh, $"Assets/LayaMeshConvertor_{mesh.name}.asset");
                Debug.Log($"Mesh转换成功，路径：Assets/LayaMeshConvertor_{mesh.name}.asset");
            }
            else
                Debug.LogError("Mesh转换失败，Mesh为空");
        }
        catch (Exception e)
        {
            Debug.LogError($"Mesh转换失败：{e.ToString()}");
        }
    }
    private static string ConverToString(byte[] data)
    {
        string str;
        StringBuilder stb = new StringBuilder();
        for (int i = 0; i < data.Length; i++)
        {
            if ((int)data[i] > 15)
            {
                stb.Append(Convert.ToString(data[i], 16).ToUpper()); //添加字符串
            }
            else  //如果是小于0F需要加个零
            {
                stb.Append("0" + Convert.ToString(data[i], 16).ToUpper());
            }
        }
        str = stb.ToString();
        return str;
    }

    private static int[] VertexStructure = new int[7];
    public static Mesh ToUnityMesh(string path, ushort vertexCount)
    {
        Mesh mesh = new Mesh();
        using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
        using (BinaryReader reader = new BinaryReader(fileStream))
        {
            int headerL = reader.ReadUInt16();
            string Header = Encoding.Default.GetString(reader.ReadBytes(headerL));
            Debug.Log(Header);
            if (Header.Contains("LAYAMODEL"))
            {
                if (Header.Contains("COMPRESSION"))
                {
                    Debug.LogError("暂不支持压缩过的Laya模型！");
                    throw new NotImplementedException();
                }
                else
                {
                    for (int i = 0; i < fileStream.Length; i++)
                    {
                        fileStream.Position = i;
                        if (ConverToString(reader.ReadBytes(6)) == "04004D455348")
                        {
                            fileStream.Position -= 6;
                            break;
                        }
                    }
                    List<string> list = new List<string>();

                    int MESHL = reader.ReadUInt16();
                    string MESH = Encoding.Default.GetString(reader.ReadBytes(MESHL));
                    Debug.Log(MESH);
                    list.Add(MESH);

                    int SUBMESHL = reader.ReadUInt16();
                    string SUBMESH = Encoding.Default.GetString(reader.ReadBytes(SUBMESHL));
                    Debug.Log(SUBMESH);
                    list.Add(SUBMESH);

                    int itemL = reader.ReadUInt16();
                    string item = Encoding.Default.GetString(reader.ReadBytes(itemL));
                    Debug.Log(item);
                    list.Add(item);
                    mesh.name = item;
                    
                    int textL = reader.ReadUInt16();
                    string text = Encoding.Default.GetString(reader.ReadBytes(textL));
                    Debug.Log(text);

                    long position10 = fileStream.Position;

                    for (int i = 0; i < VertexStructure.Length; i++)
                    {
                        VertexStructure[i] = 0;
                    }
                    List<Vector3> vertices = new List<Vector3>();
                    List<Vector3> normals = new List<Vector3>();
                    List<Color> colors = new List<Color>();
                    List<Vector2> uv = new List<Vector2>();
                    List<Vector2> uv2 = new List<Vector2>();
                    List<Vector4> tangents = new List<Vector4>();
                    if (text.Contains("POSITION"))
                    {
                        VertexStructure[0] = 1;
                    }
                    if (text.Contains("NORMAL"))
                    {
                        VertexStructure[1] = 1;
                    }
                    if (text.Contains("COLOR"))
                    {
                        VertexStructure[2] = 1;
                    }
                    if (text.Contains("UV"))
                    {
                        VertexStructure[3] = 1;
                    }
                    if (text.Contains("UV1"))
                    {
                        VertexStructure[4] = 1;
                    }
                    if (text.Contains("TANGENT"))
                    {
                        VertexStructure[6] = 1;
                    }

                    reader.ReadUInt32();
                    fileStream.Position = position10;
                    for (int j = 0; j < vertexCount; j++)
                    {
                        vertices.Add(new Vector3(
                            -BitConverter.ToSingle(reader.ReadBytes(4), 0),
                            BitConverter.ToSingle(reader.ReadBytes(4), 0),
                            BitConverter.ToSingle(reader.ReadBytes(4), 0)
                            ));
                        if (VertexStructure[1] == 1)
                        {
                            normals.Add(new Vector3(
                            -BitConverter.ToSingle(reader.ReadBytes(4), 0),
                            BitConverter.ToSingle(reader.ReadBytes(4), 0),
                            BitConverter.ToSingle(reader.ReadBytes(4), 0)
                            ));
                        }
                        if (VertexStructure[2] == 1)
                        {
                            colors.Add(new Color(
                            BitConverter.ToSingle(reader.ReadBytes(4), 0),
                            BitConverter.ToSingle(reader.ReadBytes(4), 0),
                            BitConverter.ToSingle(reader.ReadBytes(4), 0),
                            BitConverter.ToSingle(reader.ReadBytes(4), 0)
                            ));
                        }
                        if (VertexStructure[3] == 1)
                        {
                            uv.Add(new Vector2(
                            BitConverter.ToSingle(reader.ReadBytes(4), 0),
                            (BitConverter.ToSingle(reader.ReadBytes(4), 0) - 1f) / -1f
                            ));
                        }
                        if (VertexStructure[4] == 1)
                        {
                            uv2.Add(new Vector2(
                            BitConverter.ToSingle(reader.ReadBytes(4), 0),
                            (BitConverter.ToSingle(reader.ReadBytes(4), 0) - 1f) / -1f
                            ));
                        }
                        if (VertexStructure[6] == 1)
                        {
                            tangents.Add(new Vector4(
                            -BitConverter.ToSingle(reader.ReadBytes(4), 0),
                            BitConverter.ToSingle(reader.ReadBytes(4), 0),
                            BitConverter.ToSingle(reader.ReadBytes(4), 0),
                            BitConverter.ToSingle(reader.ReadBytes(4), 0)
                            ));
                        }
                    }
                    mesh.vertices = vertices.ToArray();
                    mesh.normals = normals.ToArray();
                    mesh.colors = colors.ToArray();
                    mesh.uv = uv.ToArray();
                    mesh.uv2 = uv2.ToArray();
                    mesh.tangents = tangents.ToArray();

                    List<int> triangles = new List<int>();
                    while (true)
                    {
                        if (fileStream.Position == fileStream.Length) break;
                        else triangles.Add(reader.ReadUInt16());
                    }
                    mesh.triangles = triangles.ToArray();
                }
            }
            else
            {
                Debug.LogError("不支持该模型！");
                throw new NotSupportedException();
            }
        }
        return mesh;
    }
}

[CustomEditor(typeof(MeshConvertor))]
public class MeshConvertorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        MeshConvertor myScript = (MeshConvertor)target;

        if (GUILayout.Button("转换Mesh"))
        {
            myScript.SaveAsset();
        }
    }
}
#endif