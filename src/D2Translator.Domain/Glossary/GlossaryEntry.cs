namespace D2Translator.Domain.Glossary;

public enum GlossaryDirection
{
    EnJa,
    JaEn,
    Both,
}

public enum GlossaryReviewStatus
{
    /// <summary>D2 を理解する日英評価者の確認前（仕様書 9.1）。</summary>
    Pending,
    Reviewed,
}

public sealed record GlossaryConditions
{
    /// <summary>Any または活動 ID。</summary>
    public string Activity { get; init; } = "Any";
}

public sealed record GlossaryProvenance
{
    public required string Kind { get; init; }

    public GlossaryReviewStatus ReviewStatus { get; init; } = GlossaryReviewStatus.Pending;

    public DateTimeOffset? ReviewedAt { get; init; }
}

/// <summary>仕様書 9.1、13.2 の辞書項目。多義語は senseId を分けた複数項目で表す。</summary>
public sealed record GlossaryEntry
{
    public required string TermId { get; init; }

    public required string SenseId { get; init; }

    public required string Source { get; init; }

    public required string Target { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public GlossaryDirection Direction { get; init; } = GlossaryDirection.EnJa;

    public GlossaryConditions Conditions { get; init; } = new();

    public IReadOnlyList<string> Examples { get; init; } = [];

    /// <summary>文脈で確認すべき点。曖昧さの表示に使う。</summary>
    public string? Note { get; init; }

    public required GlossaryProvenance Provenance { get; init; }
}

public sealed record GlossaryDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string DictionaryVersion { get; init; }

    public IReadOnlyList<GlossaryEntry> Entries { get; init; } = [];
}
