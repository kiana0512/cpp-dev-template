#include <cassert>
#include <iostream>

#include "vh/expression_mapper.h"
#include "vh/json_protocol.h"
#include "vh/packet_validator.h"

int main() {
    {
        vh::MapperInput input;
        input.text = "你好，我是Miku。";
        input.emotion_label = "happy";
        input.rms = 0.35;
        input.phoneme_hint = "a";
        input.sequence_id = 1;

        const auto frame = vh::ExpressionMapper::buildFrame(input);
        const auto errors = vh::PacketValidator::validate(frame);

        assert(errors.empty());

        const auto text = vh::JsonProtocol::toJsonString(frame, false);
        const auto restored = vh::JsonProtocol::fromJsonString(text);

        assert(restored.character_id == "miku_yyb_001");
        assert(restored.emotion.label == "happy");
    }

    {
        vh::ExpressionFrame bad;
        bad.character_id = "";
        bad.audio.rms = 1.5;
        bad.emotion.confidence = -0.2;
        bad.blendshapes["jawOpen"] = 2.0;

        const auto errors = vh::PacketValidator::validate(bad);
        assert(!errors.empty());
    }

    std::cout << "All protocol tests passed.\n";
    return 0;
}