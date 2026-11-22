"""Mobile remote: API and security tests.

    python -I tests/remote_test.py dist/Duyu.exe

Runs the app in --remote-test mode: demo model, no audio engine, loopback only.
"""
import concurrent.futures
import json
import math
import pathlib
import socket
import subprocess
import sys
import tempfile
import time

PORT = 47809
AUTH = f'127.0.0.1:{PORT}'
DIR = pathlib.Path(tempfile.mkdtemp(prefix='DuyuRemote_'))
passed = failed = 0


def check(ok, name, info=''):
    global passed, failed
    passed += bool(ok)
    failed += not ok
    print(('PASS  ' if ok else 'FAIL  ') + name + ('' if ok else f'  -> {str(info)[:160]}'))


def desktop(command):
    """Act as the person at the PC."""
    out, cmd, tmp = DIR / 'out.txt', DIR / 'cmd.txt', DIR / 'cmd.tmp'
    tmp.write_text(command)
    tmp.replace(cmd)
    for _ in range(150):
        time.sleep(.05)
        if not cmd.exists():
            return out.read_text(encoding='utf-8-sig')
    raise RuntimeError('app did not answer: ' + command)


def raw(data, timeout=8):
    s = socket.create_connection(('127.0.0.1', PORT), timeout)
    chunks = b''
    try:
        s.sendall(data)
        while True:
            part = s.recv(65536)
            if not part:
                break
            chunks += part
    except OSError:
        pass
    finally:
        s.close()
    if not chunks:
        return 0, '', b''
    head, _, body = chunks.partition(b'\r\n\r\n')
    return int(head.split(b' ')[1]), head.decode(), body


def call(path, body=None, key=None, host=AUTH, extra=(), marker=True, method=None, ctype='application/json'):
    data = b'' if body is None else body if isinstance(body, bytes) else json.dumps(body).encode()
    lines = [f'{method or ("GET" if body is None else "POST")} {path} HTTP/1.1']
    if host:
        lines.append(f'Host: {host}')
    if marker:
        lines.append('X-Duyu: 1')
    if key:
        lines.append(f'Cookie: other=1; duyu={key}')
    if body is not None:
        lines += [f'Content-Type: {ctype}', f'Content-Length: {len(data)}']
    lines += list(extra)
    status, head, payload = raw(('\r\n'.join(lines) + '\r\n\r\n').encode() + data)
    try:
        parsed = json.loads(payload)
    except ValueError:
        parsed = payload
    return status, head, parsed


def pair(pin):
    status, head, _ = call('/api/pair', {'pin': pin})
    key = head.split('Set-Cookie: duyu=')[1].split(';')[0] if status == 200 else None
    return status, head, key


def main(exe):
    app = subprocess.Popen([exe, '--remote-test', str(DIR), '--port', str(PORT)])
    try:
        for _ in range(150):
            time.sleep(.1)
            if (DIR / 'out.txt').exists():
                break
        pin = (DIR / 'out.txt').read_text(encoding='utf-8-sig')
        run(pin, app.pid)
        desktop('quit')
        check(app.wait(8) == 0, 'app closes cleanly')
    finally:
        if app.poll() is None:
            app.kill()


