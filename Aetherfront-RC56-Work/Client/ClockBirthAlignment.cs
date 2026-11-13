using System;
using System.Reflection;
using HarmonyLib;
using LmpClient.Systems.SettingsSys;

namespace Aetherfront.Stability.Client
{
    // A freshly loaded flight may have initialized a virtual survival epoch at
    // stock UT zero before its authenticated personal UT is installed. Rebase
    // only that epoch's personal origin, never its crew ages, supplies or frozen
    // timer domain. Loaded snapshots not yet restored retain their native path.
    public static class ClockBirthAlignment
    {
        public sealed class Lease
        {
            internal object Clock,BeforeEpoch,BeforeFreeze,BeforeLaunch;
            internal FieldInfo Epoch,Freeze,Launch;
            internal Vessel Vessel;
            internal double OldUT,OldLaunch;
            public void Rollback()
            {if(Clock!=null){Epoch.SetValue(Clock,BeforeEpoch);Freeze.SetValue(Clock,BeforeFreeze);Launch.SetValue(Clock,BeforeLaunch);}if(Vessel!=null)Vessel.launchTime=OldLaunch;}
        }
        public static Lease Begin(Vessel vessel,double oldUT,double target)
        {
            if(vessel==null||target<=oldUT+0.05)return null;
            var type=AccessTools.TypeByName("Aetherfront.Continuity.Client.SurvivalClock")??throw new TypeLoadException("Survival clock unavailable during initial alignment.");
            object clock=vessel.GetComponent(type);
            if(clock==null)return null; // No initialized epoch exists to translate.
            var restored=AccessTools.Field(type,"restored");
            var epoch=AccessTools.Field(type,"epoch");var before=epoch.GetValue(clock);
            if(before==null||!(bool)restored.GetValue(clock))return null;
            var owner=(string)AccessTools.Property(before.GetType(),"Owner").GetValue(before,null);
            if(owner!=SettingsSystem.CurrentSettings.PlayerName)throw new InvalidOperationException("Cannot rebase a foreign survival epoch.");
            double survival=(double)AccessTools.Method(before.GetType(),"Now").Invoke(before,new object[]{owner,oldUT});
            var after=Activator.CreateInstance(before.GetType(),new object[]{owner,target,survival});
            var freeze=AccessTools.Field(type,"freezeOffset");var launch=AccessTools.Field(type,"virtualLaunch");
            var lease=new Lease{Clock=clock,Epoch=epoch,Freeze=freeze,Launch=launch,BeforeEpoch=before,BeforeFreeze=freeze.GetValue(clock),BeforeLaunch=launch.GetValue(clock),Vessel=vessel,OldUT=oldUT,OldLaunch=vessel.launchTime};
            double freezeDomain=(double)AccessTools.Property(type,"FreezeOffset").GetValue(clock,null);
            double virtualLaunch=(double)AccessTools.Property(type,"LaunchTime").GetValue(clock,null);
            try
            {
                freeze.SetValue(clock,(double?)freezeDomain);launch.SetValue(clock,(double?)virtualLaunch);epoch.SetValue(clock,after);
                if(vessel.situation==Vessel.Situations.PRELAUNCH)vessel.launchTime+=target-oldUT;
                return lease;
            }
            catch{lease.Rollback();throw;}
        }
    }
}
