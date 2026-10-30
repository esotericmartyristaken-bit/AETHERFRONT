from pathlib import Path
import os,json,hashlib
c=Path(__file__).resolve().parents[3]
game=c.parent/'apf-test'; evidence=c/'tests/rc38-live'
assert (game/'KSP_x64.exe').is_file() and (evidence/'KSP-run1.log').is_file()
def put(relative,data):
    target=game/relative
    for node in [target,*target.parents]:
        if node==game.parent:break
        assert not node.exists() or not os.lstat(node).st_file_attributes & 1024
    temp=target.with_name(target.name+'.rc38-replace');temp.write_bytes(data);os.replace(temp,target)
for name,source in {
    'AetherfrontPresentationClient.dll':'rc38-presentation/code/AetherfrontPresentationClient.dll',
    'AetherfrontExpansionBootstrap.dll':'rc38-expansion-bootstrap/AetherfrontExpansionBootstrap.dll',
    'RC38LiveProbe.dll':'rc38-live-probe/RC38LiveProbe.dll',
}.items():put('GameData/LunaMultiplayer/Plugins/'+name,(c/'build'/source).read_bytes())
records=[]
for name,relative,action in [('MakingHistory','makinghistory.kspexpansion','corrupt'),('Serenity','serenity.kspexpansion','missing')]:
    rel='GameData/SquadExpansion/'+name+'/'+relative
    target=game/rel
    records.append({'path':rel,'action':action,'canonicalSha256':hashlib.sha256(target.read_bytes()).hexdigest()})
    if action=='corrupt':put(rel,b'RC38 deliberate test-only corrupt metadata\n')
    else:target.unlink() # unlink only the disposable hard link, preserving the user asset.
(evidence/'DLC-REPAIR-INPUT.json').write_text(json.dumps({'deliberateTestOnlyFaults':records,'userFilesWritten':False},indent=2)+'\n')
print('Prepared own second launch: native HWND fix and real DLC corruption/missing-file repair.')
