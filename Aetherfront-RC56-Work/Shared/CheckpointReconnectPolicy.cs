namespace Aetherfront.Synchronization
{
    public sealed class CheckpointReconnectPolicy
    {
        public bool Armed { get; private set; }
        public bool Active { get; private set; }
        public int Attempts { get; private set; }
        public int Generation { get; private set; }
        public string Request { get; private set; } = string.Empty;
        double expires, next;
        public void Arm(int generation, string request, double now)
        {
            Cancel(); Generation = generation; Request = request; Armed = true; expires = now + 180;
        }
        public bool Disconnect(int generation, string reason, double now)
        {
            if (!Armed || now > expires || generation != Generation || reason !=
                "Loading your personal checkpoint. Connect again to resume your saved fleet and mission time.") return false;
            Armed = false; Active = true; expires = now + 180; next = now + 3; return true;
        }
        public bool Tick(double now, bool mainMenu, bool disconnected, bool resetPending, bool running)
        {
            if ((Armed || Active) && (now >= expires || running && Active)) { Cancel(); return false; }
            if (!Active || !mainMenu || !disconnected || resetPending || now < next) return false;
            if (Attempts == 3) { Cancel(); return false; }
            Attempts++; next = now + 15; return true;
        }
        public void Cancel() { Armed = Active = false; Attempts = 0; Request = string.Empty; }
    }
}
