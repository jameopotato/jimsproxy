using HermesProxy.Auth;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Threading;
using Framework.Realm;
using HermesProxy.World.Server.Packets;
using ArenaTeamInspectData = HermesProxy.World.Server.Packets.ArenaTeamInspectData;
using System;

namespace HermesProxy;

public class PlayerCache
{
    public string? Name;
    public Race RaceId = Race.None;
    public Class ClassId = Class.None;
    public Gender SexId = Gender.None;
    public byte Level = 0;
}

public sealed class OwnCharacterInfo : PlayerCache
{
    public WowGuid128 AccountId;
    public WowGuid128 CharacterGuid;
    public Realm Realm = null!;
    public ulong LastLoginUnixSec;
}

public sealed class TradeSession
{
    public static uint GlobalTradeIdCounter; // Fallback for pre 2.0.0 servers
    public uint TradeId;

    public WowGuid128 Partner;
    public WowGuid128 PartnerAccount;

    public uint ClientStateIndex = 1; // incremented for every update on our side
    public uint ServerStateIndex = 1; // incremented by any trade action
}

public sealed class GameSessionData
{
    public bool HasWsgHordeFlagCarrier;
    public bool HasWsgAllyFlagCarrier;
    public bool ChannelDisplayList;
    public bool ShowPlayedTime;
    public bool IsInFarSight;
    public bool IsInTaxiFlight;
    public bool IsWaitingForTaxiStart;
    public bool IsWaitingForNewWorld;
    public bool IsWaitingForWorldPortAck;
    public bool IsFirstEnterWorld;
    public bool IsConnectedToInstance;
    public Queue<ServerPacket> PendingUninstancedPackets = new(); // Here packets are queued while IsConnectedToInstance = false;
    public readonly Lock PendingUninstancedPacketsLock = new();
    public bool IsInWorld;
    public uint? CurrentMapId;
    public uint CurrentZoneId;
    public uint CurrentTaxiNode;
    public List<byte> UsableTaxiNodes = [];
    public uint PendingTransferMapId;
    public uint LastEnteredAreaTrigger;
    public uint LastDispellSpellId;
    public Dictionary<WowGuid128, uint[]> CachedPlayerEnchants = new();
    // JimsProxy: per-unit HP cache used to compute overhealing on legacy servers
    // that don't include OverHeal in SMSG_SPELL_HEAL_LOG (1.12 vanilla). Authoritative
    // source is UNIT_FIELD_HEALTH / UNIT_FIELD_MAXHEALTH from SMSG_UPDATE_OBJECT;
    // we also bump current HP forward on heal events to stay accurate between pushes.
    public ConcurrentDictionary<WowGuid128, (int Hp, int MaxHp)> UnitHealthCache = new();
    public string LeftChannelName = "";
    public bool IsPassingOnLoot;
    public int GroupUpdateCounter;
    public uint GroupReadyCheckResponses;
    public World.Server.Packets.PartyUpdate?[] CurrentGroups = new World.Server.Packets.PartyUpdate?[2];
    public bool WeWantToLeaveGroup; // Only send kick message when we dont initiated the group-leave
    public List<OwnCharacterInfo> OwnCharacters = [];
    public WowGuid128 CurrentPlayerGuid;
    public long CurrentPlayerCreateTime;
    public OwnCharacterInfo? CurrentPlayerInfo;
    public CurrentPlayerStorage CurrentPlayerStorage = null!;
    public uint CurrentGuildCreateTime;
    public uint CurrentGuildNumAccounts;
    public WowGuid128 CurrentInteractedWithNPC;
    public WowGuid128 CurrentInteractedWithGO;
    public uint LastWhoRequestId;
    public WowGuid128 CurrentPetGuid;
    public WowGuid64 CurrentAttackTarget;        // active CMSG_ATTACK_SWING victim, cleared on ATTACK_STOP/CANCEL_COMBAT
    public bool WaitingForAttackStart;           // true between CMSG_ATTACK_SWING and SMSG_ATTACK_START
    public bool DeferredAttackStop;              // CMSG_ATTACK_STOP received while waiting for SMSG_ATTACK_START
    public uint[] CurrentArenaTeamIds = new uint[3];
    public ConcurrentQueue<ClientCastRequest> PendingNormalCasts = new();  // regular spell casts (queue for proper FIFO handling)
    public ClientCastRequest? CurrentClientNextMeleeCast; // next melee spells (Raptor Strike, Heroic Strike, etc.)
    public ClientCastRequest? CurrentClientAutoRepeatCast; // auto repeat spells (Auto Shot, Shoot, etc.)
    public ConcurrentQueue<ClientCastRequest> PendingPetCasts = new();  // pet spell casts (queue for proper FIFO handling)
    // JimsProxy (issue #43): serializes the drain-filter-rebuild helpers below with the
    // ThreadPool-thread Enqueue in WorldSocket.ForwardHeldGcdCast. Without this, the timer
    // thread can enqueue a held cast mid-drain, causing the drain to observe the new item
    // out-of-order and possibly return it as a FIFO match for an unrelated SMSG_SPELL_GO.
    // Pre-existing Enqueues from the network-thread CMSG handlers stay lock-free (same-thread
    // semantics as before this PR); only the new cross-thread path takes the lock.
    internal readonly object PendingCastsLock = new();

