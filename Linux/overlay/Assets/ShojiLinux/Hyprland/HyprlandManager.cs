using UnityEngine;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using Random = System.Random;
using static APIs.Hyprland.HyprlandUtils;

namespace APIs.Hyprland
{
    public class HyprlandManager : Singleton<HyprlandManager>, IWindowManagerImplementation, IDisposable
    {
        const int _LayerBackground = 0;
        const int _LayerBottom = 1;
        const int _LayerTop = 2;
        const int _LayerOverlay = 3;

        Vector2Int _LastCursorPosition = Vector2Int.zero;

        long _FastUntil;
        volatile bool _MonitorRefreshRequested;
        CancellationTokenSource _CancellationTokenSource;

        ConcurrentDictionary<IntPtr, HyprlandClient> _Clients;

        bool _MouseOverWindow;


        bool _IsDragging;

        public bool IsDragging
        {
            get => _IsDragging;
            set
            {
                _IsDragging = value;
                //ShowError(value.ToString());
            }
        }

        HyprlandClient _Window = null;

        HyprlandEventReader _HyprlandEventReader;
        HyprlandDispatcher _HyprlandDispatcher;
        readonly object _GeometryLock = new object();
        Vector2Int _DesiredPosition;
        bool _PositionPending, _PositionWorker;
        Rect _BodyBounds;
        volatile bool _InteractionActive;
        public volatile bool CompanionUiOpen, SeatTransition, CompanionAnimationActive;
        public bool SupportTracking => _SnappedWindow != IntPtr.Zero;
        float _NextBoundsSample;
        volatile HyprlandClient _Foreground;
        IntPtr _LastSeatSupport;
        readonly object _FocusLock = new object();
        bool _KeyboardRequested;
        readonly CompanionKeyboardOwnership _KeyboardOwner = new CompanionKeyboardOwnership();
        long _FocusSerial, _RequestedFocusSerial, _KeyboardRevision, _AttemptedKeyboardRevision;
        double _NextFocusObservation;
        public bool KeyboardLease { get { lock (_FocusLock) return _KeyboardOwner.Owned && !_KeyboardOwner.Pending; } }
        public bool KeyboardAvailable => _HyprlandDispatcher != null && _HyprlandDispatcher.LUAConfigMode &&
            CompanionPresencePolicy.KeyboardEligible(true, SurfacePresentable, _Window?.workspace?.IsSpecial == true);
        public bool PointerEligible
        {
            get
            {
                var window = _Window;
                return window != null && CompanionPresencePolicy.PointerEligible(
                    SurfacePresentable, PassiveInput, window.workspace?.IsSpecial == true) &&
                    (IsDragging || _InputMask?.Contains(_LastCursorPosition - (Vector2)window.atVector, window.sizeVector) == true);
            }
        }
        public bool KeyboardEligible { get { lock (_FocusLock) return _KeyboardRequested && KeyboardAvailable; } }
        public bool InteractionActive => _InteractionActive || _MouseOverWindow || IsDragging || CompanionAnimationActive || SeatTransition;
        public bool ObservationFresh => (Stopwatch.GetTimestamp() - Interlocked.Read(ref _LastObservation)) / (double)Stopwatch.Frequency <= 1.5;
        public bool ConfirmedHidden => ObservationFresh && _Window != null && !SurfacePresentable;
        public bool OwnedAuditionPresentable => SurfacePresentableForOwnedWorkspace(CompanionPresencePolicy.OwnedAuditionWorkspace);

        public void RequestKeyboard()
        {
            if (!KeyboardAvailable) return;
            lock (_FocusLock)
            {
                if (_KeyboardRequested) return;
                _KeyboardRequested = true; _KeyboardRevision++; _RequestedFocusSerial = _FocusSerial;
            }
        }
        public void ReleaseKeyboard()
        {
            lock (_FocusLock) _KeyboardRequested = false;
        }

