"""Deterministic adjacent-repetition candidates for human review.

The baseline intentionally uses structural adjacency and exact NFKC equality only.
It does not score semantic similarity, infer which occurrence is correct, or delete text.
Transport integration is owned by a later host workflow; this module is a pure task.
"""

from __future__ import annotations

import hashlib
import unicodedata
import uuid
from collections.abc import Iterable
from dataclasses import dataclass
from typing import NoReturn, TypeVar

RULE_VERSION = "fr005-2026-08-31.repetition.v1"
SUGGESTION_KIND = "deleteRange"
SUGGESTION_STATUS = "proposed"

ADJACENT_UTTERANCE_RULE = "adjacent_exact_utterance"
ADJACENT_TOKEN_SEQUENCE_RULE = "adjacent_exact_token_sequence"

INVALID_INPUT = "repetition.invalid_input"
INPUT_TOO_LARGE = "repetition.input_too_large"

MAX_UTTERANCES = 100_000
MAX_SENTENCES_PER_UTTERANCE = 10_000
MAX_TOKENS_PER_SENTENCE = 4_096
MAX_TOTAL_TOKENS = 1_000_000
MAX_TEXT_UTF8_BYTES = 65_536
MAX_TOKEN_BLOCK_COMPARISONS = 10_000_000

_ROLLING_HASH_BASE = 1_000_003
_ROLLING_HASH_MODULUS = (1 << 127) - 1

_ERROR_MESSAGE = "The repetition candidate task could not process its input."
_ERROR_CODES = frozenset({INVALID_INPUT, INPUT_TOO_LARGE})
_HEX_DIGITS = frozenset("0123456789abcdef")

_T = TypeVar("_T")


class RepetitionTaskError(RuntimeError):
    """A content-private worker task failure with a stable error code."""

    def __init__(self, code: str) -> None:
        if code not in _ERROR_CODES:
            raise ValueError("Unknown repetition task error code.")
        super().__init__(_ERROR_MESSAGE)
        self._code = code

    @property
    def code(self) -> str:
        return self._code


@dataclass(frozen=True, slots=True)
class RepetitionToken:
    token_id: str
    text: str

    def __post_init__(self) -> None:
        _require_id(self.token_id)
        _require_text(self.text)


@dataclass(frozen=True, slots=True)
class RepetitionSentence:
    sentence_id: str
    tokens: tuple[RepetitionToken, ...]

    def __post_init__(self) -> None:
        _require_id(self.sentence_id)
        copied = _require_tuple(self.tokens)
        if not copied or len(copied) > MAX_TOKENS_PER_SENTENCE:
            _fail(INPUT_TOO_LARGE if copied else INVALID_INPUT)
        _require_exact_instances(copied, RepetitionToken)
        _require_unique_ids(item.token_id for item in copied)
        object.__setattr__(self, "tokens", copied)


@dataclass(frozen=True, slots=True)
class RepetitionUtterance:
    utterance_id: str
    source_span_id: str
    speaker_id: str
    sentences: tuple[RepetitionSentence, ...]

    def __post_init__(self) -> None:
        _require_id(self.utterance_id)
        _require_id(self.source_span_id)
        _require_id(self.speaker_id)
        copied = _require_tuple(self.sentences)
        if not copied or len(copied) > MAX_SENTENCES_PER_UTTERANCE:
            _fail(INPUT_TOO_LARGE if copied else INVALID_INPUT)
        _require_exact_instances(copied, RepetitionSentence)
        _require_unique_ids(item.sentence_id for item in copied)
        _require_unique_ids(
            token.token_id for sentence in copied for token in sentence.tokens
        )
        object.__setattr__(self, "sentences", copied)

    @property
    def tokens(self) -> tuple[RepetitionToken, ...]:
        return tuple(token for sentence in self.sentences for token in sentence.tokens)


@dataclass(frozen=True, slots=True)
class RepetitionTranscript:
    revision_id: str
    revision_digest: str
    utterances: tuple[RepetitionUtterance, ...]

    def __post_init__(self) -> None:
        _require_id(self.revision_id)
        _require_digest(self.revision_digest)
        copied = _require_tuple(self.utterances)
        if len(copied) > MAX_UTTERANCES:
            _fail(INPUT_TOO_LARGE)
        _require_exact_instances(copied, RepetitionUtterance)
        _require_unique_ids(item.utterance_id for item in copied)
        _require_unique_ids(item.source_span_id for item in copied)
        _require_unique_ids(
            token.token_id for utterance in copied for token in utterance.tokens
        )
        total_tokens = sum(len(utterance.tokens) for utterance in copied)
        if total_tokens > MAX_TOTAL_TOKENS:
            _fail(INPUT_TOO_LARGE)
        object.__setattr__(self, "utterances", copied)


