"""Scans ChaosUkrainian/Data/Localization for duplicate translation keys.

A "duplicate" is any Key that appears more than once across the whole
Data/Localization tree (whether in the same file twice or in two different
files). For each duplicate, prints every file that contains it along with
the Ukrainian value in that file, so the values can be compared.
"""
import csv
import os
import sys
from collections import defaultdict

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(SCRIPT_DIR, "Data", "Localization")
PATH_BASE = os.path.dirname(SCRIPT_DIR)


def iter_csv_files(root):
    for dirpath, _, filenames in os.walk(root):
        for fn in filenames:
            if fn.lower().endswith(".csv"):
                yield os.path.join(dirpath, fn)


def main():
    if not os.path.isdir(ROOT):
        print(f"Directory not found: {ROOT}")
        return 1

    occurrences = defaultdict(list)  # key -> list of (relative_path, value)

    for full_path in iter_csv_files(ROOT):
        rel_path = os.path.relpath(full_path, PATH_BASE)
        with open(full_path, encoding="utf-8-sig", newline="") as f:
            reader = csv.DictReader(f)
            value_col = None
            for row in reader:
                if value_col is None:
                    value_col = next((c for c in row.keys() if c and c != "Key"), None)
                key = (row.get("Key") or "").strip()
                if not key:
                    continue
                value = row.get(value_col, "") if value_col else ""
                occurrences[key].append((rel_path, value))

    duplicates = {k: v for k, v in occurrences.items() if len(v) > 1}

    if not duplicates:
        print("No duplicate keys found.")
        return 0

    for key in sorted(duplicates):
        print(f"Duplicate key: {key}")
        for rel_path, value in duplicates[key]:
            print(f"  {rel_path}")
            print(f"    Value: {value}")
        print()

    print(f"Total duplicate keys: {len(duplicates)}")
    return 1


if __name__ == "__main__":
    sys.exit(main())