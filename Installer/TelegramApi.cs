using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VpilotRelaySetup
{
    internal sealed class TelegramException : Exception
    {
        /// <summary>Telegram error_code (401 bad token, 409 someone else is polling...), 0 for network errors.</summary>
        public int Code { get; }
        public TelegramException(int code, string message) : base(message) { Code = code; }
    }

    internal sealed class BotInfo
    {
        public string Username;
        public string Name;
    }

    internal sealed class ChatUpdate
    {
        public long UpdateId;
        public long? ChatId;
        public string ChatType;
        public string FirstName;
        public string LastName;
        public string Username;

        public string DisplayName => string.IsNullOrEmpty(LastName) ? FirstName : FirstName + " " + LastName;
    }

    /// <summary>The few Bot API calls the wizard needs.</summary>
    internal static class TelegramApi
    {
        /// <summary>Bot token as BotFather prints it: "123456789:AAH...".</summary>
        public static readonly Regex TokenRx = new Regex(@"\d{5,12}:[A-Za-z0-9_-]{30,50}");

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        static TelegramApi()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public static async Task<BotInfo> GetMeAsync(string token, CancellationToken ct = default)
        {
            var r = await CallAsync(token, "getMe", null, ct).ConfigureAwait(false) as IDictionary;
            return new BotInfo { Username = Str(r, "username"), Name = Str(r, "first_name") };
        }

        public static async Task<List<ChatUpdate>> GetUpdatesAsync(string token, long offset, int timeoutSeconds, CancellationToken ct)
        {
            var args = new Dictionary<string, string>
            {
                ["offset"] = offset.ToString(),
                ["timeout"] = timeoutSeconds.ToString(),
            };
            var list = new List<ChatUpdate>();
            if (!(await CallAsync(token, "getUpdates", args, ct).ConfigureAwait(false) is IEnumerable arr))
                return list;

            foreach (var item in arr)
            {
                if (!(item is IDictionary u)) continue;
                var msg = (u["message"] ?? u["edited_message"] ?? (u["callback_query"] as IDictionary)?["message"]) as IDictionary;
                var from = ((u["callback_query"] as IDictionary)?["from"] ?? msg?["from"]) as IDictionary;
                var chat = msg?["chat"] as IDictionary;
                list.Add(new ChatUpdate
                {
                    UpdateId = Convert.ToInt64(u["update_id"]),
                    ChatId = chat?["id"] == null ? (long?)null : Convert.ToInt64(chat["id"]),
                    ChatType = Str(chat, "type"),
                    FirstName = Str(from, "first_name") ?? Str(chat, "first_name"),
                    LastName = Str(from, "last_name"),
                    Username = Str(from, "username"),
                });
            }
            return list;
        }

        public static Task SendMessageAsync(string token, long chatId, string text, CancellationToken ct = default)
            => CallAsync(token, "sendMessage", new Dictionary<string, string>
            {
                ["chat_id"] = chatId.ToString(),
                ["text"] = text,
            }, ct);

        private static async Task<object> CallAsync(string token, string method, Dictionary<string, string> args, CancellationToken ct)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await Http.PostAsync("https://api.telegram.org/bot" + token + "/" + method,
                    new FormUrlEncodedContent(args ?? new Dictionary<string, string>()), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                throw new TelegramException(0, "Telegram did not respond (timeout). Check your internet connection.");
            }
            catch (HttpRequestException)
            {
                throw new TelegramException(0, "Could not reach Telegram. Check your internet connection.");
            }

            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                IDictionary json;
                try { json = Json.DeserializeObject(body) as IDictionary; }
                catch { json = null; }
                if (json == null)
                    throw new TelegramException((int)resp.StatusCode, "Unexpected response from Telegram.");

                if (json["ok"] is bool ok && ok)
                    return json["result"];

                var code = json["error_code"] != null ? Convert.ToInt32(json["error_code"]) : (int)resp.StatusCode;
                throw new TelegramException(code, Str(json, "description") ?? "Telegram error (" + code + ").");
            }
        }

        private static string Str(IDictionary d, string key) => d?[key] as string;
    }
}
