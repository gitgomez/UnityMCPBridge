"""Compatibility adapter tests for the Runtime v1 response envelope."""

from models.unity_response import normalize_unity_response


def test_runtime_success_envelope_preserves_legacy_mcp_shape_and_metadata():
    response = {
        "runtime_version": 1,
        "request_id": "request-1",
        "status": "succeeded",
        "code": "OK",
        "message": "Created.",
        "data": {"message": "Created.", "instance_id": 42},
        "changes": {"objects": [{"instance_id": 42}]},
        "diagnostics": [],
        "timing": {"queued_ms": 1, "execution_ms": 2, "total_ms": 3},
        "state": {},
        "receipt": {"result_digest": "sha256:abc"},
    }

    normalized = normalize_unity_response(response)

    assert normalized["success"] is True
    assert normalized["message"] == "Created."
    assert normalized["error"] is None
    assert normalized["data"] == {"instance_id": 42}
    assert normalized["code"] == "OK"
    assert normalized["runtime"]["request_id"] == "request-1"
    assert normalized["runtime"]["changes"] == response["changes"]


def test_runtime_success_flattens_wrapped_resource_response():
    response = {
        "runtime_version": 1,
        "request_id": "request-resource",
        "status": "succeeded",
        "code": "OK",
        "message": "Retrieved project info.",
        "data": {
            "success": True,
            "message": "Retrieved project info.",
            "data": {
                "projectRoot": "D:/ConsumerProject/ConsumerUnity",
                "projectName": "ConsumerUnity",
            },
        },
    }

    normalized = normalize_unity_response(response)

    assert normalized["success"] is True
    assert normalized["message"] == "Retrieved project info."
    assert normalized["data"] == {
        "projectRoot": "D:/ConsumerProject/ConsumerUnity",
        "projectName": "ConsumerUnity",
    }


def test_runtime_wrapped_legacy_failure_remains_failure():
    response = {
        "runtime_version": 1,
        "request_id": "request-failed",
        "status": "succeeded",
        "code": "OK",
        "message": "Command completed.",
        "data": {
            "success": False,
            "error": "Resource is disabled.",
            "data": None,
        },
    }

    normalized = normalize_unity_response(response)

    assert normalized["success"] is False
    assert normalized["error"] == "Resource is disabled."
    assert normalized["data"] is None


def test_runtime_failure_and_in_progress_statuses_are_not_reported_as_success():
    failed = normalize_unity_response(
        {
            "runtime_version": 1,
            "request_id": "request-2",
            "status": "failed",
            "code": "STATE_CONFLICT",
            "message": "State changed.",
            "data": {},
        }
    )
    queued = normalize_unity_response(
        {
            "runtime_version": 1,
            "request_id": "request-3",
            "status": "queued",
            "code": "REQUEST_IN_PROGRESS",
            "message": "Request is queued.",
            "data": {"retry_after_ms": 100},
        }
    )

    assert failed["success"] is False
    assert failed["error"] == "State changed."
    assert failed["code"] == "STATE_CONFLICT"
    assert queued["success"] is False
    assert queued["runtime"]["status"] == "queued"
    assert queued["data"] == {"retry_after_ms": 100}


def test_legacy_response_normalization_remains_unchanged():
    response = {
        "status": "success",
        "result": {"message": "Saved.", "path": "Assets/Test.unity"},
    }

    assert normalize_unity_response(response) == {
        "success": True,
        "message": "Saved.",
        "error": None,
        "data": {"path": "Assets/Test.unity"},
    }
