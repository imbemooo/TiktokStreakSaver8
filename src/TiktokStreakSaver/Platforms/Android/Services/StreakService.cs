using Android.App;
using Android.Content;
using Android.OS;
using Android.Webkit;
using Android.Runtime;
using AndroidX.Core.App;
using Android.Content.PM;
using Java.Interop;
using TiktokStreakSaver.Models;
using TiktokStreakSaver.Services;
using TiktokStreakSaver.Platforms.Android;
using WebView = Android.Webkit.WebView;

namespace TiktokStreakSaver.Platforms.Android.Services;

[Service(Name = AppConstants.PackageName + ".Services.StreakService", ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class StreakService : Service
{
    private const string ChannelId = "streak_service_channel";
    private const string ChannelName = "Streak Service";
    /// <summary>Ongoing foreground + completion / offline alerts that should be visible (not Low importance).</summary>
    private const string StatusChannelId = "streak_status_channel";
    private const string StatusChannelName = "Streak status";
    private const int NotificationId = 1001;

    private WebView? _webView;
    private Handler? _mainHandler;
    private SettingsService? _settingsService;
    private PowerManager.WakeLock? _wakeLock;
    private string _baseScript = string.Empty;

    // ── Service lifecycle flags ──
    private bool _isCancelRequested = false;
    private bool _automationStarted = false;

    // ── Run-level mutex: prevents concurrent automation sessions ──
    private static volatile bool _isRunning = false;
    private static readonly object _runLock = new();

    /// <summary>
    /// True while an automation session is active. Checked by StreakScheduler.RunNow
    /// and OnStartCommand to prevent overlapping runs.
    /// </summary>
    public static bool IsRunning => _isRunning;

    private static readonly object LogLock = new();
    private static readonly List<string> _logs = new();

    public static List<string> GetLogs()
    {
        lock (LogLock)
            return new List<string>(_logs);
    }

    public static void ClearLogs()
    {
        lock (LogLock)
            _logs.Clear();
    }

    private static void AppLog(string phase, string username, string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] [{phase}] [{username}] {message}";
        lock (LogLock)
            _logs.Add(entry);
        System.Diagnostics.Debug.WriteLine(entry);
    }

    public override void OnCreate()
    {
        base.OnCreate();

        // Create notification channel FIRST before anything else
        CreateNotificationChannel();
        CreateStatusNotificationChannel();

        _mainHandler = new Handler(Looper.MainLooper!);
        _settingsService = new SettingsService();
        AcquireWakeLock();

        // Start foreground IMMEDIATELY in OnCreate to avoid ANR
        StartForegroundServiceImmediate();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "STOP_SERVICE")
        {
            _isCancelRequested = true;
            AppLog("SYSTEM", "-", "Service stop requested by user");
            CompleteService(false, "Run stopped by user.");
            return StartCommandResult.NotSticky;
        }

        // Ensure we're in foreground mode (in case OnCreate didn't complete it)
        StartForegroundServiceImmediate();

        lock (_runLock)
        {
            if (_isRunning)
            {
                AppLog("SYSTEM", "-", "OnStartCommand ignored — automation already running");
                return StartCommandResult.NotSticky;
            }
            _isRunning = true;
        }

        _isCancelRequested = false;

        // Start the WebView automation on main thread
        _mainHandler?.Post(StartWebViewAutomation);

        return StartCommandResult.Sticky;
    }

    private void StartForegroundServiceImmediate()
    {
        try
        {
            var notification = CreateNotification("Preparing to send streaks...");

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartForeground error: {ex.Message}");
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        lock (_runLock)
        {
            _isRunning = false;
        }
        ReleaseWakeLock();
        CleanupWebView();
        base.OnDestroy();
    }

    private void AcquireWakeLock()
    {
        var powerManager = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = powerManager?.NewWakeLock(WakeLockFlags.Partial, "TiktokStreakSaver::StreakWakeLock");
        _wakeLock?.Acquire(30L * 60 * 1000);
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            if (notificationManager == null) return;

            var existingChannel = notificationManager.GetNotificationChannel(ChannelId);
            if (existingChannel != null) return;

            var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
            {
                Description = "Notification channel for streak service"
            };
            channel.SetShowBadge(false);

            notificationManager?.CreateNotificationChannel(channel);
        }
    }

    private void CreateStatusNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            if (notificationManager == null) return;

            if (notificationManager.GetNotificationChannel(StatusChannelId) != null) return;

            var channel = new NotificationChannel(StatusChannelId, StatusChannelName, NotificationImportance.Default)
            {
                Description = "Run results and connection issues"
            };
            notificationManager.CreateNotificationChannel(channel);
        }
    }

    private Notification CreateNotification(string message)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("TikTok Streak Saver")
            .SetContentText(message)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(message))
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)
            .SetCategory(NotificationCompat.CategoryService)
            .SetPriority(NotificationCompat.PriorityLow)
            .SetProgress(0, 0, true);

        return builder.Build()!;
    }

    private void UpdateNotification(string message)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("TikTok Streak Saver")
            .SetContentText(message)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(message))
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)
            .SetCategory(NotificationCompat.CategoryService)
            .SetPriority(NotificationCompat.PriorityLow)
            .SetProgress(0, 0, true);

        var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
        notificationManager?.Notify(NotificationId, builder.Build()!);
    }

    private async void StartWebViewAutomation()
    {
        try
        {
            _automationStarted = false;
            ClearLogs();

            AppLog("SYSTEM", "-", "Starting top-down chat automation");

            if (!NetworkConnectivity.HasWifiOrCellularInternet(this))
            {
                CompleteSkippedNoNetwork();
                return;
            }

            UpdateNotification("Preparing automation...");

            using var resourceStream = await FileSystem.OpenAppPackageFileAsync("tiktok_automation.js");
            using var reader = new StreamReader(resourceStream);
            this._baseScript = await reader.ReadToEndAsync();
            this._baseScript = string.Join("\n", this._baseScript.Split('\n').Where(line => !line.TrimStart().StartsWith("//")));
            this._baseScript = System.Text.RegularExpressions.Regex.Replace(this._baseScript, @"\s+", " ").Trim();

            var sessionService = new SessionService();
            var loginUa = sessionService.GetLoginUserAgent()
                ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

            _mainHandler!.Post(() =>
            {
                try
                {
                    _webView = new WebView(this);
                    _webView.ResumeTimers();
                    _webView.Settings.JavaScriptEnabled = true;
                    _webView.Settings.DomStorageEnabled = true;
                    _webView.Settings.DatabaseEnabled = true;
                    _webView.Settings.CacheMode = CacheModes.Normal;
                    _webView.Settings.UserAgentString = loginUa;

                    _webView.Settings.SetSupportZoom(false);
                    _webView.Settings.BuiltInZoomControls = false;
                    _webView.Settings.UseWideViewPort = false;
                    _webView.Settings.LoadWithOverviewMode = false;
                    _webView.SetInitialScale(100);

                    var dm = Resources?.DisplayMetrics;
                    float density = dm?.Density ?? 2.0f;
                    int widthPx = (int)(1920 * density);
                    int heightPx = (int)(1080 * density);
                    _webView.Layout(0, 0, widthPx, heightPx);

                    var cookieManager = CookieManager.Instance;
                    cookieManager?.SetAcceptCookie(true);
                    cookieManager?.SetAcceptThirdPartyCookies(_webView, true);
                    cookieManager?.Flush();

                    _webView.SetWebViewClient(new StreakWebViewClient(this));
                    _webView.AddJavascriptInterface(new StreakJsInterface(this), "StreakApp");
                    _webView.LoadUrl("https://www.tiktok.com/messages?lang=en");

                    _mainHandler.PostDelayed(() =>
                    {
                        if (!(_webView?.Url ?? "").Contains("tiktok.com/messages"))
                        {
                            _webView?.LoadUrl("https://www.tiktok.com/messages?lang=en");
                            _mainHandler.PostDelayed(() =>
                            {
                                if (!(_webView?.Url ?? "").Contains("tiktok.com/messages"))
                                {
                                    CompleteService(false, "Could not navigate to tiktok.com/messages");
                                }
                            }, 5000);
                        }
                    }, 5000);
                }
                catch (Exception ex)
                {
                    CompleteService(false, $"Error initializing WebView on MainThread: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            CompleteService(false, $"Error starting WebView: {ex.Message}");
        }
    }

    private void CleanupWebView()
    {
        _mainHandler?.Post(() =>
        {
            _webView?.StopLoading();
            _webView?.Destroy();
            _webView = null;
        });
    }

    internal void OnPageLoaded(string url)
    {
        if (url.Contains("tiktok.com/messages"))
        {
            if (_automationStarted) return;
            _automationStarted = true;

            UpdateNotification("Running top-down streak automation...");
            AppLog("NAVIGATION", "-", "Messages page ready - executing script");
            
            _mainHandler?.PostDelayed(ExecuteAutomationScript, 3000);
        }
        else if (url.Contains("login"))
        {
            AppLog("NAVIGATION", "-", "TikTok login required");
            CompleteService(false, "TikTok login required. Please login via the app first.");
        }
    }

    private void ExecuteAutomationScript()
    {
        if (_isCancelRequested) return;

        var message = _settingsService?.GetMessageText() ?? SettingsService.DefaultMessage;
        var maxChats = _settingsService?.GetMaxChats() ?? 10;

        var escapedMessage = message.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"").Replace("\n", "\\n");

        var script = _baseScript.Replace("[Message]", escapedMessage);
        script = script.Replace("[MaxChats]", maxChats.ToString());

        AppLog("SYSTEM", "-", $"Injecting automation script for top {maxChats} chats");
        _webView?.EvaluateJavascript(script, null);
    }

    internal void OnAutomationFinished(bool success, string summary)
    {
        if (_isCancelRequested) return;
        CompleteService(success, summary);
    }

    private PendingIntent CreateMainActivityPendingIntent()
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(this, 1, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    private void CompleteSkippedNoNetwork()
    {
        try
        {
            if (_settingsService != null)
            {
                _settingsService.AddRunResult(new StreakRunResult { Success = false, ErrorMessage = "Skipped: no Wi‑Fi or mobile data" });
            }

            var attempt = StreakScheduler.TryScheduleRetryOrGiveUp(this, SettingsService.FailureReasonNoNetwork);
            var max = SettingsService.MaxRetriesPerDay;

            string title = "TikTok Streak Saver — offline";
            string body = $"No Wi‑Fi or mobile data. Streak run skipped; retrying in 1 hour (attempt {attempt}/{max}).";
            UpdateNotification($"No Wi‑Fi or mobile data — retry in 1 hour ({attempt}/{max})");

            var finalNotification = new NotificationCompat.Builder(this, StatusChannelId)
                .SetContentTitle(title)
                .SetContentText(body)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetContentIntent(CreateMainActivityPendingIntent())
                .SetAutoCancel(true)
                .SetPriority(NotificationCompat.PriorityDefault)
                .Build()!;

            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            notificationManager?.Notify(NotificationId + 1, finalNotification);
        }
        finally
        {
            lock (_runLock)
            {
                _isRunning = false;
            }

            CleanupWebView();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    private void CompleteService(bool success, string message)
    {
        try
        {
            if (_settingsService != null)
            {
                _settingsService.AddRunResult(new StreakRunResult { Success = success, ErrorMessage = success ? null : message });
                _settingsService.SetLastRunTime(DateTime.Now);
            }

            string finalText = success ? $"Done: {message}" : $"Stopped: {message}";

            var finalNotification = new NotificationCompat.Builder(this, StatusChannelId)
                .SetContentTitle("TikTok Streak Saver")
                .SetContentText(finalText)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(finalText))
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetContentIntent(CreateMainActivityPendingIntent())
                .SetAutoCancel(true)
                .SetPriority(NotificationCompat.PriorityDefault)
                .Build()!;

            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            notificationManager?.Notify(NotificationId + 1, finalNotification);

            if (_settingsService?.IsScheduled() == true)
            {
                if (success)
                {
                    _settingsService.ResetTodayRetryCount();
                    _settingsService.SetLastRunFailed(false, null);
                    StreakScheduler.ScheduleNextRun(this);
                }
                else
                {
                    var attempt = StreakScheduler.TryScheduleRetryOrGiveUp(this, SettingsService.FailureReasonSendError);
                    if (attempt > 0)
                        AppLog("SYSTEM", "-", $"Run had errors — scheduled hourly retry {attempt}/{SettingsService.MaxRetriesPerDay}");
                    else
                        AppLog("SYSTEM", "-", $"Run had errors — retry budget exhausted, normal next-run slot scheduled");
                }
            }

            AppLog("SYSTEM", "-", $"Run complete: {(success ? "Success" : message)}");
        }
        finally
        {
            lock (_runLock)
            {
                _isRunning = false;
            }

            CleanupWebView();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    [Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
    private class StreakWebViewClient : WebViewClient
    {
        private readonly StreakService _service;

        public StreakWebViewClient(StreakService service)
        {
            _service = service;
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            if (!string.IsNullOrEmpty(url))
            {
                _service.OnPageLoaded(url);
            }
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (request?.Url is not null)
            {
                if ((request.Url.EncodedSchemeSpecificPart ?? "").StartsWith("//aweme"))
                {
                    return true;
                }
            }
            return false;
        }
    }

    [Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
    private class StreakJsInterface : Java.Lang.Object
    {
        private readonly StreakService _service;

        public StreakJsInterface(StreakService service)
        {
            _service = service;
        }

        [JavascriptInterface]
        [Export("onAutomationComplete")]
        public void OnAutomationComplete(bool success, string summary)
        {
            _service._mainHandler?.Post(() => _service.OnAutomationFinished(success, summary));
        }

        [JavascriptInterface]
        [Export("log")]
        public void Log(string message)
        {
            var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            lock (LogLock)
                StreakService._logs.Add(entry);
            global::Android.Util.Log.Debug("StreakJS", message);
        }
    }
}
