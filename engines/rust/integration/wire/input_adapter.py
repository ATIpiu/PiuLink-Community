"""Install the shared Rust gamepad codec and the Sunshine queue merge operation."""

def host_inputs(text):
    begin = text.index("  enum class batch_result_e")
    end = text.index("  /**\n   * @brief Called on a thread pool", begin)
    text = text[:begin] + text[end:]
    start = text.index("        auto batchable_entry = *i;")
    end = text.index("          // Stop batching", start)
    text = text[:start] + """        const auto &candidate = *i;
        const auto batch_result = pl_batch_input(entry.data(), entry.size(), candidate.data(), candidate.size());
        if (batch_result == PL_BATCH_STOP) {
""" + text[end:]
    text = text.replace("batch_result_e::batched", "PL_BATCH_MERGED")
    handler = text.index("void passthrough(std::shared_ptr<input_t> &input, PNV_MULTI_CONTROLLER_PACKET packet)")
    start = text.index("    if (packet->controllerNumber < 0", handler)
    end = text.index("    auto bf_new = gamepad_state.buttonFlags;", start)
    body = text[start:text.index("    std::uint16_t bf =",start)]
    decoded = """    PlPad pad {};
    if (!pl_pad_read(reinterpret_cast<const uint8_t*>(packet), sizeof(*packet), &pad)) return;
"""
    text = text[:start] + decoded + body.replace("packet->controllerNumber","pad.slot").replace("packet->activeGamepadMask","pad.mask") + """    platf::gamepad_state_t gamepad_state {
      pad.buttons, pad.lt, pad.rt, pad.axes[0], pad.axes[1], pad.axes[2], pad.axes[3]
    };
""" + text[end:]
    start = text.index("    bf = gamepad_state.buttonFlags ^")
    end = text.index("    gamepad.gamepad_state = gamepad_state;",start)
    text = text[:start] + text[start:end].replace("    bf = ", "    auto bf = ",1).replace("controller = packet->controllerNumber","controller = pad.slot") + text[end:]
    signature = "  void passthrough(std::shared_ptr<input_t> &input, std::vector<std::uint8_t> &&input_data) {"
    if text.count(signature) != 1:
        raise ValueError("Unexpected Sunshine input queue entry")
    return text.replace(signature, signature + "\n    if (!pl_valid_input(input_data.data(), input_data.size())) return;")

def client_inputs(text):
    start = text.index("    // HACK: We previously used a short for the buttonFlags")
    end = text.index("    holder = allocatePacketHolder",start)
    text = text[:start] + text[end:]
    start = text.index("    // Send each controller on a separate channel",start)
    end = text.index("    err = LbqOfferQueueItem",start)
    text = text[:start] + """    PlPad pad = { controllerNumber, (uint16_t)activeGamepadMask, (uint32_t)buttonFlags,
                  leftTrigger, rightTrigger, {leftStickX, leftStickY, rightStickX, rightStickY} };
    int slot = pl_pad_write((uint8_t*)&holder->packet, sizeof(holder->packet), AppVersionQuad[0], IS_SUNSHINE(), &pad);
    if (slot < 0) {
        freePacketHolder(holder);
        return -1;
    }
    holder->channelId = CTRL_CHANNEL_GAMEPAD_BASE + slot;
    holder->enetPacketFlags = ENET_PACKET_FLAG_RELIABLE;

""" + text[end:]
    start = text.index("                if (newPkt->buttonFlags != origPkt->buttonFlags")
    end = text.index("                    // Batching not allowed",start)
    text = text[:start] + """                NV_MULTI_CONTROLLER_PACKET merged = *origPkt;
                if (pl_batch_input((uint8_t*)&merged, sizeof(merged), (const uint8_t*)newPkt, sizeof(*newPkt)) != PL_BATCH_MERGED) {
""" + text[end:]
    start = text.index("                origPkt->leftTrigger =")
    end = text.index("                // Free the batched",start)
    return text[:start] + "                *origPkt = merged;\n\n" + text[end:]
