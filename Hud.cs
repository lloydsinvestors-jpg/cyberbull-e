// Bully's HUD inside Schedule I, built from Bully's own HUD art (TXD\HUDElems.nft, decoded by tools/export_props.py)
// on Unity's regular UI (Canvas + RawImage + TextMeshPro): the same system Schedule I's HUD uses, so it's all compiled
// into the game. (v0.3.0 drew with IMGUI's GUI.DrawTexture, which this build strips: "Method unstripping failed".)
//   top right: the current item in Bully's round icon backing, ammo (or the infinity sign), and while cycling, the whole
//   item bar with the selection highlighted; under it health and trouble; centre: crosshair; on the locked-on target:
//   Bully's target ring; bottom: action prompts with Bully's social icons.
using System.Collections.Generic;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BullyLink
{
    public class Hud
    {
        GameObject _root;
        RawImage _itemIcon, _itemBg, _infinity, _xhair, _lockRing, _health, _trouble, _promptIcon;
        Image _hpRing, _hpRingBack;
        TextMeshProUGUI _itemName, _ammo, _prompt;
        GameObject _strip;
        readonly List<(RawImage bg, RawImage icon, Item item)> _stripSlots = new();
        GameObject _promptBox;
        float _stripUntil;
        Item _lastSel = Item.Fists;
        TMP_FontAsset _font;
        Canvas _canvas;
        public System.Action<string> Trace;

        static readonly Dictionary<Item, string> IconOf = new()
        {
            [Item.Fists] = "HUDIcon_fist", [Item.Slingshot] = "HUDicon_sling", [Item.Firecracker] = "HUDicon_cbomb",
            [Item.StinkBomb] = "HUDIcon_STINKBOMB", [Item.ItchingPowder] = "HUDIcon_itchpowd", [Item.Marbles] = "HUDIcon_marble",
            [Item.Eggs] = "HUDicon_egg", [Item.SpudGun] = "HUDIcon_SPD", [Item.BottleRocket] = "HUDicon_brocketL",
            [Item.Banana] = "banana", [Item.Soda] = "Bar_iconhealth",
        };

        public bool Built => _root != null;

        public void Build()
        {
            _root = new GameObject("BullyLink_HUD");
            Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            // v0.3.1 assumed a new Canvas defaults to screen overlay; in this build it came up as a world-space canvas
            // (a giant fist icon in the sky). Set it, check it took, and if not ride on Schedule I's own screen canvas.
            try { canvas.renderMode = RenderMode.ScreenSpaceOverlay; } catch (System.Exception e) { Trace?.Invoke($"hud: renderMode setter: {e.Message}"); }
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                Canvas host = null;
                foreach (var c in Object.FindObjectsOfType<Canvas>())
                    if (c != canvas && c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceOverlay && c.gameObject.activeInHierarchy) { host = c; break; }
                if (host != null)
                {
                    _root.transform.SetParent(host.transform, false);
                    var rt = _root.GetComponent<RectTransform>();
                    rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
                    canvas.overrideSorting = true;
                    Trace?.Invoke($"hud: own canvas came up {canvas.renderMode}; riding on Schedule I's '{host.name}' screen canvas");
                }
                else Trace?.Invoke($"hud: canvas is {canvas.renderMode} and no screen canvas to ride on");
            }
            else Trace?.Invoke("hud: screen-overlay canvas OK");
            _canvas = canvas;
            canvas.sortingOrder = 500;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            foreach (var t in Object.FindObjectsOfType<TextMeshProUGUI>())
                if (t.font != null) { _font = t.font; break; }

            // current item, top right
            _itemBg = Img("ItemBg", "HUDIconBG", new Vector2(1, 1), new Vector2(-150, -110f), new Vector2(120, 120));
            _itemIcon = Img("ItemIcon", "HUDIcon_fist", new Vector2(1, 1), new Vector2(-150, -110f), new Vector2(96, 96));
            _infinity = Img("Infinity", "Infinity", new Vector2(1, 1), new Vector2(-62, -182f), new Vector2(40, 40));
            _ammo = Txt("Ammo", new Vector2(1, 1), new Vector2(-40, -170f), new Vector2(90, 44), 34, TextAlignmentOptions.Left);
            _itemName = Txt("ItemName", new Vector2(1, 1), new Vector2(-150, -240f), new Vector2(300, 30), 22, TextAlignmentOptions.Center);
            // health + trouble under it
            Img("HealthOutline", "Bar_outline", new Vector2(1, 1), new Vector2(-150, -270f), new Vector2(240, 14));
            _health = Img("Health", "Bar_health_full", new Vector2(1, 1), new Vector2(-150, -270f), new Vector2(236, 10));
            Img("TroubleOutline", "Bar_outline", new Vector2(1, 1), new Vector2(-150, -292f), new Vector2(240, 14));
            _trouble = Img("Trouble", "Bar_other_full", new Vector2(1, 1), new Vector2(-150, -292f), new Vector2(236, 10));
            foreach (var bar in new[] { _health, _trouble })
            {
                var rt = bar.rectTransform;
                rt.pivot = new Vector2(0, 0.5f);
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x - rt.sizeDelta.x / 2f, rt.anchoredPosition.y);
            }
            Img("HealthIcon", "Bar_iconhealth", new Vector2(1, 1), new Vector2(-292, -270f), new Vector2(24, 24));
            Img("TroubleIcon", "Icon_Punish", new Vector2(1, 1), new Vector2(-292, -292f), new Vector2(24, 24));

            // the item bar, shown while cycling (Bully flips through the items up top)
            _strip = new GameObject("ItemStrip");
            var srt = _strip.AddComponent<RectTransform>();
            srt.SetParent(_root.transform, false);
            Anchor(srt, new Vector2(1, 1), new Vector2(-150, -350f), new Vector2(10, 10));
            var all = (Item[])System.Enum.GetValues(typeof(Item));
            for (int i = 0; i < all.Length; i++)
            {
                var bg = Img("SlotBg" + i, "HUDIconBG", new Vector2(1, 1), Vector2.zero, new Vector2(62, 62), _strip.transform);
                var ic = Img("Slot" + i, IconOf[all[i]], new Vector2(1, 1), Vector2.zero, new Vector2(50, 50), _strip.transform);
                _stripSlots.Add((bg, ic, all[i]));
            }
            _strip.SetActive(false);

            _xhair = Img("Crosshair", "HUD_xhair", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(36, 36));
            // Bully's target health: a ring around whoever you're locked on (Bully's HUD_circleoutline), swept down with
            // Schedule I's own NPC health, yellow -> orange -> red; past red they're knocked out (Kit.Hit).
            _hpRingBack = Ring("HealthRingBack", new Color(0, 0, 0, 0.45f));
            _hpRing = Ring("HealthRing", new Color(1f, 0.85f, 0.2f));
            _lockRing = Img("Lock", "target2A", new Vector2(0, 0), Vector2.zero, new Vector2(70, 70));
            _lockRing.color = new Color(1f, 0.85f, 0.2f);

            _promptBox = new GameObject("Prompt");
            var prt = _promptBox.AddComponent<RectTransform>();
            prt.SetParent(_root.transform, false);
            Anchor(prt, new Vector2(0.5f, 0), new Vector2(0, 150), new Vector2(900, 50));
            var shade = _promptBox.AddComponent<RawImage>();
            shade.texture = Texture2D.whiteTexture;
            shade.color = new Color(0, 0, 0, 0.55f);
            shade.raycastTarget = false;
            _promptIcon = Img("PromptIcon", "Social_Wedgie01", new Vector2(0, 0.5f), new Vector2(28, 0), new Vector2(40, 40), _promptBox.transform);
            _prompt = Txt("PromptText", new Vector2(0.5f, 0.5f), new Vector2(20, 0), new Vector2(820, 44), 24, TextAlignmentOptions.Center, _promptBox.transform);
            _promptBox.SetActive(false);
            _root.SetActive(false);
        }

        public void Destroy() { if (_root != null) Object.Destroy(_root); _root = null; _stripSlots.Clear(); }

        public void Show(bool on) { if (_root != null && _root.activeSelf != on) _root.SetActive(on); }

        public void Update(Kit kit, Player me, Camera view, float health01, System.Func<Vector3, Vector3> project)
        {
            if (_root == null) return;
            var sel = kit.Selected;
            if (sel != _lastSel) { _stripUntil = Time.unscaledTime + 1.6f; _lastSel = sel; }
            _itemIcon.texture = Tex(IconOf[sel]);
            _itemName.text = kit.ItemName;
            bool endless = kit.Ammo(sel) < 0;
            _infinity.gameObject.SetActive(endless && sel != Item.Fists);
            _ammo.text = endless ? "" : kit.Ammo(sel).ToString();

            // the item bar: centred on the selection, empties dimmed
            bool strip = Time.unscaledTime < _stripUntil;
            _strip.SetActive(strip);
            if (strip)
            {
                int si = _stripSlots.FindIndex(s => s.item == sel);
                for (int i = 0; i < _stripSlots.Count; i++)
                {
                    var (bg, ic, it) = _stripSlots[i];
                    float x = (i - si) * 68f;
                    bg.rectTransform.anchoredPosition = new Vector2(x, 0);
                    ic.rectTransform.anchoredPosition = new Vector2(x, 0);
                    bool cur = i == si, empty = kit.Ammo(it) == 0;
                    bg.color = cur ? new Color(1f, 0.85f, 0.3f) : new Color(1, 1, 1, 0.6f);
                    ic.color = empty ? new Color(1, 1, 1, 0.25f) : Color.white;
                    float s = cur ? 1.25f : 1f;
                    bg.rectTransform.localScale = ic.rectTransform.localScale = new Vector3(s, s, 1);
                }
            }

            Fill(_health, health01);
            float tr = kit.Trouble / 100f;
            Fill(_trouble, tr);
            _trouble.color = tr < 0.4f ? new Color(1f, 0.9f, 0.2f) : tr < 0.75f ? new Color(1f, 0.55f, 0.1f) : new Color(0.95f, 0.15f, 0.1f);

            // lock-on ring on the target
            var target = kit.LockTarget;
            bool locked = target != null && view != null;
            if (locked)
            {
                var sp = project(target.transform.position + Vector3.up * 1.1f);
                locked = sp.z > 0;
                if (locked)
                {
                    var scale = Mathf.Max(0.01f, _canvas.scaleFactor);
                    _lockRing.rectTransform.anchoredPosition = new Vector2(sp.x / scale, sp.y / scale);
                    _lockRing.rectTransform.localEulerAngles = new Vector3(0, 0, Time.unscaledTime * 90f);
                }
            }
            _lockRing.gameObject.SetActive(locked);
            _hpRing.gameObject.SetActive(locked);
            _hpRingBack.gameObject.SetActive(locked);
            if (locked)
            {
                float hp = kit.TargetHealth01;
                _hpRing.fillAmount = hp;
                _hpRing.color = hp > 0.5f ? Color.Lerp(new Color(1f, 0.6f, 0.1f), new Color(1f, 0.85f, 0.2f), (hp - 0.5f) * 2f)
                                          : Color.Lerp(new Color(0.9f, 0.1f, 0.05f), new Color(1f, 0.6f, 0.1f), hp * 2f);
                _hpRing.rectTransform.anchoredPosition = _hpRingBack.rectTransform.anchoredPosition = _lockRing.rectTransform.anchoredPosition;
                _lockRing.color = _hpRing.color;
            }
            _xhair.gameObject.SetActive(!locked && sel != Item.Fists && sel != Item.Soda);

            // prompts
            string text = null, icon = null;
            if (kit.Grabbed != null) { text = "R / Y:  WEDGIE        Alt / X:  shove"; icon = "Social_Wedgie01"; }
            else if (locked) { text = kit.WouldApologize ? "G / D-pad down:  apologize        T / D-pad up:  taunt" : "G / D-pad down:  greet        T / D-pad up:  taunt"; icon = kit.WouldApologize ? "Social_Apologize01" : "Social_Wave01"; }
            _promptBox.SetActive(text != null);
            if (text != null) { _prompt.text = text; _promptIcon.texture = Tex(icon); }
        }

        static void Fill(RawImage bar, float v)
        {
            v = Mathf.Clamp01(v);
            var rt = bar.rectTransform;
            rt.localScale = new Vector3(Mathf.Max(0.001f, v), 1, 1);
            bar.uvRect = new Rect(0, 0, Mathf.Max(0.001f, v), 1);
        }

        // ---- building blocks ----
        static readonly Dictionary<string, Texture2D> Cache = new();
        public static Texture2D Tex(string icon)
        {
            if (Cache.TryGetValue(icon, out var t) && t != null) return t;
            t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = icon };
            ImageConversion.LoadImage(t, JimmyModel.Res($"hud_{icon}.png"));
            t.wrapMode = TextureWrapMode.Clamp;
            return Cache[icon] = t;
        }

        static void Anchor(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        RawImage Img(string name, string icon, Vector2 anchor, Vector2 pos, Vector2 size, Transform parent = null)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent ?? _root.transform, false);
            Anchor(rt, anchor, pos, size);
            var img = go.AddComponent<RawImage>();
            img.texture = Tex(icon);
            img.raycastTarget = false;
            return img;
        }

        Image Ring(string name, Color c)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(_root.transform, false);
            Anchor(rt, new Vector2(0, 0), Vector2.zero, new Vector2(92, 92));
            var img = go.AddComponent<Image>();
            var tex = Tex("HUD_circleoutline");
            img.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            img.type = Image.Type.Filled;
            img.fillMethod = Image.FillMethod.Radial360;
            img.fillOrigin = (int)Image.Origin360.Top;
            img.fillClockwise = false;
            img.fillAmount = 1f;
            img.color = c;
            img.raycastTarget = false;
            return img;
        }

        TextMeshProUGUI Txt(string name, Vector2 anchor, Vector2 pos, Vector2 size, float fontSize, TextAlignmentOptions align, Transform parent = null)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent ?? _root.transform, false);
            Anchor(rt, anchor, pos, size);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            t.fontSize = fontSize;
            t.alignment = align;
            t.color = Color.white;
            t.fontStyle = FontStyles.Bold;
            t.raycastTarget = false;
            t.enableWordWrapping = false;
            return t;
        }
    }
}
