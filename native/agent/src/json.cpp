#include "json.h"

#include <windows.h>

#include <cstdio>

namespace hokusai
{

std::string ToUtf8(std::wstring_view text)
{
    if (text.empty())
    {
        return {};
    }

    const int size = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0,
                                         nullptr, nullptr);
    std::string result(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), size, nullptr,
                        nullptr);
    return result;
}

std::string AnsiToUtf8(std::string_view text)
{
    if (text.empty())
    {
        return {};
    }

    // ANSI ODBC entry points receive text in the process code page.
    const int wide = MultiByteToWideChar(CP_ACP, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
    std::wstring buffer(static_cast<size_t>(wide), L'\0');
    MultiByteToWideChar(CP_ACP, 0, text.data(), static_cast<int>(text.size()), buffer.data(), wide);
    return ToUtf8(buffer);
}

void JsonWriter::Separator()
{
    if (!m_first)
    {
        m_text += ',';
    }

    m_first = false;
}

void JsonWriter::Escaped(std::string_view value)
{
    m_text += '"';
    for (const char c : value)
    {
        switch (c)
        {
        case '"':
            m_text += "\\\"";
            break;
        case '\\':
            m_text += "\\\\";
            break;
        case '\n':
            m_text += "\\n";
            break;
        case '\r':
            m_text += "\\r";
            break;
        case '\t':
            m_text += "\\t";
            break;
        default:
            if (static_cast<unsigned char>(c) < 0x20)
            {
                char buffer[8];
                std::snprintf(buffer, sizeof(buffer), "\\u%04x",
                              static_cast<unsigned>(static_cast<unsigned char>(c)));
                m_text += buffer;
            }
            else
            {
                m_text += c;
            }
        }
    }

    m_text += '"';
}

JsonWriter& JsonWriter::Begin()
{
    m_text += '{';
    m_first = true;
    return *this;
}

JsonWriter& JsonWriter::End()
{
    m_text += '}';
    m_first = false;
    return *this;
}

JsonWriter& JsonWriter::BeginObject()
{
    Separator();
    m_text += '{';
    m_first = true;
    return *this;
}

JsonWriter& JsonWriter::Key(std::string_view key)
{
    Separator();
    Escaped(key);
    m_text += ':';
    m_first = true;
    return *this;
}

JsonWriter& JsonWriter::BeginArray(std::string_view key)
{
    Key(key);
    m_text += '[';
    m_first = true;
    return *this;
}

JsonWriter& JsonWriter::BeginArray()
{
    Separator();
    m_text += '[';
    m_first = true;
    return *this;
}

JsonWriter& JsonWriter::EndArray()
{
    m_text += ']';
    m_first = false;
    return *this;
}

JsonWriter& JsonWriter::String(std::string_view key, std::string_view value)
{
    Key(key);
    Escaped(value);
    m_first = false;
    return *this;
}

JsonWriter& JsonWriter::Number(std::string_view key, std::int64_t value)
{
    Key(key);
    m_text += std::to_string(value);
    m_first = false;
    return *this;
}

JsonWriter& JsonWriter::Bool(std::string_view key, bool value)
{
    Key(key);
    m_text += value ? "true" : "false";
    m_first = false;
    return *this;
}

JsonWriter& JsonWriter::Value(const std::optional<std::string>& value)
{
    Separator();
    if (value)
    {
        Escaped(*value);
    }
    else
    {
        m_text += "null";
    }

    return *this;
}

JsonWriter& JsonWriter::Value(std::int64_t value)
{
    Separator();
    m_text += std::to_string(value);
    return *this;
}

} // namespace hokusai
