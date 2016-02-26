using System;
using System.Collections.Generic;

namespace kino.LeaseProvider.Configuration
{
    public class KinoConfiguration
    {
        public string RouterUri { get; set; }

        public string ScaleOutAddressUri { get; set; }

        public IEnumerable<RendezvousNode> RendezvousServers { get; set; }

        public TimeSpan PingSilenceBeforeRendezvousFailover { get; set; }

        public TimeSpan PongSilenceBeforeRouteDeletion { get; set; }

        public bool RunAsStandalone { get; set; }
    }
}