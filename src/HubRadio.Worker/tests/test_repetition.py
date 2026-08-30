from __future__ import annotations

from dataclasses import FrozenInstanceError, replace

import pytest  # pyright: ignore[reportMissingImports] - declared test extra; worker venv verified.

from hubradio_worker.tasks import repetition


def _id(value: int) -> str:
    return f"00000000-0000-0000-0000-{value:012x}"


def _token(value: int, text: str) -> repetition.RepetitionToken:
    return repetition.RepetitionToken(_id(value), text)


def _sentence(
    value: int,
    *tokens: repetition.RepetitionToken,
) -> repetition.RepetitionSentence:
    return repetition.RepetitionSentence(_id(value), tuple(tokens))


def _utterance(
    value: int,
    speaker: int,
    *sentences: repetition.RepetitionSentence,
) -> repetition.RepetitionUtterance:
    return repetition.RepetitionUtterance(
        _id(value),
        _id(1000 + value),
        _id(speaker),
        tuple(sentences),
    )


def _transcript(
    *utterances: repetition.RepetitionUtterance,
    digest_character: str = "a",
) -> repetition.RepetitionTranscript:
    return repetition.RepetitionTranscript(
        _id(9000),
        digest_character * 64,
        tuple(utterances),
    )


def _find(
    transcript: repetition.RepetitionTranscript,
    model_run_id: str | None = None,
) -> tuple[repetition.RepetitionSuggestion, ...]:
    return repetition.find_repetition_suggestions(
        transcript,
        model_run_id or _id(8000),
    )


def test_adjacent_exact_utterance_targets_later_occurrence_for_review() -> None:
    first_tokens = (_token(1, "同じ"), _token(2, "発言"))
    second_tokens = (_token(3, "同じ"), _token(4, "発言"))
    first = _utterance(10, 500, _sentence(100, *first_tokens))
    second = _utterance(11, 500, _sentence(101, *second_tokens))
    transcript = _transcript(first, second)

    suggestions = _find(transcript)

    assert len(suggestions) == 1
    suggestion = suggestions[0]
    assert suggestion.rule_version == repetition.RULE_VERSION
    assert suggestion.kind == "deleteRange"
    assert suggestion.status == "proposed"
    assert suggestion.confidence is None
    assert suggestion.automatic_deletion_allowed is False
    assert suggestion.human_review_required is True
    assert suggestion.source_revision_id == transcript.revision_id
    assert suggestion.source_revision_digest == transcript.revision_digest
    assert suggestion.source_span_id == second.source_span_id
    assert suggestion.evidence_token_ids == tuple(item.token_id for item in second_tokens)
    assert suggestion.features.rule == repetition.ADJACENT_UTTERANCE_RULE
    assert suggestion.features.comparison_source_span_id == first.source_span_id
    assert suggestion.features.comparison_token_ids == tuple(
        item.token_id for item in first_tokens
    )
    assert suggestion.features.matching_token_count == 2
    assert suggestion.features.exact_nfkc_match is True
    assert suggestion.features.structurally_adjacent is True
    assert suggestion.features.same_speaker_required is True
    assert suggestion.features.candidate_is_later_occurrence is True
    assert suggestion.features.retention_choice_required is True
    assert "同じ" not in suggestion.reason


def test_utterance_rule_accepts_nfkc_equivalence_but_not_case_or_fuzzy_match() -> None:
    compatible = _transcript(
        _utterance(10, 500, _sentence(100, _token(1, "Ｃ＋＋"))),
        _utterance(11, 500, _sentence(101, _token(2, "C++"))),
    )
    case_difference = _transcript(
        _utterance(20, 500, _sentence(200, _token(3, "GitHub"))),
        _utterance(21, 500, _sentence(201, _token(4, "github"))),
    )
    fuzzy = _transcript(
        _utterance(30, 500, _sentence(300, _token(5, "Copilot"))),
        _utterance(31, 500, _sentence(301, _token(6, "Copliot"))),
    )

    assert len(_find(compatible)) == 1
    assert _find(case_difference) == ()
    assert _find(fuzzy) == ()


def test_utterance_rule_requires_same_speaker_and_immediate_adjacency() -> None:
    different_speakers = _transcript(
        _utterance(10, 500, _sentence(100, _token(1, "同じ"))),
        _utterance(11, 501, _sentence(101, _token(2, "同じ"))),
    )
    nonadjacent = _transcript(
        _utterance(20, 500, _sentence(200, _token(3, "同じ"))),
        _utterance(21, 500, _sentence(201, _token(4, "別"))),
        _utterance(22, 500, _sentence(202, _token(5, "同じ"))),
    )

    assert _find(different_speakers) == ()
    assert _find(nonadjacent) == ()


