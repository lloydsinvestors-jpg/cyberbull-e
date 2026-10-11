"""Bully SE sound effects -> assets/out/sfx.bytes (embedded in the mod).
Sound banks are Audio/xact/<Bank>.SEA: a cue table, an XACT sound bank (SDBK), then an XACT wave bank (WBND) whose waves
are named (segment 3 = names when the bank's entryNameElementSize > 0) and stored mono MS-ADPCM (decoder in rsm.py).
sfx.bytes: "SFX1", u32 count, count x (u16 name len, name, u32 rate, u32 samples, int16[samples]).
python export_sfx.py            (the PICKS below)
"""
import struct
from pathlib import Path
import numpy as np
from rsm import msadpcm_mono
import paths

# bank -> the waves the mod uses (Vehicles.cs engines / crashes, Kit.cs weapons / impacts)
PICKS = {
    "Scooter": ["VespaEnigineStart", "VespaEngine", "VespaIdleSML2", "VespaEngineOff16k", "ScooterAcelSML", "CartCrash05EQS"],
    "GoKart01": ["K1_EngIdle", "K1_EngMedEQ", "K1_EngFstEQ", "K1_EngStopSML16", "K1_EngStartSML", "CartCrash06EQSML", "CartDmge03SML", "TireWipeoutSkidSML16k"],
    "Spudgun": ["SPUDGUN_FIRE_02A", "SpudCannonFire5A_2", "SPUD_BODYHIT_01", "SPUD_BODYHIT_02", "SPUD_BODYHIT_03"],
    "Btlrokt": ["BTL_ROCKET_FIRE_01", "BTL_ROCKET_FIRE_03", "BTL_ROCKET_FIRE_04", "BTLROCKET_IMPACT_01a", "BTLROCKET_IMPACT_02a", "BTLROCKET_IMPACT_03a"],
    "Slingsht": ["SLINGSHOT_FIRE", "SLINGSHOT_BODYHIT_01", "SLINGSHOT_BODYHIT_02", "SLINGSHOT_BODYHIT_04", "SLINGSHOT_WALLHIT_02", "SLINGSHOT_WALLHIT_03"],
    "Stinkbmb": ["STINKBOMB_EXPLODE_01b", "STINKBOMB_HISS_02a11SML", "PlateBreakSmall3"],
    "Glass": ["WINDOW_BREAK2", "PlateBreak2", "GlassCrack01EQedit2"],
    "Chrybmb": ["CHERRYBOMB_FUSE_LOOP216k", "CHERRYBOMB_EXPLODE_01a", "CHERRYBOMB_EXPLODE_02a", "CHERRYBOMB_EXPLODE_04b"],
    "Egg": ["EGG_IMPACT_01", "EGG_IMPACT_02", "EGG_IMPACT_03"],
    "Itch": ["PowderHit1", "PowderHit2"],
    "Marbles": ["MarbleDrops1", "MarbleDrops2", "MarbleDrops3"],
    "Banana": ["BannaPeel01_8va", "BananaSplats02"],
    "DrnkMcn": ["OpenCan_16k", "DrinkAhh_1SML"],
    "Fight": ["GENERIC_THROW_03", "GENERIC_THROW_04", "GENERIC_THROW_05"],
}


def waves(bank):
    b = (paths.bully() / "Audio" / "xact" / f"{bank}.SEA").read_bytes()
    w = b.index(b"WBND")
    seg = struct.unpack_from("<10I", b, w + 12)
    bd = w + seg[0]
    cnt, = struct.unpack_from("<I", b, bd + 4)
    es, ns = struct.unpack_from("<2I", b, bd + 72)
    for i in range(cnt):
        _, fmt, off, ln = struct.unpack_from("<4I", b, w + seg[2] + i * es)
        name = b[w + seg[6] + i * ns: w + seg[6] + (i + 1) * ns].split(b"\0")[0].decode()
        tag, ch, rate, align = fmt & 3, (fmt >> 2) & 7, (fmt >> 5) & 0x3FFFF, (fmt >> 23) & 0xFF
        data = b[w + seg[8] + off: w + seg[8] + off + ln]
        yield name, tag, ch, rate, (align + 22) * ch, data


def main():
    out = []
    for bank, names in PICKS.items():
        got = {}
        for name, tag, ch, rate, block, data in waves(bank):
            if name in names and name not in got:
                assert tag == 2 and ch == 1, (bank, name, tag, ch)
                got[name] = (rate, msadpcm_mono(data, block))
        for n in names:
            if n not in got:
                raise SystemExit(f"missing {bank}/{n}")
            rate, pcm = got[n]
            out.append((n, rate, pcm))
            print(f"{bank:9s} {n:28s} {rate:6d} Hz {len(pcm) / rate:5.2f} s peak {np.abs(pcm.astype(int)).max()}")
    buf = bytearray(b"SFX1") + struct.pack("<I", len(out))
    for n, rate, pcm in out:
        e = n.encode()
        buf += struct.pack("<H", len(e)) + e + struct.pack("<II", rate, len(pcm)) + pcm.astype("<i2").tobytes()
    paths.OUT.mkdir(parents=True, exist_ok=True)
    (paths.OUT / "sfx.bytes").write_bytes(bytes(buf))
    print("wrote sfx.bytes", len(buf), "bytes,", len(out), "sounds")


if __name__ == "__main__":
    main()
