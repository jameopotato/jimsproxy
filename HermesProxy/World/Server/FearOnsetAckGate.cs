using HermesProxy.World.Enums;

namespace HermesProxy.World.Server;

/// <summary>
/// JimsProxy (#479 PvP fear statue, D1): pure decision for reshaping the local player's forced
/// speed-change acknowledgement while the player has no control, extracted so the gate is
/// unit-testable (see <c>HermesProxy.Tests/World/FearOnsetAckGateTests</c>).
///
/// Mechanism (wire, 2026-10): a speed-raising fear makes the server send a forced run-speed change in
/// the onset tick. The 1.14 client, having just lost control, acknowledges it with the Falling flag set,
/// fall time 0 and zero jump speeds. Forwarded unchanged (vanilla Falling 0x2000, plus the jump block),
/// that report is the common factor of every decoded statue: the server adopts it and never starts the
/// flee legs it sends to native victims. Native 1.12 clients answer the same packet with flags 0 in that
/// spot. Clearing the whole modern moving mask (not only Falling: one onset acknowledgement carried a held
/// strafe bit) makes the legacy acknowledgement go out as flags 0 / fall time 0.
///
/// Gate truth table:
///   in control, any shape                                   -> leave alone
///   control lost, Falling, fall time 0, zero jump speeds    -> clear moving mask (the onset transient)
///   control lost, Falling, fall time > 0 or a jump speed    -> leave alone (a real fall in progress)
///   control lost, no Falling flag                           -> leave alone
/// </summary>
internal static class FearOnsetAckGate
{
    internal static bool IsOnsetTransientFallingAck(bool hasControl, uint modernFlags, uint fallTime, float jumpVerticalSpeed, float jumpHorizontalSpeed)
        => !hasControl
           && (modernFlags & (uint)MovementFlagModern.Falling) != 0
           && fallTime == 0
           && jumpVerticalSpeed == 0f
           && jumpHorizontalSpeed == 0f;

    internal static uint ClearMovingMask(uint modernFlags)
        => modernFlags & ~(uint)MovementFlagModern.MaskMoving;
}
