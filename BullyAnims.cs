// Bully's own animations on Jimmy (slingshot aim/fire, overhand throw, spud gun, planting a prank, the wedgie), decoded
// from Austin's install (tools/agr.py -> tools/export_anims.py -> anims.bytes, embedded). They're keyframes for Jimmy's
// own skeleton, so they play bone-for-bone with no retargeting. They layer over Retarget (Schedule I's walk/run):
// upper-body clips leave the legs walking; full-body clips take everything but the hips' placement.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BullyLink
{
    public class BullyClip
    {
        public string Name;
        public float Duration;
        public bool Loop;
        public Dictionary<string, (float[] t, Quaternion[] q)> Rot = new();
        public float[] RootT; public Vector3[] RootP;   // Root (hips) position, in place: seats Jimmy on bikes / the moped
    }

    public class BullyAnims
    {
        static Dictionary<string, BullyClip> _clips;

        public static Dictionary<string, BullyClip> Clips
        {
            get
            {
                if (_clips != null) return _clips;
                _clips = new Dictionary<string, BullyClip>();
                using var r = new BinaryReader(new MemoryStream(JimmyModel.Res("anims.bytes")));
                if (new string(r.ReadChars(4)) != "ANIM") throw new InvalidDataException("anims.bytes");
                r.ReadUInt32();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var c = new BullyClip { Name = Str(r), Duration = r.ReadSingle(), Loop = r.ReadByte() != 0 };
                    int nt = r.ReadUInt16();
                    for (int k = 0; k < nt; k++)
                    {
                        string bone = Str(r);
                        int nr = r.ReadUInt16();
                        var ts = new float[nr]; var qs = new Quaternion[nr];
                        for (int j = 0; j < nr; j++)
                        {
                            ts[j] = r.ReadSingle();
                            qs[j] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        }
                        int np = r.ReadUInt16();
                        var pt = new float[np]; var pp = new Vector3[np];
                        for (int j = 0; j < np; j++) { pt[j] = r.ReadSingle(); pp[j] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
                        if (np > 0 && bone == "Root") { c.RootT = pt; c.RootP = pp; }
                        if (nr > 0) c.Rot[bone] = (ts, qs);
                    }
                    _clips[c.Name] = c;
                }
                return _clips;
            }
        }

        static string Str(BinaryReader r) => System.Text.Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));

        public static Quaternion Sample((float[] t, Quaternion[] q) tr, float t01)
        {
            var ts = tr.t; var qs = tr.q;
            if (ts.Length == 1 || t01 <= ts[0]) return qs[0];
            for (int i = 1; i < ts.Length; i++)
                if (t01 <= ts[i])
                {
                    float u = ts[i] <= ts[i - 1] ? 0 : (t01 - ts[i - 1]) / (ts[i] - ts[i - 1]);
                    return Quaternion.Slerp(qs[i - 1], qs[i], u);
                }
            return qs[ts.Length - 1];
        }
    }

    public static class RootSample
    {
        public static Vector3 At(BullyClip c, float t01)
        {
            var ts = c.RootT; var ps = c.RootP;
            if (ts.Length == 1 || t01 <= ts[0]) return ps[0];
            for (int i = 1; i < ts.Length; i++)
                if (t01 <= ts[i]) { float u = ts[i] <= ts[i - 1] ? 0 : (t01 - ts[i - 1]) / (ts[i] - ts[i - 1]); return Vector3.Lerp(ps[i - 1], ps[i], u); }
            return ps[ts.Length - 1];
        }
    }

    /// <summary>Plays one Bully clip at a time on Jimmy, blended over whatever Retarget just posed.</summary>
    public class AnimLayer
    {
        static readonly HashSet<string> Upper = new()
        {
            "Root Spine1", "Root Spine2", "Root Neck", "Root Head",
            "Root L Clavicle", "Root L UpperArm", "Root L Forearm", "Root L Hand", "Root L Finger0", "Root L Finger1", "Root L Finger11",
            "Root R Clavicle", "Root R UpperArm", "Root R Forearm", "Root R Hand", "Root R Finger0", "Root R Finger1", "Root R Finger11",
        };
        static readonly HashSet<string> NotFull = new() { "Dummy", "Root", "ARROW" };

        readonly Dictionary<string, Transform> _bones;
        BullyClip _clip;
        float _time, _speed = 1f, _weight, _fadeIn = 0.12f, _fadeOut = 0.18f;
        bool _hold, _full, _stopping;
        public string Playing => _clip != null && !_stopping ? _clip.Name : null;
        /// <summary>A one-shot clip (a punch, a hit reaction) is still running: don't cut it with a walk.</summary>
        public float Weight => _weight;
        public bool Busy => _clip != null && !_stopping && !_clip.Loop && _time < _clip.Duration;
        /// <summary>Clips are keyed to Jimmy's hip height; a taller Bully adult (Cast.cs) scales the Root's placement.</summary>
        public float RootScale = 1f;
        public float T01 => _clip == null ? 0 : Mathf.Clamp01(_time / _clip.Duration);

        public AnimLayer(Dictionary<string, Transform> bones) { _bones = bones; }

        public static readonly HashSet<string> LeftArm = new()
            { "Root L Clavicle", "Root L UpperArm", "Root L Forearm", "Root L Hand", "Root L Finger0", "Root L Finger1", "Root L Finger11" };

        float _freeze = -1f;
        HashSet<string> _only;
        public bool Frozen => _clip != null && !_stopping && _freeze >= 0;

        /// <param name="freeze">hold this one moment of the clip (0..1) indefinitely, e.g. a carry pose</param>
        /// <param name="only">play on just these bones (e.g. the left arm), leaving the rest to Schedule I's animation</param>
        bool _withRoot, _rootPos;
        static readonly Quaternion Flip = Quaternion.Euler(0f, 180f, 0f);

        public void Play(string name, bool holdEnd = false, float speed = 1f, bool fullBody = false, float freeze = -1f, HashSet<string> only = null, bool withRoot = false, bool rootPos = false)
        {
            if (!BullyAnims.Clips.TryGetValue(name, out var c)) return;
            // Already playing this clip the same way: keep going. (Until v0.9.2 a looping clip called every frame -- the
            // bike's pedal cycle, the skateboard push -- restarted at frame 0 each time and never moved.)
            if (_clip == c && !_stopping && _freeze == freeze && _only == only && _full == fullBody && _withRoot == withRoot && _rootPos == rootPos) return;
            _clip = c; _time = 0; _speed = speed; _hold = holdEnd || freeze >= 0; _full = fullBody; _stopping = false;
            _freeze = freeze; _only = only; _withRoot = withRoot; _rootPos = rootPos;
        }

        public void Stop() { if (_clip != null) _stopping = true; }

        /// <summary>Change the playback speed of the running clip without restarting it (pedal cadence).</summary>
        public void SetSpeed(float s) => _speed = s;

        /// <summary>Call after Retarget.Apply() every frame.</summary>
        public void Apply(float dt)
        {
            if (_clip == null) return;
            _time += dt * _speed;
            bool ended = _freeze < 0 && !_clip.Loop && _time >= _clip.Duration;
            if (ended && !_hold) _stopping = true;
            if (_clip.Loop) _time %= Mathf.Max(0.01f, _clip.Duration);
            _weight = _stopping ? _weight - dt / _fadeOut : Mathf.Min(1f, _weight + dt / _fadeIn);
            if (_weight <= 0) { _clip = null; _weight = 0; return; }
            float t01 = _freeze >= 0 ? _freeze : Mathf.Clamp01(_time / _clip.Duration);
            if (_rootPos && _clip.RootP != null && _bones.TryGetValue("Root", out var rootBone))
            {
                var p = RootSample.At(_clip, t01);
                p = new Vector3(-p.x, p.y, -p.z) * RootScale;        // Bully's animation frame faces the other way
                rootBone.localPosition = _weight >= 1f ? p : Vector3.Lerp(rootBone.localPosition, p, _weight);
            }
            foreach (var kv in _clip.Rot)
            {
                if (_only != null ? !_only.Contains(kv.Key) : _full ? (NotFull.Contains(kv.Key) && !(_withRoot && kv.Key == "Root")) : !Upper.Contains(kv.Key)) continue;
                if (!_bones.TryGetValue(kv.Key, out var b)) continue;
                var q = BullyAnims.Sample(kv.Value, t01);
                if (kv.Key == "Root") q = Flip * q;                  // ditto for the Root's own rotation (withRoot clips)
                b.localRotation = _weight >= 1f ? q : Quaternion.Slerp(b.localRotation, q, _weight);
            }
        }
    }
}
