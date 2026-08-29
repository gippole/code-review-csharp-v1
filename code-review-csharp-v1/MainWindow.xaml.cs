using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;

namespace code_review_csharp_v1;

public partial class MainWindow : Window
{
    private CodeReviewService? _service;
    private CancellationTokenSource? _reviewCts;
    private List<ReviewItem> _currentFindings = new();
    private bool _isInitializing;

    private const string DefaultSampleCode = """
        using System;
        using System.IO;

        public class OrderProcessor
        {
            // レビューしたい C# コードをここに貼り付けるか、
            // 上部の「📂 開く」またはファイルをドラッグ＆ドロップしてください。
            public void ProcessOrder(string customerId, string rawPassword, decimal amount)
            {
                // SQL インジェクションのリスク例
                string sql = "SELECT * FROM Orders WHERE CustomerId = '" + customerId + "'";
                Console.WriteLine($"SQL: {sql}");

                // パスワードの平文ログ出力とリソース解放漏れ例
                var fs = new FileStream("audit.log", FileMode.Append);
                var writer = new StreamWriter(fs);
                writer.WriteLine($"Customer {customerId} with pass {rawPassword} charged {amount:C}");
                writer.Flush();
                // ※ fs, writer が Dispose されていません
            }
        }
        """;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    // ── Startup ──────────────────────────────────────────────────────────────
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeEditor();
        InitializeModelComboBox();

        SetStatus("Foundry Local を初期化中...");
        _service = new CodeReviewService();

        var progress = new Progress<string>(msg =>
        {
            SetStatus(msg);
            LoadingText.Text = msg;
        });

        _isInitializing = true;
        try
        {
            var selectedOption = ModelCombo.SelectedItem as ModelOption ?? CodeReviewService.SupportedModels[0];
            await _service.InitializeAsync(selectedOption.Alias, progress);
            UpdateDeviceBadge();
            SetReady(true);
        }
        catch (Exception ex)
        {
            SetStatus($"初期化エラー: {ex.Message}");
            SetReady(false);
            MessageBox.Show(
                $"Foundry Local の初期化に失敗しました。\n\n{ex.Message}\n\n" +
                "・インターネット接続を確認してください（初回はモデルダウンロードが必要です）\n" +
                "・モデルエイリアスがカタログに存在するか確認してください",
                "初期化エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void InitializeEditor()
    {
        // Setup C# syntax highlighting
        CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        CodeEditor.TextArea.SelectionCornerRadius = 2;
        CodeEditor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(90, 123, 97, 255));
        CodeEditor.TextArea.SelectionBorder = new Pen(new SolidColorBrush(Color.FromRgb(123, 97, 255)), 1);

        CodeEditor.Text = DefaultSampleCode;

        // Caret position info
        CodeEditor.TextArea.Caret.PositionChanged += (s, e) =>
        {
            int line = CodeEditor.TextArea.Caret.Line;
            int col = CodeEditor.TextArea.Caret.Column;
            int totalLines = CodeEditor.Document.LineCount;
            LineInfoText.Text = $"行 {line}, 列 {col}  ·  合計 {totalLines} 行";
        };
    }

    private void InitializeModelComboBox()
    {
        ModelCombo.ItemsSource = CodeReviewService.SupportedModels;
        ModelCombo.DisplayMemberPath = nameof(ModelOption.DisplayName);
        ModelCombo.SelectedIndex = 0;
    }

    private void UpdateDeviceBadge()
    {
        if (_service is null) return;
        string dev = _service.CurrentDeviceType;
        string ep = _service.CurrentExecutionProvider;
        DeviceLabel.Text = $"⚡ {dev} ({ep.Replace("ExecutionProvider", "")})";
    }

    private async void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _service is null || !_service.IsReady) return;

