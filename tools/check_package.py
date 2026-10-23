#!/usr/bin/env python3
"""Validates a DalamudPackager zip: required files, plugin manifest fields, no Dalamud-provided assemblies.

usage: check_package.py <latest.zip> <InternalName> <expected AssemblyVersion>
"""
import json
import sys
import zipfile

# Assemblies Dalamud already loads; bundling them causes load conflicts.
DALAMUD_PROVIDED = {
    "dalamud.dll", "dalamud.bindings.imgui.dll", "ffxivclientstructs.dll", "lumina.dll", "lumina.excel.dll",
    "newtonsoft.json.dll", "serilog.dll", "imguiscene.dll", "interopgenerator.runtime.dll", "dalamud.common.dll",
}
REQUIRED_FIELDS = ["Author", "Name", "InternalName", "AssemblyVersion", "Description", "Punchline",
                   "ApplicableVersion", "DalamudApiLevel", "RepoUrl"]


def main(path: str, internal: str, version: str) -> int:
    errors = []
    with zipfile.ZipFile(path) as z:
        names = {n.lower(): n for n in z.namelist()}
        for required in (f"{internal}.dll", f"{internal}.json"):
            if required.lower() not in names:
                errors.append(f"missing {required}")
        bundled = sorted(n for n in names if n.rsplit("/", 1)[-1] in DALAMUD_PROVIDED)
        if bundled:
            errors.append(f"bundles Dalamud-provided assemblies: {bundled}")
        manifest = json.loads(z.read(names[f"{internal}.json".lower()]).decode("utf-8-sig")) if f"{internal}.json".lower() in names else {}
    for field in REQUIRED_FIELDS:
        if manifest.get(field) in (None, ""):
            errors.append(f"manifest field {field} is missing or empty")
    if manifest.get("InternalName") != internal:
        errors.append(f"InternalName is {manifest.get('InternalName')!r}, expected {internal!r}")
    if manifest.get("AssemblyVersion") != version:
        errors.append(f"AssemblyVersion is {manifest.get('AssemblyVersion')!r}, expected {version!r}")
    if not isinstance(manifest.get("DalamudApiLevel"), int):
        errors.append("DalamudApiLevel must be an integer")
    for e in errors:
        print(f"error: {e}", file=sys.stderr)
    if not errors:
        print(f"ok: {path} ({internal} {version}, API {manifest['DalamudApiLevel']}, {len(names)} files)")
    return 1 if errors else 0


if __name__ == "__main__":
    if len(sys.argv) != 4:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
    sys.exit(main(*sys.argv[1:]))
