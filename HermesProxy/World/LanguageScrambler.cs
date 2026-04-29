using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using nietras.SeparatedValues;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;

namespace HermesProxy.World;

//MIRASU - Server-side chat scrambling for languages the receiving player doesn't understand.
//MIRASU   Vanilla 1.12 emulators (Kronos / vmangos / cmangos) send plain text + language ID and
//MIRASU   rely on the legacy 1.12 client to scramble unknown languages locally. The modern 1.14
//MIRASU   Classic client doesn't do that mapping for legacy language IDs, so foreign-faction
//MIRASU   chat reaches the receiver as readable English (HermesProxy issues #100, #213). We
//MIRASU   reproduce the original behaviour proxy-side: load the same syllable tables baked into
//MIRASU   the vanilla client (extracted via Hoizame's WoW_ClassicUIResources LanguageWords.lua
//MIRASU   dump) and scramble incoming chat when the local player's race/class can't read the
//MIRASU   language being broadcast.
public static class LanguageScrambler
{
    // Per-language: index = (length-1), value = syllables of that exact length.
    private static readonly Dictionary<uint, string[][]> _syllablesByLength = new();

    // Cached per-session understood language set. Built once on first check per
    // race/class combo so busy trade chat doesn't re-evaluate the switch per message.
    private static readonly Dictionary<(Race, Class), HashSet<uint>> _understoodCache = new();

    public static void Load()
    {
        var path = Path.Combine("CSV", "LanguageWords.csv");
        if (!File.Exists(path))
        {
            Log.Print(LogType.Warn, $"LanguageWords.csv not found at '{path}'; chat scrambling disabled.");
            return;
        }

        var collected = new Dictionary<uint, List<string>>();
        using (var reader = Sep.Reader(o => o with { HasHeader = true }).FromFile(path))
        {
            foreach (var row in reader)
            {
                uint langId = uint.Parse(row[0].Span);
                string syllable = row[1].ToString();
                if (string.IsNullOrEmpty(syllable))
                    continue;

                if (!collected.TryGetValue(langId, out var list))
                {
                    list = new List<string>();
                    collected[langId] = list;
                }
                list.Add(syllable);
            }
        }

        lock (_syllablesByLength)
        {
            _syllablesByLength.Clear();
            foreach (var kvp in collected)
            {
                int maxLen = 0;
                foreach (var s in kvp.Value)
                    if (s.Length > maxLen) maxLen = s.Length;

                var byLen = new string[maxLen][];
                for (int len = 1; len <= maxLen; len++)
                    byLen[len - 1] = kvp.Value.Where(s => s.Length == len).ToArray();
                _syllablesByLength[kvp.Key] = byLen;
            }
        }
    }

    /// <summary>True if the proxy has syllabary data for this language ID and can scramble.</summary>
    public static bool HasSyllabary(uint language) => _syllablesByLength.ContainsKey(language);

    /// <summary>
    /// Determines whether a player of the given race/class understands the legacy 1.12 language ID.
    /// Mirrors vanilla racial language defaults: Alliance races know Common + their racial; Horde
    /// races know Orcish + their racial. Universal/addon channels are always understood. Demonic is
    /// granted to any Warlock (vanilla actually gates on level 50, but the proxy doesn't always know
    /// the precise level on first-frame chat — over-permissive is the safer default for warlock chat).
    /// </summary>
    public static bool CanUnderstand(Race race, Class playerClass, uint language)
    {
        if (language == (uint)Language.Universal ||
            language == (uint)Language.Addon ||
            language == (uint)Language.AddonBfA ||
            language == (uint)Language.AddonLogged)
            return true;

        if (race == Race.None)
            return true;

        var key = (race, playerClass);
        if (!_understoodCache.TryGetValue(key, out var understood))
        {
            understood = BuildUnderstoodSet(race, playerClass);
            _understoodCache[key] = understood;
        }
        return understood.Contains(language);
    }

    private static HashSet<uint> BuildUnderstoodSet(Race race, Class playerClass)
    {
        var set = new HashSet<uint>();

        if (playerClass == Class.Warlock)
            set.Add((uint)Language.Demonic);

        switch (race)
        {
            case Race.Human:    set.Add((uint)Language.Common); break;
            case Race.Dwarf:    set.Add((uint)Language.Common);  set.Add((uint)Language.Dwarvish); break;
            case Race.NightElf: set.Add((uint)Language.Common);  set.Add((uint)Language.Darnassian); break;
            case Race.Gnome:    set.Add((uint)Language.Common);  set.Add((uint)Language.Gnomish); break;
            case Race.Draenei:  set.Add((uint)Language.Common);  set.Add((uint)Language.Draenei); break;
            case Race.Worgen:   set.Add((uint)Language.Common);  set.Add((uint)Language.Worgen); break;

            case Race.Orc:      set.Add((uint)Language.Orcish); break;
            case Race.Undead:   set.Add((uint)Language.Orcish);  set.Add((uint)Language.Gutterspeak); break;
            case Race.Tauren:   set.Add((uint)Language.Orcish);  set.Add((uint)Language.Taurahe); break;
            case Race.Troll:    set.Add((uint)Language.Orcish);  set.Add((uint)Language.Troll); break;
            case Race.BloodElf: set.Add((uint)Language.Orcish);  set.Add((uint)Language.Thalassian); break;
            case Race.Goblin:   set.Add((uint)Language.Orcish);  set.Add((uint)Language.Goblin); break;
        }

        return set;
    }

