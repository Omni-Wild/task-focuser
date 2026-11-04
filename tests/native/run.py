#!/usr/bin/env python3
"""Exercise the real layer-shell app in an isolated, software-rendered compositor."""

import argparse
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import time


def wait_for(description, check, processes=(), timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        for process in processes:
            if process.poll() is not None:
                raise RuntimeError(f"{description}: process exited with {process.returncode}")
        result = check()
        if result:
            return result
        time.sleep(0.05)
    raise RuntimeError(f"Timed out waiting for {description}")


def state_at(path):
    try:
        return json.loads(path.read_text())
    except FileNotFoundError:
        return {}


class NativeTest:
    def __init__(self, binary, artifacts):
        self.binary = str(binary)
        self.artifacts = artifacts
        self.directory = Path(tempfile.mkdtemp(prefix="tfn-"))
        self.runtime = self.directory / "r"
        self.runtime.mkdir(mode=0o700)
        self.data = self.directory / "d"
        self.data.mkdir()
        self.processes = []
        self.files = []
        self.frames = {}
        self.pointer_sequence = 0
        self.env = os.environ.copy()
        for key in ("WAYLAND_DISPLAY", "WAYLAND_SOCKET", "DISPLAY", "HYPRLAND_INSTANCE_SIGNATURE", "SWAYSOCK", "XDG_SESSION_ID"):
            self.env.pop(key, None)
        self.env.update(
            XDG_RUNTIME_DIR=str(self.runtime),
            XDG_CONFIG_HOME=str(self.directory / "config"),
            XDG_CACHE_HOME=str(self.directory / "cache"),
            XDG_DATA_HOME=str(self.directory / "data"),
            WLR_BACKENDS="headless",
            WLR_RENDERER="pixman",
            WLR_LIBINPUT_NO_DEVICES="1",
            WLR_HEADLESS_OUTPUTS="1",
            TASKFOCUSER_DATA_DIR=str(self.data),
            TASKFOCUSER_PROFILE="native-a",
            ICED_BACKEND="tiny-skia",
        )

    def run(self, *args, env=None):
        return subprocess.run(args, env=env or self.env, check=True,
                              capture_output=True, text=True, timeout=15).stdout

    def type_keys(self, *keys):
        # A new virtual keyboard must be bound before it starts sending keys.
        self.run("wtype", "-s", "250", *keys)

    def move_cursor(self, x, y):
        outputs = json.loads(self.run("swaymsg", "-r", "-t", "get_outputs"))
        active = [output["rect"] for output in outputs if output["active"]]
        # The virtual pointer protocol uses the output layout's absolute extent.
        origin_x = min(output["x"] for output in active)
        origin_y = min(output["y"] for output in active)
        width = max(output["x"] + output["width"] for output in active) - origin_x
        height = max(output["y"] + output["height"] for output in active) - origin_y
        self.pointer_command(f"move {round(x - origin_x)} {round(y - origin_y)} {width} {height}")

    def pointer_command(self, command):
        self.pointer.stdin.write((command + "\n").encode())
        self.pointer.stdin.flush()
        self.pointer_sequence += 1
        wait_for("virtual pointer event acknowledged", lambda: f"done {self.pointer_sequence}\n" in
                 (self.artifacts / "pointer.log").read_text(), [self.pointer])

    def spawn(self, name, *args, env=None, stdin=None):
        output = (self.artifacts / f"{name}.log").open("w")
        self.files.append(output)
        process = subprocess.Popen(args, env=env or self.env, stdin=stdin, stdout=output, stderr=output)
        self.processes.append(process)
        return process

    def attach_pointer(self):
        source = Path(__file__).resolve().parent
        protocol = source / "wlr-virtual-pointer-unstable-v1.xml"
        header = self.directory / "virtual-pointer.h"
        code = self.directory / "virtual-pointer.c"
        self.run("wayland-scanner", "client-header", str(protocol), str(header))
        self.run("wayland-scanner", "private-code", str(protocol), str(code))
        flags = shlex.split(self.run("pkg-config", "--cflags", "--libs", "wayland-client"))
        binary = self.directory / "pointer"
        self.run("cc", "-Wall", "-Wextra", "-Werror", "-I", str(self.directory),
                 str(source / "pointer.c"), str(code), "-o", str(binary), *flags)
        pointer = self.spawn("pointer", str(binary), stdin=subprocess.PIPE)
        self.pointer = pointer
        wait_for("persistent virtual pointer", lambda: "ready" in
                 (self.artifacts / "pointer.log").read_text(), [pointer])

    def command(self, command, profile="native-a"):
        env = dict(self.env, TASKFOCUSER_PROFILE=profile)
        self.run(self.binary, command, env=env)

    def expect_state(self, profile="native-a", **values):
        path = self.data / f"state.{profile}.json"
        return wait_for(f"{profile} state {values}",
                        lambda: all(state_at(path).get(k) == v for k, v in values.items()),
                        self.processes)

    def trace(self):
        return (self.artifacts / "app-a.log").read_text(errors="replace")

    def note_keyboard(self):
        trace = self.trace()
        notes = re.findall(r"get_layer_surface\(new id zwlr_layer_surface_v1[@#](\d+),[^\n]*"
                           r'"task-focuser-note"\)', trace)
        if not notes:
            return None
        modes = re.findall(r"zwlr_layer_surface_v1[@#]" + notes[-1]
                           + r"\.set_keyboard_interactivity\((\d+)\)", trace)
        return int(modes[-1]) if modes else None

    def note_frame_counts(self):
        trace = self.trace()
        surfaces = re.findall(r"get_layer_surface\(new id zwlr_layer_surface_v1[@#]\d+,\s*"
                              r'wl_surface[@#](\d+),[^\n]*"task-focuser-note"\)', trace)
        if not surfaces:
            raise RuntimeError("The note's Wayland surface was not recorded")
        prefix = r"wl_surface[@#]" + surfaces[-1]
        return {
            "attachments": len(re.findall(prefix + r"\.attach\(wl_buffer", trace)),
            "commits": len(re.findall(prefix + r"\.commit\(\)", trace)),
        }

    def screenshot(self, name, output="HEADLESS-1"):
        # Raw pixels let us assert that the compositor really received a frame.
        path = self.artifacts / f"{name}.ppm"
        self.run("grim", "-o", output, "-t", "ppm", str(path))
        content = path.read_bytes()
        header = re.match(rb"P6\s+(\d+)\s+(\d+)\s+255\s", content)
        if not header:
            raise RuntimeError("grim returned an unsupported PPM image")
        width, height = map(int, header.groups())
        if (width, height) != (1280, 800):
            raise RuntimeError(f"Unexpected virtual output size: {width}x{height}")
        pixels = content[header.end():]
        if len(pixels) != width * height * 3:
            raise RuntimeError("Screenshot pixel data is truncated")
        self.run("grim", "-o", output, str(self.artifacts / f"{name}.png"))
        path.unlink()
        colored = [i // 3 for i in range(0, len(pixels), 3) if any(pixels[i:i + 3])]
        self.frames[name] = {
            "pixels": len(colored),
            "bounds": ([min(i % width for i in colored), min(i // width for i in colored),
                        max(i % width for i in colored), max(i // width for i in colored)]
                       if colored else None),
        }
        (self.artifacts / "frames.json").write_text(json.dumps(self.frames, indent=2) + "\n")
        return len(colored)

    def start(self):
        config = self.directory / "sway.conf"
        config.write_text("output HEADLESS-1 mode 1280x800\n"
                          "xwayland disable\nseat seat0 fallback true\n")
        sway = self.spawn("sway", "sway", "--unsupported-gpu", "-c", str(config))
        socket = wait_for("isolated Sway IPC", lambda: next(self.runtime.glob("sway-ipc*.sock"), None), [sway])
        self.env["SWAYSOCK"] = str(socket)
        wayland = wait_for("isolated Wayland socket", lambda: next(
            (path for path in self.runtime.glob("wayland-*") if path.is_socket()), None), [sway])
        self.env["WAYLAND_DISPLAY"] = wayland.name
        self.run("swaymsg", "-r", "-t", "get_outputs")
        self.attach_pointer()
        # Keep the virtual pointer away from controls and screenshot assertions.
        self.move_cursor(1200, 700)
        if self.screenshot("00-empty") != 0:
            raise RuntimeError("The isolated output's initial background is not black")
        self.data.joinpath("state.native-a.json").write_text(json.dumps({
            "version": 2, "text": "Native initial text", "x": 100, "y": 100,
            "mode": "note", "locked": False, "soundOn": False,
            "plantsOn": False, "birdStays": False,
        }))
        self.app = self.spawn("app-a", self.binary, "--backend", "wayland", "--no-tray", "--no-sound",
                              "--show", env=dict(self.env, WAYLAND_DEBUG="client"))
        wait_for("a committed note layer", lambda: '"task-focuser-note"' in self.trace()
                 and re.search(r"wl_surface[@#]\d+\.attach\(", self.trace()), self.processes)
        time.sleep(0.4)

    def exercise(self):
        self.start()
        note_pixels = self.screenshot("01-note")
        if note_pixels < 1000:
            raise RuntimeError(f"The note did not render enough pixels: {note_pixels}")
        notes = re.findall(r"get_layer_surface\(new id zwlr_layer_surface_v1[@#](\d+),[^\n]*"
                           r'"task-focuser-note"\)', self.trace())
        if not notes:
            raise RuntimeError("The note layer was not recorded")
        prefix = r"zwlr_layer_surface_v1[@#]" + notes[-1]
        margins = re.findall(prefix + r"\.set_margin\((-?\d+),\s*(-?\d+),\s*(-?\d+),\s*(-?\d+)\)", self.trace())
        sizes = re.findall(prefix + r"\.set_size\((\d+),\s*(\d+)\)", self.trace())
        if not margins or tuple(map(int, margins[-1])) != (88, 0, 0, 88):
            raise RuntimeError(f"The note did not map saved coordinates to output margins: {margins}")
        if not sizes or any(88 + value > limit for value, limit in zip(map(int, sizes[-1]), (1280, 800))):
            raise RuntimeError(f"The initial note does not fit its output: {sizes}")
        if not re.search(r"\.set_anchor\(5\)", self.trace()):
            raise RuntimeError("The note did not request top-left output positioning")

        # This goes through virtual-keyboard protocol, the real editor, and disk IO.
        self.type_keys("-M", "ctrl", "a", "-m", "ctrl", "Native edited text")
        self.expect_state(text="Native edited text")
        self.type_keys("-M", "ctrl", "z", "-m", "ctrl")
        self.expect_state(text="Native initial text")
        self.type_keys("-M", "ctrl", "y", "-m", "ctrl")
        self.expect_state(text="Native edited text")
        long_url = "Long URL https://example.invalid/" + "unbroken" * 40
        self.type_keys("-M", "ctrl", "a", "-m", "ctrl", long_url)
        self.expect_state(text=long_url)
        self.screenshot("01-long-url")
        bounds = self.frames["01-long-url"]["bounds"]
        if bounds[2] >= 1279 or bounds[3] >= 799:
            raise RuntimeError(f"The long URL overflowed the output: {bounds}")
        if bounds[3] < self.frames["01-note"]["bounds"][3] + 20:
            raise RuntimeError("The long, unbroken URL did not expand into wrapped lines")
        self.type_keys("-M", "ctrl", "a", "-m", "ctrl", "Native edited text")
        self.expect_state(text="Native edited text")
        self.command("--lock")
        self.expect_state(locked=True)
        wait_for("locked note keyboard mode", lambda: self.note_keyboard() == 0, self.processes)
        self.type_keys("SHOULD_NOT_EDIT")
        time.sleep(0.5)
        if state_at(self.data / "state.native-a.json").get("text") != "Native edited text":
            raise RuntimeError("The locked note accepted keyboard input")
        self.screenshot("02-locked")
        # Measure real note frames after the companion's departure has settled.
        time.sleep(1)
        before = self.note_frame_counts()
        time.sleep(1.2)
        after = self.note_frame_counts()
        idle = {key: after[key] - before[key] for key in before}
        idle["seconds"] = 1.2
        (self.artifacts / "locked-idle.json").write_text(json.dumps(idle, indent=2) + "\n")
        if idle["attachments"] > 2:
            raise RuntimeError(f"The locked, idle note kept redrawing: {idle}")
        self.command("--unlock")
        self.expect_state(locked=False)
        wait_for("unlocked note keyboard mode", lambda: self.note_keyboard() == 2, self.processes)
        self.type_keys("-M", "ctrl", "a", "-m", "ctrl", "Native persisted text")
        self.expect_state(text="Native persisted text")

        self.command("--dot")
        self.expect_state(mode="dot")
        wait_for("dot layer", lambda: '"task-focuser-dot"' in self.trace(), self.processes)
        time.sleep(0.3)
        dot_pixels = self.screenshot("03-dot")
        if not 20 < dot_pixels < note_pixels:
            raise RuntimeError(f"Unexpected dot render size: {dot_pixels}, note: {note_pixels}")
        self.command("--close")
        self.expect_state(mode="closed")
        time.sleep(0.3)
        wait_for("closed output cleared after departure", lambda: self.screenshot("04-closed") <= 100,
                 self.processes, timeout=5)
        self.command("--start")
        self.expect_state(mode="dot")
        self.command("--show")
        self.expect_state(mode="note", text="Native persisted text")

        # A separate profile must acquire its own primary instance and state file.
        self.other = self.spawn("app-b", self.binary, "--backend", "wayland", "--no-tray", "--no-sound", "--close",
                                env=dict(self.env, TASKFOCUSER_PROFILE="native-b"))
        self.expect_state("native-b", mode="closed", text="")
        self.command("--lock", "native-b")
        self.expect_state("native-b", locked=True)
        self.expect_state(locked=False, text="Native persisted text", mode="note")
        self.command("--quit", "native-b")
        if self.other.wait(timeout=15) != 0:
            raise RuntimeError("The second profile did not quit successfully")
        self.processes.remove(self.other)

        self.command("--quit")
        if self.app.wait(timeout=15) != 0:
            raise RuntimeError("The primary app did not quit successfully")
        self.processes.remove(self.app)
        self.app = self.spawn("app-restarted", self.binary, "--backend", "wayland", "--no-tray", "--no-sound", "--show",
                              env=dict(self.env, WAYLAND_DEBUG="client"))
        self.expect_state(text="Native persisted text", mode="note", locked=False)
        time.sleep(0.4)
        if self.screenshot("05-restarted") < 1000:
            raise RuntimeError("The restarted note did not render")
        self.cross_monitor_drag()
        self.command("--quit")
        if self.app.wait(timeout=15) != 0:
            raise RuntimeError("The restarted app did not quit successfully")
        self.processes.remove(self.app)

    def cross_monitor_drag(self):
        self.run("swaymsg", "create_output")
        self.run("swaymsg", "output HEADLESS-2 mode 1280x800 position -1280 0")
        outputs = json.loads(self.run("swaymsg", "-r", "-t", "get_outputs"))
        if not any(output["name"] == "HEADLESS-2" and output["rect"]["x"] == -1280 for output in outputs):
            raise RuntimeError("The second virtual output has the wrong origin")
        # The host refreshes logical output bounds every two seconds.
        time.sleep(2.2)
        state = state_at(self.data / "state.native-a.json")
        start_x, start_y = state["x"] + 100, state["y"] + 3
        self.move_cursor(start_x, start_y)
        time.sleep(0.15)
        self.pointer_command("press")
        time.sleep(0.15)
        self.move_cursor(-100, 180)
        time.sleep(0.2)
        if (self.screenshot("06-straddle-right", "HEADLESS-1") < 5000
                or self.screenshot("06-straddle-left", "HEADLESS-2") < 5000):
            raise RuntimeError("The dragged note was clipped while straddling outputs")
        self.move_cursor(-600, 200)
        time.sleep(0.2)
        self.move_cursor(-700, 240)
        time.sleep(0.2)
        self.pointer_command("release")
        wait_for("cross-output drag persisted after release", lambda: (
            abs(state_at(self.data / "state.native-a.json").get("x", 0) + 800) < 1
            and abs(state_at(self.data / "state.native-a.json").get("y", 0) - 237) < 1
        ), self.processes)
        if self.screenshot("07-negative-output", "HEADLESS-2") < 1000:
            raise RuntimeError("The dragged note did not render on the negative-origin output")
        self.expect_state(text="Native persisted text", mode="note", locked=False)
        self.type_keys("-M", "ctrl", "a", "-m", "ctrl", "Native after drag text")
        self.expect_state(text="Native after drag text", mode="note", locked=False)

    def close(self):
        for process in reversed(self.processes):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
        for output in self.files:
            output.close()
        shutil.copytree(self.data, self.artifacts / "state", dirs_exist_ok=True,
                        ignore=shutil.ignore_patterns(".ipc"))
        shutil.rmtree(self.directory)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    args = parser.parse_args()
    for command in ("sway", "swaymsg", "grim", "wtype", "cc", "pkg-config", "wayland-scanner"):
        if not shutil.which(command):
            parser.error(f"Missing native-test dependency: {command}")
    binary = args.binary.resolve()
    if not binary.is_file():
        parser.error(f"App binary does not exist: {binary}")
    artifacts = Path(os.environ.get("NATIVE_TEST_ARTIFACTS", "target/native-test-artifacts")).resolve()
    artifacts.mkdir(parents=True, exist_ok=True)
    test = NativeTest(binary, artifacts)
    try:
        test.exercise()
        print(f"Native layer-shell lifecycle, keyboard, persistence, and profile tests passed. Artifacts: {artifacts}")
    except (RuntimeError, subprocess.SubprocessError, OSError) as error:
        print(f"Native test failed: {error}\nLogs and screenshots: {artifacts}", file=sys.stderr)
        if isinstance(error, subprocess.CalledProcessError) and error.stderr:
            print(error.stderr, file=sys.stderr)
        return 1
    finally:
        test.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
