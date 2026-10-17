import fs from 'node:fs';
import path from 'node:path';

function processExists(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try { process.kill(pid, 0); return true; }
  catch (error) { return error?.code === 'EPERM'; }
}

export function acquireSingleInstance(lockPath) {
  const resolved = path.resolve(lockPath);
  fs.mkdirSync(path.dirname(resolved), { recursive: true });

  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      const fd = fs.openSync(resolved, 'wx');
      fs.writeFileSync(fd, JSON.stringify({ pid: process.pid, startedAt: new Date().toISOString() }));
      fs.closeSync(fd);

      let released = false;
      const release = () => {
        if (released) return;
        released = true;
        try {
          const current = JSON.parse(fs.readFileSync(resolved, 'utf8'));
          if (Number(current?.pid) === process.pid) fs.rmSync(resolved, { force: true });
        } catch {}
      };
      process.once('exit', release);
      return release;
    } catch (error) {
      if (error?.code !== 'EEXIST') throw error;
      let holder = null;
      try { holder = JSON.parse(fs.readFileSync(resolved, 'utf8')); } catch {}
      if (processExists(Number(holder?.pid))) return null;
      fs.rmSync(resolved, { force: true });
    }
  }

  return null;
}
