"""Thin WebSocket helper built on ``websocket-client``.

Handles connect / subscribe / frame receive loop / keep-alive pings and
graceful shutdown. Providers build their protocols on top of this.

Usage pattern (see the LiveFeed providers)::

    conn = WsConnection(url, on_message=handler, ping_payload=..., ping_interval_s=20)
    conn.send_json({"op": "subscribe", "args": [...]})
    conn.run()          # loops until handler returns False (deadline) or socket closes
"""

from __future__ import annotations

import json
import time
from typing import Callable, Optional

import websocket  # pip install websocket-client


class WsConnection:
    """A single WebSocket session with an exchange endpoint.

    ``on_message(str|None -> bool|None)`` receives every text frame. Return
    ``False`` to stop the receive loop gracefully. ``send_json`` records sent
    payloads for auditing.
    """

    def __init__(
        self,
        url: str,
        on_message: Callable[[str], Optional[bool]],
        *,
        ping_payload: Optional[str] = None,
        ping_interval_s: float = 20.0,
        headers: Optional[dict] = None,
        timeout: float = 30.0,
    ):
        self.url = url
        self.on_message = on_message
        self.ping_payload = ping_payload
        self.ping_interval_s = ping_interval_s
        self.sent: list[str] = []
        self._ws = websocket.create_connection(
            url,
            timeout=timeout,
            enable_multithread=True,
            header=headers,
        )
        self._closed = False

    def send(self, text: str) -> None:
        self._ws.send(text)
        self.sent.append(text)

    def send_json(self, payload) -> None:
        self.send(json.dumps(payload))

    def recv_once(self) -> Optional[str]:
        """Reads a single text frame without starting the receive loop.

        Used by handshake-style exchanges (e.g. Deriv's ``authorize``) that must
        consume one response before the streaming loop takes over. Returns the
        decoded frame, or ``None`` when the socket closes or times out.
        """
        try:
            frame = self._ws.recv()
        except (websocket.WebSocketTimeoutException, websocket.WebSocketConnectionClosedException):
            return None
        except Exception:
            return None
        if frame is None:
            return None
        return frame if isinstance(frame, str) else frame.decode("utf-8")

    def run(self) -> None:
        last_ping = time.monotonic()
        while not self._closed:
            try:
                frame = self._ws.recv()
            except (websocket.WebSocketTimeoutException, websocket.WebSocketConnectionClosedException):
                break
            except Exception:
                break
            if frame is None:
                break
            text = frame if isinstance(frame, str) else frame.decode("utf-8")
            if self.ping_payload is not None and time.monotonic() - last_ping >= self.ping_interval_s:
                try:
                    self._ws.send(self.ping_payload)
                except Exception:
                    break
                last_ping = time.monotonic()
            if self.on_message is not None:
                try:
                    if self.on_message(text) is False:
                        break
                except StopIteration:
                    break
                except Exception:
                    break

    def close(self) -> None:
        self._closed = True
        try:
            self._ws.close()
        except Exception:
            pass