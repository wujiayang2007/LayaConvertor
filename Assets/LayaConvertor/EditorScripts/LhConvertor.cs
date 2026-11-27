#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

[Serializable]
public sealed class LhDocument
{
    public string version;
    public LhNode data;
    public LhNode root;
    public string type;
    public LhProperties props;
    public LhProperties customProps;
    public LhNode[] child;
}

[Serializable]
public sealed class LhNode
{
    public string type;
    public LhProperties props;
    public LhProperties customProps;
    public LhNode[] child;
}

[Serializable]
public sealed class LhProperties
{
    public string name;
    public bool isStatic;
    public float[] translate;
    public float[] position;
    public float[] rotation;
    public float[] scale;
    public int layer;
    public string meshPath;
    public string rootBone;
    public LhAssetReference[] materials;
    public LhAssetReference material;
    public bool isPerformanceMode;
    public float duration;
    public bool looping;
    public bool prewarm;
    public int startDelayType;
    public float startDelay;
    public float startDelayMin;
    public float startDelayMax;
    public int startLifetimeType;
    public float startLifetimeConstant;
    public float startLifetimeConstantMin;
    public float startLifetimeConstantMax;
    public int startSpeedType;
    public float startSpeedConstant;
    public float startSpeedConstantMin;
    public float startSpeedConstantMax;
    public bool threeDStartSize;
    public int startSizeType;
    public float startSizeConstant;
    public float startSizeConstantMin;
    public float startSizeConstantMax;
    public float[] startSizeConstantSeparate;
    public float[] startSizeConstantMinSeparate;
    public float[] startSizeConstantMaxSeparate;
    public bool threeDStartRotation;
    public int startRotationType;
    public float startRotationConstant;
    public float[] startColorConstant;
    public float[] gravity;
    public float gravityModifier;
    public int simulationSpace;
    public bool playOnAwake;
    public int maxParticles;
    public bool autoRandomSeed;
    public uint randomSeed;
    public LhEmission emission;
    public LhVelocityOverLifetime velocityOverLifetime;
    public LhColorOverLifetime colorOverLifetime;
    public LhSizeOverLifetime sizeOverLifetime;
    public LhShape shape;
    public int renderMode;
    public float stretchedBillboardCameraSpeedScale;
    public float stretchedBillboardSpeedScale;
    public float stretchedBillboardLengthScale;
    public float sortingFudge;
    public LhParticleRenderer renderer;
}

[Serializable]
public sealed class LhAssetReference
{
    public string type;
    public string path;
}

[Serializable]
public sealed class LhParticleRenderer
{
    public LhParticleResources resources;
}

[Serializable]
public sealed class LhParticleResources
{
    public string mesh;
    public string material;
}

[Serializable]
public sealed class LhEmission
{
    public bool enable;
    public float emissionRate;
    public LhBurst[] bursts;
}

[Serializable]
public sealed class LhBurst
{
    public float time;
    public int min;
    public int max;
}

[Serializable]
public sealed class LhVelocityOverLifetime
{
    public bool enable;
    public int space;
    public LhVectorCurve velocity;
}

[Serializable]
public sealed class LhVectorCurve
{
    public int type;
    public float[] constant;
    public float[] constantMin;
    public float[] constantMax;
}

[Serializable]
public sealed class LhColorOverLifetime
{
    public bool enable;
    public LhColorCurve color;
}

[Serializable]
public sealed class LhColorCurve
{
    public int type;
    public float[] constant;
    public LhGradient gradient;
    public LhGradient gradientMax;
}

[Serializable]
public sealed class LhGradient
{
    public LhGradientAlpha[] alphas;
    public LhGradientColor[] rgbs;
}

[Serializable]
public sealed class LhGradientAlpha
{
    public float key;
    public float value;
}

[Serializable]
public sealed class LhGradientColor
{
    public float key;
    public float[] value;
}

[Serializable]
public sealed class LhSizeOverLifetime
{
    public bool enable;
    public LhSizeCurve size;
}

[Serializable]
public sealed class LhSizeCurve
{
    public int type;
    public float constantMin;
    public float constantMax;
    public LhSizeGradient gradient;
    public LhSizeGradient gradientMax;
}

[Serializable]
public sealed class LhSizeGradient
{
    public LhSizeKey[] sizes;
}

