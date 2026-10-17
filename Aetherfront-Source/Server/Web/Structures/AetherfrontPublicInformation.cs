using Aetherfront.Core;
using Server.Aetherfront;
using System;
using System.Linq;

namespace Server.Web.Structures
{
    /// <summary>
    /// Read-only public feed for an external dashboard, Chronicle publisher, or Discord bridge.
    /// The state is projected through the same privacy rules used by the in-game snapshot.
    /// </summary>
    public sealed class AetherfrontPublicInformation
    {
        public bool Enabled { get; private set; }
        public string ServerId { get; private set; } = string.Empty;
        public long Revision { get; private set; }
        public DateTime RefreshedAtUtc { get; private set; }
        public AetherfrontState State { get; private set; } = new AetherfrontState();

        public void Refresh()
        {
            RefreshedAtUtc = DateTime.UtcNow;
            Enabled = AetherfrontServerSystem.Enabled;
            if (!Enabled)
            {
                ServerId = string.Empty;
                Revision = 0;
                State = new AetherfrontState();
                return;
            }

            var source = AetherfrontServerSystem.Snapshot();
            var projected = AetherfrontVisibility.ForAccount(source, string.Empty);
            projected.Events = projected.Events.OrderByDescending(e => e.OccurredAtUtc).Take(200)
                .OrderBy(e => e.OccurredAtUtc).ToList();
            projected.Stories = projected.Stories.Where(s => s.Public).OrderByDescending(s => s.PublishedAtUtc)
                .Take(50).ToList();
            projected.ObjectiveProgress = projected.ObjectiveProgress.Where(p =>
                projected.Objectives.Any(o => string.Equals(o.Id, p.ObjectiveId, StringComparison.OrdinalIgnoreCase) && !o.Archived)).ToList();
            projected.ResearchProgress = projected.ResearchProgress.Where(p =>
                projected.ResearchProjects.Any(r => string.Equals(r.Id, p.ProjectId, StringComparison.OrdinalIgnoreCase))).ToList();
            projected.CommandReceipts.Clear();
            State = projected;
            ServerId = projected.ServerId;
            Revision = projected.Revision;
        }
    }
}
