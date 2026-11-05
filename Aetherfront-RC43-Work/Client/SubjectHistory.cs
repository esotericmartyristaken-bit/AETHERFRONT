using System;

namespace Aetherfront.Personal.Client
{
    internal static class SubjectHistory
    {
        internal static void Restore(ScienceSubject subject)
        {
            if (!PersonalClient.Active || subject == null || HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX) return;
            decimal cumulative = Math.Max(PersonalClient.CreditedScience(subject.id), ScienceOutbox.PendingCumulative(subject.id));
            if (subject.science >= (float)cumulative) return;
            subject.science = (float)cumulative;
            if (subject.applyScienceScale) subject.scientificValue = ResearchAndDevelopment.GetSubjectValue(subject.science, subject);
        }
    }
}
