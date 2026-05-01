"""
extract-gcd-csv.py -- regenerate HermesProxy/CSV/SpellOffGcd1.csv, Spell1sGcd1.csv,
and SpellChanneled1.csv from a vanilla 1.12 client's Spell.dbc.

These CSVs drive the GCD hold-and-fire and channeled-spell logic in jimsproxy:
  - SpellOffGcd1.csv      : spells that bypass the GCD hold entirely (off-GCD, issue #43)
  - Spell1sGcd1.csv       : spells that trigger a 1000ms GCD (rogue energy, feral cat-form)
                            instead of the default 1500ms (issue #43)
  - SpellChanneled1.csv   : channeled spells (AttributesEx & 0x44) whose SPELL_START must
                            be forwarded to the client for channel bar timing (issue #91)

HOW TO RUN

    pip install mpyq
    python scripts/extract-gcd-csv.py --client "C:\path\to\World of Warcraft 1.12"

The client must be a real 1.12.x install (Kronos-recommended works). We load Spell.dbc
from Data/patch-2.MPQ because WoW's MPQ load order means patch-2 overrides patch which
overrides dbc, so patch-2's version is what the game actually uses. If your client
directory is structured differently you can point --dbc directly at an extracted
Spell.dbc file.

GROUND TRUTH

The server's spell_template SQL table (vmangos / cmangos / mangos) is NOT authoritative
for client-visible spells -- it's missing many crafting, profession, and cosmetic spells
that live only in the client's Spell.dbc. Only the client DBC has a complete picture of
what StartRecoveryCategory / StartRecoveryTime look like for every spell the 1.14 client
might send as CMSG_CAST_SPELL.

FIELD LAYOUT (vanilla 1.12, 173 fields, 8 locales)

Per vmangos core/SpellMgr/SpellEntry.h:
    Index 0   ID
    Index 6   Attributes            (SPELL_ATTR_PASSIVE = 0x40, HIDDEN = 0x80)
    Index 61-63 Effect[0..2]        (all-zero = server-only / dummy)
    Index 120 spellName enUS offset (into string block)
    Index 157 StartRecoveryCategory (0 = no GCD grouping -> off-GCD)
    Index 158 StartRecoveryTime     (0 = no GCD; 1000 = rogue/feral; 1500 = standard)

FILTERS APPLIED

We include in the CSVs only spells that are:
  - Not PASSIVE                   (passive auras never come through CMSG_CAST_SPELL)
  - Not HIDDEN_CLIENTSIDE         (internal / server-only spells)
  - Have at least one nonzero Effect slot
  - Have a non-empty spell name (enUS)

This yields ~14,400 off-GCD and ~150 1s-GCD spells. The large off-GCD count is mostly
NPC / proc / trigger spells that will never reach our CMSG handler anyway -- including
them in the whitelist is harmless (lookups only happen on real CMSG_CAST_SPELL events),
just noisier. Filter further if you want a tighter player-facing list.
"""
import argparse
import os
import struct
import sys

IDX_ID = 0
IDX_ATTRIBUTES = 6
IDX_ATTRIBUTES_EX = 7
IDX_EFFECT = [61, 62, 63]
IDX_NAME_ENUS = 120
IDX_START_RECOVERY_CATEGORY = 157
IDX_START_RECOVERY_TIME = 158

ATTR_PASSIVE = 0x00000040
ATTR_HIDDEN_CLIENTSIDE = 0x00000080
ATTR_EX_CHANNELED_1 = 0x00000004
ATTR_EX_CHANNELED_2 = 0x00000040

HEADER_SIZE = 20


def load_spell_dbc(client_root: str | None, dbc_path: str | None) -> bytes:
    """Return the raw Spell.dbc bytes, preferring patch-2.MPQ > patch.MPQ > dbc.MPQ."""
    if dbc_path:
        with open(dbc_path, 'rb') as f:
            return f.read()

    if not client_root:
        sys.exit("--client or --dbc is required")

    try:
        import mpyq
    except ImportError:
        sys.exit("mpyq is required: pip install mpyq")

    candidates = ['patch-2.MPQ', 'patch.MPQ', 'dbc.MPQ']
    for mpq_name in candidates:
        mpq_path = os.path.join(client_root, 'Data', mpq_name)
        if not os.path.exists(mpq_path):
            continue
        archive = mpyq.MPQArchive(mpq_path)
        data = archive.read_file('DBFilesClient\\Spell.dbc')
        if data:
            print(f"[dbc] loaded Spell.dbc from {mpq_name} ({len(data):,} bytes)", file=sys.stderr)
            return data
    sys.exit(f"Spell.dbc not found in any MPQ under {client_root}/Data")