def run(pin, pid):
    rows = [l.split() for l in subprocess.run(['netstat', '-ano', '-p', 'TCP'], capture_output=True, text=True).stdout.splitlines()]
    mine = [r[1] for r in rows if len(r) >= 5 and r[-1] == str(pid) and r[2] == '0.0.0.0:0']
    check(mine == [AUTH], 'listens on one address, never 0.0.0.0', mine)

    # page
    status, head, page = call('/', marker=False)
    check(status == 200 and b'<nav' in page, 'page is served')
    check(b'__NONCE__' not in page and "script-src 'nonce-" in head, 'CSP nonce applied')
    check('unsafe-inline' not in head and "frame-ancestors 'none'" in head, 'CSP strict, no framing')
    nonce = head.split("script-src 'nonce-")[1].split("'")[0]
    check(page.count(nonce.encode()) == 2, 'script and style carry the nonce')
    check(nonce != call('/')[1].split("script-src 'nonce-")[1].split("'")[0], 'nonce changes per response')
    check(call('/icon.png')[2][:4] == b'\x89PNG', 'home-screen icon is served')

    # no key, no access
    for path in ('/api/state?ch=0', '/api/meters'):
        check(call(path)[0] == 401, f'no key: {path}')
        check(call(path, key='f' * 64)[0] == 401, f'forged key: {path}')
    for path in ('/api/set', '/api/band', '/api/preset', '/api/profile', '/api/assign', '/api/nudge', '/api/sidetone'):
        check(call(path, {})[0] == 401, f'no key: POST {path}')

    # pairing
    wrong = '%06d' % ((int(pin) + 1) % 1000000)
    check(len(pin) == 6 and pin.isdigit(), 'PIN is six digits')
    check(pair(wrong)[0] == 401, 'wrong PIN rejected')
    check(call('/api/pair', {'pin': int(pin)})[0] == 400, 'non-string PIN rejected')
    status, head, key = pair(pin)
    check(status == 200 and len(key) == 64, '256-bit key issued')
    check('HttpOnly' in head and 'SameSite=Strict' in head, 'cookie HttpOnly + SameSite')
    second = pair(pin)[2]
    check(second and second != key, 'iPhone and iPad can share one PIN, keys differ')
    check(desktop('devices') == '2', 'two devices stored')

    pin = desktop('pin')
    wrong = '%06d' % ((int(pin) + 1) % 1000000)
    codes = [pair(wrong)[0] for _ in range(5)]
    check(codes == [401] * 4 + [410], 'five wrong tries burn the PIN', codes)
    check(pair(pin)[0] == 410, 'a burnt PIN no longer works, even when correct')
    pin = desktop('pin')
    with concurrent.futures.ThreadPoolExecutor(20) as pool:
        tries = list(pool.map(lambda i: pair('%06d' % ((int(pin) + 1 + i) % 1000000))[0], range(20)))
    check(tries.count(401) == 4, 'parallel guessing gets no extra tries', tries)

    # browser attacks
    good = {'p': 'MasterVol', 'i': 0, 'v': .5}
    check(call('/api/state?ch=0', key=key, host=f'evil.example:{PORT}')[0] == 421, 'DNS rebinding: foreign Host')
    check(call('/api/state?ch=0', key=key, host=None)[0] == 421, 'missing Host')
    check(call('/', host='evil.example')[0] == 421, 'page refuses foreign Host')
    check(call('/api/set', good, key, marker=False)[0] == 403, 'CSRF: missing custom header')
    check(call('/api/set', good, key, extra=['Origin: http://evil.example'])[0] == 403, 'CSRF: foreign Origin')
    check(call('/api/set', good, key, extra=['Origin: null'])[0] == 403, 'CSRF: null Origin')
    check(call('/api/set', good, key, ctype='text/plain')[0] == 415, 'CSRF: simple content type')
    status, head, _ = call('/api/set', key=key, method='OPTIONS')
    check(status == 405 and 'Access-Control' not in head, 'CORS preflight never granted')
    check(call('/api/set', good, key, extra=[f'Origin: http://{AUTH}'])[0] == 200, 'own Origin accepted')

    # every writable parameter: both ends, out of range, bad index
    status, _, state = call('/api/state?ch=0', key=key)
    check(status == 200 and len(state['curve']) == 97 and len(state['v']) > 30, 'state is complete')
    limits = {
        'MasterVol': (0, 1), 'Ceiling': (-24, -.5), 'Vol': (0, 1), 'StreamVol': (0, 1), 'Boost': (0, 12),
        'Bass': (-12, 12), 'Voice': (-12, 12), 'Treble': (-12, 12), 'Preamp': (-24, 12),
        'SmartAmount': (0, 1), 'SmartTarget': (-32, -10), 'DenoiseAmount': (0, 1), 'Immersion': (0, 1),
        'Distance': (0, 1), 'ChatMix': (-1, 1), 'MicGain': (-24, 24), 'NsAmount': (0, 1), 'ImpactAmount': (0, 1),
        'GateThreshold': (-70, -15), 'CompAmount': (0, 1), 'Sidetone': (0, 1)}
    for name, values in state['v'].items():
        low, high = limits.get(name, (0, 1))
        last = len(values) - 1
        ends = [high, low] if name != 'MicOn' else [high]  # switching the mic back on is tested below
        ok = all(call('/api/set', {'p': name, 'i': last, 'v': v}, key)[0] == 200 for v in ends)
        bad = [{'v': high + 1}, {'v': low - 1}, {'v': '1'}, {'v': None}, {'i': last + 1}, {'i': -1}]
        refused = all(call('/api/set', {'p': name, 'i': last, 'v': high, **b}, key)[0] == 400 for b in bad)
        check(ok and refused, f'range enforced: {name}')

    for name in ('SpatialTest', 'MicTest', 'SpeakerAngle', 'Retired2', 'COUNT', '0', '7', 'vol', 'Vol, Mute', ' Vol', ''):
        check(call('/api/set', {'p': name, 'i': 0, 'v': 1}, key)[0] == 403, f'not writable: {name!r}')
    check(call('/api/set', {'p': 'Mute', 'i': 0, 'v': .6}, key)[0] == 400, 'switches take only 0 or 1')

    # malformed input
    cases = {
        'truncated': b'{"p":', 'empty': b'', 'array': b'[1,2]', 'null': b'null', 'nested': b'{"a":' * 30 + b'1' + b'}' * 30,
        'NaN': b'{"p":"MasterVol","i":0,"v":NaN}', 'Infinity': b'{"p":"MasterVol","i":0,"v":Infinity}',
        'overflow': b'{"p":"MasterVol","i":0,"v":1e400}', 'float index': b'{"p":"Vol","i":1.5,"v":1}',
        'typed': b'{"__type":"System.IO.FileInfo","p":"Vol"}', 'bad utf-8': b'{"p":"\xff\xfe"}'}
    for name, body in cases.items():
        status = call('/api/set', body, key)[0]
        check(status in (400, 403), f'malformed JSON: {name}', status)
    check(math.isfinite(float(desktop('get MasterVol 0'))), 'nothing non-finite reached the model')
    check(call('/api/set', b' ' * 5000, key)[0] in (0, 413), 'oversized body refused')
    check(call('/api/state', key=key, extra=['X-Pad: ' + 'a' * 9000])[0] in (0, 431), 'oversized headers refused')
    check(call('/api/set', good, key, extra=['Transfer-Encoding: chunked'])[0] == 400, 'chunked body refused')
    check(call('/', extra=[f'Host: {AUTH}'])[0] == 400, 'duplicate header refused')
    check(raw(b'GARBAGE\r\n\r\n')[0] == 400, 'garbage request line refused')
    check(call('/api/state?ch=99999', key=key)[0] == 200, 'bad channel falls back safely')
    for path in ('/../settings.json', '/settings.json', '/api/export', '/api/import', '/Duyu.exe'):
        check(call(path, key=key)[0] == 404, f'no file access: {path}')

    # privileges
    call('/api/set', {'p': 'MicOn', 'i': 0, 'v': 0}, key)
    check(desktop('get MicOn 0') == '0', 'phone can switch the mic off')
    check(call('/api/set', {'p': 'MicOn', 'i': 0, 'v': 1}, key)[0] == 403, 'phone cannot switch the mic on')
    desktop('set MixerOn 0 0')
    desktop('seen 0')
    check(call('/api/set', {'p': 'MixerOn', 'i': 0, 'v': 1}, key)[0] == 403, 'mixer needs first consent on the PC')
    desktop('seen 1')
    check(call('/api/set', {'p': 'MixerOn', 'i': 0, 'v': 1}, key)[0] == 200, 'mixer can be switched on after consent')

    # sync and actions
    call('/api/set', {'p': 'Vol', 'i': 2, 'v': .37}, key)
    check(abs(float(desktop('get Vol 2')) - .37) < 1e-4, 'phone -> PC')
    desktop('set Vol 1 0.61')
    check(abs(call('/api/state?ch=0', key=key)[2]['v']['Vol'][1] - .61) < 1e-4, 'PC -> phone')

    band = {'ch': 0, 'i': 5, 'on': 1, 'type': 0, 'f': 1000, 'gain': 9, 'q': 1.2}
    before = call('/api/state?ch=0', key=key)[2]['curve']
    check(call('/api/band', band, key)[0] == 200, 'band edit accepted')
    after = call('/api/state?ch=0', key=key)[2]
    check(after['curve'] != before and after['names'][0] == 'Özel', 'band edit moves the curve')
    for change in ({'type': 9}, {'f': 1}, {'gain': 40}, {'q': 0}, {'ch': 6}, {'i': 10}, {'on': 2}):
        check(call('/api/band', {**band, **change}, key)[0] == 400, f'band range: {change}')

    presets = after['presets']
    index = next(i for i, p in enumerate(presets) if p['name'] == 'Gece')
    check(call('/api/preset', {'ch': 2, 'id': index, 'name': 'Gece'}, key)[0] == 200, 'preset applies')
    check(call('/api/state?ch=2', key=key)[2]['names'][2] == 'Gece', 'preset visible in state')
    check(call('/api/preset', {'ch': 2, 'id': index, 'name': 'Pop'}, key)[0] == 409, 'stale preset list detected')
    check(call('/api/preset', {'ch': 2, 'id': len(presets), 'name': 'x'}, key)[0] == 400, 'preset index checked')

    desktop('set Vol 0 0.1')
    desktop('set NsAmount 0 0.9')
    check(call('/api/profile', {'name': after['profiles'][0]['name']}, key)[0] == 200, 'profile applies')
    check(float(desktop('get Vol 0')) == 1, 'profile restores the mix')
    check(abs(float(desktop('get NsAmount 0')) - .9) < 1e-4, 'profile leaves the microphone alone')
    check(call('/api/profile', {'name': 'missing'}, key)[0] == 409, 'missing profile reported')

    check(call('/api/assign', {'exe': 'cs2.exe', 'ch': 3}, key)[0] == 200, 'app assignment')
    check(next(a for a in call('/api/state', key=key)[2]['apps'] if a['exe'] == 'cs2.exe')['ch'] == 3, 'assignment visible')
    check(call('/api/assign', {'exe': 'lsass.exe', 'ch': 0}, key)[0] == 409, 'only listed apps assignable')
    check(call('/api/assign', {'exe': 'steam.exe', 'ch': 0}, key)[0] == 409, 'shared-tree app not assignable')
    check(call('/api/assign', {'exe': 'cs2.exe', 'ch': 4}, key)[0] == 400, 'assignment range checked')

    desktop('set ChatMix 0 0')
    for _ in range(3):
        call('/api/nudge', {'dir': 1}, key)
    check(abs(float(desktop('get ChatMix 0')) - .3) < 1e-3, 'three quick taps are three steps')
    for _ in range(12):
        call('/api/nudge', {'dir': 1}, key)
    check(float(desktop('get ChatMix 0')) == 1, 'nudge clamps at the end')

    desktop('set Sidetone 0 0.42')
    call('/api/sidetone', {}, key)
    check(float(desktop('get Sidetone 0')) == 0, 'sidetone off')
    call('/api/sidetone', {}, key)
    check(abs(float(desktop('get Sidetone 0')) - .42) < 1e-4, 'sidetone remembers its level')

    with concurrent.futures.ThreadPoolExecutor(8) as pool:
        codes = list(pool.map(lambda i: call('/api/meters', key=key)[0], range(400)))
    check(codes.count(200) == 400, '400 requests, 8 at a time', {c: codes.count(c) for c in set(codes)})
    begun = time.perf_counter()
    for _ in range(50):
        call('/api/set', good, key)
    each = (time.perf_counter() - begun) / 50 * 1000
    check(each < 100, f'write round trip {each:.1f} ms')

    # slow clients
    slow = socket.create_connection(('127.0.0.1', PORT))
    slow.settimeout(1)
    begun, closed = time.time(), False
    while time.time() - begun < 9 and not closed:
        try:
            slow.sendall(b'G')
            if slow.recv(1) == b'':
                closed = True
        except socket.timeout:
            pass
        except OSError:
            closed = True
    check(closed and time.time() - begun < 8, 'slow client cut off at the deadline')
    slow.close()
    idle = [socket.create_connection(('127.0.0.1', PORT)) for _ in range(40)]
    time.sleep(6.5)
    check(call('/api/meters', key=key)[0] == 200, 'recovers from a connection flood')
    for s in idle:
        s.close()

    # export / import
    desktop('export')
    exported = json.loads((DIR / 'export.json').read_text(encoding='utf-8-sig'))
    check(exported['RemoteDevices'] == [] and exported['RemoteOn'] is False, 'export leaks no keys')
    check(key not in (DIR / 'export.json').read_text(encoding='utf-8-sig'), 'key never written in clear')
    exported['RemoteDevices'] = ['attacker-hash']
    exported['RemoteOn'] = False
    (DIR / 'import.json').write_text(json.dumps(exported), encoding='utf-8')
    count = desktop('devices')
    desktop('import')
    check(desktop('devices') == count, 'import cannot plant a device')
    check(call('/api/meters', key=key)[0] == 200, 'import cannot unpair or switch the remote off')

    # revoke and switch off
    desktop('revoke')
    check(desktop('devices') == '0', 'revoke clears devices')
    check(call('/api/state', key=key)[0] == 401 and call('/api/state', key=second)[0] == 401, 'revoked keys stop working at once')
    check(pair(pin)[0] == 410, 'revoke also burns the PIN')
    desktop('enable 0')
    try:
        socket.create_connection(('127.0.0.1', PORT), 2).close()
        check(False, 'switch off closes the port')
    except OSError:
        check(True, 'switch off closes the port')
    check(pair_after_enable(), 'switch on again pairs again')


def pair_after_enable():
    return pair(desktop('enable 1'))[0] == 200


if __name__ == '__main__':
    main(str(pathlib.Path(sys.argv[1]).resolve()))
    for f in DIR.iterdir():
        f.unlink()
    DIR.rmdir()
    print(f'\n{passed} passed, {failed} failed')
    sys.exit(1 if failed else 0)
