#!/usr/bin/env python3
"""Testes da rubrica das belts (sem rede, sem modelo): so o validador."""
from __future__ import annotations

import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from belts_opencode import BELTS, TRACKS, validate  # noqa: E402


class RubricTest(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.ws = Path(self.tmp.name)

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def write(self, name: str, content: str) -> None:
        p = self.ws / name
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(content)

    def test_build_passa_com_3_arquivos_e_500_chars(self) -> None:
        self.write("a/Reembolso.cs", "public class A {}\n" + "x" * 200)
        self.write("b/Repository.cs", "public class B {}\n" + "y" * 200)
        self.write("c/schema.sql", "create table t(id int);\n" + "z" * 120)
        r = validate("build", self.ws)
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["code_files"], 3)
        self.assertGreater(r["code_chars"], 500)

    def test_build_falha_com_2_arquivos(self) -> None:
        self.write("a.cs", "x" * 600)
        self.write("b.sql", "y" * 600)
        r = validate("build", self.ws)
        self.assertFalse(r["ok"])
        self.assertEqual(r["code_files"], 2)

    def test_build_ignora_extensao_fora_do_escopo(self) -> None:
        self.write("a.cs", "x" * 200)
        self.write("b.sql", "y" * 200)
        self.write("c.tsx", "z" * 200)
        self.write("d.md", "w" * 5000)
        r = validate("build", self.ws)
        self.assertTrue(r["ok"])
        self.assertEqual(r["code_files"], 3)

    def test_orch_passa_script_valido_e_executado(self) -> None:
        self.write(
            "solucao_reembolso.sh",
            "#!/usr/bin/env bash\nset -euo pipefail\n"
            "cat > schema.sql <<'EOF'\ncreate table reembolso(id int);\nEOF\n"
            "cat > Reembolso.cs <<'EOF'\npublic class Reembolso { }\nEOF\n",
        )
        self.write("solucao_reembolso.md", "# reembolso\ncria schema.sql e Reembolso.cs\n")
        r = validate("orch", self.ws, ["general"])
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["scripts_valid"], 1)
        self.assertEqual(r["scripts_executed"], 1)
        self.assertTrue((self.ws / "schema.sql").exists())
        self.assertTrue((self.ws / "Reembolso.cs").exists())

    def test_orch_falha_com_sintaxe_invalida(self) -> None:
        self.write("quebrado.sh", "#!/usr/bin/env bash\nif then fi\n")
        r = validate("orch", self.ws, ["general"])
        self.assertFalse(r["ok"])
        self.assertEqual(r["scripts_valid"], 0)

    def test_orch_nao_executa_comando_sensivel(self) -> None:
        self.write("perigoso.sh", "#!/usr/bin/env bash\nsudo rm -rf /\n")
        r = validate("orch", self.ws, ["general"])
        self.assertFalse(r["ok"])
        self.assertEqual(r["scripts_valid"], 1)
        self.assertEqual(r["scripts_executed"], 0)
        self.assertIn("SKIP EXEC", " ".join(r["notes"]))

    def test_enunciados_portados_sem_regra_extra(self) -> None:
        keys = [k for k, _ in BELTS]
        self.assertEqual(keys, ["azul", "verde", "roxa", "marrom", "preta"])
        for key, prompt in BELTS:
            self.assertTrue(prompt.startswith(("Crie", "Especifique")), key)
            for noise in ("BUILD-OK", "ORCH-OK", "SCRIPTER-OK", "subagente scripter",
                          "responda apenas", "use a tool", "Divida em fases"):
                self.assertNotIn(noise, prompt, key)

    def test_orch_passa_com_build_direto(self) -> None:
        # Orch pode delegar ao role Build (implementa direto), nao so ao Scripter.
        for name in ("a/Reembolso.cs", "b/Repo.cs", "c/schema.sql"):
            self.write(name, "x" * 250)
        r = validate("orch", self.ws, ["explore"])
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["delegated_roles"], ["explore"])
        self.assertEqual(r["scripts"], 0)

    def test_orch_aceita_os_dois_roles(self) -> None:
        self.write(
            "solucao.sh",
            "#!/usr/bin/env bash\nset -euo pipefail\ncat > schema.sql <<'EOF'\ncreate table t(id int);\nEOF\n",
        )
        self.write("a.cs", "x" * 250)
        self.write("b.cs", "y" * 250)
        self.write("c.cs", "z" * 250)
        r = validate("orch", self.ws, ["general", "explore"])
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["delegated_roles"], ["explore", "general"])

    def test_orch_falha_sem_delegacao(self) -> None:
        # Artefato sem subagente do mesmo modelo nao vale: orch nao implementa.
        for name in ("a.cs", "b.cs", "c.sql"):
            self.write(name, "x" * 250)
        r = validate("orch", self.ws, [])
        self.assertFalse(r["ok"])
        self.assertEqual(r["delegated_roles"], [])

    def test_tracks_mapeiam_agentes_e_modelos(self) -> None:
        self.assertEqual(TRACKS["build"]["agent"], "build")
        self.assertEqual(TRACKS["orch"]["agent"], "build")
        self.assertEqual(TRACKS["build"]["model"], "alt12b/gemma-4-12b")
        self.assertEqual(TRACKS["orch"]["model"], "alt12b/gemma-4-12b")


if __name__ == "__main__":
    unittest.main(verbosity=2)
