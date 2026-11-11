using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using MarcusW.VncClient.Protocol.MessageTypes;
using MarcusW.VncClient.Protocol.Services;
using MarcusW.VncClient.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using ASize = Avalonia.Size;

namespace MarcusW.VncClient.Avalonia.Tests;

public class DisplayScalingTests
{
    [AvaloniaTheory]
    [InlineData(1, 1280, 720)]
    [InlineData(1.25, 1600, 900)]
    [InlineData(1.5, 1920, 1080)]
    [InlineData(2, 2560, 1440)]
    public async Task Default_Viewer_Requests_Physical_Resolution_And_Maps_Input(double scale, int width, int height)
    {
        using var session = await Session.Connect();
        using var viewer = new TestView { Connection = session.Connection };
        var window = new Window { Width = 1280, Height = 720, Content = viewer };
        try
        {
            window.SetRenderScaling(scale);
            window.Show();
            await SettleResize();
            Assert.Equal(new Size(width, height), session.Requests.Last());
            Assert.True(viewer.AutoResizeRemote);
            Assert.Equal(BitmapInterpolationMode.None, RenderOptions.GetBitmapInterpolationMode(viewer));

            // Simulate the requested framebuffer arriving from the server.
            using (viewer.GrabFramebufferReference(new Size(width, height), ImmutableHashSet<Screen>.Empty)) { }
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new ASize(1280, 720), viewer.MeasureFramebuffer());
            Assert.Equal(new Position((int)(100 * scale), (int)(200 * scale)), viewer.Map(new Point(100, 200)));
            Assert.Equal(new Position(width - 1, height - 1), viewer.Map(new Point(99999, 99999)));
            Assert.Equal(Position.Origin, viewer.Map(new Point(-20, -20)));
            window.MouseMove(new Point(100, 200));
            window.MouseDown(new Point(100, 200), MouseButton.Left);
            window.MouseUp(new Point(100, 200), MouseButton.Left);
            window.MouseWheel(new Point(100, 200), new Vector(0, 1));
            Assert.NotEmpty(session.PointerMessages);
            Assert.All(session.PointerMessages, m => Assert.Equal(new Position((int)(100 * scale), (int)(200 * scale)), m.PointerPosition));
            Assert.Contains(session.PointerMessages, m => m.PressedButtons.HasFlag(MouseButtons.Left));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Monitor_Scale_And_Fullscreen_Size_Changes_Update_Without_Reconnecting()
    {
        using var session = await Session.Connect();
        using var viewer = new TestView { Connection = session.Connection };
        var window = new Window { Width = 800, Height = 600, Content = viewer };
        try
        {
            window.Show();
            await SettleResize();
            using (viewer.GrabFramebufferReference(new Size(800, 600), ImmutableHashSet<Screen>.Empty)) { }
            window.SetRenderScaling(1.25);
            await SettleResize();
            Assert.Equal(new Size(1000, 750), session.Requests.Last());
            // The old framebuffer stays at native pixel size until the server supplies the new one.
            Assert.Equal(new ASize(640, 480), viewer.MeasureFramebuffer());
            Assert.Equal(new Position(125, 250), viewer.Map(new Point(100, 200)));

            window.Width = 1536;
            window.Height = 864;
            await SettleResize();
            Assert.Equal(new Size(1920, 1080), session.Requests.Last());
            window.Width = 800;
            window.Height = 600;
            await SettleResize();
            Assert.Equal(new Size(1000, 750), session.Requests.Last());
            window.SetRenderScaling(1);
            await SettleResize();
            Assert.Equal(new Size(800, 600), session.Requests.Last());
            Assert.Equal(new ASize(800, 600), viewer.MeasureFramebuffer());
            Assert.Equal(new Position(100, 200), viewer.Map(new Point(100, 200)));
            Assert.Same(session.Connection, viewer.Connection);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task OptimalSize_OptOut_Detach_And_Background_Capability_Notification()
    {
        using var session = await Session.Connect(resizable: false);
        using var viewer = new TestView { Connection = session.Connection, OptimalSize = new ASize(640, 480) };
        var window = new Window { Width = 800, Height = 600, Content = viewer };
        try
        {
            window.SetRenderScaling(1.25);
            window.Show();
            await SettleResize();
            Assert.Empty(session.Requests);
            await Task.Run(() => session.State.ServerSupportsExtendedDesktopSize = true);
            await SettleResize();
            // Initial server framebuffer is 320x240.
            Assert.Equal(new Size(800, 600), session.Requests.Last());
            int count = session.Requests.Count;
            viewer.AutoResizeRemote = false;
            window.SetRenderScaling(1.5);
            await SettleResize();
            Assert.Equal(count, session.Requests.Count);
            viewer.AutoResizeRemote = true;
            await SettleResize();
            Assert.Equal(new Size(960, 720), session.Requests.Last());

            // A reconnect capability cycle must permit the same requested size again.
            count = session.Requests.Count;
            await Task.Run(() => {
                session.State.ServerSupportsExtendedDesktopSize = false;
                session.State.ServerSupportsExtendedDesktopSize = true;
            });
            await SettleResize();
            Assert.True(session.Requests.Count > count);
            count = session.Requests.Count;
            window.Content = null;
            window.SetRenderScaling(2);
            await SettleResize();
            Assert.Equal(count, session.Requests.Count);
            window.Content = viewer;
            await SettleResize();
            Assert.Equal(new Size(1280, 960), session.Requests.Last());
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void Alternating_Pixels_Remain_Sharp_When_Drawn(double scale)
    {
        using var viewer = new TestView();
        var window = new Window { Width = 64, Height = 64, Content = viewer };
        try
        {
            window.SetRenderScaling(scale);
            window.Show();
            using (var fb = viewer.GrabFramebufferReference(new Size(32, 32), ImmutableHashSet<Screen>.Empty))
            {
                // A black/white pattern has no dependence on RGB/BGR channel ordering.
                var row = Enumerable.Range(0, 32).Select(x => x % 2 == 0 ? unchecked((int)0xff000000) : -1).ToArray();
                for (int y = 0; y < 32; y++)
                    Marshal.Copy(row, 0, fb.Address + y * 32 * 4, 32);
            }
            Dispatcher.UIThread.RunJobs();
            using var output = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96 * scale, 96 * scale));
            output.Render(viewer);
            byte[] pixels = new byte[64 * 64 * 4];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { output.CopyPixels(new PixelRect(0, 0, 64, 64), handle.AddrOfPinnedObject(), pixels.Length, 64 * 4); }
            finally { handle.Free(); }
            for (int x = 0; x < 32; x++)
            {
                int index = (4 * 64 + x) * 4;
                Assert.Equal(x % 2 == 0 ? 0 : 255, pixels[index]);
                Assert.Equal(255, pixels[index + 3]);
            }
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    [InlineData(double.NaN, 600)]
    [InlineData(double.PositiveInfinity, 600)]
    public void Invalid_Resize_Dimensions_Are_Not_Sent(double width, double height)
        => Assert.Null(FramebufferGeometry.GetRemoteSize(new ASize(width, height), 1.25));

    [Fact]
    public void Fractional_Origins_And_Protocol_Limits_Are_Handled()
    {
        var origin = new Point(0.3, 10.5);
        var offset = FramebufferGeometry.SnapOffset(origin, 1.25);
        var pixels = new PixelSize(1600, 900);
        var rect = FramebufferGeometry.GetDisplayRect(pixels, 1.25, offset);
        Assert.Equal(Math.Round((origin.X + rect.X) * 1.25), (origin.X + rect.X) * 1.25, 8);
        Assert.Equal(new Position(100, 100), FramebufferGeometry.GetRemotePosition(new Point(offset.X + 80, offset.Y + 80), pixels, 1.25, offset));
        Assert.Equal(new Size(65535, 65535), FramebufferGeometry.GetRemoteSize(new ASize(100000, 100000), 2));
        Assert.Equal(new Size(1001, 751), FramebufferGeometry.GetRemoteSize(new ASize(800.8, 600.8), 1.25));
    }

    private static async Task SettleResize()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(650);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class TestView : VncView
    {
        public ASize MeasureFramebuffer() => base.MeasureOverride(ASize.Infinity);
        public Position Map(Point point)
        {
            Assert.True(TryGetFramebufferPosition(point, out var position));
            return position;
        }
    }

    private sealed class Session : IDisposable
    {
        public RfbConnection Connection { get; private set; } = null!;
        public ProtocolState State { get; private set; } = null!;
        public List<Size> Requests { get; } = new();
        public List<PointerEventMessage> PointerMessages { get; } = new();

        public static async Task<Session> Connect(bool resizable = true)
        {
            var session = new Session();
            var protocol = new Mock<IRfbProtocolImplementation>();
            protocol.Setup(p => p.CreateStateObject(It.IsAny<RfbConnectionContext>()))
                .Returns((RfbConnectionContext c) => session.State = new ProtocolState(c));
            protocol.Setup(p => p.CreateEncodingTypesCollection(It.IsAny<RfbConnectionContext>()))
                .Returns((RfbConnectionContext c) => DefaultImplementation.GetDefaultEncodingTypes(c).ToImmutableHashSet());
            protocol.Setup(p => p.CreateMessageTypesCollection(It.IsAny<RfbConnectionContext>()))
                .Returns((RfbConnectionContext c) => DefaultImplementation.GetDefaultMessageTypes(c).ToImmutableHashSet());
            var transport = new Mock<ITransportConnector>();
            transport.Setup(t => t.ConnectAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<ITransport>());
            protocol.Setup(p => p.CreateTransportConnector(It.IsAny<RfbConnectionContext>())).Returns(transport.Object);
            var handshake = new Mock<IRfbHandshaker>();
            handshake.Setup(h => h.DoHandshakeAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ITransport?)null);
            protocol.Setup(p => p.CreateRfbHandshaker(It.IsAny<RfbConnectionContext>())).Returns(handshake.Object);
            var initializer = new Mock<IRfbInitializer>();
            initializer.Setup(i => i.InitializeAsync(It.IsAny<CancellationToken>())).Returns(() => {
                session.State.RemoteFramebufferSize = new Size(320, 240);
                session.State.ServerSupportsExtendedDesktopSize = resizable;
                return Task.CompletedTask;
            });
            protocol.Setup(p => p.CreateRfbInitializer(It.IsAny<RfbConnectionContext>())).Returns(initializer.Object);
            protocol.Setup(p => p.CreateMessageReceiver(It.IsAny<RfbConnectionContext>())).Returns(Mock.Of<IRfbMessageReceiver>());
            var sender = new Mock<IRfbMessageSender>();
            sender.Setup(s => s.EnqueueMessage(It.IsAny<IOutgoingMessage<SetDesktopSizeMessageType>>(), It.IsAny<CancellationToken>()))
                .Callback<IOutgoingMessage<SetDesktopSizeMessageType>, CancellationToken>((m, _) =>
                    session.Requests.Add(((SetDesktopSizeMessage)m).MutationFunc(session.State.RemoteFramebufferSize, session.State.RemoteFramebufferLayout).size));
            sender.Setup(s => s.EnqueueMessage(It.IsAny<IOutgoingMessage<PointerEventMessageType>>(), It.IsAny<CancellationToken>()))
                .Callback<IOutgoingMessage<PointerEventMessageType>, CancellationToken>((m, _) => session.PointerMessages.Add((PointerEventMessage)m));
            protocol.Setup(p => p.CreateMessageSender(It.IsAny<RfbConnectionContext>())).Returns(sender.Object);
            session.Connection = await new global::MarcusW.VncClient.VncClient(NullLoggerFactory.Instance, protocol.Object)
                .ConnectAsync(new ConnectParameters {
                    TransportParameters = new TcpTransportParameters { Host = "localhost", Port = 5900 },
                    AuthenticationHandler = Mock.Of<IAuthenticationHandler>()
                });
            return session;
        }

        public void Dispose() => Connection.Dispose();
    }
}
