import {createHash,createPrivateKey,createPublicKey,sign} from 'node:crypto';
import {createRequest} from './LocalReleaseRequest.mjs';
const [host,requestId,action,version,archiveSha256,sourceCommit,credentialSha256,issuedAt,expiresAt,expectedPublicHash]=process.argv.slice(2);
if(process.argv.length!==12)process.exit(2);
const chunks=[];let length=0;
for await(const chunk of process.stdin){length+=chunk.length;if(length>8192)process.exit(2);chunks.push(chunk);}
const secret=Buffer.from(Buffer.concat(chunks).toString('utf8').trim(),'base64');
try {
 const key=createPrivateKey({key:secret,type:'pkcs8',format:'der'});
 const publicDer=createPublicKey(key).export({type:'spki',format:'der'});
 if(key.asymmetricKeyType!=='ed25519'||createHash('sha256').update(publicDer).digest('hex')!==expectedPublicHash)throw Error('Approval key differs');
 const bytes=createRequest({host,requestId,action,version,archiveSha256,sourceCommit,credentialSha256,issuedAt,expiresAt});
 const signature=sign(null,Buffer.concat([Buffer.from('AETHERFRONT VPS LOCAL RELEASE APPROVAL v3\n','ascii'),bytes]),key);
 process.stdout.write(JSON.stringify({request:bytes.toString('base64'),signature:signature.toString('base64')}));
}finally{secret.fill(0);for(const chunk of chunks)chunk.fill(0);}
