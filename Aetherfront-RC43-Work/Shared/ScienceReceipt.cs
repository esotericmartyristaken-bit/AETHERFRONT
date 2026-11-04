using System;

namespace Aetherfront.Personal.Transport
{
    // Optional JSON fields; the existing core wire contract remains unchanged.
    public sealed class ScienceReceiptEnvelope
    {
        public int ProtocolVersion { get; set; } = 1;
        public int Operation { get; set; }
        public bool Accepted { get; set; }
        public string ReasonCode { get; set; }
        public long Revision { get; set; }
        public string ServerId { get; set; }
        public ScienceClaimReceipt ScienceReceipt { get; set; }
    }

    public sealed class ScienceClaimReceipt
    {
        public string RequestId { get; set; }
        public string AccountId { get; set; }
        public int Command { get; set; }
        public string SourceId { get; set; }
        public decimal RequestedAmount { get; set; }
        public decimal RecordedCumulative { get; set; }
        public string Disposition { get; set; }
    }
}
