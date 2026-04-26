#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <string>

#include "vh/expression_mapper.h"
#include "vh/frame_packet.h"
#include "vh/frame_preset.h"
#include "vh/transport_client.h"

namespace vh {

// SenderService encapsulates the full build -> validate -> serialize -> send
// pipeline.  Both the CLI (main.cpp) and the GUI share this class.
//
// Loop control is intentionally left to the caller (e.g. a UI timer / main loop)
// so that the core service stays free of GUI-framework dependencies.
//
// Log callback convention  (level, message):
//   "INFO"  – normal operational event
//   "FRAME" – per-frame summary (emotion / phoneme / rms / bytes)
//   "JSON"  – the raw single-line JSON payload that was actually sent
//   "WARN"  – recoverable anomaly (bad phoneme fallback, empty text …)
//   "ERROR" – failure (connection lost, validation failed, send error …)
class SenderService {
public:
    struct Options {
        std::string   mode{"tcp"};
        std::string   host{"127.0.0.1"};
        std::uint16_t port{7001};
        std::string   output_path{"outputs/frames.jsonl"};
        std::string   character_id{"miku_yyb_001"};
        std::uint64_t start_seq{1};
    };

    struct SendResult {
        bool          success{false};
        std::string   json_payload;       // the exact line that was sent
        std::string   error_message;      // non-empty when success == false
        std::uint64_t seq{0};             // sequence_id used for this frame
        std::size_t   payload_bytes{0};   // length of json_payload + '\n'
    };

    using LogCallback = std::function<void(std::string /*level*/, std::string /*msg*/)>;

    explicit SenderService(Options opts, LogCallback logger = nullptr);
    ~SenderService();

    // ---- Transport lifecycle -----------------------------------------------

    // Open the underlying transport.  Returns false on failure (error logged).
    bool connect();

    // Close the transport cleanly.
    void disconnect();

    bool isConnected() const { return connected_; }

    // ---- Frame sending ------------------------------------------------------

    // Build one ExpressionFrame from `input`, validate, serialize, and send.
    // MapperInput::sequence_id and ::character_id are overridden internally.
    SendResult sendFrame(const MapperInput& input);

    // Convenience: convert a FramePreset to MapperInput and call sendFrame().
    SendResult sendPreset(const FramePreset& preset);

    // ---- State accessors ---------------------------------------------------

    std::uint64_t currentSeq()     const { return seq_; }
    void          resetSeq(std::uint64_t s) { seq_ = s; }

    // Sequence ID of the last frame that was successfully sent (0 if none).
    std::uint64_t lastSentSeq()    const { return last_sent_seq_; }

    // Raw JSON payload of the last successfully sent frame ("" if none).
    const std::string& lastJson()  const { return last_json_; }

    // Error message from the last failed sendFrame() ("" if last call succeeded).
    const std::string& lastError() const { return last_error_; }

    const Options& options()       const { return opts_; }

private:
    void log(const std::string& level, const std::string& msg);

    Options                           opts_;
    LogCallback                       logger_;
    std::unique_ptr<ITransportClient> transport_;
    std::uint64_t                     seq_{1};
    bool                              connected_{false};

    // Per-call result cache — updated on every sendFrame() invocation.
    std::uint64_t last_sent_seq_{0};
    std::string   last_json_;
    std::string   last_error_;
};

} // namespace vh
