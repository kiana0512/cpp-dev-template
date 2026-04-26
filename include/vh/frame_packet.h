#pragma once

#include <cstdint>
#include <map>
#include <string>

namespace vh {

    struct AudioState {
        bool playing{false};
        double rms{0.0};
        std::string phoneme_hint{"rest"};
    };

    struct EmotionState {
        std::string label{"neutral"};
        double confidence{1.0};
    };

    struct HeadPose {
        double pitch{0.0};
        double yaw{0.0};
        double roll{0.0};
    };

    struct MetaInfo {
        std::string source{"demo"};
        std::string text;
    };

    struct ExpressionFrame {
        std::string type{"expression_frame"};
        std::string version{"1.0"};
        std::uint64_t timestamp_ms{0};
        std::string character_id{"miku_yyb_001"};
        std::uint64_t sequence_id{0};

        AudioState audio;
        EmotionState emotion;
        std::map<std::string, double> blendshapes;
        HeadPose head_pose;
        MetaInfo meta;
    };

} // namespace vh