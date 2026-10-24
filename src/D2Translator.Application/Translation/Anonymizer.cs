using System.Text;
using System.Text.RegularExpressions;
using D2Translator.Domain.Chat;

namespace D2Translator.Application.Translation;

/// <param name="Text">外部へ送る本文。名前等は ⟦P1⟧ のような記号になっている。</param>
/// <param name="Originals">記号と、元の表記の対応。訳文で元へ戻すために端末内でだけ使う。</param>
public sealed record AnonymizedText(string Text, IReadOnlyDictionary<string, string> Originals);

/// <summary>
/// 外部送信前の匿名化（仕様書 9.2、18.1、決定 D14）。
/// 本文中の既知の話者名を一時 ID の記号に、URL とメールアドレスを番号付きの記号に置き換える。
/// 本文に書かれたその他の個人情報までは除去できず、完全な匿名化とは扱わない。
/// </summary>
public static partial class Anonymizer
{
    public const char Open = '⟦';
    public const char Close = '⟧';

    [GeneratedRegex(@"(?:https?://|www\.)[^\s⟦⟧]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, 50)]
    private static partial Regex MailPattern();

    [GeneratedRegex(@"⟦[A-Z]+\d+⟧", RegexOptions.CultureInvariant, 50)]
    private static partial Regex PlaceholderPattern();

    public static AnonymizedText Anonymize(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string text = message.Text;
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);

        // 注釈の位置は message.Text に対するもの。後ろから置き換えて位置をずらさない
        var builder = new StringBuilder(text);
        foreach (TextAnnotation mention in message.Annotations
            .Where(a => a.Kind == AnnotationKind.SpeakerMention && a.ReferenceId is not null && a.Start + a.Length <= text.Length)
            .OrderByDescending(a => a.Start))
        {
            string token = $"{Open}{mention.ReferenceId}{Close}";
            originals[token] = mention.Text;
            builder.Remove(mention.Start, mention.Length).Insert(mention.Start, token);
        }

        string masked = Mask(builder.ToString(), UrlPattern(), "URL", originals);
        masked = Mask(masked, MailPattern(), "MAIL", originals);
        return new AnonymizedText(masked, originals);
    }

    /// <summary>訳文の記号を元の表記へ戻す。</summary>
    public static string Restore(string translated, IReadOnlyDictionary<string, string> originals)
    {
        ArgumentNullException.ThrowIfNull(translated);
        foreach ((string token, string original) in originals)
        {
            translated = translated.Replace(token, original, StringComparison.Ordinal);
        }

        return translated;
    }

    /// <summary>原文にあった記号のうち、訳文から消えたもの。</summary>
    public static IReadOnlyList<string> MissingTokens(string anonymizedSource, string translated)
    {
        HashSet<string> present = [.. PlaceholderPattern().Matches(translated).Select(m => m.Value)];
        return [.. PlaceholderPattern().Matches(anonymizedSource).Select(m => m.Value).Distinct().Where(t => !present.Contains(t))];
    }

    private static string Mask(string text, Regex pattern, string kind, Dictionary<string, string> originals)
    {
        int index = 0;
        return pattern.Replace(text, match =>
        {
            string token = $"{Open}{kind}{++index}{Close}";
            originals[token] = match.Value;
            return token;
        });
    }
}
