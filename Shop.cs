// The carnival shop (Austin, 2026-10-10: "take the shop from the carnival and put it in the game at the building across
// from the gas station where i just saved the pose ... build the inside to look like the pawn shop and make it run off
// the schedual1 currency").
//   * Where: Warehouse01 at the Chemical Plant (Westville), the building with the hinged door Austin stood at
//     (pose -104.9, -3.9, 88.9; door -101.5, -3.9, 90.0), across from the Slums Gas Station. It's a fake building: one
//     exterior mesh filled with solid box colliders. Those two boxes are switched off while the shop stands (back on
//     when the mod is off).
//   * Inside: Schedule I's own pawn shop (Region_Northtown/Pawn shop) rebuilt from the game files by
//     tools/export_shop.py (shop.bytes): its static-batched geometry cut out per renderer, drawn with the game's LIVE
//     materials looked up by name (so it renders exactly like the pawn shop), its box colliders and lights, scaled
//     0.873 to fit with its entrance wall on the warehouse's door wall.
//   * The carnival part: Bully's carnie (Carni01, the fanny-pack ticket girl) behind the counter with Bully's carnie
//     voice (Brandy: CARNGAP "step right up", CARNGS a sale, CARNGEL short of money, CARNHF on the way out) and the
//     prize shop's shirt / wig / clown-shoe rack (ISouv_Gifts02) behind her.
//   * Sells: Bully's rides (bike, moped, go-kart; they wait at the motel like before) and kit refills, paid like
//     Schedule I's shops (cash first, then the bank card). At the counter: scroll / LB-RB browse, click / RT buy.
using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;

namespace BullyLink
{
    public class Shop
    {
        public static bool AtCounter;
        public Action<string> Trace = _ => { };
        public Action<string, float> Say = (_, __) => { };
        public Vehicles Rides;
        public Func<Kit> GetKit = () => null;

        const string WarehousePath = "Map/Hyland Point/Region_Westville/ChemicalPlant/Warehouse01";
        GameObject _root;
        bool _tried;
        float _retryAt;
        readonly List<Collider> _off = new();
        readonly Dictionary<IntPtr, bool> _offTrigger = new();     // each switched-off collider's own isTrigger, for the mod off
        readonly List<Renderer> _hiddenFill = new();
        JimmyModel _carnie;
        AnimLayer _carnieAnim;
        Vector3 _counterFront, _roomCentre, _carnieHead;
        float _roomRadius;
        bool _visiting, _shown;
        float _talkUntil, _attractUntil, _shortUntil;
        string _short;
        int _sel;

        class Line { public string label; public float price; public Vehicles.Kind? ride; public Item item; public int count; }
        static readonly Line[] Stock =
        {
            new Line { label = "Bike (Jimmy's BMX)", price = Vehicles.Spec[Vehicles.Kind.Bike].price, ride = Vehicles.Kind.Bike },
            new Line { label = "Moped", price = Vehicles.Spec[Vehicles.Kind.Moped].price, ride = Vehicles.Kind.Moped },
            new Line { label = "Go-Kart", price = Vehicles.Spec[Vehicles.Kind.Kart].price, ride = Vehicles.Kind.Kart },
            new Line { label = "Firecrackers x5", price = 40f, item = Item.Firecracker, count = 5 },
            new Line { label = "Stink Bombs x3", price = 45f, item = Item.StinkBomb, count = 3 },
            new Line { label = "Itching Powder x3", price = 30f, item = Item.ItchingPowder, count = 3 },
            new Line { label = "Marbles x3", price = 20f, item = Item.Marbles, count = 3 },
            new Line { label = "Eggs x6", price = 15f, item = Item.Eggs, count = 6 },
            new Line { label = "Potatoes (spud gun) x10", price = 35f, item = Item.SpudGun, count = 10 },
            new Line { label = "Bottle Rockets x3", price = 60f, item = Item.BottleRocket, count = 3 },
            new Line { label = "Banana Peels x3", price = 10f, item = Item.Banana, count = 3 },
            new Line { label = "Soda x2", price = 8f, item = Item.Soda, count = 2 },
        };

