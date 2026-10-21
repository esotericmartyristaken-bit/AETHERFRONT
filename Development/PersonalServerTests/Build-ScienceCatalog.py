"""Validate a runtime-exported KSP science catalog for the server payload."""
import argparse, decimal, hashlib, json, pathlib, re

p=argparse.ArgumentParser()
p.add_argument('source', type=pathlib.Path)
p.add_argument('output', type=pathlib.Path)
a=p.parse_args()
assert a.source.resolve()!=a.output.resolve()
text=a.source.read_text(encoding='utf-8-sig')
matches=re.findall(r'SUBJECT\s*\{\s*id\s*=\s*([^\r\n]+)\s+cap\s*=\s*([^\r\n]+)\s*\}',text)
assert matches and len(matches)==len(re.findall(r'\bSUBJECT\s*\{',text)), 'Incomplete export'
subjects={}
for source,cap in matches:
    source=source.strip(); value=decimal.Decimal(cap.strip())
    assert source and len(source)<=256 and not any(ord(c)<32 for c in source)
    assert value.is_finite() and 0<value<=1000000
    assert source not in subjects or subjects[source]==value, 'Conflicting subject cap: '+source
    subjects[source]=value
assert len(subjects)<=100000 and any(s.startswith('crewReport@') for s in subjects)
body='['+','.join('{"Id":'+json.dumps(s,ensure_ascii=False)+',"Cap":'+str(v)+'}' for s,v in sorted(subjects.items()))+']\n'
rules=re.findall(r'EXPERIMENT\s*\{\s*id\s*=\s*([^\r\n]+)\s+cap\s*=\s*([^\r\n]+)\s+scale\s*=\s*(True|False)\s*\}', text)
assert rules and len(rules)==len(re.findall(r'\bEXPERIMENT\s*\{',text)), 'Missing experiment metadata'
experiments=[]
for name,cap,scale in rules:
    value=decimal.Decimal(cap.strip())
    assert name.strip() and '@' not in name and value.is_finite() and 0<=value<=1000000
    if value==0: continue  # A zero-reward experiment has no claimable UID cap.
    experiments.append('{"Id":'+json.dumps(name.strip())+',"Cap":'+str(value)+',"ApplyScienceScale":'+str(scale=='True').lower()+'}')
assert len({n for n,_,_ in rules})==len(rules)
body='{"Version":2,"Subjects":'+body.strip()+',"Experiments":['+','.join(experiments)+']}\n'
data=body.encode('utf-8'); assert len(data)<=16*1024*1024
a.output.parent.mkdir(parents=True,exist_ok=True)
a.output.write_bytes(data)
report={'subjects':len(subjects),'recoverySubjects':sum(s.startswith('recovery@') for s in subjects),'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest(),'sourceSha256':hashlib.sha256(a.source.read_bytes()).hexdigest()}
a.output.with_suffix('.audit.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,indent=2))
