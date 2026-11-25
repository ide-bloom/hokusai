// ODBC API hooks. They observe what the application sends to the Driver Manager (statement text, parameter
// bindings and their values at execute time, fetched rows, row counts and transaction ends) and publish one
// JSON event per observation to the shared-memory ring. They never change arguments or results.
#include "hooks.h"

#include "json.h"
#include "ring.h"

#include <windows.h>

#include <detours/detours.h>
// ANSI applications link the unsuffixed exports (SQLExecDirect, not SQLExecDirectA); keep those names
// unmapped.
#define SQL_NOUNICODEMAP
#include <sql.h>
#include <sqlext.h>

#include <cstdio>
#include <map>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

namespace hokusai
{

namespace
{

struct ParamBinding
{
    SQLSMALLINT valueType = SQL_C_DEFAULT;
    SQLSMALLINT sqlType = SQL_UNKNOWN_TYPE;
    SQLPOINTER value = nullptr;
    SQLLEN bufferLength = 0;
    SQLLEN* indicator = nullptr;
    std::optional<std::string> putData; // accumulated SQLPutData chunks for data-at-execution parameters
    bool atExec = false;
};

struct ColumnBinding
{
    SQLSMALLINT targetType = SQL_C_DEFAULT;
    SQLPOINTER value = nullptr;
    SQLLEN bufferLength = 0;
    SQLLEN* indicator = nullptr;
};

struct StatementState
{
    std::string text; // prepared text (SQLPrepare)
    std::map<SQLUSMALLINT, ParamBinding> params;
    std::map<SQLUSMALLINT, ColumnBinding> columns;
    std::vector<std::string> columnNames;
    std::map<SQLUSMALLINT, std::optional<std::string>> row; // current row being assembled
    bool hasRow = false;
    SQLUSMALLINT currentPutParam = 0;
    SQLUSMALLINT lastGetDataColumn = 0;
    std::string pendingText; // text of an execution waiting for data-at-execution parameters
};

std::mutex g_lock;
std::map<SQLHSTMT, StatementState> g_statements;
thread_local bool t_inHook = false;

// Prevents nested reporting when a hook calls the real API (for example SQLDescribeColW).
struct Reentry
{
    bool active;
    Reentry() : active(!t_inHook) { t_inHook = true; }
    ~Reentry()
    {
        if (active)
        {
            t_inHook = false;
        }
    }
};

std::int64_t Now()
{
    FILETIME ft;
    GetSystemTimePreciseAsFileTime(&ft);
    return (static_cast<std::int64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
}

std::string Handle(const void* h)
{
    char buffer[32];
    std::snprintf(buffer, sizeof(buffer), "%p", h);
    return buffer;
}

std::string Hex(const unsigned char* data, size_t length)
{
    static const char digits[] = "0123456789ABCDEF";
    std::string s = "0x";
    for (size_t i = 0; i < length; ++i)
    {
        s += digits[data[i] >> 4];
        s += digits[data[i] & 0xF];
    }

    return s;
}

std::string NumericToString(const SQL_NUMERIC_STRUCT& n)
{
    // 128-bit little-endian magnitude, converted by repeated division.
    unsigned char mag[SQL_MAX_NUMERIC_LEN];
    memcpy(mag, n.val, sizeof(mag));
    std::string digits;
    bool nonZero = true;
    while (nonZero)
    {
        nonZero = false;
        unsigned remainder = 0;
        for (int i = SQL_MAX_NUMERIC_LEN - 1; i >= 0; --i)
        {
            const unsigned value = (remainder << 8) | mag[i];
            mag[i] = static_cast<unsigned char>(value / 10);
            remainder = value % 10;
            nonZero |= mag[i] != 0;
        }

        digits.insert(digits.begin(), static_cast<char>('0' + remainder));
    }

    if (n.scale > 0)
    {
        while (digits.size() <= static_cast<size_t>(n.scale))
        {
            digits.insert(digits.begin(), '0');
        }

        digits.insert(digits.end() - n.scale, '.');
    }

    return (n.sign == 0 ? "-" : "") + digits;
}

SQLSMALLINT DefaultCType(SQLSMALLINT sqlType)
{
    switch (sqlType)
    {
    case SQL_WCHAR:
    case SQL_WVARCHAR:
    case SQL_WLONGVARCHAR:
        return SQL_C_WCHAR;
    case SQL_INTEGER:
        return SQL_C_SLONG;
    case SQL_SMALLINT:
        return SQL_C_SSHORT;
    case SQL_TINYINT:
        return SQL_C_STINYINT;
    case SQL_BIGINT:
        return SQL_C_SBIGINT;
    case SQL_DOUBLE:
    case SQL_FLOAT:
        return SQL_C_DOUBLE;
    case SQL_REAL:
        return SQL_C_FLOAT;
    case SQL_BIT:
        return SQL_C_BIT;
    case SQL_TYPE_DATE:
        return SQL_C_TYPE_DATE;
    case SQL_TYPE_TIMESTAMP:
        return SQL_C_TYPE_TIMESTAMP;
    case SQL_GUID:
        return SQL_C_GUID;
    case SQL_BINARY:
    case SQL_VARBINARY:
    case SQL_LONGVARBINARY:
        return SQL_C_BINARY;
    default:
        return SQL_C_CHAR;
    }
}

// Renders a C buffer as the canonical text Hokusai compares (invariant numbers, ISO dates).
std::optional<std::string> Render(SQLSMALLINT cType, SQLSMALLINT sqlType, const void* value, SQLLEN length)
{
    if (value == nullptr || length == SQL_NULL_DATA)
    {
        return std::nullopt;
    }

    if (cType == SQL_C_DEFAULT)
    {
        cType = DefaultCType(sqlType);
    }

    char buffer[64];
    switch (cType)
    {
    case SQL_C_CHAR:
    {
        const auto* s = static_cast<const char*>(value);
        const size_t n = length == SQL_NTS || length < 0 ? strlen(s) : static_cast<size_t>(length);
        return AnsiToUtf8(std::string_view(s, n));
    }
    case SQL_C_WCHAR:
    {
        const auto* s = static_cast<const wchar_t*>(value);
        const size_t n =
            length == SQL_NTS || length < 0 ? wcslen(s) : static_cast<size_t>(length) / sizeof(wchar_t);
        return ToUtf8(std::wstring_view(s, n));
    }
    case SQL_C_SLONG:
    case SQL_C_LONG:
        return std::to_string(*static_cast<const SQLINTEGER*>(value));
    case SQL_C_ULONG:
        return std::to_string(*static_cast<const SQLUINTEGER*>(value));
    case SQL_C_SSHORT:
    case SQL_C_SHORT:
        return std::to_string(*static_cast<const SQLSMALLINT*>(value));
    case SQL_C_USHORT:
        return std::to_string(*static_cast<const SQLUSMALLINT*>(value));
    case SQL_C_STINYINT:
    case SQL_C_TINYINT:
        return std::to_string(*static_cast<const SQLSCHAR*>(value));
    case SQL_C_UTINYINT:
        return std::to_string(*static_cast<const SQLCHAR*>(value));
    case SQL_C_SBIGINT:
        return std::to_string(*static_cast<const SQLBIGINT*>(value));
    case SQL_C_UBIGINT:
        return std::to_string(*static_cast<const SQLUBIGINT*>(value));
    case SQL_C_BIT:
        return std::to_string(*static_cast<const SQLCHAR*>(value) ? 1 : 0);
    case SQL_C_DOUBLE:
        std::snprintf(buffer, sizeof(buffer), "%.15g", *static_cast<const SQLDOUBLE*>(value));
        return buffer;
    case SQL_C_FLOAT:
        std::snprintf(buffer, sizeof(buffer), "%.7g",
                      static_cast<double>(*static_cast<const SQLREAL*>(value)));
        return buffer;
    case SQL_C_NUMERIC:
        return NumericToString(*static_cast<const SQL_NUMERIC_STRUCT*>(value));
    case SQL_C_TYPE_DATE:
    case SQL_C_DATE:
    {
        const auto* d = static_cast<const SQL_DATE_STRUCT*>(value);
        std::snprintf(buffer, sizeof(buffer), "%04d-%02u-%02u", d->year, d->month, d->day);
        return buffer;
    }
    case SQL_C_TYPE_TIMESTAMP:
    case SQL_C_TIMESTAMP:
    {
        const auto* t = static_cast<const SQL_TIMESTAMP_STRUCT*>(value);
        if (t->fraction == 0)
        {
            std::snprintf(buffer, sizeof(buffer), "%04d-%02u-%02u %02u:%02u:%02u", t->year, t->month, t->day,
                          t->hour, t->minute, t->second);
        }
        else
        {
            // ODBC fractions are nanoseconds; trailing zeros are trimmed to match Hokusai's canonical form.
            std::snprintf(buffer, sizeof(buffer), "%04d-%02u-%02u %02u:%02u:%02u.%07lu", t->year, t->month,
                          t->day, t->hour, t->minute, t->second,
                          static_cast<unsigned long>(t->fraction / 100));
            std::string s = buffer;
            while (s.back() == '0')
            {
                s.pop_back();
            }

            return s;
        }

        return buffer;
    }
    case SQL_C_GUID:
    {
        const auto* g = static_cast<const SQLGUID*>(value);
        std::snprintf(buffer, sizeof(buffer), "%08lx-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x",
                      static_cast<unsigned long>(g->Data1), g->Data2, g->Data3, g->Data4[0], g->Data4[1],
                      g->Data4[2], g->Data4[3], g->Data4[4], g->Data4[5], g->Data4[6], g->Data4[7]);
        return buffer;
    }
    case SQL_C_BINARY:
        return Hex(static_cast<const unsigned char*>(value), length < 0 ? 0 : static_cast<size_t>(length));
    default:
        return std::string("<unsupported C type ") + std::to_string(cType) + ">";
    }
}

std::optional<std::string> ParamValue(const ParamBinding& p)
{
    if (p.atExec)
    {
        return p.putData;
    }

    SQLLEN length =
        p.indicator ? *p.indicator
                    : (p.valueType == SQL_C_CHAR || p.valueType == SQL_C_WCHAR ? SQL_NTS : p.bufferLength);
    if (length == SQL_NULL_DATA)
    {
        return std::nullopt;
    }

    return Render(p.valueType, p.sqlType, p.value, length);
}

// Real entry points, replaced by trampolines when hooks are attached.
decltype(&SQLExecDirectW) Real_SQLExecDirectW = SQLExecDirectW;
decltype(&SQLExecDirect) Real_SQLExecDirectA = SQLExecDirect;
decltype(&SQLPrepareW) Real_SQLPrepareW = SQLPrepareW;
decltype(&SQLPrepare) Real_SQLPrepareA = SQLPrepare;
decltype(&SQLExecute) Real_SQLExecute = SQLExecute;
decltype(&SQLBindParameter) Real_SQLBindParameter = SQLBindParameter;
decltype(&SQLParamData) Real_SQLParamData = SQLParamData;
decltype(&SQLPutData) Real_SQLPutData = SQLPutData;
decltype(&SQLBindCol) Real_SQLBindCol = SQLBindCol;
decltype(&SQLFetch) Real_SQLFetch = SQLFetch;
decltype(&SQLFetchScroll) Real_SQLFetchScroll = SQLFetchScroll;
decltype(&SQLExtendedFetch) Real_SQLExtendedFetch = SQLExtendedFetch;
decltype(&SQLGetData) Real_SQLGetData = SQLGetData;
decltype(&SQLRowCount) Real_SQLRowCount = SQLRowCount;
decltype(&SQLMoreResults) Real_SQLMoreResults = SQLMoreResults;
decltype(&SQLFreeStmt) Real_SQLFreeStmt = SQLFreeStmt;
decltype(&SQLCloseCursor) Real_SQLCloseCursor = SQLCloseCursor;
decltype(&SQLFreeHandle) Real_SQLFreeHandle = SQLFreeHandle;
decltype(&SQLEndTran) Real_SQLEndTran = SQLEndTran;
decltype(&SQLTransact) Real_SQLTransact = SQLTransact;
decltype(&SQLNumResultCols) Real_SQLNumResultCols = SQLNumResultCols;
decltype(&SQLDescribeColW) Real_SQLDescribeColW = SQLDescribeColW;

bool Succeeded(SQLRETURN rc)
{
    return rc == SQL_SUCCESS || rc == SQL_SUCCESS_WITH_INFO;
}

// Emits the row being assembled, if any. Caller holds g_lock.
void FlushRow(SQLHSTMT stmt, StatementState& s)
{
    if (!s.hasRow)
    {
        return;
    }

    JsonWriter w;
    w.Begin().String("t", "row").Number("ts", Now()).String("stmt", Handle(stmt));
    w.BeginArray("cols");
    for (const auto& name : s.columnNames)
    {
        w.Value(std::optional<std::string>(name));
    }

    w.EndArray().BeginArray("values");
    for (SQLUSMALLINT c = 1; c <= s.columnNames.size(); ++c)
    {
        const auto it = s.row.find(c);
        w.Value(it == s.row.end() ? std::optional<std::string>() : it->second);
    }

    w.EndArray().End();
    RingWrite(w.Text());
    s.row.clear();
    s.hasRow = false;
}

void DescribeColumns(SQLHSTMT stmt, StatementState& s)
{
    s.columnNames.clear();
    SQLSMALLINT count = 0;
    if (!Succeeded(Real_SQLNumResultCols(stmt, &count)))
    {
        return;
    }

    for (SQLUSMALLINT c = 1; c <= static_cast<SQLUSMALLINT>(count); ++c)
    {
        SQLWCHAR name[256] = {};
        SQLSMALLINT nameLength = 0, type = 0, digits = 0, nullable = 0;
        SQLULEN size = 0;
        Real_SQLDescribeColW(stmt, c, name, 256, &nameLength, &type, &size, &digits, &nullable);
        s.columnNames.push_back(ToUtf8(reinterpret_cast<wchar_t*>(name)));
    }
}

void ReportExecute(SQLHSTMT stmt, const std::string& text, SQLRETURN rc)
{
    std::lock_guard guard(g_lock);
    auto& s = g_statements[stmt];
    FlushRow(stmt, s);
    s.columnNames.clear();
    if (rc == SQL_NEED_DATA)
    {
        s.pendingText = text; // reported once SQLParamData completes the execution
        return;
    }

    JsonWriter w;
    w.Begin()
        .String("t", "exec")
        .Number("ts", Now())
        .String("stmt", Handle(stmt))
        .String("sql", text)
        .Number("rc", rc);
    w.BeginArray("params");
    for (const auto& [index, p] : s.params)
    {
        w.BeginObject()
            .Number("i", index)
            .Number("c", p.valueType)
            .Number("s", p.sqlType)
            .Key("v")
            .Value(ParamValue(p))
            .End();
    }

    w.EndArray().End();
    RingWrite(w.Text());
}

SQLRETURN SQL_API Hook_SQLExecDirectW(SQLHSTMT stmt, SQLWCHAR* text, SQLINTEGER length)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLExecDirectW(stmt, text, length);
    if (r.active && text != nullptr)
    {
        const auto* w = reinterpret_cast<const wchar_t*>(text);
        ReportExecute(
            stmt, ToUtf8(std::wstring_view(w, length == SQL_NTS ? wcslen(w) : static_cast<size_t>(length))),
            rc);
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLExecDirectA(SQLHSTMT stmt, SQLCHAR* text, SQLINTEGER length)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLExecDirectA(stmt, text, length);
    if (r.active && text != nullptr)
    {
        const auto* a = reinterpret_cast<const char*>(text);
        ReportExecute(
            stmt,
            AnsiToUtf8(std::string_view(a, length == SQL_NTS ? strlen(a) : static_cast<size_t>(length))), rc);
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLPrepareW(SQLHSTMT stmt, SQLWCHAR* text, SQLINTEGER length)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLPrepareW(stmt, text, length);
    if (r.active && text != nullptr && Succeeded(rc))
    {
        const auto* w = reinterpret_cast<const wchar_t*>(text);
        std::lock_guard guard(g_lock);
        g_statements[stmt].text =
            ToUtf8(std::wstring_view(w, length == SQL_NTS ? wcslen(w) : static_cast<size_t>(length)));
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLPrepareA(SQLHSTMT stmt, SQLCHAR* text, SQLINTEGER length)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLPrepareA(stmt, text, length);
    if (r.active && text != nullptr && Succeeded(rc))
    {
        const auto* a = reinterpret_cast<const char*>(text);
        std::lock_guard guard(g_lock);
        g_statements[stmt].text =
            AnsiToUtf8(std::string_view(a, length == SQL_NTS ? strlen(a) : static_cast<size_t>(length)));
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLExecute(SQLHSTMT stmt)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLExecute(stmt);
    if (r.active)
    {
        std::string text;
        {
            std::lock_guard guard(g_lock);
            text = g_statements[stmt].text;
        }

        ReportExecute(stmt, text, rc);
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLBindParameter(SQLHSTMT stmt, SQLUSMALLINT index, SQLSMALLINT ioType,
                                        SQLSMALLINT valueType, SQLSMALLINT paramType, SQLULEN columnSize,
                                        SQLSMALLINT digits, SQLPOINTER value, SQLLEN bufferLength,
                                        SQLLEN* indicator)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLBindParameter(stmt, index, ioType, valueType, paramType, columnSize, digits,
                                               value, bufferLength, indicator);
    if (r.active && Succeeded(rc))
    {
        // Values are read at execute time: ODBC binds by reference (deferred input).
        std::lock_guard guard(g_lock);
        auto& p = g_statements[stmt].params[index];
        p.valueType = valueType;
        p.sqlType = paramType;
        p.value = value;
        p.bufferLength = bufferLength;
        p.indicator = indicator;
        p.atExec = indicator != nullptr &&
                   (*indicator == SQL_DATA_AT_EXEC || *indicator <= SQL_LEN_DATA_AT_EXEC_OFFSET);
        p.putData.reset();
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLParamData(SQLHSTMT stmt, SQLPOINTER* value)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLParamData(stmt, value);
    if (!r.active)
    {
        return rc;
    }

    std::string text;
    {
        std::lock_guard guard(g_lock);
        auto& s = g_statements[stmt];
        if (rc == SQL_NEED_DATA && value != nullptr)
        {
            // The token returned is the ParameterValuePtr given at bind time; map it back to the parameter.
            for (auto& [index, p] : s.params)
            {
                if (p.atExec && p.value == *value)
                {
                    s.currentPutParam = index;
                    break;
                }
            }

            return rc;
        }

        text = s.pendingText;
        s.pendingText.clear();
    }

    if (!text.empty())
    {
        ReportExecute(stmt, text, rc);
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLPutData(SQLHSTMT stmt, SQLPOINTER data, SQLLEN length)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLPutData(stmt, data, length);
    if (r.active && Succeeded(rc))
    {
        std::lock_guard guard(g_lock);
        auto& s = g_statements[stmt];
        auto it = s.params.find(s.currentPutParam);
        if (it != s.params.end())
        {
            const auto chunk = Render(it->second.valueType, it->second.sqlType, data, length);
            if (chunk)
            {
                it->second.putData = it->second.putData.value_or("") + *chunk;
            }
        }
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLBindCol(SQLHSTMT stmt, SQLUSMALLINT column, SQLSMALLINT targetType,
                                  SQLPOINTER value, SQLLEN bufferLength, SQLLEN* indicator)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLBindCol(stmt, column, targetType, value, bufferLength, indicator);
    if (r.active && Succeeded(rc))
    {
        std::lock_guard guard(g_lock);
        auto& s = g_statements[stmt];
        if (value == nullptr)
        {
            s.columns.erase(column);
        }
        else
        {
            s.columns[column] = ColumnBinding{targetType, value, bufferLength, indicator};
        }
    }

    return rc;
}

// Starts a new row after a successful fetch, reading every bound column buffer.
void AfterFetch(SQLHSTMT stmt, SQLRETURN rc)
{
    std::lock_guard guard(g_lock);
    auto& s = g_statements[stmt];
    FlushRow(stmt, s);
    if (!Succeeded(rc))
    {
        return;
    }

    if (s.columnNames.empty())
    {
        DescribeColumns(stmt, s);
    }

    s.hasRow = true;
    s.lastGetDataColumn = 0;
    for (const auto& [column, b] : s.columns)
    {
        const SQLLEN length = b.indicator ? *b.indicator : b.bufferLength;
        s.row[column] = Render(b.targetType, SQL_UNKNOWN_TYPE, b.value, length);
    }
}

SQLRETURN SQL_API Hook_SQLFetch(SQLHSTMT stmt)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLFetch(stmt);
    if (r.active)
    {
        AfterFetch(stmt, rc);
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLFetchScroll(SQLHSTMT stmt, SQLSMALLINT orientation, SQLLEN offset)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLFetchScroll(stmt, orientation, offset);
    if (r.active)
    {
        AfterFetch(stmt, rc);
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLExtendedFetch(SQLHSTMT stmt, SQLUSMALLINT orientation, SQLLEN offset,
                                        SQLULEN* rowCount, SQLUSMALLINT* rowStatus)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLExtendedFetch(stmt, orientation, offset, rowCount, rowStatus);
    if (r.active)
    {
        AfterFetch(stmt, rc);
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLGetData(SQLHSTMT stmt, SQLUSMALLINT column, SQLSMALLINT targetType,
                                  SQLPOINTER value, SQLLEN bufferLength, SQLLEN* indicator)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLGetData(stmt, column, targetType, value, bufferLength, indicator);
    if (r.active && Succeeded(rc))
    {
        std::lock_guard guard(g_lock);
        auto& s = g_statements[stmt];
        SQLLEN length = indicator ? *indicator : bufferLength;
        if ((targetType == SQL_C_CHAR || targetType == SQL_C_WCHAR) &&
            (length == SQL_NO_TOTAL || length >= bufferLength))
        {
            // Truncated chunk: the buffer is full and null-terminated.
            length = SQL_NTS;
        }

        auto chunk = Render(targetType, SQL_UNKNOWN_TYPE, value, length);
        auto& cell = s.row[column];
        // Long values arrive in several SQLGetData calls on the same column; concatenate the chunks.
        if (column == s.lastGetDataColumn && cell && chunk)
        {
            cell = *cell + *chunk;
        }
        else
        {
            cell = chunk;
        }

        s.lastGetDataColumn = column;
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLRowCount(SQLHSTMT stmt, SQLLEN* count)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLRowCount(stmt, count);
    if (r.active && Succeeded(rc) && count != nullptr)
    {
        JsonWriter w;
        w.Begin()
            .String("t", "rowcount")
            .Number("ts", Now())
            .String("stmt", Handle(stmt))
            .Number("n", static_cast<std::int64_t>(*count))
            .End();
        RingWrite(w.Text());
    }

    return rc;
}

void CloseCursor(SQLHSTMT stmt)
{
    std::lock_guard guard(g_lock);
    auto it = g_statements.find(stmt);
    if (it != g_statements.end())
    {
        FlushRow(stmt, it->second);
        it->second.columnNames.clear();
    }
}

SQLRETURN SQL_API Hook_SQLMoreResults(SQLHSTMT stmt)
{
    Reentry r;
    if (r.active)
    {
        CloseCursor(stmt);
    }

    return Real_SQLMoreResults(stmt);
}

SQLRETURN SQL_API Hook_SQLCloseCursor(SQLHSTMT stmt)
{
    Reentry r;
    if (r.active)
    {
        CloseCursor(stmt);
    }

    return Real_SQLCloseCursor(stmt);
}

SQLRETURN SQL_API Hook_SQLFreeStmt(SQLHSTMT stmt, SQLUSMALLINT option)
{
    Reentry r;
    if (r.active)
    {
        CloseCursor(stmt);
        std::lock_guard guard(g_lock);
        auto it = g_statements.find(stmt);
        if (it != g_statements.end())
        {
            if (option == SQL_RESET_PARAMS)
            {
                it->second.params.clear();
            }
            else if (option == SQL_UNBIND)
            {
                it->second.columns.clear();
            }
            else if (option == SQL_DROP)
            {
                g_statements.erase(it);
            }
        }
    }

    return Real_SQLFreeStmt(stmt, option);
}

SQLRETURN SQL_API Hook_SQLFreeHandle(SQLSMALLINT type, SQLHANDLE handle)
{
    Reentry r;
    if (r.active && type == SQL_HANDLE_STMT)
    {
        CloseCursor(handle);
        std::lock_guard guard(g_lock);
        g_statements.erase(handle);
    }

    return Real_SQLFreeHandle(type, handle);
}

void ReportEndTran(SQLHANDLE handle, SQLSMALLINT completion)
{
    JsonWriter w;
    w.Begin()
        .String("t", "endtran")
        .Number("ts", Now())
        .String("dbc", Handle(handle))
        .Bool("commit", completion == SQL_COMMIT)
        .End();
    RingWrite(w.Text());
}

SQLRETURN SQL_API Hook_SQLEndTran(SQLSMALLINT type, SQLHANDLE handle, SQLSMALLINT completion)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLEndTran(type, handle, completion);
    if (r.active && Succeeded(rc))
    {
        ReportEndTran(handle, completion);
    }

    return rc;
}

SQLRETURN SQL_API Hook_SQLTransact(SQLHENV env, SQLHDBC dbc, SQLUSMALLINT completion)
{
    Reentry r;
    const SQLRETURN rc = Real_SQLTransact(env, dbc, completion);
    if (r.active && Succeeded(rc))
    {
        ReportEndTran(dbc != nullptr ? dbc : env, static_cast<SQLSMALLINT>(completion));
    }

    return rc;
}

template <typename T> LONG Attach(T& real, T hook)
{
    return DetourAttach(reinterpret_cast<PVOID*>(&real), reinterpret_cast<PVOID>(hook));
}

template <typename T> LONG Detach(T& real, T hook)
{
    return DetourDetach(reinterpret_cast<PVOID*>(&real), reinterpret_cast<PVOID>(hook));
}

template <typename Op> LONG ForEachHook(Op op)
{
    LONG error = NO_ERROR;
    auto check = [&error](LONG e)
    {
        if (error == NO_ERROR)
        {
            error = e;
        }
    };
    check(op(Real_SQLExecDirectW, &Hook_SQLExecDirectW));
    check(op(Real_SQLExecDirectA, &Hook_SQLExecDirectA));
    check(op(Real_SQLPrepareW, &Hook_SQLPrepareW));
    check(op(Real_SQLPrepareA, &Hook_SQLPrepareA));
    check(op(Real_SQLExecute, &Hook_SQLExecute));
    check(op(Real_SQLBindParameter, &Hook_SQLBindParameter));
    check(op(Real_SQLParamData, &Hook_SQLParamData));
    check(op(Real_SQLPutData, &Hook_SQLPutData));
    check(op(Real_SQLBindCol, &Hook_SQLBindCol));
    check(op(Real_SQLFetch, &Hook_SQLFetch));
    check(op(Real_SQLFetchScroll, &Hook_SQLFetchScroll));
    check(op(Real_SQLExtendedFetch, &Hook_SQLExtendedFetch));
    check(op(Real_SQLGetData, &Hook_SQLGetData));
    check(op(Real_SQLRowCount, &Hook_SQLRowCount));
    check(op(Real_SQLMoreResults, &Hook_SQLMoreResults));
    check(op(Real_SQLCloseCursor, &Hook_SQLCloseCursor));
    check(op(Real_SQLFreeStmt, &Hook_SQLFreeStmt));
    check(op(Real_SQLFreeHandle, &Hook_SQLFreeHandle));
    check(op(Real_SQLEndTran, &Hook_SQLEndTran));
    check(op(Real_SQLTransact, &Hook_SQLTransact));
    return error;
}

} // namespace

long AttachHooks()
{
    DetourTransactionBegin();
    DetourUpdateThread(GetCurrentThread());
    const LONG error = ForEachHook([](auto& real, auto hook) { return Attach(real, hook); });
    if (error != NO_ERROR)
    {
        DetourTransactionAbort();
        return error;
    }

    return DetourTransactionCommit();
}

long DetachHooks()
{
    DetourTransactionBegin();
    DetourUpdateThread(GetCurrentThread());
    ForEachHook([](auto& real, auto hook) { return Detach(real, hook); });
    return DetourTransactionCommit();
}

} // namespace hokusai
