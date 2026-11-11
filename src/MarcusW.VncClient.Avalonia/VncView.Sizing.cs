using System;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;

namespace MarcusW.VncClient.Avalonia
{
    public partial class VncView
    {
        public static readonly DirectProperty<VncView, bool> AutoResizeRemoteProperty =
            AvaloniaProperty.RegisterDirect<VncView, bool>(nameof(AutoResizeRemote), o => o.AutoResizeRemote, (o, v) => o.AutoResizeRemote = v, true);

        public static readonly DirectProperty<VncView, global::Avalonia.Size?> OptimalSizeProperty =
            AvaloniaProperty.RegisterDirect<VncView, global::Avalonia.Size?>(nameof(OptimalSize), o => o.OptimalSize, (o, v) => o.OptimalSize = v);

        private bool _autoResizeRemote = true;
        private global::Avalonia.Size? _optimalSize;
        private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private Size? _lastRequestedSize;
        private bool _sizingDisposed;

        /// <summary>
        /// Automatically requests a remote desktop matching the viewer's physical pixel size.
        /// Enabled by default. Requires server resize support.
        /// </summary>
        public bool AutoResizeRemote
        {
            get => _autoResizeRemote;
            set
            {
                if (SetAndRaise(AutoResizeRemoteProperty, ref _autoResizeRemote, value))
                {
                    _lastRequestedSize = null;
                    ScheduleSizeUpdate();
                }
            }
        }

        /// <summary>
        /// Optional target size in Avalonia logical units, converted using the current monitor scale.
        /// Null (the default) uses the viewer bounds. Useful when hosted in a ScrollViewer.
        /// </summary>
        public global::Avalonia.Size? OptimalSize
        {
            get => _optimalSize;
            set
            {
                if (SetAndRaise(OptimalSizeProperty, ref _optimalSize, value))
                    ScheduleSizeUpdate();
            }
        }

        private void InitSizing()
        {
            _resizeTimer.Tick += OnResizeTimer;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == BoundsProperty && OptimalSize == null)
                ScheduleSizeUpdate();
        }

        protected override void OnDisplayScaleChanged()
        {
            base.OnDisplayScaleChanged();
            ScheduleSizeUpdate();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _resizeTimer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        private void ScheduleSizeUpdate()
        {
            _resizeTimer.Stop();
            if (!_sizingDisposed && AutoResizeRemote && VisualRoot != null)
                _resizeTimer.Start();
        }

        private void OnResizeTimer(object? sender, EventArgs e)
        {
            _resizeTimer.Stop();
            SendManualSizeUpdate();
        }

        private void ResetSizeRequests()
        {
            _resizeTimer.Stop();
            _lastRequestedSize = null;
        }

        private void SendInitialSizeUpdate()
        {
            RfbConnection? connection = Connection;
            if (connection == null || _sizingDisposed)
                return;

            // Keep observing capabilities across reconnects. Protocol notifications originate
            // on background threads; all visual reads and resize scheduling stay on the UI thread.
            void PropertyChangedHandler(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(connection.DesktopIsResizable))
                    return;
                Dispatcher.UIThread.Post(() => {
                    if (_sizingDisposed || !ReferenceEquals(Connection, connection))
                        return;
                    ResetSizeRequests();
                    if (connection.DesktopIsResizable)
                        SendManualSizeUpdate();
                });
            }

            connection.PropertyChanged += PropertyChangedHandler;
            _connectionDetachDisposable.Add(Disposable.Create(() => connection.PropertyChanged -= PropertyChangedHandler));
            SendManualSizeUpdate();
        }

        private void SendManualSizeUpdate()
        {
            var connection = Connection;
            if (_sizingDisposed || !AutoResizeRemote || VisualRoot == null || connection == null || !connection.DesktopIsResizable)
                return;

            var requestedSize = FramebufferGeometry.GetRemoteSize(OptimalSize ?? Bounds.Size, DisplayScale);
            if (!requestedSize.HasValue)
                return; // Ignore zero/invalid bounds during layout or minimization.
            var size = requestedSize.Value;
            if (_lastRequestedSize == size || (_lastRequestedSize == null && connection.RemoteFramebufferSize == size))
                return;

            if (connection.EnqueueMessage(new SetDesktopSizeMessage((currentSize, currentLayout) => {
                var rectangle = new Rectangle(Position.Origin, size);
                var firstScreen = currentLayout.FirstOrDefault();
                var screen = currentLayout.Count == 0
                    ? new Screen(1, rectangle, 0)
                    : new Screen(firstScreen.Id, rectangle, firstScreen.Flags);
                return (size, ImmutableHashSet.Create(screen));
            })))
                _lastRequestedSize = size;
        }

        private void DisposeSizing()
        {
            _sizingDisposed = true;
            _resizeTimer.Stop();
            _resizeTimer.Tick -= OnResizeTimer;
        }
    }
}
