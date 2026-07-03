import {getModule, ModRegistrar} from "cs2/modding";
import {MenuUIExtensions, PauseMenuCSMExtend} from "extends/main-menu";
import {JoinGameMenu} from "./screens/join-game-menu";
import {HostGameMenu} from "./screens/host-game-menu";
import {ChatIcon, ChatPanel} from "./screens/chat";
import {CursorLabels} from "./screens/cursor-labels";
import {SyncBadge} from "./screens/sync-badge";
import {PlayerPanel} from "./screens/player-panel";

const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.extend('game-ui/common/input/button/labeled-icon-button.tsx', 'LabeledIconButton', PauseMenuCSMExtend);
    moduleRegistry.add('cs2m/screens/join-game-menu.tsx', JoinGameMenu);
    moduleRegistry.add('cs2m/screens/host-game-menu.tsx', HostGameMenu);

    moduleRegistry.extend('game-ui/common/animations/transition-group-coordinator.tsx', 'TransitionGroupCoordinator', MenuUIExtensions);

    moduleRegistry.append('GameBottomRight', ChatIcon);
    // 'Game' is the fullscreen slot (direct child of the 100%x100% game screen) — the labels layer
    // needs viewport-anchored coordinates; GameBottomRight is a narrow non-positioned strip.
    moduleRegistry.append('Game', CursorLabels);
    moduleRegistry.append('Game', SyncBadge);
    moduleRegistry.append('Game', PlayerPanel);
    getModule('game-ui/game/components/game-panel-renderer.tsx', 'gamePanelComponents')['CS2M.UI.ChatPanel'] = ChatPanel;
}

export default register;
