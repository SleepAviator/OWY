import unittest
import tomllib
from configure_telemetry import merge, ENDPOINT


class TelemetryMergeTests(unittest.TestCase):
    def test_preserves_private_provider_and_sections(self):
        text='model = "example"\n[model_providers.private]\nbase_url = "https://example.invalid"\nkey = "synthetic"\n'
        result=tomllib.loads(merge(text))
        self.assertEqual(result['model_providers'],tomllib.loads(text)['model_providers'])
        self.assertFalse(result['otel']['log_user_prompt'])
        self.assertEqual(result['otel']['exporter']['otlp-http']['endpoint'],ENDPOINT)

    def test_merges_disabled_otel_without_losing_other_settings(self):
        result=tomllib.loads(merge('[otel]\nexporter = "none"\nlog_user_prompt = true\nenvironment = "private"\n[other]\na=1\n'))
        self.assertEqual(result['otel']['environment'],'private')
        self.assertFalse(result['otel']['log_user_prompt'])
        self.assertEqual(result['other'],{'a':1})

    def test_other_exporter_is_preserved(self):
        with self.assertRaisesRegex(ValueError,'different telemetry'):
            merge('[otel]\nexporter = { otlp-http = {endpoint="https://example.invalid",protocol="json"}}\n')

    def test_idempotent(self):
        result=merge('model="example"\n')
        self.assertEqual(merge(result),result)

    def test_empty_existing_section(self):
        self.assertFalse(tomllib.loads(merge('[otel]\n[other]\nx=2\n'))['otel']['log_user_prompt'])


if __name__=='__main__':unittest.main()
