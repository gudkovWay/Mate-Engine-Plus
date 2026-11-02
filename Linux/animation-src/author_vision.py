"""Author animation-only Vision assets; never create or replace an avatar.

blender --background --factory-startup --python Linux/animation-src/author_vision.py \
    -- --output /absolute/absent-or-empty-owned-directory

Only bpy, mathutils and the Python standard library are used. This source does not
invoke Blender, Unity, a shell, a network service, or another process. Running it
inside the explicitly launched Blender produces the files in OUTPUT_NAMES.
The prop FBX is a library of independent, origin-centred rigid meshes, not a
character or an assembled scene. Runtime owns visibility, attachment handoffs,
anatomical retargeting, native seating, clearance calibration and facial expressions.
"""

import argparse
from dataclasses import dataclass
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import tempfile

import bpy
from mathutils import Matrix, Quaternion, Vector


FPS = 60
KEY_STEP = 3  # 20 Hz FK authoring; FBX bake at 30 Hz, including exact endpoints.
BAKE_STEP = 2
RIG_NAME = "VisionRig"
OUTPUT_NAMES = ("Vision.blend", "Vision.fbx", "VisionProps.fbx", "VisionRest.fbx", "manifest.json")
YAW = Quaternion((0, 0, 1), -math.pi)
SIDES = (("Left", 1), ("Right", -1))
FINGERS = ("Thumb", "Index", "Middle", "Ring", "Little")
SEGMENTS = ("Proximal", "Intermediate", "Distal")
CLIPS = (
    ("MagnifierEquip", 1.8), ("MagnifierHold", 6.0),
    ("MagnifierLower", .55), ("MagnifierRaise", .55), ("MagnifierStow", 1.0),
    ("NotebookEquip", 1.3), ("NotebookHold", 6.0),
    ("NotebookWrite", 2.0), ("NotebookThink", 2.0), ("NotebookStow", 1.2),
    ("HeadphonesEquip", 1.5), ("HeadphonesHold", 6.0),
    ("HeadphonesAdjust", 1.2), ("HeadphonesStow", 1.5),
    ("PopcornEquip", 1.2), ("PopcornHold", 6.0),
    ("PopcornBite", 2.0), ("PopcornPause", 2.0), ("PopcornStow", 1.2),
    ("ChatHold", 6.0), ("WatchHold", 6.0),
    ("Smile", 1.2), ("Laugh", 1.6), ("Surprise", 1.2),
    ("Think", 1.5), ("Concern", 1.5),
)
EXPRESSIONS = {"Smile", "Laugh", "Surprise", "Think", "Concern"}
BODY_BONES = [
    ("Hips", None, (0, 0, .96), (0, 0, 1.08)),
    ("Spine", "Hips", (0, 0, 1.08), (0, 0, 1.28)),
    ("Chest", "Spine", (0, 0, 1.28), (0, 0, 1.46)),
    ("Neck", "Chest", (0, 0, 1.46), (0, 0, 1.57)),
    ("Head", "Neck", (0, 0, 1.57), (0, 0, 1.79)),
]
for _side, _sign in SIDES:
    BODY_BONES.extend([
        (_side + "Shoulder", "Chest", (_sign * .03, 0, 1.44), (_sign * .18, 0, 1.44)),
        (_side + "UpperArm", _side + "Shoulder", (_sign * .18, 0, 1.44), (_sign * .46, 0, 1.44)),
        (_side + "LowerArm", _side + "UpperArm", (_sign * .46, 0, 1.44), (_sign * .72, 0, 1.44)),
        (_side + "Hand", _side + "LowerArm", (_sign * .72, 0, 1.44), (_sign * .81, 0, 1.44)),
        (_side + "UpperLeg", "Hips", (_sign * .095, 0, .96), (_sign * .095, -.025, .53)),
        (_side + "LowerLeg", _side + "UpperLeg", (_sign * .095, -.025, .53), (_sign * .095, 0, .10)),
        (_side + "Foot", _side + "LowerLeg", (_sign * .095, 0, .10), (_sign * .095, -.17, .055)),
        (_side + "Toes", _side + "Foot", (_sign * .095, -.17, .055), (_sign * .095, -.24, .055)),
    ])
FINGER_BONES = tuple(side + finger + segment for side, _ in SIDES
                     for finger in FINGERS for segment in SEGMENTS)
STATIC_BONES = {"Hips", "Spine"} | {
    side + suffix for side, _ in SIDES
    for suffix in ("Shoulder", "UpperLeg", "LowerLeg", "Foot", "Toes")
}


def ease(t):
    t = min(1.0, max(0.0, t))
    return t * t * (3.0 - 2.0 * t)


def basis(direction, palm):
    """Bone +Y follows the hand/finger; bone +Z is the palmar direction."""
    y = Vector(direction).normalized()
    z = Vector(palm) - y * Vector(palm).dot(y)
    if z.length < 1e-7:
        raise ValueError("Degenerate hand/limb frame")
    z.normalize()
    x = y.cross(z).normalized()
    z = x.cross(y).normalized()
    return Matrix((x, y, z)).transposed().to_quaternion()


def transform(position, rotation=None):
    rotation = rotation if rotation is not None else Quaternion()
    result = rotation.to_matrix().to_4x4()
    result.translation = Vector(position)
    return result


def grip_profile(name):
    # Angles are radians, proximal/intermediate/distal, with a separate thumb
    # opposition rotation. Rest thumb metacarpal direction is already splayed.
    profiles = {
        "relaxed": ((.18, .22, .12), (.20, .24, .16), .08),
        "power": ((.62, .64, .45), (.94, 1.08, .65), .62),
        "support": ((.28, .30, .20), (.28, .38, .24), .32),
        "pencil": ((.58, .58, .38), (.72, .84, .48), .72),
        "cup": ((.35, .43, .26), (.42, .57, .30), .40),
        "pinch": ((.64, .64, .45), (.84, .93, .54), .76),
        "open": ((.10, .12, .08), (.10, .12, .08), .10),
    }
    thumb, fingers, opposition = profiles[name]
    values = list(thumb)
    for index in range(4):
        if name in {"pencil", "pinch"} and index >= 2:
            values.extend((.92, 1.02, .62))
        elif name == "support" and index == 3:
            values.extend((.38, .46, .28))
        else:
            values.extend(fingers)
    return tuple(values), opposition


@dataclass
class HandTarget:
    position: Vector
    rotation: Quaternion
    curls: tuple
    opposition: float

    def shifted(self, delta):
        return HandTarget(self.position + Vector(delta), self.rotation.copy(),
                          self.curls, self.opposition)

    def with_grip(self, name):
        curls, opposition = grip_profile(name)
        return HandTarget(self.position.copy(), self.rotation.copy(), curls, opposition)

    def matrix(self):
        return transform(self.position, self.rotation)


@dataclass
class Pose:
    left: HandTarget
    right: HandTarget
    chest: tuple = (0.0, 0.0, 0.0)
    neck: tuple = (0.0, 0.0, 0.0)
    head: tuple = (0.0, 0.0, 0.0)


def hand(position, direction, palm, grip="relaxed"):
    curls, opposition = grip_profile(grip)
    return HandTarget(Vector(position), basis(direction, palm), curls, opposition)


def grasp(prop_matrix, prop_point, direction, palm, hand_point, grip):
    rotation = basis(direction, palm)
    wrist = prop_matrix @ Vector(prop_point) - rotation @ Vector(hand_point)
    curls, opposition = grip_profile(grip)
    return HandTarget(wrist, rotation, curls, opposition)


