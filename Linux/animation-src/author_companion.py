"""Blender-only animation authoring; exports an animation armature, never an avatar.

Run with Blender --background --python <this file> -- --output <owned directory>.
The gait advances .4 m per cycle: each foot travels .24 m over its 60% stance.
All clips use the same rest skeleton and fixed-height floor contacts.
"""
import argparse
import math
from pathlib import Path
import sys

import bpy
from mathutils import Quaternion, Vector

FPS = 60
BONES = [
    ("Hips", None, (0, 0, .96), (0, 0, 1.08)),
    ("Spine", "Hips", (0, 0, 1.08), (0, 0, 1.28)),
    ("Chest", "Spine", (0, 0, 1.28), (0, 0, 1.46)),
    ("Neck", "Chest", (0, 0, 1.46), (0, 0, 1.57)),
    ("Head", "Neck", (0, 0, 1.57), (0, 0, 1.79)),
]
for side, sign in (("Left", 1), ("Right", -1)):
    BONES.extend([
        (side + "Shoulder", "Chest", (sign * .03, 0, 1.44), (sign * .18, 0, 1.44)),
        (side + "UpperArm", side + "Shoulder", (sign * .18, 0, 1.44), (sign * .46, 0, 1.44)),
        (side + "LowerArm", side + "UpperArm", (sign * .46, 0, 1.44), (sign * .72, 0, 1.44)),
        (side + "Hand", side + "LowerArm", (sign * .72, 0, 1.44), (sign * .81, 0, 1.44)),
        (side + "UpperLeg", "Hips", (sign * .095, 0, .96), (sign * .095, -.025, .53)),
        (side + "LowerLeg", side + "UpperLeg", (sign * .095, -.025, .53), (sign * .095, 0, .10)),
        (side + "Foot", side + "LowerLeg", (sign * .095, 0, .10), (sign * .095, -.17, .055)),
        (side + "Toes", side + "Foot", (sign * .095, -.17, .055), (sign * .095, -.24, .055)),
    ])


def ease(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3.0 - 2.0 * t)


def make_rig():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    data = bpy.data.armatures.new("CompanionAuthoringRig")
    rig = bpy.data.objects.new("CompanionAuthoringRig", data)
    bpy.context.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    for name, parent, head, tail in BONES:
        bone = data.edit_bones.new(name)
        bone.head, bone.tail = head, tail
        if parent:
            bone.parent = data.edit_bones[parent]
    bpy.ops.object.mode_set(mode="OBJECT")
    for bone in rig.pose.bones:
        bone.rotation_mode = "QUATERNION"
    return rig


def point_bone(rig, name, world_direction, twist=0):
    bone = rig.pose.bones[name]
    rest = bone.bone.matrix_local.to_quaternion()
    parent_rotation = bone.parent.matrix.to_quaternion() if bone.parent else Quaternion()
    direction = parent_rotation.inverted() @ Vector(world_direction).normalized()
    rest_local = bone.bone.matrix_local.to_quaternion()
    if bone.parent:
        rest_local = bone.parent.bone.matrix_local.to_quaternion().inverted() @ rest
    target = Vector((0, 1, 0)).rotation_difference(direction)
    bone.rotation_quaternion = rest_local.inverted() @ target @ Quaternion((0, 1, 0), twist)
    bpy.context.view_layer.update()


def foot_targets(rig):
    targets = {}
    for side, sign in (("Left", 1), ("Right", -1)):
        target = bpy.data.objects.new(side + "FootContact", None)
        target.location = (sign * .095, 0, .10)
        bpy.context.collection.objects.link(target)
        pole = bpy.data.objects.new(side + "KneeGuide", None)
        pole.location = (sign * .095, -1, .5)
        bpy.context.collection.objects.link(pole)
        ik = rig.pose.bones[side + "LowerLeg"].constraints.new("IK")
        ik.target, ik.pole_target, ik.chain_count = target, pole, 2
        ik.pole_angle = -math.pi / 2
        targets[side] = target
    return targets


