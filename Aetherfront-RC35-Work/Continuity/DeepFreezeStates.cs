using System.IO;

namespace Aetherfront.Continuity
{
    public static class DeepFreezeStates
    {
        // Preserve the saved status. Approved DF1.32 uses Dead; old shared
        // checkpoints used Missing. Ownership, prior assignment, counts and
        // the full roster/world consistency checks remain in the native code.
        public static bool FrozenStatusMatches(string actual,string legacy)
        {
            if(legacy!="Missing")throw new InvalidDataException("Unreviewed frozen-status comparison");
            return actual=="Missing"||actual=="Dead";
        }
    }
}
