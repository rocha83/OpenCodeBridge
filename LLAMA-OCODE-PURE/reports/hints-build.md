# Build hints: erro de compilacao -> exemplo otimista (2026-11-10)

## Problema
A tabela de erros vivia dentro do `battery-war.sh` (case gigante, ~40 codigos) e era
impressa inteira no nudge do harness. Isso custa contexto, acopla a tabela ao harness
global (qualquer edicao arrisca regressao durante uma rodada ativa) e nao ajuda o modelo
no momento em que ele realmente precisa: **quando o build falha dentro do loop de tool**.

## Solucao
1. `hints.py` passa a ser a **fonte unica**: `hint_for(code)` e um switch hardcode que
   devolve `(descricao curta, exemplo que COMPILA)`; `codes_in`, `error_lines`, `render`
   e `alert` montam a saida. A CLI serve o harness:
   `printf '%s' "$errs" | python3 hints.py -` imprime so os codigos que apareceram.
2. `collector.py` (tunel) ganha `build_hint(body)`: a cada `POST /v1/chat*`, olha as
   ultimas mensagens de tool; se houver `error CSxxxx/MSB/NU` novo, anexa **uma**
   mensagem `user` sintetica com o erro exato (`arquivo(linha,col): error CODE: msg`) e o
   exemplo do `hints.py`. Dedupe por `codigo@arquivo(linha,col)` por conversa, teto de 8
   alertas, e a mensagem leva `x-injected: true` (nao conta em "Mensagens" nem vira
   "enunciado" no painel).
3. O harness deixa de carregar a tabela: `error_hints()` chama o `hints.py` (com fallback
   para a lista de codigos se o python falhar).

## Exemplos otimistas (amostra)
| codigo | exemplo devolvido |
| --- | --- |
| CS0246 | `namespace Cadastros; public interface IClienteRepository { Task<Cliente?> ObterAsync(int id); } public sealed class Cliente { ... }` |
| CS0234 | console puro nao tem ASP.NET: `<FrameworkReference Include="Microsoft.AspNetCore.App" />`; sem API, remova o using |
| CS8954 | `namespace Cadastros;` uma vez, primeira linha do arquivo |
| CS0101 | tipo declarado uma vez; apague a segunda definicao ou use a existente |
| CS0051 | tipo do parametro tao publico quanto o metodo: `public interface ...` + `public sealed class Service(Iface repo)` |
| CS1061 | `using Dapper;` + `conn.QueryAsync<T>(sql)` |
| NU1301 | feed transitorio: repita depois de corrigir; `curl -I https://api.nuget.org/v3/index.json` |

## Validacao
- `python3 -m py_compile hints.py collector.py` ok.
- CLI testada com erros reais da roxa (CS0246/CS8954) e do marrom (CS0234/CS0051).
- `build_hint` testado importando o collector: injeta 1x, dedupe no segundo request,
  injeta de novo quando aparece erro novo; `_new_user_count` ignora `x-injected`.
- Ativacao no collector: restart no boundary entre attempts (mesmo padrao anterior),
  porque o proxy e compartilhado com a matriz em execucao.
