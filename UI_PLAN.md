# UI_PLAN.md — melhorias de UI/UX e visibilidade de co-op (brainstorm Bruno, 2026-07-03)

> O que sobrou do brainstorm (o Bruno recusou: grade de câmeras, "o que você perdeu", mapa de calor,
> placar/MVP, linha do tempo, coordenação leve §4 inteira, momentos compartilháveis §6). Ordem de
> implementação depois que terminarmos os testes de sync. Cor por jogador é a base de tudo (amarra
> cursor/preview/ping/brilho na cor de cada um).

## 1. Consciência ao vivo — "ver o que os outros fazem"
- **Preview ao vivo (v1)** 🟢 — enquanto o amigo arrasta rua/zona/prédio, transmitir o estado do tool
  em alta frequência (como o cursor) e desenhar uma linha/forma colorida + nome do outro lado.
  Reusa CursorOverlay/CursorProjection. (v2 = fantasma real do jogo, 🔴 depois.)
- **Brilho de mudança** 🟢 — quando um prédio/rua nasce, pisca na **cor de quem colocou**.
- **Anotar no mapa** 🟡 — marcar/desenhar um ponto temporário que os outros veem ("faz aqui").

## 2. Autoria e identidade
- **Cor por jogador** 🟢 — mesma cor em cursor, preview, ping, brilho, chat. **Base — fazer primeiro.**
- **Autoria no hover** 🟡 — passar o mouse num prédio → "construído por Amigo2 às 21:43".

## 3. Confiança "tá tudo sincronizado" (NOSSO diferencial — StateHash vira UX)
- **Selo de sincronia ao vivo** 🟢 — badge "Você e Amigo1: em sync ✓" alimentado pelo StateHash.
- **Desync = aviso amigável** 🟢 — o [Hash] DRIFT vira badge na UI + botão /resync.
- **Destaque do que divergiu** 🟡 — o que está diferente da tua tela vs host brilha em vermelho.

## 5. Presença — onde cada um está
- **Ir até o jogador (goto)** 🟢 — clica no nome no painel → câmera voa até ele.
- **Setas na borda** 🟢 — apontando onde os outros trabalham.
- **Painel de jogadores rico** 🟢 — avatar, cor, ping, "fazendo o quê", botão goto.

## 7. Polish
- **Status de conexão** 🟢 (latência + "reconectando…"), notificações join/leave, join mais suave.

**Ordem sugerida:** Cor por jogador → tool no cursor → preview ao vivo v1 → selo de sincronia →
brilho de mudança → goto/painel rico → autoria no hover → anotar no mapa → polish.

_(A confirmar com Bruno: ele disse "pode tirar o 4 todo" e depois "4 pode ser" — §4 fora por ora.)_