        public void OnSceneChange()
        {
            _root = null; _carnie = null; _carnieAnim = null; _tried = false; _off.Clear(); _hiddenFill.Clear(); AtCounter = false; _shown = false;
            _leaves.Clear(); _oldDoorRb = null; DoorPrompt = null;
        }

        /// <summary>The shop stands while the mod is on; the mod off puts Warehouse01 back exactly as the game had it.</summary>
        public void SetActive(bool on)
        {
            if (on == _shown) return;
            _shown = on;
            if (_root != null) _root.SetActive(on);
            foreach (var c in _off)
            {
                if (c == null) continue;
                if (!_offTrigger.ContainsKey(c.Pointer)) _offTrigger[c.Pointer] = c.isTrigger;
                c.enabled = !on;
                c.isTrigger = on || _offTrigger[c.Pointer];
            }
            foreach (var r in _hiddenFill) if (r != null) r.forceRenderingOff = on;
            // the old physics door has no collider while it's hidden: kinematic, or gravity drops it through the floor
            if (_oldDoorRb != null) _oldDoorRb.isKinematic = on || _oldDoorKinematic;
            if (!on) { AtCounter = false; DoorPrompt = null; }
        }

        // ---- the front door (v0.13.2). Warehouse01's own door is drawn from a static-batched combined mesh, so moving it
        // only moved its collider (Austin: "the door its self wont swing open. you just walk through it"). It's hidden now,
        // with the whole original warehouse mesh: the mod draws a rebuilt copy whose west wall is cut around the
        // storefront, filled by the pawn shop's own front wall, windows and glass double door. The two leaves are real
        // objects of ours (each with its solid box), so they move: E / Schedule I's Interact swings both outward, E again
        // shuts them, and they shut themselves once you're well away.
        class Leaf { public Transform t; public float sign; }
        readonly List<Leaf> _leaves = new();
        Vector3 _doorMid;
        float _doorAngle, _doorTarget;
        Rigidbody _oldDoorRb;
        bool _oldDoorKinematic;
        public string DoorPrompt;
        const float DoorOpenDeg = 95f;
        float _probeAt;
        string _lastProbe;
        bool _reEnabledSeen;

        void BuildLeaves(BinaryReader r, Dictionary<string, Material> mats)
        {
            int nd = r.ReadInt32();
            var mid = Vector3.zero;
            for (int k = 0; k < nd; k++)
            {
                var pivot = V3(r);
                var leaf = new GameObject("shop_door_leaf");
                leaf.transform.SetParent(_root.transform, false);
                leaf.transform.localPosition = pivot;
                int np = r.ReadInt32();
                var centre = Vector3.zero; int cn = 0;
                for (int i = 0; i < np; i++)
                {
                    var (mname, mesh) = ReadMesh(r);
                    foreach (var v in mesh.vertices) { centre += v; cn++; }
                    var go = new GameObject("leaf_" + mname);
                    go.transform.SetParent(leaf.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = mats.TryGetValue(mname, out var m) ? m : JimmyModel.Tint(new Color(0.6f, 0.7f, 0.75f));
                }
                if (r.ReadByte() != 0)
                {
                    var c = V3(r); var q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); var size = V3(r);
                    var col = new GameObject("leaf_collider");
                    col.transform.SetParent(leaf.transform, false);
                    col.transform.localPosition = c; col.transform.localRotation = q;
                    col.AddComponent<BoxCollider>().size = size;
                }
                centre = cn > 0 ? centre / cn : Vector3.zero;
                // outward = the swing that carries the leaf's middle toward the street (shop-local -x)
                float sign = (Quaternion.Euler(0, 90f, 0) * centre).x < (Quaternion.Euler(0, -90f, 0) * centre).x ? 1f : -1f;
                _leaves.Add(new Leaf { t = leaf.transform, sign = sign });
                mid += _root.transform.TransformPoint(pivot + centre);
            }
            if (_leaves.Count > 0) _doorMid = mid / _leaves.Count;
        }

