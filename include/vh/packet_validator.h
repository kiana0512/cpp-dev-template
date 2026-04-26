#pragma once

#include <string>
#include <vector>

#include "vh/frame_packet.h"

namespace vh {

    class PacketValidator {
    public:
        static std::vector<std::string> validate(const ExpressionFrame& frame);
        static bool isValid(const ExpressionFrame& frame);
    };

} // namespace vh