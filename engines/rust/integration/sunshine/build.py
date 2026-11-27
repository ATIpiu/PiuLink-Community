"""Build an isolated Rust-enabled engine from a configured PiuLink Sunshine Ninja build."""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess

root = Path(__file__).resolve().parents[2]
here = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument("source", type=Path)
parser.add_argument("toolchain", type=Path, help="MSYS2 UCRT64 bin directory")
parser.add_argument("--output", type=Path, default=root / "out/rust-nvenc")
parser.add_argument("--contract-test", type=Path, help="upstream content_adaptive_rate_test.cpp")
parser.add_argument("--test-video-impairment", action="store_true", help="Build a QA sender that impairs one video block every 60 frames")
args = parser.parse_args()
source = args.source.resolve()
build = source / "build-piulink"
bin_dir = args.toolchain.resolve()
env = dict(os.environ, PATH=str(bin_dir) + os.pathsep + os.environ["PATH"])
out = args.output.resolve()
if out.is_relative_to(source) or source.is_relative_to(out):
    raise ValueError("Output and source must be separate directory trees")
out.mkdir(exist_ok=True, parents=True)
overlay = out / "overlay"
shutil.copytree(source / "src", overlay / "src", dirs_exist_ok=True)
shutil.copy2(here / "piulink_content_rate.h", overlay / "src/nvenc")
native = overlay / "src/nvenc/nvenc_base.cpp"
code = native.read_text()
def replace_once(old, new):
    global code
    if code.count(old) != 1:
        raise RuntimeError("Incompatible nvenc_base.cpp: " + old[:65])
    code = code.replace(old, new)

anchor = "    content_rate_controller.reset(client_config.bitrate, init_params.frameRateNum / std::max(1u, init_params.frameRateDen));"
replace_once(anchor, anchor + """
    if (const auto opt = std::getenv("PIULINK_RUST_RATE_ON_START"); opt && std::string_view(opt) == "1")
      content_rate_controller.set_enabled(true);
    BOOST_LOG(info) << "Rust NVENC reset ceiling=" << client_config.bitrate << " fps=" << init_params.frameRateNum
                    << "/" << init_params.frameRateDen << " enabled=" << content_rate_controller.enabled();""")
anchor = "    if (auto target = content_rate_controller.observe(encoded_frame.data.size(), lock_bitstream.frameAvgQP, encoded_frame.idr)) {"
replace_once(anchor, """    if (content_rate_controller.enabled() && frame_index % 120 == 0)
      BOOST_LOG(debug) << "Rust NVENC frame=" << frame_index << " bytes=" << encoded_frame.data.size()
                       << " average_bps=" << saved_config.rcParams.averageBitRate;
""" + anchor)
anchor = "      if (apply_bitrate(*target)) {"
replace_once(anchor, """      BOOST_LOG(info) << "Rust NVENC decision target=" << *target
                      << " frame=" << frame_index << " output_kbps=" << content_rate_controller.output_kbps()
                      << " qp_x100=" << content_rate_controller.average_qp_x100();
""" + anchor + """
        BOOST_LOG(info) << "Rust NVENC committed target=" << *target
                        << " frame=" << frame_index << " average_bps=" << saved_config.rcParams.averageBitRate;""")
shutil.copy2(here / "encode.h", native.parent)
start = code.index("    NV_ENC_MAP_INPUT_RESOURCE mapped_input_buffer", code.index("nvenc_encoded_frame nvenc_base::encode_frame("))
end = code.index("    if (encoder_state.rfi_needs_confirmation)", start)
code = code[:start] + (here / "encode_frame.inc").read_text() + "\n" + code[end:]
start = code.index("    if (nvenc_failed(nvenc->nvEncUnlockBitstream", start)
end = code.index("    encoder_state.frame_size_logger", start)
code = code[:start] + code[end:]
start = code.index("    if (first_frame >=", code.index("bool nvenc_base::invalidate_ref_frames("))
end = code.index("\n  }", start)
code = code[:start] + """    return pl_nv_invalidate(encoder, nvenc->nvEncInvalidateRefFrames, first_frame, last_frame,
                            encoder_state.last_encoded_frame_index, encoder_params.ref_frames_in_dpb,
                            &encoder_state.last_rfi_range.first, &encoder_state.last_rfi_range.second,
                            &encoder_state.rfi_needs_confirmation);""" + code[end:]
