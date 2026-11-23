namespace TaskHub.Core.Launcher;

/// <summary>
/// Scores how well a typed query matches an item name. Every word of the query must match
/// (in the name or the keywords); better kinds of match score higher:
/// exact &gt; prefix &gt; start of a word &gt; initials ("vsc" → Visual Studio Code) &gt; substring &gt; letters in order.
/// 0 means no match.
/// </summary>
public static class FuzzyMatcher
{
    public static int Score(string query, string name, string? keywords = null)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return 1;

        // The whole query against the whole name gets the strongest signals.
        var whole = ScoreWord(query.Trim(), name);
        if (whole >= 600) return whole + 100;

        var total = 0;
        foreach (var w in words)
        {
            var s = Math.Max(ScoreWord(w, name), keywords is null ? 0 : ScoreWord(w, keywords) - 50);
            if (s <= 0) return 0;
            total += s;
        }
        return Math.Max(total / words.Length, whole);
    }

    /// <summary>Score of one query word against a text (0 = no match).</summary>
    public static int ScoreWord(string word, string text)
    {
        if (word.Length == 0 || text.Length == 0) return 0;
        if (string.Equals(word, text, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (text.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return 800 - Math.Min(100, text.Length - word.Length);

        var starts = WordStarts(text);
        foreach (var i in starts)
        {
            if (string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return 600 - Math.Min(100, i);
        }

        if (word.Length >= 2 && IsInitials(word, text, starts)) return 500;

        var index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (index >= 0) return 400 - Math.Min(100, index);

        return word.Length >= 2 ? Subsequence(word, text) : 0;
    }

    private static List<int> WordStarts(string text)
    {
        var starts = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c)) continue;
            var prev = i == 0 ? ' ' : text[i - 1];
            if (i == 0 || !char.IsLetterOrDigit(prev) || (char.IsUpper(c) && char.IsLower(prev)))
                starts.Add(i);
        }
        return starts;
    }

    private static bool IsInitials(string word, string text, List<int> starts)
    {
        var w = 0;
        foreach (var i in starts)
        {
            if (w < word.Length && char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(word[w])) w++;
        }
        return w == word.Length;
    }

    /// <summary>All letters in order; consecutive runs score higher.</summary>
    private static int Subsequence(string word, string text)
    {
        var score = 0;
        var t = 0;
        var run = 0;
        foreach (var c in word)
        {
            var found = false;
            while (t < text.Length)
            {
                if (char.ToLowerInvariant(text[t++]) == char.ToLowerInvariant(c))
                {
                    found = true;
                    break;
                }
                run = 0;
            }
            if (!found) return 0;
            run++;
            score += run * 5;
        }
        return Math.Clamp(100 + score, 101, 350);
    }
}
