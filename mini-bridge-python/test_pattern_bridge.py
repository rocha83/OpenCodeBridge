#!/usr/bin/env python3
"""Testes do filtro `pattern` (sem rede): regra identica ao StripUnanchoredPatterns."""
from __future__ import annotations

import json
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from pattern_bridge import _is_kept, patch_body, strip_unanchored_patterns  # noqa: E402


def chat_payload(params: dict, legacy: bool = False) -> dict:
    if legacy:
        return {"functions": [{"name": "glob", "parameters": params}]}
    return {
        "model": "gemma-4-12b",
        "messages": [{"role": "user", "content": "oi"}],
        "tools": [{"type": "function", "function": {"name": "glob", "parameters": params}}],
    }


class AnchoredRuleTest(unittest.TestCase):
    def test_keeps_literal_anchored(self) -> None:
        self.assertTrue(_is_kept("^ses$"))
        self.assertTrue(_is_kept("^[a-z]+$"))

    def test_strips_group_anchored(self) -> None:
        # caso real do opencode: sessionID @pattern ^ses -> schema "^(ses)"
        self.assertFalse(_is_kept("^(ses)"))
        self.assertFalse(_is_kept("*.cs"))
        self.assertFalse(_is_kept("^"))


class PatchBodyTest(unittest.TestCase):
    def test_removes_group_anchored_pattern_nested(self) -> None:
        payload = chat_payload(
            {
                "type": "object",
                "properties": {
                    "pattern": {"type": "string", "description": "glob"},
                    "sessionID": {"type": "string", "pattern": "^(ses)"},
                },
            }
        )
        raw = json.dumps(payload).encode()
        out, dropped, hits, names = patch_body(raw)
        self.assertEqual(dropped, 1)
        self.assertEqual(hits, ["$.tools[0].function.parameters.properties.sessionID.pattern"])
        self.assertEqual(names, ["glob"])
        parsed = json.loads(out)
        props = parsed["tools"][0]["function"]["parameters"]["properties"]
        self.assertNotIn("pattern", props["sessionID"])
        self.assertIn("pattern", props)  # nome de propriedade nao e chave de schema

    def test_keeps_anchored_and_returns_original_bytes(self) -> None:
        payload = chat_payload(
            {"type": "object", "properties": {"id": {"type": "string", "pattern": "^[a-z]+$"}}}
        )
        raw = json.dumps(payload, separators=(",", ":")).encode()
        out, dropped, hits, _ = patch_body(raw)
        self.assertEqual(dropped, 0)
        self.assertEqual(hits, [])
        self.assertIs(out, raw)  # sem mudanca = bytes intactos (passthrough real)

    def test_legacy_functions(self) -> None:
        payload = chat_payload({"type": "object", "properties": {"p": {"pattern": "*.md"}}}, legacy=True)
        out, dropped, hits, _ = patch_body(json.dumps(payload).encode())
        self.assertEqual(dropped, 1)
        self.assertEqual(hits, ["$.functions[0].parameters.properties.p.pattern"])
        legacy_prop = json.loads(out)["functions"][0]["parameters"]["properties"]["p"]
        self.assertNotIn("pattern", legacy_prop)

    def test_deep_nesting_and_arrays(self) -> None:
        node = {"a": [{"pattern": "x?y"}, {"pattern": "^ok$"}]}
        hits: list[str] = []
        self.assertEqual(strip_unanchored_patterns(node, "$", hits), 1)
        self.assertEqual(hits, ["$.a[0].pattern"])
        self.assertEqual(node, {"a": [{}, {"pattern": "^ok$"}]})

    def test_does_not_touch_messages_or_non_text(self) -> None:
        payload = {
            "messages": [{"role": "user", "content": '{"pattern": "*.cs"}'}],
            "tools": [{"type": "function", "function": {"name": "x", "parameters": {"type": "object"}}}],
        }
        raw = json.dumps(payload).encode()
        out, dropped, _, _ = patch_body(raw)
        self.assertEqual(dropped, 0)
        self.assertIs(out, raw)

    def test_invalid_json_passthrough(self) -> None:
        raw = b"not json"
        out, dropped, hits, names = patch_body(raw)
        self.assertEqual((out, dropped, hits, names), (raw, 0, [], []))

    def test_idempotent(self) -> None:
        raw = json.dumps(
            chat_payload({"type": "object", "properties": {"s": {"pattern": "^(ses)"}}})
        ).encode()
        once, dropped1, _, _ = patch_body(raw)
        twice, dropped2, _, _ = patch_body(once)
        self.assertEqual(dropped1, 1)
        self.assertEqual(dropped2, 0)
        self.assertEqual(once, twice)


class DumpTest(unittest.TestCase):
    def test_dump_grava_stream_completo_mesmo_com_erro(self) -> None:
        import tempfile
        from pathlib import Path

        from pattern_bridge import CFG, write_dump

        with tempfile.TemporaryDirectory() as tmp:
            old = CFG.dump_dir
            CFG.dump_dir = tmp
            try:
                path = write_dump("/v1/chat/completions", "POST", 500, 12,
                                  b"req", [b"data: a\n\n", b"data: b\n\n"], 1,
                                  ["$.tools[0].pattern"], "excecao: boom")
            finally:
                CFG.dump_dir = old
            self.assertIsNotNone(path)
            stream = Path(path)
            self.assertEqual(stream.read_bytes(), b"data: a\n\ndata: b\n\n")
            meta = json.loads(stream.with_suffix(".meta.json").read_text())
            self.assertEqual(meta["status"], 500)
            self.assertEqual(meta["note"], "excecao: boom")
            self.assertEqual(meta["bytes_out"], 18)
            self.assertEqual(meta["pattern_strip"], 1)

    def test_sem_dump_dir_nao_grava(self) -> None:
        from pattern_bridge import CFG, write_dump

        old = CFG.dump_dir
        CFG.dump_dir = None
        try:
            self.assertIsNone(write_dump("/x", "POST", 200, 1, b"", [b"a"], 0, [], ""))
        finally:
            CFG.dump_dir = old


if __name__ == "__main__":
    unittest.main(verbosity=2)
