from __future__ import annotations

import json
from typing import Any, Iterable

from .contract import ContractError

MAX_INPUT_LINE_BYTES = 6 * 1_024 * 1_024
MAX_CONFIG_BYTES = 64 * 1_024
MAX_JSON_DEPTH = 32


def canonical_json_bytes(value: Any) -> bytes:
    try:
        return json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
            sort_keys=True,
        ).encode("utf-8", "strict")
    except (TypeError, ValueError, UnicodeEncodeError, RecursionError) as error:
        raise ContractError(
            "internal_failure",
            "The worker could not encode a canonical response.",
            stage="transport",
            action_id="f5.restart-worker",
        ) from error


def _pairs(pairs: Iterable[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate property")
        result[key] = value
    return result


def _constant(_: str) -> None:
    raise ValueError("non-finite number")


def parse_canonical_json(data: bytes, *, maximum: int, name: str) -> dict[str, Any]:
    if not data or len(data) > maximum:
        raise ContractError(
            "invalid_request",
            f"{name} is empty or exceeds its byte bound.",
            stage="transport",
            action_id="f5.correct-transport",
        )
    try:
        text = data.decode("utf-8", "strict")
        value = json.loads(
            text,
            object_pairs_hook=_pairs,
            parse_constant=_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError, RecursionError) as error:
        raise ContractError(
            "invalid_request",
            f"{name} is not strict JSON.",
            stage="transport",
            action_id="f5.correct-transport",
        ) from error
    if type(value) is not dict:
        raise ContractError(
            "invalid_request",
            f"{name} must contain one JSON object.",
            stage="transport",
            action_id="f5.correct-transport",
        )
    pending = [(value, 1)]
    while pending:
        item, depth = pending.pop()
        if depth > MAX_JSON_DEPTH:
            raise ContractError(
                "invalid_request",
                f"{name} exceeds the JSON nesting bound.",
                stage="transport",
                action_id="f5.correct-transport",
            )
        children = item.values() if type(item) is dict else item if type(item) is list else ()
        pending.extend((child, depth + 1) for child in children)
    try:
        canonical = canonical_json_bytes(value)
    except ContractError as error:
        raise ContractError(
            "invalid_request",
            f"{name} contains invalid Unicode or excessive nesting.",
            stage="transport",
            action_id="f5.correct-transport",
        ) from error
    if canonical != data:
        raise ContractError(
            "invalid_request",
            f"{name} must use canonical JSON encoding.",
            stage="transport",
            action_id="f5.correct-transport",
        )
    return value
