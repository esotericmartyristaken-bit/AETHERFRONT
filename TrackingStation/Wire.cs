namespace Aetherfront.Tracking
{
    public static class Wire
    {
        public const string Route = "apf-tracking-v1";
        public const int Limit = 128;
        public const int MaxPayload = 96 * 1024;

        public static bool IsRevision(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'F')) return false;
            return true;
        }

        public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class Marker
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Body { get; set; }
        public int ReferenceBody { get; set; }
        public bool Surface { get; set; }
        // Surface: latitude, longitude, altitude. Orbit: INC, ECC, SMA, LAN, LPE, MNA, EPH.
        public double[] Position { get; set; }

        public bool HasValidPosition()
        {
            if (ReferenceBody < 0 || Position == null || Position.Length != (Surface ? 3 : 7)) return false;
            foreach (double value in Position)
                if (!Wire.Finite(value)) return false;
            if (Surface) return System.Math.Abs(Position[0]) <= 90 && System.Math.Abs(Position[1]) <= 360;
            return Position[1] >= 0 && Position[1] != 1 &&
                ((Position[1] < 1 && Position[2] > 0) || (Position[1] > 1 && Position[2] < 0));
        }
    }
}
