using System;

namespace Aetherfront.Synchronization
{
    public sealed class HoldRelease
    {
        public bool Unpause, RestoreScale, RestoreThrottle;
        public float Scale, Throttle;
    }
    public sealed class HoldLease
    {
        public bool Active { get; private set; }
        public Guid Vessel { get; private set; }
        bool pauseOwned, scaleOwned, pausedBefore, externalPause;
        float scaleBefore, throttleBefore;
        public void Begin(Guid vessel, bool paused, float scale, float throttle)
        {
            if (Active) return;
            Active = true; Vessel = vessel; pausedBefore = paused; scaleBefore = scale; throttleBefore = throttle;
            pauseOwned = !paused; scaleOwned = scale != 0; externalPause = false;
        }
        public void ObserveExternalPause(bool paused) { if (Active && paused) externalPause = true; }
        public HoldRelease End(bool completed, Guid currentVessel, bool paused, float scale, float throttle)
        {
            var result = new HoldRelease(); if (!Active) return result;
            result.Unpause = pauseOwned && paused && !externalPause;
            result.RestoreScale = !externalPause && ((scaleOwned && scale == 0) || (result.Unpause && scaleBefore == 0));
            result.Scale = pausedBefore ? 0 : scaleBefore;
            result.RestoreThrottle = completed && !pausedBefore && !externalPause && currentVessel == Vessel && Vessel != Guid.Empty && throttle == 0;
            result.Throttle = throttleBefore;
            Active = false; Vessel = Guid.Empty; return result;
        }
        public HoldRelease EndScene(bool paused,float scale)
        {
            // A pause requested in the departing flight is not a pause lease
            // for the next menu. Release only the pause/scale this guard owned;
            // never restore throttle while discarding that flight scene.
            var result=new HoldRelease();if(!Active)return result;
            result.Unpause=pauseOwned&&paused;
            result.RestoreScale=scaleOwned&&scale==0;
            result.Scale=scaleBefore>0?scaleBefore:1;
            Active=false;Vessel=Guid.Empty;return result;
        }
    }
}