def author(rig, targets, name, duration, direction=1):
    action = bpy.data.actions.new(name)
    rig.animation_data_create()
    rig.animation_data.action = action
    frames = round(duration * FPS)
    for frame in range(frames + 1):
        t = frame / frames
        bpy.context.scene.frame_set(frame)
        for bone in rig.pose.bones:
            bone.location = (0, 0, 0)
            bone.rotation_quaternion = Quaternion()
            bone.scale = (1, 1, 1)
        yaw, shift, crouch, gesture = -math.pi, 0, 0, 0
        walk = name.startswith("Walk")
        if walk:
            yaw = -direction * math.pi / 2
            crouch = .004 * (1 - math.cos(4 * math.pi * t))
        elif name.startswith("Turn"):
            yaw = -math.pi + direction * math.pi / 2 * ease(t)
            shift = direction * .025 * math.sin(math.pi * t)
            crouch = .014 * math.sin(math.pi * t)
        elif name.startswith("Start"):
            yaw = -direction * math.pi / 2
            shift = direction * .025 * ease(t)
            crouch = .012 * math.sin(math.pi * t)
        elif name.startswith("Stop"):
            yaw = -math.pi + direction * math.pi / 2 * (1 - ease(t))
            shift = direction * .025 * (1 - ease(t))
            crouch = .018 * math.sin(math.pi * t)
        elif name == "Invite":
            gesture = ease(t / .28) * (1 - ease((t - .72) / .28))
        elif name == "Settle":
            crouch = .008 * math.sin(math.pi * t)
        hips = rig.pose.bones["Hips"]
        # The vertical pelvis bone's local Y is Blender world Z.
        hips.location = (shift, -crouch, 0)
        hips.rotation_quaternion = Quaternion((0, 1, 0), yaw)
        bpy.context.view_layer.update()
        for side, sign in (("Left", 1), ("Right", -1)):
            # Fixed IK contacts carry weight while the pelvis turns and settles.
            phase = (t + (0 if side == "Left" else .5)) % 1
            forward, height = 0, 0
            if walk:
                if phase < .6:
                    forward = .24 * (.5 - phase / .6)
                else:
                    swing = (phase - .6) / .4
                    forward = .24 * (ease(swing) - .5)
                    height = .055 * math.sin(math.pi * swing)
            elif name.startswith("Start") or name.startswith("Stop"):
                spread = (.12 if side == "Left" else -.08)
                blend = ease(t) if name.startswith("Start") else 1 - ease(t)
                forward = spread * blend
                height = .025 * math.sin(math.pi * t) if side == "Left" else 0
            rotation = Quaternion((0, 0, 1), yaw)
            targets[side].location = rotation @ Vector((sign * .095, -forward, .10 + height))
            bpy.data.objects[side + "KneeGuide"].location = rotation @ Vector((sign * .095, -1, .5))
            lift = gesture if side == "Right" else 0
            forward_swing = .05 * math.sin(2 * math.pi * t + (0 if side == "Right" else math.pi)) if walk else 0
            point_bone(rig, side + "UpperArm", rotation @ Vector((sign * (.16 + lift * .10), -.05 - lift * .28 + forward_swing, -.42 + lift * .24)))
            point_bone(rig, side + "LowerArm", rotation @ Vector((sign * (.10 + lift * .08), -.10 - lift * .30 + forward_swing, -.28 + lift * .35)))
            point_bone(rig, side + "Hand", rotation @ Vector((sign * .06, -.08 - lift * .12 + forward_swing, -.12 + lift * .17)), lift * .8)
            point_bone(rig, side + "Foot", rotation @ Vector((0, -.17, -.045)))
        rig.pose.bones["Head"].rotation_quaternion = Quaternion((1, 0, 0), -.05 * gesture)
        bpy.context.view_layer.update()
        evaluated = rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
        solved = {bone.name: bone.matrix.copy() for bone in evaluated.pose.bones}
        # Export FK keys from evaluated IK; transient targets are not exported.
        for bone in rig.pose.bones:
            local = solved[bone.name]
            rest = bone.bone.matrix_local.copy()
            if bone.parent:
                local = solved[bone.parent.name].inverted() @ local
                rest = bone.parent.bone.matrix_local.inverted() @ rest
            bone.matrix_basis = rest.inverted() @ local
        for bone in rig.pose.bones:
            bone.keyframe_insert("location", frame=frame, group=bone.name)
            bone.keyframe_insert("rotation_quaternion", frame=frame, group=bone.name)
    return action


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if any((output / name).exists() for name in ("Companion.blend", "Companion.fbx")):
        raise SystemExit("Refusing to overwrite existing authoring outputs")
    bpy.context.scene.render.fps = FPS
    rig = make_rig()
    targets = foot_targets(rig)
    for name, duration in (("Notice", .4), ("TurnLeft", .5), ("TurnRight", .5),
                           ("StartLeft", .35), ("StartRight", .35), ("StopLeft", .45),
                           ("StopRight", .45), ("Invite", 1.2), ("Wait", 1), ("Settle", .6),
                           ("WalkLeft", 1.2), ("WalkRight", 1.2)):
        action = author(rig, targets, name, duration, -1 if name.endswith("Left") else 1)
        action.use_fake_user = True
    rig.animation_data.action = None
    for bone in rig.pose.bones:
        for constraint in list(bone.constraints):
            bone.constraints.remove(constraint)
        bone.location = (0, 0, 0)
        bone.rotation_quaternion = Quaternion()
        bone.scale = (1, 1, 1)
    bpy.context.scene.frame_set(0)
    bpy.context.view_layer.update()
    bpy.ops.wm.save_as_mainfile(filepath=str(output / "Companion.blend"))
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.export_scene.fbx(filepath=str(output / "Companion.fbx"), use_selection=True,
        object_types={"ARMATURE"}, add_leaf_bones=False, axis_forward="Y", axis_up="Z",
        use_space_transform=False,
        apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
        bake_anim=True, bake_anim_use_all_actions=True,
        bake_anim_use_nla_strips=False, bake_anim_simplify_factor=0, bake_anim_step=1,
        use_armature_deform_only=True, armature_nodetype="NULL")


if __name__ == "__main__":
    main()
