"""region.json: the contract between GAE's exporter and the Unreal side.

Deliberately pure Python with no `unreal` import, so the parsing, grouping and validation can be
tested outside the editor. spawn_region.py is the thin UE-facing half that uses this.

Shape
-----
{
  "format": "gae.region/1",
  "source":     {...},                      provenance: game, edition, install, archives, when
  "convention": {...},                      how coordinates were transformed, and the exact inverse
  "bounds":     {"gta_min": [...], "gta_max": [...]},
  "archetypes": { "<name>": {...} },        one entry per UNIQUE archetype: the asset, its bbox
  "cells":      [ {"name": "vb_01", "instances": [...]} ],
  "unresolved": [ {...} ],                  archetypes that failed to resolve, and why
  "stats":      {...}
}

Positions and rotations in this file are ALREADY IN UNREAL SPACE (centimetres, left-handed, Z up).
The conversion lives in exactly one place, the exporter, so it cannot drift between the geometry
and the placements. `convention.inverse` documents how to get back to GTA world space if anything
downstream needs it.

The important design decision, from how the game itself works and from wanting every asset to stay
swappable: an instance references an archetype by name, it does not embed geometry. One imported
static mesh per unique archetype, many instances. Nothing is merged.
"""

import json
import os
from collections import Counter, defaultdict

FORMAT = "gae.region/1"

# Kept in lockstep with tools/test_transform_convention.py, which proves it.
CONVENTION = {
    "source_space": "GTA V world space: right-handed, Z up, metres",
    "target_space": "Unreal: left-handed, Z up, centimetres",
    "position": "ue = (gta.y, gta.x, gta.z) * 100",
    "rotation": "ue_quat = (stored.y, stored.x, stored.z, stored.w)",
    "rotation_note": (
        "CEntityDef.rotation is stored as the conjugate of the entity's rotation. Inverting it and "
        "then re-expressing the rotation in Unreal's basis produces two sign flips that cancel, "
        "which is why this reads as a bare component swap. Verified in "
        "tools/test_transform_convention.py. CMloInstanceDef is NOT conjugated; its child entities "
        "are, so interiors need their own handling when they are added."
    ),
    "scale": "ue_scale = (scaleXY, scaleXY, scaleZ) -- GTA scale is never uniform",
    "handedness": "flipped exactly once, by the XY swap (determinant -1)",
    "inverse": "gta = (ue.y, ue.x, ue.z) / 100; stored_quat = (ue.y, ue.x, ue.z, ue.w)",
    "compass": (
        "GTA north maps to Unreal +Y under this convention. To put north on +X for the Sun Position "
        "Calculator, yaw the region's root actor +90 rather than adding a second conversion."
    ),
}


class ManifestError(Exception):
    pass


class Instance:
    """One placed entity. Transform is already in Unreal space."""

    __slots__ = ("archetype", "position", "rotation", "scale", "lod_level", "lod_dist", "tint", "guid")

    def __init__(self, archetype, position, rotation, scale,
                 lod_level=0, lod_dist=0.0, tint=0, guid=0):
        self.archetype = archetype
        self.position = list(position)
        self.rotation = list(rotation)
        self.scale = list(scale)
        self.lod_level = int(lod_level)
        self.lod_dist = float(lod_dist)
        self.tint = int(tint)
        self.guid = int(guid)

    @classmethod
    def from_json(cls, d):
        try:
            return cls(d["archetype"], d["position"], d["rotation"], d["scale"],
                       d.get("lod_level", 0), d.get("lod_dist", 0.0),
                       d.get("tint", 0), d.get("guid", 0))
        except KeyError as e:
            raise ManifestError(f"instance is missing required field {e}") from None

    def to_json(self):
        d = {"archetype": self.archetype, "position": self.position,
             "rotation": self.rotation, "scale": self.scale}
        if self.lod_level: d["lod_level"] = self.lod_level
        if self.lod_dist:  d["lod_dist"] = self.lod_dist
        if self.tint:      d["tint"] = self.tint
        if self.guid:      d["guid"] = self.guid
        return d

    def validate(self):
        for field, n in (("position", 3), ("rotation", 4), ("scale", 3)):
            v = getattr(self, field)
            if len(v) != n:
                raise ManifestError(f"{self.archetype}: {field} has {len(v)} components, expected {n}")
            if not all(isinstance(c, (int, float)) for c in v):
                raise ManifestError(f"{self.archetype}: {field} has a non-numeric component")
        if all(abs(c) < 1e-9 for c in self.rotation):
            raise ManifestError(f"{self.archetype}: rotation is a zero quaternion")