@dataclass(frozen=True, slots=True)
class RepetitionReasonFeatures:
    rule: str
    comparison_source_span_id: str
    comparison_token_ids: tuple[str, ...]
    matching_token_count: int
    exact_nfkc_match: bool
    structurally_adjacent: bool
    same_speaker_required: bool
    candidate_is_later_occurrence: bool
    retention_choice_required: bool

    def __post_init__(self) -> None:
        if type(self.rule) is not str or self.rule not in {
            ADJACENT_UTTERANCE_RULE,
            ADJACENT_TOKEN_SEQUENCE_RULE,
        }:
            _fail(INVALID_INPUT)
        _require_id(self.comparison_source_span_id)
        copied = _require_tuple(self.comparison_token_ids)
        if not copied:
            _fail(INVALID_INPUT)
        _require_unique_ids(copied)
        if (
            type(self.matching_token_count) is not int
            or self.matching_token_count != len(copied)
        ):
            _fail(INVALID_INPUT)
        if (
            self.exact_nfkc_match is not True
            or self.structurally_adjacent is not True
            or self.candidate_is_later_occurrence is not True
            or self.retention_choice_required is not True
        ):
            _fail(INVALID_INPUT)
        expected_same_speaker = self.rule == ADJACENT_UTTERANCE_RULE
        if self.same_speaker_required is not expected_same_speaker:
            _fail(INVALID_INPUT)
        object.__setattr__(self, "comparison_token_ids", copied)


@dataclass(frozen=True, slots=True)
class RepetitionSuggestion:
    candidate_key: str
    model_run_id: str
    source_revision_id: str
    source_revision_digest: str
    source_span_id: str
    evidence_token_ids: tuple[str, ...]
    rule_version: str
    kind: str
    status: str
    reason: str
    confidence: None
    automatic_deletion_allowed: bool
    human_review_required: bool
    features: RepetitionReasonFeatures

    def __post_init__(self) -> None:
        _require_digest(self.candidate_key)
        _require_id(self.model_run_id)
        _require_id(self.source_revision_id)
        _require_digest(self.source_revision_digest)
        _require_id(self.source_span_id)
        copied = _require_tuple(self.evidence_token_ids)
        if not copied:
            _fail(INVALID_INPUT)
        _require_unique_ids(copied)
        if type(self.features) is not RepetitionReasonFeatures:
            _fail(INVALID_INPUT)
        if (
            type(self.rule_version) is not str
            or self.rule_version != RULE_VERSION
            or type(self.kind) is not str
            or self.kind != SUGGESTION_KIND
            or type(self.status) is not str
            or self.status != SUGGESTION_STATUS
            or self.confidence is not None
            or self.automatic_deletion_allowed is not False
            or self.human_review_required is not True
            or self.features.matching_token_count != len(copied)
            or not set(copied).isdisjoint(self.features.comparison_token_ids)
            or (
                self.features.rule == ADJACENT_TOKEN_SEQUENCE_RULE
                and self.features.comparison_source_span_id != self.source_span_id
            )
            or (
                self.features.rule == ADJACENT_UTTERANCE_RULE
                and self.features.comparison_source_span_id == self.source_span_id
            )
        ):
            _fail(INVALID_INPUT)
        _require_text(self.reason)
        expected_key = _candidate_key_from_values(
            self.source_revision_id,
            self.source_revision_digest,
            self.features.rule,
            self.source_span_id,
            copied,
            self.features.comparison_source_span_id,
            self.features.comparison_token_ids,
        )
        if self.candidate_key != expected_key:
            _fail(INVALID_INPUT)
        object.__setattr__(self, "evidence_token_ids", copied)