[Serializable]
public sealed class LhSizeKey
{
    public float key;
    public float value;
}

[Serializable]
public sealed class LhShape
{
    public bool enable;
    public int shapeType;
    public float radius;
    public float angle;
    public float length;
    public float[] box;
}

[AddComponentMenu("Laya/LH Convertor")]
public class LhConvertor : MonoBehaviour
{
    [HideInInspector]
    public string LhPath;

    private readonly Dictionary<string, Mesh> meshCache = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Material> materialCache = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
    private readonly List<SkinnedPending> pendingSkinned = new List<SkinnedPending>();
    private readonly List<string> warnings = new List<string>();
    private GameObject pendingRoot;
    private sealed class SkinnedPending
    {
        public SkinnedMeshRenderer Renderer;
        public string MeshPath;
        public string RootBoneName;
    }

    public void SaveAsset()
    {
        GameObject instance = null;
        try
        {
            string sourcePath = NormalizeLhPath(LhPath);
            LhDocument document = JsonUtility.FromJson<LhDocument>(File.ReadAllText(sourcePath, Encoding.UTF8));
            LhNode rootNode = document == null ? null : document.data ?? document.root;
            if (rootNode == null && document != null && !string.IsNullOrEmpty(document.type))
                rootNode = new LhNode { type = document.type, props = document.props, customProps = document.customProps, child = document.child };
            if (rootNode == null) throw new InvalidDataException(".lh 文件没有可解析的节点根对象。");

            meshCache.Clear(); materialCache.Clear(); pendingSkinned.Clear(); warnings.Clear(); pendingRoot = null;
            instance = CreateNode(rootNode, sourcePath);
            ResolveSkinnedBones(instance);
            string folder = "Assets/LayaConvertedPrefabs";
            EnsureFolder(folder);
            string prefabName = SanitizeFileName(string.IsNullOrWhiteSpace(instance.name) ? Path.GetFileNameWithoutExtension(sourcePath) : instance.name);
            string prefabPath = $"{folder}/{prefabName}_{HashPath(sourcePath)}.prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            if (prefab == null) throw new InvalidOperationException("Unity 未能保存 Prefab。");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"LH 转换成功：{prefabPath}\n节点：{instance.GetComponentsInChildren<Transform>(true).Length}，提示：{warnings.Count}", this);
            foreach (string warning in warnings) Debug.LogWarning(warning, prefab);
        }
        catch (Exception e)
        {
            Debug.LogError($"LH 转换失败：{e.Message}\n{e}", this);
        }
        finally
        {
            if (instance == null) instance = pendingRoot;
            if (instance != null) DestroyImmediate(instance);
            pendingRoot = null;
        }
    }

    private GameObject CreateNode(LhNode node, string lhPath)
    {
        string type = node.type;
        LhProperties props = node.props ?? new LhProperties();
        LhProperties custom = node.customProps ?? props;
        string objectName = string.IsNullOrWhiteSpace(props.name) ? type : props.name;
        var gameObject = new GameObject(string.IsNullOrWhiteSpace(objectName) ? "LayaNode" : objectName);
        if (pendingRoot == null) pendingRoot = gameObject;
        ApplyTransform(gameObject.transform, custom, props);
        gameObject.layer = Mathf.Clamp(custom.layer, 0, 31);

        string meshPath = FirstNonEmpty(custom.meshPath, props.meshPath);
        switch (type)
        {
            case "MeshSprite3D":
                AddMeshRenderer(gameObject, meshPath, custom.materials ?? props.materials, lhPath);
                break;
            case "SkinnedMeshSprite3D":
            case "SimpleSkinnedMeshSprite3D":
                AddSkinnedMeshRenderer(gameObject, meshPath, custom.materials ?? props.materials, custom.rootBone, lhPath);
                break;
            case "ShuriKenParticle3D":
                AddParticleSystem(gameObject, custom, lhPath);
                break;
            case "Scene3D": case "Sprite3D": case "Camera":
                break;
            default:
                warnings.Add($"节点 '{gameObject.name}' 的类型 '{type}' 未映射为 Unity 组件，保留为空 GameObject。");
                break;
        }

        LhNode[] children = node.child;
        if (children != null)
        {
            foreach (LhNode child in children)
            {
                if (child == null) continue;
                GameObject childObject = CreateNode(child, lhPath);
                childObject.transform.SetParent(gameObject.transform, false);
            }
        }
        return gameObject;
    }

