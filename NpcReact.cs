// Bully's NPC reactions on Schedule I's people (Austin, 2026-10-09: "take some of the npc behaviors from bully ...
// specific animations where they fall, their ragdoll physics and when they get pranked").
// Each reaction plays one of Bully's victim animations (MINI_React, SHUMIL_01; picked from contact sheets) on a hidden
// copy of Bully's skeleton and retargets it onto the NPC's own Schedule I skeleton, fading in and out over the NPC's
// own animation. Falls use Schedule I's real ragdoll physics, pushed the way Bully knocks people down.
using System;
using System.Collections.Generic;
using Il2CppScheduleOne.NPCs;
using UnityEngine;

namespace BullyLink
{
    public static class NpcReact
    {
        class Active
        {
            public NPC npc;
            public Animator anim;
            public GameObject puppet;
            public Dictionary<string, Transform> bones;
            public Retarget rt;
            public BullyClip clip;
            public float t, fade;
            public string id;
        }

        static readonly List<Active> Playing = new();
        static readonly Stack<(GameObject go, Dictionary<string, Transform> bones)> Pool = new();
        public static Func<GameObject> SkeletonSource = () => null;   // Jimmy's root: cloned (mesh hidden) as the puppet
        public static Action<string> Trace = _ => { };
        static readonly Quaternion Flip = Quaternion.Euler(0f, 180f, 0f);
        public static Dictionary<string, (Vector3 pos, Quaternion rot)> BindPose = new();

        /// <summary>Play Bully reaction `clip` on this NPC (replaces any reaction already running on them).</summary>
        public static void Play(NPC npc, string clip)
        {
            try
            {
                if (npc == null || !BullyAnims.Clips.TryGetValue(clip, out var c)) return;
                if (Cast.React(npc, clip)) return;                   // a cast NPC wears a Bully body: the clip plays on it directly
                string id = npc.ID ?? npc.name;
                Playing.RemoveAll(a => { if (a.id == id) { Release(a); return true; } return false; });
                Animator anim = null;
                if (npc.Avatar == null) return;                      // far away / mid-spawn: no body to animate
                foreach (var a in npc.Avatar.GetComponentsInChildren<Animator>(true))
                    if (a.avatar != null && a.avatar.isHuman) { anim = a; break; }
                if (anim == null) return;
                var npcBones = new Dictionary<HumanBodyBones, Transform>();
                foreach (var (hb, _) in Retarget.Pairs)
                {
                    var t = anim.GetBoneTransform(hb);
                    if (t != null) npcBones[hb] = t;
                }
                if (!npcBones.ContainsKey(HumanBodyBones.Hips)) return;
                var (go, bones) = Take();
                if (go == null) return;
                foreach (var kv in BindPose)                         // rest pose before calibrating (the clone copies Jimmy's live pose)
                    if (bones.TryGetValue(kv.Key, out var bt)) { bt.localPosition = kv.Value.pos; bt.localRotation = kv.Value.rot; }
                go.transform.SetPositionAndRotation(anim.transform.position, anim.transform.rotation);
                var puppetHum = new Dictionary<HumanBodyBones, Transform>();
                foreach (var (hb, name) in Retarget.Pairs)
                    if (bones.TryGetValue(name, out var t)) puppetHum[hb] = t;
                var rt = new Retarget(go.transform, puppetHum, anim.transform, npcBones);
                try { npc.Movement.Stop(); } catch { }
                Playing.Add(new Active { npc = npc, anim = anim, puppet = go, bones = bones, rt = rt, clip = c, id = id });
            }
            catch (Exception e) { Trace($"npc react {clip}: {e.Message}"); }
        }

        /// <summary>Call in LateUpdate, after Schedule I's animators.</summary>
        public static void Update(float dt)
        {
            for (int i = Playing.Count - 1; i >= 0; i--)
            {
                var a = Playing[i];
                try
                {
                    if (a.npc == null || a.anim == null || !a.anim.gameObject.activeInHierarchy || !a.npc.IsConscious) { Release(a); Playing.RemoveAt(i); continue; }
                    a.t += dt;
                    float t01 = Mathf.Clamp01(a.t / a.clip.Duration);
                    float w = Mathf.Min(Mathf.Clamp01(a.t / 0.2f), Mathf.Clamp01((a.clip.Duration - a.t) / 0.3f));
                    if (a.t >= a.clip.Duration) { Release(a); Playing.RemoveAt(i); continue; }
                    a.puppet.transform.SetPositionAndRotation(a.anim.transform.position, a.anim.transform.rotation);
                    foreach (var kv in a.clip.Rot)
                        if (kv.Key != "Dummy" && kv.Key != "ARROW" && a.bones.TryGetValue(kv.Key, out var b))
                            // Bully clips' Root faces +Y, ~180 deg from the rest pose (measured on 8 clips): flip it, or the
                            // NPC's hips swing round backwards during the reaction
                            b.localRotation = kv.Key == "Root" ? Flip * BullyAnims.Sample(kv.Value, t01) : BullyAnims.Sample(kv.Value, t01);
                    a.rt.Apply(w, moveRoot: false);
                    if (a.t < a.clip.Duration * 0.8f) try { a.npc.Movement.Stop(); } catch { }
                }
                catch (Exception e) { Trace($"npc react update: {e.Message}"); Release(a); Playing.RemoveAt(i); }
            }
        }

        // ---- falls: Schedule I's ragdoll, pushed Bully-style ----
        /// <summary>Slipped on marbles / a banana peel: feet out from under them, landing on their back.</summary>
        public static void Slip(NPC npc)
        {
            try
            {
                var fwd = npc.transform.forward;
                npc.Movement.ActivateRagdoll(npc.transform.position + Vector3.up * 0.15f, fwd * 1.0f + Vector3.up * 0.9f, 9f);
            }
            catch (Exception e) { Trace($"slip: {e.Message}"); }
        }

        /// <summary>Thrown by a blast: away from it, a little up.</summary>
        public static void Blast(NPC npc, Vector3 from, float strength)
        {
            try
            {
                var d = npc.transform.position - from; d.y = 0;
                npc.Movement.ActivateRagdoll(npc.transform.position + Vector3.up * 1.0f, d.normalized + Vector3.up * 0.6f, strength);
            }
            catch (Exception e) { Trace($"blast: {e.Message}"); }
        }

        static (GameObject, Dictionary<string, Transform>) Take()
        {
            if (Pool.Count > 0) { var p = Pool.Pop(); p.go.SetActive(true); return p; }
            var src = SkeletonSource();
            if (src == null) return (null, null);
            var go = UnityEngine.Object.Instantiate(src).TryCast<GameObject>();
            if (go == null) return (null, null);
            go.name = "BullyLink_ReactSkeleton";
            UnityEngine.Object.DontDestroyOnLoad(go);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;   // just the bones (also hides a copied hand prop)
            var bones = new Dictionary<string, Transform>();
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) bones[t.name] = t;
            Trace($"npc react: new skeleton ({bones.Count} bones)");
            return (go, bones);
        }

        static void Release(Active a)
        {
            if (a.puppet == null) return;
            a.puppet.SetActive(false);
            Pool.Push((a.puppet, a.bones));
        }

        public static void Clear()
        {
            foreach (var a in Playing) Release(a);
            Playing.Clear();
        }
    }
}
