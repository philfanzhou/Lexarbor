using System.Net;
using System.Text.RegularExpressions;
using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Services;

/// <summary>Reads English headwords and their printed glosses from OCR rows and table cells.</summary>
internal static partial class VocabularyPhotoEntries
{
    internal sealed record Entry(string Word, string? PartOfSpeech, string Meaning);

    internal static IReadOnlyList<Entry> Read(IReadOnlyList<VocabRecognizedBlock> blocks)
    {
        var entries = new List<Entry>();
        var lines = new List<VocabRecognizedLine>();
        foreach (var block in blocks)
        {
            if (block.ContentFormat == "plain" && block.Type is not ("title" or "caption" or "footnote") && block.Lines.Count > 0)
            {
                lines.AddRange(block.Lines);
                continue;
            }

            ReadLocatedRows(lines, entries);
            lines.Clear();
            if (block.Content is not { } content) continue;
            if (block.ContentFormat == "html")
            {
                foreach (Match row in TableRow().Matches(content))
                {
                    var cells = TableCell().Matches(row.Groups["content"].Value)
                        .Select(cell => WebUtility.HtmlDecode(HtmlTag().Replace(cell.Groups["content"].Value,
                            tag => tag.Groups["break"].Success ? " " : "")));
                    ReadRow(cells, entries);
                }
            }
            else if (block.ContentFormat == "plain" && block.Type is not ("title" or "caption" or "footnote"))
            {
                var canContinue = false;
                foreach (var row in content.Split('\n'))
                    canContinue = ReadRow([row], entries, canContinue) == 1;
            }
        }
        ReadLocatedRows(lines, entries);

        var withMeaning = entries.Where(entry => entry.Meaning.Length > 0).Select(entry => SpellingKey(entry.Word)).ToHashSet();
        return entries.Where(entry => entry.Meaning.Length > 0 || !withMeaning.Contains(SpellingKey(entry.Word)))
            .DistinctBy(entry => (SpellingKey(entry.Word), entry.Meaning))
            .ToArray();
    }

    // Match Lexarbor's ASCII case folding while retaining the OCR spelling for a miss.
    internal static string SpellingKey(string word) => string.Create(word.Length, word, static (key, source) =>
    {
        for (var index = 0; index < source.Length; index++)
        {
            var letter = source[index];
            key[index] = letter is >= 'A' and <= 'Z' ? (char)(letter + ('a' - 'A')) : letter;
        }
    });

    private static void ReadLocatedRows(List<VocabRecognizedLine> lines, List<Entry> entries)
    {
        var ordered = lines.OrderBy(MiddleY).ToArray();
        VocabRecognizedLine[] previous = [];
        var previousCount = 0;
        for (var start = 0; start < ordered.Length;)
        {
            var anchor = ordered[start];
            var end = start + 1;
            while (end < ordered.Length &&
                   MiddleY(ordered[end]) < anchor.Y1 && MiddleY(anchor) > ordered[end].Y0)
            {
                end++;
            }

            var row = ordered[start..end].OrderBy(line => line.X0).ToArray();
            var first = row[0];
            var canContinue = previousCount == 1 && previous.Any(line =>
                first.X0 >= line.X0 - 0.01 && first.X0 <= line.X1 + 0.01 &&
                first.Y0 - line.Y1 <= Math.Max(line.Y1 - line.Y0, first.Y1 - first.Y0));
            previousCount = ReadRow(row.Select(line => line.Text), entries, canContinue);
            previous = row;
            start = end;
        }
    }

    private static double MiddleY(VocabRecognizedLine line) => (line.Y0 + line.Y1) / 2;

    private static int ReadRow(IEnumerable<string> cells, List<Entry> entries, bool canContinue = false)
    {
        var before = entries.Count;
        var continued = false;
        Entry? current = null;
        foreach (var cell in cells)
        {
            foreach (var piece in SplitCell(cell))
            {
                var original = piece.Trim();
                var text = ListMarker().Replace(original, "").Trim();
                if (text.Length == 0) continue;
                if (StartsGloss(text))
                {
                    if (current is not null)
                        current = current with { Meaning = JoinGloss(current.Meaning, original) };
                    else if (canContinue)
                    {
                        var previous = entries[^1];
                        entries[^1] = previous with { Meaning = JoinGloss(previous.Meaning, original) };
                        continued = true;
                    }
                    continue;
                }

                if (current is not null)
                {
                    var boundary = GlossBoundary(text);
                    var part = PartOfSpeech().Match(TrimDelimiter(text[..boundary]));
                    if (part.Success && part.Index == 0)
                    {
                        current = current with
                        {
                            PartOfSpeech = current.PartOfSpeech ?? part.Groups["pos"].Value,
                            Meaning = JoinGloss(current.Meaning, text[boundary..].Trim()),
                        };
                        continue;
                    }
                }

                var entry = Parse(text);
                if (entry is null) continue;
                if (current is not null) entries.Add(current);
                current = entry;
            }
        }

        if (current is not null) entries.Add(current);
        return entries.Count - before + (continued ? 1 : 0);
    }

