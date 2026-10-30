using System;
using System.IO;
using UnityEngine;

[KSPAddon(KSPAddon.Startup.Instantly,true)]
public sealed class AetherfrontExpansionBootstrap : MonoBehaviour
{
    private string failure;
    private Rect window=new Rect(60,60,570,155);
    public void Awake()
    {
        DontDestroyOnLoad(this);
        try {
            string root=Path.GetFullPath(KSPUtil.ApplicationRootPath);
            // A starter does not yet contain managed mod/DLC packages. Let the
            // normal signed updater prepare them; do not show a false DLC error.
            // The server's DLC/profile gate still prevents joining incomplete installs.
            if(!File.Exists(ExpansionRepair.Payload(root,"MakingHistory"))||!File.Exists(ExpansionRepair.Payload(root,"Serenity"))){
                Debug.Log("[Aetherfront DLC] Signed payloads pending normal pack preparation; no DLC files changed.");Destroy(this);return;
            }
            Action<string> log=message=>Debug.Log("[Aetherfront DLC] "+message);
            ExpansionRepair.Install(root,"MakingHistory",ExpansionPayloadHashes.MakingHistory,log);
            ExpansionRepair.Install(root,"Serenity",ExpansionPayloadHashes.Serenity,log);
            Debug.Log("[Aetherfront DLC] Making History and Breaking Ground assets ready for stock expansion loading.");Destroy(this);
        }catch(Exception e){
            failure="The DLC preparation could not finish safely. Existing files and recovery evidence were preserved. See KSP.log. Do not join until preparation succeeds.";
            Debug.LogError("[Aetherfront DLC] "+e.GetType().Name+": "+e.Message);
        }
    }
    public void OnGUI()
    {
        if(failure!=null&&HighLogic.LoadedScene==GameScenes.MAINMENU)
            window=GUILayout.Window(0x41504644,window,_=>GUILayout.Label(failure),"Aetherfront DLC preparation");
    }
}
