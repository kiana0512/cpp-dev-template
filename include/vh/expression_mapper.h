#pragma once

#include <cstdint>
#include <string>

#include "vh/frame_packet.h"

namespace vh {

    struct MapperInput {
        std::string   text;
        std::string   emotion_label{"neutral"};
        double        emotion_confidence{1.0};
        double        rms{0.2};
        std::string   phoneme_hint{"a"};
        std::string   character_id{"miku_yyb_001"};
        std::uint64_t sequence_id{1};

        // Optional head-pose adjustment added on top of the emotion-based default.
        // Defaults to 0 so existing callers are unaffected.
        double head_pose_pitch{0.0};
        double head_pose_yaw{0.0};
        double head_pose_roll{0.0};
    };

    class ExpressionMapper {
    public:
        static ExpressionFrame buildFrame(const MapperInput& input);
    };

} // namespace vh