    using clock = std::chrono::steady_clock;
    auto ns = [](clock::time_point t) { return std::chrono::duration_cast<std::chrono::nanoseconds>(t.time_since_epoch()).count(); };
    std::shared_ptr<img_t> image;
    auto current = [&]() { image.reset(); return (bool)factory->IsCurrent(); };
    auto wait = [&](int64_t due) {
      auto target = clock::time_point(std::chrono::duration_cast<clock::duration>(std::chrono::nanoseconds(due)));
      auto now = clock::now();
      if (target > now) timer->sleep_for(target - now);
      sleep_overshoot_logger.first_point(target);
      sleep_overshoot_logger.second_point_now_and_log();
    };
    auto take = [&](int timeout, int64_t* stamp, bool* have_image) {
      auto status = snapshot(pull_free_image_cb, image, std::chrono::milliseconds(timeout), *cursor);
      *have_image = bool(image);
      if (status == capture_e::ok && image) *stamp = ns(image->frame_timestamp.value_or(clock::now()));
      return static_cast<int>(status);
    };
    auto push = [&](bool captured) { return push_captured_image_cb(std::move(image), captured); };
    auto release = [&]() { return static_cast<int>(release_snapshot()); };
    struct Lease {
      decltype(current)& is_current; decltype(wait)& wait_until; decltype(take)& snapshot;
      decltype(push)& publish; decltype(release)& release_frame;
    } lease {current,wait,take,push,release};
    const PlCaptureOps ops {
      [](void* p) {return static_cast<Lease*>(p)->is_current();},
      []() -> int64_t {return std::chrono::duration_cast<std::chrono::nanoseconds>(clock::now().time_since_epoch()).count();},
      [](void* p, int64_t due) {static_cast<Lease*>(p)->wait_until(due);},
      [](void* p, int timeout, int64_t* stamp, bool* image) {return static_cast<Lease*>(p)->snapshot(timeout,stamp,image);},
      [](void* p, bool captured) {return static_cast<Lease*>(p)->publish(captured);},
      [](void* p) {return static_cast<Lease*>(p)->release_frame();}
    };
    BOOST_LOG(info) << "Rust desktop capture pacing " << rate.num << "/" << rate.den;
    return static_cast<capture_e>(pl_capture_run(&lease, rate, &ops));
