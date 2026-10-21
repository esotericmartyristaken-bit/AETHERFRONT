namespace Aetherfront.Personal.Client
{
    public static class DefaultDisplayPolicy
    {
        public static bool IsStockDefault(int width, int height) => width == 1280 && height == 720;
        public static void Select(int nativeWidth, int nativeHeight, out int width, out int height)
        {
            bool valid = nativeWidth >= 640 && nativeHeight >= 480 && nativeWidth <= 16384 && nativeHeight <= 16384;
            width = valid ? nativeWidth : 1920;
            height = valid ? nativeHeight : 1080;
        }
    }
}
