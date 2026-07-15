using System;
using System.Collections.Generic;
using System.Text;
using Framework.Logging;

namespace HermesProxy.World.Server;

// JimsProxy: cross-version addon body translation. Strictly prefix-gated to
// raid-mandatory addons whose 1.12 and 1.14 forks diverged in mechanical,
// well-known ways. Currently only PallyPower ("PLPWR") is implemented; the
// next addition will be BigWigs. Anything else passes through untouched.
//
// PallyPower diverges across two axes:
//   1) Class index: 1.12 is 0-indexed (0=WARRIOR..7=WARLOCK), 1.14 is
//      1-indexed (1=WARRIOR..8=WARLOCK, 9=PET). Same class ORDER, different
//      base. Translation is +/- 1.
//   2) Skill (blessing) ID: the two forks reordered Salvation/Light/Kings.
//        1.12: 0=Wisdom 1=Might 2=Salvation 3=Light 4=Kings 5=Sanctuary
//        1.14: 1=Wisdom 2=Might 3=Kings    4=Salvation 5=Light 6=Sanctuary
//      Plus 1.14 added Sacrifice (7) which has no 1.12 equivalent.
//
// Affected message types (only PLPWR addon traffic — strict prefix gate):
//   - ASSIGN <name> <class> <skill>     class +/- 1, skill via lookup
//   - MASSIGN <name> <skill>            skill via lookup
//   - SELF <numbers>@<assign>           numbers: swap (rank,talent) pairs
//                                          at positions 3/4/5 to mirror the
//                                          Kings/Salvation/Light reorder.
//                                       assign: per-class skill chars,
//                                          remap each via lookup.
//   - PASSIGN <name>@<assign>           assign chars via lookup (1.14-only,
//                                          included for completeness)
//
// All other PallyPower messages (CLEAR, REQ, SYMCOUNT, FREEASSIGN, PPLEADER,
// NASSIGN, AASSIGN, ASELF) carry no class/skill IDs that need translation,
// or they're 1.14-only features that 1.12 silently ignores. COOLDOWNS is
// special-cased inbound only — see NormalizeCooldowns.
public static class AddonInteropTranslator
{
    private const string PallyPowerPrefix = "PLPWR";

    // Skill ID lookup tables. Both directions are explicit (not pure +/- 1)
    // because the Salvation/Light/Kings ordering differs between forks.
    private static readonly Dictionary<int, int> PpSkillModernToLegacy = new()
    {
        { 1, 0 }, // Wisdom
        { 2, 1 }, // Might
        { 3, 4 }, // Kings
        { 4, 2 }, // Salvation
        { 5, 3 }, // Light
        { 6, 5 }, // Sanctuary
        // Sacrifice (1.14 skill 7) has no 1.12 equivalent; passed through
        // untranslated. 1.12 doesn't define a skill 7, so it'll fall outside
        // the "0..5 known" range and the addon's own bounds-check filters it.
    };

    private static readonly Dictionary<int, int> PpSkillLegacyToModern = new()
    {
        { 0, 1 }, // Wisdom
        { 1, 2 }, // Might
        { 2, 4 }, // Salvation
        { 3, 5 }, // Light
        { 4, 3 }, // Kings
        { 5, 6 }, // Sanctuary
    };

    // SELF "numbers" pair-position remap. The 12-char numbers chunk encodes
    // 6 (rank,talent) pairs — one per blessing the paladin can cast. The
    // pair at position N represents the same blessing-id as the addon's
    // BlessingID[N], so when forks disagree on BlessingID ordering we need
    // to permute the pairs at positions 3/4/5.
    //
    // 1.14 numbers positions: [Wis][Mig][Kings][Salv][Light][Sanc]
    // 1.12 numbers positions: [Wis][Mig][Salv][Light][Kings][Sanc]
    //
    // Modern→Legacy: pos3 (Kings)→pos5, pos4 (Salv)→pos3, pos5 (Light)→pos4
    // Legacy→Modern: pos3 (Salv)→pos4, pos4 (Light)→pos5, pos5 (Kings)→pos3
    private static readonly int[] PpNumbersModernToLegacyPosMap = { 1, 2, 5, 3, 4, 6 };
    private static readonly int[] PpNumbersLegacyToModernPosMap = { 1, 2, 4, 5, 3, 6 };

    public static string TranslateOutbound(string prefix, string body)
    {
        if (!Framework.Settings.EnablePallyPowerInterop) return body;
        if (prefix != PallyPowerPrefix) return body;
        return TranslatePallyPower(body, modernToLegacy: true);
    }

    public static string TranslateInbound(string prefix, string body)
    {
        if (!Framework.Settings.EnablePallyPowerInterop) return body;
        if (prefix != PallyPowerPrefix) return body;
        return TranslatePallyPower(body, modernToLegacy: false);
    }

