"""Prepare small verified inputs. Does not modify a game or publish a release."""
import argparse, hashlib, json, pathlib, zipfile

p=argparse.ArgumentParser()
p.add_argument('--inputs',type=pathlib.Path,required=True)
p.add_argument('--output',type=pathlib.Path,required=True)
p.add_argument('--existing-game',type=pathlib.Path,required=True)
a=p.parse_args()
audit=json.loads((a.inputs/'INPUT-AUDIT.json').read_text())
allowed={
 'KerbalFX AeroFX':['KerbalFX'],'KerbalFX BlastFX':['KerbalFX'],'KerbalFX Core':['KerbalFX'],
 'KerbalFX ImpactPuffs':['KerbalFX'],'KerbalFX RoverDust':['KerbalFX'],
 'Firefly':['Firefly'],'FireflyAPI':['FireflyAPI'], 'Camera Tools Continued':['CameraTools'],
 'Neptune Camera':['NeptuneCamera'], 'Planetside Exploration Technologies':['Benjee10_MMSEV','Benjee10_sharedAssets'],
 'BetterBurnTime':['BetterBurnTime'],'Conformal Decals':['ConformalDecals'],'Tarsier Space Technology':['TarsierSpaceTech']}
visual={'KerbalFX','Firefly','FireflyAPI'}
assert len(audit)==len(allowed) and {x['mod'] for x in audit}==set(allowed)
records={}
def sha(data): return hashlib.sha256(data).hexdigest()
def write(path,data):
    if path.exists(): assert path.read_bytes()==data, 'Prepared input changed: '+str(path)
    else: path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(data)
for item in audit:
    archive=a.inputs/item['file']
    assert sha(archive.read_bytes())==item['sha256']
    with zipfile.ZipFile(archive) as z:
        for entry in z.infolist():
            if entry.is_dir():continue
            name=entry.filename
            if 'GameData/' not in name:continue
            relative='GameData/'+name.split('GameData/',1)[1]
            root=relative.split('/')[1]
            if root not in allowed[item['mod']]:continue  # Never downgrade bundled dependencies.
            path=pathlib.PurePosixPath(relative)
            assert not path.is_absolute() and '..' not in path.parts and ':' not in relative and '\\' not in relative
            data=z.read(entry)
            key=relative.casefold()
            assert key not in records or records[key]['sha256']==sha(data), 'Conflicting mod inputs'
            layer='regular' if root in visual else 'shared'
            write(a.output/layer/relative,data)
            records[key]={'path':relative,'layer':layer,'sha256':sha(data),'bytes':len(data),'upstream':item['mod']}
        # Keep the newer installed HabTechProps. Verify all bundled models and
        # textures exist; author includes an otherwise identical older version file.
        if item['mod']=='Planetside Exploration Technologies':
            for name in z.namelist():
                if '/GameData/HabTechProps/' not in name or name.endswith('/'):continue
                relative=name[name.index('GameData/'):]
                target=a.existing_game/relative
                assert target.is_file(), 'Required HabTechProps asset absent: '+relative
                if not relative.lower().endswith('.version'):assert target.read_bytes()==z.read(name), 'Review changed shared dependency: '+relative
        for name in z.namelist():
            if ('license' in name.lower() or 'licence' in name.lower() or 'readme' in name.lower()) and not name.endswith('/'):
                # Preserve original notices outside GameData. These do not load as mods.
                destination=a.output/'notices'/item['mod'].replace(' ','-')/pathlib.PurePosixPath(name).name
                data=z.read(name)
                if destination.exists() and destination.read_bytes()!=data:
                    destination=destination.with_name(sha(data)[:12]+'-'+destination.name)
                write(destination,data)
result={'schemaVersion':1,'candidate':'kesas-rc35','files':sorted(records.values(),key=lambda x:x['path']),
        'visualEffectsProfiles':['regular'],'sharedPartsProfiles':['regular','potato'],
        'bundledOldDependenciesExcluded':['ModuleManager','B9PartSwitch','HabTechProps'],
        'redistributionPermission':{'Firefly':'User confirmed 2026-10-21','Planetside Exploration Technologies':'User confirmed 2026-10-21'},
        'publicationApproved':False}
write(a.output/'STAGED-INPUTS.json',(json.dumps(result,indent=2)+'\n').encode())
print(json.dumps({'files':len(records),'sharedBytes':sum(x['bytes'] for x in records.values() if x['layer']=='shared'),
 'regularVisualBytes':sum(x['bytes'] for x in records.values() if x['layer']=='regular'),'gameModified':False}))
