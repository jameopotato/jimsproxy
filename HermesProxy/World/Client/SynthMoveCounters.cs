namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (P1-2, check D-01): MoveCounters for the proxy's own SET-class movement packets
/// that used to go out with counter 0. The 1.12 server sends every SET with counter 0 as well,
/// so a counter-0 acknowledgement of one of these looked exactly like an acknowledgement of a
/// server change and was forwarded to the server as one, for a change it never sent. Each site
/// now carries its own value, so its acknowledgement can be recognised and kept in the proxy.
///
/// The values sit in the reserved range of <see cref="WorldEntryCeremonyTracker.IsSynthCounter"/>
/// next to the carried-root cure unroot (0xFFFFFF02) and the charge strafe-latch pair
/// (<see cref="ChargePendLatchCure"/>, 0xFFFFFF03/04), below the 0xFFFFFFFF transport-clear
/// teleport sentinel. One value per site, so a capture names the site that sent it.
/// </summary>
public static class SynthMoveCounters
{
    /// <summary>SMSG_MOVE_SET_RUN_SPEED restoring the cached run speed when the player regains control.</summary>
    public const uint RegainControlRunSpeed = 0xFFFFFF05;

    /// <summary>SMSG_MOVE_UNROOT of the resumed-taxi landing (<c>SendTaxiDismountRestore</c>).</summary>
    public const uint TaxiResumeDismountUnroot = 0xFFFFFF06;

    /// <summary>SMSG_MOVE_UNROOT of the taxi landing scheduled by <c>HandleMonsterMove</c>.</summary>
    public const uint TaxiDismountUnroot = 0xFFFFFF07;

    /// <summary>SMSG_MOVE_SET_RUN_SPEED reasserted on the first destination self update after a world port.</summary>
    public const uint PostTeleportRunSpeed = 0xFFFFFF08;

    public static bool IsSynthAckCounter(uint moveCounter)
        => moveCounter is RegainControlRunSpeed
            or TaxiResumeDismountUnroot
            or TaxiDismountUnroot
            or PostTeleportRunSpeed;
}
