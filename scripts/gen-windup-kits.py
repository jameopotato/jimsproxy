"""Generate HermesProxy/CSV/SpellVisualWindupKits<exp>.csv from a wago.tools SpellVisualEvent CSV.

Usage:
    python scripts/gen-windup-kits.py SpellVisualEvent.csv HermesProxy/CSV/SpellVisualWindupKits1.csv

Input:  https://wago.tools/db2/SpellVisualEvent/csv?build=1.14.2.42597
        (columns ID,StartEvent,EndEvent,StartMinOffsetMs,StartMaxOffsetMs,EndMinOffsetMs,EndMaxOffsetMs,
         TargetType,SpellVisualKitID,SpellVisualID)

Rule:   keep rows with StartEvent=1, EndEvent=2, TargetType=1 (the caster-side kit that carries the held
        wind-up sound; runtime 2026-09-07 showed the sound-owning effect reports this kit, e.g. 99 for the
        holy heals, never the 3->13 precast kit 270, which only appears on the predicted-press copies),
        then drop any kit that also appears under a different (StartEvent, EndEvent, TargetType), so that a
        cancel by kit id (SMSG_CANCEL_SPELL_VISUAL_KIT) can never hit a non-wind-up effect.
Output: SpellVisualID,SpellVisualKitID, sorted, one row per (visual, kit).
"""
import csv
import sys


def main(src: str, dst: str) -> None:
    rows = list(csv.DictReader(open(src, encoding="utf-8")))
    windup = {(int(r["SpellVisualID"]), int(r["SpellVisualKitID"]))
              for r in rows if r["StartEvent"] == "1" and r["EndEvent"] == "2" and r["TargetType"] == "1"}
    windup_kits = {k for _, k in windup}
    dual_use = {int(r["SpellVisualKitID"]) for r in rows
                if int(r["SpellVisualKitID"]) in windup_kits
                and not (r["StartEvent"] == "1" and r["EndEvent"] == "2" and r["TargetType"] == "1")}
    kept = sorted((v, k) for v, k in windup if k not in dual_use)
    with open(dst, "w", newline="", encoding="ascii") as f:
        f.write("SpellVisualID,SpellVisualKitID\n")
        for v, k in kept:
            f.write(f"{v},{k}\n")
    print(f"wind-up rows={len(windup)} kits={len(windup_kits)} dual-use dropped={len(dual_use)} written={len(kept)} -> {dst}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    main(sys.argv[1], sys.argv[2])
