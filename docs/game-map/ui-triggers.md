# Varredura dos TriggerBindings de UI (`Game.UI`)

Enumeração exaustiva de todo `AddBinding(new TriggerBinding...)` sob
`decomp/Game/Game/UI/` (grep `AddBinding\(new TriggerBinding` — **249 ocorrências em 61
arquivos**, confirmado por `grep -c` em 05/07). Cada linha foi lida no código-fonte
(não só o grep) para decidir a classe. Convenção de classe:

- **MUTA-MUNDO** — o handler escreve um componente ECS `ISerializable`/`IBufferElementData`
  persistido (survive save/load), ou afeta um valor de sessão compartilhado (velocidade de
  simulação). Este é o checklist do que o coop precisa cobrir.
- **SÓ-LEITURA** — o handler calcula/consulta algo (preview, request) sem persistir nada.
- **LOCAL-COSMETIC** — câmera, foto, áudio/rádio, tutorial, navegação de painel/menu,
  configuração da FERRAMENTA ativa (que só produz mutação quando uma ação de colocação
  separada, fora deste binding, é aplicada), conta Paradox, editor de mapa/asset (modo de
  jogo `Editor`, fora do escopo do coop de cidade).

Metodologia e citações completas de cada linha MUTA-MUNDO estão no dossiê:
`docs/game-map/dossiers/ui-sweep.md`.

## app (AppBindings.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 1 | setClipboard | AppBindings.cs:200 | LOCAL-COSMETIC | copia texto para clipboard SO |
| 2 | exitApplication | AppBindings.cs:201 | LOCAL-COSMETIC | fecha o processo do jogo |
| 3 | errorAction | AppBindings.cs:202 | LOCAL-COSMETIC | reage a diálogo de erro |
| 4 | confirmationDialogCallback | AppBindings.cs:204 | LOCAL-COSMETIC | callback de diálogo de confirmação |
| 5 | dismissibleConfirmationDialogCallback | AppBindings.cs:205 | LOCAL-COSMETIC | idem, com opção dispensável |
| 6 | prerequisiteSelected | AppBindings.cs:211 | LOCAL-COSMETIC | seleciona pré-requisito de DLC |

## audio (AudioBindings.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 7 | playSound | AudioBindings.cs:15 | LOCAL-COSMETIC | toca efeito sonoro de UI |

## debug (Debug/DebugUISystem.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 8 | show | Debug/DebugUISystem.cs:153 | LOCAL-COSMETIC | mostra painel de debug |
| 9 | hide | Debug/DebugUISystem.cs:154 | LOCAL-COSMETIC | esconde painel de debug |
| 10 | selectPanel | Debug/DebugUISystem.cs:155 | LOCAL-COSMETIC | seleciona painel de debug |
| 11 | selectPreviousPanel | Debug/DebugUISystem.cs:156 | LOCAL-COSMETIC | navega painel de debug anterior |
| 12 | selectNextPanel | Debug/DebugUISystem.cs:157 | LOCAL-COSMETIC | navega painel de debug seguinte |

## input (InputBindings.cs, InputActionBindings.cs, InputHintBindings.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 13 | onGamepadPointerEvent | InputBindings.cs:44 | LOCAL-COSMETIC | evento de ponteiro de gamepad |
| 14 | setActiveTextFieldRect | InputBindings.cs:45 | LOCAL-COSMETIC | posiciona retângulo de campo texto |
| 15 | setActionPriority | InputActionBindings.cs:557 | LOCAL-COSMETIC | prioridade local de input action |
| 24 | onInputHintPerformed | InputHintBindings.cs:194 | LOCAL-COSMETIC | dispara evento de hint de input |

## camera (InGame/CameraUISystem.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 25 | focusEntity | InGame/CameraUISystem.cs:21 | LOCAL-COSMETIC | câmera orbital foca entidade |

## inputRebinding (Menu/InputRebindingUISystem.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 26 | cancelRebinding | Menu/InputRebindingUISystem.cs:108 | LOCAL-COSMETIC | cancela remapeamento de tecla |
| 27 | completeAndSwapConflicts | Menu/InputRebindingUISystem.cs:109 | LOCAL-COSMETIC | confirma troca de binds |
| 28 | completeAndUnsetConflicts | Menu/InputRebindingUISystem.cs:110 | LOCAL-COSMETIC | confirma remoção de binds |

## l10n (Localization/LocalizationBindings.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 29 | selectLocale | Localization/LocalizationBindings.cs:48 | LOCAL-COSMETIC | troca idioma da interface |

## notification (Menu/NotificationUISystem.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 30 | selectNotification | Menu/NotificationUISystem.cs:144 | LOCAL-COSMETIC | clica notificação, dispara callback local |

