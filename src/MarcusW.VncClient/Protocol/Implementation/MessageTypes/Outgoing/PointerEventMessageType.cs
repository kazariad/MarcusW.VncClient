using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using MarcusW.VncClient.Protocol.MessageTypes;

namespace MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing
{
    /// <summary>
    /// A message type for sending <see cref="PointerEventMessage"/>s.
    /// </summary>
    public class PointerEventMessageType : IOutgoingMessageType
    {
        private readonly TimeSpan _minimumInterval;
        private readonly Stopwatch _sinceLastEvent = new Stopwatch();

        public PointerEventMessageType() : this(TimeSpan.Zero) { }

        internal PointerEventMessageType(TimeSpan minimumInterval)
        {
            _minimumInterval = minimumInterval;
        }
        /// <inheritdoc />
        public byte Id => (byte)WellKnownOutgoingMessageType.PointerEvent;

        /// <inheritdoc />
        public string Name => "PointerEvent";

        /// <inheritdoc />
        public bool IsStandardMessageType => true;

        /// <inheritdoc />
        public void WriteToTransport(IOutgoingMessage<IOutgoingMessageType> message, ITransport transport, CancellationToken cancellationToken = default)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));
            if (!(message is PointerEventMessage pointerEventMessage))
                throw new ArgumentException($"Message is no {nameof(PointerEventMessage)}.", nameof(message));

            cancellationToken.ThrowIfCancellationRequested();

            // Optional compatibility throttling. The sender serializes calls; state is
            // per connection so one client cannot throttle another client's input.
            if (_minimumInterval > TimeSpan.Zero && _sinceLastEvent.IsRunning)
            {
                TimeSpan remaining = _minimumInterval - _sinceLastEvent.Elapsed;
                if (remaining > TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(remaining))
                    cancellationToken.ThrowIfCancellationRequested();
            }

            // Validate coordinates
            var rawX = pointerEventMessage.PointerPosition.X;
            var rawY = pointerEventMessage.PointerPosition.Y;
            
            // Reject clearly invalid coordinates (negative or absurdly large)
            if (rawX < -1000 || rawX > 100000 || rawY < -1000 || rawY > 100000)
            {
                return; // Don't send obviously invalid coordinates
            }
            
            // Clamp to valid ushort range with extra conservative bounds
            var posX = (ushort)Math.Max(0, Math.Min(32767, rawX)); // Use 32767 instead of 65535 for safety
            var posY = (ushort)Math.Max(0, Math.Min(32767, rawY));
            
            // Validate button mask
            var buttonMask = (byte)pointerEventMessage.PressedButtons;
            const byte validButtonMask = 0x7F; // Standard VNC buttons (bits 0-6)
            buttonMask = (byte)(buttonMask & validButtonMask);
            
            // Remove conflicting wheel combinations
            bool hasWheelUp = (buttonMask & 0x08) != 0;
            bool hasWheelDown = (buttonMask & 0x10) != 0;
            if (hasWheelUp && hasWheelDown)
            {
                buttonMask = (byte)(buttonMask & ~0x18); // Remove both wheel up and down
            }

            Span<byte> buffer = stackalloc byte[6];

            // Message type
            buffer[0] = Id;

            // Pressed buttons mask (use validated mask)
            buffer[1] = buttonMask;

            // Pointer position
            BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], posX);
            BinaryPrimitives.WriteUInt16BigEndian(buffer[4..], posY);

            try
            {
                transport.Stream.Write(buffer);
                if (_minimumInterval > TimeSpan.Zero)
                    _sinceLastEvent.Restart();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to send pointer event: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// A message for telling the server about mouse pointer events.
    /// </summary>
    public class PointerEventMessage : IOutgoingMessage<PointerEventMessageType>
    {
        /// <summary>
        /// Gets the pointer position.
        /// </summary>
        public Position PointerPosition { get; }

        /// <summary>
        /// Gets the pressed buttons.
        /// </summary>
        public MouseButtons PressedButtons { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PointerEventMessage"/>.
        /// </summary>
        /// <param name="pointerPosition">The pointer position.</param>
        /// <param name="pressedButtons">The pressed buttons.</param>
        public PointerEventMessage(Position pointerPosition, MouseButtons pressedButtons)
        {
            PointerPosition = pointerPosition;
            PressedButtons = pressedButtons;
        }

        /// <inheritdoc />
        public string? GetParametersOverview() => $"PointerPosition: {PointerPosition}, PressedButtons: {PressedButtons}";
    }
}
