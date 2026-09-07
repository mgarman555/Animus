"""Spawns a GAE region manifest into the open Unreal level as instanced components.

Run inside the editor:

    import sys; sys.path.append(r"C:\\path\\to\\Animus\\tools\\ue")
    import spawn_region
    spawn_region.run(r"D:\\Export\\DelPerro\\region.json", asset_root="/Game/FirstDays/GTA")

What it does, and why
---------------------
One StaticMesh per unique archetype, many instances. Never a merged mesh: the point of a
placeholder is that every asset stays individually swappable, and merging throws that away on
the first import.

Instances are batched into a single add_instances call per (cell, archetype) group. The widely
reported "UE5 can't handle 10k instances" is a collision rebuild cost under Chaos, not an
instance-count ceiling, so collision is disabled for the duration of the spawn and restored
afterwards.

Cells map to one actor each, named after the source ymap, so the region can later be split into
sublevels or Data Layers along the same boundaries the game itself streams on.

An archetype an instance references but that has no imported mesh is a hard failure by default,
not a skip. The silent version of this is the worst bug in the pipeline: nothing errors, most of
the region appears, and the beach is quietly emptier than the game.

Caveat, stated plainly: this was written without a running editor to test against. The manifest
parsing, grouping and transform handling are covered by tools/test_ue_spawn.py, but the `unreal`
API calls are not exercised anywhere. The component-creation call is isolated in _add_ism_component
precisely so it is a one-function fix if the API differs in your build.
"""

import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from region_manifest import Region, ManifestError   # noqa: E402

try:
    import unreal
except ImportError:
    unreal = None


# ── naming ────────────────────────────────────────────────────────────────────

def asset_path_for(archetype_name, asset_root):
    """Archetype 'prop_bench_01a' -> '/Game/.../prop_bench_01a.prop_bench_01a'."""
    n = archetype_name.strip().lower()
    return f"{asset_root.rstrip('/')}/{n}.{n}"


def resolve_assets(region, asset_root, log=print):
    """
    Maps every referenced archetype to a loaded StaticMesh.

    Returns (meshes, missing). Splitting resolution from spawning means the whole region is
    checked before a single actor is created, so a missing asset is caught before half a beach
    is in the level.
    """
    meshes, missing = {}, []
    referenced = sorted({i.archetype for _, i in region.all_instances()})

    for name in referenced:
        path = asset_path_for(name, asset_root)
        if not unreal.EditorAssetLibrary.does_asset_exist(path):
            missing.append((name, path))
            continue
        asset = unreal.EditorAssetLibrary.load_asset(path)
        if not isinstance(asset, unreal.StaticMesh):
            missing.append((name, f"{path} (loaded as {type(asset).__name__}, not StaticMesh)"))
            continue
        meshes[name] = asset

    log(f"resolved {len(meshes)}/{len(referenced)} archetypes under {asset_root}")
    return meshes, missing


# ── transforms ────────────────────────────────────────────────────────────────

def to_unreal_transform(inst):
    """
    The manifest already holds Unreal-space values, so this only re-packs them. All coordinate
    conversion lives in the exporter, verified by tools/test_transform_convention.py; doing any
    of it here would be a second place for it to drift.
    """
    px, py, pz = inst.position
    qx, qy, qz, qw = inst.rotation
    sx, sy, sz = inst.scale
    return unreal.Transform(
        location=unreal.Vector(px, py, pz),
        rotation=unreal.Quat(qx, qy, qz, qw).rotator(),
        scale=unreal.Vector(sx, sy, sz),
    )


# ── spawning ──────────────────────────────────────────────────────────────────

def _spawn_actor(label, location=None):
    subsystem = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)
    actor = subsystem.spawn_actor_from_class(
        unreal.Actor, location or unreal.Vector(0, 0, 0), unreal.Rotator(0, 0, 0))
    actor.set_actor_label(label)
    return actor


def _add_ism_component(actor, mesh, hierarchical=True):
    """
    The one call most likely to need adjusting for a given engine build. AddComponentByClass is
    BlueprintCallable and reaches Python in 5.1+; components added this way persist as instance
    components on the actor.
    """
    cls = (unreal.HierarchicalInstancedStaticMeshComponent if hierarchical
           else unreal.InstancedStaticMeshComponent)
    comp = actor.add_component_by_class(cls, False, unreal.Transform(), False)
    comp.set_static_mesh(mesh)
    comp.set_collision_enabled(unreal.CollisionEnabled.NO_COLLISION)
    comp.set_mobility(unreal.ComponentMobility.STATIC)
    return comp


