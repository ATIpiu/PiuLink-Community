"""Bind Moonlight's mouse batching thread and Sunshine's input handlers."""
import re

def between(text, first, after, replacement):
    a = text.index(first)
    return text[:a] + replacement + text[text.index(after, a):]

def client(text):
    text=between(text,"                bool more = false;\n\n                if (currentRelativeMouseState", "                // Don't hold",
        """                bool more = pl_pointer_relative((uint8_t*)&holder->packet.mouseMoveRel,
                    &currentRelativeMouseState.deltaX, &currentRelativeMouseState.deltaY, AppVersionQuad[0] >= 5);

""")
    # Input thread dispatches using header.magic before serializing the current coordinates.
    text=between(text,"            holder->packet.mouseMoveAbs.x =", "            // The state change",
        """            pl_pointer_absolute((uint8_t*)&holder->packet.mouseMoveAbs,
                currentAbsoluteMouseState.x, currentAbsoluteMouseState.y,
                currentAbsoluteMouseState.width, currentAbsoluteMouseState.height);

""")
    text=between(text,"    holder->packet.mouseButton.header.size","    err = LbqOfferQueueItem",
        "    pl_pointer_button((uint8_t*)&holder->packet.mouseButton, action, button, AppVersionQuad[0] >= 5);\n\n")
    text,n=re.subn(r"holder->packet.scroll.header.size = BE32\(sizeof\(NV_SCROLL_PACKET\).*?holder->packet.scroll.zero3 = 0;",
        "pl_pointer_scroll((uint8_t*)&holder->packet.scroll, scrollAmount, AppVersionQuad[0] >= 5);",text,flags=re.S)
    if n!=2: raise ValueError("Unexpected Moonlight scroll writers")
    return between(text,"    holder->packet.hscroll.header.size","    err = LbqOfferQueueItem",
        "    pl_pointer_hscroll((uint8_t*)&holder->packet.hscroll, scrollAmount);\n\n")

def host(text):
    read="    PlPointer pointer {};\n    if (!pl_pointer_read(reinterpret_cast<const uint8_t*>(packet), sizeof(*packet), &pointer)) return;\n"
    text=text.replace("    platf::move_mouse(platf_input, util::endian::big(packet->deltaX), util::endian::big(packet->deltaY));",
                      read+"    platf::move_mouse(platf_input, pointer.x, pointer.y);")
    text=between(text,"    float x = util::endian::big(packet->x);","    auto tpcoords = client_to_touchport(input, {x, y}",
        read+"    if (!pointer.w || !pointer.h) return;\n    float x=pointer.x, y=pointer.y, width=pointer.w, height=pointer.h;\n")
    text=between(text,"    auto release = util::endian::little(packet->header.magic) == MOUSE_BUTTON_UP","    if (button > 0",
        read+"    bool release = pointer.release;\n    auto button = pointer.button;\n")
    for prefix, call in [("v","scroll"),("h","hscroll")]:
        signature="void passthrough(std::shared_ptr<input_t> &input, "+("PNV_SCROLL_PACKET" if prefix=="v" else "PSS_HSCROLL_PACKET")+" packet)"
        start=text.index("    if (config::input.high_resolution_scrolling)",text.index(signature))
        end=text.index("\n  }",start)
        text=text[:start]+read+f"    int delta = pl_scroll_delta(pointer.x, config::input.high_resolution_scrolling, &input->accumulated_{prefix}scroll_delta);\n    if (delta) platf::{call}(platf_input, delta);"+text[end:]
    return text