def find_repetition_suggestions(
    transcript: RepetitionTranscript,
    model_run_id: str,
) -> tuple[RepetitionSuggestion, ...]:
    """Return exact adjacent repetition candidates without applying an edit."""

    if type(transcript) is not RepetitionTranscript:
        _fail(INVALID_INPUT)
    _require_id(model_run_id)

    suggestions: list[tuple[int, int, int, RepetitionSuggestion]] = []
    whole_utterance_targets: set[int] = set()
    token_block_comparisons = 0
    normalized_utterances = [
        tuple(_normalize(token.text) for token in utterance.tokens)
        for utterance in transcript.utterances
    ]

    for later_index in range(1, len(transcript.utterances)):
        earlier = transcript.utterances[later_index - 1]
        later = transcript.utterances[later_index]
        earlier_values = normalized_utterances[later_index - 1]
        later_values = normalized_utterances[later_index]
        if (
            earlier.speaker_id != later.speaker_id
            or earlier_values != later_values
            or not _is_substantive(earlier_values)
        ):
            continue

        earlier_token_ids = tuple(token.token_id for token in earlier.tokens)
        later_token_ids = tuple(token.token_id for token in later.tokens)
        suggestion = _create_suggestion(
            transcript,
            model_run_id,
            ADJACENT_UTTERANCE_RULE,
            later.source_span_id,
            later_token_ids,
            earlier.source_span_id,
            earlier_token_ids,
            same_speaker_required=True,
        )
        suggestions.append((later_index, 0, 0, suggestion))
        whole_utterance_targets.add(later_index)

    for utterance_index, utterance in enumerate(transcript.utterances):
        if utterance_index in whole_utterance_targets:
            continue
        sentence_token_offset = 0
        for sentence_index, sentence in enumerate(utterance.sentences):
            normalized_tokens = tuple(_normalize(token.text) for token in sentence.tokens)
            token_block_comparisons += len(normalized_tokens) ** 2 // 4
            if token_block_comparisons > MAX_TOKEN_BLOCK_COMPARISONS:
                _fail(INPUT_TOO_LARGE)
            sequence_hash = _SequenceHash.create(normalized_tokens)
            token_index = 0
            while token_index < len(sentence.tokens):
                block_length = _longest_adjacent_repeat(
                    normalized_tokens,
                    sequence_hash,
                    token_index,
                )
                if block_length == 0:
                    token_index += 1
                    continue

                comparison = sentence.tokens[
                    token_index : token_index + block_length
                ]
                candidate = sentence.tokens[
                    token_index + block_length : token_index + (2 * block_length)
                ]
                suggestion = _create_suggestion(
                    transcript,
                    model_run_id,
                    ADJACENT_TOKEN_SEQUENCE_RULE,
                    utterance.source_span_id,
                    tuple(token.token_id for token in candidate),
                    utterance.source_span_id,
                    tuple(token.token_id for token in comparison),
                    same_speaker_required=False,
                )
                suggestions.append(
                    (
                        utterance_index,
                        1,
                        sentence_token_offset + token_index + block_length,
                        suggestion,
                    )
                )
                token_index += 2 * block_length
            sentence_token_offset += len(sentence.tokens)

    suggestions.sort(key=lambda value: (value[0], value[1], value[2]))
    return tuple(value[3] for value in suggestions)


def _longest_adjacent_repeat(
    values: tuple[str, ...],
    sequence_hash: _SequenceHash,
    start: int,
) -> int:
    maximum = (len(values) - start) // 2
    for block_length in range(maximum, 0, -1):
        if sequence_hash.segment(start, start + block_length) != sequence_hash.segment(
            start + block_length,
            start + (2 * block_length),
        ):
            continue
        first = values[start : start + block_length]
        second = values[
            start + block_length : start + (2 * block_length)
        ]
        if first == second and _is_substantive(first):
            return block_length
    return 0


def _create_suggestion(
    transcript: RepetitionTranscript,
    model_run_id: str,
    rule: str,
    source_span_id: str,
    evidence_token_ids: tuple[str, ...],
    comparison_source_span_id: str,
    comparison_token_ids: tuple[str, ...],
    *,
    same_speaker_required: bool,
) -> RepetitionSuggestion:
    features = RepetitionReasonFeatures(
        rule=rule,
        comparison_source_span_id=comparison_source_span_id,
        comparison_token_ids=comparison_token_ids,
        matching_token_count=len(evidence_token_ids),
        exact_nfkc_match=True,
        structurally_adjacent=True,
        same_speaker_required=same_speaker_required,
        candidate_is_later_occurrence=True,
        retention_choice_required=True,
    )
    reason = (
        "Adjacent utterances have the same exact normalized token sequence."
        if rule == ADJACENT_UTTERANCE_RULE
        else "Adjacent token blocks have the same exact normalized sequence."
    )
    candidate_key = _candidate_key(
        transcript,
        rule,
        source_span_id,
        evidence_token_ids,
        comparison_source_span_id,
        comparison_token_ids,
    )
    return RepetitionSuggestion(
        candidate_key=candidate_key,
        model_run_id=model_run_id,
        source_revision_id=transcript.revision_id,
        source_revision_digest=transcript.revision_digest,
        source_span_id=source_span_id,
        evidence_token_ids=evidence_token_ids,
        rule_version=RULE_VERSION,
        kind=SUGGESTION_KIND,
        status=SUGGESTION_STATUS,
        reason=reason,
        confidence=None,
        automatic_deletion_allowed=False,
        human_review_required=True,
        features=features,
    )


