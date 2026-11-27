    PlOpusFormat format {stream.sampleRate, stream.channelCount, stream.streams, stream.coupledStreams,
                         config.packetDuration * stream.sampleRate / 1000, {}};
    if (stream.channelCount < 1 || stream.channelCount > 8) { packets->stop(); return; }
    std::copy_n(stream.mapping, stream.channelCount, format.mapping);
    struct pipeline_t {
      sample_queue_t samples;
      decltype(packets) output;
      void *channel;
      std::vector<float> pending;
    } pipeline {samples, packets, channel_data, {}};
    const PlEncoderOps backend {
      [](const PlOpusFormat *f, int mode, int *err) -> void * {
        return opus_multistream_encoder_create(f->rate, f->channels, f->streams, f->coupled, f->mapping, mode, err);
      },
      [](void *handle, int request, int value) {
        return opus_multistream_encoder_ctl(static_cast<OpusMSEncoder*>(handle), request, value);
      },
      [](void *handle) { opus_multistream_encoder_destroy(static_cast<OpusMSEncoder*>(handle)); },
      [](void *opaque, const float **data, size_t *count) -> int {
        auto &p = *static_cast<pipeline_t*>(opaque);
        auto sample = p.samples->pop();
        if (!sample) return 0;
        p.pending = std::move(*sample);
        *data = p.pending.data();
        *count = p.pending.size();
        return 1;
      },
      [](void *handle, const float *pcm, int samples, uint8_t *packet, int cap) {
        return opus_multistream_encode_float(static_cast<OpusMSEncoder*>(handle), pcm, samples, packet, cap);
      },
      [](void *opaque, const uint8_t *data, int bytes) -> int {
        auto &p = *static_cast<pipeline_t*>(opaque);
        try {
          buffer_t packet {static_cast<size_t>(bytes)};
          std::copy_n(data, bytes, packet.begin());
          p.output->raise(p.channel, std::move(packet));
          return 0;
        } catch (const std::bad_alloc &) {
          return OPUS_ALLOC_FAIL;
        }
      }
    };
    auto result = pl_opus_encode_stream(&format, stream.bitrate, &backend, &pipeline);
    if (result < 0) {
      BOOST_LOG(error) << "Rust audio encode failed: "sv << opus_strerror(result);
      packets->stop();
    }
