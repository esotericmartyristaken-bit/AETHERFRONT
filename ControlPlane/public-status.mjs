import fs from 'node:fs/promises';
import crypto from 'node:crypto';
import path from 'node:path';
import { validateHostReport } from './contract.mjs';

export const digest = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
export function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) { crc ^= byte; for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0); }
  return (crc ^ 0xffffffff) >>> 0;
}
// A single STORE entry avoids compression libraries and bounds controller parsing.
export function statusZip(report) {
  const data = Buffer.from(JSON.stringify(validateHostReport(report)));
  if (data.length > 32768) throw new Error('Host status exceeds limit');
  const name = Buffer.from('host-status.json');
  const local = Buffer.alloc(30); local.writeUInt32LE(0x04034b50); local.writeUInt16LE(20,4);
  local.writeUInt32LE(crc32(data),14); local.writeUInt32LE(data.length,18); local.writeUInt32LE(data.length,22); local.writeUInt16LE(name.length,26);
  const central = Buffer.alloc(46); central.writeUInt32LE(0x02014b50); central.writeUInt16LE(20,4); central.writeUInt16LE(20,6);
  central.writeUInt32LE(crc32(data),16); central.writeUInt32LE(data.length,20); central.writeUInt32LE(data.length,24); central.writeUInt16LE(name.length,28);
  const end = Buffer.alloc(22); end.writeUInt32LE(0x06054b50); end.writeUInt16LE(1,8); end.writeUInt16LE(1,10);
  end.writeUInt32LE(central.length+name.length,12); end.writeUInt32LE(local.length+name.length+data.length,16);
  return Buffer.concat([local,name,data,central,name,end]);
}
export async function noLinks(target) {
  for (let cursor = path.resolve(target);;) {
    const stat = await fs.lstat(cursor).catch(error => { if (error.code === 'ENOENT') return null; throw error; });
    if (stat?.isSymbolicLink() || (stat?.isFile() && stat.nlink !== 1)) throw new Error('Unsafe control path');
    const parent = path.dirname(cursor); if (parent === cursor) return; cursor = parent;
  }
}
export async function atomic(file, bytes) {
  await noLinks(file); await fs.mkdir(path.dirname(file),{recursive:true});
  const temporary = `${file}.${crypto.randomUUID()}.incoming`;
  const handle = await fs.open(temporary,'wx');
  try { await handle.writeFile(bytes); await handle.sync(); } finally { await handle.close(); }
  try {
    await noLinks(file);
    for(let attempt=0;;attempt++) {
      try {await fs.rename(temporary,file);break;}
      catch(error){if(!['EACCES','EBUSY','EPERM'].includes(error.code)||attempt===8)throw error;await new Promise(resolve=>setTimeout(resolve,Math.min(25*2**attempt,800)));}
    }
  }
  finally { await fs.rm(temporary,{force:true}).catch(()=>{}); }
}
export const writeJson = (file,value) => atomic(file,Buffer.from(JSON.stringify(value)));
export async function readJson(file, absent = undefined) {
  await noLinks(file);
  try { const stat=await fs.stat(file); if(stat.size>1024*1024) throw new Error('Control state too large'); return JSON.parse(await fs.readFile(file,'utf8')); }
  catch(error) { if(error.code==='ENOENT' && absent !== undefined) return absent; throw error; }
}
export async function fileHash(file) {
  await noLinks(file);const hash=crypto.createHash('sha256');const handle=await fs.open(file,'r');
  try {for await(const chunk of handle.createReadStream())hash.update(chunk);return hash.digest('hex');}
  finally {await handle.close().catch(()=>{});}
}

// Public ZIPs are deliberately hardlinked to the immutable pack cache. This
// read-only inventory must not relax the no-hardlinks rule for control code,
// journals or writes. Reject reparse paths and verify file identity and content
// timestamps before and after reading through one open handle.
export async function publicMediaInventory(directory, previous = {}) {
  await noLinks(directory);
  const stamp = stat => `${stat.dev}:${stat.ino}:${stat.size}:${stat.mtimeMs}:${stat.ctimeMs}`;
  const regular = stat => { if (!stat.isFile() || stat.isSymbolicLink()) throw new Error('Unsafe public archive'); return stat; };
  const next = {};
  const names = (await fs.readdir(directory)).filter(name => /^kesas-.*\.zip$/.test(name) && name !== 'kesas-host-status.zip').sort();
  for (const name of names) {
    const file = path.join(directory, name);
    const before = regular(await fs.lstat(file));
    const identity = stamp(before);
    let sha256 = previous[name]?.stamp === identity && /^[a-f0-9]{64}$/.test(previous[name]?.sha256 || '') ? previous[name].sha256 : null;
    if (!sha256) {
      const hash = crypto.createHash('sha256');
      const handle = await fs.open(file, 'r');
      try {
        if (stamp(regular(await handle.stat())) !== identity) throw new Error('Public archive changed during inventory');
        const buffer = Buffer.alloc(1024 * 1024);
        for (;;) {
          const { bytesRead } = await handle.read(buffer, 0, buffer.length, null);
          if (!bytesRead) break;
          hash.update(buffer.subarray(0, bytesRead));
        }
        if (stamp(regular(await handle.stat())) !== identity) throw new Error('Public archive changed during inventory');
        sha256 = hash.digest('hex');
      } finally { await handle.close(); }
    }
    if (stamp(regular(await fs.lstat(file))) !== identity) throw new Error('Public archive changed during inventory');
    next[name] = { stamp: identity, sha256 };
  }
  await noLinks(directory);
  return { cache: next, sha256: digest(Buffer.from(JSON.stringify(Object.entries(next).map(([name, row]) => ({ name, sha256: row.sha256 }))))) };
}