    private void AddMeshRenderer(GameObject gameObject, string meshPath, LhAssetReference[] materialRefs, string lhPath)
    {
        if (string.IsNullOrEmpty(meshPath)) { warnings.Add($"网格节点 '{gameObject.name}' 没有 meshPath。"); return; }
        string absolute = ResolveReference(lhPath, meshPath);
        Mesh mesh = GetMeshAsset(absolute);
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = GetMaterials(materialRefs, lhPath, mesh.subMeshCount);
    }

    private void AddSkinnedMeshRenderer(GameObject gameObject, string meshPath, LhAssetReference[] materialRefs, string rootBone, string lhPath)
    {
        if (string.IsNullOrEmpty(meshPath)) { warnings.Add($"蒙皮网格节点 '{gameObject.name}' 没有 meshPath。"); return; }
        string absolute = ResolveReference(lhPath, meshPath);
        Mesh mesh = GetMeshAsset(absolute);
        SkinnedMeshRenderer renderer = gameObject.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.sharedMaterials = GetMaterials(materialRefs, lhPath, mesh.subMeshCount);
        pendingSkinned.Add(new SkinnedPending { Renderer = renderer, MeshPath = absolute, RootBoneName = rootBone });
    }

    private void ResolveSkinnedBones(GameObject root)
    {
        var transforms = new Dictionary<string, Transform>(StringComparer.Ordinal);
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (!transforms.ContainsKey(t.name)) transforms.Add(t.name, t);
        foreach (SkinnedPending pending in pendingSkinned)
        {
            string[] names = MeshConvertor.ReadBoneNames(pending.MeshPath);
            var bones = new Transform[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                if (!transforms.TryGetValue(names[i], out bones[i]))
                    warnings.Add($"蒙皮网格 '{pending.Renderer.name}' 找不到骨骼节点 '{names[i]}'；该 bone slot 将留空。");
            }
            pending.Renderer.bones = bones;
            if (!string.IsNullOrEmpty(pending.RootBoneName) && transforms.TryGetValue(pending.RootBoneName, out Transform rootBone))
                pending.Renderer.rootBone = rootBone;
            else if (bones.Length > 0) pending.Renderer.rootBone = bones[0];
        }
    }

