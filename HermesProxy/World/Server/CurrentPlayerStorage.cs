using System.Collections.Generic;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Server;

public class CurrentPlayerStorage
{
    private readonly GlobalSessionData _globalSession;
    public CompletedQuestTracker CompletedQuests { get; private set; } = null!;
    public PlayerSettings Settings { get; private set; } = null!;

    public CurrentPlayerStorage(GlobalSessionData globalSession)
    {
        _globalSession = globalSession;
    }

    public void LoadCurrentPlayer()
    {
        CompletedQuests = new CompletedQuestTracker(_globalSession);
        Settings = new PlayerSettings(_globalSession);
        CompletedQuests.Reload();
        Settings.Reload();
    }
}

public class PlayerSettings
{
    private InternalStorage _internalStorage = null!;
    private PlayerFlags _lastCapturedFlags;

    public bool NeedToForcePatchFlags { get; private set; }


    public GlobalSessionData Session { get; }

    public PlayerSettings(GlobalSessionData globalSession)
    {
        Session = globalSession;
    }

    public void SetAutoBlockGuildInvites(bool value)
    {
        _internalStorage.AutoBlockGuildInvites = value;
        NeedToForcePatchFlags = true;
        Save();
    }

    public void PatchFlags(ref PlayerFlags flags)
    {
        _lastCapturedFlags = flags;
        NeedToForcePatchFlags = false;

        if (_internalStorage.AutoBlockGuildInvites)
            flags |= PlayerFlags.AutoDeclineGuild;
        else
            flags &= ~(PlayerFlags.AutoDeclineGuild);
    }

    public PlayerFlags CreateNewFlags()
    {
        var flags = _lastCapturedFlags;
        PatchFlags(ref flags);
        return flags;
    }

    private void Save()
    {
        Session.AccountMetaDataMgr.SaveCharacterSettingsStorage(Session.GameState.CurrentPlayerInfo!.Realm.Name, Session.GameState.CurrentPlayerInfo!.Name!, _internalStorage);
    }
    
    public class InternalStorage
    {
        // A JSON encoder / decoder is used to store the settings
        // Make use of a public { get; set; } Property so that the JSON serializer can change it

        // The player can request a change in the Interface settings
        // but the actual value has to be reflected in the local CharacterFlags
        public bool AutoBlockGuildInvites { get; set; }
    }

    public void Reload()
    {
        _internalStorage = Session.AccountMetaDataMgr.LoadCharacterSettingsStorage(Session.GameState.CurrentPlayerInfo!.Realm.Name, Session.GameState.CurrentPlayerInfo!.Name!);
    }
}

public class CompletedQuestTracker
{
    private Dictionary<int, ulong> _cachedQuestCompleted = new();

    // Every completed quest ID currently on disk. Used to avoid appending a duplicate line for a
    // repeatable turn-in -- the file is only read back as a set of IDs, so without this it grew
    // unbounded (e.g. Argent Dawn Scourgestone grinds add hundreds of rows for the same quest).
    private readonly HashSet<uint> _completedQuestIds = new();

    public GlobalSessionData Session { get; }

    public CompletedQuestTracker(GlobalSessionData globalSession)
    {
        Session = globalSession;
    }

    public void MarkQuestAsNotCompleted(uint questQuestId)
    {
        _completedQuestIds.Remove(questQuestId);
        Session.AccountMetaDataMgr.MarkQuestAsNotCompleted(Session.GameState.CurrentPlayerInfo!.Realm.Name, Session.GameState.CurrentPlayerInfo!.Name!, questQuestId);

        var questBit = GameData.GetUniqueQuestBit(questQuestId);
        if (questBit.HasValue)
        {
            SendSingleUpdateToClient(questBit.Value, false);
        }
    }

    public void MarkQuestAsCompleted(uint questQuestId)
    {
        // Only persist the first completion of a (possibly repeatable) quest -- re-appending a
        // repeatable turn-in just bloats the file, which is only read back as a set of IDs.
        if (_completedQuestIds.Add(questQuestId))
        {
            Session.AccountMetaDataMgr.MarkQuestAsCompleted(Session.GameState.CurrentPlayerInfo!.Realm.Name, Session.GameState.CurrentPlayerInfo!.Name!, questQuestId);
        }

        var questBit = GameData.GetUniqueQuestBit(questQuestId);
        if (questBit.HasValue)
        {
            SendSingleUpdateToClient(questBit.Value, true);
        }
    }

    public void Reload()
    {
        var questIds = Session.AccountMetaDataMgr.GetAllCompletedQuests(Session.GameState.CurrentPlayerInfo!.Realm.Name, Session.GameState.CurrentPlayerInfo!.Name!);

        _cachedQuestCompleted = new Dictionary<int, ulong>();
        _completedQuestIds.Clear();
        foreach (uint questId in questIds)
        {
            _completedQuestIds.Add(questId);

            uint? questBit = GameData.GetUniqueQuestBit(questId);
            if (!questBit.HasValue)
                continue;

            int idx = (int)(((questBit - 1) >> 6));
            int bitIdx = (int)((questBit - 1) & 63);
            _cachedQuestCompleted.TryAdd(idx, 0);
            _cachedQuestCompleted[idx] |= ((ulong)1) << bitIdx;
        }
    }
    
    private void SendSingleUpdateToClient(uint questBit, bool isSet)
    {
        int idx = (int)(((questBit - 1) >> 6));
        int bitIdx = (int)((questBit - 1) & 63);
        _cachedQuestCompleted.TryAdd(idx, 0);
        if (isSet)
            _cachedQuestCompleted[idx] |= ((ulong)1) << bitIdx;
        else
            _cachedQuestCompleted[idx] &= ~(((ulong)1) << bitIdx);
        
        ObjectUpdate updateData = new ObjectUpdate(Session.GameState.CurrentPlayerGuid, UpdateTypeModern.Values, Session);
        updateData.ActivePlayerData.QuestCompleted[idx] = _cachedQuestCompleted[idx];

        UpdateObject updatePacket = new UpdateObject(Session.GameState);
        updatePacket.ObjectUpdates.Add(updateData);
        Session.WorldClient!.SendPacketToClient(updatePacket);
    }

    public void WriteAllCompletedIntoArray(ulong?[] dest)
    {
        foreach (var kv in _cachedQuestCompleted)
        {
            dest[kv.Key] = kv.Value;
        }
    }
}
