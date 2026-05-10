using System.Configuration;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace code_review_csharp_v1;

public partial class App : Application
{
    // ログファイルのパス（exeと同じフォルダに出力）
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ① UIスレッド上の未処理例外（async void 含む）
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // ② バックグラウンドスレッド（Task.Run など）の未処理例外
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // ③ await された Task で観測されなかった例外
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    // ── ① UIスレッド ────────────────────────────────────────────
    private void OnDispatcherUnhandledException(object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        Log("DispatcherUnhandledException", e.Exception);

        var result = MessageBox.Show(
            $"予期しないエラーが発生しました。\n\n" +
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            $"ログ: {LogPath}\n\n" +
            "続行しますか？（「いいえ」を選ぶとアプリを終了します）",
            "エラー",
            MessageBoxButton.YesNo,
            MessageBoxImage.Error);

        // Handled = true にするとアプリを落とさず続行できる
        e.Handled = result == MessageBoxResult.Yes;
    }

    // ── ② バックグラウンドスレッド ─────────────────────────────
    private void OnDomainUnhandledException(object sender,
        UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log("AppDomain.UnhandledException", ex);

        // IsTerminating = true のときはランタイムがプロセスを落とす直前
        // UIスレッドでないため Dispatcher 経由でダイアログを出す
        Dispatcher.Invoke(() =>
        {
            MessageBox.Show(
                $"回復不能なエラーが発生しました。アプリを終了します。\n\n" +
                $"{ex?.GetType().Name}: {ex?.Message}\n\n" +
                $"ログ: {LogPath}",
                "致命的エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        });
    }

    // ── ③ 未 observe の Task 例外 ──────────────────────────────
    private void OnUnobservedTaskException(object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        Log("UnobservedTaskException", e.Exception);

        // SetObserved() でランタイムのプロセス終了を抑止する
        e.SetObserved();

        Dispatcher.InvokeAsync(() =>
        {
            MessageBox.Show(
                $"バックグラウンド処理でエラーが発生しました。\n\n" +
                $"{e.Exception.InnerException?.Message ?? e.Exception.Message}\n\n" +
                $"ログ: {LogPath}",
                "バックグラウンドエラー",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        });
    }

    // ── ログ書き込み ────────────────────────────────────────────
    private static void Log(string source, Exception? ex)
    {
        try
        {
            var entry = $"""
                [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]
                {ex?.GetType().FullName}: {ex?.Message}
                {ex?.StackTrace}
                --- Inner ---
                {ex?.InnerException}
                ============================================================

                """;
            File.AppendAllText(LogPath, entry);
        }
        catch { /* ログ書き込み自体が失敗しても握りつぶす */ }
    }
}