    // JimsProxy (issue #43): GCD hold-and-fire state. While the player is on a GCD (tracked
    // from SMSG_SPELL_GO), new CMSG_CAST_SPELL presses are held in _heldGcdCast instead of
    // flooding the server. At GCD expiry a Timer fires the most-recent held cast via the
    // OnGcdHeldCastFire callback set by WorldSocket.
    private readonly object _gcdLock = new();
    private long _gcdExpireTimestampMs;                 // 0 = no GCD active; Environment.TickCount64 baseline
    private ClientCastRequest? _heldGcdCast;            // most recently-pressed cast while GCD active (overwritten on new press)
    private Timer? _gcdExpiryTimer;
    private uint _gcdGeneration;                        // incremented each BeginGcd; callback compares against its captured generation to detect stale fires
    public Action<ClientCastRequest>? OnGcdHeldCastFire; // set by WorldSocket at attach time; invoked on a ThreadPool thread at GCD expiry
    //MIRASU - Tracks the unique CastID assigned to in-flight non-player casts so SPELL_GO and
    //MIRASU   SPELL_FAILED_OTHER can reference the same cast the SPELL_START introduced.
    //MIRASU   Without this, mob casts reuse a deterministic CastID (spellId+casterCounter) on
    //MIRASU   every cycle and the modern client treats consecutive casts as the same in-flight
    //MIRASU   cast -- visuals/sounds drift and target-frame cast bars don't dismiss on kick.
    public ConcurrentDictionary<(WowGuid128 caster, uint spellId), WowGuid128> OtherCasterActiveCastIds = new();
    //MIRASU - monotonic sequence used to make non-player CastIDs unique per cast.
    public int OtherCastSequenceCounter;
    // Tracks last-seen UNIT_CHANNEL_SPELL per unit so we can synthesize
    // SMSG_SPELL_CHANNEL_START/UPDATE for observers (vanilla only sends
    // MSG_CHANNEL_START to the caster, not to nearby players).
    public ConcurrentDictionary<WowGuid128, int> UnitChannelSpells = new();
    public WowGuid64 LastLootTargetGuid;
    public Dictionary<(uint QuestID, sbyte StorageIndex), uint> QuestItemObjectiveProgress = new(); //MIRASU - proxy-local running totals for quest item pickups; legacy update-field cache isn't refreshed on partial updates, so we track ourselves
    public uint CurrentLootCoins; //MIRASU - remembers coin amount from SMSG_LOOT_RESPONSE so proxy can synthesize SMSG_LOOT_MONEY_NOTIFY when client picks up gold (Kronos/TC-1.12 doesn't emit it)
    public List<WowGuid128>? MasterLootCandidates;
    public WowGuid64 LastMasterLootSentTarget;
    public List<int> ActionButtons = [];
    public Dictionary<WowGuid128, Dictionary<byte, int>> UnitAuraDurationUpdateTime = [];
    public Dictionary<WowGuid128, Dictionary<byte, int>> UnitAuraDurationLeft = [];
    public Dictionary<WowGuid128, Dictionary<byte, int>> UnitAuraDurationFull = [];
    public Dictionary<WowGuid128, Dictionary<byte, WowGuid128>> UnitAuraCaster = [];
    public Dictionary<WowGuid128, PlayerCache> CachedPlayers = [];
    public HashSet<WowGuid128> IgnoredPlayers = [];
    public Dictionary<WowGuid128, uint> PlayerGuildIds = [];
    public readonly Lock ObjectCacheLock = new();
    public Dictionary<WowGuid128, Dictionary<int, UpdateField>> ObjectCacheLegacy = [];
    public Dictionary<WowGuid128, UpdateFieldsArray> ObjectCacheModern = [];
    public Dictionary<WowGuid128, ObjectType> OriginalObjectTypes = [];
    public Dictionary<WowGuid128, uint[]> ItemGems = [];
    public Dictionary<uint, Class> CreatureClasses = [];
    public Dictionary<string, int> ChannelIds = [];
    public Dictionary<int, string> ChannelNamesById = [];
    public Dictionary<uint, uint> ItemBuyCount = [];
    public Dictionary<uint, uint> RealSpellToLearnSpell = [];
    public Dictionary<uint, ArenaTeamData> ArenaTeams = [];
    public World.Server.Packets.MailListResult? PendingMailListPacket;
    public HashSet<uint> RequestedItemTextIds = [];
    public Dictionary<uint, string> ItemTexts = [];
    public Dictionary<uint, uint> BattleFieldQueueTypes = [];
    public Dictionary<uint, long> BattleFieldQueueTimes = [];
    public Dictionary<uint, uint> DailyQuestsDone = [];
    public HashSet<WowGuid128> FlagCarrierGuids = [];
    public Dictionary<WowGuid64, ushort> ObjectSpawnCount = [];
    public HashSet<WowGuid64> DespawnedGameObjects = [];
    public HashSet<WowGuid128> HunterPetGuids = [];
    public Dictionary<WowGuid128, ArenaTeamInspectData[]> PlayerArenaTeams = [];
    public HashSet<string> AddonPrefixes = [];
    public Dictionary<byte, Dictionary<byte, int>> FlatSpellMods = [];
    public Dictionary<byte, Dictionary<byte, int>> PctSpellMods = [];
    public Dictionary<WowGuid128, Dictionary<uint, WowGuid128>> LastAuraCasterOnTarget = [];
    public TradeSession? CurrentTrade = null;
    public HashSet<uint> RequestedItemHotfixes = [];
    public HashSet<uint> RequestedItemSparseHotfixes = [];

    // Mobs we've seen send Flying spline or FixedZ movement flags. Vanilla servers
    // don't populate UNIT_FIELD_HOVERHEIGHT consistently (Twinstar e.g. leaves it at 0),
    // so we need a server-agnostic hover signal. Once a guid lands here, all subsequent
    // packets for it get the hover override regardless of HOVERHEIGHT.
    public HashSet<WowGuid128> KnownHoveringMobs = [];

    private GameSessionData()
    {

    }

    public static GameSessionData CreateNewGameSessionData(GlobalSessionData globalSession)
    {
        var self = new GameSessionData();
        self.CurrentPlayerStorage = new CurrentPlayerStorage(globalSession);
        return self;
    }

    /// <summary>
    /// Test-only factory — skips CurrentPlayerStorage initialization so tests that only need
    /// the GCD hold state machine (issue #43) can construct a bare GameSessionData without
    /// standing up a full GlobalSessionData graph.
    /// </summary>
    internal static GameSessionData CreateForTesting()
    {
        return new GameSessionData();
    }
    