        void UpdateDoor(Player me, bool input, float dt)
        {
            DoorPrompt = null;
            if (_leaves.Count == 0) return;
            float dist = Vector3.Distance(Flat(me.transform.position), Flat(_doorMid));
            if (_doorTarget > 0 && dist > 9f) _doorTarget = 0f;
            // v0.13.4 probe (Austin, v0.13.3: "invisivle barriar stops me from going in the door"; nothing in the game's
            // scene or in shop.bytes sits in the doorway on paper): while you're at the open door, name once a second
            // exactly which colliders are inside the doorway
            if (dist < 3f && _doorAngle > 60f && Time.unscaledTime > _probeAt)
            {
                _probeAt = Time.unscaledTime + 1f;
                try
                {
                    var hits = Physics.OverlapBox(new Vector3(_doorMid.x, me.transform.position.y + 0.95f, _doorMid.z), new Vector3(0.3f, 0.6f, 0.35f));   // body height, clear of the lintel
                    var sb = new System.Text.StringBuilder();
                    foreach (var h in hits)
                    {
                        if (h == null || h.isTrigger || h.transform.IsChildOf(me.transform.root)) continue;
                        sb.Append($"'{(h.transform.parent != null ? h.transform.parent.name : "")}/{h.name}' ");
                    }
                    var line = sb.Length > 0 ? sb.ToString() : "nothing (clear)";
                    if (line != _lastProbe) { _lastProbe = line; Trace($"shop: doorway colliders: {line}"); }
                }
                catch (Exception e) { Trace($"shop: probe failed: {e.Message}"); _probeAt = float.MaxValue; }
            }
            if (input && dist < 2.0f)
            {
                DoorPrompt = _doorTarget > 0 ? "Close" : "Open";
                bool e = false;
                try { e = Il2CppScheduleOne.GameInput.GetButtonDown(Il2CppScheduleOne.GameInput.ButtonCode.Interact); } catch { }
                if (e || BullyInput.KeyDown(0x45))
                {
                    _doorTarget = _doorTarget > 0 ? 0f : DoorOpenDeg;
                    Trace($"shop: door {(_doorTarget > 0 ? "opened" : "closed")}");
                }
            }
            if (Mathf.Approximately(_doorAngle, _doorTarget)) return;
            _doorAngle = Mathf.MoveTowards(_doorAngle, _doorTarget, 220f * dt);
            foreach (var l in _leaves) l.t.localRotation = Quaternion.Euler(0, l.sign * _doorAngle, 0);
        }

        static (string, Mesh) ReadMesh(BinaryReader r)
        {
            string mname = Str(r);
            int nv = r.ReadInt32();
            var v = new Vector3[nv]; var nr = new Vector3[nv]; var uv = new Vector2[nv];
            for (int i = 0; i < nv; i++) { v[i] = V3(r); nr[i] = V3(r); uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle()); }
            int ni = r.ReadInt32();
            var idx = new int[ni];
            for (int i = 0; i < ni; i++) idx[i] = (int)r.ReadUInt32();
            var mesh = new Mesh { name = "shop_" + mname };
            mesh.vertices = v; mesh.normals = nr; mesh.uv = uv;
            mesh.SetTriangles(idx, 0);
            mesh.RecalculateBounds();
            return (mname, mesh);
        }

