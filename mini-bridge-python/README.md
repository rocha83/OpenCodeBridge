# mini-bridge-python

Bridge mínima em Python (stdlib, sem dependências) para servir modelos locais no
opencode v2 com **um único patch**: remover chaves JSON-Schema `pattern` que o
llama.cpp não consegue compilar na gramática de tool call. Todo o resto do
payload passa intacto.

## Por que existe

O opencode v2 anuncia tools cujo schema carrega `pattern` (ex.: `sessionID` com
`@pattern ^ses`, que vira `"pattern": "^(ses)"`). O parser de gramática do
llama.cpp exige âncora literal `^...$`; qualquer outra forma derruba a request
(`Unable to generate parser ... Pattern must start with '^'`). A regra aqui é a
mesma do `StripUnanchoredPatterns` da bridge C#:

- mantém `^...$`;
- remove `*.cs` (glob) e `^(ses)` (ancorada por grupo, termina em `)`).

## O que faz

| comportamento | detalhe |
|---|---|
| passthrough | se nenhum `pattern` foi removido, o corpo original segue **byte a byte** |
| escopo do filtro | só `tools` / `functions` (chat/completions **e** responses), recursivo |
| streaming | SSE repassado em fluxo, com encerramento no `data: [DONE]` |
| dump de stream | corpo inteiro fica **em memória** e é gravado no fim, **mesmo em erro/queda** (`--dump-stream DIR`) |
| erro upstream | corpo (>=400) é bufferizado, devolvido com `Content-Length` e logado em `error_body` |
| auditoria | JSONL por request: status, ms, bytes, `pattern_strip`, hits, tools, caminho do dump |

### Correções de HTTP que o testes revelaram

1. **Sem `Content-Length` nem `Transfer-Encoding`** o cliente HTTP/1.1 não vê o
   fim do corpo: o opencode ficava pendurado mesmo depois do `[DONE]`. Agora todo
   corpo sai em `chunked` com terminador.
2. **llama mantém a conexão aberta após `data: [DONE]`** (keep-alive). O relay
   fecha no marcador em vez de esperar EOF: request de 8 tokens caía de ~59 s
   (timeout do cliente) para 1,7 s.

## Uso

```sh
./run.sh start 4300 http://127.0.0.1:4100   # 12B
./run.sh start 4301 http://127.0.0.1:4110   # E2B (2 slots de 64k)
./run.sh status
./run.sh stop
./run.sh dump                                # analisa os dumps
```

No `opencode.json` do projeto, o provider precisa de
`package: "@opencode/ai/providers/openai-compatible"` para ir em
`/v1/chat/completions` (sem isso o v2 ignora `api: "openai-chat"` e usa
`/v1/responses`, que quebra no round-trip de tool output):

```jsonc
"providers": {
  "alt12b": {
    "package": "@opencode/ai/providers/openai-compatible",
    "settings": { "baseURL": "http://127.0.0.1:4300/v1", "apiKey": "guest@123" },
    "body": { "chat_template_kwargs": { "enable_thinking": false } },
    "models": { "gemma-4-12b-alt-build": { "modelID": "gemma-4-12b" } }
  }
}
```

`enable_thinking: false` é o único jeito de desligar thinking neste build
(`reasoning_budget: 0` é ignorado). Medido no `:4100`, `max_tokens=24`:
com off → 2 tokens, 0 reasoning, `"ok"`; sem o kwarg → 24 tokens, 60 chars de
reasoning e nenhum conteúdo.

## Sonda e análise

```sh
# prova direta contra o llama (sem bridge)
python3 probe_pattern.py --url http://127.0.0.1:4100/v1/chat/completions --pattern '*.cs'

# analisa dumps: repetição (thinking/content), tool call inválida, tags vazadas
python3 analyze_dumps.py --dir logs/dumps-4300
```

`analyze_dumps.py` reconstrói `reasoning_content` e `content` do SSE e reporta:

- taxa de repetição de 4-gramas e maior bloco repetido (thinking e content);
- `tool_calls[].function.arguments` inválido/truncado, arguments vazio/não-objeto,
  tool repetida na mesma resposta e `finish_reason=length` com tool call;
- tags de controle vazadas (`<|channel>`, `</thought>`, `<channel|>`, etc.).

## Testes

```sh
python3 -m unittest -v test_pattern_bridge
```

Cobrem: regra de âncora, recursão em `$defs`/arrays, `functions` legado,
passthrough byte-a-byte quando nada muda, idempotência, JSON inválido, e o dump
(mesmo com erro, com nota e contagem de bytes).
