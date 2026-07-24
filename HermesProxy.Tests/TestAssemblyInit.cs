using System.Runtime.CompilerServices;
using HermesProxy.Enums;

namespace HermesProxy.Tests;

// Seeds the process-wide client build BEFORE any test class's static constructor runs.
//
// Why: many test classes carry a `static ctor { if (ClientBuild == Zero) ClientBuild = X; }` guard,
// and X differs (MovementInfoSpanTests seeds V9_0_1_36216; a dozen World tests seed V1_14_2_42597).
// xUnit runs classes in parallel, so WHICHEVER static ctor ran first seeded the whole run — and under
// 9.0.1 several vanilla cast opcodes don't resolve (ModernVersion.GetCurrentOpcode -> 0 ->
// GetUniversalOpcode -> MSG_NULL_ACTION), which intermittently broke every opcode-asserting test in
// the run (first observed as the HoneyWriter hold tests all failing together). A ModuleInitializer
// runs at assembly load, before any static ctor, so the seed race is gone: every run is 1.14.2 — the
// build the overwhelming majority of the suite targets and under which all classes are proven green.
// The per-class if-Zero guards remain as harmless no-ops.
internal static class TestAssemblyInit
{
    [ModuleInitializer]
    internal static void SeedClientBuild()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }
}
