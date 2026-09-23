from __future__ import annotations

import json
from typing import Any, Iterable

MAX_INPUT_LINE_BYTES = 5_700_000
MAX_CONFIG_BYTES = 128 * 1_024


class JsonContractError(ValueError):
    pass


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
        raise JsonContractError("canonical JSON encoding failed") from error


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
        raise JsonContractError(f"{name} is empty or exceeds its byte bound")
    try:
        text = data.decode("utf-8", "strict")
        value = json.loads(
            text,
            object_pairs_hook=_pairs,
            parse_constant=_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError, RecursionError) as error:
        raise JsonContractError(f"{name} is not strict JSON") from error
    if type(value) is not dict:
        raise JsonContractError(f"{name} must contain one JSON object")
    containers = [iter(value.values())]
    while containers:
        try:
            child = next(containers[-1])
        except StopIteration:
            containers.pop()
            continue
        if type(child) in (dict, list):
            if len(containers) >= 64:
                raise JsonContractError(f"{name} exceeds its nesting bound")
            containers.append(iter(child.values() if type(child) is dict else child))
    if canonical_json_bytes(value) != data:
        raise JsonContractError(f"{name} must use canonical JSON encoding")
    return value
