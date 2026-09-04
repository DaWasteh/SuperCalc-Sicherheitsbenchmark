using System.Text;

namespace SuperCalcBenchmark.Core;

/// <summary>
/// Repairs the most common "almost JSON" defects produced by local LLMs so that a response
/// which clearly contains a findings payload is not silently degraded to the heuristic text
/// fallback. The pass is only applied after strict parsing failed; valid JSON is never touched.
///
/// Repairs (each reported by a stable token so archives can show what was fixed):
///   leading_zero        "line_start": 0218            -> 218
///   invalid_escape      "regex \d+" / "path \."       -> "regex \\d+"
///   raw_control_char    literal newline/tab in string -> \n / \t
///   unescaped_quote     "std::cout << "x" << y"       -> "std::cout << \"x\" << y"
///   missing_comma       "a": 1 \n "b": 2 / } {        -> inserts the comma
///   unterminated_string response cut off inside a string -> closes it
/// </summary>
public static class LenientJsonRepair
{
    public sealed record RepairResult(string Json, IReadOnlyList<string> Repairs)
    {
        public bool Changed => Repairs.Count > 0;
    }

    public static RepairResult Repair(string json)
    {
        json ??= string.Empty;
        var output = new StringBuilder(json.Length + 64);
        var repairs = new List<string>();
        var inString = false;
        var lastSignificant = '\0';

        void Note(string repair)
        {
            if (!repairs.Contains(repair, StringComparer.Ordinal))
            {
                repairs.Add(repair);
            }
        }

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];

            if (inString)
            {
                if (c == '\\')
                {
                    if (i + 1 >= json.Length)
                    {
                        output.Append("\\\\");
                        Note("invalid_escape");
                        continue;
                    }

                    var next = json[i + 1];
                    if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                    {
                        output.Append(c).Append(next);
                        i++;
                        continue;
                    }

                    if (next == 'u' && i + 5 < json.Length && IsHex4(json, i + 2))
                    {
                        output.Append(json, i, 6);
                        i += 5;
                        continue;
                    }

                    // Backslash followed by something JSON does not allow (\d, \., \s, \x ...).
                    // Keep the character and escape the backslash itself.
                    output.Append("\\\\");
                    Note("invalid_escape");
                    continue;
                }

                if (c == '"')
                {
                    var role = ClassifyQuote(json, i + 1);
                    if (role == QuoteRole.Closing)
                    {
                        inString = false;
                        lastSignificant = '"';
                        output.Append(c);
                        continue;
                    }

                    if (role == QuoteRole.ClosingBeforeMissingComma)
                    {
                        inString = false;
                        lastSignificant = ',';
                        output.Append(c);
                        output.Append(',');
                        Note("missing_comma");
                        continue;
                    }

                    output.Append("\\\"");
                    Note("unescaped_quote");
                    continue;
                }

                if (c < 0x20)
                {
                    output.Append(c switch
                    {
                        '\n' => "\\n",
                        '\r' => "\\r",
                        '\t' => "\\t",
                        '\b' => "\\b",
                        '\f' => "\\f",
                        _ => "\\u" + ((int)c).ToString("x4")
                    });
                    Note("raw_control_char");
                    continue;
                }

                output.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                output.Append(c);
                continue;
            }

            var startsValue = c is '"' or '{' or '[' or '-' or 't' or 'f' or 'n' || char.IsAsciiDigit(c);
            if (startsValue && NeedsSeparator(lastSignificant))
            {
                // "a": 1 <newline> "b": 2   or   } {   inside an array: the comma is missing.
                output.Append(',');
                Note("missing_comma");
            }

            if (c == '"')
            {
                inString = true;
                output.Append(c);
                continue;
            }

