#pragma once

#include <string>

namespace vh {

// FramePreset represents one selectable preset entry loaded from
// configs/presets.json (or the built-in defaults).
//
// head_pose_* values are additive offsets layered on top of the
// emotion-driven head-pose defaults in ExpressionMapper::buildFrame().
// Setting them to 0 (the default) leaves the emotion defaults intact.
//
// This struct uses only std::string so it is usable in the core library
// (sender_service, CLI) and any host that loads presets.json without
// pulling in a GUI framework.
struct FramePreset {
    std::string name;
    std::string text;
    std::string emotion{"neutral"};   // neutral | happy | sad | angry | surprised
    std::string phoneme{"rest"};      // rest | a | i | u | e | o
    double      rms{0.3};
    double      confidence{0.9};
    double      headPitch{0.0};       // degrees, additive
    double      headYaw{0.0};
    double      headRoll{0.0};
};

} // namespace vh