def blend_hand(a, b, t):
    return HandTarget(a.position.lerp(b.position, t), a.rotation.slerp(b.rotation, t),
                      tuple(x + (y - x) * t for x, y in zip(a.curls, b.curls)),
                      a.opposition + (b.opposition - a.opposition) * t)


def blend_pose(a, b, t):
    def angles(x, y):
        return tuple(u + (v - u) * t for u, v in zip(x, y))
    return Pose(blend_hand(a.left, b.left, t), blend_hand(a.right, b.right, t),
                angles(a.chest, b.chest), angles(a.neck, b.neck), angles(a.head, b.head))


def timeline(t, stops):
    if t <= stops[0][0]:
        return blend_pose(stops[0][1], stops[0][1], 0)
    for (start, a), (end, b) in zip(stops, stops[1:]):
        if t <= end:
            return blend_pose(a, b, ease((t - start) / (end - start)))
    return blend_pose(stops[-1][1], stops[-1][1], 0)


def build_states():
    """All target coordinates precede the companion's fixed -pi facing yaw."""
    left = hand((.14, -.21, 1.08), (0, -.9, -.25), (0, -.25, .9))
    right = hand((-.14, -.21, 1.08), (0, -.9, -.25), (0, -.25, .9))
    neutral = Pose(left, right)
    magnifier_matrix = transform((-.065, -.263, 1.67))
    magnifier_hand = grasp(magnifier_matrix, (0, 0, -.13), (1, 0, 0),
                            (0, 1, 0), (0, .045, .018), "power")
    magnifier = Pose(left, magnifier_hand, chest=(.10, 0, 0), head=(.035, 0, 0))
    lower = Pose(left, magnifier_hand.shifted((-.075, .015, -.285)), chest=(.04, 0, 0))
    right_hip = hand((-.245, -.12, 1.04), (1, 0, 0), (0, 1, 0), "power")
    notebook_matrix = transform((0, -.33, 1.15))
    notebook_hand = grasp(notebook_matrix, (.075, .015, -.006), (0, -1, 0),
                           (0, 0, 1), (0, .045, .025), "support")
    pencil_rotation = Vector((0, 0, 1)).rotation_difference(Vector((-.20, .35, 1)).normalized())
    pencil_matrix = transform((-.065, -.355, 1.164), pencil_rotation)
    pencil_hand = grasp(pencil_matrix, (0, 0, .045), (0, -1, -.20),
                         (1, 0, 0), (0, .045, .020), "pencil")
    notebook = Pose(notebook_hand, pencil_hand)
    notebook_pickup = Pose(notebook_hand.shifted((0, .10, -.09)),
                           pencil_hand.shifted((-.10, .16, -.14)))
    notebook_think = Pose(notebook_hand, pencil_hand.shifted((.015, .175, .265)),
                          head=(.025, -.045, .015))
    headphones_matrix = transform((0, 0, 1.665))

    def cups(prop_matrix):
        return Pose(
            grasp(prop_matrix, (.135, 0, -.005), (0, 0, 1), (-1, 0, 0),
                  (0, .045, .015), "cup"),
            grasp(prop_matrix, (-.135, 0, -.005), (0, 0, 1), (1, 0, 0),
                  (0, .045, .015), "cup"))

    on_ears = cups(headphones_matrix)
    headphones_pickup = cups(transform((0, -.29, 1.14)))
    headphones_front = cups(transform((0, -.28, 1.665)))
    # Slightly above the ears on approach avoids dragging the band through the head.
    headphones_above = cups(transform((0, -.07, 1.72)))
    popcorn_matrix = transform((0, -.30, 1.075))
    popcorn_hand = grasp(popcorn_matrix, (.060, 0, .055), (0, 0, 1),
                          (-1, 0, 0), (0, .045, .018), "support")
    popcorn = Pose(popcorn_hand, right)
    kernel_matrix = transform((-.025, -.30, 1.228))
    kernel_hand = grasp(kernel_matrix, (0, 0, 0), (0, -1, .30), (1, 0, 0),
                        (0, .055, .015), "pinch")
    popcorn_pick = Pose(popcorn_hand, kernel_hand)
    popcorn_mouth = Pose(popcorn_hand, kernel_hand.shifted((-.005, .14, .374)))
    popcorn_half = Pose(popcorn_hand, kernel_hand.shifted((-.035, .05, .177)))
    popcorn_pickup = Pose(popcorn_hand.shifted((.075, .12, -.035)), right)
    chat = Pose(left, hand((-.13, -.16, 1.535), (0, 0, 1), (1, 0, 0), "relaxed"),
                head=(.025, .04, .015))
    return {
        "neutral": neutral, "magnifier": magnifier, "magnifier_lower": lower,
        "magnifier_pickup": Pose(left, right_hip), "notebook": notebook,
        "notebook_pickup": notebook_pickup, "notebook_think": notebook_think,
        "headphones": neutral, "headphones_cups": on_ears,
        "headphones_pickup": headphones_pickup, "headphones_front": headphones_front,
        "headphones_above": headphones_above,
        "headphones_adjust": Pose(left, on_ears.right), "popcorn": popcorn,
        "popcorn_pick": popcorn_pick, "popcorn_mouth": popcorn_mouth,
        "popcorn_half": popcorn_half, "popcorn_pickup": popcorn_pickup, "chat": chat,
    }, {
        "Magnifier": magnifier_matrix, "Notebook": notebook_matrix,
        "Pencil": pencil_matrix, "Headphones": headphones_matrix,
        "PopcornBox": popcorn_matrix, "PopcornKernel": kernel_matrix,
    }


def clip_paths(states):
    s = states
    n = s["neutral"]
    return {
        "MagnifierEquip": [(0, n), (.22, s["magnifier_pickup"]),
                            (.32, s["magnifier_pickup"]), (.68, s["magnifier_lower"]), (1, s["magnifier"])],
        "MagnifierLower": [(0, s["magnifier"]), (1, s["magnifier_lower"])],
        "MagnifierRaise": [(0, s["magnifier_lower"]), (1, s["magnifier"])],
        "MagnifierStow": [(0, s["magnifier_lower"]), (.72, s["magnifier_pickup"]), (1, n)],
        "NotebookEquip": [(0, n), (.22, s["notebook_pickup"]), (1, s["notebook"])],
        "NotebookThink": [(0, s["notebook"]), (.36, s["notebook_think"]),
                           (.68, s["notebook_think"]), (1, s["notebook"])],
        "NotebookStow": [(0, s["notebook"]), (.78, s["notebook_pickup"]), (1, n)],
        "HeadphonesEquip": [(0, n), (.16, s["headphones_pickup"]),
                             (.43, s["headphones_front"]), (.58, s["headphones_above"]),
                             (.72, s["headphones_cups"]), (.82, s["headphones_cups"]), (1, n)],
        "HeadphonesAdjust": [(0, n), (.36, s["headphones_adjust"]),
                              (.64, s["headphones_adjust"]), (1, n)],
        "HeadphonesStow": [(0, n), (.18, s["headphones_cups"]),
                            (.28, s["headphones_cups"]), (.42, s["headphones_above"]),
                            (.57, s["headphones_front"]), (.84, s["headphones_pickup"]), (1, n)],
        "PopcornEquip": [(0, n), (.22, s["popcorn_pickup"]), (1, s["popcorn"])],
        "PopcornBite": [(0, s["popcorn"]), (.20, s["popcorn_pick"]),
                         (.49, s["popcorn_mouth"]), (.61, s["popcorn_mouth"]),
                         (.80, s["popcorn_half"]), (1, s["popcorn"])],
        "PopcornPause": [(0, s["popcorn"]), (.20, s["popcorn_pick"]),
                          (.38, s["popcorn_half"]), (.68, s["popcorn_half"]),
                          (.84, s["popcorn_pick"]), (1, s["popcorn"])],
        "PopcornStow": [(0, s["popcorn"]), (.78, s["popcorn_pickup"]), (1, n)],
    }


