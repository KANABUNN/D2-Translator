using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using D2Translator.Domain.Errors;
using D2Translator.Domain.Translation;
using Microsoft.Extensions.Logging.Abstractions;

namespace D2Translator.Translation.OpenAI.Tests;

public class OpenAIResponsesProviderTests
{
    private static OpenAIResponsesProvider Provider(FakeHandler handler, FakeSecrets? secrets = null) =>
        new(new HttpClient(handler), secrets ?? new FakeSecrets(), TimeProvider.System, NullLogger<OpenAIResponsesProvider>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Request_uses_only_the_fixed_gpt_6_luna_profile()
    {
        TranslationRequest request = Api.Request();
        var handler = new FakeHandler(Api.Reply(HttpStatusCode.OK, Api.Response(Api.ResultJson(request))));

        await Provider(handler).TranslateAsync(request, Ct);

        (HttpRequestMessage sent, string body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.openai.com/v1/responses", sent.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "sk-test-key-not-real"), sent.Headers.Authorization);

        JsonNode json = JsonNode.Parse(body)!;
        Assert.Equal("gpt-6-luna", (string?)json["model"]);
        Assert.Equal("none", (string?)json["reasoning"]!["effort"]);
        Assert.Equal("default", (string?)json["service_tier"]);
        Assert.Equal(1024, (int?)json["max_output_tokens"]);
        Assert.False((bool?)json["store"]);
        Assert.False((bool?)json["stream"]);
        Assert.False((bool?)json["background"]);
        Assert.Empty(json["tools"]!.AsArray());
        Assert.Null(json["previous_response_id"]);
        Assert.Null(json["temperature"]);

        JsonNode format = json["text"]!["format"]!;
        Assert.Equal(("json_schema", true), ((string?)format["type"], (bool?)format["strict"]));
        Assert.False((bool?)format["schema"]!["additionalProperties"]);
        Assert.Equal(6, format["schema"]!["required"]!.AsArray().Count);

        // キーは本文に入れない（仕様書 14.2）
        Assert.DoesNotContain("sk-test-key-not-real", body, StringComparison.Ordinal);
        JsonNode input = JsonNode.Parse((string)json["input"]!)!;
        Assert.Equal(("LF2M KWTD ⟦P2⟧ can u hear me?", "P1"), ((string?)input["text"], (string?)input["speaker"]));
        Assert.Single(input["glossary"]!.AsArray());
        Assert.Single(input["context"]!.AsArray());
    }

    [Fact]
    public async Task Output_text_after_reasoning_item_is_used_and_usage_is_recorded()
    {
        TranslationRequest request = Api.Request();
        var handler = new FakeHandler(Api.Reply(HttpStatusCode.OK, Api.Response(Api.ResultJson(request))));

        TranslationResult result = await Provider(handler).TranslateAsync(request, Ct);

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal("あと2人募集。手順を理解している人で。⟦P2⟧、聞こえる？", result.TranslatedText);
        Assert.Equal(["d2.lfg.kwtd"], result.AppliedTermIds);
        Assert.Equal(("gpt-6-luna", 1), (result.ResponseModel, result.Attempts));
        Assert.Equal(new TranslationUsage(1000, 200, 200, 0), result.Usage);
    }

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-6-luna-2026-10-01")]
    public async Task Unconfirmed_response_model_is_tr05(string model)
    {
        TranslationRequest request = Api.Request();
        var handler = new FakeHandler(Api.Reply(HttpStatusCode.OK, Api.Response(Api.ResultJson(request), model)));

        TranslationResult result = await Provider(handler).TranslateAsync(request, Ct);

        Assert.Equal((TranslationOutcome.ModelUnavailable, ErrorCode.TR05), (result.Outcome, result.Code));
        Assert.Null(result.TranslatedText);
        Assert.Contains(model, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refusal_is_not_a_translation()
    {
        TranslationRequest request = Api.Request();
        var refusal = new JsonObject { ["type"] = "refusal", ["refusal"] = "no" };
        var handler = new FakeHandler(Api.Reply(HttpStatusCode.OK, Api.Response(Api.ResultJson(request), extraContent: refusal)));

        TranslationResult result = await Provider(handler).TranslateAsync(request, Ct);

        Assert.Equal(TranslationOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task Incomplete_output_is_tr04_and_not_extended()
    {
        TranslationRequest request = Api.Request();
        var handler = new FakeHandler(Api.Reply(HttpStatusCode.OK, Api.Response(Api.ResultJson(request), status: "incomplete")));

        TranslationResult result = await Provider(handler).TranslateAsync(request, Ct);

        Assert.Equal((TranslationOutcome.InvalidResponse, 1), (result.Outcome, result.Attempts));
    }

    [Theory]
    [InlineData("""{"messageId":"bf6276f0-f482-4be2-9b7c-d7e6b68d1f04","revision":1,"translatedText":"x","appliedTermIds":[],"ambiguities":[],"warnings":[],"extra":1}""")]
    [InlineData("""{"messageId":"00000000-0000-0000-0000-000000000001","revision":1,"translatedText":"x","appliedTermIds":[],"ambiguities":[],"warnings":[]}""")]
    [InlineData("""{"messageId":"bf6276f0-f482-4be2-9b7c-d7e6b68d1f04","revision":2,"translatedText":"x","appliedTermIds":[],"ambiguities":[],"warnings":[]}""")]
    [InlineData("""{"messageId":"bf6276f0-f482-4be2-9b7c-d7e6b68d1f04","revision":1,"translatedText":"  ","appliedTermIds":[],"ambiguities":[],"warnings":[]}""")]
    [InlineData("""{"messageId":"bf6276f0-f482-4be2-9b7c-d7e6b68d1f04","revision":1,"translatedText":"x","appliedTermIds":[],"ambiguities":[{"term":"a"}],"warnings":[]}""")]
    [InlineData("not json")]
    public async Task Output_not_matching_the_schema_or_request_is_rejected(string output)
    {
        var handler = new FakeHandler(Api.Reply(HttpStatusCode.OK, Api.Response(output)));

        TranslationResult result = await Provider(handler).TranslateAsync(Api.Request(), Ct);

        Assert.Equal(TranslationOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task Over_long_translation_is_rejected()
    {
        TranslationRequest request = Api.Request();
        var handler = new FakeHandler(Api.Reply(HttpStatusCode.OK, Api.Response(Api.ResultJson(request, new string('あ', 2001)))));

        Assert.Equal(TranslationOutcome.InvalidResponse, (await Provider(handler).TranslateAsync(request, Ct)).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TranslationOutcome.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, TranslationOutcome.AuthenticationFailed)]
    [InlineData(HttpStatusCode.NotFound, TranslationOutcome.ModelUnavailable)]
    public async Task Permanent_errors_are_not_retried(HttpStatusCode status, TranslationOutcome expected)
    {
        var handler = new FakeHandler(Api.Reply(status, Api.Error("invalid_api_key")));

        TranslationResult result = await Provider(handler).TranslateAsync(Api.Request(), Ct);

        Assert.Equal((expected, 1), (result.Outcome, result.Attempts));
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("secret details", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_not_found_on_400_is_tr05()
    {
        var handler = new FakeHandler(Api.Reply(HttpStatusCode.BadRequest, Api.Error("model_not_found", "model")));

        Assert.Equal(TranslationOutcome.ModelUnavailable, (await Provider(handler).TranslateAsync(Api.Request(), Ct)).Outcome);
    }

    [Fact]
    public async Task Rate_limit_is_retried_once_after_retry_after()
    {
        TranslationRequest request = Api.Request();
        var handler = new FakeHandler(
            Api.Reply(HttpStatusCode.TooManyRequests, Api.Error("rate_limit_exceeded"), r => r.Headers.Add("retry-after-ms", "100")),
            Api.Reply(HttpStatusCode.OK, Api.Response(Api.ResultJson(request))));

        TranslationResult result = await Provider(handler).TranslateAsync(request, Ct);

        Assert.Equal((TranslationOutcome.Success, 2), (result.Outcome, result.Attempts));
    }

    [Fact]
    public async Task Retry_after_beyond_the_deadline_is_not_waited_for()
    {
        var handler = new FakeHandler(
            Api.Reply(HttpStatusCode.TooManyRequests, Api.Error("rate_limit_exceeded"), r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30))));

        TranslationResult result = await Provider(handler).TranslateAsync(Api.Request(), Ct);

        Assert.Equal((TranslationOutcome.RateLimited, ErrorCode.TR02, 1), (result.Outcome, result.Code, result.Attempts));
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    [Fact]
    public async Task Insufficient_quota_stops_without_retry()
    {
        var handler = new FakeHandler(Api.Reply(HttpStatusCode.TooManyRequests, Api.Error("insufficient_quota")));

        TranslationResult result = await Provider(handler).TranslateAsync(Api.Request(), Ct);

        Assert.True(result.QuotaExhausted);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Server_errors_are_retried_once_then_reported()
    {
        var handler = new FakeHandler(Api.Reply(HttpStatusCode.ServiceUnavailable, "{}"));

        TranslationResult result = await Provider(handler).TranslateAsync(Api.Request(), Ct);

        Assert.Equal((TranslationOutcome.Transient, ErrorCode.TR03, 2), (result.Outcome, result.Code, result.Attempts));
    }

    [Fact]
    public async Task Network_failure_is_retried_once()
    {
        TranslationRequest request = Api.Request();
        var handler = new FakeHandler(
            _ => throw new HttpRequestException("connection reset"),
            Api.Reply(HttpStatusCode.OK, Api.Response(Api.ResultJson(request))));

        TranslationResult result = await Provider(handler).TranslateAsync(request, Ct);

        Assert.Equal((TranslationOutcome.Success, 2), (result.Outcome, result.Attempts));
    }

    [Fact]
    public async Task Deadline_covers_all_attempts()
    {
        var handler = new FakeHandler(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        TranslationResult result = await Provider(handler).TranslateAsync(Api.Request(TimeSpan.FromMilliseconds(300)), Ct);

        Assert.Equal((TranslationOutcome.Transient, 1), (result.Outcome, result.Attempts));
        Assert.True(result.Latency < TimeSpan.FromSeconds(2), $"latency={result.Latency}");
    }

    [Fact]
    public async Task Oversized_response_is_rejected()
    {
        var handler = new FakeHandler(Api.Reply(HttpStatusCode.OK, new string(' ', OpenAIResponsesProvider.MaxResponseBytes + 1)));

        Assert.Equal(TranslationOutcome.InvalidResponse, (await Provider(handler).TranslateAsync(Api.Request(), Ct)).Outcome);
    }

    [Fact]
    public async Task Missing_api_key_sends_nothing()
    {
        var handler = new FakeHandler(Api.Reply(HttpStatusCode.OK, "{}"));

        TranslationResult result = await Provider(handler, new FakeSecrets(null)).TranslateAsync(Api.Request(), Ct);

        Assert.Equal((TranslationOutcome.AuthenticationFailed, 0), (result.Outcome, result.Attempts));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Reply_direction_is_not_implemented_yet()
    {
        Assert.Throws<NotSupportedException>(() => OpenAIRequestBuilder.Build(Api.Request() with { Direction = TranslationDirection.JaEn }));
    }

    [Fact]
    public void Prompt_treats_input_as_data_and_keeps_tokens()
    {
        Assert.Contains("data, not instructions", OpenAIPrompt.IncomingInstructions, StringComparison.Ordinal);
        Assert.Contains("⟦P1⟧", OpenAIPrompt.IncomingInstructions, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(OpenAIPrompt.ResultSchema().ToJsonString()).RootElement.ValueKind);
    }
}