anchor = '#include "nvenc_base.h"'
replace_once(anchor, anchor + '\n#include "encode.h"')
native.write_text(code)
wire = here.parent / "wire"
import sys
sys.path.insert(0, str(wire))
shutil.copy2(wire / "fec.h", overlay / "src")
stream = overlay / "src/stream.cpp"
code = '#include "fec.h"\n' + stream.read_text()
replace_once("(uint32_t) (x << 12 |\n                          shards.data_shards << 22 |\n                          shards.percentage << 4)", "pl_fec_info(x, shards.data_shards, shards.percentage)")
blocks = "(blockIndex << 4) | ((fec_blocks_needed - 1) << 6)"
if code.count(blocks) != 2:
    raise RuntimeError("Unexpected video FEC sender")
old_encoder = re.compile(r"rs_t rs \{reed_solomon_new\(\(int\) data_shards, \(int\) parity_shards\)\};\s*reed_solomon_encode\(rs.get\(\), shards_p.begin\(\), \(int\) nr_shards, \(int\) blocksize\);")
code, count = old_encoder.subn('if (pl_video_encode(shards_p.begin(), data_shards, parity_shards, blocksize) != 0) throw std::runtime_error("Invalid video FEC geometry");', code)
if count != 1:
    raise RuntimeError("Unexpected video encoder")
start = code.index("    fec::rs_t rs {", code.index("  void audioBroadcastThread"))
end = code.index("    audio_packet.rtp.header", start)
code = code[:start] + "    crypto::aes_t iv(16);\n\n" + code[end:]
replace_once("reed_solomon_encode(rs.get(), shards_p.begin(), RTPA_TOTAL_SHARDS, bytes);",
             'if (pl_audio_encode(shards_p.begin(), bytes) != 0) throw std::runtime_error("Invalid audio FEC block");')
start = code.index("  std::vector<uint8_t> concat_and_insert(")
end = code.index("  std::vector<uint8_t> replace(", start)
code = code[:start] + code[end:]
replace_once("auto payload_new = concat_and_insert(sizeof(video_packet_raw_t), payload_blocksize, std::string_view {(char *) &frame_header, sizeof(frame_header)}, payload);", """auto bytes = pl_packetized_size(frame_header.size(), payload.size(), sizeof(video_packet_raw_t), payload_blocksize);
      std::vector<uint8_t> payload_new(bytes);
      if (!pl_packetize(payload_new.data(), bytes, frame_header.data(), frame_header.size(),
                        reinterpret_cast<const uint8_t*>(payload.data()), payload.size(), sizeof(video_packet_raw_t), payload_blocksize)) {
        BOOST_LOG(warning) << "Invalid video packet layout";
        continue;
      }""")
