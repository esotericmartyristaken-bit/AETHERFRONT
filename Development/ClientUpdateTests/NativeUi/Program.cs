using System;
using AetherfrontInstallUI;
using KesasInstall;

internal static class Program
{
 static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
 static void Main(){
  NativeInstall.Mode=0;NativeInstall.Restarts=NativeInstall.Disposals=0;
  Check(InstallWindow.Run("fixture","fixture","pack",true)==0 && NativeInstall.Restarts==1 && NativeInstall.Disposals==1,"headless managed install restarts exactly once without a prompt");
  Check(InstallWindow.Run("fixture","fixture","client",true)==0 && NativeInstall.Restarts==2,"headless client install restarts automatically");
  Check(InstallWindow.Run("fixture","fixture","rollback",true)==0 && NativeInstall.Restarts==2,"rollback never automatically starts KSP");
  NativeInstall.Mode=1;Check(InstallWindow.Run("fixture","fixture","pack",true)==1 && NativeInstall.Restarts==2,"failed verification cannot restart");
  NativeInstall.Mode=2;Check(InstallWindow.Run("fixture","fixture","pack",true)==1 && NativeInstall.Disposals==4,"restart failure reported and completion disposed");
  Console.WriteLine("PASS five native helper flow checks; no window, game, installation or network.");
 }
}
namespace KesasInstall
{
 public static class NativeInstall
 {
  public static int Mode,Restarts,Disposals;
  public static string Status=>"fixture";
  public static Completion Run(string root,string path,string kind){if(Mode==1)throw new Exception("fixture verification failure");return new Completion();}
  public sealed class Completion:IDisposable
  {
   public void Restart(){if(Mode==2)throw new Exception("fixture restart failure");Restarts++;}
   public void Dispose(){Disposals++;}
  }
 }
}
