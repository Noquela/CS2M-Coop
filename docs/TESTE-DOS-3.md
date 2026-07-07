# Teste dos 3 — roteiro de validação final (v1.0.57.0)

O mod tem **10/11 fixes provados** em teste autônomo (2 simulações na mesma máquina).
Este roteiro cobre exatamente o que **só o jogo real multi-PC** valida — ~15 min de jogo.

## Setup
- Os 3 com o MESMO zip (`CS2M-v1.0.57.0.zip`) descompactado em
  `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CS2M\`.
- Nada pra configurar: todos os fixes já vêm ligados.
- Host sobe o save; os 2 entram. O selo de sincronia (badge) deve ficar "Em sync".

## O teste que falta (o item 11) — 2 min
1. **Um jogador que NÃO é o host** planta uma **fazenda** (Industrial > Agriculture,
   ex.: IndustrialAgricultureHub01).
2. Os 3 olham o MESMO lugar: o prédio **e o campo amarelo** devem aparecer idênticos
   (mesma posição, mesmo tamanho) nas 3 telas.
3. Host move essa fazenda (ferramenta de mover) → o campo e a estradinha interna
   devem acompanhar nas 3 telas.

## Checklist rápido do resto (já provado no autônomo — só confirmar a olho)
- [ ] Client desenha rua cruzando outra → junção igual nas 3 telas
- [ ] Client REDESENHA uma rua por cima de outra existente → nada some
- [ ] Client APAGA uma fonte d'água/distrito que o HOST criou → some pra todos
- [ ] Dois jogadores mudam TAXAS de categorias diferentes quase juntos → as duas valem
- [ ] Client muda frequência de veículos / preço de passagem de uma linha → reflete
- [ ] Zonas pintadas por qualquer um → mesmas células nas 3 telas

## Se QUALQUER coisa divergir na tela
1. Anotar O QUE e ONDE (print ajuda).
2. Me mandar os logs (cada PC):
   `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2M.log`
3. Com os logs eu localizo a entidade exata em minutos (statediff).

## O que é NORMAL divergir (não é bug)
Cidadãos, carros, trânsito individual, posts do Chirper e o instante em que um
prédio "cresce" num terreno zonado — a simulação emergente é local por design.
O que importa (e está sincado): tudo que os jogadores CONSTROEM e EDITAM.
