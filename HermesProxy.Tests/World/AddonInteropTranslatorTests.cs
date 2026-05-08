using HermesProxy.World.Server;
using Xunit;

namespace HermesProxy.Tests.World;

public class AddonInteropTranslatorTests
{
    public AddonInteropTranslatorTests()
    {
        // Settings are static; tests rely on the interop being enabled, which
        // is also the production default. Force-enable in case a prior test
        // mutated it.
        global::Framework.Settings.EnablePallyPowerInterop = true;
    }

    // -------------------------------------------------------------------
    // ASSIGN: class index +/- 1 AND skill ID via lookup.
    // 1.12: 0=WAR..7=WLK, skills 0=Wis 1=Mig 2=Salv 3=Light 4=Kings 5=Sanc
    // 1.14: 1=WAR..8=WLK, skills 1=Wis 2=Mig 3=Kings 4=Salv 5=Light 6=Sanc
    // -------------------------------------------------------------------

    [Theory]
    // Druid (class 4 modern, 3 legacy), Kings (3 modern, 4 legacy)
    [InlineData("ASSIGN Bob 4 3", "ASSIGN Bob 3 4")]
    // Mage (7 modern, 6 legacy), Wisdom (1 modern, 0 legacy)
    [InlineData("ASSIGN Sara 7 1", "ASSIGN Sara 6 0")]
    // Warlock (8 modern, 7 legacy), Sanctuary (6 modern, 5 legacy)
    [InlineData("ASSIGN Q 8 6",   "ASSIGN Q 7 5")]
    // Hunter (6 modern, 5 legacy), Salvation (4 modern, 2 legacy)
    [InlineData("ASSIGN H 6 4",   "ASSIGN H 5 2")]
    // Priest (3 modern, 2 legacy), Light (5 modern, 3 legacy)
    [InlineData("ASSIGN P 3 5",   "ASSIGN P 2 3")]
    public void TranslateOutbound_PallyPowerAssign_TranslatesClassAndSkill(string input, string expected)
    {
        Assert.Equal(expected, AddonInteropTranslator.TranslateOutbound("PLPWR", input));
    }

    [Theory]
    [InlineData("ASSIGN Bob 3 4", "ASSIGN Bob 4 3")]   // Druid + Kings
    [InlineData("ASSIGN Sara 6 0", "ASSIGN Sara 7 1")] // Mage + Wisdom
    [InlineData("ASSIGN Q 7 5",   "ASSIGN Q 8 6")]     // Warlock + Sanctuary
    [InlineData("ASSIGN H 5 2",   "ASSIGN H 6 4")]     // Hunter + Salvation
    [InlineData("ASSIGN P 2 3",   "ASSIGN P 3 5")]     // Priest + Light
    public void TranslateInbound_PallyPowerAssign_TranslatesClassAndSkill(string input, string expected)
    {
        Assert.Equal(expected, AddonInteropTranslator.TranslateInbound("PLPWR", input));
    }

    // Round-trip: outbound then inbound preserves original. Critical because
    // two 1.14 proxy users round-trip through the legacy server's broadcast.
    [Theory]
    [InlineData("ASSIGN Bob 4 3")]
    [InlineData("ASSIGN Sara 7 1")]
    [InlineData("ASSIGN Q 8 6")]
    [InlineData("ASSIGN H 6 4")]
    [InlineData("ASSIGN P 3 5")]
    public void RoundTrip_AssignOutboundThenInbound_PreservesOriginal(string original)
    {
        string onWire = AddonInteropTranslator.TranslateOutbound("PLPWR", original);
        string roundTripped = AddonInteropTranslator.TranslateInbound("PLPWR", onWire);
        Assert.Equal(original, roundTripped);
    }

    // -------------------------------------------------------------------
    // MASSIGN: skill-only translation, no class.
    // -------------------------------------------------------------------

    [Theory]
    [InlineData("MASSIGN Bob 3", "MASSIGN Bob 4")]   // Kings: 3 modern → 4 legacy
    [InlineData("MASSIGN Sara 4", "MASSIGN Sara 2")] // Salvation: 4 modern → 2 legacy
    [InlineData("MASSIGN Q 5",   "MASSIGN Q 3")]     // Light: 5 modern → 3 legacy
    [InlineData("MASSIGN R 1",   "MASSIGN R 0")]     // Wisdom: 1 modern → 0 legacy
    public void TranslateOutbound_PallyPowerMassign_TranslatesSkill(string input, string expected)
    {
        Assert.Equal(expected, AddonInteropTranslator.TranslateOutbound("PLPWR", input));
    }

    [Theory]
    [InlineData("MASSIGN Bob 4", "MASSIGN Bob 3")]
    [InlineData("MASSIGN Sara 2", "MASSIGN Sara 4")]
    [InlineData("MASSIGN Q 3",   "MASSIGN Q 5")]
    [InlineData("MASSIGN R 0",   "MASSIGN R 1")]
    public void TranslateInbound_PallyPowerMassign_TranslatesSkill(string input, string expected)
    {
        Assert.Equal(expected, AddonInteropTranslator.TranslateInbound("PLPWR", input));
    }

    // -------------------------------------------------------------------
    // SELF: numbers pair-permutation AND assign char remap.
    //
    // Numbers: 12 chars = 6 pairs of (rank,talent). Pairs at positions
    // 3/4/5 hold Kings/Salvation/Light in 1.14 but Salvation/Light/Kings
    // in 1.12 — pairs must permute on the wire boundary.
    //
    // Assign: per-class skill chars 0-7 or 'n'. Each digit char remaps
    // via the same skill-ID lookup table.
    // -------------------------------------------------------------------

