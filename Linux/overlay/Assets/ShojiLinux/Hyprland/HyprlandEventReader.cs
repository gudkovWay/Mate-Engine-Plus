using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace APIs.Hyprland
{
    public class HyprlandEventArgs : EventArgs
    {
        public HyprlandEventArgs(string eventName, string[] parameters) : base()
        {
            EventName = eventName;
            Parameters = parameters;
        }

        public string EventName { get; set; }
        public string[] Parameters { get; set; }

        public override string ToString() => $"{EventName} : {string.Join(" , ", Parameters)}";
    }

    public delegate void HyprlandEventDelegate(object sender, HyprlandEventArgs args);

    /// <summary>
    /// Reads hyprland events in realtime
    /// </summary>
    public class HyprlandEventReader : HyprlandSocketBase, IDisposable
    {
        public void Dispose()
        {
            _Disposed = true;
            _Socket?.Dispose();
        }

        const string _SocketName = ".socket2.sock";
        Socket _Socket;
        bool _Disposed;

        public bool SocketExists() => SockerExistsImpl(_SocketName);

        public async void Start(IList<string> eventFilter)
        {
            try
            {
                var eventSocketPath = GetSocketPath(_SocketName);
                _Socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await _Socket.ConnectAsync(new UnixDomainSocketEndPoint(eventSocketPath));
                using var stream = new NetworkStream(_Socket, ownsSocket: true);
                using var reader = new StreamReader(stream);
                while (!_Disposed)
                {
                    var output = await reader.ReadLineAsync();
                    if (output == null) break;
                    var separator = output.IndexOf(">>", StringComparison.Ordinal);
                    if (separator <= 0) continue;
                    var name = output.Substring(0, separator);
                    if (eventFilter.Contains(name)) continue;
                    var parameters = output.Substring(separator + 2).Split(',');
                    HyprlandEvent?.Invoke(this, new HyprlandEventArgs(name, parameters));
                }
            }
            catch (Exception error)
            {
                if (!_Disposed) Console.Error.WriteLine("Hyprland event stream closed: " + error.Message);
            }
        }

        public event HyprlandEventDelegate HyprlandEvent;
    }
}