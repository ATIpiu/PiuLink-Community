"""Apply wire validation and bounded session state to the final native adapters."""
from pathlib import Path
from shutil import copy2


def edit(path, old, new, count=1):
    code = path.read_text()
    if code.count(old) != count:
        raise ValueError(f"Unrecognized {path.name} anchor: {old[:65]}")
    path.write_text(code.replace(old, new))


def host(src):
    copy2(Path(__file__).with_name("replay.h"), src)
    stream = src / "stream.cpp"
    edit(stream, '#include "fec.h"', '#include "replay.h"\n#include "fec.h"')
    edit(stream, "      std::uint64_t seq;", "      PlReplayWindow received {};\n      std::uint64_t seq;")
    edit(stream, "        record.erase(record.begin()", """        if (!pl_accept_authenticated(&session->control.received, util::endian::little(header->seq))) return;
        record.erase(record.begin()""")
    for kind, typ, name in [("int32_t *stats = (int32_t *)", "int32_t", "stats[4]"),
                            ("auto frames = (std::int64_t *)", "std::int64_t", "frames[2]")]:
        dest = name.split("[")[0]
        edit(stream, kind + " payload.data();", f"""{typ} {name};
      if (payload.size() < sizeof({dest})) return;
      std::memcpy({dest}, payload.data(), sizeof({dest}));""")
    edit(stream, "auto tagged_cipher_length = util::endian::big(*(int32_t *) payload.data());", """uint32_t declared;
      if (payload.size() < sizeof(declared)) return;
      std::memcpy(&declared, payload.data(), sizeof(declared));
      auto tagged_cipher_length = util::endian::big(declared);
      if (tagged_cipher_length < 16 || tagged_cipher_length != payload.size() - sizeof(declared)) return;""")
    edit(stream, "auto type = *(std::uint16_t *) plaintext.data();", """if (plaintext.size() < 4) return;
      const uint16_t type = plaintext[0] | (uint16_t(plaintext[1]) << 8);
      const size_t inner_length = plaintext[2] | (uint16_t(plaintext[3]) << 8);
      if (inner_length != plaintext.size() - 4) return;""")
    # Batching may empty the input queue while callbacks remain in the pool.
    # Count the callbacks themselves and terminate the overloaded session cleanly.
    inp = src / "input.cpp"
    signature = "void passthrough(std::shared_ptr<input_t> &input, std::vector<std::uint8_t> &&input_data)"
    for f in (inp, src / "input.h"):
        edit(f, signature, signature.replace("void ", "bool ", 1))
    edit(inp, "    std::mutex input_queue_lock;", "    size_t pending_callbacks = 0;\n    std::mutex input_queue_lock;")
    edit(inp, "if (!pl_valid_input(input_data.data(), input_data.size())) return;", "if (!pl_valid_input(input_data.data(), input_data.size())) return true;")
    edit(inp, "      input->input_queue.push_back", """      if (input->pending_callbacks >= 256) return false;
      ++input->pending_callbacks;
      input->input_queue.push_back""")
    edit(inp, "    task_pool.push(passthrough_next_message, input);", """    task_pool.push([input]() {
      passthrough_next_message(input);
      std::lock_guard<std::mutex> lock(input->input_queue_lock);
      --input->pending_callbacks;
    });
    return true;""")
    edit(stream, "input::passthrough(session->input, std::move(plaintext));",
         "if (!input::passthrough(session->input, std::move(plaintext))) session::stop(*session);", 2)
    edit(src / "rtsp.h", "uint32_t rtsp_iv_counter;", "uint32_t rtsp_iv_counter;\n    uint32_t last_authenticated_request = 0;")
    edit(src / "rtsp.cpp", "      msg_t req {new msg_t::element_type {}};\n      if (auto status = pl_rtsp_parse(req.get(), (char *) header->payload()", """      const auto seq = util::endian::big<uint32_t>(header->sequenceNumber);
      if (seq <= socket->session->last_authenticated_request) return;
      socket->session->last_authenticated_request = seq;
      msg_t req {new msg_t::element_type {}};
      if (auto status = pl_rtsp_parse(req.get(), (char *) header->payload()""")
    pair = src / "nvhttp.cpp"
    code = pair.read_text()
    begin = code.index("    for (auto &[name, val] : request->header)", code.index("  void print_req("))
    pair.write_text(code[:begin] + code[code.index("\n  }", begin):])
    edit(pair, "        BOOST_LOG(debug) << sess.client.cert;\n", "")
    # One operator PIN prompt, bounded per-address cooldown after expiration or failure.
    # Keep active leases stable even when the requesting client repeats discovery.
    edit(pair, "  static void expire_pairing()", """  static std::string pairing_peer;
  static std::unordered_map<std::string, std::chrono::steady_clock::time_point> pairing_retry_after;
  static void delay_pairing_retry(int seconds) {
    if (!pairing_peer.empty()) pairing_retry_after[pairing_peer] = std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
  }
  static void expire_pairing()""")
    edit(pair, "    if (std::chrono::steady_clock::now() >= pairing_deadline) map_id_sess.clear();", """    auto now = std::chrono::steady_clock::now();
    for (auto it = pairing_retry_after.begin(); it != pairing_retry_after.end();) {
      if (it->second <= now) it = pairing_retry_after.erase(it);
      else ++it;
    }
    if (!map_id_sess.empty() && now >= pairing_deadline) {
      delay_pairing_retry(30);
      map_id_sess.clear();
    }""")
    code = pair.read_text()
    i = code.index("        if (auto existing = map_id_sess.find(")
    pair.write_text(code[:i] + """        const auto peer = request->remote_endpoint().address().to_string();
        if (pairing_retry_after.count(peer) || pairing_retry_after.size() >= 128) {
          tree.put("root.<xmlattr>.status_code", 429);
          return;
        }
""" + code[code.index("        if (!map_id_sess.empty())", i):])
    edit(pair, "        pairing_deadline =", "        pairing_peer = peer;\n        pairing_deadline =")
    # GameStream clients commonly send the same uniqueid. Keep continuation
    # messages on their original route as well as checking the transcript.
    edit(pair, "if (sess_it == std::end(map_id_sess))",
         "if (sess_it == std::end(map_id_sess) || pairing_peer != request->remote_endpoint().address().to_string())")
    edit(pair, "    remove_session(sess);  // Security measure", "    delay_pairing_retry(5);\n    remove_session(sess);  // Security measure")
    edit(pair, '    } else {\n      tree.put("root.paired", 0);\n    }\n\n    remove_session(sess);',
         '    } else {\n      delay_pairing_retry(5);\n      tree.put("root.paired", 0);\n    }\n\n    remove_session(sess);')


