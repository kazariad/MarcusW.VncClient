using System;
using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MarcusW.VncClient.Avalonia.Adapters.Rendering;
using MarcusW.VncClient.Rendering;
using IRenderTarget = MarcusW.VncClient.Rendering.IRenderTarget;

namespace MarcusW.VncClient.Avalonia
{
    /// <summary>
    /// A control that provides access to a target framebuffer for rendering frames onto it.
    /// </summary>
    /// <remarks>
    /// This control ignores the VNC multi-screen feature and therefore renders all screens (the whole framebuffer) in one piece.
    /// </remarks>
    public class RfbRenderTarget : Control, IRenderTarget, IDisposable
    {
        private volatile WriteableBitmap? _bitmap;
        private readonly object _bitmapReplacementLock = new object();

        private volatile bool _disposed;
        private TopLevel? _topLevel;
        private Point _framebufferOffset;

        /// <summary>Current physical pixels per Avalonia logical unit. Access on the UI thread.</summary>
        protected double DisplayScale { get; private set; } = 1;

        public RfbRenderTarget()
        {
            ClipToBounds = true;
            UseLayoutRounding = true;
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
            LayoutUpdated += OnLayoutUpdated;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_disposed)
                return;
            _topLevel = TopLevel.GetTopLevel(this);
            if (_topLevel != null)
                _topLevel.ScalingChanged += OnScalingChanged;
            UpdateDisplayScale();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            DetachTopLevel();
            base.OnDetachedFromVisualTree(e);
        }

        private void DetachTopLevel()
        {
            if (_topLevel != null)
                _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
        }

        private void OnScalingChanged(object? sender, EventArgs e) => UpdateDisplayScale();

        private void UpdateDisplayScale()
        {
            DisplayScale = _topLevel?.RenderScaling ?? 1;
            UpdateFramebufferOffset();
            InvalidateMeasure();
            InvalidateVisual();
            OnDisplayScaleChanged();
        }

        /// <summary>Called on attachment and whenever the host monitor's scale changes.</summary>
        protected virtual void OnDisplayScaleChanged() { }

        private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateFramebufferOffset();

        private void UpdateFramebufferOffset()
        {
            var origin = VisualRoot is Visual root ? this.TranslatePoint(default, root) : null;
            var offset = FramebufferGeometry.SnapOffset(origin ?? default, DisplayScale);
            if (_framebufferOffset == offset)
                return;
            _framebufferOffset = offset;
            InvalidateVisual();
        }

        /// <summary>Maps input to the actual framebuffer, including during a pending remote resize.</summary>
        protected bool TryGetFramebufferPosition(Point point, out Position position)
        {
            lock (_bitmapReplacementLock)
            {
                if (_bitmap == null)
                {
                    position = default;
                    return false;
                }
                position = FramebufferGeometry.GetRemotePosition(point, _bitmap.PixelSize, DisplayScale, _framebufferOffset);
                return true;
            }
        }

        /// <inheritdoc />
        public virtual IFramebufferReference GrabFramebufferReference(Size size, IImmutableSet<Screen> layout)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RfbRenderTarget));

            PixelSize requiredPixelSize = Conversions.GetPixelSize(size);

            // Creation of a new buffer necessary?
            // No synchronization necessary, because the only conflicting write operation is
            // in this same method and the field is marked volatile to avoid caching issues.
            // ReSharper disable once InconsistentlySynchronizedField
            bool sizeChanged = _bitmap == null || _bitmap.PixelSize != requiredPixelSize;

            WriteableBitmap bitmap;
            if (sizeChanged)
            {
                // Create new bitmap with required size and the format that is preferred by the current platform (therefore 'null').
                // Store remote pixels unchanged. Display scaling belongs to UI-thread drawing,
                // not bitmap allocation on the receiver thread or cached bitmap DPI.
                bitmap = new WriteableBitmap(requiredPixelSize, new Vector(96, 96), null);

                // Wait for the rendering being finished before replacing the bitmap
                lock (_bitmapReplacementLock)
                {
                    _bitmap?.Dispose();
                    _bitmap = bitmap;
                }
            }
            else
            {
                // ReSharper disable once InconsistentlySynchronizedField
                bitmap = _bitmap!;
            }

            // Lock framebuffer and return as converted reference
            // ReSharper disable once InconsistentlySynchronizedField
            ILockedFramebuffer lockedFramebuffer = bitmap.Lock();

            return new AvaloniaFramebufferReference(lockedFramebuffer, () => Dispatcher.UIThread.Post(() => {
                if (_disposed)
                    return;
                if (sizeChanged)
                    InvalidateMeasure();
                InvalidateVisual();
            }));
        }

        /// <inheritdoc />
        public override void Render(DrawingContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            // Ensure the bitmap does not get disposed or replaced during rendering
            lock (_bitmapReplacementLock)
            {
                if (_bitmap == null)
                    return;

                var rect = FramebufferGeometry.GetDisplayRect(_bitmap.PixelSize, DisplayScale, _framebufferOffset);
                context.DrawImage(_bitmap, rect);
            }
        }

        /// <inheritdoc />
        protected override global::Avalonia.Size MeasureOverride(global::Avalonia.Size availableSize)
        {
            // Retrieve bitmap size
            global::Avalonia.Size? bitmapSize;
            lock (_bitmapReplacementLock)
                bitmapSize = _bitmap == null ? null : FramebufferGeometry.GetDisplayRect(_bitmap.PixelSize, DisplayScale, default).Size;

            // Has any bitmap been rendered yet?
            if (!bitmapSize.HasValue)
                //return global::Avalonia.Size.Empty;
                return AvaloniaExtensions.EmptySize;

            // Request the size of the current bitmap
            return bitmapSize.Value;
        }

        /// <inheritdoc />
        public void Dispose() => Dispose(true);

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                DetachTopLevel();
                LayoutUpdated -= OnLayoutUpdated;
                lock (_bitmapReplacementLock)
                {
                    _bitmap?.Dispose();
                    _bitmap = null;
                }
            }

            _disposed = true;
        }
    }
}