def sample_pose(name, t, states, paths):
    if name in paths:
        pose = timeline(t, paths[name])
        if name == "HeadphonesAdjust" and .36 < t < .64:
            # A small cup reseat, not a hand held stationary beside the ear.
            pulse = math.sin(math.pi * (t - .36) / .28) ** 2
            pose.right = pose.right.shifted((.004 * pulse, 0, .003 * pulse))
        return pose
    bases = {
        "MagnifierHold": "magnifier", "NotebookHold": "notebook",
        "NotebookWrite": "notebook", "HeadphonesHold": "headphones",
        "PopcornHold": "popcorn", "ChatHold": "chat", "WatchHold": "neutral",
    }
    if name in bases:
        base = states[bases[name]]
        pose = blend_pose(base, base, 0)
        if name == "NotebookWrite":
            if .10 < t < .90:
                phase = ((t - .10) / .80 * 4) % 1.0
                if phase <= .65:
                    stroke = ease(phase / .65)
                    lift = 0.0
                else:
                    return_phase = (phase - .65) / .35
                    stroke = 1 - ease(return_phase)
                    lift = .008 * math.sin(math.pi * return_phase) ** 2
                pose.right = pose.right.shifted((.012 * stroke, .004 * stroke, lift))
            pose.head = (.025 * math.sin(math.pi * t) ** 2, 0, 0)
        else:
            # Exact matching loop endpoints; wrists remain anchored in canonical
            # space while arm FK compensates for this small breathing accent.
            wave = math.sin(2 * math.pi * t)
            pose.chest = (base.chest[0] + .006 * wave, base.chest[1], base.chest[2])
            pose.neck = (base.neck[0] + .004 * wave, base.neck[1], base.neck[2])
            pose.head = (base.head[0], base.head[1] + .007 * wave, base.head[2])
        return pose
    if name in EXPRESSIONS:
        base = states["neutral"]
        pose = blend_pose(base, base, 0)
        envelope = math.sin(math.pi * t) ** 2
        if name == "Smile":
            pose.head = (.025 * envelope, 0, .022 * envelope)
        elif name == "Laugh":
            pose.head = ((-.025 + .022 * math.sin(6 * math.pi * t)) * envelope, 0, 0)
            pose.chest = (.015 * math.sin(8 * math.pi * t) * envelope, 0, 0)
        elif name == "Surprise":
            pose.neck = (-.045 * envelope, 0, 0)
            pose.head = (-.035 * envelope, 0, 0)
            pose.chest = (-.012 * envelope, 0, 0)
        elif name == "Think":
            pose.head = (.02 * envelope, .075 * envelope, -.045 * envelope)
        elif name == "Concern":
            pose.head = (.04 * envelope, -.025 * envelope, .03 * envelope)
            pose.chest = (.012 * envelope, 0, 0)
        return pose
    raise ValueError("Unimplemented action: " + name)


def make_rig():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    data = bpy.data.armatures.new(RIG_NAME)
    rig = bpy.data.objects.new(RIG_NAME, data)
    bpy.context.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    for name, parent, head, tail in BODY_BONES:
        bone = data.edit_bones.new(name)
        bone.head, bone.tail = head, tail
        bone.use_deform = True
        if parent:
            bone.parent = data.edit_bones[parent]
    for side, sign in SIDES:
        palm = data.edit_bones[side + "Hand"]
        palm.align_roll(Vector((0, -1, 0)))
        palm_matrix = palm.matrix.copy()
        for index, finger in enumerate(FINGERS):
            if finger == "Thumb":
                start = Vector((-sign * .034, .018, 0))
                direction = Vector((-sign * .85, .52, 0)).normalized()
                lengths = (.030, .023, .020)
            else:
                offset = (-.026, -.009, .009, .026)[index - 1] * sign
                start = Vector((offset, (.065, .071, .068, .059)[index - 1], 0))
                direction = Vector((0, 1, 0))
                lengths = ((.034, .023, .018), (.038, .026, .020),
                           (.035, .024, .018), (.028, .020, .016))[index - 1]
            parent = palm
            for segment, length in zip(SEGMENTS, lengths):
                end = start + direction * length
                bone = data.edit_bones.new(side + finger + segment)
                bone.head, bone.tail = palm_matrix @ start, palm_matrix @ end
                bone.parent = parent
                bone.use_connect = segment != "Proximal"
                bone.use_deform = True
                bone.align_roll(Vector((0, -1, 0)))
                parent, start = bone, end
    bpy.ops.object.mode_set(mode="OBJECT")
    rig.show_in_front = True
    rig.data.display_type = "STICK"
    for bone in rig.pose.bones:
        bone.rotation_mode = "QUATERNION"
    return rig


def reset_pose(rig, facing=True):
    for bone in rig.pose.bones:
        bone.location = (0, 0, 0)
        bone.rotation_quaternion = Quaternion()
        bone.scale = (1, 1, 1)
    if facing:
        # Identical to author_companion: pelvis local Y is world vertical Z.
        # This is a constant facing offset, NEVER animated root motion.
        rig.pose.bones["Hips"].rotation_quaternion = Quaternion((0, 1, 0), -math.pi)
    bpy.context.view_layer.update()


def local_angles(bone, angles):
    pitch, yaw, roll = angles
    # Body bone local X is lateral, local Y is vertical, local Z is depth.
    bone.rotation_quaternion = (Quaternion((0, 1, 0), yaw)
                                @ Quaternion((1, 0, 0), pitch)
                                @ Quaternion((0, 0, 1), roll))


def world_rotation(rig, name, desired):
    bone = rig.pose.bones[name]
    rest = bone.bone.matrix_local.to_quaternion()
    if bone.parent:
        rest_local = bone.parent.bone.matrix_local.to_quaternion().inverted() @ rest
        parent_pose = bone.parent.matrix.to_quaternion()
    else:
        rest_local, parent_pose = rest, Quaternion()
    bone.rotation_quaternion = rest_local.inverted() @ parent_pose.inverted() @ desired
    bpy.context.view_layer.update()


def solve_arm(rig, side, sign, target):
    upper = rig.pose.bones[side + "UpperArm"]
    lower = rig.pose.bones[side + "LowerArm"]
    shoulder = upper.head.copy()
    wrist = YAW @ target.position
    delta = wrist - shoulder
    distance = delta.length
    a, b = upper.bone.length, lower.bone.length
    if not abs(a - b) + 1e-5 < distance < a + b - 1e-5:
        raise ValueError(f"Unreachable {side} wrist: {tuple(target.position)}, distance={distance}")
    direction = delta / distance
    pole = YAW @ Vector((sign * .47, -.06, 1.11))
    bend = pole - shoulder
    bend -= direction * bend.dot(direction)
    if bend.length < 1e-6:
        raise ValueError("Degenerate analytic elbow pole")
    bend.normalize()
    along = (a * a - b * b + distance * distance) / (2 * distance)
    height = math.sqrt(max(0, a * a - along * along))
    elbow = shoulder + direction * along + bend * height
    upper_direction, lower_direction = elbow - shoulder, wrist - elbow
    plane_normal = upper_direction.cross(lower_direction).normalized()
    world_rotation(rig, side + "UpperArm", basis(upper_direction, plane_normal * sign))
    desired_hand_rotation = YAW @ target.rotation
    world_rotation(rig, side + "LowerArm",
                   basis(lower_direction, desired_hand_rotation @ Vector((0, 0, 1))))
    world_rotation(rig, side + "Hand", desired_hand_rotation)
    error = (rig.pose.bones[side + "Hand"].head - wrist).length
    if error > 2e-5:
        raise ValueError(f"FK wrist reconstruction error: {side}: {error}")
    for finger_index, finger in enumerate(FINGERS):
        for segment_index, segment in enumerate(SEGMENTS):
            bone = rig.pose.bones[side + finger + segment]
            curl = target.curls[finger_index * 3 + segment_index]
            rotation = Quaternion((1, 0, 0), curl)
            if finger == "Thumb" and segment == "Proximal":
                rotation = Quaternion((0, 1, 0), sign * target.opposition) @ rotation
            bone.rotation_quaternion = rotation


