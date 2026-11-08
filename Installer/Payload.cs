using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VpilotRelaySetup
{
    /// <summary>The embedded plugin files and how they are put into vPilot\Plugins.</summary>
    internal static class Payload
    {
        public const string DllFileName = "vPilotRelay.dll";
        public const string IniFileName = "vPilotRelay.ini";

        public static void CopyPlugin(string pluginsDir)
        {
            Directory.CreateDirectory(pluginsDir);
            var target = Path.Combine(pluginsDir, DllFileName);
            var temp = target + ".new";
            File.WriteAllBytes(temp, Read("Payload." + DllFileName));
            // Replace in one step so a locked/failed copy never leaves a half-written plugin behind.
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
        }

        /// <summary>Writes BotToken/ChatId, keeping every other setting (and comment) of an existing ini.</summary>
        public static void WriteSettings(string pluginsDir, string token, long chatId)
        {
            var path = Path.Combine(pluginsDir, IniFileName);
            var lines = File.Exists(path)
                ? File.ReadAllLines(path).ToList()
                : Encoding.UTF8.GetString(Read("Payload." + IniFileName)).TrimStart('﻿')
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

            IniFile.Set(lines, "Telegram", "BotToken", token);
            IniFile.Set(lines, "Telegram", "ChatId", chatId.ToString());
            File.WriteAllText(path, string.Join("\r\n", lines).TrimEnd() + "\r\n", new UTF8Encoding(false));
        }

        private static byte[] Read(string name)
        {
            using (var s = typeof(Payload).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException("Setup payload missing: " + name);
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }
    }

    /// <summary>Minimal ini editing that preserves layout and comments.</summary>
    internal static class IniFile
    {
        public static string Get(IList<string> lines, string section, string key)
        {
            var i = FindKey(lines, section, key);
            if (i < 0) return null;
            var line = lines[i];
            return line.Substring(line.IndexOf('=') + 1).Trim();
        }

        public static void Set(List<string> lines, string section, string key, string value)
        {
            var i = FindKey(lines, section, key);
            if (i >= 0)
            {
                lines[i] = key + "=" + value;
                return;
            }
            var header = FindSection(lines, section);
            if (header < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add(key + "=" + value);
            }
            else
            {
                lines.Insert(header + 1, key + "=" + value);
            }
        }

        private static int FindSection(IList<string> lines, string section)
        {
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static int FindKey(IList<string> lines, string section, string key)
        {
            var header = FindSection(lines, section);
            if (header < 0) return -1;
            for (var i = header + 1; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("[")) break;
                if (t.Length == 0 || t[0] == ';' || t[0] == '#') continue;
                var eq = t.IndexOf('=');
                if (eq > 0 && t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }
    }
}
