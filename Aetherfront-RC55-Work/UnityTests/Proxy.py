"""Bounded loopback-only UDP impairment for the two owned modded clients."""
import asyncio, json, random, sys, time
from pathlib import Path

output = Path(sys.argv[1]).resolve()
assert output.name == 'rc55-stability' and output.parent.name == 'rc50-input'
policy = output / 'proxy-policy.json'
randomizer = random.Random(55)
state = {'nonce': '', 'port':28876, 'hold_until': 0, 'drop_until': 0, 'queued': 0, 'forwarded': 0, 'dropped': 0}
def refresh():
    if policy.exists():
        value = json.loads(policy.read_text())
        if value.get('nonce') != state['nonce']:
            state['nonce'] = value.get('nonce')
            state['port'] = int(value.get('port', 28876))
            state['hold_until'] = time.monotonic() + min(20, max(0, float(value.get('holdServerSeconds', 0))))
            state['drop_until'] = time.monotonic() + min(25, max(0, float(value.get('dropClientSeconds', 0))))
            with (output / 'proxy.log').open('a') as f: f.write(json.dumps({'utc':time.time(), 'policy':value}) + '\n')
def forward(transport, data, address, server_direction, port):
    refresh()
    if port == state['port'] and not server_direction and time.monotonic() < state['drop_until']:
        state['dropped'] += 1; return
    if state['queued'] >= 4096:
        state['dropped'] += 1; return
    delay = .1 + randomizer.random() * .05
    if port == state['port'] and server_direction: delay = max(delay, state['hold_until'] - time.monotonic())
    state['queued'] += 1
    def send():
        state['queued'] -= 1; state['forwarded'] += 1; transport.sendto(data, address)
    asyncio.get_running_loop().call_later(delay, send)
class Upstream(asyncio.DatagramProtocol):
    def __init__(self, parent): self.parent = parent
    def connection_made(self, transport): self.transport = transport
    def datagram_received(self, data, address):
        if self.parent.client: forward(self.parent.transport, data, self.parent.client, True, self.parent.port)
class Front(asyncio.DatagramProtocol):
    def connection_made(self, transport): self.transport = transport; self.client = None; self.port = transport.get_extra_info('sockname')[1]
    def datagram_received(self, data, address):
        if address[0] != '127.0.0.1': return
        if self.client and self.client != address:
            old = self.upstream; self.client = address; self.upstream = None
            if old: old.close()
            asyncio.create_task(self.replace_upstream(data, address)); return
        if self.upstream is None: return
        self.client = address; forward(self.upstream, data, ('127.0.0.1', 28875), False, self.port)
    async def replace_upstream(self, data, address):
        candidate, _ = await asyncio.get_running_loop().create_datagram_endpoint(lambda:Upstream(self), local_addr=('127.0.0.1', 0))
        if self.client != address: candidate.close(); return
        self.upstream = candidate
        forward(self.upstream, data, ('127.0.0.1', 28875), False, self.port)
async def main():
    loop = asyncio.get_running_loop()
    for port in [28876, 28877]:
        front = Front()
        front.upstream, _ = await loop.create_datagram_endpoint(lambda:Upstream(front), local_addr=('127.0.0.1', 0))
        await loop.create_datagram_endpoint(lambda:front, local_addr=('127.0.0.1', port))
    (output / 'proxy-ready.txt').write_text('Loopback-only ports 28876/28877 ->28875; baseline RTT 200-300ms; bounded4096 datagrams.\n')
    while True:
        await asyncio.sleep(1); refresh()
        (output / 'proxy-status.json').write_text(json.dumps(state))
asyncio.run(main())
