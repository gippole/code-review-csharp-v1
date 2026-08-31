using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using OpenAI;
using System.ClientModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace code_review_csharp_v1;

// ──────────────────────────────────────────────
// Model Option definition
// ──────────────────────────────────────────────
public record ModelOption(string Alias, string DisplayName, string Description);

// ──────────────────────────────────────────────
// Model: one review finding
// ──────────────────────────────────────────────
public enum Severity { High, Med, Low }

public record ReviewItem
{
    public Severity Severity { get; init; }
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string? LineHint { get; init; }
    public string? SuggestedFix { get; init; }
}

// ──────────────────────────────────────────────
// Internal JSON contract for the LLM response
// ──────────────────────────────────────────────
internal class ReviewResponse
{
    [JsonPropertyName("findings")]
    public List<FindingDto> Findings { get; set; } = new();
}

internal class FindingDto
{
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Low";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("line_hint")]
    public string? LineHint { get; set; }

    [JsonPropertyName("suggested_fix")]
    public string? SuggestedFix { get; set; }
}

// ──────────────────────────────────────────────
// Service: Foundry Local + OpenAI SDK
// ──────────────────────────────────────────────
public class CodeReviewService : IAsyncDisposable
{
    public static readonly IReadOnlyList<ModelOption> SupportedModels = new List<ModelOption>
    {
        new("qwen3-4b", "Qwen 3 (4B)", "標準・高精度 推論モデル（推奨）"),
        new("qwen2.5-coder-1.5b", "Qwen 2.5 Coder (1.5B)", "超軽量・高速 コード特化モデル"),
        new("qwen2.5-coder-7b", "Qwen 2.5 Coder (7B)", "高精度 コード特化モデル"),
        new("phi-4-mini", "Phi-4 Mini (3.8B)", "Microsoft 高性能小型モデル"),
        new("qwen3.5-4b", "Qwen 3.5 (4B)", "最新世代 汎用推論モデル")
    };

    private ILoggerFactory? _loggerFactory;
    private FoundryLocalManager? _manager;
    private IModel? _model;
    private volatile bool _ready;

    public string CurrentModelAlias { get; private set; } = "qwen3-4b";
    public string CurrentDeviceType => _model?.Info?.Runtime?.DeviceType.ToString() ?? "GPU";
    public string CurrentExecutionProvider => _model?.Info?.Runtime?.ExecutionProvider.ToString() ?? "WebGpuExecutionProvider";
    public bool IsReady => _ready;

    // ── Initialization ──────────────────────────────
    public async Task InitializeAsync(string? modelAlias = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        CurrentModelAlias = modelAlias ?? "qwen3-4b";
        Report(progress, "Foundry Local SDK を初期化中...");

        _loggerFactory = LoggerFactory.Create(b =>
            b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
        var logger = _loggerFactory.CreateLogger<CodeReviewService>();

        var config = new Configuration
        {
            AppName = "CodeReviewApp",
            LogLevel = Microsoft.AI.Foundry.Local.LogLevel.Debug
        };

        await FoundryLocalManager.CreateAsync(config, logger);
        _manager = FoundryLocalManager.Instance;

        await LoadModelInternalAsync(CurrentModelAlias, progress, ct);
    }

    // ── Model Switching ─────────────────────────────
    public async Task SwitchModelAsync(string modelAlias, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_manager is null)
            throw new InvalidOperationException("Foundry Local が初期化されていません。");

        if (_model is not null && CurrentModelAlias == modelAlias && _ready)
            return;

        _ready = false;
        Report(progress, "現在のモデルをアンロード中...");
        if (_model is not null)
        {
            try { await _model.UnloadAsync(); } catch { }
            _model = null;
        }

        CurrentModelAlias = modelAlias;
        await LoadModelInternalAsync(modelAlias, progress, ct);
    }

    private async Task LoadModelInternalAsync(string modelAlias, IProgress<string>? progress, CancellationToken ct)
    {
        if (_manager is null) return;

        var catalog = await _manager.GetCatalogAsync();
        _model = await catalog.GetModelAsync(modelAlias)
                 ?? throw new InvalidOperationException(
                     $"モデル '{modelAlias}' がカタログに見つかりません。\n" +
                     "`foundry model list` で利用可能な alias を確認してください。");

        // Download if not cached
        if (!await _model.IsCachedAsync())
        {
            Report(progress, $"モデル '{modelAlias}' をダウンロード中 (初回のみ)...");
            await _model.DownloadAsync(p =>
                Report(progress, $"ダウンロード中 ({modelAlias}): {p:F0}%"), ct);
        }

        Report(progress, $"モデル '{modelAlias}' をロード中...");
        await _model.LoadAsync(ct);

        _ready = true;
        Report(progress, $"準備完了 ✓ [{modelAlias} - {CurrentDeviceType}]");
    }

