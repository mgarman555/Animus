using System.Buffers.Binary;

namespace GameAssetExplorer.Engines.RageEngine;

/// <summary>
/// The four key artifacts NG decryption needs. They are not shipped with anything and are not a
/// hex string you can paste into a settings box: they live inside your own gta5.exe, and
/// CodeWalker locates them by SHA-1 hash search at startup.
///
/// The cheap way to get them is to run CodeWalker once against your install and call its
/// "save keys" path, which writes all four next to each other:
///
///   gtav_aes_key.dat            32 bytes
///   gtav_ng_key.dat             101 x 272 = 27,472 bytes
///   gtav_ng_decrypt_tables.dat  17 x 16 x 256 x 4 = 278,528 bytes
///   gtav_hash_lut.dat           256 bytes
///
/// The fourth file is the one people forget. NG subkey selection runs a LUT-substituted hash,
/// not joaat, so a three-file key set decrypts every table of contents to noise and gives you
/// no other symptom to chase.
/// </summary>
public sealed class GtaKeys
{
    public const int AesKeySize   = 32;
    public const int NgKeyCount   = 101;
    public const int NgKeySize    = 272;
    public const int NgRounds     = 17;
    public const int NgTableCols  = 16;
    public const int NgTableRows  = 256;
    public const int HashLutSize  = 256;

    public const string AesKeyFile   = "gtav_aes_key.dat";
    public const string NgKeyFile    = "gtav_ng_key.dat";
    public const string NgTablesFile = "gtav_ng_decrypt_tables.dat";
    public const string HashLutFile  = "gtav_hash_lut.dat";

    public byte[]     AesKey          { get; private init; } = Array.Empty<byte>();
    public byte[][]   NgKeys          { get; private init; } = Array.Empty<byte[]>();
    public uint[][][] NgDecryptTables { get; private init; } = Array.Empty<uint[][]>();
    public byte[]     HashLut         { get; private init; } = Array.Empty<byte>();

    /// <summary>Where these came from, for the log line on mount.</summary>
    public string SourceDirectory { get; private init; } = string.Empty;

    public static bool TryLoad(string directory, out GtaKeys? keys, out string error)
    {
        keys = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            error = $"Key directory not found: {directory}";
            return false;
        }

        string aesPath    = Path.Combine(directory, AesKeyFile);
        string ngKeyPath  = Path.Combine(directory, NgKeyFile);
        string tablesPath = Path.Combine(directory, NgTablesFile);
        string lutPath    = Path.Combine(directory, HashLutFile);

        var missing = new[] { aesPath, ngKeyPath, tablesPath, lutPath }
                          .Where(p => !File.Exists(p))
                          .Select(p => Path.GetFileName(p))
                          .ToArray();
        if (missing.Length > 0)
        {
            error = $"Missing key file(s) in {directory}: {string.Join(", ", missing)}";
            return false;
        }

        try
        {
            byte[] aes    = File.ReadAllBytes(aesPath);
            byte[] ngRaw  = File.ReadAllBytes(ngKeyPath);
            byte[] tabRaw = File.ReadAllBytes(tablesPath);
            byte[] lut    = File.ReadAllBytes(lutPath);

            if (!CheckSize(aes,    AesKeySize,                                   AesKeyFile,   ref error)) return false;
            if (!CheckSize(ngRaw,  NgKeyCount * NgKeySize,                       NgKeyFile,    ref error)) return false;
            if (!CheckSize(tabRaw, NgRounds * NgTableCols * NgTableRows * 4,     NgTablesFile, ref error)) return false;
            if (!CheckSize(lut,    HashLutSize,                                  HashLutFile,  ref error)) return false;

            var ngKeys = new byte[NgKeyCount][];
            for (int i = 0; i < NgKeyCount; i++)
            {
                ngKeys[i] = new byte[NgKeySize];
                Array.Copy(ngRaw, i * NgKeySize, ngKeys[i], 0, NgKeySize);
            }

            var tables = new uint[NgRounds][][];
            int o = 0;
            for (int r = 0; r < NgRounds; r++)
            {
                tables[r] = new uint[NgTableCols][];
                for (int c = 0; c < NgTableCols; c++)
                {
                    var row = new uint[NgTableRows];
                    for (int k = 0; k < NgTableRows; k++, o += 4)
                        row[k] = BinaryPrimitives.ReadUInt32LittleEndian(tabRaw.AsSpan(o, 4));
                    tables[r][c] = row;
                }
            }

            keys = new GtaKeys
            {
                AesKey          = aes,
                NgKeys          = ngKeys,
                NgDecryptTables = tables,
                HashLut         = lut,
                SourceDirectory = directory,
            };
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed reading key files from {directory}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Looks in the configured directory first, then the usual places a CodeWalker key dump
    /// ends up, so the common case needs no configuration at all.
    /// </summary>
    public static bool TryLoadFromKnownLocations(string? configured, out GtaKeys? keys, out string error)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(appData, "GameAssetExplorer", "RageKeys"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Keys"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "Keys"));

        var errors = new List<string>();
        foreach (var dir in candidates)
        {
            if (TryLoad(dir, out keys, out string err)) return true;
            errors.Add(err);
        }

        keys = null;
        error = "No usable RAGE key set found. Run CodeWalker once against your GTA V install and "
              + $"save its keys, then put the four .dat files in {candidates[1]}. Tried:{Environment.NewLine}"
              + string.Join(Environment.NewLine, errors.Select(e => "  " + e));
        return false;
    }

    private static bool CheckSize(byte[] data, int expected, string name, ref string error)
    {
        if (data.Length == expected) return true;
        error = $"{name} is {data.Length} bytes, expected {expected}. The key dump is truncated or from a different tool.";
        return false;
    }
}
