// Schedule I's people played by Bully's adults (Austin, 2026-10-10: "pull adult npc models for the npcs in schedual 1 as
// its npcs, bring the voices too. do a 1 to 1 replacement for all the charicters in schedual 1"; auto-matched, his call).
//   * Bodies: 66 Bully adults (staff, cops, shopkeepers, carnies, townies) exported by tools/export_cast.py into
//     UserData\BullyLink\peds and loaded from disk (~38 MB, too big to embed). Each NPC wears one the way Jimmy wears the
//     player: Schedule I's body hidden, the Bully body retargeted bone for bone from the NPC's animator every frame.
//   * Casting: UserData\BullyLink.cast.txt (npc id, Bully model, voice), written the first time each NPC is seen, so
//     everyone keeps their actor for good; edit a line to recast. Police get Bully's four cops; a few named parts are
//     hand-picked (Picks); everyone else gets the least-used adult of their gender (Bully has 54 men and 12 women,
//     Schedule I more of both, so faces repeat once a pool runs out).
//   * Moves (v0.15; Austin: "map their movements and passibe behavior after the bully, still allow them to aproach and do
//     all the normal things ... they get distorted when reacting"): Schedule I's AI still walks them everywhere, but an
//     upright NPC's body plays Bully's own clips (idle / walk / run at their real speed, women Bully's girls' set, fist
//     stance and punches in a fist fight, a hit reaction sized to every blow, knockdown -> get up, cowering when
//     panicked). Sitting, lying, ragdolls and anything in their hands (a deal, a phone, a bat, a gun) stay Schedule I's
//     own pose, retargeted. NPCs swinging a weapon keep Schedule I's swing; bare fists throw Bully's jab / cross / hook.
//   * Voices: each actor brings their Bully voice. Schedule I's own lines (VOEmitter.Play: greeting, question, angry,
//     scared...) are swapped for that voice's matching Bully lines; the mod's own reactions use it too (Kit.VoiceOf).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.VoiceOver;
using UnityEngine;

namespace BullyLink
{
    public static class Cast
    {
        class Ped { public string model, file, voice, role; public bool female; public int order; }
        class Actor
        {
            public NPC npc; public string id; public Ped ped;
            public Animator anim; public AnimatorCullingMode cull;
            public JimmyModel body; public Retarget rt;
            public readonly List<Renderer> hidden = new();
            public float rehide;
            // v0.15 Bully moves
            public AnimLayer layer;
            public FootPlant plant;                 // v0.22.26: feet on the ground
            public Transform siHips;
            public float standH, speed, itemCheck, nextIdleSwap, ownSwingUntil;
            public Vector3 lastPos;
            public bool holding, female, seeded;
            public string idle, next;
            public Il2CppScheduleOne.Combat.CombatBehaviour combat;
            // v0.17 body fit: Schedule I's physical changes (JimmyEffects), its held items + hand to mouth (BodyFit)
            public JimmyEffects fx;
            public HandItems items;
            public Transform siHead, siHandL, siHandR, bHead, bUpL, bForeL, bHandL, bUpR, bForeR, bHandR;
            public float height = 1f, itemScan;
            // v0.18.3 diagnostics (Austin: punched NPCs still "blink in and out"): what the body was doing, frame by frame
            public string mode = "";
            public Vector3 push; public float pushLeft;           // v0.20: the shove a hit gives (metres still to go)
            public float watchUntil, watchNext;
        }

        public static Action<string> Trace = _ => { };
        /// <summary>The mod is on (F6): bodies and voices swapped. Off = Clear() puts Schedule I's people back.</summary>
        public static bool On;
        static readonly string UserData = MelonLoader.Utils.MelonEnvironment.UserDataDirectory;
        static readonly string CastPath = Path.Combine(UserData, "BullyLink.cast.txt");
        static List<Ped> _roster;
        static readonly Dictionary<string, (string model, string voice)> _cast = new();
        static readonly Dictionary<string, Actor> _actors = new();
        static readonly Dictionary<string, JimmyModel> _templates = new();
        static readonly HashSet<string> _failed = new();
        static float _scanAt;
        static bool _loaded;

        // named parts, by Schedule I character class (gender must match or it falls through to the pools)
        static readonly Dictionary<string, string> Picks = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Albert"] = "TE_Chemistry",       // Albert Hoover, the chemist supplier -> Dr. Watts
            ["Thomas"] = "TE_Principal",       // Thomas Benzies, the cartel boss -> Dr. Crabblesnitch
            ["Marco"] = "TE_Autoshop",         // Marco, the auto shop -> Mr. Neil (shop class)
            ["Mick"] = "TO_CSOwner_2",         // Mick, the pawn shop
            ["Ming"] = "TO_GroceryOwner",      // Mrs. Ming's restaurant -> falls through (she's a woman)
            ["Dan"] = "TO_Handy",              // Dan, the hardware store
            ["Jeremy"] = "TO_Business1",       // Jeremy, the car dealer
            ["Benji"] = "TO_Carny02",          // Benji, the first dealer
            ["Fixer"] = "TO_Associate",        // Manny the Fixer, hires your workers
            ["Oscar"] = "TO_Carny01",
            ["Salvador"] = "TO_Tattooist",
            ["Shirley"] = "TO_Carnie_female",
            ["Walter"] = "TO_Oldman2",
            ["Igor"] = "TO_Dockworker",
            ["Ray"] = "TO_RichM1",             // Ray, the realtor
        };

