# Regras irrefutáveis do túnel python (`collector.py`)

1. **Reasoning proibido (padrão).** Pode ser sobreposto explicitamente com
   `THINK_FORCE=on` no ambiente do collector — nesse modo o proxy força
   `enable_thinking = true` em todo `/v1/chat*` (medido em `think_chars`). Sem
   override, vale: Todo `POST /v1/chat*` sai com
   `chat_template_kwargs.enable_thinking = false`. O collector injeta o kwarg
   **mesmo quando o cliente pede `true`** (sem o kwarg o llama-server liga o
   thinking por padrão). Medição: `thinking_blocked` no `/api/summary` e
   `oc_thinking_blocked_total` em `:9091/metrics`.
2. **Túnel é passthrough**, com duas exceções declaradas: o nudge do anti-repeat
   (1 mensagem `user` a mais) e o corte sintético (não chama o llama).
3. **Anti-repeat**: assinatura de tool consecutiva (nudge 3 / corte 6) e ciclo de
   2 chamadas alternadas **que falham** (nudge 3 / corte 6). `read`↔`write` sem
   erro é edição normal e não conta.
4. **Tempo é referência de comportamento**, não corte: mission-tools 300 s,
   belts/tools-full 480 s; acima disso o caso é marcado `SUSPEITO: possivel laco`.
5. **Observer em memória**: nada é escrito por request; dump só no fim da rodada
   (`/api/dump`, SIGTERM, atexit). Ocioso = gauges zeradas + última rodada
   congelada em `/api/recent`.
