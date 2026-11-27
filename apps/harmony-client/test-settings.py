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
toggles = {
    "视频": ["启用远程串流设置", "开流前自动选码率", "串流中自动调节码率"],
    "音频": ["主机同时播放音频", "空间音频", "音频兼容模式"],
    "振动": ["振动反馈", "音频振动"],
    "手柄": ["多手柄输入", "翻转 A/B X/Y", "手柄运动传感器", "设备运动传感器回退", "手柄触摸板作鼠标"],
    "屏幕控制器": ["显示屏幕控制器", "显示屏幕键盘"],
    "鼠标和触控": ["显示本地光标", "触屏作为触控板", "三指唤出输入法", "低延迟触摸参数", "双击拖动"],
    "主机": ["NVIDIA 游戏优化", "断开后锁定屏幕"],
    "性能与调试": ["同步解码 / 极低延迟 (实验性)", "VRR 可变刷新率 (实验性)"],
    "界面": ["小图标模式"],
}
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
    for label in toggles.get(name, []):
        try:
            toggle = matching_toggle(layout(), label)
        except StopIteration:
            results[name].setdefault("unavailableInViewport", []).append(label)
            continue
        original = toggle["checked"]
        tap(toggle)
        changed = matching_toggle(layout(), label)
        if changed["checked"] == original:
            results[name].setdefault("unchanged", []).append(label)
            continue
        tap(changed)
        if matching_toggle(layout(), label)["checked"] != original:
            raise RuntimeError("Toggle did not restore: " + label)
        results[name].setdefault("toggleRoundTrips", []).append(label)
    args.output.write_text(json.dumps(results, indent=2, ensure_ascii=False), encoding="utf-8")
    tap(find_header(name))
    print(name, flush=True)


if any(row.get("unchanged") for row in results.values()):
    raise SystemExit("Some settings did not change; see the output JSON")
