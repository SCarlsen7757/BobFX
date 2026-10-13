import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
// Each run owns its publish directory, even when another local app is running.
const output = fileURLToPath(new URL(`./.app/${process.pid}/`, import.meta.url));
// Test the published app, including generated Blazor scripts and scoped styles.
const publish = spawnSync('dotnet', [
    'publish', 'BobFx.Core/BobFx.Core.csproj', '--configuration', 'Release',
    '--no-restore', '--output', output, '/p:UseAppHost=false',
], { cwd: root, stdio: 'inherit' });
if (publish.error) throw publish.error;
if (publish.status !== 0) process.exit(publish.status ?? 1);

const server = spawn('dotnet', ['BobFx.Core.dll'], { cwd: output, stdio: 'inherit' });
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => server.kill(signal));
server.on('error', error => { console.error(error); process.exitCode = 1; });
server.on('exit', code => { process.exitCode = code ?? 1; });
