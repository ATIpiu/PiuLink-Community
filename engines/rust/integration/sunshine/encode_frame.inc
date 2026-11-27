    NV_ENC_MAP_INPUT_RESOURCE mapped = {NV_ENC_MAP_INPUT_RESOURCE_VER};
    mapped.registeredResource = registered_input_buffer;
    NV_ENC_PIC_PARAMS picture = {NV_ENC_PIC_PARAMS_VER};
    picture.inputWidth = encoder_params.width;
    picture.inputHeight = encoder_params.height;
    picture.encodePicFlags = force_idr ? NV_ENC_PIC_FLAG_FORCEIDR : 0;
    picture.inputTimeStamp = frame_index;
    picture.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
    picture.outputBitstream = output_bitstream;
    picture.completionEvent = async_event_handle;
    NV_ENC_LOCK_BITSTREAM lock_bitstream = {NV_ENC_LOCK_BITSTREAM_VER};
    lock_bitstream.outputBitstream = output_bitstream;
    lock_bitstream.doNotWait = async_event_handle ? 1 : 0;
    nvenc_encoded_frame encoded_frame {};
    struct Context {
      nvenc_base *engine;
      NV_ENC_MAP_INPUT_RESOURCE &input;
      NV_ENC_PIC_PARAMS &picture;
      NV_ENC_LOCK_BITSTREAM &bits;
      nvenc_encoded_frame &result;
    } ctx {this,mapped,picture,lock_bitstream,encoded_frame};
    const PlNvOps ops {
      nvenc->nvEncMapInputResource, nvenc->nvEncEncodePicture, nvenc->nvEncLockBitstream,
      nvenc->nvEncUnlockBitstream, nvenc->nvEncUnmapInputResource,
      [](void *opaque) {
        auto &c = *static_cast<Context*>(opaque);
        c.picture.inputBuffer = c.input.mappedResource;
        c.picture.bufferFmt = c.input.mappedBufferFmt;
      },
      [](void *opaque, uint32_t ms) {return static_cast<Context*>(opaque)->engine->wait_for_async_event(ms);},
      [](void *opaque) {
        auto &c = *static_cast<Context*>(opaque);
        const auto *data = static_cast<const uint8_t*>(c.bits.bitstreamBufferPtr);
        if (!data || !c.bits.bitstreamSizeInBytes) return false;
        try { c.result.data.assign(data, data + c.bits.bitstreamSizeInBytes); }
        catch (const std::bad_alloc &) { return false; }
        c.result.frame_index = c.bits.outputTimeStamp;
        c.result.idr = c.bits.pictureType == NV_ENC_PIC_TYPE_IDR;
        c.result.after_ref_frame_invalidation = c.engine->encoder_state.rfi_needs_confirmation;
        return true;
      },
      [](void *opaque,uint8_t stage,int32_t status) {
        static constexpr const char *names[] = {"map","submit","wait","lock","copy","unlock","unmap"};
        auto *engine = static_cast<Context*>(opaque)->engine;
        if (status) engine->nvenc_failed(static_cast<NVENCSTATUS>(status));
        BOOST_LOG(error) << "Rust NVENC " << names[stage] << " failed; status=" << status;
      }
    };
    const PlNvFrame frame {encoder, &ctx, &mapped, &picture, &lock_bitstream, &mapped.mappedResource,
                           output_bitstream, async_event_handle != nullptr};
    if (!pl_encode_frame(&frame, &ops)) return {};
