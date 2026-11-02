#!/usr/bin/env python3
"""Generate the authored companion assets before Unity imports a prepared project."""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile

from prepare_vision_assets import prepare

ROOT = Path(__file__).resolve().parents[1]


def generate(project, blender='blender'):
    project = project.resolve(strict=True)
    resources = project / 'Assets/ShojiLinux/Resources'
    motion = resources / 'CompanionMotion'
    vision = resources / 'CompanionVision'
    if motion.exists() or vision.exists():
        raise ValueError('Refusing existing companion resources; preserve or explicitly remove owned generated outputs first')
    temporary = project / 'Temp'
    temporary.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='companion-authoring-', dir=temporary) as directory:
        stage = Path(directory)
        for name in ('companion', 'vision'):
            subprocess.run([blender, '--background', '--factory-startup', '--threads', '1',
                            '--python', str(ROOT / f'animation-src/author_{name}.py'), '--',
                            '--output', str(stage / name)], check=True, timeout=180)
        motion.mkdir(parents=True)
        shutil.copy2(stage / 'companion/Companion.fbx', motion / 'Companion.fbx')
        prepare(stage / 'vision', vision)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('project', type=Path)
    parser.add_argument('--blender', default='blender')
    args = parser.parse_args()
    generate(args.project, args.blender)
