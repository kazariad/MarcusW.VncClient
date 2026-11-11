namespace MarcusW.VncClient
{
    /// <summary>
    /// Controls negotiation of client-requested remote desktop resizing.
    /// </summary>
    public enum RemoteResizeMode
    {
        /// <summary>
        /// Advertise resizing support and enable requests only after the server confirms support.
        /// Continue without client-requested resizing when the server does not support it.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Explicitly opt into resizing negotiation. Like Auto, this requires server confirmation;
        /// it does not force unsupported servers to resize. Currently equivalent to Auto.
        /// </summary>
        On = 1,

        /// <summary>
        /// Do not advertise ExtendedDesktopSize or allow client-requested resizing.
        /// Server-initiated framebuffer size changes are still handled.
        /// </summary>
        Off = 2
    }
}
