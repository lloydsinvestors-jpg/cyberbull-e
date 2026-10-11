// Bully's wardrobe in every property in the game, owned or for sale (v0.22, all of them since v0.22.2; Austin, 2026-10-10: "in every owned property put a wardrobe from
// bully and pull every possible outfit for jimmy and make it saveable").
//   * The furniture: Bully's 'armoir' (the louvred closet door from Jimmy's dorm, tools/export_props.py), flush on the wall
//     nearest the property's interior spawn point, facing into the room. Re-placed whenever a property is bought.
//   * R / pad Y beside it: the wardrobe. The camera swings round in front of Jimmy; Up / Down picks the slot (hair, hat,
//     top, bottoms, shoes, left / right wrist), Left / Right flips through every Bully item for it (live on Jimmy),
//     Backspace takes that slot off, Enter saves + closes, R / Y closes without saving (puts back what he had).
using System;
using System.Collections.Generic;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;

namespace BullyLink
{
    public class Wardrobe
    {
        public Action<string> Trace = _ => { };
        public Func<JimmyModel> Jimmy = () => null;
        /// <summary>The wardrobe screen is up (Kit / rides stand down; the camera looks at Jimmy's front).</summary>
        public static bool Open;
        public static Transform ViewTarget;           // Jimmy's root while the wardrobe's open (the camera's subject)
        public string Prompt;

        readonly Dictionary<string, GameObject> _placed = new();      // property code -> wardrobe
        readonly Dictionary<string, float> _failedAt = new();         // property code -> when no good wall was found
        float _scanAt;
        Dictionary<string, string> _working, _before;
        int _slot;
        PlayerMovement _mv;

        public void OnSceneChange() { _placed.Clear(); _failedAt.Clear(); Close(false, null); }

        public void Update(Player me, bool input)
        {
            Prompt = null;
            if (me == null || !Outfit.Available) return;
            if (Time.unscaledTime > _scanAt) { _scanAt = Time.unscaledTime + 5f; PlaceAll(); }
            if (Open) { Drive(me); return; }
            if (!input) return;
            foreach (var kv in _placed)
            {
                if (kv.Value == null) continue;
                if (Vector3.Distance(kv.Value.transform.position, me.transform.position) > 1.8f) continue;
                Prompt = "R / Y: wardrobe";
                if (BullyInput.KeyDown(BullyInput.VK_R) || BullyInput.PadDown(BullyInput.Y)) OpenUp(me);
                return;
            }
        }

        // ============================================================ placement
        void PlaceAll()
        {
            try
            {
                // v0.22.2 (Austin: "there needs to be a wardrobe in every buyable location"): every property in the game,
                // owned or not (v0.22.0 did owned only)
                var all = Il2CppScheduleOne.Property.Property.Properties;
                if (all == null) return;
                foreach (var p in all)
                {
                    if (p == null) continue;
                    string code = p.PropertyCode ?? p.name;
                    if (_placed.TryGetValue(code, out var g) && g != null) continue;
                    if (_failedAt.TryGetValue(code, out var fa) && Time.unscaledTime - fa < 60f) continue;   // no good wall: try again in a minute
                    // v0.22.3 (Austin: "the bungalo didnt"): an INSIDE reference point. SpawnPoint is the doorstep outside; with no
                    // InteriorSpawnPoint the wardrobe went into the outside wall. Then: where its workers idle (always inside).
                    Transform at = p.InteriorSpawnPoint; string src = "interior spawn point";
                    if (at == null) { try { var idle = p.EmployeeIdlePoints; if (idle != null && idle.Length > 0) { at = idle[0]; src = "employee idle point"; } } catch { } }
                    if (at == null) { at = p.SpawnPoint; src = "doorstep spawn point"; }
                    if (at == null) continue;
                    var w = Place(at.position, $"{p.PropertyName ?? code} (from its {src})", indoorsKnown: src != "doorstep spawn point");
                    if (w != null) _placed[code] = w; else _failedAt[code] = Time.unscaledTime;
                }
            }
            catch (Exception e) { Trace($"wardrobe: placing failed: {e.Message}"); }
        }

