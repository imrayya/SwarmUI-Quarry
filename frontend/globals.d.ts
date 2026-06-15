declare function registerNewTool(id: string, name: string): HTMLElement;

declare function genericRequest<T = unknown>(
    endpoint: string,
    data: Record<string, unknown>,
    callback: (data: T) => void,
): void;

interface QuarryJQuery {
    modal(action: "show" | "hide"): void;
}

declare function $(selector: string | Element): QuarryJQuery;

// --- SwarmUI core globals used by the Quarry browser tab ---

declare function getRequiredElementById(id: string): HTMLElement;
declare function triggerChangeFor(elem: HTMLElement): void;
declare function trimSpaces(text: string): string;
declare function regexEscape(text: string): string;
declare function copyText(text: string): void;

// Set true once the websocket session is established and sessionReadyCallbacks have fired (main.js).
declare const swarmHasLoaded: boolean;
// Callbacks run once, right after the session is ready (main.js).
declare const sessionReadyCallbacks: Array<() => void>;

declare const uiImprover: {
    getLastSelectedTextbox(): [HTMLTextAreaElement | null, number];
};

declare const browserUtil: {
    makeVisible(elem: Element | Document): void;
};

interface GenTabLayoutLike {
    managedTabs: MovableGenTab[];
    managedTabContainers: Element[];
    reapplyPositions(): void;
}

// The generate tab's layout manager (layout.js); constructed before extension scripts run.
declare const genTabLayout: GenTabLayoutLike;

// One movable sub-tab; constructing it wires up the custom (non-bootstrap) click handling.
declare class MovableGenTab {
    constructor(navLink: Element, handler: GenTabLayoutLike);
    contentElem: HTMLElement;
    navElem: HTMLElement;
    update(): void;
}

// A browsable item handed to GenPageBrowserClass. The browser stamps `name` onto each card's data-name and
// reads `data.src`; our code only ever reads `name` back off it.
interface QuarryBrowserFile {
    name: string;
    data: Record<string, unknown>;
}

// SwarmUI's reusable folder/file browser (browsers.js) — the same component that powers the Wildcards tab.
declare class GenPageBrowserClass {
    constructor(
        container: string,
        listFoldersAndFiles: (
            path: string,
            isRefresh: boolean,
            callback: (folders: string[], files: QuarryBrowserFile[]) => void,
            depth: number,
        ) => void,
        id: string,
        defaultFormat: string,
        describe: (file: QuarryBrowserFile) => unknown,
        select: (file: QuarryBrowserFile, div: HTMLElement | null) => void,
        extraHeader?: string,
        defaultDepth?: number,
    );
    contentDiv: HTMLElement;
    builtEvent: (() => void) | null;
    refreshHandler: (callback: () => void) => void;
    navigate(folder: string, callback?: (() => void) | null): void;
    refresh(): void;
}