def apply_pose(rig, pose):
    reset_pose(rig)
    local_angles(rig.pose.bones["Chest"], pose.chest)
    local_angles(rig.pose.bones["Neck"], pose.neck)
    local_angles(rig.pose.bones["Head"], pose.head)
    bpy.context.view_layer.update()
    solve_arm(rig, "Left", 1, pose.left)
    solve_arm(rig, "Right", -1, pose.right)
    bpy.context.view_layer.update()


def action_curves(action):
    # Blender 3.6/4.x legacy Actions and Blender 4.4+/5.x layered Actions.
    if hasattr(action, "fcurves"):
        return list(action.fcurves)
    curves = []
    for layer in action.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                curves.extend(bag.fcurves)
    return curves


def author_actions(rig, states, paths):
    rig.animation_data_create()
    actions, endpoints = {}, {}
    for name, duration in CLIPS:
        action = bpy.data.actions.new(name)
        action.use_fake_user = True
        rig.animation_data.action = action
        end_frame = round(duration * FPS)
        samples = set(range(0, end_frame + 1, KEY_STEP)) | {0, end_frame}
        # Explicit gesture/handoff stops also get keys. Times in the manifest are
        # quantized by the same function so the runtime never races a handoff.
        for fraction, _ in paths.get(name, []):
            samples.add(round(fraction * end_frame))
        if name == "NotebookWrite":
            for cycle in range(4):
                for fraction in (0, .65, 1):
                    samples.add(round((.10 + .20 * (cycle + fraction)) * end_frame))
        previous = {}
        endpoint_samples = []
        for frame in sorted(samples):
            bpy.context.scene.frame_set(frame)
            apply_pose(rig, sample_pose(name, frame / end_frame, states, paths))
            for bone in rig.pose.bones:
                q = bone.rotation_quaternion.copy()
                if bone.name in previous and q.dot(previous[bone.name]) < 0:
                    q.negate()
                    bone.rotation_quaternion = q
                previous[bone.name] = q
                if bone.name not in STATIC_BONES or frame in (0, end_frame):
                    bone.keyframe_insert("rotation_quaternion", frame=frame, group=bone.name)
                if frame in (0, end_frame):
                    bone.keyframe_insert("location", frame=frame, group=bone.name)
                    bone.keyframe_insert("scale", frame=frame, group=bone.name)
            if frame in (0, end_frame):
                endpoint_samples.append({bone.name: bone.rotation_quaternion.copy()
                                         for bone in rig.pose.bones})
        curves = action_curves(action)
        if not curves:
            raise RuntimeError("Blender Action channels were not created")
        for curve in curves:
            for key in curve.keyframe_points:
                key.interpolation = "LINEAR"
        if hasattr(action, "use_frame_range"):
            action.use_frame_range = True
            action.frame_start, action.frame_end = 0, end_frame
        if abs(action.frame_range[0]) > 1e-5 or abs(action.frame_range[1] - end_frame) > 1e-5:
            raise ValueError("Unexpected action duration: " + name)
        actions[name] = action
        endpoints[name] = endpoint_samples
    rig.animation_data.action = None
    reset_pose(rig, facing=False)
    return actions, endpoints


def verify_contract(rig, actions, endpoints):
    """In-process invariants when the owner later runs Blender, not import tests."""
    expected = {name for name, _ in CLIPS}
    if set(actions) != expected or len(actions) != 26:
        raise ValueError("The complete 26-action contract is required")
    expected_bones = {row[0] for row in BODY_BONES} | set(FINGER_BONES)
    if set(rig.data.bones.keys()) != expected_bones or len(expected_bones) != 51:
        raise ValueError("Expected original 21 body bones plus 30 named finger bones")
    if any(bone.constraints for bone in rig.pose.bones):
        raise ValueError("Export rig must contain baked FK, no constraints")

    def same(a, b, label):
        for name in expected_bones:
            if abs(a[name].dot(b[name])) < 1 - 1e-6:
                raise ValueError(f"Pose seam mismatch: {label}/{name}")

    neutral = endpoints["WatchHold"][0]
    for name in expected:
        if name.endswith("Hold") or name in EXPRESSIONS:
            same(*endpoints[name], name + " loop/neutral endpoints")
        for endpoint in endpoints[name]:
            for bone_name in STATIC_BONES:
                if abs(endpoint[bone_name].dot(neutral[bone_name])) < 1 - 1e-6:
                    raise ValueError("Root/leg motion is forbidden: " + name)
    for family in ("Magnifier", "Notebook", "Headphones", "Popcorn"):
        same(endpoints[family + "Equip"][0], neutral, family + " equip starts neutral")
        same(endpoints[family + "Equip"][1], endpoints[family + "Hold"][0], family + " equip/hold")
        same(endpoints[family + "Stow"][1], neutral, family + " stow ends neutral")
    same(endpoints["MagnifierHold"][0], endpoints["MagnifierLower"][0], "magnifier hold/lower")
    same(endpoints["MagnifierLower"][1], endpoints["MagnifierRaise"][0], "magnifier lower/raise")
    same(endpoints["MagnifierRaise"][1], endpoints["MagnifierHold"][0], "magnifier raise/hold")
    same(endpoints["MagnifierLower"][1], endpoints["MagnifierStow"][0], "magnifier chest/stow")
    for family, accents in (("Notebook", ("Write", "Think")),
                            ("Headphones", ("Adjust",)), ("Popcorn", ("Bite", "Pause"))):
        held = endpoints[family + "Hold"][0]
        same(held, endpoints[family + "Stow"][0], family + " hold/stow")
        for accent in accents:
            same(held, endpoints[family + accent][0], family + accent + " start")
            same(held, endpoints[family + accent][1], family + accent + " end")
    for name in EXPRESSIONS:
        same(endpoints[name][0], neutral, name + " neutral reference")


