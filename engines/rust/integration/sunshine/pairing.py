"""Route the four pairing messages to the Rust transcript tied to the native session."""
from pathlib import Path
from shutil import copy2


def integrate(src):
    copy2(Path(__file__).with_suffix(".h"), src)
    header = src / "nvhttp.h"
    code = header.read_text().replace('#include "crypto.h"', '#include "crypto.h"\n#include "pairing.h"')
    start = code.index("  enum class PAIR_PHASE")
    code = code[:start] + code[code.index("  struct pair_session_t", start):]
    start = code.index("    std::unique_ptr<crypto::aes_t>")
    code = code[:start] + """    std::unique_ptr<PlPair, decltype(&pl_pair_free)> transcript {pl_pair_new(), pl_pair_free};

""" + code[code.index("    struct {", start):]
    start = code.index("    /**", code.index("} async_insert_pin;"))
    header.write_text(code[:start] + code[code.index("\n  };", start):])
    unit = src / "nvhttp.cpp"
    code = '#include <openssl/rand.h>\n' + unit.read_text()

    def replace_between(a, b, replacement):
        nonlocal code
        i = code.index(a)
        code = code[:i] + replacement + code[code.index(b, i):]

    replace_between("    if (sess.last_phase", '    tree.put("root.paired", 1);', """    const auto salt = util::from_hex<std::array<uint8_t, 16>>(sess.async_insert_pin.salt, true);
    if (!pl_pair_begin(sess.transcript.get(), salt.data(), pin.data(), pin.size())) {
      fail_pair(sess, tree, "Invalid PIN or unexpected pairing message");
      return;
    }
""")
    replace_between("    if (sess.last_phase", '    tree.put("root.paired", 1);', """    auto cert = crypto::x509(conf_intern.servercert);
    if (!cert || challenge.size() != 16) {
      fail_pair(sess, tree, "Invalid client challenge");
      return;
    }
    auto signature = crypto::signature(cert);
    std::array<uint8_t, 32> random {};
    std::array<uint8_t, 48> encrypted {};
    if (signature.empty() || RAND_bytes(random.data(), random.size()) != 1 ||
        !pl_pair_challenge(sess.transcript.get(), challenge.data(), signature.data(), signature.size(), random.data(), encrypted.data())) {
      fail_pair(sess, tree, "Pairing challenge failed");
      return;
    }
""")
    replace_between("    if (sess.last_phase", '    serversecret.insert', """    std::string serversecret(16, '\\0');
    if (encrypted_response.size() != 32 || !pl_pair_proof(sess.transcript.get(), encrypted_response.data(), serversecret.data())) {
      fail_pair(sess, tree, "Unexpected client proof");
      return;
    }
    auto sign = crypto::sign256(crypto::pkey(conf_intern.pkey), serversecret);
    if (sign.empty()) {
      fail_pair(sess, tree, "Host signature failed");
      return;
    }
""")
    replace_between("    if (sess.last_phase", "    auto &client = sess.client;", "")
    replace_between("    std::string data;\n    data.reserve(sess.serverchallenge", '      tree.put("root.paired", 1);', """    const bool verified = crypto::verify256(x509, secret, sign);
    if (!x509_sign.empty() && pl_pair_finish(sess.transcript.get(), x509_sign.data(), x509_sign.size(), secret.data(), verified)) {
""")
    code = code.replace('if (get_arg(args, "salt").size() != 32', '''const auto salt = get_arg(args, "salt");
        if (salt.size() != 32 || salt.find_first_not_of("0123456789abcdefABCDEF") != std::string::npos''')
    code = code.replace("if (sess.last_phase != PAIR_PHASE::NONE) return false;\n    getservercert(sess, tree, pin);",
                        "if (!pl_pair_fresh(sess.transcript.get())) return false;\n    auto async_response = std::move(sess.async_insert_pin.response);")
    code = code.replace("    // response to the request for pin", "    getservercert(sess, tree, pin);\n    // response to the request for pin")
    code = code.replace("    auto &async_response = sess.async_insert_pin.response;\n", "")
    code = code.replace("    // reset async_response\n    async_response = std::decay_t<decltype(async_response.left())>();\n", "")
    start = code.index("  void launch(bool &host_audio,")
    end = code.index("  void cancel(resp_https_t", start)
    unit.write_text(code[:start] + Path(__file__).with_name("launch.inc").read_text() + code[end:])
