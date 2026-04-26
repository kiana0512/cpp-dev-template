#include "vh/packet_validator.h"

#include <cmath>

namespace vh {
    namespace {

        bool inUnitRange(double v) {
            return std::isfinite(v) && v >= 0.0 && v <= 1.0;
        }

        bool inHeadRange(double v) {
            return std::isfinite(v) && v >= -90.0 && v <= 90.0;
        }

    } // namespace

    std::vector<std::string> PacketValidator::validate(const ExpressionFrame& frame) {
        std::vector<std::string> errors;

        if (frame.type != "expression_frame") {
            errors.emplace_back("type must be expression_frame");
        }

        if (frame.version.empty()) {
            errors.emplace_back("version must not be empty");
        }

        if (frame.character_id.empty()) {
            errors.emplace_back("character_id must not be empty");
        }

        if (!inUnitRange(frame.audio.rms)) {
            errors.emplace_back("audio.rms must be in [0, 1]");
        }

        if (!inUnitRange(frame.emotion.confidence)) {
            errors.emplace_back("emotion.confidence must be in [0, 1]");
        }

        for (const auto& [name, value] : frame.blendshapes) {
            if (name.empty()) {
                errors.emplace_back("blendshape key must not be empty");
                continue;
            }

            if (!inUnitRange(value)) {
                errors.emplace_back("blendshape '" + name + "' must be in [0, 1]");
            }
        }

        if (!inHeadRange(frame.head_pose.pitch)) {
            errors.emplace_back("head_pose.pitch must be in [-90, 90]");
        }
        if (!inHeadRange(frame.head_pose.yaw)) {
            errors.emplace_back("head_pose.yaw must be in [-90, 90]");
        }
        if (!inHeadRange(frame.head_pose.roll)) {
            errors.emplace_back("head_pose.roll must be in [-90, 90]");
        }

        return errors;
    }

    bool PacketValidator::isValid(const ExpressionFrame& frame) {
        return validate(frame).empty();
    }

} // namespace vh