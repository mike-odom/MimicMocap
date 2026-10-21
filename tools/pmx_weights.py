# Report which bones actually deform an MMD model (sum of vertex weights per vertex group).
#   blender -b --python tools/pmx_weights.py -- <model.pmx>
import sys
import bpy

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
bpy.ops.wm.read_homefile(use_empty=True)
import addon_utils
for module in ("bl_ext.user_default.mmd_tools", "mmd_tools"):
    try:
        if addon_utils.enable(module, default_set=False) is not None:
            break
    except Exception:  # noqa: BLE001
        pass
bpy.ops.mmd_tools.import_model(filepath=argv[0], scale=0.08, types={"MESH", "ARMATURE"}, clean_model=False, log_level="WARNING")

totals = {}
for o in bpy.data.objects:
    if o.type != "MESH":
        continue
    names = {g.index: g.name for g in o.vertex_groups}
    for v in o.data.vertices:
        for g in v.groups:
            if g.weight > 0.0:
                totals[names[g.group]] = totals.get(names[g.group], 0.0) + g.weight
print("== deforming bones (weight sum) ==")
for name, w in sorted(totals.items(), key=lambda kv: -kv[1]):
    print(f"{w:10.1f}\t{name}")
