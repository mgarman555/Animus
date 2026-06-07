using GameAssetExplorer.Core.Services;

namespace GameAssetExplorer.Engines.NaughtyDog;

/// <summary>
/// Naughty Dog .pak reader — faithful port of alphaZomega's fmt_nd_pak.py PakFile class
/// (Session 1: foundation only — header, page table, fixup table, type-string discovery).
///
/// Reference: C:\Tools\Noesis\plugins\python\fmt_nd_pak.py (v1.53, April 2023)
/// Mesh / texture / skeleton parsing comes in Sessions 2–4.
/// </summary>
public class NdPakReader
{
    public enum NdGame { U4, TLL, TLOU2, TLOUP1, Unknown }

    /// <summary>One entry in the pak's page table — { fileOffset, size, flags }.</summary>
    public readonly record struct PageEntry(uint FileOffset, uint Size, uint Flags);

    /// <summary>One discovered ResItem with its name + type tag and offsets.</summary>
    public readonly record struct PakEntry(
        string Type,
        string ItemName,
        int    PageStart,           // pageEntries[pageOfItem].FileOffset
        int    ResItemOffset,       // offset within that page where the ResItem begins
        int    PageIndex);

    /// <summary>One PointerFixup mapping a source address to (target page, source byte address).</summary>
    public readonly record struct PointerFixup(int TargetPageIndex, int SourceByteAddr);

    public byte[] Data { get; }
    public NdGame Game { get; private set; } = NdGame.Unknown;
    public bool   IsTLOU2  => Game == NdGame.TLOU2;
    public bool   IsTLOUP1 => Game == NdGame.TLOUP1;

    /// <summary>Magic at offset 0; one of 2681 (0xA79), 2685 (0xA7D), 68217, 68221, 2147486329.</summary>
    public uint Magic { get; private set; }

    /// <summary>For TLOU2 / TLOUP1 this is 48; for U4/TLL it is 32.</summary>
    public int ResItemPaddingSz { get; private set; } = 32;

    public List<PageEntry> Pages { get; } = new();

    /// <summary>Maps source byte address (in this pak's data) to (target page, address of the page-id field).</summary>
    public Dictionary<int, PointerFixup> PointerFixups { get; } = new();

    public List<PakEntry> Entries { get; } = new();

    /// <summary>First GEOMETRY_1 record found, or null. (TLOU2 character paks have exactly one.)</summary>
    public PakEntry? GeoEntry { get; private set; }

    /// <summary>First JOINT_HIERARCHY record found, or null.</summary>
    public PakEntry? JointEntry { get; private set; }

    /// <summary>VRAM_DESC records keyed by their texture hash (uint64 at +56 inside the desc).</summary>
    public Dictionary<ulong, PakEntry> VramByHash { get; } = new();

