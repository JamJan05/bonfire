#!/usr/bin/env python3
"""Writes THIRD-PARTY-NOTICES.txt for a published Bonfire build.

Lists every NuGet package the app restores for linux-x64 (direct and transitive, from the
committed lock file) plus the .NET runtime pack the build actually bundled (from the published
deps.json), each with its license text: the files the package ships, or — for packages that only
declare an SPDX expression — the standard text with the copyright from the package metadata.
Generation fails when a license text cannot be provided. Usage:
    tools/gen-notices.py <output-file> <published bonfire.deps.json> [<nuget-packages-dir>]
"""
import json
import os
import sys
import xml.etree.ElementTree as ET

out = sys.argv[1]
deps = json.load(open(sys.argv[2], encoding="utf-8"))
root = sys.argv[3] if len(sys.argv) > 3 else os.path.expanduser("~/.nuget/packages")
here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

lock = json.load(open(os.path.join(here, "src/Bonfire.App/packages.lock.json"), encoding="utf-8"))
packages = {}
for framework in ("net10.0", "net10.0/linux-x64"):
    for name, info in lock["dependencies"].get(framework, {}).items():
        if info.get("type") == "Project":
            continue
        packages[name] = info["resolved"]

runtime_packs = [k.split("/", 1) for k in deps["libraries"] if k.startswith("runtimepack.Microsoft.NETCore.App.Runtime.")]
if len(runtime_packs) != 1:
    sys.exit("the published deps.json does not name exactly one .NET runtime pack")
name, version = runtime_packs[0]
packages[name.removeprefix("runtimepack.")] = version  # exactly the runtime that was bundled

templates = {
    "MIT": open(os.path.join(here, "packaging/licenses/MIT.txt"), encoding="utf-8").read(),
    "Apache-2.0": open(os.path.join(here, "packaging/licenses/Apache-2.0.txt"), encoding="utf-8").read(),
}

NOTICE_NAMES = ("license", "licence", "notice", "third-party-notices", "thirdpartynotices")

def files_of(pkgdir):
    for entry in sorted(os.listdir(pkgdir)):
        base = entry.lower()
        if any(base.startswith(n) for n in NOTICE_NAMES) and os.path.isfile(os.path.join(pkgdir, entry)):
            yield entry

missing = []
with open(out, "w", encoding="utf-8") as f:
    f.write("Bonfire — third-party software notices\n")
    f.write("=======================================\n\n")
    f.write("Bonfire is licensed under GPL-3.0-or-later (see LICENSE). The binary distribution\n")
    f.write("bundles the following third-party components under their own licenses.\n\n")
    for name in sorted(packages, key=str.lower):
        version = packages[name]
        pkgdir = os.path.join(root, name.lower(), version)
        nuspec = os.path.join(pkgdir, f"{name.lower()}.nuspec")
        license_text, url, project, copyright_line, expression = "unknown", "", "", "", None
        if os.path.isfile(nuspec):
            meta = ET.parse(nuspec).getroot().find("{*}metadata")
            lic = meta.find("{*}license")
            if lic is not None:
                license_text = lic.text if lic.get("type") == "expression" else f"see {lic.text}"
                expression = lic.text.strip() if lic.get("type") == "expression" else None
            elif meta.find("{*}licenseUrl") is not None:
                license_text = "see license URL"
            url = (meta.findtext("{*}licenseUrl") or "").strip()
            project = (meta.findtext("{*}projectUrl") or "").strip()
            copyright_line = (meta.findtext("{*}copyright") or "").strip()  # never invented
        else:
            missing.append(f"{name} {version}")
        f.write("-" * 72 + "\n")
        f.write(f"{name} {version}\nLicense: {license_text}\n")
        if project:
            f.write(f"Project: {project}\n")
        if url and "aka.ms/deprecateLicenseUrl" not in url:
            f.write(f"License URL: {url}\n")
        shipped = list(files_of(pkgdir)) if os.path.isdir(pkgdir) else []
        for entry in shipped:
            f.write(f"\n--- {entry} ---\n")
            f.write(open(os.path.join(pkgdir, entry), encoding="utf-8", errors="replace").read().strip() + "\n")
        if not shipped:
            if expression not in templates:
                missing.append(f"{name} {version} (no license text for {license_text})")
            elif expression == "MIT" and not copyright_line:
                missing.append(f"{name} {version} (MIT without a declared copyright notice)")
            else:
                # Apache-2.0 needs the license (and a NOTICE file, which these packages would ship);
                # a copyright line is added only when the package declares one.
                f.write(f"\n--- {expression} license text ---\n")
                f.write(templates[expression].replace("{copyright}", copyright_line).strip() + "\n")
                if expression == "Apache-2.0" and copyright_line:
                    f.write(f"\n{copyright_line}\n")
        f.write("\n")

    # The Inter font is embedded in Avalonia.Fonts.Inter; the font itself is under the SIL OFL 1.1.
    f.write("-" * 72 + "\n")
    f.write("Inter typeface (embedded in Avalonia.Fonts.Inter)\nLicense: OFL-1.1\nProject: https://rsms.me/inter/\n\n")
    f.write(open(os.path.join(here, "packaging/licenses/OFL-1.1.txt"), encoding="utf-8").read().strip() + "\n")

if missing:
    sys.exit("cannot provide license texts for: " + ", ".join(missing))
print(f"{len(packages)} components")
