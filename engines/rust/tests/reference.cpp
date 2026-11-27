#include "piulink_content_rate.h"
#include <iostream>
#include <string>
int main() {
    nvenc::piulink::content_rate_controller_t rate;
    std::string op;
    while (std::cin >> op) {
        unsigned a, b, key, target = 0;
        size_t bytes;
        if (op == "reset") { std::cin >> a >> b; rate.reset(a, b); }
        else if (op == "enable") { std::cin >> a; rate.set_enabled(a); }
        else if (op == "applied") { std::cin >> a; rate.applied(a); }
        else if (op == "ceiling") { std::cin >> a; rate.set_ceiling(a); }
        else if (op == "frame") {
            std::cin >> bytes >> b >> key;
            target = rate.observe(bytes, b, key).value_or(0);
        } else return 2;
        std::cout << target << " " << rate.output_kbps() << " " << rate.average_qp_x100() << "\n";
    }
}
