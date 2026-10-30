"""Export the unpublished release and compact, sanitized acceptance evidence."""
from pathlib import Path
import hashlib,json,re,zipfile
from datetime import datetime,timezone

c=Path(__file__).resolve().parents[2]
b=c/'build';t=c/'tests/rc38-live';release=b/'rc38-release'
load=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
build=load(release/'BUILD-RESULT.json');smoke=load(release/'NATIVE-SIGNING-SMOKE.json')
assert smoke['templateSha256']==sha(release/'Native/ClientTemplate.zip')
assert smoke['templateSignedAndVerified'] and smoke['tamperedArchiveRejected'] and not smoke['productionKeyUsed']
final_launch=load(t/'LAUNCH-run3.json')
log=(t/('launch-'+str(final_launch['pid'])+'.txt')).read_text(encoding='utf-8-sig')
assert 'PASS native borderless style active' in log and 'PASS small APF icon assigned' in log
assert log.count('PASS editor initialized without craft edits')>=2, 'Complete both editor scene checks first'
assert 'COMMAND vab' in log and 'COMMAND sph' in log
known_invalid_assumption='FAIL stock loader recognizes Making History in this startup'
assert all(known_invalid_assumption in s for s in log.splitlines() if 'FAIL ' in s)
assert 'PROBE ERROR' not in log
policy=load(t/'MAKING-HISTORY-POLICY.json')
assert any('OptionalMakingHistoryDLCFeaturesAlwaysDisable = true' in s for s in policy['policy'])
assert 'END own test instance' in log
audit=load(t/'LOG-AUDIT.json')
for key in ('exceptions','partCompilationFailures','b9SeriousFatal','tweakScaleHarmonyLoadingFailures','presentationFailures','dlcFixtureFailures'):
    assert audit['KSP-run3.log'][key]['count']==0, 'Confirmed failure must not be exported as passed: '+key
assert audit['ModuleManager-run3.log']['errorEntries']==0
assert all(r['restored'] for r in load(t/'DLC-REPAIR-READBACK.json'))
result={
    'version':'kesas-rc38','status':'Implemented and packaged locally; unpublished',
    'utc':datetime.now(timezone.utc).isoformat(),'automatedAssertions':821,
    'changes':['APF toolbar/flag/taskbar branding','WASD editor wheel respects UI/camera locks',
       'Native borderless Windowed mode','ZTheme 1.2.2 + HUDReplacer 1.4.2 in both profiles',
       'Parallax collisions disabled at level -1','Own-subspace monotonic warp boundary',
       'Automatic signed DLC repair before stock loading, durable recovery and unchanged-launch cache'],
    'passed':['67 presentation, 72 clock boundary, 110 server continuity, 33 client clock, 401 client continuity, 138 DLC assertions',
       'Native release verifier; both profile catalogs + server manifest under CLR and shipped KSP Mono',
       'Final client template signed/verified using disposable key; tampered archive rejected',
       'Full KSP startup/part compilation; actual Parallax runtime -1',
       'Real corrupted Making History and missing Breaking Ground metadata repaired within one startup',
       'APF native icon handles, borderless style and Windowed mode in actual KSP',
       'ZTheme 625 global replacement images and three scene groups loaded',
       'VAB and SPH entered/exited through native game APIs; real WASD Harmony wheel hook active'],
    'skipped':['Physical mouse-wheel/Alt-Tab and multiple-monitor interaction (no PC input used)',
       'Exact affected-player Escape/auto-warp/craft-load disconnect reproduction; matching incident logs unavailable',
       'Live multiplayer/packet-loss/USI acceptance and the separate stock auto-warp overshoot path',
       'KAL controller UI interaction; ZTheme recommends HUDReplacer 1.4.3, while maintained public release is 1.4.2',
       'Production signing/publication/deployment, held by user instruction'],
    'initialFailures':[
       'Run 1: old disposable fixture lacked DLC payloads; APFBeta log showed both expansions ready',
       'Run 1: Mono Process.MainWindowHandle returned zero; corrected with native own-process enumeration',
       'Run 2: test harness omitted HighLogic.CurrentGame before Game.Start; corrected in the harness',
       'Run 3: harness expected MH mission-feature Installed flag; preserved KSPCF policy intentionally disables it while keeping DLC parts. This assertion is recorded as an invalid harness assumption, not a pass.'],
    'scope':{'productionChanged':False,'gitPushed':False,'userDllsSavesIdentityChanged':False,
       'userConfigChange':'Only previously requested ParallaxNoCollisions.cfg preference override',
       'testInstall':'Existing work/apf-test only; test save copied to RC38Probe'},
    'liveBuildNote':'Run 2 verifies real DLC repair within one startup. Run 3 uses the final packaged DLLs and reuses both unchanged DLC receipts without repair.',
    'makingHistoryPolicy':policy,
    'warningClassification':'Inherited optional RemoteTech integrations, missing textures and schema warnings remain separate. Added DLCs also expose stock cargo/inventory and duplicate-trait warnings. One old KSPCF texture-cache miss fell back to loading; no new part-compilation/B9/TweakScale/Harmony failure was observed.',
    'release':build,'signingSmoke':smoke,'logAudit':audit,
}
(release/'RESULTS.json').write_text(json.dumps(result,indent=2)+'\n')
text='RC38 prepared locally; NOT PUSHED OR PUBLISHED.\n\n'
text+='Changes\n'+''.join('- '+s+'\n' for s in result['changes'])
text+='\nPassed\n'+''.join('- '+s+'\n' for s in result['passed'])
text+='\nSkipped / limitations\n'+''.join('- '+s+'\n' for s in result['skipped'])
text+='\nDLC repair runs before stock expansion loading in the first startup after normal signed pack apply. It requires no extra DLC-only restart or manual SquadExpansion deletion. Unchanged launches reuse a versioned receipt.\n'
text+='\nThe native bundle is for the guarded host publisher after authorization. This review archive is not an end-user starter. New media are included; existing ten immutable media assets are referenced and reused. No keys, passwords or saves are included.\n'
(release/'RESULTS.txt').write_text(text)
inputs={
    'Aetherfront-Release-kesas-rc38-UNPUBLISHED.zip':release/build['archive'],
    'RESULTS.txt':release/'RESULTS.txt','RESULTS.json':release/'RESULTS.json',
    'SHA256.txt':release/'SHA256.txt',
    'Evidence/LOG-AUDIT.json':t/'LOG-AUDIT.json',
}
for folder in (b/'rc38-themes/Media',):
    for f in folder.glob('*.zip'):inputs['Media/'+f.name]=f
