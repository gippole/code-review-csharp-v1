# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```powershell
dotnet build
dotnet run --project code-review-csharp-v1/code-review-csharp-v1.csproj
dotnet build -c Release
```

There are no tests and no lint configuration. The solution uses the modern `.slnx` format.

## What This Is

A WPF desktop application that performs AI-powered C# code review entirely locally, using:
- **Microsoft Foundry Local** (`Microsoft.AI.Foundry.Local.WinML` v1.1.0) — manages model download, caching, and the inference lifecycle; provides `OpenAIChatClient` for chat completions
- **OpenAI SDK** (`OpenAI` v2.10.0) — present as a dependency but chat is handled via the Foundry Local `OpenAIChatClient` (which internally uses `Betalgo.Ranul.OpenAI` v9.1.0)

Target: `net9.0-windows10.0.26100.0` (WPF, Windows-only). The UI is in Japanese.

Current model: **`qwen3-4b`** (alias registered in the Foundry Local catalog). Run `foundry model list` to confirm available aliases. A commented-out alternative `qwen2.5-coder-1.5b` exists in the source.

## Architecture

**`CodeReviewService.cs`** — the only service class. Manages the full Foundry Local lifecycle:
1. Creates a `FoundryLocalManager` and downloads/caches the model
2. `ReviewAsync(code, ct)` — streams a JSON response from the model via `OpenAIChatClient`, parses it into `ReviewItem[]`
3. Implements `IAsyncDisposable`: unloads model then disposes the logger factory on disposal

The model is instructed via a hardcoded system prompt to return JSON `{"findings": [...]}`. `ParseResponse` strips Qwen3 `<think>...</think>` blocks, then strips markdown fences, then deserializes. If JSON parsing fails, a single fallback `ReviewItem` describing the parse error is returned instead of throwing.

**`MainWindow.xaml/.xaml.cs`** — all UI logic. Key patterns:
- `ShowState(string)` drives visibility for the four UI states: `Empty`, `Loading`, `Results`, `NoIssues`
- Results are sorted by severity (High → Med → Low) before display
- A `CancellationTokenSource` is created per review and cancelled on window close
- Shutdown calls `DisposeAsync()` as fire-and-forget (intentional, avoids `async void`)

**`App.xaml.cs`** — registers three global exception handlers (dispatcher, AppDomain, TaskScheduler) that write to `crash.log` in the output directory and show an error dialog.

**XAML value converters** (defined inside `MainWindow.xaml`): `SeverityToColorConverter`, `SeverityToLabelConverter`, `SeverityToIconConverter` — map the `Severity` enum to UI presentation.

## Key Design Decisions

- `Task.Run()` wraps `InitializeAsync` and `DisposeAsync` to keep the UI thread unblocked
- Logging minimum level is `Warning`; the Foundry Local manager receives a console logger
- `chatClient.Settings.MaxTokens = 4096` is set explicitly — local models default to a very small limit (often 256–512 tokens) that truncates JSON mid-response
- The system prompt begins with `/no_think` to disable Qwen3's thinking mode; `ParseResponse` also strips `<think>...</think>` as a fallback in case thinking output leaks through
- Chat is performed via `IModel.GetChatClientAsync()` → `OpenAIChatClient` (Foundry Local's own client), not the standard `OpenAI.Chat.ChatClient`. Use `chatClient.Settings.*` to tune generation parameters (Temperature, MaxTokens, TopP, FrequencyPenalty)
