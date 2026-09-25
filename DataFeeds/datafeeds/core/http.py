"""Minimal REST helpers (stdlib). JSON-in/JSON-out with timeouts and retries."""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.parse
import urllib.request
from typing import Optional

DEFAULT_TIMEOUT = 30
MAX_RETRIES = 4


class HttpError(RuntimeError):
    def __init__(self, status, url, body):
        self.status = status
        self.url = url
        self.body = body
        super().__init__(f"HTTP {status} for {url}: {body[:300]}")


def _request(
    url: str,
    payload: Optional[dict] = None,
    headers: Optional[dict] = None,
    timeout: int = DEFAULT_TIMEOUT,
    retries: int = MAX_RETRIES,
) -> str:
    data = None
    request_headers = {"User-Agent": "quantlab-datafeeds/0.1", "Accept": "application/json"}
    if headers:
        request_headers.update(headers)
    if payload is not None:
        data = json.dumps(payload).encode("utf-8")
        request_headers.setdefault("Content-Type", "application/json")

    last_error: Optional[Exception] = None
    for attempt in range(1, retries + 1):
        try:
            request = urllib.request.Request(url, data=data, headers=request_headers)
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return response.read().decode("utf-8")
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", errors="replace")
            if exc.code in (429, 500, 502, 503, 504) and attempt < retries:
                time.sleep(min(2**attempt, 10))
                last_error = HttpError(exc.code, url, body)
                continue
            raise HttpError(exc.code, url, body) from exc
        except (urllib.error.URLError, TimeoutError, ConnectionError) as exc:
            last_error = exc
            if attempt < retries:
                time.sleep(min(2**attempt, 10))
    raise RuntimeError(f"request failed for {url}: {last_error}") from last_error


def http_get_json(url: str, params: Optional[dict] = None, headers: Optional[dict] = None, **kw):
    if params:
        url = url + ("&" if "?" in url else "?") + urllib.parse.urlencode(params)
    body = _request(url, headers=headers, **kw)
    return json.loads(body)


def http_post_json(url: str, payload: dict, headers: Optional[dict] = None, **kw):
    body = _request(url, payload, headers=headers, **kw)
    return json.loads(body)