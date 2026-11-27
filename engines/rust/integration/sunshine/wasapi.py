from pathlib import Path
import shutil


def integrate(path):
    shutil.copy2(Path(__file__).with_suffix(".h"), path.parent)
    code = '#include "wasapi.h"\n' + path.read_text()
    start = code.index("      auto sample_size = sample_out.size();")
    end = code.index("\n    }", start)
    code = code[:start] + """      PlWasapi capture {audio_capture.get(), audio_event.get(), (uint32_t)channels,
        (uint32_t)default_latency_ms, continuous_audio, std::begin(sample_buf),
        (size_t)(std::end(sample_buf) - std::begin(sample_buf)), &buffered_samples, this,
        [](void *ctx) {
          auto self = static_cast<mic_wasapi_t*>(ctx);
          if (!self->endpt_notification.check_default_render_device_changed()) return false;
          if (self->default_endpt_changed_cb) (*self->default_endpt_changed_cb)();
          return true;
        },
        [](uint32_t event) {
          if (event == 1) { BOOST_LOG(debug) << "Audio capture signaled buffer discontinuity"; }
          else { BOOST_LOG(warning) << "Audio capture buffer overflow"; }
        }
      };
      return (capture_e)pl_wasapi_sample(&capture, sample_out.data(), sample_out.size());""" + code[end:]
    start = code.index("    capture_e _fill_buffer()")
    end = code.index("  public:", start)
    code = code[:start] + code[end:]
    path.write_text(code.replace("      sample_buf_pos = std::begin(sample_buf);", "      buffered_samples = 0;")
                        .replace("    float *sample_buf_pos;", "    size_t buffered_samples = 0;"))
