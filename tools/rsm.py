"""Decode a Bully SE .rsm speech line (from speech.py) to 16-bit PCM .wav.
An .rsm is an XACT sound bank ("SDBK") padded to 2048, then an XACT wave bank ("WBND") holding one MS-ADPCM entry.
WBND: magic, u32 version, u32 headerVersion, 5 x (u32 offset, u32 length) segments (offsets relative to WBND):
  [0] bank data, [1] entry metadata, [2] seek tables, [3] entry names, [4] wave data.
Entry metadata: u32 flagsAndDuration, u32 format, u32 playOffset, u32 playLength, ...
Format bits: tag 0-1 (2 = ADPCM), channels 2-4, sample rate 5-22, block align 23-30 (ADPCM: bytes = (align + 22) * channels).
python rsm.py <in.rsm> <out.wav>
"""
import struct, sys, wave
import numpy as np

COEF = [(256, 0), (512, -256), (0, 0), (192, 64), (240, 0), (460, -208), (392, -232)]
ADAPT = [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230]


def parse(b):
    w = b.index(b"WBND")
    seg = struct.unpack_from("<10I", b, w + 12)
    meta = w + seg[2]
    _, fmt, off, length = struct.unpack_from("<4I", b, meta)
    tag, ch, rate, align = fmt & 3, (fmt >> 2) & 7, (fmt >> 5) & 0x3FFFF, (fmt >> 23) & 0xFF
    data = b[w + seg[8] + off: w + seg[8] + off + length]
    return tag, ch, rate, (align + 22) * ch, data


def msadpcm_mono(data, block):
    out = []
    for p in range(0, len(data) - 6, block):
        blk = data[p:p + block]
        if len(blk) < 7:
            break
        pred = blk[0]
        if pred > 6:
            pred = 0
        c1, c2 = COEF[pred]
        delta, s1, s2 = struct.unpack_from("<hhh", blk, 1)
        out += [s2, s1]
        for byte in blk[7:]:
            for nib in (byte >> 4, byte & 15):
                sn = nib - 16 if nib & 8 else nib
                pr = (s1 * c1 + s2 * c2) >> 8
                v = max(-32768, min(32767, pr + sn * delta))
                out.append(v)
                s2, s1 = s1, v
                delta = max(16, (ADAPT[nib] * delta) >> 8)
    return np.array(out, np.int16)


if __name__ == "__main__":
    b = open(sys.argv[1], "rb").read()
    tag, ch, rate, block, data = parse(b)
    print(f"tag {tag} channels {ch} rate {rate} block {block} bytes {len(data)}")
    assert tag == 2 and ch == 1, "only mono MS-ADPCM handled"
    pcm = msadpcm_mono(data, block)
    with wave.open(sys.argv[2], "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate); w.writeframes(pcm.tobytes())
    print(f"{len(pcm)} samples = {len(pcm) / rate:.2f}s, peak {np.abs(pcm.astype(int)).max()}, rms {np.sqrt((pcm.astype(float) ** 2).mean()):.0f}")
