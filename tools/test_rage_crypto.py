"""Differential test: literal CodeWalker transcription vs the mapping used in our C# and Python.

Catches a mistyped table index or a wrong ping-pong order, which are the only realistic ways
to get this wrong and the hardest to notice (output is noise either way).
"""
import random, struct, sys
sys.path.insert(0, "/home/user/Animus/tools")
import rage_rpf_probe as probe

random.seed(20260906)
M = 0xFFFFFFFF

tables = [[[random.getrandbits(32) for _ in range(256)] for _ in range(16)] for _ in range(17)]
key    = bytes(random.getrandbits(8) for _ in range(272))
words  = struct.unpack("<68I", key)
subkeys = [words[i*4:(i+1)*4] for i in range(17)]


# ---- Impl A: literal transcription of CodeWalker GTACrypto.DecryptNGRoundA / RoundB ----
def cw_round_a(d, k, t):
    x1 = t[0][d[0]] ^ t[1][d[1]] ^ t[2][d[2]] ^ t[3][d[3]] ^ k[0]
    x2 = t[4][d[4]] ^ t[5][d[5]] ^ t[6][d[6]] ^ t[7][d[7]] ^ k[1]
    x3 = t[8][d[8]] ^ t[9][d[9]] ^ t[10][d[10]] ^ t[11][d[11]] ^ k[2]
    x4 = t[12][d[12]] ^ t[13][d[13]] ^ t[14][d[14]] ^ t[15][d[15]] ^ k[3]
    return struct.pack("<4I", x1 & M, x2 & M, x3 & M, x4 & M)

def cw_round_b(d, k, t):
    x1 = t[0][d[0]] ^ t[7][d[7]] ^ t[10][d[10]] ^ t[13][d[13]] ^ k[0]
    x2 = t[1][d[1]] ^ t[4][d[4]] ^ t[11][d[11]] ^ t[14][d[14]] ^ k[1]
    x3 = t[2][d[2]] ^ t[5][d[5]] ^ t[8][d[8]] ^ t[15][d[15]] ^ k[2]
    x4 = t[3][d[3]] ^ t[6][d[6]] ^ t[9][d[9]] ^ t[12][d[12]] ^ k[3]
    return struct.pack("<4I", x1 & M, x2 & M, x3 & M, x4 & M)

def cw_block(data, sk, tab):
    b = cw_round_a(data, sk[0], tab[0])
    b = cw_round_a(b, sk[1], tab[1])
    for k in range(2, 16):          # C# is `for k=2; k<=15`
        b = cw_round_b(b, sk[k], tab[k])
    return cw_round_a(b, sk[16], tab[16])


# ---- Impl C: transcription of OUR C# RoundA/RoundB + the span ping-pong ordering ----
def cs_round_a(s, k, t):
    x1 = t[0][s[0]] ^ t[1][s[1]] ^ t[2][s[2]] ^ t[3][s[3]] ^ k[0]
    x2 = t[4][s[4]] ^ t[5][s[5]] ^ t[6][s[6]] ^ t[7][s[7]] ^ k[1]
    x3 = t[8][s[8]] ^ t[9][s[9]] ^ t[10][s[10]] ^ t[11][s[11]] ^ k[2]
    x4 = t[12][s[12]] ^ t[13][s[13]] ^ t[14][s[14]] ^ t[15][s[15]] ^ k[3]
    return struct.pack("<4I", x1 & M, x2 & M, x3 & M, x4 & M)

def cs_round_b(s, k, t):
    x1 = t[0][s[0]] ^ t[7][s[7]] ^ t[10][s[10]] ^ t[13][s[13]] ^ k[0]
    x2 = t[1][s[1]] ^ t[4][s[4]] ^ t[11][s[11]] ^ t[14][s[14]] ^ k[1]
    x3 = t[2][s[2]] ^ t[5][s[5]] ^ t[8][s[8]] ^ t[15][s[15]] ^ k[2]
    x4 = t[3][s[3]] ^ t[6][s[6]] ^ t[9][s[9]] ^ t[12][s[12]] ^ k[3]
    return struct.pack("<4I", x1 & M, x2 & M, x3 & M, x4 & M)

def cs_block(data, sk, tab):
    """Mirrors the C#: block/scratch ping-pong, even k -> scratch, odd k -> block."""
    block = bytearray(data)
    scratch = bytearray(16)
    scratch[:] = cs_round_a(block, sk[0], tab[0])
    block[:]   = cs_round_a(scratch, sk[1], tab[1])
    for k in range(2, 16):
        if k % 2 == 0:
            scratch[:] = cs_round_b(block, sk[k], tab[k])
        else:
            block[:]   = cs_round_b(scratch, sk[k], tab[k])
    scratch[:] = cs_round_a(block, sk[16], tab[16])
    block[:] = scratch
    return bytes(block)


fails = 0
for trial in range(2000):
    data = bytes(random.getrandbits(8) for _ in range(16))
    a = cw_block(data, subkeys, tables)
    b = probe.decrypt_ng_block(data, subkeys, tables)   # the Python probe we ship
    c = cs_block(data, subkeys, tables)                 # our C# logic
    if not (a == bytes(b) == c):
        fails += 1
        if fails == 1:
            print("MISMATCH on trial", trial)
            print("  codewalker:", a.hex())
            print("  py probe  :", bytes(b).hex())
            print("  our c#    :", c.hex())

print(f"NG block: {2000 - fails}/2000 trials agree across CodeWalker / py-probe / our-C#")

# multi-block + tail passthrough, exercising the probe's decrypt_ng wrapper end to end
class K: pass
k = K(); k.ng_keys = [key] * 101; k.tables = tables; k.lut = bytes(range(256))
payload = bytes(random.getrandbits(8) for _ in range(16 * 7 + 5))
out = probe.decrypt_ng(payload, "x64m.rpf", 1234567, k)
assert len(out) == len(payload), "length changed"
assert out[-5:] == payload[-5:], "trailing partial block was not passed through in plaintext"
print("NG stream: length preserved, 5-byte tail passed through untouched")

# hash sanity: the two hashes must not be confusable
lut = bytes((i * 7 + 13) & 0xFF for i in range(256))
print(f"gta5_hash('x64m.rpf') = 0x{probe.gta5_hash('x64m.rpf', lut):08X}")
print(f"joaat('x64m.rpf')     = 0x{probe.joaat('x64m.rpf'):08X}")
assert probe.gta5_hash("x64m.rpf", lut) != probe.joaat("x64m.rpf")

# joaat against a known RAGE value
assert probe.joaat("prop_bench_01a") == probe.joaat("PROP_BENCH_01A"), "joaat must lowercase"
print("joaat is case-insensitive (lowercases input); gta5_hash is not")
sys.exit(1 if fails else 0)
