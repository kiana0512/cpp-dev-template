#pragma once

#include <filesystem>
#include <string>

#include "vh/frame_packet.h"

namespace vh {

    class JsonProtocol {
    public:
        static std::string toJsonString(const ExpressionFrame& frame, bool pretty = true);
        static ExpressionFrame fromJsonString(const std::string& text);

        static void writeExpressionFrameToFile(const std::filesystem::path& path,
                                               const ExpressionFrame& frame,
                                               bool pretty = true);

        static ExpressionFrame readExpressionFrameFromFile(const std::filesystem::path& path);
    };

} // namespace vh