for name in ('NATIVE-SIGNING-SMOKE.json','CATALOG-VALIDATION.json','CATALOG-VALIDATION-MONO.json'):
    inputs['Evidence/'+name]=release/name
for folder,pattern in ((t,'launch-*.txt'),(t,'LAUNCH*.json'),(t,'DLC-*.json'),(t,'MAKING-HISTORY-POLICY.json'),(t,'USER-INSTALL-*.json'),(t,'FINAL-LOADED-CODE.json'),(t,'STORAGE-*.json'),(t,'*.png'),(t,'KSP-run*.log'),(t,'Player-run*.log'),(t,'ModuleManager-run*.log')):
    for f in folder.glob(pattern):inputs['Evidence/'+f.name]=f
for folder,name in [('rc38-presentation','TEST-RESULTS.txt'),('rc38-clock-server','BOUNDARY-TESTS.txt'),('rc38-clock-server','SERVER-REGRESSIONS.txt'),('rc38-clock-client','CLIENT-CLOCK-TESTS.txt'),('rc38-clock-client','CONTINUITY-MONO-REGRESSIONS.txt'),('rc38-expansion-bootstrap','TEST-RESULTS.txt')]:
    inputs['Evidence/'+folder+'-'+name]=b/folder/name
target=Path('C:/Users/jkell/Music/Aetherfront-RC38-UNPUBLISHED-Review.zip')
with zipfile.ZipFile(target,'w',zipfile.ZIP_DEFLATED,compresslevel=7) as z:
    for name,file in sorted(inputs.items()):
        assert '/saves/' not in name.lower() and not name.endswith(('.key','.sfs'))
        if file.suffix in ('.txt','.log','.json'):
            data=file.read_text(encoding='utf-8-sig',errors='replace')
            data=re.sub(r'(?im)^.*(?:password|private[_ -]?key|authorization|access[_ -]?token|bearer)\s*[:=].*$', '[credential-bearing log line redacted]',data)
            data=re.sub(r'-----BEGIN .*PRIVATE KEY-----.*?-----END .*PRIVATE KEY-----','[private key redacted]',data,flags=re.S)
            z.writestr(name,data.encode())
        else:z.write(file,name)
with zipfile.ZipFile(target) as z:assert z.testzip() is None
target.with_suffix('.zip.sha256').write_text(sha(target)+'  '+target.name+'\n')
print(json.dumps({'archive':str(target),'bytes':target.stat().st_size,'sha256':sha(target),'assertions':821,'pushed':False},indent=2))
