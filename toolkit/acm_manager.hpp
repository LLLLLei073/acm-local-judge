#pragma once
#include "json.hpp"
#include <iostream>
#include <string>
#include <stdexcept>
namespace acm {
using json = nlohmann::json;
inline std::string encode(const std::string& s) {
    static constexpr char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out; unsigned val = 0; int bits = -6;
    for (unsigned char c : s) { val = (val << 8) | c; bits += 8; while (bits >= 0) { out += alphabet[(val >> bits) & 63]; bits -= 6; } }
    if (bits > -6) out += alphabet[((val << 8) >> (bits + 8)) & 63];
    while (out.size() % 4) out += '=';
    return out;
}
inline std::string decode(const std::string& s) {
    const std::string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out; unsigned val = 0; int bits = -8;
    for (unsigned char c : s) { if (c == '=') break; auto i = alphabet.find(c); if (i == std::string::npos) throw std::runtime_error("invalid base64"); val = (val << 6) | static_cast<unsigned>(i); bits += 6; if (bits >= 0) { out += char((val >> bits) & 255); bits -= 8; } }
    return out;
}
inline void command(const json& value) { std::cout << value.dump() << '\n' << std::flush; }
inline json receive() { std::string line; if (!std::getline(std::cin, line)) return {{"type", "eof"}}; return json::parse(line); }
inline void send(int id, const std::string& data) { command({{"type", "send"}, {"id", id}, {"data", encode(data)}}); }
inline void close(int id) { command({{"type", "close"}, {"id", id}}); }
inline void start() { command({{"type", "start"}}); }
inline void verdict(const std::string& value, const std::string& message = "") { command({{"type", "verdict"}, {"verdict", value}, {"message", message}}); }
}
