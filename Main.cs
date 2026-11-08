using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RossCarlson.Vatsim.Vpilot.Plugins;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;
using RossCarlson.Vatsim.Vpilot.Plugins.Exceptions;

namespace VpilotRelay
{
    /// <summary>vPilot side: plugin entry point, vPilot events and shared state. Telegram UI is in Bot.cs.</summary>
    public partial class Main : IPlugin
    {
        public string Name { get; } = "vPilot Relay";

        private IBroker _broker;
        private Settings _settings;
        private TelegramClient _telegram;
        private SimRadio _sim;
        private SynchronizationContext _uiContext;

        private string _callsign;
        private string _typeCode;
        // Recent connections, newest first (persisted in vPilotRelay.recent).
        private List<(string Callsign, string TypeCode, string Selcal)> _recentConnects =
            new List<(string Callsign, string TypeCode, string Selcal)>();
        private DateTime? _connectedAt;
        private bool _muted;
        private bool _radioAll;

        private sealed class Controller
        {
            public string Callsign;
            public int Khz;
            public double Lat, Lon;
        }
        private readonly Dictionary<string, Controller> _controllers = new Dictionary<string, Controller>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        // Pending METAR / ATIS requests made from Telegram, so only answers we asked for are relayed.
        private DateTime _metarUntil;
        private string _atisFor;
        private DateTime _atisUntil;