def test_adjacent_token_blocks_select_the_later_exact_sequence() -> None:
    tokens = (
        _token(1, "この"),
        _token(2, "機能"),
        _token(3, "この"),
        _token(4, "機能"),
        _token(5, "です"),
    )
    utterance = _utterance(10, 500, _sentence(100, *tokens))
    transcript = _transcript(utterance)

    suggestions = _find(transcript)

    assert len(suggestions) == 1
    suggestion = suggestions[0]
    assert suggestion.source_span_id == utterance.source_span_id
    assert suggestion.evidence_token_ids == (tokens[2].token_id, tokens[3].token_id)
    assert suggestion.features.rule == repetition.ADJACENT_TOKEN_SEQUENCE_RULE
    assert suggestion.features.comparison_source_span_id == utterance.source_span_id
    assert suggestion.features.comparison_token_ids == (
        tokens[0].token_id,
        tokens[1].token_id,
    )
    assert suggestion.features.matching_token_count == 2
    assert suggestion.features.same_speaker_required is False
    assert "この" not in suggestion.reason


def test_token_rule_prefers_longest_nonoverlapping_block_and_stable_order() -> None:
    first_sentence = _sentence(
        100,
        _token(1, "A"),
        _token(2, "A"),
        _token(3, "B"),
    )
    second_sentence = _sentence(
        101,
        _token(4, "X"),
        _token(5, "Y"),
        _token(6, "X"),
        _token(7, "Y"),
    )
    transcript = _transcript(_utterance(10, 500, first_sentence, second_sentence))

    suggestions = _find(transcript)

    assert len(suggestions) == 2
    assert suggestions[0].evidence_token_ids == (_id(2),)
    assert suggestions[1].evidence_token_ids == (_id(6), _id(7))
    assert suggestions[1].features.comparison_token_ids == (_id(4), _id(5))


def test_punctuation_only_repetition_is_not_a_content_candidate() -> None:
    transcript = _transcript(
        _utterance(
            10,
            500,
            _sentence(100, _token(1, "。"), _token(2, "。")),
        )
    )

    assert _find(transcript) == ()


def test_whole_utterance_candidate_suppresses_nested_candidate_in_later_copy() -> None:
    first = _utterance(
        10,
        500,
        _sentence(100, _token(1, "はい"), _token(2, "はい")),
    )
    second = _utterance(
        11,
        500,
        _sentence(101, _token(3, "はい"), _token(4, "はい")),
    )

    suggestions = _find(_transcript(first, second))

    assert len(suggestions) == 2
    assert suggestions[0].features.rule == repetition.ADJACENT_TOKEN_SEQUENCE_RULE
    assert suggestions[0].source_span_id == first.source_span_id
    assert suggestions[1].features.rule == repetition.ADJACENT_UTTERANCE_RULE
    assert suggestions[1].source_span_id == second.source_span_id
    assert not any(
        suggestion.features.rule == repetition.ADJACENT_TOKEN_SEQUENCE_RULE
        and suggestion.source_span_id == second.source_span_id
        for suggestion in suggestions
    )


def test_candidate_key_is_deterministic_and_independent_of_run_identity() -> None:
    transcript = _transcript(
        _utterance(
            10,
            500,
            _sentence(100, _token(1, "反復"), _token(2, "反復")),
        )
    )

    first = _find(transcript, _id(8000))[0]
    repeated = _find(transcript, _id(8000))[0]
    other_run = _find(transcript, _id(8001))[0]
    changed_digest = _find(
        repetition.RepetitionTranscript(
            transcript.revision_id,
            "b" * 64,
            transcript.utterances,
        ),
        _id(8000),
    )[0]

    assert first == repeated
    assert first.candidate_key == other_run.candidate_key
    assert first.model_run_id != other_run.model_run_id
    assert first.candidate_key != changed_digest.candidate_key
    assert len(first.candidate_key) == 64


