// ===========================================================================
//  Swagger da OpenCodeBridge — pagina propria, sem dependencia externa.
// ---------------------------------------------------------------------------
//  Sem pacotes NuGet (Swashbuckle) e sem CDN: a especificacao OpenAPI e um
//  texto estatico e a pagina HTML/CSS/JS e 100% autocontida. Funciona ate
//  com o opencode apontado remotamente para a bridge.
//  Identificadores em en-US, comentarios em pt-BR.
// ===========================================================================

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge;

internal static partial class Program
{
    // ---------------------------------------------------------------------
    // Especificacao OpenAPI 3.0 dos endpoints da bridge.
    // ---------------------------------------------------------------------
    static readonly string SwaggerJson = """
        {
          "openapi": "3.0.0",
          "info": {
            "title": "OpenCodeBridge API",
            "version": "1.4",
            "description": "Padrao novo (/v1/responses, traduzido) + padrao antigo (/v1/chat/completions, /v1/completions, repasse) + gerenciamento."
          },
          "paths": {
            "/health": {
              "get": {
                "summary": "Saude da bridge",
                "responses": {
                  "200": {
                    "description": "Bridge no ar"
                  }
                }
              }
            },
            "/api/status": {
              "get": {
                "summary": "Config + telemetria",
                "security": [{"bearerAuth": []}],
                "responses": {
                  "200": {
                    "description": "Foto da bridge"
                  }
                }
              }
            },
            "/api/metrics": {
              "get": {
                "summary": "Vazao tokens/s (p/ exibir no e2e)",
                "security": [{"bearerAuth": []}],
                "responses": {
                  "200": {
                    "description": "requests, tokens e tps"
                  }
                }
              }
            },
            "/api/swagger.json": {
              "get": {
                "summary": "Esta especificacao",
                "responses": {
                  "200": {
                    "description": "OpenAPI JSON"
                  }
                }
              }
            },
            "/v1/responses": {
              "post": {
                "summary": "NOVO: traducao Responses->chat (fluxo do opencode)",
                "security": [{"bearerAuth": []}],
                "requestBody": {
                  "content": {
                    "application/json": {
                      "example": {
                        "model": "openai/Gemma4-E4B-Q4",
                        "stream": true,
                        "instructions": "seja breve",
                        "input": [
                          {
                            "type": "message",
                            "role": "user",
                            "content": "liste arquivos"
                          }
                        ],
                        "tools": [
                          {
                            "type": "function",
                            "name": "bash",
                            "description": "shell",
                            "inputSchema": {
                              "type": "object"
                            }
                          }
                        ]
                      }
                    }
                  }
                },
                "responses": {
                  "200": {
                    "description": "SSE Responses ou JSON"
                  }
                }
              }
            },
            "/v1/chat/completions": {
              "post": {
                "summary": "ANTIGO: repasse cru p/ o vLLM (chat)",
                "security": [{"bearerAuth": []}],
                "requestBody": {
                  "content": {
                    "application/json": {
                      "example": {
                        "model": "Gemma4-E4B-Q4",
                        "messages": [
                          {
                            "role": "user",
                            "content": "diga ok"
                          }
                        ],
                        "max_tokens": 50
                      }
                    }
                  }
                },
                "responses": {
                  "200": {
                    "description": "Resposta original do vLLM"
                  }
                }
              }
            },
            "/v1/completions": {
              "post": {
                "summary": "ANTIGO (legado): repasse cru p/ o vLLM (prompt simples)",
                "security": [{"bearerAuth": []}],
                "requestBody": {
                  "content": {
                    "application/json": {
                      "example": {
                        "model": "Gemma4-E4B-Q4",
                        "prompt": "diga ok",
                        "max_tokens": 50
                      }
                    }
                  }
                },
                "responses": {
                  "200": {
                    "description": "Resposta original do vLLM"
                  }
                }
              }
            },
            "/v1/models": {
              "get": {
                "summary": "ANTIGO: lista modelos (repasse)",
                "security": [{"bearerAuth": []}],
                "responses": {
                  "200": {
                    "description": "Lista do vLLM"
                  }
                }
              }
            },
            "/api/auth/login": {
              "post": {
                "summary": "Login JWT (usuarios do Web); devolve Bearer p/ endpoints com cadeado",
                "requestBody": { "content": { "application/json": { "example": { "email": "admin@bridge.pcss.com.br", "password": "123" } } } },
                "responses": { "200": { "description": "token, token_type Bearer, expires_in, admin" }, "401": { "description": "credenciais invalidas" } }
              }
            },
            "/api/harness": {
              "get": {
                "summary": "Flag mestra do harness (publico; a Web consulta aqui)",
                "responses": { "200": { "description": "harness true/false" } }
              }
            },
            "/api/translate": {
              "post": {
                "summary": "Dry-run: mostra o chat request sem chamar o modelo",
                "requestBody": {
                  "content": {
                    "application/json": {
                      "example": {
                        "model": "openai/Gemma4-E4B-Q4",
                        "instructions": "x",
                        "input": "oi",
                        "tools": [
                          {
                            "type": "function",
                            "name": "bash",
                            "description": "s",
                            "inputSchema": {
                              "type": "object"
                            }
                          }
                        ]
                      }
                    }
                  }
                },
                "responses": {
                  "200": {
                    "description": "Chat request traduzido"
                  }
                }
              }
            },
            "/api/convert": {
              "post": {
                "summary": "Converte chat completion pronto em objeto Responses",
                "requestBody": {
                  "content": {
                    "application/json": {
                      "example": {
                        "model": "Gemma4-E4B-Q4",
                        "temperature": 0,
                        "completion": {
                          "choices": [
                            {
                              "message": {
                                "role": "assistant",
                                "content": "ok"
                              }
                            }
                          ],
                          "usage": {
                            "prompt_tokens": 10,
                            "completion_tokens": 2
                          }
                        }
                      }
                    }
                  }
                },
                "responses": {
                  "200": {
                    "description": "Objeto Responses"
                  }
                }
              }
            },
            "/api/config": {
              "post": {
                "summary": "Ajuste remoto (thinking, temperature, max_tokens)",
                "security": [{"bearerAuth": []}],
                "requestBody": {
                  "content": {
                    "application/json": {
                      "example": {
                        "thinking": "events",
                        "temperature": 0,
                        "max_tokens": 8192
                      }
                    }
                  }
                },
                "responses": {
                  "200": {
                    "description": "Status atualizado"
                  }
                }
              }
            }
          },
          "components": { "securitySchemes": { "bearerAuth": { "type": "http", "scheme": "bearer", "bearerFormat": "JWT",
            "description": "JWT do POST /api/auth/login (usuarios do Web). Legado: a ApiKey tambem passa no header Authorization: Bearer." } } }
        }
        """;

