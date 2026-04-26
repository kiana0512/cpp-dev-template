#include "vh/json_protocol.h"

#include <fstream>
#include <sstream>
#include <stdexcept>

#include <nlohmann/json.hpp>

namespace vh {
namespace {

using json = nlohmann::json;

json toJson(const ExpressionFrame& frame) {
    return json{
        {"type", frame.type},
        {"version", frame.version},
        {"timestamp_ms", frame.timestamp_ms},
        {"character_id", frame.character_id},
        {"sequence_id", frame.sequence_id},
        {"audio", {
            {"playing", frame.audio.playing},
            {"rms", frame.audio.rms},
            {"phoneme_hint", frame.audio.phoneme_hint}
        }},
        {"emotion", {
            {"label", frame.emotion.label},
            {"confidence", frame.emotion.confidence}
        }},
        {"blendshapes", frame.blendshapes},
        {"head_pose", {
            {"pitch", frame.head_pose.pitch},
            {"yaw", frame.head_pose.yaw},
            {"roll", frame.head_pose.roll}
        }},
        {"meta", {
            {"source", frame.meta.source},
            {"text", frame.meta.text}
        }}
    };
}

ExpressionFrame fromJson(const json& j) {
    ExpressionFrame frame;

    frame.type = j.value("type", "expression_frame");
    frame.version = j.value("version", "1.0");
    frame.timestamp_ms = j.value("timestamp_ms", 0ULL);
    frame.character_id = j.value("character_id", "miku_yyb_001");
    frame.sequence_id = j.value("sequence_id", 0ULL);

    if (j.contains("audio")) {
        const auto& a = j.at("audio");
        frame.audio.playing = a.value("playing", false);
        frame.audio.rms = a.value("rms", 0.0);
        frame.audio.phoneme_hint = a.value("phoneme_hint", "rest");
    }

    if (j.contains("emotion")) {
        const auto& e = j.at("emotion");
        frame.emotion.label = e.value("label", "neutral");
        frame.emotion.confidence = e.value("confidence", 1.0);
    }

    if (j.contains("blendshapes")) {
        frame.blendshapes = j.at("blendshapes").get<std::map<std::string, double>>();
    }

    if (j.contains("head_pose")) {
        const auto& h = j.at("head_pose");
        frame.head_pose.pitch = h.value("pitch", 0.0);
        frame.head_pose.yaw = h.value("yaw", 0.0);
        frame.head_pose.roll = h.value("roll", 0.0);
    }

    if (j.contains("meta")) {
        const auto& m = j.at("meta");
        frame.meta.source = m.value("source", "demo");
        frame.meta.text = m.value("text", "");
    }

    return frame;
}

} // namespace

std::string JsonProtocol::toJsonString(const ExpressionFrame& frame, bool pretty) {
    const auto j = toJson(frame);
    return pretty ? j.dump(2) : j.dump();
}

ExpressionFrame JsonProtocol::fromJsonString(const std::string& text) {
    return fromJson(nlohmann::json::parse(text));
}

void JsonProtocol::writeExpressionFrameToFile(const std::filesystem::path& path,
                                              const ExpressionFrame& frame,
                                              bool pretty) {
    std::ofstream out(path, std::ios::binary);
    if (!out) {
        throw std::runtime_error("Failed to open file for writing: " + path.string());
    }

    out << toJsonString(frame, pretty);
}

ExpressionFrame JsonProtocol::readExpressionFrameFromFile(const std::filesystem::path& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) {
        throw std::runtime_error("Failed to open file for reading: " + path.string());
    }

    std::ostringstream buffer;
    buffer << in.rdbuf();
    return fromJsonString(buffer.str());
}

} // namespace vh