## menu (Menu/MenuUISystem.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 31 | setActiveScreen | Menu/MenuUISystem.cs:539 | LOCAL-COSMETIC | navega tela do menu |
| 32 | continueGame | Menu/MenuUISystem.cs:540 | MUTA-MUNDO | carrega o último savegame |
| 33 | newGame | Menu/MenuUISystem.cs:570 | MUTA-MUNDO | cria uma cidade nova |
| 34 | loadGame | Menu/MenuUISystem.cs:574 | MUTA-MUNDO | carrega savegame escolhido |
| 35 | saveGame | Menu/MenuUISystem.cs:578 | SÓ-LEITURA | lê estado, grava arquivo de save |
| 36 | deleteSave | Menu/MenuUISystem.cs:582 | LOCAL-COSMETIC | apaga arquivo de save |
| 37 | shareSave | Menu/MenuUISystem.cs:596 | LOCAL-COSMETIC | abre painel de upload do save |
| 38 | shareMap | Menu/MenuUISystem.cs:607 | LOCAL-COSMETIC | abre painel de upload do mapa |
| 39 | quicksave | Menu/MenuUISystem.cs:618 | SÓ-LEITURA | lê estado, salva rápido em arquivo |
| 40 | quickload | Menu/MenuUISystem.cs:656 | MUTA-MUNDO | recarrega o save mais recente |
| 41 | startEditor | Menu/MenuUISystem.cs:660 | MUTA-MUNDO | troca sessão para o modo editor |
| 42 | showPdxModsUI | Menu/MenuUISystem.cs:681 | LOCAL-COSMETIC | abre a loja de mods |
| 43 | exitToMainMenu | Menu/MenuUISystem.cs:719 | LOCAL-COSMETIC | encerra a sessão local de jogo |
| 44 | onSaveGameScreenVisibilityChanged | Menu/MenuUISystem.cs:735 | LOCAL-COSMETIC | gera preview de screenshot do save |
| 45 | applyTutorialSettings | Menu/MenuUISystem.cs:746 | LOCAL-COSMETIC | liga/desliga tutorial local |
| 46 | selectCloudTarget | Menu/MenuUISystem.cs:754 | LOCAL-COSMETIC | escolhe destino de nuvem local |
| 47 | selectMapFilter | Menu/MenuUISystem.cs:759 | LOCAL-COSMETIC | filtra lista de mapas exibida |

## chirper (InGame/ChirperUISystem.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 48 | addLike | InGame/ChirperUISystem.cs:132 | MUTA-MUNDO | curte chirp, seta `ChirpFlags.Liked` |
| 49 | removeLike | InGame/ChirperUISystem.cs:140 | MUTA-MUNDO | descurte chirp, limpa flag |
| 50 | selectLink | InGame/ChirperUISystem.cs:148 | LOCAL-COSMETIC | segue link de chirp/infoview |

## options (Menu/OptionsUISystem.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 51 | confirmDisplay | Menu/OptionsUISystem.cs:603 | LOCAL-COSMETIC | confirma resolução de tela |
| 52 | revertDisplay | Menu/OptionsUISystem.cs:607 | LOCAL-COSMETIC | reverte config de tela local |
| 53 | onOptionsPageClosed | Menu/OptionsUISystem.cs:615 | LOCAL-COSMETIC | telemetria ao fechar página |
| 54 | selectPage | Menu/OptionsUISystem.cs:622 | LOCAL-COSMETIC | navega página de opções |
| 55 | filteredWidgets | Menu/OptionsUISystem.cs:627 | LOCAL-COSMETIC | busca textual nas opções |
| 56 | toolchain.dependency.action | Menu/OptionsUISystem.cs:662 | LOCAL-COSMETIC | ação toolchain de dependência |
| 57 | cancelDirectoryBrowser | Menu/OptionsUISystem.cs:670 | LOCAL-COSMETIC | cancela seletor de pasta |

## Editor (kGroup "editor"/bottomBar, modo de jogo Editor — fora do escopo do coop de cidade)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 58 | setTimeOfDay | Editor/EditorBottomBarUISystem.cs:55 | LOCAL-COSMETIC | preview de luz no editor |
| 59 | resetTimeOfDay | Editor/EditorBottomBarUISystem.cs:56 | LOCAL-COSMETIC | reseta hora no editor |
| 60 | setDate | Editor/EditorBottomBarUISystem.cs:57 | LOCAL-COSMETIC | preview de data no editor |
| 61 | resetDate | Editor/EditorBottomBarUISystem.cs:58 | LOCAL-COSMETIC | reseta data no editor |
| 62 | setCloudiness | Editor/EditorBottomBarUISystem.cs:59 | LOCAL-COSMETIC | preview de nuvens no editor |
| 63 | resetCloudiness | Editor/EditorBottomBarUISystem.cs:60 | LOCAL-COSMETIC | reseta nuvens no editor |
| 64 | toggleCameraMode | Editor/EditorBottomBarUISystem.cs:61 | LOCAL-COSMETIC | troca modo de câmera do editor |