            if (c == '-' || char.IsAsciiDigit(c))
            {
                var start = i;
                var cursor = i;
                if (json[cursor] == '-')
                {
                    cursor++;
                }

                var digitsStart = cursor;
                while (cursor < json.Length && char.IsAsciiDigit(json[cursor]))
                {
                    cursor++;
                }

                var integerPart = json[digitsStart..cursor];
                if (integerPart.Length > 1 && integerPart[0] == '0')
                {
                    var trimmed = integerPart.TrimStart('0');
                    output.Append(json, start, digitsStart - start).Append(trimmed.Length == 0 ? "0" : trimmed);
                    Note("leading_zero");
                }
                else
                {
                    output.Append(json, start, cursor - start);
                }

                // Fraction and exponent belong to the same token; copy them verbatim so
                // 0.05 is never mistaken for a leading-zero integer.
                var tailStart = cursor;
                if (cursor < json.Length && json[cursor] == '.')
                {
                    cursor++;
                    while (cursor < json.Length && char.IsAsciiDigit(json[cursor]))
                    {
                        cursor++;
                    }
                }

                if (cursor < json.Length && (json[cursor] == 'e' || json[cursor] == 'E'))
                {
                    var exponent = cursor + 1;
                    if (exponent < json.Length && (json[exponent] == '+' || json[exponent] == '-'))
                    {
                        exponent++;
                    }

                    if (exponent < json.Length && char.IsAsciiDigit(json[exponent]))
                    {
                        cursor = exponent;
                        while (cursor < json.Length && char.IsAsciiDigit(json[cursor]))
                        {
                            cursor++;
                        }
                    }
                }

                output.Append(json, tailStart, cursor - tailStart);
                lastSignificant = '0';
                i = cursor - 1;
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                // true / false / null (or garbage). Consume the whole word so the comma
                // heuristic sees the literal as one value token.
                var cursor = i;
                while (cursor < json.Length && char.IsAsciiLetter(json[cursor]))
                {
                    cursor++;
                }

                output.Append(json, i, cursor - i);
                lastSignificant = 'l';
                i = cursor - 1;
                continue;
            }

            lastSignificant = c;
            output.Append(c);
        }

        if (inString)
        {
            output.Append('"');
            Note("unterminated_string");
        }

        return new RepairResult(output.ToString(), repairs);
    }

    private enum QuoteRole
    {
        Closing,
        ClosingBeforeMissingComma,
        Inner
    }

    /// <summary>
    /// Decides whether a quote inside a string closes it. A closing quote is followed by a
    /// structural character; a quote followed by prose (or an operator such as &lt;&lt;) is an
    /// unescaped inner quote. A quote followed by what looks like the next property key means
    /// the comma between two properties is missing.
    /// </summary>
    private static QuoteRole ClassifyQuote(string json, int index)
    {
        while (index < json.Length && char.IsWhiteSpace(json[index]))
        {
            index++;
        }

        if (index >= json.Length)
        {
            return QuoteRole.Closing;
        }

        var next = json[index];
        if (next is ',' or '}' or ']' or ':')
        {
            return QuoteRole.Closing;
        }

        if (next == '"' && LooksLikePropertyKey(json, index))
        {
            return QuoteRole.ClosingBeforeMissingComma;
        }

        if (next == '{' && LooksLikeObjectAfterValue(json, index))
        {
            return QuoteRole.ClosingBeforeMissingComma;
        }

        return QuoteRole.Inner;
    }

    private static bool LooksLikePropertyKey(string json, int quoteIndex)
    {
        var cursor = quoteIndex + 1;
        var length = 0;
        while (cursor < json.Length && json[cursor] != '"' && json[cursor] != '\n' && length < 80)
        {
            cursor++;
            length++;
        }

        if (cursor >= json.Length || json[cursor] != '"' || length == 0)
        {
            return false;
        }

        cursor++;
        while (cursor < json.Length && char.IsWhiteSpace(json[cursor]))
        {
            cursor++;
        }

        return cursor < json.Length && json[cursor] == ':';
    }

    private static bool LooksLikeObjectAfterValue(string json, int braceIndex)
    {
        var cursor = braceIndex + 1;
        while (cursor < json.Length && char.IsWhiteSpace(json[cursor]))
        {
            cursor++;
        }

        return cursor < json.Length && json[cursor] == '"' && LooksLikePropertyKey(json, cursor);
    }

    // A value token directly after a closed string, closed container, number, or literal
    // means a comma was dropped. ':' , '{' '[' and ',' legitimately precede values.
    private static bool NeedsSeparator(char lastSignificant)
        => lastSignificant is '"' or '}' or ']' or '0' or 'l';

    private static bool IsHex4(string json, int index)
    {
        for (var offset = 0; offset < 4; offset++)
        {
            if (!char.IsAsciiHexDigit(json[index + offset]))
            {
                return false;
            }
        }

        return true;
    }
}