        public void Update(Player me, bool input)
        {
            if (me == null) return;
            if (_root == null)
            {
                if (_tried || Time.unscaledTime < _retryAt) return;
                _retryAt = Time.unscaledTime + 5f;
                if (!Build()) return;
                _shown = false; SetActive(true);
            }
            if (!_shown) return;
            float dt = Time.deltaTime, now = Time.time;
            // v0.13.5: Schedule I switches Warehouse01's fill box back ON by itself (v0.13.4's doorway probe:
            // "doorway colliders: 'Colliders/Cube' ..." although the build had switched it off) -- that was the invisible
            // barrier filling the doorway. It's held off every frame, and as a trigger nothing walks into it anyway.
            foreach (var fill in _off)
            {
                if (fill == null) continue;
                if (fill.enabled || !fill.isTrigger)
                {
                    if (!_reEnabledSeen) { _reEnabledSeen = true; Trace($"shop: the game switched '{fill.transform.parent?.name}/{fill.name}' back on; holding it off (+ trigger)"); }
                    fill.enabled = false; fill.isTrigger = true;
                }
            }
            UpdateDoor(me, input, dt);
            _carnieAnim?.Apply(dt);
            if (_carnieAnim != null && _carnieAnim.Playing == null) _carnieAnim.Play("carnie_idle", fullBody: true, withRoot: true, rootPos: true);

            // a visit: step inside -> she calls you over; walk out -> "have fun"
            float inRoom = Vector3.Distance(Flat(me.transform.position), Flat(_roomCentre));
            bool inside = inRoom < _roomRadius && Mathf.Abs(me.transform.position.y - _roomCentre.y) < 2.5f;
            if (inside && !_visiting) { _visiting = true; Carnie("CARNGAP", "carnie_attract"); }
            else if (!inside && _visiting && inRoom > _roomRadius + 1.5f) { _visiting = false; Carnie("CARNHF", "carnie_beckon"); }

            AtCounter = input && inside && Vector3.Distance(Flat(me.transform.position), Flat(_counterFront)) < 1.6f;
            if (!AtCounter) return;
            int c = BullyInput.Cycle;
            if (c != 0) _sel = (_sel + c + Stock.Length) % Stock.Length;
            if (BullyInput.FireDown) Buy(Stock[_sel]);
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0, v.z);

        void Carnie(string cat, string clip)
        {
            Voice.Say("Brandy", cat, _carnieHead);
            if (_carnieAnim != null && clip != null) _carnieAnim.Play(clip, fullBody: true, withRoot: true, rootPos: true);
        }

        void Buy(Line l)
        {
            if (l.ride.HasValue && Rides != null && Rides.Owns(l.ride.Value)) { Short($"You already own the {Vehicles.Spec[l.ride.Value].name}: it's parked at the motel."); return; }
            var kit = GetKit();
            if (!l.ride.HasValue && (kit == null || kit.Ammo(l.item) >= Item_Max(l.item))) { Short($"Your {Kit.NameOf(l.item)} are full."); return; }
            if (!Pay(l.price, l.label)) { Carnie("CARNGEL", null); return; }
            if (l.ride.HasValue) { Rides?.Grant(l.ride.Value); Say($"Bought the {l.label}. It's parked at the motel.", 6f); }
            else { kit.AddAmmo(l.item, l.count); Say($"Bought {l.label}.", 4f); }
            Carnie("CARNGS", "carnie_talk");
            Trace($"shop: sold {l.label} for ${l.price:N0}");
        }

        static int Item_Max(Item i) => Kit.MaxOf(i);

        void Short(string s) { _short = s; _shortUntil = Time.time + 4f; }

        /// <summary>Schedule I's money: cash first (with the game's own cash sound), else the bank card, like its shops.</summary>
        bool Pay(float price, string label)
        {
            try
            {
                var money = UnityEngine.Object.FindObjectOfType<MoneyManager>();
                if (money == null) { Short("Can't reach Schedule I's money right now."); return false; }
                float cash = money.cashBalance, bank = 0f;
                try { bank = money.onlineBalance; } catch { }
                if (cash >= price) money.ChangeCashBalance(-price, true, true);
                else if (bank >= price) money.CreateOnlineTransaction($"Carnival shop: {label}", -price, 1f, "BullyLink carnival shop");
                else
                {
                    Short($"Not enough money for the {label}: ${price:N0} (cash ${cash:N0}, bank ${bank:N0})");
                    Trace($"shop: {_short}");
                    return false;
                }
                float cashAfter = money.cashBalance, bankAfter = bank;
                try { bankAfter = money.onlineBalance; } catch { }
                Trace($"shop: paid ${price:N0} for {label}: cash {cash:N0} -> {cashAfter:N0}, bank {bank:N0} -> {bankAfter:N0}");
                return true;
            }
            catch (Exception e) { Trace($"shop pay: {e.Message}"); Short("Payment failed (see the trace)."); return false; }
        }