    private void AddParticleSystem(GameObject gameObject, LhProperties props, string lhPath)
    {
        ParticleSystem system = gameObject.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = system.main;
        main.duration = Mathf.Max(0.01f, props.duration);
        main.loop = props.looping;
        main.prewarm = props.prewarm && props.looping;
        main.startDelay = ToMinMaxCurve(props.startDelayType, props.startDelay, props.startDelayMin, props.startDelayMax);
        main.startLifetime = ToMinMaxCurve(props.startLifetimeType, props.startLifetimeConstant, props.startLifetimeConstantMin, props.startLifetimeConstantMax);
        main.startSpeed = ToMinMaxCurve(props.startSpeedType, props.startSpeedConstant, props.startSpeedConstantMin, props.startSpeedConstantMax);
        main.startSize3D = props.threeDStartSize && props.startSizeConstantSeparate != null && props.startSizeConstantSeparate.Length >= 3;
        if (main.startSize3D)
        {
            main.startSizeX = new ParticleSystem.MinMaxCurve(Component(props.startSizeConstantSeparate, 0, props.startSizeConstant));
            main.startSizeY = new ParticleSystem.MinMaxCurve(Component(props.startSizeConstantSeparate, 1, props.startSizeConstant));
            main.startSizeZ = new ParticleSystem.MinMaxCurve(Component(props.startSizeConstantSeparate, 2, props.startSizeConstant));
        }
        else main.startSize = ToMinMaxCurve(props.startSizeType, props.startSizeConstant, Component(props.startSizeConstantMinSeparate, 0, props.startSizeConstantMin), Component(props.startSizeConstantMaxSeparate, 0, props.startSizeConstantMax));
        main.startColor = ReadColor(props.startColorConstant, Color.white);
        main.gravityModifier = props.gravityModifier;
        main.simulationSpace = props.simulationSpace == 1 ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
        main.maxParticles = props.maxParticles > 0 ? props.maxParticles : 1000;
        main.playOnAwake = props.playOnAwake;
        system.useAutoRandomSeed = props.autoRandomSeed;
        if (!props.autoRandomSeed) system.randomSeed = props.randomSeed;

        ParticleSystem.EmissionModule emission = system.emission;
        if (props.emission != null)
        {
            emission.enabled = props.emission.enable;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(Mathf.Max(0f, props.emission.emissionRate));
            if (props.emission.bursts != null && props.emission.bursts.Length > 0)
            {
                var bursts = new ParticleSystem.Burst[props.emission.bursts.Length];
                for (int i = 0; i < bursts.Length; i++)
                {
                    LhBurst b = props.emission.bursts[i];
                    bursts[i] = new ParticleSystem.Burst(Mathf.Max(0f, b.time), (short)Mathf.Clamp(b.min, 0, short.MaxValue), (short)Mathf.Clamp(b.max, 0, short.MaxValue));
                }
                emission.SetBursts(bursts);
            }
        }

        ApplyVelocity(system, props.velocityOverLifetime);
        ApplyColorOverLifetime(system, props.colorOverLifetime);
        ApplySizeOverLifetime(system, props.sizeOverLifetime);
        ApplyShape(system, props.shape);
        ParticleSystemRenderer particleRenderer = gameObject.GetComponent<ParticleSystemRenderer>();
        particleRenderer.renderMode = ToParticleRenderMode(props.renderMode);
        particleRenderer.lengthScale = Mathf.Max(0f, props.stretchedBillboardLengthScale);
        particleRenderer.cameraVelocityScale = Mathf.Max(0f, props.stretchedBillboardCameraSpeedScale);
        particleRenderer.velocityScale = Mathf.Max(0f, props.stretchedBillboardSpeedScale);
        particleRenderer.sortingFudge = props.sortingFudge;

        string materialPath = props.material == null ? null : props.material.path;
        if (string.IsNullOrEmpty(materialPath) && props.renderer != null && props.renderer.resources != null)
            materialPath = props.renderer.resources.material;
        if (!string.IsNullOrEmpty(materialPath)) particleRenderer.sharedMaterial = GetMaterialAsset(ResolveReference(lhPath, materialPath));
        if (props.shape != null && props.shape.enable && props.shape.shapeType == 6 && props.renderer?.resources != null && !string.IsNullOrEmpty(props.renderer.resources.mesh))
            particleRenderer.mesh = GetMeshAsset(ResolveReference(lhPath, props.renderer.resources.mesh));
    }

    private static void ApplyVelocity(ParticleSystem system, LhVelocityOverLifetime source)
    {
        if (source == null) return;
        ParticleSystem.VelocityOverLifetimeModule module = system.velocityOverLifetime;
        module.enabled = source.enable;
        module.space = source.space == 1 ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
        if (source.velocity?.constant != null)
        {
            float[] v = source.velocity.constant;
            module.x = new ParticleSystem.MinMaxCurve(Component(v, 0, 0f));
            module.y = new ParticleSystem.MinMaxCurve(Component(v, 1, 0f));
            module.z = new ParticleSystem.MinMaxCurve(-Component(v, 2, 0f));
        }
    }

    private static void ApplyColorOverLifetime(ParticleSystem system, LhColorOverLifetime source)
    {
        if (source == null || source.color == null) return;
        ParticleSystem.ColorOverLifetimeModule module = system.colorOverLifetime;
        module.enabled = source.enable;
        if (source.color.type == 1 && source.color.gradient != null)
            module.color = ToGradient(source.color.gradient);
        else
            module.color = new ParticleSystem.MinMaxGradient(ReadColor(source.color.constant, Color.white));
    }

