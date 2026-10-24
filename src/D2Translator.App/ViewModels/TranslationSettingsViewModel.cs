using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D2Translator.App.Services;
using D2Translator.Application.Translation;
using D2Translator.Domain.Translation;

namespace D2Translator.App.ViewModels;

/// <summary>
/// UI06 設定のうち翻訳の部分（仕様書 11.4、12、18）。使用 AI とモデル ID は読取り専用で、
/// 任意のモデル名や他社 API を入力する欄は設けない。認証情報の値は再表示しない。
/// </summary>
public sealed partial class TranslationSettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly TranslationCoordinator _translator;
    private readonly ISecretStore _secrets;

    public TranslationSettingsViewModel(AppSettingsService settings, TranslationCoordinator translator, ISecretStore secrets)
    {
        _settings = settings;
        _translator = translator;
        _secrets = secrets;

        CloudEnabled = settings.Current.Translation.CloudEnabled;
        ConsentGiven = CloudEnabled;
        ContextEnabled = settings.Current.Context.Enabled;
        TestResult = string.Empty;
        KeyStatus = string.Empty;
        Message = settings.HasFixedValueViolation
            ? "設定ファイルの固定値（モデル、推論量等）が改変されています（DATA01）。ファイルを修正するまでクラウド翻訳を有効にできません。"
            : settings.IsWritable ? string.Empty : "設定ファイルが不正なため、変更はこの実行中だけ有効です（DATA01）。";
        RefreshKeyStatus();
    }

    public string FixedProfile =>
        $"OpenAI Responses API / {OpenAIFixedProfile.ModelDisplayName}（{OpenAIFixedProfile.ModelId}）{Environment.NewLine}" +
        $"推論量 {OpenAIFixedProfile.ReasoningEffort}、処理ティア {OpenAIFixedProfile.ServiceTier}、store=false、ツールなし{Environment.NewLine}" +
        $"送信先 {OpenAIFixedProfile.Endpoint}{Environment.NewLine}" +
        "接続できない場合も、別のモデルや別のサービスへは切り替えません。";

    public string DataNotice =>
        "クラウド翻訳を有効にすると、次の内容を OpenAI（api.openai.com）へ送信します。" + Environment.NewLine +
        "・他プレイヤーの発言の本文。話者名と、本文中で見つけた既知の名前は P1 等の記号に、URL とメールアドレスも記号に置き換えます。" +
        "本文に書かれたその他の個人情報までは取り除けません。" + Environment.NewLine +
        "・本文に関係する D2 辞書の項目。" + Environment.NewLine +
        "・文脈を有効にした場合だけ、同じ会話の直近の原文（最大 6 件、120 秒、1500 文字）。" + Environment.NewLine +
        "画面の画像、ローカルのファイル、API キー以外の設定は送りません。自分の発言と、参加通知などのシステム表示は翻訳しません。" + Environment.NewLine +
        "他プレイヤーの発言を第三者のサービスで処理することになります。store=false を指定しますが、OpenAI の不正利用監視のためのログは通常最大 30 日保持されることがあり、" +
        "「外部へ一切保存されない」わけではありません。" + Environment.NewLine +
        "API 利用料は登録した API キーの OpenAI 契約に課金されます（目安: 入力 1000・出力 200 トークンの要求 1 回で約 0.0002 米ドル。" +
        $"{OpenAIPricing.ReferenceDate:yyyy-MM-dd} 時点の公表単価による推計で、請求額の保証ではありません）。OpenAI 側の予算上限も併せて設定してください。";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    public partial bool CloudEnabled { get; set; }

    [ObservableProperty]
    public partial bool ConsentGiven { get; set; }

    [ObservableProperty]
    public partial bool ContextEnabled { get; set; }

    [ObservableProperty]
    public partial string KeyStatus { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteKeyCommand))]
    public partial bool HasKey { get; set; }

    [ObservableProperty]
    public partial string TestResult { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    public partial bool IsTesting { get; set; }

    public bool CanEnableCloud => !_settings.HasFixedValueViolation;

    partial void OnCloudEnabledChanged(bool value)
    {
        if (value && (!ConsentGiven || _settings.HasFixedValueViolation))
        {
            // 送信内容と送信先の確認なしに有効にしない（仕様書 2.3、12）
            CloudEnabled = false;
            Message = _settings.HasFixedValueViolation
                ? "設定ファイルの固定値が改変されているため有効にできません（DATA01）。"
                : "送信内容と送信先を確認し、「上記の内容を確認しました」を選んでから有効にしてください。";
            return;
        }

        Apply();
    }

    partial void OnConsentGivenChanged(bool value)
    {
        if (!value && CloudEnabled)
        {
            CloudEnabled = false;
        }
    }

    partial void OnContextEnabledChanged(bool value) => Apply();

    /// <summary>PasswordBox から受け取ったキーを DPAPI で保存する。値は保持しない。</summary>
    public async Task SaveKeyAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            Message = "API キーを入力してください。";
            return;
        }

        await _secrets.SaveAsync(OpenAIFixedProfile.SecretRef, key, CancellationToken.None);
        _translator.OnApiKeyChanged();
        RefreshKeyStatus();
        Message = "API キーを保存しました（Windows のユーザー単位で暗号化）。";
    }

    [RelayCommand(CanExecute = nameof(HasKey))]
    private async Task DeleteKeyAsync()
    {
        await _secrets.DeleteAsync(OpenAIFixedProfile.SecretRef, CancellationToken.None);
        _translator.OnApiKeyChanged();
        RefreshKeyStatus();
        Message = "API キーを削除しました。バックアップや OS の履歴からの完全な消去は保証しません。";
    }

    private bool CanTest() => CloudEnabled && !IsTesting;

    /// <summary>接続確認（仕様書 6.4）。短い検証文を 1 回送り、API 利用料が生じ得る。</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        TestResult = "送信中…";
        try
        {
            TranslationResult result = await _translator.TestConnectionAsync(CancellationToken.None);
            string usage = result.Usage is { } u
                ? $"入力 {u.InputTokens} / 出力 {u.OutputTokens} トークン、推計 {OpenAIPricing.EstimateUsd(u):0.000000} 米ドル"
                : "利用量は返りませんでした（料金未確定）";
            TestResult = result.IsSuccess
                ? $"成功: {result.TranslatedText}{Environment.NewLine}応答モデル {result.ResponseModel}、{result.Latency.TotalMilliseconds:0} ms、{usage}"
                : $"失敗: {result.Code} {result.Detail}{Environment.NewLine}{usage}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            TestResult = $"失敗: {ex.GetType().Name}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void Apply()
    {
        bool saved = _settings.Update(s => s with
        {
            Translation = s.Translation with { CloudEnabled = CloudEnabled },
            Context = s.Context with { Enabled = ContextEnabled },
        });
        _translator.Configure(TranslationOptions.From(_settings.Current));
        if (!saved && _settings.IsWritable)
        {
            Message = "設定を保存できませんでした。この実行中だけ有効です。";
        }
    }

    private void RefreshKeyStatus()
    {
        HasKey = _secrets.Exists(OpenAIFixedProfile.SecretRef);
        KeyStatus = HasKey ? "登録済み（値は表示しません）" : "未登録";
    }
}
