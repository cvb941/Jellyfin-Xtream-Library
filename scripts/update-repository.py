"""Publish a checked release asset in this fork's Jellyfin beta catalog."""

from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sys

version, asset = sys.argv[1:]
if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
    raise SystemExit("Expected a four-part numeric version")

path = Path("repository.json")
catalog = json.loads(path.read_text()) if path.exists() else [{
    "guid": "63ba5fcd-c8ce-421a-83e8-ba0b11030d53",
    "name": "Xtream Library",
    "description": "Xtream Library with DASH live and archive support. cvb941 beta fork for Jellyfin 12.",
    "overview": "Xtream Live TV, catch-up and native VOD libraries",
    "owner": "cvb941",
    "category": "LiveTV",
    "versions": [],
}]
plugin = next(p for p in catalog if p["guid"] == "63ba5fcd-c8ce-421a-83e8-ba0b11030d53")
entry = {
    "version": version,
    "changelog": "Beta: DASH live/catch-up, archive codec probing and highest-quality video; fix media-source IDs and empty sources before PlaybackInfo.",
    "targetAbi": "12.0.0.0",
    "sourceUrl": f"https://github.com/cvb941/Jellyfin-Xtream-Library/releases/download/v{version}/jellyfin-xtream-library_{version}.zip",
    "checksum": hashlib.md5(Path(asset).read_bytes(), usedforsecurity=False).hexdigest(),
    "timestamp": datetime.now(timezone.utc).isoformat(),
}
plugin["versions"] = [entry] + [v for v in plugin["versions"] if v["version"] != version]
path.write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")
Path("manifest-dev.json").write_text(path.read_text(encoding="utf-8"), encoding="utf-8")
