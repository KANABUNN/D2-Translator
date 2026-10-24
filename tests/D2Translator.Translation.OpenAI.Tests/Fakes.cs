using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using D2Translator.Application.Translation;
using D2Translator.Domain.Translation;

namespace D2Translator.Translation.OpenAI.Tests;

internal sealed class FakeSecrets(string? key = "sk-test-key-not-real") : ISecretStore
{
    public bool Exists(string secretRef) => key is not null;

    public Task<SecretLease?> OpenAsync(string secretRef, CancellationToken ct) =>
        Task.FromResult(key is null ? null : new SecretLease(key));

    public Task SaveAsync(string secretRef, string value, CancellationToken ct) => Task.CompletedTask;

    public Task DeleteAsync(string secretRef, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>要求を記録し、用意した応答を順に返す HTTP ハンドラー。</summary>
internal sealed class FakeHandler(params Func<HttpRequestMessage, Task<HttpResponseMessage>>[] responses) : HttpMessageHandler
{
    private int _index;

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond = responses[Math.Min(_index++, responses.Length - 1)];
        return await respond(request).WaitAsync(cancellationToken);
    }
}

internal static class Api
{
    public static TranslationRequest Request(TimeSpan? deadline = null) => new()
    {
        RequestId = Guid.NewGuid(),
        MessageId = Guid.Parse("bf6276f0-f482-4be2-9b7c-d7e6b68d1f04"),
        Revision = 1,
        Direction = TranslationDirection.EnJa,
        SourceText = "LF2M KWTD ⟦P2⟧ can u hear me?",
        SpeakerKey = "P1",
        Glossary = [new GlossaryHint("d2.lfg.kwtd", "KWTD", "手順を理解していること")],
        Context = [new ContextLine("P3", "ready up")],
        GlossaryVersion = "example",
        Deadline = deadline ?? TimeSpan.FromSeconds(4),
    };

    public static string ResultJson(TranslationRequest request, string text = "あと2人募集。手順を理解している人で。⟦P2⟧、聞こえる？") =>
        new JsonObject
        {
            ["messageId"] = request.MessageId.ToString(),
            ["revision"] = request.Revision,
            ["translatedText"] = text,
            ["appliedTermIds"] = new JsonArray("d2.lfg.kwtd"),
            ["ambiguities"] = new JsonArray(),
            ["warnings"] = new JsonArray(),
        }.ToJsonString();

    /// <summary>推論の項目を先に置いた、Responses API 形式の成功応答。</summary>
    public static string Response(string outputText, string model = "gpt-6-luna", string status = "completed", JsonNode? extraContent = null)
    {
        var content = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = outputText, ["annotations"] = new JsonArray() });
        if (extraContent is not null)
        {
            content.Insert(0, extraContent);
        }

        return new JsonObject
        {
            ["id"] = "resp_test",
            ["object"] = "response",
            ["status"] = status,
            ["model"] = model,
            ["output"] = new JsonArray(
                new JsonObject { ["type"] = "reasoning", ["id"] = "rs_1", ["summary"] = new JsonArray() },
                new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = content }),
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 1000,
                ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 200 },
                ["output_tokens"] = 200,
                ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
                ["total_tokens"] = 1200,
            },
        }.ToJsonString();
    }

    public static Func<HttpRequestMessage, Task<HttpResponseMessage>> Reply(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null) =>
        _ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            configure?.Invoke(response);
            return Task.FromResult(response);
        };

    public static string Error(string code, string? param = null) =>
        new JsonObject { ["error"] = new JsonObject { ["message"] = "secret details must not leak", ["type"] = "x", ["param"] = param, ["code"] = code } }.ToJsonString();
}