        // ============================================================ building it
        bool Build()
        {
            var wh = GameObject.Find(WarehousePath);
            if (wh == null) return false;          // not loaded yet (or another scene): retry in 5 s
            _tried = true;
            try
            {
                // the game's live materials by name (the shop's geometry is drawn with exactly these)
                var mats = new Dictionary<string, Material>();
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Material>()))
                {
                    var m = o.TryCast<Material>();
                    if (m == null) continue;
                    var n = m.name.Replace(" (Instance)", "");
                    if (!mats.ContainsKey(n)) mats[n] = m;
                }
                using var r = new BinaryReader(new MemoryStream(JimmyModel.Res("shop.bytes")));
                if (new string(r.ReadChars(4)) != "SHOP") throw new InvalidDataException("shop.bytes");
                uint ver = r.ReadUInt32();
                var pos = V3(r); float yaw = r.ReadSingle(), scale = r.ReadSingle();
                _root = new GameObject("BullyLink_CarnivalShop");
                _root.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
                _root.transform.localScale = Vector3.one * scale;
                int nm = r.ReadInt32(), missing = 0;
                var missingNames = new HashSet<string>();
                for (int k = 0; k < nm; k++)
                {
                    var (mname, mesh) = ReadMesh(r);
                    var go = new GameObject("shop_" + mname);
                    go.transform.SetParent(_root.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    if (mats.TryGetValue(mname, out var mat)) mr.sharedMaterial = mat;
                    else { mr.sharedMaterial = JimmyModel.Tint(new Color(0.55f, 0.5f, 0.45f)); missing++; missingNames.Add(mname); }
                }
                int nb = r.ReadInt32();
                for (int k = 0; k < nb; k++)
                {
                    var c = V3(r); var q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); var size = V3(r);
                    var go = new GameObject("shop_collider");
                    go.transform.SetParent(_root.transform, false);
                    go.transform.localPosition = c; go.transform.localRotation = q;
                    go.AddComponent<BoxCollider>().size = size;
                }
                int nl = r.ReadInt32();
                for (int k = 0; k < nl; k++)
                {
                    var p = V3(r); var col = V3(r); float inten = r.ReadSingle(), range = r.ReadSingle(); r.ReadInt32(); r.ReadSingle();
                    var go = new GameObject("shop_light");
                    go.transform.SetParent(_root.transform, false);
                    go.transform.localPosition = p;
                    // a new Light is a point light; Light.type / spotAngle are interop stand-ins in this build (stripcheck),
                    // so every shop light stays a point light rather than calling them
                    var li = go.AddComponent<Light>();
                    li.color = new Color(col.x, col.y, col.z); li.intensity = inten; li.range = range * scale;
                }
                var anchors = new Dictionary<string, (Vector3 p, float yaw)>();
                int na = r.ReadInt32();
                for (int k = 0; k < na; k++) { var n = Str(r); anchors[n] = (V3(r), r.ReadSingle()); }
                if (ver >= 2) BuildLeaves(r, mats);
                Vector3 roomLo = ver >= 2 ? V3(r) : new Vector3(-4f, 0, -5f), roomHi = ver >= 2 ? V3(r) : new Vector3(4f, 3.6f, 5f);

