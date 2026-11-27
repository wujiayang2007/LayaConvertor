#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[Serializable]
public sealed class LmatDocument
{
    public string version;
    public LmatProperties props;
}

[Serializable]
public sealed class LmatProperties
{
    public string name;
    public string type;
    public int cull = 2;
    public int blend;
    public int srcBlend = 1;
    public int dstBlend;
    public bool alphaTest;
    public float alphaTestValue = 0.5f;
    public bool depthWrite = true;
    public int depthTest = 515;
    public int renderQueue = 1;
    public float albedoIntensity = 1f;
    public bool enableLighting;
    public float shininess = 0.2f;
    public LmatTexture[] textures;
    public LmatVector[] vectors;
    public string[] defines;
}

[Serializable]
public sealed class LmatTexture
{
    public string name;
    public string path;
    public LmatTextureParams @params;
}

[Serializable]
public sealed class LmatTextureParams
{
    public bool mipmap = true;
    public int filterMode = 1;
    public int wrapModeU;
    public int wrapModeV;
    public int anisoLevel = 1;
    public int format;
}

[Serializable]
public sealed class LmatVector
{
    public string name;
    public float[] value;
}

[AddComponentMenu("Laya/LMAT Convertor")]
public class LmatConvertor : MonoBehaviour
{
    [HideInInspector]
    public string LmatPath;

