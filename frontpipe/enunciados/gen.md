# ENUNCIADO (GEN, role build)

Monte o front do projeto `app` (ASP.NET Core MVC), sem JSX, com React 18 via
`React.createElement`.

Escreva estes 3 arquivos (caminhos relativos a raiz):

1) `app/Views/Home/Index.cshtml` — Razor minimo (`@{ Layout = null; }`), `lang="pt-br"`,
   head com `/css/bootstrap.min.css`, `/css/app.css`, `/js/tailwind.js` e titulo "Frontpipe";
   body com classes `bg-slate-100 min-h-screen p-8`, `<div id="root"></div>` e, nesta ordem,
   `/js/react.production.min.js`, `/js/react-dom.production.min.js`, `/js/app.js`.

2) `app/wwwroot/js/app.js` — `React.createElement` +
   `ReactDOM.createRoot(document.getElementById('root')).render(...)` de um
   `<main className="container mx-auto bg-white rounded-xl shadow-md p-6">` contendo, nesta ordem:
   `<h2>Frontpipe</h2>`,
   `<div id="quadro"><div id="titulo">Painel de vendas</div></div>` e
   `<button className="btn btn-primary mt-4">Atualizar</button>`.
   Sem classes Tailwind/Bootstrap nem estilo inline em `#quadro`/`#titulo`.

3) `app/wwwroot/css/app.css` — CSS puro:
   `#quadro { box-sizing: border-box; width: 240px; height: 120px; padding: 8px; background-color: #f1f5f9; border: 1px solid #94a3b8; }`
   `#titulo { font-size: 18px; font-weight: 600; color: #0f172a; }`
   Com um comentario de 1 linha no topo dizendo que o tamanho vem daqui.

Nao crie outros arquivos nem rode `dotnet`.

Ao terminar, responda SOMENTE: FRONTPIPE-GEN-OK


<!-- INVARIANTE-TUNEL-LLAMA:v1 -->
## Invariante — túnel de stream do llama é exclusivo da bridge/core

**Somente a bridge (`Rochas.OpenCodeBridge`, camada Core) fala com o llama-server.** Testes (`Rochas.OpenCodeBridge.Test`), `mini-bridge-python`, Web UI, Grafana e scripts são clientes/observadores: não injetam, não reescrevem, não "cutucam" e não reordenam mensagens dentro do túnel.

- Cliente envia a **requisição** (`/v1/chat/completions`, `/v1/responses`); o stream do modelo volta repassado **sem edição** pelo core.
- Proibido no cliente: nudge/continuation prompt, injeção de "próximo passo", reescrita de turno, reordenação ou truncamento de histórico.
- Harness e coerção (`main_harness`, tails, `coerce_text_tools`, `coerce_narrated`, `degenerate_retry`) vivem **só** na bridge/core, atrás de flag.
- Comportamento de teste se ajusta por **enunciado** ou **flag da bridge** — nunca editando o conteúdo do túnel no cliente.
- Nudges de continuação do `TestContext.ToolAgent` foram **removidos em 2026-11-07** (`TCTX_NUDGES` não existe mais): turno sem tool call encerra o loop e o veredito sai do artefato.
- Fonte única: `docs/TUNEL-LLAMA.md`.

