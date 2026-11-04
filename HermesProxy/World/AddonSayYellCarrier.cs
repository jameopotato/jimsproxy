using HermesProxy.World.Enums;

using System;
using System.Globalization;

namespace HermesProxy.World;

// JimsProxy (addon say/yell carrier): since Classic 1.13.3 an addon can send its hidden
// messages to SAY and YELL (nearby players) as well as party, raid, guild and whisper;
// Nova World Buffs shares its timers by addon yell at login, on zone changes, on flights
// and every ten minutes. The modern client never shows or animates an addon message.
//
// A 1.12 server has no say or yell addon channel. Forwarded as 1.12 say or yell chat in
// the addon language, these messages reach every 1.12 client nearby, which plays the
// shout animation on every yell and the talk animation on every say before it looks at
// the language (the text itself is never shown). Players on 1.12 clients saw proxied
// players "constantly shouting".
//
// So the proxy carries them between proxied players on a channel only proxies join.
// Channel messages animate nothing. The sending proxy puts the type and its own position
// in a small header; the receiving proxy checks the server's listen range against its
// own player (the server does that for a real say or yell) and hands the message to its
// client as the say or yell addon message it was, through the same path as before. 1.12
// clients that never join the channel receive nothing, which is what their addons could
// use anyway.
public static class AddonSayYellCarrier
{
    // A random name, so nobody joins it by guessing (the member list of a channel is visible
    // to anyone in it). The server capitalizes the first letter and compares names
    // case-insensitively.
    public const string ChannelName = "Vq7mz3xk9rt4";

    // Message text: "JPY1\t<S|Y>,<map>,<x>,<y>\t<prefix>\t<body>". The body is the rest of
    // the text, so a body may hold tabs.
    const string Marker = "JPY1";

    // ListenRange.Say and ListenRange.Yell server defaults (25 and 300 yards), plus a margin
    // for positions that are a movement packet or two old.
    public const float SayRangeYards = 25f + 10f;
    public const float YellRangeYards = 300f + 20f;

    public readonly record struct Message(bool Yell, uint MapId, float X, float Y, string Prefix, string Body);

    public static bool IsCarrierType(ChatMessageTypeModern type)
        => type == ChatMessageTypeModern.Say || type == ChatMessageTypeModern.Yell;

    public static bool IsCarrierChannel(string? channelName)
        => string.Equals(channelName, ChannelName, StringComparison.OrdinalIgnoreCase);

    public static string Encode(bool yell, uint mapId, float x, float y, string prefix, string body)
    {
        var c = CultureInfo.InvariantCulture;
        return string.Concat(
            Marker, "\t",
            yell ? "Y" : "S", ",", mapId.ToString(c), ",",
            ((int)MathF.Round(x)).ToString(c), ",", ((int)MathF.Round(y)).ToString(c),
            "\t", prefix, "\t", body);
    }

    public static bool TryDecode(string? text, out Message message)
    {
        message = default;
        if (text == null || !text.StartsWith(Marker + "\t", StringComparison.Ordinal))
            return false;

        int headerStart = Marker.Length + 1;
        int tab2 = text.IndexOf('\t', headerStart);
        if (tab2 < 0)
            return false;
        int tab3 = text.IndexOf('\t', tab2 + 1);
        if (tab3 < 0)
            return false;

        string[] header = text.Substring(headerStart, tab2 - headerStart).Split(',');
        if (header.Length != 4)
            return false;
        bool yell = header[0] == "Y";
        if (!yell && header[0] != "S")
            return false;
        var c = CultureInfo.InvariantCulture;
        if (!uint.TryParse(header[1], NumberStyles.Integer, c, out uint mapId)
            || !int.TryParse(header[2], NumberStyles.Integer, c, out int x)
            || !int.TryParse(header[3], NumberStyles.Integer, c, out int y))
            return false;

        string prefix = text.Substring(tab2 + 1, tab3 - tab2 - 1);
        if (prefix.Length == 0)
            return false;

        message = new Message(yell, mapId, x, y, prefix, text.Substring(tab3 + 1));
        return true;
    }

    // Whether the listener would have heard the say or yell: same map, within the listen
    // range in the horizontal plane. An unknown listener position hears nothing.
    public static bool InRange(in Message message, uint? listenerMapId, bool listenerPositionKnown, float listenerX, float listenerY)
    {
        if (!listenerPositionKnown || listenerMapId == null || listenerMapId.Value != message.MapId)
            return false;
        float dx = listenerX - message.X;
        float dy = listenerY - message.Y;
        float range = message.Yell ? YellRangeYards : SayRangeYards;
        return dx * dx + dy * dy <= range * range;
    }
}