    public void SaveAsset()
    {
        try
        {
            string absolutePath = NormalizeToAbsolute(LmatPath);
            Material material = GetOrCreateAsset(absolutePath);
            string assetPath = AssetDatabase.GetAssetPath(material);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"LMAT 转换成功：{assetPath}", this);
        }
        catch (Exception e)
        {
            Debug.LogError($"LMAT 转换失败：{e.Message}\n{e}", this);
        }
    }

    public static LmatDocument ReadDocument(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 .lmat 文件。", path);
        LmatDocument document = JsonUtility.FromJson<LmatDocument>(File.ReadAllText(path, Encoding.UTF8));
        if (document == null || document.props == null) throw new InvalidDataException(".lmat JSON 缺少 props 对象。");
        if (document.version != "LAYAMATERIAL:01")
            throw new NotSupportedException($"当前转换器按工程样本实现了 LAYAMATERIAL:01；文件版本为 {document.version}。");
        return document;
    }

    public static Material GetOrCreateAsset(string path)
    {
        string absolutePath = Path.GetFullPath(path);
        LmatDocument document = ReadDocument(absolutePath);
        string outputFolder = "Assets/LayaConvertedMaterials";
        EnsureAssetFolder(outputFolder);
        string fileName = SanitizeFileName(string.IsNullOrWhiteSpace(document.props.name)
            ? Path.GetFileNameWithoutExtension(absolutePath)
            : document.props.name);
        string assetPath = $"{outputFolder}/{fileName}_{StablePathHash(absolutePath)}.mat";
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        if (existing != null) return existing;
        Material material = CreateMaterial(document, absolutePath);
        AssetDatabase.CreateAsset(material, assetPath);
        return material;
    }

    private static string StablePathHash(string path)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())))
                .Replace("-", "").Substring(0, 10);
    }

    private static Material CreateMaterial(LmatDocument document, string lmatPath)
    {
        LmatProperties props = document.props;
        Shader shader = FindShader(props);
        if (shader == null) throw new InvalidOperationException("Unity 内置材质 Shader 不可用；请确认项目使用 Built-in Render Pipeline。");
        var material = new Material(shader) { name = string.IsNullOrWhiteSpace(props.name) ? Path.GetFileNameWithoutExtension(lmatPath) : props.name };

        LmatVector albedo = FindVector(props, "albedoColor");
        LmatVector tint = FindVector(props, "tintColor");
        LmatVector specular = FindVector(props, "specularColor");
        LmatVector tilingOffset = FindVector(props, "tilingOffset");
        Color color = ReadColor(albedo ?? tint, Color.white);
        if (albedo != null)
        {
            color.r *= props.albedoIntensity;
            color.g *= props.albedoIntensity;
            color.b *= props.albedoIntensity;
        }
        SetColorIfPresent(material, "_Color", color);
        SetColorIfPresent(material, "_TintColor", color);
        if (specular != null) SetColorIfPresent(material, "_SpecColor", ReadColor(specular, Color.white));
        SetFloatIfPresent(material, "_Glossiness", Mathf.Clamp01(props.shininess));
        SetFloatIfPresent(material, "_Cutoff", props.alphaTestValue);

        ApplyRenderState(material, props);
        ApplyVectors(material, props, tilingOffset);
        ApplyTextures(material, props, lmatPath, tilingOffset);

        if (props.defines != null && props.defines.Length > 0)
            Debug.LogWarning($"LMAT '{material.name}' 含有 Laya Shader define：{string.Join(", ", props.defines)}。Unity 内置 Shader 无法复现这些自定义宏，已保留其通用材质和混合设置。", material);
        return material;
    }

    private static Shader FindShader(LmatProperties props)
    {
        if (!string.IsNullOrWhiteSpace(props.type))
        {
            Shader requested = Shader.Find(props.type);
            if (requested != null) return requested;
        }
        bool additive = props.blend != 0 && props.srcBlend == 770 && props.dstBlend == 1;
        if (additive) return Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Unlit/Transparent");
        if (!props.enableLighting)
        {
            if (props.alphaTest) return Shader.Find("Unlit/Transparent Cutout") ?? Shader.Find("Unlit/Texture");
            if (props.blend != 0) return Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? Shader.Find("Unlit/Transparent");
            return Shader.Find("Unlit/Texture");
        }
        return Shader.Find("Standard (Specular setup)") ?? Shader.Find("Standard");
    }

    private static void ApplyRenderState(Material material, LmatProperties props)
    {
        SetFloatIfPresent(material, "_Cull", Mathf.Clamp(props.cull, 0, 2));
        SetFloatIfPresent(material, "_ZWrite", props.depthWrite ? 1 : 0);
        SetFloatIfPresent(material, "_ZTest", ToUnityCompareFunction(props.depthTest));
        SetFloatIfPresent(material, "_SrcBlend", ToUnityBlendMode(props.srcBlend));
        SetFloatIfPresent(material, "_DstBlend", ToUnityBlendMode(props.dstBlend));

        bool transparent = props.blend != 0;
        if (material.HasProperty("_Mode"))
        {
            float mode = props.alphaTest ? 1f : transparent ? 3f : 0f;
            material.SetFloat("_Mode", mode);
            if (props.alphaTest) material.EnableKeyword("_ALPHATEST_ON");
            else material.DisableKeyword("_ALPHATEST_ON");
            if (transparent && !props.alphaTest) material.EnableKeyword("_ALPHABLEND_ON");
            else material.DisableKeyword("_ALPHABLEND_ON");
        }
        material.SetOverrideTag("RenderType", props.alphaTest ? "TransparentCutout" : transparent ? "Transparent" : "Opaque");
        material.renderQueue = props.alphaTest ? (int)RenderQueue.AlphaTest : transparent ? (int)RenderQueue.Transparent : (int)RenderQueue.Geometry;
    }

    private static void ApplyVectors(Material material, LmatProperties props, LmatVector tilingOffset)
    {
        if (props.vectors == null) return;
        foreach (LmatVector vector in props.vectors)
        {
            if (vector == null || string.IsNullOrEmpty(vector.name) || vector.value == null || vector.name == "tilingOffset") continue;
            string property = vector.name.StartsWith("_", StringComparison.Ordinal) ? vector.name : "_" + char.ToUpperInvariant(vector.name[0]) + vector.name.Substring(1);
            if (vector.name == "albedoColor" || vector.name == "tintColor" || vector.name == "specularColor") continue;
            if (!material.HasProperty(property)) continue;
            if (vector.value.Length == 2) material.SetVector(property, new Vector4(vector.value[0], vector.value[1], 0f, 0f));
            else if (vector.value.Length == 3) material.SetVector(property, new Vector4(vector.value[0], vector.value[1], vector.value[2], 0f));
            else if (vector.value.Length == 4) material.SetVector(property, new Vector4(vector.value[0], vector.value[1], vector.value[2], vector.value[3]));
        }
    }

    private static void ApplyTextures(Material material, LmatProperties props, string lmatPath, LmatVector tilingOffset)
    {
        if (props.textures == null) return;
        bool mainTextureSet = false;
        string sourceDirectory = Path.GetDirectoryName(lmatPath);
        foreach (LmatTexture texture in props.textures)
        {
            if (texture == null || string.IsNullOrWhiteSpace(texture.path)) continue;
            string absoluteTexturePath = Path.GetFullPath(Path.Combine(sourceDirectory, texture.path.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(absoluteTexturePath))
            {
                Debug.LogWarning($"LMAT 贴图不存在：{absoluteTexturePath}");
                continue;
            }
            string assetPath = EnsureTextureAsset(absoluteTexturePath, props.name);
            Texture2D textureAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (textureAsset == null) throw new InvalidDataException($"无法导入材质贴图：{absoluteTexturePath}");
            ConfigureTexture(assetPath, texture.@params);

            string property = TextureProperty(texture.name);
            if (string.IsNullOrEmpty(property) || !material.HasProperty(property)) continue;
            material.SetTexture(property, textureAsset);
            if (property == "_MainTex") mainTextureSet = true;
        }
        if (mainTextureSet && tilingOffset != null && tilingOffset.value != null && tilingOffset.value.Length >= 4)
        {
            material.mainTextureScale = new Vector2(tilingOffset.value[0], tilingOffset.value[1]);
            material.mainTextureOffset = new Vector2(tilingOffset.value[2], tilingOffset.value[3]);
        }
    }

    private static string EnsureTextureAsset(string sourcePath, string materialName)
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string fullSource = Path.GetFullPath(sourcePath);
        string rootPrefix = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (fullSource.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string relative = fullSource.Substring(rootPrefix.Length).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                AssetDatabase.ImportAsset(relative, ImportAssetOptions.ForceSynchronousImport);
                return relative;
            }
        }

        string safeName = SanitizeFileName(string.IsNullOrWhiteSpace(materialName) ? "Material" : materialName);
        string folder = $"Assets/LayaConvertedTextures/{safeName}";
        EnsureAssetFolder(folder);
        string hash;
        using (SHA256 sha = SHA256.Create())
            hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(fullSource))).Replace("-", "").Substring(0, 10);
        string destination = $"{folder}/{Path.GetFileNameWithoutExtension(fullSource)}_{hash}{Path.GetExtension(fullSource)}";
        string fullDestination = Path.Combine(projectRoot, destination.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullDestination)) File.Copy(fullSource, fullDestination);
        AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
        return destination;
    }

    private static void ConfigureTexture(string assetPath, LmatTextureParams parameters)
    {
        if (parameters == null) return;
        TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null) return;
        importer.mipmapEnabled = parameters.mipmap;
        importer.filterMode = parameters.filterMode >= 0 && parameters.filterMode <= 2 ? (FilterMode)parameters.filterMode : FilterMode.Bilinear;
        importer.wrapModeU = ToUnityWrapMode(parameters.wrapModeU);
        importer.wrapModeV = ToUnityWrapMode(parameters.wrapModeV);
        importer.anisoLevel = Mathf.Clamp(parameters.anisoLevel, 0, 16);
        importer.SaveAndReimport();
    }

    private static string TextureProperty(string layaName)
    {
        switch (layaName)
        {
            case "albedoTexture": case "diffuseTexture": return "_MainTex";
            case "normalTexture": return "_BumpMap";
            case "specularTexture": return "_SpecGlossMap";
            case "emissionTexture": return "_EmissionMap";
            default: return null;
        }
    }

    private static int ToUnityBlendMode(int laya)
    {
        switch (laya)
        {
            case 0: return (int)BlendMode.Zero;
            case 1: return (int)BlendMode.One;
            case 768: return (int)BlendMode.SrcColor;
            case 769: return (int)BlendMode.OneMinusSrcColor;
            case 770: return (int)BlendMode.SrcAlpha;
            case 771: return (int)BlendMode.OneMinusSrcAlpha;
            case 772: return (int)BlendMode.DstAlpha;
            case 773: return (int)BlendMode.OneMinusDstAlpha;
            case 774: return (int)BlendMode.DstColor;
            case 775: return (int)BlendMode.OneMinusDstColor;
            case 776: return (int)BlendMode.SrcAlphaSaturate;
            default: return laya;
        }
    }

    private static int ToUnityCompareFunction(int laya)
    {
        switch (laya)
        {
            case 512: return (int)CompareFunction.Never;
            case 513: return (int)CompareFunction.Less;
            case 514: return (int)CompareFunction.Equal;
            case 515: return (int)CompareFunction.LessEqual;
            case 516: return (int)CompareFunction.Greater;
            case 517: return (int)CompareFunction.NotEqual;
            case 518: return (int)CompareFunction.GreaterEqual;
            case 519: return (int)CompareFunction.Always;
            default: return laya;
        }
    }

    private static TextureWrapMode ToUnityWrapMode(int laya)
    {
        switch (laya) { case 1: return TextureWrapMode.Clamp; case 2: return TextureWrapMode.Mirror; default: return TextureWrapMode.Repeat; }
    }

    private static LmatVector FindVector(LmatProperties props, string name)
    {
        if (props.vectors == null) return null;
        foreach (LmatVector vector in props.vectors) if (vector != null && vector.name == name) return vector;
        return null;
    }

    private static Color ReadColor(LmatVector vector, Color fallback)
    {
        if (vector == null || vector.value == null || vector.value.Length < 3) return fallback;
        return new Color(vector.value[0], vector.value[1], vector.value[2], vector.value.Length > 3 ? vector.value[3] : 1f);
    }

    private static void SetColorIfPresent(Material material, string property, Color value)
    {
        if (material.HasProperty(property)) material.SetColor(property, value);
    }

    private static void SetFloatIfPresent(Material material, string property, float value)
    {
        if (material.HasProperty(property)) material.SetFloat(property, value);
    }

    private static string NormalizeToAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请先拖入或选择 .lmat 文件。");
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(full), ".lmat", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("输入文件必须是 .lmat。");
        return full;
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "LayaMaterial" : value;
    }

    private static void EnsureAssetFolder(string assetFolder)
    {
        string[] parts = assetFolder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
}