                // the room, the counter (the player's side faces the entrance: shop-local -x), the carnie behind it
                var T = _root.transform;
                _roomCentre = T.TransformPoint(new Vector3(0, 0, 0));
                _roomRadius = 4.0f * scale;
                var counter = anchors.TryGetValue("CashCounter", out var cc) ? cc.p : new Vector3(0, 0, 0);
                counter.y = 0;
                _counterFront = T.TransformPoint(counter + new Vector3(-0.9f, 0, 0));
                var carniePos = T.TransformPoint(counter + new Vector3(0.8f, 0, 0));
                var facing = Quaternion.LookRotation(T.TransformDirection(Vector3.left), Vector3.up);
                _carnie = JimmyModel.Build(null, Trace, "carnie.bytes", "BullyLink_Carnie", 1f, keep: false);   // dressed like Jimmy, in the game's in-use material
                _carnie.Root.transform.SetParent(_root.transform, true);
                _carnie.Root.transform.SetPositionAndRotation(carniePos, facing);
                _carnie.Root.transform.localScale = Vector3.one * (1f / scale);   // she's full size; the room is scaled
                _carnie.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                _carnieAnim = new AnimLayer(_carnie.ByName);
                _carnieAnim.Play("carnie_idle", fullBody: true, withRoot: true, rootPos: true);
                _carnieHead = carniePos + Vector3.up * 1.6f;
                // the prize shop's shirt / wig / clown-shoe rack against the back wall behind her
                var rack = PropLib.Make("ISouv_Gifts02");
                if (rack != null)
                {
                    rack.transform.SetParent(_root.transform, false);
                    rack.transform.localPosition = new Vector3(3.45f, 0, Mathf.Clamp(counter.z + 1.6f, -3.6f, 3.6f));
                    rack.transform.localRotation = Quaternion.Euler(0, 90f, 0);
                    var box = rack.AddComponent<BoxCollider>(); box.center = new Vector3(0, 0.5f, 0); box.size = new Vector3(2.4f, 1.0f, 1.0f);
                }