        if (ModelCombo.SelectedItem is ModelOption selected)
        {
            SetReady(false);
            SetStatus($"モデルを '{selected.DisplayName}' に切り替え中...");
            ShowState("loading");
            LoadingText.Text = $"モデル '{selected.DisplayName}' をロード中...";

            var progress = new Progress<string>(msg =>
            {
                SetStatus(msg);
                LoadingText.Text = msg;
            });

            try
            {
                await _service.SwitchModelAsync(selected.Alias, progress);
                UpdateDeviceBadge();
                SetReady(true);
                ShowState(_currentFindings.Count > 0 ? "results" : "empty");
            }
            catch (Exception ex)
            {
                SetStatus($"モデル切り替え失敗: {ex.Message}");
                SetReady(false);
                MessageBox.Show($"モデル切り替えに失敗しました。\n\n{ex.Message}", "エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _reviewCts?.Cancel();

        if (_service is not null)
        {
            var svc = _service;
            _service = null;
            _ = Task.Run(async () =>
            {
                try { await svc.DisposeAsync(); }
                catch { /* シャットダウン中の例外は無視 */ }
            });
        }
    }

    // ── UI helpers ────────────────────────────────────────────────────────────
    private void SetReady(bool ready)
    {
        ReviewBtn.IsEnabled = ready;
        ModelCombo.IsEnabled = ready;
        InitDot.Fill = ready
            ? new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
        InitLabel.Text = ready ? "準備完了" : "待機中/エラー";
        InitLabel.Foreground = ready
            ? (Brush)FindResource("SeverityLowBrush")
            : (Brush)FindResource("SeverityHighBrush");
        SetStatus(ready ? $"準備完了 ✓ [{_service?.CurrentModelAlias}] コードをレビューしてください" : "処理中 / 初期化待ち");
    }

    private void SetStatus(string msg) => StatusLabel.Text = msg;

    private void ShowState(string which)
    {
        EmptyState.Visibility = which == "empty" ? Visibility.Visible : Visibility.Collapsed;
        LoadingState.Visibility = which == "loading" ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroll.Visibility = which == "results" ? Visibility.Visible : Visibility.Collapsed;
        NoIssuesState.Visibility = which == "noissues" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── File & Quick Action Handlers ──────────────────────────────────────────
    private void OpenFileBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "C# ファイル (*.cs)|*.cs|すべてのファイル (*.*)|*.*",
            Title = "レビューする C# ファイルを選択"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                CodeEditor.Text = File.ReadAllText(dialog.FileName);
                SetStatus($"ファイル '{Path.GetFileName(dialog.FileName)}' を読み込みました");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ファイルの読み込みに失敗しました: {ex.Message}", "エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void PasteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
        {
            CodeEditor.Text = Clipboard.GetText();
            SetStatus("クリップボードからコードを貼り付けました");
        }
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        CodeEditor.Text = "";
        _currentFindings.Clear();
        ResultsList.ItemsSource = null;
        SummaryPanel.Visibility = Visibility.Collapsed;
        CopyMarkdownBtn.Visibility = Visibility.Collapsed;
        ShowState("empty");
        SetStatus("エディタと結果をクリアしました");
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                try
                {
                    CodeEditor.Text = File.ReadAllText(files[0]);
                    SetStatus($"ドラッグ＆ドロップで '{Path.GetFileName(files[0])}' を読み込みました");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"ファイル読み込みに失敗しました: {ex.Message}", "エラー",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    // ── Review & Cancellation ────────────────────────────────────────────────
    private async void ReviewBtn_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeEditor.Text.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show("レビューする C# コードを入力してください。", "入力エラー",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Toggle UI for in-progress review
        ReviewBtn.IsEnabled = false;
        ReviewBtn.Visibility = Visibility.Collapsed;
        CancelBtn.Visibility = Visibility.Visible;
        ModelCombo.IsEnabled = false;

        _reviewCts?.Cancel();
        _reviewCts = new CancellationTokenSource();

        ShowState("loading");
        LoadingText.Text = "AI がコードを解析・推論中...";
        SummaryPanel.Visibility = Visibility.Collapsed;
        CopyMarkdownBtn.Visibility = Visibility.Collapsed;
        SetStatus("レビュー実行中...");

        var progress = new Progress<string>(msg =>
        {
            SetStatus(msg);
            LoadingText.Text = msg;
        });

        try
        {
            var items = await _service!.ReviewAsync(code, progress, _reviewCts.Token);
            _currentFindings = items;

            if (items.Count == 0)
            {
                ShowState("noissues");
                SetStatus("レビュー完了 — 潜在的な問題は見つかりませんでした 🎉");
                SummaryPanel.Visibility = Visibility.Collapsed;
                CopyMarkdownBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Sort: High → Med → Low
                var sorted = items
                    .OrderBy(x => x.Severity)
                    .ToList();

                ResultsList.ItemsSource = sorted;
                ShowState("results");

                // Update summary badges
                int high = sorted.Count(x => x.Severity == Severity.High);
                int med = sorted.Count(x => x.Severity == Severity.Med);
                int low = sorted.Count(x => x.Severity == Severity.Low);

                HighCount.Text = $"🔴 High {high}";
                MedCount.Text = $"🟠 Med {med}";
                LowCount.Text = $"🟢 Low {low}";

                HighBadge.Visibility = high > 0 ? Visibility.Visible : Visibility.Collapsed;
                MedBadge.Visibility = med > 0 ? Visibility.Visible : Visibility.Collapsed;
                LowBadge.Visibility = low > 0 ? Visibility.Visible : Visibility.Collapsed;
                SummaryPanel.Visibility = Visibility.Visible;
                CopyMarkdownBtn.Visibility = Visibility.Visible;

                SetStatus($"レビュー完了 — {items.Count} 件の指摘 (High:{high} Med:{med} Low:{low})");
            }
        }
        catch (OperationCanceledException)
        {
            ShowState(_currentFindings.Count > 0 ? "results" : "empty");
            SetStatus("レビューを中断しました");
        }
        catch (Exception ex)
        {
            ShowState("empty");
            SetStatus($"レビューエラー: {ex.Message}");
            MessageBox.Show($"レビュー中にエラーが発生しました。\n\n{ex.Message}",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ReviewBtn.IsEnabled = true;
            ReviewBtn.Visibility = Visibility.Visible;
            CancelBtn.Visibility = Visibility.Collapsed;
            ModelCombo.IsEnabled = true;
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _reviewCts?.Cancel();
        SetStatus("推論の中止を要求しました...");
    }

    // ── Results Actions: Copy Markdown, Copy Fix, Jump ───────────────────────
    private void CopyMarkdownBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFindings.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine($"# AI コードレビュー結果 ({DateTime.Now:yyyy-MM-dd HH:mm})");
        sb.AppendLine($"**使用モデル**: {_service?.CurrentModelAlias ?? "Unknown"} ({_service?.CurrentDeviceType ?? "GPU"})");
        sb.AppendLine();

        int high = _currentFindings.Count(x => x.Severity == Severity.High);
        int med = _currentFindings.Count(x => x.Severity == Severity.Med);
        int low = _currentFindings.Count(x => x.Severity == Severity.Low);
        sb.AppendLine($"**指摘概要**: 合計 {_currentFindings.Count} 件 (🔴 High: {high} / 🟠 Med: {med} / 🟢 Low: {low})");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        int index = 1;
        foreach (var item in _currentFindings.OrderBy(x => x.Severity))
        {
            string icon = item.Severity switch { Severity.High => "🔴 [High]", Severity.Med => "🟠 [Med]", _ => "🟢 [Low]" };
            sb.AppendLine($"### {index++}. {icon} {item.Title}");
            sb.AppendLine($"- **カテゴリ**: {item.Category}");
            if (!string.IsNullOrWhiteSpace(item.LineHint))
                sb.AppendLine($"- **対象箇所**: `{item.LineHint}`");
            sb.AppendLine($"- **詳細**: {item.Description}");

            if (!string.IsNullOrWhiteSpace(item.SuggestedFix))
            {
                sb.AppendLine("- **推奨修正コード**:");
                sb.AppendLine("```csharp");
                sb.AppendLine(item.SuggestedFix);
                sb.AppendLine("```");
            }
            sb.AppendLine();
        }

        Clipboard.SetText(sb.ToString());
        SetStatus("📋 レビュー結果を Markdown 形式でクリップボードにコピーしました！");
    }

    private void CopyFixBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string fixCode && !string.IsNullOrWhiteSpace(fixCode))
        {
            Clipboard.SetText(fixCode);
            SetStatus("💡 修正コード案をクリップボードにコピーしました！");
        }
    }

    private void LineHint_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is ReviewItem item && !string.IsNullOrWhiteSpace(item.LineHint))
        {
            JumpToLineOrKeyword(item.LineHint);
        }
    }

    private void JumpToLineOrKeyword(string lineHint)
    {
        try
        {
            // Check if lineHint mentions line number, e.g. "Line 12" or "12行目" or "L12"
            var match = Regex.Match(lineHint, @"(?:Line|line|行|L)\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int lineNum))
            {
                if (lineNum > 0 && lineNum <= CodeEditor.Document.LineCount)
                {
                    var line = CodeEditor.Document.GetLineByNumber(lineNum);
                    CodeEditor.ScrollTo(lineNum, 1);
                    CodeEditor.Select(line.Offset, line.Length);
                    CodeEditor.Focus();
                    SetStatus($"エディタの {lineNum} 行目にジャンプしました");
                    return;
                }
            }

            // Otherwise search for token or keyword in editor
            var keyword = lineHint.Replace("'", "").Replace("\"", "").Trim();
            if (keyword.Length > 2)
            {
                int index = CodeEditor.Text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    var loc = CodeEditor.Document.GetLocation(index);
                    CodeEditor.ScrollTo(loc.Line, loc.Column);
                    CodeEditor.Select(index, keyword.Length);
                    CodeEditor.Focus();
                    SetStatus($"エディタ内の '{keyword}' ({loc.Line} 行目) にジャンプしました");
                    return;
                }
            }
        }
        catch { /* best-effort navigation */ }
    }
}

// ════════════════════════════════════════════════
// Value Converters
// ════════════════════════════════════════════════

/// <summary>Converts Severity → foreground Brush or background Brush (parameter="bg")</summary>
public class SeverityToColorConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isBg = parameter as string == "bg";
        return value is Severity sev
            ? sev switch
            {
                Severity.High => isBg ? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x55, 0x55))
                                      : new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55)),
                Severity.Med => isBg ? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xB8, 0x6C))
                                      : new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6C)),
                _ => isBg ? new SolidColorBrush(Color.FromArgb(0x40, 0x50, 0xFA, 0x7B))
                                      : new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B)),
            }
            : Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Converts Severity → Japanese label</summary>
public class SeverityToLabelConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Severity sev
            ? sev switch { Severity.High => "High", Severity.Med => "Med", _ => "Low" }
            : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Converts Severity → emoji icon</summary>
public class SeverityToIconConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Severity sev
            ? sev switch { Severity.High => "🔴", Severity.Med => "🟠", _ => "🟢" }
            : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Converts null/empty string → Collapsed, otherwise Visible</summary>
public class NullToCollapsedConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}