using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace APIs.Hyprland
{
    /// <summary>
    /// Dispatches commands to the hyprland windowmanager directly via the socket
    /// </summary>
    public class HyprlandDispatcher : HyprlandSocketBase, IDisposable
    {
        sealed class DispatchItem
        {
            internal readonly TaskCompletionSource<string> Completion =
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal string Command;
        }

        const string _SocketName = ".socket.sock";
        CancellationTokenSource _CancellationTokenSource;
        ConcurrentQueue<DispatchItem> _DispatchItems;
        SemaphoreSlim _QueueSemaphore = new SemaphoreSlim(0);
        UnixDomainSocketEndPoint _EndPoint;
        Task _Worker;

        HyprlandCommandGenerator _CommandGenerator;

        bool _LUAConfigMode = false;

        public bool LUAConfigMode => _LUAConfigMode;

        bool GetConfigMode()
        {
            var psi = new ProcessStartInfo()
            {
                FileName = "hyprctl",
                Arguments = "systeminfo",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(2000))
            {
                p.Kill();
                throw new TimeoutException("Hyprland configuration query expired");
            }
            if (p.ExitCode != 0) throw new IOException("Hyprland configuration query failed");
            return output.GetAwaiter().GetResult().Contains("configProvider: lua");
        }

        public void Initialize()
        {
            _CancellationTokenSource = new CancellationTokenSource();
            _DispatchItems = new ConcurrentQueue<DispatchItem>();

            _EndPoint = new UnixDomainSocketEndPoint(GetSocketPath(_SocketName));

            _LUAConfigMode = GetConfigMode();

            if(_LUAConfigMode)
                _CommandGenerator = new LUACommandGenerator();
            else
                _CommandGenerator = new HyprLangCommandGenerator();

            _Worker = Task.Run(async () =>
            {
                var buffer = new byte[8192];
                try
                {
                    while (!_CancellationTokenSource.IsCancellationRequested)
                    {
                        await _QueueSemaphore.WaitAsync(_CancellationTokenSource.Token);
                        if (!_DispatchItems.TryDequeue(out DispatchItem item) || item.Completion.Task.IsCompleted)
                            continue;
                        try
                        {
                            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_CancellationTokenSource.Token);
                            deadline.CancelAfter(2000);
                            using var registration = deadline.Token.Register(() => socket.Dispose());
                            await socket.ConnectAsync(_EndPoint);
                            var data = Encoding.UTF8.GetBytes(item.Command);
                            int sent = 0;
                            while (sent < data.Length)
                            {
                                int count = await socket.SendAsync(new ArraySegment<byte>(data, sent, data.Length - sent), SocketFlags.None);
                                if (count == 0) throw new EndOfStreamException("Hyprland socket closed during send");
                                sent += count;
                            }
                            using var response = new MemoryStream();
                            int received;
                            while ((received = await socket.ReceiveAsync(buffer, SocketFlags.None)) > 0)
                            {
                                if (response.Length + received > 1024 * 1024)
                                    throw new IOException("Hyprland response exceeds limit");
                                response.Write(buffer, 0, received);
                            }
                            item.Completion.TrySetResult(Encoding.UTF8.GetString(response.GetBuffer(), 0, (int)response.Length));
                        }
                        catch (Exception error)
                        {
                            item.Completion.TrySetException(error);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    while (_DispatchItems.TryDequeue(out DispatchItem item))
                        item.Completion.TrySetCanceled();
                }
            });
        }

        public bool SocketExists() => SockerExistsImpl(_SocketName);

        public async Task<HyprlandClients> GetClientsAsync()
        {
            var response = await DispatchCommandAsync("-j/clients");
            response = $"{{ \"clients\" : {response} }}";
            var hashcode = response.GetHashCode();
            var clients = JsonUtility.FromJson<HyprlandClients>(response);
            clients.hashCode = hashcode;
            return clients;
        }

        public async Task<HyprlandMonitor[]> GetMonitorsAsync()
        {
            var response = await DispatchCommandAsync("-j/monitors");
            response = $"{{ \"monitors\" : {response} }}";
            var monitors = JsonUtility.FromJson<HyprlandMonitors>(response);
            return monitors.monitors;
        }

        public async Task<Dictionary<string, HyprlandLayerLevels>> GetLayersAsync()
        {
            var response = await DispatchCommandAsync("-j/layers");
            return JsonConvert.DeserializeObject<Dictionary<string, HyprlandLayerLevels>>(response);
        }

        public async Task<HyprlandWorkspace> GetActiveWorkspace()
        {
            var response = await DispatchCommandAsync("-j/activeworkspace");
            return JsonUtility.FromJson<HyprlandWorkspace>(response);
        }

        public async Task<HyprlandClient> GetActiveWindowAsync()
        {
            var response = await DispatchCommandAsync("-j/activewindow");
            return JsonUtility.FromJson<HyprlandClient>(response);
        }

        public Task FocusWindowIfActiveAsync(string address, int pid, string expectedActive, bool unlockTarget)
        {
            if (!_LUAConfigMode) throw new NotSupportedException("Atomic keyboard focus requires Hyprland Lua IPC");
            if (!ValidAddress(address) || !ValidAddress(expectedActive)) throw new ArgumentException("Invalid focus window address");
            var unlock = unlockTarget
                ? "hl.dispatch(hl.dsp.window.set_prop({prop='no_focus',value='false',window=t})); "
                : "";
            return DispatchCommandAsync("eval local a=hl.get_active_window(); local t=hl.get_window('address:" + address +
                "'); if a and a.address=='" + expectedActive + "' and t and t.pid==" + pid +
                " and t.mapped and not t.hidden and t.visible and t.workspace and not t.workspace.special and t.monitor and t.monitor.dpms_status" +
                " and (t.pinned or t.workspace.active) then " + unlock + "hl.dispatch(hl.dsp.focus({window=t})); end");
        }

        static bool ValidAddress(string address)
        {
            if (string.IsNullOrEmpty(address) || !address.StartsWith("0x", StringComparison.Ordinal) || address.Length > 18) return false;
            for (int i = 2; i < address.Length; i++)
                if (!Uri.IsHexDigit(address[i])) return false;
            return address.Length > 2;
        }

        public async Task<Vector2Int> GetCursorPositionAsync()
        {
            var response = await DispatchCommandAsync("cursorpos");
            return HyprlandVectorToVector2Int(response);
        }

        private static Vector2Int HyprlandVectorToVector2Int(string hyprlandVector)
        {
            var parts = hyprlandVector.Trim().Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y))
                return new Vector2Int(x, y);
            return Vector2Int.zero;
        }

        public async Task SetPropAsync(string windowAddress, string propName, params object[] propValue) => await DispatchCommandAsync(_CommandGenerator.SetPropAsync(windowAddress, propName, propValue));

        public async Task MoveWindowToWorkspace(string windowAddress, int workspace, bool silent) => await DispatchCommandAsync(_CommandGenerator.MoveWindowToWorkspace(windowAddress,workspace,silent));

        public async Task SetFloatingAsync(string windowAddress) => await DispatchCommandAsync(_CommandGenerator.SetFloatingAsync(windowAddress));

        public async Task TogglePinAsync(string windowAddress) => await DispatchCommandAsync(_CommandGenerator.TogglePinAsync(windowAddress));

        public Task SetZOrderAsync(string windowAddress, bool top) => DispatchCommandAsync(_LUAConfigMode
            ? $"dispatch hl.dsp.window.alter_zorder({{ mode = \"{(top ? "top" : "bottom")}\", window = \"address:{windowAddress}\" }})"
            : $"dispatch alterzorder {(top ? "top" : "bottom")},address:{windowAddress}");

        public async Task ResizewindowpixelExactWindowSizeAsync(string windowAddress, Vector2Int size)  => await DispatchCommandAsync(_CommandGenerator.ResizewindowpixelExactWindowSize(windowAddress,size));

        public async Task MoveWindowPixelExactAsync(string windowAddress, Vector2Int pos) => await DispatchCommandAsync(_CommandGenerator.MoveWindowPixelExact(windowAddress,pos));

        async Task<string> DispatchCommandAsync(string command)
        {
            _CancellationTokenSource.Token.ThrowIfCancellationRequested();
            if (_DispatchItems.Count >= 32)
                throw new IOException("Hyprland command queue is full");
            var item = new DispatchItem { Command = command };
            _DispatchItems.Enqueue(item);
            _QueueSemaphore.Release();
            if (await Task.WhenAny(item.Completion.Task, Task.Delay(5000, _CancellationTokenSource.Token)) != item.Completion.Task)
                item.Completion.TrySetException(new TimeoutException("Hyprland command expired"));
            var response = await item.Completion.Task;
            if ((command.StartsWith("dispatch ", StringComparison.Ordinal) || command.StartsWith("eval ", StringComparison.Ordinal)) && response.Trim() != "ok")
                throw new IOException("Hyprland rejected command: " + response);
            return response;
        }

        public void Dispose()
        {
            _CancellationTokenSource?.Cancel();
            try
            {
                if (_Worker != null && !_Worker.Wait(1500))
                    Console.Error.WriteLine("Hyprland dispatcher did not stop before shutdown deadline");
            }
            catch (AggregateException error) { Console.Error.WriteLine(error.GetBaseException().Message); }
        }
    }

    abstract internal class HyprlandCommandGenerator
    {
        internal abstract string ResizewindowpixelExactWindowSize(string windowAddress, Vector2Int size);
        internal abstract string MoveWindowPixelExact(string windowAddress, Vector2Int pos);
        internal abstract string SetFloatingAsync(string windowAddress);
        internal abstract string TogglePinAsync(string windowAddress);
        internal abstract string MoveWindowToWorkspace(string windowAddress, int workspace, bool silent);
        internal abstract string SetPropAsync(string windowAddress, string propName, params object[] propValue);
    }

    internal class LUACommandGenerator : HyprlandCommandGenerator
    {
        internal override string MoveWindowPixelExact(string windowAddress, Vector2Int pos) => $"dispatch hl.dsp.window.move({{ x = {pos.x}, y = {pos.y} , window = \"address:{windowAddress}\" }} )";

        internal override string MoveWindowToWorkspace(string windowAddress, int workspace, bool silent) => $"dispatch hl.dsp.window.move({{ workspace = {workspace}, window = \"address:{windowAddress}\", follow = {(!silent).ToString().ToLower()} }})";

        internal override string ResizewindowpixelExactWindowSize(string windowAddress, Vector2Int size) => $"dispatch hl.dsp.window.resize({{ x = {size.x}, y = {size.y} , window = \"address:{windowAddress}\" }} )";

        internal override string SetFloatingAsync(string windowAddress) => $"dispatch hl.dsp.window.float({{ window = \"address:{windowAddress}\" }} )";

        internal override string SetPropAsync(string windowAddress, string propName, params object[] propValue) => $"dispatch hl.dsp.window.set_prop({{ prop = \"{propName}\", value = \"{string.Join(" ", propValue)}\" , window = \"address:{windowAddress}\" }})";

        internal override string TogglePinAsync(string windowAddress) => $"dispatch hl.dsp.window.pin({{  window = \"address:{windowAddress}\" }} )";
    }

    internal class HyprLangCommandGenerator : HyprlandCommandGenerator
    {
        internal override string MoveWindowPixelExact(string windowAddress, Vector2Int pos) => $"dispatch movewindowpixel exact {pos.x} {pos.y} , address:{windowAddress}";

        internal override string MoveWindowToWorkspace(string windowAddress, int workspace, bool silent) => $"dispatch {(silent ? "movetoworkspacesilent" : "movetoworkspace")} {workspace},address:{windowAddress}";
        
        internal override string ResizewindowpixelExactWindowSize(string windowAddress, Vector2Int size) => $"dispatch resizewindowpixel exact {size.x} {size.y} , address:{windowAddress}";

        internal override string SetFloatingAsync(string windowAddress) => $"dispatch setfloating address:{windowAddress}";

        internal override string SetPropAsync(string windowAddress, string propName, params object[] propValue)
        {
            var args = new string[4 + propValue.Length];
            args[0] = "dispatch";
            args[1] = "setprop";
            args[2] = $"address:{windowAddress}";
            args[3] = propName;
            for (int i = 4; i < propValue.Length + 4; i++)
                args[i] = propValue[i - 4]?.ToString();
            return string.Join(" ", args);
        }

        internal override string TogglePinAsync(string windowAddress) => $"dispatch pin address:{windowAddress}";
    }
}