    public uint GetCurrentGroupSize()
    {
        var group = GetCurrentGroup();
        if (group == null)
            return 0;

        // Don't count self.
        return (uint)(group.PlayerList.Count > 1 ? group.PlayerList.Count - 1 : 0);
    }
    public WowGuid128 GetCurrentGroupLeader()
    {
        var group = GetCurrentGroup();
        if (group == null)
            return WowGuid128.Empty;

        return group.LeaderGUID;
    }
    public LootMethod GetCurrentLootMethod()
    {
        var group = GetCurrentGroup();
        if (group == null)
            return LootMethod.FreeForAll;

        return group.LootSettings.Method;
    }
    public WowGuid128 GetCurrentGroupGuid()
    {
        var group = GetCurrentGroup();
        if (group == null)
            return WowGuid128.Empty;

        return group.PartyGUID;
    }
    public World.Server.Packets.PartyUpdate? GetCurrentGroup()
    {
        return CurrentGroups[GetCurrentPartyIndex()];
    }
    public sbyte GetCurrentPartyIndex()
    {
        return (sbyte)(IsInBattleground() ? 1 : 0);
    }
    public byte GetItemSpellSlot(WowGuid128 guid, uint spellId)
    {
        int OBJECT_FIELD_ENTRY = LegacyVersion.GetUpdateField(ObjectField.OBJECT_FIELD_ENTRY);
        if (OBJECT_FIELD_ENTRY < 0)
            return 0;

        var updates = GetCachedObjectFieldsLegacy(guid);
        if (updates == null)
            return 0;

        uint itemId = updates[OBJECT_FIELD_ENTRY].UInt32Value;
        return GameData.GetItemEffectSlot(itemId, spellId);
    }
    /// <summary>
    /// If the modern client sent a spell id that the legacy server doesn't know for this item
    /// (e.g. SoM 1.14.1+ renumbered Diamond Flask 17626 → 363880), resolve the legacy spell id
    /// from the item's cached ItemEffects (slot 0 = on-use trinket/potion entry).
    /// Returns 0 when no remap is needed (modern id == legacy id) or when item data isn't cached yet.
    /// </summary>
    public uint GetLegacyItemSpellId(WowGuid128 itemGuid, uint modernSpellId)
    {
        uint itemId = GetItemId(itemGuid);
        if (itemId == 0)
            return 0;

        var slotMap = GameData.GetItemEffectSlotMap(itemId);
        if (slotMap == null)
            return 0;

        // Modern spell id is already known to the legacy server — no remap needed.
        if (slotMap.ContainsKey(modernSpellId))
            return 0;

        // On-use items keep their effect at slot 0; return that legacy spell id.
        foreach (var kvp in slotMap)
        {
            if (kvp.Value == 0)
            {
                // Also remember the legacy → modern direction so subsequent aura updates
                // (which carry the legacy spell id) can be translated back to the modern id
                // the client recognizes — otherwise the buff icon never appears next to the minimap.
                // We learn it here from the client's actual CMSG_USE_ITEM rather than relying on
                // ItemEffect CSV data, which can be stale for SoM-renumbered items.
                GameData.LegacyToModernSpellId[kvp.Key] = modernSpellId;
                return kvp.Key;
            }
        }
        return 0;
    }
    public uint GetItemId(WowGuid128 guid)
    {
        int OBJECT_FIELD_ENTRY = LegacyVersion.GetUpdateField(ObjectField.OBJECT_FIELD_ENTRY);
        if (OBJECT_FIELD_ENTRY < 0)
            return 0;

        var updates = GetCachedObjectFieldsLegacy(guid);
        if (updates == null)
            return 0;

        uint itemId = updates[OBJECT_FIELD_ENTRY].UInt32Value;
        return itemId;
    }
    public void SetFlatSpellMod(byte spellMod, byte spellMask, int amount)
    {
        ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(FlatSpellMods, spellMod, out _);
        dict ??= [];
        dict[spellMask] = amount;
    }
    public void SetPctSpellMod(byte spellMod, byte spellMask, int amount)
    {
        ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(PctSpellMods, spellMod, out _);
        dict ??= [];
        dict[spellMask] = amount;
    }
    public ArenaTeamInspectData GetArenaTeamDataForPlayer(WowGuid128 guid, byte slot)
    {
        if (PlayerArenaTeams.TryGetValue(guid, out var teams) && teams[slot] != null)
            return teams[slot];

        return new ArenaTeamInspectData();
    }
    public void StoreArenaTeamDataForPlayer(WowGuid128 guid, byte slot, ArenaTeamInspectData team)
    {
        ref var teams = ref CollectionsMarshal.GetValueRefOrAddDefault(PlayerArenaTeams, guid, out _);
        teams ??= new ArenaTeamInspectData[ArenaTeamConst.MaxArenaSlot];
        teams[slot] = team;
    }
    public WowGuid64 GetInventorySlotItem(int slot)
    {
        int PLAYER_FIELD_INV_SLOT_HEAD = LegacyVersion.GetUpdateField(PlayerField.PLAYER_FIELD_INV_SLOT_HEAD);
        if (PLAYER_FIELD_INV_SLOT_HEAD >= 0)
        {
            var updates = GetCachedObjectFieldsLegacy(CurrentPlayerGuid);
            if (updates != null)
                return updates.GetGuidValue(PLAYER_FIELD_INV_SLOT_HEAD + slot * 2).To64();
        }
        return WowGuid64.Empty;
    }
    public WowGuid64 GetInventorySlotItem(byte containerSlot, byte slot)
    {
        // Main backpack: read directly from player inventory fields
        if (containerSlot == ItemConst.NullSlot)
            return GetInventorySlotItem(slot);

        // Extra bag: read from the bag container's slot fields
        var bagGuid64 = GetInventorySlotItem(containerSlot);
        if (bagGuid64 == WowGuid64.Empty)
            return WowGuid64.Empty;

        int containerSlotField = LegacyVersion.GetUpdateField(ContainerField.CONTAINER_FIELD_SLOT_1);
        if (containerSlotField < 0)
            return WowGuid64.Empty;

        var bagGuid128 = bagGuid64.To128(this);
        var bagFields = GetCachedObjectFieldsLegacy(bagGuid128);
        if (bagFields == null)
            return WowGuid64.Empty;

        return bagFields.GetGuidValue(containerSlotField + slot * 2);
    }
    public uint GetItemStackCount(WowGuid128 itemGuid)
    {
        uint count = GetLegacyFieldValueUInt32(itemGuid, ItemField.ITEM_FIELD_STACK_COUNT);
        return count > 0 ? count : 1;
    }
    public (byte containerSlot, byte slot)? FindItemInInventory(WowGuid64 itemGuid64)
    {
        // Search main backpack
        for (int i = World.Enums.Vanilla.InventorySlots.ItemStart; i < World.Enums.Vanilla.InventorySlots.ItemEnd; i++)
        {
            if (GetInventorySlotItem(i) == itemGuid64)
                return (ItemConst.NullSlot, (byte)i);
        }

        // Search extra bag containers
        int containerSlotField = LegacyVersion.GetUpdateField(ContainerField.CONTAINER_FIELD_SLOT_1);
        int numSlotsField = LegacyVersion.GetUpdateField(ContainerField.CONTAINER_FIELD_NUM_SLOTS);
        if (containerSlotField < 0 || numSlotsField < 0)
            return null;

        for (int bagIdx = World.Enums.Vanilla.InventorySlots.BagStart; bagIdx < World.Enums.Vanilla.InventorySlots.BagEnd; bagIdx++)
        {
            var bagGuid64 = GetInventorySlotItem(bagIdx);
            if (bagGuid64 == WowGuid64.Empty)
                continue;

            var bagGuid128 = bagGuid64.To128(this);
            var bagFields = GetCachedObjectFieldsLegacy(bagGuid128);
            if (bagFields == null)
                continue;

            if (!bagFields.TryGetValue(numSlotsField, out var numSlotsValue))
                continue;
            int numSlots = (int)numSlotsValue.UInt32Value;

            for (int slot = 0; slot < numSlots; slot++)
            {
                var slotGuid = bagFields.GetGuidValue(containerSlotField + slot * 2);
                if (slotGuid == itemGuid64)
                    return ((byte)bagIdx, (byte)slot);
            }
        }

        return null;
    }
    public (byte containerSlot, byte slot)? FindEmptyInventorySlot()
    {
        // Search main backpack first
        for (int i = World.Enums.Vanilla.InventorySlots.ItemStart; i < World.Enums.Vanilla.InventorySlots.ItemEnd; i++)
        {
            if (GetInventorySlotItem(i) == WowGuid64.Empty)
                return (ItemConst.NullSlot, (byte)i);
        }

        // Search extra bag containers
        int containerSlotField = LegacyVersion.GetUpdateField(ContainerField.CONTAINER_FIELD_SLOT_1);
        int numSlotsField = LegacyVersion.GetUpdateField(ContainerField.CONTAINER_FIELD_NUM_SLOTS);
        if (containerSlotField < 0 || numSlotsField < 0)
            return null;

        for (int bagIdx = World.Enums.Vanilla.InventorySlots.BagStart; bagIdx < World.Enums.Vanilla.InventorySlots.BagEnd; bagIdx++)
        {
            var bagGuid64 = GetInventorySlotItem(bagIdx);
            if (bagGuid64 == WowGuid64.Empty)
                continue;

            var bagGuid128 = bagGuid64.To128(this);
            var bagFields = GetCachedObjectFieldsLegacy(bagGuid128);
            if (bagFields == null)
                continue;

            if (!bagFields.TryGetValue(numSlotsField, out var numSlotsValue))
                continue;
            int numSlots = (int)numSlotsValue.UInt32Value;

            for (int slot = 0; slot < numSlots; slot++)
            {
                var slotGuid = bagFields.GetGuidValue(containerSlotField + slot * 2);
                if (slotGuid == WowGuid64.Empty)
                    return ((byte)bagIdx, (byte)slot);
            }
        }

        return null;
    }
    public ushort GetObjectSpawnCounter(WowGuid64 guid)
    {
        if (ObjectSpawnCount.TryGetValue(guid, out ushort count))
            return count;
        return 0;
    }
    public void IncrementObjectSpawnCounter(WowGuid64 guid)
    {
        ref ushort count = ref CollectionsMarshal.GetValueRefOrAddDefault(ObjectSpawnCount, guid, out bool existed);
        if (existed)
            count++;
        // else: default(ushort) = 0, matching the original "Add(guid, 0)" behavior.
    }
    public void SetDailyQuestSlot(uint slot, uint questId)
    {
        if (questId != 0)
            DailyQuestsDone[slot] = questId;
        else
            DailyQuestsDone.Remove(slot);
    }
    public bool IsAlliancePlayer(WowGuid128 guid)
    {
        PlayerCache? cache;
        if (CachedPlayers.TryGetValue(guid, out cache))
            return GameData.IsAllianceRace(cache.RaceId);
        return false;
    }
    public bool IsInBattleground()
    {
        if (CurrentMapId == null)
            return false;

        uint bgId = GameData.GetBattlegroundIdFromMapId((uint)CurrentMapId);
        if (bgId == 0)
        {
            return false;
        }

        // Only if we are properly queued for the BG.
        foreach (var queue in BattleFieldQueueTypes)
        {
            if (LegacyVersion.RemovedInVersion(Enums.ClientVersionBuild.V2_0_1_6180))
            {
                if (queue.Value == CurrentMapId)
                    return true;
            }
            else
            {
                if (queue.Value == bgId)
                    return true;
            }
        }

        return false;
    }
    public long GetBattleFieldQueueTime(uint queueSlot)
    {
        if (BattleFieldQueueTimes.TryGetValue(queueSlot, out var time))
            return time;

        time = Time.UnixTime;
        BattleFieldQueueTimes.Add(queueSlot, time);
        return time;
    }
    public void StoreBattleFieldQueueType(uint queueSlot, uint mapOrBgId)
    {
        BattleFieldQueueTypes[queueSlot] = mapOrBgId;
    }
    public uint GetBattleFieldQueueType(uint queueSlot)
    {
        return BattleFieldQueueTypes.TryGetValue(queueSlot, out var value) ? value : 0u;
    }
    public void StoreAuraDurationLeft(WowGuid128 guid, byte slot, int duration, int currentTime)
    {
        ref var leftDict = ref CollectionsMarshal.GetValueRefOrAddDefault(UnitAuraDurationLeft, guid, out _);
        leftDict ??= [];
        leftDict[slot] = duration;

        ref var timeDict = ref CollectionsMarshal.GetValueRefOrAddDefault(UnitAuraDurationUpdateTime, guid, out _);
        timeDict ??= [];
        timeDict[slot] = currentTime;
    }
    public void StoreAuraDurationFull(WowGuid128 guid, byte slot, int duration)
    {
        ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(UnitAuraDurationFull, guid, out _);
        dict ??= [];
        dict[slot] = duration;
    }
    public void ClearAuraDuration(WowGuid128 guid, byte slot)
    {
        if (UnitAuraDurationUpdateTime.TryGetValue(guid, out var timeDict))
            timeDict.Remove(slot);

        if (UnitAuraDurationLeft.TryGetValue(guid, out var leftDict))
            leftDict.Remove(slot);

        if (UnitAuraDurationFull.TryGetValue(guid, out var fullDict))
            fullDict.Remove(slot);
    }
    public void GetAuraDuration(WowGuid128 guid, byte slot, out int left, out int full)
    {
        left = -1;
        if (UnitAuraDurationLeft.TryGetValue(guid, out var leftDict) &&
            leftDict.TryGetValue(slot, out var leftVal))
            left = leftVal;

        full = left;
        if (UnitAuraDurationFull.TryGetValue(guid, out var fullDict) &&
            fullDict.TryGetValue(slot, out var fullVal))
            full = fullVal;

        if (left > 0 &&
            UnitAuraDurationUpdateTime.TryGetValue(guid, out var timeDict) &&
            timeDict.TryGetValue(slot, out var time))
            left -= Environment.TickCount - time;
    }
    public void StoreAuraCaster(WowGuid128 target, byte slot, WowGuid128 caster)
    {
        ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(UnitAuraCaster, target, out _);
        dict ??= [];
        dict[slot] = caster;
    }
    public void ClearAuraCaster(WowGuid128 guid, byte slot)
    {
        if (UnitAuraCaster.TryGetValue(guid, out var dict))
            dict.Remove(slot);
    }
    public WowGuid128 GetAuraCaster(WowGuid128 target, byte slot)
    {
        if (UnitAuraCaster.TryGetValue(target, out var dict) &&
            dict.TryGetValue(slot, out var caster))
            return caster;

        return default;
    }
    public WowGuid128 GetAuraCaster(WowGuid128 target, byte slot, uint spellId)
    {
        WowGuid128 caster = GetAuraCaster(target, slot);
        if (caster == default)
        {
            caster = GetLastAuraCasterOnTarget(target, spellId);
            if (caster != default)
                StoreAuraCaster(target, slot, caster);
        }

        return caster;
    }
    public void StoreLastAuraCasterOnTarget(WowGuid128 target, uint spellId, WowGuid128 caster)
    {
        ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(LastAuraCasterOnTarget, target, out _);
        dict ??= [];
        dict[spellId] = caster;
    }
    public WowGuid128 GetLastAuraCasterOnTarget(WowGuid128 target, uint spellId)
    {
        if (LastAuraCasterOnTarget.TryGetValue(target, out var dict) &&
            dict.TryGetValue(spellId, out var caster))
        {
            dict.Remove(spellId);
            return caster;
        }

        return default;
    }