    private static void ApplySizeOverLifetime(ParticleSystem system, LhSizeOverLifetime source)
    {
        if (source == null || source.size == null) return;
        ParticleSystem.SizeOverLifetimeModule module = system.sizeOverLifetime;
        module.enabled = source.enable;
        if (source.size.type == 1 && source.size.gradient != null && source.size.gradient.sizes != null && source.size.gradient.sizes.Length > 0)
        {
            var keys = new Keyframe[source.size.gradient.sizes.Length];
            for (int i = 0; i < keys.Length; i++) keys[i] = new Keyframe(source.size.gradient.sizes[i].key, source.size.gradient.sizes[i].value);
            module.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(keys));
        }
        else module.size = new ParticleSystem.MinMaxCurve(source.size.constantMin, source.size.constantMax);
    }

    private static void ApplyShape(ParticleSystem system, LhShape source)
    {
        if (source == null) return;
        ParticleSystem.ShapeModule module = system.shape;
        module.enabled = source.enable;
        int shape = source.shapeType;
        if (shape >= 0 && shape <= (int)ParticleSystemShapeType.Sprite)
            module.shapeType = (ParticleSystemShapeType)shape;
        module.radius = Mathf.Max(0f, source.radius);
        module.angle = Mathf.Clamp(source.angle, 0f, 90f);
        module.length = Mathf.Max(0f, source.length);
        if (source.box != null && source.box.Length >= 3)
            module.scale = new Vector3(Mathf.Abs(source.box[0]), Mathf.Abs(source.box[1]), Mathf.Abs(source.box[2]));
    }

    private void ApplyTransform(Transform transform, LhProperties custom, LhProperties props)
    {
        float[] position = custom.translate ?? custom.position ?? props.translate ?? props.position;
        float[] rotation = custom.rotation ?? props.rotation;
        float[] scale = custom.scale ?? props.scale;
        if (position != null && position.Length >= 3) transform.localPosition = new Vector3(-position[0], position[1], position[2]);
        if (rotation != null && rotation.Length >= 4) transform.localRotation = new Quaternion(rotation[0], -rotation[1], -rotation[2], rotation[3]);
        if (scale != null && scale.Length >= 3) transform.localScale = new Vector3(scale[0], scale[1], scale[2]);
    }

    private Mesh GetMeshAsset(string absolutePath)
    {
        if (meshCache.TryGetValue(absolutePath, out Mesh cached)) return cached;
        string folder = "Assets/LayaConvertedMeshes";
        EnsureFolder(folder);
        string assetPath = $"{folder}/{SanitizeFileName(Path.GetFileNameWithoutExtension(absolutePath))}_{HashPath(absolutePath)}.asset";
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (mesh == null)
        {
            mesh = MeshConvertor.ToUnityMesh(absolutePath);
            AssetDatabase.CreateAsset(mesh, assetPath);
        }
        meshCache[absolutePath] = mesh;
        return mesh;
    }

    private Material[] GetMaterials(LhAssetReference[] references, string lhPath, int subMeshCount)
    {
        if (references == null || references.Length == 0) return new Material[subMeshCount];
        var materials = new Material[Mathf.Max(subMeshCount, references.Length)];
        for (int i = 0; i < references.Length; i++)
        {
            if (references[i] == null || string.IsNullOrEmpty(references[i].path)) continue;
            materials[i] = GetMaterialAsset(ResolveReference(lhPath, references[i].path));
        }
        return materials;
    }

    private Material GetMaterialAsset(string absolutePath)
    {
        if (materialCache.TryGetValue(absolutePath, out Material cached)) return cached;
        Material material = LmatConvertor.GetOrCreateAsset(absolutePath);
        materialCache[absolutePath] = material;
        return material;
    }

    private static string ResolveReference(string lhPath, string reference)
    {
        string normalized = reference.Replace('/', Path.DirectorySeparatorChar);
        string resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(lhPath), normalized));
        if (!File.Exists(resolved)) throw new FileNotFoundException($".lh 引用的资源不存在：{reference}", resolved);
        return resolved;
    }

    private static ParticleSystem.MinMaxCurve ToMinMaxCurve(int type, float constant, float min, float max)
    {
        if (type == 2) return new ParticleSystem.MinMaxCurve(min, max);
        return new ParticleSystem.MinMaxCurve(constant);
    }

    private static ParticleSystem.MinMaxGradient ToGradient(LhGradient source)
    {
        var gradient = new Gradient();
        LhGradientColor[] colors = source.rgbs ?? Array.Empty<LhGradientColor>();
        LhGradientAlpha[] alphas = source.alphas ?? Array.Empty<LhGradientAlpha>();
        var colorKeys = new GradientColorKey[Mathf.Max(2, colors.Length)];
        var alphaKeys = new GradientAlphaKey[Mathf.Max(2, alphas.Length)];
        if (colors.Length == 0)
        {
            colorKeys[0] = new GradientColorKey(Color.white, 0f);
            colorKeys[1] = new GradientColorKey(Color.white, 1f);
        }
        else for (int i = 0; i < colors.Length; i++) colorKeys[i] = new GradientColorKey(ReadColor(colors[i].value, Color.white), colors[i].key);
        if (alphas.Length == 0)
        {
            alphaKeys[0] = new GradientAlphaKey(1f, 0f);
            alphaKeys[1] = new GradientAlphaKey(1f, 1f);
        }
        else for (int i = 0; i < alphas.Length; i++) alphaKeys[i] = new GradientAlphaKey(alphas[i].value, alphas[i].key);
        gradient.SetKeys(colorKeys, alphaKeys);
        return new ParticleSystem.MinMaxGradient(gradient);
    }

    private static ParticleSystemRenderMode ToParticleRenderMode(int mode)
    {
        switch (mode)
        {
            case 1: return ParticleSystemRenderMode.Stretch;
            case 2: return ParticleSystemRenderMode.HorizontalBillboard;
            case 3: return ParticleSystemRenderMode.VerticalBillboard;
            case 4: return ParticleSystemRenderMode.Mesh;
            default: return ParticleSystemRenderMode.Billboard;
        }
    }

    private static Color ReadColor(float[] value, Color fallback)
    {
        if (value == null || value.Length < 3) return fallback;
        return new Color(value[0], value[1], value[2], value.Length > 3 ? value[3] : 1f);
    }

    private static float Component(float[] value, int index, float fallback) => value != null && value.Length > index ? value[index] : fallback;
    private static string FirstNonEmpty(string a, string b) => string.IsNullOrEmpty(a) ? b : a;
    private static string NormalizeLhPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请先拖入或选择 .lh 文件。");
        string absolute = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(absolute), ".lh", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("输入文件必须是 .lh。");
        if (!File.Exists(absolute)) throw new FileNotFoundException("找不到 .lh 文件。", absolute);
        return absolute;
    }

    private static string HashPath(string path)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()))).Replace("-", "").Substring(0, 10);
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "LayaPrefab" : value;
    }

    private static void EnsureFolder(string assetFolder)
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

