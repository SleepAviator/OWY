"""Private, minimal Codex telemetry merge. Never restarts Codex or prints config."""
import datetime
import os
from pathlib import Path
import re
import shutil
import sys
import tomllib

ENDPOINT = 'http://127.0.0.1:4319/v1/logs'
EXPORTER = 'exporter = { otlp-http = { endpoint = "' + ENDPOINT + '", protocol = "json" } }'


def merge(text):
    data = tomllib.loads(text)
    otel = data.get('otel', {})
    desired = {'otlp-http': {'endpoint': ENDPOINT, 'protocol': 'json'}}
    if otel.get('exporter') == desired and otel.get('log_user_prompt', False) is False:
        return text
    exporter = otel.get('exporter', 'none')
    if exporter not in ('none',) and exporter != desired:
        raise ValueError('A different telemetry exporter is already configured; it was preserved.')
    if not otel and not re.search(r'(?m)^\s*\[otel\]\s*$', text):
        updated = text.rstrip() + '\n\n[otel]\nlog_user_prompt = false\n' + EXPORTER + '\n'
    else:
        match = re.search(r'(?m)^\s*\[otel\]\s*$', text)
        if not match:
            raise ValueError('Existing telemetry uses unsupported table syntax; it was preserved.')
        end = re.search(r'(?m)^\s*\[', text[match.end():])
        stop = match.end() + end.start() if end else len(text)
        section = text[match.end():stop]
        if 'exporter' in otel:
            section, n = re.subn(r'(?m)^\s*exporter\s*=\s*["\']none["\']\s*(?:#.*)?$', '\n' + EXPORTER, section)
            if not n and exporter != desired:
                raise ValueError('Existing telemetry exporter could not be merged safely.')
        else:
            section = '\n' + EXPORTER + section
        if 'log_user_prompt' in otel:
            section, n = re.subn(r'(?m)^\s*log_user_prompt\s*=\s*(true|false)\s*(?:#.*)?$', '\nlog_user_prompt = false', section)
            if not n:
                raise ValueError('Existing prompt setting could not be merged safely.')
        else:
            section = '\nlog_user_prompt = false' + section
        updated = text[:match.end()] + section.rstrip() + '\n\n' + text[stop:]
    after = tomllib.loads(updated)
    if after.get('otel', {}).get('exporter') != desired or after['otel']['log_user_prompt'] is not False:
        raise ValueError('Telemetry validation failed; original configuration was preserved.')
    if {k: v for k, v in data.items() if k != 'otel'} != {k: v for k, v in after.items() if k != 'otel'}:
        raise ValueError('Unrelated settings changed; original configuration was preserved.')
    return updated


def main():
    home = Path(os.environ.get('CODEX_HOME', str(Path.home() / '.codex')))
    path = home / 'config.toml'
    raw = path.read_bytes() if path.exists() else b''
    text = raw.decode('utf-8-sig')
    updated = merge(text)
    if updated == text:
        print('Telemetry is already configured. Restart Codex yourself if needed.')
        return
    backups = Path(os.environ['LOCALAPPDATA']) / 'OWY' / 'telemetry-backups'
    backups.mkdir(parents=True, exist_ok=True)
    if path.exists():
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
        shutil.copy2(path, backups / ('config-' + stamp + '.toml'))
    home.mkdir(parents=True, exist_ok=True)
    pending = path.with_name('config.toml.owy.tmp')
    prefix = b'\xef\xbb\xbf' if raw.startswith(b'\xef\xbb\xbf') else b''
    pending.write_bytes(prefix + updated.encode('utf-8'))
    # Refuse overwriting a concurrent edit between backup/validation and commit.
    current = path.read_bytes() if path.exists() else b''
    if current != raw:
        pending.unlink()
        raise ValueError('Codex configuration changed during the merge; retry after saving it.')
    os.replace(pending, path)
    print('Telemetry configured for loopback JSON with prompt logging disabled. Restart Codex yourself.')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
