using System;
using Avalonia;

namespace MarcusW.VncClient.Avalonia
{
    // Shared conversion rules for resize requests, drawing and input.
    internal static class FramebufferGeometry
    {
        public static Size? GetRemoteSize(global::Avalonia.Size logicalSize, double scale)
        {
            if (!double.IsFinite(logicalSize.Width) || !double.IsFinite(logicalSize.Height)
                || logicalSize.Width <= 0 || logicalSize.Height <= 0)
                return null;

            return new Size(ToDimension(logicalSize.Width * scale), ToDimension(logicalSize.Height * scale));
        }

        private static int ToDimension(double pixels)
            => (int)Math.Clamp(Math.Round(pixels, MidpointRounding.AwayFromZero), 1, ushort.MaxValue);

        public static Point SnapOffset(Point origin, double scale)
            => new Point(Math.Round(origin.X * scale) / scale - origin.X,
                Math.Round(origin.Y * scale) / scale - origin.Y);

        public static Rect GetDisplayRect(PixelSize pixels, double scale, Point offset)
            => new Rect(offset, new global::Avalonia.Size(pixels.Width / scale, pixels.Height / scale));

        public static Position GetRemotePosition(Point point, PixelSize pixels, double scale, Point offset)
            => new Position(
                (int)Math.Clamp(Math.Floor((point.X - offset.X) * scale), 0, Math.Min(pixels.Width - 1, ushort.MaxValue)),
                (int)Math.Clamp(Math.Floor((point.Y - offset.Y) * scale), 0, Math.Min(pixels.Height - 1, ushort.MaxValue)));
    }
}
