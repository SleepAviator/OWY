"""Local Codex-only worker. Bounded numeric IPC, RAM accounting, no output files."""
import json
import math
import sys
sys.dont_write_bytecode = True
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent / 'tokenscope'))
from live_probe import Probe
from live_meter import ReceivedOutput, project_sources, validate_packet

MAX_FRAME_BYTES = 128 * 1024


class Meter:
    def __init__(self, now):
        self.received = ReceivedOutput(now, retention_seconds=600)
        self.sequence = 0

    def frame(self, raw, now, wall):
        packet = validate_packet(raw)
        # Receipt metadata is projected independently from generation timing.
        receipts = []
        for item in raw.get('receipts', []):
            if (item.get('runtime') != 'codex' or item.get('basis') not in ('native_log', 'codex_otel')
                    or not isinstance(item.get('event_key'), str) or len(item['event_key']) > 256
                    or not isinstance(item.get('output_tokens'), int) or isinstance(item['output_tokens'], bool)
                    or not 0 < item['output_tokens'] < 2**63
                    or not isinstance(item.get('received_at'), (int, float))
                    or not math.isfinite(item['received_at']) or item['received_at'] > wall + 1):
                raise ValueError('Invalid receipt projection')
            receipts.append({key: item[key] for key in ('event_key', 'session_key', 'runtime', 'provider', 'model',
                                                       'basis', 'output_tokens', 'received_at')})
        packet['receipts'] = receipts
        packet['authoritative_sessions'] = raw.get('authoritative_sessions', [])
        states = {'Local': dict(packet=packet, received_at=now, error=None)}
        live = project_sources(states, now)
        self.received.receive(states, now)
        self.received.finish(live, now)
        pace = self.received.average_snapshot(now)
        # Accounting happens before IPC/UI coalescing. Only completed seconds
        # become history; the live average already includes a current receipt.
        changes = [dict(second=row['start'], output_tokens=row['output_tokens'],
                        observed=row['observed'], partial=row['partial'])
                   for row in self.received.history_updates(now, wall)]
        self.sequence += 1
        telemetry = raw.get('telemetry') or {}
        received_at = telemetry.get('last_received_at')
        batches = telemetry.get('diagnostics', {}).get('export_batches_received', 0)
        if (received_at is not None and (not isinstance(received_at, (int, float))
                                        or not math.isfinite(received_at) or not 0 <= received_at <= wall + 1)):
            raise ValueError('Invalid telemetry receipt timestamp')
        if not isinstance(batches, int) or isinstance(batches, bool) or not 0 <= batches <= 2**53:
            raise ValueError('Invalid telemetry batch counter')
        return dict(version=1, sequence=self.sequence, sampled_at=wall,
                    total_tps=live['total_tps'], average_tps=live['average_tps'],
                    contributing_sessions=live['contributing_sessions'], pace_300_tps=pace['average_tps'],
                    pace_300_tokens=pace['output_tokens'], pace_partial=pace['partial'],
                    coverage=live['status'], issues=live['issues'], pending_count=len(live['pending_sessions']),
                    telemetry_received_at=received_at, telemetry_batches=batches,
                    sessions=[dict(tps=s['tps'], provider=s['provider'], model=s['model']) for s in live['sessions']],
                    receipt_changes=changes)


def run():
    stop = threading.Event()
    def controller():
        for line in sys.stdin:
            if line.strip() == 'quit':
                break
        stop.set()
    threading.Thread(target=controller, name='owy-controller', daemon=True).start()
    probe = Probe(dict(collection_profile='codex', codex_otel_port='4319'))
    meter = Meter(time.monotonic())
    try:
        while not stop.is_set():
            started = time.monotonic()
            frame = meter.frame(probe.snapshot(), started, time.time())
            text = json.dumps(frame, separators=(',', ':'), allow_nan=False)
            if len(text.encode('utf-8')) > MAX_FRAME_BYTES:
                raise ValueError('Worker snapshot exceeds IPC bound')
            print(text, flush=True)
            stop.wait(max(0, 1 - (time.monotonic() - started)))
    except BrokenPipeError:
        stop.set()
    finally:
        probe.close()


if __name__ == '__main__':
    run()
