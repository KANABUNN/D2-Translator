using D2Translator.Domain.Geometry;
using D2Translator.Domain.Ocr;
using D2Translator.TextTracking.Rows;

namespace D2Translator.TextTracking.Tests;

public class RowAssemblerTests
{
    private const int Height = 20;

    private static OcrWord Word(string text, int x, int width, int y = 10) => new(text, new PixelRect(x, y, width, Height));

    private static OcrLine Line(params OcrWord[] words)
    {
        int left = words.Min(w => w.Bounds.X), right = words.Max(w => w.Bounds.Right);
        int top = words.Min(w => w.Bounds.Y), bottom = words.Max(w => w.Bounds.Bottom);
        return new OcrLine(string.Join(' ', words.Select(w => w.Text)), new PixelRect(left, top, right - left, bottom - top), words);
    }

    /// <summary>日本語 OCR のように1文字ずつの語を並べる。</summary>
    private static OcrWord[] Chars(string text, int x, int y = 10) =>
        [.. text.Select((c, i) => Word(c.ToString(), x + i * 20, 18, y))];

    [Fact]
    public void Japanese_prefix_comes_from_cjk_engine_and_body_from_latin_engine()
    {
        OcrLine cjk = Line(Chars("[ファイアチーム]宛て:", 10));
        // 英語 OCR は日本語の範囲を記号列にし、本文は正しく読む
        OcrLine latin = Line(Word("{7c", 12, 100), Word("tco", 120, 120), Word("Need", 260, 50), Word("two", 318, 36), Word("more", 362, 50));

        OcrLine row = Assert.Single(ScriptAwareRowAssembler.Assemble([latin], [cjk]));

        Assert.Equal("[ファイアチーム]宛て: Need two more", row.Text);
    }

    [Fact]
    public void Name_and_join_notice_are_joined_without_space()
    {
        OcrWord[] notice = Chars("がチームチャットに参加しました。", 152);
        OcrLine cjk = Line([Word("Pancake916#1234", 10, 140), .. notice]);
        OcrLine latin = Line(Word("Pancake916#1234", 10, 140), Word("Ctco", 152, 200));

        OcrLine row = Assert.Single(ScriptAwareRowAssembler.Assemble([latin], [cjk]));

        Assert.Equal("Pancake916#1234がチームチャットに参加しました。", row.Text);
    }

    [Fact]
    public void Hyphen_after_katakana_becomes_long_vowel_mark()
    {
        OcrLine cjk = Line(Chars("[ファイアチ-ム]宛て:", 10));

        Assert.Equal("[ファイアチーム]宛て:", Assert.Single(ScriptAwareRowAssembler.Assemble([], [cjk])).Text);
    }

    [Fact]
    public void Latin_word_partly_over_japanese_is_trimmed()
    {
        OcrLine cjk = Line(Chars("がチーム", 150));
        OcrLine latin = Line(Word("Kestrel#1234Ct", 10, 160));

        OcrLine row = Assert.Single(ScriptAwareRowAssembler.Assemble([latin], [cjk]));

        Assert.Equal("Kestrel#1234がチーム", row.Text);
    }

    [Fact]
    public void Lines_split_by_a_gap_are_merged_into_one_row()
    {
        OcrLine name = Line(Word("[Kestrel#5678]:", 10, 120));
        OcrLine body = Line(Word("Sounds", 180, 60, y: 11), Word("broken", 248, 60, y: 11));

        OcrLine row = Assert.Single(ScriptAwareRowAssembler.Assemble([name, body], []));

        Assert.Equal("[Kestrel#5678]: Sounds broken", row.Text);
    }

    [Fact]
    public void Rows_are_ordered_top_to_bottom()
    {
        OcrLine second = Line(Word("second", 10, 60, y: 40));
        OcrLine first = Line(Word("first", 10, 50, y: 10));

        IReadOnlyList<OcrLine> rows = ScriptAwareRowAssembler.Assemble([second, first], []);

        Assert.Equal(["first", "second"], rows.Select(r => r.Text));
    }

    [Fact]
    public void Single_japanese_symbol_misread_from_latin_does_not_cut_words()
    {
        // 日本語 OCR が英単語 for の r を「と読んだ場合
        OcrLine cjk = Line(Word("fo", 10, 20), Word("「", 31, 8));
        OcrLine latin = Line(Word("for", 10, 30), Word("the", 48, 30));

        OcrLine row = Assert.Single(ScriptAwareRowAssembler.Assemble([latin], [cjk]));

        Assert.Equal("for the", row.Text);
    }

    [Fact]
    public void Latin_reading_from_cjk_engine_fills_gaps()
    {
        // 英語 OCR が1文字の本文を落とした場合
        OcrLine cjk = Line([.. Chars("[クラン]宛て:", 10), Word("B", 200, 12)]);

        OcrLine row = Assert.Single(ScriptAwareRowAssembler.Assemble([], [cjk]));

        Assert.Equal("[クラン]宛て: B", row.Text);
    }
}
