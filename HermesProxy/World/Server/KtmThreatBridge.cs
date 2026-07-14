using System;
using System.Globalization;

namespace HermesProxy.World.Server;

// JimsProxy KTM (KLHThreatMeter) threat interop — outbound rewrite half.
//
// When our built-in threat engine is active it computes better, Kronos-
// calibrated threat than the client-side LibThreatClassic2 estimate that the
// KTMClassic / KTM addons run. Both the addon and our engine want to feed the
// 1.12 "KLHTM" wire protocol that 1.12 KLHThreatMeter raiders listen on, so a
// 1.14 player running a KTM addon would otherwise broadcast the addon's weaker
// number to the raid.
//
// This bridge makes OUR number win by rewriting the local client's outbound
// KLHTM "t <n>" threat line to our engine's value at the HandleAddonMessage
// chokepoint — the same rewrite pattern as HealCommBridge / AddonInteropTranslator.
// Result: exactly one KLHTM stream per player, carrying our data. The
// complementary ORIGINATION path (proxy emits KLHTM itself when NO KTM addon is
// present) is a separate follow-up.
//
// KTM wire grammar (from KTMClassic's LibThreatClassic2 fork, ktm_prefix
// "KLHTM"):
//   "t <int>"        current-target threat, floor()'d   ← the only line we rewrite
//   "target <name>"  officer set-master-target          ← passthrough
//   "cleartarget"    officer clear-master-target        ← passthrough
//   "clear"          officer clear-all-threat           ← passthrough
// Only the "t " threat line carries a value that's ours to correct; the officer
// commands are coordination grammar and must pass through untouched. "target"
// starts with 't' but not "t " (t + space), so the strict "t " prefix cleanly
// isolates the threat line from the set-master-target command.
public static class KtmThreatBridge
{
    public const string KtmPrefix = "KLHTM";

    // KTM threat line: "t " followed by a base-10 integer.
    private const string ThreatLinePrefix = "t ";

    // Rewrite an outbound KTM threat broadcast to our engine's number.
    //
    //   prefix     the addon message prefix; only "KLHTM" is touched
    //   body       the addon message body (e.g. "t 12345")
    //   ourThreat  our engine's floored current-target threat for the local
    //              player, or 0 when we have no number (engine off / in a
    //              battleground / no target / untracked mob)
    //
    // Returns the body to forward. Non-KLHTM prefixes, non-threat KLHTM lines
    // (officer commands), malformed threat lines, and ourThreat <= 0 all pass
    // the body through UNCHANGED — we never blank a raider's meter or corrupt
    // the addon's coordination grammar. Only a well-formed "t <n>" combined
    // with a positive ourThreat is rewritten to "t <ourThreat>".
    //
    // Passing the addon's own number through when ourThreat is 0 is deliberate:
    // 0 means "engine off or no data yet", not "genuinely zero threat", so
    // deferring to the addon's estimate during the observation gap is strictly
    // better than broadcasting a 0 that would blank the 1.12 raider's meter.
    public static string RewriteOutbound(string prefix, string body, long ourThreat)
    {
        if (prefix != KtmPrefix)
            return body;
        if (ourThreat <= 0)
            return body;
        if (string.IsNullOrEmpty(body) || !body.StartsWith(ThreatLinePrefix, StringComparison.Ordinal))
            return body;

        // Everything after "t " must be a single non-negative integer for this
        // to be the threat line. Parse strictly; anything else (e.g. a future
        // addon variant that appends fields) passes through untouched so we
        // never mangle a message shape we don't fully understand.
        string valueToken = body.Substring(ThreatLinePrefix.Length).Trim();
        if (!IsNonNegativeInteger(valueToken))
            return body;

        return ThreatLinePrefix + ourThreat.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsNonNegativeInteger(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        foreach (char c in s)
        {
            if (c < '0' || c > '9')
                return false;
        }
        return true;
    }
}