    // ── Review ──────────────────────────────────────
    public async Task<List<ReviewItem>> ReviewAsync(string code, LanguageOption? language = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!_ready || _model is null || _manager is null)
            throw new InvalidOperationException("InitializeAsync を先に呼び出してください。");

        var lang = language ?? LanguageConfig.SupportedLanguages[0];
        var chatClient = await _model.GetChatClientAsync();

        var systemPrompt = BuildSystemPrompt(lang.DisplayName);
        var userPrompt = BuildUserPrompt(code, lang.DisplayName, lang.MarkdownCodeFence);

        var sb = new StringBuilder();

        ChatMessage[] messages =
        [
            new ChatMessage { Role = "system", Content = systemPrompt },
            new ChatMessage { Role = "user", Content = userPrompt }
        ];

        // Optimize generation settings for stable JSON and code review
        chatClient.Settings.MaxTokens = 8192;
        chatClient.Settings.Temperature = 0.1f;

        Report(progress, "AI がコードを解析・推論中...");
        var streamingResponse = chatClient.CompleteChatStreamingAsync(messages, ct);
        await foreach (var chunk in streamingResponse)
        {
            if (chunk.Choices.Count > 0)
                sb.Append(chunk.Choices[0].Message.Content ?? "");
        }

        return ParseResponse(sb.ToString());
    }

    // ── Cleanup ─────────────────────────────────────
    public async ValueTask DisposeAsync()
    {
        if (_manager is not null)
        {
            try
            {
                if (_model is not null) await _model.UnloadAsync();
            }
            catch { /* best-effort */ }
        }
        _loggerFactory?.Dispose();
    }

    private static string BuildSystemPrompt(string languageName) => $$"""
        /no_think
        ### Role
        You are an expert software engineer and code reviewer specializing in {{languageName}}.

        ### Task
        Review the provided {{languageName}} code and identify ONLY concrete issues: logic mistakes, security vulnerabilities, memory/resource leaks, and potential runtime exceptions.
        - Do NOT praise the code.
        - Do NOT suggest mere style preferences or subjective clean code opinions.
        - For each finding, provide a clear explanation and a concrete suggested fix snippet.

        ### Constraints
        - Focus strictly on logic bugs, runtime safety, resource management, and security.
        - If no issues are found, return `{"findings": []}`.

        Respond strictly in the following JSON format (no markdown fences, no conversational preamble):
        {
          "findings": [
            {
              "severity": "High" | "Med" | "Low",
              "category": "セキュリティ" | "うっかりミス" | "潜在バグ" | "パフォーマンス" | "その他",
              "title": "<short title in Japanese, max 50 chars>",
              "description": "<concrete explanation in Japanese, 1-3 sentences>",
              "line_hint": "<relevant line number, method name, or identifier — e.g. 'Line 12: userPassword' or 'ExecuteQuery'>",
              "suggested_fix": "<concrete replacement {{languageName}} code snippet for the problematic part, or empty if not applicable>"
            }
          ]
        }

        Severity guide:
        - High: exploitable vulnerability, data loss risk, crash/exception, race condition
        - Med: logic flaw, memory/handle leak, unhandled edge case
        - Low: easy-to-overlook mistake, minor optimization, redundant allocation

        If no issues are found, return: {"findings": []}
        """;

    private static string BuildUserPrompt(string code, string languageName, string codeFence) =>
        $"以下の{languageName}コードをレビューしてください:\n\n```{codeFence}\n{code}\n```";

    private static List<ReviewItem> ParseResponse(string raw)
    {
        var json = CleanAndExtractJson(raw);
        json = RepairUnescapedQuotes(json);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        try
        {
            var dto = JsonSerializer.Deserialize<ReviewResponse>(json, options);

            return dto?.Findings.Select(f => new ReviewItem
            {
                Severity = ParseSeverity(f.Severity),
                Category = f.Category,
                Title = f.Title,
                Description = f.Description,
                LineHint = f.LineHint,
                SuggestedFix = string.IsNullOrWhiteSpace(f.SuggestedFix) ? null : f.SuggestedFix.Trim()
            }).ToList() ?? new();
        }
        catch (Exception ex)
        {
            // Fallback: Attempt regex-based finding extraction if JSON deserializer fails
            var regexItems = TryExtractFindingsWithRegex(raw);
            if (regexItems.Count > 0) return regexItems;

            return new List<ReviewItem>
            {
                new ReviewItem
                {
                    Severity = Severity.Low,
                    Category = "その他",
                    Title = "レスポンスの解析に失敗しました",
                    Description = $"AIの応答をJSONとして解析できませんでした ({ex.Message})。モデルの応答: " +
                                  raw[..Math.Min(8192, raw.Length)],
                    LineHint = null,
                    SuggestedFix = null
                }
            };
        }
    }

    private static string CleanAndExtractJson(string raw)
    {
        var text = raw.Trim();

        // 1. Remove all complete <think>...</think> or <thought>...</thought> blocks (case-insensitive, multiline)
        text = Regex.Replace(text, @"<(think|thought)>[\s\S]*?<\/\1>", "", RegexOptions.IgnoreCase);

        // 2. If an unclosed <think> or <thought> tag exists before the first '{', strip it
        text = Regex.Replace(text, @"^<(think|thought)>[\s\S]*?(?=\{)", "", RegexOptions.IgnoreCase);

        // 3. Extract content inside markdown ```json ... ``` or ```csharp ... ``` or ``` ... ```
        var codeBlockMatch = Regex.Match(text, @"```(?:[a-zA-Z0-9_\+#\-]+)?\s*(\{[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);
        if (codeBlockMatch.Success)
        {
            text = codeBlockMatch.Groups[1].Value;
        }

        // 4. Find outermost JSON object from first '{' to last '}'
        int firstBrace = text.IndexOf('{');
        int lastBrace = text.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            text = text.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        return text.Trim();
    }

    private static List<ReviewItem> TryExtractFindingsWithRegex(string raw)
    {
        var list = new List<ReviewItem>();
        try
        {
            var pattern = @"\{\s*""severity""\s*:\s*""(?<sev>[^""]*)""[\s\S]*?""category""\s*:\s*""(?<cat>[^""]*)""[\s\S]*?""title""\s*:\s*""(?<title>[^""]*)""[\s\S]*?""description""\s*:\s*""(?<desc>[^""]*)""(?:[\s\S]*?""line_hint""\s*:\s*(?:""(?<hint>[^""]*)""|null))?(?:[\s\S]*?""suggested_fix""\s*:\s*(?:""(?<fix>[^""]*)""|null))?[\s\S]*?\}";
            var matches = Regex.Matches(raw, pattern, RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                if (m.Success)
                {
                    list.Add(new ReviewItem
                    {
                        Severity = ParseSeverity(m.Groups["sev"].Value),
                        Category = m.Groups["cat"].Value,
                        Title = m.Groups["title"].Value,
                        Description = m.Groups["desc"].Value,
                        LineHint = m.Groups["hint"].Success ? m.Groups["hint"].Value : null,
                        SuggestedFix = m.Groups["fix"].Success ? m.Groups["fix"].Value : null
                    });
                }
            }
        }
        catch { }
        return list;
    }

    private static Severity ParseSeverity(string s) => s.ToUpperInvariant() switch
    {
        "HIGH" => Severity.High,
        "MED" or "MEDIUM" => Severity.Med,
        _ => Severity.Low
    };

    // Repairs unescaped " inside JSON string values produced by LLMs.
    private static string RepairUnescapedQuotes(string json)
    {
        var sb = new StringBuilder(json.Length + 16);
        bool inString = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            // Honour existing escape sequences — copy both chars and skip
            if (c == '\\' && inString)
            {
                sb.Append(c);
                if (i + 1 < json.Length)
                    sb.Append(json[++i]);
                continue;
            }

            if (c != '"')
            {
                sb.Append(c);
                continue;
            }

            if (!inString)
            {
                inString = true;
                sb.Append(c);
                continue;
            }

            // Inside a string: decide if this " closes it or is an internal unescaped quote.
            int j = i + 1;
            while (j < json.Length && json[j] is ' ' or '\t' or '\r' or '\n') j++;
            bool isStructural = j >= json.Length || json[j] is ':' or ',' or '}' or ']';

            if (isStructural)
            {
                inString = false;
                sb.Append(c);
            }
            else
            {
                sb.Append('\\');
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static void Report(IProgress<string>? p, string msg) => p?.Report(msg);
}