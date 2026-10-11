// Jimmy's Bully kit inside Schedule I (Bully mode): the weapon wheel and his items, lock-on, grapple + wedgie + shove,
// greet / taunt / apologize, the trouble meter, Bully voices for Jimmy AND for Schedule I's people, and Bully's HUD.
// Everything lands on Schedule I's own NPC systems: NPC.ReceiveImpact (their real flinch / fight-back / call-the-cops
// responses), NPCMovement.ActivateRagdoll, SetPanicked, ShowWorldSpaceDialogue.
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.Combat;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;

namespace BullyLink
{
    public enum Item { Fists, Slingshot, Firecracker, StinkBomb, ItchingPowder, Marbles, Eggs, SpudGun, BottleRocket, Banana, Soda }

    public class Kit
    {
        // ---- the arsenal: (name, starting ammo, -1 = endless; slingshot ammo is endless in Bully) ----
        static readonly Dictionary<Item, (string name, int start, int max)> Spec = new()
        {
            [Item.Fists] = ("Fists", -1, -1),
            [Item.Slingshot] = ("Slingshot", -1, -1),
            [Item.Firecracker] = ("Firecrackers", 10, 20),
            [Item.StinkBomb] = ("Stink Bombs", 5, 10),
            [Item.ItchingPowder] = ("Itching Powder", 5, 10),
            [Item.Marbles] = ("Marbles", 5, 10),
            [Item.Eggs] = ("Eggs", 12, 24),
            [Item.SpudGun] = ("Spud Gun", 20, 40),
            [Item.BottleRocket] = ("Bottle Rockets", 6, 12),
            [Item.Banana] = ("Banana Peels", 5, 10),
            [Item.Soda] = ("Soda", 3, 6),
        };
        readonly Dictionary<Item, int> _ammo = new();
        Item _sel = Item.Fists;
        float _cooldown, _restock;

        public static bool Grabbing;           // read by BullyInput: no Schedule I punch while Jimmy has someone by the collar
        /// <summary>Bully's fists are up (Fists picked, nothing of Schedule I's in hand): Alt / X is Bully's punch, not the
        /// game's (BullyInput). Holding any Schedule I item (bat, knife, gun...) hands the attack back to the game.</summary>
        public static bool BullyFists;
        /// <summary>Something of Schedule I's is equipped (gun, knife, bat, product...): its mouse buttons are the game's (v0.18).</summary>
        public static bool SiEquipped;
        public bool Blocking;
        NPC _grab; float _grabUntil;
        NPC _lock; bool _locking;
        float _trouble, _lastOffense;
        readonly Dictionary<string, float> _hitBy = new();     // npc id -> when Jimmy last hit them (apologies)
        readonly Dictionary<string, string> _voice = new();
        List<NPC> _npcs = new(); float _npcScan;
        float _chatterAt;
        readonly System.Random _rng = new();
        public Func<Transform> Hand = () => null;
        public Func<Transform> HandL = () => null;
        public AnimLayer Anim;
        public Func<Camera> View = () => Camera.main;
        public Action<string> Trace = _ => { };

        readonly List<Shot> _shots = new();
        readonly List<Zone> _zones = new();
        readonly List<(Transform t, float until, Vector3 basePos)> _lifts = new();

        class Shot { public Item item; public Vector3 pos, vel; public float g, life; public GameObject go; public bool landed; public float fuse = -1; }
        class Zone { public Item item; public Vector3 pos; public float radius, until, tick; public GameObject go; public HashSet<string> done = new(); }

        public static Kit Current;
        /// <summary>v0.22.22: a ride ran someone over (Vehicles.RunOver): damage by speed, through Hit (cops: an assault;
        /// civilians: Bully's knockout-not-kill rule).</summary>
        public static void RideHit(NPC n, Player me, Vector3 point, Vector3 dir, float force, float share)
        {
            try { Current?.Hit(n, me, point, dir, force, Dmg(n, share), EImpactType.Explosion); } catch { }
        }

        public Kit()
        {
            Current = this;
            foreach (var kv in Spec) _ammo[kv.Key] = kv.Value.start;
        }

        /// <summary>The bike plays Bully's riding throw / slingshot clip when Jimmy fires from it ("throw" | "sling").</summary>
        public static Action<string> RideFired;

        /// <summary>The carnival shop's refills: top an item up (capped at its max); false if it's endless or full.</summary>
        public bool AddAmmo(Item i, int n)
        {
            var s = Spec[i];
            if (s.start < 0 || _ammo[i] >= s.max) return false;
            _ammo[i] = Math.Min(s.max, _ammo[i] + n);
            return true;
        }
        public static string NameOf(Item i) => Spec[i].name;
        public static int MaxOf(Item i) => Spec[i].max;

        /// <summary>Bully's punch / jab from the bike (Bikes.act: BIKE_PUNCH_LEFT/RIGHT, JAB): hits whoever's beside the
        /// rider on that side within reach. Returns who got hit (null = a swing at air).</summary>
        public NPC RideHit(Player me, Vector3 at, Vector3 side, float reach, bool jab)
        {
            if (Time.time > _npcScan) { ScanNpcs(); _npcScan = Time.time + 2f; }
            NPC best = null; float bd = reach;
            foreach (var n in _npcs)
            {
                if (!Alive(n)) continue;
                var d = n.transform.position - at; d.y = 0;
                float dist = d.magnitude;
                if (dist < bd && Vector3.Dot(d.normalized, side) > 0.2f) { best = n; bd = dist; }
            }
            if (best == null) return null;
            var dir = side + me.transform.forward * 0.5f;
            Hit(best, me, Chest(best), dir, jab ? 3f : 7f, Dmg(best, jab ? 0.08f : 0.16f), EImpactType.Punch);
            NpcSay(best, "BUMPR", "INDIG", "DONTHIT");
            NpcReact.Play(best, "npc_ow");
            if (!jab && _rng.NextDouble() < 0.5) JimmySay(Pick("PJEER", "PTAUN"));
            Offense(jab ? 4f : 8f, best);
            return best;
        }

        public string ItemName => Spec[_sel].name;
        public Item Selected => _sel;
        public int Ammo(Item i) => _ammo[i];
        public float Trouble => _trouble;
        public NPC LockTarget => _locking && Alive(_lock) ? _lock : null;
        public NPC Grabbed => _grab;

        /// <summary>The locked-on NPC's health, 0..1, straight from Schedule I's NPCHealth.</summary>
        public float TargetHealth01
        {
            get { try { var n = LockTarget; return n == null ? 0f : Mathf.Clamp01(n.Health.NormalizedHealth); } catch { return 1f; } }
        }
        public bool WouldApologize => _lock != null && _hitBy.TryGetValue(SafeId(_lock), out var t) && Time.time - t < 120f;

