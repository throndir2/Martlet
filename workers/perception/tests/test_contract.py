from __future__ import annotations

import base64
import copy
import json
import unittest
import uuid
import zlib
from datetime import datetime, timezone

from martlet_perception_worker.contract import (
    ARTIFACT_ROLES,
    CONTRACT_ID,
    MAX_QUESTION_CHARACTERS,
    ROLES,
    ContractError,
    FrameContent,
    PerceptionRequest,
    artifact_identity_sha256,
    h03b_route_events,
    inspect_png,
    make_observation,
    parse_contract_header,
    parse_utc,
    response_wire,
    validate_engine_output,
    format_utc,
)
from martlet_perception_worker.jsonio import (
    JsonContractError,
    canonical_json_bytes,
    parse_canonical_json,
)

from tests.support import FixtureEnvironment, png, request_wire


class CanonicalContractTests(unittest.TestCase):
    def test_contract_roles_and_artifacts_are_exact_and_have_no_detector(self) -> None:
        self.assertEqual(CONTRACT_ID, "martlet.perception.worker")
        self.assertEqual(ROLES, ("ocr", "visual_question_answering"))
        self.assertEqual(
            ARTIFACT_ROLES,
            (
                "runtime_image",
                "model_weights",
                "model_configuration",
                "tokenizer",
                "projector",
            ),
        )
        self.assertNotIn("detector", ROLES)
        self.assertNotIn("detector", ARTIFACT_ROLES)

    def test_canonical_json_rejects_whitespace_duplicates_nonfinite_and_utf8(self) -> None:
        cases = (
            b'{"a": 1}',
            b'{"a":1,"a":1}',
            b'{"a":NaN}',
            b"\xff",
            b"[]",
        )
        for value in cases:
            with self.subTest(value=value):
                with self.assertRaises(JsonContractError):
                    parse_canonical_json(value, maximum=64, name="test")
        expected = {"a": [1, "two"], "z": False}
        self.assertEqual(
            parse_canonical_json(
                canonical_json_bytes(expected),
                maximum=64,
                name="test",
            ),
            expected,
        )

    def test_header_rejects_wrong_contract_and_minor_version(self) -> None:
        valid = {
            "contract_id": CONTRACT_ID,
            "protocol_version": {"major": 1, "minor": 0},
        }
        parse_contract_header(valid)
        for changed in (
            {**valid, "contract_id": "generic.worker"},
            {**valid, "protocol_version": {"major": 1, "minor": 1}},
            {**valid, "protocol_version": {"major": 2, "minor": 0}},
        ):
            with self.subTest(changed=changed):
                with self.assertRaises(ContractError):
                    parse_contract_header(changed)

    def test_valid_rgb_and_rgba_png_are_fully_inspected(self) -> None:
        self.assertEqual(inspect_png(png(3, 2, color_type=2)), (3, 2))
        self.assertEqual(inspect_png(png(3, 2, color_type=6)), (3, 2))

    def test_malformed_png_shapes_fail_closed(self) -> None:
        valid = png()
        cases = [
            b"",
            b"\x00" * 64,
            bytes((valid[0] ^ 1,)) + valid[1:],
            valid[:-1] + bytes((valid[-1] ^ 1,)),
            png(filter_type=9),
            png(interlace=1),
            png(color_type=0),
            png(extra_chunk=(b"tEXt", b"private")),
            valid + b"\x00",
            png(compressed_suffix=b"\x00"),
            png(width=4_097, height=1),
        ]
        for value in cases:
            with self.subTest(size=len(value)):
                with self.assertRaises(ContractError):
                    inspect_png(value)

    def test_png_decompression_is_strictly_bounded_by_declared_dimensions(
        self,
    ) -> None:
        header = (
            (1).to_bytes(4, "big")
            + (1).to_bytes(4, "big")
            + bytes((8, 2, 0, 0, 0))
        )
        oversized_rows = b"\x00" * (16 * 1_024 * 1_024)
        image = (
            b"\x89PNG\r\n\x1a\n"
            + _png_chunk(b"IHDR", header)
            + _png_chunk(b"IDAT", zlib.compress(oversized_rows, level=9))
            + _png_chunk(b"IEND", b"")
        )
        self.assertLess(len(image), 32 * 1_024)
        with self.assertRaises(ContractError):
            inspect_png(image)

    def test_inline_png_digest_dimensions_and_canonical_base64_are_bound(self) -> None:
        image = png()
        wire = {
            "byte_count": len(image),
            "data_base64": base64.b64encode(image).decode("ascii"),
            "height": 2,
            "kind": "inline_png",
            "sha256": __import__("hashlib").sha256(image).hexdigest(),
            "width": 2,
        }
        content = FrameContent.parse(wire)
        self.assertEqual(bytes(content.data or b""), image)
        content.wipe()
        self.assertIsNone(content.data)
        for key, value in (
            ("sha256", "0" * 64),
            ("width", 3),
            ("byte_count", len(image) + 1),
            ("data_base64", wire["data_base64"] + "="),
        ):
            changed = dict(wire)
            changed[key] = value
            with self.subTest(key=key):
                with self.assertRaises(ContractError):
                    FrameContent.parse(changed)

    def test_reference_is_opaque_bounded_and_rejects_urls_or_paths(self) -> None:
        base = {
            "byte_count": 79,
            "height": 2,
            "kind": "ephemeral_gateway_reference",
            "reference_expires_at_utc": "2030-01-01T00:00:00.0000000+00:00",
            "reference_id": "frame-ref-001",
            "sha256": "a" * 64,
            "width": 2,
        }
        parsed = FrameContent.parse(base)
        self.assertEqual(parsed.reference_id, "frame-ref-001")
        self.assertIsNone(parsed.data)
        for reference in (
            "https://host/frame",
            "C:\\frames\\one.png",
            "../frame",
            "/tmp/frame",
            "\\\\server\\share",
        ):
            changed = dict(base)
            changed["reference_id"] = reference
            with self.subTest(reference=reference):
                with self.assertRaises(ContractError):
                    FrameContent.parse(changed)

    def test_role_question_policy_and_limits_are_exact(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            valid = request_wire(environment)
            parsed = PerceptionRequest.parse(
                valid,
                now=datetime.now(timezone.utc),
            )
            self.assertIsNone(parsed.task.question)
            parsed.wipe()
            changed = copy.deepcopy(valid)
            changed["task"]["question"] = "OCR must not accept a prompt"
            with self.assertRaises(ContractError):
                PerceptionRequest.parse(changed, now=datetime.now(timezone.utc))
            changed = copy.deepcopy(valid)
            changed["policy"]["artifact_download_allowed"] = True
            with self.assertRaises(ContractError):
                PerceptionRequest.parse(changed, now=datetime.now(timezone.utc))
        with FixtureEnvironment("visual_question_answering") as environment:
            changed = request_wire(
                environment,
                question="x" * (MAX_QUESTION_CHARACTERS + 1),
            )
            with self.assertRaises(ContractError):
                PerceptionRequest.parse(changed, now=datetime.now(timezone.utc))

    def test_request_rejects_role_epoch_and_parent_identity_mismatch(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            valid = request_wire(environment)
            for mutate in (
                lambda item: item["task"].update(
                    role="visual_question_answering",
                    question="What?",
                ),
                lambda item: item.update(epoch=2),
                lambda item: item["ids"].update(
                    parent_request_id=item["ids"]["request_id"]
                ),
            ):
                changed = copy.deepcopy(valid)
                mutate(changed)
                with self.assertRaises(ContractError):
                    PerceptionRequest.parse(
                        changed,
                        now=datetime.now(timezone.utc),
                    )
            changed = copy.deepcopy(valid)
            changed["epoch"] = 2_147_483_647
            changed["frame"]["capture_epoch"] = 2_147_483_647
            with self.assertRaises(ContractError):
                PerceptionRequest.parse(
                    changed,
                    now=datetime.now(timezone.utc),
                )

    def test_dotnet_seventh_tick_round_trips_into_provenance(self) -> None:
        exact = "2026-09-22T00:00:00.1234567+00:00"
        self.assertEqual(format_utc(parse_utc(exact, "timestamp")), exact)
        with FixtureEnvironment("ocr") as environment:
            wire = request_wire(environment)
            captured = format_utc(
                parse_utc(wire["frame"]["captured_at_utc"], "captured")
            )
            wire["frame"]["captured_at_utc"] = captured[:-7] + "7+00:00"
            request = PerceptionRequest.parse(
                wire,
                now=datetime.now(timezone.utc),
            )
            output = {
                "ocr": {
                    "regions": [
                        {
                            "bounds": {
                                "bottom": 10_000,
                                "left": 0,
                                "right": 10_000,
                                "top": 0,
                            },
                            "confidence": {"kind": "unavailable"},
                            "index": 0,
                            "text": "fixture",
                        }
                    ]
                },
                "role": "ocr",
            }
            observation = make_observation(
                request,
                environment.worker,
                "fixture-host",
                output,
                processed_at_utc=datetime.now(timezone.utc),
            )
            self.assertEqual(
                observation["provenance"]["captured_at_utc"],
                wire["frame"]["captured_at_utc"],
            )
            request.wipe()

    def test_engine_output_requires_confidence_uncertainty_and_known_shape(self) -> None:
        valid = {
            "role": "visual_question_answering",
            "vlm": {
                "answer": "Visible fixture.",
                "confidence": {"kind": "unavailable"},
                "uncertainty": "Fixture only.",
            },
        }
        self.assertEqual(
            validate_engine_output(
                valid,
                role="visual_question_answering",
                maximum_output_bytes=16_384,
            )["vlm"]["uncertainty"],
            "Fixture only.",
        )
        for changed in (
            {"role": "visual_question_answering", "vlm": {"answer": "partial"}},
            {"role": "unknown", "output": "unknown"},
            {
                "role": "visual_question_answering",
                "vlm": {
                    "answer": "x",
                    "confidence": {"kind": "calibrated", "value": 2},
                    "uncertainty": "bad",
                },
            },
        ):
            with self.subTest(changed=changed):
                with self.assertRaises(ContractError):
                    validate_engine_output(
                        changed,
                        role="visual_question_answering",
                        maximum_output_bytes=16_384,
                    )

    def test_completed_observation_is_exact_h03b_payload_and_event_sequence(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            request = PerceptionRequest.parse(
                request_wire(environment),
                now=datetime.now(timezone.utc),
            )
            output = {
                "ocr": {
                    "detected_language": "fixture",
                    "regions": [
                        {
                            "bounds": {
                                "bottom": 10_000,
                                "left": 0,
                                "right": 10_000,
                                "top": 0,
                            },
                            "confidence": {"kind": "unavailable"},
                            "index": 0,
                            "text": "FIXTURE - NOT AI",
                        }
                    ],
                },
                "role": "ocr",
            }
            observation = make_observation(
                request,
                environment.worker,
                "fixture-host",
                output,
                processed_at_utc=datetime.now(timezone.utc),
            )
            response = response_wire(
                request,
                environment.worker,
                outcome="completed",
                observation=observation,
            )
            trace_id = str(uuid.uuid4())
            events = h03b_route_events(
                request,
                response,
                trace_id=trace_id,
            )
            self.assertEqual(
                [item["type"] for item in events],
                ["started", "observation", "completed"],
            )
            self.assertEqual([item["sequence"] for item in events], [0, 1, 2])
            self.assertEqual(
                events[0]["route_id"],
                "martlet.gateway.perception-ocr.v1",
            )
            self.assertEqual(
                events[0]["artifact_identity_sha256"],
                artifact_identity_sha256(environment.worker),
            )
            self.assertEqual(events[0]["trace_id"], trace_id)
            decoded = base64.b64decode(events[1]["data_base64"], validate=True)
            self.assertEqual(decoded, canonical_json_bytes(observation))
            payload = json.loads(decoded)
            self.assertEqual(
                set(payload),
                {"role", "provenance", "expires_at_utc", "ocr"},
            )
            self.assertEqual(payload["provenance"]["frame_id"], request.frame.frame_id)
            self.assertEqual(payload["provenance"]["host_id"], "fixture-host")
            request.wipe()

    def test_gateway_failure_mapping_is_bounded_optional_context(self) -> None:
        with FixtureEnvironment("visual_question_answering") as environment:
            request = PerceptionRequest.parse(
                request_wire(environment),
                now=datetime.now(timezone.utc),
            )
            response = response_wire(
                request,
                environment.worker,
                outcome="failed",
                error=ContractError(
                    "model_not_ready",
                    "The selected model is absent.",
                    remedy_code="perception.install-pinned-runtime",
                ),
            )
            events = h03b_route_events(
                request,
                response,
                trace_id=str(uuid.uuid4()),
            )
            self.assertEqual(events[-1]["type"], "failed")
            self.assertEqual(events[-1]["code"], "context.unavailable")
            self.assertEqual(
                events[-1]["route_id"],
                "martlet.gateway.perception-vlm.v1",
            )
            request.wipe()


def _png_chunk(kind: bytes, data: bytes) -> bytes:
    return (
        len(data).to_bytes(4, "big")
        + kind
        + data
        + (zlib.crc32(data, zlib.crc32(kind)) & 0xFFFFFFFF).to_bytes(4, "big")
    )


if __name__ == "__main__":
    unittest.main()