    // Spell Cast Queue Helper Methods

    /// <summary>
    /// Try to find and dequeue a pending cast by SpellId.
    /// Uses FIFO order since TCP guarantees packet ordering.
    /// </summary>
    public bool TryDequeuePendingNormalCast(uint spellId, out ClientCastRequest? cast)
    {
        // Since TCP preserves order, the first matching SpellId is the correct one
        var pending = new List<ClientCastRequest>();
        cast = null;

        lock (PendingCastsLock)
        {
            while (PendingNormalCasts.TryDequeue(out var current))
            {
                if (cast == null && CastMatchesSpellId(current, spellId))
                {
                    cast = current;
                }
                else
                {
                    pending.Add(current);
                }
            }

            // Re-enqueue non-matching casts
            foreach (var item in pending)
            {
                PendingNormalCasts.Enqueue(item);
            }
        }

        return cast != null;
    }

    /// <summary>
    /// Match a pending cast against an incoming server spellId, accepting either
    /// the modern (client-sent) SpellId or the LegacySpellId we resolved at item-use time.
    /// Needed for SoM 1.14.1+ items where Blizzard renumbered the on-use spell id
    /// (e.g. Diamond Flask 17626 → 363880); the legacy emulator still replies with the old id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CastMatchesSpellId(ClientCastRequest cast, uint spellId)
    {
        return cast.SpellId == spellId || (cast.LegacySpellId != 0 && cast.LegacySpellId == spellId);
    }

