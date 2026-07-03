# Sandboxie — duas sims reais numa máquina (padrão-ouro do caça-bug)

> Objetivo: rodar uma **segunda instância 1.5.3f1** isolada no Sandboxie, na SUA máquina, sem amigos.
> Aí são **dois mundos de verdade** conversando por localhost — e o StateHash/wiretap-diff pegam
> divergência REAL (inclusive a visual do cliente, que o bot sozinho não vê). Não é burlar o crack:
> é isolar o namespace de um processo que é seu, pra a trava "1 instância" do Unity não bloquear.
>
> A automação já está pronta do lado do mod: o autopilot tem modo **host** (auto-hospeda) e **client**
> (auto-conecta). Falta só provar que o crack roda sandboxed.

## Passo 0 — antes do Sandboxie, o teste grátis de 2 min

Talvez nem precise do Sandboxie. Abra o jogo (crack) normal e, com ele aberto, tente abrir um
**segundo** `Cities2.exe` (duplo-clique no mesmo exe). Duas possibilidades:
- **Abriu os dois** → a trava "1 instância" não te bloqueia; pula direto pro Passo 3 (automação), sem
  Sandboxie.
- **O segundo recusou/fechou na hora** → é o mutex do Unity; segue pro Passo 1 (Sandboxie isola isso).

## Passo 1 — instalar o Sandboxie-Plus

- Baixe o **Sandboxie-Plus** (fork mantido, open-source): https://sandboxie-plus.com → Download, ou
  GitHub `sandboxie-plus/Sandboxie` releases. Instala normal (precisa de admin).
- É leve, roda em Windows nativo (não é VM) — GPU e velocidade cheias.

## Passo 2 — teste de fogo: o crack roda sandboxed?

Este é o único "não sei". Alguns cracks/anti-tamper não gostam de sandbox.
1. No Sandboxie-Plus: **Sandbox → Create New Box** → nome `CS2Client` (tipo: Standard).
2. Botão direito na box → **Run → Run Program** → aponte pro
   `C:\JogosCrackeados\Cities.Skylines.II.v1.5.3f1\game\Cities2.exe`.
3. **Chegou no menu principal?**
   - **Sim** → 🎉 destravou. Segue pro Passo 3.
   - **Não** (crasha/trava no boot) → o crack rejeita sandbox. Plano B: usar uma VM Windows com
     GPU (Hyper-V GPU-PV) ou, mais simples, seguir só com o **bot hunter** (que já acha muita coisa).
     Me avisa o que aconteceu.

## Passo 3 — automação (eu monto os scripts)

Com os dois abrindo, o teste automático fica assim (eu escrevo o runner):
1. **Host** (crack normal): `CS2M_AUTOPILOT=host CS2M_AP_TEST=0 CS2M_WIRETAP=1 CS2M_STATEHASH=1`
   → sobe hospedando em :1111.
2. **Cliente** (crack sandboxed): `CS2M_AUTOPILOT=client CS2M_AP_IP=127.0.0.1 CS2M_AP_PORT=1111
   CS2M_WIRETAP=1 CS2M_STATEHASH=1` → conecta sozinho, baixa o mundo, vira cliente real.
   - No Sandboxie dá pra setar env vars por box (Box Options → ou via um `.bat` que faz `set` e
     lança), então isso automatiza.
3. **Driver das ações**: o **bot hunter** conecta como 3º e martela os cenários (T/X, sobreposição,
   rajada). Host E cliente aplicam tudo.
4. **Detecção automática**: o StateHash do cliente compara o mundo dele com o do host e loga
   `[Hash] DRIFT categoria` se divergirem; o wiretap dos dois + o `wiretap-diff` acham o comando
   perdido. **Bug de duas-sims aparece sozinho, com a categoria e o comando.**

Isso é o caça-bug de fidelidade máxima, rodando sem ninguém. O único gargalo é o Passo 2.

## Notas
- Saves/config do cliente sandboxed ficam DENTRO do sandbox (copy-on-write) — não sujam os seus.
- O mod carrega dos dois porque a pasta `LocalLow\...\Mods\CS2M` é lida por ambos (o Sandboxie
  permite leitura do sistema real por padrão).
- Rodar dois CS2 puxa RAM/GPU — sua workstation aguenta, mas feche o Lumion durante o teste.
