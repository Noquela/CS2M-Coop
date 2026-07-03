# wiretap-diff — localizador de desync das gravações do CS2M

Cruza as gravações `CS2M_wiretap_*.jsonl` dos jogadores (geradas com `CS2M_WIRETAP=1`, ver
`FIELD_TEST.md`) e aponta os **comandos de ação que uns viram e outros não** — o comando perdido é
onde os mundos divergiram.

## Uso
```bash
# depois do teste de campo: junte os 3 arquivos numa pasta e rode
python analyze.py host.jsonl amigo1.jsonl amigo2.jsonl

python analyze.py *.jsonl --type Net     # foca em comandos de rede (ruas/canos)
python analyze.py *.jsonl --type Zone    # foca em zoneamento
python analyze.py *.jsonl --all          # inclui os periódicos (cursor, speed, stats, demanda...)
```
Requer só Python 3 (sem dependências).

## O que ele diz
- **Resumo por arquivo**: quantos comandos, OUT/IN, os tipos mais frequentes.
- **Divergências**: cada comando de ação presente em alguns jogadores e ausente em outros, com
  `TEM:` / `FALTA:` — o `FALTA` é o provável ponto do desync.
- Se **nenhuma** divergência de transporte e mesmo assim a tela divergiu → o bug é no **apply**, não
  no envio; aí o radar é `[Hash] DRIFT` / `[Invariant]` no `CS2M.log` do host.

## Como funciona
Assina cada comando por `tipo + campos de payload` (ignora `sender`, que o host re-carimba, e os
metadados seq/tempo/direção). A assinatura é idêntica entre máquinas para o mesmo comando lógico,
então comparar os conjuntos de assinaturas por arquivo revela o que faltou em quem. Presença (viu ao
menos uma vez) importa mais que contagem — floods idempotentes colapsam numa assinatura.

## Limitação (v52)
O wiretap v52 não dumpa conteúdo de arrays (aparecem como `System.Int32[]`), então comandos
distinguidos SÓ por array (ex.: duas pinturas no MESMO bloco que diferem só nas células) colapsam
numa assinatura. Ainda pega "o comando daquele bloco/rota se perdeu". Precisão por-célula exigiria
dump de array no `WireTap.cs` (mudança no mod → nova versão para os 3).
