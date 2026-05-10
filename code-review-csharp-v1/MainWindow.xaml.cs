using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace code_review_csharp_v1;

public partial class MainWindow : Window
{
    private CodeReviewService? _service;
    private CancellationTokenSource? _reviewCts;
    private const string PlaceholderText = "// ここにレビューしたいコードを貼り付けてください";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    // ── Startup ──────────────────────────────────────────────────────────────
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SetStatus("Foundry Local を初期化中...");
        _service = new CodeReviewService();

        var progress = new Progress<string>(msg =>
        {
            SetStatus(msg);
            LoadingText.Text = msg;
        });

        try
        {
            await _service.InitializeAsync(progress);
            SetReady(true);
        }
        catch (Exception ex)
        {
            SetStatus($"初期化エラー: {ex.Message}");
            SetReady(false);
            MessageBox.Show(
                $"Foundry Local の初期化に失敗しました。\n\n{ex.Message}\n\n" +
                "・foundry が PATH に存在するか確認してください\n" +
                "・モデルエイリアスが正しいか `foundry model list` で確認してください",
                "初期化エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // CancellationToken のキャンセルは同期で OK
        _reviewCts?.Cancel();

        // ❌ async void + await はプロセスクラッシュの原因になるため Closing では await しない
        // ✅ DisposeAsync は Task.Run に逃がして fire-and-forget で完了させる
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
        InitDot.Fill = ready
            ? new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
        InitLabel.Text = ready ? "準備完了" : "エラー";
        InitLabel.Foreground = ready
            ? (Brush)FindResource("SeverityLowBrush")
            : (Brush)FindResource("SeverityHighBrush");
        SetStatus(ready ? "準備完了 ✓  コードを貼り付けてレビューを開始してください" : "初期化失敗");
    }

    private void SetStatus(string msg) => StatusLabel.Text = msg;

    private void ShowState(string which)
    {
        EmptyState.Visibility = which == "empty" ? Visibility.Visible : Visibility.Collapsed;
        LoadingState.Visibility = which == "loading" ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroll.Visibility = which == "results" ? Visibility.Visible : Visibility.Collapsed;
        NoIssuesState.Visibility = which == "noissues" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Button handlers ──────────────────────────────────────────────────────
    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        CodeInput.Text = PlaceholderText;
        CodeInput.Foreground = (Brush)FindResource("TextSecondaryBrush");
        ResultsList.ItemsSource = null;
        SummaryPanel.Visibility = Visibility.Collapsed;
        ShowState("empty");
        SetStatus("クリアしました");
    }

    private void CodeInput_GotFocus(object sender, RoutedEventArgs e)
    {
        if (CodeInput.Text == PlaceholderText)
        {
            CodeInput.Text = "";
            CodeInput.Foreground = (Brush)FindResource("TextPrimaryBrush");
        }
    }

    private async void ReviewBtn_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(code) || code == PlaceholderText.Trim())
        {
            MessageBox.Show("レビューするコードを入力してください。", "入力エラー",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Disable button during review
        ReviewBtn.IsEnabled = false;
        _reviewCts?.Cancel();
        _reviewCts = new CancellationTokenSource();

        ShowState("loading");
        LoadingText.Text = "AIがコードを解析中...";
        SummaryPanel.Visibility = Visibility.Collapsed;
        SetStatus("レビュー中...");

        try
        {
            var items = await _service!.ReviewAsync(code, _reviewCts.Token);

            if (items.Count == 0)
            {
                ShowState("noissues");
                SetStatus("レビュー完了 — 問題は見つかりませんでした 🎉");
                SummaryPanel.Visibility = Visibility.Collapsed;
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

                SetStatus($"レビュー完了 — {items.Count} 件の指摘 (High:{high} Med:{med} Low:{low})");
            }
        }
        catch (OperationCanceledException)
        {
            ShowState("empty");
            SetStatus("キャンセルされました");
        }
        catch (Exception ex)
        {
            ShowState("empty");
            SetStatus($"エラー: {ex.Message}");
            MessageBox.Show($"レビュー中にエラーが発生しました。\n\n{ex.Message}",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ReviewBtn.IsEnabled = true;
        }
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