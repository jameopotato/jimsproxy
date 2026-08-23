using System.Runtime.CompilerServices;
using HermesProxy.Enums;

namespace HermesProxy.Tests;

// JimsProxy (suite-flake mode 2, 2026-08-13): ModernVersion/LegacyVersion latch
// Settings.ClientBuild/ServerBuild in their STATIC CONSTRUCTORS at first touch, and a
// thrown cctor is cached by the runtime for the whole process. The suite used to rely
// on a per-class convention — `if (ClientBuild == Zero) ClientBuild = ...` in test
// constructors — but any UNGUARDED class winning the first-touch race (e.g.
// QuestLogFullTranslationTests, deterministic solo-red) parsed the build "Zero",
// threw ArgumentOutOfRangeException, and poisoned ~50 packet-constructing tests for
// the rest of the run (~5-10% of full runs, order-dependent). Pinning both builds
// before ANY test executes makes the latch deterministic for every current and
// future test; the scattered per-class guards become harmless no-ops.
internal static class TestEnvironmentInitializer
{
    [ModuleInitializer]
    internal static void PinProtocolBuilds()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
        if (global::Framework.Settings.ServerBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ServerBuild = ClientVersionBuild.V1_12_1_5875;
    }
}