def test_hash_filter_never_replaces_exact_sequence_confirmation(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    transcript = _transcript(
        _utterance(
            10,
            500,
            _sentence(
                100,
                _token(1, "A"),
                _token(2, "B"),
                _token(3, "C"),
                _token(4, "D"),
            ),
        )
    )

    monkeypatch.setattr(repetition._SequenceHash, "segment", lambda *_: 0)

    assert _find(transcript) == ()


def test_inputs_and_outputs_are_frozen_and_defensively_require_tuples() -> None:
    token = _token(1, "反復")
    sentence = _sentence(100, token, _token(2, "反復"))
    utterance = _utterance(10, 500, sentence)
    transcript = _transcript(utterance)
    suggestion = _find(transcript)[0]

    with pytest.raises(FrozenInstanceError):
        token.text = "変更"  # type: ignore[misc]
    with pytest.raises(FrozenInstanceError):
        suggestion.status = "adopted"  # type: ignore[misc]
    with pytest.raises(repetition.RepetitionTaskError) as captured:
        repetition.RepetitionSentence(_id(101), [token])  # type: ignore[arg-type]
    assert captured.value.code == repetition.INVALID_INPUT
    assert isinstance(transcript.utterances, tuple)
    assert isinstance(suggestion.evidence_token_ids, tuple)
    assert not hasattr(suggestion, "delete")
    assert not hasattr(suggestion, "adopt")


@pytest.mark.parametrize(
    "factory",
    [
        lambda: repetition.RepetitionToken(
            "00000000-0000-0000-0000-000000000000", "text"
        ),
        lambda: repetition.RepetitionToken(
            "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA", "text"
        ),
        lambda: repetition.RepetitionToken(
            "00000000-0000-0000-0000-000000000001", " private "
        ),
        lambda: repetition.RepetitionToken(
            "00000000-0000-0000-0000-000000000001", "private\ud800"
        ),
        lambda: repetition.RepetitionSentence(
            _id(100), (_token(1, "a"), _token(1, "b"))
        ),
        lambda: repetition.RepetitionSentence(_id(100), ()),
        lambda: repetition.RepetitionSentence(
            _id(100), (object(),)  # type: ignore[arg-type]
        ),
        lambda: repetition.RepetitionUtterance(
            _id(10), _id(1010), _id(500), ()
        ),
        lambda: repetition.RepetitionTranscript(
            _id(9000), "a" * 64, (object(),)  # type: ignore[arg-type]
        ),
        lambda: repetition.RepetitionTranscript(_id(9000), "A" * 64, ()),
    ],
)
def test_invalid_identity_text_duplicate_and_digest_fail_privately(factory: object) -> None:
    with pytest.raises(repetition.RepetitionTaskError) as captured:
        factory()  # type: ignore[operator]

    assert captured.value.code == repetition.INVALID_INPUT
    assert str(captured.value) == (
        "The repetition candidate task could not process its input."
    )
    assert "private" not in str(captured.value)


def test_output_contract_rejects_inconsistent_features_and_candidate_key() -> None:
    suggestion = _find(
        _transcript(
            _utterance(
                10,
                500,
                _sentence(100, _token(1, "反復"), _token(2, "反復")),
            )
        )
    )[0]

    invalid_values = (
        lambda: replace(suggestion, candidate_key="0" * 64),
        lambda: replace(suggestion.features, matching_token_count=True),
        lambda: replace(
            suggestion.features,
            comparison_token_ids=("not-a-canonical-id",),
        ),
    )
    for create_invalid_value in invalid_values:
        with pytest.raises(repetition.RepetitionTaskError) as captured:
            create_invalid_value()
        assert captured.value.code == repetition.INVALID_INPUT


def test_size_bounds_fail_with_distinct_private_error(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(repetition, "MAX_TEXT_UTF8_BYTES", 3)

    with pytest.raises(repetition.RepetitionTaskError) as captured:
        repetition.RepetitionToken(_id(1), "長文")

    assert captured.value.code == repetition.INPUT_TOO_LARGE
    assert "長文" not in str(captured.value)

    monkeypatch.setattr(repetition, "MAX_TEXT_UTF8_BYTES", 65_536)
    monkeypatch.setattr(repetition, "MAX_TOKEN_BLOCK_COMPARISONS", 1)
    transcript = _transcript(
        _utterance(
            10,
            500,
            _sentence(
                100,
                _token(2, "一"),
                _token(3, "二"),
                _token(4, "三"),
            ),
        )
    )
    with pytest.raises(repetition.RepetitionTaskError) as work_limit:
        _find(transcript)
    assert work_limit.value.code == repetition.INPUT_TOO_LARGE


def test_empty_transcript_has_no_candidates_and_unknown_error_code_is_rejected() -> None:
    assert _find(_transcript()) == ()
    with pytest.raises(ValueError, match="Unknown repetition task error code"):
        repetition.RepetitionTaskError("repetition.future")