        // v0.22.1 (Austin: "put the wardrobe into the wall somewhere dont block doors"): v0.22.0 stuck it 4 cm off the nearest
        // wall with no door check, and skipped walls closer than 0.6 m (the RV never got one, retried every 5 s).
        const float HalfW = 0.38f, Height = 2.25f;

        GameObject Place(Vector3 near, string label, bool indoorsKnown)
        {
            // doors around here: anything called door (the door, its frame, its controller), up to 3 parents up
            var doors = new List<Vector3>();
            foreach (var c in Physics.OverlapSphere(near, 10f, ~0, QueryTriggerInteraction.Collide))
            {
                if (c == null) continue;
                var t = c.transform;
                for (int k = 0; k < 4 && t != null; k++, t = t.parent)
                    if (IsDoorName(t.name)) { doors.Add(c.bounds.center); break; }
            }
            var from = near + Vector3.up * 1.2f;
            float bestD = float.MaxValue; Vector3 bestSpot = default, bestN = default; float bestFloor = 0f; int tried = 0, nearDoor = 0, notSolid = 0, noFloor = 0, outdoors = 0;
            // v0.22.3: in rounds, relaxing until something fits (v0.22.2: Hyland Manor / Taco Ticklers / the RV found nothing):
            // door clearance 1.4 -> 1.0 -> 0.8 m, wall flatness 5 -> 10 -> 20 cm, and the last round only needs the middle
            // strip of wall solid (the RV's walls are curved)
            var rounds = new[] { (door: 1.4f, tol: 0.05f, full: true), (door: 1.0f, tol: 0.10f, full: true), (door: 0.8f, tol: 0.20f, full: false) };
            int round = 0;
            for (; round < rounds.Length && bestD == float.MaxValue; round++)
            {
                var rule = rounds[round];
                tried = nearDoor = notSolid = noFloor = outdoors = 0;
                for (int k = 0; k < 32; k++)
                {
                    var dir = Quaternion.Euler(0, k * 11.25f, 0) * Vector3.forward;
                    if (!Physics.Raycast(from, dir, out var h, 6f, ~0, QueryTriggerInteraction.Ignore) || Mathf.Abs(h.normal.y) > 0.3f || h.distance < 0.3f) continue;
                    var n = new Vector3(h.normal.x, 0, h.normal.z).normalized;
                    var tan = Vector3.Cross(Vector3.up, n);
                    for (float off = -2f; off <= 2.01f; off += 0.5f)            // slide along the wall looking for a solid stretch
                    {
                        tried++;
                        var wallPt = h.point + tan * off;
                        // the floor in front of it; where a downward ray finds none (the RV), the spawn point's own height
                        float floor;
                        if (Physics.Raycast(wallPt + n * 0.4f + Vector3.up * 0.1f, Vector3.down, out var fl, 2.5f, ~0, QueryTriggerInteraction.Ignore)) floor = fl.point.y;
                        else if (Mathf.Abs(wallPt.y - 1.2f - near.y) < 0.6f) { floor = near.y; noFloor++; }
                        else { noFloor++; continue; }
                        if (DoorNear(doors, wallPt, rule.door)) { nearDoor++; continue; }
                        // indoors only: a roof over the spot (an outside wall has open sky above it)
                        // v0.22.4: only when starting from the doorstep: the RV's roof has no collider and the Docks
                        // Warehouse's is over 8 m up, so every spot there read "outdoors" from inside
                        if (!indoorsKnown && !Physics.Raycast(new Vector3(wallPt.x, floor + 2.4f, wallPt.z) + n * 0.4f, Vector3.up, 30f, ~0, QueryTriggerInteraction.Ignore)) { outdoors++; continue; }
                        if (!Solid(wallPt, n, tan, floor, rule.tol, rule.full, out var plane)) { notSolid++; continue; }
                        var spot = new Vector3(wallPt.x, floor, wallPt.z) - n * (Vector3.Dot(new Vector3(wallPt.x, floor, wallPt.z) - plane, n));   // onto the wall plane
                        float d = Vector3.Distance(new Vector3(spot.x, near.y, spot.z), near);
                        if (d < bestD) { bestD = d; bestSpot = spot; bestN = n; bestFloor = floor; }
                    }
                }
            }
            if (bestD == float.MaxValue)
            {
                Trace($"wardrobe: no clear wall in {label} ({tried} spots: {nearDoor} by a door, {notSolid} not solid wall, {noFloor} without a floor under them, {outdoors} outdoors); trying again in a minute");
                return null;
            }
            var go = PropLib.Make("armoir");
            if (go == null) { Trace("wardrobe: no armoir model"); return null; }
            go.name = "BullyLink_Wardrobe";
            go.transform.rotation = Quaternion.LookRotation(bestN, Vector3.up);       // the door faces into the room
            Bounds b = default; bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
            // set INTO the wall: the door's back 2 cm behind the wall face, its front ~1 cm proud, like a built-in closet
            float depth = any ? Mathf.Abs(Vector3.Dot(b.size, bestN)) : 0.056f;
            var centre = bestSpot + bestN * (depth * 0.5f - 0.045f);
            if (any) go.transform.position += new Vector3(centre.x - b.center.x, bestFloor - b.min.y, centre.z - b.center.z);
            else go.transform.position = new Vector3(centre.x, bestFloor, centre.z);
            Trace($"wardrobe: set into a wall in {label} at {go.transform.position}, {bestD:F1} m from it, facing {bestN}, round {round} of 3, {doors.Count} door part(s) nearby");
            return go;
        }

