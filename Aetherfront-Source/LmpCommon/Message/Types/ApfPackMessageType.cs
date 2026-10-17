namespace LmpCommon.Message.Types
{
    public enum ApfPackMessageType
    {
        PackManifestRequest = 0,
        PackManifestReply = 1,
        PackStatusReport = 2,
        PackSyncRequired = 3,
        PackSyncAccepted = 4
    }
}