        // ---- Bully's models: in Jimmy's hand, and in flight ----
        static readonly Dictionary<Item, string> HeldModel = new()
        {
            [Item.Slingshot] = "slingshot", [Item.Firecracker] = "Cherryb", [Item.StinkBomb] = "stinkbomb", [Item.ItchingPowder] = "W_Itch",
            [Item.Marbles] = "MarbBag", [Item.Eggs] = "EGGPROJ", [Item.SpudGun] = "spudg", [Item.BottleRocket] = "BROCKETLAUNCHER",
            [Item.Banana] = "banana", [Item.Soda] = "ammo_potatocan",
        };
        static readonly Dictionary<Item, string> FlyModel = new()
        {
            [Item.Firecracker] = "Cherryb", [Item.StinkBomb] = "stinkbomb", [Item.ItchingPowder] = "W_Itch", [Item.Eggs] = "EGGPROJ",
            [Item.SpudGun] = "potato",          // v0.22.8: Bully's own potato (it flew as a plain tinted sphere before)
            [Item.BottleRocket] = "BROCKET", [Item.Slingshot] = "marble", [Item.Marbles] = "MarbBag", [Item.Banana] = "banana",
        };
        GameObject _held; Item _heldItem = (Item)(-1);

        void UpdateHeld()
        {
            if (_heldItem == _sel && _held != null) return;
            if (_held != null) UnityEngine.Object.Destroy(_held);
            _held = null; _heldItem = _sel;
            var hand = _sel == Item.Slingshot ? HandL() : Hand();
            if (hand == null || !HeldModel.TryGetValue(_sel, out var model)) return;
            try
            {
                _held = PropLib.Make(model);
                if (_held == null) return;
                _held.transform.SetParent(hand, false);
                _held.transform.localPosition = _sel == Item.Slingshot ? PalmL : PalmR;   // grip-first export: the Handle sits in the palm
                _held.transform.localRotation = Quaternion.identity;
                SetHeldVisible(_heldVisible);
            }
            catch (Exception e) { Trace($"held prop {model}: {e.Message}"); }
        }

        /// <summary>Bully's holding / aiming poses for the current item (called every frame).</summary>
        void Poses()
        {
            if (Anim == null) return;
            var playing = Anim.Playing;
            bool busy = playing == "sling_fire" || playing == "throw" || playing == "plant" || playing == "wedgie";
            if (busy) return;
            if (_sel == Item.Slingshot && (BullyInput.LockOn || BullyInput.Fire)) Anim.Play("sling_aim", holdEnd: true);
            else if ((_sel == Item.SpudGun || _sel == Item.BottleRocket) && (BullyInput.LockOn || BullyInput.Fire)) Anim.Play("spud_aim", holdEnd: true, speed: 2.5f);
            else if (playing == "sling_aim" || playing == "spud_aim") Anim.Stop();
        }

        bool _heldVisible = true;
        static readonly Quaternion SlingCarry = new Quaternion(0.03911f, -0.01298f, -0.0118f, 0.99908f);   // fork up, across his body (measured from the model)
        static readonly Quaternion SpudCarry = new Quaternion(-0.00017f, 0.99928f, -0.03776f, 0.00454f);   // barrel (model -Z) pointing ahead
        static readonly Vector3 PalmL = new Vector3(-0.07f, -0.005f, 0f), PalmR = new Vector3(-0.07f, 0.005f, 0f);   // from Bully's finger bones
        static readonly Vector3 SpudMuzzle = new Vector3(-0.002f, -0.017f, -0.224f);   // Muzzle node, from the Handle
        static readonly Quaternion RocketCarry = new Quaternion(0.00009f, 0.9945f, 0.10469f, 0.00089f);   // launcher barrel (model -Z, 6 deg up) ahead
        static readonly Vector3 RocketMuzzle = new Vector3(-0.001f, 0.111f, -0.522f);

        public bool Aiming => (_sel == Item.Slingshot || _sel == Item.SpudGun || _sel == Item.BottleRocket) && (BullyInput.LockOn || BullyInput.Fire);
        static bool Gun(Item i) => i == Item.Slingshot || i == Item.SpudGun || i == Item.BottleRocket;

        /// <summary>Slingshot / spud gun orientation every frame, after the animators: the arm comes from Schedule I's walk
        /// (carrying) or Bully's aim animation, the prop is held upright facing Jimmy's way or the aim.</summary>
        public void LateHold()
        {
            if (_held == null || !_held.activeSelf || !Gun(_sel)) return;
            var hand = _sel == Item.Slingshot ? HandL() : Hand();
            if (hand == null) return;
            // Both models are built facing like a character (slingshot fork up / spud barrel ahead after the carry
            // offset), so: carrying = face where Jimmy faces; aiming = face exactly where the camera aims (fork up,
            // barrel on the crosshair). Identity-on-the-hand hung the fork upside down in the aim pose (render check).
            var cam = View();
            var dir = Aiming && cam != null ? cam.transform.forward : hand.root.forward;
            if (!Aiming) { dir.y = 0; if (dir.sqrMagnitude < 1e-4f) dir = hand.root.forward; }
            _held.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up) * (_sel == Item.Slingshot ? SlingCarry : _sel == Item.SpudGun ? SpudCarry : RocketCarry);
        }

        /// <summary>First person: the held prop is shadow only, like Jimmy's body; third person: drawn.</summary>
        public void SetHeldVisible(bool on)
        {
            _heldVisible = on;
            if (_held == null) return;
            foreach (var r in _held.GetComponentsInChildren<Renderer>(true))
                r.shadowCastingMode = on ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        }

        /// <summary>Bully hides the item in the hand once it's been thrown; it reappears for the next one.</summary>
        void HideHeldFor(float secs)
        {
            if (_held == null) return;
            _held.SetActive(false);
            var h = _held;
            Later(secs, () => { if (h != null && h == _held && _ammo[_sel] != 0) h.SetActive(true); });
        }

        public void Hide()
        {
            Anim?.Stop();
            if (_held != null) UnityEngine.Object.Destroy(_held);
            _held = null; _heldItem = (Item)(-1);
        }

