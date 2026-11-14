namespace Lexarbor.Domain.Models;

public sealed class VocabularyPartOfSpeech
{
    private static readonly VocabularyPartOfSpeech[] Types =
    [
        new("noun", "n", "n."),
        new("verb", "v", "v.", "vt", "vt.", "vi", "vi.", "transitive verb", "intransitive verb", "aux", "aux.", "auxiliary verb", "modal verb"),
        new("adjective", "adj", "adj.", "a", "a."),
        new("adverb", "adv", "adv."),
        new("pronoun", "pron", "pron."),
        new("preposition", "prep", "prep."),
        new("conjunction", "conj", "conj."),
        new("determiner", "det", "det."),
        new("article", "art", "art."),
        new("numeral", "num", "num."),
        new("interjection", "int", "int.", "interj", "interj.")
    ];

    private VocabularyPartOfSpeech(string code, params string[] aliases)
    {
        Code = code;
        Aliases = Array.AsReadOnly([code, .. aliases]);
    }

    public string Code { get; }
    public IReadOnlyList<string> Aliases { get; }
    public static IReadOnlyList<string> KnownAliases { get; } = Array.AsReadOnly(Types.SelectMany(type => type.Aliases).ToArray());

    public static VocabularyPartOfSpeech? FromLabel(string? label)
    {
        if (label == null) return null;
        var key = CatalogKeys.Of(label);
        return Types.FirstOrDefault(type => type.Aliases.Contains(key, StringComparer.Ordinal));
    }
}
