# Sandboxie — duas sims reais numa máquina (padrão-ouro do caça-bug)

> Objetivo: rodar uma **segunda instância 1.5.3f1** isolada no Sandboxie, na SUA máquina, sem amigos.
> Aí são **dois mundos de verdade** conversando por localhost — e o StateHash/wiretap-diff pegam
> divergência REAL (inclusive a visual do cliente, que o bot sozinho não vê). Não é burlar nada:
> é isolar o namespace de um processo que é seu, pra a trava "1 instância" do Unity não bloquear.
>
> A automação já está pronta do lado do mod: o autopilot tem modo **host** (auto-hospeda) e **client**
> (auto-conecta). Falta só provar que o jogo roda sandboxed.

## Passo 0 — antes do Sandboxie, o teste grátis de 2 min

Talvez nem precise do Sandboxie. Abra o jogo normal e, com ele aberto, tente abrir um
**segundo** `Cities2.exe` (duplo-clique no mesmo exe). Duas possibilidades:
- **Abriu os dois** → a trava "1 instância" não te bloqueia; pula direto pro Passo 3 (automação), sem
  Sandboxie.
- **O segundo recusou/fechou na hora** → é o mutex do Unity; segue pro Passo 1 (Sandboxie isola isso).

## Passo 1 — instalar o Sandboxie-Plus

- Baixe o **Sandboxie-Plus** (fork mantido, open-source): https://sandboxie-plus.com → Download, ou
  GitHub `sandboxie-plus/Sandboxie` releases. Instala normal (precisa de admin).
- É leve, roda em Windows nativo (não é VM) — GPU e velocidade cheias.

## Passo 2 — teste de fogo: o jogo roda sandboxed?

Este é o único "não sei". Alguns anti-tamper não gostam de sandbox.
1. No Sandboxie-Plus: **Sandbox → Create New Box** → nome `CS2Client` (tipo: Standard).
2. Botão direito na box → **Run → Run Program** → aponte pro
   `<pasta-do-jogo>\Cities2.exe`.
3. **Chegou no menu principal?**
   - **Sim** → 🎉 destravou. Segue pro Passo 3.
   - **Não** (crasha/trava no boot) → o jogo rejeita sandbox. Plano B: usar uma VM Windows com
     GPU (Hyper-V GPU-PV) ou, mais simples, seguir só com o **bot hunter** (que já acha muita coisa).
     Me avisa o que aconteceu.

## Passo 3 — automação (eu monto os scripts)

**Topologia (pedido do Bruno): host + sandbox + bot = 3 "players", 2 sims reais.**
1. **Host** (anti-tamper normal): `CS2M_AUTOPILOT=host CS2M_AP_TEST=0 CS2M_WIRETAP=1 CS2M_STATEHASH=1`
   → sobe hospedando em :1111. **Sim real nº1** (dá pra screenshot).
2. **Cliente** (anti-tamper sandboxed): `CS2M_AUTOPILOT=client CS2M_AP_IP=127.0.0.1 CS2M_AP_PORT=1111
   CS2M_WIRETAP=1 CS2M_STATEHASH=1` → conecta sozinho, vira cliente real. **Sim real nº2** (screenshot).
   - Env vars por box no Sandboxie via um `.bat` que faz `set` e lança.
3. **Bot** (headless normal) como **3º player** → testa o relay estrela (star) e serve de ator extra.

**Detecção em 3 camadas:**
- **StateHash**: o cliente compara o mundo dele com o do host → `[Hash] DRIFT categoria`.
- **Wiretap + diff** nos três → acha o comando perdido.
- **Screenshots (pedido do Bruno — fecha o buraco do bug VISUAL):** capturar a janela do host e a do
  sandbox (via PowerShell `PrintWindow`/`CopyFromScreen`; o Claude lê PNG e inspeciona/compara). Pega
  o caso "colocou torto na tela mas o estado ECS bate", que o hash não vê. Requisitos: rodar em
  **janela/borderless** (fullscreen exclusivo não captura bem) e **sincronizar a câmera** das duas
  instâncias no mesmo ponto (via comando/autopilot) pra a comparação ser 1:1.

**Cenários CONCORRENTES (pedido do Bruno — o que mais importa):** não é um driver sequencial; são
ações SIMULTÂNEAS conflitantes, que é onde mora a race condition. Ex.: **player A desenha uma rua e,
no mesmo instante, o player B tenta desenhar EM CIMA** — testa echo guard, dedup, ordenação e
resolução de conflito ao mesmo tempo. Faço host-autopilot + bot(s) dispararem ações com timing
sobreposto (mesmas coords, quase juntos), depois screenshot + StateHash + wiretap-diff conferem se os
três convergiram pro MESMO resultado.

Isso é o caça-bug de fidelidade máxima, rodando sem ninguém. O único gargalo é o Passo 2.

## Notas
- Saves/config do cliente sandboxed ficam DENTRO do sandbox (copy-on-write) — não sujam os seus.
- O mod carrega dos dois porque a pasta `LocalLow\...\Mods\CS2M` é lida por ambos (o Sandboxie
  permite leitura do sistema real por padrão).
- Rodar dois CS2 puxa RAM/GPU — sua workstation aguenta, mas feche o Lumion durante o teste.
