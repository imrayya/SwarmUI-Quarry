// The "Quarry" bottom-bar tab: a file browser over Quarry datasets, modeled on SwarmUI's Wildcards tab but
// served entirely from our own data (no Wildcards-folder placeholders). Clicking a dataset inserts `<q:NAME>`
// into the prompt; datasets referenced by the current prompt are highlighted, including filtered references
// like `<q:NAME[tags=girl]>`.

import { insertQuarryTag, onReferences, recomputeReferences } from "./prompt";
import { escapeHtml, formatRowCount, openPreview } from "./settings";
import type { DatasetDto, SettingsResponse } from "./types";

const TAB_ID = "Quarry-Tab";
const LIST_ID = "quarry_list";
const BROWSER_ID = "quarrybrowser";
// Datasets have no thumbnail of their own; reuse SwarmUI's generic model placeholder.
const PLACEHOLDER_IMAGE = "/imgs/model_placeholder.jpg";

let datasets: DatasetDto[] = [];
let datasetByName: Record<string, DatasetDto> = {};
let browser: GenPageBrowserClass | null = null;
// Lowercased names of datasets referenced by the current prompt (kept in sync via the shared prompt watcher).
let referencedNames = new Set<string>();

const setDatasets = (list: DatasetDto[]): void => {
    datasets = list;
    datasetByName = {};
    for (const dataset of list) {
        datasetByName[dataset.name] = dataset;
    }
};

const fetchDatasets = (done: () => void): void => {
    genericRequest<SettingsResponse>("QuarryGetSettings", {}, (data) => {
        if (data.success) {
            setDatasets(data.datasets ?? []);
        }
        done();
    });
};

