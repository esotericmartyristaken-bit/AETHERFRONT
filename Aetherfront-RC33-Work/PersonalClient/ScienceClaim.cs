using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aetherfront.Core;

namespace Aetherfront.Personal.Client
{
    public sealed class PendingScienceClaims
    {
        public int Version { get; set; } = 1;
        public List<PendingScienceClaim> Claims { get; set; } = new List<PendingScienceClaim>();
    }
    public sealed class PendingScienceClaim
    {
        public string ServerId { get; set; }
        public string AccountId { get; set; }
        public int Command { get; set; }
        public string SourceId { get; set; }
        public decimal Amount { get; set; }
        public long Reason { get; set; }
        public long ChunkIndex { get; set; }
        private long cachedChunk = -1;
        private string cachedId;
        public decimal GetChunkAmount() => Command == 42 ? Math.Min(Amount, PersonalProgression.MaxAward) : Amount;
        public string GetChunkSourceId()
        {
            if (Command != 42 || ChunkIndex == 0) return SourceId;
            if (cachedId != null && cachedChunk == ChunkIndex) return cachedId;
            using (var hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(SourceId + ":" + ChunkIndex.ToString(CultureInfo.InvariantCulture)));
                var guid = new byte[16]; Array.Copy(bytes, guid, guid.Length);
                cachedId = "award:" + new Guid(guid).ToString("N"); cachedChunk = ChunkIndex;
                return cachedId;
            }
        }
        public bool AcknowledgeChunk()
        {
            if (Command != 42 || Amount <= PersonalProgression.MaxAward) return true;
            Amount -= PersonalProgression.MaxAward; ChunkIndex++;
            return false;
        }
    }
}
