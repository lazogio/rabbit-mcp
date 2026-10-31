#!/usr/bin/env python3
# Copyright (c) 2026 lazogio
# SPDX-License-Identifier: MIT
"""
The check strict MCP clients make on every tool result: structuredContent must satisfy the tool's outputSchema.
The official TypeScript SDK throws otherwise, after the tool has already sent or moved its messages.
Covers what a result can get wrong (types, required, properties, items, $ref, anyOf/oneOf) without third-party packages.
"""

TYPES = {"object": dict, "array": list, "string": str, "boolean": bool, "null": type(None)}


def _is(value, name):
    if name in ("number", "integer"):
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    return isinstance(value, TYPES.get(name, object))


def problems(schema, value, path="$", root=None):
    """Readable mismatches between a value and its JSON Schema; empty when it fits."""
    if not isinstance(schema, dict):
        return []
    root = root or schema
    if "$ref" in schema:
        target = root
        for part in schema["$ref"].lstrip("#/").split("/"):
            target = target[part]
        return problems(target, value, path, root)

    branches = schema.get("anyOf") or schema.get("oneOf")
    if branches and all(problems(branch, value, path, root) for branch in branches):
        return [f"{path}: fits none of its alternatives"]

    types = schema.get("type")
    if types is not None and not any(_is(value, t) for t in (types if isinstance(types, list) else [types])):
        return [f"{path}: is {type(value).__name__}, the schema says {types}"]

    found = []
    if isinstance(value, dict):
        found += [f"{path}.{name}: required by the schema but missing" for name in schema.get("required", []) if name not in value]
        for name, sub in schema.get("properties", {}).items():
            if name in value:
                found += problems(sub, value[name], f"{path}.{name}", root)
    elif isinstance(value, list):
        for i, item in enumerate(value):
            found += problems(schema.get("items"), item, f"{path}[{i}]", root)
    return found