def parse(blob: bytes):
    magic, record_count, field_count, record_size, string_size = \
        struct.unpack_from('<4sIIII', blob, 0)
    if magic != b'WDBC':
        sys.exit(f"Not a WDBC file (got {magic!r})")
    if field_count != 173:
        sys.exit(f"Expected 173 fields for 1.12 Spell.dbc, got {field_count}. Wrong client version?")

    string_offset = HEADER_SIZE + record_count * record_size
    string_block = blob[string_offset:string_offset + string_size]

    def read_string(off: int) -> str:
        if off == 0:
            return ''
        end = string_block.find(b'\0', off)
        return string_block[off:end].decode('utf-8', errors='replace') if end > off else ''

    off_gcd: list[int] = []
    one_s_gcd: list[int] = []
    channeled: list[int] = []
    filtered = {'passive': 0, 'hidden': 0, 'no_effect': 0, 'no_name': 0, 'zero_id': 0}

    for i in range(record_count):
        base = HEADER_SIZE + i * record_size
        sid      = struct.unpack_from('<I', blob, base + IDX_ID * 4)[0]
        attrs    = struct.unpack_from('<I', blob, base + IDX_ATTRIBUTES * 4)[0]
        attrs_ex = struct.unpack_from('<I', blob, base + IDX_ATTRIBUTES_EX * 4)[0]
        effects  = [struct.unpack_from('<I', blob, base + ix * 4)[0] for ix in IDX_EFFECT]
        name_ofs = struct.unpack_from('<I', blob, base + IDX_NAME_ENUS * 4)[0]
        rec_cat  = struct.unpack_from('<i', blob, base + IDX_START_RECOVERY_CATEGORY * 4)[0]
        rec_time = struct.unpack_from('<i', blob, base + IDX_START_RECOVERY_TIME * 4)[0]

        if sid == 0: filtered['zero_id'] += 1; continue
        if attrs & ATTR_PASSIVE: filtered['passive'] += 1; continue
        if attrs & ATTR_HIDDEN_CLIENTSIDE: filtered['hidden'] += 1; continue
        if all(e == 0 for e in effects): filtered['no_effect'] += 1; continue
        if not read_string(name_ofs): filtered['no_name'] += 1; continue

        if rec_cat == 0 or rec_time == 0:
            off_gcd.append(sid)
        if rec_time == 1000:
            one_s_gcd.append(sid)
        if attrs_ex & (ATTR_EX_CHANNELED_1 | ATTR_EX_CHANNELED_2):
            channeled.append(sid)

    return off_gcd, one_s_gcd, channeled, filtered, record_count


def write_csv(path: str, ids: list[int]) -> None:
    with open(path, 'w', newline='\n', encoding='utf-8') as f:
        f.write('SpellId\n')
        for sid in sorted(ids):
            f.write(f'{sid}\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--client', help='Path to a vanilla 1.12 WoW install (folder containing Data/)')
    parser.add_argument('--dbc', help='Path to a pre-extracted Spell.dbc file (alternative to --client)')
    parser.add_argument('--out-dir', default=os.path.join('HermesProxy', 'CSV'),
                        help='Output directory (default: HermesProxy/CSV relative to cwd)')
    args = parser.parse_args()

    blob = load_spell_dbc(args.client, args.dbc)
    off_gcd, one_s_gcd, channeled, filtered, total = parse(blob)

    print(f"Total records:          {total:,}", file=sys.stderr)
    for k, v in filtered.items():
        print(f"  filtered {k:<10} {v:>6,}", file=sys.stderr)
    print(f"Off-GCD (castable):     {len(off_gcd):,}", file=sys.stderr)
    print(f"1s-GCD (castable):      {len(one_s_gcd):,}", file=sys.stderr)
    print(f"Channeled (castable):   {len(channeled):,}", file=sys.stderr)

    os.makedirs(args.out_dir, exist_ok=True)
    off_gcd_path = os.path.join(args.out_dir, 'SpellOffGcd1.csv')
    one_s_gcd_path = os.path.join(args.out_dir, 'Spell1sGcd1.csv')
    channeled_path = os.path.join(args.out_dir, 'SpellChanneled1.csv')
    write_csv(off_gcd_path, off_gcd)
    write_csv(one_s_gcd_path, one_s_gcd)
    write_csv(channeled_path, channeled)
    print(f"Wrote {off_gcd_path}", file=sys.stderr)
    print(f"Wrote {one_s_gcd_path}", file=sys.stderr)
    print(f"Wrote {channeled_path}", file=sys.stderr)


if __name__ == '__main__':
    main()