        // Schedule I line type -> Bully categories, tried in order (not every Bully voice recorded every one)
        static readonly Dictionary<EVOLineType, string[]> Lines = new()
        {
            [EVOLineType.Greeting] = new[] { "CONVG", "STORW", "CHATTER" },
            [EVOLineType.Command] = new[] { "WARNT", "WARNMI", "WARNC", "CONVPP" },
            [EVOLineType.Thanks] = new[] { "RESPGD", "CONVPP", "STORBB", "BYE", "CONVGR" },
            [EVOLineType.Alerted] = new[] { "BUSTING", "WARNTRU", "WARNT", "INDIG", "BUMPR" },
            [EVOLineType.Concerned] = new[] { "INDIG", "WARNMI", "CONVNS", "BUMPR" },
            [EVOLineType.Question] = new[] { "CONVQ", "CONVQR", "CONVG" },
            [EVOLineType.Hurt] = new[] { "FIGHB", "BUMPR", "DONTHIT" },
            [EVOLineType.VeryHurt] = new[] { "DONTHIT", "FIGHB", "BUMPR" },
            [EVOLineType.Annoyed] = new[] { "TAUNRES", "INDIG", "BUMPR" },
            [EVOLineType.Angry] = new[] { "TAUNAG", "TAUN", "FIGH" },
            [EVOLineType.Surprised] = new[] { "BUMPR", "INDIG" },
            [EVOLineType.Scared] = new[] { "SCARED", "DONTHIT", "FIGHB" },
            [EVOLineType.PoliceChatter] = new[] { "CHATTER", "WARNT" },
            [EVOLineType.Acknowledge] = new[] { "CONVGR", "CONVPP", "RESPGD", "CONVG" },
            [EVOLineType.Die] = new[] { "FIGHB", "BUMPR" },
            [EVOLineType.Grunt] = new[] { "BUMPR", "FIGHB" },
            [EVOLineType.No] = new[] { "CONVNR", "CONVNS", "CONVNP", "INDIG" },
        };

        // ============================================================ casting
        static void Load()
        {
            _loaded = true;
            try
            {
                var roster = Path.Combine(JimmyModel.PedDir, "roster.txt");
                if (!File.Exists(roster)) { Trace($"cast: no Bully adults at {JimmyModel.PedDir} (tools/export_cast.py); Schedule I keeps its people"); return; }
                _roster = new List<Ped>();
                foreach (var line in File.ReadAllLines(roster))
                {
                    var f = line.Split('\t');
                    if (f.Length < 5) continue;
                    _roster.Add(new Ped { model = f[0], file = f[1], female = f[2] == "f", voice = f[3], role = f[4], order = _roster.Count });
                }
                if (File.Exists(CastPath))
                    foreach (var line in File.ReadAllLines(CastPath))
                    {
                        if (line.StartsWith("#")) continue;
                        var f = line.Split('\t');
                        if (f.Length >= 3) _cast[f[0]] = (f[1], f[2]);
                    }
                Trace($"cast: {_roster.Count} Bully adults ({_roster.Count(p => p.female)} women), {_cast.Count} parts already cast");
            }
            catch (Exception e) { _roster = null; Trace($"cast: couldn't load ({e.Message})"); }
        }

        static Ped Find(string model) => _roster.FirstOrDefault(p => string.Equals(p.model, model, StringComparison.OrdinalIgnoreCase));
        static int Uses(Ped p) => _cast.Values.Count(c => string.Equals(c.model, p.model, StringComparison.OrdinalIgnoreCase));

        static bool Female(NPC n)
        {
            try { return n.Avatar.Appearance.Gender == Il2CppScheduleOne.Core.Avatar.EGender.Female; } catch { return false; }
        }

        static Ped PedFor(NPC n, string id, out string voice)
        {
            voice = null;
            if (_cast.TryGetValue(id, out var c))
            {
                var known = Find(c.model);
                if (known != null) { voice = c.voice; return known; }
            }
            bool female = Female(n);
            string cls = "";
            try { cls = n.GetIl2CppType().Name; } catch { }
            Ped pick = null;
            if (Picks.TryGetValue(cls, out var pm)) { var p = Find(pm); if (p != null && p.female == female && Uses(p) == 0) pick = p; }
            if (pick == null)
            {
                bool cop = n.TryCast<Il2CppScheduleOne.Police.PoliceOfficer>() != null;
                string pref = cop ? "cop"
                    : n.TryCast<Il2CppScheduleOne.Employees.Employee>() != null ? "worker"
                    : n.TryCast<Il2CppScheduleOne.Economy.Dealer>() != null || n.TryCast<Il2CppScheduleOne.Economy.Supplier>() != null ? "rough" : null;
                var reserved = new HashSet<string>(Picks.Values, StringComparer.OrdinalIgnoreCase);
                var pool = cop ? _roster.Where(p => p.role == "cop") : _roster.Where(p => p.female == female && p.role != "cop");
                if (!pool.Any()) pool = _roster.Where(p => p.role != "cop");
                pick = pool.OrderBy(Uses)
                           .ThenBy(p => pref != null && p.role == pref ? 0 : 1)
                           .ThenBy(p => reserved.Contains(p.model) ? 1 : 0)
                           .ThenBy(p => p.order).First();
            }
            voice = pick.voice;
            _cast[id] = (pick.model, voice);
            try
            {
                if (!File.Exists(CastPath))
                    File.WriteAllText(CastPath, "# BullyLink cast: Schedule I npc id <TAB> Bully model <TAB> Bully voice. Edit a line to recast (models + voices: UserData\\BullyLink\\peds\\roster.txt).\n");
                File.AppendAllText(CastPath, $"{id}\t{pick.model}\t{voice}\n");
            }
            catch { }
            Trace($"cast: {id} ({cls}, {(female ? "woman" : "man")}) -> {pick.model}, voice {voice}");
            return pick;
        }