start = code.index("      video_short_frame_header_t frame_header = {};")
end = code.index("      auto fecPercentage",start)
code = code[:start] + """      std::array<uint8_t, 8> frame_header {};
      auto elapsed_us = packet->frame_timestamp ?
        std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - *packet->frame_timestamp).count() : 0;
      if (!pl_write_header(frame_header.data(), payload.size(), session->config.packetsize - sizeof(NV_VIDEO_PACKET),
                           packet->is_idr(), packet->after_ref_frame_invalidation, elapsed_us)) {
        BOOST_LOG(warning) << "Invalid frame packet capacity";
        continue;
      }
      if (packet->frame_timestamp)
        frame_processing_latency_logger.collect_and_log((frame_header[1] + (frame_header[2] << 8)) / 10.);
""" + code[end:]
start = code.index("      // There are 2 bits for FEC block count")
end = code.index("      try {", start)
code = code[:start] + """      PlFrameLayout layout {};
      if (!pl_frame_layout(payload.size(), blocksize, fecPercentage, false, &layout)) {
        BOOST_LOG(warning) << "Dropping frame outside the GameStream packet count limit";
        continue;
      }
      auto fec_blocks_needed = layout.blocks;
      fecPercentage = layout.percentage;
      std::array<std::string_view, 4> fec_blocks;
      auto fec_blocks_begin = fec_blocks.begin();
      auto fec_blocks_end = fec_blocks_begin + fec_blocks_needed;
      size_t offset = 0;
      for (size_t i = 0; i < fec_blocks_needed; ++i) {
        fec_blocks[i] = payload.substr(offset, layout.lengths[i]);
        offset += layout.lengths[i];
      }
""" + code[end:]
from audio import integrate as integrate_audio_sender
code = integrate_audio_sender(code)
if args.test_video_impairment:
    from impairment import inject
    code = inject(code)
from video_sender import integrate as integrate_video_sender
from host_session import integrate as integrate_host_session
from video_crypto import integrate as integrate_video_crypto
from media_crypto_adapter import host as integrate_media_crypto
from enet_host import integrate as integrate_enet_host
code = integrate_enet_host(code)
stream.write_text(integrate_media_crypto(integrate_video_crypto(integrate_host_session(integrate_video_sender(code)))))
from pairing import integrate as integrate_pairing
integrate_pairing(overlay / "src")
from texture import integrate as integrate_texture
integrate_texture(overlay / "src")
from nv_init import integrate as integrate_nv_init
integrate_nv_init(overlay / "src")
from avcodec import integrate as integrate_avcodec
integrate_avcodec(overlay/"src")
from wgc import integrate as integrate_wgc
integrate_wgc(overlay/"src/platform/windows")
from wasapi import integrate as integrate_wasapi
integrate_wasapi(overlay / "src/platform/windows/audio.cpp")
audio_unit = overlay / "src/audio.cpp"
shutil.copy2(wire / "opus_stream.h", overlay / "src")
code = '#include "opus_stream.h"\n' + audio_unit.read_text()
start = code.index("    opus_t opus {", code.index("  void encodeThread("))
end = code.index("\n  }",start)
audio_unit.write_text(code[:start] + (here / "opus_loop.inc").read_text() + code[end:])
win_udp = overlay / "src/platform/windows/misc.cpp"
shutil.copy2(here / "udp.h", win_udp.parent)
code = '#include "udp.h"\n#include <boost/asio/ip/udp.hpp>\n' + win_udp.read_text()
start = code.index("  bool send_batch(batched_send_info_t &send_info)")
end = code.index("  class qos_t:",start)
win_udp.write_text(code[:start] + (here / "udp_send.inc").read_text() + "\n" + code[end:])
display_base = overlay / "src/platform/windows/display_base.cpp"
shutil.copy2(here / "capture.h", display_base.parent)
code = '#include "capture.h"\n' + display_base.read_text()
start = code.index("    auto capture_status = release_frame();", code.index("capture_e duplication_t::next_frame("))
end = code.index("\n  }", start)
code = code[:start] + """    int32_t hr = 0;
    const auto status = static_cast<capture_e>(pl_dxgi_acquire(dup.get(), &has_frame, timeout.count(), &frame_info,
                                                               reinterpret_cast<void**>(res_p), &hr));
    if (status == capture_e::ok && frame_info.ProtectedContentMaskedOut &&
        std::chrono::steady_clock::now() > last_protected_content_warning_time + 10s) {
      BOOST_LOG(warning) << "Windows is blocking protected desktop content from capture.";
      last_protected_content_warning_time = std::chrono::steady_clock::now();
    }
    if (status == capture_e::error) BOOST_LOG(error) << "Rust DXGI acquire failed: " << util::hex(hr).to_string_view();
    return status;""" + code[end:]
