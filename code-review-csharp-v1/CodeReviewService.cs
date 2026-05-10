using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using OpenAI;
using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace code_review_csharp_v1;

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
}

// ──────────────────────────────────────────────
// Service: Foundry Local + OpenAI SDK
// ──────────────────────────────────────────────
public class CodeReviewService : IAsyncDisposable
{
    // Phi-4-mini alias registered in Foundry Local catalog
    // Run `foundry model list` to confirm the alias on your machine.
    //private const string ModelAlias = "qwen2.5-coder-1.5b";
    private const string ModelAlias = "qwen3-4b";

    private ILoggerFactory? _loggerFactory;
    private FoundryLocalManager? _manager;
    private IModel? _model;
    private volatile bool _ready;

    public event Action<string>? StatusChanged;

    // ── Initialization ──────────────────────────────
    public async Task InitializeAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Report(progress, "Foundry Local SDK を初期化中...");

        _loggerFactory = LoggerFactory.Create(b =>
            b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
        var logger = _loggerFactory.CreateLogger<CodeReviewService>();

        var config = new Configuration
        {
            AppName = "CodeReviewApp",
            LogLevel = Microsoft.AI.Foundry.Local.LogLevel.Debug,
        };

        await FoundryLocalManager.CreateAsync(config, logger);
        _manager = FoundryLocalManager.Instance;

        var catalog = await _manager.GetCatalogAsync();
        _model = await catalog.GetModelAsync(ModelAlias)
                 ?? throw new InvalidOperationException(
                     $"モデル '{ModelAlias}' がカタログに見つかりません。\n" +
                     "`foundry model list` で利用可能な alias を確認してください。");

        // Download if not cached
        if (!await _model.IsCachedAsync())
        {
            Report(progress, "モデルをダウンロード中 (初回のみ)...");
            await _model.DownloadAsync(p =>
                Report(progress, $"ダウンロード中: {p:F0}%"), ct);
        }

        Report(progress, "モデルをロード中...");
        await _model.LoadAsync(ct);

        _ready = true;
        Report(progress, "準備完了 ✓");
    }

    // ── Review ──────────────────────────────────────
    public async Task<List<ReviewItem>> ReviewAsync(string code, CancellationToken ct = default)
    {
        if (!_ready || _model is null || _manager is null)
            throw new InvalidOperationException("InitializeAsync を先に呼び出してください。");

        //var client = BuildOpenAiClient();
        var chatClient = await _model.GetChatClientAsync();

        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(code);

        var sb = new StringBuilder();

        // Create a chat message
        List<ChatMessage> messages = new()
        {
            new ChatMessage { Role = "system", Content = systemPrompt },
            new ChatMessage { Role = "user", Content = userPrompt }
        };

        var streamingResponse = chatClient.CompleteChatStreamingAsync(messages, null, ct);
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


    private static string BuildSystemPrompt() => """
        You are a senior software engineer performing a focused code review.
        Your task: identify ONLY concrete issues — careless mistakes and security concerns.
        Do NOT praise the code. Do NOT suggest style preferences.

        Respond strictly in the following JSON format (no markdown, no preamble):
        {
          "findings": [
            {
              "severity": "High" | "Med" | "Low",
              "category": "セキュリティ" | "うっかりミス" | "潜在バグ" | "その他",
              "title": "<short title in Japanese, max 50 chars>",
              "description": "<concrete explanation in Japanese, 1-3 sentences>",
              "line_hint": "<optional: relevant code fragment or line keyword>"
            }
          ]
        }

        Severity guide:
        - High: exploitable vulnerability, data loss risk, crash
        - Med: logic error, improper resource handling, non-obvious bug
        - Low: easy-to-overlook mistake, minor risk

        If no issues are found, return: {"findings": []}
        """;

    private static string BuildUserPrompt(string code) =>
        $"以下のコードをレビューしてください:\n\n```\n{code}\n```";

    private static List<ReviewItem> ParseResponse(string raw)
    {
        // Strip possible markdown fences
        var json = raw.Trim();
        if (json.StartsWith("```")) json = StripFences(json);

        try
        {
            var dto = JsonSerializer.Deserialize<ReviewResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return dto?.Findings.Select(f => new ReviewItem
            {
                Severity = ParseSeverity(f.Severity),
                Category = f.Category,
                Title = f.Title,
                Description = f.Description,
                LineHint = f.LineHint
            }).ToList() ?? new();
        }
        catch
        {
            // Fallback: return a single item describing parse failure
            return new List<ReviewItem>
            {
                new ReviewItem
                {
                    Severity = Severity.Low,
                    Category = "その他",
                    Title = "レスポンスの解析に失敗しました",
                    Description = "AIの応答をJSONとして解析できませんでした。モデルの応答: " +
                                  raw[..Math.Min(200, raw.Length)],
                }
            };
        }
    }

    private static Severity ParseSeverity(string s) => s.ToUpperInvariant() switch
    {
        "HIGH" => Severity.High,
        "MED" or "MEDIUM" => Severity.Med,
        _ => Severity.Low
    };

    private static string StripFences(string s)
    {
        var lines = s.Split('\n').ToList();
        if (lines.Count > 0 && lines[0].StartsWith("```")) lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].TrimEnd() == "```") lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    private static void Report(IProgress<string>? p, string msg) => p?.Report(msg);
}