namespace LoRaWan.NetworkServer
{
    using System.Threading.Tasks;
    using LoRaTools.LoRaMessage;
    using LoRaTools.LoRaPhysical;

    // Packet forwarder
    public interface IPacketForwarder
    {
        /// <summary>
        /// Send downstream message to LoRa device
        /// </summary>
        Task SendDownstreamAsync(DownlinkPktFwdMessage message);
    }

    public sealed class LoRaDeviceRequest
    {
        public LoRaTools.Regions.Region LoRaRegion { get; }
        public LoRaOperationTimeWatcher OperationTimer { get; }
        public Rxpk Rxpk { get; }
        public LoRaPayload Payload { get; }
        public NetworkServerConfiguration Configuration { get; }

        public ILoRaDeviceFrameCounterUpdateStrategyFactory FrameCounterUpdateStrategyFactory { get; }

        public ILoRaPayloadDecoder PayloadDecoder { get; }

        public IPacketForwarder PacketForwarder { get; }
    }
}