class MeshBuilder:
    """Small flat-shaded rigid meshes; material slots survive the MESH-only FBX."""

    def __init__(self):
        self.vertices = []
        self.faces = []
        self.material_indices = []

    def part(self, vertices, faces, material=0):
        offset = len(self.vertices)
        self.vertices.extend(tuple(v) for v in vertices)
        self.faces.extend(tuple(offset + i for i in face) for face in faces)
        self.material_indices.extend([material] * len(faces))

    def box(self, center, size, material=0, rotation=None):
        x, y, z = (v / 2 for v in size)
        vertices = [(-x, -y, -z), (x, -y, -z), (x, y, -z), (-x, y, -z),
                    (-x, -y, z), (x, -y, z), (x, y, z), (-x, y, z)]
        rotation = rotation if rotation is not None else Quaternion()
        vertices = [rotation @ Vector(v) + Vector(center) for v in vertices]
        self.part(vertices, [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
                             (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)], material)

    def cylinder(self, start, end, radius, material=0, sides=12, end_radius=None):
        start, end = Vector(start), Vector(end)
        axis = (end - start).normalized()
        reference = Vector((0, 0, 1)) if abs(axis.z) < .9 else Vector((0, 1, 0))
        u = axis.cross(reference).normalized()
        v = axis.cross(u).normalized()
        end_radius = radius if end_radius is None else end_radius
        vertices = []
        for point, r in ((start, radius), (end, end_radius)):
            for i in range(sides):
                angle = 2 * math.pi * i / sides
                vertices.append(point + r * (math.cos(angle) * u + math.sin(angle) * v))
        faces = [tuple(reversed(range(sides))), tuple(range(sides, 2 * sides))]
        for i in range(sides):
            j = (i + 1) % sides
            faces.append((i, j, j + sides, i + sides))
        self.part(vertices, faces, material)

    def ring(self, outer, inner, depth, material=0, sides=32):
        vertices = []
        for y, radius in ((-depth / 2, outer), (depth / 2, outer),
                          (-depth / 2, inner), (depth / 2, inner)):
            vertices.extend((radius * math.cos(2 * math.pi * i / sides), y,
                             radius * math.sin(2 * math.pi * i / sides)) for i in range(sides))
        faces = []
        for i in range(sides):
            j = (i + 1) % sides
            faces.extend([(i, j, sides + j, sides + i),
                          (2 * sides + j, 2 * sides + i, 3 * sides + i, 3 * sides + j),
                          (i, 2 * sides + i, 2 * sides + j, j),
                          (sides + j, 3 * sides + j, 3 * sides + i, sides + i)])
        self.part(vertices, faces, material)

    def puff(self, center, radii, material=0):
        # An octahedron with subdivided latitude rings: 26 verts, 32 faces.
        center = Vector(center)
        rx, ry, rz = radii
        vertices = [center + Vector((0, 0, -rz))]
        for latitude in (-math.pi / 4, 0, math.pi / 4):
            for i in range(8):
                angle = i * math.pi / 4
                vertices.append(center + Vector((rx * math.cos(latitude) * math.cos(angle),
                                                 ry * math.cos(latitude) * math.sin(angle),
                                                 rz * math.sin(latitude))))
        vertices.append(center + Vector((0, 0, rz)))
        faces = []
        for i in range(8):
            j = (i + 1) % 8
            faces.append((0, 1 + j, 1 + i))
            for row in range(2):
                a, b = 1 + row * 8, 1 + (row + 1) * 8
                faces.append((a + i, a + j, b + j, b + i))
            faces.append((17 + i, 17 + j, 25))
        self.part(vertices, faces, material)

    def object(self, name, materials, collection):
        mesh = bpy.data.meshes.new(name + "Mesh")
        mesh.from_pydata(self.vertices, [], self.faces)
        mesh.update()
        obj = bpy.data.objects.new(name, mesh)
        collection.objects.link(obj)
        for material in materials:
            mesh.materials.append(material)
        for polygon, index in zip(mesh.polygons, self.material_indices):
            polygon.material_index = index
            polygon.use_smooth = False
        obj["vision_prop"] = name
        obj["units"] = "metres"
        return obj


def material(name, color, roughness=.8, alpha=1.0):
    result = bpy.data.materials.new(name)
    result.diffuse_color = (*color, alpha)
    result.use_nodes = True
    shader = result.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = (*color, 1)
    shader.inputs["Roughness"].default_value = roughness
    shader.inputs["Metallic"].default_value = 0
    shader.inputs["Alpha"].default_value = alpha
    if alpha < 1:
        transmission = shader.inputs.get("Transmission Weight") or shader.inputs.get("Transmission")
        if transmission is not None:
            transmission.default_value = .7
        shader.inputs["IOR"].default_value = 1.45
        if hasattr(result, "surface_render_method"):
            result.surface_render_method = "DITHERED"
        elif hasattr(result, "blend_method"):
            result.blend_method = "BLEND"
        result["runtime_shader_required"] = "Transparent, no screen sampling or magnification"
    return result


def make_props():
    collection = bpy.data.collections.new("VisionProps")
    bpy.context.scene.collection.children.link(collection)
    dark = material("VisionCharcoal", (.105, .12, .125))
    cover = material("VisionSlateCover", (.22, .27, .28))
    paper = material("VisionBlankPaper", (.72, .70, .63))
    wood = material("VisionMutedWood", (.43, .34, .23))
    graphite = material("VisionGraphite", (.075, .08, .08))
    cushion = material("VisionSoftCushion", (.17, .18, .18), .95)
    carton = material("VisionCarton", (.47, .43, .35))
    stripe = material("VisionCartonBand", (.29, .31, .30))
    corn = material("VisionPopcorn", (.78, .74, .59))
    lens_material = material("VisionLensTransparent", (.66, .74, .73), .18, .16)
    props = {}

    mesh = MeshBuilder()
    mesh.ring(.068, .055, .010, sides=32)
    mesh.cylinder((0, 0, -.185), (0, 0, -.059), .011, sides=10)
    mesh.cylinder((0, 0, -.190), (0, 0, -.175), .013, sides=10)
    props["Magnifier"] = mesh.object("Magnifier", [dark], collection)
    mesh = MeshBuilder()
    mesh.cylinder((0, -.001, 0), (0, .001, 0), .054, sides=32)
    lens = mesh.object("Lens", [lens_material], collection)
    lens.parent = props["Magnifier"]
    lens.matrix_parent_inverse = Matrix.Identity(4)
    props["Lens"] = lens

    mesh = MeshBuilder()
    mesh.box((0, 0, -.003), (.018, .176, .009), 0)
    for sign in (-1, 1):
        tilt = Quaternion((0, 1, 0), -sign * .075)
        mesh.box((sign * .062, 0, .001), (.124, .176, .007), 0, tilt)
        mesh.box((sign * .062, 0, .007), (.116, .165, .007), 1, tilt)
        # The page block is entirely blank: no text, symbols or fabricated notes.
    props["Notebook"] = mesh.object("Notebook", [cover, paper], collection)

    mesh = MeshBuilder()
    mesh.cylinder((0, 0, 0), (0, 0, .005), .0006, 2, sides=6, end_radius=.0016)
    mesh.cylinder((0, 0, .005), (0, 0, .017), .0016, 1, sides=6, end_radius=.0038)
    mesh.cylinder((0, 0, .017), (0, 0, .145), .0038, 0, sides=6)
    props["Pencil"] = mesh.object("Pencil", [cover, wood, graphite], collection)

    mesh = MeshBuilder()
    # A single rigid arch around a generic 16-18 cm head, not dynamic cloth.
    for i in range(16):
        a, b = math.pi * i / 16, math.pi * (i + 1) / 16
        start = (.114 * math.cos(a), .006, .132 * math.sin(a))
        end = (.114 * math.cos(b), .006, .132 * math.sin(b))
        mesh.cylinder(start, end, .009, 0, sides=8)
    for sign in (-1, 1):
        mesh.box((sign * .116, 0, -.005), (.038, .065, .080), 0)
        mesh.box((sign * .093, 0, -.005), (.014, .061, .073), 1)
    props["Headphones"] = mesh.object("Headphones", [dark, cushion], collection)

    mesh = MeshBuilder()
    # Hollow tapered carton with an actual rim; internal fill belongs to this
    # rigid mesh. The separate kernel is the only independently attached bite.
    outer_bottom = [(-.049, -.039, 0), (.049, -.039, 0), (.049, .039, 0), (-.049, .039, 0)]
    outer_top = [(-.065, -.050, .15), (.065, -.050, .15), (.065, .050, .15), (-.065, .050, .15)]
    inner_top = [(-.061, -.046, .15), (.061, -.046, .15), (.061, .046, .15), (-.061, .046, .15)]
    inner_bottom = [(-.045, -.035, .005), (.045, -.035, .005), (.045, .035, .005), (-.045, .035, .005)]
    faces = [(0, 3, 2, 1), (12, 13, 14, 15)]
    for i in range(4):
        j = (i + 1) % 4
        faces.extend([(i, j, 4 + j, 4 + i), (4 + i, 4 + j, 8 + j, 8 + i),
                      (8 + i, 8 + j, 12 + j, 12 + i)])
    mesh.part(outer_bottom + outer_top + inner_top + inner_bottom, faces, 0)
    # One muted band on the front; no lettering or high-contrast novelty stripes.
    mesh.box((0, -.045, .073), (.106, .003, .018), 1,
             Quaternion((1, 0, 0), -.073))
    for row in range(3):
        for column in range(4):
            x = -.043 + column * .028 + (.003 if row % 2 else 0)
            y = -.029 + row * .027
            z = .133 + .004 * ((row + column) % 3)
            mesh.puff((x, y, z), (.016, .015, .014), 2)
    props["PopcornBox"] = mesh.object("PopcornBox", [carton, stripe, corn], collection)

    mesh = MeshBuilder()
    mesh.puff((0, 0, 0), (.009, .008, .009), 0)
    mesh.puff((.006, .001, .004), (.006, .006, .006), 0)
    mesh.puff((-.004, -.003, .005), (.006, .005, .006), 0)
    props["PopcornKernel"] = mesh.object("PopcornKernel", [corn], collection)
    if set(props) != {"Magnifier", "Lens", "Notebook", "Pencil", "Headphones", "PopcornBox", "PopcornKernel"}:
        raise ValueError("Prop mesh contract mismatch")
    for obj in props.values():
        if obj.type != "MESH" or obj.modifiers or obj.animation_data:
            raise ValueError("Props must be rigid unanimated meshes")
        if obj.matrix_basis != Matrix.Identity(4):
            raise ValueError("Prop mesh roots must retain neutral unit transforms")
    return props


