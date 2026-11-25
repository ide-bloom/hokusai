#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace hokusai
{

std::string ToUtf8(std::wstring_view text);
std::string AnsiToUtf8(std::string_view text);

// Minimal JSON object writer for agent events; values are always escaped.
class JsonWriter
{
  public:
    JsonWriter& Begin();
    JsonWriter& End();
    JsonWriter& BeginArray(std::string_view key);
    JsonWriter& BeginArray();
    JsonWriter& EndArray();
    JsonWriter& BeginObject();
    JsonWriter& Key(std::string_view key);
    JsonWriter& String(std::string_view key, std::string_view value);
    JsonWriter& Number(std::string_view key, std::int64_t value);
    JsonWriter& Bool(std::string_view key, bool value);
    JsonWriter& Value(const std::optional<std::string>& value);
    JsonWriter& Value(std::int64_t value);
    const std::string& Text() const { return m_text; }

  private:
    void Separator();
    void Escaped(std::string_view value);
    std::string m_text;
    bool m_first = true;
};

} // namespace hokusai
