import { spawn, spawnSync } from 'node:child_process';
import { existsSync, readdirSync, rmSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const apps = fileURLToPath(new URL('./.app/', import.meta.url));
// Each run owns its publish directory, even when another local app is running.
const output = join(apps, String(process.pid));

function isRunning(pid) {
    try { process.kill(pid, 0); return true; }
    catch (error) { return error.code === 'EPERM'; }
}
function remove(directory) {
    // A leftover directory must not fail the run; the next run tries again.
    try { rmSync(directory, { recursive: true, force: true, maxRetries: 3 }); }
    catch (error) { console.warn(`Could not remove ${directory}: ${error.message}`); }
}
// The test runner may kill this process without letting it clean up, so earlier runs are cleared here.
if (existsSync(apps))
    for (const name of readdirSync(apps))
        if (!/^\d+$/.test(name) || !isRunning(Number(name))) remove(join(apps, name));

// Test the published app, including the production Vue build and lazy background.
const publish = spawnSync('dotnet', [
    'publish', 'BobFx.Core/BobFx.Core.csproj', '--configuration', 'Release',
    '--no-restore', '--disable-build-servers', '-m:1', '--output', output, '/p:UseAppHost=false',
], { cwd: root, stdio: 'inherit' });
if (publish.error) throw publish.error;
if (publish.status !== 0) { remove(output); process.exit(publish.status ?? 1); }

const server = spawn('dotnet', ['BobFx.Core.dll'], {
    cwd: output, stdio: 'inherit', env: { ...process.env, Settings__DataPath: join(output, 'data') },
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => server.kill(signal));
server.on('error', error => { console.error(error); process.exitCode = 1; });
server.on('exit', code => { process.exitCode = code ?? 1; remove(output); });