        // ================================================================== per frame
        public void Update(Player me)
        {
            float now = Time.time, dt = Time.deltaTime;
            bool wasSi = SiEquipped;
            try { SiEquipped = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerInventory>.Instance.isAnythingEquipped; } catch { SiEquipped = false; }
            // v0.22.5 (Austin: "the spud gun does nothing"): with a Schedule I item still equipped, every click went to it and
            // Bully's weapons stood down. Now the last thing picked owns the mouse: equipping a Schedule I item puts Bully's
            // weapon away (back to fists), picking a Bully weapon puts the Schedule I item away (CycleItem).
            if (SiEquipped && !wasSi && _sel != Item.Fists) { _sel = Item.Fists; Trace("kit: a Schedule I item came out: Bully's weapon put away"); }
            if (now > _npcScan) { ScanNpcs(); _npcScan = now + 2f; }
            if (Wardrobe.Open) { BullyFists = false; return; }          // v0.22: the wardrobe has the keys
            _cooldown -= dt;
            if (now > _restock) { Restock(); _restock = now + 90f; }       // Bully: you'd buy more; here a slow trickle
            if (now - _lastOffense > 8f) _trouble = Mathf.Max(0, _trouble - 4f * dt);

            // Bully lets Jimmy use his kit on the BMX (Bikes.act: Weapons / Air/Throw): weapon wheel + fire stay live on the
            // bike, grapple / social / lock-on don't. Any other ride, standing at one, or browsing the shop: hands off.
            bool onBike = Vehicles.Riding && Vehicles.RidingKind == "bike";
            if (Skate.Riding || (Vehicles.Riding && !onBike) || Vehicles.NearRide || Shop.AtCounter) { BullyFists = false; StepShots(me, dt); StepZones(me, now); StepLifts(now); return; }
            int c = BullyInput.Cycle;
            if (c != 0) CycleItem(c);
            if (BullyInput.UnequipDown) _sel = Item.Fists;
            UpdateHeld();
            if (onBike)
            {
                BullyFists = false;
                if (_sel != Item.Fists && _cooldown <= 0 && (BullyInput.FireDown || (_sel == Item.SpudGun && BullyInput.Fire))) FireItem(me);
                StepShots(me, dt); StepZones(me, now); StepLifts(now);
                return;
            }
            Poses();

            // lock-on (right click / LT)
            if (BullyInput.LockOn)
            {
                if (!_locking || !Alive(_lock) || Dist(_lock, me) > 30f)
                {
                    var was = _lock;
                    _lock = PickTarget(me);
                    if (!_locking || _lock != was)
                        Trace(_lock != null ? $"lock: {SafeId(_lock)} at {Dist(_lock, me):F1} m" : $"lock: nobody ({_npcs.Count} NPCs known, {CountAlive()} usable)");
                }
                _locking = true;
            }
            else { _locking = false; _lock = null; }

            // social, while locked on
            if (_locking && Alive(_lock))
            {
                if (BullyInput.GreetDown) GreetOrApologize(me, _lock);
                if (BullyInput.TauntDown) Taunt(me, _lock);
            }

            // grapple (R / Y): wedgie, shove
            if (_grab != null && (!Alive(_grab) || now > _grabUntil || Dist(_grab, me) > 2.5f)) Release();
            if (BullyInput.GrappleDown)
            {
                if (_grab != null) Wedgie(me, _grab);
                else { var n = Nearest(me, 1.8f, 70f); if (n != null) Grab(me, n); }
            }
            else if (_grab != null && BullyInput.MeleeDown) Shove(me, _grab);
            Grabbing = _grab != null;
            Fists(me, now, dt);

            // fire (left click / RT); a Schedule I item in hand has the left click instead (v0.18)
            if (_sel != Item.Fists && !SiEquipped && _cooldown <= 0 && (BullyInput.FireDown || (_sel == Item.SpudGun && BullyInput.Fire)))
                FireItem(me);

            StepShots(me, dt);
            StepZones(me, now);
            StepLifts(now);
            Ambient(me, now);
        }

        void CycleItem(int dir)
        {
            var all = (Item[])Enum.GetValues(typeof(Item));
            int i = Array.IndexOf(all, _sel);
            for (int k = 0; k < all.Length; k++)
            {
                i = (i + dir + all.Length) % all.Length;
                if (_ammo[all[i]] != 0) { _sel = all[i]; if (_sel != Item.Fists) PutAwaySchedOneItem(); return; }
            }
        }

        /// <summary>A Bully weapon picked from the wheel: Schedule I's equipped item goes back in the hotbar (its own Unequip).</summary>
        void PutAwaySchedOneItem()
        {
            try
            {
                var inv = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerInventory>.Instance;
                if (inv == null || !inv.isAnythingEquipped) return;
                var slot = inv.equippedSlot;
                string what = "item";
                try { what = inv.EquippedItem?.Name ?? what; } catch { }
                slot?.Unequip();
                try { inv.EquippedSlotIndex = -1; } catch { }
                SiEquipped = false;
                Trace($"kit: {NameOf(_sel)} picked: Schedule I's {what} put away");
            }
            catch (Exception e) { Trace($"kit: couldn't put the Schedule I item away ({e.Message})"); }
        }

        void Restock()
        {
            foreach (var kv in Spec)
                if (kv.Value.start > 0) _ammo[kv.Key] = Math.Min(kv.Value.max, _ammo[kv.Key] + Math.Max(1, kv.Value.start / 4));
        }

