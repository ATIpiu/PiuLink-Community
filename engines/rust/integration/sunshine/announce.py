from pathlib import Path
import re


def integrate(code, directory):
    schema = Path(__file__).with_name("announce_fields.rs").read_text()
    fields = re.findall(r'^    (\w+), b"[^"]+", (?:None|Some\(\d+\)); // (\S+)$', schema, re.M)
    if len(fields) != schema.count('b"'):
        raise ValueError("Incomplete Sunshine ANNOUNCE schema mapping")
    header = directory / "announce.h"
    header.write_text('#include <cstdint>\nstruct PlOffer {\n' + ''.join(f"  int64_t {field};\n" for field, _ in fields) +
                      '};\nextern "C" bool pl_announce(const void*, int, PlOffer*);\n'
                      'extern "C" int32_t pl_announced_bitrate(int64_t, int32_t, bool, int32_t);\n')
    start = code.index("    std::string_view payload {req->payload", code.index("  void cmd_announce("))
    end = code.index("    // When using stereo audio", start)
    code = '#include "announce.h"\n' + code[:start] + """    PlOffer offer {};
    if (!pl_announce(req->payload, req->payloadLength, &offer)) {
      respond(sock, session, &option, 400, "BAD REQUEST", req->sequenceNumber, {});
      return;
    }
    stream::config_t config;
    config.monitor.capture_route = session.capture_route;
    config.audio.flags[audio::config_t::HOST_AUDIO] = session.host_audio;
""" + ''.join(f"    config.{member} = offer.{field};\n" for field, member in fields if member != "-") + """
    if (offer.nv_flags & 0x20) config.encryptionFlagsEnabled |= SS_ENC_AUDIO;

""" + code[end:]
    start = code.index("    if (configuredBitrateKbps)")
    end = code.index("    if (config.monitor.videoFormat",start)
    return code[:start] + """    if (offer.configured) {
      config.monitor.bitrate = pl_announced_bitrate(offer.configured, config::stream.fec_percentage,
                             config.audio.flags[audio::config_t::HIGH_QUALITY], config.audio.channels);
      BOOST_LOG(debug) << "Rust ANNOUNCE adjusted video bitrate " << config.monitor.bitrate << " Kbps";
    }

""" + code[end:]
