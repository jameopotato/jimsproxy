"""Generate the HermesProxy/CSV/UnitScale*<exp>.csv tables from wago.tools DB2 exports.

The proxy divides a 1.12 server's OBJECT_FIELD_SCALE_X by the display factor the modern
client applies to a unit (see HermesProxy/World/UnitScale.cs). These four tables are the
client's own data for that factor, cut down to the columns it uses.

Usage:
    python scripts/gen-unit-scale-csv.py <wago dir> HermesProxy/CSV 1

<wago dir> holds these exports for the client build (1.14.2.42597 for expansion 1):
    https://wago.tools/db2/CreatureDisplayInfo/csv?build=1.14.2.42597      -> CreatureDisplayInfo.csv
    https://wago.tools/db2/CreatureDisplayInfoExtra/csv?build=1.14.2.42597 -> CreatureDisplayInfoExtra.csv
    https://wago.tools/db2/CreatureModelData/csv?build=1.14.2.42597        -> CreatureModelData.csv
    https://wago.tools/db2/ChrRaceXChrModel/csv?build=1.14.2.42597         -> ChrRaceXChrModel.csv
    https://wago.tools/db2/ChrModel/csv?build=1.14.2.42597                 -> ChrModel.csv

Output (the last argument is the modern expansion suffix):
    UnitScaleDisplays<exp>.csv     DisplayID,ModelID,CreatureModelScale,ExtendedDisplayInfoID,PlayerOverrideScale
    UnitScaleDisplayRaces<exp>.csv ExtendedDisplayInfoID,DisplayRaceID,DisplaySexID
    UnitScaleRaceModels<exp>.csv   ChrRacesID,Sex,DisplayID
    UnitScaleModels<exp>.csv       ModelID,ModelScale,TamedPetBaseScale
CreatureFamily scales come from the existing HermesProxy/CSV/CreatureFamily.csv.
"""
import csv
import os
import sys


def num(text: str) -> str:
    # wago prints float32 values in full (1.14999997616); 7 significant digits round-trips a float32.
    value = float(text)
    return str(int(value)) if value.is_integer() else format(value, ".7g")


def rows(directory: str, name: str):
    with open(os.path.join(directory, name), encoding="utf-8") as f:
        return list(csv.DictReader(f))


def write(path: str, header: str, lines) -> int:
    with open(path, "w", newline="", encoding="ascii") as f:
        f.write(header + "\n")
        count = 0
        for line in lines:
            f.write(",".join(line) + "\n")
            count += 1
    return count


def main(src: str, dst: str, exp: str) -> None:
    displays = sorted(rows(src, "CreatureDisplayInfo.csv"), key=lambda r: int(r["ID"]))
    extras = sorted(rows(src, "CreatureDisplayInfoExtra.csv"), key=lambda r: int(r["ID"]))
    models = sorted(rows(src, "CreatureModelData.csv"), key=lambda r: int(r["ID"]))
    chr_models = {r["ID"]: r for r in rows(src, "ChrModel.csv")}
    race_models = rows(src, "ChrRaceXChrModel.csv")

    n_disp = write(os.path.join(dst, f"UnitScaleDisplays{exp}.csv"),
                   "DisplayID,ModelID,CreatureModelScale,ExtendedDisplayInfoID,PlayerOverrideScale",
                   ((r["ID"], r["ModelID"], num(r["CreatureModelScale"]), r["ExtendedDisplayInfoID"],
                     num(r["PlayerOverrideScale"])) for r in displays))
    n_extra = write(os.path.join(dst, f"UnitScaleDisplayRaces{exp}.csv"),
                    "ExtendedDisplayInfoID,DisplayRaceID,DisplaySexID",
                    ((r["ID"], r["DisplayRaceID"], r["DisplaySexID"]) for r in extras))
    race_rows = sorted(((r["ChrRacesID"], chr_models[r["ChrModelID"]]["Sex"], chr_models[r["ChrModelID"]]["DisplayID"])
                        for r in race_models if r["ChrModelID"] in chr_models),
                       key=lambda t: (int(t[0]), int(t[1])))
    n_race = write(os.path.join(dst, f"UnitScaleRaceModels{exp}.csv"), "ChrRacesID,Sex,DisplayID", race_rows)
    n_model = write(os.path.join(dst, f"UnitScaleModels{exp}.csv"), "ModelID,ModelScale,TamedPetBaseScale",
                    ((r["ID"], num(r["ModelScale"]), num(r["TamedPetBaseScale"])) for r in models))
    print(f"displays={n_disp} extras={n_extra} race models={n_race} models={n_model} -> {dst}")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        print(__doc__)
        sys.exit(2)
    main(sys.argv[1], sys.argv[2], sys.argv[3])
