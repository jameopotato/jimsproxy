# JimsProxy configuration reference

This reference lists every configuration key JimsProxy reads, its default value, and its
intended use. Installation and setup are covered in the
[manual installation guide](MANUAL-INSTALL.md) and the [quick-start guide](QUICK-INSTALL.md).

---

## The configuration file

The proxy reads **`HermesProxy.config`** (XML) from the folder that contains its executable.
The file name is inherited from the upstream project. At startup the proxy sets its working
directory to the executable's folder, so `HermesProxy.config`, `CSV/`, `Logs/`, and
`AccountData/` are always resolved relative to the executable, regardless of how it is started.

**Launcher installations.** The Classic WoW Launcher ([jimothy.cc/install](https://jimothy.cc/install))
manages this file. Setup, repair, and proxy updates regenerate it from the launcher's template,
carrying over the settings the launcher knows, and each save in the launcher's Settings tab
rewrites the keys the launcher manages. Manual edits to those keys are overwritten. In a
launcher installation, change settings through the Settings tab.

### Command-line overrides

Any key can be overridden for one run without editing the file:

```
JimsProxy --set ServerAddress=logon.example.com --set ServerPort=3724
JimsProxy --config MyOtherServer.config
```

`--config` selects a different configuration file, which allows one file per server. All flags
are listed in the manual guide under [Command line flags](MANUAL-INSTALL.md#command-line-flags).

---

## Key groups

| Group | Intended use |
|---|---|
| **Connection** | Required for a working connection. |
| **Ports** | Local listener ports; must be free and must match the client's portal setting. |
| **Diagnostics** | Enable while collecting information for a bug report; disable afterwards. |
| **Tuning** | Adjustable behaviour with no effect on whether the connection works. |
| **Kill switches** | Enabled by default. Setting one to `false` disables a shipped fix. |
| **Experimental** | Disabled by default. Intended for testing at a developer's request. |

19 of the 40 keys are present in the shipped configuration file. The remaining keys are read with
their built-in defaults and take effect only when added to the file. The **In file** column
marks which is which. **Default** is the effective out-of-the-box value: the shipped file's value
for keys marked ✅, the built-in default for keys marked ❌. Where the shipped value differs from
the built-in default, the row says so.

---

## Connection

| Key | Default | In file | Description |
|---|---|:---:|---|
| `ServerAddress` | `127.0.0.1` | ✅ | The server's login address (the value used with `SET REALMLIST` on a 1.12 client). Kronos: `login.twinstar-wow.com`; Kronos 2: `login2.twinstar-wow.com`; Kronos 3: `login3.twinstar-wow.com`. |
| `ServerPort` | `3724` | ✅ | Login port. |
| `ServerBuild` | `auto` | ✅ | Legacy server version. `auto` derives it from `ClientBuild` (1.12.1 for a 1.14 client). Explicit values: `5875` (1.12.1), `8606` (2.4.3). |
| `ClientBuild` | `42597` | ✅ | Must match the game client's build; a mismatch fails at login. `42597` is 1.14.2. Other accepted values: `40618` (1.14.0), `41794` (1.14.1), `40892` (2.5.2), `42328` (2.5.3). |
| `ClientSeed` | *(static seed)* | ✅ | Fallback authentication seed. Per-build seeds are loaded from `CSV/BuildAuthSeeds.csv` at startup and take precedence. The value corresponds to the seed used by the Arctium launcher's `--staticseed` option. Leave unchanged. |
| `ServerType` | `Kronos` | ✅ | Server fork. Selects fork-specific wire formats for some client messages and the item-data overlays loaded at startup. Values: `Kronos`, `Generic`. `Generic` is untested scaffolding for other forks. |
| `ReportedOS` | `OSX` | ✅ | Operating system identifier reported to the server. |
| `ReportedPlatform` | `x86` | ✅ | Platform identifier reported to the server. |
| `ExternalAddress` | `127.0.0.1` | ✅ | The address clients connect to. Relevant only when the proxy serves clients on other machines. |

When `ClientBuild` is absent from the file, the built-in default is build `40892` (2.5.2), not a
Classic Era build. Keep the key present.

## Ports

All four listeners bind to `127.0.0.1`, and all four ports must be free.

| Key | Default | In file | Description |
|---|---|:---:|---|
| `BNetPort` | `1119` | ✅ | Battle.net (portal) listener. The port referenced by `SET portal` in the client's `Config.wtf`. |
| `RealmPort` | `8084` | ✅ | Realm listener. |
| `InstancePort` | `8086` | ✅ | World listener. Its `Starting WorldSocket service` startup line is the ready signal. |
| `RestPort` | `8081` | ✅ | REST listener. |

`BNetPort` and the client's portal must match: `SET portal "127.0.0.1:1119"` in `WTF/Config.wtf`
pairs with `BNetPort=1119`. A mismatch prevents the client from reaching the proxy.

A second proxy instance on the same machine requires a different port set (for example
`1120 / 8082 / 8085 / 8087`) and a client portal that references the second instance's
`BNetPort`.

## Diagnostics

| Key | Default | In file | Description |
|---|---|:---:|---|
| `StructuredLog` | `true` | ✅ | Writes structured JSONL diagnostic events to `Logs/jimsproxy-*.jsonl`, one file per session. Required for actionable bug reports. |
| `DebugOutput` | `false` | ✅ | Additional detail in the console and logs. Enable while reproducing a problem. |
| `PacketsLog` | `false` | ✅ | Writes a full packet capture per session to `PacketsLog/`. Files are large and accumulate. Enable only when a capture is requested. The built-in default is `true`; removing the key enables captures. |
| `VerboseLog` | `false` | ✅ | Per-packet console output. Very high volume; intended for detailed debugging. |
| `SpanStatsLog` | `false` | ❌ | Enables the `SpanStats` developer log category. |

## Tuning: cast timing

| Key | Default | Range | In file | Description |
|---|---|---|:---:|---|
| `SpellQueueWindowMs` | `400` | 0–1300 | ❌ | Width of the spell-queue window. A press arriving within the last N ms of an active global cooldown or cast bar is held and released at expiry; earlier presses are forwarded and the server arbitrates. `400` matches the retail client. Higher values (for example `1000`, `1300`) queue earlier presses. |
| `SpellCastEarlyFireOffsetMs` | `0` | 0–50 | ✅ | Releases a held cast this many ms before the estimated global-cooldown expiry, compensating for network latency. Higher values can increase "Spell not ready" rejections on low-latency connections. |
| `FormExitStartDeferMs` | `100` | 0–300 | ❌ | Druid form-exit fix (#379). After a local form cancel, defers the next cast's `SPELL_START` so the model swap renders first, which prevents a looping cast sound or stuck transform sound. Compensates for client render time, not network latency. `0` disables. |

## Tuning: gameplay and compatibility

| Key | Default | In file | Description |
|---|---|:---:|---|
| `ThreatEngine` | `false` | ✅ | Synthesizes threat data so the 1.14 client's threat APIs are populated. Vanilla 1.12 servers do not send threat values. Required for threat-meter addons. Inactive in battlegrounds. |
| `EnablePallyPowerInterop` | `true` | ❌ | Translates PallyPower blessing-assignment indices between the 1.12 and 1.14 addon versions. Limited to the `PLPWR` addon prefix. |
| `ClientTcpNoDelay` | `true` | ❌ | `TCP_NODELAY` on the client-facing socket. `false` restores kernel-batched (Nagle) delivery, which correlated with worse cast responsiveness in testing. The proxy-to-server socket is unaffected. |

## Reliability and timeouts

| Key | Default | Range | In file | Description |
|---|---|---|:---:|---|
| `AuthHandshakeTimeoutMs` | `15000` | 1000–60000 | ❌ | Upper bound on the login handshake. If the login server accepts the connection but does not respond, login fails within this time instead of waiting indefinitely. |
| `EnableUnplannedReconnect` | `false` | — | ❌ | On an abrupt mid-session disconnect, attempts one reconnect with the cached session key before giving up. When `false`, the disconnect is reported to the client immediately. |
| `UnplannedReconnectTimeoutMs` | `5000` | 1000–30000 | ❌ | Timeout for that reconnect attempt. |

## Kill switches

Each key enables a shipped fix and is `true` by default. Setting a key to `false` disables the
fix and restores the behaviour listed in the table. The keys exist to isolate regressions
without changing proxy builds. None of them is present in the shipped configuration file.

| Key | Default | Behaviour when set to `false` |
|---|---|---|
| `StrafeCancelPreempt` | `true` | Cancelling a cast by strafing takes about 700 ms to register instead of about 190 ms. The 1.14 client does not send a cancel on strafe; the proxy synthesizes it. |
| `SynthStandOnFear` | `true` | A character feared while seated remains seated for the fear's duration, and fear-break trinkets fail with "not standing". |
| `WorldEntryCarriedRootCure` | `true` | Movement lock after crossing a loading boundary while the server considers the character rooted (#328). |
| `LoginEvictionMerge` | `true` | A permanently stalled loading screen when the server evicts the character at login from a full instance. |
| `LoginPreCreateOpHold` | `true` | Input lock (no turning or casting until relog) on the same evicted-login path. |
| `StuckLogoutStunCancelFix` | `true` | "You can't do that while stunned" after a fast relogin following an abrupt in-combat disconnect. |
| `StuckLogoutStunClientStrip` | `true` | The client-side half of the previous fix. |
| `Charm382StripPetInCombat` | `true` | Suspected cause of a severe frame-rate drop near player-on-player mind control in battlegrounds (#382). |

## Experimental

Disabled by default and under evaluation. Intended for testing at a developer's request.

| Key | Default | In file | Description |
|---|---|:---:|---|
| `LowLatencyMode` | `false` | ❌ | Forwards every cast immediately instead of holding it during the global cooldown. Removes hold-queue races that can leave spells stuck for players under about 40 ms round-trip time. On typical latency the server rejects more casts. |
| `SuppressSpellCastErrors` | `false` | ❌ | Hides transient "Spell not ready" and "Another action is in progress" messages during rapid input. Cosmetic; independent of `LowLatencyMode`. |
| `RttPrefire` | `off` | ❌ | Cast chaining across the global-cooldown boundary. Read only when `LowLatencyMode` is `true`. `off`: forward everything. `timer`: hold a press arriving in the last 400 ms of the cooldown and release it at the estimated expiry. Further developer-only modes exist for testing and are not supported. |
| `IdentityPinnedCastIds` | `false` | ❌ | Makes cast start/finish pairing deterministic. Effective only when `LowLatencyMode` is `true`. |
| `RefireSpellGo` | `false` | ❌ | Re-sends a stripped duplicate of an instant cast's completion event about 8 ms later, closing casts the client dropped when start and finish arrived in the same frame (stuck cast pose, looping cast sound, lit action button). Independent of `LowLatencyMode`. |

---

## Obsolete keys

`OverrideRtt` and `UncapFireOffset` are no longer read. They can be removed from older
configuration files.

## Minimum configuration

Every key has a built-in default, so the proxy starts from a sparse file, subject to the
`ClientBuild` and `PacketsLog` notes above. The file itself must exist; the proxy exits at
startup without it. Startup validation checks that `ClientSeed` is well-formed, that
`ClientBuild` and `ServerBuild` are supported versions, and that the five ports are within
1–65535; a failed check is reported and the proxy exits. `ServerAddress` is not validated; an
incorrect address results in a failed connection. The recommended configuration is the shipped
file with `ServerAddress` changed.
