"""SHA-1 digests locating GTA V's key material inside gta5.exe.

These are digests, not keys. They identify where in your own executable the AES key, the 101 NG
subkeys, the 272 NG decrypt tables and the 256-byte hash LUT live; the key bytes themselves are
never stored here and come only from your copy of the game.

Digest values and the search method are from CodeWalker's GTAKeys.cs / HashSearch
(MIT, Copyright (c) 2015 Neodymium). See third_party/NOTICE-CodeWalker.md.

Generated from CodeWalker.Core/GameFiles/Utils/GTAKeys.cs; do not hand-edit.
"""

import base64

_H = 20   # SHA-1 digest length

# AES-256 key, 0x20 bytes
AES_KEY_B64 = (
    "oHlhKKd1cgrCBNmBn2jBcuOVLG0="
)

# hash substitution LUT, 0x100 bytes
HASH_LUT_B64 = (
    "iNN5O456bKyqi4kol75yjZ5/utQ="
)

# 101 NG subkeys, 0x110 bytes each
NG_KEYS_B64 = (
    "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"
    "TIU4OQ=="
)

# 272 NG decrypt tables, 0x400 bytes each
NG_TABLES_B64 = (
    "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"
)



def _split(b64):
    raw = base64.b64decode(b64)
    return [raw[i:i + _H] for i in range(0, len(raw), _H)]


# (label, list-of-digests, blob length in bytes, expected count)
GROUPS = [
    ("aes_key",   _split(AES_KEY_B64),   0x20,  1),
    ("hash_lut",  _split(HASH_LUT_B64),  0x100, 1),
    ("ng_keys",   _split(NG_KEYS_B64),   0x110, 101),
    ("ng_tables", _split(NG_TABLES_B64), 0x400, 272),
]

for _label, _d, _len, _n in GROUPS:
    assert len(_d) == _n, f"{_label}: {len(_d)} digests, expected {_n}"
