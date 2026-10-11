// Bully voice lines, read live from the player's own Bully install (nothing bundled; v0.23: the folder is the one the
// BullyLink Launcher exported from, the "bully" line of UserData\BullyLink\data\export.txt):
//   Audio/PLAYLIST/Speech.bin + Speech.lst: "Hash", u32 count, count x (u32 hash, u32 offset, u32 size); names in .lst order.
//   Each entry (.rsm) = an XACT sound bank (SDBK) then an XACT wave bank (WBND) with one mono MS-ADPCM wave (~22 kHz).
//   The table's sizes run short of the wave data (rounded down), so the wave bank header decides how much to read.
// Decoded to PCM here and played through a Unity AudioSource; if Unity's AudioClip.Create misbehaves (it's an
// Il2CppInterop stand-in in this build), falls back to Windows' own PlaySound so Jimmy still talks.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BullyLink
{
    public static class Voice
    {
        public static string BullyDir;

        /// <summary>The Bully folder the launcher's exporter read from (export.txt: "bully<TAB>path").</summary>
        static string FindBully()
        {
            var f = Path.Combine(JimmyModel.DataDir, "export.txt");
            if (!File.Exists(f)) return null;
            foreach (var line in File.ReadAllLines(f))
                if (line.StartsWith("bully\t")) return line.Substring(6).Trim();
            return null;
        }
        static Dictionary<string, List<(long off, int size, string name)>> _byGroup;   // "Jimmy_PTAUN" -> takes
        static FileStream _bin;
        static bool _unityAudioBroken, _unityAudioTried;
        static GameObject _current;          // the line playing now
        // v0.22.25 (Austin: "FIX UNCONTIOUS OR DEAD PEOPLE TALKING"): who's saying the current line, so it can be cut off
        // the moment they're knocked out or killed (callers set NextOwner just before an NPC's line)
        public static IntPtr NextOwner;
        static IntPtr _owner;
        public static void StopFor(IntPtr owner)
        {
            if (owner == IntPtr.Zero || owner != _owner || _current == null) return;
            UnityEngine.Object.Destroy(_current); _current = null; _owner = IntPtr.Zero;
        }
        static readonly System.Random Rng = new();
        public static Action<string> Trace = _ => { };

        public static bool Ready => _byGroup != null;

        public static void Load()
        {
            if (_byGroup != null) return;
            BullyDir ??= FindBully();
            if (BullyDir == null || !Directory.Exists(BullyDir)) throw new DirectoryNotFoundException($"no Bully folder ({BullyDir ?? "nothing exported yet"}): run the CyberBull-E Launcher");
            var pl = Path.Combine(BullyDir, "Audio", "PLAYLIST");
            var names = File.ReadAllLines(Path.Combine(pl, "Speech.lst"));
            _bin = new FileStream(Path.Combine(pl, "Speech.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var r = new BinaryReader(_bin);
            if (new string(r.ReadChars(4)) != "Hash") throw new InvalidDataException("Speech.bin: no Hash header");
            int n = r.ReadInt32();
            var map = new Dictionary<string, List<(long, int, string)>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n && i < names.Length; i++)
            {
                r.ReadUInt32();
                uint off = r.ReadUInt32(), size = r.ReadUInt32();
                var nm = names[i];
                int v = nm.LastIndexOf("_v", StringComparison.OrdinalIgnoreCase);
                var group = v > 0 ? nm.Substring(0, v) : Path.GetFileNameWithoutExtension(nm);
                if (!map.TryGetValue(group, out var list)) map[group] = list = new List<(long, int, string)>();
                list.Add((off, (int)size, nm));
            }
            _byGroup = map;
            Trace($"voice: {n} lines indexed from Speech.bin");
        }

        public static bool Has(string speaker, string category) => _byGroup != null && _byGroup.ContainsKey(speaker + "_" + category);

        /// <summary>Decode a random take of speaker_category ("Jimmy", "PTAUN") to mono PCM.</summary>
        public static bool TryDecode(string speaker, string category, out short[] pcm, out int rate)
        {
            pcm = null; rate = 0;
            if (_byGroup == null || !_byGroup.TryGetValue(speaker + "_" + category, out var takes) || takes.Count == 0) return false;
            var (off, size, _) = takes[Rng.Next(takes.Count)];
            byte[] b;
            lock (_bin)
            {
                _bin.Position = off;
                b = new byte[size + 65536];
                int got = _bin.Read(b, 0, b.Length);
                Array.Resize(ref b, got);
            }
            return DecodeRsm(b, out pcm, out rate);
        }

        static readonly int[] C1 = { 256, 512, 0, 192, 240, 460, 392 }, C2 = { 0, -256, 0, 64, 0, -208, -232 };
        static readonly int[] Adapt = { 230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230 };

        public static bool DecodeRsm(byte[] b, out short[] pcm, out int rate)
        {
            pcm = null; rate = 0;
            int w = IndexOf(b, (byte)'W', (byte)'B', (byte)'N', (byte)'D');
            if (w < 0) return false;
            int metaOff = BitConverter.ToInt32(b, w + 12 + 8), dataOff = BitConverter.ToInt32(b, w + 12 + 32);
            int meta = w + metaOff;
            uint fmt = BitConverter.ToUInt32(b, meta + 4);
            int playOff = BitConverter.ToInt32(b, meta + 8), playLen = BitConverter.ToInt32(b, meta + 12);
            int tag = (int)(fmt & 3), ch = (int)((fmt >> 2) & 7);
            rate = (int)((fmt >> 5) & 0x3FFFF);
            int block = ((int)((fmt >> 23) & 0xFF) + 22) * ch;
            if (tag != 2 || ch != 1 || rate < 8000) return false;
            int start = w + dataOff + playOff, end = Math.Min(b.Length, start + playLen);
            var o = new List<short>(playLen * 2 + 16);
            for (int p = start; p + 7 <= end; p += block)
            {
                int pred = Math.Min((int)b[p], 6), c1 = C1[pred], c2 = C2[pred];
                int delta = BitConverter.ToInt16(b, p + 1), s1 = BitConverter.ToInt16(b, p + 3), s2 = BitConverter.ToInt16(b, p + 5);
                o.Add((short)s2); o.Add((short)s1);
                for (int q = p + 7; q < Math.Min(p + block, end); q++)
                    for (int h = 0; h < 2; h++)
                    {
                        int nib = h == 0 ? b[q] >> 4 : b[q] & 15;
                        int sn = (nib & 8) != 0 ? nib - 16 : nib;
                        int v = Math.Clamp(((s1 * c1 + s2 * c2) >> 8) + sn * delta, -32768, 32767);
                        o.Add((short)v);
                        s2 = s1; s1 = v;
                        delta = Math.Max(16, (Adapt[nib] * delta) >> 8);
                    }
            }
            pcm = o.ToArray();
            return pcm.Length > 0;
        }

        static int IndexOf(byte[] b, byte a, byte c, byte d, byte e)
        {
            for (int i = 0; i + 3 < b.Length; i++)
                if (b[i] == a && b[i + 1] == c && b[i + 2] == d && b[i + 3] == e) return i;
            return -1;
        }

        /// <summary>Say a line from a world position (null = Jimmy's own voice, played flat).</summary>
        public static bool Say(string speaker, string category, Vector3? at = null, float volume = 1f)
        {
            try
            {
                if (!TryDecode(speaker, category, out var pcm, out var rate)) return false;
                if (!_unityAudioBroken)
                {
                    try { PlayUnity(pcm, rate, at, volume); return true; }
                    catch (Exception e) { _unityAudioBroken = true; Trace($"voice: Unity audio failed ({e.Message}); using Windows playback"); }
                }
                PlayWindows(pcm, rate);
                return true;
            }
            catch (Exception e) { Trace($"voice: {speaker}_{category} failed: {e.Message}"); return false; }
        }

        /// <summary>A cast NPC's line (Cast.cs). v0.14.1 (Austin: "the voices are overlapping make one dialoog cancil the
        /// last one so it only says 1 line at a time"): one line in the whole world at once, Jimmy's included; a new one
        /// cuts off whatever is playing.</summary>
        public static bool SayAt(string speaker, string category, Vector3 at, float volume = 0.9f)
        {
            try
            {
                if (_unityAudioBroken || !TryDecode(speaker, category, out var pcm, out var rate)) return false;
                PlayUnity(pcm, rate, at, volume);
                return true;
            }
            catch (Exception e) { Trace($"voice: {speaker}_{category} failed: {e.Message}"); return false; }
        }

        static void PlayUnity(short[] pcm, int rate, Vector3? at, float volume)
        {
            var f = new float[pcm.Length];
            for (int i = 0; i < f.Length; i++) f[i] = pcm[i] / 32768f;
            if (!_unityAudioTried) Trace("voice: first Unity AudioClip.Create (a rebuilt stand-in in this build); if the game dies here, this is why");
            var clip = AudioClip.Create("bully_vo", f.Length, 1, rate, false);
            clip.SetData(f, 0);
            if (!_unityAudioTried) { _unityAudioTried = true; Trace("voice: Unity audio OK"); }
            // one voice at a time: a new line cuts off whatever's still playing (Jimmy's or anyone's)
            if (_current != null) { UnityEngine.Object.Destroy(_current); _current = null; }
            var go = new GameObject("BullyLink_VO");
            _current = go;
            _owner = NextOwner; NextOwner = IntPtr.Zero;
            var cam = Camera.main;
            go.transform.position = at ?? (cam != null ? cam.transform.position : Vector3.zero);
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.volume = volume;
            src.spatialBlend = at.HasValue ? 1f : 0f;
            src.minDistance = 2f;
            src.maxDistance = 30f;
            src.Play();
            UnityEngine.Object.Destroy(go, f.Length / (float)rate + 0.5f);
        }

        [DllImport("winmm.dll")] static extern bool PlaySound(byte[] data, IntPtr mod, uint flags);
        static byte[] _playing;          // keep the buffer alive while Windows plays it (SND_ASYNC)

        static void PlayWindows(short[] pcm, int rate)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int bytes = pcm.Length * 2;
            w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVEfmt ".ToCharArray());
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(bytes);
            foreach (var s in pcm) w.Write(s);
            _playing = ms.ToArray();
            PlaySound(_playing, IntPtr.Zero, 0x0004 | 0x0001 | 0x0002);   // SND_MEMORY | SND_ASYNC | SND_NODEFAULT
        }

        // ---- who speaks for Schedule I's people: each NPC keeps one Bullworth voice ----
        static readonly string[] Boys = { "Algie", "Bif", "Bo", "Bucky", "Casey", "Chad", "Clint", "Constant", "Corneli", "Damon", "Davis", "Donald",
            "Duncan", "Ethan", "Fatty", "Gord", "Gordon", "Gurney", "Hal", "Ivan", "Juri", "Justin", "Kirby", "Lance", "Lefty", "Leon", "Lucky",
            "Luis", "Melvin", "Norton", "Omar", "Otto", "Parker", "Peanut", "Pedro", "Ray", "Ricky", "Russell", "Sheldon", "Tad", "Thad", "Tom",
            "Trent", "Trevor", "Troy", "Vance", "Wade" };
        static readonly string[] Girls = { "Pinky", "Mandy", "Beatrice", "Lola", "Zoe", "Angie", "Christy", "Eunice", "Melody", "Karen", "Gloria", "Bethany" };

        public static string VoiceFor(string npcId, bool female)
        {
            var pool = female ? Girls : Boys;
            int h = 17;
            foreach (var c in npcId ?? "") h = h * 31 + c;
            return pool[(h & 0x7fffffff) % pool.Length];
        }

        /// <summary>An NPC line: tries each category in turn (not every kid recorded every reaction).</summary>
        public static bool NpcSay(string voice, Vector3 at, params string[] categories)
        {
            foreach (var c in categories)
                if (Has(voice, c)) return Say(voice, c, at);
            return false;
        }
    }
}
