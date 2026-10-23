using System;
using System.IO;

namespace Aetherfront.Continuity
{
    // A vessel's survival clock travels with its supplies and crew. Its elapsed
    // time follows the current authority's personal clock, never server wall time.
    public sealed class SurvivalEpoch
    {
        public string Owner { get; private set; }
        public double OwnerUT { get; private set; }
        public double SurvivalUT { get; private set; }

        public SurvivalEpoch(string owner, double ownerUT, double survivalUT)
        {
            Validate(ownerUT); Validate(survivalUT);
            if (string.IsNullOrEmpty(owner) || owner.Length > 128) throw new InvalidDataException("Invalid survival clock owner");
            Owner=owner; OwnerUT=ownerUT; SurvivalUT=survivalUT;
        }

        public double Now(string authority, double personalUT)
        {
            Validate(personalUT);
            if (!string.Equals(Owner, authority, StringComparison.Ordinal))
                throw new InvalidOperationException("Bind the vessel clock before changing authority");
            double elapsed=personalUT-OwnerUT;
            if (elapsed < -0.05) throw new InvalidDataException("Personal clock moved backwards during vessel simulation");
            double time=SurvivalUT+Math.Max(0,elapsed); Validate(time); return time;
        }

        public void Bind(string authority, double personalUT)
        {
            Validate(personalUT);
            if (string.IsNullOrEmpty(authority) || authority.Length > 128) throw new InvalidDataException("Invalid survival authority");
            if (string.Equals(Owner,authority,StringComparison.Ordinal)) return;
            // Cross-clock transfer preserves the entire virtual epoch, so old
            // habitability timestamps never become negative/uninitialized values.
            Owner=authority; OwnerUT=personalUT;
        }

        public void Checkpoint(string authority,double personalUT)
        {
            SurvivalUT=Now(authority,personalUT); OwnerUT=personalUT;
        }

        public bool RecoverLoadedCheckpoint(string authority,double personalUT)
        {
            Validate(personalUT);
            if(Owner!=authority || personalUT>=OwnerUT)return false;
            // A vessel packet may have committed after the last account clock
            // checkpoint before a crash. Keep consumed supplies and virtual time,
            // anchoring them to the recovered personal clock exactly once on load.
            OwnerUT=personalUT; return true;
        }

        private static void Validate(double value)
        {
            if(double.IsNaN(value)||double.IsInfinity(value)||value<0||value>1e12)
                throw new InvalidDataException("Survival clock outside supported range");
        }
    }
}