start = code.index("    if (!has_frame)", code.index("capture_e duplication_t::release_frame()"))
end = code.index("\n  }", start)
code = code[:start] + """    int32_t hr = 0;
    auto status = static_cast<capture_e>(pl_dxgi_release(dup.get(), &has_frame, &hr));
    if (status == capture_e::error) BOOST_LOG(error) << "Rust DXGI release failed: " << util::hex(hr).to_string_view();
    return status;""" + code[end:]
start = code.index("    auto adjust_client_frame_rate =")
end = code.index("    // Keep the display awake",start)
code = code[:start] + """    const auto rate = pl_capture_rate({client_frame_rate_strict.Numerator,client_frame_rate_strict.Denominator},
        {display_refresh_rate.Numerator,display_refresh_rate.Denominator}, display_refresh_rate_rounded, client_frame_rate);
""" + code[end:]
start = code.index("    while (true)",code.index("    sleep_overshoot_logger.reset();",start))
end = code.index("\n  }",start)
display_base.write_text(code[:start] + (here / "capture_loop.inc").read_text() + code[end:])
native_input = overlay / "src/input.cpp"
code = '#include "fec.h"\n' + native_input.read_text()
start = code.index("    auto release =", code.index("void passthrough(std::shared_ptr<input_t> &input, PNV_KEYBOARD_PACKET"))
end = code.index("    // Set synthetic modifier flags", start)
tail = code.index("    update_shortcutFlags(", end)
code = code[:start] + """    static_assert(sizeof(NV_KEYBOARD_PACKET) == 14);
    PlKey key {};
    if (!pl_read_key(reinterpret_cast<const uint8_t *>(packet), &key)) return;
    bool release = key.release;
    int keyCode = key.code;

""" + code[end:tail].replace("packet->modifiers", "key.modifiers").replace("packet->flags", "key.flags") + code[tail:]
from pointer_adapter import host
from input_adapter import host_inputs
native_input.write_text(host_inputs(host(code)))
shutil.copy2(wire / "rtsp_adapter.h", overlay / "src")
rtsp = overlay / "src/rtsp.cpp"
code = rtsp.read_text()
replace_once("#include <moonlight-common-c/src/Rtsp.h>", '#include <moonlight-common-c/src/Rtsp.h>\n#include "rtsp_adapter.h"')
replace_once("parseRtspMessage(req.get(), (char *) plaintext.data(), (int) plaintext.size())",
             "pl_rtsp_parse(req.get(), (char *) plaintext.data(), (int) plaintext.size(), malloc, free)")
replace_once("parseRtspMessage(req.get(), socket->msg_buf.data(), (int) (end - socket->msg_buf.data()))",
             "pl_rtsp_parse(req.get(), socket->msg_buf.data(), (int) (end - socket->msg_buf.data()), malloc, free)")
replace_once("serializeRtspMessage(resp.get(), &serialized_len)};", "pl_rtsp_serialize(resp.get(), &serialized_len, malloc)};\n    if (!raw_resp) return;")
start = code.index("    std::string_view target {", code.index("  void cmd_setup(rtsp_server_t *server, tcp::socket &sock, launch_session_t &session, msg_t &&req) {"))
end = code.index("    seqn.next = &session_option;", start)
code = code[:start] + """    auto kind = pl_setup_kind(req->message.request.target);
    std::uint16_t port;
    switch (kind) {
      case 2: port = net::map_port(stream::AUDIO_STREAM_PORT); break;
      case 3: port = net::map_port(stream::VIDEO_STREAM_PORT); break;
      case 4: port = net::map_port(stream::CONTROL_PORT); break;
      default: cmd_not_found(sock, session, std::move(req)); return;
    }

""" + code[end:]
replace_once('if (type == "control"sv) {','if (kind == 4) {')
replace_once("    server->insert(stream_session);", """    if (!stream_session) {
      respond(sock, session, &option, 400, "BAD REQUEST", req->sequenceNumber, {});
      return;
    }
    server->insert(stream_session);""")
