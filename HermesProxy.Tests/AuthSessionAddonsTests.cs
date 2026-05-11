using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Framework.IO;
using HermesProxy;
using Xunit;

namespace HermesProxy.Tests;

// Wire-format coverage for the CMSG_AUTH_SESSION addon-data section builder
// and the matching SMSG_ADDON_INFO response parser. The section carries four
// canonical Blizzard_* addon records (BindingUI / TalentUI / MacroUI / RaidUI)
// — these tests verify the proxy emits the section in the exact format vanilla
// servers expect, parses the response correctly, and that Derive() produces
// deterministic non-zero flag bytes that pass server-side validation.
public class AuthSessionAddonsTests
{
    [Fact]
    public void Derive_ReturnsFourBytes()
    {
        byte[] flagBytes = AuthSessionAddons.Derive();
        Assert.Equal(4, flagBytes.Length);
    }

    [Fact]
    public void Derive_IsDeterministic_SameValueAcrossCalls()
    {
        byte[] a = AuthSessionAddons.Derive();
        byte[] b = AuthSessionAddons.Derive();
        byte[] c = AuthSessionAddons.Derive();
        Assert.Equal(a, b);
        Assert.Equal(b, c);
    }

    [Fact]
    public void Derive_PassesServerFlagFieldValidation()
    {
        // Server validation: all 4 bytes nonzero, AND at least one byte != 0x01.
        byte[] flagBytes = AuthSessionAddons.Derive();
        for (int i = 0; i < 4; i++)
            Assert.NotEqual((byte)0, flagBytes[i]);
        Assert.True(flagBytes.Any(b => b != 0x01),
            "Derive must produce at least one byte != 0x01 to pass server validation");
    }

    [Fact]
    public void BuildAddonAuthSection_DefaultDerivedFlagBytes_HasCorrectStructure()
    {
        byte[] flagBytes = AuthSessionAddons.Derive();
        byte[] section = AuthSessionAddons.BuildAddonAuthSection(flagBytes);

        Assert.True(section.Length > 4, "Section should have uncompressed size + compressed payload");
        uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(section);

        int expectedUncompressed = 0;
        foreach (var name in AuthSessionAddons.AddonRecordNames)
            expectedUncompressed += Encoding.UTF8.GetByteCount(name) + 1 + 1 + 4 + 4;
        Assert.Equal((uint)expectedUncompressed, uncompressedSize);
    }

    [Fact]
    public void BuildAddonAuthSection_PacksFlagBytesIntoRecords()
    {
        // Round-trip: build the section with known flag bytes, decompress the
        // payload, scan the records, verify each addon record's flags byte
        // matches the corresponding input byte.
        byte[] flagBytes = { 0xAB, 0xCD, 0xEF, 0x12 };
        byte[] section = AuthSessionAddons.BuildAddonAuthSection(flagBytes);

        uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(section);
        byte[] compressedPayload = section.Skip(4).ToArray();
        byte[] uncompressed = ZLib.Decompress(compressedPayload, uncompressedSize);

        using var ms = new MemoryStream(uncompressed);
        for (int i = 0; i < AuthSessionAddons.AddonRecordNames.Length; i++)
        {
            string name = ReadCString(ms);
            byte flags = (byte)ms.ReadByte();
            uint modulusCrc = ReadUInt32LE(ms);
            uint urlCrc = ReadUInt32LE(ms);

            Assert.Equal(AuthSessionAddons.AddonRecordNames[i], name);
            Assert.Equal(flagBytes[i], flags);
            Assert.Equal(0x4C1C776Du, modulusCrc); // CorrectModulusCRC
            Assert.Equal(0u, urlCrc);
        }
        Assert.Equal(uncompressed.Length, ms.Position); // no trailing bytes
    }

    [Fact]
    public void BuildAddonAuthSection_NullFlagBytes_FallsBackToDerive()
    {
        byte[] section = AuthSessionAddons.BuildAddonAuthSection(null!);
        Assert.NotNull(section);
        Assert.True(section.Length > 4);
    }

    [Fact]
    public void ParseAddonInfoResponse_ServerEcho_ExtractsFlagBytes()
    {
        // Wire-level test: construct the exact server response shape (per addon:
        // uint8 const=2, uint8 flags, uint8 has_modulus, [256B modulus],
        // uint32 unknown=0, uint8 url=0) with known flag bytes, then verify
        // ParseAddonInfoResponse extracts them.
        byte[] expected = { 0xDE, 0xAD, 0xBE, 0xEF };
        byte[] payload = BuildSyntheticServerResponse(expected, includeModulus: true);

        byte[]? actual = AuthSessionAddons.ParseAddonInfoResponse(payload);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParseAddonInfoResponse_NoModulusBytes_StillExtracts()
    {
        byte[] expected = { 0x12, 0x34, 0x56, 0x78 };
        byte[] payload = BuildSyntheticServerResponse(expected, includeModulus: false);

        byte[]? actual = AuthSessionAddons.ParseAddonInfoResponse(payload);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParseAddonInfoResponse_TruncatedPayload_ReturnsNull()
    {
        byte[] expected = { 0xAA, 0xBB, 0xCC, 0xDD };
        byte[] full = BuildSyntheticServerResponse(expected, includeModulus: true);
        byte[] truncated = full.Take(full.Length / 2).ToArray();

        Assert.Null(AuthSessionAddons.ParseAddonInfoResponse(truncated));
    }

    [Fact]
    public void ParseAddonInfoResponse_EmptyPayload_ReturnsNull()
    {
        Assert.Null(AuthSessionAddons.ParseAddonInfoResponse(Array.Empty<byte>()));
    }

    // ------- helpers -------

    private static byte[] BuildSyntheticServerResponse(byte[] flagBytes, bool includeModulus)
    {
        using var ms = new MemoryStream();
        for (int i = 0; i < 4; i++)
        {
            ms.WriteByte(0x02); // const
            ms.WriteByte(flagBytes[i]); // flags
            ms.WriteByte(includeModulus ? (byte)1 : (byte)0); // has_modulus
            if (includeModulus)
            {
                byte[] modulus = new byte[256];
                for (int b = 0; b < 256; b++) modulus[b] = (byte)(b & 0xFF);
                ms.Write(modulus, 0, 256);
            }
            ms.Write(new byte[] { 0, 0, 0, 0 }, 0, 4); // uint32 unknown = 0
            ms.WriteByte(0); // url
        }
        return ms.ToArray();
    }

    private static string ReadCString(MemoryStream ms)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = ms.ReadByte()) > 0)
            sb.Append((char)b);
        return sb.ToString();
    }

    private static uint ReadUInt32LE(MemoryStream ms)
    {
        Span<byte> buf = stackalloc byte[4];
        for (int i = 0; i < 4; i++) buf[i] = (byte)ms.ReadByte();
        return BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }
}