## paradox (Menu/ParadoxBindings.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 65 | linkAccount | Menu/ParadoxBindings.cs:356 | LOCAL-COSMETIC | vincula conta Paradox |
| 66 | unlinkAccount | Menu/ParadoxBindings.cs:357 | LOCAL-COSMETIC | desvincula conta Paradox |
| 67 | logout | Menu/ParadoxBindings.cs:358 | LOCAL-COSMETIC | logout de conta Paradox |
| 68 | closeActiveDialog | Menu/ParadoxBindings.cs:360 | LOCAL-COSMETIC | fecha diálogo Paradox ativo |
| 69 | showLoginForm | Menu/ParadoxBindings.cs:361 | LOCAL-COSMETIC | abre formulário de login |
| 70 | submitPasswordReset | Menu/ParadoxBindings.cs:362 | LOCAL-COSMETIC | envia reset de senha |
| 71 | submitLoginForm | Menu/ParadoxBindings.cs:363 | LOCAL-COSMETIC | envia formulário de login |
| 72 | showRegistrationForm | Menu/ParadoxBindings.cs:367 | LOCAL-COSMETIC | abre formulário de registro |
| 73 | showLink | Menu/ParadoxBindings.cs:368 | LOCAL-COSMETIC | abre link externo |
| 74 | submitRegistrationForm | Menu/ParadoxBindings.cs:369 | LOCAL-COSMETIC | envia formulário de registro |
| 75 | confirmAccountLink | Menu/ParadoxBindings.cs:370 | LOCAL-COSMETIC | confirma vínculo de conta |
| 76 | confirmAccountLinkOverwrite | Menu/ParadoxBindings.cs:371 | LOCAL-COSMETIC | confirma sobrescrever vínculo |
| 77 | markLegalDocumentAsViewed | Menu/ParadoxBindings.cs:372 | LOCAL-COSMETIC | marca termo legal como lido |
| 78 | showTermsOfUse | Menu/ParadoxBindings.cs:373 | LOCAL-COSMETIC | mostra termos de uso |
| 79 | showPrivacyPolicy | Menu/ParadoxBindings.cs:374 | LOCAL-COSMETIC | mostra política de privacidade |
| 80 | onOptionSelected | Menu/ParadoxBindings.cs:375 | LOCAL-COSMETIC | opção de diálogo Paradox |
| 81 | prerequisiteClicked | Menu/ParadoxBindings.cs:377 | LOCAL-COSMETIC | clique em pré-requisito de DLC |
| 82 | requestModDetail | Menu/ParadoxBindings.cs:379 | SÓ-LEITURA | pede detalhe de um mod |
| 83 | subscribeToMods | Menu/ParadoxBindings.cs:381 | LOCAL-COSMETIC | assina mods no Paradox Mods |

## whatsnew / assetUpload

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 84 | close | Menu/WhatsNewPanelUISystem.cs:47 | LOCAL-COSMETIC | fecha painel de novidades |
| 85 | close | Menu/StandaloneAssetUploadPanelUISystem.cs:31 | LOCAL-COSMETIC | fecha painel de upload de asset |

## editorHierarchy / editorPanel / editorTool / editor / editorTutorials (modo Editor)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 86 | setWidth | Editor/EditorHierarchyUISystem.cs:1584 | LOCAL-COSMETIC | redimensiona painel do editor |
| 87 | setHeight | Editor/EditorHierarchyUISystem.cs:1592 | LOCAL-COSMETIC | redimensiona painel do editor |
| 88 | setViewportRange | Editor/EditorHierarchyUISystem.cs:1600 | LOCAL-COSMETIC | scroll da lista de hierarquia |
| 89 | setSelectedId | Editor/EditorHierarchyUISystem.cs:1605 | LOCAL-COSMETIC | seleciona item no editor |
| 90 | onContextAction | Editor/EditorHierarchyUISystem.cs:1653 | LOCAL-COSMETIC | ação de contexto do editor |
| 91 | setExpanded | Editor/EditorHierarchyUISystem.cs:1712 | LOCAL-COSMETIC | expande nó da árvore |
| 92 | save | Editor/EditorHierarchyUISystem.cs:1728 | LOCAL-COSMETIC | abre painel salvar asset |
| 93 | locate | Editor/EditorHierarchyUISystem.cs:1734 | LOCAL-COSMETIC | câmera localiza item no editor |
| 94 | bulldoze | Editor/EditorHierarchyUISystem.cs:1739 | LOCAL-COSMETIC | apaga entidade dentro do editor |
| 95 | setSearchQuery | Editor/EditorHierarchyUISystem.cs:1752 | LOCAL-COSMETIC | busca textual na hierarquia |
| 96 | cancel | Editor/EditorPanelUISystem.cs:72 | LOCAL-COSMETIC | cancela painel do editor |
| 97 | close | Editor/EditorPanelUISystem.cs:73 | LOCAL-COSMETIC | fecha painel do editor |
| 98 | setWidth | Editor/EditorPanelUISystem.cs:74 | LOCAL-COSMETIC | redimensiona painel do editor |
| 99 | setAdvisorHeight | Editor/EditorPanelUISystem.cs:75 | LOCAL-COSMETIC | redimensiona painel do assessor |
| 101 | setActiveScreen | Editor/EditorScreenUISystem.cs:37 | LOCAL-COSMETIC | navega tela do editor |
| 117 | selectTool | Editor/EditorToolUISystem.cs:89 | LOCAL-COSMETIC | seleciona ferramenta do editor |
| 118 | completeListIntro | Editor/EditorTutorialsUISystem.cs:188 | LOCAL-COSMETIC | completa intro do tutorial do editor |
| 119 | toggleTutorials | Editor/EditorTutorialsUISystem.cs:193 | LOCAL-COSMETIC | liga/desliga tutoriais do editor |

