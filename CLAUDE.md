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

A WPF desktop application that performs AI-powered multi-language code review entirely locally, using:
- **Microsoft Foundry Local** (`Microsoft.AI.Foundry.Local.WinML` v1.1.0) — manages model download, caching, and inference lifecycle
- **AvalonEdit** (`AvalonEdit` v6.3.1) — rich multi-language code editor with syntax highlighting, line numbers, and dark styling
- **CommunityToolkit.Mvvm** (`CommunityToolkit.Mvvm` v8.4.2) — modern MVVM infrastructure
- **OpenAI SDK** (`OpenAI` v2.10.0) — present as a dependency; chat completions run via Foundry Local `OpenAIChatClient`

Target: `net9.0-windows10.0.26100.0` (WPF, Windows-only). The UI is in Japanese.

Supported languages:
- `C#` (`.cs`)
- `C` (`.c`, `.h`)
- `C++` (`.cpp`, `.cc`, `.cxx`, `.hpp`, `.hxx`, `.h`)
- `Python` (`.py`, `.pyw`)
- `Dart` (`.dart`)

Supported models in catalog:
- `qwen3-4b` (Default: High accuracy reasoning model)
- `qwen2.5-coder-1.5b` (Fast, lightweight coding model)
- `qwen2.5-coder-7b` (High accuracy coding model)
- `phi-4-mini` (Microsoft 3.8B compact model)
- `qwen3.5-4b` (Latest general reasoning model)

## Architecture

**`LanguageConfig.cs`** — defines supported programming languages (`LanguageOption`), sample code presets, file filters, and XSHD syntax highlighting definitions (Python, Dart).

**`CodeReviewService.cs`** — manages the Foundry Local lifecycle and model execution:
1. Manages `FoundryLocalManager` instance and model switching (`SwitchModelAsync`)
2. `ReviewAsync(code, language, progress, ct)` — streams response via `OpenAIChatClient`, sets `Temperature = 0.1` and `MaxTokens = 8192` with language-specialized prompts
3. Parses JSON into `ReviewItem[]` including `Severity`, `Category`, `Title`, `Description`, `LineHint`, and `SuggestedFix`
4. Implements `IAsyncDisposable` for clean unloading and logger disposal

**`MainWindow.xaml/.xaml.cs`** — UI and interaction:
- AvalonEdit `TextEditor` with multi-language syntax highlighting, Drag & Drop (auto-detects language), Open, Paste, and Clear actions
- Dynamic Language ComboBox (`C#`, `C`, `C++`, `Python`, `Dart`), Model ComboBox, and GPU/Device badge
- Review progress state with **Cancel (⏹ 中止)** support
- Review results with High/Med/Low counts, clickable `LineHint` (jumps to line in editor), `SuggestedFix` preview and copy button
- **📋 Markdown Copy** button to export entire review findings to clipboard with appropriate language code fences

**`App.xaml.cs`** — registers three global exception handlers (dispatcher, AppDomain, TaskScheduler) writing to `crash.log`.

**XAML value converters**: `SeverityToColorConverter`, `SeverityToLabelConverter`, `SeverityToIconConverter`, `NullToCollapsedConverter`.
