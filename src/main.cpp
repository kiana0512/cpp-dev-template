#include <algorithm>
#include <chrono>
#include <cmath>
#include <csignal>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <limits>
#include <map>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#include <nlohmann/json.hpp>

#ifdef _WIN32
    #ifndef NOMINMAX
        #define NOMINMAX
    #endif
    #include <windows.h>
    #include <shellapi.h>  // CommandLineToArgvW
#endif

#include "vh/expression_mapper.h"
#include "vh/json_protocol.h"
#include "vh/packet_validator.h"
#include "vh/transport_client.h"

namespace {

// ---------------------------------------------------------------------------
// Signal handler — lets the main loop exit cleanly on Ctrl+C.
// ---------------------------------------------------------------------------

volatile std::sig_atomic_t g_interrupted = 0;

void onSignal(int) {
    g_interrupted = 1;
}

// ---------------------------------------------------------------------------
// Common helpers
// ---------------------------------------------------------------------------

std::uint64_t currentTimestampMs() {
    return static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::system_clock::now().time_since_epoch())
            .count());
}

// ---------------------------------------------------------------------------
// Console UTF-8 (Windows only)
// ---------------------------------------------------------------------------

void setupConsoleUtf8() {
#ifdef _WIN32
    SetConsoleCP(CP_UTF8);
    SetConsoleOutputCP(CP_UTF8);
#endif
}

// ---------------------------------------------------------------------------
// UTF-16 -> UTF-8 conversion (Windows only)
// ---------------------------------------------------------------------------