        /// <summary>This NPC's Bully voice (null until they're cast).</summary>
        public static string VoiceFor(NPC n)
        {
            if (!On || n == null) return null;
            try { return _cast.TryGetValue(n.ID, out var c) ? c.voice : null; } catch { return null; }
        }

        // ============================================================ bodies
        static JimmyModel Template(Ped p)
        {
            if (_templates.TryGetValue(p.model, out var t)) return t;
            t = JimmyModel.Build(null, _ => { }, res: p.file + ".bytes", goName: "BullyLink_Cast_" + p.model, scale: 1f, keep: true);
            t.Root.SetActive(false);                 // never drawn or animated: clones are made from its bind pose
            _templates[p.model] = t;
            Trace($"cast: built {p.model} ({t.Bones.Length} bones, {t.Renderer.sharedMesh.vertexCount} verts)");
            return t;
        }

        static bool Bind(Actor a)
        {
            var av = a.npc.Avatar;
            if (av == null) return false;
            try { if (av.Ragdolled) return false; } catch { }       // calibrate from an upright NPC
            Animator anim = null;
            foreach (var x in av.GetComponentsInChildren<Animator>(true))
                if (x.avatar != null && x.avatar.isHuman) { anim = x; break; }
            if (anim == null) return false;
            var si = new Dictionary<HumanBodyBones, Transform>();
            foreach (var (hb, _) in Retarget.Pairs) { var t = anim.GetBoneTransform(hb); if (t != null) si[hb] = t; }
            if (!si.ContainsKey(HumanBodyBones.Hips)) return false;
            a.body = Template(a.ped).Clone("BullyLink_Actor_" + a.id);
            a.body.Root.SetActive(true);
            // v0.17: the Bully adult at the height of the Schedule I character (their leg lengths, before the retarget measures)
            float siLeg = 0f, bLeg = 0f;
            if (si.TryGetValue(HumanBodyBones.LeftUpperLeg, out var t0) && si.TryGetValue(HumanBodyBones.LeftLowerLeg, out var t1) && si.TryGetValue(HumanBodyBones.LeftFoot, out var t2))
                siLeg = Vector3.Distance(t0.position, t1.position) + Vector3.Distance(t1.position, t2.position);
            if (a.body.ByName.TryGetValue("Root L Thigh", out var b0) && a.body.ByName.TryGetValue("Root L Calf", out var b1) && a.body.ByName.TryGetValue("Root L Foot", out var b2))
                bLeg = Vector3.Distance(b0.position, b1.position) + Vector3.Distance(b1.position, b2.position);
            // v0.18.4 (Austin: "jimmys a bit bigger then them now"): matching Schedule I's absolute leg length shrank Bully's
            // adults below Jimmy. Keep each adult at Bully's own size and apply only Schedule I's difference from the player.
            float playerLeg = PlayerLeg();
            a.height = siLeg > 0.2f && playerLeg > 0.2f ? Mathf.Clamp(siLeg / playerLeg, 0.85f, 1.2f) : 1f;
            a.body.Root.transform.localScale = Vector3.one * a.height;
            a.rt = new Retarget(anim.transform, si, a.body.Root.transform, a.body.Humanoid());
            a.anim = anim;
            a.siHips = si[HumanBodyBones.Hips];
            si.TryGetValue(HumanBodyBones.Head, out a.siHead);
            si.TryGetValue(HumanBodyBones.LeftHand, out a.siHandL);
            si.TryGetValue(HumanBodyBones.RightHand, out a.siHandR);
            var bb = a.body.ByName;
            bb.TryGetValue("Root Head", out a.bHead);
            bb.TryGetValue("Root L UpperArm", out a.bUpL); bb.TryGetValue("Root L Forearm", out a.bForeL); bb.TryGetValue("Root L Hand", out a.bHandL);
            bb.TryGetValue("Root R UpperArm", out a.bUpR); bb.TryGetValue("Root R Forearm", out a.bForeR); bb.TryGetValue("Root R Hand", out a.bHandR);
            a.rt.Apply();                                   // one pure retarget pose to calibrate the hands against
            a.items = new HandItems(hideOriginals: true);
            try { a.items.Add(av.LeftHandContainer, a.siHandL, a.bHandL); a.items.Add(av.RightHandContainer, a.siHandR, a.bHandR); } catch { }
            a.items.Calibrate();
            try { a.fx = new JimmyEffects(av, null, anim.transform, a.body, local: false, baseScale: a.height); } catch { a.fx = null; }
            // standing hip height = leg length (thigh + shin from the bones, pose-independent; someone bound while sitting
            // would otherwise set a seated "standing" height) + a little for the hip joint and the foot
            a.standH = 0.9f;
            if (si.TryGetValue(HumanBodyBones.LeftUpperLeg, out var th) && si.TryGetValue(HumanBodyBones.LeftLowerLeg, out var sh) && si.TryGetValue(HumanBodyBones.LeftFoot, out var ft))
                a.standH = Mathf.Max(0.4f, (Vector3.Distance(th.position, sh.position) + Vector3.Distance(sh.position, ft.position)) * 1.12f);
            // v0.22.22 (Austin: "fix some of the female npcs mouths gaping open"): Bully has no jaw bone; "Root Eyes" carries
            // the lower face (on TE_Nurse its vertices run down to the chin, 1.70-1.81 m of a 1.65-1.92 m head). Every NPC
            // clip sets that bone (and EyeLids / Brow) to the rotation authored for the clip's own skeleton, and the
            // adult women's faces rest at a different angle, so the clips pulled their jaws open. Their face bones now
            // keep the model's own rest pose.
            var layerBones = a.body.ByName;
            if (a.ped.female)
            {
                layerBones = new Dictionary<string, Transform>(a.body.ByName);
                foreach (var fb in FaceBones) layerBones.Remove(fb);
            }
            a.layer = new AnimLayer(layerBones);
            a.plant = new FootPlant(a.body);
            // Bully's clips carry Jimmy's hip height: scale the Root's placement to this adult
            if (NpcReact.BindPose.TryGetValue("Root", out var jr) && a.body.ByName.ContainsKey("Root"))
            {
                int ri = Array.FindIndex(a.body.Bones, b => b.name == "Root");
                if (ri >= 0 && jr.pos.magnitude > 0.1f) a.layer.RootScale = a.body.BindPos[ri].magnitude / jr.pos.magnitude;
            }
            a.female = a.ped.female;
            a.idle = a.female ? "npcf_idle" : "npc_idle";
            a.lastPos = anim.transform.position;
            try { a.combat = a.npc.Behaviour.CombatBehaviour; } catch { }
            // the game culls animators whose renderers aren't drawn; with its body hidden the Bully body would freeze
            try { a.cull = anim.cullingMode; } catch { a.cull = AnimatorCullingMode.CullUpdateTransforms; }   // getter is an interop stand-in (stripcheck)
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            Hide(a);
            return true;
        }

