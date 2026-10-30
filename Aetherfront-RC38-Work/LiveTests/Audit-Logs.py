"""Summarize fresh test logs separately from inherited fixture warnings."""
from pathlib import Path
import json, re

c=Path(__file__).resolve().parents[3]
out=c/'tests/rc38-live'
before=(out/'before/KSP.log').read_text(encoding='utf-8-sig',errors='replace')
normalize=lambda s: re.sub(r'^\[(?:LOG|ERR|WRN|EXC) [^]]+\]\s*','',s).strip()
old={normalize(s) for s in before.splitlines()}
patterns={
    'exceptions':r'^\[EXC ',
    'partCompilationFailures':r'PartCompiler: Cannot (?:compile|clone)|Model was not compiled correctly|Cannot find model|Failed to compile',
    'b9SeriousFatal':r'B9PartSwitch.*(?:serious|fatal)|(?:serious|fatal).*B9PartSwitch',
    'tweakScaleHarmonyLoadingFailures':r'(?:TweakScale|Harmony).*(?:failed to load|could not load|missing assembly|ReflectionTypeLoadException)|(?:Could not load|failed to load).*(?:TweakScale|Harmony)',
    'presentationFailures':r'\[Aetherfront Presentation\].*(?:failed|stopped)',
    'dlcFixtureFailures':r'\[Aetherfront DLC\].*(?:Exception|could not)',
}
report={}
for file in sorted(out.glob('KSP-run*.log')):
    lines=file.read_text(encoding='utf-8-sig',errors='replace').splitlines()
    results={}
    for key,pattern in patterns.items():
        matches=[normalize(s) for s in lines if re.search(pattern,s,re.I)]
        results[key]={'count':len(matches),'inheritedExactSignatures':sum(s in old for s in matches),
                      'samples':list(dict.fromkeys(matches))[:8]}
    warnings=[normalize(s) for s in lines if re.match(r'^\[(?:ERR|WRN) ',s)]
    results['otherWarnings']={'count':len(warnings),'inheritedExactSignatures':sum(s in old for s in warnings),
                              'samples':list(dict.fromkeys(warnings))[:12],
                              'unmatchedSignatureSamples':list(dict.fromkeys(s for s in warnings if s not in old))[:20]}
    results['parallaxLevels']=re.findall(r'\[Parallax\] collisionLevel\s*=\s*(-?\d+)', '\n'.join(lines))
    report[file.name]=results
for file in sorted(out.glob('ModuleManager-run*.log')):
    text=file.read_text(encoding='utf-8-sig',errors='replace')
    report[file.name]={'errorEntries':len(re.findall(r'^\[ERR ',text,re.M)),
                       'patchesApplied':re.findall(r'ModuleManager:\s*(\d+) patches applied',text),
                       'completed':bool(re.search(r'\] Done!\s*$',text,re.M))}
(out/'LOG-AUDIT.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps(report,indent=2))