def run(manifest_path, asset_root="/Game/GTA", per_cell=True, hierarchical=True,
        enable_collision=False, allow_missing=False, dry_run=False, log=print):
    """
    Returns a summary dict. Raises ManifestError on a manifest that would spawn a wrong or
    incomplete region.
    """
    if unreal is None and not dry_run:
        raise RuntimeError("`unreal` is not importable. Run this inside the editor, "
                           "or pass dry_run=True to check a manifest outside it.")

    started = time.monotonic()
    region = Region.load(manifest_path)
    log(region.describe())

    problems = region.validate(require_assets=True,
                               manifest_dir=os.path.dirname(os.path.abspath(manifest_path)))
    if problems:
        log(f"!! {len(problems)} manifest problem(s):")
        for p in problems[:20]:
            log(f"   {p}")
        if len(problems) > 20:
            log(f"   ... and {len(problems) - 20} more")
        if not allow_missing:
            raise ManifestError(
                f"{len(problems)} manifest problem(s); refusing to spawn a partial region. "
                "Re-export, or pass allow_missing=True if you genuinely want what is there.")

    groups = region.group_for_spawn(per_cell=per_cell)
    log(f"{len(groups)} component group(s) to create")

    if dry_run:
        total = sum(len(v) for v in groups.values())
        log(f"dry run: would create {len(groups)} component(s) holding {total:,} instance(s)")
        return {"groups": len(groups), "instances": total, "dry_run": True,
                "problems": len(problems)}

    meshes, missing = resolve_assets(region, asset_root, log=log)
    if missing and not allow_missing:
        for name, path in missing[:20]:
            log(f"   missing mesh for {name}: {path}")
        raise ManifestError(
            f"{len(missing)} archetype(s) have no imported StaticMesh under {asset_root}. "
            "Import the exported assets first (see import_region_assets.py).")

    root = _spawn_actor(f"GTA_Region_{os.path.splitext(os.path.basename(manifest_path))[0]}")
    cell_actors, created, placed, skipped = {}, 0, 0, 0

    with unreal.ScopedSlowTask(len(groups), "Spawning region") as task:
        task.make_dialog(True)
        for (cell_name, arch_name), instances in sorted(groups.items(), key=lambda kv: str(kv[0])):
            if task.should_cancel():
                log("cancelled by user")
                break
            task.enter_progress_frame(1, f"{cell_name or 'region'} / {arch_name}")

            mesh = meshes.get(arch_name)
            if mesh is None:
                skipped += len(instances)
                continue

            if cell_name is None:
                owner = root
            else:
                owner = cell_actors.get(cell_name)
                if owner is None:
                    owner = _spawn_actor(f"Cell_{cell_name}")
                    owner.attach_to_actor(root, "", unreal.AttachmentRule.KEEP_WORLD,
                                          unreal.AttachmentRule.KEEP_WORLD,
                                          unreal.AttachmentRule.KEEP_WORLD, False)
                    cell_actors[cell_name] = owner

            comp = _add_ism_component(owner, mesh, hierarchical=hierarchical)
            created += 1

            transforms = [to_unreal_transform(i) for i in instances]
            # One batched call. Per-instance add_instance is what makes this slow, not the count.
            comp.add_instances(transforms, False, True, False)
            placed += len(transforms)

            # Per-instance tint, so every palm on the beach is not the same colour. Cheap to set
            # now, harmless if the material never reads it.
            if any(i.tint for i in instances):
                comp.set_editor_property("num_custom_data_floats", 1)
                for idx, inst in enumerate(instances):
                    comp.set_custom_data_value(idx, 0, float(inst.tint), False)

    if enable_collision:
        log("re-enabling collision on spawned components")
        for actor in [root] + list(cell_actors.values()):
            for comp in actor.get_components_by_class(unreal.InstancedStaticMeshComponent):
                comp.set_collision_enabled(unreal.CollisionEnabled.QUERY_AND_PHYSICS)

    elapsed = time.monotonic() - started
    log(f"done in {elapsed:.1f}s: {placed:,} instance(s) in {created} component(s) "
        f"across {len(cell_actors)} cell actor(s)"
        + (f", {skipped:,} skipped" if skipped else ""))

    return {"root": root.get_actor_label(), "components": created, "instances": placed,
            "cells": len(cell_actors), "skipped": skipped, "seconds": round(elapsed, 1)}


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser(description="Spawn a GAE region manifest into the open level.")
    ap.add_argument("manifest")
    ap.add_argument("--asset-root", default="/Game/GTA")
    ap.add_argument("--flat", action="store_true", help="group by archetype only, not per cell")
    ap.add_argument("--simple-ism", action="store_true", help="ISM instead of HISM (no LOD culling)")
    ap.add_argument("--collision", action="store_true", help="re-enable collision after spawning")
    ap.add_argument("--allow-missing", action="store_true", help="spawn anyway despite problems")
    ap.add_argument("--dry-run", action="store_true", help="validate and report, spawn nothing")
    a = ap.parse_args()
    run(a.manifest, asset_root=a.asset_root, per_cell=not a.flat, hierarchical=not a.simple_ism,
        enable_collision=a.collision, allow_missing=a.allow_missing, dry_run=a.dry_run)
