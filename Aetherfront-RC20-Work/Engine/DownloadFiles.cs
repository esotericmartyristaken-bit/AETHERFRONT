using System;
using System.IO;
using System.Threading;

namespace KesasInstall
{
    public static class DownloadFiles
    {
        public static bool IsSharingViolation(IOException error)
        {
            int code=error.HResult&65535;
            return code==32||code==33||error.Message.IndexOf("Sharing violation",StringComparison.OrdinalIgnoreCase)>=0;
        }
        public static T Retry<T>(Func<T> action,Func<bool> canceled)
        {
            int[] delays={100,250,500,1000};
            for(int attempt=0;;attempt++) {
                if(canceled())throw new OperationCanceledException("Download canceled. Installed files were kept.");
                try{return action();}
                catch(IOException error) when(IsSharingViolation(error)&&attempt<delays.Length) {
                    int remaining=delays[attempt];while(remaining>0){if(canceled())throw new OperationCanceledException("Download canceled. Installed files were kept.");int slice=Math.Min(50,remaining);Thread.Sleep(slice);remaining-=slice;}
                }
            }
        }
        public static FileStream OpenWriter(string path,FileMode mode,Func<bool> canceled)
        {
            // Permit scanners/readers, never another writer or a rename while
            // the chunk is open. Acquire this handle before requesting bytes.
            return Retry(()=>new FileStream(path,mode,FileAccess.Write,FileShare.Read,262144),canceled);
        }
        public static long Length(string path,Func<bool> canceled)
        {
            try{return Retry(()=>new FileInfo(path).Length,canceled);}
            catch(FileNotFoundException){return 0;}catch(DirectoryNotFoundException){return 0;}
        }
        public static IDisposable Acquire(string root)
        {
            string path=Path.Combine(root,"APF","download.lock");
            ApfCore.ApfPaths.EnsureNoReparsePoints(root,path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            try{return Retry(()=>new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read),()=>false);}
            catch(IOException error) when(IsSharingViolation(error)){throw new IOException("This game folder is in use by another download or process. Close other KSP instances and retry.",error);}
        }
    }
}
