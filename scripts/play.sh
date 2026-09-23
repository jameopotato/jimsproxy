#!/usr/bin/env bash
# play.sh: start JimsProxy, wait until it is ready, start the game, and stop the proxy when
# the game exits. Linux and macOS. See docs/MANUAL-INSTALL.md.

set -u

READY_LINE="Starting WorldSocket service"
SHUTDOWN_SENTINEL="__LAUNCHER_SHUTDOWN__"
SHUTDOWN_ACK="__PROXY_SHUTDOWN_ACK__"

TIMEOUT=60   # seconds to wait for the ready line

PROXY_DIR=""
GAME_EXE=""
GAME_CMD=""

usage() {
  cat <<'USAGE'
Usage: play.sh [--proxy-dir DIR] [--game-exe PATH] [--game-cmd CMD]

  --proxy-dir DIR   folder containing JimsProxy
                    (default: this script's folder, then ./Hermes, then ../Hermes)
  --game-exe PATH   WowClassic_ForCustomServers.exe
                    (default: <root>/World of Warcraft/_classic_era_/, where <root> contains Hermes)
  --game-cmd CMD    command that starts the game (default: wine "<game-exe>"),
                    for example a Lutris, Bottles or Steam command
  -h, --help        show this text
USAGE
}

while [ $# -gt 0 ]; do
  case "$1" in
    --proxy-dir) PROXY_DIR="$2"; shift 2 ;;
    --game-exe)  GAME_EXE="$2"; shift 2 ;;
    --game-cmd)  GAME_CMD="$2"; shift 2 ;;
    -h|--help)   usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