/// Splits the flat dataset-name list into the folders and files visible at `path`, matching SwarmUI's
/// ListModels semantics exactly: a file shows when its depth below `path` is `< depth`, and every ancestor
/// folder up to `depth` levels deep is surfaced. Folder names are relative to `path`; file names are full.
const computeFoldersAndFiles = (
    allNames: string[],
    path: string,
    depth: number,
): { folders: string[]; files: string[] } => {
    const clampedDepth = Math.max(1, Math.min(20, Math.round(depth)));
    const prefix = path === "" ? "" : `${path.replace(/\/+$/, "")}/`;
    const folders = new Set<string>();
    const files: string[] = [];
    const seen = new Set<string>();
    for (const name of allNames) {
        if (!name.startsWith(prefix) || name.length <= prefix.length) {
            continue;
        }
        const part = name.substring(prefix.length);
        const slashes = (part.match(/\//g) ?? []).length;
        if (slashes > 0) {
            const folderPart = part.substring(0, part.lastIndexOf("/"));
            const subfolders = folderPart.split("/");
            for (let i = 1; i <= clampedDepth && i <= subfolders.length; i++) {
                folders.add(subfolders.slice(0, i).join("/"));
            }
        }
        if (slashes < clampedDepth && !seen.has(name)) {
            seen.add(name);
            files.push(name);
        }
    }
    return { folders: [...folders], files };
};

const listQuarryFoldersAndFiles = (
    path: string,
    _isRefresh: boolean,
    callback: (folders: string[], files: QuarryBrowserFile[]) => void,
    depth: number,
): void => {
    // A disk re-scan is handled by refreshHandler below; here we only pull the list once, on first use.
    const run = (): void => {
        const { folders, files } = computeFoldersAndFiles(
            datasets.map((dataset) => dataset.name),
            path,
            depth,
        );
        const prefix =
            path === "" ? "" : path.endsWith("/") ? path : `${path}/`;
        const fileObjs: QuarryBrowserFile[] = files.map((name) => ({
            name,
            data: {
                ...datasetByName[name],
                display: name.substring(prefix.length),
                image: PLACEHOLDER_IMAGE,
                src: "",
            },
        }));
        callback(
            folders.sort((a, b) => a.localeCompare(b)),
            fileObjs,
        );
    };
    if (datasets.length === 0) {
        fetchDatasets(run);
    } else {
        run();
    }
};

const describeQuarry = (file: QuarryBrowserFile): unknown => {
    const name = file.name;
    const dataset = datasetByName[name];
    const display = name.replaceAll("/", " / ");
    const className = referencedNames.has(name.toLowerCase())
        ? "model-selected"
        : "";
    if (!dataset) {
        return {
            name,
            description: escapeHtml(name),
            buttons: [],
            className,
            searchable: name,
            image: PLACEHOLDER_IMAGE,
            display,
        };
    }
    const cols = (dataset.columns ?? [])
        .map((col) => `${col.name}${col.kind === "list" ? " [list]" : ""}`)
        .join(", ");
    const buttons: Array<{ label: string; onclick: () => void }> = [];
    let description: string;
    if (dataset.error) {
        description = `<span class="quarry-card-title">${escapeHtml(name)}</span><br><span class="quarry-card-error">⚠️ ${escapeHtml(dataset.error)}</span>`;
    } else {
        const meta: string[] = [];
        if (dataset.rowCount != null) {
            meta.push(`${formatRowCount(dataset.rowCount)} rows`);
        }
        if (dataset.resolvedPromptColumn) {
            meta.push(`prompt: ${escapeHtml(dataset.resolvedPromptColumn)}`);
        }
        const lines = [
            `<span class="quarry-card-title">${escapeHtml(name)}</span>`,
            `<span class="quarry-card-meta">${meta.join(" · ")}</span>`,
        ];
        if (cols) {
            lines.push(
                `<span class="quarry-card-cols">${escapeHtml(cols)}</span>`,
            );
        }
        description = lines.join("<br>");
        buttons.push({ label: "Preview", onclick: () => openPreview(name) });
    }
    buttons.push({
        label: "Copy reference",
        onclick: () => copyText(`<q:${name}>`),
    });
    return {
        name,
        description,
        buttons,
        className,
        searchable: `${name}, ${cols}`,
        image: PLACEHOLDER_IMAGE,
        display,
        detail_list: [escapeHtml(display), escapeHtml(cols)],
    };
};

const selectQuarry = (file: QuarryBrowserFile): void => {
    insertQuarryTag(file.name);
};

/// Toggles the in-prompt highlight on each visible card to match the referenced set.
const refreshCardHighlights = (): void => {
    if (!browser?.contentDiv) {
        return;
    }
    for (const child of Array.from(browser.contentDiv.children)) {
        const cardName = (child as HTMLElement).dataset?.name;
        if (cardName) {
            child.classList.toggle(
                "model-selected",
                referencedNames.has(cardName.toLowerCase()),
            );
        }
    }
};

const createBrowser = (): void => {
    browser = new GenPageBrowserClass(
        LIST_ID,
        listQuarryFoldersAndFiles,
        BROWSER_ID,
        "Small Cards",
        describeQuarry,
        selectQuarry,
        "",
    );
    // The refresh button re-scans the datasets folder on disk, then re-renders.
    browser.refreshHandler = (callback) => {
        genericRequest<SettingsResponse>("QuarryRefresh", {}, (data) => {
            if (data.success) {
                setDatasets(data.datasets ?? []);
                callback();
            } else {
                // Inactive (Quarry disabled / no folder) or error — fall back to the current server list.
                fetchDatasets(callback);
            }
        });
    };
    // After each (re)render, re-evaluate the prompt so freshly built cards pick up their highlight.
    browser.builtEvent = () => recomputeReferences();
    onReferences((names) => {
        referencedNames = new Set(names.map((name) => name.toLowerCase()));
        refreshCardHighlights();
    });
    // The settings panel fires this after a save/refresh so the browser reloads without a full page refresh.
    document.addEventListener("quarry:datasets-changed", () =>
        browser?.refresh(),
    );
    // Load the list once the session is ready (like SwarmUI's own model browsers). If we were injected after
    // that already happened, load immediately.
    if (typeof swarmHasLoaded !== "undefined" && swarmHasLoaded) {
        browser.navigate("");
    } else {
        sessionReadyCallbacks.push(() => browser?.navigate(""));
    }
};

/// Wires a MovableGenTab for our nav link so it gets SwarmUI's custom (non-bootstrap) tab behavior. Normally
/// runs before genTabLayout.init(), which then finalizes the tab; the post-init branch covers late injection.
const registerTabWithLayout = (navLink: HTMLElement): void => {
    if (typeof genTabLayout === "undefined" || !genTabLayout) {
        return;
    }
    const tab = new MovableGenTab(navLink, genTabLayout);
    genTabLayout.managedTabs.push(tab);
    if (genTabLayout.managedTabContainers.length > 0) {
        tab.contentElem.style.height = "100%";
        tab.contentElem.style.width = "100%";
        if (
            !genTabLayout.managedTabContainers.includes(
                tab.contentElem.parentElement,
            )
        ) {
            genTabLayout.managedTabContainers.push(
                tab.contentElem.parentElement,
            );
        }
        tab.update();
        tab.navElem.addEventListener("click", () =>
            browserUtil.makeVisible(tab.contentElem),
        );
        genTabLayout.reapplyPositions();
    }
};

/// Injects the Quarry tab into the bottom bar and builds its browser. Must run before genTabLayout.init()
/// (which scans the tab list) — main.ts calls this synchronously at script load, ahead of that.
const injectTab = (): void => {
    const nav = document.getElementById("bottombartabcollection");
    const content = document.getElementById("t2i_bottom_bar_content");
    if (!nav || !content || document.getElementById(TAB_ID)) {
        return;
    }
    const li = document.createElement("li");
    li.className = "nav-item";
    li.setAttribute("role", "presentation");
    li.innerHTML = `<a class="nav-link translate" data-bs-toggle="tab" href="#${TAB_ID}" aria-selected="false" tabindex="-1" role="tab">Quarry</a>`;
    // Sit next to Wildcards, just before the Tools tab.
    const toolsNav = nav.querySelector('a[href="#Tools-Tab"]');
    if (toolsNav?.parentElement) {
        nav.insertBefore(li, toolsNav.parentElement);
    } else {
        nav.appendChild(li);
    }
    const pane = document.createElement("div");
    pane.className = "tab-pane genpage-bottom-tab";
    pane.id = TAB_ID;
    pane.setAttribute("role", "tabpanel");
    pane.innerHTML = `<div class="browser_container" id="${LIST_ID}"></div>`;
    content.appendChild(pane);
    const navLink = li.querySelector("a");
    if (navLink) {
        registerTabWithLayout(navLink);
    }
    createBrowser();
};

export const quarryBrowser = {
    injectTab,
};
