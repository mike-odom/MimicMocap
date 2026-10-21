# Convert an MMD model (PMX) to an FBX that Unity can import as a Humanoid, using Blender + MMD Tools.
#
#   blender -b --python tools/pmx_to_fbx.py -- <model.pmx> <out.fbx> [scale]
#
# Requires the MMD Tools add-on (https://github.com/MMD-Blender/blender_mmd_tools) installed and enabled.
# Bone names are kept in Japanese; MimicCharacterTools maps them to Mecanim bones on import.
# Textures are copied next to the FBX (path_mode COPY); materials are rebuilt as plain Principled BSDFs so the
# exporter writes them.
import os
import sys

import bpy

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if len(argv) < 2:
    print("usage: blender -b --python pmx_to_fbx.py -- <model.pmx> <out.fbx> [scale]")
    sys.exit(2)
pmx_path = os.path.abspath(argv[0])
fbx_path = os.path.abspath(argv[1])
scale = float(argv[2]) if len(argv) > 2 else 0.08  # 1 MMD unit is about 8 cm

# Empty scene but keep the user preferences (read_factory_settings would disable the extension again).
bpy.ops.wm.read_homefile(use_empty=True)

import addon_utils
for module in ("bl_ext.user_default.mmd_tools", "mmd_tools"):
    try:
        if addon_utils.enable(module, default_set=False) is not None:
            break
    except Exception as e:  # noqa: BLE001
        print("could not enable", module, e)
if not hasattr(bpy.ops.mmd_tools, "import_model"):
    print("MMD Tools is not available; install it into this Blender first")
    sys.exit(1)

print("importing", pmx_path, "scale", scale)
bpy.ops.mmd_tools.import_model(
    filepath=pmx_path,
    scale=scale,
    # Mesh and armature only: no rigid bodies/joints (Unity gets a plain skinned rig) and no morphs, which the
    # app never drives and which add ~130 MB of blendshape data per model to the player.
    types={"MESH", "ARMATURE"},
    clean_model=True,
    remove_doubles=False,
    log_level="WARNING",
)

armature = next((o for o in bpy.data.objects if o.type == "ARMATURE"), None)
if armature is None:
    print("no armature imported")
    sys.exit(1)

# Report the bone tree so the Unity mapping can be checked against the real names.
print("== bones ==")
for b in armature.data.bones:
    print(f"{b.name}\tparent={b.parent.name if b.parent else '-'}\thead={tuple(round(v, 3) for v in b.head_local)}")

meshes = [o for o in bpy.data.objects if o.type == "MESH"]
print("== meshes ==", [m.name for m in meshes])


def bone_depth(pb):
    d = 0
    while pb.parent is not None:
        pb = pb.parent
        d += 1
    return d


def is_ancestor(ancestor, pb):
    while pb is not None:
        if pb == ancestor:
            return True
        pb = pb.parent
    return False


def ik_follower_bones(arm):
    """
    Some models (Sour's Miku among them) skin the arm and neck mesh to helper bones that MMD only ever moves through
    an IK chain: 腕W points from the shoulder at the elbow, ひじW from the elbow at the wrist, 手W at the hand tip, 首W at
    the head. The FBX has no IK, so in Unity those bones never move and the mesh stays in the rest pose while the real
    bones go on without it. For each deform bone with an IK constraint, find the plain controllable bone that starts
    at the same joint and is an ancestor of the IK target (the deepest one: 腕 rather than 肩C), so its weights can be
    handed over.
    """
    pairs = {}
    for pb in arm.pose.bones:
        ik = next((c for c in pb.constraints if c.type == "IK"), None)
        if ik is None or not ik.subtarget:
            continue
        target = arm.pose.bones.get(ik.subtarget)
        if target is None or target.parent is None:
            continue
        head = pb.bone.head_local
        best = None
        for cand in arm.pose.bones:
            if cand == pb or cand.name.startswith("_") or any(c.type == "IK" for c in cand.constraints):
                continue
            if (cand.bone.head_local - head).length > 1e-4:
                continue
            if not getattr(cand.mmd_bone, "is_controllable", True):
                continue
            if not is_ancestor(cand, target.parent):
                continue
            if best is None or bone_depth(cand) > bone_depth(best):
                best = cand
        if best is not None:
            pairs[pb.name] = best.name
    return pairs


def transfer_weights(mesh_objects, pairs):
    """Move every vertex weight of each source group onto its destination group and drop the source group."""
    moved = {}
    for obj in mesh_objects:
        for src_name, dst_name in pairs.items():
            src = obj.vertex_groups.get(src_name)
            if src is None:
                continue
            dst = obj.vertex_groups.get(dst_name) or obj.vertex_groups.new(name=dst_name)
            total = 0.0
            for v in obj.data.vertices:
                for g in v.groups:
                    if g.group == src.index and g.weight > 0.0:
                        dst.add([v.index], g.weight, "ADD")
                        total += g.weight
            obj.vertex_groups.remove(src)
            if total > 0.0:
                moved[src_name] = moved.get(src_name, 0.0) + total
    return moved