## user (Menu/UserBindings.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 100 | switchUser | Menu/UserBindings.cs:62 | LOCAL-COSMETIC | troca usuário da plataforma |

## kGroup "cinematicCamera" (InGame/CinematicCameraUISystem.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 102 | setPlaybackDuration | InGame/CinematicCameraUISystem.cs:101 | LOCAL-COSMETIC | duração da sequência cinemática |
| 103 | setTimelinePosition | InGame/CinematicCameraUISystem.cs:105 | LOCAL-COSMETIC | posiciona timeline cinemática |
| 104 | togglePlayback | InGame/CinematicCameraUISystem.cs:111 | LOCAL-COSMETIC | play/pause da câmera cinemática |
| 105 | stopPlayback | InGame/CinematicCameraUISystem.cs:119 | LOCAL-COSMETIC | para a câmera cinemática |
| 106 | captureKey | InGame/CinematicCameraUISystem.cs:125 | LOCAL-COSMETIC | captura keyframe de câmera |
| 107 | removeCameraTransformKey | InGame/CinematicCameraUISystem.cs:141 | LOCAL-COSMETIC | remove keyframe de transform |
| 108 | removeKeyFrame | InGame/CinematicCameraUISystem.cs:154 | LOCAL-COSMETIC | remove keyframe genérico |
| 109 | reset | InGame/CinematicCameraUISystem.cs:182 | LOCAL-COSMETIC | reseta sequência cinemática |
| 110 | toggleLoop | InGame/CinematicCameraUISystem.cs:189 | LOCAL-COSMETIC | liga loop da sequência |
| 111 | toggleCurveEditorFocus | InGame/CinematicCameraUISystem.cs:209 | LOCAL-COSMETIC | foco no editor de curvas |
| 112 | onAfterPlaybackDurationChange | InGame/CinematicCameraUISystem.cs:215 | LOCAL-COSMETIC | pós-ajuste de duração |
| 113 | save | InGame/CinematicCameraUISystem.cs:222 | LOCAL-COSMETIC | salva asset de câmera local |
| 114 | load | InGame/CinematicCameraUISystem.cs:255 | LOCAL-COSMETIC | carrega asset de câmera local |
| 115 | delete | InGame/CinematicCameraUISystem.cs:274 | LOCAL-COSMETIC | apaga asset de câmera local |
| 116 | selectCloudTarget | InGame/CinematicCameraUISystem.cs:284 | LOCAL-COSMETIC | escolhe nuvem para asset |

