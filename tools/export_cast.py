"""Export Bully's adults for the Schedule I cast swap (Cast.cs): every adult ped model in CAST below -> assets/out/peds/
(<out>.bytes + <out>_*.png via export_jimmy.main). They load from disk at runtime (UserData/BullyLink/peds), not embedded:
~0.7 MB each would put ~50 MB in the DLL.

The adult roster (model, out name, gender, Bully voice, role). Voices: teachers / staff by their own class lines in
Speech.lst (MATH_ = Hattrick, CHEM_ = Watts, GEO_ = Matthews, BIO_ = Slawter, SHOP_ = Neil, ENG_ = Galloway, MUS_ =
Peters, ART_ = Phillips, PA announcements = Danvers); cops = the BUSTING + WARN voices that aren't staff; shopkeepers =
the STOR* voices; carnies = the CARN* voices; townies = the CONV* voices. Townie model <-> voice pairs are Tweak's picks
(Bully keeps that table in compiled code), so any of them can be swapped in UserData/BullyLink.cast.txt.

Run: python export_cast.py            (all)   |   python export_cast.py TO_Cop TE_Nurse   (some)
"""
import sys
from pathlib import Path
import export_jimmy

# model, gender, voice, role
CAST = [
    # staff (Bullworth Academy)
    ("TE_Principal", "m", "Crabble", "boss"),
    ("TE_English", "m", "Galloway", "town"),
    ("TE_MathTeacher", "m", "Hattrick", "town"),
    ("TE_GymTeacher", "m", "Burton", "town"),
    ("TE_Chemistry", "m", "Watts", "chem"),
    ("TE_Biology", "m", "Slawter", "chem"),
    ("TE_Geography", "m", "Matthews", "town"),
    ("TE_History", "m", "Wiggins", "town"),
    ("TE_Autoshop", "m", "Neil", "mechanic"),
    ("TE_Janitor", "m", "Luntz", "worker"),
    ("TE_HallMonitor", "m", "Peabody", "town"),
    ("TE_Nurse", "f", "McRae", "doctor"),
    ("TE_Secretary", "f", "Danvers", "town"),
    ("TE_Librarian", "f", "Carvin", "town"),
    ("TE_Cafeteria", "f", "Edna", "cook"),
    ("TE_Art", "f", "Phillips", "town"),
    ("TE_Music", "f", "MsPeters", "town"),
    # police
    ("TO_Cop", "m", "Morrison", "cop"),
    ("TO_Cop2", "m", "Monson", "cop"),
    ("TO_Cop3", "m", "Williams", "cop"),
    ("TO_Cop4", "m", "Ivanovich", "cop"),
    # shopkeepers
    ("TO_GroceryOwner", "m", "Moratti", "shop"),
    ("TO_GroceryClerk", "m", "Denny", "shop"),
    ("TO_CSOwner_2", "m", "Oh", "shop"),
    ("TO_CSOwner_3", "m", "Stan", "shop"),
    ("TO_BikeOwner", "m", "Floyd", "shop"),
    ("TO_Comic", "m", "Tobias", "shop"),
    ("TO_BarberRich", "m", "Carmich", "shop"),
    ("TO_MotelOwner", "m", "Sullivan", "shop"),
    ("TO_FireOwner", "m", "Isaacs", "shop"),
    ("TO_Record", "m", "Martin", "shop"),
    ("TO_Tattooist", "m", "Ramirez", "shop"),
    ("TO_PunkBarber", "m", "Castillo", "shop"),
    ("TO_BarberPoor", "m", "Bambillo", "shop"),   # face texture: a balding heavyset man
    # carnival
    ("TO_Carny01", "m", "Hector", "rough"),
    ("TO_Carny02", "m", "Freeley", "rough"),
    ("TO_Carnie_female", "f", "Crystal", "rough"),
    # townies
    ("TO_Business1", "m", "Doolin", "suit"),
    ("TO_Business2", "m", "Huntingd", "suit"),
    ("TO_Business3", "m", "Buckingh", "suit"),
    ("TO_Business4", "m", "Lisburn", "suit"),
    ("TO_Business5", "m", "ORourke", "suit"),
    ("TO_RichM1", "m", "Osbourne", "suit"),
    ("TO_RichM2", "m", "MrGordon", "suit"),
    ("TO_RichM3", "m", "McInnis", "suit"),
    ("TO_Associate", "m", "Svenson", "suit"),
    ("TO_Poorman2", "m", "Bob", "poor"),
    ("TO_Oldman2", "m", "Fenwick", "old"),
    ("TO_Hobo", "m", "Hobo", "poor"),
    ("TO_Handy", "m", "Handy", "worker"),
    ("TO_Construct01", "m", "Johnson", "worker"),
    ("TO_Construct02", "m", "Kopke", "worker"),
    ("TO_Dockworker", "m", "Krakauer", "worker"),
    ("TO_Industrial", "m", "Chuck", "worker"),
    ("TO_Millworker", "m", "Milhailo", "worker"),
    ("TO_Mailman", "m", "Smith", "worker"),
    ("TO_Fireman", "m", "Rushinski", "worker"),
    ("TO_GN_Workman", "m", "Bubas", "worker"),
    ("TO_Orderly", "m", "Theo", "doctor"),        # "THEO" name tag on his uniform texture
    ("TO_Orderly2", "m", "Gregory", "worker"),
    ("TO_Asylumpatient", "m", "Salvatore", "poor"),
    ("TO_BusinessW1", "f", "Abby", "suit"),
    ("TO_BusinessW2", "f", "Maria", "suit"),
    ("TO_RichW1", "f", "Betty", "suit"),
    ("TO_RichW2", "f", "Phillips", "suit"),
    ("TO_Poorwoman", "f", "Brandy", "poor"),
]


def out_name(model):
    return "ped_" + model.lower()


def run(want=()):
    import paths
    peds = paths.PEDS
    peds.mkdir(parents=True, exist_ok=True)
    export_jimmy.OUT = peds
    ok, bad = 0, []
    for model, g, voice, role in CAST:
        if want and model.lower() not in want: continue
        try:
            export_jimmy.main(model, out_name(model)); ok += 1
        except Exception as e:
            bad.append(f"{model}: {e}")
    lines = [f"{m}\t{out_name(m)}\t{g}\t{v}\t{r}" for m, g, v, r in CAST if (peds / (out_name(m) + ".bytes")).exists()]
    (peds / "roster.txt").write_text("\n".join(lines) + "\n")
    print(f"exported {ok}; roster {len(lines)}; failed {len(bad)}")
    for b in bad: print("  FAILED", b)
    export_jimmy.OUT = None
    return ok, bad


if __name__ == "__main__":
    run(set(a.lower() for a in sys.argv[1:]))
