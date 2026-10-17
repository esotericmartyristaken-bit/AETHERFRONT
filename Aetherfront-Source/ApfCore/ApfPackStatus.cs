
namespace ApfCore
{
    public enum ApfPackStatus
    {
        Unknown = 0,
        Clean = 1,
        SyncRequired = 2,
        Accepted = 3,
        Invalid = 4,
        Failed = 5
    }
    public sealed class ApfSyncOperation
    {
        public string Kind { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
    }
}