def _candidate_key(
    transcript: RepetitionTranscript,
    rule: str,
    source_span_id: str,
    evidence_token_ids: tuple[str, ...],
    comparison_source_span_id: str,
    comparison_token_ids: tuple[str, ...],
) -> str:
    return _candidate_key_from_values(
        transcript.revision_id,
        transcript.revision_digest,
        rule,
        source_span_id,
        evidence_token_ids,
        comparison_source_span_id,
        comparison_token_ids,
    )


def _candidate_key_from_values(
    revision_id: str,
    revision_digest: str,
    rule: str,
    source_span_id: str,
    evidence_token_ids: tuple[str, ...],
    comparison_source_span_id: str,
    comparison_token_ids: tuple[str, ...],
) -> str:
    fields = (
        RULE_VERSION,
        revision_id,
        revision_digest,
        rule,
        source_span_id,
        *evidence_token_ids,
        comparison_source_span_id,
        *comparison_token_ids,
    )
    payload = "\0".join(fields).encode("utf-8", errors="strict") + b"\n"
    return hashlib.sha256(payload).hexdigest()


def _normalize(value: str) -> str:
    _require_text(value)
    try:
        return unicodedata.normalize("NFKC", value)
    except (TypeError, ValueError):
        _fail(INVALID_INPUT)


def _is_substantive(values: tuple[str, ...]) -> bool:
    return any(
        unicodedata.category(character)[0] in {"L", "N", "S"}
        for value in values
        for character in value
    )


def _require_tuple(value: tuple[_T, ...]) -> tuple[_T, ...]:
    if type(value) is not tuple:
        _fail(INVALID_INPUT)
    return value


def _require_exact_instances(values: tuple[object, ...], expected_type: type) -> None:
    if any(type(value) is not expected_type for value in values):
        _fail(INVALID_INPUT)


def _require_unique_ids(values: Iterable[str]) -> None:
    try:
        copied: tuple[str, ...] = tuple(values)
    except (TypeError, ValueError):
        _fail(INVALID_INPUT)
    for value in copied:
        _require_id(value)
    if len(copied) != len(set(copied)):
        _fail(INVALID_INPUT)


def _require_id(value: object) -> None:
    if not isinstance(value, str) or type(value) is not str:
        _fail(INVALID_INPUT)
    try:
        parsed = uuid.UUID(value)
    except (ValueError, AttributeError):
        _fail(INVALID_INPUT)
    if parsed.int == 0 or str(parsed) != value:
        _fail(INVALID_INPUT)


def _require_digest(value: object) -> None:
    if (
        not isinstance(value, str)
        or type(value) is not str
        or len(value) != 64
        or any(character not in _HEX_DIGITS for character in value)
    ):
        _fail(INVALID_INPUT)


def _require_text(value: object) -> None:
    if (
        not isinstance(value, str)
        or type(value) is not str
        or not value
        or value != value.strip()
    ):
        _fail(INVALID_INPUT)
    try:
        encoded = value.encode("utf-8", errors="strict")
    except UnicodeEncodeError:
        _fail(INVALID_INPUT)
    if len(encoded) > MAX_TEXT_UTF8_BYTES:
        _fail(INPUT_TOO_LARGE)
    if any(
        unicodedata.category(character) in {"Cc", "Cs", "Zl", "Zp"}
        for character in value
    ):
        _fail(INVALID_INPUT)


def _fail(code: str) -> NoReturn:
    raise RepetitionTaskError(code)


@dataclass(frozen=True, slots=True)
class _SequenceHash:
    prefixes: tuple[int, ...]
    powers: tuple[int, ...]

    @classmethod
    def create(cls, values: tuple[str, ...]) -> _SequenceHash:
        symbols = {
            value: index + 1
            for index, value in enumerate(sorted(set(values)))
        }
        prefixes = [0]
        powers = [1]
        for value in values:
            prefixes.append(
                (
                    (prefixes[-1] * _ROLLING_HASH_BASE)
                    + symbols[value]
                )
                % _ROLLING_HASH_MODULUS
            )
            powers.append(
                (powers[-1] * _ROLLING_HASH_BASE) % _ROLLING_HASH_MODULUS
            )
        return cls(tuple(prefixes), tuple(powers))

    def segment(self, start: int, end: int) -> int:
        return (
            self.prefixes[end]
            - (self.prefixes[start] * self.powers[end - start])
        ) % _ROLLING_HASH_MODULUS