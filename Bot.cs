using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VpilotRelay
{
    /// <summary>Telegram side: /menu, inline buttons, commands and prompts.</summary>
    public partial class Main
    {
        private const int MaxActions = 300;
        private static readonly TimeSpan PromptLifetime = TimeSpan.FromMinutes(5);
        private static readonly Regex CallsignRx = new Regex(@"^[A-Z0-9_\-]{2,15}$");
        private static readonly Regex SquawkRx = new Regex(@"^[0-7]{4}$");
        private static readonly Regex PilotCallsignRx = new Regex(@"^[A-Z0-9]{2,10}$");
        private static readonly Regex TypeCodeRx = new Regex(@"^[A-Z0-9]{2,4}$");
        private const int UnicomKhz = 122800;
        private const string SelcalLetters = "ABCDEFGHJKLMPQRS"; // I, N, O are not used in SELCAL

        private static readonly (string, string)[] BotCommands =
        {
            ("menu", "Main menu"),
            ("connect", "Connect to VATSIM: /connect BAW123 B738 AB-CD"),
            ("pm", "Private message: /pm EGLL_TWR message"),
            ("radio", "Send on frequency: /radio message"),
            ("freq", "Tune a radio: /freq 118.100 [1|2] [s]"),
            ("atc", "Online ATC nearby"),
            ("metar", "METAR: /metar EGLL"),
            ("atis", "Controller info: /atis EGLL_ATIS"),
            ("squawk", "Set squawk: /squawk 1234"),
            ("ident", "Squawk ident"),
            ("status", "Status"),
            ("mute", "Toggle silent mode"),
            ("help", "Commands"),
        };

        // Telegram message_id -> what a reply to that message does ("pm:CALLSIGN", "radio", "freq", ...).
        private readonly Dictionary<long, string> _actions = new Dictionary<long, string>();
        private readonly Queue<long> _actionOrder = new Queue<long>();
        private string _lastPrompt;
        private DateTime _lastPromptUntil;

        private long ChatId => _settings.ChatId.Value;

        private async Task OnTelegramMessage(IncomingMessage msg)
        {
            if (!_settings.ChatId.HasValue)
            {
                await _telegram.SendMessageAsync(msg.ChatId,
                    "Your Chat ID: " + msg.ChatId + "\nPut it on the ChatId= line of vPilotRelay.ini and restart vPilot.")
                    .ConfigureAwait(false);
                return;
            }

            // Only the owner's chat may control vPilot.
            if (msg.ChatId != ChatId)
                return;

            if (msg.CallbackId != null)
            {
                await _telegram.AnswerCallbackAsync(msg.CallbackId).ConfigureAwait(false);
                await HandleButton(msg.CallbackData, msg.MessageId).ConfigureAwait(false);
                return;
            }

            if (msg.Text.StartsWith("/"))
            {
                await HandleCommand(msg.Text, msg.MessageId).ConfigureAwait(false);
                return;
            }

            string action = null;
            if (msg.ReplyToMessageId.HasValue)
                lock (_lock) _actions.TryGetValue(msg.ReplyToMessageId.Value, out action);
            if (action == null && _lastPrompt != null && DateTime.UtcNow < _lastPromptUntil)
                action = _lastPrompt;
            _lastPrompt = null;

            if (action == null)
            {
                await _telegram.SendMessageAsync(ChatId,
                    "I don't know where to send this. Reply to a ✉️ notification or pick from the menu:",
                    msg.MessageId, MainMenuKeyboard()).ConfigureAwait(false);
                return;
            }

            await RunAction(action, msg.Text, msg.MessageId).ConfigureAwait(false);
        }

        // ---------- typed commands ----------

        private async Task HandleCommand(string text, long messageId)
        {
            var parts = text.Split(new[] { ' ' }, 2);
            var cmd = parts[0].Split('@')[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (cmd)
            {
                case "/start":
                case "/menu":
                    await SendMainMenu().ConfigureAwait(false);
                    break;
                case "/connect":
                    if (arg.Length == 0) await ShowConnect(null).ConfigureAwait(false);
                    else await RunAction("con", arg, messageId).ConfigureAwait(false);
                    break;
                case "/help":
                    await Reply(HelpText, messageId).ConfigureAwait(false);
                    break;
                case "/pm":
                    if (arg.Length == 0) await ShowPmList(null).ConfigureAwait(false);
                    else await RunAction("pmfree", arg, messageId).ConfigureAwait(false);
                    break;
                case "/radio":
                    if (arg.Length == 0) await Prompt("📡 Type the message to send on frequency:", "radio", "message").ConfigureAwait(false);
                    else await RunAction("radio", arg, messageId).ConfigureAwait(false);
                    break;
                case "/freq":
                    if (arg.Length == 0) await ShowFreqList(null).ConfigureAwait(false);
                    else await RunAction("freq", arg, messageId).ConfigureAwait(false);
                    break;
                case "/atc":
                    await Reply(AtcText(), messageId).ConfigureAwait(false);
                    break;
                case "/metar":
                    if (arg.Length == 0) await Prompt("🌤 Type the airport ICAO code (e.g. EGLL):", "metar", "EGLL").ConfigureAwait(false);
                    else await RunAction("metar", arg, messageId).ConfigureAwait(false);
                    break;
                case "/atis":
                    if (arg.Length == 0) await ShowAtisList(null).ConfigureAwait(false);
                    else await RunAction("atis", arg, messageId).ConfigureAwait(false);
                    break;
                case "/squawk":
                    if (arg.Length == 0) await Prompt("🔢 Type the squawk code (e.g. 7000):", "squawk", "7000").ConfigureAwait(false);
                    else await RunAction("squawk", arg, messageId).ConfigureAwait(false);
                    break;
                case "/ident":
                    await Ident(messageId).ConfigureAwait(false);
                    break;
                case "/status":
                    await Reply(StatusText(), messageId).ConfigureAwait(false);
                    break;
                case "/mute":
                    _muted = !_muted;
                    await Reply(_muted ? "🔕 Silent mode on (private messages still make a sound)." : "🔔 Silent mode off.", messageId).ConfigureAwait(false);
                    break;
                default:
                    await Reply("Unknown command. Send /menu or /help.", messageId).ConfigureAwait(false);
                    break;
            }
        }

        // ---------- inline buttons ----------

        private async Task HandleButton(string data, long menuMessageId)
        {
            var p = data.Split(':');
            switch (p[0])
            {
                case "m":
                    // The page the button was on stays as it is; the menu comes as a new message.
                    await SendMainMenu().ConfigureAwait(false);
                    break;
                case "x":
                    // Cancelling a confirmation: drop its buttons so it can't be confirmed later, then show a new menu.
                    await _telegram.EditMessageAsync(ChatId, menuMessageId, "❎ Cancelled.").ConfigureAwait(false);
                    await SendMainMenu().ConfigureAwait(false);
                    break;
                case "pml":
                    await ShowPmList(menuMessageId).ConfigureAwait(false);
                    break;
                case "pm":
                    await Prompt("✉️ Type your message to " + p[1] + ":", "pm:" + p[1], "message").ConfigureAwait(false);
                    break;
                case "pmw":
                    await Prompt("✉️ Type the callsign and the message (e.g. EGLL_TWR request startup):", "pmfree", "CALLSIGN message").ConfigureAwait(false);
                    break;
                case "fql":
                    await ShowFreqList(menuMessageId).ConfigureAwait(false);
                    break;
                case "fs":
                    await Tune(p[1][0] == '2' ? 2 : 1, p[1].EndsWith("s"), int.Parse(p[2], CultureInfo.InvariantCulture), null).ConfigureAwait(false);
                    break;
                case "fqw":
                    await Prompt("📻 Type the frequency. Examples:\n118.100 → COM1 active\n121.800 s → COM1 standby\n124.350 2 → COM2 active", "freq", "118.100").ConfigureAwait(false);
                    break;
                case "rad":
                    await Prompt("📡 Type the message to send on frequency:", "radio", "message").ConfigureAwait(false);
                    break;
                case "atc":
                    await _telegram.EditMessageAsync(ChatId, menuMessageId, AtcText(), BackKeyboard()).ConfigureAwait(false);
                    break;
                case "met":
                    await Prompt("🌤 Type the airport ICAO code (e.g. EGLL):", "metar", "EGLL").ConfigureAwait(false);
                    break;
                case "atl":
                    await ShowAtisList(menuMessageId).ConfigureAwait(false);
                    break;
                case "at":
                    await RunAction("atis", p[1], null).ConfigureAwait(false);
                    break;
                case "sqk":
                    await Prompt("🔢 Type the squawk code (e.g. 7000):", "squawk", "7000").ConfigureAwait(false);
                    break;
                case "idt":
                    await Ident(null).ConfigureAwait(false);
                    break;
                case "sts":
                    await _telegram.EditMessageAsync(ChatId, menuMessageId, StatusText(),
                        new Keyboard().Row(("🔄 Refresh", "sts"), ("◀ Menu", "m")).ToJson()).ConfigureAwait(false);
                    break;
                case "mute":
                    _muted = !_muted;
                    await _telegram.EditMessageAsync(ChatId, menuMessageId, MainMenuText(), MainMenuKeyboard()).ConfigureAwait(false);
                    break;
                case "rmode":
                    _radioAll = !_radioAll;
                    await _telegram.EditMessageAsync(ChatId, menuMessageId, MainMenuText(), MainMenuKeyboard()).ConfigureAwait(false);
                    break;
                case "con":
                    await ShowConnect(menuMessageId).ConfigureAwait(false);
                    break;
                case "conw":
                    await PromptConnectCallsign().ConfigureAwait(false);
                    break;
                case "con!":
                    await Connect(p[1], p[2], p.Length > 3 ? p[3] : "", menuMessageId).ConfigureAwait(false);
                    break;
                case "dsc":
                    await _telegram.EditMessageAsync(ChatId, menuMessageId,
                        "🔌 Disconnect from VATSIM?" + (_callsign != null ? " (" + _callsign + ")" : ""),
                        new Keyboard().Row(("✅ Yes, disconnect", "dsc!"), ("◀ Cancel", "x")).ToJson()).ConfigureAwait(false);
                    break;
                case "dsc!":
                    var err = OnVpilot(b => b.RequestDisconnect());
                    await _telegram.EditMessageAsync(ChatId, menuMessageId,
                        err == null ? "🔌 Disconnect requested." : "❌ " + err, BackKeyboard()).ConfigureAwait(false);
                    break;
            }
        }

        // ---------- actions (typed text after a prompt, a reply, or a command argument) ----------

        private async Task RunAction(string action, string text, long? messageId)
        {
            if (action.StartsWith("pm:"))
            {
                await SendPm(action.Substring(3), text, messageId).ConfigureAwait(false);
                return;
            }
            if (action == "con" || action.StartsWith("con:"))
            {
                await ConnectStep(action, text, messageId).ConfigureAwait(false);
                return;
            }

            switch (action)
            {
                case "pmfree":
                {
                    var parts = text.Split(new[] { ' ' }, 2);
                    var cs = parts[0].ToUpperInvariant();
                    if (!CallsignRx.IsMatch(cs))
                    {
                        await Reply("❌ Invalid callsign: " + parts[0], messageId).ConfigureAwait(false);
                        return;
                    }
                    if (parts.Length < 2 || parts[1].Trim().Length == 0)
                        await Prompt("✉️ Type your message to " + cs + ":", "pm:" + cs, "message").ConfigureAwait(false);
                    else
                        await SendPm(cs, parts[1].Trim(), messageId).ConfigureAwait(false);
                    return;
                }
                case "radio":
                {
                    var err = OnVpilot(b => b.SendRadioMessage(text));
                    if (err != null)
                    {
                        await Reply("❌ Could not send on frequency: " + err, messageId).ConfigureAwait(false);
                        return;
                    }
                    var id = await Reply("✅ 📡 Sent on frequency.", messageId).ConfigureAwait(false);
                    if (id.HasValue) RememberAction(id.Value, "radio");
                    return;
                }
                case "freq":
                {
                    if (!TryParseFreq(text, out var khz, out var com, out var standby))
                    {
                        await Reply("❌ Could not read that frequency. Examples: 118.100, 121.800 s, 124.350 2", messageId).ConfigureAwait(false);
                        return;
                    }
                    await Tune(com, standby, khz, messageId).ConfigureAwait(false);
                    return;
                }
                case "metar":
                {
                    var icao = text.Trim().ToUpperInvariant();
                    if (!Regex.IsMatch(icao, "^[A-Z0-9]{3,4}$"))
                    {
                        await Reply("❌ Invalid ICAO code: " + text, messageId).ConfigureAwait(false);
                        return;
                    }
                    _metarUntil = DateTime.UtcNow.AddSeconds(30);
                    var err = OnVpilot(b => b.RequestMetar(icao));
                    await Reply(err == null ? "🌤 " + icao + " METAR requested…" : "❌ " + err, messageId).ConfigureAwait(false);
                    return;
                }
                case "atis":
                {
                    var cs = text.Trim().ToUpperInvariant();
                    if (!CallsignRx.IsMatch(cs))
                    {
                        await Reply("❌ Invalid callsign: " + text, messageId).ConfigureAwait(false);
                        return;
                    }
                    _atisFor = cs;
                    _atisUntil = DateTime.UtcNow.AddSeconds(30);
                    var err = OnVpilot(b => b.RequestAtis(cs));
                    await Reply(err == null ? "ℹ️ " + cs + " info requested…" : "❌ " + err, messageId).ConfigureAwait(false);
                    return;
                }
                case "squawk":
                {
                    var code = text.Trim();
                    if (!SquawkRx.IsMatch(code))
                    {
                        await Reply("❌ A squawk is 4 digits from 0 to 7 (e.g. 7000).", messageId).ConfigureAwait(false);
                        return;
                    }
                    var err = _sim.SetSquawk(code);
                    if (err != null)
                    {
                        await Reply("❌ " + err, messageId).ConfigureAwait(false);
                        return;
                    }
                    await Task.Delay(1500).ConfigureAwait(false);
                    var st = _sim.State;
                    var now = st.HasValue ? ((int)st.Value.Transponder).ToString("X4") : null;
                    await Reply(now == null || now == code
                        ? "✅ Squawk " + code
                        : "⚠️ Squawk sent, but the simulator shows " + now + ". This aircraft's transponder may ignore external commands.",
                        messageId).ConfigureAwait(false);
                    return;
                }
            }
        }

        private async Task SendPm(string callsign, string text, long? messageId)
        {
            var err = OnVpilot(b => b.SendPrivateMessage(callsign, text));
            if (err != null)
            {
                await Reply("❌ Not sent (" + callsign + "): " + err, messageId).ConfigureAwait(false);
                return;
            }
            var id = await Reply("✅ Sent → " + callsign, messageId).ConfigureAwait(false);
            // Replying to the confirmation (or to your own message) continues the conversation.
            if (id.HasValue) RememberAction(id.Value, "pm:" + callsign);
            if (messageId.HasValue) RememberAction(messageId.Value, "pm:" + callsign);
        }

        private async Task Tune(int com, bool standby, int khz, long? messageId)
        {
            var label = "COM" + com + (standby ? " standby" : " active") + " " + FormatKhz(khz);
            var err = _sim.SetCom(com, standby, khz);
            if (err != null)
            {
                await Reply("❌ " + label + " failed: " + err, messageId).ConfigureAwait(false);
                return;
            }

            await Task.Delay(1500).ConfigureAwait(false);
            var st = _sim.State;
            if (st.HasValue)
            {
                var s = st.Value;
                var mhz = com == 2 ? (standby ? s.Com2Standby : s.Com2Active) : (standby ? s.Com1Standby : s.Com1Active);
                var actual = (int)Math.Round(mhz * 1000);
                if (actual != khz)
                {
                    await Reply("⚠️ " + label + " sent, but the simulator shows " + FormatKhz(actual) +
                                ". This aircraft's radio may ignore external frequency commands.", messageId).ConfigureAwait(false);
                    return;
                }
            }
            await Reply("✅ " + label, messageId).ConfigureAwait(false);
        }

        // ---------- VATSIM connect ----------

        private async Task ShowConnect(long? editMessageId)
        {
            if (_callsign != null)
            {
                await ShowOrEdit(editMessageId, "🟢 Already connected: " + _callsign, BackKeyboard()).ConfigureAwait(false);
                return;
            }

            var recent = RecentConnects();
            if (recent.Count == 0)
            {
                await PromptConnectCallsign().ConfigureAwait(false);
                return;
            }

            var kb = new Keyboard();
            foreach (var r in recent)
                kb.Row(("🔁 " + ConnectSummary(r.Callsign, r.TypeCode, r.Selcal), ConnectData(r.Callsign, r.TypeCode, r.Selcal)));
            kb.Row(("✍️ Enter new details", "conw")).Row(("◀ Menu", "m"));
            await ShowOrEdit(editMessageId,
                "🟢 Connect to VATSIM\n\nTap one of your recent connections (newest first) or enter new details.",
                kb.ToJson()).ConfigureAwait(false);
        }

        private Task PromptConnectCallsign()
            => Prompt("🟢 Connect to VATSIM\n\nType your callsign (e.g. BAW123).\nYou can also send everything in one line: BAW123 B738 AB-CD",
                "con", "BAW123");

        private Task PromptConnectSelcal(string cs, string type)
            => Prompt("🔔 " + cs + " · " + type + " — type your SELCAL code (e.g. AB-CD).\nNo SELCAL? Send -",
                "con:" + cs + ":" + type, "AB-CD");

        /// <summary>
        /// Step-by-step connect wizard. action = "con", "con:CALLSIGN" or "con:CALLSIGN:TYPE" (what is already known);
        /// text may contain one or more of the missing fields in order (callsign, type, SELCAL).
        /// </summary>
        private async Task ConnectStep(string action, string text, long? messageId)
        {
            var known = action.Split(':').Skip(1).ToList();
            var answeringSelcal = known.Count == 2;
            var tokens = text.Trim().ToUpperInvariant()
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries).ToList();

            if (known.Count == 0)
            {
                if (tokens.Count == 0 || !PilotCallsignRx.IsMatch(tokens[0]))
                {
                    await Reply("❌ Invalid callsign: " + text.Trim() + " (2-10 letters/digits, e.g. BAW123)", messageId).ConfigureAwait(false);
                    await PromptConnectCallsign().ConfigureAwait(false);
                    return;
                }
                known.Add(tokens[0]);
                tokens.RemoveAt(0);
            }

            var cs = known[0];
            if (known.Count == 1 && tokens.Count > 0)
            {
                if (TypeCodeRx.IsMatch(tokens[0]))
                {
                    known.Add(tokens[0]);
                    tokens.RemoveAt(0);
                }
                else
                {
                    await Reply("❌ Invalid aircraft type: " + tokens[0] + " (ICAO type code, e.g. B738, A20N, C172)", messageId).ConfigureAwait(false);
                }
            }
            if (known.Count == 1)
            {
                await Prompt("✈️ " + cs + " — type the ICAO aircraft type (e.g. B738, A20N, C172):", "con:" + cs, "B738").ConfigureAwait(false);
                return;
            }

            var type = known[1];
            if (tokens.Count == 0 && !answeringSelcal)
            {
                await PromptConnectSelcal(cs, type).ConfigureAwait(false);
                return;
            }

            if (!TryParseSelcal(string.Join("", tokens), out var selcal))
            {
                await Reply("❌ Invalid SELCAL: " + string.Join(" ", tokens) +
                            "\nIt must be 4 letters, only from " + SelcalLetters + " (e.g. AB-CD).", messageId).ConfigureAwait(false);
                await PromptConnectSelcal(cs, type).ConfigureAwait(false);
                return;
            }

            var kb = new Keyboard().Row(("✅ Connect", ConnectData(cs, type, selcal)), ("◀ Cancel", "x"));
            await _telegram.SendMessageAsync(ChatId,
                "🟢 Connect to VATSIM with these details?\n\nCallsign: " + cs + "\nAircraft: " + type +
                "\nSELCAL: " + (selcal.Length == 0 ? "none" : selcal),
                messageId, kb.ToJson()).ConfigureAwait(false);
        }

        private async Task Connect(string callsign, string typeCode, string selcal, long menuMessageId)
        {
            if (_callsign != null)
            {
                await _telegram.EditMessageAsync(ChatId, menuMessageId, "🟢 Already connected: " + _callsign, BackKeyboard()).ConfigureAwait(false);
                return;
            }
            // vPilot calls selcalCode.ToUpper() unconditionally, so "no SELCAL" must be "" — null throws NullReferenceException.
            var err = OnVpilot(b => b.RequestConnect(callsign, typeCode, selcal ?? ""));
            await _telegram.EditMessageAsync(ChatId, menuMessageId,
                err == null
                    ? "⏳ Connection requested: " + ConnectSummary(callsign, typeCode, selcal) +
                      (_settings.RelayConnection ? "\nI'll let you know once you're connected." : "")
                    : "❌ Could not connect: " + err,
                BackKeyboard()).ConfigureAwait(false);
        }

        private static string ConnectSummary(string callsign, string typeCode, string selcal)
            => callsign + " · " + typeCode + (string.IsNullOrEmpty(selcal) ? "" : " · " + selcal);

        private static string ConnectData(string callsign, string typeCode, string selcal)
            => "con!:" + callsign + ":" + typeCode + ":" + (selcal ?? "");

        /// <summary>"ABCD", "AB-CD", "ab cd" → "AB-CD"; "-", "none", "no" → "" (no SELCAL).</summary>
        private static bool TryParseSelcal(string text, out string selcal)
        {
            selcal = "";
            var t = text.Trim().ToUpperInvariant();
            if (t.Length == 0 || t == "-" || t == "NO" || t == "NONE")
                return true;
            var letters = new string(t.Where(char.IsLetter).ToArray());
            if (letters.Length != 4 || t.Any(char.IsDigit) || letters.Any(ch => SelcalLetters.IndexOf(ch) < 0))
                return false;
            selcal = letters.Substring(0, 2) + "-" + letters.Substring(2);
            return true;
        }

        private async Task Ident(long? messageId)
        {
            var err = OnVpilot(b => b.SquawkIdent());
            await Reply(err == null ? "🆔 Ident sent." : "❌ " + err, messageId).ConfigureAwait(false);
        }

        // ---------- menus ----------

        private Task SendMainMenu() => _telegram.SendMessageAsync(ChatId, MainMenuText(), null, MainMenuKeyboard());

        private string MainMenuText()
        {
            var sb = new StringBuilder("✈️ vPilot Relay\n");
            sb.Append(_callsign != null ? "🟢 Connected: " + _callsign : "🔴 Not connected to VATSIM");
            int count;
            lock (_lock) count = _controllers.Count;
            if (_callsign != null) sb.Append(" · ").Append(count).Append(" ATC nearby");
            sb.Append("\n\nWhat would you like to do?");
            return sb.ToString();
        }

        private string MainMenuKeyboard()
            => new Keyboard()
                .Row(("✉️ Private message", "pml"), ("📻 Tune radio", "fql"))
                .Row(("📡 Send on frequency", "rad"), ("🗼 Online ATC", "atc"))
                .Row(("🌤 METAR", "met"), ("ℹ️ ATIS / info", "atl"))
                .Row(("🔢 Squawk", "sqk"), ("🆔 Ident", "idt"))
                .Row(("📊 Status", "sts"), (_muted ? "🔕 Silent: on" : "🔔 Silent: off", "mute"))
                .Row((_radioAll ? "📻 Radio alerts: all" : "📻 Radio alerts: mentions only", "rmode"))
                .Row(_callsign != null ? ("🔌 Disconnect", "dsc") : ("🟢 Connect to VATSIM", "con"))
                .ToJson();

        private static string BackKeyboard() => new Keyboard().Row(("◀ Menu", "m")).ToJson();

        private async Task ShowPmList(long? editMessageId)
        {
            var kb = new Keyboard();
            AddControllerButtons(kb, c => (c.Callsign, "pm:" + c.Callsign));
            kb.Row(("✍️ Type a callsign", "pmw")).Row(("◀ Menu", "m"));
            await ShowOrEdit(editMessageId, "✉️ Who do you want to message?" + NoAtcHint(), kb.ToJson()).ConfigureAwait(false);
        }

        private async Task ShowFreqList(long? editMessageId)
        {
            // Every station button tunes COM1 active directly.
            var kb = new Keyboard().Row(("📢 UNICOM " + FormatKhz(UnicomKhz), "fs:1a:" + UnicomKhz.ToString(CultureInfo.InvariantCulture)));
            AddControllerButtons(kb,
                c => (c.Callsign + " " + FormatKhz(c.Khz), "fs:1a:" + c.Khz.ToString(CultureInfo.InvariantCulture)),
                tunableOnly: true);
            kb.Row(("✍️ Type a frequency", "fqw")).Row(("◀ Menu", "m"));
            var sim = _sim.EnsureConnected() == null ? CurrentComText() : "\n⚠️ Could not connect to the simulator.";
            await ShowOrEdit(editMessageId, "📻 Which station? The frequency goes to COM1 active." + sim + NoAtcHint(),
                kb.ToJson()).ConfigureAwait(false);
        }

        private async Task ShowAtisList(long? editMessageId)
        {
            var kb = new Keyboard();
            var list = ControllersByDistance()
                .OrderBy(c => c.Callsign.EndsWith("_ATIS", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            AddButtons(kb, list, c => (c.Callsign, "at:" + c.Callsign));
            kb.Row(("◀ Menu", "m"));
            await ShowOrEdit(editMessageId, "ℹ️ Which station's ATIS / info do you want?" + NoAtcHint(), kb.ToJson()).ConfigureAwait(false);
        }

        private void AddControllerButtons(Keyboard kb, Func<Controller, (string, string)> button, bool tunableOnly = false)
        {
            var list = ControllersByDistance();
            if (tunableOnly) list = list.Where(c => c.Khz >= 118000 && c.Khz <= 136990).ToList();
            AddButtons(kb, list, button);
        }

        private static void AddButtons(Keyboard kb, List<Controller> list, Func<Controller, (string, string)> button)
        {
            foreach (var pair in list.Take(30).Select((c, i) => (c, i)).GroupBy(x => x.i / 2))
                kb.Row(pair.Select(x => button(x.c)).ToArray());
        }

        private string NoAtcHint()
        {
            lock (_lock)
                if (_controllers.Count > 0) return "";
            return _callsign == null
                ? "\n\n(You are not connected to VATSIM.)"
                : "\n\n(No online ATC in range.)";
        }

        private async Task ShowOrEdit(long? editMessageId, string text, string keyboard)
        {
            if (editMessageId.HasValue)
                await _telegram.EditMessageAsync(ChatId, editMessageId.Value, text, keyboard).ConfigureAwait(false);
            else
                await _telegram.SendMessageAsync(ChatId, text, null, keyboard).ConfigureAwait(false);
        }

        // ---------- texts ----------

        private string AtcText()
        {
            var list = ControllersByDistance();
            if (list.Count == 0)
                return "🗼 Online ATC" + NoAtcHint();
            var me = _sim.State;
            var sb = new StringBuilder("🗼 Online ATC (" + list.Count + ")\n\n");
            foreach (var c in list.Take(60))
            {
                sb.Append(c.Callsign).Append("  ").Append(FormatKhz(c.Khz));
                if (me.HasValue)
                    sb.Append("  ").Append(Math.Round(DistanceNm(me.Value.Latitude, me.Value.Longitude, c.Lat, c.Lon))).Append(" nm");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private string CurrentComText()
        {
            var st = _sim.State;
            if (!st.HasValue) return "";
            var s = st.Value;
            return "\n\nCOM1: " + FormatMhz(s.Com1Active) + " (stby " + FormatMhz(s.Com1Standby) + ")" +
                   "\nCOM2: " + FormatMhz(s.Com2Active) + " (stby " + FormatMhz(s.Com2Standby) + ")";
        }

        private string StatusText()
        {
            var sb = new StringBuilder("📊 Status\n\n");
            if (_callsign != null)
            {
                sb.Append("🟢 ").Append(_callsign);
                if (!string.IsNullOrEmpty(_typeCode)) sb.Append(" (").Append(_typeCode).Append(')');
                if (_connectedAt.HasValue)
                {
                    var d = DateTime.UtcNow - _connectedAt.Value;
                    sb.Append(" · ").Append((int)d.TotalHours).Append(" h ").Append(d.Minutes).Append(" min");
                }
            }
            else
            {
                sb.Append("🔴 Not connected to VATSIM");
            }

            _sim.EnsureConnected();
            var st = _sim.State;
            if (st.HasValue)
            {
                var s = st.Value;
                sb.Append("\n\n").Append(s.OnGround > 0.5 ? "🛬 On the ground" : "🛫 Airborne")
                  .Append("\nAltitude: ").Append(Math.Round(s.AltitudeFt).ToString("N0", CultureInfo.InvariantCulture)).Append(" ft")
                  .Append("\nIAS: ").Append(Math.Round(s.IasKt)).Append(" kt · GS: ").Append(Math.Round(s.GroundSpeedKt)).Append(" kt")
                  .Append("\nHeading: ").Append(((int)Math.Round(s.HeadingMag) + 360) % 360).Append("°")
                  .Append("\nSquawk: ").Append(((int)s.Transponder).ToString("X4"))
                  .Append(CurrentComText());
            }
            else
            {
                sb.Append("\n\n(No simulator data)");
            }

            int count;
            lock (_lock) count = _controllers.Count;
            sb.Append("\n\n🗼 ATC nearby: ").Append(count)
              .Append("\n").Append(_muted ? "🔕 Silent mode on" : "🔔 Silent mode off")
              .Append("\n📻 Radio alerts: ").Append(_radioAll ? "all" : "mentions only");
            return sb.ToString();
        }

        private const string HelpText =
            "📖 Commands\n\n" +
            "/menu — main menu (buttons)\n" +
            "/connect BAW123 B738 AB-CD — connect to VATSIM (SELCAL optional)\n" +
            "/pm EGLL_TWR message — private message\n" +
            "/radio message — send on the active TX frequency\n" +
            "/freq 118.100 — COM1 active\n" +
            "/freq 121.800 s — COM1 standby\n" +
            "/freq 124.350 2 — COM2 active\n" +
            "/atc — online ATC nearby\n" +
            "/metar EGLL — METAR\n" +
            "/atis EGLL_ATIS — ATIS / controller info\n" +
            "/squawk 1234 — transponder code\n" +
            "/ident — squawk ident\n" +
            "/status — aircraft and connection status\n" +
            "/mute — toggle notification sound\n\n" +
            "Reply to a ✉️ private message or 📻❗ (addressed to you) notification to answer directly.";

        // ---------- helpers ----------

        private Task<long?> Reply(string text, long? replyTo) => _telegram.SendMessageAsync(ChatId, text, replyTo);

        private async Task Prompt(string text, string action, string placeholder)
        {
            var markup = "{\"force_reply\":true,\"input_field_placeholder\":" +
                         Newtonsoft.Json.JsonConvert.ToString(placeholder) + "}";
            var id = await _telegram.SendMessageAsync(ChatId, text, null, markup).ConfigureAwait(false);
            if (id.HasValue) RememberAction(id.Value, action);
            _lastPrompt = action;
            _lastPromptUntil = DateTime.UtcNow + PromptLifetime;
        }

        private void RememberAction(long messageId, string action)
        {
            lock (_lock)
            {
                if (!_actions.ContainsKey(messageId)) _actionOrder.Enqueue(messageId);
                _actions[messageId] = action;
                while (_actionOrder.Count > MaxActions)
                    _actions.Remove(_actionOrder.Dequeue());
            }
        }

        private static bool TryParseFreq(string text, out int khz, out int com, out bool standby)
        {
            khz = 0; com = 1; standby = false;
            var parts = text.Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            foreach (var p in parts.Skip(1))
            {
                if (p == "2" || p == "com2") com = 2;
                else if (p == "1" || p == "com1") com = 1;
                else if (p == "s" || p == "stby" || p == "standby") standby = true;
                else return false;
            }
            if (!double.TryParse(parts[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return false;
            if (v >= 100000) v /= 1000; // 118100 -> 118.1
            khz = (int)Math.Round(v * 1000);
            return khz >= 118000 && khz <= 136990;
        }

        private static string FormatMhz(double mhz) => mhz.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