        public void SampleKeyboardIntent()
        {
            if (!Input.GetMouseButtonDown(0)) return;
            var selection = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            var field = selection == null ? null : selection.GetComponent<UnityEngine.UI.InputField>();
            if (field == null || !field.interactable || !field.isActiveAndEnabled) return;
            var canvas = field.GetComponentInParent<Canvas>();
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            if (RectTransformUtility.RectangleContainsScreenPoint(selection.transform as RectTransform, Input.mousePosition, camera))
                RequestKeyboard();
        }
        volatile bool _Disposed;
        Renderer[] _AvatarRenderers;
        Camera _AvatarCamera;
        float _NextRendererLookup;
        Animator _SampledAnimator;
        AvatarAnimatorController _SampledController;
        AvatarLocomotionController _SampledLocomotion;
        Vector2Int _InitialSize;
        public volatile bool PassiveInput;
        readonly CompanionMotionLease _CompanionLease = new CompanionMotionLease();
        Rect _CompanionSafetyBounds;
        public bool CompanionMotion { get { lock (_GeometryLock) return _CompanionLease.Owned; } }
        public bool TryOwnMotion(object owner)
        {
            lock (_GeometryLock)
            {
                bool existing = _CompanionLease.Owned;
                if (SupportTracking || !_CompanionLease.TryAcquire(owner)) return false;
                if (!existing) _CompanionSafetyBounds = _BodyBounds;
                return true;
            }
        }
        public void ReleaseMotion(object owner)
        {
            lock (_GeometryLock)
                if (_CompanionLease.Release(owner)) CompanionAnimationActive = false;
        }
        long _LastObservation;
        public bool SurfacePresentable
        {
            get
            {
                var window = _Window;
                if (window == null || !window.mapped || window.hidden ||
                    (Stopwatch.GetTimestamp() - Interlocked.Read(ref _LastObservation)) / (double)Stopwatch.Frequency > 1.5)
                    return false;
                var monitors = _Monitors;
                if (monitors == null) return false;
                foreach (var monitor in monitors)
                    if (monitor.id == window.monitor && !monitor.disabled && monitor.dpmsStatus == true &&
                        (window.pinned || window.workspace?.Matches(monitor.activeWorkspace) == true)) return true;
                return false;
            }
        }
        public bool SurfacePresentableForOwnedWorkspace(string exactWorkspace)
        {
            var window = _Window;
            if (string.IsNullOrEmpty(exactWorkspace) || !exactWorkspace.StartsWith("special:omp-", StringComparison.Ordinal) ||
                window == null || !window.mapped || window.workspace?.name != exactWorkspace ||
                (Stopwatch.GetTimestamp() - Interlocked.Read(ref _LastObservation)) / (double)Stopwatch.Frequency > 1.5)
                return false;
            var monitors = _Monitors;
            if (monitors == null) return false;
            foreach (var monitor in monitors)
                if (monitor.id == window.monitor && !monitor.disabled && monitor.dpmsStatus == true) return true;
            return false;
        }
        public Rect BodyBounds { get { lock (_GeometryLock) return _BodyBounds; } }
        volatile Rect[] _CompanionInputRegions = Array.Empty<Rect>();
        public void SetCompanionInputRegions(Rect[] normalizedRegions) { _CompanionInputRegions = normalizedRegions; }
        volatile CompanionInputMask _InputMask;
        public void SetInputMask(byte[] pixels, int width, int height) =>
            _InputMask = new CompanionInputMask(pixels, width, height);
        public HyprlandMonitor[] Monitors => _Monitors;
        public HyprlandClient CompanionWindow => _Window;
        public HyprlandClient ForegroundWindow => _Foreground;
        public long ForegroundRevision { get { lock (_FocusLock) return _FocusSerial; } }

