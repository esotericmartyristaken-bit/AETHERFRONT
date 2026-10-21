"""Create the separate local multiplayer test client, with private state absent.

The locally signed overlay is built separately. This never changes APFBeta.
"""
import argparse, hashlib, json, os, pathlib, shutil, stat

parser=argparse.ArgumentParser()
parser.add_argument('--source', required=True, type=pathlib.Path, help='Existing read-only KSP installation; never copied with personal state')
group=parser.add_mutually_exclusive_group()
group.add_argument('--resume-overlay', action='store_true')
group.add_argument('--complete-base-runtime', action='store_true')
args=parser.parse_args()

controller=pathlib.Path(__file__).resolve().parents[3]
source=args.source.resolve()
destination=controller.parent/'apf-test' # Keep native KSP paths below MAX_PATH.
overlay=controller/'build/rc33-local-overlay'
resume=args.resume_overlay or args.complete_base_runtime
assert overlay.is_dir() and ((resume and destination.is_dir() and not (destination/'KSP.log').exists()) or not destination.exists())
immutable={'.dds','.png','.tga','.jpg','.jpeg','.mu','.ogg','.wav','.unity3d','.bundle','.resource','.ress','.assets'}
copied=linked=0
def checked(path):
    info=path.lstat()
    assert not path.is_symlink() and not (info.st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT), 'Reparse source refused: '+str(path)
    return info
def file_copy(path,target):
    global copied,linked
    info=checked(path);target.parent.mkdir(parents=True,exist_ok=True)
    if path.suffix.lower() in immutable:
        os.link(path,target);linked+=info.st_size
    else:
        shutil.copy2(path,target);copied+=info.st_size
def tree(path,target):
    checked(path)
    for child in path.iterdir():
        checked(child)
        if child.name in {'LunaMultiplayer','ModuleManager.ConfigCache','ModuleManager.ConfigSHA','ModuleManager.Physics','ModuleManager.TechTree','ControllerTestDriver.dll'}: continue
        if child.is_dir(): tree(child,target/child.name)
        elif child.suffix.lower() not in {'.log','.bak','.tmp'}: file_copy(child,target/child.name)
for root in (source,destination.parent,overlay):
    for ancestor in [root,*root.parents]:checked(ancestor)
if not resume:
    destination.mkdir()
    for name in ('KSP_x64.exe','UnityPlayer.dll','buildID64.txt'):
        if (source/name).is_file():file_copy(source/name,destination/name)
    for name in ('KSP_x64_Data','GameData','MonoBleedingEdge','Internals','Parts','Plugins','Resources'):
        tree(source/name,destination/name)
elif args.complete_base_runtime:
    for name in ('MonoBleedingEdge','Internals','Parts','Plugins','Resources'):
        assert not (destination/name).exists(), 'Do not overlay an existing runtime folder'
        tree(source/name,destination/name)
# Keep fresh vanilla display defaults to exercise first-launch initialization.
# Do not copy settings, saves, APF identity, updater receipts, trust or caches.
for path in overlay.rglob('*'):
    checked(path)
    if path.is_file():
        target=destination/path.relative_to(overlay)
        if target.exists():
            checked(target)
            assert target.read_bytes()==path.read_bytes(), 'Overlay differs from base asset: '+str(target.relative_to(destination))
            continue
        target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(path,target);copied+=path.stat().st_size
report={'root':str(destination),'linkedImmutableBytes':linked if not resume else None,'copiedBytes':copied if not resume else None,'resumedOverlayAfterBaseCopy':resume,'sourceSettingsCopied':False,'sourceIdentityCopied':False,'sourceSavesCopied':False,'productionTrustCopied':False}
(controller/'tests/personal-client-fixture.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,indent=2))
