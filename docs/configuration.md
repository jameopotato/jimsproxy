# JimsProxy configuration reference

Every configuration key JimsProxy reads, with its default and intended use. Installation is
covered in the [manual](MANUAL-INSTALL.md) and [quick-start](QUICK-INSTALL.md) guides.

---

## The configuration file

The proxy reads `HermesProxy.config` (XML; the name is inherited from upstream) from the folder
that contains its executable, and resolves `CSV/`, `Logs/` and `AccountData/` from the same
folder, however it is started.

In a launcher installation ([jimothy.cc/install](https://jimothy.cc/install)) the launcher manages
this file: setup, repair and proxy updates regenerate it, and each save in the launcher's
Settings tab rewrites the keys it manages. Change settings there.

Command-line flags override keys for one run, and `--config` selects another file, for example
one per server. All flags: [Command line flags](MANUAL-INSTALL.md#command-line-flags).

```
JimsProxy --set ServerAddress=logon.example.com --set ServerPort=3724
JimsProxy --config MyOtherServer.config
```

**In file** marks the 19 keys present in the shipped file; the other 21 take their built-in
default unless added. **Default** is the effective out-of-the-box value.

---

## Connection

| Key | Default | In file | Description |
|---|---|:---:|---|
| `ServerAddress` | `127.0.0.1` | ✅ | Server login address. Kronos: `login.twinstar-wow.com`, `login2.twinstar-wow.com`, `login3.twinstar-wow.com`. |
| `ServerPort` | `3724` | ✅ | Login port. |
| `ServerBuild` | `auto` | ✅ | Legacy server build. `auto` selects `5875` (1.12.1) for a 1.14 client. Explicit values: `5875`, `6005`, `6141` (1.12.x), `8606` (2.4.3). |
| `ClientBuild` | `42597` | ✅ | Must match the client build exactly. `42597` (1.14.2) is the tested build. If the key is absent the built-in default is `40892` (2.5.2), so keep it. |
| `ClientSeed` | *(static seed)* | ✅ | Fallback authentication seed; per-build seeds from `CSV/BuildAuthSeeds.csv` take precedence. Leave unchanged. |
| `ServerType` | `Kronos` | ✅ | Server fork: selects fork-specific message formats and item-data overlays. `Kronos` or `Generic` (untested). |
| `ReportedOS` | `OSX` | ✅ | Operating system reported to the server. |
| `ReportedPlatform` | `x86` | ✅ | Platform reported to the server. |
| `ExternalAddress` | `127.0.0.1` | ✅ | Address clients connect to; relevant only when serving other machines. |

## Ports

With the default `ExternalAddress`, all listeners bind to `127.0.0.1`. All four ports must be free.

| Key | Default | In file | Description |
|---|---|:---:|---|
| `BNetPort` | `1119` | ✅ | Login listener. `SET portal` in the client's `WTF/Config.wtf` must reference it. |
| `RestPort` | `8081` | ✅ | REST listener. |
| `RealmPort` | `8084` | ✅ | Realm listener. |
| `InstancePort` | `8086` | ✅ | World listener; its `Starting WorldSocket service` line is the ready signal. |

A second instance on the same machine needs its own port set, for example
`1120 / 8082 / 8085 / 8087`.

## Diagnostics

| Key | Default | In file | Description |
|---|---|:---:|---|
| `StructuredLog` | `true` | ✅ | JSONL diagnostic log per session in `Logs/`. Needed for bug reports. |
| `DebugOutput` | `false` | ✅ | Extra console and log detail, for reproducing a problem. |
| `PacketsLog` | `false` | ✅ | Full packet capture per session in `PacketsLog/`; large. Enable only for a requested capture. The built-in default is `true`: removing the key enables captures. |
| `VerboseLog` | `false` | ✅ | Per-packet console output; very high volume. |
| `SpanStatsLog` | `false` | ❌ | Developer log category `SpanStats`. |

## Cast timing

| Key | Default | Range | In file | Description |
|---|---|---|:---:|---|
| `SpellQueueWindowMs` | `400` | 0–1300 | ❌ | A press within the last N ms of an active global cooldown or cast bar is held and released at expiry; earlier presses go to the server. `400` matches retail. |
| `SpellCastEarlyFireOffsetMs` | `0` | 0–50 | ✅ | Releases a held cast N ms before the estimated expiry to offset latency. Higher values can cause "Spell not ready" rejections. |
| `FormExitStartDeferMs` | `100` | 0–300 | ❌ | After a druid form cancel, delays the next cast's start so the model swap renders first, preventing looping cast or transform sounds (#379). `0` disables. |

## Gameplay and compatibility

| Key | Default | In file | Description |
|---|---|:---:|---|
| `ThreatEngine` | `false` | ✅ | Synthesizes threat for threat-meter addons; vanilla servers send none. Inactive in battlegrounds. |
| `EnablePallyPowerInterop` | `true` | ❌ | Translates PallyPower blessing assignments between 1.12 and 1.14 players (`PLPWR` prefix only). |
| `ClientTcpNoDelay` | `true` | ❌ | `TCP_NODELAY` on the client socket. `false` restores Nagle batching, which tested worse for cast responsiveness. |

## Reliability

| Key | Default | Range | In file | Description |
|---|---|---|:---:|---|
| `AuthHandshakeTimeoutMs` | `15000` | 1000–60000 | ❌ | Login fails after this long if the login server accepts the connection but does not respond. |
| `EnableUnplannedReconnect` | `false` | — | ❌ | After an abrupt disconnect, try one reconnect with the cached session key. |
| `UnplannedReconnectTimeoutMs` | `5000` | 1000–30000 | ❌ | Timeout for that reconnect attempt. |

## Kill switches

`true` by default and not in the shipped file. Each enables a shipped fix; `false` restores the
behaviour listed, for isolating regressions.

| Key | Behaviour when `false` |
|---|---|
| `StrafeCancelPreempt` | Strafe-cancelling a cast registers after about 700 ms instead of 190 ms. |
| `SynthStandOnFear` | A character feared while seated stays seated; fear-break trinkets fail. |
| `WorldEntryCarriedRootCure` | Movement lock after a loading screen while the server considers the character rooted (#328). |
| `LoginEvictionMerge` | Loading screen stalls when the server evicts the character from a full instance at login. |
| `LoginPreCreateOpHold` | Input lock on the same evicted-login path. |
| `StuckLogoutStunCancelFix` | "You can't do that while stunned" after a fast relogin following an in-combat disconnect. |
| `StuckLogoutStunClientStrip` | Client-side half of the previous fix. |
| `Charm382StripPetInCombat` | Suspected cause of frame-rate drops near mind-controlled players in battlegrounds (#382). |

## Experimental

Off by default and not in the shipped file. For testing at a developer's request.

| Key | Description |
|---|---|
| `LowLatencyMode` | Forwards every cast immediately instead of holding it during the global cooldown. Helps below about 40 ms round-trip time; at typical latency the server rejects more casts. |
| `SuppressSpellCastErrors` | Hides transient "Spell not ready" and "Another action is in progress" messages. |
| `RttPrefire` | Only with `LowLatencyMode`. `off` (default) forwards everything; `timer` holds a press in the last 400 ms of the cooldown and releases it at the estimated expiry. Other modes are developer-only. |
| `IdentityPinnedCastIds` | Only with `LowLatencyMode`. Deterministic cast start/finish pairing. |
| `RefireSpellGo` | Re-sends an instant cast's completion about 8 ms later to close casts the client dropped (stuck pose, looping sound, lit button). |

---

## Obsolete keys

`OverrideRtt` and `UncapFireOffset` are no longer read and can be removed.

## Validation

The file must exist; every key has a built-in default (see `ClientBuild` and `PacketsLog`
above). At startup the proxy exits on a malformed `ClientSeed`, an unsupported `ClientBuild` or
`ServerBuild`, or a port outside 1–65534. `ServerAddress` is not validated.
