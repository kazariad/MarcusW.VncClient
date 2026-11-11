using System;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace MarcusW.VncClient.Avalonia
{
    public partial class VncView
    {
        private static readonly TimeSpan ClipboardPollingInterval = TimeSpan.FromMilliseconds(500);

        private DispatcherTimer? _clipboardMonitorTimer;
        private bool _clipboardReadInProgress;
        private bool _hasObservedClipboardText;
        private bool _clipboardSendPending;
        private string _lastObservedClipboardText = string.Empty;
        private int _clipboardRevision;

        private void StartClipboardMonitoring(RfbConnection connection)
        {
            if (!ReferenceEquals(Connection, connection) || _clipboardMonitorTimer != null)
                return;

            ResetClipboardTracking();

            var timer = new DispatcherTimer(DispatcherPriority.Background) {
                Interval = ClipboardPollingInterval
            };

            void HandleTimerTick(object? sender, EventArgs e) => _ = SynchronizeLocalClipboardAsync(connection);
            void HandleConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
            {
                Dispatcher.UIThread.Post(() => {
                    if (!ReferenceEquals(Connection, connection))
                        return;

                    ResetClipboardTracking();
                    if (e.CurrentState == ConnectionState.Connected)
                        _ = SynchronizeLocalClipboardAsync(connection);
                });
            }

            timer.Tick += HandleTimerTick;
            connection.ConnectionStateChanged += HandleConnectionStateChanged;
            _clipboardMonitorTimer = timer;

            _connectionDetachDisposable.Add(Disposable.Create(() => {
                timer.Stop();
                timer.Tick -= HandleTimerTick;
                connection.ConnectionStateChanged -= HandleConnectionStateChanged;

                if (ReferenceEquals(_clipboardMonitorTimer, timer))
                    _clipboardMonitorTimer = null;

                ResetClipboardTracking();
            }));

            timer.Start();

            if (connection.ConnectionState == ConnectionState.Connected)
                _ = SynchronizeLocalClipboardAsync(connection);
        }

        private async Task SynchronizeLocalClipboardAsync(RfbConnection connection)
        {
            if (_clipboardReadInProgress ||
                !ReferenceEquals(Connection, connection) ||
                connection.ConnectionState != ConnectionState.Connected)
            {
                return;
            }

            IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
                return;

            _clipboardReadInProgress = true;
            int revision = Volatile.Read(ref _clipboardRevision);

            try
            {
                string? clipboardText = await clipboard.TryGetTextAsync().ConfigureAwait(true);

                // A connection change or an incoming server update while the read was pending
                // makes the result stale. The next timer tick will read the current value.
                if (!ReferenceEquals(Connection, connection) ||
                    connection.ConnectionState != ConnectionState.Connected ||
                    revision != Volatile.Read(ref _clipboardRevision))
                {
                    return;
                }

                string normalizedText = clipboardText ?? string.Empty;
                bool hadObservedClipboardText = _hasObservedClipboardText;
                if (hadObservedClipboardText &&
                    string.Equals(_lastObservedClipboardText, normalizedText, StringComparison.Ordinal) &&
                    !_clipboardSendPending)
                {
                    return;
                }

                _hasObservedClipboardText = true;
                _lastObservedClipboardText = normalizedText;

                // Do not clear the server clipboard merely because no text format was present
                // when monitoring started. A later transition from text to no text is a real
                // clipboard change and is represented by an empty ClientCutText message.
                if (clipboardText != null || hadObservedClipboardText || _clipboardSendPending)
                {
                    // The sender can disappear during a connection-state race. Retain a failed
                    // send so the unchanged clipboard value (including an empty value) is retried.
                    _clipboardSendPending = !connection.SendClipboardText(normalizedText);
                }
            }
            catch
            {
                // Clipboard access can be unavailable temporarily (for example before the
                // control is attached to a top-level window). Keep monitoring and retry later.
            }
            finally
            {
                _clipboardReadInProgress = false;
            }
        }

        private async Task ApplyServerClipboardTextAsync(string text, int revision)
        {
            if (revision != Volatile.Read(ref _clipboardRevision))
                return;

            IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
                return;

            // Record the value before writing it. A pending or subsequent monitor read then
            // treats this as an observed server value instead of echoing it back to the server.
            _hasObservedClipboardText = true;
            _clipboardSendPending = false;
            _lastObservedClipboardText = text;

            try
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            }
            catch
            {
                // Keep server-to-client clipboard failures non-fatal, matching clipboard reads.
            }
        }

        private void ResetClipboardTracking()
        {
            Interlocked.Increment(ref _clipboardRevision);
            _hasObservedClipboardText = false;
            _clipboardSendPending = false;
            _lastObservedClipboardText = string.Empty;
        }
    }
}
