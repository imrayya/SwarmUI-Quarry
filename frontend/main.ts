import { quarryBrowser } from "./browser";
import { startPromptWatcher } from "./prompt";
import { quarry } from "./settings";

// Inject the bottom-bar tab now, synchronously: genTabLayout.init() (scheduled by finalscript.js, which loads
// right after this script) scans the tab list once, so the tab must already be in the DOM by then.
quarryBrowser.injectTab();

const boot = (): void => {
    quarry.init();
    startPromptWatcher();
};

if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
} else {
    boot();
}
