using HermesProxy.World.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Framework;
using Framework.Logging;

namespace HermesProxy.World.Server;

public class AccountMetaDataManager
{
    private const string LAST_CHARACTER_FILE = "last_character.txt";
    private const string COMPLETED_QUESTS_FILE = "completed_quests.csv";
    private const string SETTINGS_FILE = "settings.json";
    //MIRASU - per-character disk persistence for quest item running totals so the modern over-head
    //MIRASU   quest toast survives a full proxy restart (close-game-then-relaunch, or crash). The
    //MIRASU   in-memory snapshot in GlobalSessionData covers logout-to-charselect-relog where the
    //MIRASU   proxy stays alive; this disk file is the persistent backstop for the harder case.
    private const string QUEST_ITEM_PROGRESS_FILE = "quest-item-progress.json";

    private readonly string _accountName;

    private string GetAccountMetaDataDirectory()
    {
        string path = Path.GetFullPath(Path.Combine("AccountData", _accountName));

        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);

        return path;
    }

    private string GetAccountCharacterMetaDataDirectory(string realm, string characterName)
    {
        string path = Path.GetFullPath(Path.Combine("AccountData", _accountName, realm, characterName));

        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);

        return path;
    }
    
    public AccountMetaDataManager(string accountName)
    {
        _accountName = accountName;
    }

    public (string realmName, string charName, ulong charLowerGuid, long lastLoginUnixSec)? GetLastSelectedCharacter()
    {
        var path = Path.Combine(GetAccountMetaDataDirectory(), LAST_CHARACTER_FILE);
        if (!File.Exists(path))
            return null;

        var rawContent = File.ReadAllText(path, Encoding.UTF8);
        var content = rawContent.Split(',');
        if (content.Length != 4)
        {
            Log.Print(LogType.Error, $"Invalid split size in 'GetLastSelectedCharacter' for account '{_accountName}'");
            return null;
        }
        
        return (content[0], content[1], ulong.Parse(content[3]), long.Parse(content[2]));
    }

    public void SaveLastSelectedCharacter(string realmName, string charName, ulong charLowerGuid, long lastLoginUnixSec)
    {
        var dir = GetAccountMetaDataDirectory();
        var path = Path.Combine(dir, LAST_CHARACTER_FILE);

        File.WriteAllText(path, $"{realmName},{charName},{charLowerGuid},{lastLoginUnixSec}", Encoding.UTF8);
        Log.Print(LogType.Debug, $"Saved last selected char in '{path}'");
    }

    public void InvalidateLastSelectedCharacter()
    {
        var dir = GetAccountMetaDataDirectory();
        var path = Path.Combine(dir, LAST_CHARACTER_FILE);

        if (!File.Exists(path))
            return;

        File.WriteAllText(path, "");
        Log.Print(LogType.Debug, $"Invalidated last selected character entry in '{path}'");
    }

    public List<uint> GetAllCompletedQuests(string realmName, string charName)
    {
        var dir = GetAccountCharacterMetaDataDirectory(realmName, charName);
        var path = Path.Combine(dir, COMPLETED_QUESTS_FILE);

        if (!File.Exists(path))
            return new List<uint>();

        List<string> lines = File.ReadAllLines(path).ToList();

        var completedQuestIds = lines.Select(x => uint.Parse(x.Split(',').FirstOrDefault() ?? "0")).ToList();
        return completedQuestIds;
    }

    public void MarkQuestAsCompleted(string realmName, string charName, uint questId)
    {
        var dir = GetAccountCharacterMetaDataDirectory(realmName, charName);
        var path = Path.Combine(dir, COMPLETED_QUESTS_FILE);

        var when = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        File.AppendAllLines(path, new[]{$"{questId},{when}"}, Encoding.UTF8);
    }

    public void MarkQuestAsNotCompleted(string realmName, string charName, uint questId)
    {
        var dir = GetAccountCharacterMetaDataDirectory(realmName, charName);
        var path = Path.Combine(dir, COMPLETED_QUESTS_FILE);

        string needle = questId.ToString();
        List<string> lines = File.ReadAllLines(path).ToList();
        lines.RemoveAll(l => l.Split(',').FirstOrDefault()?.Equals(needle) ?? false);
        File.WriteAllLines(path, lines);
    }

    public void SaveCharacterSettingsStorage(string realmName, string charName, PlayerSettings.InternalStorage settings)
    {
        var dir = GetAccountCharacterMetaDataDirectory(realmName, charName);
        var path = Path.Combine(dir, SETTINGS_FILE);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var jsonString = JsonSerializer.Serialize(settings, options);
        File.WriteAllText(path, jsonString, Encoding.UTF8);
    }

    public PlayerSettings.InternalStorage LoadCharacterSettingsStorage(string realmName, string charName)
    {
        var dir = GetAccountCharacterMetaDataDirectory(realmName, charName);
        var path = Path.Combine(dir, SETTINGS_FILE);

        if (!File.Exists(path))
        {
            var fallback = new PlayerSettings.InternalStorage();
            SaveCharacterSettingsStorage(realmName, charName, fallback);
            return fallback; // Default fallback
        }

        var jsonString = File.ReadAllText(path, Encoding.UTF8);
        var loadedJson = JsonSerializer.Deserialize<PlayerSettings.InternalStorage>(jsonString);

        return loadedJson!;
    }

    //MIRASU - serializes concurrent calls to SaveQuestItemProgress so the temp-file/rename pair
    //MIRASU   isn't interleaved between the WorldClient thread (item credit) and the server thread
    //MIRASU   (abandon-clear). One static lock per process is fine: file I/O is fast and contention
    //MIRASU   is low (only this character's session writes through here per proxy instance).
    private static readonly object _questItemProgressFileLock = new();

    //MIRASU - persist quest item running totals to disk for this character. Atomic write via
    //MIRASU   temp-file + rename so a crash mid-write can't corrupt the file. Called eagerly from
    //MIRASU   GlobalSessionData on every pickup increment and on COMPLETE/FAILED/abandon so the
    //MIRASU   disk file always reflects the latest state -- the proxy can be killed mid-session
    //MIRASU   (launcher kill on game close) and the next start sees an up-to-date file.
    public void SaveQuestItemProgress(string realmName, string charName, IEnumerable<KeyValuePair<(uint QuestID, sbyte StorageIndex), uint>> entries)
    {
        var dir = GetAccountCharacterMetaDataDirectory(realmName, charName);
        var path = Path.Combine(dir, QUEST_ITEM_PROGRESS_FILE);
        var tmp = path + ".tmp";

        var dto = new QuestItemProgressFile
        {
            SchemaVersion = 1,
            Entries = entries.Select(kvp => new QuestItemProgressEntry
            {
                QuestId = kvp.Key.QuestID,
                StorageIndex = kvp.Key.StorageIndex,
                Count = kvp.Value,
            }).ToList(),
        };
        var jsonString = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        lock (_questItemProgressFileLock)
        {
            File.WriteAllText(tmp, jsonString, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
    }

    //MIRASU - load quest item running totals from disk for this character. Returns null if no
    //MIRASU   file exists or the file is corrupt / from a future schema; caller treats null as
    //MIRASU   "no saved progress" (which is also the cold-start state).
    public Dictionary<(uint QuestID, sbyte StorageIndex), uint>? LoadQuestItemProgress(string realmName, string charName)
    {
        var dir = GetAccountCharacterMetaDataDirectory(realmName, charName);
        var path = Path.Combine(dir, QUEST_ITEM_PROGRESS_FILE);
        if (!File.Exists(path))
            return null;

        try
        {
            var jsonString = File.ReadAllText(path, Encoding.UTF8);
            var dto = JsonSerializer.Deserialize<QuestItemProgressFile>(jsonString);
            if (dto == null || dto.SchemaVersion != 1)
                return null;
            var result = new Dictionary<(uint QuestID, sbyte StorageIndex), uint>(dto.Entries.Count);
            foreach (var entry in dto.Entries)
                result[(entry.QuestId, entry.StorageIndex)] = entry.Count;
            return result;
        }
        catch (Exception ex)
        {
            Log.Print(LogType.Error, $"Failed to load quest item progress for '{charName}@{realmName}': {ex.Message}");
            return null;
        }
    }
}

//MIRASU - DTOs for quest item progress disk persistence. Kept as a flat list rather than a dict
//MIRASU   because System.Text.Json doesn't natively serialize tuple-keyed dictionaries.
public class QuestItemProgressFile
{
    public int SchemaVersion { get; set; }
    public List<QuestItemProgressEntry> Entries { get; set; } = new();
}

public class QuestItemProgressEntry
{
    public uint QuestId { get; set; }
    public sbyte StorageIndex { get; set; }
    public uint Count { get; set; }
}

public class AccountData
{
    public WowGuid128 Guid;
    public long Timestamp;
    public uint Type;
    public uint UncompressedSize;
    public byte[] CompressedData = null!;
}
public class AccountDataManager
{
    public AccountData[] Data = null!;
    string _accountName;
    string _realmName;
    
    public AccountDataManager(string accountName, string realmName)
    {
        _accountName = accountName;
        _realmName = realmName.Trim();
    }

    public static bool IsGlobalDataType(uint type)
    {
        switch ((AccountDataType)type)
        {
            case AccountDataType.GlobalConfigCache:
            case AccountDataType.GlobalBindingsCache:
            case AccountDataType.GlobalMacrosCache:
            case AccountDataType.GlobalTTSCache:
            case AccountDataType.GlobalFlaggedCache:
                return true;
        }
        return false;
    }

    public string GetAccountDataDirectory()
    {
        string path = Path.GetFullPath(Path.Combine("AccountData", _accountName, _realmName));

        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);

        return path;
    }

    public string GetFullFileName(WowGuid128 guid, uint type)
    {
        string file;
        if (IsGlobalDataType(type))
            file = $"data-{type}.bin";
        else
            file = $"data-{type}-{guid.GetLowValue()}-{guid.GetHighValue()}.bin";

        string path = GetAccountDataDirectory();
        path = Path.Combine(path, file);
        return path;
    }

    public void LoadAllData(WowGuid128 guid)
    {
        Data = new AccountData[ModernVersion.GetAccountDataCount()];

        for (uint i = 0; i < ModernVersion.GetAccountDataCount(); i++)
        {
            Data[i] = LoadData(guid, i)!;
        }
    }

    public AccountData? LoadData(WowGuid128 guid, uint type)
    {
        string fileName = GetFullFileName(guid, type);
        if (!File.Exists(fileName))
            return null;

        try
        {
            using BinaryReader reader = new BinaryReader(File.OpenRead(fileName));
            AccountData data = new();
            ulong guidLow = reader.ReadUInt64();
            ulong guidHigh = reader.ReadUInt64();
            data.Guid = new WowGuid128(guidLow, guidHigh);

            if (!IsGlobalDataType(type) && guid != data.Guid)
            {
                Log.Print(LogType.Warn, $"AccountData cache '{fileName}' has wrong GUID ({data.Guid} vs expected {guid}); discarding.");
                return null;
            }

            data.Timestamp = reader.ReadInt64();
            data.Type = reader.ReadUInt32();
            if (type != data.Type)
            {
                Log.Print(LogType.Warn, $"AccountData cache '{fileName}' has wrong type ({data.Type} vs expected {type}); discarding.");
                return null;
            }
            data.UncompressedSize = reader.ReadUInt32();

            int compressedSize = reader.ReadInt32();
            data.CompressedData = reader.ReadBytes(compressedSize);
            return data;
        }
        catch (Exception ex)
        {
            Log.Print(LogType.Warn, $"AccountData cache '{fileName}' is corrupt or unreadable ({ex.GetType().Name}: {ex.Message}); discarding. A fresh file will be written on next save.");
            return null;
        }
    }

    public void SaveData(WowGuid128 guid, long timestamp, uint type, uint uncompressedSize, byte[] compressedData)
    {
        if (compressedData == null)
            return;
        if (Data[type] == null)
            Data[type] = new();

        Data[type].Guid = guid;
        Data[type].Timestamp = timestamp;
        Data[type].Type = type;
        Data[type].UncompressedSize = uncompressedSize;
        Data[type].CompressedData = compressedData;

        string finalPath = GetFullFileName(guid, type);
        string tempPath = finalPath + ".tmp";

        using (BinaryWriter writer = new BinaryWriter(File.Open(tempPath, FileMode.Create)))
        {
            writer.Write(guid.GetLowValue());
            writer.Write(guid.GetHighValue());
            writer.Write(timestamp);
            writer.Write(type);
            writer.Write(uncompressedSize);
            writer.Write(compressedData.Length);
            writer.Write(compressedData);
        }
        File.Move(tempPath, finalPath, overwrite: true);
    }

    public byte[] LoadCUFProfiles()
    {
        string fileName = Path.Combine(GetAccountDataDirectory(), "cuf.bin");

        if (File.Exists(fileName))
        {
            using (FileStream file = File.OpenRead(fileName))
            {
                using (BinaryReader reader = new BinaryReader(file))
                {
                    return File.ReadAllBytes(fileName);
                }
            }
        }

        return new byte[4];
    }

    public void SaveCUFProfiles(byte[] data)
    {
        string fileName = Path.Combine(GetAccountDataDirectory(), "cuf.bin");

        using (BinaryWriter writer = new BinaryWriter(File.Open(fileName, FileMode.Create)))
        {
            writer.Write(data);
        }
    }
}
