// The main menu (scene 'Menu'), Austin 2026-10-10: "on the home screen befor joining the game where the char is standing
// in front of the rv add jimmy standing next to him and where it says schedual 1 make it say schedual I X Bully in the
// bully font for the bully part".
//   * Jimmy stands beside the menu's character (Rig/Avatar/BodyContainer, found by its humanoid animator), facing the
//     same way, on whichever side the menu camera actually sees, playing Bully's idle.
//   * The title: the game's 'MenuTitle' image (the Schedule I logo) gets a green "x" in the game's own font and Bully's logo
//     lettering after it (tools/export_logo.py: BULLY set from the gold serif letters in Bully's liNA.nft BullyB2).
// Built once per visit to the menu (the scene rebuilds them); F6 off doesn't touch the menu.
using System;
using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BullyLink
{
    public static class Menu
    {
        public static Action<string> Trace = _ => { };
        static float _buildAt = -1f;
        static JimmyModel _jimmy;
        static AnimLayer _anim;
        static bool _inMenu;

        public static void OnScene(string name)
        {
            _inMenu = name == "Menu";
            _jimmy = null; _anim = null;                      // the old scene's objects died with it
            _buildAt = _inMenu ? Time.unscaledTime + 1.5f : -1f;
        }

        public static void Update()
        {
            if (!_inMenu) return;
            if (_buildAt > 0 && Time.unscaledTime >= _buildAt)
            {
                _buildAt = -1f;
                try { Title(); } catch (Exception e) { Trace($"menu: title failed: {e.Message}"); }
                try { Jimmy(); } catch (Exception e) { Trace($"menu: Jimmy failed: {e.Message}"); }
            }
            if (_anim != null && _jimmy?.Root != null)
            {
                if (_anim.Playing == null) _anim.Play("npc_idle", fullBody: true, withRoot: true, rootPos: true);
                _anim.Apply(Time.deltaTime);
            }
        }

        static Camera MenuCamera()
        {
            if (Camera.main != null) return Camera.main;
            Camera best = null;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>())
                if (c != null && c.enabled && c.gameObject.activeInHierarchy && (best == null || c.depth > best.depth)) best = c;
            return best;
        }

        static void Jimmy()
        {
            Animator guy = null;
            foreach (var a in UnityEngine.Object.FindObjectsOfType<Animator>())
                if (a != null && a.gameObject.activeInHierarchy && a.avatar != null && a.avatar.isHuman) { guy = a; break; }
            if (guy == null) { Trace("menu: no character on the menu to stand Jimmy next to"); return; }
            Material anyMat = null;
            foreach (var r in guy.GetComponentsInChildren<SkinnedMeshRenderer>(true)) { anyMat = r.sharedMaterial; break; }
            _jimmy = JimmyModel.Build(anyMat, _ => { }, goName: "BullyLink_MenuJimmy", keep: false);
            var f = guy.transform.forward; f.y = 0; f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
            var right = Vector3.Cross(Vector3.up, f).normalized;
            var cam = MenuCamera();
            // the side the camera sees better (closer to the middle of the frame); his right by default
            Vector3 pick = guy.transform.position + right * 0.8f;
            if (cam != null)
            {
                Vector3 a = guy.transform.position + right * 0.8f, b = guy.transform.position - right * 0.8f;
                var va = cam.WorldToViewportPoint(a + Vector3.up); var vb = cam.WorldToViewportPoint(b + Vector3.up);
                bool okA = va.z > 0 && va.x > 0.05f && va.x < 0.95f, okB = vb.z > 0 && vb.x > 0.05f && vb.x < 0.95f;
                pick = okA && (!okB || Mathf.Abs(va.x - 0.5f) <= Mathf.Abs(vb.x - 0.5f)) ? a : okB ? b : a;
                Trace($"menu: camera '{cam.name}' at {cam.transform.position}; Jimmy's spots on screen x {va.x:F2} / {vb.x:F2}");
            }
            _jimmy.Root.transform.SetPositionAndRotation(pick, Quaternion.LookRotation(f, Vector3.up));
            _jimmy.Root.layer = 0;
            _jimmy.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            _anim = new AnimLayer(_jimmy.ByName);
            // his last played save's outfit on the menu too (v0.22.6: outfits belong to save slots)
            try
            {
                var last = Il2CppScheduleOne.Persistence.LoadManager.LastPlayedGame;
                var key = last != null ? Outfit.KeyOf(last.SavePath) : "";
                if (key.Length > 0) Outfit.Apply(_jimmy, Outfit.ForSave(key));
            }
            catch (Exception e) { Trace($"menu: outfit: {e.Message}"); }
            _anim.Play("npc_idle", fullBody: true, withRoot: true, rootPos: true);
            Trace($"menu: Jimmy stands at {pick} beside '{guy.transform.parent?.parent?.name}/{guy.name}' at {guy.transform.position}");
        }

        static void Title()
        {
            Image title = null;
            foreach (var im in UnityEngine.Object.FindObjectsOfType<Image>())
                if (im != null && im.gameObject.activeInHierarchy && im.sprite != null && im.sprite.name == "MenuTitle") { title = im; break; }
            if (title == null) { Trace("menu: no MenuTitle image found"); return; }
            var t = title.rectTransform;
            float h = t.rect.height, w = t.rect.width;
            TMP_FontAsset font = null;
            foreach (var tx in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>()) if (tx.font != null) { font = tx.font; break; }

            // "X" just right of the Schedule I logo, in the game's own font
            var xgo = new GameObject("BullyLink_TitleX");
            var xrt = xgo.AddComponent<RectTransform>();
            xrt.SetParent(t, false);
            xrt.anchorMin = xrt.anchorMax = new Vector2(1f, 0.5f);
            xrt.pivot = new Vector2(0f, 0.5f);
            xrt.sizeDelta = new Vector2(h * 0.42f, h * 0.5f);
            xrt.anchoredPosition = new Vector2(h * 0.06f, 0f);
            var x = xgo.AddComponent<TextMeshProUGUI>();
            if (font != null) x.font = font;
            // v0.19.1 (Austin: "make the x lowercase and make it green"): Schedule I's own green, taken from the version
            // number under the title (Title/Text), so it matches the game's palette
            var green = new Color(0.45f, 0.85f, 0.25f);
            foreach (var tx in t.GetComponentsInChildren<TextMeshProUGUI>(true)) if (tx != null && tx.gameObject.name == "Text") { green = tx.color; break; }
            x.text = "x"; x.fontSize = h * 0.42f; x.fontStyle = FontStyles.Bold; x.color = green;
            x.alignment = TextAlignmentOptions.Center; x.enableWordWrapping = false; x.raycastTarget = false;

            // then BULLY in Bully's own logo lettering
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "bully_logo" };
            ImageConversion.LoadImage(tex, JimmyModel.Res("bully_logo.png"));
            tex.wrapMode = TextureWrapMode.Clamp;
            float lh = h * 0.42f, lw = lh * tex.width / Mathf.Max(1f, tex.height);
            var lgo = new GameObject("BullyLink_TitleBully");
            var lrt = lgo.AddComponent<RectTransform>();
            lrt.SetParent(t, false);
            lrt.anchorMin = lrt.anchorMax = new Vector2(1f, 0.5f);
            lrt.pivot = new Vector2(0f, 0.5f);
            lrt.sizeDelta = new Vector2(lw, lh);
            lrt.anchoredPosition = new Vector2(h * 0.06f + h * 0.42f + h * 0.04f, 0f);
            var img = lgo.AddComponent<Image>();
            img.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            img.preserveAspect = true; img.raycastTarget = false;
            Trace($"menu: title now 'Schedule I x BULLY' (logo {w:F0}x{h:F0}, green x {x.color} + Bully lettering {lw:F0}x{lh:F0} to its right)");
        }
    }
}
