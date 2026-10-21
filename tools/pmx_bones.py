# Dump an MMD model's bone tree with MMD-specific flags (append/grant parents, twist axes, IK) so the Unity
# mapping can be checked against bones that only move through MMD constraints.
#   blender -b --python tools/pmx_bones.py -- <model.pmx> [filter-substring ...]
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
filters = argv[1:]

arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
print("== bones ==")
for pb in arm.pose.bones:
    name = pb.name
    if filters and not any(f in name for f in filters):
        continue
    mb = pb.mmd_bone
    flags = []
    if getattr(mb, "has_additional_rotation", False):
        flags.append(f"append_rot<-{mb.additional_transform_bone}x{mb.additional_transform_influence:.2f}")
    if getattr(mb, "has_additional_location", False):
        flags.append(f"append_loc<-{mb.additional_transform_bone}x{mb.additional_transform_influence:.2f}")
    if getattr(mb, "enabled_fixed_axis", False):
        flags.append("fixed_axis")
    if getattr(mb, "enabled_local_axes", False):
        flags.append("local_axes")
    if not getattr(mb, "is_visible", True):
        flags.append("hidden")
    if not getattr(mb, "is_controllable", True):
        flags.append("uncontrollable")
    for c in pb.constraints:
        flags.append(f"constraint:{c.type}")
    parent = pb.parent.name if pb.parent else "-"
    head = tuple(round(v, 3) for v in pb.bone.head_local)
    print(f"{name}\tparent={parent}\thead={head}\t{' '.join(flags)}")
