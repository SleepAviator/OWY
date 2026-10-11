import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parent / 'tokenscope'))
from live_probe import Probe, NativeState
from live_telemetry import TelemetryState, opaque
from owy_worker import Meter

BASE = 1767225600


def event(kind, stamp, tokens=None, sid='session', response='response'):
    return dict(kind=kind, stamp=BASE+stamp, source_event_at=BASE+stamp, output_tokens=tokens,
                session_key=opaque('codex', sid) if sid else None,
                response_key=opaque('codex', 'response', response), provider='OpenAI', model='gpt-test')


class ReceiptTests(unittest.TestCase):
    def test_untimed_and_late_receipts_affect_pace_without_invented_generation_rate(self):
        for start, finish, received in ((None, 5, 6), (1, 5, 12)):
            with self.subTest(start=start):
                state=TelemetryState(BASE)
                events=([event('response.created',start)] if start else [])+[event('response.completed',finish,300)]
                state.process(events,BASE+received)
                raw=state.snapshot(BASE+received)
                raw['authoritative_sessions']=raw['covered_session_keys']
                self.assertEqual(raw['samples'], [])
                self.assertEqual(raw['receipts'][0]['output_tokens'],300)
                meter=Meter(0)
                first=meter.frame(raw,received,BASE+received)
                self.assertEqual(first['pace_300_tps'],1)
                self.assertFalse(any(row['output_tokens'] for row in first['receipt_changes']))
                raw['sampled_at']=BASE+received+1
                second=meter.frame(raw,received+1,BASE+received+1)
                self.assertEqual(second['pace_300_tps'],1)
                self.assertIsNone(first['total_tps'])
                self.assertTrue(second['pace_partial'])

    def test_exact_receipt_expiry_includes_current_partial_second(self):
        state=TelemetryState(BASE)
        state.process([event('response.completed',5,7496)],BASE+5.25)
        meter=Meter(0)
        first=meter.frame(state.snapshot(BASE+5.25),5.25,BASE+5.25)
        self.assertEqual(first['pace_300_tokens'],7496)
        self.assertAlmostEqual(first['pace_300_tps'],7496/300)
        self.assertFalse(any(row['output_tokens'] for row in first['receipt_changes']))
        completed=meter.frame(state.snapshot(BASE+6),6,BASE+6)
        self.assertEqual(sum(row['output_tokens'] for row in completed['receipt_changes']),7496)
        self.assertEqual(meter.frame(state.snapshot(BASE+305.24),305.24,BASE+305.24)['pace_300_tokens'],7496)
        self.assertEqual(meter.frame(state.snapshot(BASE+305.26),305.26,BASE+305.26)['pace_300_tokens'],0)

    def test_waiting_is_a_receipt_zero_but_a_stalled_worker_is_unavailable(self):
        state=TelemetryState(BASE)
        state.process([event('response.created',.1)],BASE+.1)
        meter=Meter(0)
        meter.frame(state.snapshot(BASE+.1),.1,BASE+.1)
        waiting=meter.frame(state.snapshot(BASE+1),1,BASE+1)
        self.assertIsNone(waiting['total_tps'])
        self.assertEqual(waiting['pace_300_tps'],0)
        self.assertTrue(waiting['receipt_changes'][0]['observed'])
        self.assertEqual(waiting['receipt_changes'][0]['output_tokens'],0)
        stalled=meter.frame(state.snapshot(BASE+5),5,BASE+5)
        self.assertTrue(all(not row['observed'] for row in stalled['receipt_changes']))

    def test_completed_receipts_keep_their_interval_without_retry_replay(self):
        state=TelemetryState(BASE)
        state.process([event('response.completed',5,100)],BASE+5.25)
        meter=Meter(0)
        meter.frame(state.snapshot(BASE+5.25),5.25,BASE+5.25)
        first=meter.frame(state.snapshot(BASE+6),6,BASE+6)
        original=next(row for row in first['receipt_changes'] if row['output_tokens'])
        # A repeated cumulative report is not another delivery or history write.
        retry=meter.frame(state.snapshot(BASE+7),7,BASE+7)
        self.assertEqual(sum(row['output_tokens'] for row in retry['receipt_changes']),0)
        self.assertEqual(retry['pace_300_tokens'],100)
        self.assertEqual(original['second'],BASE+5)

    def test_cumulative_retry_counts_only_increase(self):
        state=TelemetryState(BASE)
        meter=Meter(0)
        for count, stamp in ((100,5),(100,6),(150,7),(120,8)):
            state.process([event('response.completed',5,count)],BASE+stamp)
            frame=state.snapshot(BASE+stamp)
            meter.frame(frame,stamp,BASE+stamp)
        frame=state.snapshot(BASE+9)
        result=meter.frame(frame,9,BASE+9)
        self.assertEqual(result['pace_300_tokens'],150)

    def test_pending_or_zero_count_does_not_hide_later_valid_usage(self):
        for initial in (None, 0):
            with self.subTest(initial=initial):
                state=TelemetryState(BASE)
                state.process([event('response.completed',5,initial)],BASE+5)
                state.process([event('response.completed',5,200)],BASE+6)
                self.assertEqual(state.snapshot(BASE+6)['receipts'][0]['output_tokens'],200)

    def test_idle_zero_and_300_second_expiry(self):
        state=TelemetryState(BASE)
        meter=Meter(0)
        state.process([event('response.completed',1,900)],BASE+1)
        for second in range(1,303):
            raw=state.snapshot(BASE+second)
            value=meter.frame(raw,second,BASE+second)
            if second==2:self.assertEqual(value['pace_300_tps'],3)
        self.assertEqual(value['pace_300_tps'],0)

    def test_profile_has_no_claude_or_cc_switch_io(self):
        with patch('live_probe.time.time',return_value=BASE), patch.object(Probe,'discover'), patch.object(Probe,'read_codex_activity'):
            probe=Probe({'collection_profile':'codex','codex_home':'/does-not-exist'})
            self.assertEqual(probe.read_database(),[])
            probe.close()

    def test_native_untimed_receipt_and_startup_exclusion(self):
        with patch('live_probe.time.time',return_value=BASE),patch.object(Probe,'discover'),patch.object(Probe,'read_codex_activity'):
            probe=Probe({'collection_profile':'codex'})
        state=NativeState('codex');state.sid='session';probe.now=BASE+10
        state.sample(probe,'response',{'output_tokens':300,'reasoning_output_tokens':200},None,None,BASE+9,True)
        self.assertEqual(probe.receipts['response']['output_tokens'],300)
        self.assertEqual(probe.samples,{})
        state.sample(probe,'startup',{'output_tokens':1000},1,2,BASE-1,False)
        self.assertNotIn('startup',probe.receipts)
        probe.close()

    def test_unattributed_output_receipt_is_retained(self):
        state=TelemetryState(BASE)
        state.process([event('response.completed',5,100,sid=None)],BASE+5)
        self.assertEqual(state.snapshot(BASE+5)['receipts'][0]['output_tokens'],100)


if __name__=='__main__': unittest.main()
