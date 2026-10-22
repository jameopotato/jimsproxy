using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System.Collections;
using System.Collections.Generic;

namespace HermesProxy.World.Client;

// JimsProxy (unit-scale): every unit size the proxy sends the modern client goes through
// this file. The rule and its data live in World/UnitScale.cs: send the 1.12 server's
// SCALE_X divided by the display factor the modern client multiplies in itself.
public partial class WorldClient
{
    // The factor follows the unit's display, level (a family's level scale) and pet number,
    // so the scale is re-sent whenever one of those arrives, even with no new SCALE_X.
    private void TranslateUnitScale(BitArray updateMaskArray, Dictionary<int, UpdateField> updates, ObjectType objectType, ObjectUpdate updateData, WowGuid128 guid)
    {
        if (!IsUnitObjectType(objectType) || !UnitScale.TranslationEnabled)
            return;

        bool scaleInUpdate = InUpdateMask(updateMaskArray, LegacyVersion.GetUpdateField(ObjectField.OBJECT_FIELD_SCALE_X));
        bool factorInputInUpdate = InUpdateMask(updateMaskArray, LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_DISPLAYID))
                                   || InUpdateMask(updateMaskArray, LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_LEVEL))
                                   || InUpdateMask(updateMaskArray, LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_PETNUMBER));
        if (!scaleInUpdate && !factorInputInUpdate)
            return;

        // `updates` holds every field the proxy knows for the unit, this update merged in.
        float serverScale = ReadLegacyFloat(updates, ObjectField.OBJECT_FIELD_SCALE_X);
        if (serverScale <= 0f)
            return;

        var unit = ResolveUnitScaleInputs(objectType, updates, out uint entry, out bool templateKnown);
        var factor = UnitScale.ComputeFactor(unit);
        float clientScale = UnitScale.ToClientObjectScale(serverScale, factor.Value);
        updateData.ObjectData.Scale = clientScale;

        if (!templateKnown)
            RequestCreatureTemplateForUnitScale(guid, entry);

        // Fires on every unit create and scale change: DebugOutput only.
        if (Framework.Settings.DebugOutput)
            Log.Event("unit.scale.translated", new
            {
                guid = guid.ToString(),
                object_type = objectType.ToString(),
                entry = entry,
                display_id = unit.DisplayId,
                level = unit.Level,
                family_id = unit.FamilyId,
                template_known = templateKnown,
                is_pet = unit.IsPet,
                server_scale = serverScale,
                display_scale = factor.DisplayScale,
                race_model_scale = factor.RaceModelScale,
                model_scale = factor.ModelScale,
                family_scale = factor.FamilyScale,
                family_applied = factor.FamilyApplied,
                known_display = factor.KnownDisplay,
                factor = factor.Value,
                client_scale = clientScale,
            });
    }

    // What the modern client sizes the unit from, read from the proxy's copy of its 1.12
    // fields (the client gets the same display, level and pet number unchanged).
    // templateKnown is false for a creature whose CreatureFamily the proxy has not learned.
    internal static UnitScale.UnitInputs ResolveUnitScaleInputs(ObjectType objectType, Dictionary<int, UpdateField> fields, out uint entry, out bool templateKnown)
    {
        bool isPlayer = objectType == ObjectType.Player || objectType == ObjectType.ActivePlayer;
        uint displayId = ReadLegacyUInt(fields, UnitField.UNIT_FIELD_DISPLAYID);
        int level = (int)ReadLegacyUInt(fields, UnitField.UNIT_FIELD_LEVEL);
        bool isPet = ReadLegacyUInt(fields, UnitField.UNIT_FIELD_PETNUMBER) != 0;

        entry = 0;
        templateKnown = true;
        int familyId = 0;
        if (!isPlayer)
        {
            entry = ReadLegacyUInt(fields, ObjectField.OBJECT_FIELD_ENTRY);
            var template = entry != 0 ? GameData.GetCreatureTemplate(entry) : null;
            if (template != null)
                familyId = template.Family;
            else
                templateKnown = entry == 0 || UnitScale.IsCharacterModelDisplay(displayId);
        }

        return new UnitScale.UnitInputs(isPlayer, displayId, level, familyId, isPet);
    }

    // The scale the proxy sends the client for this unit right now, and the client's factor.
    internal static float ComputeClientUnitScale(ObjectType objectType, Dictionary<int, UpdateField> fields, out float factor)
    {
        var unit = ResolveUnitScaleInputs(objectType, fields, out _, out _);
        factor = UnitScale.ClientDisplayFactor(unit);
        float serverScale = ReadLegacyFloat(fields, ObjectField.OBJECT_FIELD_SCALE_X);
        return UnitScale.TranslationEnabled ? UnitScale.ToClientObjectScale(serverScale, factor) : serverScale;
    }

    // A creature's family is in its query answer only, and the client keeps those answers
    // across sessions and stops asking, so the proxy asks the server itself (once per entry
    // per session). The answer, whoever asked, re-sends every waiting unit's scale
    // (QueryHandler -> ResendUnitScalesForEntry).
    private void RequestCreatureTemplateForUnitScale(WowGuid128 guid, uint entry)
    {
        var state = GetSession().GameState;
        state.UnitScaleTemplatePending[guid] = entry;
        if (!state.UnitScaleQueriedEntries.Add(entry))
            return;

        WorldPacket queryPacket = new WorldPacket(Opcode.CMSG_QUERY_CREATURE);
        queryPacket.WriteUInt32(entry);
        queryPacket.WriteGuid(new WowGuid64(HighGuidTypeLegacy.Creature, entry, 1));
        SendPacketToServer(queryPacket);
    }

    private void ResendUnitScalesForEntry(uint entry)
    {
        var state = GetSession().GameState;
        var pending = state.UnitScaleTemplatePending;
        if (pending.Count == 0)
            return;

        List<WowGuid128>? waiting = null;
        foreach (var (guid, pendingEntry) in pending)
        {
            if (pendingEntry == entry)
                (waiting ??= new List<WowGuid128>()).Add(guid);
        }
        if (waiting == null)
            return;

        foreach (var guid in waiting)
        {
            pending.Remove(guid);
            var fields = state.GetCachedObjectFieldsLegacy(guid);
            if (fields == null)
                continue;
            if (ReadLegacyFloat(fields, ObjectField.OBJECT_FIELD_SCALE_X) <= 0f)
                continue;

            float clientScale = ComputeClientUnitScale(state.GetOriginalObjectType(guid), fields, out float factor);
            UpdateObject updateObject = new UpdateObject(state);
            ObjectUpdate update = new ObjectUpdate(guid, UpdateTypeModern.Values, GetSession());
            update.ObjectData.Scale = clientScale;
            updateObject.ObjectUpdates.Add(update);
            SendPacketToClient(updateObject);

            if (Framework.Settings.DebugOutput)
                Log.Event("unit.scale.template_resent", new
                {
                    guid = guid.ToString(),
                    entry = entry,
                    factor = factor,
                    client_scale = clientScale,
                });
        }
    }

    // JimsProxy (collision-visual-parity #359): the height the modern client's collision
    // cylinder must have to agree with the model it is rendering: the model's authored
    // CreatureModelData.CollisionHeight (for the mesh at scale 1.0) times the size the client
    // draws it at, which is the scale the proxy sends times the client's own display factor.
    // With the unit-scale translation that product is the 1.12 server's SCALE_X, so the
    // cylinder matches the 1.12 size in every form.
    //
    // Deliberately NOT ported from the upstream formula (_BLU 2023): the
    // Max(scale, displaySize) hitbox floor (it inflated every CMS<1 form above its visible
    // size) and the displayId-keyed GetModelData lookup.
    // Zero-height models fall back to PlayerHeight at the call site.
    public static float ComputeVisualCollisionHeight(int displayId, int mountDisplayId, float clientScale, float displayFactor)
    {
        var model = GameData.GetModelData(UnitScale.ClientModelId((uint)displayId));
        float drawnScale = clientScale * displayFactor;

        if (mountDisplayId != 0 && LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        { // in vanilla there were no mount collisions
            var mountModel = GameData.GetModelData(UnitScale.ClientModelId((uint)mountDisplayId));
            float mountCms = GameData.GetClientEffectiveDisplayScale((uint)mountDisplayId);
            return clientScale * mountModel.MountHeight * mountCms + model.Height * drawnScale * 0.5f;
        }

        return model.Height * drawnScale;
    }

    private static bool IsUnitObjectType(ObjectType objectType)
        => objectType == ObjectType.Unit || objectType == ObjectType.Player || objectType == ObjectType.ActivePlayer;

    private static bool InUpdateMask(BitArray updateMaskArray, int fieldIndex)
        => fieldIndex >= 0 && fieldIndex < updateMaskArray.Length && updateMaskArray[fieldIndex];

    private static float ReadLegacyFloat<T>(Dictionary<int, UpdateField> fields, T field) where T : System.Enum
    {
        int index = LegacyVersion.GetUpdateField(field);
        return index >= 0 && fields.TryGetValue(index, out var value) ? value.FloatValue : 0f;
    }

    private static uint ReadLegacyUInt<T>(Dictionary<int, UpdateField> fields, T field) where T : System.Enum
    {
        int index = LegacyVersion.GetUpdateField(field);
        return index >= 0 && fields.TryGetValue(index, out var value) ? value.UInt32Value : 0u;
    }
}
