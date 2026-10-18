using System;
using System.IO;
using ApfCore;

namespace KesasInstall
{
    public static partial class PackageCasing
    {
        public static string CanonicalRoot(ApfPackage package)
        {
            if(package==null)throw new ArgumentNullException(nameof(package));
            string destination=PreserveCase(package.Destination,true);
            string root=string.IsNullOrEmpty(destination)?PreserveCase(package.StripPrefix,false):destination;
            if(!ApfPaths.IsSafeRelative(root) ||
                !string.Equals(ApfPaths.Normalize(root),ApfPaths.PackageRoot(package),StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe canonical package root.");
            return root;
        }

        private static string PreserveCase(string value,bool removeGameData)
        {
            if(value!=null && (!string.Equals(value,value.Trim(),StringComparison.Ordinal) || Path.IsPathRooted(value)))
                throw new InvalidDataException("Rooted or padded canonical package path.");
            string path=string.Join("/",(value??string.Empty).Trim().Replace('\\','/').Split(new[]{'/'},StringSplitOptions.RemoveEmptyEntries));
            if(removeGameData && path.Equals("GameData",StringComparison.OrdinalIgnoreCase))return string.Empty;
            if(removeGameData && path.StartsWith("GameData/",StringComparison.OrdinalIgnoreCase))path=path.Substring("GameData/".Length);
            return path;
        }
    }
}