## InGame — seções de painel de entidade selecionada (ActionsSection, ColorSection, DistrictsSection, DevTreeUISystem, DestroyedBuildingSection, LinesSection, ScheduleSection, ServiceBudgetUISystem, SignatureBuildingUISystem, TitleSection, TicketPriceSection, VehicleCountSection, UpgradesSection, UpgradeMenuUISystem, SelectVehiclesSection)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 16 | focus | InGame/ActionsSection.cs:159 | LOCAL-COSMETIC | foca câmera na entidade |
| 17 | toggleMove | InGame/ActionsSection.cs:163 | LOCAL-COSMETIC | ativa modo mover objeto |
| 18 | follow | InGame/ActionsSection.cs:178 | LOCAL-COSMETIC | câmera segue cidadão selecionado |
| 19 | delete | InGame/ActionsSection.cs:191 | MUTA-MUNDO | marca `Deleted`, remove entidade |
| 20 | toggle | InGame/ActionsSection.cs:206 | MUTA-MUNDO | liga/desliga política fora-de-serviço |
| 21 | toggleEmptying | InGame/ActionsSection.cs:217 | MUTA-MUNDO | política de esvaziar aterro |
| 22 | toggleLotTool | InGame/ActionsSection.cs:221 | LOCAL-COSMETIC | ativa ferramenta de lote |
| 23 | toggleTrafficRoutes | InGame/ActionsSection.cs:234 | LOCAL-COSMETIC | mostra overlay de rotas de tráfego |
| 120 | setColor | InGame/ColorSection.cs:32 | MUTA-MUNDO | cor da linha de transporte |
| 121 | removeDistrict | InGame/DistrictsSection.cs:79 | MUTA-MUNDO | remove vínculo prédio-distrito de serviço |
| 122 | toggleSelectionTool | InGame/DistrictsSection.cs:100 | LOCAL-COSMETIC | ativa ferramenta de seleção de distrito |
| 123 | toggleDistrictTool | InGame/DistrictsSection.cs:113 | LOCAL-COSMETIC | ativa ferramenta de pintar distrito |
| 124 | disableTool | InGame/DistrictsSection.cs:126 | LOCAL-COSMETIC | desativa ferramenta ativa |
| 125 | purchaseNode | InGame/DevTreeUISystem.cs:290 | MUTA-MUNDO | compra nó da árvore de progressão |
| 126 | toggleRebuild | InGame/DestroyedBuildingSection.cs:71 | LOCAL-COSMETIC | ativa ferramenta de reconstrução |
| 146 | toggle | InGame/LinesSection.cs:234 | MUTA-MUNDO | liga/desliga linha de transporte |
| 170 | setSchedule | InGame/ScheduleSection.cs:37 | MUTA-MUNDO | política de horário dia/noite da linha |
| 171 | setServiceBudget | InGame/ServiceBudgetUISystem.cs:155 | MUTA-MUNDO | ajusta % do orçamento do serviço |
| 172 | setServiceFee | InGame/ServiceBudgetUISystem.cs:159 | MUTA-MUNDO | ajusta tarifa (fee) do serviço |
| 173 | resetService | InGame/ServiceBudgetUISystem.cs:167 | MUTA-MUNDO | reseta orçamento e tarifas |
| 174 | removeUnlockedSignature | InGame/SignatureBuildingUISystem.cs:39 | LOCAL-COSMETIC | fecha popup de marco de assinatura |
| 190 | renameEntity | InGame/TitleSection.cs:40 | MUTA-MUNDO | renomeia a entidade selecionada |
| 191 | setTicketPrice | InGame/TicketPriceSection.cs:37 | MUTA-MUNDO | define preço de passagem |
| 178 | selectVehicles | InGame/SelectVehiclesSection.cs:218 | MUTA-MUNDO | define modelo de veículo do depósito |
| 179 | deselectVehicles | InGame/SelectVehiclesSection.cs:270 | MUTA-MUNDO | remove modelo de veículo do depósito |
| 243 | setVehicleCount | InGame/VehicleCountSection.cs:248 | MUTA-MUNDO | ajusta contagem de veículos da linha |
| 244 | delete | InGame/UpgradesSection.cs:52 | MUTA-MUNDO | apaga extensão/prédio |
| 245 | relocate | InGame/UpgradesSection.cs:60 | LOCAL-COSMETIC | ativa modo mover extensão |
| 246 | focus | InGame/UpgradesSection.cs:65 | LOCAL-COSMETIC | câmera foca extensão |
| 247 | toggle | InGame/UpgradesSection.cs:70 | MUTA-MUNDO | liga/desliga extensão ativa |
| 248 | selectUpgrade | InGame/UpgradeMenuUISystem.cs:208 | LOCAL-COSMETIC | ativa ferramenta de colocar upgrade |
| 249 | clearUpgradeSelection | InGame/UpgradeMenuUISystem.cs:245 | LOCAL-COSMETIC | cancela seleção de upgrade |

## eventJournal, game, tutorials, infoviews, lifePath (InGame)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 127 | openJournal | InGame/EventJournalUISystem.cs:50 | LOCAL-COSMETIC | abre diário de eventos |
| 128 | closeJournal | InGame/EventJournalUISystem.cs:54 | LOCAL-COSMETIC | fecha diário de eventos |
| 129 | setActiveScreen | InGame/GameScreenUISystem.cs:55 | LOCAL-COSMETIC | navega tela pausa/jogo |
| 130 | togglePanel | InGame/GamePanelUISystem.cs:90 | LOCAL-COSMETIC | abre/fecha painel de jogo |
| 131 | showPanel | InGame/GamePanelUISystem.cs:103 | LOCAL-COSMETIC | mostra painel por nome |
| 132 | closePanel | InGame/GamePanelUISystem.cs:110 | LOCAL-COSMETIC | fecha painel por nome |
| 133 | closeActivePanel | InGame/GamePanelUISystem.cs:119 | LOCAL-COSMETIC | fecha painel ativo |
| 134 | showProgressionPanel | InGame/GamePanelUISystem.cs:219 | LOCAL-COSMETIC | abre painel de progressão |
| 135 | showEconomyPanel | InGame/GamePanelUISystem.cs:221 | LOCAL-COSMETIC | abre painel de economia |
| 136 | showCityInfoPanel | InGame/GamePanelUISystem.cs:223 | LOCAL-COSMETIC | abre painel de info da cidade |
| 137 | showTransportationOverviewPanel | InGame/GamePanelUISystem.cs:226 | LOCAL-COSMETIC | abre painel de visão de transporte |
| 138 | showLifePathDetail | InGame/GamePanelUISystem.cs:229 | LOCAL-COSMETIC | abre detalhe de vida do cidadão |
| 139 | completeListIntro | InGame/GameTutorialsUISystem.cs:28 | LOCAL-COSMETIC | completa lista intro do tutorial |
| 140 | completeListOutro | InGame/GameTutorialsUISystem.cs:32 | LOCAL-COSMETIC | completa lista outro do tutorial |
| 141 | completeIntro | InGame/GameTutorialsUISystem.cs:36 | LOCAL-COSMETIC | completa intro, liga tutorial |
| 142 | setActiveInfoview | InGame/InfoviewsUISystem.cs:123 | LOCAL-COSMETIC | troca overlay de infoview (visão local) |
| 143 | setInfomodeActive | InGame/InfoviewsUISystem.cs:132 | LOCAL-COSMETIC | liga submodo de infoview |
| 144 | followCitizen | InGame/LifePathUISystem.cs:105 | LOCAL-COSMETIC | câmera segue cidadão |
| 145 | unfollowCitizen | InGame/LifePathUISystem.cs:109 | LOCAL-COSMETIC | para de seguir cidadão |

