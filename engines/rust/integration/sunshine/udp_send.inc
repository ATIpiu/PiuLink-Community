  template<class Info>
  static bool rust_send(Info &info, const char *headers, size_t header_size,
                        const PlUdpSpan *spans, size_t num_spans, size_t unit,
                        size_t offset, size_t count) {
    const boost::asio::ip::udp::endpoint target {info.target_address, info.target_port};
    std::array<uint8_t,16> source {};
    if (info.source_address.is_v6()) {
      source = info.source_address.to_v6().to_bytes();
    } else {
      const auto v4 = info.source_address.to_v4().to_bytes();
      std::copy(v4.begin(), v4.end(), source.begin());
    }
    const PlUdpBatch batch {info.native_socket, target.data(), static_cast<int>(target.size()),
                            source.data(), info.source_address.is_v6(), reinterpret_cast<const uint8_t*>(headers),
                            header_size, spans, num_spans, unit, offset, count};
    return pl_udp_send(&batch);
  }

  bool send_batch(batched_send_info_t &info) {
    static_assert(sizeof(buffer_descriptor_t) == sizeof(PlUdpSpan));
    static_assert(offsetof(buffer_descriptor_t, buffer) == offsetof(PlUdpSpan, data));
    static_assert(offsetof(buffer_descriptor_t, size) == offsetof(PlUdpSpan, len));
    return rust_send(info, info.headers, info.header_size,
                     reinterpret_cast<const PlUdpSpan*>(info.payload_buffers.data()), info.payload_buffers.size(),
                     info.payload_size, info.block_offset, info.block_count);
  }

  bool send(send_info_t &info) {
    const PlUdpSpan payload {reinterpret_cast<const uint8_t*>(info.payload),info.payload_size};
    auto sent = rust_send(info, info.header, info.header_size, &payload, 1, info.payload_size, 0, 1);
    if (!sent) BOOST_LOG(warning) << "WSASendMsg() failed: "sv << WSAGetLastError();
    return sent;
  }
