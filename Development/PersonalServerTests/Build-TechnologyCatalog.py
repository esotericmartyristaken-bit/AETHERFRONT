"""Compile server costs/prerequisites from the actual generated KSP tech tree."""
import argparse,hashlib,json,pathlib,re
p=argparse.ArgumentParser()
p.add_argument('tree',type=pathlib.Path);p.add_argument('output',type=pathlib.Path)
a=p.parse_args()
root={'name':'root','values':{},'nodes':[]};stack=[root];pending=None
for raw in a.tree.read_text(encoding='utf-8-sig').splitlines():
    line=raw.strip()
    if not line or line.startswith('//'):continue
    if line=='{':
        assert pending is not None,'Unnamed config node'
        node={'name':pending,'values':{},'nodes':[]};stack[-1]['nodes'].append(node);stack.append(node);pending=None
    elif line=='}':
        assert len(stack)>1 and pending is None,'Unbalanced config';stack.pop()
    elif '=' in line:
        key,value=line.split('=',1);key=key.strip();value=value.strip()
        assert key not in stack[-1]['values'],'Duplicate node property: '+key
        stack[-1]['values'][key]=value
    else:
        assert pending is None,'Unconsumed node name';pending=line
assert len(stack)==1 and pending is None
trees=[n for n in root['nodes'] if n['name']=='TechTree'];assert len(trees)==1
rows=[]
for node in trees[0]['nodes']:
    if node['name']!='RDNode':continue
    v=node['values'];parents=[n['values']['parentID'] for n in node['nodes'] if n['name']=='Parent']
    cost=int(v['cost']);assert 0<=cost<=1000000000
    rows.append({'Id':v['id'],'Title':v.get('title',v['id']),'Description':v.get('description',''),
        'ScienceCost':cost,'PrerequisiteIds':parents,'AnyPrerequisite':v.get('anyToUnlock','False').lower()=='true','Starter':v['id']=='start'})
ids={r['Id'] for r in rows};assert len(rows)==len(ids) and 'start' in ids and len(rows)>100
assert all(p in ids for r in rows for p in r['PrerequisiteIds']),'Unregistered prerequisite'
for row in rows:
    assert row['Id'] not in row['PrerequisiteIds']
    assert not row['Title'].startswith('#'),'Unresolved title localization: '+row['Id']
# Require an acyclic tree, including OR alternatives, before deployment.
remaining={r['Id']:set(r['PrerequisiteIds']) for r in rows};resolved=set()
while remaining:
    ready=[k for k,v in remaining.items() if v<=resolved]
    assert ready,'Cyclic technology prerequisites'
    for key in ready:resolved.add(key);del remaining[key]
data=(json.dumps(rows,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
a.output.parent.mkdir(parents=True,exist_ok=True);a.output.write_bytes(data)
report={'nodes':len(rows),'anyPrerequisiteNodes':sum(r['AnyPrerequisite'] for r in rows),'treeSha256':hashlib.sha256(a.tree.read_bytes()).hexdigest(),'catalogSha256':hashlib.sha256(data).hexdigest()}
a.output.with_suffix('.audit.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,indent=2))
