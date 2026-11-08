using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VpilotRelay
{
    internal sealed class IncomingMessage
    {
        public long ChatId;
        public long MessageId;
        public long? ReplyToMessageId;
        public string Text;

        // Set when this is an inline-button press instead of a typed message.
        public string CallbackId;
        public string CallbackData;
    }

    /// <summary>Inline keyboard builder: rows of (label, callback data).</summary>
    internal sealed class Keyboard
    {
        private readonly JArray _rows = new JArray();

        public Keyboard Row(params (string Label, string Data)[] buttons)
        {
            var row = new JArray();
            foreach (var b in buttons)
                row.Add(new JObject { ["text"] = b.Label, ["callback_data"] = b.Data });
            if (row.Count > 0) _rows.Add(row);
            return this;
        }

        public string ToJson() => new JObject { ["inline_keyboard"] = _rows }.ToString(Formatting.None);
    }

    internal sealed class TelegramClient : IDisposable
    {
        private const int PollTimeoutSeconds = 50;
        public const int MaxTextLength = 4000;

        private readonly string _baseUrl;
        private readonly HttpClient _http;
        private readonly Action<string> _log;
        private CancellationTokenSource _cts;
        private long _offset;

        public TelegramClient(string botToken, Action<string> log)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _baseUrl = "https://api.telegram.org/bot" + botToken + "/";
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(PollTimeoutSeconds + 20) };
            _log = log;
        }

        /// <summary>Sends a text message. Returns Telegram's message_id, or null on failure.</summary>
        public async Task<long?> SendMessageAsync(long chatId, string text, long? replyTo = null,
            string replyMarkup = null, bool silent = false)
        {
            var form = new Dictionary<string, string>
            {
                ["chat_id"] = chatId.ToString(),
                ["text"] = Truncate(text),
                ["disable_web_page_preview"] = "true",
            };
            if (replyTo.HasValue)
            {
                form["reply_to_message_id"] = replyTo.Value.ToString();
                form["allow_sending_without_reply"] = "true";
            }
            if (replyMarkup != null) form["reply_markup"] = replyMarkup;
            if (silent) form["disable_notification"] = "true";

            var result = await CallAsync("sendMessage", form).ConfigureAwait(false);
            return result?.Value<long>("message_id");
        }

        public Task EditMessageAsync(long chatId, long messageId, string text, string replyMarkup = null)
        {
            var form = new Dictionary<string, string>
            {
                ["chat_id"] = chatId.ToString(),
                ["message_id"] = messageId.ToString(),
                ["text"] = Truncate(text),
                ["disable_web_page_preview"] = "true",
            };
            if (replyMarkup != null) form["reply_markup"] = replyMarkup;
            return CallAsync("editMessageText", form);
        }

        public Task AnswerCallbackAsync(string callbackId, string text = null)
        {
            var form = new Dictionary<string, string> { ["callback_query_id"] = callbackId };
            if (text != null) form["text"] = text;
            return CallAsync("answerCallbackQuery", form);
        }

        public Task SetCommandsAsync(IEnumerable<(string Command, string Description)> commands)
        {
            var arr = new JArray();
            foreach (var c in commands)
                arr.Add(new JObject { ["command"] = c.Command, ["description"] = c.Description });
            return CallAsync("setMyCommands", new Dictionary<string, string> { ["commands"] = arr.ToString(Formatting.None) });
        }

        private async Task<JToken> CallAsync(string method, Dictionary<string, string> form)
        {
            try
            {
                using (var resp = await _http.PostAsync(_baseUrl + method, new FormUrlEncodedContent(form)).ConfigureAwait(false))
                {
                    var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                    if (json.Value<bool>("ok"))
                        return json["result"];
                    var desc = json.Value<string>("description") ?? "";
                    if (!desc.Contains("message is not modified"))
                        _log("Telegram " + method + " error: " + desc);
                }
            }
            catch (Exception ex)
            {
                _log("Telegram " + method + " error: " + ex.Message);
            }
            return null;
        }

        private static string Truncate(string text)
            => text == null ? "" : text.Length <= MaxTextLength ? text : text.Substring(0, MaxTextLength) + "…";

        public void StartPolling(Func<IncomingMessage, Task> onMessage)
        {
            Stop();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => PollLoop(onMessage, token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts = null;
        }

        private async Task PollLoop(Func<IncomingMessage, Task> onMessage, CancellationToken token)
        {
            // Skip messages sent while vPilot was closed, so stale replies are not sent to controllers.
            await DropPendingUpdates(token).ConfigureAwait(false);

            var backoff = 1;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var url = _baseUrl + "getUpdates?timeout=" + PollTimeoutSeconds + "&offset=" + _offset +
                              "&allowed_updates=" + Uri.EscapeDataString("[\"message\",\"callback_query\"]");
                    using (var resp = await _http.GetAsync(url, token).ConfigureAwait(false))
                    {
                        var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                        if (!json.Value<bool>("ok"))
                            throw new Exception(json.Value<string>("description"));

                        foreach (var update in (JArray)json["result"])
                        {
                            _offset = update.Value<long>("update_id") + 1;
                            var incoming = Parse(update);
                            if (incoming == null) continue;
                            try { await onMessage(incoming).ConfigureAwait(false); }
                            catch (Exception ex) { _log("Message handling error: " + ex.Message); }
                        }
                    }
                    backoff = 1;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log("Telegram getUpdates error: " + ex.Message + " (retrying in " + backoff + " s)");
                    try { await Task.Delay(TimeSpan.FromSeconds(backoff), token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    backoff = Math.Min(backoff * 2, 60);
                }
            }
        }

        private static IncomingMessage Parse(JToken update)
        {
            var cb = update["callback_query"];
            if (cb != null)
            {
                var cbMsg = cb["message"];
                if (cbMsg == null) return null;
                return new IncomingMessage
                {
                    ChatId = cbMsg["chat"].Value<long>("id"),
                    MessageId = cbMsg.Value<long>("message_id"),
                    CallbackId = cb.Value<string>("id"),
                    CallbackData = cb.Value<string>("data") ?? "",
                };
            }

            var msg = update["message"];
            var text = msg?.Value<string>("text");
            if (msg == null || string.IsNullOrWhiteSpace(text)) return null;
            return new IncomingMessage
            {
                ChatId = msg["chat"].Value<long>("id"),
                MessageId = msg.Value<long>("message_id"),
                ReplyToMessageId = msg["reply_to_message"]?.Value<long>("message_id"),
                Text = text.Trim(),
            };
        }

        private async Task DropPendingUpdates(CancellationToken token)
        {
            try
            {
                using (var resp = await _http.GetAsync(_baseUrl + "getUpdates?offset=-1&timeout=0", token).ConfigureAwait(false))
                {
                    var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                    if (json["result"] is JArray arr && arr.Count > 0)
                        _offset = arr[arr.Count - 1].Value<long>("update_id") + 1;
                }
            }
            catch (Exception ex)
            {
                _log("Telegram startup error: " + ex.Message);
            }
        }

        public void Dispose()
        {
            Stop();
            _http.Dispose();
        }
    }
}
