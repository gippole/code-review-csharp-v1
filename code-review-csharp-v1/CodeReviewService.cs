using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using OpenAI;
using System.ClientModel;
using System.IO;
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
    //private const string ModelAlias = "qwen3.5-4b";

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
#if false // モデルやログのディレクトリを変えたいとき
            ModelCacheDir = Path.Combine(Environment.CurrentDirectory, "cache\\", "models\\"),
            LogsDir = Path.Combine(Environment.CurrentDirectory, "logs\\"),
            AppDataDir = Path.Combine(Environment.CurrentDirectory, "data\\")
#endif
        };

        await FoundryLocalManager.CreateAsync(config, logger);
        _manager = FoundryLocalManager.Instance;

        var catalog = await _manager.GetCatalogAsync();
#if false
        var models = await catalog.ListModelsAsync();

        foreach (var model in models)
        {
            Console.WriteLine(model.Id);
        }
#endif
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

        ChatMessage[] messages =
        [
            new ChatMessage { Role = "system", Content = systemPrompt },
            new ChatMessage { Role = "user", Content = userPrompt }
        ];

        chatClient.Settings.MaxTokens = 8192;
        var streamingResponse = chatClient.CompleteChatStreamingAsync(messages, ct);
        await foreach (var chunk in streamingResponse)
        {
            if (chunk.Choices.Count > 0)
                sb.Append(chunk.Choices[0].Message.Content ?? "");
        }

        return ParseResponse(sb.ToString());

        //var response = chatClient.CompleteChatAsync(messages, ct);
        //return ParseResponse(response.Result.Choices[0].Message.Content ?? "");
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
        /no_think
        ### Role
        You are a senior software engineer performing a focused code review.

        ### Task
        Identify ONLY concrete issues: careless logic mistakes and security concerns.
        - Do NOT praise the code.
        - Do NOT suggest style preferences or "clean code" improvements.

        ### Constraints
        - Assume the code builds and compiles successfully.
        - Function and variable names are assumed to be correct.
        - Focus strictly on logic, potential runtime errors, and security vulnerabilities.
        - If no issues are found, reply only with No issues identified.

        Respond strictly in the following JSON format (no markdown, no preamble):
        {
          "findings": [
            {
              "severity": "High" | "Med" | "Low",
              "category": "セキュリティ" | "うっかりミス" | "潜在バグ" | "その他",
              "title": "<short title in Japanese, max 50 chars>",
              "description": "<concrete explanation in Japanese, 1-3 sentences>",
              "line_hint": "<optional: relevant identifier or short keyword — use single quotes for any string literals, never double quotes>"
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
        // Strip Qwen3 thinking block if present
        var json = raw.Trim();
        var thinkEnd = json.IndexOf("</think>", StringComparison.Ordinal);
        if (thinkEnd >= 0) json = json[(thinkEnd + 8)..].TrimStart();

        // Strip possible markdown fences
        if (json.StartsWith("```")) json = StripFences(json);

        json = RepairUnescapedQuotes(json);

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
                                  raw[..Math.Min(8192, raw.Length)],
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

    // Repairs unescaped " inside JSON string values produced by LLMs.
    // Heuristic: a " is a closing quote only if followed (ignoring whitespace) by : , } ] or end-of-input.
    // Everything else is an unescaped internal quote and gets escaped as \".
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
            // A closing " is followed (past whitespace) by a JSON structural character.
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

    private static string StripFences(string s)
    {
        var lines = s.Split('\n').ToList();
        if (lines.Count > 0 && lines[0].StartsWith("```")) lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].TrimEnd() == "```") lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    private static void Report(IProgress<string>? p, string msg) => p?.Report(msg);
}