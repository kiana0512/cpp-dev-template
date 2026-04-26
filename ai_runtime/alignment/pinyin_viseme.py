from __future__ import annotations

import re


PAUSE_CHARS = set("，、。！？,.!?;；:：\n\r\t ")


def _pinyin_tokens(text: str) -> list[str] | None:
    try:
        from pypinyin import Style, pinyin  # type: ignore
    except Exception:
        return None
    out: list[str] = []
    for item in pinyin(text, style=Style.NORMAL, errors="default"):
        if item:
            out.append(str(item[0]).lower())
    return out


def pinyin_to_viseme(token: str) -> str:
    t = token.lower()
    if not t:
        return "pause"
    if any(t.endswith(x) for x in ["ang", "an", "ai", "ao", "a"]):
        return "a"
    if any(t.endswith(x) for x in ["ing", "in", "ian", "iao", "ie", "i"]):
        return "i"
    if any(t.endswith(x) for x in ["ong", "ou", "uo", "o"]):
        return "o"
    if any(t.endswith(x) for x in ["eng", "en", "ei", "er", "e"]):
        return "e"
    if any(t.endswith(x) for x in ["un", "ui", "iu", "u", "v", "ü"]):
        return "u"
    if t[-1] in ("n", "g"):
        return "n"
    return "a"


def english_to_visemes(word: str) -> list[str]:
    out: list[str] = []
    for ch in word.lower():
        if ch in "aeiou":
            out.append(ch)
        elif ch in "mbp":
            out.append("m")
        elif ch in "fv":
            out.append("m")
    return out or ["a"]


def text_to_viseme_tokens(text: str) -> list[str]:
    if not text:
        return ["pause"]

    tokens: list[str] = []
    buffer = ""
    pinyin_tokens = _pinyin_tokens(text)
    pinyin_index = 0

    def flush_ascii() -> None:
        nonlocal buffer
        if buffer:
            for word in re.findall(r"[A-Za-z]+", buffer):
                tokens.extend(english_to_visemes(word))
            buffer = ""

    for ch in text:
        if ch in PAUSE_CHARS:
            flush_ascii()
            if not tokens or tokens[-1] != "pause":
                tokens.append("pause")
            continue
        if ch.isascii():
            buffer += ch
            continue
        flush_ascii()
        if pinyin_tokens and pinyin_index < len(pinyin_tokens):
            tokens.append(pinyin_to_viseme(pinyin_tokens[pinyin_index]))
            pinyin_index += 1
        else:
            code = ord(ch)
            tokens.append(["a", "i", "u", "e", "o"][code % 5])

    flush_ascii()
    return tokens or ["pause"]