    private static string TranslatePallyPower(string body, bool modernToLegacy)
    {
        if (string.IsNullOrEmpty(body)) return body;

        if (body.StartsWith("ASSIGN ", StringComparison.Ordinal))
            return TranslateAssign(body, modernToLegacy);
        if (body.StartsWith("MASSIGN ", StringComparison.Ordinal))
            return TranslateMassign(body, modernToLegacy);
        if (body.StartsWith("SELF ", StringComparison.Ordinal))
            return TranslateSelf(body, modernToLegacy);
        if (body.StartsWith("PASSIGN ", StringComparison.Ordinal))
            return TranslatePassign(body, modernToLegacy);

        // Inbound only: modern senders always emit a well-formed COOLDOWNS tail.
        if (!modernToLegacy && body.Contains("COOLDOWNS", StringComparison.Ordinal))
            return NormalizeCooldowns(body);

        // Everything else (CLEAR / REQ / SYMCOUNT / FREEASSIGN / PPLEADER /
        // NASSIGN / AASSIGN / ASELF) carries no fields that need version
        // remap — pass through untouched.
        return body;
    }

    // "ASSIGN <name> <class> <skill>" — translate class index and skill ID.
    // Name can theoretically contain spaces; split from the right on the
    // last two tokens.
    private static string TranslateAssign(string body, bool modernToLegacy)
    {
        const string prefix = "ASSIGN ";
        int firstSpace = body.LastIndexOf(' ');
        if (firstSpace < 0) return body;
        int secondSpace = body.LastIndexOf(' ', firstSpace - 1);
        if (secondSpace <= prefix.Length - 1) return body;

        string namePart = body.Substring(prefix.Length, secondSpace - prefix.Length);
        string classToken = body.Substring(secondSpace + 1, firstSpace - secondSpace - 1);
        string skillToken = body.Substring(firstSpace + 1);

        if (!int.TryParse(classToken, out int classIdx)) return body;
        if (!int.TryParse(skillToken, out int skillIdx)) return body;

        int newClass = classIdx + (modernToLegacy ? -1 : +1);
        if (newClass < 0)
        {
            Drop("ASSIGN", "negative_class", modernToLegacy, classIdx);
            return string.Empty;
        }
        int newSkill = TranslateSkillId(skillIdx, modernToLegacy);

        return $"{prefix}{namePart} {newClass} {newSkill}";
    }

    // "MASSIGN <name> <skill>" — translate skill ID only (no class index).
    private static string TranslateMassign(string body, bool modernToLegacy)
    {
        const string prefix = "MASSIGN ";
        int lastSpace = body.LastIndexOf(' ');
        if (lastSpace <= prefix.Length - 1) return body;

        string namePart = body.Substring(prefix.Length, lastSpace - prefix.Length);
        string skillToken = body.Substring(lastSpace + 1);
        if (!int.TryParse(skillToken, out int skillIdx)) return body;

        int newSkill = TranslateSkillId(skillIdx, modernToLegacy);
        return $"{prefix}{namePart} {newSkill}";
    }

    // "SELF <numbers>@<assign>" — translate both halves. Numbers chunk is
    // 12 chars (6 pairs), assign chunk is N chars (one per class).
    private static string TranslateSelf(string body, bool modernToLegacy)
    {
        const string prefix = "SELF ";
        int atIndex = body.IndexOf('@', prefix.Length);
        if (atIndex < 0) return body;

        string numbers = body.Substring(prefix.Length, atIndex - prefix.Length);
        string assign = body.Substring(atIndex + 1);

        string newNumbers = RemapNumbersPairs(numbers, modernToLegacy);
        string newAssign = RemapAssignChars(assign, modernToLegacy);

        return $"{prefix}{newNumbers}@{newAssign}";
    }

    // "PASSIGN <name>@<assign>" — remap assign chars only.
    private static string TranslatePassign(string body, bool modernToLegacy)
    {
        const string prefix = "PASSIGN ";
        int atIndex = body.IndexOf('@', prefix.Length);
        if (atIndex < 0) return body;

        string namePart = body.Substring(prefix.Length, atIndex - prefix.Length);
        string assign = body.Substring(atIndex + 1);
        string newAssign = RemapAssignChars(assign, modernToLegacy);
        return $"{prefix}{namePart}@{newAssign}";
    }