## loan, mapTiles, milestone, photoMode, policies, production, radio (InGame)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 147 | requestLoanOffer | InGame/LoanUISystem.cs:45 | SÓ-LEITURA | calcula oferta de empréstimo (preview) |
| 148 | acceptLoanOffer | InGame/LoanUISystem.cs:46 | MUTA-MUNDO | efetiva mudança no empréstimo |
| 149 | resetLoanOffer | InGame/LoanUISystem.cs:47 | SÓ-LEITURA | descarta oferta calculada |
| 150 | setMapTileViewActive | InGame/MapTilesUISystem.cs:116 | LOCAL-COSMETIC | ativa visão de tiles do mapa |
| 151 | purchaseMapTiles | InGame/MapTilesUISystem.cs:131 | MUTA-MUNDO | compra os tiles selecionados |
| 152 | clearUnlockedMilestone | InGame/MilestoneUISystem.cs:316 | LOCAL-COSMETIC | fecha popup de marco desbloqueado |
| 153 | selectTab | InGame/PhotoModeUISystem.cs:122 | LOCAL-COSMETIC | troca aba do modo foto |
| 154 | setCinematicCameraVisible | InGame/PhotoModeUISystem.cs:123 | LOCAL-COSMETIC | mostra painel de câmera cinemática |
| 155 | setOverlayHidden | InGame/PhotoModeUISystem.cs:130 | LOCAL-COSMETIC | esconde overlay do modo foto |
| 156 | takeScreenshot | InGame/PhotoModeUISystem.cs:131 | LOCAL-COSMETIC | tira screenshot local |
| 157 | toggleOrbitCameraActive | InGame/PhotoModeUISystem.cs:132 | LOCAL-COSMETIC | liga câmera orbital do modo foto |
| 158 | setPolicy | InGame/PoliciesUISystem.cs:174 | MUTA-MUNDO | ativa política na entidade selecionada |
| 159 | setCityPolicy | InGame/PoliciesUISystem.cs:178 | MUTA-MUNDO | ativa política em nível de cidade |
| 160 | selectResource | InGame/ProductionCompanyUISystem.cs:224 | LOCAL-COSMETIC | filtra recurso exibido no painel |
| 161 | setVolume | InGame/RadioUISystem.cs:88 | LOCAL-COSMETIC | ajusta volume do rádio |
| 162 | setPaused | InGame/RadioUISystem.cs:89 | LOCAL-COSMETIC | pausa rádio local |
| 163 | setMuted | InGame/RadioUISystem.cs:90 | LOCAL-COSMETIC | muta rádio local |
| 164 | setSkipAds | InGame/RadioUISystem.cs:91 | LOCAL-COSMETIC | pula anúncios do rádio |
| 165 | playPrevious | InGame/RadioUISystem.cs:92 | LOCAL-COSMETIC | faixa anterior do rádio |
| 166 | playNext | InGame/RadioUISystem.cs:93 | LOCAL-COSMETIC | próxima faixa do rádio |
| 167 | focusEmergency | InGame/RadioUISystem.cs:94 | LOCAL-COSMETIC | câmera foca emergência do rádio |
| 168 | selectNetwork | InGame/RadioUISystem.cs:95 | LOCAL-COSMETIC | troca rede de rádio |
| 169 | selectStation | InGame/RadioUISystem.cs:96 | LOCAL-COSMETIC | troca estação de rádio |