[CustomEditor(typeof(LmatConvertor))]
public class LmatConvertorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        SerializedProperty path = serializedObject.FindProperty(nameof(LmatConvertor.LmatPath));
        EditorGUILayout.LabelField("Laya .lmat => Unity Material", EditorStyles.boldLabel);
        Rect area = GUILayoutUtility.GetRect(0f, 64f, GUILayout.ExpandWidth(true));
        GUI.Box(area, "将 .lmat 文件拖放到此处", EditorStyles.helpBox);
        HandleDrop(area, path);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("当前文件", GUILayout.Width(60f));
        EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(path.stringValue) ? "未选择" : path.stringValue,
            EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        if (GUILayout.Button("浏览…", GUILayout.Width(64f)))
        {
            string directory = string.IsNullOrEmpty(path.stringValue) ? Application.dataPath : Path.GetDirectoryName(path.stringValue);
            string selected = EditorUtility.OpenFilePanel("选择 Laya 材质文件", directory, "lmat");
            if (!string.IsNullOrEmpty(selected)) path.stringValue = Path.GetFullPath(selected);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.HelpBox("目前将 Laya 通用颜色、贴图、UV、混合和深度属性映射到 Unity 内置材质。自定义 Laya Shader define 不会被等价转换。", MessageType.Info);
        serializedObject.ApplyModifiedProperties();
        if (GUILayout.Button("转换 Material")) ((LmatConvertor)target).SaveAsset();
    }

    private static void HandleDrop(Rect area, SerializedProperty property)
    {
        Event evt = Event.current;
        if (!area.Contains(evt.mousePosition) || (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)) return;
        string candidate = null;
        foreach (string item in DragAndDrop.paths)
        {
            if (IsLmat(item)) { candidate = item; break; }
        }
        if (candidate == null)
        {
            foreach (UnityEngine.Object item in DragAndDrop.objectReferences)
            {
                string itemPath = AssetDatabase.GetAssetPath(item);
                if (IsLmat(itemPath)) { candidate = itemPath; break; }
            }
        }
        DragAndDrop.visualMode = candidate == null ? DragAndDropVisualMode.Rejected : DragAndDropVisualMode.Copy;
        if (evt.type == EventType.DragPerform && candidate != null)
        {
            DragAndDrop.AcceptDrag();
            if (!Path.IsPathRooted(candidate)) candidate = Path.Combine(Directory.GetParent(Application.dataPath).FullName, candidate);
            property.stringValue = Path.GetFullPath(candidate);
            evt.Use();
        }
        else if (evt.type == EventType.DragUpdated) evt.Use();
    }

    private static bool IsLmat(string path) => !string.IsNullOrEmpty(path) &&
        string.Equals(Path.GetExtension(path), ".lmat", StringComparison.OrdinalIgnoreCase);
}
#endif