        static bool DoorNear(List<Vector3> doors, Vector3 p, float clear)
        {
            foreach (var d in doors)
            {
                var v = d - p; v.y = 0;
                if (v.magnitude < clear) return true;
            }
            return false;
        }

        /// <summary>A door, not "indoor" / "outdoor" / a doormat (those made Hyland Manor look like 67 doors).</summary>
        static bool IsDoorName(string n)
        {
            if (n.IndexOf("door", StringComparison.OrdinalIgnoreCase) < 0) return false;
            foreach (var x in new[] { "indoor", "outdoor", "doormat" }) if (n.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        /// <summary>A solid, flat wall the closet's full width and height: rays back into the wall at its corners and middle
        /// all hit within 5 cm of one plane (a doorway, window or gap fails it).</summary>
        static bool Solid(Vector3 wallPt, Vector3 n, Vector3 tan, float floor, float tol, bool full, out Vector3 plane)
        {
            plane = default;
            float? first = null;
            foreach (float dx in full ? new[] { -HalfW, 0f, HalfW } : new[] { 0f })
                foreach (float hy in new[] { 0.25f, 1.2f, Height - 0.1f })
                {
                    var o = new Vector3(wallPt.x, floor + hy, wallPt.z) + tan * dx + n * 0.35f;
                    if (!Physics.Raycast(o, -n, out var h, 0.7f, ~0, QueryTriggerInteraction.Ignore)) return false;
                    if (Vector3.Dot(h.normal, n) < (full ? 0.85f : 0.6f)) return false;
                    if (first == null) { first = h.distance; plane = h.point; }
                    else if (Mathf.Abs(h.distance - first.Value) > tol) return false;
                }
            return true;
        }

        // ============================================================ the wardrobe screen
        void OpenUp(Player me)
        {
            var j = Jimmy();
            if (j == null) return;
            Open = true;
            ViewTarget = j.Root.transform;
            _before = new Dictionary<string, string>(Outfit.Current);
            _working = new Dictionary<string, string>(Outfit.Current);
            _slot = 2;                                                              // start on the top
            try { _mv = UnityEngine.Object.FindObjectOfType<PlayerMovement>(); if (_mv != null) _mv.CanMove = false; } catch { }
            Voice.Say("Jimmy", "PIDLE");
            Trace("wardrobe: open");
        }

        void Close(bool save, Player me)
        {
            if (!Open) return;
            Open = false;
            ViewTarget = null;
            var j = Jimmy();
            // v0.22.6 (Austin: "make save outfit save to last save"): Enter writes it into the save being played, right away
            if (save) { Outfit.Wear(_working); Outfit.Commit(); }
            else if (_before != null && j != null) Outfit.Apply(j, _before);
            try { if (_mv != null) _mv.CanMove = true; } catch { }
            Trace($"wardrobe: closed ({(save ? "saved to " + Outfit.SaveKey : "nothing changed")})");
        }

        void Drive(Player me)
        {
            var j = Jimmy();
            if (j == null) { Close(false, me); return; }
            var slot = Outfit.Slots[_slot];
            var items = Outfit.Items(slot);
            if (BullyInput.KeyDown(0x26)) _slot = (_slot + Outfit.Slots.Length - 1) % Outfit.Slots.Length;   // Up
            if (BullyInput.KeyDown(0x28)) _slot = (_slot + 1) % Outfit.Slots.Length;                         // Down
            int step = BullyInput.KeyDown(0x27) ? 1 : BullyInput.KeyDown(0x25) ? -1 : 0;                      // Right / Left
            if (step != 0 && items.Count > 0)
            {
                _working.TryGetValue(slot, out var cur);
                int i = items.FindIndex(t => t.name == cur);                       // -1 = his own / nothing
                i += step;
                if (i < -1) i = items.Count - 1;
                if (i >= items.Count) i = -1;
                _working[slot] = i < 0 ? "" : items[i].name;
                Outfit.Apply(j, _working);
            }
            if (BullyInput.KeyDown(0x08)) { _working[slot] = ""; Outfit.Apply(j, _working); }                // Backspace
            if (BullyInput.KeyDown(0x0D)) { Close(true, me); return; }                                       // Enter
            if (BullyInput.KeyDown(BullyInput.VK_R) || BullyInput.PadDown(BullyInput.Y)) Close(false, me);
        }

        public void OnGUI()
        {
            if (!Open) return;
            float w = 420, h = 40 + Outfit.Slots.Length * 26 + 48;
            var r = new Rect(30, Screen.height / 2f - h / 2f, w, h);
            GUI.Box(r, "");
            GUI.Label(new Rect(r.x + 12, r.y + 8, w - 24, 24), "<b>WARDROBE</b>   (Bully's clothes)");
            for (int k = 0; k < Outfit.Slots.Length; k++)
            {
                var slot = Outfit.Slots[k];
                var items = Outfit.Items(slot);
                _working.TryGetValue(slot, out var cur);
                int i = items.FindIndex(t => t.name == cur);
                string val = i < 0 ? (slot == "TORSO" || slot == "LEGS" || slot == "FEET" ? "Jimmy's own" : "none") : $"{Pretty(items[i].name)}  ·  {Cap(items[i].style)}  ({i + 1}/{items.Count})";
                string line = $"{(k == _slot ? "<color=#FFD24A>> " : "   ")}{Outfit.SlotName[slot]}: {val}{(k == _slot ? "</color>" : "")}";
                GUI.Label(new Rect(r.x + 12, r.y + 36 + k * 26, w - 24, 24), line);
            }
            GUI.Label(new Rect(r.x + 12, r.y + h - 44, w - 24, 40), "Up/Down slot · Left/Right change · Backspace take off\nEnter save to this save · R / Y cancel");
        }

        static string Cap(string s) => string.IsNullOrEmpty(s) ? "" : s.Substring(0, 1) + s.Substring(1).ToLowerInvariant();

        /// <summary>"R_SSleeves1" -> "S Sleeves 1": readable enough (Bully keeps the real names under hashed text keys).</summary>
        static string Pretty(string n)
        {
            int u = n.IndexOf('_');
            var core = u >= 0 && u <= 3 ? n.Substring(u + 1) : n;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < core.Length; i++)
            {
                char c = core[i];
                if (c == '_') { sb.Append(' '); continue; }
                if (i > 0 && ((char.IsUpper(c) && char.IsLower(core[i - 1])) || (char.IsDigit(c) && !char.IsDigit(core[i - 1])))) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
