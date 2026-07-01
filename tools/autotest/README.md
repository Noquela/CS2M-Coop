# CS2M — teste autônomo local (2 instâncias, 1 PC)

Roda o pipeline de sincronização inteiro **sem segundo jogador e sem mouse**, subindo
duas instâncias do jogo na mesma máquina que conversam por `127.0.0.1`.

## Por que existe

Não dá pra rodar o ECS do jogo numa CLI pura (o `Unity.Entities` precisa do runtime
nativo da Unity). Então o jeito de testar "o prédio/estrada/zona realmente aparece?" é
com o jogo real. Este harness faz o **jogo se testar sozinho**.

## Como funciona

O `AutopilotSystem` (dentro do mod) só liga quando a env var `CS2M_AUTOPILOT` está
setada — logo o build normal dos amigos fica **idêntico**. Env vars:

| var | efeito |
|---|---|
| `CS2M_AUTOPILOT` | `host` ou `client` (qualquer outra coisa = desligado) |
| `CS2M_AP_PORT` | porta (default 1111) |
| `CS2M_AP_IP` | ip do host, no cliente (default 127.0.0.1) |
| `CS2M_AP_LOG` | arquivo de log próprio da instância (host e cliente separados) |
| `CS2M_AP_TEST` | `0` desliga o roteiro de placement no host (default ligado) |

- **host**: `-continuelastsave` carrega a última cidade → autopilot hospeda → quando um
  cliente entra, dispara o roteiro (árvore, prédio, estrada, delete-árvore) usando os
  MESMOS comandos dos detectores reais.
- **cliente**: no menu, auto-conecta no localhost, baixa o mapa, e loga quantos objetos
  remotos (`remoteObjects`) e edges (`totalEdges`) materializou.

## Rodar

```powershell
powershell -ExecutionPolicy Bypass -File run_localhost_test.ps1
```

O script sobe host, espera hospedar, sobe cliente, espera o roteiro e imprime as duas
transcrições `[Auto]` + um veredito. Logs em `tools/autotest/out/{host,client}.log`.

Flags: `-Port 1111`, `-HostLoadTimeoutSec 300`, `-TestTimeoutSec 240`, `-KillWhenDone`.

## O que o veredito confirma

- `remoteObjects>=1` no cliente → **object placement sync funciona** (apply cria a entidade).
- `totalEdges` aumentou → **net sync funciona** (a estrada remota materializou).
- Delete: `remoteObjects` cai depois do passo de delete → **delete sync funciona**.
- `[Place] APPLIED` / `[Net] INJECT` no `CS2M.log` do jogo → detalhe por-entidade.

## Ressalvas

- Duas instâncias de um jogo AAA na mesma máquina é **pesado** (RAM/GPU). Espere lentidão.
- O crack pode ter trava de instância única própria (o código do jogo não tem). Se a 2a
  instância não subir, é isso — aí o teste tem que ser em 2 PCs.
- As duas instâncias compartilham a pasta LocalLow (mesmo `CS2M.log`, mesmos settings).
  Por isso cada lado tem seu `CS2M_AP_LOG` próprio — o sinal do autopilot é confiável
  mesmo que o log compartilhado do jogo fique embaralhado.