    // ---------------------------------------------------------------------
    // Pagina de documentacao: tema black, zero dependencia externa.
    // ---------------------------------------------------------------------
    static readonly string SwaggerHtml = """
        <!DOCTYPE html>
        <html lang="pt-BR">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>ai.bridge.api</title>
        <style>
          :root { --bg:#1e1e1e; --panel:#252526; --line:#3a3a3a; --txt:#d4d4d4;
                  --dim:#9e9e9e; --green:#6a9955; --blue:#4ec9b0; --amber:#dcdcaa; --red:#f44747; }
          * { box-sizing:border-box; }
          body { background:var(--bg); color:var(--txt); font-family:ui-monospace,Menlo,Consolas,monospace;
                 margin:0; padding:24px; }
          header { display:flex; align-items:center; gap:12px; margin-bottom:20px; flex-wrap:wrap; }
          h1 { font-size:20px; margin:0; }
          h1 span { color:var(--green); }
          #pill { font-size:12px; border:1px solid var(--line); border-radius:20px;
                  padding:4px 12px; color:var(--dim); }
          #pill b { color:var(--green); }
          .card { background:var(--panel); border:1px solid var(--line); border-radius:10px;
                  margin-bottom:12px; overflow:hidden; }
          .head { display:flex; gap:10px; align-items:center; padding:12px 14px; cursor:pointer; }
          .head:hover { background:#333333; }
          .m { font-size:12px; font-weight:bold; border-radius:6px; padding:3px 10px; min-width:52px; text-align:center; }
          .GET { background:#052e16; color:var(--green); border:1px solid #14532d; }
          .POST { background:#082f49; color:var(--blue); border:1px solid #0c4a6e; }
          .path { font-size:14px; }
          .sum { color:var(--dim); font-size:12px; margin-left:auto; }
          .body { display:none; border-top:1px solid var(--line); padding:14px; }
          .open .body { display:block; }
          textarea { width:100%; min-height:130px; background:#2d2d2d; color:#d4d4d4;
                     border:1px solid var(--line); border-radius:8px; padding:10px;
                     font-family:inherit; font-size:12px; resize:vertical; }
          .row { display:flex; gap:8px; margin-top:10px; align-items:center; }
          button { background:#4ec9b0; color:#1e1e1e; font-weight:bold; border:0; border-radius:8px;
                   padding:8px 18px; cursor:pointer; font-family:inherit; }
          button:hover { background:var(--green); }
          .st { font-size:12px; color:var(--amber); }
          pre { background:#1e1e1e; border:1px solid var(--line); border-radius:8px; padding:10px;
                font-size:12px; max-height:320px; overflow:auto; white-space:pre-wrap;
                word-break:break-word; color:#c9e7ff; }
          footer { color:var(--dim); font-size:11px; margin-top:16px; }
        </style>
        </head>
        <body>
        <header><h1>ai.bridge.<span>api</span></h1><div id="pill">status: ...</div></header>
        <div class="card"><div class="body" style="display:block;border:0">
          <b>Auth JWT</b> <span class="sum">login gera Bearer p/ endpoints com cadeado; ApiKey legada continua valendo</span>
          <div class="row"><input id="lemail" placeholder="email" value="admin@bridge.pcss.com.br" style="flex:2;background:#2d2d2d;color:#d4d4d4;border:1px solid var(--line);border-radius:8px;padding:8px">
          <input id="lpass" type="password" placeholder="password" style="flex:1;background:#2d2d2d;color:#d4d4d4;border:1px solid var(--line);border-radius:8px;padding:8px">
          <button id="lgo">Login</button><span class="st" id="lst"></span></div>
          <div class="row"><input id="ltok" placeholder="Bearer JWT (auto apos login)" style="flex:1;background:#2d2d2d;color:#4ec9b0;border:1px solid var(--line);border-radius:8px;padding:8px">
          <button id="luse">Usar</button><button id="lout">Sair</button></div>
        </div></div>
        <div id="list">carregando especificacao...</div>
        <footer>ai.bridge.api v1.4 · Responses&rarr;chat/completions · clique no endpoint para expandir e testar</footer>
        <script>
        async function init() {
          const spec = await (await fetch('api/swagger.json')).json();
          document.getElementById('list').innerHTML = '';
          try {
            const st = await (await fetch('api/status')).json();
            document.getElementById('pill').innerHTML =
              'status: <b>ok</b> · model ' + st.model + ' · up ' + st.uptime_seconds + 's · req ' + st.total_requests;
          } catch (e) { document.getElementById('pill').textContent = 'status: erro'; }
          Object.entries(spec.paths).forEach(([path, methods], idx) => {
            Object.entries(methods).forEach(([verb, op]) => {
              const ex = op.requestBody?.content?.['application/json']?.example;
              const card = document.createElement('div');
              card.className = 'card' + (idx === 0 ? ' open' : '');
              card.innerHTML =
                '<div class="head"><span class="m ' + verb.toUpperCase() + '">' + verb.toUpperCase() + '</span>' +
                '<span class="path">' + path + '</span><span class="sum">' + (op.summary || '') + lockOf(op) + '</span></div>' +
                '<div class="body">' +
                (ex ? '<textarea id="t' + idx + '">' + JSON.stringify(ex, null, 2) + '</textarea>' : '<i style="color:#888">sem corpo</i>') +
                '<div class="row"><button data-p="' + path + '" data-v="' + verb + '" data-i="' + idx + '">Enviar</button>' +
                '<span class="st" id="s' + idx + '"></span></div><pre id="r' + idx + '" style="display:none"></pre></div>';
              card.querySelector('.head').onclick = () => card.classList.toggle('open');
              list.appendChild(card);
            });
          });
          const tok = () => localStorage.getItem('ai_bridge_jwt') || '';
          const ltok = document.getElementById('ltok');
          if (ltok) ltok.value = tok();
          document.getElementById('lgo').onclick = async () => {
            const em = document.getElementById('lemail').value, pw = document.getElementById('lpass').value,
                  st = document.getElementById('lst');
            st.textContent = 'autenticando...';
            try {
              const r = await fetch('api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ email: em, password: pw }) });
              const j = await r.json();
              if (r.ok && j.token) { localStorage.setItem('ai_bridge_jwt', j.token); ltok.value = j.token; st.textContent = 'ok' + (j.admin ? ' (admin)' : ''); }
              else st.textContent = 'HTTP ' + r.status + ' ' + (j.error || '');
            } catch (e) { st.textContent = 'erro: ' + e; }
          };
          document.getElementById('luse').onclick = () => { localStorage.setItem('ai_bridge_jwt', ltok.value.trim()); };
          document.getElementById('lout').onclick = () => { localStorage.removeItem('ai_bridge_jwt'); ltok.value = ''; };
          const lockOf = (op) => (op && op.security) ? ' 🔒' : '';
          document.querySelectorAll('button').forEach(b => b.onclick = async () => {
            const i = b.dataset.i, ta = document.getElementById('t' + i),
                  st = document.getElementById('s' + i), out = document.getElementById('r' + i);
            st.textContent = 'enviando...'; out.style.display = 'none';
            try {
              const opt = { method: b.dataset.v.toUpperCase(), headers: { 'Content-Type': 'application/json' } };
              if (tok()) opt.headers['Authorization'] = 'Bearer ' + tok();
              if (ta) opt.body = ta.value;
              const r = await fetch(b.dataset.p.slice(1), opt);
              const txt = await r.text();
              st.textContent = 'HTTP ' + r.status;
              out.textContent = txt.slice(0, 6000); out.style.display = 'block';
            } catch (e) { st.textContent = 'erro: ' + e; }
          });
        }
        init();
        </script>
        </body>
        </html>
        """;

    // Serve a pagina e o JSON da especificacao.
    /// <summary>Serve a pagina de documentacao (tema black).</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    static void ServeSwaggerPage(HttpListenerContext ctx)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(SwaggerHtml);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    /// <summary>Serve a especificacao OpenAPI 3.0 em JSON.</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    static void ServeSwaggerJson(HttpListenerContext ctx)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(SwaggerJson);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }
}