class Archetype:
    """A unique asset. Referenced by many instances, imported once."""

    __slots__ = ("name", "hash", "asset", "bbox_min", "bbox_max", "lod_dist",
                 "texture_dict", "source_archive")

    def __init__(self, name, hash=0, asset=None, bbox_min=None, bbox_max=None,
                 lod_dist=0.0, texture_dict=None, source_archive=None):
        self.name = name
        self.hash = int(hash)
        self.asset = asset                    # path to the exported file, relative to the manifest
        self.bbox_min = list(bbox_min) if bbox_min else None
        self.bbox_max = list(bbox_max) if bbox_max else None
        self.lod_dist = float(lod_dist)
        self.texture_dict = texture_dict
        self.source_archive = source_archive  # which .rpf won, so mod overrides are traceable

    @classmethod
    def from_json(cls, name, d):
        return cls(name, d.get("hash", 0), d.get("asset"), d.get("bbox_min"), d.get("bbox_max"),
                   d.get("lod_dist", 0.0), d.get("texture_dict"), d.get("source_archive"))

    def to_json(self):
        d = {"hash": self.hash}
        for k in ("asset", "bbox_min", "bbox_max", "texture_dict", "source_archive"):
            v = getattr(self, k)
            if v is not None:
                d[k] = v
        if self.lod_dist:
            d["lod_dist"] = self.lod_dist
        return d

    @property
    def extent(self):
        """Size in GTA metres, or None. Useful for the greybox pass and for sanity checks."""
        if not (self.bbox_min and self.bbox_max):
            return None
        return [hi - lo for lo, hi in zip(self.bbox_min, self.bbox_max)]


class Cell:
    """One source ymap. Kept as a grouping so cells can become sublevels or data layers."""

    __slots__ = ("name", "hash", "parent", "instances")

    def __init__(self, name, hash=0, parent=None, instances=None):
        self.name = name
        self.hash = int(hash)
        self.parent = parent                  # parent ymap name, for the LOD hierarchy
        self.instances = instances or []

    @classmethod
    def from_json(cls, d):
        if "name" not in d:
            raise ManifestError("cell is missing 'name'")
        return cls(d["name"], d.get("hash", 0), d.get("parent"),
                   [Instance.from_json(i) for i in d.get("instances", [])])

    def to_json(self):
        d = {"name": self.name, "hash": self.hash,
             "instances": [i.to_json() for i in self.instances]}
        if self.parent:
            d["parent"] = self.parent
        return d


