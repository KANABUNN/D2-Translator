using System.Net.Http.Headers;
using D2Translator.Application.Translation;
using D2Translator.Domain.Translation;
using Microsoft.Extensions.Logging;

namespace D2Translator.Translation.OpenAI;

/// <summary>
/// OpenAI Responses API の GPT-6 Luna で翻訳する（仕様書 6.3、14.2）。
/// 認証キーは Authorization ヘッダーだけに入れ、本文、URL、ログに出さない。
/// 期限内で、429 と一時的な障害だけ1回再試行する（仕様書 15.4）。別モデルや別サービスへ切り替えない。
/// </summary>
public sealed class OpenAIResponsesProvider(
    HttpClient http,
    ISecretStore secrets,
    TimeProvider time,
    ILogger<OpenAIResponsesProvider> logger) : ITranslationProvider
{
    public const int MaxResponseBytes = 64 * 1024;
    private const int MaxAttempts = 2;

    private static readonly Uri Endpoint = new(OpenAIFixedProfile.Endpoint);

    /// <summary>リダイレクトを追わず、認証ヘッダーを他の送信先へ渡さない HTTP クライアント（仕様書 18.2）。</summary>
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseCookies = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        long started = time.GetTimestamp();

        using SecretLease? key = await secrets.OpenAsync(OpenAIFixedProfile.SecretRef, ct).ConfigureAwait(false);
        if (key is null)
        {
            return Finish(request, new AttemptOutcome(TranslationOutcome.AuthenticationFailed, "OpenAI の API キーが登録されていません。"), 0, started);
        }

        byte[] body = OpenAIRequestBuilder.Build(request);
        using var deadline = new CancellationTokenSource(request.Deadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        AttemptOutcome outcome = new(TranslationOutcome.Transient, "要求を送れませんでした。");
        int attempts = 0;
        while (attempts < MaxAttempts)
        {
            attempts++;
            outcome = await SendOnceAsync(body, key.Value, request, linked.Token, ct).ConfigureAwait(false);
            if (!outcome.Retryable || attempts >= MaxAttempts)
            {
                break;
            }

            TimeSpan remaining = request.Deadline - time.GetElapsedTime(started);
            TimeSpan wait = outcome.RetryAfter ?? TimeSpan.Zero;
            if (wait >= remaining)
            {
                // Retry-After を待つと期限に収まらないので自動再試行しない
                break;
            }

            try
            {
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                outcome = new AttemptOutcome(TranslationOutcome.Transient, "時間内に応答がありませんでした。");
                break;
            }
        }

        return Finish(request, outcome, attempts, started);
    }

    private async Task<AttemptOutcome> SendOnceAsync(byte[] body, string apiKey, TranslationRequest request, CancellationToken token, CancellationToken caller)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        // 送信直前の検証（仕様書 14.2）。送信先とモデルは固定値から変えない
        if (message.RequestUri is not { Scheme: "https", Host: "api.openai.com", AbsolutePath: "/v1/responses" })
        {
            throw new InvalidOperationException("送信先が固定のエンドポイントではありません。");
        }

        try
        {
            using HttpResponseMessage response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            byte[]? payload = await ReadLimitedAsync(response.Content, token).ConfigureAwait(false);
            if (payload is null)
            {
                return new AttemptOutcome(TranslationOutcome.InvalidResponse, "応答が上限 64KB を超えました。");
            }

            return OpenAIResponseParser.Classify(response.StatusCode, RetryAfter(response), payload, request);
        }
        catch (HttpRequestException)
        {
            return new AttemptOutcome(TranslationOutcome.Transient, "通信に失敗しました。", Retryable: true);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return new AttemptOutcome(TranslationOutcome.Transient, "時間内に応答がありませんでした。");
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
        {
            return null;
        }

        await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out IEnumerable<string>? values) &&
            double.TryParse(values.FirstOrDefault(), System.Globalization.CultureInfo.InvariantCulture, out double ms))
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta)
        {
            return delta;
        }

        return header?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null;
    }

    private TranslationResult Finish(TranslationRequest request, AttemptOutcome outcome, int attempts, long started)
    {
        TimeSpan latency = time.GetElapsedTime(started);
        // 本文、訳文、キーはログに書かない（仕様書 17.4）
        logger.LogInformation("Translation {Outcome} attempts={Attempts} latencyMs={Latency:0} model={Model}",
            outcome.Outcome, attempts, latency.TotalMilliseconds, outcome.ResponseModel);

        ParsedTranslation? parsed = outcome.Translation;
        return new TranslationResult
        {
            RequestId = request.RequestId,
            Outcome = outcome.Outcome,
            TranslatedText = parsed?.TranslatedText,
            AppliedTermIds = parsed?.AppliedTermIds ?? [],
            Ambiguities = parsed?.Ambiguities ?? [],
            Warnings = parsed?.Warnings ?? [],
            ResponseModel = outcome.ResponseModel,
            Usage = outcome.Usage,
            Latency = latency,
            Attempts = attempts,
            RetryAfter = outcome.Outcome == TranslationOutcome.RateLimited ? outcome.RetryAfter : null,
            QuotaExhausted = outcome.QuotaExhausted,
            Detail = outcome.Detail,
        };
    }
}
