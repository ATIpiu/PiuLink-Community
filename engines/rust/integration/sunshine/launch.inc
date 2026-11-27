  enum class launch_action : int32_t { keep_audio, set_audio, bind_capture, probe_display, encryption_allowed, execute, publish };
  struct launch_facts { bool resume, keys, audio, app; int32_t running; bool idle, prepared, lease_conflict; };
  struct launch_result { int32_t reason, status; bool rollback; };
  extern "C" launch_result pl_https_launch(const launch_facts *, void *, int32_t (*)(void *, launch_action));

  void start_or_resume(bool resume, bool &host_audio, resp_https_t response, req_https_t request) {
    std::unique_lock display_lock {display_lease_mutex};
    auto args = request->parse_query_string();
    auto present = [&](const char *key) { return args.find(key) != args.end(); };
    const auto prepared = get_arg(args, "piulinkPrepared", "");
    const launch_facts facts {
      resume, present("rikey") && present("rikeyid"), present("localAudioPlayMode"), present("appid"),
      proc::proc.running(), rtsp_stream::session_count() == 0,
      valid_display_lease(prepared, request->authenticated_peer(), get_arg(args, "mode", "")),
      !prepared.empty() || std::chrono::steady_clock::now() < display_lease_expires
    };

    std::shared_ptr<rtsp_stream::launch_session_t> session;
    pt::ptree tree;
    std::exception_ptr pending;
    auto action = [&](launch_action op) -> int32_t {
      switch (op) {
        case launch_action::set_audio:
          host_audio = util::from_view(get_arg(args, "localAudioPlayMode"));
          [[fallthrough]];
        case launch_action::keep_audio:
          session = make_launch_session(host_audio, args);
          return 0;
        case launch_action::bind_capture:
          session->capture_route = std::make_shared<video::live_capture_t>(display_lease_output);
          active_capture = session->capture_route;
          return 0;
        case launch_action::probe_display:
          display_device::configure_display(config::video, *session);
          return video::probe_encoders();
        case launch_action::encryption_allowed:
          return session->rtsp_cipher || net::encryption_mode_for_address(request->remote_endpoint().address()) != config::ENCRYPTION_MODE_MANDATORY;
        case launch_action::execute: {
          auto id = util::from_view(get_arg(args, "appid"));
          return id > 0 ? proc::proc.execute((int)id, session) : 0;
        }
        case launch_action::publish:
          tree.put("root.sessionUrl0", std::format("{}{}:{}", session->rtsp_url_scheme,
              net::addr_to_url_escaped_string(request->local_endpoint().address()),
              static_cast<int>(net::map_port(rtsp_stream::RTSP_SETUP_PORT))));
          rtsp_stream::launch_session_raise(session);
          return 0;
      }
      return -1;
    };
    // Never unwind a C++ platform exception through Rust.
    auto safe_action = [&](launch_action op) noexcept -> int32_t {
      if (!pending) {
        try { return action(op); } catch (...) { pending = std::current_exception(); }
      }
      return op == launch_action::encryption_allowed ? 0 : -1;
    };
    auto result = pl_https_launch(&facts, &safe_action, [](void *ctx, launch_action op) {
      return (*static_cast<decltype(safe_action)*>(ctx))(op);
    });
    bool rollback = result.rollback || (pending && !resume && facts.idle && !facts.prepared && session);
    auto guard = util::fail_guard([&]() { if (rollback) display_device::revert_configuration(); });
    if (pending) std::rethrow_exception(pending);
    static constexpr const char *messages[] = {
      "", "No running app to resume", "", "An app is already running on this host",
      "Display preparation expired or belongs to another paired client",
      "Failed to initialize video capture/encoding. Is a display connected and turned on?",
      "Encryption is mandatory for this host but unsupported by the client", "Failed to start the specified application"
    };
    tree.put("root.<xmlattr>.status_code", result.status);
    if (result.reason) {
      tree.put("root.<xmlattr>.status_message", result.reason == 2 ?
          (resume ? "Missing a required resume parameter" : "Missing a required launch parameter") : messages[result.reason]);
    }
    if (result.reason != 4) {
      bool resume_field = result.reason == 0 || result.reason == 5 ? resume : result.reason <= 3;
      tree.put(resume_field ? "root.resume" : "root.gamesession", result.reason == 0 ? 1 : 0);
    }
    std::ostringstream data;
    pt::write_xml(data, tree);
    response->write(data.str());
    response->close_connection_after_response = true;
  }

  void launch(bool &audio, resp_https_t response, req_https_t request) { start_or_resume(false, audio, response, request); }
  void resume(bool &audio, resp_https_t response, req_https_t request) { start_or_resume(true, audio, response, request); }
