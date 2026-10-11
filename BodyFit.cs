// Hands, mouths and held things on a Bully body that wears a Schedule I pose (v0.17; Austin: "make sure when they smoke or
// drink it actually goes to their mouth"). Retargeting copies bone ROTATIONS, so a Bully arm with Bully proportions ends up
// a few cm away from where Schedule I put its own hand, and the item (hung on the hidden Schedule I hand) floats beside it.
//   * Mouth: whenever Schedule I brings a hand to its face (smoking, drinking, eating), the Bully hand is put at the same
//     spot relative to the Bully head with a two-bone IK on the arm; the hand keeps its retargeted rotation.
//   * HandItems: what the game holds is SHOWN in the Bully hand by visual twins; the game's own items are never moved
//     (v0.18.1: moving the real bat let Jimmy's swing hit himself).
using System.Collections.Generic;
using UnityEngine;

namespace BullyLink
{
    public static class BodyFit
    {
        /// <summary>Analytic two-bone IK (upper arm a, forearm b, hand c) toward t, blended by w. The elbow bends in the
        /// plane it's already in, so the pose keeps its character.</summary>
        public static void TwoBone(Transform a, Transform b, Transform c, Vector3 t, float w)
        {
            if (a == null || b == null || c == null || w <= 0f) return;
            var aLr = a.localRotation; var bLr = b.localRotation;
            var aGr = a.rotation; var bGr = b.rotation;
            Vector3 ap = a.position, bp = b.position, cp = c.position;
            float lab = (bp - ap).magnitude, lcb = (cp - bp).magnitude;
            if (lab < 1e-4f || lcb < 1e-4f) return;
            float lat = Mathf.Clamp((t - ap).magnitude, 0.01f, lab + lcb - 0.001f);
            float ac_ab0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot((cp - ap).normalized, (bp - ap).normalized), -1f, 1f));
            float ba_bc0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot((ap - bp).normalized, (cp - bp).normalized), -1f, 1f));
            float ac_at0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot((cp - ap).normalized, (t - ap).normalized), -1f, 1f));
            float ac_ab1 = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
            float ba_bc1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));
            var axis0 = Vector3.Cross(cp - ap, bp - ap);
            if (axis0.sqrMagnitude < 1e-8f) axis0 = Vector3.Cross(cp - ap, Vector3.up);
            axis0.Normalize();
            var axis1 = Vector3.Cross(cp - ap, t - ap);
            var r0 = Quaternion.AngleAxis((ac_ab1 - ac_ab0) * Mathf.Rad2Deg, Quaternion.Inverse(aGr) * axis0);
            var r1 = Quaternion.AngleAxis((ba_bc1 - ba_bc0) * Mathf.Rad2Deg, Quaternion.Inverse(bGr) * axis0);
            var r2 = axis1.sqrMagnitude > 1e-8f ? Quaternion.AngleAxis(ac_at0 * Mathf.Rad2Deg, Quaternion.Inverse(aGr) * axis1.normalized) : Quaternion.identity;
            a.localRotation = Quaternion.Slerp(aLr, aLr * r0 * r2, w);
            b.localRotation = Quaternion.Slerp(bLr, bLr * r1, w);
        }

        /// <summary>Schedule I's hand near its face -> the Bully hand at the same offset from the Bully head (the retarget
        /// keeps both heads facing the same way, so the world offset carries over). Call after the pose is final.</summary>
        public static void Mouth(Transform siHead, Transform siHand, Transform bHead, Transform bUpper, Transform bFore, Transform bHand, float reach = 0.38f)
        {
            if (siHead == null || siHand == null || bHead == null || bUpper == null || bFore == null || bHand == null) return;
            var d = siHand.position - siHead.position;
            float dist = d.magnitude;
            if (dist > reach) return;
            float w = Mathf.Clamp01((reach - dist) / 0.15f);         // eases in as the hand comes up
            var keep = bHand.rotation;
            TwoBone(bUpper, bFore, bHand, bHead.position + d, w);
            bHand.rotation = keep;
        }
    }

    /// <summary>What Schedule I has in a character's hands, SHOWN in the Bully body's hands.
    /// v0.18.1 (Austin: "swinging the bat from schedual1 hurts jimmy"): until now the game's real hand containers were
    /// moved onto the Bully hand every frame and (for Jimmy) their renderers put on layer 0 so the camera drew them. The
    /// real items carry the game's colliders and hit logic; the game keeps the player's third-person items on a layer its
    /// own melee checks skip, so a bat moved into Jimmy's swing on layer 0 could hit its owner. Now the game's items are
    /// never touched: each one gets a visual twin (its mesh + materials, no collider, layer 0) drawn where the Bully hand
    /// holds it. hideOriginals: an NPC's item is drawn by the game too (hide it); the player's isn't (the game culls it).</summary>
    /// <summary>v0.22.26 (Austin: "make it ti where all player and npc models are on the ground no floating npcs, some of
    /// them when waiting in dealing spots are off the ground"): a Bully body follows Schedule I's character root, but
    /// its legs are a different length and the game's root isn't always on the ground (NPCs parked at deal spots float),
    /// so the feet hung in the air. After the pose is final, the lower foot is measured against the real ground under the
    /// body and the whole body is moved by the difference (eased; up to 0.35 m either way; nothing when the gap is bigger,
    /// i.e. jumping / falling / on stairs mid-step).</summary>
    public class FootPlant
    {
        readonly Transform _root, _l, _r;
        readonly float _sole;
        float _off;
        public float Offset => _off;

        public FootPlant(JimmyModel body)
        {
            _root = body.Root.transform;
            body.ByName.TryGetValue("Root L Foot", out _l);
            body.ByName.TryGetValue("Root R Foot", out _r);
            // the ankle's height over the sole, measured in the bind pose the body was built in (its root is the floor)
            float scale = Mathf.Max(0.5f, _root.lossyScale.y);
            float m = _l != null && _r != null ? Mathf.Min(_l.position.y, _r.position.y) - _root.position.y : -1f;
            _sole = m > 0.03f * scale && m < 0.18f * scale ? m : 0.09f * scale;
        }

        public void Apply(Transform self, float dt, bool active)
        {
            if (_root == null || _l == null || _r == null) return;
            float want = 0f;
            if (active)
            {
                var mid = (_l.position + _r.position) * 0.5f;
                float lowest = Mathf.Min(_l.position.y, _r.position.y) - _sole;
                float best = float.MaxValue, groundY = 0f; bool found = false;
                foreach (var h in Physics.RaycastAll(new Vector3(mid.x, lowest + 0.6f, mid.z), Vector3.down, 1.2f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (h.collider == null || (self != null && h.collider.transform.IsChildOf(self))) continue;
                    var rb = h.collider.attachedRigidbody;
                    if (rb != null && !rb.isKinematic) continue;                  // loose props, ragdolls
                    if (h.distance < best) { best = h.distance; groundY = h.point.y; found = true; }
                }
                if (found)
                {
                    float d = groundY - lowest;
                    if (Mathf.Abs(d) <= 0.35f) want = d;
                }
            }
            _off = Mathf.Lerp(_off, want, 1f - Mathf.Exp(-14f * dt));
            if (Mathf.Abs(_off) > 0.002f) _root.position += Vector3.up * _off;
        }
    }

    public class HandItems
    {
        class Hand { public Transform c, siHand, bHand; public Quaternion k; public bool calibrated; }
        class Twin { public Renderer src; public GameObject go; public MeshRenderer mr; public Hand h; public bool hid; }
        readonly List<Hand> _hands = new();
        readonly List<Twin> _twins = new();
        readonly HashSet<System.IntPtr> _known = new();
        readonly bool _hideOriginals;
        readonly System.Action<string> _trace;

        public HandItems(bool hideOriginals, System.Action<string> trace = null) { _hideOriginals = hideOriginals; _trace = trace; }
        /// <summary>v0.21: no twins drawn (Jimmy on the skateboard: the board left the hand but its in-hand pieces stay live,
        /// and their twins drew trucks floating beside him).</summary>
        public bool Suspended;

        public void Add(Transform container, Transform siHand, Transform bHand)
        {
            if (container == null || siHand == null || bHand == null) return;
            _hands.Add(new Hand { c = container, siHand = siHand, bHand = bHand });
        }

        /// <summary>Learn each hand's rig difference; call while the Bully body wears a pure retarget pose.</summary>
        public void Calibrate()
        {
            foreach (var h in _hands)
                if (!h.calibrated && h.siHand != null && h.bHand != null) { h.k = Quaternion.Inverse(h.siHand.rotation) * h.bHand.rotation; h.calibrated = true; }
        }

        /// <summary>Pose the twins (every frame, after the Bully pose is final); rescan = look for new items (a few times a second).
        /// Returns the names of what's held (rescan only, else null).</summary>
        public string Sync(bool rescan)
        {
            System.Text.StringBuilder names = null;
            if (rescan)
            {
                names = new System.Text.StringBuilder();
                foreach (var h in _hands)
                {
                    if (h.c == null) continue;
                    foreach (var r in h.c.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null) continue;
                        if (r.gameObject.activeInHierarchy && r.enabled) names.Append(r.name).Append(' ');
                        if (!_known.Add(r.Pointer)) continue;
                        var mf = r.GetComponent<MeshFilter>();
                        int cols = 0;
                        try { cols = r.GetComponentsInChildren<Collider>(true).Length; } catch { }
                        if (mf == null || mf.sharedMesh == null || r.TryCast<MeshRenderer>() == null)
                        { _trace?.Invoke($"held: '{r.name}' (layer {r.gameObject.layer}, {cols} collider(s)) is not a plain mesh: no twin"); continue; }
                        var go = new GameObject("BullyLink_HeldTwin_" + r.name) { layer = 0 };
                        go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                        var mr = go.AddComponent<MeshRenderer>();
                        mr.sharedMaterials = r.sharedMaterials;
                        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                        _twins.Add(new Twin { src = r, go = go, mr = mr, h = h });
                        _trace?.Invoke($"held: '{r.name}' (game layer {r.gameObject.layer}, {cols} collider(s)) shown by a visual twin; the game's own is left alone");
                    }
                }
            }
            foreach (var t in _twins)
            {
                if (t.go == null) continue;
                var r = t.src; var h = t.h;
                bool on = !Suspended && r != null && r.gameObject.activeInHierarchy && r.enabled && h.calibrated && h.siHand != null && h.bHand != null;
                if (t.go.activeSelf != on) t.go.SetActive(on);
                if (!on) continue;
                var d = h.bHand.rotation * Quaternion.Inverse(h.siHand.rotation * h.k);   // the game's hand -> the Bully hand
                var rt = r.transform;
                t.go.transform.SetPositionAndRotation(h.bHand.position + d * (rt.position - h.siHand.position), d * rt.rotation);
                t.go.transform.localScale = rt.lossyScale;
                if (rescan) t.mr.sharedMaterials = r.sharedMaterials;
                if (_hideOriginals && !r.forceRenderingOff) { r.forceRenderingOff = true; t.hid = true; }
            }
            return names?.ToString().Trim();
        }

        public void Release()
        {
            foreach (var t in _twins)
            {
                try { if (t.go != null) Object.Destroy(t.go); } catch { }
                try { if (t.hid && t.src != null) t.src.forceRenderingOff = false; } catch { }
            }
            _twins.Clear(); _known.Clear(); _hands.Clear();
        }
    }
}
