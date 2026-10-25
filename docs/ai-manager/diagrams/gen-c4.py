#!/usr/bin/env python3
"""Gerador deterministico do C4 (draw.io XML) do plano de absorcao.

Arquitetura representada (v2, absorcao):
  - AI.Manager e o sistema concentrador (Web/API, workflow existente,
    TestBench, SQLite/evidencia);
  - AI.Manager.Bridge e um projeto DENTRO do AI.Manager
    (Host, Protocol, Flow/TestCircuitDriver, Contracts/Client);
  - Rochas.OpenCodeBridge aparece como DOADOR/TRANSITORIO, fora do sistema;
  - Q4 :4124 e o gate; Q3 :4125 e espelho pos-green; llama.cpp fica externo.

Uso:
  python3 gen-c4.py            # grava ai-manager-next-steps-c4.drawio ao lado
  python3 gen-c4.py --stdout   # imprime o XML

O PNG e derivado do .drawio pela CLI oficial (ver LAB-SETUP §4.6):
  drawio -x -f png --size page -s 2.5 -o ai-manager-next-steps-c4.png ai-manager-next-steps-c4.drawio
"""
from __future__ import annotations

import html
import sys
from pathlib import Path

PAGE_W, PAGE_H = 1600, 960
DRAWIO = "app.diagrams.net"

# --- estilo base -------------------------------------------------------------
BOX = (
    "rounded=1;arcSize=6;whiteSpace=wrap;html=1;fontSize=12;"
    "fillColor={fill};strokeColor={stroke};strokeWidth=1;fontColor=#18202E;"
    "align=center;verticalAlign=middle;spacing=6;"
)
BOX_LEFT = BOX.replace("align=center", "align=left;spacingLeft=10")
TITLE = "text;html=1;strokeColor=none;fillColor=none;align=left;verticalAlign=middle;fontSize=24;fontStyle=1;fontColor=#18202E;"
SUB = "text;html=1;strokeColor=none;fillColor=none;align=left;verticalAlign=middle;fontSize=11;fontColor=#4E5A6C;"
BOUNDARY = (
    "rounded=1;arcSize=2;whiteSpace=wrap;html=1;dashed={dashed};dashPattern=6 4;"
    "fillColor={fill};strokeColor={stroke};strokeWidth=1;verticalAlign=top;"
    "align=left;spacingLeft=14;spacingTop=8;fontSize=14;fontStyle=1;fontColor=#18202E;"
)
EDGE = (
    "edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;endArrow=block;endSize=6;"
    "strokeColor={stroke};strokeWidth=1.2;fontSize=9;fontColor=#4E5A6C;"
    "labelBackgroundColor=#FFFFFF;"
)

C_AI = "#EAF2FD"        # AI.Manager (sistema)
C_BRIDGE = "#E6EEFB"    # AI.Manager.Bridge (projeto interno)
C_DONOR = "#F4F4F5"     # OpenCodeBridge (doador)
C_MODEL = "#EAF6EE"     # llama.cpp
C_GATE = "#FDF0E6"      # bridge Q4 (gate)
C_MIRROR = "#F3F0FB"    # bridge Q3 (espelho)
C_STORE = "#FFF7E0"     # storage
C_EXT = "#F7F8FA"       # externo neutro
S_AI = "#788496"
S_BRIDGE = "#3E6FB0"
S_DONOR = "#8A8A8A"
S_MODEL = "#4E8A63"
S_GATE = "#B06A2C"
S_MIRROR = "#6A4FB0"
S_STORE = "#B09029"
S_EXT = "#788496"

cells: list[str] = []


def cell(
    cid: str,
    value: str,
    x: int,
    y: int,
    w: int,
    h: int,
    style: str = BOX,
    fill: str = "#FFFFFF",
    stroke: str = "#D2D8E2",
    dashed: str = "0",
    boundary: bool = False,
) -> None:
    if boundary:
        st = BOUNDARY.format(fill=fill, stroke=stroke, dashed=dashed)
    else:
        st = style.format(fill=fill, stroke=stroke)
    v = html.escape(value, quote=True).replace("&#x27;", "'")
    cells.append(
        f'        <mxCell id="{cid}" value="{v}" style="{st}" vertex="1" parent="1">\n'
        f'          <mxGeometry x="{x}" y="{y}" width="{w}" height="{h}" as="geometry" />\n'
        f"        </mxCell>"
    )