## selectedInfo, statistics, time, taxation, toolbarBottom (InGame)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 175 | selectEntity | InGame/SelectedInfoUISystem.cs:196 | LOCAL-COSMETIC | seleciona entidade para painel de info |
| 176 | clearSelection | InGame/SelectedInfoUISystem.cs:210 | LOCAL-COSMETIC | limpa seleção de info |
| 177 | setSelectedRoute | InGame/SelectedInfoUISystem.cs:214 | LOCAL-COSMETIC | seleciona rota associada |
| 180 | addStat | InGame/StatisticsUISystem.cs:335 | LOCAL-COSMETIC | adiciona série ao gráfico local |
| 181 | addStatChildren | InGame/StatisticsUISystem.cs:363 | LOCAL-COSMETIC | expande subgrupo do gráfico local |
| 182 | removeStat | InGame/StatisticsUISystem.cs:376 | LOCAL-COSMETIC | remove série do gráfico local |
| 183 | clearStats | InGame/StatisticsUISystem.cs:418 | LOCAL-COSMETIC | limpa séries do gráfico local |
| 184 | setSampleRange | InGame/StatisticsUISystem.cs:425 | LOCAL-COSMETIC | período de amostragem do gráfico |
| 185 | setSimulationPaused | InGame/TimeUISystem.cs:117 | MUTA-MUNDO | pausa a simulação compartilhada |
| 186 | setSimulationSpeed | InGame/TimeUISystem.cs:128 | MUTA-MUNDO | altera velocidade de simulação compartilhada |
| 187 | setTaxRate | InGame/TaxationUISystem.cs:189 | MUTA-MUNDO | define alíquota geral de imposto |
| 188 | setAreaTaxRate | InGame/TaxationUISystem.cs:198 | MUTA-MUNDO | define alíquota por tipo de zona |
| 189 | setResourceTaxRate | InGame/TaxationUISystem.cs:206 | MUTA-MUNDO | define alíquota por recurso |
| 192 | setCityName | InGame/ToolbarBottomUISystem.cs:93 | MUTA-MUNDO | renomeia a cidade |

## toolbar, tool (InGame/ToolbarUISystem.cs, InGame/ToolUISystem.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 193 | setSelectedThemes | InGame/ToolbarUISystem.cs:390 | LOCAL-COSMETIC | filtra temas no seletor de asset |
| 194 | setSelectedAssetPacks | InGame/ToolbarUISystem.cs:406 | LOCAL-COSMETIC | filtra packs no seletor de asset |
| 195 | selectAssetMenu | InGame/ToolbarUISystem.cs:422 | LOCAL-COSMETIC | abre menu de assets |
| 196 | selectAssetCategory | InGame/ToolbarUISystem.cs:449 | LOCAL-COSMETIC | abre categoria de assets |
| 197 | selectAsset | InGame/ToolbarUISystem.cs:474 | LOCAL-COSMETIC | escolhe prefab a construir |
| 198 | clearAssetSelection | InGame/ToolbarUISystem.cs:507 | LOCAL-COSMETIC | limpa seleção de asset |
| 199 | toggleToolOptions | InGame/ToolbarUISystem.cs:511 | LOCAL-COSMETIC | abre opções da ferramenta ativa |
| 200 | setAgeMask | InGame/ToolbarUISystem.cs:515 | LOCAL-COSMETIC | filtra idade do prefab a colocar |
| 201 | setWaterSimSpeed | InGame/ToolUISystem.cs:160 | LOCAL-COSMETIC | velocidade de preview de água |
| 202 | setWaterSimulateBackdrop | InGame/ToolUISystem.cs:161 | LOCAL-COSMETIC | simula água de fundo (preview) |
| 203 | setWaterShowSourceNames | InGame/ToolUISystem.cs:162 | LOCAL-COSMETIC | mostra nomes de fonte de água |
| 204 | selectTool | InGame/ToolUISystem.cs:163 | LOCAL-COSMETIC | ativa ferramenta por id |
| 205 | selectToolMode | InGame/ToolUISystem.cs:164 | LOCAL-COSMETIC | muda submodo da ferramenta |
| 206 | setSelectedSnapMask | InGame/ToolUISystem.cs:165 | LOCAL-COSMETIC | configura máscara de snap |
| 207 | elevationUp | InGame/ToolUISystem.cs:166 | LOCAL-COSMETIC | sobe elevação da ferramenta |
| 208 | elevationDown | InGame/ToolUISystem.cs:167 | LOCAL-COSMETIC | desce elevação da ferramenta |
| 209 | elevationScroll | InGame/ToolUISystem.cs:168 | LOCAL-COSMETIC | rola elevação via scroll |
| 210 | setElevationStep | InGame/ToolUISystem.cs:169 | LOCAL-COSMETIC | define passo de elevação |
| 211 | toggleParallelMode | InGame/ToolUISystem.cs:170 | LOCAL-COSMETIC | liga modo de construção paralela |
| 212 | setParallelOffset | InGame/ToolUISystem.cs:171 | LOCAL-COSMETIC | define offset paralelo |
| 213 | setUndergroundMode | InGame/ToolUISystem.cs:172 | LOCAL-COSMETIC | liga modo subterrâneo da ferramenta |
| 214 | setDistance | InGame/ToolUISystem.cs:173 | LOCAL-COSMETIC | espaçamento entre objetos a colocar |
| 215 | selectBrush | InGame/ToolUISystem.cs:185 | LOCAL-COSMETIC | escolhe tipo de pincel de terreno |
| 216 | setBrushHeight | InGame/ToolUISystem.cs:186 | LOCAL-COSMETIC | altura do pincel de terreno |
| 217 | setBrushSize | InGame/ToolUISystem.cs:187 | LOCAL-COSMETIC | tamanho do pincel |
| 218 | setBrushStrength | InGame/ToolUISystem.cs:188 | LOCAL-COSMETIC | força do pincel |
| 219 | setBrushAngle | InGame/ToolUISystem.cs:189 | LOCAL-COSMETIC | ângulo do pincel |
| 220 | setColor | InGame/ToolUISystem.cs:190 | LOCAL-COSMETIC | cor do próximo objeto a colocar |
| 221 | setWaterSimSpeed (dup) | InGame/ToolUISystem.cs:193 | LOCAL-COSMETIC | duplicata: velocidade de sim. de água |
| 222 | setShowWaterSourceNames | InGame/ToolUISystem.cs:198 | LOCAL-COSMETIC | duplicata: nomes de fonte de água |
| 223 | setSimulateBackdropWater | InGame/ToolUISystem.cs:203 | LOCAL-COSMETIC | duplicata: simula água de fundo |