                // Warehouse01's own exterior stays (v0.13.3: like the tattoo parlour, the building's own mesh outside, our
                // door in its real doorway); only its static-batched physics door is hidden, collider off, body held still
                var oldDoor = wh.transform.Find("SM_Door");
                if (oldDoor != null)
                {
                    foreach (var rend in oldDoor.GetComponentsInChildren<Renderer>(true)) if (rend.enabled) _hiddenFill.Add(rend);
                    foreach (var col in oldDoor.GetComponents<Collider>()) _off.Add(col);
                    _oldDoorRb = oldDoor.GetComponent<Rigidbody>();
                    if (_oldDoorRb != null) _oldDoorKinematic = _oldDoorRb.isKinematic;
                }
                // Warehouse01's solid fill boxes off (the roof slabs stay), so the door opens into the shop
                var fills = wh.transform.Find("Colliders");
                if (fills != null)
                    for (int i = 0; i < fills.childCount; i++)
                    {
                        var ch = fills.GetChild(i);
                        if (ch.name != "Cube" && ch.name != "Cube (1)") continue;
                        foreach (var col in ch.GetComponents<Collider>())
                        {
                            _off.Add(col);
                            // those boxes were the warehouse's ONLY collision: keep everything outside the shop room solid
                            // (the dead space behind / beside it), so its other walls can't be walked through
                            if (col.enabled) SolidAround(col.bounds, T.TransformPoint(roomLo), T.TransformPoint(roomHi), scale);
                        }
                        var rend = ch.GetComponent<Renderer>();
                        if (rend != null && rend.enabled) _hiddenFill.Add(rend);
                    }
                Trace($"shop: built in Warehouse01 at {pos} (scale {scale:F3}): {nm} meshes ({missing} without a live game material{(missing > 0 ? ": " + string.Join(", ", missingNames) : "")}), {nb} colliders, {nl} lights, carnie at {carniePos}, {_off.Count} warehouse colliders switched off, {_hiddenFill.Count} original renderers hidden, {_leaves.Count} glass door leaves, {_solid} solid pieces around the room");
                return true;
            }
            catch (Exception e)
            {
                Trace($"shop: build failed: {e}");
                if (_root != null) UnityEngine.Object.Destroy(_root);
                _root = null;
                return false;
            }
        }

        int _solid;

        /// <summary>A warehouse fill box minus the shop room (flat, axis-aligned: the shop sits at yaw 0): up to four solid
        /// pieces around the room, children of the shop (so they go when the mod is off).</summary>
        void SolidAround(Bounds b, Vector3 ra, Vector3 rb, float scale)
        {
            float rx0 = Mathf.Min(ra.x, rb.x), rx1 = Mathf.Max(ra.x, rb.x), rz0 = Mathf.Min(ra.z, rb.z), rz1 = Mathf.Max(ra.z, rb.z);
            float bx0 = b.min.x, bx1 = b.max.x, bz0 = b.min.z, bz1 = b.max.z;
            void Piece(float x0, float x1, float z0, float z1)
            {
                if (x1 - x0 < 0.05f || z1 - z0 < 0.05f) return;
                var go = new GameObject("shop_warehouse_solid");
                go.transform.SetParent(_root.transform, false);
                go.transform.position = new Vector3((x0 + x1) / 2, b.center.y, (z0 + z1) / 2);
                go.AddComponent<BoxCollider>().size = new Vector3(x1 - x0, b.size.y, z1 - z0) / scale;   // the shop root is scaled
                _solid++;
            }
            if (bx1 <= rx0 || bx0 >= rx1 || bz1 <= rz0 || bz0 >= rz1) { Piece(bx0, bx1, bz0, bz1); return; }
            // (nothing west of the room: that's the west wall's slab, solid from shop.bytes' own wall boxes, doorway open)
            Piece(rx1, bx1, bz0, bz1);                                   // east of it (behind its back wall)
            float mx0 = Mathf.Max(bx0, rx0), mx1 = Mathf.Min(bx1, rx1);
            Piece(mx0, mx1, bz0, rz0);                                   // south of it
            Piece(mx0, mx1, rz1, bz1);                                   // north of it
        }

        static Vector3 V3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static string Str(BinaryReader r) => System.Text.Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));

        // ============================================================ the counter menu
        public void OnGUI()
        {
            if (_shown && DoorPrompt != null)      // Schedule I's interaction look: the key and the action under the crosshair
            {
                var pr = new Rect(Screen.width / 2f - 70, Screen.height / 2f + 40, 140, 30);
                GUI.Box(pr, "");
                GUI.Label(new Rect(pr.x + 10, pr.y + 5, pr.width - 20, 22), $"<b>[E]</b>  {DoorPrompt}");
            }
            if (!AtCounter || !_shown) return;
            var kit = GetKit();
            float w = 440, h = 30 + Stock.Length * 22 + 52;
            var r = new Rect(Screen.width - w - 24, Screen.height / 2f - h / 2f, w, h);
            GUI.Box(r, "");
            GUI.Label(new Rect(r.x + 12, r.y + 6, w - 24, 22), "<b><color=#FFD24A>CARNIVAL SHOP</color></b>   scroll / LB-RB browse · click / RT buy");
            for (int i = 0; i < Stock.Length; i++)
            {
                var l = Stock[i];
                string state = l.ride.HasValue ? (Rides != null && Rides.Owns(l.ride.Value) ? "  <color=#7CFC7C>owned</color>" : "")
                                               : kit != null ? $"  <color=#AAAAAA>({kit.Ammo(l.item)}/{Kit.MaxOf(l.item)})</color>" : "";
                string line = $"{(i == _sel ? "<color=#FFD24A>></color> <b>" : "   ")}{l.label}{(i == _sel ? "</b>" : "")}   ${l.price:N0}{state}";
                GUI.Label(new Rect(r.x + 12, r.y + 30 + i * 22, w - 24, 22), line);
            }
            if (Time.time < _shortUntil && !string.IsNullOrEmpty(_short))
                GUI.Label(new Rect(r.x + 12, r.y + h - 46, w - 24, 40), $"<color=#FF8A6A>{_short}</color>");
        }
    }
}
