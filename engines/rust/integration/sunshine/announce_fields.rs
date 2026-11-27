attributes! {
    channels, b"x-nv-audio.surround.numChannels", None; // audio.channels
    mask, b"x-nv-audio.surround.channelMask", None; // audio.mask
    packet_duration, b"x-nv-aqos.packetDuration", Some(5); // audio.packetDuration
    quality, b"x-nv-audio.surround.AudioQuality", None; // audio.flags[audio::config_t::HIGH_QUALITY]
    control, b"x-nv-general.useReliableUdp", Some(1); // controlProtocolType
    packet_size, b"x-nv-video[0].packetSize", None; // packetsize
    fec_min, b"x-nv-vqos[0].fec.minRequiredFecPackets", Some(0); // minRequiredFecPackets
    client_flags, b"x-ml-general.featureFlags", Some(0); // mlFeatureFlags
    audio_qos, b"x-nv-aqos.qosTrafficType", Some(4); // audioQosType
    video_qos, b"x-nv-vqos[0].qosTrafficType", Some(5); // videoQosType
    encryption, b"x-ss-general.encryptionEnabled", Some(0); // encryptionFlagsEnabled
    nv_flags, b"x-nv-general.featureFlags", Some(135); // -
    height, b"x-nv-video[0].clientViewportHt", None; // monitor.height
    width, b"x-nv-video[0].clientViewportWd", None; // monitor.width
    fps, b"x-nv-video[0].maxFPS", None; // monitor.framerate
    refresh, b"x-nv-video[0].clientRefreshRateX100", Some(0); // monitor.framerateX100
    bitrate, b"x-nv-vqos[0].bw.maximumBitrateKbps", None; // monitor.bitrate
    slices, b"x-nv-video[0].videoEncoderSlicesPerFrame", None; // monitor.slicesPerFrame
    refs, b"x-nv-video[0].maxNumReferenceFrames", None; // monitor.numRefFrames
    colorspace, b"x-nv-video[0].encoderCscMode", Some(0); // monitor.encoderCscMode
    codec, b"x-nv-vqos[0].bitStreamFormat", Some(0); // monitor.videoFormat
    hdr, b"x-nv-video[0].dynamicRangeMode", Some(0); // monitor.dynamicRange
    chroma, b"x-ss-video[0].chromaSamplingType", Some(0); // monitor.chromaSamplingType
    intra, b"x-ss-video[0].intraRefresh", Some(0); // monitor.enableIntraRefresh
    configured, b"x-ml-video.configuredBitrateKbps", Some(0); // -
}
