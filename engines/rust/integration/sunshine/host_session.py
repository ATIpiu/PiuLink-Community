"""Bind the Windows broadcast workers to the Rust session lifetime."""

def integrate(code):
    start = code.index("  static inline void while_starting_do_nothing(")
    end = code.index("  class control_server_t", start)
    code = code[:start] + code[end:]
    code = code.replace("  namespace session { extern std::atomic_uint running_sessions; }", "")
    code = code.replace("std::atomic<session::state_e> state;", "std::unique_ptr<PlHostSession, decltype(&pl_host_free)> state {pl_host_new(), pl_host_free};")
    code = code.replace("session->state == session::state_e::RUNNING", "session::state(*session) == session::state_e::RUNNING")
    code = code.replace("session->state.load(std::memory_order_acquire)", "session::state(*session)")
    code = code.replace("session::running_sessions != 1", "pl_host_active() != 1")
    code = code.replace("while_starting_do_nothing(session->state);", "if (!pl_host_wait(session->state.get())) return;")
    code = code.replace("    std::atomic_uint running_sessions;", "")
    code = code.replace("return session.state.load(std::memory_order_relaxed);", "return static_cast<state_e>(pl_host_state(session.state.get()));")
    start = code.index("      while_starting_do_nothing(session.state);")
    end = code.index("      session.shutdown_event->raise",start)
    code = code[:start] + "      if (!pl_host_stop(session.state.get())) return;\n" + code[end:]
    code = code.replace("if (--running_sessions == 0)", "if (pl_host_finish(session.state.get()))")
    start = code.index("      // Insert this session into the session list")
    end = code.index("      auto addr = ",start)
    code = code[:start] + code[end:]
    start = code.index("      session.audioThread = std::thread")
    end = code.index("        platf::streaming_will_start();", start)
    code = code[:start] + """      if (!pl_host_begin(session.state.get())) return -1;
      bool first;
      try {
        // Both workers wait until the control registry has the fully initialized peer.
        session.audioThread = std::thread {audioThread, &session};
        session.videoThread = std::thread {videoThread, &session};
        auto lg = session.broadcast_ref->control_server._sessions.lock();
        session.broadcast_ref->control_server._sessions->push_back(&session);
        first = pl_host_publish(session.state.get(), true);
      } catch (const std::exception &e) {
        pl_host_publish(session.state.get(), false);
        if (session.audioThread.joinable()) session.audioThread.join();
        if (session.videoThread.joinable()) session.videoThread.join();
        BOOST_LOG(error) << "Failed to initialize streaming workers: " << e.what();
        return -1;
      }
      if (first) {
""" + code[end:]
    code = code.replace("      session->state.store(state_e::STOPPED, std::memory_order_relaxed);", "")
    return code