        // ================================================================== NPC helpers
        void ScanNpcs()
        {
            var list = new List<NPC>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<NPC>()))
            {
                var n = o.TryCast<NPC>();
                if (n != null) list.Add(n);
            }
            _npcs = list;
        }

        // Each check on its own: one property that throws mustn't rule an NPC out (v0.3.0 locked onto nobody).
        static bool Alive(NPC n)
        {
            if (n == null) return false;
            try { if (!n.gameObject.activeInHierarchy) return false; } catch { return false; }
            try { if (!n.IsConscious) return false; } catch { }
            try { if (n.IsInVehicle) return false; } catch { }
            try { if (n.isInBuilding) return false; } catch { }
            return true;
        }

        int CountAlive() { int c = 0; foreach (var n in _npcs) if (Alive(n)) c++; return c; }

        static float Dist(NPC n, Player me) => Vector3.Distance(n.transform.position, me.transform.position);
        static Vector3 Chest(NPC n) => n.transform.position + Vector3.up * 1.3f;

        NPC Nearest(Player me, float range, float maxAngle)
        {
            NPC best = null; float bd = range;
            var fwd = me.transform.forward;
            foreach (var n in _npcs)
            {
                if (!Alive(n)) continue;
                var d = n.transform.position - me.transform.position; d.y = 0;
                float dist = d.magnitude;
                if (dist < bd && Vector3.Angle(fwd, d) < maxAngle) { best = n; bd = dist; }
            }
            return best;
        }

        // Who Jimmy locks onto: whoever's closest to where you're looking (flat, from Jimmy), else the nearest person
        // in front of him. Bully's lock-on is generous; so is this.
        NPC PickTarget(Player me)
        {
            var cam = View();
            var look = cam != null ? cam.transform.forward : me.transform.forward;
            look.y = 0;
            if (look.sqrMagnitude < 1e-4f) look = me.transform.forward;
            var from = me.transform.position;
            NPC best = null; float bestScore = float.MaxValue;
            foreach (var n in _npcs)
            {
                if (!Alive(n)) continue;
                var to = n.transform.position - from; to.y = 0;
                float dist = to.magnitude, ang = Vector3.Angle(look, to);
                if (dist > 25f || dist < 0.3f) continue;
                if (ang > 35f && !(dist < 6f && ang < 90f)) continue;
                float score = ang * 0.25f + dist;
                if (score < bestScore) { best = n; bestScore = score; }
            }
            return best;
        }

        string VoiceOf(NPC n)
        {
            var cast = Cast.VoiceFor(n);                      // their Bully adult's voice (Cast.cs)
            if (cast != null) return cast;
            string id = SafeId(n);
            if (_voice.TryGetValue(id, out var v)) return v;
            bool female = false;
            try { female = n.Avatar.Appearance.Gender.ToString().Contains("Female"); } catch { }
            return _voice[id] = Voice.VoiceFor(id, female);
        }

        static string SafeId(NPC n) { try { return n.ID ?? n.name; } catch { return n.name; } }

        // v0.22.25: nobody knocked out or dead says anything (lines queued with Later() check again when they fire)
        void Bubble(NPC n, string text, float secs = 3f) { if (Cast.Down(n)) return; try { n.ShowWorldSpaceDialogue(text, secs); } catch (Exception e) { Trace($"bubble: {e.Message}"); } }
        void NpcSay(NPC n, params string[] cats) { if (Cast.Down(n)) return; try { Voice.NextOwner = n.Pointer; Voice.NpcSay(VoiceOf(n), Chest(n), cats); } catch { } }
        static void JimmySay(string cat) => Voice.Say("Jimmy", cat);

        void Offense(float amount, NPC victim)
        {
            _trouble = Mathf.Min(100f, _trouble + amount);
            _lastOffense = Time.time;
            if (victim != null) _hitBy[SafeId(victim)] = Time.time;
            if (_trouble >= 100f)
                foreach (var n in _npcs)        // full meter: everyone around has had enough of the new kid
                    if (Alive(n) && Vector3.Distance(n.transform.position, Hand()?.position ?? Vector3.zero) < 15f)
                        try { n.SetPanicked_Client(); } catch { }
        }

        /// <summary>Damage as a share of this NPC's max health (Schedule I NPCs don't all have 100).</summary>
        static float Dmg(NPC n, float share)
        {
            try { return Mathf.Max(1f, n.Health.MaxHealth * share); } catch { return 25f * share; }
        }

        void Hit(NPC n, Player me, Vector3 point, Vector3 dir, float force, float damage, EImpactType type)
        {
            float before = -1;
            try { before = n.Health.Health; } catch { }
            // Police (v0.15, Austin: "if i attack the police they should react the way the police react in schedual 1. even
            // when being attacked by bully wepons"): the blow lands exactly like Schedule I's own attacks, real damage IN the
            // impact, an attack type (a slingshot pellet / thrown junk counts as a punch, not an annoying prop), from the
            // player, so NPCResponses_Police runs its own assault responses (arrest, pursuit, fighting back). The Bully
            // knockout-not-kill rule doesn't apply to cops: what Schedule I does to someone who hits a cop, it does.
            if (n.TryCast<Il2CppScheduleOne.Police.PoliceOfficer>() != null)
            {
                try
                {
                    var t = type == EImpactType.PhysicsProp ? EImpactType.Punch : type;
                    n.ReceiveImpact(new Impact(point, dir.normalized, force, Mathf.Max(1f, damage), t, me.NetworkObject));
                    float a2 = -1; try { a2 = n.Health.Health; } catch { }
                    Trace($"hit cop {SafeId(n)}: {t} dmg {Mathf.Max(1f, damage):F1} as an assault, health {before:F1} -> {a2:F1}");
                }
                catch (Exception e) { Trace($"impact (cop): {e.Message}"); }
                return;
            }
            // Bully rules: Jimmy's pranks knock people out, they don't kill. A hit that would take Schedule I's health
            // to zero lands with no damage and Schedule I's own KnockOut() instead (the ring went past red).
            try
            {
                var hp = n.Health;
                if (hp != null && !hp.IsKnockedOut && !hp.IsDead && damage > 0 && hp.Health - damage <= 0.5f)
                {
                    damage = 0;
                    Later(0.05f, () => { try { if (!hp.IsKnockedOut && !hp.IsDead) { hp.KnockOut(); NpcSay(n, "SCARED", "DONTHIT"); Trace($"KO: {SafeId(n)}"); } } catch (Exception e) { Trace($"KO: {e.Message}"); } });
                }
            }
            catch { }
            try
            {
                // pranks: the impact carries only the shove + reaction; the damage goes in as NON-lethal through NPCHealth,
                // so however Schedule I scales impact damage, nobody dies from a Bully prank.
                // Fists (v0.18.2, Austin: punched NPCs "react and then not be phased"): a zero-damage impact reads to the
                // game as an annoyance, so they shrugged real hits off. A punch now lands like Schedule I's own (damage IN
                // the impact), so they fight back / run / call the cops; punches are non-lethal in Schedule I, and a blow
                // that would empty the bar was already turned into the knockout above.
                bool fist = type == EImpactType.Punch;
                var imp = new Impact(point, dir.normalized, force, fist ? damage : 0f, type, me.NetworkObject);
                n.ReceiveImpact(imp);
                if (!fist && damage > 0) n.Health.TakeDamage(damage, false);
                float after = -1, max = -1;
                try { after = n.Health.Health; max = n.Health.MaxHealth; } catch { }
                Trace($"hit {SafeId(n)}: {type} dmg {damage:F1}, health {before:F1} -> {after:F1} / {max:F1}");
            }
            catch (Exception e) { Trace($"impact: {e.Message}"); }
        }

        // ================================================================== Bully's fist fighting (v0.15)
        // Austin: "implement the fighting machanics from bully as well" + "keep the schedual 1 use of wepons find a way to
        // intagrate". Fists picked and nothing of Schedule I's in hand: tap Alt / X = Bully's combo (jab, cross, hook, kick
        // finisher), hold then let go = the haymaker, hold V = guard (most of a blow taken behind it comes back). Lock-on
        // (right click) just targets (v0.20.2). A Schedule I weapon in hand = the game's own attack, untouched.
        struct Move { public string clip, react; public float at, reach, force, share; }
        static readonly Move[] Combo =
        {
            new Move { clip = "fist_jab", at = 0.40f, reach = 1.5f, force = 3f, share = 0.06f, react = "hit_light" },
            new Move { clip = "fist_cross", at = 0.45f, reach = 1.5f, force = 4.5f, share = 0.08f, react = "hit_head" },
            new Move { clip = "fist_hook", at = 0.45f, reach = 1.6f, force = 6f, share = 0.12f, react = "hit_stagger" },
            new Move { clip = "fist_kick", at = 0.40f, reach = 1.8f, force = 9.5f, share = 0.15f, react = "hit_down" },
        };
        static readonly Move Haymaker = new Move { clip = "fist_haymaker", at = 0.35f, reach = 1.7f, force = 12f, share = 0.25f, react = "hit_down2" };
        const int VK_V = 0x56;
        Move? _move; bool _moveLanded;
        int _combo; float _comboUntil, _meleeHeld = -1f, _hpWas = -1f;
        Vector3 _lastPos; float _mySpeed;

        void Fists(Player me, float now, float dt)
        {
            BullyFists = _sel == Item.Fists && !SiEquipped && _grab == null;
            var p = me.transform.position; var d = p - _lastPos; d.y = 0; _lastPos = p;
            _mySpeed = Mathf.Lerp(_mySpeed, dt > 1e-4f ? d.magnitude / dt : 0f, 1f - Mathf.Exp(-8f * dt));
            bool still = _mySpeed < 0.6f;

            // the guard
            float hp = -1f;
            try { hp = me.Health.CurrentHealth; } catch { }
            Blocking = BullyFists && BullyInput.Key(VK_V) && _move == null;
            if (Blocking)
            {
                Anim?.Play("fist_block", holdEnd: true);
                if (_hpWas > 0 && hp >= 0 && hp < _hpWas - 0.01f)
                    try { float back = (_hpWas - hp) * 0.75f; me.Health.RecoverHealth(back); hp += back; Trace($"fists: blocked, {back:F1} of {_hpWas - hp + back:F1} damage back"); } catch { }
            }
            else if (Anim != null && Anim.Playing == "fist_block") Anim.Stop();
            _hpWas = hp;
            if (!BullyFists) { _meleeHeld = -1f; _move = null; if (Anim != null && Anim.Playing == "fist_stance") Anim.Stop(); return; }

            // a move under way: it lands at its moment
            if (_move.HasValue)
            {
                var m = _move.Value;
                if (Anim == null || Anim.Playing != m.clip) _move = null;
                else if (!_moveLanded && Anim.T01 >= m.at) { _moveLanded = true; Land(me, m); }
            }

            if (BullyInput.PunchDown) _meleeHeld = now;
            bool charged = _meleeHeld > 0 && now - _meleeHeld > 0.45f;
            if (charged && _move == null) Anim?.Play(Haymaker.clip, fullBody: still, freeze: 0.12f);     // winding up
            if (_meleeHeld > 0 && !BullyInput.Punch)
            {
                if (charged) Begin(Haymaker, still, now);
                else if (_move == null || Anim == null || Anim.T01 > 0.55f)          // Bully lets the next hit cut the last one's recovery
                {
                    if (now > _comboUntil) _combo = 0;
                    Begin(Combo[_combo], still, now);
                    _combo = (_combo + 1) % Combo.Length;
                }
                _meleeHeld = -1f;
            }
            // v0.20.2 (Austin: "holding right click cause him to air box lol make it just target"): no fight stance on the
            // lock-on any more (the looping fist_stance clip read as shadowboxing); lock-on just targets. Anything still
            // playing it (an older bind) is stopped.
            if (Anim != null && Anim.Playing == "fist_stance") Anim.Stop();
        }

        void Begin(Move m, bool still, float now)
        {
            Anim?.Play(m.clip, fullBody: still);
            _move = m; _moveLanded = false;
            float dur = BullyAnims.Clips.TryGetValue(m.clip, out var c) ? c.Duration : 0.6f;
            _comboUntil = now + dur + 0.45f;
        }

        void Land(Player me, Move m)
        {
            var n = LockTarget;
            if (n != null && Dist(n, me) > m.reach + 0.3f) n = null;
            n ??= Nearest(me, m.reach, 60f);
            if (n == null) { Trace($"fists: {m.clip} hit air (nobody within {m.reach:F1} m in front)"); return; }
            var dir = n.transform.position - me.transform.position; dir.y = 0;
            Cast.NextReaction(n, m.react);
            Hit(n, me, Chest(n), dir, m.force, Dmg(n, m.share), EImpactType.Punch);
            if (m.react.StartsWith("hit_down")) { NpcSay(n, "DONTHIT", "FIGHB", "BUMPR"); if (_rng.NextDouble() < 0.6) JimmySay(Pick("PJEER", "PTAUN")); }
            else if (_rng.NextDouble() < 0.4) NpcSay(n, "FIGHB", "BUMPR", "INDIG");
            Offense(m.react.StartsWith("hit_down") ? 9f : 4f, n);
            Trace($"fists: {m.clip} landed on {SafeId(n)}");
        }

        static void Ragdoll(NPC n, Vector3 point, Vector3 dir, float force)
        {
            try { n.Movement.ActivateRagdoll(point, dir.normalized, force); } catch { }
        }

        // ================================================================== social + grapple
        void GreetOrApologize(Player me, NPC n)
        {
            bool sorry = _hitBy.TryGetValue(SafeId(n), out var t) && Time.time - t < 120f;
            if (sorry)
            {
                JimmySay("PAPOGEN");
                _trouble = Mathf.Max(0, _trouble - 20f);
                _hitBy.Remove(SafeId(n));
                Later(0.9f, () => { NpcSay(n, "RESPGD", "BYE", "GREE"); Bubble(n, Pick("...Fine. Whatever.", "Yeah, you'd better be sorry.", "Just stay away from me.")); });
            }
            else
            {
                JimmySay("PGREEGEN");
                Later(0.8f, () => { NpcSay(n, "RESPGD", "GREE", "CHATTER"); Bubble(n, Pick("Hey.", "Oh, hi.", "What's up, new kid?", "Do I know you?")); });
            }
        }

        void Taunt(Player me, NPC n)
        {
            JimmySay(Pick("PTAUN", "PTAUNCOM", "PJEER"));
            Offense(5f, null);
            Later(1.0f, () =>
            {
                if (!Alive(n)) return;
                if (_rng.NextDouble() < 0.55)
                {   // they come at him: a harmless shove from Jimmy flips Schedule I's fight-back response
                    NpcSay(n, "TAUNRES", "FIGH", "TAUN");
                    Bubble(n, Pick("You wanna go?!", "Oh, it's ON.", "Say that again!", "You're dead, new kid!"));
                    Hit(n, me, Chest(n), n.transform.position - me.transform.position, 0.1f, 0f, EImpactType.Punch);
                }
                else
                {
                    NpcSay(n, "INDIG", "FLUST", "TAUNRES");
                    Bubble(n, Pick("Whatever, loser.", "Real mature.", "Leave me alone!"));
                }
            });
        }

        void Grab(Player me, NPC n)
        {
            _grab = n;
            _grabUntil = Time.time + 3f;
            try { n.Movement.Stop(); n.Movement.FacePoint(me.transform.position, 0.2f); } catch { }
            NpcSay(n, "DONTHIT", "SCARED", "INDIG");
            Bubble(n, Pick("Hey! Let go!", "What are you doing?!", "Get off me!"), 2f);
        }

        void Release() { _grab = null; Grabbing = false; }

        void Wedgie(Player me, NPC n)
        {
            JimmySay(Pick("PTAUNHUM", "PTAUNHUMH"));
            NpcSay(n, "SCARED", "DONTHIT", "FLUST");
            Bubble(n, Pick("AAAGH! MY UNDERWEAR!", "NOT THE WEDGIE!", "My shorts! MY SHORTS!", "I'm telling!"), 3f);
            Anim?.Play("wedgie", fullBody: true);
            Later(0.45f, () =>
            {
                try
                {
                    var av = n.Avatar.transform;
                    _lifts.Add((av, Time.time + 0.9f, av.localPosition));    // hoisted by the waistband
                }
                catch { }
            });
            Hit(n, me, n.transform.position + Vector3.up, Vector3.up, 0.5f, Dmg(n, 0.12f), EImpactType.Punch);
            Later(1.0f, () => NpcReact.Play(n, "npc_wedgied"));
            Later(2.7f, () => { try { n.SetPanicked_Client(); } catch { } });
            Offense(12f, n);
            Release();
        }

        void Shove(Player me, NPC n)
        {
            var dir = n.transform.position - me.transform.position; dir.y = 0;
            JimmySay(Pick("PJEER", "PTAUN"));
            NpcSay(n, "BUMPR", "INDIG");
            Ragdoll(n, Chest(n), dir + Vector3.up * 0.2f, 9f);
            Offense(8f, n);
            Release();
        }

        // ================================================================== items
        void FireItem(Player me)
        {
            if (_ammo[_sel] == 0) { CycleItem(1); return; }
            var hand = Hand();
            var from = hand != null ? hand.position : me.transform.position + Vector3.up * 1.4f;
            var cam = View();
            Vector3 aimAt;
            if (_locking && Alive(_lock)) aimAt = Chest(_lock);
            else if (cam != null)
            {
                var ray = new Ray(cam.transform.position, cam.transform.forward);
                aimAt = Physics.Raycast(ray, out var h, 60f, ~0, QueryTriggerInteraction.Ignore) && !h.transform.IsChildOf(me.transform.root)
                    ? h.point : ray.GetPoint(40f);
            }
            else aimAt = from + me.transform.forward * 20f;

            switch (_sel)
            {
                case Item.Soda:
                    try { me.Health.RecoverHealth(40f); } catch { }
                    Sfx.Play(me.transform.position + Vector3.up * 1.4f, 0.8f, "OpenCan_16k");
                    Later(0.5f, () => Sfx.Play(me.transform.position + Vector3.up * 1.5f, 0.8f, "DrinkAhh_1SML"));
                    JimmySay("PDRINK");
                    Use(1f);
                    return;
                case Item.Banana:
                    {
                        if (Vehicles.Riding) RideFired?.Invoke("throw");
                        else Anim?.Play("plant", speed: 2.2f);
                        // on the bike they drop behind it (a trap for whoever's chasing), on foot in front
                        var spot = me.transform.position + me.transform.forward * (Vehicles.Riding ? -1.5f : 1.2f);
                        if (Physics.Raycast(spot + Vector3.up, Vector3.down, out var g, 3f, ~0, QueryTriggerInteraction.Ignore)) spot = g.point;
                        var it = _sel;
                        Later(0.4f, () => Sfx.Play(spot, 0.9f, it == Item.Marbles ? new[] { "MarbleDrops1", "MarbleDrops2", "MarbleDrops3" } : new[] { "BannaPeel01_8va", "BananaSplats02" }));
                        Later(0.4f, () => AddZone(it, spot, it == Item.Marbles ? 1.8f : 0.8f, it == Item.Marbles ? 30f : 60f, it == Item.Marbles ? new Color(0.3f, 0.5f, 1f) : new Color(1f, 0.9f, 0.2f), 0.12f));
                        JimmySay("PTHROWB");
                        Use(0.6f);
                        return;
                    }
            }

            float speed = 16f, grav = 9.8f;
            switch (_sel)
            {
                case Item.Slingshot: speed = 45f; grav = 3f; break;
                case Item.SpudGun: speed = 35f; grav = 6f; break;
                case Item.BottleRocket: speed = 28f; grav = 0.8f; break;
                case Item.Eggs: speed = 18f; break;
                case Item.Marbles: speed = 14f; break;
            }
            bool thrown = _sel != Item.Slingshot && _sel != Item.SpudGun && _sel != Item.BottleRocket;
            if (Vehicles.Riding) RideFired?.Invoke(thrown ? "throw" : "sling");   // the bike plays Bully's own riding throw / aim
            else if (_sel == Item.Slingshot) Anim?.Play("sling_fire");
            else if (thrown) Anim?.Play("throw");
            var item = _sel;
            if (thrown)
            {
                Later(0.26f, () => { Launch(me, item, aimAt, speed, grav); HideHeldFor(0.5f); });
                if (_rng.NextDouble() < 0.35) JimmySay("PTHROWB");
                Use(0.7f);
                return;
            }
            Launch(me, item, aimAt, speed, grav);
            Use(_sel == Item.Slingshot ? 0.55f : _sel == Item.SpudGun ? 0.35f : 0.7f);
        }

        void Launch(Player me, Item item, Vector3 aimAt, float speed, float grav)
        {
            var hand = item == Item.Slingshot ? HandL() : Hand();
            var from = hand != null ? hand.position : me.transform.position + Vector3.up * 1.4f;
            if (_held != null && _held.activeSelf && item == Item.SpudGun) from = _held.transform.TransformPoint(SpudMuzzle);
            else if (_held != null && _held.activeSelf && item == Item.BottleRocket) from = _held.transform.TransformPoint(RocketMuzzle);
            else if (_held != null && _held.activeSelf && item == Item.Slingshot) from = _held.transform.TransformPoint(new Vector3(0f, 0.2f, 0f));
            var vel = Ballistic(from, aimAt, speed, grav);
            var shot = new Shot { item = item, pos = from, vel = vel, g = grav, life = 6f };
            shot.go = FlyModel.TryGetValue(item, out var fm) ? PropLib.Make(fm) : null;
            if (shot.go == null) shot.go = Blob(ShotColor(item), item == Item.SpudGun ? 0.09f : 0.12f);
            if (item == Item.BottleRocket) Bundle.Fx("FX_RocketTrail", from, life: 7f, parent: shot.go.transform);
            shot.go.transform.position = from;
            if (item == Item.SpudGun) shot.go.transform.localScale = Vector3.one * 0.6f;      // Bully's potato is 23 cm long: a spud-gun round
            _shots.Add(shot);
            // v0.22.20: Bully's own sounds for every shot / throw
            switch (item)
            {
                case Item.SpudGun: Sfx.Play(from, 1f, "SPUDGUN_FIRE_02A", "SpudCannonFire5A_2"); break;
                case Item.BottleRocket: Sfx.Play(from, 1f, "BTL_ROCKET_FIRE_01", "BTL_ROCKET_FIRE_03", "BTL_ROCKET_FIRE_04"); break;
                case Item.Slingshot: Sfx.Play(from, 0.9f, "SLINGSHOT_FIRE"); break;
                default: Sfx.Play(from, 0.7f, "GENERIC_THROW_03", "GENERIC_THROW_04", "GENERIC_THROW_05"); break;
            }
            if (item == Item.SpudGun && Time.time > _spudLog) { _spudLog = Time.time + 3f; Trace($"spud: fired from {from} at {aimAt} ({Vector3.Distance(from, aimAt):F1} m), shot {(shot.go != null ? shot.go.name : "none")}"); }
        }

        float _spudLog, _spudHitLog;

        void Use(float cooldown)
        {
            if (_ammo[_sel] > 0) _ammo[_sel]--;
            _cooldown = cooldown;
            if (_ammo[_sel] == 0) CycleItem(1);
        }

        static Vector3 Ballistic(Vector3 from, Vector3 to, float speed, float g)
        {
            var d = to - from;
            float t = d.magnitude / speed;
            var v = d / Mathf.Max(t, 0.05f);
            v.y += 0.5f * g * t;             // lift so it arrives at the aim point
            return v;
        }

        static Color ShotColor(Item i) => i switch
        {
            Item.Eggs => new Color(0.95f, 0.93f, 0.85f),
            Item.Firecracker => new Color(0.85f, 0.1f, 0.1f),
            Item.StinkBomb => new Color(0.35f, 0.6f, 0.2f),
            Item.ItchingPowder => new Color(0.9f, 0.85f, 0.7f),
            Item.SpudGun => new Color(0.6f, 0.45f, 0.25f),
            Item.BottleRocket => new Color(0.9f, 0.3f, 0.1f),
            _ => new Color(0.4f, 0.4f, 0.45f),
        };

        GameObject Blob(Color c, float size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.transform.localScale = Vector3.one * size;
            var m = JimmyModel.Tint(c);
            if (m != null) go.GetComponent<Renderer>().sharedMaterial = m;
            return go;
        }

        void StepShots(Player me, float dt)
        {
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                var s = _shots[i];
                s.life -= dt;
                if (s.fuse >= 0)
                {
                    s.fuse -= dt;
                    if (s.fuse < 0) { Sfx.Play(s.pos, 1f, "CHERRYBOMB_EXPLODE_01a", "CHERRYBOMB_EXPLODE_02a", "CHERRYBOMB_EXPLODE_04b"); Explode(me, s.pos, 2.5f, 8f); Kill(i); }
                    continue;
                }
                if (s.landed) continue;
                var next = s.pos + s.vel * dt;
                s.vel += Vector3.down * s.g * dt;
                var seg = next - s.pos;
                if (seg.sqrMagnitude > 1e-6f && CastSkippingPlayer(me, s.pos, seg, 0.06f, out var hit))
                {
                    s.pos = hit.point;
                    s.go.transform.position = s.pos;
                    OnImpact(me, s, hit, i);
                    continue;
                }
                s.pos = next;
                s.go.transform.position = s.pos;
                if (s.vel.sqrMagnitude > 0.01f)       // rockets fly nose first; everything else tumbles
                    s.go.transform.rotation = s.item == Item.BottleRocket ? Quaternion.LookRotation(s.vel) : s.go.transform.rotation * Quaternion.Euler(400f * dt, 230f * dt, 0);
                if (s.life <= 0) Kill(i);
            }
        }

        bool CastSkippingPlayer(Player me, Vector3 from, Vector3 seg, float radius, out RaycastHit best)
        {
            best = default;
            float bd = float.MaxValue; bool any = false;
            var root = me.transform.root;
            foreach (var h in Physics.SphereCastAll(from, radius, seg.normalized, seg.magnitude, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null || h.transform.IsChildOf(root)) continue;
                if (h.distance < bd) { bd = h.distance; best = h; any = true; }
            }
            return any;
        }

        void OnImpact(Player me, Shot s, RaycastHit hit, int index)
        {
            NPC n = null;
            try { n = hit.collider.GetComponentInParent<NPC>(); } catch { }
            var dir = s.vel.normalized;
            var at = hit.point;
            switch (s.item)                                                    // v0.22.20: Bully's impact sounds
            {
                case Item.Slingshot: if (n != null) Sfx.Play(at, 0.9f, "SLINGSHOT_BODYHIT_01", "SLINGSHOT_BODYHIT_02", "SLINGSHOT_BODYHIT_04"); else Sfx.Play(at, 0.8f, "SLINGSHOT_WALLHIT_02", "SLINGSHOT_WALLHIT_03"); break;
                case Item.Eggs: Sfx.Play(at, 0.9f, "EGG_IMPACT_01", "EGG_IMPACT_02", "EGG_IMPACT_03"); break;
                case Item.SpudGun: Sfx.Play(at, n != null ? 1f : 0.6f, "SPUD_BODYHIT_01", "SPUD_BODYHIT_02", "SPUD_BODYHIT_03"); break;
                case Item.BottleRocket: Sfx.Play(at, 1f, "BTLROCKET_IMPACT_01a", "BTLROCKET_IMPACT_02a", "BTLROCKET_IMPACT_03a"); break;
                case Item.Firecracker: Sfx.Play(at, 0.7f, "CHERRYBOMB_FUSE_LOOP216k"); break;
                case Item.StinkBomb:                                           // the glass vial breaks, then the stink
                    Sfx.Play(at, 1f, "PlateBreakSmall3", "GlassCrack01EQedit2");
                    Sfx.Play(at, 0.9f, "STINKBOMB_EXPLODE_01b");
                    Sfx.Play(at, 0.6f, "STINKBOMB_HISS_02a11SML");
                    break;
                case Item.ItchingPowder: Sfx.Play(at, 0.9f, "PowderHit1", "PowderHit2"); break;
            }
            switch (s.item)
            {
                case Item.Slingshot:
                    Bundle.Fx("FX_Hit", hit.point);
                    if (n != null) { Hit(n, me, hit.point, dir, 6f, Dmg(n, 0.26f), EImpactType.PhysicsProp); NpcSay(n, "BUMPR", "INDIG", "DONTHIT"); Bubble(n, Pick("OW!", "Hey!", "Who did that?!")); NpcReact.Play(n, "npc_ow"); Offense(8f, n); }
                    Kill(index); break;
                case Item.Eggs:
                    Bundle.Fx("FX_EggSplat", hit.point, Quaternion.LookRotation(hit.normal));
                    if (n != null) { Hit(n, me, hit.point, dir, 1.5f, Dmg(n, 0.06f), EImpactType.PhysicsProp); NpcSay(n, "FLUST", "INDIG"); Bubble(n, Pick("EWW! GROSS!", "Is that EGG?!", "My clothes!")); NpcReact.Play(n, "npc_egged"); Offense(6f, n); }
                    Kill(index); break;
                case Item.SpudGun:
                    if (Time.time > _spudHitLog) { _spudHitLog = Time.time + 3f; Trace($"spud: hit '{hit.collider.name}' {(n != null ? "(" + SafeId(n) + ")" : "")} {Vector3.Distance(hit.point, me.transform.position):F1} m away"); }
                    Bundle.Fx("FX_Dust", hit.point);
                    if (n != null) { Hit(n, me, hit.point, dir, 16f, Dmg(n, 0.45f), EImpactType.PhysicsProp); NpcSay(n, "DONTHIT", "BUMPR"); Bubble(n, "OOF!"); NpcReact.Play(n, "npc_ow"); Offense(12f, n); }
                    Kill(index); break;
                case Item.BottleRocket:
                    Explode(me, hit.point, 3f, 9f); Kill(index); break;
                case Item.Firecracker:
                    s.landed = true; s.fuse = 1.8f; break;                       // lies there fizzing, then BANG
                case Item.StinkBomb:
                    AddZone(Item.StinkBomb, hit.point, 4f, 8f, new Color(0.4f, 0.65f, 0.2f), 0.5f); Kill(index); Offense(10f, null); break;
                case Item.Marbles:
                    {   // v0.22.21 (Austin: "make the bag of marbles throwable where it throws a projectile and scatters the
                        // marbles where it lands"): Bully's marble bag flies, bursts on landing, and the marbles spill out
                        // across the ground under it (a hit on a wall / person drops them straight down)
                        var spill = hit.point;
                        if (hit.normal.y < 0.6f || n != null) spill -= dir * 0.3f;
                        if (Physics.Raycast(spill + Vector3.up * 0.5f, Vector3.down, out var g, 4f, ~0, QueryTriggerInteraction.Ignore)) spill = g.point;
                        Sfx.Play(spill, 1f, "MarbleDrops1", "MarbleDrops2", "MarbleDrops3");
                        AddZone(Item.Marbles, spill, 2.2f, 30f, new Color(0.3f, 0.5f, 1f), 0.12f);
                        if (n != null) { NpcSay(n, "BUMPR", "INDIG"); Offense(4f, n); }
                        Kill(index); break;
                    }
                case Item.ItchingPowder:
                    AddZone(Item.ItchingPowder, hit.point, 2.5f, 3f, new Color(0.95f, 0.9f, 0.75f), 0.35f); Kill(index); Offense(6f, null); break;
                default:
                    Kill(index); break;
            }
        }

        void Explode(Player me, Vector3 at, float hurtR, float scareR)
        {
            foreach (var n in _npcs)
            {
                if (!Alive(n)) continue;
                float d = Vector3.Distance(n.transform.position, at);
                if (d < hurtR) { Hit(n, me, Chest(n), n.transform.position - at + Vector3.up, 5f, Dmg(n, 0.25f + 0.25f * Mathf.Clamp01(1f - d / hurtR)), EImpactType.Explosion); NpcReact.Blast(n, at, 8f + (hurtR - d) * 3f); Offense(6f, n); }
                else if (d < scareR)
                {
                    NpcReact.Play(n, "npc_startle");
                    var victim = n;
                    Later(1.6f, () => { try { victim.SetPanicked_Client(); } catch { } });
                    if (_rng.NextDouble() < 0.4) NpcSay(n, "SCARED", "DONTHIT");
                }
            }
            Offense(15f, null);
            if (Bundle.Fx("FX_Bang", at) == null)
            {
                var flash = Blob(new Color(1f, 0.85f, 0.3f), 0.6f);
                flash.transform.position = at;
                UnityEngine.Object.Destroy(flash, 0.15f);
            }
        }

        void Kill(int i)
        {
            if (_shots[i].go != null) UnityEngine.Object.Destroy(_shots[i].go);
            _shots.RemoveAt(i);
        }

        void AddZone(Item item, Vector3 at, float r, float secs, Color c, float size)
        {
            var z = new Zone { item = item, pos = at, radius = r, until = Time.time + secs };
            z.go = new GameObject("BullyLink_" + item);
            z.go.transform.position = at;
            if (item == Item.StinkBomb && Bundle.Fx("FX_StinkCloud", at + Vector3.up * 0.3f, life: secs + 3f) != null) { _zones.Add(z); return; }
            if (item == Item.ItchingPowder && Bundle.Fx("FX_Itch", at + Vector3.up * 0.2f) != null)
            {
                Bundle.Fx("FX_ItchCloud", at + Vector3.up * 0.4f);
                _zones.Add(z);
                return;
            }
            int puffs = item == Item.Banana ? 1 : item == Item.Marbles ? 14 : 7;     // a burst bag: plenty of marbles
            for (int k = 0; k < puffs; k++)
            {
                GameObject b = null;
                if (item == Item.Marbles || item == Item.Banana)
                {
                    b = PropLib.Make(item == Item.Banana ? "banana" : "marble");
                    if (b != null) b.transform.localRotation = Quaternion.Euler(90f, (float)_rng.NextDouble() * 360f, 0);
                }
                if (b == null) b = Blob(c, item == Item.Banana ? size : size * (0.6f + (float)_rng.NextDouble() * 0.8f));
                b.transform.SetParent(z.go.transform, false);
                var o = UnityEngine.Random.insideUnitCircle * r * (item == Item.StinkBomb ? 0.6f : 0.9f);
                b.transform.localPosition = new Vector3(o.x, item == Item.StinkBomb || item == Item.ItchingPowder ? 0.4f + (float)_rng.NextDouble() : 0.03f, o.y);
            }
            _zones.Add(z);
        }

        void StepZones(Player me, float now)
        {
            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                var z = _zones[i];
                if (now > z.until) { UnityEngine.Object.Destroy(z.go); _zones.RemoveAt(i); continue; }
                if (z.item == Item.StinkBomb) z.go.transform.Rotate(0, 20f * Time.deltaTime, 0);
                if (now < z.tick) continue;
                z.tick = now + 0.25f;
                foreach (var n in _npcs)
                {
                    if (!Alive(n)) continue;
                    var flat = n.transform.position - z.pos; flat.y = 0;
                    if (flat.magnitude > z.radius) continue;
                    var id = SafeId(n);
                    switch (z.item)
                    {
                        case Item.StinkBomb:
                            if (z.done.Add(id))
                            {
                                NpcReact.Play(n, "npc_gag");
                                var victim = n;
                                Later(2.6f, () => { try { victim.SetPanicked_Client(); } catch { } });
                                NpcSay(n, "FLUST", "SCARED", "INDIG");
                                Bubble(n, Pick("UGH! What's that SMELL?!", "*gag*", "Who farted?!", "I'm gonna puke!"));
                            }
                            break;
                        case Item.ItchingPowder:
                            if (z.done.Add(id))
                            {
                                try { n.SetUnsettled(12f); } catch { }
                                NpcReact.Play(n, "npc_itch");
                                NpcSay(n, "FLUST", "INDIG");
                                Bubble(n, Pick("It ITCHES! It ITCHES!", "*scratch scratch*", "Get it off me!"), 4f);
                            }
                            break;
                        case Item.Marbles:
                        case Item.Banana:
                            if (z.done.Add(id))
                            {
                                NpcReact.Slip(n);
                                NpcSay(n, "BUMPR", "INDIG");
                                Offense(6f, n);
                                if (z.item == Item.Banana) z.until = 0;           // a peel only gets one victim
                            }
                            break;
                    }
                }
            }
        }

        void StepLifts(float now)
        {
            for (int i = _lifts.Count - 1; i >= 0; i--)
            {
                var (t, until, basePos) = _lifts[i];
                if (t == null) { _lifts.RemoveAt(i); continue; }
                float left = until - now;
                if (left <= 0) { t.localPosition = basePos; _lifts.RemoveAt(i); continue; }
                float k = Mathf.Sin(Mathf.Clamp01(1f - left / 0.9f) * Mathf.PI);
                t.localPosition = basePos + Vector3.up * 0.35f * k;
            }
        }

        // ================================================================== ambient Bullworth behaviour
        void Ambient(Player me, float now)
        {
            if (now < _chatterAt) return;
            foreach (var n in _npcs)
            {
                if (!Alive(n)) continue;
                float d = Dist(n, me);
                if (d > 4f || d < 0.4f) continue;
                bool holdsGrudge = _hitBy.TryGetValue(SafeId(n), out var t) && now - t < 300f;
                if (holdsGrudge) { NpcSay(n, "TAUN", "JEER", "INDIG"); Bubble(n, Pick("You again?!", "I'm watching you, Hopkins.", "Jerk.")); }
                else if (d < 1.1f) NpcSay(n, "BUMPR", "BUMPV", "BUMPF");
                else if (_trouble > 60f) NpcSay(n, "SCARED", "DONTHIT", "TAUN");
                else if (_rng.NextDouble() < 0.5) NpcSay(n, "CHATTER", "GREE", "CONVG");
                else continue;
                _chatterAt = now + 9f + (float)_rng.NextDouble() * 10f;
                return;
            }
            _chatterAt = now + 1f;
        }

        // ================================================================== timers + HUD
        readonly List<(float at, Action a)> _later = new();
        void Later(float secs, Action a) => _later.Add((Time.time + secs, a));
        public void RunTimers()
        {
            for (int i = _later.Count - 1; i >= 0; i--)
                if (Time.time >= _later[i].at) { var a = _later[i].a; _later.RemoveAt(i); try { a(); } catch (Exception e) { Trace($"timer: {e.Message}"); } }
        }

        string Pick(params string[] s) => s[_rng.Next(s.Length)];
    }
}