from announce import integrate as integrate_announce
from record_adapter import host as integrate_rtsp_record
rtsp.write_text(integrate_rtsp_record(integrate_announce(code, rtsp.parent), rtsp.parent, wire))
from hardening import host as harden_host
harden_host(overlay / "src")
cargo = shutil.which("cargo") or str(Path.home() / ".cargo/bin/cargo.exe")
rust_target = root / "target/nvenc"
subprocess.run([cargo, "build", "--manifest-path", here / "Cargo.toml", "--release",
                "--target-dir", rust_target], check=True)
dll = out / "piulink_nvenc_rate.dll"
shutil.copy2(rust_target / "release" / dll.name, dll)
exports = sorted(set(re.findall(r'fn (pl_rate_\w+)\(', (here / "abi.rs").read_text())))
exports += ["pl_fec_info", "pl_fec_blocks", "pl_fec_read", "pl_video_encode", "pl_video_recover", "pl_audio_encode", "pl_audio_recover", "pl_frame_layout", "pl_write_header", "pl_read_header", "pl_packetized_size", "pl_packetize", "pl_write_key", "pl_read_key", "pl_rtsp_parse", "pl_rtsp_serialize", "pl_pointer_read", "pl_scroll_delta", "pl_batch_input", "pl_pad_read", "pl_audio_data", "pl_audio_parity", "pl_audio_iv", "pl_pacing_init", "pl_pacing_due", "pl_pacing_end", "pl_video_source", "pl_video_shard", "pl_valid_input", "pl_setup_kind", "pl_host_new", "pl_host_free", "pl_host_begin", "pl_host_publish", "pl_host_state", "pl_host_wait", "pl_host_stop", "pl_host_finish", "pl_host_active", "pl_opus_encode_stream", "pl_udp_send", "pl_capture_rate", "pl_capture_run", "pl_dxgi_acquire", "pl_dxgi_release", "pl_encode_frame", "pl_nv_invalidate", "pl_announce", "pl_announced_bitrate", "pl_wasapi_sample", "pl_rtsp_record", "pl_video_cipher_new", "pl_video_cipher_free", "pl_video_cipher_seal", "pl_opus_crypt", "pl_control_record", "pl_avcodec_encode", "pl_wgc_start", "pl_wgc_free", "pl_wgc_cursor", "pl_wgc_release", "pl_wgc_next"]
exports += ["pl_nv_initialize", "pl_nv_plan", "pl_enet_dispatch", "pl_convert_texture", "pl_https_launch", "pl_pair_new", "pl_pair_free", "pl_pair_fresh", "pl_pair_begin", "pl_pair_challenge", "pl_pair_proof", "pl_pair_finish"]
definition = out / "rate.def"
definition.write_text("LIBRARY piulink_nvenc_rate.dll\nEXPORTS\n" + "\n".join(exports))
library = out / "rate.dll.a"
subprocess.run([bin_dir / "dlltool.exe", "-d", definition, "-l", library], check=True, env=env)

def ninja(*params):
    return subprocess.check_output([bin_dir / "ninja.exe", "-C", build, "-t", *params], text=True, encoding="utf-8")

db = json.loads(ninja("compdb", "-x"))
dependents = set()
for line in ninja("deps").splitlines():
    if ": #deps" in line:
        obj = line.split(": #deps")[0]
    elif line.strip().replace("\\", "/").endswith(("/piulink_content_rate.h","/platform/windows/display.h", "/nvhttp.h", "/rtsp.h", "/input.h")):
        dependents.add(obj)
if not dependents:
    raise RuntimeError("Missing Ninja header dependency records")
replacements = {}