def vector_json(value):
    return [round(float(component), 9) for component in value]


def matrix_json(value):
    return [vector_json(row) for row in value]


def transform_json(value):
    position, rotation, scale = value.decompose()
    return {
        "position_m": vector_json(position),
        "rotation_quaternion_xyzw": vector_json((rotation.x, rotation.y, rotation.z, rotation.w)),
        "rotation_euler_xyz_radians": vector_json(rotation.to_euler("XYZ")),
        "scale": vector_json(scale), "matrix_row_major": matrix_json(value),
    }


def attachment(bone, bone_matrix, prop_matrix):
    return {"bone": bone, **transform_json(bone_matrix.inverted() @ prop_matrix)}


def prop_manifest(rig, states, matrices, props):
    definitions = {
        "Magnifier": ("RightHand", states["magnifier"].right, (0, 0, -.13),
                      (0, .045, .018), "power", "Origin at lens centre; circle in XZ, handle down -Z."),
        "Notebook": ("LeftHand", states["notebook"].left, (.075, .015, -.006),
                     (0, .045, .025), "support", "Origin at open spine; blank pages face +Z, width along X."),
        "Pencil": ("RightHand", states["notebook"].right, (0, 0, .045),
                   (0, .045, .020), "pencil", "Origin at graphite tip, shaft extends +Z by .145 m."),
        "PopcornBox": ("LeftHand", states["popcorn"].left, (.060, 0, .055),
                       (0, .045, .018), "support", "Origin at base centre; open top +Z at .15 m."),
        "PopcornKernel": ("RightHand", states["popcorn_pick"].right, (0, 0, 0),
                          (0, .055, .015), "pinch", "Origin at pinch centre, one bite only."),
    }
    result = {}
    for name, (bone, target, prop_point, hand_point, grip, description) in definitions.items():
        matrix = matrices[name]
        primary = attachment(bone, target.matrix(), matrix)
        # Exact reference coincidence is useful even though real VRM finger pad
        # placement still needs per-avatar calibration after skeletal transfer.
        reconstructed = target.matrix() @ Vector(hand_point)
        if (matrix @ Vector(prop_point) - reconstructed).length > 1e-6:
            raise ValueError("Inconsistent attachment grip reference: " + name)
        result[name] = {
            "mesh_root": name, "mesh_children": ["Lens"] if name == "Magnifier" else [],
            "coordinate_system": description, "primary_attachment": primary,
            "grip_references": [{"bone": bone, "mesh_local_point_m": list(prop_point),
                                 "bone_local_point_m": list(hand_point), "finger_profile": grip}],
            "canonical_held_transform_before_facing_yaw": transform_json(matrix),
        }
    head_matrix = rig.data.bones["Head"].matrix_local
    headphones = matrices["Headphones"]
    cups = states["headphones_cups"]
    result["Headphones"] = {
        "mesh_root": "Headphones", "mesh_children": [],
        "coordinate_system": "Origin between ear cups; +Z arch, +/-X cups; one rigid mesh.",
        "primary_attachment": attachment("Head", head_matrix, headphones),
        "carry_attachment": attachment("RightHand", cups.right.matrix(), headphones),
        "grip_references": [],
        "canonical_held_transform_before_facing_yaw": transform_json(headphones),
    }
    for side, sign in SIDES:
        target = cups.left if side == "Left" else cups.right
        point = (sign * .135, 0, -.005)
        hand_point = (0, .045, .015)
        if (headphones @ Vector(point) - target.matrix() @ Vector(hand_point)).length > 1e-6:
            raise ValueError("Inconsistent headphone cup reference")
        result["Headphones"]["grip_references"].append({
            "bone": side + "Hand", "mesh_local_point_m": list(point),
            "bone_local_point_m": list(hand_point), "finger_profile": "cup",
        })
    result["Magnifier"]["clearance_reference"] = {
        "canonical_right_eye_m": [-.04, -.09, 1.68],
        "lens_center_m": [-.065, -.263, 1.67],
        "nominal_forward_clearance_m": .173,
        "note": "Generic guide only; runtime must account for selected VRM face, hair and glasses.",
    }
    result["Pencil"]["tip_mesh_local_m"] = [0, 0, 0]
    result["Pencil"]["eraser_end_mesh_local_m"] = [0, 0, .145]
    result["PopcornKernel"]["mouth_target_before_facing_yaw_m"] = [-.03, -.16, 1.602]
    result["Headphones"]["ear_cup_centres_mesh_local_m"] = [[-.093, 0, -.005], [.093, 0, -.005]]
    result["Headphones"]["nominal_inner_cup_gap_m"] = .172
    for name, record in result.items():
        obj = props[name]
        record["mesh_bounds_m"] = {
            "min": [round(min(v.co[i] for v in obj.data.vertices), 9) for i in range(3)],
            "max": [round(max(v.co[i] for v in obj.data.vertices), 9) for i in range(3)],
        }
        record["vertices"] = len(obj.data.vertices)
        record["polygons"] = len(obj.data.polygons)
        record["materials"] = [mat.name for mat in obj.data.materials]
        obj["primary_attachment_bone"] = record["primary_attachment"]["bone"]
    result["Lens"] = {
        "mesh_root": "Magnifier", "mesh_name": "Lens", "parent_mesh": "Magnifier",
        "local_transform_to_parent": transform_json(Matrix.Identity(4)),
        "primary_attachment": result["Magnifier"]["primary_attachment"],
        "grip_references": result["Magnifier"]["grip_references"],
        "materials": ["VisionLensTransparent"],
        "runtime_shader": "Transparent lens, alpha approximately .16; no screen texture or magnification.",
    }
    return result