    [Fact]
    public void TranslateOutbound_PallyPowerSelf_PermutesNumbersAndRemapsAssign()
    {
        // Modern numbers chunk: pos1=Wis(11), pos2=Mig(22), pos3=Kings(33),
        // pos4=Salv(44), pos5=Light(55), pos6=Sanc(66). Assign chars: per-class
        // modern skill IDs, where 1=Wis 2=Mig 3=Kings 4=Salv 5=Light 6=Sanc.
        const string modern = "SELF 112233445566@123456nn";

        string legacy = AddonInteropTranslator.TranslateOutbound("PLPWR", modern);

        // After permutation, legacy numbers chunk should hold pos3=Salv(44),
        // pos4=Light(55), pos5=Kings(33). Assign chars remapped: 1→0, 2→1,
        // 3→4, 4→2, 5→3, 6→5.
        Assert.Equal("SELF 112244553366@014235nn", legacy);
    }

    [Fact]
    public void TranslateInbound_PallyPowerSelf_PermutesNumbersAndRemapsAssign()
    {
        const string legacy = "SELF 112244553366@014235nn";
        string modern = AddonInteropTranslator.TranslateInbound("PLPWR", legacy);
        Assert.Equal("SELF 112233445566@123456nn", modern);
    }

    [Theory]
    [InlineData("SELF 112233445566@123456nn")]
    [InlineData("SELF nnnnnnnnnnnn@nnnnnnnnn")]
    [InlineData("SELF 1010n0n0n0@n3nn1nnn")]  // sample format from real 1.12 traffic
    public void RoundTrip_SelfOutboundThenInbound_PreservesOriginal(string original)
    {
        string onWire = AddonInteropTranslator.TranslateOutbound("PLPWR", original);
        string roundTripped = AddonInteropTranslator.TranslateInbound("PLPWR", onWire);
        Assert.Equal(original, roundTripped);
    }

    // -------------------------------------------------------------------
    // PASSIGN: 1.14-only message; assign chars remapped same as SELF.
    // Test exists for completeness; 1.12 won't see PASSIGN traffic at all.
    // -------------------------------------------------------------------

    [Fact]
    public void TranslateOutbound_PallyPowerPassign_RemapsAssignChars()
    {
        const string modern = "PASSIGN Bob@123456nn";
        Assert.Equal("PASSIGN Bob@014235nn", AddonInteropTranslator.TranslateOutbound("PLPWR", modern));
    }

    // -------------------------------------------------------------------
    // Strict prefix gating — other addons untouched even if format matches.
    // -------------------------------------------------------------------

    [Theory]
    [InlineData("BigWigs")]
    [InlineData("Details")]
    [InlineData("")]
    public void TranslateOutbound_OtherPrefixes_PassThrough(string prefix)
    {
        const string body = "ASSIGN Bob 4 3";
        Assert.Equal(body, AddonInteropTranslator.TranslateOutbound(prefix, body));
        Assert.Equal(body, AddonInteropTranslator.TranslateInbound(prefix, body));
    }

    // -------------------------------------------------------------------
    // Unknown / passthrough message types.
    // -------------------------------------------------------------------

    [Theory]
    [InlineData("CLEAR")]
    [InlineData("REQ")]
    [InlineData("SYMCOUNT 5")]
    [InlineData("PPLEADER Bob")]
    [InlineData("FREEASSIGN YES | SYMCOUNT 5 | COOLDOWNS:0:0:0:0")]
    [InlineData("AASSIGN Bob 3")]
    [InlineData("ASELF abc@def")]
    [InlineData("NASSIGN x y z 1")]
    public void TranslatePallyPower_UntouchedMessageTypes_PassThrough(string body)
    {
        Assert.Equal(body, AddonInteropTranslator.TranslateOutbound("PLPWR", body));
        Assert.Equal(body, AddonInteropTranslator.TranslateInbound("PLPWR", body));
    }

    // -------------------------------------------------------------------
    // Defensive guards.
    // -------------------------------------------------------------------

    [Fact]
    public void TranslateOutbound_AssignClassZero_ReturnsEmptyToDropMessage()
    {
        Assert.Equal(string.Empty, AddonInteropTranslator.TranslateOutbound("PLPWR", "ASSIGN Bob 0 3"));
    }

    [Theory]
    [InlineData("ASSIGN")]                         // missing all tokens
    [InlineData("ASSIGN Bob")]                     // missing class+skill
    [InlineData("ASSIGN Bob 2")]                   // missing skill
    [InlineData("ASSIGN Bob notanumber 3")]        // non-numeric class
    [InlineData("ASSIGN Bob 3 notanumber")]        // non-numeric skill
    [InlineData("MASSIGN")]                        // missing all tokens
    [InlineData("MASSIGN Bob")]                    // missing skill
    [InlineData("MASSIGN Bob notanumber")]         // non-numeric skill
    [InlineData("SELF")]                           // missing body
    [InlineData("SELF noatsign")]                  // missing @ separator
    [InlineData("SELF abc@def")]                   // numbers chunk too short (<12)
    public void TranslatePallyPower_MalformedMessages_PassThrough(string body)
    {
        Assert.Equal(body, AddonInteropTranslator.TranslateOutbound("PLPWR", body));
        Assert.Equal(body, AddonInteropTranslator.TranslateInbound("PLPWR", body));
    }

    [Fact]
    public void TranslateOutbound_WhenDisabled_PassThroughEverything()
    {
        try
        {
            global::Framework.Settings.EnablePallyPowerInterop = false;
            const string body = "ASSIGN Bob 4 3";
            Assert.Equal(body, AddonInteropTranslator.TranslateOutbound("PLPWR", body));
            Assert.Equal(body, AddonInteropTranslator.TranslateInbound("PLPWR", body));
        }
        finally
        {
            global::Framework.Settings.EnablePallyPowerInterop = true;
        }
    }
}
