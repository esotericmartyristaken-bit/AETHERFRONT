"""Reuse immutable installed DLC assets in the one disposable test install.

No user files are written. Mutable receipts are deliberately not linked/copied;
the actual signed bootstrap must validate and generate this fixture's receipts.
"""
from pathlib import Path
import os, json
import hashlib

c=Path(__file__).resolve().parents[3]
game=c.parent/'apf-test'
source=Path('C:/Users/jkell/Documents/Codex/APFBeta')
roots=('GameData/AetherfrontMakingHistory','GameData/AetherfrontBreakingGround',
       'GameData/SquadExpansion/MakingHistory','GameData/SquadExpansion/Serenity')
count=0; size=0
for root in roots:
    start=source/root
    assert start.is_dir()
    for directory, dirs, names in os.walk(start,followlinks=False):
        path=Path(directory)
        assert not os.lstat(path).st_file_attributes & 1024
        for name in dirs: assert not os.lstat(path/name).st_file_attributes & 1024
        for name in names:
            original=path/name
            assert not os.lstat(original).st_file_attributes & 1024
            target=game/original.relative_to(source)
            if target.exists():
                assert os.path.samefile(target,original), 'Unexpected prior fixture DLC; no overwrite: '+str(target)
                continue
            target.parent.mkdir(parents=True,exist_ok=True)
            os.link(original,target)
            count+=1;size+=original.stat().st_size
result={'immutableHardlinkedFiles':count,'logicalBytesReused':size,'newAssetDataBytes':0,
        'mutableReceiptsLinked':False,'userFilesWritten':False,'testRoot':str(game)}
(c/'tests/rc38-live/DLC-FIXTURE.json').write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps(result,indent=2))
