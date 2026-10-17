using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using ApfCore;

namespace KesasLauncher
{
    public static class Entry
    {
        public static int Run(string root)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string id;
            using(var hash=SHA256.Create()) id=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant()))).Replace("-","");
            bool created;
            using(var guard=new Mutex(true,"Local\\KESAS-Launcher-"+id,out created))
            {
                if(!created) throw new IOException("The launcher is already open for this KSP folder.");
                try { using(var window=new InstallerWindow(root)) { Application.Run(window); return window.Restart ? 2 : 0; } }
                finally {guard.ReleaseMutex();}
            }
        }
    }
    public sealed class InstallerWindow : Form
    {
        private readonly string root;
        private readonly CheckBox potato=new CheckBox();
        private readonly Label size=new Label();
        private readonly Label status=new Label();
        private readonly Button install=new Button();
        private readonly Button cancel=new Button();
        private bool busy;
        private volatile bool canceled;
        public bool Restart {get;private set;}
        public InstallerWindow(string game)
        {
            root=game;Text="KESAS";ClientSize=new Size(660,340);MinimumSize=Size;MaximumSize=Size;
            StartPosition=FormStartPosition.CenterScreen;Font=new Font("Segoe UI",10);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;
            var title=new Label{Text="KESAS multiplayer",Location=new Point(20,18),Size=new Size(620,35),Font=new Font("Segoe UI",18,FontStyle.Bold)};
            var path=new Label{Text=root,Location=new Point(22,62),Size=new Size(620,36),AutoEllipsis=true};
            potato.Text="Potato mode (no optional graphics mods)";potato.Location=new Point(22,108);potato.Size=new Size(610,30);
            var details=new Label{Text="Both profiles use the same parts, planets, resources and gameplay plugins.\nPotato uses low graphics, quarter-size textures, no scatter and no shadows.",Location=new Point(22,146),Size=new Size(610,48)};
            size.Location=new Point(22,201);size.Size=new Size(610,28);
            status.Location=new Point(22,235);status.Size=new Size(610,42);status.Text="Saves and player identity are kept. Downloads can resume after interruption.";
            install.Text="Install / Update & Start KSP";install.UseMnemonic=false;install.Location=new Point(22,291);install.Size=new Size(410,32);
            cancel.Text="Close";cancel.Location=new Point(450,291);cancel.Size=new Size(180,32);
            Controls.AddRange(new Control[]{title,path,potato,details,size,status,install,cancel});
            string profile=Path.Combine(root,"GameData","LunaMultiplayer","Data","KesasInstallProfile.json");
            string defaults=Path.Combine(root,"GameData","LunaMultiplayer","Launcher","default-profile.txt");
            potato.Checked=File.Exists(profile)?File.ReadAllText(profile).Contains("potato"):File.Exists(defaults)&&File.ReadAllText(defaults).Trim()=="potato";
            potato.CheckedChanged+=(s,e)=>UpdateSize();
            install.Click+=async(s,e)=>await Install();cancel.Click+=(s,e)=>{if(busy){canceled=true;cancel.Enabled=false;status.Text="Canceling download...";}else Close();};
            FormClosing+=(s,e)=>{if(busy){e.Cancel=true;MessageBox.Show(this,"Wait for installation to finish, or cancel the download first.","KESAS");}};
            UpdateSize();
        }
        private void UpdateSize()
        {
            try { var catalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Path.Combine(root,"GameData","LunaMultiplayer","Launcher","install-catalog.json")));var profile=potato.Checked?catalog.Potato:catalog.Full;size.Text=(potato.Checked?"Potato":"Full")+" download: "+Installer.Format(profile.ManagedPackages.Sum(p=>p.SizeBytes))+". Already installed packages are reused."; }
            catch {size.Text="Install information will be refreshed from the server.";}
        }
        private async Task Install()
        {
            busy=true;canceled=false;install.Enabled=false;potato.Enabled=false;cancel.Enabled=true;cancel.Text="Cancel download";
            try
            {
                var worker=new Installer(root);
                worker.IsCanceled=()=>canceled;
                worker.Progress=message=>BeginInvoke(new Action(()=>{status.Text=message;if(message.StartsWith("Installing",StringComparison.Ordinal)){cancel.Enabled=false;cancel.Text="Installing...";}}));
                worker.Confirm=message=>(bool)Invoke(new Func<bool>(()=>MessageBox.Show(this,message,"KESAS profile changes",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)==DialogResult.Yes));
                string mode=potato.Checked?"potato":"full";
                var result=await Task.Run(()=>worker.Run(mode));
                Restart=result.RestartLauncher;busy=false;Close();
            }
            catch(OperationCanceledException error){status.Text=error.Message;}
            catch(Exception error){status.Text="Installation did not finish. Installed files have recovery backups.";MessageBox.Show(this,error.GetBaseException().Message,"KESAS",MessageBoxButtons.OK,MessageBoxIcon.Error);}
            finally{busy=false;install.Enabled=true;potato.Enabled=true;cancel.Enabled=true;cancel.Text="Close";}
        }
    }
}
