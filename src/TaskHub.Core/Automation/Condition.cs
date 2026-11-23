using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub.Core.Automation;

/// <summary>
/// The <c>if</c> part of a rule — a small, safe expression language evaluated against the event:
/// <code>
/// task.tags contains 'standup'
/// task.priority == 'urgent' or task.title startswith 'Call'
/// extension in ['.pdf', '.docx'] and not (name contains 'draft')
/// percent &lt;= 15
/// name matches '^invoice-\d+'
/// </code>
/// Operators: <c>== != &lt; &lt;= &gt; &gt;=</c>, <c>contains</c>, <c>startswith</c>, <c>endswith</c>,
/// <c>matches</c> (regex), <c>in [..]</c>, <c>and</c>, <c>or</c>, <c>not</c>, parentheses.
/// Text comparisons ignore case. A field on its own is true when it has a value
/// (not empty, not 0, not false). Missing fields are empty.
/// </summary>
public sealed class Condition
{
    private readonly Func<JsonObject, object?> _eval;

    private Condition(string text, Func<JsonObject, object?> eval)
    {
        Text = text;
        _eval = eval;
    }

    public string Text { get; }

    public bool Evaluate(JsonObject context) => Truthy(_eval(context));

    public static Condition Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new AutomationException("The condition is empty.");
        var parser = new Parser(Tokenize(text));
        var eval = parser.ParseExpression();
        parser.ExpectEnd();
        return new Condition(text.Trim(), eval);
    }

    public static bool TryParse(string text, out Condition? condition, out string? error)
    {
        try
        {
            condition = Parse(text);
            error = null;
            return true;
        }
        catch (AutomationException ex)
        {
            condition = null;
            error = ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ values

    /// <summary>Looks up a dotted path (case-insensitive) and converts it to a plain value.</summary>
    public static object? Resolve(JsonObject context, string path)
    {
        JsonNode? node = context;
        foreach (var segment in path.Split('.'))
        {
            if (node is not JsonObject obj) return null;
            JsonNode? next = null;
            var found = false;
            foreach (var (key, value) in obj)
            {
                if (string.Equals(key, segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = value;
                    found = true;
                    break;
                }
            }
            if (!found) return null;
            node = next;
        }
        return ToValue(node);
    }

    internal static object? ToValue(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonArray array:
                return array.Select(ToValue).ToList();
            case JsonObject obj:
                return obj;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String: return value.GetValue<string>();
                    case JsonValueKind.True: return true;
                    case JsonValueKind.False: return false;
                    case JsonValueKind.Number:
                        return double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                        return null;
                    default:
                        return value.ToJsonString();
                }
            default:
                return null;
        }
    }

    /// <summary>Text form of a value, as used by templates and string comparisons.</summary>
    public static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString(CultureInfo.InvariantCulture),
        IEnumerable<object?> list => string.Join(", ", list.Select(Format)),
        JsonNode n => n.ToJsonString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        double d => d != 0,
        string s => s.Length > 0 && !s.Equals("false", StringComparison.OrdinalIgnoreCase),
        IReadOnlyCollection<object?> list => list.Count > 0,
        _ => true,
    };

    private static bool TryNumber(object? v, out double d)
    {
        switch (v)
        {
            case double x:
                d = x;
                return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                d = parsed;
                return true;
            default:
                d = 0;
                return false;
        }
    }

    private static bool ValueEquals(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is IEnumerable<object?> || b is IEnumerable<object?>) return Format(a) == Format(b);
        if (a is bool || b is bool) return Truthy(a) == Truthy(b);
        if (a is double || b is double)
        {
            if (TryNumber(a, out var x) && TryNumber(b, out var y)) return x.Equals(y);
            return false;
        }
        return string.Equals(Format(a), Format(b), StringComparison.OrdinalIgnoreCase);
    }

    private static int Compare(object? a, object? b)
    {
        if (TryNumber(a, out var x) && TryNumber(b, out var y)) return x.CompareTo(y);
        var sa = Format(a);
        var sb = Format(b);
        if (DateTimeOffset.TryParse(sa, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var da)
            && DateTimeOffset.TryParse(sb, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var db))
            return da.CompareTo(db);
        return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(object? haystack, object? needle) => haystack switch
    {
        null => false,
        IEnumerable<object?> list => list.Any(item => ValueEquals(item, needle)),
        _ => Format(haystack).Contains(Format(needle), StringComparison.OrdinalIgnoreCase),
    };

    private static bool Matches(object? value, Regex regex)
    {
        try
        {
            return value is IEnumerable<object?> list ? list.Any(i => regex.IsMatch(Format(i))) : regex.IsMatch(Format(value));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ tokens

    private enum Kind { Identifier, String, Number, Operator, LParen, RParen, LBracket, RBracket, Comma, End }

    private readonly record struct Token(Kind Kind, string Text, int Position);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            var start = i;
            switch (c)
            {
                case '(':
                    tokens.Add(new Token(Kind.LParen, "(", start));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new Token(Kind.RParen, ")", start));
                    i++;
                    continue;
                case '[':
                    tokens.Add(new Token(Kind.LBracket, "[", start));
                    i++;
                    continue;
                case ']':
                    tokens.Add(new Token(Kind.RBracket, "]", start));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new Token(Kind.Comma, ",", start));
                    i++;
                    continue;
                case '\'' or '"':
                {
                    var quote = c;
                    var sb = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (i >= text.Length) throw new AutomationException($"Unclosed text starting at position {start + 1}.");
                        if (text[i] == quote)
                        {
                            // A doubled quote inside the text is a literal quote: 'it''s'
                            if (i + 1 < text.Length && text[i + 1] == quote)
                            {
                                sb.Append(quote);
                                i += 2;
                                continue;
                            }
                            i++;
                            break;
                        }
                        sb.Append(text[i++]);
                    }
                    tokens.Add(new Token(Kind.String, sb.ToString(), start));
                    continue;
                }
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(new Token(Kind.Number, text[start..i], start));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.' or '-')) i++;
                tokens.Add(new Token(Kind.Identifier, text[start..i].TrimEnd('.'), start));
                continue;
            }
            if (c is '=' or '!' or '<' or '>')
            {
                i++;
                if (i < text.Length && text[i] == '=') i++;
                var op = text[start..i];
                if (op == "!") throw new AutomationException($"Unexpected '!' at position {start + 1}. Use 'not' or '!='.");
                tokens.Add(new Token(Kind.Operator, op == "=" ? "==" : op, start));
                continue;
            }
            if (c == '&' && i + 1 < text.Length && text[i + 1] == '&')
            {
                tokens.Add(new Token(Kind.Identifier, "and", start));
                i += 2;
                continue;
            }
            if (c == '|' && i + 1 < text.Length && text[i + 1] == '|')
            {
                tokens.Add(new Token(Kind.Identifier, "or", start));
                i += 2;
                continue;
            }
            throw new AutomationException($"Unexpected '{c}' at position {start + 1}.");
        }
        tokens.Add(new Token(Kind.End, "", text.Length));
        return tokens;
    }

    // ------------------------------------------------------------------ parser

    private sealed class Parser(List<Token> tokens)
    {
        private static readonly HashSet<string> WordOperators = new(StringComparer.OrdinalIgnoreCase)
        {
            "contains", "startswith", "endswith", "matches", "in",
        };

        private int _pos;

        private Token Peek => tokens[_pos];

        private Token Next() => tokens[_pos++];

        private bool IsWord(string word) =>
            Peek.Kind == Kind.Identifier && string.Equals(Peek.Text, word, StringComparison.OrdinalIgnoreCase);

        public void ExpectEnd()
        {
            if (Peek.Kind != Kind.End)
                throw new AutomationException($"Unexpected '{Peek.Text}' at position {Peek.Position + 1}.");
        }

        public Func<JsonObject, object?> ParseExpression() => ParseOr();

        private Func<JsonObject, object?> ParseOr()
        {
            var left = ParseAnd();
            while (IsWord("or"))
            {
                Next();
                var l = left;
                var r = ParseAnd();
                left = ctx => Truthy(l(ctx)) || Truthy(r(ctx));
            }
            return left;
        }

        private Func<JsonObject, object?> ParseAnd()
        {
            var left = ParseNot();
            while (IsWord("and"))
            {
                Next();
                var l = left;
                var r = ParseNot();
                left = ctx => Truthy(l(ctx)) && Truthy(r(ctx));
            }
            return left;
        }

        private Func<JsonObject, object?> ParseNot()
        {
            if (IsWord("not"))
            {
                Next();
                var inner = ParseNot();
                return ctx => !Truthy(inner(ctx));
            }
            return ParseComparison();
        }

        private Func<JsonObject, object?> ParseComparison()
        {
            var left = ParseOperand();
            string? op = null;
            var negate = false;
            if (Peek.Kind == Kind.Operator)
            {
                op = Next().Text;
            }
            else if (IsWord("not") && _pos + 1 < tokens.Count && tokens[_pos + 1].Kind == Kind.Identifier
                     && WordOperators.Contains(tokens[_pos + 1].Text))
            {
                // "tags not contains 'x'", "ext not in [...]"
                Next();
                negate = true;
                op = Next().Text.ToLowerInvariant();
            }
            else if (Peek.Kind == Kind.Identifier && WordOperators.Contains(Peek.Text))
            {
                op = Next().Text.ToLowerInvariant();
            }
            if (op is null) return left;

            Func<JsonObject, object?> result;
            if (op == "matches")
            {
                var patternToken = Next();
                if (patternToken.Kind != Kind.String)
                    throw new AutomationException($"'matches' needs a quoted pattern at position {patternToken.Position + 1}.");
                Regex regex;
                try
                {
                    regex = new Regex(patternToken.Text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException ex)
                {
                    throw new AutomationException($"Invalid pattern '{patternToken.Text}': {ex.Message}");
                }
                result = ctx => Matches(left(ctx), regex);
            }
            else
            {
                var right = ParseOperand();
                result = op switch
                {
                    "==" => ctx => ValueEquals(left(ctx), right(ctx)),
                    "!=" => ctx => !ValueEquals(left(ctx), right(ctx)),
                    "<" => ctx => Compare(left(ctx), right(ctx)) < 0,
                    "<=" => ctx => Compare(left(ctx), right(ctx)) <= 0,
                    ">" => ctx => Compare(left(ctx), right(ctx)) > 0,
                    ">=" => ctx => Compare(left(ctx), right(ctx)) >= 0,
                    "contains" => ctx => Contains(left(ctx), right(ctx)),
                    "in" => ctx => Contains(right(ctx), left(ctx)),
                    "startswith" => ctx => Format(left(ctx)).StartsWith(Format(right(ctx)), StringComparison.OrdinalIgnoreCase),
                    "endswith" => ctx => Format(left(ctx)).EndsWith(Format(right(ctx)), StringComparison.OrdinalIgnoreCase),
                    _ => throw new AutomationException($"Unknown operator '{op}'."),
                };
            }
            if (!negate) return result;
            var inner = result;
            return ctx => !Truthy(inner(ctx));
        }

        private Func<JsonObject, object?> ParseOperand()
        {
            var t = Next();
            switch (t.Kind)
            {
                case Kind.String:
                {
                    var s = t.Text;
                    return _ => s;
                }
                case Kind.Number:
                {
                    if (!double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                        throw new AutomationException($"Invalid number '{t.Text}'.");
                    return _ => n;
                }
                case Kind.LParen:
                {
                    var inner = ParseOr();
                    if (Next().Kind != Kind.RParen) throw new AutomationException($"Missing ')' for '(' at position {t.Position + 1}.");
                    return inner;
                }
                case Kind.LBracket:
                {
                    var items = new List<Func<JsonObject, object?>>();
                    if (Peek.Kind != Kind.RBracket)
                    {
                        while (true)
                        {
                            items.Add(ParseOperand());
                            if (Peek.Kind == Kind.Comma)
                            {
                                Next();
                                continue;
                            }
                            break;
                        }
                    }
                    if (Next().Kind != Kind.RBracket) throw new AutomationException($"Missing ']' for '[' at position {t.Position + 1}.");
                    return ctx => items.Select(f => f(ctx)).ToList();
                }
                case Kind.Identifier:
                {
                    var word = t.Text;
                    if (word.Equals("true", StringComparison.OrdinalIgnoreCase)) return _ => true;
                    if (word.Equals("false", StringComparison.OrdinalIgnoreCase)) return _ => false;
                    if (word.Equals("null", StringComparison.OrdinalIgnoreCase) || word.Equals("empty", StringComparison.OrdinalIgnoreCase)) return _ => null;
                    if (word.Equals("and", StringComparison.OrdinalIgnoreCase) || word.Equals("or", StringComparison.OrdinalIgnoreCase))
                        throw new AutomationException($"Expected a value before '{word}' at position {t.Position + 1}.");
                    return ctx => Resolve(ctx, word);
                }
                case Kind.End:
                    throw new AutomationException("The condition ends too early.");
                default:
                    throw new AutomationException($"Unexpected '{t.Text}' at position {t.Position + 1}.");
            }
        }
    }
}
