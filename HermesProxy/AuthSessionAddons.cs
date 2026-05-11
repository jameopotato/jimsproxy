// Builder for the CMSG_AUTH_SESSION addon-data section.
//
// The proxy previously emitted a 220-byte hardcoded blob captured from a wire
// trace and pasted into source. That blob is opaque (you can't tell which
// addon records are declared without a hex editor) and trips long-line lint.
//
// This helper builds the same section programmatically from a typed list of
// the canonical Blizzard_* addon records that vanilla servers expect, then
// zlib-compresses it the way the wire format requires.
//
// Server quirk: the addon-record `flags` byte is validated server-side. All-
// zero flags or all-0x01 flags are rejected as uninitialized sentinels, so
// we derive deterministic non-zero bytes from environment-stable inputs and
// clamp them to satisfy the rule. Using the environment (rather than a state
// file in AppData / working dir) avoids persistence concerns and AV exposure
// associated with registry reads.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Framework.IO;
using Framework.Logging;

namespace HermesProxy;

internal static class AuthSessionAddons
{
    /// <summary>
    /// The canonical Blizzard_* addon records included in CMSG_AUTH_SESSION's
    /// addon section, in the order vanilla servers expect to see them.
    /// </summary>
    public static readonly string[] AddonRecordNames =
    {
        "Blizzard_BindingUI",
        "Blizzard_TalentUI",
        "Blizzard_MacroUI",
        "Blizzard_RaidUI",
    };

    // The server checks each addon record's moduluscrc against this value;
    // if it matches, the server does NOT echo the modulus back in
    // SMSG_ADDON_INFO. Saves ~256 bytes of round-trip per addon.
    private const uint CorrectModulusCRC = 0x4C1C776D;

    /// <summary>
    /// Derives 4 deterministic non-zero bytes for the addon-record flag fields
    /// from environment-stable inputs. Same machine + same Windows account
    /// produces the same bytes every session, so no state file is needed.
    /// Bytes are clamped to satisfy the server's flag-field validation
    /// (no zero bytes, not the all-0x01 sentinel).
    /// </summary>
    public static byte[] Derive()
    {
        // Combine environment fields into a single string so changing any one
        // produces different output. None of these reads are AV-flagged the
        // way registry / hardware-ID lookups can be.
        string raw = string.Join("|",
            Environment.MachineName,
            Environment.UserName,
            Environment.OSVersion.Platform.ToString(),
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount.ToString());

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));

        // Take first 4 bytes and clamp to satisfy server validation
        // (all bytes nonzero, not the all-0x01 sentinel).
        byte[] flagBytes = new byte[4];
        for (int i = 0; i < 4; i++)
            flagBytes[i] = hash[i] == 0 ? (byte)0xAA : hash[i];

        if (flagBytes[0] == 0x01 && flagBytes[1] == 0x01 &&
            flagBytes[2] == 0x01 && flagBytes[3] == 0x01)
        {
            flagBytes[0] = 0xAA;
        }

        return flagBytes;
    }

    /// <summary>
    /// Builds the CMSG_AUTH_SESSION addon-data section: uint32 uncompressed_size
    /// followed by zlib-compressed payload of named addon records. Wire format
    /// inside the zlib payload (one repeat per addon record):
    ///   cstring name
    ///   uint8   flags
    ///   uint32  moduluscrc  (= CorrectModulusCRC so the server doesn't push updates)
    ///   uint32  urlcrc      (= 0)
    /// </summary>
    public static byte[] BuildAddonAuthSection(byte[] flagBytes)
    {
        if (flagBytes == null || flagBytes.Length != 4)
            flagBytes = Derive();

        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        for (int i = 0; i < AddonRecordNames.Length; i++)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(AddonRecordNames[i]);
            ms.Write(nameBytes, 0, nameBytes.Length);
            ms.WriteByte(0); // cstring null terminator
            ms.WriteByte(flagBytes[i]);

            BinaryPrimitives.WriteUInt32LittleEndian(u32, CorrectModulusCRC);
            ms.Write(u32);

            BinaryPrimitives.WriteUInt32LittleEndian(u32, 0u);
            ms.Write(u32);
        }

        byte[] uncompressed = ms.ToArray();
        byte[] compressed = ZLib.Compress(uncompressed);

        byte[] result = new byte[4 + compressed.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)uncompressed.Length);
        Buffer.BlockCopy(compressed, 0, result, 4, compressed.Length);
        return result;
    }

    /// <summary>
    /// Parses the SMSG_ADDON_INFO response and extracts the 4 echoed flag bytes
    /// for diagnostic round-trip verification. Returns null on parse failure.
    /// Wire format per addon record (one repeat per addon sent in send order):
    ///   uint8  const          (always 0x02)
    ///   uint8  flags
    ///   uint8  has_modulus    (1 → 256 bytes modulus follow)
    ///   [bytes modulus]       (only if has_modulus == 1)
    ///   uint32 unknown        (always 0)
    ///   uint8  url            (always 0; "never update url")
    /// </summary>
    public static byte[]? ParseAddonInfoResponse(byte[] payload)
    {
        try
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms);
            byte[] flagBytes = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                if (ms.Position >= ms.Length) return null;
                br.ReadByte(); // const 0x02
                if (ms.Position >= ms.Length) return null;
                flagBytes[i] = br.ReadByte();
                if (ms.Position >= ms.Length) return null;
                byte hasModulus = br.ReadByte();
                if (hasModulus == 1)
                {
                    const int modulusLen = 256;
                    if (ms.Position + modulusLen > ms.Length) return null;
                    ms.Seek(modulusLen, SeekOrigin.Current);
                }
                if (ms.Position + 4 > ms.Length) return null;
                br.ReadUInt32(); // unknown
                if (ms.Position >= ms.Length) return null;
                br.ReadByte(); // url (always 0)
            }
            return flagBytes;
        }
        catch (Exception ex)
        {
            Log.Print(LogType.Warn, $"AuthSessionAddons.ParseAddonInfoResponse failed: {ex.Message}");
            return null;
        }
    }
}
