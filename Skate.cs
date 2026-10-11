// Bully skateboarding on Schedule I's skateboard (Austin, 2026-10-09: "now add the bully skateboarding mechanics").
// Schedule I's board keeps doing the physics (hover, lean, jump build, push); on top, in Bully mode:
//   * Bully's own board (SK8Board.nif) replaces the board mesh, fitted to it and leaning with it
//   * Jimmy rides with Bully's skateboard animations (Skateboard.agr): riding stance, kick-push, crouch while charging
//     a jump, the ollie in the air
//   * Bully's controls: Shift / pad A kick-push, Space / pad B jump (hold to charge, Schedule I's own), R / pad Y off
using System;
using System.Collections.Generic;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Skating;
using UnityEngine;

namespace BullyLink
{
    public class Skate
    {

        Skateboard _board;
        GameObject _bully;
        // v0.21 (Austin: "skateboarding is broken the trucks are to the left of the board and jimmy isnt standing directly
        // over either of them"): Jimmy's feet, fitted onto Schedule I's skater's feet each frame (LateFit)
        Transform _lFoot, _rFoot, _feetOf, _hips;
        // v0.21.4: the riding stance's hips, learned relative to the board (position + the angle a hip axis makes with the
        // nose), so every other move keeps the body exactly there
        Vector3 _hipsOnBoard; float _hipsAngle; int _hipsAxis = -1; bool _stanceKnown;
        bool _alongX;
        readonly List<Renderer> _hidden = new();
        bool _airborne, _pushAnim;
        float _pushUntil;
        public Action<string> Trace = _ => { };
        public static bool Riding;          // read by the camera hook and BullyInput

        AnimLayer _layer;                // Jimmy's animation layer (which skate clip is playing, for LateFit)

        public void Update(Player me, AnimLayer anim)
        {
            _layer = anim;
            Skateboard sk = null;
            try { if (me.IsSkating) sk = me.ActiveSkateboard; } catch { }
            Riding = sk != null;
            if (sk == null) { if (_board != null) Detach(anim); return; }
            if (sk != _board) Attach(sk);
            foreach (var hr in _hidden) if (hr != null && !hr.forceRenderingOff) hr.forceRenderingOff = true;   // v0.21.1: the game re-shows its own

            if (BullyInput.KeyDown(BullyInput.VK_LSHIFT) || BullyInput.PadDown(BullyInput.A))
            {   // Bully: tap Shift / pad A to kick-push
                try { sk.Push(); } catch (Exception e) { Trace($"skate push: {e.Message}"); }
                _pushUntil = Time.time + 0.6f;
            }

            if (anim == null) return;
            bool grounded = true; float build = 0f; bool pushing = false;
            try { grounded = sk.isGrounded; build = sk.JumpBuildAmount; pushing = sk.IsPushing; } catch { }
            if (!grounded)
            {
                if (!_airborne) anim.Play("sk_ollie", speed: 1.2f, fullBody: true, withRoot: true);   // pop
                _airborne = true;
                return;
            }
            if (_airborne && anim.Playing == "sk_ollie" && anim.T01 < 0.95f) return;               // let the landing finish
            _airborne = false;
            if (build > 0.05f) anim.Play("sk_crouch", freeze: 0f, fullBody: true, withRoot: true);
            else if (pushing || Time.time < _pushUntil) anim.Play("sk_push", fullBody: true, withRoot: true);
            else if (anim.Playing != "sk_push" || anim.T01 > 0.9f) anim.Play("sk_ride", freeze: 0f, fullBody: true, withRoot: true);
        }

        // the skateboard's own parts, by name. v0.21.1 took every renderer under the skateboard and swept up the RIDER too
        // (Schedule I parents the player to the board while riding: eyes, eyebrows, a 1.4 x 2.1 m 'Impostor'), so the
        // "deck" read 1.5 m and Bully's board came out huge. And 'Plane' is the deck itself (0.21 x 0.75 m, along z), not a
        // shadow card as v0.21.0 assumed.
        const float BoardSize = 1.12f;                 // Bully's board vs Schedule I's deck length (1.0 read a bit small)
        static readonly string[] BoardParts = { "Board", "Plane", "Truck", "Hanger", "Wheel" };
        static bool IsBoardPart(string n) { foreach (var p in BoardParts) if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true; return false; }