def android(project, native):
    copy2(Path(__file__).with_name("replay.h"), native)
    control = native / "ControlStream.c"
    edit(control, '#include "piulink_fec.h"', '#include "replay.h"\n#include "piulink_fec.h"')
    edit(control, "static uint64_t currentEnetSequenceNumber;", "static PlReplayWindow receivedControl;\nstatic uint64_t currentEnetSequenceNumber;")
    edit(control, "    currentEnetSequenceNumber = 0;", "    memset(&receivedControl, 0, sizeof(receivedControl));\n    currentEnetSequenceNumber = 0;")
    edit(control, "        const uint8_t *body=(uint8_t*)encPacket+24;", """        if (!pl_accept_authenticated(&receivedControl, LE32(encPacket->seq))) return false;
        const uint8_t *body=(uint8_t*)encPacket+24;""")
    rtsp = native / "RtspConnection.c"
    edit(rtsp, "static uint32_t encryptionSequenceNumber;", "static uint32_t lastAuthenticatedReply;\nstatic uint32_t encryptionSequenceNumber;")
    edit(rtsp, "    currentSeqNumber = 1;", "    lastAuthenticatedReply = 0;\n    currentSeqNumber = 1;")
    edit(rtsp, "        wire+=24;", """        uint32_t seq;
        memcpy(&seq,wire+4,sizeof(seq));
        seq = BE32(seq);
        if (seq <= lastAuthenticatedReply) return false;
        lastAuthenticatedReply = seq;
        wire+=24;""")
    edit(rtsp, "    return unsealRtspMessage((char*)bytes,size,((TcpContext*)ctx)->response);", """    TcpContext *transaction = (TcpContext*)ctx;
    if (!unsealRtspMessage((char*)bytes,size,transaction->response)) return false;
    if (encryptedRtspEnabled) {
        const char *sent = getOptionContent(transaction->request->options, "CSeq");
        const char *reply = getOptionContent(transaction->response->options, "CSeq");
        if (!sent || !reply || strcmp(sent,reply)) {
            freeMessage(transaction->response);
            return false;
        }
    }
    return true;""")
    video = native / "RtpVideoQueue.c"
    edit(video, "    queue->currentFrameNumber = 1;", "    queue->bufferFirstRecvTimeMs = PltGetMillis();\n    queue->currentFrameNumber = 1;")
    edit(video, "if (isBefore16(nvPacket->frameIndex, queue->currentFrameNumber))", """if ((int32_t)(nvPacket->frameIndex - queue->currentFrameNumber) < 0 ||
        (uint32_t)(nvPacket->frameIndex - queue->currentFrameNumber) >
            512 + (queue->bufferFirstRecvTimeMs ? (PltGetMillis() - queue->bufferFirstRecvTimeMs) / 2 : 0))""")
    android_app(project)


