using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace VpilotRelay
{
    internal sealed class Settings
    {
        public const string IniFileName = "vPilotRelay.ini";

        public string IniPath { get; private set; }
        public string BotToken { get; private set; } = "";
        public long? ChatId { get; private set; }

        public bool RelayPrivate { get; private set; } = true;
        public bool RelayRadio { get; private set; } = true;
        public bool RadioAll { get; private set; } // false = only messages mentioning our callsign
        public bool RelaySelcal { get; private set; } = true;
        public bool RelayBroadcast { get; private set; } = true;
        public bool RelayConnection { get; private set; } = true;

        public const int MaxRecentConnects = 3;
        private string RecentPath => Path.Combine(Path.GetDirectoryName(IniPath) ?? "", "vPilotRelay.recent");

        /// <summary>
        /// Our own list of recent connections, newest first. vPilot only updates its LastConnectInfo when connecting
        /// from its own dialog, not for plugin connects, so we keep this list ourselves.
        /// </summary>
        public List<(string Callsign, string TypeCode, string Selcal)> LoadRecentConnects()
        {
            var list = new List<(string, string, string)>();
            try
            {
                if (!File.Exists(RecentPath)) return list;
                foreach (var line in File.ReadAllLines(RecentPath))
                {
                    var p = line.Trim().Split('|');
                    if (p.Length >= 2 && p[0].Length > 0 && p[1].Length > 0)
                        list.Add((p[0], p[1], p.Length > 2 ? p[2] : ""));
                }
            }
            catch { /* ignore */ }
            return list;
        }

        public void SaveRecentConnects(IEnumerable<(string Callsign, string TypeCode, string Selcal)> list)
        {
            try
            {
                var lines = new List<string>();
                foreach (var c in list) lines.Add(c.Callsign + "|" + c.TypeCode + "|" + (c.Selcal ?? ""));
                File.WriteAllLines(RecentPath, lines);
            }
            catch { /* ignore */ }
        }

        /// <summary>vPilot's last connection from its own dialog (vPilotConfig.xml → LastConnectInfo), or null.</summary>
        public (string Callsign, string TypeCode, string Selcal)? ReadLastConnectInfo()
        {
            try
            {
                var pluginsDir = Path.GetDirectoryName(IniPath);
                var path = Path.Combine(Path.GetDirectoryName(pluginsDir) ?? "", "vPilotConfig.xml");
                if (!File.Exists(path)) return null;
                var el = System.Xml.Linq.XDocument.Load(path).Root?.Element("LastConnectInfo");
                if (el == null || ((string)el.Attribute("ObserverMode"))?.ToLowerInvariant() == "true") return null;
                var cs = ((string)el.Attribute("Callsign") ?? "").Trim();
                var type = ((string)el.Attribute("TypeCode") ?? "").Trim();
                if (cs.Length == 0 || type.Length == 0) return null;
                return (cs, type, ((string)el.Attribute("SelCalCode") ?? "").Trim());
            }
            catch
            {
                return null;
            }
        }

        public static Settings Load()
        {
            var s = new Settings();
            s.IniPath = Path.Combine(GetPluginsDir(), IniFileName);
            if (!File.Exists(s.IniPath))
                return s;

            var ini = ParseIni(File.ReadAllLines(s.IniPath));
            s.BotToken = Get(ini, "Telegram", "BotToken", "").Trim();
            if (long.TryParse(Get(ini, "Telegram", "ChatId", "").Trim(), out var chatId))
                s.ChatId = chatId;

            s.RelayPrivate = GetBool(ini, "Relay", "Private", true);
            s.RelayRadio = GetBool(ini, "Relay", "Radio", true);
            s.RadioAll = Get(ini, "Relay", "RadioMode", "mentions").Trim()
                .Equals("all", StringComparison.OrdinalIgnoreCase);
            s.RelaySelcal = GetBool(ini, "Relay", "Selcal", true);
            s.RelayBroadcast = GetBool(ini, "Relay", "Broadcast", true);
            s.RelayConnection = GetBool(ini, "Relay", "Connection", true);
            return s;
        }

        private static string GetPluginsDir()
        {
            // Prefer the folder this DLL was loaded from (it lives in vPilot\Plugins).
            try
            {
                var dir = Path.GetDirectoryName(typeof(Settings).Assembly.Location);
                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, IniFileName)))
                    return dir;
            }
            catch { /* ignore */ }

            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\vPilot"))
            {
                var install = key?.GetValue("Install_Dir") as string;
                if (!string.IsNullOrEmpty(install))
                    return Path.Combine(install, "Plugins");
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vPilot", "Plugins");
        }

        private static Dictionary<string, Dictionary<string, string>> ParseIni(IEnumerable<string> lines)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var section = "";
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                    continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                if (!result.TryGetValue(section, out var dict))
                    result[section] = dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                dict[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return result;
        }

        private static string Get(Dictionary<string, Dictionary<string, string>> ini, string section, string key, string def)
            => ini.TryGetValue(section, out var d) && d.TryGetValue(key, out var v) ? v : def;

        private static bool GetBool(Dictionary<string, Dictionary<string, string>> ini, string section, string key, bool def)
        {
            var v = Get(ini, section, key, null);
            if (v == null) return def;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ||
                   v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