    // Permute (rank,talent) pairs in the 12-char SELF numbers chunk so the
    // receiving fork's positional read lands on the right blessing. Pairs
    // are 2 chars wide; positions 1-based to match the addon's loop.
    private static string RemapNumbersPairs(string numbers, bool modernToLegacy)
    {
        if (numbers.Length < 12) return numbers; // malformed; pass through
        int[] map = modernToLegacy ? PpNumbersModernToLegacyPosMap : PpNumbersLegacyToModernPosMap;
        var output = new StringBuilder(12);
        for (int targetPos = 1; targetPos <= 6; targetPos++)
        {
            // Find which source-pos maps to this target-pos.
            int sourcePos = -1;
            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] == targetPos)
                {
                    sourcePos = i + 1;
                    break;
                }
            }
            if (sourcePos < 1 || sourcePos > 6) return numbers; // malformed map; pass through
            int sourceIdx = (sourcePos - 1) * 2;
            output.Append(numbers[sourceIdx]);
            output.Append(numbers[sourceIdx + 1]);
        }
        // Preserve any trailing chars beyond the 12 (defensive — addon
        // protocol shouldn't have any but don't truncate user data).
        if (numbers.Length > 12)
            output.Append(numbers, 12, numbers.Length - 12);
        return output.ToString();
    }

    // Per-class skill char remap. Each char is a single decimal digit
    // (0-7) or 'n' for "no assignment". 'n' and any non-digit pass through.
    private static string RemapAssignChars(string assign, bool modernToLegacy)
    {
        if (string.IsNullOrEmpty(assign)) return assign;
        var output = new StringBuilder(assign.Length);
        foreach (char c in assign)
        {
            if (c >= '0' && c <= '9')
            {
                int skillIdx = c - '0';
                int newSkill = TranslateSkillId(skillIdx, modernToLegacy);
                if (newSkill >= 0 && newSkill <= 9)
                    output.Append((char)('0' + newSkill));
                else
                    output.Append(c); // out of single-digit range; preserve
            }
            else
            {
                output.Append(c);
            }
        }
        return output.ToString();
    }

    // The 1.14 addon strsplits the whole message on ':' into 4 positional tokens and errors permanently on any malformed tail; rewrite it to 4 receiver-safe tokens.
    private static string NormalizeCooldowns(string body)
    {
        int idx = body.IndexOf("COOLDOWNS", StringComparison.Ordinal);
        string head = body.Substring(0, idx);
        string tail = body.Substring(idx + "COOLDOWNS".Length);

        string[] tokens = ExtractCooldownTokens(tail);
        for (int pair = 0; pair < 2; pair++)
        {
            string duration = tokens[pair * 2];
            string remaining = tokens[pair * 2 + 1];
            // Mirror the receiver's guard: safe iff remaining is "n" or both numeric.
            if (remaining != "n" && (!IsNumericToken(duration) || !IsNumericToken(remaining)))
            {
                tokens[pair * 2] = "n";
                tokens[pair * 2 + 1] = "n";
            }
        }

        // A colon before COOLDOWNS would shift every strsplit position on the receiver.
        head = head.Replace(":", "");

        string normalized = $"{head}COOLDOWNS:{tokens[0]}:{tokens[1]}:{tokens[2]}:{tokens[3]}";
        if (normalized != body)
        {
            Log.Event("addon.interop.normalized", new
            {
                prefix = PallyPowerPrefix,
                message_type = "COOLDOWNS",
                original = body,
                result = normalized,
            });
        }
        return normalized;
    }

    // Salvage up to 4 leading valid tokens from the tail; anything else becomes "n".
    private static string[] ExtractCooldownTokens(string tail)
    {
        var tokens = new[] { "n", "n", "n", "n" };
        if (tail.Length == 0)
            return tokens;
        // Well-formed tails are colon-delimited; tolerate a space-delimited variant.
        char separator = tail[0] == ':' ? ':' : ' ';
        string[] parts = tail.Split(separator);
        // parts[0] is the text between "COOLDOWNS" and the first separator — non-empty means an unknown shape.
        if (parts[0].Length != 0)
            return tokens;
        int filled = 0;
        for (int i = 1; i < parts.Length && filled < 4; i++)
        {
            if (parts[i] == "n" || IsNumericToken(parts[i]))
                tokens[filled++] = parts[i];
            else
                break;
        }
        return tokens;
    }

    // Strict plain-decimal check, deliberately narrower than Lua tonumber() so NaN/Infinity/hex/exponent forms can never reach the receiver's arithmetic.
    private static bool IsNumericToken(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        int start = s[0] == '-' ? 1 : 0;
        bool hasDigit = false, hasDot = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= '0' && c <= '9')
                hasDigit = true;
            else if (c == '.' && !hasDot)
                hasDot = true;
            else
                return false;
        }
        return hasDigit;
    }

    private static int TranslateSkillId(int skillIdx, bool modernToLegacy)
    {
        var table = modernToLegacy ? PpSkillModernToLegacy : PpSkillLegacyToModern;
        return table.TryGetValue(skillIdx, out int translated) ? translated : skillIdx;
    }

    private static void Drop(string messageType, string reason, bool modernToLegacy, int original)
    {
        Log.Event("addon.interop.dropped", new
        {
            prefix = PallyPowerPrefix,
            direction = modernToLegacy ? "outbound" : "inbound",
            message_type = messageType,
            reason,
            original_value = original,
        });
    }
}
