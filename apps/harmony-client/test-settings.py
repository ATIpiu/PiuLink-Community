"""Check native settings sections on a PiuLink test emulator already showing Settings."""
import argparse
import json
import re
import subprocess
import tempfile
import time
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--hdc", required=True, type=Path)
parser.add_argument("--target", required=True)
parser.add_argument("--output", required=True, type=Path)
args = parser.parse_args()
groups = ["视频", "音频", "振动", "手柄", "USB 驱动", "屏幕控制器", "鼠标和触控", "主机",
          "后处理", "性能与调试", "界面", "备份与恢复", "关于"]
toggles = {"音频": "音频兼容模式", "视频": "串流中自动调节码率"}
results = {}


def hdc(*command):
    result = subprocess.run([str(args.hdc), "-t", args.target, *command], check=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if b"[Fail]" in result.stdout or b"ErrorCode:" in result.stdout:
        raise RuntimeError(result.stdout.decode(errors="replace"))


def flatten(node):
    yield node["attributes"]
    for child in node.get("children", []):
        yield from flatten(child)


def layout():
    with tempfile.TemporaryDirectory() as scratch:
        dest = Path(scratch) / "layout.json"
        hdc("shell", "uitest", "dumpLayout", "-p", "/data/local/tmp/piulink-settings.json")
        hdc("file", "recv", "/data/local/tmp/piulink-settings.json", str(dest))
        return list(flatten(json.loads(dest.read_text(encoding="utf-8"))))


def center(node):
    left, top, right, bottom = map(int, re.findall(r"-?\d+", node["bounds"]))
    return (left + right) // 2, (top + bottom) // 2


def tap(node):
    x, y = center(node)
    hdc("shell", "uitest", "uiInput", "click", str(x), str(y))
    time.sleep(0.55)


def section(nodes, name):
    return next((n for n in nodes if n["text"] == name and n["type"] == "Text"
                 and n["bounds"].startswith("[210,")), None)


def find_header(name):
    for _ in range(8):
        nodes = layout()
        header = section(nodes, name)
        if header:
            return header
        hdc("shell", "uitest", "uiInput", "swipe", "600", "600", "600", "2220", "1000")
    raise RuntimeError("Missing section " + name)


def matching_toggle(nodes, label):
    title = next(n for n in nodes if n["text"] == label)
    y = center(title)[1]
    return next(n for n in nodes if n["type"] == "Toggle" and n["enabled"] == "true"
                and abs(center(n)[1] - y) < 80)


initial = layout()
if not any(n["text"] == "自定义串流体验" for n in initial):
    raise SystemExit("Open PiuLink Settings on the Pura 90 Pro portrait emulator first.")
if not all(section(initial, name) for name in groups):
    raise SystemExit("Collapse all settings sections before this fixed-profile test.")
for name in groups:
    tap(find_header(name))
    nodes = layout()
    samples = [n["text"] for n in nodes if n["text"] and n["type"] == "Text"
               and n["text"] not in groups + ["设置", "自定义串流体验"]
               and center(n)[1] > 300]
    if not samples:
        # A group at the bottom expands below the visible screen.
        hdc("shell", "uitest", "uiInput", "swipe", "610", "2340", "610", "830", "1000")
        nodes = layout()
        samples = [n["text"] for n in nodes if n["type"] == "Text" and n["text"]
                   and n["text"] not in groups + ["设置", "自定义串流体验"] and center(n)[1] > 300]
    if not samples:
        raise RuntimeError("No visible controls in " + name)
    results[name] = {"visibleItems": samples}
    label = toggles.get(name)
    if label:
        toggle = matching_toggle(nodes, label)
        original = toggle["checked"]
        tap(toggle)
        changed = matching_toggle(layout(), label)
        if changed["checked"] == original:
            raise RuntimeError("Toggle did not change: " + label)
        tap(changed)
        if matching_toggle(layout(), label)["checked"] != original:
            raise RuntimeError("Toggle did not restore: " + label)
        results[name]["toggleRoundTrip"] = label
    args.output.write_text(json.dumps(results, indent=2, ensure_ascii=False), encoding="utf-8")
    tap(find_header(name))
    print(name, flush=True)

