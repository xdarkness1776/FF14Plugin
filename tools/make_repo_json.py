#!/usr/bin/env python3
"""Builds or validates the Dalamud custom repository file (repo.json).

  make_repo_json.py build <out repo.json> <download base url> <zip>[=<asset name>] ...
      One store entry per zip, from the plugin manifest inside the zip. The download link of each entry
      is <download base url>/<asset name>. Existing entries for other plugins in <out> are kept.
  make_repo_json.py validate <repo.json> [<zip> ...]
      Checks the structure and, when zips are given, that versions match the packages.

Store entry keys follow https://dalamud.dev/plugin-publishing/custom-repositories/.
"""
import json
import os
import sys
import time
import zipfile

ENTRY_KEYS = ["Author", "Name", "InternalName", "AssemblyVersion", "Description", "Punchline",
              "ApplicableVersion", "DalamudApiLevel", "RepoUrl", "Tags", "IconUrl", "AcceptsFeedback"]
REQUIRED = ["Author", "Name", "InternalName", "AssemblyVersion", "Description", "Punchline", "ApplicableVersion",
            "DalamudApiLevel", "RepoUrl", "DownloadLinkInstall", "DownloadLinkUpdate", "LastUpdate"]


def manifest_from_zip(path):
    with zipfile.ZipFile(path) as z:
        candidates = [n for n in z.namelist() if n.endswith(".json") and "/" not in n and not n.endswith(".deps.json")]
        for name in candidates:
            data = json.loads(z.read(name).decode("utf-8-sig"))
            if "InternalName" in data and "AssemblyVersion" in data:
                return data
    raise SystemExit(f"error: no plugin manifest in {path}")


def build(out, base_url, specs):
    entries = []
    if os.path.exists(out):
        with open(out, encoding="utf-8") as f:
            entries = json.load(f)
    for spec in specs:
        zip_path, _, asset = spec.partition("=")
        asset = asset or os.path.basename(zip_path)
        m = manifest_from_zip(zip_path)
        url = base_url.rstrip("/") + "/" + asset
        entry = {k: m[k] for k in ENTRY_KEYS if k in m}
        entry.update({
            "TestingAssemblyVersion": None,
            "IsHide": False,
            "IsTestingExclusive": False,
            "DownloadLinkInstall": url,
            "DownloadLinkUpdate": url,
            "LastUpdate": str(int(time.time())),
        })
        entries = [e for e in entries if e.get("InternalName") != entry["InternalName"]] + [entry]
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(entries, f, indent=2)
        f.write("\n")
    print(f"wrote {out} with {len(entries)} entr{'y' if len(entries) == 1 else 'ies'}")


def validate(path, zips):
    with open(path, encoding="utf-8") as f:
        entries = json.load(f)
    errors = []
    if not isinstance(entries, list):
        errors.append("repo.json must be a JSON array")
        entries = []
    names = [e.get("InternalName") for e in entries]
    if len(names) != len(set(names)):
        errors.append("duplicate InternalName entries")
    for e in entries:
        for k in REQUIRED:
            if e.get(k) in (None, ""):
                errors.append(f"{e.get('InternalName')}: missing {k}")
        for k in ("DownloadLinkInstall", "DownloadLinkUpdate"):
            if not str(e.get(k, "")).startswith("https://"):
                errors.append(f"{e.get('InternalName')}: {k} must be an https URL")
    for z in zips:
        m = manifest_from_zip(z)
        match = [e for e in entries if e.get("InternalName") == m["InternalName"]]
        if not match:
            errors.append(f"{m['InternalName']} is not in repo.json")
        elif match[0].get("AssemblyVersion") != m["AssemblyVersion"] or match[0].get("DalamudApiLevel") != m["DalamudApiLevel"]:
            errors.append(f"{m['InternalName']}: repo.json version/API level do not match the package")
    for err in errors:
        print(f"error: {err}", file=sys.stderr)
    if not errors:
        print(f"ok: {path} ({len(entries)} entries)")
    return 1 if errors else 0


if __name__ == "__main__":
    if len(sys.argv) >= 5 and sys.argv[1] == "build":
        build(sys.argv[2], sys.argv[3], sys.argv[4:])
    elif len(sys.argv) >= 3 and sys.argv[1] == "validate":
        sys.exit(validate(sys.argv[2], sys.argv[3:]))
    else:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