def edge(src: str, dst: str, label: str = "", stroke: str = "#788496",
         dashed: str = "0", exit: str = "", entry: str = "") -> None:
    st = EDGE.format(stroke=stroke)
    if dashed == "1":
        st += "dashed=1;dashPattern=4 4;"
    if exit:
        st += f"exitX={exit[0]};exitY={exit[1]};exitDx=0;exitDy=0;"
    if entry:
        st += f"entryX={entry[0]};entryY={entry[1]};entryDx=0;entryDy=0;"
    eid = f"e_{src}_{dst}"
    v = html.escape(label, quote=True)
    cells.append(
        f'        <mxCell id="{eid}" value="{v}" style="{st}" edge="1" parent="1" source="{src}" target="{dst}">\n'
        f'          <mxGeometry relative="1" as="geometry" />\n'
        f"        </mxCell>"
    )


def build() -> str:
    # ---- cabecalho ----------------------------------------------------------
    cell("title", "AI.Manager — C4 Nível 2 (Contêineres): absorção do OpenCodeBridge como AI.Manager.Bridge",
         30, 6, 1180, 34, style=TITLE, fill="none", stroke="none")
    cell("subtitle",
         "AI.Manager é o concentrador da solução de treinamento e testes de modelos locais de baixa instrução  •  "
         "AI.Manager.Bridge é projeto interno (extraível)  •  Q4 :4124 = gate  •  Q3 :4125 = espelho pós-green",
         30, 42, 1180, 22, style=SUB, fill="none", stroke="none")

    cell("legend",
         "<b>Legenda</b><br><font style='font-size:9px'>"
         "—— chamada síncrona&nbsp;&nbsp;&nbsp;-- evidência / migração<br>"
         "■ AI.Manager&nbsp;&nbsp; ■ AI.Manager.Bridge&nbsp;&nbsp; ■ doador&nbsp;&nbsp; "
         "■ modelo&nbsp;&nbsp; ■ storage</font>",
         1240, 10, 330, 62, style=BOX_LEFT, fill="#FFFFFF", stroke="#D2D8E2")

    # ---- sistema AI.Manager -------------------------------------------------
    cell("boundary_ai",
         "<b>AI.Manager</b><br><font style='font-size:9px;color:#4E5A6C'>"
         "sistema concentrador — experimento, workflow, circuito, enunciado, evidência</font>",
         110, 100, 760, 700, fill=C_AI, stroke=S_AI, dashed="1", boundary=True)

    cell("web", "<b>Web UI</b><br><font style='font-size:9px'>catálogo • wizard • mapa do circuito • console • evidência</font>",
         148, 152, 320, 78, fill="#FFFFFF")
    cell("api", "<b>API + Application Services</b><br><font style='font-size:9px'>CRUD • ManifestBuilder • Validator • Acceptance • Runner Registry</font>",
         500, 152, 330, 78, fill="#FFFFFF")
    cell("workflow", "<b>Workflow existente</b><br><font style='font-size:9px'>Experiment (Kind=Training|Test) • TrainingWorkflowCard • aprovação/status</font>",
         148, 262, 320, 92, fill="#FFFFFF")
    cell("testbench", "<b>TestBench / Domain</b><br><font style='font-size:9px'>ExperimentPlan • CircuitStage/Step • TestScenario • TestRun • MirrorRun • Evidence</font>",
         500, 262, 330, 92, fill="#FFFFFF")

    # ---- AI.Manager.Bridge (projeto interno) --------------------------------
    cell("boundary_bridge",
         "<b>AI.Manager.Bridge</b> — projeto interno (extraível após paridade)<br>"
         "<font style='font-size:9px;color:#4E5A6C'>transporte e controle de fluxo entre cliente e engine</font>",
         140, 384, 700, 246, fill=C_BRIDGE, stroke=S_BRIDGE, dashed="0", boundary=True)

    cell("br_host", "<b>Host</b><br><font style='font-size:9px'>Responses • chat • SSE • /api/status • /api/metrics</font>",
         160, 438, 210, 68, fill="#FFFFFF", stroke=S_BRIDGE)
    cell("br_protocol", "<b>Protocol</b><br><font style='font-size:9px'>tradução • tools • harness/tails • breaker • permissões</font>",
         386, 438, 220, 68, fill="#FFFFFF", stroke=S_BRIDGE)
    cell("br_flow", "<b>Flow</b><br><font style='font-size:9px'>TestCircuitDriver • DAG • ManifestCircuitTemplate • gate • artefatos</font>",
         622, 438, 200, 68, fill="#FFFFFF", stroke=S_BRIDGE)
    cell("br_contracts", "<b>Contracts</b><br><font style='font-size:9px'>manifesto • resultado • checksum</font>",
         160, 522, 210, 62, fill="#FFFFFF", stroke=S_BRIDGE)
    cell("br_client", "<b>Client</b><br><font style='font-size:9px'>HTTP/SSE tipado (AI.Manager + host legado)</font>",
         386, 522, 220, 62, fill="#FFFFFF", stroke=S_BRIDGE)
    cell("br_telemetry", "<b>Telemetria / evidência</b><br><font style='font-size:9px'>events.jsonl • summary.tsv • hashes</font>",
         622, 522, 200, 62, fill="#FFFFFF", stroke=S_BRIDGE)

    cell("store", "<b>SQLite</b><br><font style='font-size:9px'>definições • versões • runs • metadados de evidência • auditoria</font>",
         148, 652, 320, 78, fill=C_STORE, stroke=S_STORE)
    cell("seed", "<b>Seed do WAR</b><br><font style='font-size:9px'>war-baseline.json (importação, nunca runtime) • WarBaselineSeeder idempotente</font>",
         500, 652, 330, 78, fill=C_STORE, stroke=S_STORE)

    # ---- doador -------------------------------------------------------------
    cell("boundary_donor",
         "<b>Rochas.OpenCodeBridge</b> — doador / transitório (Git separado até o cutover)<br>"
         "<font style='font-size:9px;color:#4E5A6C'>congelado para features; consome AI.Manager.Bridge durante a transição; arquivado após paridade + 3 gates Q4 verdes</font>",
         110, 818, 760, 118, fill=C_DONOR, stroke=S_DONOR, dashed="1", boundary=True)
    cell("donor", "<b>Host legado + Test + WAR</b><br><font style='font-size:9px'>Program.cs (~245 KB) • TestCircuitDriver • WarTemplate • E2E belts • Web</font>",
         148, 866, 682, 56, fill="#FFFFFF", stroke=S_DONOR)

    # ---- fora do AI.Manager -------------------------------------------------
    cell("user", "<b>Autor / operador</b><br><font style='font-size:9px'>pessoa — guia o circuito e aprova o experimento</font>",
         990, 140, 580, 66, fill=C_EXT, stroke=S_EXT)
    cell("bridge_q4", "<b>Bridge Q4 :4124</b> — GATE<br><font style='font-size:9px'>instância estável; único critério de aceite</font>",
         990, 246, 580, 66, fill=C_GATE, stroke=S_GATE)
    cell("llama_q4", "<b>llama.cpp Q4 :4100</b><br><font style='font-size:9px'>Gemma4 E4B Q4_K_M • ctx 131072 (1 slot) • saída 16384</font>",
         990, 340, 580, 58, fill=C_MODEL, stroke=S_MODEL)
    cell("bridge_q3", "<b>Bridge Q3 :4125</b> — espelho pós-green<br><font style='font-size:9px'>roda o mesmo manifestHash só depois do Q4 verde; nunca é gate</font>",
         990, 432, 580, 66, fill=C_MIRROR, stroke=S_MIRROR)
    cell("llama_q3", "<b>llama.cpp Q3 :4110</b><br><font style='font-size:9px'>Gemma4 E4B UD-Q3 • ctx 114688 (2×57344) • saída 16384</font>",
         990, 526, 580, 58, fill=C_MODEL, stroke=S_MODEL)
    cell("github", "<b>GitHub</b><br><font style='font-size:9px'>versiona AI.Manager; OpenCodeBridge até o arquivamento</font>",
         990, 622, 580, 58, fill=C_EXT, stroke=S_EXT)
    cell("cloudflare", "<b>Cloudflare (acesso externo)</b><br><font style='font-size:9px'>publica a UI; opcional nesta fase</font>",
         990, 712, 580, 58, fill=C_EXT, stroke=S_EXT)

    # ---- relacionamentos ----------------------------------------------------
    edge("user", "web", "HTTPS", exit=("0.5", "1"), entry=("0.5", "0"))
    edge("web", "api", "", exit=("1", "0.5"), entry=("0", "0.5"))
    edge("api", "workflow", "", stroke=S_BRIDGE, exit=("0.15", "1"), entry=("0.85", "0"))
    edge("workflow", "testbench", "", stroke=S_BRIDGE, exit=("1", "0.5"), entry=("0", "0.5"))
    edge("api", "br_client", "in-process (1º corte)", stroke=S_BRIDGE, exit=("0.5", "1"), entry=("0.5", "0"))
    edge("testbench", "br_flow", "plano/execução", stroke=S_BRIDGE, exit=("0.5", "1"), entry=("0.5", "0"))
    edge("br_flow", "br_host", "chama /v1/responses", stroke=S_BRIDGE, exit=("0", "0.5"), entry=("1", "0.5"))
    edge("br_host", "br_protocol", "", stroke=S_BRIDGE, exit=("1", "0.5"), entry=("0", "0.5"))
    edge("br_flow", "br_contracts", "manifesto", stroke=S_BRIDGE, exit=("0.3", "1"), entry=("1", "0.5"))
    edge("br_flow", "store", "gate + metadados", stroke=S_STORE, exit=("0", "0.85"), entry=("1", "0.3"))
    edge("br_telemetry", "store", "evidência", stroke=S_STORE, exit=("0.5", "1"), entry=("0.8", "0"))
    edge("seed", "testbench", "importação", stroke=S_STORE, dashed="1", exit=("0.5", "0"), entry=("1", "0.85"))
    edge("donor", "br_client", "transição (Bridge.Client)", stroke=S_DONOR, dashed="1", exit=("0.9", "0"), entry=("0.5", "1"))
    edge("br_host", "bridge_q4", "", stroke=S_GATE, exit=("1", "0.5"), entry=("0", "0.25"))
    edge("bridge_q4", "llama_q4", "inferência", stroke=S_GATE, exit=("0.7", "1"), entry=("0.7", "0"))
    edge("br_flow", "bridge_q3", "espelho (pós-green)", stroke=S_MIRROR, dashed="1", exit=("1", "0.3"), entry=("0", "0.75"))
    edge("bridge_q3", "llama_q3", "inferência", stroke=S_MIRROR, exit=("0.7", "1"), entry=("0.7", "0"))
    edge("store", "github", "", stroke=S_EXT, dashed="1", exit=("0.5", "1"), entry=("0.1", "0.5"))

    body = "\n".join(cells)
    return (
        f'<mxfile host="{DRAWIO}" modified="2026-10-25T00:00:00.000Z" agent="AI.Manager" '
        f'version="24.7.17" type="device">\n'
        f'  <diagram id="ai-manager-c4" name="C4 Nível 2 — Contêineres (absorção)">\n'
        f'    <mxGraphModel dx="1600" dy="960" grid="1" gridSize="10" guides="1" tooltips="1" '
        f'connect="1" arrows="1" fold="1" page="1" pageScale="1" pageWidth="{PAGE_W}" '
        f'pageHeight="{PAGE_H}" math="0" shadow="0">\n'
        f"      <root>\n"
        f'        <mxCell id="0" />\n'
        f'        <mxCell id="1" parent="0" />\n'
        f"{body}\n"
        f"      </root>\n"
        f"    </mxGraphModel>\n"
        f"  </diagram>\n"
        f"</mxfile>\n"
    )


def main() -> int:
    xml = build()
    if "--stdout" in sys.argv:
        sys.stdout.write(xml)
        return 0
    out = Path(__file__).with_name("ai-manager-next-steps-c4.drawio")
    out.write_text(xml, encoding="utf-8")
    print(f"escrito: {out} ({len(xml)} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
