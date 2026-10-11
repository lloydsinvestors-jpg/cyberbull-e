// One-shot rendering diagnostics (v0.4.1): what Schedule I's URP is actually set to (so the editor bundle can compile
// matching shader variants; v0.4.0's Jimmy cast a shadow but drew nothing), and which visible objects draw with a
// broken / unsupported shader (the magenta tree rectangles), by name, so the cause is evidence, not a guess.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BullyLink
{
    public static class Diag
    {
        public static void Dump(Action<string> trace)
        {
            var sb = new StringBuilder();
            try
            {
                var rp = GraphicsSettings.currentRenderPipeline;
                sb.Append($"diag: pipeline '{rp?.name}'");
                var urp = rp?.TryCast<UniversalRenderPipelineAsset>();
                if (urp != null)
                {
                    sb.Append($" | mainLight {urp.mainLightRenderingMode} shadows {urp.supportsMainLightShadows} res {urp.mainLightShadowmapResolution} cascades {urp.shadowCascadeCount} dist {urp.shadowDistance}");
                    sb.Append($" | addLights {urp.additionalLightsRenderingMode} addShadows {urp.supportsAdditionalLightShadows} soft {urp.supportsSoftShadows}/{urp.softShadowQuality}");
                    sb.Append($" | HDR {urp.supportsHDR} msaa {urp.msaaSampleCount} depthTex {urp.supportsCameraDepthTexture} mixed {urp.supportsMixedLighting} cookies {urp.supportsLightCookies} layers {urp.useRenderingLayers} probeBlend {urp.reflectionProbeBlending} boxProj {urp.reflectionProbeBoxProjection} grading {urp.colorGradingMode}");
                    try { sb.Append($" | renderer {urp.scriptableRenderer?.GetIl2CppType()?.Name}"); } catch { }
                }
            }
            catch (Exception e) { sb.Append($" (pipeline read failed: {e.Message})"); }
            trace(sb.ToString());

            // a game material that draws fine: its keywords are what the bundle's variants must cover
            try
            {
                var seen = new HashSet<string>();
                int n = 0;
                foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
                {
                    if (r == null || !r.isVisible) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null || m.shader == null || m.shader.name != "Universal Render Pipeline/Lit") continue;
                        var kw = string.Join(",", m.shaderKeywords);
                        if (seen.Add(kw) && n++ < 8) trace($"diag: game Lit material '{m.name}' keywords [{kw}]");
                    }
                }
            }
            catch (Exception e) { trace($"diag: material scan failed: {e.Message}"); }

            // anything visible drawing with a missing / unsupported / error shader
            try
            {
                int bad = 0;
                var shaders = new Dictionary<string, int>();
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.isVisible) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        string why = m == null ? "no material" : m.shader == null ? "no shader" : !m.shader.isSupported ? "shader not supported" : m.shader.name.Contains("Error") ? "error shader" : null;
                        string sn = m?.shader?.name ?? "-";
                        if (r.gameObject.name.IndexOf("tree", StringComparison.OrdinalIgnoreCase) >= 0 || r.gameObject.name.IndexOf("pine", StringComparison.OrdinalIgnoreCase) >= 0 || r.gameObject.name.IndexOf("foliage", StringComparison.OrdinalIgnoreCase) >= 0)
                            shaders[$"{r.GetIl2CppType().Name}:{sn}"] = shaders.TryGetValue($"{r.GetIl2CppType().Name}:{sn}", out var c) ? c + 1 : 1;
                        if (why != null && bad++ < 25)
                            trace($"diag: BROKEN '{Path(r.transform)}' ({r.GetIl2CppType().Name}) material '{m?.name}' shader '{sn}': {why}");
                    }
                }
                trace($"diag: {bad} visible broken material slots");
                foreach (var kv in shaders) trace($"diag: tree-ish renderers {kv.Key} x{kv.Value}");
            }
            catch (Exception e) { trace($"diag: renderer scan failed: {e.Message}"); }

            try
            {
                foreach (var t in new[] { "_MAIN_LIGHT_SHADOWS", "_MAIN_LIGHT_SHADOWS_CASCADE", "_MAIN_LIGHT_SHADOWS_SCREEN", "_ADDITIONAL_LIGHTS", "_ADDITIONAL_LIGHTS_VERTEX", "_ADDITIONAL_LIGHT_SHADOWS", "_SHADOWS_SOFT", "_FORWARD_PLUS", "_REFLECTION_PROBE_BLENDING", "_REFLECTION_PROBE_BOX_PROJECTION", "_SCREEN_SPACE_OCCLUSION", "_LIGHT_LAYERS", "_LIGHT_COOKIES", "_CLUSTERED_RENDERING", "LIGHTMAP_SHADOW_MIXING", "SHADOWS_SHADOWMASK", "_DBUFFER_MRT1", "_DBUFFER_MRT2", "_DBUFFER_MRT3", "_RENDER_PASS_ENABLED", "FOG_LINEAR", "FOG_EXP", "FOG_EXP2" })
                    if (Shader.IsKeywordEnabled(t)) trace($"diag: global keyword ON {t}");
            }
            catch (Exception e) { trace($"diag: keyword scan failed: {e.Message}"); }
        }

        static string Path(Transform t)
        {
            var s = t.name;
            for (int i = 0; i < 3 && t.parent != null; i++) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }
    }
}
