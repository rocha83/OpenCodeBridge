# ENUNCIADO (GEN, role build - 12B GPU)

Voce e um dev front-end senior. No workspace ja existe um projeto ASP.NET Core MVC
(chamado app) com os assets vendorizados em `app/wwwroot/js` (react, react-dom) e
`app/wwwroot/css` (bootstrap.min.css) e `app/wwwroot/js/tailwind.js` (Tailwind Play CDN).

Escreva/sobrescreva EXATAMENTE estes 3 arquivos, com a tool `write_file` (caminhos
relativos a raiz do workspace):

1) `app/Views/Home/Index.cshtml` — Razor minimo, com `@{ Layout = null; }`, `<!DOCTYPE html>`,
   `<html lang="pt-br">`, `<head>` com:
   - `<link rel="stylesheet" href="/css/bootstrap.min.css">`
   - `<link rel="stylesheet" href="/css/app.css">`
   - `<script src="/js/tailwind.js"></script>`
   - `<title>Frontpipe</title>`
   No `<body>` (classes Tailwind `bg-slate-100 min-h-screen p-8`):
   - `<div id="root"></div>`
   - no fim do body, nesta ordem:
     `<script src="/js/react.production.min.js"></script>`,
     `<script src="/js/react-dom.production.min.js"></script>`,
     `<script src="/js/app.js"></script>`

2) `app/wwwroot/js/app.js` — React 18 SEM JSX: use `React.createElement` e
   `ReactDOM.createRoot(document.getElementById('root')).render(...)`.
   Renderize um `<main className="container mx-auto bg-white rounded-xl shadow-md p-6">`
   (classes Tailwind, so no wrapper) contendo, nesta ordem:
   - um `<h2>` com o texto "Frontpipe"
   - `<div id="quadro"><div id="titulo">Painel de vendas</div></div>`
   - `<button className="btn btn-primary mt-4">Atualizar</button>`
   NAO aplique classes Tailwind/Bootstrap em `#quadro` nem em `#titulo` (o tamanho dos dois
   vem so do `app.css`); nao use estilo inline neles.

3) `app/wwwroot/css/app.css` — CSS puro com EXATAMENTE estes valores (em px), e o
   `#quadro` com `box-sizing: border-box` (a largura que vale e' a de fora):
   #quadro { box-sizing: border-box; width: 240px; height: 120px; padding: 8px; background-color: #f1f5f9; border: 1px solid #94a3b8; }
   #titulo { font-size: 18px; font-weight: 600; color: #0f172a; }
   Inclua tambem um comentario de 1 linha no topo dizendo que o tamanho vem daqui.

Regras: nao crie outros arquivos, nao rode `dotnet`, nao altere mais nada.
Quando os 3 arquivos estiverem escritos, responda SOMENTE: FRONTPIPE-GEN-OK
