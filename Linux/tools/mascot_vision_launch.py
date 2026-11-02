#!/usr/bin/env python3
"""Keep the opt-in observer inside the selected player's lifecycle and profile."""
import argparse
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

from mascot_vision import load_config


def stop(process):
    if process.poll() is not None:
        return
    os.killpg(process.pid, signal.SIGTERM)
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=2)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--player', type=Path, required=True)
    parser.add_argument('--config', type=Path, required=True)
    parser.add_argument('arguments', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    home = Path.home()
    if not (home / '.mateengine-profile').is_file() or home.is_symlink():
        parser.error('Vision requires the selected independent mascot profile')
    config = load_config(args.config)
    state = home / 'player-data' / 'vision-state.json'
    expected = os.environ.get('MATEENGINE_VISION_STATE')
    if expected and Path(expected).absolute() != state:
        parser.error('Vision state must remain in the selected mascot profile')
    environment = dict(os.environ, MATEENGINE_VISION_STATE=str(state))
    arguments = args.arguments[1:] if args.arguments[:1] == ['--'] else args.arguments
    player = subprocess.Popen([str(args.player.resolve(strict=True)), *arguments], env=environment, start_new_session=True)
    observer = None
    running = True

    def requested_stop(_signal, _frame):
        nonlocal running
        running = False

    signal.signal(signal.SIGINT, requested_stop)
    signal.signal(signal.SIGTERM, requested_stop)
    try:
        tools = Path(__file__).parent
        command = [sys.executable, str(tools / 'mascot_vision.py'), '--config', str(args.config),
                   '--state', str(state), '--capture-helper', str(tools / 'vision-toplevels'),
                   '--interval', str(config.get('intervalSeconds', 20))]
        if environment.get('MATEENGINE_VISION_WINDOW'):
            command += ['--window', environment['MATEENGINE_VISION_WINDOW']]
        observer = subprocess.Popen(command, env=environment, start_new_session=True)
        while running and player.poll() is None:
            time.sleep(.1)
        return player.returncode if player.returncode is not None else 0
    finally:
        if observer is not None:
            stop(observer)
        stop(player)


if __name__ == '__main__':
    raise SystemExit(main())
