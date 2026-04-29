using Framework;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Handlers for SMSG opcodes coming the legacy world server
    private static string HexDump(byte[] data, int max = 256)
    {
        if (data == null || data.Length == 0) return "";
        int len = Math.Min(data.Length, max);
        var sb = new System.Text.StringBuilder(len * 2);
        for (int i = 0; i < len; i++)
            sb.Append(data[i].ToString("X2"));
        return sb.ToString();
    }

    [PacketHandler(Opcode.SMSG_PET_SPELLS_MESSAGE)]
    void HandlePetSpellsMessage(WorldPacket packet)
    {
        // Defensive wrapper: a quest tame on Kronos (e.g. spell 19684 used via
        // quest item) sends SMSG_PET_SPELLS_MESSAGE in a shape we don't fully
        // parse — reading runs off the end of the buffer and throws
        // ArgumentOutOfRangeException, killing the WorldClient ReceiveLoop and
        // DCing the player. Until we figure out the exact format mismatch, keep
        // the parser self-contained so a malformed packet drops the pet bar
        // instead of DCing the session.
        uint dbgPacketSize = packet.GetSize();
        // Capture the raw bytes BEFORE reading so the hex dump on failure is
        // useful even if the packet has already been consumed past the failure
        // point. ReadToEnd / GetData semantics differ across packet types; we
        // call GetData which returns the full underlying buffer.
        byte[] dbgRawBytes = packet.GetData();
        try
        {
            WowGuid64 guid = packet.ReadGuid();
            GetSession().GameState.CurrentPetGuid = guid.To128(GetSession().GameState);
            GetSession().GameState.ClearPendingPetCasts();

            // Equal to "Clear spells" pre cataclysm
            if (guid.IsEmpty())
            {
                Log.Event("pet.spells.cleared", new { packet_size = dbgPacketSize });
                // Pet dismissed/lost — drop family cache so a new pet at the
                // same low-counter (unlikely but possible) doesn't inherit the
                // old family.
                GetSession().GameState.CachedPetCreatureFamily.Clear();
                PetClearSpells clear = new();
                SendPacketToClient(clear);
                return;
            }

            PetSpells spells = new();
            spells.PetGUID = guid.To128(GetSession().GameState);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
                spells.CreatureFamily = packet.ReadUInt16();
            else
            {
                // For pre-3.1.0 servers (Vanilla/TBC), CreatureFamily is not in the packet.
                // Look it up from the creature template using the pet's entry ID.
                uint creatureEntry = GetSession().GameState.GetItemId(spells.PetGUID);
                if (creatureEntry != 0)
                {
                    CreatureTemplate? template = GameData.GetCreatureTemplate(creatureEntry);
                    if (template != null)
                        spells.CreatureFamily = (ushort)template.Family;
                }
                // Layered fallbacks for creature_family. The modern client's
                // PetPaperDollFrame_SetStats calls strupper(UnitCreatureFamily("pet"))
                // which throws if the family ID has no name mapping. Family 0
                // is always nil. We must always send a non-zero family.
                //
                //   1. If the creature template lookup succeeded, use it.
                //   2. Else fall back to a previously-cached value for this pet.
                //   3. Else fall back to family=1 (Wolf) — generic vanilla beast
                //      family, guaranteed to resolve to a non-nil name.
                //
                // Quest tames (charm-based, e.g. spell 19686) hit case 3 because
                // the controlled creature has no Beast family in its template;
                // we send Wolf so the paper-doll frame doesn't error out.
                var familyCache = GetSession().GameState.CachedPetCreatureFamily;
                if (spells.CreatureFamily != 0)
                {
                    familyCache[spells.PetGUID] = spells.CreatureFamily;
                }
                else if (familyCache.TryGetValue(spells.PetGUID, out var cachedFamily) && cachedFamily != 0)
                {
                    spells.CreatureFamily = cachedFamily;
                    Log.Event("pet.creature_family.fallback_cached", new
                    {
                        pet_guid = spells.PetGUID.ToString(),
                        cached_family = cachedFamily,
                    });
                }
                else
                {
                    spells.CreatureFamily = 1; // Wolf — generic beast fallback
                    Log.Event("pet.creature_family.fallback_default", new
                    {
                        pet_guid = spells.PetGUID.ToString(),
                        creature_entry = creatureEntry,
                        defaulted_to = 1,
                    });
                }
            }

            spells.TimeLimit = packet.ReadUInt32();
            spells.ReactState = (ReactStates)packet.ReadUInt8();
            spells.CommandState = (CommandStates)packet.ReadUInt8();
            packet.ReadUInt8(); // unused
            spells.Flag = packet.ReadUInt8();

            const int maxCreatureSpells = 10;
            for (int i = 0; i < maxCreatureSpells; i++) // Read pet/vehicle spell ids
                spells.ActionButtons[i] = packet.ReadUInt32();

            byte spellCount = packet.ReadUInt8();
            for (int i = 0; i < spellCount; i++)
                spells.Actions.Add(packet.ReadUInt32());

            byte cdCount = packet.ReadUInt8();
            for (int i = 0; i < cdCount; i++)
            {
                PetSpellCooldown cooldown = new();

                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
                    cooldown.SpellID = packet.ReadUInt32();
                else
                    cooldown.SpellID = packet.ReadUInt16();

                cooldown.Category = packet.ReadUInt16();
                cooldown.Duration = packet.ReadUInt32();
                cooldown.CategoryDuration = packet.ReadUInt32();

                spells.Cooldowns.Add(cooldown);
            }

            Log.Event("pet.spells.parsed", new
            {
                pet_guid = spells.PetGUID.ToString(),
                creature_family = spells.CreatureFamily,
                time_limit_ms = spells.TimeLimit,
                react_state = spells.ReactState.ToString(),
                command_state = spells.CommandState.ToString(),
                flag = spells.Flag,
                spell_count = spellCount,
                cd_count = cdCount,
                packet_size = dbgPacketSize,
            });

            SendPacketToClient(spells);
        }
        catch (Exception ex)
        {
            Log.Print(LogType.Error, $"PetSpellsMessage parse failed (non-fatal, packet dropped): packetSize={dbgPacketSize} {ex.GetType().Name}: {ex.Message}");
            Log.Event("pet.spells_message.parse_failed", new
            {
                exception_type = ex.GetType().Name,
                exception_message = ex.Message,
                packet_size = dbgPacketSize,
                packet_hex = HexDump(dbgRawBytes),
            });
        }
    }

    [PacketHandler(Opcode.SMSG_PET_ACTION_SOUND)]
    void HandlePetActionSound(WorldPacket packet)
    {
        PetActionSound sound = new PetActionSound();
        sound.UnitGUID = packet.ReadGuid().To128(GetSession().GameState);
        sound.Action = packet.ReadUInt32();
        Log.Event("pet.action_sound", new
        {
            unit_guid = sound.UnitGUID.ToString(),
            action = sound.Action,
        });
        SendPacketToClient(sound);
    }

    [PacketHandler(Opcode.SMSG_PET_BROKEN)]
    void HandlePetBroken(WorldPacket packet)
    {
        Log.Event("pet.broken", new { });
        PrintNotification notify = new PrintNotification();
        notify.NotifyText = "Your pet has run away";
        SendPacketToClient(notify);
    }

    [PacketHandler(Opcode.SMSG_PET_UNLEARN_CONFIRM)]
    void HandlePetUnlearnConfirm(WorldPacket packet)
    {
        RespecWipeConfirm respec = new RespecWipeConfirm();
        respec.TrainerGUID = packet.ReadGuid().To128(GetSession().GameState);
        respec.Cost = packet.ReadUInt32();
        respec.RespecType = SpecResetType.PetTalents;
        SendPacketToClient(respec);
    }

    [PacketHandler(Opcode.MSG_LIST_STABLED_PETS)]
    void HandleListStabledPets(WorldPacket packet)
    {
        PetGuids pets = new PetGuids();
        var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(GetSession().GameState.CurrentPlayerGuid);
        int UNIT_FIELD_SUMMON = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_SUMMON);
        if (UNIT_FIELD_SUMMON >= 0 && updateFields != null && updateFields.ContainsKey(UNIT_FIELD_SUMMON))
        {
            WowGuid128 guid = GetGuidValue(updateFields, UnitField.UNIT_FIELD_SUMMON).To128(GetSession().GameState);
            if (!guid.IsEmpty())
                pets.Guids.Add(guid);
        }
        SendPacketToClient(pets);

        PetStableList stable = new PetStableList();
        stable.StableMaster = packet.ReadGuid().To128(GetSession().GameState);
        byte count = packet.ReadUInt8();
        stable.NumStableSlots = packet.ReadUInt8();
        for (byte i = 0; i < count; i++)
        {
            PetStableInfo pet = new PetStableInfo();
            pet.PetNumber = packet.ReadUInt32();
            pet.CreatureID = packet.ReadUInt32();
            pet.ExperienceLevel = packet.ReadUInt32();
            pet.PetName = packet.ReadCString();
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
                pet.LoyaltyLevel = (byte)packet.ReadUInt32();
            pet.PetFlags = packet.ReadUInt8();

            if (pet.PetFlags != 1)
                pet.PetFlags = 3;

            CreatureTemplate? template = GameData.GetCreatureTemplate(pet.CreatureID);
            if (template != null)
                pet.DisplayID = template.Display.CreatureDisplay[0].CreatureDisplayID;
            else
            {
                WorldPacket query = new WorldPacket(Opcode.CMSG_QUERY_CREATURE);
                query.WriteUInt32(pet.CreatureID);
                query.WriteGuid(WowGuid64.Empty);
                SendPacket(query);
            }

            stable.Pets.Add(pet);
        }
        SendPacketToClient(stable);
    }

    [PacketHandler(Opcode.SMSG_PET_STABLE_RESULT)]
    void HandlePetStableResult(WorldPacket packet)
    {
        PetStableResult stable = new PetStableResult();
        stable.Result = packet.ReadUInt8();
        SendPacketToClient(stable);
    }

    [PacketHandler(Opcode.SMSG_PET_TAME_FAILURE)]
    void HandlePetTameFailure(WorldPacket packet)
    {
        PetTameFailure tameFailure = new PetTameFailure();
        tameFailure.Reason = packet.ReadUInt8();
        SendPacketToClient(tameFailure);
    }
}
