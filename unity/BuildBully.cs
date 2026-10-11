// Builds bullylink.bundle for the BullyLink Schedule I mod (Tweak, 2026-10-08).
// Same Unity (2022.3.62f2) and URP (14.0.11) as Schedule I, so the shader variants compiled into the bundle are ones the
// game can draw: Jimmy's and the props' materials (no more magenta) and real particle effects made from Bully's own
// effect textures (no more bubbles). Textures come from Austin's Bully install via the mod's tools (copied into
// Assets/Bully/Tex before building).
// Run: Unity.exe -batchmode -nographics -quit -projectPath D:/Unity/BullyLinkAssets -executeMethod BuildBully.Build -logFile build.log
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class BuildBully
{
    const string Root = "Assets/Bully";
    const string Tex = Root + "/Tex";
    const string Out = "D:/Unity/BullyLinkPublic/Bundle";   // PUBLIC copy: placeholder fx textures, no Bully pixels
    const string BundleName = "bullylink";

    public static void Build()
    {
        Directory.CreateDirectory(Root + "/Materials");
        Directory.CreateDirectory(Root + "/FX");
        EnsureUrp();
        AssetDatabase.Refresh();
        foreach (var p in Directory.GetFiles(Tex, "*.png"))
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(p.Replace('\\', '/'));
            if (ti == null) continue;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = true;
            ti.wrapMode = Path.GetFileName(p).StartsWith("fx_") ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            ti.SaveAndReimport();
        }

        // ---- lit materials: one per Jimmy / prop texture ("mat_<texture>") ----
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        foreach (var p in Directory.GetFiles(Tex, "*.png").Select(x => x.Replace('\\', '/')))
        {
            var n = Path.GetFileNameWithoutExtension(p);
            if (!(n.StartsWith("Player_") || n.StartsWith("prop_"))) continue;
            var m = new Material(lit) { name = "mat_" + n };
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(p));
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0.15f);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Cull", 0f);
            Save(m, $"{Root}/Materials/mat_{n}.mat");
        }
        var flat = new Material(lit) { name = "mat_flat" };
        flat.SetColor("_BaseColor", Color.white);
        flat.SetFloat("_Smoothness", 0.2f);
        Save(flat, $"{Root}/Materials/mat_flat.mat");

        // ---- particle effects ----
        var smoke = ParticleMat("smoke5", additive: false);
        var smoke2 = ParticleMat("smoke2", additive: false);
        var fire = ParticleMat("Fireball3", additive: true);
        var spark = ParticleMat("Sparks01", additive: true);
        var dust = ParticleMat("geo_dust1", additive: false);
        var egg = ParticleMat("EggSplat01", additive: false);
        var ash = ParticleMat("ash", additive: false);
        var hit = ParticleMat("HitImpact01", additive: true);

        Fx("FX_StinkCloud", smoke, loop: true, life: 3.5f, rate: 7, burst: 10, size: (1.4f, 2.4f), speed: (0.1f, 0.5f),
           color: (new Color(0.45f, 0.75f, 0.2f, 0.55f), new Color(0.3f, 0.55f, 0.15f, 0f)), radius: 1.6f, gravity: -0.02f, duration: 7f);
        Fx("FX_Itch", ash, loop: false, life: 2.2f, rate: 0, burst: 60, size: (0.05f, 0.14f), speed: (0.5f, 2.2f),
           color: (new Color(1f, 0.97f, 0.88f, 0.95f), new Color(1f, 0.97f, 0.88f, 0f)), radius: 0.4f, gravity: 0.15f);
        Fx("FX_ItchCloud", smoke2, loop: false, life: 2.0f, rate: 0, burst: 12, size: (0.8f, 1.6f), speed: (0.2f, 0.8f),
           color: (new Color(0.95f, 0.92f, 0.8f, 0.45f), new Color(0.95f, 0.92f, 0.8f, 0f)), radius: 0.6f, gravity: -0.01f);
        var bang = Fx("FX_Bang", fire, loop: false, life: 0.35f, rate: 0, burst: 6, size: (0.6f, 1.3f), speed: (0.2f, 1.0f),
           color: (new Color(1f, 0.85f, 0.4f, 1f), new Color(1f, 0.3f, 0.05f, 0f)), radius: 0.15f, gravity: 0f, save: false);
        AddChild(bang, Fx("Sparks", spark, loop: false, life: 0.6f, rate: 0, burst: 30, size: (0.04f, 0.1f), speed: (3f, 8f),
           color: (new Color(1f, 0.9f, 0.5f, 1f), new Color(1f, 0.5f, 0.1f, 0f)), radius: 0.1f, gravity: 1.2f, save: false));
        AddChild(bang, Fx("Smoke", smoke, loop: false, life: 2.5f, rate: 0, burst: 8, size: (0.8f, 1.6f), speed: (0.3f, 1.0f),
           color: (new Color(0.35f, 0.35f, 0.35f, 0.6f), new Color(0.5f, 0.5f, 0.5f, 0f)), radius: 0.3f, gravity: -0.05f, save: false));
        SavePrefab(bang, "FX_Bang");
        Fx("FX_RocketTrail", smoke2, loop: true, life: 0.9f, rate: 60, burst: 0, size: (0.15f, 0.4f), speed: (0f, 0.2f),
           color: (new Color(0.85f, 0.85f, 0.85f, 0.6f), new Color(0.6f, 0.6f, 0.6f, 0f)), radius: 0.03f, gravity: -0.05f, worldSpace: true);
        Fx("FX_EggSplat", egg, loop: false, life: 0.8f, rate: 0, burst: 10, size: (0.12f, 0.3f), speed: (0.5f, 2.0f),
           color: (new Color(1f, 0.95f, 0.6f, 1f), new Color(1f, 0.9f, 0.4f, 0f)), radius: 0.05f, gravity: 1.5f);
        Fx("FX_Dust", dust, loop: false, life: 0.9f, rate: 0, burst: 8, size: (0.25f, 0.6f), speed: (0.4f, 1.4f),
           color: (new Color(0.75f, 0.68f, 0.55f, 0.7f), new Color(0.75f, 0.68f, 0.55f, 0f)), radius: 0.1f, gravity: 0.1f);
        Fx("FX_Hit", hit, loop: false, life: 0.25f, rate: 0, burst: 3, size: (0.3f, 0.6f), speed: (0f, 0.3f),
           color: (new Color(1f, 1f, 1f, 1f), new Color(1f, 1f, 1f, 0f)), radius: 0.02f, gravity: 0f);

        // ---- bundle everything under Assets/Bully ----
        foreach (var guid in AssetDatabase.FindAssets("", new[] { Root }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetDatabase.IsValidFolder(path)) continue;
            AssetImporter.GetAtPath(path).assetBundleName = BundleName;
        }
        AssetDatabase.SaveAssets();
        Directory.CreateDirectory(Out);
        var man = BuildPipeline.BuildAssetBundles(Out, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        Debug.Log("BULLYLINK BUILD " + (man != null ? "OK: " + string.Join(", ", man.GetAllAssetBundles()) : "FAILED"));
    }

    static void EnsureUrp()
    {
        if (GraphicsSettings.renderPipelineAsset is UniversalRenderPipelineAsset existing) { MatchGame(existing); return; }
        Directory.CreateDirectory(Root + "/../Settings");
        var data = ScriptableObject.CreateInstance<UniversalRendererData>();
        AssetDatabase.CreateAsset(data, "Assets/Settings/URP_Renderer.asset");
        var urp = UniversalRenderPipelineAsset.Create(data);
        AssetDatabase.CreateAsset(urp, "Assets/Settings/URP.asset");
        GraphicsSettings.renderPipelineAsset = urp;
        QualitySettings.renderPipeline = urp;
        AssetDatabase.SaveAssets();
        Debug.Log("BULLYLINK: URP asset created and assigned");
        MatchGame(urp);
    }

    /// <summary>Schedule I's URP asset, read live by the mod's diagnostics (2026-10-08): "URP_3Ultra", main light per-pixel
    /// with shadows (4096, 3 cascades, 180 m), additional lights per-pixel with shadows, soft shadows High, HDR, mixed
    /// lighting, light cookies, no rendering layers, no probe blending / box projection, forward UniversalRenderer.
    /// Global keywords on in game: _MAIN_LIGHT_SHADOWS_CASCADE, _ADDITIONAL_LIGHTS, _ADDITIONAL_LIGHT_SHADOWS, _SHADOWS_SOFT.
    /// URP strips variants by the asset's features, so the bundle only gets the game's variants if these match.</summary>
    static void MatchGame(UniversalRenderPipelineAsset urp)
    {
        var so = new SerializedObject(urp);
        void Set(string field, int v)
        {
            var p = so.FindProperty(field);
            if (p == null) { Debug.Log("BULLYLINK: URP field missing " + field); return; }
            if (p.propertyType == SerializedPropertyType.Boolean) p.boolValue = v != 0;
            else if (p.propertyType == SerializedPropertyType.Enum) p.enumValueIndex = v;
            else p.intValue = v;
        }
        Set("m_MainLightRenderingMode", 1);            // PerPixel
        Set("m_MainLightShadowsSupported", 1);
        Set("m_MainLightShadowmapResolution", 4096);
        Set("m_ShadowCascadeCount", 3);
        Set("m_AdditionalLightsRenderingMode", 1);     // PerPixel
        Set("m_AdditionalLightShadowsSupported", 1);
        Set("m_SoftShadowsSupported", 1);
        Set("m_MixedLightingSupported", 1);
        Set("m_SupportsLightCookies", 1);
        Set("m_SupportsHDR", 1);
        Set("m_SupportsLightLayers", 0);
        Set("m_ReflectionProbeBlending", 0);
        Set("m_ReflectionProbeBoxProjection", 0);
        var dist = so.FindProperty("m_ShadowDistance");
        if (dist != null) dist.floatValue = 180f;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(urp);
        // URP's own stripping, driven by the (now matching) asset features. With it off the build had 55,296 variants.
        var gs = AssetDatabase.FindAssets("t:UniversalRenderPipelineGlobalSettings");
        foreach (var g in gs)
        {
            var asset = AssetDatabase.LoadAssetAtPath<Object>(AssetDatabase.GUIDToAssetPath(g));
            var gso = new SerializedObject(asset);
            var strip = gso.FindProperty("m_StripUnusedVariants");
            if (strip != null) { strip.boolValue = true; gso.ApplyModifiedPropertiesWithoutUndo(); Debug.Log("BULLYLINK: unused-variant stripping ON (features match the game; off = 55k variants, ~14 h)"); }
        }
        AssetDatabase.SaveAssets();
        Debug.Log("BULLYLINK: URP matched to Schedule I's URP_3Ultra");
    }

    static Material ParticleMat(string tex, bool additive)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit")) { name = "fxmat_" + tex };
        m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Tex}/fx_{tex}.png"));
        m.SetColor("_BaseColor", Color.white);
        m.SetFloat("_Surface", 1f);                                  // transparent
        m.SetFloat("_Blend", additive ? 2f : 0f);                    // additive / alpha
        m.SetOverrideTag("RenderType", "Transparent");
        m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
        m.SetInt("_ZWrite", 0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)RenderQueue.Transparent;
        Save(m, $"{Root}/Materials/fxmat_{tex}.mat");
        return m;
    }

    static GameObject Fx(string name, Material mat, bool loop, float life, float rate, int burst, (float, float) size, (float, float) speed,
                         (Color, Color) color, float radius, float gravity, float duration = 1f, bool worldSpace = false, bool save = true)
    {
        var go = new GameObject(name);
        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.loop = loop;
        main.duration = loop ? duration : Mathf.Max(life, 0.1f);
        main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.7f, life);
        main.startSize = new ParticleSystem.MinMaxCurve(size.Item1, size.Item2);
        main.startSpeed = new ParticleSystem.MinMaxCurve(speed.Item1, speed.Item2);
        main.startRotation = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
        main.startColor = color.Item1;
        main.gravityModifier = gravity;
        main.simulationSpace = worldSpace ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
        main.stopAction = loop ? ParticleSystemStopAction.None : ParticleSystemStopAction.Destroy;
        main.playOnAwake = true;
        main.maxParticles = 300;
        var em = ps.emission;
        em.rateOverTime = rate;
        if (burst > 0) em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)burst) });
        var sh = ps.shape;
        sh.shapeType = ParticleSystemShapeType.Sphere;
        sh.radius = radius;
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(color.Item1, 0), new GradientColorKey(color.Item2, 1) },
                  new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(color.Item1.a, 0.12f), new GradientAlphaKey(color.Item2.a, 1) });
        col.color = g;
        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.7f, 1, 1.4f));
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.shadowCastingMode = ShadowCastingMode.Off;
        if (save) SavePrefab(go, name);
        return go;
    }

    static void AddChild(GameObject parent, GameObject child)
    {
        child.transform.SetParent(parent.transform, false);
        var m = child.GetComponent<ParticleSystem>().main;
        m.stopAction = ParticleSystemStopAction.None;
    }

    static void SavePrefab(GameObject go, string name)
    {
        PrefabUtility.SaveAsPrefabAsset(go, $"{Root}/FX/{name}.prefab");
        Object.DestroyImmediate(go);
    }

    static void Save(Object o, string path)
    {
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(o, path);
    }
}
