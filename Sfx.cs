// v0.22.20 (Austin: "give sound effects to the moped and go cart for their engines and the blasters for their shots and the
// glass stink bomb for the glas breaking etc"): Bully's own sound effects, decoded from its XACT wave banks
// (tools/export_sfx.py -> sfx.bytes, embedded) and played through Unity audio like the voice lines (Voice.cs).
//   * Play: a one-shot at a world position (3D), a random pick when given several names.
//   * Loop: an engine-style looping source that follows a transform; the caller sets its clip, volume and pitch each frame.
// Clips are kept alive across scenes (HideAndDontSave) and rebuilt if Unity unloads one anyway.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BullyLink
{
    public static class Sfx
    {
        public static Action<string> Trace;
        static Dictionary<string, (int rate, float[] data)> _pcm;
        static readonly Dictionary<string, AudioClip> _clips = new();
        static readonly System.Random _rng = new();
        static bool _broken;

        static void Load()
        {
            if (_pcm != null) return;
            _pcm = new Dictionary<string, (int, float[])>();
            try
            {
                using var r = new BinaryReader(new MemoryStream(JimmyModel.Res("sfx.bytes")));
                if (new string(r.ReadChars(4)) != "SFX1") throw new InvalidDataException("sfx.bytes");
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var name = new string(r.ReadChars(r.ReadUInt16()));
                    int rate = r.ReadInt32(), len = r.ReadInt32();
                    var f = new float[len];
                    for (int k = 0; k < len; k++) f[k] = r.ReadInt16() / 32768f;
                    _pcm[name] = (rate, f);
                }
                Trace?.Invoke($"sfx: {_pcm.Count} Bully sound effects loaded");
            }
            catch (Exception e) { Trace?.Invoke($"sfx: load failed: {e.Message}"); }
        }

        public static AudioClip Clip(string name)
        {
            if (_broken) return null;
            Load();
            if (_clips.TryGetValue(name, out var c) && c != null) return c;
            if (!_pcm.TryGetValue(name, out var p)) { Trace?.Invoke($"sfx: no sound '{name}'"); return null; }
            try
            {
                c = AudioClip.Create("bully_sfx_" + name, p.data.Length, 1, p.rate, false);
                c.SetData(p.data, 0);
                c.hideFlags = HideFlags.HideAndDontSave;
                _clips[name] = c;
                return c;
            }
            catch (Exception e) { _broken = true; Trace?.Invoke($"sfx: Unity audio failed ({e.Message}); sound effects off"); return null; }
        }

        /// <summary>A one-shot at a world position (one of the names at random).</summary>
        public static void Play(Vector3 at, float volume, params string[] names) => PlayPitched(at, volume, 1f, names);

        public static void PlayPitched(Vector3 at, float volume, float pitch, params string[] names)
        {
            try
            {
                if (names == null || names.Length == 0) return;
                var clip = Clip(names[_rng.Next(names.Length)]);
                if (clip == null) return;
                var go = new GameObject("BullyLink_SFX");
                go.transform.position = at;
                var src = go.AddComponent<AudioSource>();
                float pt = pitch * (0.95f + 0.1f * (float)_rng.NextDouble());
                src.clip = clip; src.volume = volume; src.pitch = pt;
                src.spatialBlend = 1f; src.minDistance = 3f; src.maxDistance = 60f;
                src.Play();
                UnityEngine.Object.Destroy(go, clip.length / Mathf.Max(0.3f, pt) + 0.3f);
            }
            catch (Exception e) { Trace?.Invoke($"sfx: play failed: {e.Message}"); }
        }

        /// <summary>A looping source riding along with a transform (an engine).</summary>
        public class Loop
        {
            readonly GameObject _go;
            readonly AudioSource _src;
            string _name;

            public Loop(Transform follow, float maxDistance = 50f)
            {
                _go = new GameObject("BullyLink_SFXLoop");
                if (follow != null) { _go.transform.SetParent(follow, false); }
                _src = _go.AddComponent<AudioSource>();
                _src.loop = true; _src.spatialBlend = 1f; _src.minDistance = 4f; _src.maxDistance = maxDistance;
            }

            public bool Alive => _go != null && _src != null;

            /// <summary>Which loop, how loud, what pitch (a new clip restarts it).</summary>
            public void Set(string name, float volume, float pitch)
            {
                if (!Alive) return;
                if (name != _name)
                {
                    var c = Clip(name);
                    if (c == null) return;
                    _name = name; _src.clip = c; _src.Play();
                }
                else if (!_src.isPlaying) _src.Play();
                _src.volume = volume; _src.pitch = pitch;
            }

            public void Stop() { if (_go != null) UnityEngine.Object.Destroy(_go); }
        }
    }
}
