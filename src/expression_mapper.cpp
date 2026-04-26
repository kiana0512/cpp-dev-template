#include "vh/expression_mapper.h"

#include <algorithm>
#include <chrono>
#include <map>

namespace vh {
namespace {

double clamp01(double v) {
    return std::max(0.0, std::min(1.0, v));
}

std::uint64_t nowMs() {
    const auto now = std::chrono::time_point_cast<std::chrono::milliseconds>(
        std::chrono::system_clock::now());
    return static_cast<std::uint64_t>(now.time_since_epoch().count());
}

// ---------------------------------------------------------------------------
// Blendshape keys follow the ARKit naming convention where possible.
// This makes the protocol directly compatible with MetaHuman and ARKit
// face-tracking pipelines, and is the standard the UE5 receiver will expect.
//
// Full key set (15 keys):
//   jawOpen, mouthSmile, mouthFrown,
//   eyeBlinkLeft, eyeBlinkRight,
//   eyeSquintLeft, eyeSquintRight,
//   eyeWideLeft, eyeWideRight,
//   browInnerUp, browDown,
//   browOuterUpLeft, browOuterUpRight,
//   noseSneerLeft, noseSneerRight
// ---------------------------------------------------------------------------

using BS = std::map<std::string, double>;

BS makeNeutral() {
    return {
        {"jawOpen",          0.10},
        {"mouthSmile",       0.00},
        {"mouthFrown",       0.00},
        {"eyeBlinkLeft",     0.03},
        {"eyeBlinkRight",    0.03},
        {"eyeSquintLeft",    0.00},
        {"eyeSquintRight",   0.00},
        {"eyeWideLeft",      0.00},
        {"eyeWideRight",     0.00},
        {"browInnerUp",      0.00},
        {"browDown",         0.00},
        {"browOuterUpLeft",  0.00},
        {"browOuterUpRight", 0.00},
        {"noseSneerLeft",    0.00},
        {"noseSneerRight",   0.00},
    };
}

BS makeHappy() {
    return {
        {"jawOpen",          0.20},
        {"mouthSmile",       0.78},
        {"mouthFrown",       0.00},
        {"eyeBlinkLeft",     0.05},
        {"eyeBlinkRight",    0.05},
        // Squinting eyes are the visual signature of genuine happiness.
        {"eyeSquintLeft",    0.32},
        {"eyeSquintRight",   0.32},
        {"eyeWideLeft",      0.00},
        {"eyeWideRight",     0.00},
        {"browInnerUp",      0.18},
        {"browDown",         0.00},
        {"browOuterUpLeft",  0.10},
        {"browOuterUpRight", 0.10},
        {"noseSneerLeft",    0.00},
        {"noseSneerRight",   0.00},
    };
}

BS makeSad() {
    return {
        {"jawOpen",          0.12},
        {"mouthSmile",       0.00},
        {"mouthFrown",       0.58},
        {"eyeBlinkLeft",     0.10},
        {"eyeBlinkRight",    0.10},
        {"eyeSquintLeft",    0.08},
        {"eyeSquintRight",   0.08},
        {"eyeWideLeft",      0.00},
        {"eyeWideRight",     0.00},
        // Raised inner brows are the most reliable sadness signal.
        {"browInnerUp",      0.48},
        {"browDown",         0.08},
        {"browOuterUpLeft",  0.00},
        {"browOuterUpRight", 0.00},
        {"noseSneerLeft",    0.00},
        {"noseSneerRight",   0.00},
    };
}

BS makeAngry() {
    return {
        {"jawOpen",          0.18},
        {"mouthSmile",       0.00},
        {"mouthFrown",       0.30},
        {"eyeBlinkLeft",     0.02},
        {"eyeBlinkRight",    0.02},
        {"eyeSquintLeft",    0.22},
        {"eyeSquintRight",   0.22},
        {"eyeWideLeft",      0.00},
        {"eyeWideRight",     0.00},
        {"browInnerUp",      0.00},
        {"browDown",         0.72},
        {"browOuterUpLeft",  0.00},
        {"browOuterUpRight", 0.00},
        // Nose wrinkle amplifies the angry expression significantly.
        {"noseSneerLeft",    0.55},
        {"noseSneerRight",   0.55},
    };
}

BS makeSurprised() {
    return {
        {"jawOpen",          0.55},
        {"mouthSmile",       0.00},
        {"mouthFrown",       0.00},
        {"eyeBlinkLeft",     0.00},
        {"eyeBlinkRight",    0.00},
        {"eyeSquintLeft",    0.00},
        {"eyeSquintRight",   0.00},
        // Wide eyes are the defining feature of surprise.
        {"eyeWideLeft",      0.82},
        {"eyeWideRight",     0.82},
        {"browInnerUp",      0.38},
        {"browDown",         0.00},
        {"browOuterUpLeft",  0.50},
        {"browOuterUpRight", 0.50},
        {"noseSneerLeft",    0.00},
        {"noseSneerRight",   0.00},
    };
}

BS emotionPreset(const std::string& label) {
    if (label == "happy")     return makeHappy();
    if (label == "sad")       return makeSad();
    if (label == "angry")     return makeAngry();
    if (label == "surprised") return makeSurprised();
    return makeNeutral();
}

// Modulate jawOpen and mouth shape based on audio RMS and current phoneme.
// This is a simplified approximation of lip-sync without a full V2F model.
void applyAudioAndPhoneme(BS& bs,
                          double rms,
                          const std::string& phoneme_hint) {
    // RMS drives jaw opening: more volume -> more open mouth.
    const double jaw_from_rms = clamp01(0.12 + rms * 0.88);
    bs["jawOpen"] = std::max(bs["jawOpen"], jaw_from_rms);

    if (phoneme_hint == "a") {
        bs["jawOpen"] = clamp01(bs["jawOpen"] + 0.10);
    } else if (phoneme_hint == "o") {
        bs["jawOpen"] = clamp01(bs["jawOpen"] + 0.06);
    } else if (phoneme_hint == "i") {
        bs["mouthSmile"] = clamp01(bs["mouthSmile"] + 0.10);
        bs["eyeSquintLeft"]  = clamp01(bs["eyeSquintLeft"]  + 0.05);
        bs["eyeSquintRight"] = clamp01(bs["eyeSquintRight"] + 0.05);
    } else if (phoneme_hint == "e") {
        bs["jawOpen"] = clamp01(bs["jawOpen"] + 0.04);
        bs["mouthSmile"] = clamp01(bs["mouthSmile"] + 0.04);
    } else if (phoneme_hint == "u") {
        bs["mouthFrown"] = clamp01(bs["mouthFrown"] + 0.05);
    }
    // "rest" and unrecognised phonemes: no additional adjustment.
}

} // namespace

ExpressionFrame ExpressionMapper::buildFrame(const MapperInput& input) {
    ExpressionFrame frame;
    frame.timestamp_ms = nowMs();
    frame.character_id = input.character_id;
    frame.sequence_id  = input.sequence_id;

    frame.audio.playing      = true;
    frame.audio.rms          = clamp01(input.rms);
    frame.audio.phoneme_hint = input.phoneme_hint;

    frame.emotion.label      = input.emotion_label;
    frame.emotion.confidence = clamp01(input.emotion_confidence);

    frame.blendshapes = emotionPreset(input.emotion_label);
    applyAudioAndPhoneme(frame.blendshapes, frame.audio.rms, input.phoneme_hint);

    // Subtle head pose per emotion — keeps the character feeling alive.
    if (input.emotion_label == "happy") {
        frame.head_pose.yaw = -3.0;
    } else if (input.emotion_label == "sad") {
        frame.head_pose.pitch = -5.0;
    } else if (input.emotion_label == "surprised") {
        frame.head_pose.pitch = 4.0;
    } else if (input.emotion_label == "angry") {
        frame.head_pose.yaw   =  2.0;
        frame.head_pose.pitch = -1.5;
    }

    // Layer optional per-preset head-pose adjustments on top of the emotion default.
    // Values are clamped to the [-90, 90] range validated by PacketValidator.
    auto clamp90 = [](double v) { return std::max(-90.0, std::min(90.0, v)); };
    frame.head_pose.pitch = clamp90(frame.head_pose.pitch + input.head_pose_pitch);
    frame.head_pose.yaw   = clamp90(frame.head_pose.yaw   + input.head_pose_yaw);
    frame.head_pose.roll  = clamp90(frame.head_pose.roll  + input.head_pose_roll);

    frame.meta.source = "expression_mapper";
    frame.meta.text   = input.text;

    return frame;
}

} // namespace vh
