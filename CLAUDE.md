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
- **Microsoft Foundry Local** (`Microsoft.AI.Foundry.Local.WinML` v1.1.0) — manages a local ONNX inference web service
- **Phi-4-mini** model — downloaded and cached on first run, served at `http://127.0.0.1:55599`
- **OpenAI SDK** (`OpenAI` v2.10.0) — communicates with the local service via the OpenAI-compatible API

Target: `net9.0-windows10.0.26100.0` (WPF, Windows-only). The UI is in Japanese.

## Architecture

**`CodeReviewService.cs`** — the only service class. Manages the full Foundry Local lifecycle:
1. Creates a `FoundryLocalManager` and downloads/caches the `phi-4-mini` model alias
2. Starts the local web service; creates an `OpenAIClient` pointing at the local endpoint
3. `ReviewAsync(code, ct)` — streams a JSON response from the model, parses it into `ReviewItem[]`
4. Implements `IAsyncDisposable`: unloads model then stops the web service on disposal

The model is instructed via a hardcoded system prompt to return JSON `{"findings": [...]}`. If JSON parsing fails, a single fallback `ReviewItem` describing the parse error is returned instead of throwing.

**`MainWindow.xaml/.xaml.cs`** — all UI logic. Key patterns:
- `ShowState(string)` drives visibility for the four UI states: `Empty`, `Loading`, `Results`, `NoIssues`
- Results are sorted by severity (High → Med → Low) before display
- A `CancellationTokenSource` is created per review and cancelled on window close
- Shutdown calls `DisposeAsync()` as fire-and-forget (intentional, avoids `async void`)

**`App.xaml.cs`** — registers three global exception handlers (dispatcher, AppDomain, TaskScheduler) that write to `crash.log` in the output directory and show an error dialog.

**XAML value converters** (defined inside `MainWindow.xaml`): `SeverityToColorConverter`, `SeverityToLabelConverter`, `SeverityToIconConverter` — map the `Severity` enum to UI presentation.

## Key Design Decisions

- All configuration is hardcoded: model alias `"phi-4-mini"`, service URL `http://127.0.0.1:55599`
- `Task.Run()` wraps `InitializeAsync` and `DisposeAsync` to keep the UI thread unblocked
- Logging minimum level is `Warning`; the Foundry Local manager receives a console logger
- The `ReviewItem` record and `Severity` enum are the only public data types; internal JSON DTOs (`ReviewResponse`, `FindingDto`) are private records in `CodeReviewService.cs`