def android_app(project):
    java = project / "app/src/main/java/com/limelight/nvstream/http"
    manager = java / "PairingManager.java"
    code = manager.read_text()
    # Older input trees expose the provisional pin and use public discovery to skip the PIN.
    if "public static boolean isTrustedAutoPairEnabled" in code:
        start = code.index("        try {", code.index("public static boolean isTrustedAutoPairEnabled"))
        manager.write_text(code[:start] + "        return false;\n" +
                           code[code.index("    }\n\n    public X509Certificate", start):])
        edit(manager, "private X509Certificate serverCert;", "private X509Certificate pairedCert;")
        edit(manager, "        return serverCert;", "        return pairedCert;")
        # Public polling stays available on older app shells, but trust still requires a pin.
        app = java.parents[1]
        edit(app / "computers/PairingStatePersistencePolicy.java",
             "        return publicPollState;", "        return PairState.NOT_PAIRED;")
        edit(app / "nvstream/NvConnection.java",
             "if (context.serverCert == null &&\n                h.getPairState(serverInfo) != PairingManager.PairState.PAIRED)",
             "if (context.serverCert == null)")
    http = java / "NvHTTP.java"
    if 'throw new IOException("Host XML response exceeds size limit")' in http.read_text():
        return
    edit(http, "        return getPairState(getServerInfo(true));", """        if (serverCert == null) {
            getPublicServerInfo(true);
            return PairState.NOT_PAIRED;
        }
        return getPairState(openHttpConnectionToString(httpClientLongConnectTimeout, getHttpsUrl(true), "serverinfo"));""")
    edit(http, "                .hostnameVerifier(hv)", "                .followRedirects(false)\n                .hostnameVerifier(hv)")
    edit(http, """                    if (certificates.length == 1 && certificates[0].equals(NvHTTP.this.serverCert)) {
                        // Allow any hostname if it's our pinned cert
                        return true;
                    }""", """                    if (NvHTTP.this.serverCert != null)
                        return certificates.length > 0 && NvHTTP.this.serverCert.equals(certificates[0]);""")
    edit(http, """                } catch (SSLPeerUnverifiedException e) {
                    e.printStackTrace();
                }""", """                } catch (SSLPeerUnverifiedException e) {
                    return false;
                }""")
    edit(http, """        if (verbose) {
            // Use the raw function so the app list is printed
            return getAppListByReader(new StringReader(getAppListRaw()));
        }
        else {
            try (final ResponseBody resp = openHttpConnection(httpClientLongConnectTimeout, getHttpsUrl(true), "applist")) {
                return getAppListByReader(new InputStreamReader(resp.byteStream()));
            }
        }""", """        return getAppListByReader(new StringReader(getAppListRaw()));""")
    edit(http, ".readTimeout(READ_TIMEOUT,", ".callTimeout(25, TimeUnit.SECONDS)\n                .readTimeout(READ_TIMEOUT,")
    edit(http, ".readTimeout(0, TimeUnit.MILLISECONDS)", ".callTimeout(130, TimeUnit.SECONDS)\n                .readTimeout(125, TimeUnit.SECONDS)")
    old = """            ResponseBody resp = openHttpConnection(client, baseUrl, path, query);
            String respString = resp.string();
            resp.close();"""
    edit(http, old, """            String respString;
            try (ResponseBody resp = openHttpConnection(client, baseUrl, path, query)) {
                int limit = path.equals("applist") ? 4 * 1024 * 1024 : 128 * 1024;
                if (resp.contentLength() > limit || resp.source().request(limit + 1L))
                    throw new IOException("Host XML response exceeds size limit");
                respString = resp.string();
            }""")
    edit(http, 'LimeLog.info(getCompleteUrl(baseUrl, path, query)+" -> "+respString);', 'LimeLog.info("Host request completed: "+path);')
    edit(http, 'LimeLog.warning(getCompleteUrl(baseUrl, path, query)+" -> "+e.getMessage());', 'LimeLog.warning("Host request failed: "+path);')