    private static IEnumerable<string> SplitCell(string text)
    {
        var han = Han().Match(text);
        if (!han.Success) return text.Split('\t', '|', '｜', ',', '，', ';', '；', '、');

        // OCR may return more than one printed entry in a single line. Split on explicit entry separators
        // followed by an English headword, and keep the punctuation inside the Chinese definition.
        var glosses = InlineEntryBoundary().Split(text[han.Index..]);
        var pieces = text[..han.Index].Split('\t', '|', '｜');
        pieces[^1] += glosses[0];
        return pieces.Concat(glosses.Skip(1));
    }

    private static bool StartsGloss(string text)
    {
        var start = text.TrimStart(' ', '（', '(', '【', '[', '「', '『', '“', '《', '；', ';', '、', '，', ',');
        var match = Han().Match(start);
        return match.Success && match.Index == 0;
    }

    private static string JoinGloss(string first, string next) =>
        first.Length == 0 ? next : next.Length == 0 ? first : first + " " + next;

    private static int GlossBoundary(string text)
    {
        var han = Han().Match(text);
        if (!han.Success) return text.Length;

        var boundary = han.Index;
        while (boundary > 0 && "（(【[「『“《 \t".Contains(text[boundary - 1])) boundary--;
        return boundary;
    }

    private static string TrimDelimiter(string text) => text.TrimEnd(' ', '\t', ':', '：', '—', '–', ',', '，', '|', '｜');

    private static Entry? Parse(string text)
    {
        var boundary = GlossBoundary(text);
        var head = TrimDelimiter(text[..boundary]);
        var part = PartOfSpeech().Match(head);
        var partOfSpeech = part.Success && part.Index > 0 ? part.Groups["pos"].Value : null;
        if (partOfSpeech is not null) head = TrimDelimiter(head[..part.Index]);
        head = Pronunciation().Replace(head, "").Trim();
        if (head.Length > VocabNotebook.MaxWordLength || !Spelling().IsMatch(head)) return null;

        return new Entry(head, partOfSpeech, text[boundary..].Trim());
    }

    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Latin = @"A-Za-zÀ-ÖØ-öø-ɏＡ-Ｚａ-ｚ";
    private const string Word = "[" + Latin + "][" + Latin + @"0-9]*(?:['’‐‑-][" + Latin + @"0-9]+)*";
    private const string Pos = @"(?:adj|adv|prep|pron|conj|det|num|art|interj|int|aux|modal|abbr|phr|vt|vi|n|v)[.．]?";
    private const string NumberedMarker = @"[0-9０-９]+[.．、)）:：]\s*";

    [GeneratedRegex("^" + Word + @"(?:\s+" + Word + ")*$")]
    private static partial Regex Spelling();

    [GeneratedRegex(@"(?:^|[\s:：])\s*[\[(（]?(?<pos>" + Pos + @"(?:\s*/\s*" + Pos + @")*)[\])）]?$", IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex PartOfSpeech();

    [GeneratedRegex(@"\s*(?:/[^/\r\n]+/|\[[^\]\r\n]+\])$", RegexOptions.NonBacktracking)]
    private static partial Regex Pronunciation();

    [GeneratedRegex("^(?:" + NumberedMarker + @"|[•●▪]\s*)")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"(?:[|\t｜]\s*(?=(?:" + NumberedMarker + @")?[" + Latin + @"])|[\s;；](?=" + NumberedMarker + "[" + Latin + "]))")]
    private static partial Regex InlineEntryBoundary();

    [GeneratedRegex(@"[\u3400-\u9fff\uf900-\ufaff]")]
    private static partial Regex Han();

    [GeneratedRegex(@"<tr\b[^>]*>(?<content>.*?)</tr\s*>", IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking)]
    private static partial Regex TableRow();

    [GeneratedRegex(@"<td\b[^>]*>(?<content>.*?)</td\s*>", IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking)]
    private static partial Regex TableCell();

    [GeneratedRegex(@"<(?:(?<break>br\b|/p\b)[^>]*|[^>]*)>", IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex HtmlTag();
}
