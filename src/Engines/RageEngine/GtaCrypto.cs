using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GameAssetExplorer.Engines.RageEngine;

/// <summary>
/// The two block ciphers that guard RPF7 tables of contents, plus the two hashes RAGE uses.
///
/// Ported from CodeWalker's GTACrypto.cs and GTA5Hash (MIT, Copyright (c) 2015 Neodymium).
/// See third_party/NOTICE-CodeWalker.md. Restructured to work on spans instead of allocating
/// a pair of 16-byte arrays per block, because a full GTA V mount runs this over every
/// archive's whole TOC.
///
/// NG is NOT AES and shares no code with it: 17 rounds of a table-driven SP network, keyed by
/// 101 x 272-byte subkey blocks and 17 x 16 x 256 uint32 tables that live inside gta5.exe.
/// Only tables of contents are NG-encrypted. Asset bodies inside an archive are plaintext
/// (CodeWalker flags IsEncrypted for .ysc scripts alone), so they need deflate and nothing else.
/// </summary>
public static class GtaCrypto
{
    // ── AES ───────────────────────────────────────────────────────────────────

    /// <summary>AES-256-ECB, no padding, over length - (length % 16). The tail passes through.</summary>
    public static byte[] DecryptAes(byte[] data, byte[] key)
    {
        var buffer  = (byte[])data.Clone();
        int aligned = data.Length - data.Length % 16;
        if (aligned == 0) return buffer;

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Key     = key;
        aes.BlockSize = 128;
        aes.Mode    = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var dec = aes.CreateDecryptor();
        dec.TransformBlock(buffer, 0, aligned, buffer, 0);
        return buffer;
    }

    // ── NG ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Picks the per-file NG subkey. The index is derived from the LUT-substituted hash of the
    /// name, NOT from joaat, and the name is used with its original casing.
    /// </summary>
    public static byte[] GetNgKey(string name, uint length, GtaKeys keys)
    {
        uint hash   = Gta5Hash(name, keys.HashLut);
        uint keyIdx = (hash + length + (101 - 40)) % 0x65;
        return keys.NgKeys[keyIdx];
    }

    public static byte[] DecryptNg(byte[] data, string name, uint length, GtaKeys keys)
        => DecryptNg(data, GetNgKey(name, length, keys), keys.NgDecryptTables);

    public static byte[] DecryptNg(byte[] data, byte[] key, uint[][][] tables)
    {
        if (key.Length != GtaKeys.NgKeySize)
            throw new ArgumentException($"NG subkey must be {GtaKeys.NgKeySize} bytes, got {key.Length}.", nameof(key));

        // 272 bytes = 68 uints = 17 subkeys of 4. Read explicitly little-endian rather than
        // Buffer.BlockCopy so the port isn't silently host-endian dependent.
        var keyUints = new uint[GtaKeys.NgKeySize / 4];
        for (int i = 0; i < keyUints.Length; i++)
            keyUints[i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4, 4));

        var result = (byte[])data.Clone();   // any trailing partial block passes through in plaintext
        int blocks = data.Length / 16;
        for (int b = 0; b < blocks; b++)
            DecryptNgBlock(result.AsSpan(b * 16, 16), keyUints, tables);

        return result;
    }

    private static void DecryptNgBlock(Span<byte> block, uint[] key, uint[][][] tables)
    {
        Span<byte> scratch = stackalloc byte[16];

        // Rounds 0, 1 and 16 use the straight column mapping; 2..15 use the shifted one.
        RoundA(block, scratch, key, 0, tables[0]);
        RoundA(scratch, block, key, 1, tables[1]);

        // Ping-pong: an even k leaves the result in scratch, an odd k leaves it in block,
        // so after k = 15 the live buffer is block again.
        for (int k = 2; k <= 15; k++)
        {
            if ((k & 1) == 0) RoundB(block, scratch, key, k, tables[k]);
            else              RoundB(scratch, block, key, k, tables[k]);
        }

        RoundA(block, scratch, key, 16, tables[16]);
        scratch.CopyTo(block);
    }

    private static void RoundA(ReadOnlySpan<byte> s, Span<byte> d, uint[] key, int round, uint[][] t)
    {
        int k = round * 4;
        uint x1 = t[0][s[0]]   ^ t[1][s[1]]   ^ t[2][s[2]]   ^ t[3][s[3]]   ^ key[k];
        uint x2 = t[4][s[4]]   ^ t[5][s[5]]   ^ t[6][s[6]]   ^ t[7][s[7]]   ^ key[k + 1];
        uint x3 = t[8][s[8]]   ^ t[9][s[9]]   ^ t[10][s[10]] ^ t[11][s[11]] ^ key[k + 2];
        uint x4 = t[12][s[12]] ^ t[13][s[13]] ^ t[14][s[14]] ^ t[15][s[15]] ^ key[k + 3];
        Write4(d, x1, x2, x3, x4);
    }

    private static void RoundB(ReadOnlySpan<byte> s, Span<byte> d, uint[] key, int round, uint[][] t)
    {
        int k = round * 4;
        uint x1 = t[0][s[0]] ^ t[7][s[7]]   ^ t[10][s[10]] ^ t[13][s[13]] ^ key[k];
        uint x2 = t[1][s[1]] ^ t[4][s[4]]   ^ t[11][s[11]] ^ t[14][s[14]] ^ key[k + 1];
        uint x3 = t[2][s[2]] ^ t[5][s[5]]   ^ t[8][s[8]]   ^ t[15][s[15]] ^ key[k + 2];
        uint x4 = t[3][s[3]] ^ t[6][s[6]]   ^ t[9][s[9]]   ^ t[12][s[12]] ^ key[k + 3];
        Write4(d, x1, x2, x3, x4);
    }

    private static void Write4(Span<byte> d, uint x1, uint x2, uint x3, uint x4)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(d,            x1);
        BinaryPrimitives.WriteUInt32LittleEndian(d[4..],       x2);
        BinaryPrimitives.WriteUInt32LittleEndian(d[8..],       x3);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..],      x4);
    }

    // ── Hashes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The LUT-substituted hash used ONLY to pick an NG subkey. Not joaat, not interchangeable
    /// with it, and the input is not lowercased — passing a lowercased name here selects the
    /// wrong subkey and the TOC decrypts to noise with no other symptom.
    /// </summary>
    public static uint Gta5Hash(string text, byte[] lut)
    {
        unchecked   // the algorithm is defined by its wraparound
        {
            uint result = 0;
            foreach (char c in text)
            {
                uint temp = 1025 * (lut[c & 0xFF] + result);
                result = (temp >> 6) ^ temp;
            }
            return 32769 * ((9 * result >> 11) ^ (9 * result));
        }
    }

    /// <summary>
    /// Jenkins one-at-a-time over the lowercased string. This is the hash that keys archetype
    /// names to .ydr/.ytd files. Unrelated to Gta5Hash above.
    /// </summary>
    public static uint Joaat(string text)
    {
        unchecked
        {
            uint h = 0;
            foreach (char c in text)
            {
                h += char.ToLowerInvariant(c);
                h += h << 10;
                h ^= h >> 6;
            }
            h += h << 3;
            h ^= h >> 11;
            h += h << 15;
            return h;
        }
    }
}
