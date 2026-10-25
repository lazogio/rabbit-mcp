#!/usr/bin/env python3
# Copyright (c) 2026 lazogio
# SPDX-License-Identifier: MIT
"""
Writes a release version into the MCP registry manifest (src/RabbitMcp.Server/.mcp/server.json) and checks the rules
the registry enforces on publish, so a branch run fails instead of the tag:
  - description at most 100 characters;
  - nuget packages carry the version in "version";
  - OCI packages carry it only in the identifier tag (ghcr.io/owner/image:1.2.3) and have no "version" field.

Usage: scripts/stamp-server-json.py 1.2.3 [path/to/server.json]
"""
import json
import sys

version = sys.argv[1]
path = sys.argv[2] if len(sys.argv) > 2 else "src/RabbitMcp.Server/.mcp/server.json"

with open(path) as f:
    manifest = json.load(f)

description = manifest["description"]
if len(description) > 100:
    sys.exit(f"server.json description is {len(description)} > 100 characters")

manifest["version"] = version
for package in manifest["packages"]:
    if package["registryType"] == "oci":
        package.pop("version", None)
        image = package["identifier"]
        if ":" in image.rsplit("/", 1)[-1]:
            image = image.rsplit(":", 1)[0]
        package["identifier"] = f"{image}:{version}"
    else:
        package["version"] = version

with open(path, "w") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