#ifdef _WIN32
std::string wideToUtf8(const wchar_t* wide) {
    if (!wide || *wide == L'\0') {
        return {};
    }

    const int needed = WideCharToMultiByte(
        CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    if (needed <= 0) {
        throw std::runtime_error("WideCharToMultiByte(size query) failed");
    }

    std::string result(static_cast<std::size_t>(needed), '\0');

    const int written = WideCharToMultiByte(
        CP_UTF8, 0, wide, -1, result.data(), needed, nullptr, nullptr);
    if (written <= 0) {
        throw std::runtime_error("WideCharToMultiByte(conversion) failed");
    }

    result.resize(static_cast<std::size_t>(written - 1));
    return result;
}

std::vector<std::string> buildUtf8Args(int argc, char** argv) {
    int wargc = 0;
    LPWSTR* wargv = CommandLineToArgvW(GetCommandLineW(), &wargc);
    if (!wargv) {
        return std::vector<std::string>(argv, argv + argc);
    }

    std::vector<std::string> result;
    result.reserve(static_cast<std::size_t>(wargc));
    for (int i = 0; i < wargc; ++i) {
        result.push_back(wideToUtf8(wargv[i]));
    }

    LocalFree(wargv);
    return result;
}
#else
std::vector<std::string> buildUtf8Args(int argc, char** argv) {
    return std::vector<std::string>(argv, argv + argc);
}
#endif

// ---------------------------------------------------------------------------
// Argument parsing helpers
// argv_vec[0] is the program path — parseArgs starts at index 1.
// ---------------------------------------------------------------------------

std::map<std::string, std::string>
parseArgs(const std::vector<std::string>& argv_vec) {
    std::map<std::string, std::string> args;
    const int n = static_cast<int>(argv_vec.size());

    for (int i = 1; i < n; ++i) {
        const std::string& key = argv_vec[i];
        if (key.rfind("--", 0) != 0) {
            continue;
        }

        if (i + 1 < n && argv_vec[i + 1].rfind("--", 0) != 0) {
            args[key] = argv_vec[++i];
        } else {
            args[key] = "true";
        }
    }

    return args;
}

[[noreturn]] void throwBadArg(const std::string& key,
                              const std::string& value,
                              const std::string& reason) {
    throw std::invalid_argument(
        "Invalid value for " + key + " = '" + value + "': " + reason);
}

double getDouble(const std::map<std::string, std::string>& args,
                 const std::string& key,
                 double fallback) {
    const auto it = args.find(key);
    if (it == args.end()) {
        return fallback;
    }

    try {
        std::size_t pos = 0;
        const double value = std::stod(it->second, &pos);
        if (pos != it->second.size()) {
            throwBadArg(key, it->second, "not a pure floating-point number");
        }
        return value;
    } catch (const std::exception&) {
        throwBadArg(key, it->second, "cannot parse as double");
    }
}

double getPositiveDouble(const std::map<std::string, std::string>& args,
                         const std::string& key,
                         double fallback) {
    const double value = getDouble(args, key, fallback);
    if (value <= 0.0) {
        throwBadArg(key, std::to_string(value), "must be > 0");
    }
    return value;
}

int getInt(const std::map<std::string, std::string>& args,
           const std::string& key,
           int fallback) {
    const auto it = args.find(key);
    if (it == args.end()) {
        return fallback;
    }

    try {
        std::size_t pos = 0;
        const long long value = std::stoll(it->second, &pos);
        if (pos != it->second.size()) {
            throwBadArg(key, it->second, "not a pure integer");
        }
        if (value < std::numeric_limits<int>::min() ||
            value > std::numeric_limits<int>::max()) {
            throwBadArg(key, it->second, "out of int range");
        }
        return static_cast<int>(value);
    } catch (const std::exception&) {
        throwBadArg(key, it->second, "cannot parse as int");
    }
}

int getNonNegativeInt(const std::map<std::string, std::string>& args,
                      const std::string& key,
                      int fallback) {
    const int value = getInt(args, key, fallback);
    if (value < 0) {
        throwBadArg(key, std::to_string(value), "must be >= 0");
    }
    return value;
}

std::uint64_t getUInt64(const std::map<std::string, std::string>& args,
                        const std::string& key,
                        std::uint64_t fallback) {
    const auto it = args.find(key);
    if (it == args.end()) {
        return fallback;
    }

    try {
        std::size_t pos = 0;
        const unsigned long long value = std::stoull(it->second, &pos);
        if (pos != it->second.size()) {
            throwBadArg(key, it->second, "not a pure unsigned integer");
        }
        return static_cast<std::uint64_t>(value);
    } catch (const std::exception&) {
        throwBadArg(key, it->second, "cannot parse as uint64");
    }
}

std::uint16_t getPort(const std::map<std::string, std::string>& args,
                      const std::string& key,
                      std::uint16_t fallback) {
    const auto value = getUInt64(args, key, fallback);
    if (value > 65535ULL) {
        throwBadArg(key, std::to_string(value), "must be in [0, 65535]");
    }
    return static_cast<std::uint16_t>(value);
}

// ---------------------------------------------------------------------------
// Usage
// ---------------------------------------------------------------------------

void printUsage() {
    std::cout
        << "vh_demo_sender -- VirtualHuman expression frame sender\n\n"
        << "Single frame (default):\n"
        << "  vh_demo_sender --config configs/sample_frame.json\n"
        << "  vh_demo_sender --text \"Hello\" --emotion happy --rms 0.45\n"
        << "  vh_demo_sender --text \"Hello\" --emotion happy"
           " --mode tcp --host 127.0.0.1 --port 7001\n\n"
        << "Continuous / demo:\n"
        << "  vh_demo_sender --demo --mode stdout\n"
        << "  vh_demo_sender --demo --loop --fps 24 --mode tcp --port 7001\n"
        << "  vh_demo_sender --demo --count 50 --fps 24 --mode file\n\n"
        << "Parameters:\n"
        << "  --config PATH        Load a single frame from a JSON file\n"
        << "  --text TEXT          Input text (UTF-8; Chinese supported)\n"
        << "  --emotion LABEL      neutral | happy | sad | angry | surprised\n"
        << "  --confidence F       Emotion confidence [0,1]  (default: 0.95)\n"
        << "  --rms F              Audio RMS [0,1]           (default: 0.40)\n"
        << "  --phoneme C          rest | a | i | u | e | o  (default: a)\n"
        << "  --character ID       Character ID  (default: miku_yyb_001)\n"
        << "  --seq N              Starting sequence ID      (default: 1)\n"
        << "  --pitch F            Head pose pitch override  (optional)\n"
        << "  --yaw F              Head pose yaw override    (optional)\n"
        << "  --roll F             Head pose roll override   (optional)\n\n"
        << "  --demo               Cycle through built-in emotion sequence\n"
        << "  --loop               Run indefinitely until Ctrl+C\n"
        << "  --count N            Total frames in manual/demo (default: 1; full preset run: --presets-json)\n"
        << "  --fps F              Target frame rate         (default: 10)\n"
        << "  --interval MS        Frame interval in ms (overrides --fps)\n\n"
        << "  --mode MODE          stdout (default) | file | tcp\n"
        << "  --host HOST          TCP host  (default: 127.0.0.1)\n"
        << "  --port PORT          TCP port  (default: 7001)\n"
        << "  --output PATH        File output path"
           "  (default: outputs/frames.jsonl)\n\n"
        << "  --presets-json PATH  Single TCP connection: send all presets from JSON (same schema as configs/presets.json)\n"
        << "  --hold-sec N         Seconds per preset in presets-json mode (default: 3)\n"
        << "  --preset-gap-ms N    Milliseconds between presets in presets-json mode (default: 100)\n"
        << "  --morph-smoke-test   Send ARKit-only keys cycling eyeSquint/blink/wide (default 90 frames)\n";
}

// ---------------------------------------------------------------------------
// Built-in demo sequence
// ---------------------------------------------------------------------------

struct DemoStep {
    const char* emotion;
    const char* phoneme;
    double      rms;
    const char* text;
};

constexpr DemoStep kDemoSteps[] = {
    {"neutral",   "rest", 0.00, "..."},
    {"happy",     "a",    0.45, "Hello, I am Miku!"},
    {"happy",     "i",    0.40, "Nice to meet you."},
    {"surprised", "a",    0.55, "Oh! Really?"},
    {"surprised", "o",    0.50, "That is amazing!"},
    {"sad",       "e",    0.22, "I see..."},
    {"sad",       "rest", 0.08, "That is a shame."},
    {"neutral",   "rest", 0.00, "..."},
    {"angry",     "a",    0.60, "No way!"},
    {"angry",     "u",    0.50, "I cannot believe it!"},
    {"happy",     "a",    0.42, "Just kidding!"},
    {"happy",     "i",    0.35, "Everything is fine."},
    {"neutral",   "rest", 0.02, "..."},
};

constexpr std::size_t kDemoStepCount =
    sizeof(kDemoSteps) / sizeof(kDemoSteps[0]);

vh::ExpressionFrame buildManualFrame(
    const std::map<std::string, std::string>& args,
    const std::string& character_id,
    std::uint64_t seq) {
    vh::MapperInput input;
    input.text =
        args.count("--text") ? args.at("--text") : "Hello.";
    input.emotion_label =
        args.count("--emotion") ? args.at("--emotion") : "happy";
    input.emotion_confidence =
        getDouble(args, "--confidence", 0.95);
    input.rms =
        getDouble(args, "--rms", 0.40);
    input.phoneme_hint =
        args.count("--phoneme") ? args.at("--phoneme") : "a";
    input.character_id = character_id;
    input.sequence_id  = seq;

    auto frame = vh::ExpressionMapper::buildFrame(input);

    if (args.count("--pitch")) {
        frame.head_pose.pitch = getDouble(args, "--pitch", frame.head_pose.pitch);
    }
    if (args.count("--yaw")) {
        frame.head_pose.yaw = getDouble(args, "--yaw", frame.head_pose.yaw);
    }
    if (args.count("--roll")) {
        frame.head_pose.roll = getDouble(args, "--roll", frame.head_pose.roll);
    }

    return frame;
}

vh::ExpressionFrame buildDemoFrame(const DemoStep& step,
                                   const std::string& character_id,
                                   std::uint64_t seq) {
    vh::MapperInput input;
    input.text               = step.text;
    input.emotion_label      = step.emotion;
    input.rms                = step.rms;
    input.phoneme_hint       = step.phoneme;
    input.emotion_confidence = 0.90;
    input.character_id       = character_id;
    input.sequence_id        = seq;

    return vh::ExpressionMapper::buildFrame(input);
}

// ---------------------------------------------------------------------------
// Single TCP connection — full presets.json sequence (recommended for UE)
// ---------------------------------------------------------------------------

int runPresetsJsonFile(const std::map<std::string, std::string>& args) {
    const std::string path = args.at("--presets-json");
    std::ifstream ifs(path, std::ios::binary);
    if (!ifs) {
        std::cerr << "[ERROR] Cannot open --presets-json: " << path << "\n";
        return 1;
    }

    nlohmann::json j;
    try {
        ifs >> j;
    } catch (const std::exception& ex) {
        std::cerr << "[ERROR] presets JSON parse failed: " << ex.what() << "\n";
        return 1;
    }

    if (!j.contains("presets") || !j["presets"].is_array()) {
        std::cerr << "[ERROR] JSON must contain a \"presets\" array.\n";
        return 1;
    }

    const double fps = getPositiveDouble(args, "--fps", 24.0);
    const int hold_sec = std::max(1, getInt(args, "--hold-sec", 3));
    const int interval_ms =
        args.count("--interval")
            ? std::max(getNonNegativeInt(args, "--interval", 100), 1)
            : std::max(static_cast<int>(1000.0 / fps), 1);
    const int count_per_preset = std::max(
        1,
        static_cast<int>(std::round(fps * static_cast<double>(hold_sec))));
    const int gap_ms = getNonNegativeInt(args, "--preset-gap-ms", 100);

    const std::string mode =
        args.count("--mode") ? args.at("--mode") : "stdout";
    const std::string host =
        args.count("--host") ? args.at("--host") : "127.0.0.1";
    const std::uint16_t port = getPort(args, "--port", 7001);
    const std::string out_path =
        args.count("--output") ? args.at("--output") : "outputs/frames.jsonl";

    auto transport = vh::createTransport(mode, host, port, out_path);
    if (!transport) {
        std::cerr << "[ERROR] Unknown transport mode: " << mode << "\n";
        return 3;
    }

    std::cerr << "[PRESETS-JSON] path=" << path << " presets=" << j["presets"].size()
              << " count_per_preset=" << count_per_preset << " interval_ms=" << interval_ms
              << " gap_ms=" << gap_ms << " mode=" << mode << "\n";

    std::cerr << "[INFO] Single connection: connect() once, send entire sequence, then close.\n";
    if (!transport->connect()) {
        std::cerr << "[ERROR] connect() failed (exit 4). target=" << host << ":" << port << "\n";
        return 4;
    }
    std::cerr << "[INFO] connect() OK.\n";

    std::uint64_t seq = getUInt64(args, "--seq", 1);
    const std::string character_id =
        args.count("--character") ? args.at("--character") : "miku_yyb_001";

    int sent = 0;
    const auto& presets = j["presets"];
    const std::size_t preset_count = presets.size();

    for (std::size_t pi = 0; pi < preset_count; ++pi) {
        const auto& p = presets[pi];
        const std::string pname = p.value("name", std::string("?"));
        std::cerr << "[PRESET " << (pi + 1) << "/" << preset_count << "] " << pname << "\n";

        vh::MapperInput in;
        in.text = p.value("text", std::string(""));
        in.emotion_label = p.value("emotion", std::string("neutral"));
        in.phoneme_hint = p.value("phoneme", std::string("a"));
        in.rms = p.value("rms", 0.4);
        in.emotion_confidence = p.value("confidence", 0.9);
        in.character_id = character_id;
        in.head_pose_pitch = 0.0;
        in.head_pose_yaw = 0.0;
        in.head_pose_roll = 0.0;
        if (p.contains("head_pose") && p["head_pose"].is_object()) {
            const auto& hp = p["head_pose"];
            in.head_pose_pitch = hp.value("pitch", 0.0);
            in.head_pose_yaw = hp.value("yaw", 0.0);
            in.head_pose_roll = hp.value("roll", 0.0);
        }

        for (int fi = 0; fi < count_per_preset; ++fi) {
            if (g_interrupted) {
                break;
            }
            in.sequence_id = seq;
            vh::ExpressionFrame frame = vh::ExpressionMapper::buildFrame(in);
            frame.sequence_id = seq;
            frame.timestamp_ms = currentTimestampMs();

            const auto errors = vh::PacketValidator::validate(frame);
            if (!errors.empty()) {
                std::cerr << "Validation failed (seq=" << seq << "):\n";
                for (const auto& e : errors) {
                    std::cerr << "  - " << e << "\n";
                }
                transport->close();
                return 2;
            }

            const std::string payload = vh::JsonProtocol::toJsonString(frame, false);
            if (!transport->sendText(payload)) {
                std::cerr << "[ERROR] sendText failed seq=" << seq << " (exit 5)\n";
                transport->close();
                return 5;
            }

            if (mode != "stdout") {
                std::cout << "[seq=" << seq << " emotion=" << frame.emotion.label
                          << " phoneme=" << frame.audio.phoneme_hint << " preset=" << pname << "]\n";
            }

            ++seq;
            ++sent;
            if (!g_interrupted && (fi + 1 < count_per_preset)) {
                std::this_thread::sleep_for(std::chrono::milliseconds(interval_ms));
            }
        }

        if (g_interrupted) {
            break;
        }
        if (pi + 1 < preset_count && gap_ms > 0) {
            std::cerr << "[PRESET-GAP] " << gap_ms << " ms before next preset\n";
            std::this_thread::sleep_for(std::chrono::milliseconds(gap_ms));
        }
    }

    transport->close();
    std::cerr << "[INFO] presets-json finished. Frames sent: " << sent << "\n";
    return 0;
}

// ---------------------------------------------------------------------------
// Morph smoke test — only ARKit keys mapped to known JP morphs on receiver
// ---------------------------------------------------------------------------

int runMorphSmokeTest(const std::map<std::string, std::string>& args) {
    const int count = std::max(1, getInt(args, "--count", 90));
    const double fps = getPositiveDouble(args, "--fps", 24.0);
    const int interval_ms =
        args.count("--interval")
            ? std::max(getNonNegativeInt(args, "--interval", 100), 1)
            : std::max(static_cast<int>(1000.0 / fps), 1);

    const std::string mode =
        args.count("--mode") ? args.at("--mode") : "stdout";
    const std::string host =
        args.count("--host") ? args.at("--host") : "127.0.0.1";
    const std::uint16_t port = getPort(args, "--port", 7001);
    const std::string out_path =
        args.count("--output") ? args.at("--output") : "outputs/frames.jsonl";

    auto transport = vh::createTransport(mode, host, port, out_path);
    if (!transport || !transport->connect()) {
        std::cerr << "[ERROR] morph-smoke-test connect failed\n";
        return 4;
    }

    std::uint64_t seq = getUInt64(args, "--seq", 1);
    const std::string character_id =
        args.count("--character") ? args.at("--character") : "miku_yyb_001";

    std::cerr << "[MORPH-SMOKE] frames=" << count << " interval_ms=" << interval_ms << "\n";

    for (int i = 0; i < count; ++i) {
        if (g_interrupted) {
            break;
        }
        vh::MapperInput in;
        in.text = "smoke";
        in.emotion_label = "neutral";
        in.rms = 0.0;
        in.phoneme_hint = "rest";
        in.emotion_confidence = 1.0;
        in.character_id = character_id;
        in.sequence_id = seq;

        vh::ExpressionFrame frame = vh::ExpressionMapper::buildFrame(in);
        frame.blendshapes.clear();

        const std::uint64_t phase = (seq / 30) % 3;
        if (phase == 0) {
            frame.blendshapes["eyeSquintLeft"] = 1.0;
            frame.blendshapes["eyeSquintRight"] = 1.0;
        } else if (phase == 1) {
            frame.blendshapes["eyeBlinkLeft"] = 1.0;
        } else {
            frame.blendshapes["eyeWideLeft"] = 1.0;
            frame.blendshapes["eyeWideRight"] = 1.0;
        }

        frame.sequence_id = seq;
        frame.timestamp_ms = currentTimestampMs();

        const auto errors = vh::PacketValidator::validate(frame);
        if (!errors.empty()) {
            transport->close();
            return 2;
        }

        const std::string payload = vh::JsonProtocol::toJsonString(frame, false);
        if (!transport->sendText(payload)) {
            std::cerr << "[ERROR] sendText failed seq=" << seq << "\n";
            transport->close();
            return 5;
        }

        std::cout << "[smoke seq=" << seq << " phase=" << phase << "]\n";
        ++seq;
        if (i + 1 < count) {
            std::this_thread::sleep_for(std::chrono::milliseconds(interval_ms));
        }
    }

    transport->close();
    std::cerr << "[INFO] morph-smoke-test done.\n";
    return 0;
}

// ---------------------------------------------------------------------------
// Core send loop — receives a UTF-8 argument vector.
// ---------------------------------------------------------------------------

int run(const std::vector<std::string>& argv_vec) {
    try {
        const auto args = parseArgs(argv_vec);

        if (args.count("--help") || argv_vec.size() <= 1) {
            printUsage();
            return 0;
        }

        if (args.count("--presets-json")) {
            return runPresetsJsonFile(args);
        }
        if (args.count("--morph-smoke-test")) {
            return runMorphSmokeTest(args);
        }

        const bool demo_mode = args.count("--demo") > 0;
        const bool loop_mode = args.count("--loop") > 0;
        const int count = getNonNegativeInt(args, "--count", loop_mode ? 0 : 1);

        if (!loop_mode && count == 0) {
            std::cout << "Nothing to send (--count 0).\n";
            return 0;
        }

        const double fps = getPositiveDouble(args, "--fps", 10.0);
        const int interval_ms =
            args.count("--interval")
                ? std::max(getNonNegativeInt(args, "--interval", 100), 1)
                : std::max(static_cast<int>(1000.0 / fps), 1);

        const std::string mode =
            args.count("--mode") ? args.at("--mode") : "stdout";
        const std::string host =
            args.count("--host") ? args.at("--host") : "127.0.0.1";
        const std::uint16_t port = getPort(args, "--port", 7001);
        const std::string out_path =
            args.count("--output") ? args.at("--output")
                                   : "outputs/frames.jsonl";

        auto transport = vh::createTransport(mode, host, port, out_path);
        if (!transport) {
            std::cerr << "[ERROR] Unknown transport mode: '" << mode
                      << "'. Valid values: stdout | file | tcp\n";
            return 3;
        }

        std::cerr << "[INFO] Connecting: mode=" << mode;
        if (mode == "tcp") {
            std::cerr << " host=" << host << " port=" << port;
        }
        std::cerr << "\n";

        if (!transport->connect()) {
            std::cerr << "[ERROR] connect() failed (exit code 4).\n";
            if (mode == "tcp") {
                std::cerr << "  -> Make sure UE is running and listening on "
                          << host << ":" << port << "\n";
                std::cerr << "  -> Check netstat -an | findstr " << port << "\n";
            }
            return 4;
        }
        std::cerr << "[INFO] connect() succeeded.\n";

        if (!loop_mode && count == 1 && !demo_mode && !args.count("--config")) {
            std::cerr
                << "[INFO] You are in manual mode with default --count 1 (only ONE frame will be sent).\n"
                << "       Full preset list (recommended): --presets-json configs/presets.json "
                   "--fps 24 --hold-sec 3 --mode tcp --host 127.0.0.1 --port 7001\n"
                << "       Or multi-frame manual: add e.g. --count 72 --fps 24\n";
        }

        std::uint64_t seq = getUInt64(args, "--seq", 1);
        const std::string character_id =
            args.count("--character") ? args.at("--character")
                                      : "miku_yyb_001";

        vh::ExpressionFrame config_frame;
        const bool use_config = args.count("--config") > 0 && !demo_mode;
        if (use_config) {
            config_frame = vh::JsonProtocol::readExpressionFrameFromFile(
                args.at("--config"));
        }

        int sent = 0;
        std::size_t demo_idx = 0;

        while (!g_interrupted && (loop_mode || sent < count)) {
            vh::ExpressionFrame frame;

            if (use_config) {
                frame = config_frame;
            } else if (demo_mode) {
                const auto& step = kDemoSteps[demo_idx % kDemoStepCount];
                frame = buildDemoFrame(step, character_id, seq);
                ++demo_idx;
            } else {
                frame = buildManualFrame(args, character_id, seq);
            }

            frame.sequence_id = seq;
            frame.timestamp_ms = currentTimestampMs();

            const auto errors = vh::PacketValidator::validate(frame);
            if (!errors.empty()) {
                std::cerr << "Validation failed (seq=" << seq << "):\n";
                for (const auto& e : errors) {
                    std::cerr << "  - " << e << "\n";
                }
                transport->close();
                return 2;
            }

            const std::string payload =
                vh::JsonProtocol::toJsonString(frame, false);

            if (!transport->sendText(payload)) {
                std::cerr << "[ERROR] sendText() failed at seq=" << seq
                          << " (exit code 5). The receiver may have closed the connection.\n";
                if (mode == "tcp") {
                    std::cerr << "  -> target=" << host << ":" << port << "\n";
                }
                transport->close();
                return 5;
            }

            if (mode != "stdout") {
                std::cout << "[seq=" << seq
                          << " emotion=" << frame.emotion.label
                          << " phoneme=" << frame.audio.phoneme_hint
                          << " rms=" << frame.audio.rms
                          << " pitch=" << frame.head_pose.pitch
                          << " yaw=" << frame.head_pose.yaw
                          << " roll=" << frame.head_pose.roll
                          << "]\n";
            }

            ++seq;
            ++sent;

            if (!g_interrupted && (loop_mode || sent < count)) {
                std::this_thread::sleep_for(
                    std::chrono::milliseconds(interval_ms));
            }
        }

        if (g_interrupted) {
            std::cout << "\nInterrupted by user.\n";
        }

        transport->close();
        std::cout << "Done. Frames sent: " << sent << "\n";
        return 0;

    } catch (const std::exception& ex) {
        std::cerr << "Fatal: " << ex.what() << "\n";
        return 1;
    }
}

} // namespace

int main(int argc, char** argv) {
    setupConsoleUtf8();
    std::signal(SIGINT, onSignal);
    return run(buildUtf8Args(argc, argv));
}