        public void SampleInteraction()
        {
            if (_InitialSize == Vector2Int.zero && SaveLoadHandler.Instance?.data != null)
            {
                switch (SaveLoadHandler.Instance.data.windowSizeState)
                {
                    case SaveLoadHandler.SettingsData.WindowSizeState.Small: _InitialSize = new Vector2Int(768, 512); break;
                    case SaveLoadHandler.SettingsData.WindowSizeState.Big: _InitialSize = new Vector2Int(2048, 1536); break;
                    default: _InitialSize = new Vector2Int(1536, 1024); break;
                }
            }
            var portrait = AvatarBigScreenHandler.ActiveHandlers.Count > 0 ? AvatarBigScreenHandler.ActiveHandlers[0] : null;
            _InteractionActive = CompanionUiOpen || MenuActions.IsAnyMenuOpen() || TutorialMenu.IsActive ||
                (portrait != null && (portrait.IsBigScreenActive || portrait.IsTransitioning)) ||
                (_SampledController != null && (_SampledController.isDancing || _SampledController.isDragging)) ||
                (_SampledLocomotion != null && _SampledLocomotion.IsWalking);
            if (!CompanionUiOpen && !MenuActions.IsAnyMenuOpen()) ReleaseKeyboard();
            if (!InteractionActive && Time.unscaledTime < _NextBoundsSample) return;
            _NextBoundsSample = Time.unscaledTime + 1f / 30f;
            if (Time.unscaledTime >= _NextRendererLookup)
            {
                _NextRendererLookup = Time.unscaledTime + 1f;
                var receiver = UnityEngine.Object.FindFirstObjectByType<AvatarAnimatorReceiver>();
                var animator = receiver == null ? null : receiver.avatarAnimator;
                if (animator != _SampledAnimator)
                {
                    _SampledAnimator = animator;
                    _AvatarRenderers = animator == null ? null : animator.GetComponentsInChildren<Renderer>();
                    _SampledController = animator == null ? null : animator.GetComponent<AvatarAnimatorController>();
                    _SampledLocomotion = animator == null ? null : animator.GetComponent<AvatarLocomotionController>();
                }
                _AvatarCamera = Camera.main;
            }
            var window = _Window;
            if (_AvatarCamera == null || _AvatarRenderers == null || window == null ||
                Screen.width <= 0 || Screen.height <= 0) return;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            var found = false;
            foreach (var renderer in _AvatarRenderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var bounds = renderer.bounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var point = _AvatarCamera.WorldToScreenPoint(bounds.center + Vector3.Scale(bounds.extents,
                        new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1,
                            (corner & 4) == 0 ? -1 : 1)));
                    if (point.z <= 0) continue;
                    var local = new Vector2(point.x * window.sizeVector.x / Screen.width,
                        (Screen.height - point.y) * window.sizeVector.y / Screen.height);
                    min = Vector2.Min(min, local);
                    max = Vector2.Max(max, local);
                    found = true;
                }
            }
            if (found)
                lock (_GeometryLock)
                    if (!IsDragging || _BodyBounds.width <= 0)
                    {
                        _BodyBounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                        if (_CompanionLease.Owned)
                            _CompanionSafetyBounds = Rect.MinMaxRect(Mathf.Min(_CompanionSafetyBounds.xMin, min.x),
                                Mathf.Min(_CompanionSafetyBounds.yMin, min.y), Mathf.Max(_CompanionSafetyBounds.xMax, max.x),
                                Mathf.Max(_CompanionSafetyBounds.yMax, max.y));
                    }
        }

        Vector2Int BoundPosition(Vector2Int position)
        {
            var body = _CompanionLease.Owned ? _CompanionSafetyBounds : _BodyBounds;
            if (_Monitors == null || body.width <= 0 || body.height <= 0) return position;
            var center = (Vector2)position + body.center;
            HyprlandMonitor selected = null;
            var distance = float.MaxValue;
            foreach (var monitor in _Monitors)
            {
                if (monitor.disabled || monitor.width <= 0 || monitor.height <= 0) continue;
                var area = monitor.workArea;
                var nearest = new Vector2(Mathf.Clamp(center.x, area.xMin, area.xMax),
                    Mathf.Clamp(center.y, area.yMin, area.yMax));
                var candidate = (nearest - center).sqrMagnitude;
                if (candidate < distance) { selected = monitor; distance = candidate; }
            }
            if (selected == null) return position;
            var safe = selected.workArea;
            var minX = safe.xMin + 12 - body.xMin;
            var maxX = safe.xMax - 12 - body.xMax;
            var minY = safe.yMin + 12 - body.yMin;
            var maxY = safe.yMax - 12 - body.yMax;
            return new Vector2Int(Mathf.RoundToInt(minX <= maxX ? Mathf.Clamp(position.x, minX, maxX) : (minX + maxX) / 2),
                Mathf.RoundToInt(minY <= maxY ? Mathf.Clamp(position.y, minY, maxY) : (minY + maxY) / 2));
        }

        public HyprlandManager()
        {
            _Clients = new ConcurrentDictionary<IntPtr, HyprlandClient>();
            _Layers = new ConcurrentDictionary<IntPtr, HyprlandClient>();
            _CancellationTokenSource = new CancellationTokenSource();

            _HyprlandDispatcher = new HyprlandDispatcher();
            _HyprlandDispatcher.Initialize();

            ShowError($"LUA Config: {_HyprlandDispatcher.LUAConfigMode}");

            _HyprlandEventReader = new HyprlandEventReader();
            _HyprlandEventReader.HyprlandEvent += HyprlandEvent;
            _HyprlandEventReader.Start(new List<string>
            {
                // Active-window events revoke keyboard leases; they must not be filtered.
                HyprlandEventNames.WindowTitle,
                HyprlandEventNames.WindowTitleV2
            });
            _LoopTask = Task.Run(() => UpdateAsync(_CancellationTokenSource.Token));
        }

        public void Dispose()
        {
            if (_Disposed) return;
            _Disposed = true;
            _CancellationTokenSource.Cancel();
            _HyprlandEventReader.HyprlandEvent -= HyprlandEvent;
            _HyprlandEventReader.Dispose();
            _HyprlandDispatcher.Dispose();
            try
            {
                if (_LoopTask != null && !_LoopTask.Wait(1500))
                    ShowError("Hyprland observation did not stop before shutdown deadline");
            }
            catch (AggregateException error) { ShowError(error.GetBaseException().Message); }
        }

        async void HyprlandEvent(object sender, HyprlandEventArgs hyprlandEventArgs)
        {
            try
            {
                switch (hyprlandEventArgs.EventName)
                {
                    case HyprlandEventNames.ActiveWindowV2:
                        lock (_FocusLock)
                        {
                            _FocusSerial++;
                            var address = hyprlandEventArgs.Parameters.Length > 0 ? hyprlandEventArgs.Parameters[0] : "";
                            var changed = string.IsNullOrEmpty(address) ? IntPtr.Zero : AddressToIntPtr(address);
                            if (changed != (_Window?.addressIntPtr ?? IntPtr.Zero) && (_KeyboardOwner.Owned || _KeyboardRequested))
                            {
                                _KeyboardRequested = false;
                                _KeyboardOwner.ObserveExternal();
                            }
                        }
                        TriggerImidiateUpdates();
                        break;
                    case HyprlandEventNames.MoveToWorkspace:
                    case HyprlandEventNames.FocusMonitor:
                        _MonitorRefreshRequested = true;
                        TriggerImidiateUpdates();
                        break;
                    case HyprlandEventNames.MoveWindowToWorkspace:
                        {
                            var movedWindow =  AddressToIntPtr(hyprlandEventArgs.Parameters[0]);
                            if (hyprlandEventArgs.Parameters.Length < 2 ||
                                !int.TryParse(hyprlandEventArgs.Parameters[1], out var targetWorkspace)) break;

                            if (movedWindow == _SnappedWindow) TriggerImidiateUpdates();
                            break;
                        }
                    case HyprlandEventNames.CreateWorkspace:
                    case HyprlandEventNames.DestroyWorkspace:
                    case HyprlandEventNames.MoveToWorkspaceToMonitor:
                    case HyprlandEventNames.MoveWindowIntoGroup:
                    case HyprlandEventNames.MoveWindowOutOfGroup:
                    case HyprlandEventNames.CloseWindow:
                    case HyprlandEventNames.OpenWindow:
                        {
                            // layout likely changed
                            TriggerImidiateUpdates();
                            break;
                        }
                    case HyprlandEventNames.PinState:
                        {
                            var ptr = AddressToIntPtr(hyprlandEventArgs.Parameters[0]);
                            if (ptr == _SnappedWindow) TriggerImidiateUpdates();
                            // ShowError(hyprlandEventArgs.EventName);
                            break;
                        }
                    case HyprlandEventNames.MonitorAdded:
                    case HyprlandEventNames.MonitorRemoved:
                        {
                            _MonitorRefreshRequested = true;
                            // ShowError(hyprlandEventArgs.EventName);
                            break;
                        }
                    case HyprlandEventNames.LayerAdded:
                    case HyprlandEventNames.LayerRemoved:
                        {
                            await UpdateLayersAsync();
                            // ShowError(hyprlandEventArgs.EventName);
                            break;
                        }
                }
            }
            catch(Exception ex)
            {
                ShowError(ex.ToString());
            }
        }

        public void HideFromTaskbar(bool reallyHide)
        {
            // not supported
            // has to be configured in the bar/widget application
        }

        public void SetWindowBorderless(bool value)
        {
            // not necessary
        }

        public void SetWindowType(WindowType type)
        {
            // not supported
        }

        Task _LoopTask;


        async Task SetInitialWindowPropsAsync()
        {
            // turn the window into a floating window
            if(!_Window.floating)
                await _HyprlandDispatcher.SetFloatingAsync(_Window.address);
            // no_focus also removes pointer hit-testing; no_follow_mouse only blocks hover focus.
            await _HyprlandDispatcher.SetPropAsync(_Window.address, "no_follow_mouse", true);
            _CurrentFocusState = null;
            await SetFocusableAsync(PointerEligible);
            lock (_FocusLock)
            {
                _KeyboardRequested = false;
                _KeyboardOwner.Clear(); _FocusSerial++;
            }
            // remove all window decorations
            await _HyprlandDispatcher.SetPropAsync(_Window.address, "decorate", false);
            // disable the window background blur
            await _HyprlandDispatcher.SetPropAsync(_Window.address, "no_blur", true);

            if (_InitialSize != Vector2Int.zero) SetWindowSize(_InitialSize);
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MATEENGINE_CONTROL_SOCKET")) &&
                _Window.workspace?.IsSpecial == false && !_Window.pinned)
                await _HyprlandDispatcher.TogglePinAsync(_Window.address);
            await _HyprlandDispatcher.SetZOrderAsync(_Window.address, true);
        }

        void TriggerImidiateUpdates()
        {
            Interlocked.Exchange(ref _FastUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency);
        }

        async Task UpdateAsync(CancellationToken cancellationToken)
        {
            double nextCursor = 0, nextClients = 0, nextMonitors = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // check if the avatar window has been identified
                    if (_Window == null)
                    {
                        // initial setup
                        _Monitors = await _HyprlandDispatcher.GetMonitorsAsync();
                        await UpdateLayersAsync();
                        await UpdateWindowsAsync();
                        if (_Window != null)
                            await SetInitialWindowPropsAsync();
                    }
                    else
                    {
                        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                        bool fast = IsDragging || CompanionAnimationActive || SupportTracking || Stopwatch.GetTimestamp() < Interlocked.Read(ref _FastUntil);
                        if (now >= nextCursor)
                        {
                            _LastCursorPosition = await _HyprlandDispatcher.GetCursorPositionAsync();
                            nextCursor = now + .016;
                        }
                        if (now >= nextMonitors || _MonitorRefreshRequested)
                        {
                            _MonitorRefreshRequested = false;
                            _Monitors = await _HyprlandDispatcher.GetMonitorsAsync();
                            nextMonitors = now + 1;
                        }
                        if (now >= nextClients)
                        {
                            await UpdateWindowsAsync();
                            Interlocked.Exchange(ref _LastObservation, Stopwatch.GetTimestamp());
                            nextClients = now + (fast ? .048 : .25);
                        }
                    }
                    UpdatePointerProximity();
                    await UpdateKeyboardAsync();
                    await SetFocusableAsync(PointerEligible);
                }
                catch (Exception ex)
                {
                    // An unlock may have succeeded before observation failed. Reconcile its owner on the next pass.
                    lock (_FocusLock) { _KeyboardRequested = false; _KeyboardOwner.ObservationFailed(); }
                    _CurrentFocusState = null;
                    ShowError(ex.ToString());
                }
                try { await Task.Delay(16, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        async Task UpdateLayersAsync()
        {
            // sync layers 
            var layersPerMonitor = await _HyprlandDispatcher.GetLayersAsync();
            foreach (var monitorName in layersPerMonitor.Keys)
            {
                var levels = layersPerMonitor[monitorName];
                foreach (var levelKey in levels.levels.Keys)
                {
                    var level = int.Parse(levelKey);
                    if (level == _LayerTop || level == _LayerBottom)
                    {
                        var layers = levels.levels[levelKey];
                        foreach (var layer in layers)
                        {
                            if(layer.posVector.y == 0)
                                continue;
                            _Layers[layer.addressIntPtr] = new HyprlandClient
                            {
                                address = layer.address,
                                at = new int[] { layer.posVector.x, layer.posVector.y },
                                floating = false,
                                size = new int[] { layer.sizeVector.x, layer.sizeVector.y },
                                pinned = true,
                                initialClass = layer.name,
                                pid = layer.pid,
                                monitor = _Monitors.FirstOrDefault(a => a.name == monitorName)?.id ?? 0
                            };
                        }
                    }
                }
            }

            foreach (var address in _Layers.Keys)
            {
                if(_Layers[address].initialClass == "hyprland.imaginary")
                    continue;
                if (!layersPerMonitor.Any(a => a.Value.levels.Any(b => b.Value.Any(c => c.addressIntPtr == address))))
                {
                    // ShowError($"Remove Layer: {_Layers[address].initialClass} {address}");
                    _Layers.TryRemove(address, out _);
                }
            }

            // create imaginary layers on each monitor when allowHyprlandMonitorSitting is enabled
            if (_Monitors != null && _Monitors.Length > 0)
            {
                foreach (var monitor in _Monitors)
                {
                    var pointer = new IntPtr(monitor.name.GetHashCode());
                    if(!SaveLoadHandler.Instance.data.allowHyprlandMonitorSitting)
                    {
                        if(_Layers.ContainsKey(pointer))
                        {
                            // ShowError($"Remove imaginary layer: {monitor.name}");
                            _Layers.TryRemove(pointer, out _);
                        }
                        continue;
                    }
                    
                    if (!_Layers.ContainsKey(pointer))
                    {
                        _Layers[pointer] = new HyprlandClient
                        {
                            address = IntPtrToHex(pointer),
                            at = new int[] { monitor.position.x, monitor.position.y + monitor.size.y - 2 },
                            floating = false,
                            size = new int[] { monitor.size.x, 100 },
                            pinned = true,
                            initialClass = "hyprland.imaginary",
                            title = monitor.name,
                            pid = 0,
                            monitor = monitor.id
                        };
                        //ShowError($"Create imaginary layer: {monitor.name}, pos:{_Layers[pointer].atVector}, size:{_Layers[pointer].sizeVector}");
                    }
                }
            }
            // ShowError("------");
            // foreach(var layers in _Layers.Values)
            //     ShowError($"{layers.address}: {layers.monitor} {layers.title} {layers.atVector} {layers.sizeVector}");
            // ShowError("------");
        }

        ConcurrentDictionary<IntPtr, HyprlandClient> _Layers;

        int _LastHashCode = 0;

        async Task UpdateWindowsAsync()
        {
            var clients = await _HyprlandDispatcher.GetClientsAsync();
            // layout has not changed => skip
            if (clients.hashCode == _LastHashCode)
                return;
            _LastHashCode = clients.hashCode;

            // detect avatar window
            if (_Window == null)
                _Window = clients.clients.FirstOrDefault(a => a.pid == Process.GetCurrentProcess().Id);
            else
                _Window = clients.clients.FirstOrDefault(a => a.address == _Window.address);
            _Foreground = null;
            foreach (var client in clients.clients)
                if (client.focusHistoryID == 0 && client.address != _Window?.address && client.mapped && !client.hidden)
                { _Foreground = client; break; }
            if (_Foreground != null) _LastSeatSupport = _Foreground.addressIntPtr;
            if (_Window == null)
                return;
            

            // sync other windows
            var closedWindows = _Clients.Where(existing => !clients.clients.Any(found => found.address == existing.Value.address)).Select(a => a.Key).ToList();
            foreach (var closedWindow in closedWindows)
                _Clients.TryRemove(closedWindow, out _);

            foreach (var client in clients.clients)
            {
                // workspace changed of the snapped window before the event caught it
                if (client.addressIntPtr == _SnappedWindow && client.workspace?.Matches(_Window.workspace) != true)
                    TriggerImidiateUpdates();
                _Clients[client.addressIntPtr] = client;
            }

            // Polling observations must not perpetually restart the fast-update window.
        }

        void UpdatePointerProximity()
        {
            var window = _Window;
            if (window == null) return;
            if (PassiveInput || !SurfacePresentable)
            {
                _MouseOverWindow = false;
                return;
            }
            Rect body;
            lock (_GeometryLock) body = _BodyBounds;
            body.position += (Vector2)window.atVector;
            body = Rect.MinMaxRect(body.xMin - 32, body.yMin - 32, body.xMax + 32, body.yMax + 32);
            var local = _LastCursorPosition - (Vector2)window.atVector;
            var normalized = new Vector2(local.x / Mathf.Max(1, window.sizeVector.x), local.y / Mathf.Max(1, window.sizeVector.y));
            _MouseOverWindow = body.Contains(_LastCursorPosition);
            foreach (var region in _CompanionInputRegions)
                if (region.Contains(normalized)) { _MouseOverWindow = true; break; }
        }

        HyprlandMonitor[] _Monitors;

        public Vector2Int GetMousePosition()
        {
            //ShowError(_LastCursorPosition);
            return _LastCursorPosition;
        }

        private bool? _CurrentFocusState;

        async Task UpdateKeyboardAsync()
        {
            var window = _Window;
            if (window == null) return;
            bool wanted, owned, pending; long revision, serial, requestedSerial;
            string previous;
            int previousPid;
            lock (_FocusLock)
            {
                if (!KeyboardAvailable) _KeyboardRequested = false;
                wanted = _KeyboardRequested; owned = _KeyboardOwner.Owned; pending = _KeyboardOwner.Pending;
                revision = _KeyboardRevision; serial = _FocusSerial; requestedSerial = _RequestedFocusSerial;
                previous = _KeyboardOwner.PreviousWindow; previousPid = _KeyboardOwner.PreviousPid;
            }
            if (owned && pending)
            {
                var observed = await _HyprlandDispatcher.GetActiveWindowAsync();
                bool retained;
                lock (_FocusLock)
                {
                    if (serial != _FocusSerial) return;
                    _KeyboardOwner.Reconcile(observed?.address == window.address);
                    retained = _KeyboardOwner.Owned;
                }
                if (!retained) await SetFocusableAsync(PointerEligible);
                return;
            }
            if (wanted && !owned && revision != _AttemptedKeyboardRevision)
            {
                _AttemptedKeyboardRevision = revision;
                lock (_FocusLock)
                    if (!CompanionPresencePolicy.KeyboardRequestCurrent(_KeyboardRequested, requestedSerial, _FocusSerial))
                    { if (revision == _KeyboardRevision) _KeyboardRequested = false; return; }
                var active = await _HyprlandDispatcher.GetActiveWindowAsync();
                lock (_FocusLock)
                    if (!CompanionPresencePolicy.KeyboardRequestCurrent(_KeyboardRequested, requestedSerial, _FocusSerial) || revision != _KeyboardRevision)
                    { if (revision == _KeyboardRevision) _KeyboardRequested = false; return; }
                if (!KeyboardAvailable || active == null || string.IsNullOrEmpty(active.address))
                {
                    lock (_FocusLock) if (revision == _KeyboardRevision) _KeyboardRequested = false;
                    return;
                }
                // Eligibility and the final owner comparison execute in one compositor Lua request.
                var current = await _HyprlandDispatcher.GetActiveWindowAsync();
                lock (_FocusLock)
                {
                    if (!CompanionPresencePolicy.KeyboardRequestCurrent(_KeyboardRequested, requestedSerial, _FocusSerial) || revision != _KeyboardRevision ||
                        current?.address != active.address) { if (revision == _KeyboardRevision) _KeyboardRequested = false; return; }
                    _KeyboardOwner.Begin(active.address == window.address ? null : active.address, active.pid);
                }
                _CurrentFocusState = null;
                await _HyprlandDispatcher.FocusWindowIfActiveAsync(window.address, window.pid, active.address, true);
                lock (_FocusLock) serial = _FocusSerial;
                var granted = await _HyprlandDispatcher.GetActiveWindowAsync();
                lock (_FocusLock)
                {
                    if (serial == _FocusSerial)
                    {
                        _KeyboardOwner.Reconcile(granted?.address == window.address);
                        if (!_KeyboardOwner.Owned && revision == _KeyboardRevision) _KeyboardRequested = false;
                    }
                    else _KeyboardOwner.ObservationFailed();
                }
                _NextFocusObservation = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency + .25;
            }
            else if (wanted && owned && Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency >= _NextFocusObservation)
            {
                _NextFocusObservation = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency + .25;
                var current = await _HyprlandDispatcher.GetActiveWindowAsync();
                if (current?.address != window.address)
                {
                    lock (_FocusLock)
                    {
                        if (serial != _FocusSerial) return;
                        if (revision == _KeyboardRevision) _KeyboardRequested = false;
                        _KeyboardOwner.Clear();
                    }
                    await SetFocusableAsync(PointerEligible);
                }
            }
            else if (!wanted)
            {
                if (owned && previous != null)
                {
                    var current = await _HyprlandDispatcher.GetActiveWindowAsync();
                    bool restore;
                    lock (_FocusLock)
                    {
                        if (serial != _FocusSerial || _KeyboardRequested) return;
                        restore = _KeyboardOwner.Owned && current?.address == window.address;
                    }
                    if (restore && TryFindVisibleClient(previous, out var prior) && prior.workspace?.IsSpecial == false)
                        await _HyprlandDispatcher.FocusWindowIfActiveAsync(previous, previousPid, window.address, false);
                }
                lock (_FocusLock) _KeyboardOwner.Clear();
                await SetFocusableAsync(PointerEligible);
            }
        }

        async Task SetFocusableAsync(bool focusable)
        {
            if(focusable != _CurrentFocusState)
            {
                var window = _Window;
                if (window == null) return;
                await _HyprlandDispatcher.SetPropAsync(window.address, "no_focus", !focusable);
                _CurrentFocusState = focusable;
            }
        }

        public void SetWindowPosition(Vector2Int position)
        {
            if (CompanionMotion && !IsDragging) return;
            if (IsDragging || SupportTracking) QueuePosition(position, false);
            else SetCompanionPosition(position);
        }

        // Automatic portrait positioning requires ownership; manual grips and seats have separate entry points.
        public void SetPortraitPosition(object owner, Vector2Int position)
        {
            lock (_GeometryLock)
                if (_CompanionLease.IsOwner(owner)) QueuePosition(position, false);
        }

        public void SetCompanionPosition(Vector2Int position) => QueuePosition(position, true);
        internal void SetManualPosition(Vector2Int position) => QueuePosition(position, false);

        void QueuePosition(Vector2Int position, bool bounded)
        {
            lock (_GeometryLock)
            {
                if (_Window == null || _Disposed) return;
                _DesiredPosition = bounded ? BoundPosition(position) : position;
                _PositionPending = true;
                if (_PositionWorker) return;
                _PositionWorker = true;
            }
            _ = FlushPositionAsync();
        }

        async Task FlushPositionAsync()
        {
            try
            {
                while (!_Disposed)
                {
                    Vector2Int position;
                    string address;
                    lock (_GeometryLock)
                    {
                        if (!_PositionPending || _Window == null) break;
                        position = _DesiredPosition;
                        address = _Window.address;
                        _PositionPending = false;
                    }
                    await _HyprlandDispatcher.MoveWindowPixelExactAsync(address, position);
                }
            }
            catch (Exception error) { if (!_Disposed) ShowError(error.ToString()); }
            finally
            {
                bool again;
                lock (_GeometryLock)
                {
                    _PositionWorker = false;
                    again = _PositionPending && !_Disposed;
                }
                if (again) _ = FlushPositionAgainAsync();
            }
        }

        Task FlushPositionAgainAsync()
        {
            lock (_GeometryLock)
            {
                if (_PositionWorker || !_PositionPending || _Disposed) return Task.CompletedTask;
                _PositionWorker = true;
            }
            return FlushPositionAsync();
        }

        public async void SetWindowSize(Vector2Int size)
        {
            if (_Window == null || _Disposed) return;
            try
            {
                await _HyprlandDispatcher.ResizewindowpixelExactWindowSizeAsync(_Window.address, size);
                TriggerImidiateUpdates();
            }
            catch (Exception error) { if (!_Disposed) ShowError(error.ToString()); }
        }

        public Vector2Int GetWindowPosition()
        {
            lock (_GeometryLock)
                return _PositionPending || _PositionWorker ? _DesiredPosition : _Window?.atVector ?? Vector2Int.zero;
        }

        private static void ShowError(object messageObject, [CallerMemberName] string callsource = "")
        {
            Console.WriteLine($"\u001b[31m{nameof(HyprlandManager)}.{callsource}: {messageObject}\u001b[0m");
        }

        public int GetWindowPid(IntPtr window)
        {
            var pid = -1;
            if (window == _XUnityWindow)
                window = _Window.addressIntPtr;
            if (_Clients.ContainsKey(window))
                pid = _Clients[window].pid;
            // ShowError($"{IntPtrToHex(window)} {pid}");
            return pid;
        }

        public List<IntPtr> FindWindowsByPid(int targetPid)
        {
            var windows = _Clients.Where(a => a.Value.pid == targetPid).Select(a => a.Key).ToList();
            // ShowError($"{targetPid} {string.Join(",",windows.Select(a => IntPtrToHex(a)))}");
            return windows;
        }

        public List<IntPtr> GetAllWindows()
        {
            var windows = _Clients.Keys.ToList();
            // ShowError(string.Join(",",windows));
            return windows;
        }

        public bool IsWindowVisible(IntPtr window)
        {
            var client = FindClientByWindowId(window);
            return ClientVisible(client, true);
        }

        bool ClientVisible(HyprlandClient client, bool ownedAudition)
        {
            if (!ObservationFresh || client == null || !client.mapped || client.hidden || _Monitors == null) return false;
            foreach (var monitor in _Monitors)
                if (monitor.id == client.monitor && !monitor.disabled && monitor.dpmsStatus &&
                    (client.pinned || client.workspace?.Matches(monitor.activeWorkspace) == true ||
                        ownedAudition && OwnedAuditionPresentable && client.workspace?.name == CompanionPresencePolicy.OwnedAuditionWorkspace))
                    return true;
            return false;
        }

        bool TryFindVisibleClient(string address, out HyprlandClient client)
        {
            client = null;
            foreach (var pair in _Clients)
                if (pair.Value.address == address) { client = pair.Value; return ClientVisible(client, false); }
            return false;
        }

        public bool TryGetSeatSupport(IntPtr window, float width, float headroom, out RectInt rect) =>
            TryGetSeatSupport(window, width, headroom, out rect, out _, out _);

        public bool TryGetSeatSupport(IntPtr window, float width, float headroom, out RectInt rect, out int pid, out int workspace, bool bottom = false)
        {
            rect = default; pid = 0; workspace = 0;
            var client = FindClientByWindowId(window);
            if (client == null || _Monitors == null) return false;
            var monitor = Array.Find(_Monitors, item => item.id == client.monitor);
            if (!CompanionPresencePolicy.SeatClientEligible(client, _Window, monitor, ObservationFresh,
                OwnedAuditionPresentable ? CompanionPresencePolicy.OwnedAuditionWorkspace : null)) return false;
            rect = new RectInt(client.atVector, client.sizeVector);
            pid = client.pid; workspace = client.workspace.id;
            return monitor != null && CompanionFramePolicy.SupportFits(rect, monitor.workArea, width, headroom, bottom);
        }

        public bool SeatPlacementSafe(IntPtr support, Vector2Int position, Rect body)
        {
            var client = FindClientByWindowId(support);
            var monitors = _Monitors;
            if (client == null || monitors == null) return false;
            var monitor = Array.Find(monitors, item => item.id == client.monitor && !item.disabled && item.dpmsStatus);
            return monitor != null && CompanionPresencePolicy.BodyFits(monitor.workArea, position, body);
        }

        public IntPtr ForegroundSeatSupport
        {
            get
            {
                var client = _Foreground;
                if (client != null) return client.addressIntPtr;
                var previous = FindClientByWindowId(_LastSeatSupport);
                return ClientVisible(previous, true) ? _LastSeatSupport : IntPtr.Zero;
            }
        }

        public async void SetTopmost(bool topmost)
        {
            var window = _Window;
            if (window == null || _Disposed) return;
            try { await _HyprlandDispatcher.SetZOrderAsync(window.address, topmost); }
            catch (Exception error) { if (!_Disposed) ShowError(error.ToString()); }
        }

        public bool IsWindowFullscreen(IntPtr window)
        {
            var client = FindClientByWindowId(window);
            var fullscreen = client?.fullscreen == 2;
            //ShowError($"{IntPtrToHex(window)} {fullscreen}");
            return fullscreen;
        }

        public bool IsWindowMaximized(IntPtr window)
        {
            var client = FindClientByWindowId(window);
            var maximixed = client?.fullscreen == 1;
            //ShowError($"{IntPtrToHex(window)} {maximixed}");
            return maximixed;
        }

        public Vector2Int GetWindowSize(IntPtr window)
        {
            var w = FindClientByWindowId(window);
            if (w == null)
                w = _Window;
            return w.sizeVector;
        }

        public Vector2Int GetTotalDisplaySize()
        {
            var rects = GetAllMonitors();
            var minX = Math.Abs(rects.Min(a => a.Rect.x));
            var minY = Math.Abs(rects.Min(a => a.Rect.y));
            var maxX = rects.Max(a => a.Rect.x);
            var maxY = rects.Max(a => a.Rect.y);
            var rightMost = rects.OrderByDescending(a => a.Rect.x).FirstOrDefault().Rect;
            var bottomMost = rects.OrderByDescending(a => a.Rect.y).FirstOrDefault().Rect;

            var width = minX + maxX + rightMost.width;
            var height = minY + maxY + bottomMost.height;
            var display = new Vector2Int(width, height);
            // ShowError(display);
            return display;
        }

        public bool GetWindowRect(IntPtr window, out RectInt rect)
        {
            var w = FindClientByWindowId(window);
            if (w == null)
            {
                ShowError($"{IntPtrToHex(window)}");
                rect = RectInt.zero;
                return false;
            }
            var pos = w.atVector;
            var size = w.sizeVector;
            // Client geometry is real geometry; seating owns its eligibility policy.
            rect = new RectInt(pos, size);
            // ShowError($"{IntPtrToHex(window)} {rect} {w.initialClass}");
            return true;
        }

        public HyprlandClient FindClientByWindowId(IntPtr window)
        {
            if (_Window == null)
                return null;
            if (window == _XUnityWindow)
                window = _Window.addressIntPtr;
            if (_Clients.ContainsKey(window))
                return _Clients[window];
            if (_Layers.ContainsKey(window))
                return _Layers[window];
            return null;
        }

        Dictionary<IntPtr, HyprlandClient> GetAllClientsOnWorkspace()
        {
            if (_Window == null)
                return null;
            var windows = new Dictionary<IntPtr, HyprlandClient>();
            foreach (var client in _Clients.ToList())
            {
                if (client.Value.workspace?.Matches(_Window.workspace) == true || client.Value.pinned)
                    windows.Add(client.Key, client.Value);
            }
            return windows;
        }

        public List<IntPtr> GetClientStackingList()
        {
            var windowsOnWorkspace = GetAllClientsOnWorkspace();
            var stacking = new List<IntPtr>();

            if (_Window != null)
            {
                // remove the avatar window
                windowsOnWorkspace.Remove(_Window.addressIntPtr);
                // add surfaces in selected layers
                stacking.AddRange(_Layers.Where(a => a.Value.monitor == _Window.monitor).Select(a => a.Key));
            }
            // add the tiled windows
            stacking.AddRange(windowsOnWorkspace.Where(a => !a.Value.floating).Select(a => a.Key));
            // add floating windows
            stacking.AddRange(windowsOnWorkspace.Where(a => a.Value.floating).OrderBy(a => a.Value.focusHistoryID).Select(a => a.Key));
            // ShowError(string.Join(",", stackingPointers));
            return stacking;
        }

        public List<(IntPtr Id, RectInt Rect)> GetAllMonitors()
        {
            var monitors = _Monitors.Select(a => (new IntPtr(a.id), new RectInt(a.position, a.size))).ToList();
            // ShowError(string.Join(";", monitors.Select(a => $"{a.Item1}:({a.Item2})")));
            return monitors;
        }

        public bool IsDesktop(IntPtr window)
        {
            // wayland handles such things via surfaces in the background layers,
            // surfaces in the background layer are already filtered out
            return false;
        }

        public bool IsDock(IntPtr window)
        {
            // if the window is on a layer its a surface
            return _Layers.ContainsKey(window);
        }

        public string GetClassName(IntPtr window)
        {
            var className = FindClientByWindowId(window)?.initialClass ?? string.Empty;
            // ShowError($"{IntPtrToHex(window)} : {className}");
            return className;
        }

        IntPtr? _XUnityWindow;

        public void SetXUnityWindow(IntPtr unityWindow)
        {
            // store the XWindow so that the hyprland manager recognizes it as the avatarwindow
            // ShowError(IntPtrToHex(unityWindow));
            _XUnityWindow = unityWindow;
        }

        volatile IntPtr _SnappedWindow;

        public void SetSnapedWindow(IntPtr window)
        {
            // A support never changes the mascot's workspace or its pin preference.
            _SnappedWindow = window;
            TriggerImidiateUpdates();
        }
    }
}