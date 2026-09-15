#!/usr/bin/env python3
"""Insert or replace a plugin version entry in a Jellyfin repository manifest.json."""
import json
import re
import sys
from pathlib import Path

import yaml


def main() -> None:
    build_yaml_path = Path(sys.argv[1])
    manifest_path = Path(sys.argv[2])
    zip_path = Path(sys.argv[3])
    source_url = sys.argv[4]
    checksum = sys.argv[5]
    timestamp = sys.argv[6]

    build = yaml.safe_load(build_yaml_path.read_text())

    # build.yaml's changelog contains every past release under "## x.y.z.w"
    # headings. It's written as a YAML folded (">") scalar, which collapses
    # each heading onto the same line as its body text, so split on the
    # heading pattern directly rather than by line.
    changelog_text = str(build.get("changelog", "")).strip()
    version = str(build["version"])
    sections = {}
    matches = list(re.finditer(r"##\s+(\S+)", changelog_text))
    for i, m in enumerate(matches):
        start = m.end()
        end = matches[i + 1].start() if i + 1 < len(matches) else len(changelog_text)
        sections[m.group(1)] = changelog_text[start:end].strip()
    changelog = sections.get(version, changelog_text)

    if manifest_path.exists():
        manifest = json.loads(manifest_path.read_text())
    else:
        manifest = []

    entry = None
    for plugin in manifest:
        if plugin.get("guid") == build["guid"]:
            entry = plugin
            break

    if entry is None:
        entry = {
            "guid": build["guid"],
            "name": build["name"],
            "description": build.get("description", "").strip(),
            "overview": build.get("overview", ""),
            "owner": build.get("owner", ""),
            "category": build.get("category", "General"),
            "versions": [],
        }
        manifest.append(entry)
    else:
        entry["description"] = build.get("description", "").strip()
        entry["overview"] = build.get("overview", "")
        entry["owner"] = build.get("owner", "")
        entry["category"] = build.get("category", "General")

    version_entry = {
        "version": version,
        "changelog": changelog,
        "targetAbi": build["targetAbi"],
        "sourceUrl": source_url,
        "checksum": checksum,
        "timestamp": timestamp,
    }

    entry["versions"] = [v for v in entry["versions"] if v.get("version") != version]
    entry["versions"].append(version_entry)
    entry["versions"].sort(key=lambda v: [int(p) for p in v["version"].split(".")], reverse=True)

    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")


if __name__ == "__main__":
    main()