say()  { printf '[play] %s\n' "$*"; }
die()  { printf '[play] ERROR: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------- locate files
script_dir="$(cd "$(dirname "$0")" && pwd)"

find_proxy_dir() {
  local d
  for d in "$PROXY_DIR" "$script_dir" "$script_dir/Hermes" "$script_dir/../Hermes" "$PWD" "$PWD/Hermes"; do
    [ -n "$d" ] || continue
    if [ -x "$d/JimsProxy" ] || [ -f "$d/JimsProxy" ]; then
      (cd "$d" && pwd); return 0
    fi
  done
  return 1
}

PROXY_DIR="$(find_proxy_dir)" || die "JimsProxy binary not found. Pass --proxy-dir <folder containing JimsProxy>.
       JimsProxy.exe is the Windows build; Linux and macOS need the native binary built from source."
PROXY_BIN="$PROXY_DIR/JimsProxy"
[ -x "$PROXY_BIN" ] || chmod +x "$PROXY_BIN" 2>/dev/null || die "cannot make $PROXY_BIN executable"
[ -f "$PROXY_DIR/HermesProxy.config" ] || die "HermesProxy.config is missing next to $PROXY_BIN.
       The direct download bundle does not include one. Get it from
       https://raw.githubusercontent.com/jameopotato/jimsproxy/master/HermesProxy/HermesProxy.config
       put it beside the proxy binary, and set ServerAddress (see docs/MANUAL-INSTALL.md)."
[ -d "$PROXY_DIR/CSV" ] || die "CSV/ folder is missing next to $PROXY_BIN; the proxy cannot start without it."

root_dir="$(dirname "$PROXY_DIR")"
if [ -z "$GAME_EXE" ]; then
  for cand in \
    "$root_dir/World of Warcraft/_classic_era_/WowClassic_ForCustomServers.exe" \
    "$root_dir/_classic_era_/WowClassic_ForCustomServers.exe" \
    "$root_dir/WowClassic_ForCustomServers.exe"; do
    [ -f "$cand" ] && { GAME_EXE="$cand"; break; }
  done
fi
if [ -z "$GAME_EXE" ] && [ -z "$GAME_CMD" ]; then
  die "game client not found. Pass --game-exe <path to WowClassic_ForCustomServers.exe> or --game-cmd '<command>'."
fi
if [ -n "$GAME_EXE" ] && [ ! -f "$GAME_EXE" ]; then
  die "game executable does not exist: $GAME_EXE"
fi
if [ -z "$GAME_CMD" ]; then
  command -v wine >/dev/null 2>&1 || die "wine is not on PATH. Install Wine, or pass --game-cmd with the command that starts the game."
  GAME_CMD="wine \"$GAME_EXE\""
fi

# ---------------------------------------------------------------- config values
config_value() {  # config_value Key -> value from HermesProxy.config
  sed -n "s/.*<add key=\"$1\" value=\"\([^\"]*\)\".*/\1/p" "$PROXY_DIR/HermesProxy.config" | head -n 1
}
BNET_PORT="$(config_value BNetPort)";        BNET_PORT="${BNET_PORT:-1119}"
REALM_PORT="$(config_value RealmPort)";      REALM_PORT="${REALM_PORT:-8084}"
INSTANCE_PORT="$(config_value InstancePort)"; INSTANCE_PORT="${INSTANCE_PORT:-8086}"
REST_PORT="$(config_value RestPort)";        REST_PORT="${REST_PORT:-8081}"
SERVER_ADDRESS="$(config_value ServerAddress)"

if [ "${SERVER_ADDRESS:-127.0.0.1}" = "127.0.0.1" ]; then
  say "WARNING: ServerAddress in HermesProxy.config is still 127.0.0.1 (localhost)."
  say "         For Kronos, set it to login.twinstar-wow.com. See docs/MANUAL-INSTALL.md."
fi

# ---------------------------------------------------------------- port check
port_listening() {  # true if something already listens on TCP port $1
  local p="$1"
  if command -v ss >/dev/null 2>&1; then
    ss -ltn 2>/dev/null | awk 'NR>1{print $4}' | grep -Eq "[:.]${p}\$"; return
  fi
  if command -v lsof >/dev/null 2>&1; then
    lsof -nP -iTCP:"$p" -sTCP:LISTEN >/dev/null 2>&1; return
  fi
  if command -v netstat >/dev/null 2>&1; then
    netstat -an 2>/dev/null | awk '$6=="LISTEN"{print $4}' | grep -Eq "[:.]${p}\$"; return
  fi
  return 1
}
busy=""
for p in "$BNET_PORT" "$REALM_PORT" "$INSTANCE_PORT" "$REST_PORT"; do
  port_listening "$p" && busy="$busy $p"
done
if [ -n "$busy" ]; then
  die "port(s)$busy already in use. A previous JimsProxy is probably still running; stop it
       (pgrep -fl JimsProxy) or change the ports in HermesProxy.config."
fi

# ---------------------------------------------------------------- portal fix
if [ -n "$GAME_EXE" ]; then
  game_dir="$(dirname "$GAME_EXE")"
  wtf="$game_dir/WTF/Config.wtf"
  expected="SET portal \"127.0.0.1:${BNET_PORT}\""
  if [ -f "$wtf" ]; then
    current="$(grep -E '^SET portal ' "$wtf" | head -n 1 | tr -d '\r')"
    if [ "$current" != "$expected" ]; then
      cp "$wtf" "$wtf.bak"
      if [ -n "$current" ]; then
        awk -v repl="$expected" 'BEGIN{done=0} /^SET portal /{ if(!done){print repl; done=1}; next } {print}' "$wtf" > "$wtf.tmp"
      else
        cat "$wtf" > "$wtf.tmp"; printf '%s\n' "$expected" >> "$wtf.tmp"
      fi
      mv "$wtf.tmp" "$wtf"
      say "Config.wtf: set portal to 127.0.0.1:${BNET_PORT} (backup in Config.wtf.bak)"
    fi
  else
    mkdir -p "$game_dir/WTF"
    printf '%s\nSET textLocale "enUS"\nSET audioLocale "enUS"\n' "$expected" > "$wtf"
    say "Config.wtf: created with portal 127.0.0.1:${BNET_PORT}"
  fi
fi

# ---------------------------------------------------------------- start proxy
LOG="$PROXY_DIR/play-console.log"
: > "$LOG"
tmpdir="$(mktemp -d)"
mkfifo "$tmpdir/stdin"
exec 3<>"$tmpdir/stdin"            # keep both ends open: the proxy's stdin must stay open (not EOF)

PROXY_PID=""
TAIL_PID=""
cleanup() {
  local rc=$?
  trap - EXIT INT TERM
  if [ -n "$PROXY_PID" ] && kill -0 "$PROXY_PID" 2>/dev/null; then
    stop_proxy
  fi
  [ -n "$TAIL_PID" ] && kill "$TAIL_PID" 2>/dev/null
  exec 3>&- 2>/dev/null
  rm -rf "$tmpdir"
  exit "$rc"
}
stop_proxy() {
  say "stopping the proxy..."
  printf '%s\n' "$SHUTDOWN_SENTINEL" >&3 2>/dev/null || true
  local i
  for i in 1 2 3 4 5 6 7 8 9 10; do          # up to 5 s for a clean exit (flushes the JSONL log)
    kill -0 "$PROXY_PID" 2>/dev/null || { say "proxy exited cleanly"; return 0; }
    sleep 0.5
  done
  kill -TERM "$PROXY_PID" 2>/dev/null        # SIGTERM is also handled gracefully by the proxy
  for i in 1 2 3 4 5 6; do
    kill -0 "$PROXY_PID" 2>/dev/null || { say "proxy exited"; return 0; }
    sleep 0.5
  done
  say "proxy did not exit, killing it"
  kill -KILL "$PROXY_PID" 2>/dev/null
  wait "$PROXY_PID" 2>/dev/null
}
trap cleanup EXIT INT TERM HUP

say "starting $PROXY_BIN"
(cd "$PROXY_DIR" && exec "$PROXY_BIN" --no-version-check) <&3 >"$LOG" 2>&1 &
PROXY_PID=$!
tail -n +1 -f "$LOG" 2>/dev/null &
TAIL_PID=$!

# ---------------------------------------------------------------- wait for ready
say "waiting for \"$READY_LINE\" (up to ${TIMEOUT}s)..."
deadline=$(( SECONDS + TIMEOUT ))
while ! grep -q "$READY_LINE" "$LOG" 2>/dev/null; do
  if ! kill -0 "$PROXY_PID" 2>/dev/null; then
    sleep 0.5
    die "the proxy exited before it was ready. Read the lines above (full log: $LOG)."
  fi
  if grep -Eq "Config loading failed|verification of the config failed|Failed to start|AesGcm is not supported" "$LOG" 2>/dev/null; then
    sleep 0.5
    die "the proxy reported a startup error. Read the lines above (full log: $LOG)."
  fi
  [ "$SECONDS" -lt "$deadline" ] || die "timed out after ${TIMEOUT}s waiting for the proxy to become ready (log: $LOG)"
  sleep 0.5
done
say "proxy is ready on 127.0.0.1:${BNET_PORT}"

# ---------------------------------------------------------------- start game
say "starting the game: $GAME_CMD"
game_started=$SECONDS
if [ -n "$GAME_EXE" ]; then
  (cd "$(dirname "$GAME_EXE")" && eval "$GAME_CMD")
else
  eval "$GAME_CMD"
fi
game_rc=$?
# Steam/Lutris/Bottles commands often return immediately while the game keeps
# running detached. If a WoW process is still alive, keep waiting for it.
ancestor_pids() {  # this script and every shell above it (their command lines may mention the exe)
  local p=$$
  while [ -n "$p" ] && [ "$p" != 0 ] && [ "$p" != 1 ]; do
    echo "$p"
    p="$(ps -o ppid= -p "$p" 2>/dev/null | tr -d ' ')"
  done
}
EXCLUDE_PIDS="$(ancestor_pids; echo "$PROXY_PID"; echo "$TAIL_PID")"
game_running() {  # a WowClassic*.exe process that is not us, our parents, the proxy or the log tail
  pgrep -f 'WowClassic[^ ]*\.exe' 2>/dev/null | grep -vxF "$EXCLUDE_PIDS" | grep -q .
}
if [ "$(( SECONDS - game_started ))" -lt 5 ]; then
  # Returned almost immediately: give a detached launcher up to 5 s to spawn the game.
  for _ in 1 2 3 4 5; do game_running && break; sleep 1; done
fi
if game_running; then
  say "game is running (detached); waiting for it to exit..."
  while game_running; do sleep 2; done
fi
say "game exited (code $game_rc)"
exit 0
