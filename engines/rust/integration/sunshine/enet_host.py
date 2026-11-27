def integrate(code):
    start = code.index("    ENetEvent event;",code.index("void control_server_t::iterate("))
    end = code.index("\n  }",start)
    return 'extern "C" int32_t pl_enet_dispatch(int32_t,bool,bool,size_t);\n'.join(
        (code[:code.index("  void control_server_t::iterate(")],code[code.index("  void control_server_t::iterate("):start])
    ) + """    static_assert(ENET_EVENT_TYPE_CONNECT == 1 && ENET_EVENT_TYPE_DISCONNECT == 2 && ENET_EVENT_TYPE_RECEIVE == 3);
    ENetEvent event {};
    if (enet_host_service(_host.get(), &event, (enet_uint32)timeout.count()) <= 0) return;
    net::packet_t packet {event.type == ENET_EVENT_TYPE_RECEIVE ? event.packet : nullptr};
    auto session = get_session(event.peer, event.data);
    if (session) session->pingTimeout = std::chrono::steady_clock::now() + config::stream.ping_timeout;
    switch (pl_enet_dispatch(event.type, session != nullptr, session && session->state == session::state_e::RUNNING,
                             packet ? packet->dataLength : 0)) {
      case 1: BOOST_LOG(info) << "CLIENT CONNECTED"; break;
      case 2: BOOST_LOG(info) << "CLIENT DISCONNECTED"; session::stop(*session); break;
      case 3: {
        const uint16_t type = uint16_t(packet->data[0]) | (uint16_t(packet->data[1]) << 8);
        call(type, session, {(char *)packet->data + 2, packet->dataLength - 2}, false);
        break;
      }
      case 4: enet_peer_disconnect_now(event.peer, 0); break;
      case 5: BOOST_LOG(info) << "CLIENT DISCONNECTED"; break;
    }""" + code[end:]
