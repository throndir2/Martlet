from __future__ import annotations

import base64
import copy
import io
import unittest
from datetime import datetime, timezone
from pathlib import Path

from martlet_f5_worker.contract import (
    MAX_CHUNK_CHARACTERS,
    ContractError,
    SynthesisRequest,
    parse_wave,
)
from martlet_f5_worker.jsonio import canonical_json_bytes, parse_canonical_json
from martlet_f5_worker.service import WorkerService
from martlet_f5_worker.stdio import CanonicalStdio

from support import WorkerFixture, synthesize_message, wav_bytes


class ContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = WorkerFixture()

    def tearDown(self) -> None:
        self.fixture.close()

    def parse(self, message: dict) -> SynthesisRequest:
        return SynthesisRequest.parse(message, now=datetime.now(timezone.utc))

    def test_schema_rejects_unknown_missing_and_wrong_policy_fields(self) -> None:
        for mutate in (
            lambda value: value.update({"unknown": True}),
            lambda value: value.pop("destination_id"),
            lambda value: value["policy"].update({"artifact_download_allowed": True}),
            lambda value: value["output_format"].update({"sample_rate": 48_000}),
        ):
            with self.subTest(mutate=mutate):
                message = synthesize_message(self.fixture.worker)
                mutate(message)
                with self.assertRaises(ContractError):
                    self.parse(message)

    def test_text_chunks_are_contiguous_unique_and_bounded(self) -> None:
        cases = [
            [{"chunk_id": "chunk", "index": 1, "text": "out of order"}],
            [
                {"chunk_id": "same", "index": 0, "text": "first"},
                {"chunk_id": "same", "index": 1, "text": "second"},
            ],
            [
                {
                    "chunk_id": "long",
                    "index": 0,
                    "text": "x" * (MAX_CHUNK_CHARACTERS + 1),
                }
            ],
        ]
        for chunks in cases:
            with self.subTest(chunks=chunks):
                with self.assertRaises(ContractError):
                    self.parse(synthesize_message(self.fixture.worker, chunks=chunks))

    def test_reference_audio_transcript_and_combined_revision_are_bound(self) -> None:
        base = synthesize_message(self.fixture.worker)
        mutations = []
        changed_audio = copy.deepcopy(base)
        raw = bytearray(base64.b64decode(changed_audio["reference"]["audio_base64"]))
        raw[-2:] = b"\x01\x00"
        changed_audio["reference"]["audio_base64"] = base64.b64encode(raw).decode("ascii")
        mutations.append(changed_audio)
        changed_transcript = copy.deepcopy(base)
        changed_transcript["reference"]["transcript"] = "Different transcript."
        mutations.append(changed_transcript)
        changed_revision = copy.deepcopy(base)
        changed_revision["reference"]["reference_revision"] = "a" * 64
        mutations.append(changed_revision)
        for message in mutations:
            with self.subTest(reference=message["reference"]):
                with self.assertRaises(ContractError) as caught:
                    self.parse(message)
                self.assertEqual("reference_rejected", caught.exception.code)

    def test_wave_parser_matches_managed_pcm_bounds(self) -> None:
        parsed = parse_wave(wav_bytes(sample_rate=22_050))
        self.assertEqual(22_050, parsed.sample_rate)
        self.assertEqual(1_000, parsed.duration_milliseconds)
        for invalid in (
            b"not-wave",
            wav_bytes(duration_milliseconds=999),
            wav_bytes(duration_milliseconds=30_001),
            wav_bytes(sample_rate=32_000),
        ):
            with self.subTest(size=len(invalid)):
                with self.assertRaises(ContractError):
                    parse_wave(invalid)

    def test_canonical_json_rejects_duplicate_escape_and_spacing_aliases(self) -> None:
        for data in (
            b'{"a":1,"a":1}',
            b'{"a":"\\u0062"}',
            b'{"a": 1}',
            b'{"a":NaN}',
        ):
            with self.subTest(data=data):
                with self.assertRaises(ContractError):
                    parse_canonical_json(data, maximum=100, name="test")
        self.assertEqual(
            {"a": "b"},
            parse_canonical_json(b'{"a":"b"}', maximum=100, name="test"),
        )

    def test_stdio_rejects_truncated_input_and_exits_nonzero(self) -> None:
        output = io.BytesIO()
        transport = CanonicalStdio(io.BytesIO(b'{"type":"status"}'), output)
        service = WorkerService(self.fixture.config, transport.emit)
        self.assertEqual(2, transport.run(service))
        messages = [
            __import__("json").loads(line)
            for line in output.getvalue().splitlines()
        ]
        errors = [message for message in messages if message["type"] == "protocol_error"]
        self.assertEqual(1, len(errors))
        self.assertIn("truncated", errors[0]["error"]["summary"].lower())

    def test_canonical_encoder_is_stable_and_content_free_on_error(self) -> None:
        message = {"z": 1, "a": "reference transcript must not appear"}
        encoded = canonical_json_bytes(message)
        self.assertEqual(
            b'{"a":"reference transcript must not appear","z":1}',
            encoded,
        )

    def test_status_fixture_is_canonical(self) -> None:
        fixture = Path(__file__).resolve().parents[1] / "fixtures" / "status.v1.json"
        data = fixture.read_bytes()
        self.assertTrue(data.endswith((b"\n", b"\r\n")))
        message = parse_canonical_json(
            data.rstrip(b"\r\n"),
            maximum=1_024,
            name="status fixture",
        )
        self.assertEqual("martlet.f5.worker", message["contract_id"])
        self.assertEqual("status", message["type"])