    /// <summary>
    /// Try to find a pending cast by SpellId and mark it as started (for SPELL_START).
    /// </summary>
    public bool TryMarkPendingNormalCastStarted(uint spellId, out ClientCastRequest? cast)
    {
        cast = null;

        foreach (var item in PendingNormalCasts)
        {
            if (CastMatchesSpellId(item, spellId) && !item.HasStarted)
            {
                item.HasStarted = true;
                cast = item;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clear all pending normal casts (used on timeout or disconnect).
    /// </summary>
    public void ClearPendingNormalCasts()
    {
        lock (PendingCastsLock)
        {
            while (PendingNormalCasts.TryDequeue(out _)) { }
        }
    }

    /// <summary>
    /// Check if there's a normal cast that has already started (is in progress).
    /// Used to reject new casts without forwarding to server.
    /// </summary>
    public bool HasStartedNormalCast()
    {
        foreach (var item in PendingNormalCasts)
        {
            if (item.HasStarted)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Clear only pending normal casts that haven't started yet.
    /// Keeps started casts so SPELL_GO can dequeue them later.
    /// Returns the cleared casts so they can be failed.
    /// </summary>
    public List<ClientCastRequest> ClearNonStartedNormalCasts()
    {
        var cleared = new List<ClientCastRequest>();
        var keep = new List<ClientCastRequest>();

        lock (PendingCastsLock)
        {
            while (PendingNormalCasts.TryDequeue(out var current))
            {
                if (current.HasStarted)
                    keep.Add(current);
                else
                    cleared.Add(current);
            }

            // Re-enqueue started casts
            foreach (var item in keep)
            {
                PendingNormalCasts.Enqueue(item);
            }
        }

        return cleared;
    }

    /// <summary>
    /// Try to find and dequeue a pending pet cast by SpellId.
    /// </summary>
    public bool TryDequeuePendingPetCast(uint spellId, out ClientCastRequest? cast)
    {
        var pending = new List<ClientCastRequest>();
        cast = null;

        while (PendingPetCasts.TryDequeue(out var current))
        {
            if (cast == null && CastMatchesSpellId(current, spellId))
            {
                cast = current;
            }
            else
            {
                pending.Add(current);
            }
        }

        foreach (var item in pending)
        {
            PendingPetCasts.Enqueue(item);
        }

        return cast != null;
    }

    /// <summary>
    /// Try to find a pending pet cast by SpellId and mark it as started.
    /// </summary>
    public bool TryMarkPendingPetCastStarted(uint spellId, out ClientCastRequest? cast)
    {
        cast = null;

        foreach (var item in PendingPetCasts)
        {
            if (CastMatchesSpellId(item, spellId) && !item.HasStarted)
            {
                item.HasStarted = true;
                cast = item;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clear all pending pet casts.
    /// </summary>
    public void ClearPendingPetCasts()
    {
        while (PendingPetCasts.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Check if there's a pet cast that has already started (is in progress).
    /// Used to reject new casts without forwarding to server.
    /// </summary>
    public bool HasStartedPetCast()
    {
        foreach (var item in PendingPetCasts)
        {
            if (item.HasStarted)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Clear only pending pet casts that haven't started yet.
    /// Keeps started casts so SPELL_GO can dequeue them later.
    /// Returns the cleared casts so they can be failed.
    /// </summary>
    public List<ClientCastRequest> ClearNonStartedPetCasts()
    {
        var cleared = new List<ClientCastRequest>();
        var keep = new List<ClientCastRequest>();

        while (PendingPetCasts.TryDequeue(out var current))
        {
            if (current.HasStarted)
                keep.Add(current);
            else
                cleared.Add(current);
        }

        // Re-enqueue started casts
        foreach (var item in keep)
        {
            PendingPetCasts.Enqueue(item);
        }

        return cleared;
    }

    // JimsProxy (issue #43): GCD hold-and-fire helpers.

    /// <summary>
    /// Returns true if a GCD hold window is currently active (i.e. a subsequent cast should be held
    /// rather than forwarded). Uses Environment.TickCount64 as the timebase.
    /// </summary>
    public bool IsGcdHoldActive()
    {
        lock (_gcdLock)
        {
            return _gcdExpireTimestampMs > Environment.TickCount64;
        }
    }

    /// <summary>
    /// JimsProxy: ms remaining in the current GCD hold window, or 0 if no GCD is active.
    /// Used by diagnostics to show how deep into the GCD a held/displaced press landed —
    /// helps distinguish "user mashed early in GCD" from "user mashed in the natural
    /// retail spell-queue window (last ~400ms)" without changing behavior.
    /// </summary>
    public long GetGcdRemainingMs()
    {
        lock (_gcdLock)
        {
            long remaining = _gcdExpireTimestampMs - Environment.TickCount64;
            return remaining > 0 ? remaining : 0;
        }
    }

    /// <summary>
    /// Store <paramref name="cast"/> as the pending held cast for the current GCD window.
    /// Returns true if the GCD is still active (cast was stored). Returns false if the GCD
    /// already expired in the meantime (caller should forward immediately via the normal path).
    /// If a previously-held cast existed, it's returned via <paramref name="displaced"/> so
    /// the caller can decide how to handle it (today: silently drop).
    /// </summary>
    public bool TryHoldCastDuringGcd(ClientCastRequest cast, out ClientCastRequest? displaced)
    {
        displaced = null;
        lock (_gcdLock)
        {
            if (_gcdExpireTimestampMs <= Environment.TickCount64)
                return false;
            displaced = _heldGcdCast;
            _heldGcdCast = cast;
            return true;
        }
    }

    /// <summary>
    /// Start (or restart) a GCD hold window. The timer fires at <paramref name="fireAtTickMs"/>,
    /// at which point any pending held cast is handed to OnGcdHeldCastFire on a ThreadPool thread.
    /// expireAtTickMs and fireAtTickMs are Environment.TickCount64 timestamps.
    /// </summary>
    public void BeginGcd(long expireAtTickMs, long fireAtTickMs)
    {
        lock (_gcdLock)
        {
            _gcdExpiryTimer?.Dispose();
            _gcdExpireTimestampMs = expireAtTickMs;
            unchecked { _gcdGeneration++; }
            uint myGeneration = _gcdGeneration;
            long delayMs = Math.Max(0, fireAtTickMs - Environment.TickCount64);
            // Timer.Dispose() does NOT wait for an already-queued callback, so a stale
            // callback from a prior GCD window can race against a freshly-installed timer.
            // We capture the generation counter into the callback's state arg and bail in
            // OnGcdTimerElapsed if the generation no longer matches _gcdGeneration.
            _gcdExpiryTimer = new Timer(OnGcdTimerElapsed, state: myGeneration, delayMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Cancel the active GCD hold window and drop any held cast. Used on session teardown
    /// or when the client cancels a cast while we're holding one for them. Returns the
    /// previously-held cast (if any) so callers on a live session can route it through
    /// SendCastRequestFailed to resolve the client's ClientGUID/ServerGUID tracking.
    /// OnDisconnect and HandleLogoutComplete ignore the return value since the session is
    /// going away.
    /// </summary>
    public ClientCastRequest? CancelGcdHold()
    {
        lock (_gcdLock)
        {
            ClientCastRequest? dropped = _heldGcdCast;
            _gcdExpiryTimer?.Dispose();
            _gcdExpiryTimer = null;
            _gcdExpireTimestampMs = 0;
            _heldGcdCast = null;
            // Bump generation so any already-queued callback from the cancelled timer sees a
            // stale generation and bails. Prevents post-cancel firing on session teardown.
            unchecked { _gcdGeneration++; }
            // Also null the fire delegate so a stale Invoke that escaped the lock (see the
            // TOCTOU window in OnGcdTimerElapsed between the generation check and the
            // post-lock Invoke) turns into a no-op instead of operating on a rotated GameState.
            // HandleCastSpell re-registers the delegate on the next cast via its null check.
            OnGcdHeldCastFire = null;
            return dropped;
        }
    }

    /// <summary>
    /// Test-only: peek at the currently-held cast without consuming it. Returns null when none held.
    /// </summary>
    internal ClientCastRequest? PeekHeldGcdCast()
    {
        lock (_gcdLock)
        {
            return _heldGcdCast;
        }
    }

    private void OnGcdTimerElapsed(object? state)
    {
        ClientCastRequest? toFire;
        lock (_gcdLock)
        {
            // Reject stale fires: a queued callback from a prior BeginGcd can run after the
            // current generation has moved on. Clobbering _heldGcdCast / _gcdExpireTimestampMs
            // here would zero out state belonging to the new GCD window, silently disabling
            // the hold for the rest of it.
            if (state is not uint myGeneration || myGeneration != _gcdGeneration)
                return;

            toFire = _heldGcdCast;
            _heldGcdCast = null;
            _gcdExpireTimestampMs = 0;
            // Don't null _gcdExpiryTimer here: a concurrent BeginGcd could have already replaced it.
        }
        if (toFire != null)
            OnGcdHeldCastFire?.Invoke(toFire);
    }

    /// <summary>
    /// Try to find and dequeue a pending cast by ItemGUID (for item use failures).
    /// Only matches casts that haven't started yet.
    /// </summary>
    public bool TryDequeueItemCast(WowGuid128 itemGuid, out ClientCastRequest? cast)
    {
        var pending = new List<ClientCastRequest>();
        cast = null;

        lock (PendingCastsLock)
        {
            while (PendingNormalCasts.TryDequeue(out var current))
            {
                if (cast == null && !current.HasStarted && current.ItemGUID == itemGuid)
                {
                    cast = current;
                }
                else
                {
                    pending.Add(current);
                }
            }

            // Re-enqueue non-matching casts
            foreach (var item in pending)
            {
                PendingNormalCasts.Enqueue(item);
            }
        }

        return cast != null;
    }

    public void StorePlayerGuildId(WowGuid128 guid, uint guildId)
    {
        PlayerGuildIds[guid] = guildId;
    }
    public uint GetPlayerGuildId(WowGuid128 guid)
    {
        return PlayerGuildIds.TryGetValue(guid, out var value) ? value : 0u;
    }
    public uint[]? GetGemsForItem(WowGuid128 guid)
    {
        return ItemGems.TryGetValue(guid, out var gems) ? gems : null;
    }
    public void SaveGemsForItem(WowGuid128 guid, uint?[] gems)
    {
        ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(ItemGems, guid, out _);
        existing ??= new uint[ItemConst.MaxGemSockets];

        for (int i = 0; i < ItemConst.MaxGemSockets; i++)
        {
            if (gems[i] != null)
                existing[i] = (uint)gems[i]!;
        }
    }
    public WowGuid128 GetPetGuidByNumber(uint petNumber)
    {
        lock (ObjectCacheLock)
        {
            foreach (var itr in ObjectCacheModern)
            {
                if (itr.Key.GetHighType() == HighGuidType.Pet &&
                    itr.Key.GetEntry() == petNumber)
                {
                    return itr.Key;
                }
            }
            return default;
        }
    }
    public void StoreOriginalObjectType(WowGuid128 guid, ObjectType type)
    {
        OriginalObjectTypes[guid] = type;
    }
    public ObjectType GetOriginalObjectType(WowGuid128 guid)
    {
        return OriginalObjectTypes.TryGetValue(guid, out var type) ? type : guid.GetObjectType();
    }
    public void StoreRealSpell(uint realSpellId, uint learnSpellId)
    {
        RealSpellToLearnSpell[realSpellId] = learnSpellId;
    }
    public uint GetLearnSpellFromRealSpell(uint spellId)
    {
        return RealSpellToLearnSpell.TryGetValue(spellId, out var learnSpell) ? learnSpell : spellId;
    }
    public void StoreCreatureClass(uint entry, Class classId)
    {
        CreatureClasses[entry] = classId;
    }
    public void SetItemBuyCount(uint itemId, uint buyCount)
    {
        ItemBuyCount[itemId] = buyCount;
    }
    public uint GetItemBuyCount(uint itemId)
    {
        return ItemBuyCount.TryGetValue(itemId, out var count) ? count : 1u;
    }
    public void SetChannelId(string name, int id)
    {
        // If the name was previously mapped to a different id, evict the stale
        // reverse entry so ChannelNamesById can't accumulate dead ids.
        if (ChannelIds.TryGetValue(name, out var oldId) && oldId != id)
            ChannelNamesById.Remove(oldId);

        ChannelIds[name] = id;
        ChannelNamesById[id] = name;
    }
    public string GetChannelName(int id)
    {
        return ChannelNamesById.TryGetValue(id, out var name) ? name : "";
    }

    public string GetPlayerName(WowGuid128 guid)
    {
        if (CachedPlayers.TryGetValue(guid, out var cache) && cache.Name != null)
            return cache.Name;
        return "";
    }

    public WowGuid128 GetPlayerGuidByName(string name)
    {
        name = name.Trim().Replace("\0", "");
        foreach (var player in CachedPlayers)
        {
            if (player.Value.Name == name && !WowGuid128.IsUnknownPlayerGuid(player.Key))
                return player.Key;
        }
        return default;
    }

    public void UpdatePlayerCache(WowGuid128 guid, PlayerCache data)
    {
        if (data.Name != null)
            data.Name = data.Name.Trim().Replace("\0", "");

        if (CachedPlayers.TryGetValue(guid, out var existing))
        {
            if (!string.IsNullOrEmpty(data.Name))
                existing.Name = data.Name;
            if (data.RaceId != Race.None)
                existing.RaceId = data.RaceId;
            if (data.ClassId != Class.None)
                existing.ClassId = data.ClassId;
            if (data.SexId != Gender.None)
                existing.SexId = data.SexId;
            if (data.Level != 0)
                existing.Level = data.Level;
        }
        else
            CachedPlayers.Add(guid, data);
    }

    public Class GetUnitClass(WowGuid128 guid)
    {
        if (CachedPlayers.TryGetValue(guid, out var cache))
            return cache.ClassId;

        if (CreatureClasses.TryGetValue(guid.GetEntry(), out var classId))
            return classId;

        return Class.Warrior;
    }

    public int GetLegacyFieldValueInt32<T>(WowGuid128 guid, T field) where T : Enum
    {
        int fieldIndex = LegacyVersion.GetUpdateField(field);
        if (fieldIndex < 0)
            return 0;

        var updates = GetCachedObjectFieldsLegacy(guid);
        if (updates != null && updates.TryGetValue(fieldIndex, out var value))
            return value.Int32Value;

        return 0;
    }

    public uint GetLegacyFieldValueUInt32<T>(WowGuid128 guid, T field) where T : Enum
    {
        int fieldIndex = LegacyVersion.GetUpdateField(field);
        if (fieldIndex < 0)
            return 0;

        var updates = GetCachedObjectFieldsLegacy(guid);
        if (updates != null && updates.TryGetValue(fieldIndex, out var value))
            return value.UInt32Value;

        return 0;
    }

    public float GetLegacyFieldValueFloat<T>(WowGuid128 guid, T field) where T : Enum
    {
        int fieldIndex = LegacyVersion.GetUpdateField(field);
        if (fieldIndex < 0)
            return 0;

        var updates = GetCachedObjectFieldsLegacy(guid);
        if (updates != null && updates.TryGetValue(fieldIndex, out var value))
            return value.FloatValue;

        return 0;
    }

    public Dictionary<int, UpdateField>? GetCachedObjectFieldsLegacy(WowGuid128 guid)
    {
        lock (ObjectCacheLock)
        {
            ObjectCacheLegacy.TryGetValue(guid, out var dict);
            return dict;
        }
    }

    public UpdateFieldsArray? GetCachedObjectFieldsModern(WowGuid128 guid)
    {
        lock (ObjectCacheLock)
        {
            ObjectCacheModern.TryGetValue(guid, out var array);
            return array;
        }
    }
}

public class ClientCastRequest
{
    public bool HasStarted;
    public uint SpellId;
    public uint LegacySpellId; // 0 = same as SpellId; non-zero when modern client used a renumbered spell (e.g. SoM 1.14.1+ items)
    public uint SpellXSpellVisualId;
    public long Timestamp;
    public WowGuid128 ClientGUID;
    public WowGuid128 ServerGUID;
    public WowGuid128 ItemGUID;

    // JimsProxy (issue #43): when a cast is HELD during a GCD hold window, we keep the
    // fully-built CMSG_CAST_SPELL packet here so the timer callback can forward it
    // verbatim at GCD expiry. Null for casts that were forwarded immediately (normal path).
    public WorldPacket? HeldPacketForReplay;

    // JimsProxy: TickCount64 timestamp when this cast was placed into the GCD hold slot.
    // Diagnostic only — used by spell.held_fire to compute hold duration. 0 if never held.
    public long HeldAtTickMs;

    // JimsProxy: cast time (ms) reported by SMSG_SPELL_START. 0 means instant.
    // Distinguishes truly cast-time spells (Frostbolt, Polymorph) from instants that
    // *also* emit SMSG_SPELL_START on Kronos 1.12 (Arcane Explosion, Counterspell, etc.).
    // The GCD hold gate in HandleSpellGo uses this instead of HasStarted so Kronos-flavored
    // instants still trigger BeginGcd. See JimsProxy issue #43 follow-up.
    public uint StartedCastTimeMs;
}
public class ArenaTeamData
{
    public string Name = null!;
    public uint TeamSize;
    public uint WeekPlayed;
    public uint WeekWins;
    public uint SeasonPlayed;
    public uint SeasonWins;
    public uint Rating;
    public uint Rank;
    public uint BackgroundColor;
    public uint EmblemStyle;
    public uint EmblemColor;
    public uint BorderStyle;
    public uint BorderColor;
}
public class GlobalSessionData
{
    public BNetServer.Networking.AccountInfo AccountInfo = null!;
    public BNetServer.Networking.GameAccountInfo GameAccountInfo = null!;
    public string Username = null!;
    public string LoginTicket = null!;
    public byte[] SessionKey = null!;
    public string Locale = null!;
    public string OS = null!;
    public uint Build;
    public GameSessionData GameState;
    
    public RealmId RealmId;
    public RealmManager RealmManager = new();
    public Realm? Realm => RealmManager.GetRealm(RealmId);

    public AccountMetaDataManager AccountMetaDataMgr = null!;
    public AccountDataManager AccountDataMgr = null!;

    public WorldSocket RealmSocket = null!;
    public WorldSocket InstanceSocket = null!;
    public AuthClient AuthClient = null!;
    public WorldClient? WorldClient;
    public SniffFile ModernSniff = null!;

    public Dictionary<string, WowGuid128> GuildsByName = [];
    public Dictionary<uint, List<string>> GuildRanks = [];

    public GlobalSessionData()
    {
        GameState = GameSessionData.CreateNewGameSessionData(this);
        AuthClient = new AuthClient(this);
    }

    public void StoreGuildRankNames(uint guildId, List<string> ranks)
    {
        GuildRanks[guildId] = ranks;
    }
    public uint GetGuildRankIdByName(uint guildId, string name)
    {
        if (GuildRanks.TryGetValue(guildId, out var ranks))
        {
            for (int i = 0; i < ranks.Count; i++)
            {
                if (ranks[i] == name)
                    return (uint)i;
            }
        }
        return 0;
    }
    public string GetGuildRankNameById(uint guildId, byte rankId)
    {
        if (GuildRanks.TryGetValue(guildId, out var ranks))
            return ranks[rankId];

        return $"Rank {rankId}";
    }
    public void StoreGuildGuidAndName(WowGuid128 guid, string name)
    {
        GuildsByName[name] = guid;
    }
    public WowGuid128 GetGuildGuid(string name)
    {
        if (GuildsByName.TryGetValue(name, out var guid))
            return guid;

        guid = WowGuid128.Create(HighGuidType703.Guild, (ulong)(GuildsByName.Count + 1));
        GuildsByName.Add(name, guid);
        return guid;
    }

    public WowGuid128 GetGameAccountGuidForPlayer(WowGuid128 playerGuid)
    {
        if (GameState.OwnCharacters.Any(own => own.CharacterGuid == playerGuid))
            return WowGuid128.Create(HighGuidType703.WowAccount, GameAccountInfo.Id);
        else
            return WowGuid128.Create(HighGuidType703.WowAccount, playerGuid.GetCounter());
    }

    public WowGuid128 GetBnetAccountGuidForPlayer(WowGuid128 playerGuid)
    {
        if (GameState.OwnCharacters.Any(own => own.CharacterGuid == playerGuid))
            return WowGuid128.Create(HighGuidType703.BNetAccount, AccountInfo.Id);
        else
            return WowGuid128.Create(HighGuidType703.BNetAccount, playerGuid.GetCounter());
    }

    public void OnDisconnect()
    {
        // JimsProxy: structured session.disconnect — emitted once per cleanup with snapshot
        Framework.Logging.Log.Event("session.disconnect", new
        {
            had_auth_client = AuthClient != null,
            had_world_client = WorldClient != null,
            had_realm_socket = RealmSocket != null,
            had_instance_socket = InstanceSocket != null,
            had_modern_sniff = ModernSniff != null,
            account_login = AccountInfo?.Login,
        });

        // JimsProxy (issue #43): cancel any held GCD cast and dispose its timer so it can't
        // fire after InstanceSocket has been torn down.
        GameState?.CancelGcdHold();

        if (ModernSniff != null)
        {
            ModernSniff.CloseFile();
            ModernSniff = null!;
        }
        if (AuthClient != null)
        {
            AuthClient.Disconnect();
            AuthClient = null!;
        }
        if (WorldClient != null)
        {
            WorldClient.Disconnect();
            WorldClient = null;
        }
        if (RealmSocket != null)
        {
            RealmSocket.CloseSocket();
            RealmSocket = null!;
        }
        if (InstanceSocket != null)
        {
            InstanceSocket.CloseSocket();
            InstanceSocket = null!;
        }

        GameState = GameSessionData.CreateNewGameSessionData(this);
    }

    public void SendHermesTextMessage(string message, bool isError = false)
    {
        var socket = InstanceSocket;
        if (socket == null)
        {
            return;
        }

        var wholeMessage = new StringBuilder();
        wholeMessage.Append("|cFF111111[|r|cFF33DD22HermesProxy|r|cFF111111]|r ");
        if (isError)
            wholeMessage.Append("|cFFFF0000");
        wholeMessage.Append(message);

        var chatPkt = new ChatPkt(this, ChatMessageTypeModern.System, wholeMessage.ToString());
        socket.SendPacket(chatPkt);
    }
}