[CustomEditor(typeof(LhConvertor))]
public class LhConvertorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        SerializedProperty path = serializedObject.FindProperty(nameof(LhConvertor.LhPath));
        EditorGUILayout.LabelField("Laya .lh => Unity Prefab", EditorStyles.boldLabel);
        Rect area = GUILayoutUtility.GetRect(0f, 64f, GUILayout.ExpandWidth(true));
        GUI.Box(area, "将 .lh 文件拖放到此处", EditorStyles.helpBox);
        HandleDrop(area, path);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("当前文件", GUILayout.Width(60f));
        EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(path.stringValue) ? "未选择" : path.stringValue,
            EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        if (GUILayout.Button("浏览…", GUILayout.Width(64f)))
        {
            string directory = string.IsNullOrEmpty(path.stringValue) ? Application.dataPath : Path.GetDirectoryName(path.stringValue);
            string selected = EditorUtility.OpenFilePanel("选择 Laya 层级文件", directory, "lh");
            if (!string.IsNullOrEmpty(selected)) path.stringValue = Path.GetFullPath(selected);
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.HelpBox("生成 Prefab；转换层级、变换、Mesh、材质及常见粒子模块。Laya 自定义组件和部分曲线模块会输出提示。", MessageType.Info);
        serializedObject.ApplyModifiedProperties();
        if (GUILayout.Button("转换 Prefab")) ((LhConvertor)target).SaveAsset();
    }

    private static void HandleDrop(Rect area, SerializedProperty property)
    {
        Event evt = Event.current;
        if (!area.Contains(evt.mousePosition) || (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)) return;
        string candidate = null;
        foreach (string item in DragAndDrop.paths) if (IsLh(item)) { candidate = item; break; }
        if (candidate == null)
            foreach (UnityEngine.Object item in DragAndDrop.objectReferences)
            {
                string itemPath = AssetDatabase.GetAssetPath(item);
                if (IsLh(itemPath)) { candidate = itemPath; break; }
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

    private static bool IsLh(string path) => !string.IsNullOrEmpty(path) &&
        string.Equals(Path.GetExtension(path), ".lh", StringComparison.OrdinalIgnoreCase);
}
#endif
