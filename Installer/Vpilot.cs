using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace VpilotRelaySetup
{
    /// <summary>What we know about a vPilot installation folder.</summary>
    internal sealed class VpilotInfo
    {
        public string Dir;
        public bool ExeFound;
        public bool PluginApiFound;
        public bool Running;

        /// <summary>Settings of an earlier vPilot Relay install, if any.</summary>
        public string ExistingToken;
        public long? ExistingChatId;
        public string ExistingVersion;

        public string PluginsDir => Dir == null ? null : Path.Combine(Dir, "Plugins");
        public string ExePath => Dir == null ? null : Path.Combine(Dir, "vPilot.exe");
        public bool Ok => ExeFound && PluginApiFound;
        public bool HasExistingSettings => !string.IsNullOrEmpty(ExistingToken) && ExistingChatId.HasValue;
    }

    internal static class Vpilot
    {
        public const string ExeName = "vPilot.exe";
        public const string PluginApiDll = "RossCarlson.Vatsim.Vpilot.Plugins.dll";

        /// <summary>Finds vPilot: its registry entry, a running vPilot, then the default folder.</summary>
        public static string FindDir()
        {
            var candidates = new List<string>();
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\vPilot"))
                    if (key?.GetValue("Install_Dir") is string dir && dir.Length > 0) candidates.Add(dir);
            }
            catch { /* ignore */ }

            foreach (var p in Process.GetProcessesByName("vPilot"))
            {
                try { candidates.Add(Path.GetDirectoryName(p.MainModule.FileName)); }
                catch { /* access denied / exited */ }
                finally { p.Dispose(); }
            }

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vPilot"));

            return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, ExeName)));
        }

        public static VpilotInfo Inspect(string dir)
        {
            var info = new VpilotInfo { Dir = dir, Running = IsRunning() };
            if (dir == null) return info;

            info.ExeFound = File.Exists(Path.Combine(dir, ExeName));
            info.PluginApiFound = File.Exists(Path.Combine(dir, PluginApiDll));

            var ini = Path.Combine(info.PluginsDir, Payload.IniFileName);
            if (File.Exists(ini))
            {
                var lines = SafeReadLines(ini);
                info.ExistingToken = IniFile.Get(lines, "Telegram", "BotToken");
                if (long.TryParse(IniFile.Get(lines, "Telegram", "ChatId"), out var chat)) info.ExistingChatId = chat;
            }
            var dll = Path.Combine(info.PluginsDir, Payload.DllFileName);
            if (File.Exists(dll))
            {
                try { info.ExistingVersion = FileVersionInfo.GetVersionInfo(dll).FileVersion; }
                catch { /* ignore */ }
            }
            return info;
        }

        public static bool IsRunning()
        {
            var procs = Process.GetProcessesByName("vPilot");
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }

        /// <summary>Asks vPilot to close (like clicking X) and waits. Returns true once no vPilot is running.</summary>
        public static async Task<bool> CloseAsync()
        {
            foreach (var p in Process.GetProcessesByName("vPilot"))
            {
                try { p.CloseMainWindow(); }
                catch { /* ignore */ }
                finally { p.Dispose(); }
            }
            for (var i = 0; i < 30 && IsRunning(); i++)
                await Task.Delay(500);
            return !IsRunning();
        }

        public static void Launch(VpilotInfo info)
            => Process.Start(new ProcessStartInfo(info.ExePath) { WorkingDirectory = info.Dir, UseShellExecute = true });

        private static string[] SafeReadLines(string path)
        {
            try { return File.ReadAllLines(path); }
            catch { return new string[0]; }
        }
    }
}