class Region:
    def __init__(self, source=None, bounds=None, archetypes=None, cells=None, unresolved=None):
        self.source = source or {}
        self.bounds = bounds or {}
        self.archetypes = archetypes or {}
        self.cells = cells or []
        self.unresolved = unresolved or []

    # ── io ────────────────────────────────────────────────────────────────────

    @classmethod
    def load(cls, path):
        with open(path, "r", encoding="utf-8") as f:
            d = json.load(f)

        fmt = d.get("format")
        if fmt != FORMAT:
            raise ManifestError(f"unsupported manifest format {fmt!r}, expected {FORMAT!r}")

        conv = d.get("convention", {})
        if conv.get("position") != CONVENTION["position"]:
            raise ManifestError(
                "manifest was written with a different coordinate convention:\n"
                f"  manifest: {conv.get('position')!r}\n"
                f"  expected: {CONVENTION['position']!r}\n"
                "Re-export rather than converting by hand; mismatched conventions mirror the "
                "region against its geometry and look like a rotation bug.")

        return cls(
            source=d.get("source", {}),
            bounds=d.get("bounds", {}),
            archetypes={n: Archetype.from_json(n, a) for n, a in d.get("archetypes", {}).items()},
            cells=[Cell.from_json(c) for c in d.get("cells", [])],
            unresolved=d.get("unresolved", []),
        )

    def save(self, path):
        with open(path, "w", encoding="utf-8") as f:
            json.dump(self.to_json(), f, indent=2)

    def to_json(self):
        return {
            "format": FORMAT,
            "source": self.source,
            "convention": CONVENTION,
            "bounds": self.bounds,
            "archetypes": {n: a.to_json() for n, a in sorted(self.archetypes.items())},
            "cells": [c.to_json() for c in self.cells],
            "unresolved": self.unresolved,
            "stats": self.stats(),
        }

    # ── queries ───────────────────────────────────────────────────────────────

    def all_instances(self):
        for cell in self.cells:
            for inst in cell.instances:
                yield cell, inst

    def stats(self):
        counts = Counter(i.archetype for _, i in self.all_instances())
        lods = Counter(i.lod_level for _, i in self.all_instances())
        return {
            "cells": len(self.cells),
            "archetypes": len(self.archetypes),
            "instances": sum(counts.values()),
            "unresolved_archetypes": len(self.unresolved),
            "instances_by_lod_level": dict(sorted(lods.items())),
            "top_archetypes_by_instance_count": counts.most_common(10),
        }

    def group_for_spawn(self, per_cell=True):
        """
        Instances batched into one call per group. Keyed by (cell, archetype) when per_cell, else
        by archetype alone.

        Per-cell is the default because it mirrors how the game streams and gives one component
        per ymap per asset, which is what makes cells individually toggleable later. Flattening to
        archetype-only produces fewer, larger components: better raw draw performance, at the cost
        of losing the cell boundary.
        """
        groups = defaultdict(list)
        for cell, inst in self.all_instances():
            groups[(cell.name if per_cell else None, inst.archetype)].append(inst)
        return dict(groups)

    # ── validation ────────────────────────────────────────────────────────────

    def validate(self, require_assets=True, manifest_dir=None):
        """
        Returns a list of problems. Empty means clean.

        The failure this is really guarding against: an instance referencing an archetype that
        never resolved. Nothing errors, the spawner quietly places 14,200 of 15,000 things, and
        the beach is just emptier than the game. That is invisible unless it is checked here.
        """
        problems = []

        for cell, inst in self.all_instances():
            try:
                inst.validate()
            except ManifestError as e:
                problems.append(f"cell {cell.name}: {e}")

        referenced = {i.archetype for _, i in self.all_instances()}
        missing = sorted(referenced - set(self.archetypes))
        for name in missing:
            n = sum(1 for _, i in self.all_instances() if i.archetype == name)
            problems.append(f"{n} instance(s) reference archetype {name!r}, which has no entry")

        if require_assets:
            for name, arch in sorted(self.archetypes.items()):
                if name not in referenced:
                    continue
                if not arch.asset:
                    problems.append(f"archetype {name!r} is referenced but has no exported asset")
                elif manifest_dir:
                    p = os.path.join(manifest_dir, arch.asset)
                    if not os.path.exists(p):
                        problems.append(f"archetype {name!r} points at a missing file: {arch.asset}")

        for u in self.unresolved:
            problems.append(
                f"unresolved archetype {u.get('archetype', '?')} "
                f"({u.get('count', '?')} instance(s)): {u.get('reason', 'no reason recorded')}")

        return problems

    def describe(self):
        s = self.stats()
        lines = [
            f"region: {s['instances']:,} instances of {s['archetypes']:,} archetypes "
            f"across {s['cells']:,} cell(s)",
        ]
        if self.bounds.get("gta_min"):
            lo, hi = self.bounds["gta_min"], self.bounds["gta_max"]
            lines.append(f"  gta bounds  X[{lo[0]:.0f},{hi[0]:.0f}] "
                         f"Y[{lo[1]:.0f},{hi[1]:.0f}] Z[{lo[2]:.0f},{hi[2]:.0f}]")
        if s["instances_by_lod_level"]:
            lines.append("  by lod level: " + ", ".join(
                f"{k}={v:,}" for k, v in s["instances_by_lod_level"].items()))
        if s["unresolved_archetypes"]:
            lines.append(f"  !! {s['unresolved_archetypes']} unresolved archetype(s)")
        lines.append("  heaviest: " + ", ".join(
            f"{n} x{c:,}" for n, c in s["top_archetypes_by_instance_count"][:5]))
        return "\n".join(lines)
