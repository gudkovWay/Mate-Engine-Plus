#!/usr/bin/env python3
"""Normalize verified Blender exports for the existing Unity import pipeline."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil


def vector(value):
    return dict(zip(('x', 'y', 'z'), value))


def rotation(value):
    return dict(zip(('x', 'y', 'z', 'w'), value))


def attachment(value):
    return dict(bone=value['bone'], position=vector(value['position_m']),
                rotation=rotation(value['rotation_quaternion_xyzw']))


def prepare(source, output):
    source = source.resolve(strict=True)
    if output.exists():
        raise ValueError('Refusing existing native vision asset directory')
    manifest = json.loads((source / 'manifest.json').read_text())
    if manifest['schema'] != 'astra.vision.authoring.v1' or len(manifest['clips']) != 26:
        raise ValueError('Incomplete Blender action contract')
    for name in ('Vision.fbx', 'VisionProps.fbx', 'VisionRest.fbx'):
        data = (source / name).read_bytes()
        if hashlib.sha256(data).hexdigest() != manifest['generated_artifacts'][name]['sha256']:
            raise ValueError('Changed authored export: ' + name)
    props = []
    for name, item in manifest['props'].items():
        if name == 'Lens':
            continue
        main = attachment(item['primary_attachment'])
        carry = attachment(item['carry_attachment']) if 'carry_attachment' in item else None
        grips = item.get('grip_references', [])
        grip = next((entry for entry in grips if entry['bone'] == main['bone']), None)
        lower, upper = item['mesh_bounds_m']['min'], item['mesh_bounds_m']['max']
        size = vector([high - low for low, high in zip(lower, upper)])
        props.append(dict(name=name, bone=main['bone'], position=main['position'], rotation=main['rotation'], size=size,
                          carryBone=carry['bone'] if carry else '',
                          carryPosition=carry['position'] if carry else dict(x=0, y=0, z=0),
                          carryRotation=carry['rotation'] if carry else dict(x=0, y=0, z=0, w=1),
                          hasGrip=grip is not None,
                          boneGrip=vector(grip['bone_local_point_m']) if grip else dict(x=0, y=0, z=0),
                          meshGrip=vector(grip['mesh_local_point_m']) if grip else dict(x=0, y=0, z=0)))
    clips = []
    for item in manifest['clips']:
        events = [dict(time=event['seconds'], operation=event['operation'], prop=event['prop'],
                       carry=event['attachment_profile'] == 'carry_attachment') for event in item['events']]
        clips.append(dict(name=item['name'], seconds=item['duration_seconds'], loop=item['loop'], events=events))
    output.mkdir(parents=True)
    for name in ('Vision.fbx', 'VisionProps.fbx'):
        shutil.copy2(source / name, output / name)
    (output / 'Editor').mkdir()
    shutil.copy2(source / 'VisionRest.fbx', output / 'Editor/VisionRest.fbx')
    rest = [dict(name=bone['name'], matrix=[value for row in bone['armature_space_rest_matrix_row_major'] for value in row])
            for bone in manifest['rest_bones']]
    (output / 'VisionUnity.json').write_text(json.dumps(dict(bones=manifest['rest_bone_names'], rest=rest,
        clips=clips, props=props), indent=2) + '\n')
    print(json.dumps(dict(clips=len(clips), props=len(props), output=str(output))))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    prepare(args.source, args.output)