        static float _playerLeg;
        /// <summary>The Schedule I player's leg length (thigh + shin), the yardstick for everyone's height.</summary>
        static float PlayerLeg()
        {
            if (_playerLeg > 0.2f) return _playerLeg;
            try
            {
                var av = Il2CppScheduleOne.PlayerScripts.Player.Local?.Avatar;
                if (av == null) return 0f;
                foreach (var an in av.GetComponentsInChildren<Animator>(true))
                {
                    if (an.avatar == null || !an.avatar.isHuman) continue;
                    Transform th = an.GetBoneTransform(HumanBodyBones.LeftUpperLeg), sh = an.GetBoneTransform(HumanBodyBones.LeftLowerLeg), ft = an.GetBoneTransform(HumanBodyBones.LeftFoot);
                    if (th != null && sh != null && ft != null) _playerLeg = Vector3.Distance(th.position, sh.position) + Vector3.Distance(sh.position, ft.position);
                    break;
                }
                if (_playerLeg > 0.2f) Trace($"cast: player leg {_playerLeg:F3} m = the height yardstick (each Bully adult keeps its own size x the Schedule I character's height / the player's)");
            }
            catch { }
            return _playerLeg;
        }

        /// <summary>Hide Schedule I's body (skin, clothes, hair, eyes), not what's in their hands or the effect particles.</summary>
        static void Hide(Actor a)
        {
            var av = a.npc.Avatar;
            if (av == null) return;
            Transform lh = null, rh = null;
            try { lh = av.LeftHandContainer; rh = av.RightHandContainer; } catch { }
            foreach (var r in av.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.forceRenderingOff) continue;
                if ((lh != null && r.transform.IsChildOf(lh)) || (rh != null && r.transform.IsChildOf(rh))) continue;
                if (r.TryCast<ParticleSystemRenderer>() != null || r.TryCast<TrailRenderer>() != null || r.TryCast<LineRenderer>() != null) continue;
                r.forceRenderingOff = true;
                a.hidden.Add(r);
            }
        }

        static void Drop(Actor a)
        {
            try { a.items?.Release(); } catch { }
            try { a.fx?.Release(); } catch { }
            foreach (var r in a.hidden) try { if (r != null) r.forceRenderingOff = false; } catch { }
            a.hidden.Clear();
            try { if (a.anim != null) a.anim.cullingMode = a.cull; } catch { }
            try { if (a.body?.Root != null) UnityEngine.Object.Destroy(a.body.Root); } catch { }
        }

        /// <summary>Call in LateUpdate after Schedule I's animators and NpcReact.</summary>
        public static void Update()
        {
            if (!On) return;
            if (!_loaded) Load();
            if (_roster == null || _roster.Count == 0) return;
            float now = Time.unscaledTime, dt = Time.deltaTime;
            if (now > _scanAt) _scanAt = now + (Scan() ? 0.25f : 2f);    // a backlog keeps scanning fast
            var cam = Camera.main;
            var camPos = cam != null ? cam.transform.position : Vector3.zero;
            List<string> gone = null;
            foreach (var kv in _actors)
            {
                var a = kv.Value;
                try
                {
                    if (a.npc == null || a.anim == null || a.body?.Root == null) { (gone ??= new()).Add(kv.Key); continue; }
                    bool live = a.anim.gameObject.activeInHierarchy;       // indoors / despawned NPCs switch their avatar off
                    if (a.body.Root.activeSelf != live) a.body.Root.SetActive(live);
                    if (!live) { a.mode = "animator-off"; if (now < a.watchUntil && now >= a.watchNext) { a.watchNext = now + 0.1f; Watch(a, camPos); } continue; }
                    if (now > a.rehide) { a.rehide = now + 2f; Hide(a); }   // outfits and accessories get rebuilt
                    // v0.18.4 (the blink, caught by the v0.18.3 recorder: "game body shown 3" after every hit): the game turns
                    // its own body back on by itself; re-hiding only every 2 s showed both bodies at once, then one snapped
                    // off. Hold the known parts hidden every frame (cheap), rescan for new ones every 2 s as before.
                    foreach (var hr in a.hidden) if (hr != null && !hr.forceRenderingOff) hr.forceRenderingOff = true;
                    var at = a.anim.transform;
                    if ((at.position - camPos).sqrMagnitude < 90f * 90f)
                    {
                        a.rt.Apply(); Moves(a, dt, now); Fit(a);
                        // standing / walking / reacting on their feet: planted (ragdolls, sitting, lying, knocked down: as posed)
                        bool upright = a.mode != null && !a.mode.StartsWith("si:ragdoll") && !a.mode.StartsWith("si:out") && !a.mode.StartsWith("si:low") && !a.mode.Contains("hit_down") && !a.mode.Contains("getup");
                        a.plant?.Apply(a.npc.transform.root, dt, upright);
                    }
                    if (now < a.watchUntil && now >= a.watchNext) { a.watchNext = now + 0.1f; Watch(a, camPos); }
                    else a.body.Root.transform.SetPositionAndRotation(at.position, at.rotation);   // far: carry the pose along
                }
                catch (Exception e) { Trace($"cast: {a.id} dropped ({e.Message})"); (gone ??= new()).Add(kv.Key); }
            }
            if (gone != null) foreach (var k in gone) { Drop(_actors[k]); _actors.Remove(k); }
        }

        /// <summary>v0.22.25: knocked out, dead or otherwise not conscious: says nothing.</summary>
        public static bool Down(NPC n)
        {
            if (n == null) return false;
            try { var h = n.Health; if (h != null && (h.IsDead || h.IsKnockedOut)) return true; } catch { }
            try { if (!n.IsConscious) return true; } catch { }
            return false;
        }

        // ============================================================ Bully moves (v0.15)
        static readonly string[] FaceBones = { "Root Eyes", "Root EyeLids", "Root Brow" };

        /// <summary>Bully's clip for this frame over the retargeted pose, or nothing (Schedule I's own pose shows).</summary>
        static void Moves(Actor a, float dt, float now)
        {
            if (Down(a.npc)) Voice.StopFor(a.npc.Pointer);           // v0.22.25: a line in progress dies with them
            var at = a.anim.transform;
            var flat = at.position - a.lastPos; flat.y = 0;
            a.lastPos = at.position;
            float v = dt > 1e-4f ? flat.magnitude / dt : 0f;
            a.speed = Mathf.Lerp(a.speed, Mathf.Min(v, 9f), 1f - Mathf.Exp(-8f * dt));

            // Schedule I's own pose: ragdolls, sitting / lying / crouching (hips low), anything in their hands, its own weapon swing
            bool rag = false;
            try { rag = a.npc.Avatar.Ragdolled; } catch { }
            bool upright = Vector3.Dot(a.siHips.position - at.position, at.up) > a.standH * 0.8f;   // world units, like standH
            if (now > a.itemCheck) { a.itemCheck = now + 0.5f; a.holding = Holding(a); }
            // v0.20 (Austin: "make it where the npc stumbles or phisically reacts to getting hit not just call the cops";
            // the recorder: after a hit they were straight into "si:holding" = the phone out, which threw the reaction away)
            // a hit reaction plays out first; only a ragdoll or a knockout beats it. Then they can dial.
            bool reacting = a.layer.Busy && a.layer.Playing != null && (a.layer.Playing.StartsWith("hit_") || a.layer.Playing.StartsWith("npc_"));
            if (!rag && a.npc.IsConscious && (reacting || a.next != null))
            {
                if (!reacting) { var nx = a.next; a.next = null; Play(a, nx, hold: nx == "hit_getup"); }
                a.mode = "react:" + a.layer.Playing;
                try { a.npc.Movement.Stop(); } catch { }
                Shove(a, dt);
                a.layer.Apply(dt);
                return;
            }
            if (rag || !upright || a.holding || now < a.ownSwingUntil || !a.npc.IsConscious)
            {
                a.mode = rag ? "si:ragdoll" : !a.npc.IsConscious ? "si:out" : a.holding ? "si:holding" : now < a.ownSwingUntil ? "si:swing" : "si:low-hips";
                a.next = null;
                if (a.layer.Playing != null) a.layer.Stop();
                a.layer.Apply(dt);
                return;
            }

            if (a.layer.Busy)                       // a punch or a hit reaction plays out
            {
                a.mode = "busy:" + a.layer.Playing;
                if (a.layer.Playing != null && (a.layer.Playing.StartsWith("hit_down") || a.layer.Playing == "hit_getup"))
                    try { a.npc.Movement.Stop(); } catch { }     // knocked down: don't get walked across the street
                a.layer.Apply(dt);
                return;
            }

            bool fighting = false;
            try { fighting = a.combat != null && a.combat.Active; } catch { }
            bool panicked = false;
            try { panicked = a.npc.IsPanicked; } catch { }

            string clip; float speed = 1f;
            if (a.speed > 2.8f) { clip = a.female ? "npcf_run" : "npc_run"; speed = Mathf.Clamp(a.speed / (a.female ? 5.77f : 4.12f), 0.7f, 1.5f); }
            else if (a.speed > 1.75f && !a.female) { clip = "npc_walkfast"; speed = Mathf.Clamp(a.speed / 2.15f, 0.7f, 1.4f); }
            else if (a.speed > 0.2f) { clip = a.female ? "npcf_walk" : "npc_walk"; speed = Mathf.Clamp(a.speed / (a.female ? 1.81f : 1.39f), 0.5f, 1.5f); }
            else if (fighting) clip = "fist_stance";
            else if (panicked) clip = "npc_cower";
            else
            {
                if (now > a.nextIdleSwap)                    // Bully's people shift their weight and look around now and then
                {
                    a.nextIdleSwap = now + UnityEngine.Random.Range(6f, 14f);
                    a.idle = a.female || UnityEngine.Random.value < 0.6f ? (a.female ? "npcf_idle" : "npc_idle") : "npc_idle2";
                }
                clip = a.idle;
            }
            a.layer.Play(clip, holdEnd: clip == "npc_cower", fullBody: true, withRoot: true, rootPos: true);
            a.mode = "bully:" + clip;
            a.layer.SetSpeed(speed);
            a.layer.Apply(dt);
        }

        /// <summary>v0.17: after the pose is final: Schedule I's hand to its mouth -> the Bully hand to the Bully mouth, the
        /// game's body effects (big head, giraffe, shrinking, particles) mirrored, and what's in their hands onto the Bully hands.</summary>
        static void Fit(Actor a)
        {
            if (a.holding)                                  // only with something in hand (a cigarette, a drink): not a face scratch
            {
                BodyFit.Mouth(a.siHead, a.siHandL, a.bHead, a.bUpL, a.bForeL, a.bHandL);
                BodyFit.Mouth(a.siHead, a.siHandR, a.bHead, a.bUpR, a.bForeR, a.bHandR);
            }
            if (a.fx != null) try { a.fx.Apply(); } catch { a.fx = null; }
            if (a.items != null) { bool rescan = Time.unscaledTime >= a.itemScan; if (rescan) a.itemScan = Time.unscaledTime + 0.5f; a.items.Sync(rescan); }
        }

        /// <summary>One line of what a just-hit NPC's bodies are doing (both the Bully one and the game's hidden one).</summary>
        static void Watch(Actor a, Vector3 camPos)
        {
            try
            {
                var av = a.npc.Avatar;
                int siShown = 0;
                if (av != null)
                    foreach (var r in av.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        if (r != null && r.enabled && !r.forceRenderingOff && r.gameObject.activeInHierarchy) siShown++;
                var br = a.body.Renderer;
                bool vis = false; try { vis = br.isVisible; } catch { }
                bool rag = false; try { rag = av.Ragdolled; } catch { }
                var at = a.anim.transform;
                float hips = Vector3.Dot(a.siHips.position - at.position, at.up) / Mathf.Max(0.01f, a.standH);
                Vector3 rootLocal = a.body.ByName.TryGetValue("Root", out var rb) ? rb.localPosition : Vector3.zero;
                Trace($"watch {a.id}: {a.mode} w{a.layer.Weight:F2} | bully body {(a.body.Root.activeInHierarchy ? "on" : "OFF")} renderer {(br.enabled ? "on" : "OFF")} visible {vis} | game animator {(a.anim.gameObject.activeInHierarchy ? "on" : "OFF")} avatar {(av != null && av.gameObject.activeInHierarchy ? "on" : "OFF")} ragdoll {rag} game body shown {siShown} | body-npc {Vector3.Distance(a.body.Root.transform.position, at.position):F2} m, hips {hips:F2} of standing, Root local {rootLocal}, cam {Vector3.Distance(camPos, at.position):F1} m");
            }
            catch (Exception e) { Trace($"watch {a.id}: {e.Message}"); }
        }

        /// <summary>Slide a hit NPC back along the blow over ~0.3 s, a step at a time, never off the walkable ground.</summary>
        static void Shove(Actor a, float dt)
        {
            if (a.pushLeft <= 0f) return;
            float stepLen = Mathf.Min(a.pushLeft, a.push.magnitude / 0.3f * dt);
            a.pushLeft -= stepLen;
            try
            {
                var at = a.npc.transform.position;
                var to = at + a.push.normalized * stepLen;
                if (a.npc.Movement.IsNPCPositionValid(to)) a.npc.Movement.Warp(to);
                else a.pushLeft = 0f;                                   // a wall / the kerb of the navmesh: stop there
            }
            catch { a.pushLeft = 0f; }
        }

        static void Play(Actor a, string clip, bool hold = false) => a.layer.Play(clip, holdEnd: hold, fullBody: true, withRoot: true, rootPos: true);

        /// <summary>Something in their hands (Schedule I's hand containers): their own pose shows (deals, phones, weapons).</summary>
        static bool Holding(Actor a)
        {
            try
            {
                var av = a.npc.Avatar;
                foreach (var c in new[] { av.LeftHandContainer, av.RightHandContainer })
                {
                    if (c == null) continue;
                    foreach (var r in c.GetComponentsInChildren<Renderer>(false))
                        if (r != null && r.enabled && r.gameObject.activeInHierarchy) return true;
                }
            }
            catch { }
            return false;
        }

        static Actor ActorOf(NPC n)
        {
            if (!On || n == null) return null;
            try { return _actors.TryGetValue(n.ID, out var a) && a.layer != null ? a : null; } catch { return null; }
        }

        /// <summary>A Bully reaction clip (NpcReact's MINI_React / SHUMIL picks, or a hit_*) straight on a cast NPC's Bully body,
        /// no double retarget. False if they're not cast (NpcReact plays it the old way).</summary>
        public static bool React(NPC n, string clip)
        {
            var a = ActorOf(n);
            if (a == null || !BullyAnims.Clips.ContainsKey(clip)) return false;
            Play(a, clip);
            a.next = clip.StartsWith("hit_down") ? "hit_getup" : null;
            _reactedAt[a.id] = Time.unscaledTime;
            return true;
        }

        /// <summary>Kit's fist moves name the reaction their blow should get (light / head / gut / down); the impact hook
        /// that follows uses it instead of guessing from the force.</summary>
        public static void NextReaction(NPC n, string clip) { try { _pending[n.ID] = (clip, Time.unscaledTime); } catch { } }
        static readonly Dictionary<string, (string clip, float at)> _pending = new();
        static readonly Dictionary<string, float> _reactedAt = new();

        static void ImpactPostfix(NPC __instance, Il2CppScheduleOne.Combat.Impact __0)   // __0: bound by position, whatever the game named it
        {
            var impact = __0;
            try
            {
                var a = ActorOf(__instance);
                if (a == null || impact == null) return;
                float now = Time.unscaledTime;
                a.watchUntil = now + 2.5f;                                                      // v0.18.3: record the next 2.5 s
                if (_reactedAt.TryGetValue(a.id, out var r) && now - r < 0.15f) return;      // NpcReact already picked one
                var type = impact.ImpactType;
                if (type == Il2CppScheduleOne.Combat.EImpactType.Explosion || type == Il2CppScheduleOne.Combat.EImpactType.Bullet) return;   // the game's ragdoll / hurt
                string clip;
                if (_pending.TryGetValue(a.id, out var p) && now - p.at < 0.3f) { clip = p.clip; _pending.Remove(a.id); }
                else
                {
                    float f = impact.ImpactForce;
                    clip = f < 0.5f ? null : f < 4f ? "hit_light" : f < 7f ? (UnityEngine.Random.value < 0.5f ? "hit_head" : "hit_gut") : f < 10f ? "hit_stagger" : "hit_down";
                }
                if (clip != null && React(__instance, clip))
                {
                    // a real shove along the blow (Schedule I moves its NPCs on its navmesh: Warp onto valid ground only)
                    var d = impact.ImpactForceDirection; d.y = 0;
                    float dist = clip == "hit_light" ? 0.12f : clip == "hit_head" || clip == "hit_gut" ? 0.25f : clip == "hit_stagger" ? 0.6f : clip == "hit_down2" ? 1.1f : clip.StartsWith("hit_down") ? 0.9f : 0f;
                    if (d.sqrMagnitude > 1e-4f && dist > 0f) { a.push = d.normalized * dist; a.pushLeft = dist; }
                }
            }
            catch { }
        }

        static void AttackPostfix(Il2CppScheduleOne.AvatarFramework.Equipping.AvatarMeleeWeapon __instance)
        {
            try
            {
                if (!On || __instance == null) return;
                var a = ActorOf(__instance.GetComponentInParent<NPC>());
                if (a == null) return;
                if (__instance.ImpactType != Il2CppScheduleOne.Combat.EImpactType.Punch) { a.ownSwingUntil = Time.unscaledTime + 1.2f; return; }   // a bat / knife: Schedule I's swing
                var moves = new[] { "fist_jab", "fist_cross", "fist_hook" };
                Play(a, moves[UnityEngine.Random.Range(0, moves.Length)]);
            }
            catch { }
        }

        /// <summary>Puts up to 3 more NPCs in Bully bodies; true if more are waiting. A first load would otherwise build
        /// every body (mesh + textures) in one frame and freeze the game for seconds.</summary>
        static bool Scan()
        {
            int built = 0;
            foreach (var n in UnityEngine.Object.FindObjectsOfType<NPC>())
            {
                if (n == null) continue;
                if (built >= 3) { Trace($"cast: {_actors.Count} NPCs in Bully bodies so far"); return true; }
                string id;
                try { id = n.ID; } catch { continue; }
                if (string.IsNullOrEmpty(id) || _actors.ContainsKey(id) || _failed.Contains(id)) continue;
                try
                {
                    var ped = PedFor(n, id, out _);
                    var a = new Actor { npc = n, id = id, ped = ped };
                    if (Bind(a)) { _actors[id] = a; built++; }
                }
                catch (Exception e) { _failed.Add(id); Trace($"cast: {id} kept Schedule I's body ({e.Message})"); }
            }
            if (built > 0) Trace($"cast: {built} more NPCs in Bully bodies ({_actors.Count} in all)");
            return false;
        }

        /// <summary>F6 off: everyone back to Schedule I's own body and voice.</summary>
        public static void Clear()
        {
            On = false;
            foreach (var a in _actors.Values) Drop(a);
            _actors.Clear();
            _failed.Clear();
        }

        /// <summary>A new scene destroyed the NPCs and their actors (the templates live on).</summary>
        public static void OnSceneChange() { _actors.Clear(); _failed.Clear(); _emitters.Clear(); _pending.Clear(); _reactedAt.Clear(); }

        // ============================================================ voices
        static readonly Dictionary<IntPtr, (NPC npc, string id)> _emitters = new();

        static bool VoicePrefix(VOEmitter __instance, object[] __args)
        {
            try
            {
                if (!On || __instance == null || __args == null || __args.Length == 0 || !(__args[0] is EVOLineType type)) return true;
                if (!Lines.TryGetValue(type, out var cats)) return true;                 // snores, thinking: the game's own
                if (!_emitters.TryGetValue(__instance.Pointer, out var who))
                {
                    var npc = __instance.GetComponentInParent<NPC>();
                    who = (npc, npc != null ? npc.ID : null);
                    _emitters[__instance.Pointer] = who;
                }
                // v0.22.25: an unconscious / dead NPC's talk lines are silenced, Bully's and the game's alike (snores and
                // other non-speech sounds aren't in Lines and stay the game's)
                if (who.npc != null && Down(who.npc)) return false;
                if (who.id == null || !_cast.TryGetValue(who.id, out var c)) return true;
                foreach (var cat in cats)
                    if (Voice.Has(c.voice, cat))
                    {
                        Voice.NextOwner = who.npc != null ? who.npc.Pointer : IntPtr.Zero;
                        return !Voice.SayAt(c.voice, cat, __instance.transform.position);   // played: skip the game's line
                    }
                return true;
            }
            catch { return true; }
        }

        // Same crash guard as the effect hooks: a marker goes down before the hook and is cleared once the game is past
        // startup; if a launch dies in between, the next launch of the same build leaves the game's voices alone.
        static readonly string Guard = Path.Combine(UserData, "BullyLink.voguard.txt");
        public static void Survived() { try { if (File.Exists(Guard)) File.Delete(Guard); } catch { } }

        public static void Install(HarmonyLib.Harmony h, string version)
        {
            try
            {
                if (File.Exists(Guard) && File.ReadAllText(Guard).Trim() == version)
                {
                    Trace($"cast: the last launch of v{version} died after the voice hook went in; Schedule I keeps its own voices this time");
                    return;
                }
                File.WriteAllText(Guard, version);
            }
            catch { }
            void Post(MethodBase m, string fn)
            {
                if (m == null) { Trace($"cast: {fn} target not found"); return; }
                var label = $"{m.DeclaringType?.Name}.{m.Name}";
                var code = FxPatches.NativeCode(m);
                if (code == IntPtr.Zero) { Trace($"cast: {label}: no native address, not hooked"); return; }
                if (FxPatches.TooSmall(code)) { Trace($"cast: {label} is too small to detour safely; not hooked"); return; }
                Trace($"cast: hooking {label} @0x{code.ToInt64():X}");
                try { h.Patch(m, postfix: new HarmonyMethod(typeof(Cast).GetMethod(fn, BindingFlags.NonPublic | BindingFlags.Static))); }
                catch (Exception e) { Trace($"cast: couldn't hook {label}: {e.Message}"); }
            }
            Post(typeof(NPC).GetMethod("ReceiveImpact", BindingFlags.Public | BindingFlags.Instance), nameof(ImpactPostfix));
            Post(typeof(Il2CppScheduleOne.AvatarFramework.Equipping.AvatarMeleeWeapon).GetMethod("Attack", BindingFlags.Public | BindingFlags.Instance), nameof(AttackPostfix));
            var prefix = new HarmonyMethod(typeof(Cast).GetMethod(nameof(VoicePrefix), BindingFlags.NonPublic | BindingFlags.Static));
            var done = new HashSet<IntPtr>();
            foreach (var m in typeof(VOEmitter).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Play") continue;
                var ps = m.GetParameters();
                if (ps.Length == 0 || ps[0].ParameterType != typeof(EVOLineType)) continue;
                var label = $"VOEmitter.Play({string.Join(", ", ps.Select(p => p.ParameterType.Name))})";
                var code = FxPatches.NativeCode(m);
                if (code == IntPtr.Zero) { Trace($"cast: {label}: no native address, not hooked"); continue; }
                if (!done.Add(code)) { Trace($"cast: {label} shares machine code with another overload; hooked once"); continue; }
                if (FxPatches.TooSmall(code)) { Trace($"cast: {label} is too small to detour safely; not hooked"); continue; }
                Trace($"cast: hooking {label} @0x{code.ToInt64():X}");
                try { h.Patch(m, prefix: prefix); }
                catch (Exception e) { Trace($"cast: couldn't hook {label}: {e.Message}"); }
            }
        }
    }
}
