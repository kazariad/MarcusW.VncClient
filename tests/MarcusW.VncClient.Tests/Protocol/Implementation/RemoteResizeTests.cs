using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation;
using MarcusW.VncClient.Protocol.Implementation.EncodingTypes.Pseudo;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Incoming;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Protocol.Implementation.Services.Communication;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using MarcusW.VncClient.Protocol.Services;
using MarcusW.VncClient.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MarcusW.VncClient.Tests.Protocol.Implementation
{
    public class RemoteResizeTests
    {
        [Fact]
        public void Defaults_To_Auto()
        {
            Assert.Equal(RemoteResizeMode.Auto, new ConnectParameters().RemoteResizeMode);
        }

        [Theory]
        [InlineData(RemoteResizeMode.Auto, true, false)]
        [InlineData(RemoteResizeMode.Auto, true, true)]
        [InlineData(RemoteResizeMode.On, true, false)]
        [InlineData(RemoteResizeMode.On, true, true)]
        [InlineData(RemoteResizeMode.Off, false, false)]
        [InlineData(RemoteResizeMode.Off, false, true)]
        public async Task Advertises_Resize_Independently_Of_Streaming(RemoteResizeMode mode, bool advertised, bool streaming)
        {
            var context = CreateContext(mode);
            context.Connection.Parameters.EnableContinuousUpdates = streaming;
            using var stream = new MemoryStream();
            context.Transport = TransportFor(stream);
            using var sender = new RfbMessageSender(context);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            sender.StartSendLoop();
            try
            {
                sender.EnqueueInitialMessages(timeout.Token);
                await sender.SendMessageAndWaitAsync(new FramebufferUpdateRequestMessage(true,
                    new Rectangle(0, 0, 800, 600)), timeout.Token).WaitAsync(timeout.Token);
            }
            finally
            {
                await sender.StopSendLoopAsync();
            }

            byte[] bytes = stream.ToArray();
            Assert.Equal(2, bytes[20]); // SetEncodings follows the 20-byte SetPixelFormat.
            int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(22));
            int[] encodings = Enumerable.Range(0, count)
                .Select(i => BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(24 + i * 4))).ToArray();
            Assert.Equal(advertised, encodings.Contains(-308)); // ExtendedDesktopSize
            Assert.Contains(-223, encodings); // Legacy DesktopSize remains available.
            Assert.Equal(streaming, encodings.Contains(-312)); // Fence is independent of resizing.
            Assert.Equal(streaming, encodings.Contains(-313)); // ContinuousUpdates is independent of resizing.
            Assert.Equal(3, bytes[24 + count * 4]);
            Assert.Equal(0, bytes[25 + count * 4]); // Non-incremental request elicits confirmation.
            Assert.False(context.Connection.DesktopIsResizable);
        }

        [Theory]
        [InlineData(RemoteResizeMode.Auto)]
        [InlineData(RemoteResizeMode.On)]
        [InlineData(RemoteResizeMode.Off)]
        public void Does_Not_Send_Resize_Before_Confirmation(RemoteResizeMode mode)
        {
            var context = CreateContext(mode);
            using var sender = new RfbMessageSender(context);
            Assert.Throws<InvalidOperationException>(() => sender.EnqueueMessage(ResizeRequest()));
            Assert.False(context.Connection.DesktopIsResizable);
        }

        [Theory]
        [InlineData(RemoteResizeMode.Auto, true)]
        [InlineData(RemoteResizeMode.On, true)]
        [InlineData(RemoteResizeMode.Off, false)]
        public void Confirmation_Publishes_Layout_Before_Enabling_Requests(RemoteResizeMode mode, bool enabled)
        {
            var context = CreateContext(mode);
            bool notified = false;
            using var sender = new RfbMessageSender(context);
            context.Connection.PropertyChanged += (_, e) => {
                if (e.PropertyName != nameof(RfbConnection.DesktopIsResizable))
                    return;
                notified = true;
                Assert.Equal(42u, Assert.Single(context.Connection.RemoteFramebufferLayout).Id);
                // Automatic clients can queue their first request from this notification.
                sender.EnqueueMessage(ResizeRequest());
            };

            using var payload = ScreenPayload();
            new ExtendedDesktopSizeEncodingType(context).ReadPseudoEncoding(payload, new Rectangle(0, 0, 800, 600));
            Assert.Equal(payload.Length, payload.Position);
            Assert.Equal(enabled, notified);
            Assert.Equal(enabled, context.Connection.DesktopIsResizable);
            Assert.True(context.GetState<ProtocolState>().ServerSupportsExtendedDesktopSize);

            if (!enabled)
                Assert.Throws<InvalidOperationException>(() => sender.EnqueueMessage(ResizeRequest()));
            else
            {
                using var output = new MemoryStream();
                var writer = context.SupportedMessageTypes!.OfType<SetDesktopSizeMessageType>().Single();
                writer.WriteToTransport(ResizeRequest(), TransportFor(output));
                byte[] bytes = output.ToArray();
                Assert.Equal(251, bytes[0]);
                Assert.Equal(1024, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2)));
                Assert.Equal(768, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4)));
                Assert.Equal(42u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)));
                Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)));
            }
        }

        [Fact]
        public void Auto_Falls_Back_And_Still_Accepts_Legacy_Server_Resize()
        {
            var context = CreateContext(RemoteResizeMode.Auto);
            var reader = new FramebufferUpdateMessageType(context);
            // A server without the extension can send an ordinary empty update.
            using var emptyUpdate = new MemoryStream(new byte[3]);
            reader.ReadMessage(TransportFor(emptyUpdate));
            Assert.False(context.Connection.DesktopIsResizable);

            using var noPayload = new MemoryStream();
            new DesktopSizeEncodingType(context).ReadPseudoEncoding(noPayload, new Rectangle(0, 0, 1024, 768));
            Assert.Equal(new Size(1024, 768), context.Connection.RemoteFramebufferSize);
            Assert.False(context.Connection.DesktopIsResizable);
        }

        [Fact]
        public void Rejected_Resize_Consumes_Payload_And_Preserves_Current_Layout()
        {
            var context = CreateContext(RemoteResizeMode.Auto);
            var reader = new ExtendedDesktopSizeEncodingType(context);
            using var initial = ScreenPayload();
            reader.ReadPseudoEncoding(initial, new Rectangle(0, 0, 800, 600));
            var originalLayout = context.Connection.RemoteFramebufferLayout;

            // Failed replies have undefined dimensions/layout, which must not be applied.
            using var rejected = ScreenPayload();
            reader.ReadPseudoEncoding(rejected, new Rectangle(1, 1, 0, 0));
            Assert.Equal(rejected.Length, rejected.Position);
            Assert.Equal(new Size(800, 600), context.Connection.RemoteFramebufferSize);
            Assert.Same(originalLayout, context.Connection.RemoteFramebufferLayout);
            Assert.True(context.Connection.DesktopIsResizable);
        }

        [Fact]
        public void New_Session_Does_Not_Reuse_Previous_Resize_Capability()
        {
            var context = CreateContext(RemoteResizeMode.Auto);
            using var initial = ScreenPayload();
            new ExtendedDesktopSizeEncodingType(context).ReadPseudoEncoding(initial, new Rectangle(0, 0, 800, 600));
            Assert.True(context.Connection.DesktopIsResizable);

            var reconnected = InitializeContext(context.Connection);
            Assert.False(reconnected.Connection.DesktopIsResizable);
            Assert.False(reconnected.GetState<ProtocolState>().ServerSupportsExtendedDesktopSize);
        }

        [Fact]
        public void Configuration_Is_Validated_And_Frozen()
        {
            var parameters = new ConnectParameters {
                TransportParameters = new TcpTransportParameters { Host = "localhost", Port = 5900 },
                AuthenticationHandler = Mock.Of<IAuthenticationHandler>(),
                RemoteResizeMode = (RemoteResizeMode)99
            };
            Assert.Throws<ConnectParametersValidationException>(() => parameters.ValidateAndFreezeRecursively());
            parameters.RemoteResizeMode = RemoteResizeMode.Off;
            parameters.ValidateAndFreezeRecursively();
            Assert.Throws<InvalidOperationException>(() => parameters.RemoteResizeMode = RemoteResizeMode.Auto);
        }

        private static RfbConnectionContext CreateContext(RemoteResizeMode mode)
        {
            var parameters = new ConnectParameters {
                RemoteResizeMode = mode,
                PostInitializationDelay = TimeSpan.Zero,
                PostSetEncodingsDelay = TimeSpan.Zero
            };
            return InitializeContext(new RfbConnection(new DefaultImplementation(), NullLoggerFactory.Instance, parameters));
        }

        private static RfbConnectionContext InitializeContext(RfbConnection connection)
        {
            var context = new RfbConnectionContext(connection);
            var state = new ProtocolState(context);
            context.State = state;
            context.SupportedEncodingTypes = DefaultImplementation.GetDefaultEncodingTypes(context).ToImmutableHashSet();
            context.SupportedMessageTypes = DefaultImplementation.GetDefaultMessageTypes(context).ToImmutableHashSet();
            context.MessageSender = Mock.Of<IRfbMessageSender>();
            state.Prepare();
            state.RemoteFramebufferSize = new Size(800, 600);
            state.RemoteFramebufferFormat = PixelFormat.Plain;
            return context;
        }

        private static ITransport TransportFor(Stream stream)
        {
            var transport = new Mock<ITransport>();
            transport.SetupGet(t => t.Stream).Returns(stream);
            return transport.Object;
        }

        private static MemoryStream ScreenPayload()
        {
            byte[] payload = new byte[20];
            payload[0] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 42);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(12), 800);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(14), 600);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16), 7);
            return new MemoryStream(payload);
        }

        private static SetDesktopSizeMessage ResizeRequest() => new SetDesktopSizeMessage((_, layout) => {
            Screen screen = layout.Single();
            var size = new Size(1024, 768);
            return (size, ImmutableHashSet.Create(new Screen(screen.Id, new Rectangle(Position.Origin, size), screen.Flags)));
        });
    }
}
