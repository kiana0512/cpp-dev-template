#include "vh/sender_service.h"

#include <sstream>
#include <vector>

#include "vh/json_protocol.h"
#include "vh/packet_validator.h"

namespace vh {

namespace {

bool isValidPhoneme(const std::string& p) {
    static const std::vector<std::string> kValid = {"rest", "a", "i", "u", "e", "o"};
    for (const auto& v : kValid) {
        if (v == p) return true;
    }
    return false;
}

} // namespace

SenderService::SenderService(Options opts, LogCallback logger)
    : opts_(std::move(opts))
    , logger_(std::move(logger))
    , seq_(opts_.start_seq)
{
    log("INFO", "SenderService created: mode=" + opts_.mode
        + " target=" + opts_.host + ":" + std::to_string(opts_.port)
        + " character=" + opts_.character_id
        + " start_seq=" + std::to_string(opts_.start_seq));
}

SenderService::~SenderService() {
    if (connected_) {
        disconnect();
    }
}

bool SenderService::connect() {
    if (connected_) {
        log("WARN", "Already connected — ignoring redundant connect()");
        return true;
    }

    log("INFO", "Connecting: mode=" + opts_.mode
        + " target=" + opts_.host + ":" + std::to_string(opts_.port));

    transport_ = createTransport(opts_.mode, opts_.host, opts_.port, opts_.output_path);
    if (!transport_) {
        last_error_ = "Unknown transport mode: \"" + opts_.mode
            + "\". Valid values: tcp | file | stdout";
        log("ERROR", last_error_);
        return false;
    }

    if (!transport_->connect()) {
        last_error_ = "Transport connect() failed: host=" + opts_.host
            + " port=" + std::to_string(opts_.port)
            + " mode=" + opts_.mode
            + ". Verify the receiver is running and the address is reachable.";
        log("ERROR", last_error_);
        transport_.reset();
        return false;
    }

    connected_ = true;
    log("INFO", "Connected successfully to "
        + opts_.host + ":" + std::to_string(opts_.port)
        + " (mode=" + opts_.mode + ")");
    return true;
}

void SenderService::disconnect() {
    if (transport_) {
        transport_->close();
        transport_.reset();
    }
    const bool wasConnected = connected_;
    connected_ = false;
    if (wasConnected) {
        log("INFO", "Disconnected from "
            + opts_.host + ":" + std::to_string(opts_.port));
    }
}

SenderService::SendResult SenderService::sendFrame(const MapperInput& inputIn) {
    SendResult result;
    result.seq = seq_;
    last_error_.clear();

    if (!connected_ || !transport_) {
        last_error_ =
            "Not connected — call connect() before sendFrame(). "
            "mode=" + opts_.mode
            + " target=" + opts_.host + ":" + std::to_string(opts_.port);
        result.error_message = last_error_;
        log("ERROR", last_error_);
        return result;
    }

    // Override managed fields; leave everything else as supplied by caller.
    MapperInput input   = inputIn;
    input.sequence_id   = seq_;
    input.character_id  = opts_.character_id;

    // Phoneme sanity-check with fallback.
    if (!isValidPhoneme(input.phoneme_hint)) {
        log("WARN", "Invalid phoneme_hint='" + input.phoneme_hint
            + "' — falling back to 'rest'. Valid: rest / a / i / u / e / o");
        input.phoneme_hint = "rest";
    }

    if (input.text.empty()) {
        log("WARN", "meta.text is empty (seq=" + std::to_string(seq_) + ")");
    }

    // ---- Build frame -------------------------------------------------------
    ExpressionFrame frame;
    try {
        frame = ExpressionMapper::buildFrame(input);
    } catch (const std::exception& ex) {
        last_error_ =
            "ExpressionMapper::buildFrame() threw: " + std::string(ex.what())
            + " (seq=" + std::to_string(seq_) + ")";
        result.error_message = last_error_;
        log("ERROR", last_error_);
        return result;
    }

    // ---- Validate ----------------------------------------------------------
    const auto errors = PacketValidator::validate(frame);
    if (!errors.empty()) {
        std::ostringstream oss;
        oss << "PacketValidator failed (seq=" << seq_ << "):";
        for (std::size_t i = 0; i < errors.size(); ++i) {
            oss << (i == 0 ? " " : " | ") << errors[i];
        }
        last_error_ = oss.str();
        result.error_message = last_error_;
        log("ERROR", last_error_);
        return result;
    }

    // ---- Serialize ---------------------------------------------------------
    std::string payload;
    try {
        payload = JsonProtocol::toJsonString(frame, false);
    } catch (const std::exception& ex) {
        last_error_ =
            "JsonProtocol::toJsonString() threw: " + std::string(ex.what())
            + " (seq=" + std::to_string(seq_) + ")";
        result.error_message = last_error_;
        log("ERROR", last_error_);
        return result;
    }

    result.json_payload  = payload;
    result.payload_bytes = payload.size() + 1; // +1 for the '\n' appended by transport

    log("JSON", payload);
    log("FRAME", "seq="    + std::to_string(seq_)
        + " emotion=" + frame.emotion.label
        + " phoneme=" + frame.audio.phoneme_hint
        + " rms="     + std::to_string(frame.audio.rms)
        + " pitch="   + std::to_string(frame.head_pose.pitch)
        + " yaw="     + std::to_string(frame.head_pose.yaw)
        + " bytes="   + std::to_string(result.payload_bytes));

    // ---- Send --------------------------------------------------------------
    if (!transport_->sendText(payload)) {
        last_error_ =
            "transport_->sendText() failed at seq=" + std::to_string(seq_)
            + ". The connection may have been dropped."
            + " target=" + opts_.host + ":" + std::to_string(opts_.port);
        result.error_message = last_error_;
        log("ERROR", last_error_);
        connected_ = false; // mark as dropped so callers can detect it
        return result;
    }

    // Success — update caches.
    last_json_     = payload;
    last_sent_seq_ = seq_;
    result.success = true;
    ++seq_;

    log("INFO", "Sent seq=" + std::to_string(last_sent_seq_)
        + " emotion=" + frame.emotion.label
        + " bytes="   + std::to_string(result.payload_bytes)
        + " -> "      + opts_.host + ":" + std::to_string(opts_.port));

    return result;
}

SenderService::SendResult SenderService::sendPreset(const FramePreset& preset) {
    MapperInput input;
    input.text               = preset.text;
    input.emotion_label      = preset.emotion;
    input.phoneme_hint       = preset.phoneme;
    input.rms                = preset.rms;
    input.emotion_confidence = preset.confidence;
    input.head_pose_pitch    = preset.headPitch;
    input.head_pose_yaw      = preset.headYaw;
    input.head_pose_roll     = preset.headRoll;
    // sequence_id and character_id are overridden inside sendFrame()
    return sendFrame(input);
}

void SenderService::log(const std::string& level, const std::string& msg) {
    if (logger_) {
        logger_(level, msg);
    }
}

} // namespace vh