def compile_unit(entry):
    obj = entry["output"]
    dst = out / Path(obj).relative_to("CMakeFiles/sunshine.dir")
    dst.parent.mkdir(parents=True, exist_ok=True)
    cmd = entry["command"]
    if " -o " + obj + " -c " + source.as_posix() + "/src/" not in cmd or " -MD -MT " not in cmd:
        raise RuntimeError("Unexpected compiler command; refusing to write into the base build")
    cmd = re.sub(r' -MD -MT .*? -o ', ' -o ', cmd)
    cmd = cmd.replace(" -o " + obj, ' -o "' + dst.as_posix() + '"')
    cmd = cmd.replace(" -c " + source.as_posix() + "/src/", " -c " + overlay.as_posix() + "/src/")
    cmd = cmd.replace("-I" + source.as_posix() + " ", '-I"' + overlay.as_posix() + '" -I' + source.as_posix() + " ")
    subprocess.run(cmd, cwd=build, env=env, check=True)
    return obj, dst

sender, = [e for e in db if Path(e["file"]).as_posix().endswith("/src/stream.cpp")]
dependents.add(sender["output"])
input_unit, = [e for e in db if Path(e["file"]).as_posix().endswith("/src/input.cpp")]
dependents.add(input_unit["output"])
rtsp_unit, = [e for e in db if Path(e["file"]).as_posix().endswith("/src/rtsp.cpp")]
dependents.add(rtsp_unit["output"])
wasapi_entry, = [e for e in db if Path(e["file"]).as_posix().endswith("/src/platform/windows/audio.cpp")]
dependents.add(wasapi_entry["output"])
audio_entry, = [e for e in db if Path(e["file"]).as_posix().endswith("/src/audio.cpp")]
dependents.add(audio_entry["output"])
udp_unit, = [e for e in db if Path(e["file"]).as_posix().endswith("/src/platform/windows/misc.cpp")]
dependents.add(udp_unit["output"])
capture_unit, = [e for e in db if Path(e["file"]).as_posix().endswith("/src/platform/windows/display_base.cpp")]
dependents.add(capture_unit["output"])
entries = [entry for entry in db if entry["output"] in dependents]
if len(entries) != len(dependents):
    raise RuntimeError("Incomplete compile database")
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    replacements.update(pool.map(compile_unit, entries))
link = next(e["command"] for e in db if e["output"] == "sunshine.exe")
flags = link.split("c++.exe ", 1)[1].rsplit(" && cd .", 1)[0]
for obj, dst in replacements.items():
    if flags.count(obj + " ") != 1:
        raise RuntimeError("Missing or ambiguous Sunshine object on link line")
    flags = flags.replace(obj + " ", '"' + dst.as_posix() + '" ')
if flags.count(" -o sunshine.exe") != 1:
    raise RuntimeError("Unrecognized linker output path")
flags = flags.replace(" -o sunshine.exe", ' "' + library.as_posix() + '" -o "' + (out / "sunshine.exe").as_posix() + '"')
flags = flags.replace(" -Wl,--out-implib,libsunshine.dll.a", "")
rsp = out / "link.rsp"
rsp.write_text(flags)
subprocess.run([bin_dir / "c++.exe", "@" + rsp.as_posix()], cwd=build, check=True, env=env)
if args.contract_test:
    test_exe = out / "adapter-test.exe"
    subprocess.run([bin_dir / "c++.exe", "-std=c++17", "-I" + str(here),
                    args.contract_test.resolve(strict=True), library, "-o", test_exe], env=env, check=True)
    subprocess.run([test_exe], env=env, check=True)
def digest(file):
    return hashlib.sha256(file.read_bytes()).hexdigest()
(out / "build.json").write_text(json.dumps(dict(
    video_impairment=args.test_video_impairment, engine_sha256=digest(out / "sunshine.exe"), dll_sha256=digest(dll),
    original_header_sha256=digest(source / "src/nvenc/piulink_content_rate.h"),
    original_nvenc_sha256=digest(source / "src/nvenc/nvenc_base.cpp"),
    recompiled_units=sorted(dependents), contract_test_passed=bool(args.contract_test)), indent=2))
print(f"Linked {len(entries)} Rust-aware translation units into {out / 'sunshine.exe'}")