    public NdPakReader(byte[] data, NdGame gameHint = NdGame.Unknown)
    {
        Data = data;
        Game = gameHint;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Read the pak header, page table, fixup table, and walk every page header
    /// entry to discover all ResItems (GEOMETRY_1, VRAM_DESC, JOINT_HIERARCHY, etc.).
    /// </summary>
    public bool ReadHeader()
    {
        if (Data.Length < 0x20) { Log.Warn($"NdPakReader: data too small ({Data.Length} bytes)"); return false; }

        // 0x00: m_magic
        Magic = R32(0);
        // Per fmt_nd_pak.py: accepted magics are 2681, 2685, 68217, 68221, 2147486329
        if (Magic != 2681 && Magic != 2685 && Magic != 68217 && Magic != 68221 && Magic != 2147486329)
        {
            Log.Warn($"NdPakReader: unrecognized magic 0x{Magic:X8}");
            return false;
        }

        // 0x04: m_hdrSize
        // 0x08: m_pakLoginTableIdx (page index where login table lives)
        // 0x0C: m_pakLoginTableOffset
        uint hdrSize             = R32(0x04);
        uint pakLoginTableIdx    = R32(0x08);
        uint pakLoginTableOffset = R32(0x0C);

        // 0x10: m_pageCt
        // 0x14: m_pPakPageEntryTable
        uint pageCt              = R32(0x10);
        uint pPakPageEntryTable  = R32(0x14);

        // 0x18: m_numPointerFixUpPages
        // 0x1C: m_pointerFixUpTableOffset
        uint pointerFixUpTableOffset = R32(0x1C);

        // TLOUP1 has 3 extra header fields after 0x2C; we don't need them yet.
        bool tlouP1Magic = (Magic == 2685 || Magic == 68221);
        if (tlouP1Magic && Game == NdGame.Unknown) Game = NdGame.TLOUP1;

        // ── Page table ───────────────────────────────────────────────────────
        Pages.Clear();
        if (pPakPageEntryTable + pageCt * 12u > (uint)Data.Length)
        {
            Log.Warn($"NdPakReader: page table OOB (offset={pPakPageEntryTable}, count={pageCt})");
            return false;
        }
        for (int i = 0; i < pageCt; i++)
        {
            int o = (int)pPakPageEntryTable + i * 12;
            Pages.Add(new PageEntry(R32(o), R32(o + 4), R32(o + 8)));
        }

        // ── PointerFixup table ───────────────────────────────────────────────
        // Header at pointerFixUpTableOffset:
        //   uint pageEntryNumber
        //   uint dataOffset           ← absolute byte offset where the entries begin
        //   uint numLoginPageEntries  ← how many fixups to read
        PointerFixups.Clear();
        if (pointerFixUpTableOffset + 12 > (uint)Data.Length)
        {
            Log.Warn("NdPakReader: fixup table header OOB");
            return false;
        }
        uint fixupDataOffset      = R32((int)pointerFixUpTableOffset + 4);
        uint numLoginPageEntries  = R32((int)pointerFixUpTableOffset + 8);

        if (fixupDataOffset + numLoginPageEntries * 8u > (uint)Data.Length)
        {
            Log.Warn($"NdPakReader: fixup data OOB (offset={fixupDataOffset}, count={numLoginPageEntries})");
            return false;
        }

        for (int i = 0; i < numLoginPageEntries; i++)
        {
            int o = (int)fixupDataOffset + i * 8;
            ushort page1Idx     = R16(o);          // source page (where the pointer field lives)
            ushort page2Idx     = R16(o + 2);      // target page (what the pointer resolves into)
            uint   pointerOffs  = R32(o + 4);      // offset within the source page

            if (page1Idx >= Pages.Count || page2Idx >= Pages.Count) continue;

            // Source absolute byte address = pointerOffs + pages[page1Idx].FileOffset
            int sourceAddr = (int)(pointerOffs + Pages[page1Idx].FileOffset);
            // Address of the page-id field (page2Idx itself) is `o`; fmt_nd_pak stores `bs.tell()-6` after reading, equivalent to `o`
            PointerFixups[sourceAddr] = new PointerFixup(page2Idx, o);
        }

        // ── Determine TLOU2 vs U4/TLL via the PakLoginTable's first item ─────
        // fmt_nd_pak: dialogOptions.isTLOU2 = (readUIntAt(bs, pakLoginTableItemStart+32) == 74565)
        if (Game == NdGame.Unknown)
        {
            if (pakLoginTableIdx < Pages.Count)
            {
                int loginItemStart = (int)Pages[(int)pakLoginTableIdx].FileOffset + (int)pakLoginTableOffset;
                if (loginItemStart + 36 <= Data.Length && R32(loginItemStart + 32) == 74565)
                    Game = NdGame.TLOU2;
                else
                    Game = NdGame.U4;   // best-effort default; TLL also lands here
            }
        }

        ResItemPaddingSz = (Game == NdGame.TLOU2 || Game == NdGame.TLOUP1) ? 48 : 32;

        // ── Walk every page's header to enumerate ResItems ───────────────────
        Entries.Clear();
        VramByHash.Clear();
        GeoEntry   = null;
        JointEntry = null;

        if (Game != NdGame.TLOUP1)
        {
            // U4 / TLL / TLOU2 layout:
            //   At each page+12 there's: m_pageSize (uint), pad(2), m_numPageHeaderEntries (ushort)
            //   Then `m_numPageHeaderEntries` records of:
            //     uint64 m_namePtr     (relative to page start)
            //     uint   m_resItemOffset
            //     uint   pad
            //   At m_resItemOffset (page-relative) the ResItem starts:
            //     uint64 m_itemNameOffset
            //     uint64 m_itemTypeOffset
            for (int p = 0; p < Pages.Count; p++)
            {
                int start = (int)Pages[p].FileOffset;
                if (start + 18 > Data.Length) continue;

                // (skipping m_pageSize at +12, +16 pad)
                ushort numPageHeaderEntries = R16(start + 18);
                int    cursor              = start + 20;

                for (int ph = 0; ph < numPageHeaderEntries; ph++)
                {
                    if (cursor + 16 > Data.Length) break;
                    long  pageEntryNamePtr = (long)R64(cursor);
                    uint  resItemOffset    = R32(cursor + 8);
                    cursor += 16;

                    int itemAbs = (int)resItemOffset + start;
                    if (itemAbs + 16 > Data.Length) continue;

                    long  itemNameOffset = (long)R64(itemAbs);
                    long  itemTypeOffset = (long)R64(itemAbs + 8);
                    string itemName = ReadStringAt(start + (int)itemNameOffset);
                    string itemType = ReadStringAt(start + (int)itemTypeOffset);

                    var entry = new PakEntry(
                        Type: itemType,
                        ItemName: itemName,
                        PageStart: start,
                        ResItemOffset: (int)resItemOffset,
                        PageIndex: p);

                    Entries.Add(entry);
                    DispatchEntry(entry);
                }
            }
        }
        else
        {
            // TLOUP1 path uses the PakLoginTable + StringIDs. We don't currently support
            // TLOUP1 character paks; skip this branch for v1 — a future session can
            // port the StringID lookup table if needed.
            Log.Info("NdPakReader: TLOUP1 layout detected — login-table walk not yet ported (Session 1 covers TLOU2 only)");
        }

        return true;
    }

    // ── ResItem dispatch (port of fmt_nd_pak.checkResItem) ───────────────────

    private void DispatchEntry(PakEntry entry)
    {
        switch (entry.Type)
        {
            case "GEOMETRY_1":
                GeoEntry ??= entry;
                break;

            case "JOINT_HIERARCHY":
                JointEntry ??= entry;
                break;

            case "VRAM_DESC":
            {
                int descBase = entry.PageStart + entry.ResItemOffset;
                if (IsTLOU2) descBase += 16;   // fmt_nd_pak: TLOU2 adds 16 bytes here

                if (descBase + 64 > Data.Length) break;
                ulong texHash = R64(descBase + 56);

                // Only the FIRST occurrence of each hash wins — matches fmt_nd_pak's behaviour
                // where vrams[texHash] is set once per pak.
                if (!VramByHash.ContainsKey(texHash))
                    VramByHash[texHash] = entry;
                break;
            }
        }
    }

    /// <summary>
    /// Resolve a 64-bit "pointer" stored at the given byte address.
    /// Returns the absolute byte offset within Data, or null if the value is 0
    /// (or, optionally, treats 0 as a real pointer for TLOUP1 zero-condition cases).
    /// Mirrors fmt_nd_pak's PakFile.readPointerFixup.
    /// </summary>
    public long? ReadPointerFixup(int address, bool zeroIsValid = false)
    {
        if (address + 8 > Data.Length) return null;
        long offset = (long)R64(address);
        if (offset > 0 || zeroIsValid)
        {
            if (PointerFixups.TryGetValue(address, out var fix))
            {
                if (fix.TargetPageIndex < Pages.Count)
                    return offset + Pages[fix.TargetPageIndex].FileOffset;
            }
            // No fixup at this address — the value isn't actually a pointer in this pak.
            // (Silent: callers like NdMeshExtractor probe many candidate offsets and false
            // negatives are expected. Use ReadPointerFixupVerbose if you want the warning.)
            return null;
        }
        return offset;
    }

    /// <summary>Like ReadPointerFixup but logs a warning when the fixup is missing.</summary>
    public long? ReadPointerFixupVerbose(int address, string label)
    {
        var v = ReadPointerFixup(address);
        if (v is null && address + 8 <= Data.Length && (long)R64(address) > 0)
            Log.Warn($"NdPakReader: {label} fixup missing at 0x{address:X}");
        return v;
    }

    /// <summary>
    /// Diagnostic dump — logs everything we discovered so we can compare against
    /// what Noesis's fmt_nd_pak.py reports for the same file.
    /// </summary>
    public void LogDiagnostics(string label)
    {
        Log.Info($"NdPakReader[{label}]: magic=0x{Magic:X}  game={Game}  pages={Pages.Count}  fixups={PointerFixups.Count}  resItems={Entries.Count}  vrams={VramByHash.Count}  geo={(GeoEntry != null)}  joint={(JointEntry != null)}");

        // Counts per type
        var byType = Entries.GroupBy(e => e.Type)
                            .OrderByDescending(g => g.Count())
                            .Take(20);
        foreach (var g in byType)
            Log.Info($"NdPakReader[{label}]:   {g.Count(),5}  × {g.Key}");

        if (GeoEntry is { } geo)
        {
            int riBase = geo.PageStart + geo.ResItemOffset;
            int countAddr = riBase + ResItemPaddingSz + 8;
            if (countAddr + 4 <= Data.Length)
                Log.Info($"NdPakReader[{label}]:   GEOMETRY_1 @ page {geo.PageIndex}, riOff=0x{geo.ResItemOffset:X}, numSubmeshDesc={R32(countAddr)}");
        }
    }

    // ── Primitive readers (little-endian) ────────────────────────────────────

    private uint   R32(int o) => BitConverter.ToUInt32(Data, o);
    private ushort R16(int o) => BitConverter.ToUInt16(Data, o);
    private ulong  R64(int o) => BitConverter.ToUInt64(Data, o);

    private string ReadStringAt(int o)
    {
        if (o < 0 || o >= Data.Length) return string.Empty;
        int end = o;
        while (end < Data.Length && Data[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(Data, o, end - o);
    }
}
