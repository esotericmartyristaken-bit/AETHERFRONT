import { REPOSITORY, validateControlDescriptor, requireValue, sequence } from './contract.mjs';
import { digest } from './public-status.mjs';

export function parseStrictJson(text) {
  const value = JSON.parse(text);
  const stack = [];
  for (const token of text.match(/"(?:\\.|[^"\\])*"|[{}\[\]:,]|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null/g) || []) {
    const top = stack.at(-1);
    if (token === '{') stack.push({keys:new Set(),key:true});
    else if(token === '[') stack.push({array:true});
    else if(token === '}' || token === ']') stack.pop();
    else if(token === ',' && top && !top.array) top.key=true;
    else if(token.startsWith('"') && top?.key) {
      const name=JSON.parse(token); requireValue(!top.keys.has(name),'Duplicate JSON field'); top.keys.add(name);top.key=false;
    }
  }
  return value;
}
function trusted(raw, asset) {
  const url = new URL(raw);
  requireValue(url.protocol === 'https:' && !url.port && !url.username && !url.password && !url.hash, 'Untrusted control transport');
  requireValue((asset ? ['github.com','release-assets.githubusercontent.com','objects.githubusercontent.com','github-releases.githubusercontent.com'] : ['raw.githubusercontent.com','api.github.com']).includes(url.hostname), 'Untrusted control transport');
  return url.href;
}
export class GitHubChannel {
  constructor(fetcher=globalThis.fetch) { this.fetch=fetcher; }
  async bytes(url,maximum,asset=false,timeout=20000) {
    let current=trusted(url,asset);
    const signal=AbortSignal.timeout(timeout);
    for(let hop=0;hop<5;hop++) {
      const response=await this.fetch(current,{redirect:'manual',signal,headers:{'User-Agent':'Aetherfront-Control','Accept':'application/json, application/octet-stream'}});
      if([301,302,303,307,308].includes(response.status)) {
        const location=response.headers.get('location');await response.body?.cancel();
        requireValue(location,'Missing control redirect');current=trusted(new URL(location,current).href,asset);continue;
      }
      if(!response.ok){await response.body?.cancel();throw Object.assign(new Error('Control channel unavailable'),{httpStatus:response.status});}
      let total=0;const chunks=[];
      try { for await(const chunk of response.body) {total+=chunk.length;requireValue(total<=maximum,'Control response too large');chunks.push(Buffer.from(chunk));} }
      finally {await response.body?.cancel().catch(()=>{});}
      return Buffer.concat(chunks);
    }
    throw new Error('Control redirect limit');
  }
  async intent(maximumSeen=0) {
    const relative='deployment/control-plane.json';
    let observed;
    try{observed=await this.bytes(`https://raw.githubusercontent.com/${REPOSITORY}/refs/heads/deploy/${relative}`,16384);}
    catch(error){if(error.httpStatus===404)return {absent:true};throw error;}
    const candidate=validateControlDescriptor(parseStrictJson(observed.toString('utf8')));
    // An ignored/current observation cannot authorize any side effect. Avoid
    // consuming GitHub's anonymous API budget once per minute for that case.
    if(sequence(candidate.version)<=maximumSeen)return {descriptor:candidate,ignored:true};
    const reference=parseStrictJson((await this.bytes(`https://api.github.com/repos/${REPOSITORY}/git/ref/heads/deploy`,16384)).toString('utf8'));
    const commit=reference.object?.sha;
    requireValue(reference.ref==='refs/heads/deploy' && reference.object?.type==='commit' && /^[a-f0-9]{40}$/.test(commit),'Unauthenticated control channel reference');
    const pinned=await this.bytes(`https://raw.githubusercontent.com/${REPOSITORY}/${commit}/${relative}`,16384);
    requireValue(digest(pinned)===digest(observed),'Control channel changed during authentication');
    return {descriptor:validateControlDescriptor(parseStrictJson(pinned.toString('utf8'))),commit,descriptorSha256:digest(pinned)};
  }
  async archive(descriptor) {
    validateControlDescriptor(descriptor);
    const bytes=await this.bytes(`https://github.com/${REPOSITORY}/releases/download/${descriptor.releaseTag}/${descriptor.assetName}`,descriptor.sizeBytes,true,15*60000);
    requireValue(bytes.length===descriptor.sizeBytes && digest(bytes)===descriptor.sha256,'Control archive authentication/hash mismatch');
    return bytes;
  }
}