        public void Initialize(IBroker broker)
        {
            _broker = broker;
            _uiContext = SynchronizationContext.Current;

            try
            {
                _settings = Settings.Load();
            }
            catch (Exception ex)
            {
                Debug("Could not read settings: " + ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(_settings.BotToken))
            {
                Debug("BotToken is empty. Settings file: " + _settings.IniPath);
                return;
            }

            _radioAll = _settings.RadioAll;
            _recentConnects = _settings.LoadRecentConnects();
            _telegram = new TelegramClient(_settings.BotToken, Debug);
            _sim = new SimRadio(Debug);

            _broker.NetworkConnected += OnNetworkConnected;
            _broker.NetworkDisconnected += OnNetworkDisconnected;
            _broker.SessionEnded += OnSessionEnded;
            _broker.PrivateMessageReceived += OnPrivateMessageReceived;
            _broker.RadioMessageReceived += OnRadioMessageReceived;
            _broker.SelcalAlertReceived += OnSelcalAlertReceived;
            _broker.BroadcastMessageReceived += OnBroadcastMessageReceived;
            _broker.MetarReceived += OnMetarReceived;
            _broker.AtisReceived += OnAtisReceived;
            _broker.ControllerAdded += OnControllerAdded;
            _broker.ControllerDeleted += OnControllerDeleted;
            _broker.ControllerFrequencyChanged += OnControllerFrequencyChanged;
            _broker.ControllerLocationChanged += OnControllerLocationChanged;

            _telegram.StartPolling(OnTelegramMessage);
            _ = _telegram.SetCommandsAsync(BotCommands);

            if (_settings.ChatId.HasValue)
                Notify("✈️ vPilot Relay is running.\nSend /menu to see what you can do.");
            else
                Debug("ChatId is empty. Send any message to the bot on Telegram and it will tell you your Chat ID.");

            Debug("vPilot Relay loaded.");
        }

        // ---------- vPilot events ----------

        private void OnNetworkConnected(object sender, NetworkConnectedEventArgs e)
        {
            _callsign = e.Callsign;
            _typeCode = e.TypeCode;
            if (!e.ObserverMode && !string.IsNullOrEmpty(e.Callsign) && !string.IsNullOrEmpty(e.TypeCode))
                RememberConnect(e.Callsign.ToUpperInvariant(), e.TypeCode.ToUpperInvariant(), (e.SelcalCode ?? "").ToUpperInvariant());
            _connectedAt = DateTime.UtcNow;
            Task.Run(() => _sim.EnsureConnected());
            if (_settings.RelayConnection)
                Notify("🟢 Connected to VATSIM: " + e.Callsign + (string.IsNullOrEmpty(e.TypeCode) ? "" : " (" + e.TypeCode + ")"));
        }

        private void OnNetworkDisconnected(object sender, EventArgs e)
        {
            _callsign = null;
            _connectedAt = null;
            lock (_lock) _controllers.Clear();
            if (_settings.RelayConnection)
                Notify("🔴 Disconnected from VATSIM.");
        }

        private void OnSessionEnded(object sender, EventArgs e)
        {
            _telegram?.Stop();
            _sim?.Dispose();
        }

        private void OnPrivateMessageReceived(object sender, PrivateMessageReceivedEventArgs e)
        {
            if (!_settings.RelayPrivate) return;
            Notify("✉️ " + e.From + "\n" + e.Message + "\n\n↩️ Reply to answer", action: "pm:" + e.From, forceSound: true);
        }

        private void OnRadioMessageReceived(object sender, RadioMessageReceivedEventArgs e)
        {
            if (!_settings.RelayRadio) return;
            var mentioned = _callsign != null &&
                            e.Message.IndexOf(_callsign, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!_radioAll && !mentioned)
                return;

            var freqs = e.Frequencies == null || e.Frequencies.Length == 0
                ? "" : " [" + string.Join(", ", e.Frequencies.Select(f => FormatKhz(NormalizeKhz(f)))) + "]";
            // Messages that mention us are treated like PMs: replying answers on frequency.
            Notify((mentioned ? "📻❗ " : "📻 ") + e.From + freqs + "\n" + e.Message,
                action: mentioned ? "radio" : null, forceSound: mentioned);
        }

        private void OnSelcalAlertReceived(object sender, SelcalAlertReceivedEventArgs e)
        {
            if (_settings.RelaySelcal)
                Notify("🔔 SELCAL: " + e.From, forceSound: true);
        }

        private void OnBroadcastMessageReceived(object sender, BroadcastMessageReceivedEventArgs e)
        {
            if (_settings.RelayBroadcast)
                Notify("📢 Broadcast — " + e.From + "\n" + e.Message);
        }

        private void OnMetarReceived(object sender, MetarReceivedEventArgs e)
        {
            if (DateTime.UtcNow > _metarUntil) return;
            Notify("🌤 " + e.Metar, forceSound: true);
        }

        private void OnAtisReceived(object sender, AtisReceivedEventArgs e)
        {
            if (DateTime.UtcNow > _atisUntil || !string.Equals(e.From, _atisFor, StringComparison.OrdinalIgnoreCase))
                return;
            _atisFor = null;
            var lines = e.Lines == null || e.Lines.Count == 0 ? "(empty)" : string.Join("\n", e.Lines);
            Notify("ℹ️ " + e.From + "\n" + lines, forceSound: true);
        }

        private void OnControllerAdded(object sender, ControllerAddedEventArgs e)
        {
            lock (_lock)
                _controllers[e.Callsign] = new Controller
                {
                    Callsign = e.Callsign, Khz = NormalizeKhz(e.Frequency), Lat = e.Latitude, Lon = e.Longitude,
                };
        }

        private void OnControllerDeleted(object sender, ControllerDeletedEventArgs e)
        {
            lock (_lock) _controllers.Remove(e.Callsign);
        }

        private void OnControllerFrequencyChanged(object sender, ControllerFrequencyChangedEventArgs e)
        {
            lock (_lock)
                if (_controllers.TryGetValue(e.Callsign, out var c)) c.Khz = NormalizeKhz(e.NewFrequency);
        }

        private void OnControllerLocationChanged(object sender, ControllerLocationChangedEventArgs e)
        {
            lock (_lock)
                if (_controllers.TryGetValue(e.Callsign, out var c)) { c.Lat = e.NewLatitude; c.Lon = e.NewLongitude; }
        }

        // ---------- helpers ----------

        private void RememberConnect(string callsign, string typeCode, string selcal)
        {
            List<(string, string, string)> snapshot;
            lock (_lock)
            {
                _recentConnects.RemoveAll(c => c.Callsign == callsign && c.TypeCode == typeCode && c.Selcal == selcal);
                _recentConnects.Insert(0, (callsign, typeCode, selcal));
                if (_recentConnects.Count > Settings.MaxRecentConnects)
                    _recentConnects.RemoveRange(Settings.MaxRecentConnects, _recentConnects.Count - Settings.MaxRecentConnects);
                snapshot = _recentConnects.ToList();
            }
            Task.Run(() => _settings.SaveRecentConnects(snapshot));
        }

        private List<(string Callsign, string TypeCode, string Selcal)> RecentConnects()
        {
            lock (_lock)
                if (_recentConnects.Count > 0) return _recentConnects.ToList();
            var fromVpilot = _settings.ReadLastConnectInfo();
            return fromVpilot.HasValue
                ? new List<(string, string, string)> { fromVpilot.Value }
                : new List<(string, string, string)>();
        }

        /// <summary>Sends a notification to the owner. action: what a reply to it should do (see Bot.cs).</summary>
        private void Notify(string text, string action = null, bool forceSound = false)
        {
            if (_telegram == null || !_settings.ChatId.HasValue)
                return;
            var chatId = _settings.ChatId.Value;
            var silent = _muted && !forceSound;
            _ = Task.Run(async () =>
            {
                var id = await _telegram.SendMessageAsync(chatId, text, silent: silent).ConfigureAwait(false);
                if (id.HasValue && action != null)
                    RememberAction(id.Value, action);
            });
        }

        /// <summary>Runs a broker call on vPilot's UI thread. Returns null on success or an error description.</summary>
        private string OnVpilot(Action<IBroker> call)
        {
            string error = null;
            void Run()
            {
                try { call(_broker); }
                catch (NotConnectedException) { error = "You are not connected to VATSIM."; }
                catch (NotTransmittingException) { error = "No transmit frequency selected (TX)."; }
                catch (SimNotReadyException) { error = "The simulator is not ready."; }
                catch (AlreadyConnectedException) { error = "You are already connected."; }
                catch (Exception ex) { error = ex.Message; }
            }

            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                    dispatcher.Invoke((Action)Run);
                else if (_uiContext != null && SynchronizationContext.Current != _uiContext)
                    _uiContext.Send(_ => Run(), null);
                else
                    Run();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return error;
        }

        private List<Controller> ControllersByDistance()
        {
            List<Controller> list;
            lock (_lock) list = _controllers.Values.ToList();
            var me = _sim?.State;
            if (me.HasValue)
                return list.OrderBy(c => DistanceNm(me.Value.Latitude, me.Value.Longitude, c.Lat, c.Lon)).ToList();
            return list.OrderBy(c => c.Callsign).ToList();
        }

        /// <summary>vPilot frequencies may come as 118500 (kHz), 18500 (no leading 1) or Hz.</summary>
        private static int NormalizeKhz(int f)
        {
            while (f >= 1000000) f /= 1000;
            if (f > 0 && f < 100000) f += 100000;
            return f;
        }

        private static string FormatKhz(int khz)
            => (khz / 1000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

        private static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 3440.065;
            double ToRad(double d) => d * Math.PI / 180;
            var dLat = ToRad(lat2 - lat1);
            var dLon = ToRad(lon2 - lon1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        private void Debug(string text)
        {
            void Post()
            {
                try { _broker?.PostDebugMessage("[vPilot Relay] " + text); } catch { /* ignore */ }
            }
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                    dispatcher.BeginInvoke((Action)Post);
                else if (_uiContext != null && SynchronizationContext.Current != _uiContext)
                    _uiContext.Post(_ => Post(), null);
                else
                    Post();
            }
            catch { /* ignore */ }
        }
    }
}