        void Attach(Skateboard sk)
        {
            Detach(null);
            _board = sk;
            try
            {
                var vis = sk.GetComponentInChildren<SkateboardVisuals>(true);
                var deck = vis != null && vis.Board != null ? vis.Board : sk.transform;
                var sp = sk.transform;
                Vector3 dLo = Vector3.one * 1e9f, dHi = Vector3.one * -1e9f; bool dAny = false;
                float wheelBottom = float.MaxValue;
                foreach (var r in sk.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !IsBoardPart(r.name)) continue;                       // the rider and anything else: untouched
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null && r.enabled)
                    {
                        Vector3 lo = Vector3.one * 1e9f, hi = Vector3.one * -1e9f;
                        var mb = mf.sharedMesh.bounds;
                        for (int c = 0; c < 8; c++)
                        {
                            var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                            var lp = sp.InverseTransformPoint(r.transform.TransformPoint(corner));
                            lo = Vector3.Min(lo, lp); hi = Vector3.Max(hi, lp);
                        }
                        bool flat = r.name.StartsWith("Plane", StringComparison.OrdinalIgnoreCase) || r.name.StartsWith("Board", StringComparison.OrdinalIgnoreCase);
                        // the deck: the flat part near the board (a stray copy far off, like the 0.60 m wheel seen at 18:13, is skipped)
                        if (flat && Mathf.Abs((lo.x + hi.x) / 2) < 0.3f && Mathf.Abs((lo.z + hi.z) / 2) < 0.3f) { dLo = Vector3.Min(dLo, lo); dHi = Vector3.Max(dHi, hi); dAny = true; }
                        if (r.name.StartsWith("Wheel", StringComparison.OrdinalIgnoreCase) && Mathf.Abs((lo.x + hi.x) / 2) < 0.3f) wheelBottom = Mathf.Min(wheelBottom, lo.y);
                    }
                    r.forceRenderingOff = true;
                    _hidden.Add(r);
                }
                _bully = PropLib.Make("SK8Board");
                if (_bully == null) { Trace("skate: no SK8Board model"); return; }
                _bully.transform.SetParent(deck, false);
                var size = dHi - dLo;
                bool alongX = dAny && size.x > size.z * 1.5f;
                _alongX = alongX;
                _bully.transform.rotation = Quaternion.LookRotation(alongX ? sp.right : sp.forward, sp.up);
                if (dAny)
                {
                    if (wheelBottom == float.MaxValue) wheelBottom = dLo.y - 0.08f;
                    float siLen = Mathf.Max(size.x, size.z);
                    // v0.21.3 (Austin: "alittle too small a tad bit bigger"): 12% over Schedule I's deck length
                    _bully.transform.localScale = Vector3.one * (siLen * BoardSize / 1.041f) / Mathf.Max(0.001f, deck.lossyScale.x);
                    var centre = sp.TransformPoint(new Vector3((dLo.x + dHi.x) / 2, wheelBottom, (dLo.z + dHi.z) / 2));
                    Vector3 blo = Vector3.one * 1e9f, bhi = Vector3.one * -1e9f; bool any2 = false;
                    foreach (var r in _bully.GetComponentsInChildren<Renderer>(true))
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf == null || mf.sharedMesh == null) continue;
                        var mb = mf.sharedMesh.bounds;
                        for (int c = 0; c < 8; c++)
                        {
                            var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                            var q = sp.InverseTransformPoint(r.transform.TransformPoint(corner));
                            blo = Vector3.Min(blo, q); bhi = Vector3.Max(bhi, q); any2 = true;
                        }
                    }
                    if (any2) _bully.transform.position += centre - sp.TransformPoint(new Vector3((blo.x + bhi.x) / 2, blo.y, (blo.z + bhi.z) / 2));
                    Trace($"skate: Bully board on: deck {size} (length {siLen:F2} m, Bully board {siLen * BoardSize:F2} m, runs along {(alongX ? "x" : "z")}), wheels at {wheelBottom:F3}; {_hidden.Count} board parts hidden");
                }
                else Trace($"skate: no deck part found ({_hidden.Count} board parts hidden); Bully board left at its own size");
            }
            catch (Exception e) { Trace($"skate attach: {e.Message}"); }
        }

        void Detach(AnimLayer anim)
        {
            foreach (var r in _hidden) if (r != null) r.forceRenderingOff = false;
            _hidden.Clear();
            if (_bully != null) UnityEngine.Object.Destroy(_bully);
            _bully = null;
            _board = null;
            _stanceKnown = false; _hipsAxis = -1;
            Riding = false;
            _airborne = false;
            if (anim != null && anim.Playing != null && anim.Playing.StartsWith("sk_")) anim.Stop();
        }

        public void Off(AnimLayer anim) { if (_board != null) Detach(anim); }

        float _fitLog;
        /// <summary>After Jimmy's pose is final (Mod.OnLateUpdate): stand him where Schedule I's own skater stands. Its feet
        /// ARE on the board, so Jimmy is turned until the line between his feet matches the line between its feet (the smaller
        /// turn, so a goofy / regular mismatch can't spin him round), slid so his feet's middle is on theirs, and lifted so his
        /// lower foot is level with theirs. v0.21.0 aimed at a measured deck centre instead; the measurement was off and it
        /// shoved him 0.40 m (F10 18:10: Schedule I's feet diagonal across the board, Jimmy's in a line 0.33 m ahead).</summary>
        public void LateFit(Transform jimmyRoot, Animator siAnim)
        {
            if (!Riding || _board == null || jimmyRoot == null || siAnim == null) return;
            if (_feetOf != jimmyRoot)
            {
                _feetOf = jimmyRoot; _lFoot = _rFoot = _hips = null; _stanceKnown = false; _hipsAxis = -1;
                foreach (var t in jimmyRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "Root L Foot") _lFoot = t;
                    else if (t.name == "Root R Foot") _rFoot = t;
                    else if (t.name == "Root") _hips = t;
                }
            }
            var sl = siAnim.GetBoneTransform(HumanBodyBones.LeftFoot); var sr = siAnim.GetBoneTransform(HumanBodyBones.RightFoot);
            if (_lFoot == null || _rFoot == null || _hips == null || sl == null || sr == null) return;
            var up = Vector3.up;
            Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
            var board = _board.transform;
            var nose = Flat(_alongX ? board.right : board.forward);
            if (nose.sqrMagnitude < 1e-4f) return;
            string clip = _layer?.Playing;
            bool riding = clip == null || clip == "sk_ride";
            Vector3 HipAxis(int k) => k == 0 ? _hips.right : k == 1 ? _hips.up : _hips.forward;

            if (riding || !_stanceKnown)
            {
                // the riding stance: both feet on the deck. v0.21.2: his foot line along the board, left foot toward the
                // nose (Bully rides regular), slid / lifted onto Schedule I's skater's spot.
                var jv = Flat(_lFoot.position - _rFoot.position);
                float yaw = 0f;
                if (jv.sqrMagnitude > 0.0025f)
                {
                    yaw = Vector3.SignedAngle(jv, nose, up);
                    jimmyRoot.RotateAround((_lFoot.position + _rFoot.position) * 0.5f, up, yaw);
                }
                var siMid = (sl.position + sr.position) * 0.5f;
                var jMid = (_lFoot.position + _rFoot.position) * 0.5f;
                var flat = Flat(siMid - jMid);
                if (flat.magnitude > 0.5f) flat = flat.normalized * 0.5f;
                float lift = Mathf.Clamp((Mathf.Min(sl.position.y, sr.position.y) + 0.015f) - Mathf.Min(_lFoot.position.y, _rFoot.position.y), -0.3f, 0.3f);
                jimmyRoot.position += flat + up * lift;
                // learn where the hips sit and face on the board in this stance
                if (riding)
                {
                    if (_hipsAxis < 0)
                    {   // the hip bone's most horizontal local axis carries its heading
                        float best = -1f;
                        for (int k = 0; k < 3; k++) { float h = Flat(HipAxis(k)).magnitude; if (h > best) { best = h; _hipsAxis = k; } }
                    }
                    _hipsOnBoard = board.InverseTransformPoint(_hips.position);
                    _hipsAngle = Vector3.SignedAngle(nose, Flat(HipAxis(_hipsAxis)), up);
                    _stanceKnown = true;
                }
                if (Time.time > _fitLog) { _fitLog = Time.time + 5f; Trace($"skate: riding stance: foot line along the board (turned {yaw:F0} deg), on the skater's spot (slid {flat.magnitude:F2} m, {lift:+0.00;-0.00} m up)"); }
                return;
            }

            // v0.21.4 (Austin: "look at skate for the skateboarding positioning he keeps turning 180 to push"): pushing,
            // crouching, the ollie. The feet leave the deck (the push foot swings past the front one, flipping the foot line,
            // and the fit turned him 180 to follow it). Like Skate (and real skating): the body stays sideways on the board,
            // only the legs work. The hips are held at the riding stance's spot and heading on the board.
            float now = Vector3.SignedAngle(nose, Flat(HipAxis(_hipsAxis)), up);
            float turn = Mathf.DeltaAngle(now, _hipsAngle);
            jimmyRoot.RotateAround(_hips.position, up, turn);
            var want = board.TransformPoint(_hipsOnBoard);
            jimmyRoot.position += want - _hips.position;
            if (Time.time > _fitLog) { _fitLog = Time.time + 5f; Trace($"skate: {clip}: hips held in the riding stance (turned {turn:F0} deg)"); }
        }
    }
}