def clip_manifest(paths):
    records = []
    event_specs = {
        "MagnifierEquip": [(.22, "attach_show", "Magnifier", "primary_attachment")],
        "MagnifierStow": [(.72, "hide_detach", "Magnifier", "primary_attachment")],
        "NotebookEquip": [(.22, "attach_show", "Notebook", "primary_attachment"),
                           (.22, "attach_show", "Pencil", "primary_attachment")],
        "NotebookStow": [(.78, "hide_detach", "Notebook", "primary_attachment"),
                          (.78, "hide_detach", "Pencil", "primary_attachment")],
        "HeadphonesEquip": [(.16, "attach_show", "Headphones", "carry_attachment"),
                             (.72, "reparent", "Headphones", "primary_attachment")],
        "HeadphonesStow": [(.28, "reparent", "Headphones", "carry_attachment"),
                            (.84, "hide_detach", "Headphones", "carry_attachment")],
        "PopcornEquip": [(.22, "attach_show", "PopcornBox", "primary_attachment")],
        "PopcornStow": [(0, "hide_detach", "PopcornKernel", "primary_attachment"),
                         (.78, "hide_detach", "PopcornBox", "primary_attachment")],
        "PopcornBite": [(.20, "attach_show", "PopcornKernel", "primary_attachment"),
                         (.55, "hide_detach", "PopcornKernel", "primary_attachment")],
        "PopcornPause": [(.20, "attach_show", "PopcornKernel", "primary_attachment"),
                          (.84, "hide_detach", "PopcornKernel", "primary_attachment")],
    }
    endpoints = {
        "MagnifierEquip": ("Neutral", "MagnifierRaised"),
        "MagnifierHold": ("MagnifierRaised", "MagnifierRaised"),
        "MagnifierLower": ("MagnifierRaised", "MagnifierChest"),
        "MagnifierRaise": ("MagnifierChest", "MagnifierRaised"),
        "MagnifierStow": ("MagnifierChest", "Neutral"),
        "NotebookEquip": ("Neutral", "NotebookHeld"),
        "NotebookStow": ("NotebookHeld", "Neutral"),
        "HeadphonesEquip": ("Neutral", "HeadphonesWorn"),
        "HeadphonesStow": ("HeadphonesWorn", "Neutral"),
        "PopcornEquip": ("Neutral", "PopcornHeld"),
        "PopcornStow": ("PopcornHeld", "Neutral"),
        "ChatHold": ("ChatCheek", "ChatCheek"), "WatchHold": ("Neutral", "Neutral"),
    }
    for name, duration in CLIPS:
        end_frame = round(duration * FPS)
        if name in endpoints:
            start, end = endpoints[name]
        elif name.startswith("Notebook"):
            start = end = "NotebookHeld"
        elif name.startswith("Headphones"):
            start = end = "HeadphonesWorn"
        elif name.startswith("Popcorn"):
            start = end = "PopcornHeld"
        else:
            start = end = "Neutral"
        events = []
        for fraction, operation, prop, attachment_name in event_specs.get(name, []):
            frame = round(fraction * end_frame)
            events.append({"frame": frame, "seconds": frame / FPS,
                           "operation": operation, "prop": prop,
                           "attachment_profile": attachment_name})
        records.append({
            "name": name, "duration_seconds": duration, "frame_start": 0,
            "frame_end": end_frame, "fps": FPS, "loop": name.endswith("Hold"),
            "start_pose": start, "end_pose": end,
            "root_motion": False, "events": events,
            "gesture_stop_frames": sorted({round(t * end_frame) for t, _ in paths.get(name, [])}),
            "runtime_mask": (["Chest", "Neck", "Head"] if name in EXPRESSIONS else "upper_body_and_fingers"),
            "expression_reference": "WatchHold@0" if name in EXPRESSIONS else None,
            "face_shapes": False,
        })
    return records


def build_manifest(rig, actions, props, states, matrices, paths):
    rest = []
    for bone in rig.data.bones:
        rest.append({
            "name": bone.name, "human_body_bone": bone.name,
            "parent": bone.parent.name if bone.parent else None,
            "head_m": vector_json(bone.head_local), "tail_m": vector_json(bone.tail_local),
            "armature_space_rest_matrix_row_major": matrix_json(bone.matrix_local),
            "parent_local_rest_transform": transform_json(
                bone.parent.matrix_local.inverted() @ bone.matrix_local
                if bone.parent else bone.matrix_local),
        })
    prop_records = prop_manifest(rig, states, matrices, props)
    return {
        "schema": "astra.vision.authoring.v1", "generator": "author_vision.py",
        "blender_version": bpy.app.version_string,
        "outputs": list(OUTPUT_NAMES), "animation_only": True, "avatar_mesh": None,
        "units": "metres", "fps": FPS, "source_key_step_frames": KEY_STEP,
        "fbx_bake_step_frames": BAKE_STEP,
        "fbx": {"axis_forward": "Y", "axis_up": "Z", "use_space_transform": False,
                "apply_unit_scale": True, "apply_scale_options": "FBX_SCALE_ALL",
                "animation_object_types": ["ARMATURE"], "prop_object_types": ["MESH"],
                "add_leaf_bones": False, "all_actions": True, "nla_strips": False,
                "simplify_factor": 0, "force_start_end_keying": True,
                "take_naming": "Action names below are authoritative. Native importer may expose standard VisionRig|Action FBX take prefixes; normalize only that prefix."},
        "coordinates": {
            "blender_up": "+Z", "canonical_front_before_facing_yaw": "-Y",
            "canonical_left_before_facing_yaw": "+X",
            "fixed_hips_local_y_yaw_radians": -math.pi,
            "fixed_facing_world_quaternion_xyzw": vector_json((YAW.x, YAW.y, YAW.z, YAW.w)),
            "rest_convention": "Original companion T-rest and leg chain; every action uses the same constant -pi Hips yaw. No object/root/leg displacement.",
            "prop_local_transforms": "Bone-local Blender rest/pose axes, metres, right-handed. These are NOT already Unity local transforms. Apply the importer bone-basis conversion and calibrate selected VRM hands/head; do not copy Euler components blindly.",
            "prop_library": "All six mesh roots have identity object transforms at the origin; split native prefab by exact mesh root name. Lens is an identity-transform mesh child of Magnifier. No Empty roots.",
        },
        "rest_bone_names": [bone.name for bone in rig.data.bones], "rest_bones": rest,
        "human_mapping": {bone.name: bone.name for bone in rig.data.bones},
        "clip_names": list(actions), "clips": clip_manifest(paths), "props": prop_records,
        "runtime_contract": {
            "avatar": "Retarget this generic animation-only Human skeleton to the existing selected VRM; never replace it.",
            "seating": "Exclude Hips, legs, feet, toes and root from the animation layer. Blend only upper body and fingers over native seating; retain seating contacts.",
            "magnifier_stow": "Lower to MagnifierChest first, then MagnifierStow. Raise reverses Lower exactly.",
            "routine_switch": "Cancel accent, blend to its routine held pose, finish stow-before-equip. On interruption restore or hide transient bite props and resolve headphones hand/head parent before resuming.",
            "chat_watch": "Use runtime crossfades into/out of ChatHold and WatchHold; these are holds, not instantaneous neutral entry transitions.",
            "expressions": "Smile/Laugh/Surprise/Think/Concern are restrained body/head accents with neutral endpoints. Additive reference WatchHold@0, mask Chest/Neck/Head only while holding props. Face shapes remain runtime-owned.",
            "headphones_handoff": "Use carry_attachment on RightHand while lifting/removing, then primary_attachment on Head. Both cup grips coincide at handoff; left hand is a secondary contact, never a second parent. Preserve world pose during calibrated reparenting.",
            "prop_visibility": "Events describe presentation, not authored prop animation. Initial props hidden; reconstruct held visibility on direct seek/resume. Notebook and pencil equip/stow together. Kernel is visible only during Bite/Pause event intervals.",
            "notebook": "Four short 12 mm pencil strokes with lifted returns; no actual notes are generated. NotebookThink brings pencil end to chin and returns.",
            "grips": "Grip references are nominal palmar/pinch anchors. Articulated fingers are authored, but finger pad contacts and hand-size offsets require selected-avatar calibration.",
            "lens": "Use transparent runtime shader on Lens, not a screen-capture/magnification surface. Calibrate right eye/hair clearance and preserve face readability.",
            "priority": "Manual dance/portrait/drag/menu and safety/privacy suspension must override these clips; runtime owns look targets and clipping avoidance.",
        },
        "verification": {
            "source_only_until_executed": True,
            "generation_invariants": ["26 actions with exact 60 Hz frame ranges", "51 Human-named bones",
                                      "analytic wrist reach and reconstruction", "matching routine and loop endpoints",
                                      "static root and lower body", "exact nominal grip reference coincidence",
                                      "seven rigid named meshes and six prop roots"],
            "not_verified_by_generation": ["Unity Human avatar validity after FBX import",
                                           "retargeted selected VRM fidelity and finger contact",
                                           "lens/head/hair and window clearance",
                                           "transparent runtime lens shader", "seated contact in native runtime",
                                           "rendered appearance or actual facial expressions"],
        },
    }


