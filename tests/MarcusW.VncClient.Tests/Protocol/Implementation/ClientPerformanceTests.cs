using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.EncodingTypes;
using MarcusW.VncClient.Protocol.Implementation;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Incoming;
using MarcusW.VncClient.Protocol.MessageTypes;
using MarcusW.VncClient.Protocol.Services;
using MarcusW.VncClient.Protocol.Implementation.Services.Communication;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MarcusW.VncClient.Tests.Protocol.Implementation
{
    public class ClientPerformanceTests
    {
        [Theory]
        [InlineData(null, null, -255)]
        [InlineData(false, -1, -250)]
        [InlineData(true, 0, -256)]
        [InlineData(true, 1, -255)]
        [InlineData(true, 9, -247)]
        public async Task Initial_Wire_Encodings_Include_Compression_And_Optional_Streaming(
            bool? enableContinuousUpdates, int? compressionLevel, int expectedCompressionId)
        {
            var parameters = new ConnectParameters {
                PostInitializationDelay = TimeSpan.Zero,
                PostSetEncodingsDelay = TimeSpan.Zero
            };
            if (enableContinuousUpdates.HasValue)
                parameters.EnableContinuousUpdates = enableContinuousUpdates.Value;
            if (compressionLevel.HasValue)
                parameters.PreferredCompressionLevel = compressionLevel.Value;
            var connection = new RfbConnection(new DefaultImplementation(), NullLoggerFactory.Instance, parameters);
            var context = new RfbConnectionContext(connection);
            var state = new ProtocolState(context);
            context.State = state;
            context.SupportedEncodingTypes = DefaultImplementation.GetDefaultEncodingTypes(context).ToImmutableHashSet();
            context.SupportedMessageTypes = DefaultImplementation.GetDefaultMessageTypes(context).ToImmutableHashSet();
            state.Prepare();
            state.RemoteFramebufferSize = new Size(800, 600);
            state.RemoteFramebufferFormat = PixelFormat.Plain;

            using var stream = new MemoryStream();
            var transport = new Mock<ITransport>();
            transport.SetupGet(t => t.Stream).Returns(stream);
            context.Transport = transport.Object;

            using var sender = new RfbMessageSender(context);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            sender.StartSendLoop();
            try
            {
                sender.EnqueueInitialMessages(timeout.Token);
                // A completed final message guarantees all initial bytes have been written.
                await sender.SendMessageAndWaitAsync(new FramebufferUpdateRequestMessage(true,
                    new Rectangle(0, 0, 800, 600)), timeout.Token).WaitAsync(timeout.Token);
            }
            finally
            {
                await sender.StopSendLoopAsync();
            }

            byte[] bytes = stream.ToArray();
            Assert.Equal(0, bytes[0]); // SetPixelFormat (20 bytes)
            Assert.Equal(2, bytes[20]); // SetEncodings
            int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(22));
            var ids = new List<int>();
            for (int i = 0; i < count; i++)
                ids.Add(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(24 + i * 4)));

            Assert.Contains(expectedCompressionId, ids);
            Assert.Contains((int)WellKnownEncodingType.JpegFineGrainedQualityLevelLow + 80, ids);
            Assert.Single(ids, id => id >= -256 && id <= -247);
            Assert.Contains(0, ids); // Raw fallback
            Assert.Contains(16, ids); // ZRLE
            Assert.Equal(DefaultImplementation.IsTightAvailable, ids.Contains(7));
            Assert.Equal(enableContinuousUpdates ?? true, ids.Contains(-312)); // Fence
            Assert.Equal(enableContinuousUpdates ?? true, ids.Contains(-313)); // ContinuousUpdates
            int firstRequest = 24 + count * 4;
            Assert.Equal(3, bytes[firstRequest]);
            Assert.Equal(0, bytes[firstRequest + 1]); // Initial request remains non-incremental
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(false, true)]
        public void Streaming_Requires_Server_Confirmation_And_Respects_Opt_Out(bool enabled, bool confirmed)
        {
            var parameters = new ConnectParameters { EnableContinuousUpdates = enabled };
            using var connection = new RfbConnection(new DefaultImplementation(), NullLoggerFactory.Instance, parameters);
            var context = new RfbConnectionContext(connection);
            var state = new ProtocolState(context);
            context.State = state;
            context.SupportedEncodingTypes = DefaultImplementation.GetDefaultEncodingTypes(context).ToImmutableHashSet();
            context.SupportedMessageTypes = DefaultImplementation.GetDefaultMessageTypes(context).ToImmutableHashSet();
            state.Prepare();
            state.RemoteFramebufferSize = new Size(800, 600);
            state.RemoteFramebufferFormat = PixelFormat.Plain;
            var sender = new Mock<IRfbMessageSender>();
            context.MessageSender = sender.Object;
            using var stream = new MemoryStream(new byte[3]); // Empty framebuffer update.
            var transport = new Mock<ITransport>();
            transport.SetupGet(t => t.Stream).Returns(stream);
            if (confirmed)
                new EndOfContinuousUpdatesMessageType(context).ReadMessage(transport.Object);

            new FramebufferUpdateMessageType(context).ReadMessage(transport.Object);

            bool streaming = enabled && confirmed;
            Assert.Equal(streaming, state.ContinuousUpdatesEnabled);
            sender.Verify(s => s.EnqueueMessage(It.IsAny<IOutgoingMessage<EnableContinuousUpdatesMessageType>>(),
                It.IsAny<CancellationToken>()), streaming ? Times.Once() : Times.Never());
            sender.Verify(s => s.EnqueueFramebufferUpdateRequest(new Rectangle(0, 0, 800, 600), true,
                It.IsAny<CancellationToken>()), streaming ? Times.Never() : Times.Once());
        }

        [Fact]
        public void Default_Pointer_Burst_Does_Not_Accumulate_Throttle_Delays()
        {
            using var stream = new MemoryStream();
            var transport = new Mock<ITransport>();
            transport.SetupGet(t => t.Stream).Returns(stream);
            var writer = new PointerEventMessageType();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 1000; i++)
                writer.WriteToTransport(new PointerEventMessage(new Position(i, i), MouseButtons.None),
                    transport.Object, timeout.Token);

            Assert.Equal(6000, stream.Length);
            byte[] bytes = stream.ToArray();
            Assert.Equal(999, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(5996)));
            Assert.Equal(999, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(5998)));
        }

        [Fact]
        public void Optional_Pointer_Throttle_Can_Be_Cancelled()
        {
            using var stream = new MemoryStream();
            var transport = new Mock<ITransport>();
            transport.SetupGet(t => t.Stream).Returns(stream);
            var writer = new PointerEventMessageType(TimeSpan.FromSeconds(30));
            var message = new PointerEventMessage(Position.Origin, MouseButtons.None);
            writer.WriteToTransport(message, transport.Object);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Assert.Throws<OperationCanceledException>(() => writer.WriteToTransport(message, transport.Object, timeout.Token));
            Assert.Equal(6, stream.Length);
        }

        [Fact]
        public void Tcp_Transport_Disables_Packet_Batching()
        {
            using var client = new TcpClient();
            using var transport = new TcpTransport(client);
            Assert.True(client.NoDelay);
        }
    }
}
