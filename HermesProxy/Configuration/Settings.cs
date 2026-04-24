using HermesProxy.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using Framework.Logging;
using Framework.Networking;
using HermesProxy;
using HermesProxy.Configuration;

namespace Framework;

public static class Settings
{
    public static byte[] ClientSeed = null!;
    public static ClientVersionBuild ClientBuild;
    public static ClientVersionBuild ServerBuild;
    public static string ServerAddress = null!;
    public static int ServerPort;
    public static string ReportedOS = null!;
    public static string ReportedPlatform = null!;
    public static string ExternalAddress = null!;
    public static int RestPort;
    public static int BNetPort;
    public static int RealmPort;
    public static int InstancePort;
    public static bool DebugOutput;
    public static bool PacketsLog;
    public static bool SpanStatsLog;
    // JimsProxy: structured JSONL diagnostic logging
    public static bool StructuredLog;
    public static bool VerboseLog;
    // JimsProxy (issue #43): how many ms before the local GCD expiry estimate the proxy
    // releases a held cast. 0 = fire exactly at expiry (cast lands ~RTT late at server).
    // Clamped to 0..50 in LoadAndVerifyFrom.
    public static int SpellCastEarlyFireOffsetMs;

    public static bool LoadAndVerifyFrom(ConfigurationParser config)
    {
        ClientSeed = config.GetByteArray("ClientSeed", "179D3DC3235629D07113A9B3867F97A7".ParseAsByteArray());
        ClientBuild = config.GetEnum("ClientBuild", ClientVersionBuild.V2_5_2_40892);
        var serverBuildStr = config.GetString("ServerBuild", "auto");
        if (serverBuildStr == "auto")
            ServerBuild = VersionChecker.GetBestLegacyVersion(ClientBuild);
        else
            ServerBuild = config.GetEnum("ServerBuild", ClientVersionBuild.Zero);
        ServerAddress = config.GetString("ServerAddress", "127.0.0.1");
        ServerPort = config.GetInt("ServerPort", 3724);
        ReportedOS = config.GetString("ReportedOS", "OSX");
        ReportedPlatform = config.GetString("ReportedPlatform", "x86");
        ExternalAddress = config.GetString("ExternalAddress", "127.0.0.1");
        RestPort = config.GetInt("RestPort", 8081);
        BNetPort = config.GetInt("BNetPort", 1119);
        RealmPort = config.GetInt("RealmPort", 8084);
        InstancePort = config.GetInt("InstancePort", 8086);
        DebugOutput = config.GetBoolean("DebugOutput", false);
        PacketsLog = config.GetBoolean("PacketsLog", true);
        SpanStatsLog = config.GetBoolean("SpanStatsLog", false);
        // JimsProxy: structured logging defaults on; toggle VerboseLog to enable per-packet Verbose console output
        StructuredLog = config.GetBoolean("StructuredLog", true);
        VerboseLog = config.GetBoolean("VerboseLog", false);
        SpellCastEarlyFireOffsetMs = Math.Clamp(config.GetInt("SpellCastEarlyFireOffsetMs", 0), 0, 50);
        Log.StructuredLogEnabled = StructuredLog;
        Log.VerboseLogEnabled = VerboseLog;
        // Open the JSONL file now so session.start's payload can include the full path.
        // Without this, the first call to Log.Event evaluates payload args (including
        // Log.StructuredLogPath) before EnsureJsonlOpen runs inside Event().
        Log.StartStructuredLog();

        return VerifyConfig();
    }
    
    private static bool VerifyConfig()
    {
        if (ClientSeed.Length != 16)
        {
            Log.Print(LogType.Server, "ClientSeed must have byte length of 16 (32 characters)");
            return false;
        }

        if (!VersionChecker.IsSupportedModernVersion(ClientBuild))
        {
            Log.Print(LogType.Server, $"Unsupported ClientBuild '{ClientBuild}'");
            return false;
        }

        if (!VersionChecker.IsSupportedLegacyVersion(ServerBuild))
        {
            Log.Print(LogType.Server, $"Unsupported ServerBuild '{ServerBuild}', use 'auto' to select best");
            return false;
        }

        if (!IsValidPortNumber(RestPort))
        {
            Log.Print(LogType.Server, $"Specified battle.net port ({RestPort}) out of allowed range (1-65535)");
            return false;
        }

        if (!IsValidPortNumber(ServerPort))
        {
            Log.Print(LogType.Server, $"Specified battle.net port ({BNetPort}) out of allowed range (1-65535)");
            return false;
        }

        if (!IsValidPortNumber(BNetPort))
        {
            Log.Print(LogType.Server, $"Specified battle.net port ({BNetPort}) out of allowed range (1-65535)");
            return false;
        }

        if (!IsValidPortNumber(RealmPort))
        {
            Log.Print(LogType.Server, $"Specified battle.net port ({RealmPort}) out of allowed range (1-65535)");
            return false;
        }

        if (!IsValidPortNumber(InstancePort))
        {
            Log.Print(LogType.Server, $"Specified battle.net port ({InstancePort}) out of allowed range (1-65535)");
            return false;
        }

        bool IsValidPortNumber(int someNumber)
        {
            return someNumber > IPEndPoint.MinPort && someNumber < IPEndPoint.MaxPort;
        }

        return true;
    }
}