def retire_bones(arm, pairs):
    """Hand each retired bone's children to its replacement (same joint, so nothing moves) and delete it."""
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="EDIT")
    eb = arm.data.edit_bones
    for src_name, dst_name in pairs.items():
        src, dst = eb.get(src_name), eb.get(dst_name)
        if src is None or dst is None:
            continue
        for child in list(src.children):
            child.use_connect = False
            child.parent = dst
        eb.remove(src)
    bpy.ops.object.mode_set(mode="OBJECT")


followers = ik_follower_bones(armature)
moved = transfer_weights(meshes, followers)
# Only bones that carried mesh are retired; accessories hanging off them (Sour's wrist cube under ひじW) move with
# them to the real bone, so they bend with the elbow too.
retired = {src: dst for src, dst in followers.items() if moved.get(src, 0.0) > 0.0}
retire_bones(armature, retired)
print("== IK follower bones ==")
for src, dst in sorted(followers.items()):
    w = moved.get(src, 0.0)
    print(f"{src} -> {dst}	weight {w:.1f}	{'retired' if src in retired else 'no mesh, kept'}")


def base_texture(mat):
    """The MMD base texture image of a material (not the toon ramp or sphere map)."""
    if not mat.use_nodes:
        return None
    nodes = mat.node_tree.nodes
    for name in ("mmd_base_tex",):
        n = nodes.get(name)
        if n is not None and n.type == "TEX_IMAGE" and n.image is not None:
            return n.image
    for n in nodes:
        if n.type == "TEX_IMAGE" and n.image is not None:
            folder = os.path.basename(os.path.dirname(n.image.filepath.replace("\\", "/"))).lower()
            if folder not in ("toon", "spa", "sph"):
                return n.image
    return None


def rebuild_material(mat):
    """
    Replace MMD Tools' shader node group with a plain Principled BSDF that the FBX exporter understands:
    base texture into Base Color, MMD's material alpha into Alpha (parts a morph normally shows ship with
    alpha 0 and must stay hidden), a matte roughness so Unity's import is not a shiny plastic.
    """
    image = base_texture(mat)
    mmd = getattr(mat, "mmd_material", None)
    alpha = float(getattr(mmd, "alpha", 1.0)) if mmd is not None else 1.0
    diffuse = tuple(getattr(mmd, "diffuse_color", (1.0, 1.0, 1.0)))[:3] if mmd is not None else (1.0, 1.0, 1.0)
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Base Color"].default_value = (*diffuse, 1.0) if image is None else (1.0, 1.0, 1.0, 1.0)
    bsdf.inputs["Roughness"].default_value = 0.85
    bsdf.inputs["Alpha"].default_value = alpha
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    if image is not None:
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.image = image
        nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    try:
        mat.blend_method = "BLEND" if alpha < 0.999 else "OPAQUE"
    except Exception:  # noqa: BLE001  (removed in newer Blender)
        pass
    return image, alpha


for m in meshes:
    for slot in m.material_slots:
        mat = slot.material
        if mat is None:
            continue
        image, alpha = rebuild_material(mat)
        print(f"material {mat.name}\talpha={alpha:.2f}\ttexture={image.filepath if image else None}")

# Unity reads bone names from the FBX; keep them. Select everything that matters and export.
bpy.ops.object.select_all(action="DESELECT")
armature.select_set(True)
for m in meshes:
    m.select_set(True)
bpy.context.view_layer.objects.active = armature

os.makedirs(os.path.dirname(fbx_path), exist_ok=True)
bpy.ops.export_scene.fbx(
    filepath=fbx_path,
    use_selection=True,
    object_types={"ARMATURE", "MESH"},
    add_leaf_bones=False,
    bake_anim=False,
    use_armature_deform_only=False,
    mesh_smooth_type="FACE",
    use_mesh_modifiers=True,
    # COPY without embedding writes the PNGs next to the FBX with relative references, which Unity's importer
    # resolves on its own; embedded textures were not picked up.
    path_mode="COPY",
    embed_textures=False,
    apply_scale_options="FBX_SCALE_ALL",
    axis_forward="-Z",
    axis_up="Y",
)
print("exported", fbx_path)

# Unity's importer resolves a material's texture by file name from the FBX's folder or a Textures subfolder,
# so put every base texture there ourselves (the exporter's COPY mode did not).
import shutil
tex_dir = os.path.join(os.path.dirname(fbx_path), "Textures")
os.makedirs(tex_dir, exist_ok=True)
copied = set()
for mat in bpy.data.materials:
    image = base_texture(mat) if mat.use_nodes else None
    if image is None or image.filepath in copied:
        continue
    src = bpy.path.abspath(image.filepath)
    if os.path.isfile(src):
        shutil.copy2(src, os.path.join(tex_dir, os.path.basename(src)))
        copied.add(image.filepath)
print("textures copied:", sorted(os.path.basename(p) for p in copied))
