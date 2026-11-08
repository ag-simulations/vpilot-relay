using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VpilotRelaySetup
{
    public partial class MainWindow : Window
    {
        private enum Page { Welcome, Vpilot, Bot, Token, Chat, Install }

        private readonly ObservableCollection<StepItem> _steps = new ObservableCollection<StepItem>();
        private readonly FrameworkElement[] _pages;
        private readonly DispatcherTimer _tokenTimer;
        private Page _page;

        // Wizard state
        private VpilotInfo _vpilot;
        private string _customDir;
        private string _token;       // verified bot token
        private BotInfo _bot;
        private long? _chatId;
        private string _chatName;
        private bool _keepExisting;  // "keep settings, just update" shortcut from the vPilot page
        private bool _installing, _installed, _installFailed;

        private CancellationTokenSource _listenCts;
        private int _tokenCheck;

        private static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public MainWindow()
        {
            InitializeComponent();

            var titles = new[] { "Welcome", "vPilot", "Telegram bot", "Bot token", "Your account", "Install" };
            for (var i = 0; i < titles.Length; i++)
                _steps.Add(new StepItem { Number = i + 1, Title = titles[i], IsLast = i == titles.Length - 1 });
            StepList.ItemsSource = _steps;
            VersionText.Text = "Version " + AppVersion;

            _pages = new FrameworkElement[] { PageWelcome, PageVpilot, PageBot, PageToken, PageChat, PageInstall };
            _tokenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _tokenTimer.Tick += async (s, e) =>
            {
                _tokenTimer.Stop();
                await VerifyTokenAsync();
            };

            Closing += (s, e) => _listenCts?.Cancel();
            ShowPage(Page.Welcome, animate: false);
#if DEBUG
            Loaded += (s, e) => Snapshot.TryRun(this);
#endif
        }

        // ================= navigation =================

        private void ShowPage(Page page, bool animate = true)
        {
            if (_page == Page.Chat && page != Page.Chat) StopListening();
            _page = page;

            for (var i = 0; i < _pages.Length; i++)
                _pages[i].Visibility = i == (int)page ? Visibility.Visible : Visibility.Collapsed;
            for (var i = 0; i < _steps.Count; i++)
                _steps[i].State = _installed || i < (int)page ? "done" : i == (int)page ? "current" : "pending";

            if (animate) FadeIn(_pages[(int)page]);

            switch (page)
            {
                case Page.Vpilot: RefreshVpilot(); break;
                case Page.Token: EnterTokenPage(); break;
                case Page.Chat: EnterChatPage(); break;
                case Page.Install: EnterInstallPage(); break;
            }
            UpdateFooter();
        }

        private static void FadeIn(FrameworkElement el)
        {
            var shift = new TranslateTransform(0, 12);
            el.RenderTransform = shift;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        }

        private void UpdateFooter()
        {
            BackButton.Visibility = _page == Page.Welcome || _installed ? Visibility.Collapsed : Visibility.Visible;
            BackButton.IsEnabled = !_installing;
            NextIcon.Visibility = Visibility.Visible;
            NextIcon.Text = "";

            switch (_page)
            {
                case Page.Welcome:
                    SetNext("Start", true);
                    break;
                case Page.Vpilot:
                    SetNext("Next", _vpilot?.Ok == true);
                    break;
                case Page.Bot:
                    SetNext("I have my token", true);
                    break;
                case Page.Token:
                    SetNext("Next", _token != null);
                    break;
                case Page.Chat:
                    SetNext("Next", _chatId.HasValue);
                    break;
                case Page.Install:
                    if (_installed)
                    {
                        SetNext("Finish", true);
                        NextIcon.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        SetNext(_installFailed ? "Try again" : "Install", !_installing && _token != null && _chatId.HasValue);
                        NextIcon.Text = _installFailed ? "" : "";
                    }
                    break;
            }
        }

        private void SetNext(string text, bool enabled)
        {
            NextText.Text = text;
            NextButton.IsEnabled = enabled;
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (!NextButton.IsEnabled) return;
            if (_page == Page.Install)
            {
                if (_installed) Close();
                else await InstallAsync();
                return;
            }
            ShowPage(_page + 1);
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_installing) return;
            if (_page == Page.Install && _keepExisting)
            {
                _keepExisting = false;
                ShowPage(Page.Vpilot);
                return;
            }
            ShowPage(_page - 1);
        }

        // ================= window chrome =================

        private void DragWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_installing) return;
            Close();
        }

        private void CompanyLogo_Click(object sender, MouseButtonEventArgs e) => OpenUrl("https://ag-simulations.com");

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            OpenUrl(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser registered */ }
        }

        // ================= 1. vPilot =================

        private void RefreshVpilot()
        {
            var dir = _customDir ?? Vpilot.FindDir();
            _vpilot = Vpilot.Inspect(dir);
            VpilotPath.Text = dir ?? "vPilot not found";

            if (dir == null)
                SetCheck(ExeIcon, ExeTitle, ExeDetail, StatusKind.Error, "vPilot not found",
                    "If it isn't installed, get it from vpilot.rosscarlson.dev first. If it is, use \"Change…\" to pick vPilot.exe.");
            else if (!_vpilot.ExeFound)
                SetCheck(ExeIcon, ExeTitle, ExeDetail, StatusKind.Error, "No vPilot.exe in this folder", "Use \"Change…\" to pick vPilot.exe in the right folder.");
            else
                SetCheck(ExeIcon, ExeTitle, ExeDetail, StatusKind.Ok, "vPilot found", VersionOf(_vpilot.ExePath));

            if (_vpilot.PluginApiFound)
                SetCheck(ApiIcon, ApiTitle, ApiDetail, StatusKind.Ok, "Plugin support available", "This vPilot version supports plugins.");
            else
                SetCheck(ApiIcon, ApiTitle, ApiDetail, _vpilot.ExeFound ? StatusKind.Error : StatusKind.Pending, "Plugin support",
                    _vpilot.ExeFound ? "Plugin interface not found. Update vPilot to the latest version." : "Checked once vPilot is found.");

            if (_vpilot.Running)
                SetCheck(RunIcon, RunTitle, RunDetail, StatusKind.Warn, "vPilot is running",
                    "The plugin can't be replaced while vPilot is running; you'll need to close it before installing.");
            else
                SetCheck(RunIcon, RunTitle, RunDetail, StatusKind.Ok, "vPilot is closed", "Ready to install.");
            RunActions.Visibility = _vpilot.Running ? Visibility.Visible : Visibility.Collapsed;

            ExistingCard.Visibility = _vpilot.Ok && _vpilot.HasExistingSettings ? Visibility.Visible : Visibility.Collapsed;
            ExistingTitle.Text = "Existing installation found" +
                                 (string.IsNullOrEmpty(_vpilot.ExistingVersion) ? "" : " (version " + ShortVersion(_vpilot.ExistingVersion) + ")");
            UpdateFooter();
        }

        private static void SetCheck(ContentControl icon, TextBlock title, TextBlock detail, StatusKind kind, string t, string d)
        {
            icon.Content = Ui.StatusIcon(kind, 32);
            title.Text = t;
            detail.Text = d ?? "";
            detail.Visibility = string.IsNullOrEmpty(d) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string VersionOf(string exe)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
                return string.IsNullOrEmpty(v) ? null : "Version " + ShortVersion(v);
            }
            catch { return null; }
        }

        private static string ShortVersion(string v)
        {
            var parts = v.Split('.', ' ', '+');
            return string.Join(".", parts.Take(3));
        }

        private void BrowseVpilot_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select vPilot.exe",
                Filter = "vPilot|vPilot.exe|Programs (*.exe)|*.exe",
                CheckFileExists = true,
            };
            if (_vpilot?.Dir != null && Directory.Exists(_vpilot.Dir)) dlg.InitialDirectory = _vpilot.Dir;
            if (dlg.ShowDialog(this) == true)
            {
                _customDir = Path.GetDirectoryName(dlg.FileName);
                RefreshVpilot();
            }
        }

        private void Recheck_Click(object sender, RoutedEventArgs e) => RefreshVpilot();

        private async void CloseVpilot_Click(object sender, RoutedEventArgs e)
        {
            CloseVpilotButton.IsEnabled = false;
            CloseVpilotText.Text = "Closing…";
            var closed = await Vpilot.CloseAsync();
            CloseVpilotButton.IsEnabled = true;
            CloseVpilotText.Text = "Close vPilot";
            RefreshVpilot();
            if (!closed)
                RunDetail.Text = "vPilot didn't close; it may be showing a confirmation dialog. Close it yourself, then press refresh.";
        }

        private async void KeepExisting_Click(object sender, RoutedEventArgs e)
        {
            _keepExisting = true;
            _token = _vpilot.ExistingToken;
            _chatId = _vpilot.ExistingChatId;
            _chatName = null;
            _bot = null;
            ShowPage(Page.Install);
            try
            {
                _bot = await TelegramApi.GetMeAsync(_token);
                if (_page == Page.Install && !_installed && !_installing) RenderSummary();
            }
            catch { /* shown as "bot from existing settings"; the test message at the end will tell if the token is broken */ }
        }

        // ================= 2. bot =================

        private void OpenBotFather_Click(object sender, RoutedEventArgs e) => OpenUrl("https://t.me/BotFather");

        private void OpenTelegramWeb_Click(object sender, RoutedEventArgs e) => OpenUrl("https://web.telegram.org/");

        private async void CopyNewBot_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText("/newbot"); }
            catch { return; }
            CopyNewBotIcon.Text = "";
            CopyNewBotText.Text = "Copied";
            await Task.Delay(1600);
            CopyNewBotIcon.Text = "";
            CopyNewBotText.Text = "Copy";
        }

        // ================= 3. token =================

        private void EnterTokenPage()
        {
            if (TokenBox.Text.Length == 0 && _token == null && !string.IsNullOrEmpty(_vpilot?.ExistingToken))
                TokenBox.Text = _vpilot.ExistingToken;
            Dispatcher.BeginInvoke(new Action(() => { TokenBox.Focus(); TokenBox.CaretIndex = TokenBox.Text.Length; }),
                DispatcherPriority.Input);
        }

        private void PasteToken_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText()) TokenBox.Text = Clipboard.GetText().Trim();
            }
            catch { /* clipboard busy */ }
            TokenBox.Focus();
        }

        private void TokenBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TokenPlaceholder.Visibility = TokenBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var text = TokenBox.Text.Trim();

            // Pasting BotFather's whole message is fine: pick the token out of it.
            var m = TelegramApi.TokenRx.Match(text);
            if (m.Success && m.Value != TokenBox.Text)
            {
                TokenBox.Text = m.Value;
                TokenBox.CaretIndex = m.Value.Length;
                return;
            }

            _tokenTimer.Stop();
            _tokenCheck++;
            if (text != _token)
            {
                _token = null;
                _bot = null;
            }

            if (text.Length == 0)
                TokenStatus.Visibility = Visibility.Collapsed;
            else if (!m.Success)
                SetTokenStatus(text.Length < 20 ? StatusKind.Info : StatusKind.Error,
                    text.Length < 20 ? "Paste the whole token" : "This doesn't look like a bot token",
                    "Format: digits, a colon and a long string of letters — 123456789:AAH4k…");
            else if (_token == null)
            {
                SetTokenStatus(StatusKind.Busy, "Checking with Telegram…", null);
                _tokenTimer.Start();
            }
            UpdateFooter();
        }

        private async Task VerifyTokenAsync()
        {
            var token = TokenBox.Text.Trim();
            var check = ++_tokenCheck;
            try
            {
                var bot = await TelegramApi.GetMeAsync(token);
                if (check != _tokenCheck) return;
                if (_token != token) _chatId = null; // a different bot: the chat must be detected again
                _token = token;
                _bot = bot;
                SetTokenStatus(StatusKind.Ok, "@" + bot.Username + " is ready", "Bot name: " + bot.Name);
            }
            catch (TelegramException ex)
            {
                if (check != _tokenCheck) return;
                if (ex.Code == 401 || ex.Code == 404)
                    SetTokenStatus(StatusKind.Error, "Invalid token",
                        "Telegram doesn't recognise this token. Make sure you copied the whole token from BotFather.");
                else
                    SetTokenStatus(StatusKind.Error, "Could not check", ex.Message);
            }
            UpdateFooter();
        }

        private void SetTokenStatus(StatusKind kind, string title, string detail)
            => Ui.SetStatusBox(TokenStatus, TokenStatusIcon, TokenStatusTitle, TokenStatusDetail, kind, title, detail);

        // ================= 4. chat id =================

        private void EnterChatPage()
        {
            ChatBotName.Text = _bot != null ? "@" + _bot.Username + " — " + _bot.Name : "Your bot";
            if (_chatId.HasValue) ShowChatFound();
            else StartListening();
        }

        private void OpenBot_Click(object sender, RoutedEventArgs e)
        {
            if (_bot != null) OpenUrl("https://t.me/" + _bot.Username);
        }

        private void StartListening()
        {
            StopListening();
            _listenCts = new CancellationTokenSource();
            ListenWaiting.Visibility = Visibility.Visible;
            ListenFound.Visibility = Visibility.Collapsed;
            ManualToggle.Visibility = Visibility.Visible;
            ListenTitle.Text = "Waiting for your message…";
            ListenDetail.Text = "Keep this window open; you'll see it here the moment you message your bot.";
            _ = ListenAsync(_token, _listenCts.Token);
        }

        private void StopListening()
        {
            _listenCts?.Cancel();
            _listenCts = null;
        }

        private async Task ListenAsync(string token, CancellationToken ct)
        {
            long offset = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var updates = await TelegramApi.GetUpdatesAsync(token, offset, 25, ct);
                    if (ct.IsCancellationRequested) return;
                    ListenTitle.Text = "Waiting for your message…";
                    ListenDetail.Text = "Keep this window open; you'll see it here the moment you message your bot.";

                    ChatUpdate found = null;
                    foreach (var u in updates)
                    {
                        offset = u.UpdateId + 1;
                        if (u.ChatId.HasValue && u.ChatType == "private") found = u;
                    }
                    if (found == null) continue;

                    // Mark them as read, so the plugin doesn't answer this /start later.
                    try { await TelegramApi.GetUpdatesAsync(token, offset, 0, ct); } catch { /* ignore */ }
                    if (ct.IsCancellationRequested) return;

                    _chatId = found.ChatId;
                    _chatName = found.DisplayName;
                    ShowChatFound(found.Username);
                    UpdateFooter();
                    await SendHelloAsync(token, found);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (TelegramException ex)
                {
                    if (ct.IsCancellationRequested) return;
                    ListenTitle.Text = ex.Code == 409 ? "The bot is in use elsewhere" : "Connection problem";
                    ListenDetail.Text = ex.Code == 409
                        ? "vPilot may be running with this bot. Close vPilot; I'll try again in a few seconds."
                        : ex.Message + " Retrying…";
                    try { await Task.Delay(3000, ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private async Task SendHelloAsync(string token, ChatUpdate who)
        {
            try
            {
                await TelegramApi.SendMessageAsync(token, who.ChatId.Value,
                    "👋 Hi " + who.FirstName + "!\nvPilot Relay setup found you. You can continue on your PC.");
                FoundDetail.Text += " · Sent you a confirmation on Telegram.";
            }
            catch { /* not important */ }
        }

        private void ShowChatFound(string username = null)
        {
            ListenWaiting.Visibility = Visibility.Collapsed;
            ListenFound.Visibility = Visibility.Visible;
            ManualToggle.Visibility = Visibility.Collapsed;
            ManualPanel.Visibility = Visibility.Collapsed;
            FadeIn(ListenFound);
            FoundTitle.Text = _chatName != null ? "Hi " + _chatName.Split(' ')[0] + "!" : "Account set";
            FoundDetail.Text = (username != null ? "@" + username + " · " : "") + "Chat ID " + _chatId;
        }

        private void NotMe_Click(object sender, RoutedEventArgs e)
        {
            _chatId = null;
            _chatName = null;
            StartListening();
            UpdateFooter();
        }

        private void ManualToggle_Click(object sender, RoutedEventArgs e)
        {
            var open = ManualPanel.Visibility != Visibility.Visible;
            ManualPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            ManualToggleIcon.Text = open ? "" : "";
            if (open) ManualChatBox.Focus();
        }

        private async void ManualUse_Click(object sender, RoutedEventArgs e)
        {
            ManualError.Foreground = (Brush)FindResource("BodyBrush");
            if (!long.TryParse(ManualChatBox.Text.Trim(), out var id))
            {
                ManualError.Foreground = Brushes.Firebrick;
                ManualError.Text = "A Chat ID contains digits only.";
                return;
            }
            ManualUseButton.IsEnabled = false;
            ManualError.Text = "Checking…";
            try
            {
                // Sending a message proves the bot can reach this chat.
                await TelegramApi.SendMessageAsync(_token, id, "👋 vPilot Relay setup will use this chat.");
                StopListening();
                _chatId = id;
                _chatName = null;
                ManualError.Text = "";
                ShowChatFound();
                FoundDetail.Text = "Chat ID " + id + " · Sent you a confirmation on Telegram.";
                UpdateFooter();
            }
            catch (TelegramException ex)
            {
                ManualError.Foreground = Brushes.Firebrick;
                ManualError.Text = ex.Code == 400 || ex.Code == 403
                    ? "Can't reach this chat. Send your bot a message first."
                    : ex.Message;
            }
            finally
            {
                ManualUseButton.IsEnabled = true;
            }
        }

        // ================= 5. install =================

        private void EnterInstallPage()
        {
            if (_installed) return;
            InstallMain.Visibility = Visibility.Visible;
            DonePanel.Visibility = Visibility.Collapsed;
            if (!_installing && !_installFailed)
            {
                ProgressList.Children.Clear();
                InstallError.Visibility = Visibility.Collapsed;
                SetInstallView(running: false);
            }
            RenderSummary();
        }

        /// <summary>Before installing: title + summary of what will be installed. While/after: title + progress list.</summary>
        private void SetInstallView(bool running)
        {
            SummaryCard.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            ProgressCard.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (running)
            {
                InstallTitle.Text = "Installing…";
                InstallLead.Text = "This takes a few seconds.";
                FadeIn(ProgressCard);
                return;
            }
            var update = _vpilot?.ExistingVersion != null;
            InstallTitle.Text = update ? "Ready to update" : "Ready to install";
            InstallLead.Text = update
                ? "The installed plugin will be replaced with version " + AppVersion + ". If everything looks right, press Install."
                : "I'll install with the details below. If everything looks right, press Install.";
        }

        private void RenderSummary()
        {
            SummaryList.Children.Clear();
            AddSummaryRow("", "Install location", _vpilot?.PluginsDir ?? "-", false);
            AddSummaryRow("", "Telegram bot", _bot != null ? "@" + _bot.Username + "  ·  " + _bot.Name : "Bot from existing settings", true);
            AddSummaryRow("", "Your Telegram account", (_chatName != null ? _chatName + "  ·  " : "") + "Chat ID " + _chatId, true);
        }

        private void AddSummaryRow(string glyph, string label, string value, bool separator)
        {
            if (separator)
                SummaryList.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("LineBrush") });

            var grid = new Grid { Margin = new Thickness(0, 11, 0, 11) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var badge = new Border { Style = (Style)FindResource("IconBadge"), Width = 34, Height = 34, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Center };
            badge.Child = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 15, Foreground = (Brush)FindResource("PrimaryBrush") };
            grid.Children.Add(badge);

            var texts = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Small"), Foreground = (Brush)FindResource("MutedBrush") });
            texts.Children.Add(new TextBlock
            {
                Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ui.Ink,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value, Margin = new Thickness(0, 1, 0, 0),
            });
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            SummaryList.Children.Add(grid);
        }

        private sealed class ProgressRow
        {
            public ContentControl Icon;
            public TextBlock Title;
            public TextBlock Detail;
        }

        private ProgressRow AddProgressRow(string title)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var row = new ProgressRow
            {
                Icon = new ContentControl { Focusable = false, Content = Ui.StatusIcon(StatusKind.Pending, 26), VerticalAlignment = VerticalAlignment.Top },
                Title = new TextBlock { Text = title, FontSize = 14, Foreground = Ui.Body, VerticalAlignment = VerticalAlignment.Center },
                Detail = new TextBlock { Style = (Style)FindResource("Small"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) },
            };
            var texts = new StackPanel { Margin = new Thickness(12, 3, 0, 0) };
            texts.Children.Add(row.Title);
            texts.Children.Add(row.Detail);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(row.Icon);
            grid.Children.Add(texts);
            ProgressList.Children.Add(grid);
            FadeIn(grid);
            return row;
        }

        private static void SetProgress(ProgressRow row, StatusKind kind, string detail = null)
        {
            row.Icon.Content = Ui.StatusIcon(kind, 26);
            row.Title.Foreground = kind == StatusKind.Pending ? Ui.Body : Ui.Ink;
            row.Title.FontWeight = kind == StatusKind.Busy ? FontWeights.SemiBold : FontWeights.Normal;
            row.Detail.Text = detail ?? "";
            row.Detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task InstallAsync()
        {
            _installing = true;
            _installFailed = false;
            InstallError.Visibility = Visibility.Collapsed;
            ProgressList.Children.Clear();
            SetInstallView(running: true);
            UpdateFooter();

            var check = AddProgressRow("Checking that vPilot is closed");
            var copy = AddProgressRow("Copying the plugin to the Plugins folder");
            var settings = AddProgressRow("Saving Telegram settings");
            var test = AddProgressRow("Sending a test message on Telegram");
            var testWarning = (string)null;

            try
            {
                SetProgress(check, StatusKind.Busy);
                await Task.Delay(350);
                if (Vpilot.IsRunning())
                {
                    SetProgress(check, StatusKind.Error);
                    Fail("vPilot is running", "The plugin can't be replaced while vPilot is running. Close vPilot and press \"Try again\".", offerClose: true);
                    return;
                }
                SetProgress(check, StatusKind.Ok);

                SetProgress(copy, StatusKind.Busy);
                var pluginsDir = _vpilot.PluginsDir;
                await Task.Run(() => Payload.CopyPlugin(pluginsDir));
                await Task.Delay(250);
                SetProgress(copy, StatusKind.Ok);

                SetProgress(settings, StatusKind.Busy);
                string token = _token;
                var chatId = _chatId.Value;
                await Task.Run(() => Payload.WriteSettings(pluginsDir, token, chatId));
                await Task.Delay(250);
                SetProgress(settings, StatusKind.Ok);

                SetProgress(test, StatusKind.Busy);
                try
                {
                    await TelegramApi.SendMessageAsync(token, chatId,
                        "✅ vPilot Relay " + AppVersion + " is installed!\n\nWhen you start vPilot you'll get \"✈️ vPilot Relay is running\" here. " +
                        "Then send /menu to control everything with buttons.");
                    SetProgress(test, StatusKind.Ok);
                }
                catch (TelegramException ex)
                {
                    testWarning = ex.Message;
                    SetProgress(test, StatusKind.Warn, "Not sent: " + ex.Message);
                }

                await Task.Delay(700);
                _installed = true;
                ShowDone(testWarning);
            }
            catch (UnauthorizedAccessException)
            {
                SetFailedRow(copy, settings);
                Fail("No permission to write to the folder",
                    "vPilot may be installed in a protected folder. Right-click the setup and choose \"Run as administrator\".");
            }
            catch (IOException ex)
            {
                SetFailedRow(copy, settings);
                Fail("Could not write the file", ex.Message + " If vPilot is running, close it and try again.", offerClose: Vpilot.IsRunning());
            }
            catch (Exception ex)
            {
                SetFailedRow(copy, settings);
                Fail("Unexpected error", ex.Message);
            }
            finally
            {
                _installing = false;
                UpdateFooter();
            }
        }

        private static void SetFailedRow(params ProgressRow[] rows)
        {
            var busy = rows.FirstOrDefault(r => r.Icon.Content is Spinner);
            if (busy != null) SetProgress(busy, StatusKind.Error);
        }

        private void Fail(string title, string detail, bool offerClose = false)
        {
            _installFailed = true;
            InstallTitle.Text = "Installation not completed";
            InstallLead.Text = "Fix the problem and try again; nothing has been broken.";
            InstallErrorTitle.Text = title;
            InstallErrorDetail.Text = detail;
            InstallCloseVpilot.Visibility = offerClose ? Visibility.Visible : Visibility.Collapsed;
            InstallError.Visibility = Visibility.Visible;
            FadeIn(InstallError);
        }

        private async void InstallCloseVpilot_Click(object sender, RoutedEventArgs e)
        {
            InstallCloseVpilot.IsEnabled = false;
            InstallCloseVpilotText.Text = "Closing…";
            var closed = await Vpilot.CloseAsync();
            InstallCloseVpilot.IsEnabled = true;
            InstallCloseVpilotText.Text = "Close vPilot";
            if (closed)
            {
                InstallErrorTitle.Text = "vPilot closed";
                InstallErrorDetail.Text = "You can press \"Try again\" now.";
                InstallCloseVpilot.Visibility = Visibility.Collapsed;
            }
            else
            {
                InstallErrorDetail.Text = "vPilot didn't close; it may be showing a confirmation dialog. Close it yourself and press \"Try again\".";
            }
        }

        private void ShowDone(string testWarning)
        {
            InstallMain.Visibility = Visibility.Collapsed;
            DonePanel.Visibility = Visibility.Visible;
            FadeIn(DonePanel);
            foreach (var s in _steps) s.State = "done";
            DoneLead.Text = testWarning == null
                ? "vPilot Relay " + AppVersion + " is installed in vPilot and a test message was sent to your Telegram."
                : "vPilot Relay " + AppVersion + " is installed in vPilot. The test message could not be sent (" + testWarning + "); the bot will still work once vPilot is running.";
            LaunchButton.Visibility = _vpilot?.ExeFound == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateFooter();
        }

        private void Launch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Vpilot.Launch(_vpilot);
                LaunchButton.IsEnabled = false;
                LaunchIcon.Text = "";
                LaunchText.Text = "Starting vPilot";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not start vPilot: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

#if DEBUG
        /// <summary>Debug-only hooks for rendering screenshots of each page (see Snapshot.cs).</summary>
        internal void PreviewState(int page, string variant)
        {
            _bot = new BotInfo { Username = "my_vpilot_bot", Name = "vPilot Relay" };
            switch ((Page)page)
            {
                case Page.Token:
                    _token = "123456789:EXAMPLE_TOKEN_for_screenshots_only_x";
                    TokenBox.Text = _token;
                    SetTokenStatus(StatusKind.Ok, "@my_vpilot_bot is ready", "Bot name: vPilot Relay");
                    ShowPage(Page.Token, false);
                    break;
                case Page.Chat:
                    _token = "x";
                    _page = Page.Chat;
                    ShowPage(Page.Chat, false);
                    StopListening();
                    if (variant == "found")
                    {
                        _chatId = 123456789;
                        _chatName = "Arda G";
                        ShowChatFound("ardag");
                        FoundDetail.Text += " · Sent you a confirmation on Telegram.";
                    }
                    UpdateFooter();
                    break;
                case Page.Install:
                    _token = "x";
                    _chatId = 123456789;
                    _chatName = "Arda G";
                    _vpilot = Vpilot.Inspect(Vpilot.FindDir());
                    ShowPage(Page.Install, false);
                    if (variant == "done") { _installed = true; ShowDone(null); }
                    else if (variant == "error")
                    {
                        SetInstallView(running: true);
                        var a = AddProgressRow("Checking that vPilot is closed");
                        AddProgressRow("Copying the plugin to the Plugins folder");
                        AddProgressRow("Saving Telegram settings");
                        AddProgressRow("Sending a test message on Telegram");
                        SetProgress(a, StatusKind.Error);
                        Fail("vPilot is running", "The plugin can't be replaced while vPilot is running. Close vPilot and press \"Try again\".", true);
                        UpdateFooter();
                    }
                    else if (variant == "busy")
                    {
                        SetInstallView(running: true);
                        var a = AddProgressRow("Checking that vPilot is closed");
                        var b = AddProgressRow("Copying the plugin to the Plugins folder");
                        AddProgressRow("Saving Telegram settings");
                        AddProgressRow("Sending a test message on Telegram");
                        SetProgress(a, StatusKind.Ok);
                        SetProgress(b, StatusKind.Busy);
                    }
                    break;
                default:
                    ShowPage((Page)page, false);
                    break;
            }
        }
#endif
    }
}
