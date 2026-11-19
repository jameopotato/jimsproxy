using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System.Collections;
using System.Collections.Generic;

namespace HermesProxy.World.Client;

// JimsProxy (unit-scale): the sizes the proxy translates for the modern client go through
// this file. The rule and its data live in World/UnitScale.cs: send the 1.12 server's
// SCALE_X divided by the display factor the modern client multiplies in itself.
//
// v1 scope: players and humanoid NPCs. Every other unit keeps the May 2026 per-case code
// at the end of StoreObjectUpdateInternal (the NPC branch skips humanoid displays while
// the tables are loaded).
public partial class WorldClient
{
    // The factor follows the unit's display, so the scale is re-sent when the display
    // changes even with no new SCALE_X (a shapeshift, a costume).
    private void TranslateUnitScale(BitArray updateMaskArray, Dictionary<int, UpdateField> updates, ObjectType objectType, ObjectUpdate updateData, WowGuid128 guid)
    {
        if (!IsUnitObjectType(objectType) || !UnitScale.TranslationEnabled)
            return;

        bool scaleInUpdate = InUpdateMask(updateMaskArray, LegacyVersion.GetUpdateField(ObjectField.OBJECT_FIELD_SCALE_X));
        bool displayInUpdate = InUpdateMask(updateMaskArray, LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_DISPLAYID));
        if (!scaleInUpdate && !displayInUpdate)
            return;

        // `updates` holds every field the proxy knows for the unit, this update merged in.
        float serverScale = ReadLegacyFloat(updates, ObjectField.OBJECT_FIELD_SCALE_X);
        if (serverScale <= 0f)
            return;

        var unit = ResolveUnitScaleInputs(objectType, updates);
        if (!IsTranslatedUnit(unit))
            return;

        // The session's pet (by the May block's own test) keeps the May pet path, which reads
        // ObjectData.Scale as the server's value: a mind-controlled humanoid, a humanoid guardian.
        if (IsLocalPet(guid, updateData))
            return;

        var factor = UnitScale.ComputeFactor(unit);
        float clientScale = UnitScale.ToClientObjectScale(serverScale, factor.Value);
        updateData.ObjectData.Scale = clientScale;

        // Fires on every player or humanoid create and scale change: DebugOutput only.
        if (Framework.Settings.DebugOutput)
            Log.Event("unit.scale.translated", new
            {
                guid = guid.ToString(),
                object_type = objectType.ToString(),
                entry = ReadLegacyUInt(updates, ObjectField.OBJECT_FIELD_ENTRY),
                display_id = unit.DisplayId,
                server_scale = serverScale,
                display_scale = factor.DisplayScale,
                race_model_scale = factor.RaceModelScale,
                model_scale = factor.ModelScale,
                known_display = factor.KnownDisplay,
                factor = factor.Value,
                client_scale = clientScale,
            });
    }

    // v1: a player, or a creature drawn with a race's character model (a display with a
    // CreatureDisplayInfoExtra row). Those never have a CreatureFamily, so their factor needs
    // no creature template.
    internal static bool IsTranslatedUnit(in UnitScale.UnitInputs unit)
        => unit.IsPlayer || UnitScale.IsCharacterModelDisplay(unit.DisplayId);

    // What the modern client sizes the unit from, read from the proxy's copy of its 1.12
    // fields (the client gets the same display unchanged). No family or pet terms in v1.
    internal static UnitScale.UnitInputs ResolveUnitScaleInputs(ObjectType objectType, Dictionary<int, UpdateField> fields)
    {
        bool isPlayer = objectType == ObjectType.Player || objectType == ObjectType.ActivePlayer;
        uint displayId = ReadLegacyUInt(fields, UnitField.UNIT_FIELD_DISPLAYID);
        int level = (int)ReadLegacyUInt(fields, UnitField.UNIT_FIELD_LEVEL);
        return new UnitScale.UnitInputs(isPlayer, displayId, level, 0, false);
    }

    // The scale the proxy sends the client for a translated unit right now, and the client's
    // factor. For a unit outside the v1 scope (or with the tables missing) the server's
    // scale, with factor 1: what the client was sent before.
    internal static float ComputeClientUnitScale(ObjectType objectType, Dictionary<int, UpdateField> fields, out float factor)
    {
        float serverScale = ReadLegacyFloat(fields, ObjectField.OBJECT_FIELD_SCALE_X);
        factor = 1.0f;
        if (!UnitScale.TranslationEnabled)
            return serverScale;
        var unit = ResolveUnitScaleInputs(objectType, fields);
        if (!IsTranslatedUnit(unit))
            return serverScale;
        factor = UnitScale.ClientDisplayFactor(unit);
        return UnitScale.ToClientObjectScale(serverScale, factor);
    }

    // JimsProxy (collision-visual-parity #359, translated units): the height the modern
    // client's collision cylinder must have to agree with the model it draws: the model's
    // authored CreatureModelData.CollisionHeight (for the mesh at scale 1.0) times the size
    // the client draws it at, which is the scale the proxy sends times the client's own
    // display factor. With the translation that product is the 1.12 server's SCALE_X.
    // Zero-height models fall back to PlayerHeight at the call site.
    public static float ComputeTranslatedCollisionHeight(int displayId, int mountDisplayId, float clientScale, float displayFactor)
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

    // The May block's test for the session's pet (UpdateHandler, the pet-scale branch).
    private bool IsLocalPet(WowGuid128 guid, ObjectUpdate updateData)
    {
        var state = GetSession().GameState;
        var currentPetGuid = state.CurrentPetGuid;
        var localPlayerGuid = state.CurrentPlayerGuid;
        return (currentPetGuid != default && guid == currentPetGuid)
               || (localPlayerGuid != default
                   && updateData.UnitData.SummonedBy != null
                   && (WowGuid128)updateData.UnitData.SummonedBy == localPlayerGuid);
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