    /// <summary>
    /// Replaces alphabetic runs in <paramref name="text"/> with deterministically-chosen syllables
    /// from the language's authentic vanilla syllable table. Same-length output. Pipe-escape
    /// sequences (|c color codes, |H hyperlinks, |T textures, |h, |r, |n) are preserved verbatim
    /// so item links and color formatting survive scrambling. Returns text unchanged if no
    /// syllabary is loaded for the language.
    /// </summary>
    public static string Scramble(string text, uint language)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (!_syllablesByLength.TryGetValue(language, out var byLen) || byLen.Length == 0)
            return text;

        var output = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '|' && i + 1 < text.Length)
            {
                int consumed = TryCopyEscapeSequence(text, i, output);
                if (consumed > 0)
                {
                    i += consumed;
                    continue;
                }
            }

            if (char.IsLetter(c))
            {
                int start = i;
                while (i < text.Length && char.IsLetter(text[i]))
                    i++;
                AppendScrambledWord(output, text.AsSpan(start, i - start), byLen);
            }
            else
            {
                output.Append(c);
                i++;
            }
        }

        return output.ToString();
    }

    private static void AppendScrambledWord(StringBuilder dest, ReadOnlySpan<char> word, string[][] byLen)
    {
        int targetLen = word.Length;
        if (targetLen <= 0) return;

        int writeStart = dest.Length;
        bool capitalize = char.IsUpper(word[0]);

        // FNV-1a 32-bit hash seeded from the lowercased word — same word always
        // scrambles to the same syllables, which matches vanilla client behavior
        // (players learn to recognize repeated phrases in foreign chat).
        uint seed = FNV1a32(word);

        int remaining = targetLen;
        int maxAvailable = byLen.Length;

        while (remaining > 0)
        {
            int desiredLen = Math.Min(remaining, maxAvailable);
            string[]? bucket = null;

            // Prefer the longest available syllable that fits the remaining space.
            for (int len = desiredLen; len >= 1; len--)
            {
                var candidates = byLen[len - 1];
                if (candidates.Length > 0)
                {
                    bucket = candidates;
                    break;
                }
            }
            if (bucket == null)
                break;

            uint idx = seed % (uint)bucket.Length;
            string syllable = bucket[(int)idx];

            // Source syllables are stored capitalized; lowercase for concatenation,
            // we re-capitalize the first character of the whole word at the end.
            for (int k = 0; k < syllable.Length && remaining > 0; k++)
            {
                dest.Append(char.ToLowerInvariant(syllable[k]));
                remaining--;
            }

            seed = (seed ^ idx) * 16777619u + (uint)syllable.Length;
        }

        if (capitalize && dest.Length > writeStart && char.IsLower(dest[writeStart]))
            dest[writeStart] = char.ToUpper(dest[writeStart]);
    }

    private static int TryCopyEscapeSequence(string text, int start, StringBuilder output)
    {
        if (start + 1 >= text.Length || text[start] != '|') return 0;
        char code = text[start + 1];
        switch (code)
        {
            case 'c':
                // |cAARRGGBB color start: 2 + 8 hex = 10 chars
                int cLen = Math.Min(10, text.Length - start);
                output.Append(text, start, cLen);
                return cLen;

            case 'r': // color reset
            case 'n': // newline
                output.Append(text, start, 2);
                return 2;

            case 'h': // hyperlink boundary marker
                output.Append(text, start, 2);
                return 2;

            case 'H': // |H<linkdata>|h - copy through |h verbatim so the link wire data stays intact
            {
                int hEnd = text.IndexOf("|h", start + 2, StringComparison.Ordinal);
                if (hEnd < 0) return 0;
                int hLen = hEnd - start + 2;
                output.Append(text, start, hLen);
                return hLen;
            }

            case 'T': // |T<texture>|t - copy through |t (icon textures)
            {
                int tEnd = text.IndexOf("|t", start + 2, StringComparison.Ordinal);
                if (tEnd < 0) return 0;
                int tLen = tEnd - start + 2;
                output.Append(text, start, tLen);
                return tLen;
            }

            default:
                return 0;
        }
    }

    private static uint FNV1a32(ReadOnlySpan<char> s)
    {
        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;
        uint hash = offsetBasis;
        for (int i = 0; i < s.Length; i++)
        {
            hash ^= char.ToLowerInvariant(s[i]);
            hash *= prime;
        }
        return hash;
    }
}
