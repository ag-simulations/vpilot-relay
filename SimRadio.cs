using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.FlightSimulator.SimConnect;

namespace VpilotRelay
{
    /// <summary>
    /// Own SimConnect connection to the simulator, used for things vPilot's plugin API cannot do
    /// (tuning COM radios, setting the transponder code, reading aircraft state).
    /// </summary>
    internal sealed class SimRadio : IDisposable
    {
        private enum Ev : uint { Com1Active = 1, Com1Standby, Com2Active, Com2Standby, Xpndr }
        private enum Def : uint { State = 1 }
        private enum Req : uint { State = 1 }
        private enum Grp : uint { Default = 1 }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct AircraftState
        {
            public double Com1Active;   // MHz
            public double Com1Standby;
            public double Com2Active;
            public double Com2Standby;
            public double Transponder;  // BCO16
            public double AltitudeFt;
            public double IasKt;
            public double GroundSpeedKt;
            public double HeadingMag;
            public double Latitude;
            public double Longitude;
            public double OnGround;
        }

        private readonly object _lock = new object();
        private readonly Action<string> _log;
        private SimConnect _sc;
        private EventWaitHandle _wait;
        private Thread _thread;
        private volatile bool _running;

        public AircraftState? State { get; private set; }
        public DateTime StateTime { get; private set; }

        public SimRadio(Action<string> log) { _log = log; }

        public bool IsConnected { get { lock (_lock) return _sc != null; } }

        /// <summary>Connects if not already connected. Returns null on success or an error.</summary>
        public string EnsureConnected()
        {
            lock (_lock)
            {
                if (_sc != null) return null;
                try
                {
                    _wait = new EventWaitHandle(false, EventResetMode.AutoReset);
                    _sc = new SimConnect("vPilot Relay", IntPtr.Zero, 0, _wait, 0);
                    _sc.OnRecvQuit += (s, e) => Disconnect();
                    _sc.OnRecvException += (s, e) => _log("SimConnect error: " + (SIMCONNECT_EXCEPTION)e.dwException);
                    _sc.OnRecvSimobjectData += OnData;

                    _sc.MapClientEventToSimEvent(Ev.Com1Active, "COM_RADIO_SET_HZ");
                    _sc.MapClientEventToSimEvent(Ev.Com1Standby, "COM_STBY_RADIO_SET_HZ");
                    _sc.MapClientEventToSimEvent(Ev.Com2Active, "COM2_RADIO_SET_HZ");
                    _sc.MapClientEventToSimEvent(Ev.Com2Standby, "COM2_STBY_RADIO_SET_HZ");
                    _sc.MapClientEventToSimEvent(Ev.Xpndr, "XPNDR_SET");

                    Add("COM ACTIVE FREQUENCY:1", "MHz");
                    Add("COM STANDBY FREQUENCY:1", "MHz");
                    Add("COM ACTIVE FREQUENCY:2", "MHz");
                    Add("COM STANDBY FREQUENCY:2", "MHz");
                    Add("TRANSPONDER CODE:1", "BCO16");
                    Add("INDICATED ALTITUDE", "feet");
                    Add("AIRSPEED INDICATED", "knots");
                    Add("GROUND VELOCITY", "knots");
                    Add("PLANE HEADING DEGREES MAGNETIC", "degrees");
                    Add("PLANE LATITUDE", "degrees");
                    Add("PLANE LONGITUDE", "degrees");
                    Add("SIM ON GROUND", "bool");
                    _sc.RegisterDataDefineStruct<AircraftState>(Def.State);
                    _sc.RequestDataOnSimObject(Req.State, Def.State, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                        SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

                    _running = true;
                    _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "vPilotRelay-SimConnect" };
                    _thread.Start();
                    return null;
                }
                catch (Exception ex)
                {
                    CloseLocked();
                    return ex is COMException ? "Could not connect to the simulator (is it running?)" : ex.Message;
                }
            }
        }

        private void Add(string name, string units)
            => _sc.AddToDataDefinition(Def.State, name, units, SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);

        private void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    if (!_wait.WaitOne(1000)) continue;
                    lock (_lock) _sc?.ReceiveMessage();
                }
                catch (Exception)
                {
                    Disconnect();
                }
            }
        }

        private void OnData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            if (data.dwRequestID == (uint)Req.State && data.dwData.Length > 0)
            {
                State = (AircraftState)data.dwData[0];
                StateTime = DateTime.UtcNow;
            }
        }

        /// <summary>com: 1 or 2. Returns null on success or an error.</summary>
        public string SetCom(int com, bool standby, int khz)
        {
            var ev = com == 2 ? (standby ? Ev.Com2Standby : Ev.Com2Active) : (standby ? Ev.Com1Standby : Ev.Com1Active);
            return Transmit(ev, (uint)(khz * 1000));
        }

        /// <summary>code: 4 octal digits, e.g. "7000". Returns null on success or an error.</summary>
        public string SetSquawk(string code)
            => Transmit(Ev.Xpndr, Convert.ToUInt32(code, 16)); // BCD16: "1234" -> 0x1234

        private string Transmit(Ev ev, uint value)
        {
            var err = EnsureConnected();
            if (err != null) return err;
            try
            {
                lock (_lock)
                    _sc.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, ev, value,
                        Grp.Default, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                return null;
            }
            catch (Exception ex)
            {
                Disconnect();
                return ex.Message;
            }
        }

        public void Disconnect()
        {
            lock (_lock) CloseLocked();
        }

        private void CloseLocked()
        {
            _running = false;
            try { _sc?.Dispose(); } catch { /* ignore */ }
            _sc = null;
            State = null;
        }

        public void Dispose() => Disconnect();
    }
}