def selected(objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]


def export_assets(stage, rig, props, manifest):
    scene = bpy.context.scene
    scene.frame_start, scene.frame_end = 0, max(round(duration * FPS) for _, duration in CLIPS)
    scene.frame_set(0)
    rig.animation_data.action = None
    reset_pose(rig, facing=False)
    selected([rig])
    # Save the neutral skeleton and all fake-user Actions, never a character mesh.
    # Meshes are a deliberately co-origin library; no staged transforms leak into FBX.
    bpy.context.preferences.filepaths.save_version = 0
    result = bpy.ops.wm.save_as_mainfile(filepath=str(stage / "Vision.blend"), check_existing=False)
    if "FINISHED" not in result:
        raise RuntimeError("Vision.blend save did not finish")
    common = {
        "use_selection": True, "axis_forward": "Y", "axis_up": "Z",
        "use_space_transform": False, "apply_unit_scale": True,
        "apply_scale_options": "FBX_SCALE_ALL", "global_scale": 1.0,
        "use_custom_props": True, "path_mode": "AUTO", "embed_textures": False,
    }
    result = bpy.ops.export_scene.fbx(
        filepath=str(stage / "VisionRest.fbx"), object_types={"ARMATURE"},
        add_leaf_bones=False, use_armature_deform_only=True, armature_nodetype="NULL",
        bake_anim=False, **common)
    if "FINISHED" not in result:
        raise RuntimeError("Neutral bind-reference FBX export did not finish")
    result = bpy.ops.export_scene.fbx(
        filepath=str(stage / "Vision.fbx"), object_types={"ARMATURE"},
        add_leaf_bones=False, use_armature_deform_only=True, armature_nodetype="NULL",
        bake_anim=True, bake_anim_use_all_bones=True, bake_anim_use_all_actions=True,
        bake_anim_use_nla_strips=False, bake_anim_simplify_factor=0,
        bake_anim_step=BAKE_STEP, bake_anim_force_startend_keying=True, **common)
    if "FINISHED" not in result:
        raise RuntimeError("Animation FBX export did not finish")
    selected(list(props.values()))
    result = bpy.ops.export_scene.fbx(
        filepath=str(stage / "VisionProps.fbx"), object_types={"MESH"},
        bake_anim=False, use_mesh_modifiers=False, mesh_smooth_type="FACE", **common)
    if "FINISHED" not in result:
        raise RuntimeError("Prop FBX export did not finish")
    manifest["generated_artifacts"] = {}
    for filename in OUTPUT_NAMES[:-1]:
        path = stage / filename
        if not path.is_file() or path.stat().st_size == 0:
            raise RuntimeError("Missing/empty output: " + filename)
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        manifest["generated_artifacts"][filename] = {
            "bytes": path.stat().st_size, "sha256": digest.hexdigest(),
        }
    with (stage / "manifest.json").open("x", encoding="utf-8") as stream:
        json.dump(manifest, stream, indent=2, allow_nan=False)
        stream.write("\n")


def claim_output(raw):
    path = Path(raw).expanduser()
    if not path.is_absolute():
        raise ValueError("--output must be an absolute absent or empty owned directory")
    if ".." in path.parts:
        raise ValueError("--output must not contain '..'")
    for ancestor in (path, *path.parents):
        if ancestor.is_symlink():
            raise ValueError("Refusing symlink output path/ancestor: " + str(ancestor))
    if path.exists():
        if not path.is_dir() or path.stat().st_uid != os.geteuid():
            raise ValueError("Output must be a directory owned by the invoking user")
        if any(path.iterdir()):
            raise ValueError("Refusing nonempty output directory; no unknown outputs may be overwritten")
    else:
        path.mkdir(mode=0o700, parents=True, exist_ok=False)
    lock = path / ".vision-authoring.lock"
    with lock.open("x", encoding="utf-8") as stream:
        stream.write("author_vision.py owns this previously empty output directory\n")
    identity = lock.stat()
    if any(entry.name != lock.name for entry in path.iterdir()):
        lock.unlink()
        raise ValueError("Output changed while being claimed")
    return path, lock, (identity.st_dev, identity.st_ino)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    args = parser.parse_args(argv)
    output, lock, lock_identity = claim_output(args.output)
    try:
        scene = bpy.context.scene
        scene.render.fps, scene.render.fps_base = FPS, 1.0
        scene.unit_settings.system = "METRIC"
        scene.unit_settings.scale_length = 1.0
        states, matrices = build_states()
        paths = clip_paths(states)
        rig = make_rig()
        actions, endpoints = author_actions(rig, states, paths)
        verify_contract(rig, actions, endpoints)
        props = make_props()
        manifest = build_manifest(rig, actions, props, states, matrices, paths)
        # Native writers only touch newly created private staging paths. Hard-link
        # promotion is exclusive (unlike rename/replace) and cannot overwrite a
        # file that another writer puts in the destination during generation.
        with tempfile.TemporaryDirectory(prefix=".vision-stage-", dir=output) as temporary:
            stage = Path(temporary)
            export_assets(stage, rig, props, manifest)
            unexpected = [entry.name for entry in output.iterdir()
                          if entry.name not in {lock.name, stage.name}]
            if unexpected:
                raise RuntimeError("Output changed during authoring; refusing publication: " + repr(unexpected))
            for filename in OUTPUT_NAMES:
                os.link(stage / filename, output / filename)
        print(f"Authored {len(actions)} actions, {len(rig.data.bones)} bones and {len(props)} meshes in {output}")
        print("FBX generation is not a Unity import, selected-VRM calibration or render verification.")
    finally:
        if lock.exists() and not lock.is_symlink():
            identity = lock.stat()
            if (identity.st_dev, identity.st_ino) == lock_identity:
                lock.unlink()


if __name__ == "__main__":
    main()