## transportationOverview (InGame/TransportationOverviewUISystem.cs)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 224 | delete | InGame/TransportationOverviewUISystem.cs:138 | MUTA-MUNDO | apaga a linha de transporte |
| 225 | select | InGame/TransportationOverviewUISystem.cs:145 | LOCAL-COSMETIC | seleciona linha na lista |
| 226 | setColor | InGame/TransportationOverviewUISystem.cs:152 | MUTA-MUNDO | cor da linha e de seus veículos |
| 227 | rename | InGame/TransportationOverviewUISystem.cs:170 | MUTA-MUNDO | renomeia a linha de transporte |
| 228 | setActive | InGame/TransportationOverviewUISystem.cs:178 | MUTA-MUNDO | ativa/desativa a linha (política) |
| 229 | showLine | InGame/TransportationOverviewUISystem.cs:186 | MUTA-MUNDO | remove flag `HiddenRoute` da linha |
| 230 | hideLine | InGame/TransportationOverviewUISystem.cs:199 | MUTA-MUNDO | marca a linha com `HiddenRoute` |
| 231 | setSchedule | InGame/TransportationOverviewUISystem.cs:212 | MUTA-MUNDO | horário dia/noite da linha (política) |
| 232 | resetVisibility | InGame/TransportationOverviewUISystem.cs:234 | MUTA-MUNDO | limpa oculto/destaque de todas as linhas |
| 233 | toggleHighlight | InGame/TransportationOverviewUISystem.cs:241 | LOCAL-COSMETIC | realça linha na lista (overlay) |
| 234 | setSelectedPassengerType | InGame/TransportationOverviewUISystem.cs:256 | LOCAL-COSMETIC | filtra tipo de passageiro exibido |
| 235 | setSelectedCargoType | InGame/TransportationOverviewUISystem.cs:260 | LOCAL-COSMETIC | filtra tipo de carga exibido |

## tutorials (InGame/TutorialsUISystem.cs — base de GameTutorialsUISystem)

| # | Nome | Arquivo:Linha | Classe | Justificativa |
|---|------|---------------|--------|----------------|
| 236 | activateTutorial | InGame/TutorialsUISystem.cs:191 | LOCAL-COSMETIC | ativa tutorial específico |
| 237 | activateTutorialPhase | InGame/TutorialsUISystem.cs:195 | LOCAL-COSMETIC | ativa fase do tutorial |
| 238 | forceTutorial | InGame/TutorialsUISystem.cs:199 | LOCAL-COSMETIC | força tutorial via advisor |
| 239 | completeActiveTutorialPhase | InGame/TutorialsUISystem.cs:203 | LOCAL-COSMETIC | completa fase ativa do tutorial |
| 240 | completeActiveTutorial | InGame/TutorialsUISystem.cs:210 | LOCAL-COSMETIC | completa o tutorial ativo |
| 241 | setTutorialTagActive | InGame/TutorialsUISystem.cs:214 | LOCAL-COSMETIC | ativa/desativa tag de tutorial |
| 242 | activateTutorialTrigger | InGame/TutorialsUISystem.cs:222 | LOCAL-COSMETIC | dispara gatilho de tutorial |

---

## Contagem final

- **249** bindings enumerados (bate com `grep -c 'AddBinding(new TriggerBinding' decomp/Game/Game/UI -r` = 249, 61 arquivos; conferido linha a linha, sem duplicata/lacuna no índice 1-249).
- **MUTA-MUNDO: 43** — o checklist de cobertura do coop (ver dossiê, seção 6).
- **SÓ-LEITURA: 5** (`saveGame`, `quicksave`, `requestModDetail`, `requestLoanOffer`, `resetLoanOffer`).
- **LOCAL-COSMETIC: 201** — câmera/foto/rádio/tutorial/menu/conta Paradox/config de ferramenta/editor de mapa.
