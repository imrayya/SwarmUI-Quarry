import { setCompletionDatasets } from "./complete";
import { openDownloadModal } from "./download";
import {
    insertQuarryTag,
    onReferences,
    recomputeReferences,
    setAddToExistingTag,
} from "./prompt";
import { QUARRY_TAB_BODY_ID } from "./tab";
import type {
    CleanTempResponse,
    DatasetDto,
    InstallResponse,
    PreviewResponse,
    SettingsResponse,
} from "./types";
import {
    datasetFolder,
    datasetLeafName,
    escapeHtml,
    groupByFolder,
} from "./util";

export { datasetFolder, datasetLeafName };

const MESSAGE_TIMEOUT_MS = 5000;
export const PREVIEW_ROW_LIMIT = 100;
export const PREVIEW_LOAD_MORE_COUNT = 500;
const ADD_TO_EXISTING_TAG_ID = "quarry-add-to-existing-tag";
const PREVIEW_MODAL_ID = "quarry-preview-modal";
const PREVIEW_TITLE_ID = "quarry-preview-title";
const PREVIEW_BODY_ID = "quarry-preview-body";
const PREVIEW_STATUS_ID = "quarry-preview-status";
const PREVIEW_LOAD_MORE_ID = "quarry-preview-loadmore";
const PREVIEW_CLEAR_ID = "quarry-preview-clear";

const expandedFolders = new Set<string>();

export const renderDatasetOptions = (dataset: DatasetDto): string =>
    dataset.columns
        .map((col) => {
            const selected =
                col.name === dataset.resolvedPromptColumn ? " selected" : "";
            const badge = col.kind === "list" ? " [list]" : "";
            return `<option value="${escapeHtml(col.name)}"${selected}>${escapeHtml(col.name)}${badge}</option>`;
        })
        .join("");

export const renderTagCheckboxes = (dataset: DatasetDto): string =>
    dataset.columns
        .map((col) => {
            const checked = (dataset.configuredTagColumns ?? []).includes(
                col.name,
            )
                ? " checked"
                : "";
            const badge = col.kind === "list" ? " [list]" : "";
            return `<label class="quarry-tag-option"><input type="checkbox" class="quarry-dataset-tag" data-dataset="${escapeHtml(dataset.name)}" value="${escapeHtml(col.name)}"${checked}> ${escapeHtml(col.name)}${badge}</label>`;
        })
        .join("");

export const formatRowCount = (count: number | null | undefined): string =>
    count == null ? "—" : count.toLocaleString();

export const applyInPromptHighlights = (
    container: HTMLElement,
    names: string[],
): void => {
    const wanted = new Set(names.map((n) => n.toLowerCase()));
    container
        .querySelectorAll<HTMLElement>("tr.quarry-dataset-row")
        .forEach((row) => {
            const name = (row.getAttribute("data-dataset") ?? "").toLowerCase();
            row.classList.toggle("quarry-dataset-in-prompt", wanted.has(name));
        });
};

export const renderDatasetNameButton = (
    name: string,
    label: string = name,
): string =>
    `<button type="button" class="quarry-dataset-name quarry-dataset-name-link" data-dataset="${name}" title="Add a reference to this dataset to your prompt">${label}</button>`;

export const renderDatasetRow = (
    dataset: DatasetDto,
    displayName: string = dataset.name,
): string => {
    const name = escapeHtml(dataset.name);
    const label = escapeHtml(displayName);
    if (dataset.error) {
        return `<tr class="quarry-dataset-row quarry-dataset-error" data-dataset="${name}">
            <td class="quarry-dataset-name-cell">${renderDatasetNameButton(name, label)}</td>
            <td colspan="4"><span class="quarry-dataset-error-msg">⚠️ ${escapeHtml(dataset.error)}</span></td>
        </tr>`;
    }
    return `<tr class="quarry-dataset-row" data-dataset="${name}">
        <td class="quarry-dataset-name-cell">${renderDatasetNameButton(name, label)}</td>
        <td><select class="quarry-dataset-column" data-dataset="${name}">${renderDatasetOptions(dataset)}</select></td>
        <td class="quarry-dataset-tags" title="Columns the 'tags' keyword searches across">${renderTagCheckboxes(dataset)}</td>
        <td class="quarry-dataset-rows" data-dataset="${name}" title="Rows in the dataset (loads when you preview it if not already counted)">${formatRowCount(dataset.rowCount)}</td>
        <td><button type="button" class="basic-button quarry-preview-button" data-dataset="${name}" title="Preview this dataset's rows (load more in the dialog)">Preview</button></td>
    </tr>`;
};

export interface DatasetFolderGroup {
    folder: string;
    datasets: DatasetDto[];
}

export const groupDatasetsByFolder = (
    datasets: DatasetDto[],
): { loose: DatasetDto[]; groups: DatasetFolderGroup[] } => {
    const { loose, groups } = groupByFolder(datasets);
    return {
        loose,
        groups: groups.map((group) => ({
            folder: group.folder,
            datasets: group.items,
        })),
    };
};

export const renderFolderGroup = (
    group: DatasetFolderGroup,
    expanded: boolean,
): string => {
    const folder = escapeHtml(group.folder);
    const collapsedClass = expanded ? "" : " quarry-collapsed";
    const rows = group.datasets
        .map((dataset) =>
            renderDatasetRow(dataset, datasetLeafName(dataset.name)),
        )
        .join("");
    return `<tbody class="quarry-folder-group${collapsedClass}" data-folder="${folder}">
        <tr class="quarry-folder-row">
            <td colspan="5">
                <button type="button" class="quarry-folder-toggle" data-folder="${folder}" aria-expanded="${expanded}" title="Show or hide the datasets in this folder">
                    <span class="quarry-folder-caret" aria-hidden="true"></span>
                    <span class="quarry-folder-name">${folder}</span>
                    <span class="quarry-folder-count" title="${group.datasets.length} dataset(s)">${group.datasets.length}</span>
                </button>
            </td>
        </tr>
        ${rows}
    </tbody>`;
};

export const renderDatasets = (
    datasets: DatasetDto[],
    expandedFolders: ReadonlySet<string> = new Set<string>(),
): string => {
    if (!datasets || datasets.length === 0) {
        return `<div class="quarry-datasets-empty">No datasets found. Set a folder containing CSV / JSON / JSONL / Parquet / Lance files, then Refresh.</div>`;
    }
    const { loose, groups } = groupDatasetsByFolder(datasets);
    const groupBodies = groups
        .map((group) =>
            renderFolderGroup(group, expandedFolders.has(group.folder)),
        )
        .join("");
    const looseBody = loose.length
        ? `<tbody>${loose.map((dataset) => renderDatasetRow(dataset)).join("")}</tbody>`
        : "";
    return `<table class="quarry-datasets-table">
        <thead>
            <tr><th>Dataset</th><th>Prompt column</th><th>Tag columns</th><th>Rows</th><th>Preview</th></tr>
        </thead>
        ${groupBodies}${looseBody}
    </table>`;
};

export const renderPreviewTable = (
    columns: string[],
    rows: string[][],
): string => {
    if (!columns || columns.length === 0) {
        return `<div class="quarry-preview-empty">No columns to display.</div>`;
    }
    const head = columns.map((col) => `<th>${escapeHtml(col)}</th>`).join("");
    if (!rows || rows.length === 0) {
        return `<table class="quarry-preview-table simple-table">
            <thead><tr>${head}</tr></thead>
            <tbody><tr><td class="quarry-preview-empty" colspan="${columns.length}">No rows.</td></tr></tbody>
        </table>`;
    }
    const body = rows
        .map((row) => {
            const cells = columns
                .map((_, i) => `<td>${escapeHtml(row[i] ?? "")}</td>`)
                .join("");
            return `<tr>${cells}</tr>`;
        })
        .join("");
    return `<table class="quarry-preview-table simple-table">
        <thead><tr>${head}</tr></thead>
        <tbody>${body}</tbody>
    </table>`;
};

export const renderPreviewStatus = (
    shown: number,
    total: number | null | undefined,
): string =>
    total == null
        ? `Showing ${shown.toLocaleString()} row(s).`
        : `Showing ${shown.toLocaleString()} of ${total.toLocaleString()} row(s).`;

export const renderForm = (folder: string): string => `
    <div class="quarry-settings">
        <form id="quarry-form">
            <div class="input-group input-group-open">
                <span class="input-group-header input-group-noshrink">
                    <span class="header-label-wrap"><span class="header-label">🦆 Quarry</span></span>
                </span>
                <div class="input-group-content">
                    <div class="quarry-actions">
                        <button type="button" id="quarry-refresh" class="basic-button">Refresh</button>
                        <button type="button" id="quarry-download-datasets" class="basic-button" title="Browse and download ready-made datasets from the official collection">Download Datasets</button>
                    </div>
                    <div id="quarry-datasets" class="quarry-datasets"></div>
                    <div class="auto-input auto-input-flex">
                        <label for="quarry-folder"><span class="auto-input-name">Datasets folder</span></label>
                        <input class="auto-text" type="text" id="quarry-folder" value="${escapeHtml(folder)}" placeholder="/path/to/datasets" autocomplete="off">
                    </div>
                    <div class="quarry-setting-row">
                        <span class="auto-input-qbutton info-popover-button" onclick="doPopover('${ADD_TO_EXISTING_TAG_ID}', arguments[0])">?</span>
                        <label for="${ADD_TO_EXISTING_TAG_ID}">Add to existing <code>&lt;q:&gt;</code> tag</label>
                        <input type="checkbox" id="${ADD_TO_EXISTING_TAG_ID}" checked>
                        <div class="sui-popover sui-info-popover" id="popover_${ADD_TO_EXISTING_TAG_ID}"><b>Add to existing &lt;q:&gt; tag</b><br>On by default. When on, clicking a dataset name adds it to the first existing <code>&lt;q:…&gt;</code> tag (e.g. <code>&lt;q:A,B&gt;</code>) instead of inserting a separate one.</div>
                    </div>
                </div>
            </div>
            <div id="quarry-message" class="quarry-message"></div>
            <div class="quarry-actions">
                <button type="submit" class="basic-button">Save Settings</button>
                <button type="button" id="quarry-clean-temp" class="basic-button" title="Delete leftover placeholder .txt files an older Quarry version wrote into SwarmUI's Wildcards folder">Clean temp files</button>
            </div>
        </form>
    </div>`;

export const renderInstallGate = (): string => `
    <div class="quarry-settings quarry-install-gate">
        <div class="input-group input-group-open">
            <span class="input-group-header input-group-noshrink">
                <span class="header-label-wrap"><span class="header-label">🦆 Quarry</span></span>
            </span>
            <div class="input-group-content">
                <p class="quarry-install-intro">Quarry needs the DuckDB <code>lance</code> extension to read its datasets — a one-time download of a ~235&nbsp;MB signed binary from the official DuckDB extension repository.</p>
                <div class="quarry-actions">
                    <button type="button" id="quarry-install" class="basic-button">⬇ Install Requirements</button>
                </div>
                <div id="quarry-install-status" class="quarry-install-status"></div>
            </div>
        </div>
    </div>`;

export const collectPromptColumns = (
    container: HTMLElement,
): Record<string, string> => {
    const result: Record<string, string> = {};
    const selects = container.querySelectorAll<HTMLSelectElement>(
        "select.quarry-dataset-column",
    );
    selects.forEach((select) => {
        const name = select.getAttribute("data-dataset");
        if (name) {
            result[name] = select.value;
        }
    });
    return result;
};

export const collectTagColumns = (
    container: HTMLElement,
): Record<string, string[]> => {
    const result: Record<string, string[]> = {};
    const boxes = container.querySelectorAll<HTMLInputElement>(
        "input.quarry-dataset-tag",
    );
    boxes.forEach((box) => {
        const name = box.getAttribute("data-dataset");
        if (!name) {
            return;
        }
        if (!(name in result)) {
            result[name] = [];
        }
        if (box.checked) {
            result[name].push(box.value);
        }
    });
    return result;
};

let messageTimer: ReturnType<typeof setTimeout> | null = null;

const applyTableHighlights = (names: string[]): void => {
    const container = document.getElementById("quarry-datasets");
    if (container) {
        applyInPromptHighlights(container, names);
    }
};

const applyResponse = (data: SettingsResponse): void => {
    const folderEl = document.getElementById(
        "quarry-folder",
    ) as HTMLInputElement | null;
    if (folderEl) {
        folderEl.value = data.datasetsFolder ?? "";
    }
    const addToExisting = data.addToExistingTag ?? true;
    const addToExistingEl = document.getElementById(
        ADD_TO_EXISTING_TAG_ID,
    ) as HTMLInputElement | null;
    if (addToExistingEl) {
        addToExistingEl.checked = addToExisting;
    }
    setAddToExistingTag(addToExisting);
    setCompletionDatasets(data.datasets);
    const datasetsEl = document.getElementById("quarry-datasets");
    if (datasetsEl) {
        datasetsEl.innerHTML = renderDatasets(
            data.datasets ?? [],
            expandedFolders,
        );
        recomputeReferences();
    }
};

const showMessage = (message: string, type: "success" | "error"): void => {
    const el = document.getElementById("quarry-message");
    if (!el) {
        return;
    }
    el.textContent = message;
    el.className = `quarry-message quarry-message-${type}`;
    if (messageTimer) {
        clearTimeout(messageTimer);
    }
    messageTimer = setTimeout(() => {
        el.textContent = "";
        el.className = "quarry-message";
        messageTimer = null;
    }, MESSAGE_TIMEOUT_MS);
};

const loadSettings = (): void => {
    genericRequest<SettingsResponse>("QuarryGetSettings", {}, (data) => {
        if (!data.success) {
            return;
        }
        if (data.requirementsInstalled === false) {
            showInstallGate();
            return;
        }
        ensureFormRendered();
        applyResponse(data);
    });
};

const saveSettings = (): void => {
    const folder = (
        document.getElementById("quarry-folder") as HTMLInputElement
    ).value.trim();
    const container = document.getElementById("quarry-datasets");
    const promptColumns = container ? collectPromptColumns(container) : {};
    const tagColumns = container ? collectTagColumns(container) : {};
    const addToExistingEl = document.getElementById(
        ADD_TO_EXISTING_TAG_ID,
    ) as HTMLInputElement | null;
    genericRequest<SettingsResponse>(
        "QuarrySaveSettings",
        {
            datasetsFolder: folder,
            promptColumnsJson: JSON.stringify(promptColumns),
            tagColumnsJson: JSON.stringify(tagColumns),
            addToExistingTag: addToExistingEl?.checked ?? true,
        },
        (data) => {
            if (data.success) {
                applyResponse(data);
                showMessage("Settings saved.", "success");
            } else {
                showMessage(
                    `Failed to save: ${data.error ?? "unknown error"}`,
                    "error",
                );
            }
        },
    );
};

const refresh = (): void => {
    const button = document.getElementById(
        "quarry-refresh",
    ) as HTMLButtonElement | null;
    if (button) {
        button.disabled = true;
    }
    genericRequest<SettingsResponse>("QuarryRefresh", {}, (data) => {
        if (button) {
            button.disabled = false;
        }
        if (data.success) {
            applyResponse(data);
            showMessage(data.message ?? "Refreshed.", "success");
        } else {
            showMessage(
                `Refresh failed: ${data.error ?? "unknown error"}`,
                "error",
            );
        }
    });
};

const cleanTempFiles = (): void => {
    const button = document.getElementById(
        "quarry-clean-temp",
    ) as HTMLButtonElement | null;
    if (button) {
        button.disabled = true;
    }
    genericRequest<CleanTempResponse>("QuarryCleanTempFiles", {}, (data) => {
        if (button) {
            button.disabled = false;
        }
        if (!data.success) {
            showMessage(
                `Clean failed: ${data.error ?? "unknown error"}`,
                "error",
            );
            return;
        }
        const removed = data.removed ?? 0;
        showMessage(
            removed > 0
                ? `Removed ${removed.toLocaleString()} leftover placeholder file(s).`
                : "No leftover placeholder files found.",
            "success",
        );
    });
};

let previewDataset: string | null = null;
let previewShown = 0;
let previewTotal: number | null = null;
let previewExhausted = false;
let previewBusy = false;

const ensurePreviewModal = (): void => {
    if (document.getElementById(PREVIEW_MODAL_ID)) {
        return;
    }
    const modal = document.createElement("div");
    modal.className = "modal";
    modal.id = PREVIEW_MODAL_ID;
    modal.tabIndex = -1;
    modal.setAttribute("role", "dialog");
    modal.innerHTML = `
        <div class="modal-dialog modal-xl quarry-preview-dialog" role="document">
            <div class="modal-content">
                <div class="modal-header">
                    <h5 class="modal-title" id="${PREVIEW_TITLE_ID}">Preview</h5>
                </div>
                <div class="modal-body">
                    <div id="${PREVIEW_BODY_ID}" class="quarry-preview-body"></div>
                </div>
                <div class="modal-footer quarry-preview-footer">
                    <span id="${PREVIEW_STATUS_ID}" class="quarry-preview-status"></span>
                    <span class="quarry-preview-footer-actions">
                        <button type="button" id="${PREVIEW_CLEAR_ID}" class="basic-button" title="Drop this dataset's cached preview rows and reload the first page">Clear cache</button>
                        <button type="button" id="${PREVIEW_LOAD_MORE_ID}" class="basic-button" title="Load and cache ${PREVIEW_LOAD_MORE_COUNT} more rows">Load ${PREVIEW_LOAD_MORE_COUNT} more</button>
                        <button type="button" class="btn btn-secondary basic-button" data-bs-dismiss="modal">Close</button>
                    </span>
                </div>
            </div>
        </div>`;
    document.body.appendChild(modal);
    modal
        .querySelector('[data-bs-dismiss="modal"]')
        ?.addEventListener("click", hidePreviewModal);
    document
        .getElementById(PREVIEW_LOAD_MORE_ID)
        ?.addEventListener("click", loadMorePreview);
    document
        .getElementById(PREVIEW_CLEAR_ID)
        ?.addEventListener("click", clearPreviewCache);
};

const showPreviewModal = (): void => {
    if (typeof $ === "function") {
        $(`#${PREVIEW_MODAL_ID}`).modal("show");
    }
};

const hidePreviewModal = (): void => {
    if (typeof $ === "function") {
        $(`#${PREVIEW_MODAL_ID}`).modal("hide");
    }
};

const applyRowCount = (dataset: string, count: number | null): void => {
    const selector = `td.quarry-dataset-rows[data-dataset="${dataset.replace(/(["\\])/g, "\\$1")}"]`;
    for (const cell of Array.from(
        document.querySelectorAll<HTMLElement>(selector),
    )) {
        cell.textContent = formatRowCount(count);
    }
};

const updatePreviewControls = (): void => {
    const loadMore = document.getElementById(
        PREVIEW_LOAD_MORE_ID,
    ) as HTMLButtonElement | null;
    const clear = document.getElementById(
        PREVIEW_CLEAR_ID,
    ) as HTMLButtonElement | null;
    const status = document.getElementById(PREVIEW_STATUS_ID);
    if (loadMore) {
        loadMore.disabled = previewBusy || previewExhausted || !previewDataset;
    }
    if (clear) {
        clear.disabled = previewBusy || !previewDataset;
    }
    if (status) {
        status.textContent = previewBusy
            ? "Loading…"
            : renderPreviewStatus(previewShown, previewTotal);
    }
};

const fetchPreview = (
    dataset: string,
    limit: number,
    isLoadMore: boolean,
): void => {
    previewBusy = true;
    updatePreviewControls();
    genericRequest<PreviewResponse>(
        "QuarryPreviewDataset",
        { dataset, limit },
        (data) => {
            previewBusy = false;
            if (previewDataset !== dataset) {
                return;
            }
            const bodyEl = document.getElementById(PREVIEW_BODY_ID);
            if (!data.success) {
                if (bodyEl && !isLoadMore) {
                    bodyEl.innerHTML = `<div class="quarry-preview-error">${escapeHtml(data.error ?? "Failed to load preview.")}</div>`;
                }
                updatePreviewControls();
                return;
            }
            const columns = data.columns ?? [];
            const rows = data.rows ?? [];
            previewShown = rows.length;
            previewTotal = data.rowCount ?? null;
            previewExhausted =
                rows.length < limit ||
                (previewTotal !== null && previewShown >= previewTotal);
            applyRowCount(dataset, previewTotal);
            if (bodyEl) {
                bodyEl.innerHTML = renderPreviewTable(columns, rows);
            }
            updatePreviewControls();
        },
    );
};

export const openPreview = (dataset: string): void => {
    ensurePreviewModal();
    previewDataset = dataset;
    previewShown = 0;
    previewTotal = null;
    previewExhausted = false;
    const titleEl = document.getElementById(PREVIEW_TITLE_ID);
    if (titleEl) {
        titleEl.textContent = `Preview — ${dataset}`;
    }
    const bodyEl = document.getElementById(PREVIEW_BODY_ID);
    if (bodyEl) {
        bodyEl.innerHTML = `<div class="quarry-preview-loading">Loading…</div>`;
    }
    showPreviewModal();
    fetchPreview(dataset, PREVIEW_ROW_LIMIT, false);
};

const loadMorePreview = (): void => {
    if (!previewDataset || previewBusy || previewExhausted) {
        return;
    }
    fetchPreview(previewDataset, previewShown + PREVIEW_LOAD_MORE_COUNT, true);
};

const clearPreviewCache = (): void => {
    const dataset = previewDataset;
    if (!dataset || previewBusy) {
        return;
    }
    previewBusy = true;
    updatePreviewControls();
    genericRequest<{ success: boolean; error?: string }>(
        "QuarryClearPreviewCache",
        { dataset },
        (data) => {
            previewBusy = false;
            if (previewDataset !== dataset) {
                return;
            }
            if (!data.success) {
                updatePreviewControls();
                return;
            }
            previewShown = 0;
            previewExhausted = false;
            const bodyEl = document.getElementById(PREVIEW_BODY_ID);
            if (bodyEl) {
                bodyEl.innerHTML = `<div class="quarry-preview-loading">Loading…</div>`;
            }
            fetchPreview(dataset, PREVIEW_ROW_LIMIT, false);
        },
    );
};

const toggleFolder = (toggle: HTMLElement): void => {
    const folder = toggle.getAttribute("data-folder");
    const group = toggle.closest<HTMLElement>(".quarry-folder-group");
    if (!folder || !group) {
        return;
    }
    const collapsed = group.classList.toggle("quarry-collapsed");
    toggle.setAttribute("aria-expanded", String(!collapsed));
    if (collapsed) {
        expandedFolders.delete(folder);
    } else {
        expandedFolders.add(folder);
    }
};

const datasetsClickHandler = (event: Event): void => {
    const target = event.target as HTMLElement | null;
    const folderToggle = target?.closest<HTMLElement>(".quarry-folder-toggle");
    if (folderToggle) {
        toggleFolder(folderToggle);
        return;
    }
    const previewButton = target?.closest<HTMLElement>(
        ".quarry-preview-button",
    );
    if (previewButton) {
        const dataset = previewButton.getAttribute("data-dataset");
        if (dataset) {
            openPreview(dataset);
        }
        return;
    }
    const nameCell = target?.closest<HTMLElement>(".quarry-dataset-name-cell");
    if (nameCell) {
        const dataset = nameCell
            .querySelector<HTMLElement>(".quarry-dataset-name-link")
            ?.getAttribute("data-dataset");
        if (dataset) {
            insertQuarryTag(dataset);
        }
    }
};

const ensureFormRendered = (): void => {
    if (document.getElementById("quarry-form")) {
        return;
    }
    const host = document.getElementById(QUARRY_TAB_BODY_ID);
    if (!host) {
        return;
    }
    host.innerHTML = renderForm("");
    document
        .getElementById("quarry-form")
        ?.addEventListener("submit", (event) => {
            event.preventDefault();
            saveSettings();
        });
    document
        .getElementById("quarry-refresh")
        ?.addEventListener("click", refresh);
    document
        .getElementById("quarry-clean-temp")
        ?.addEventListener("click", cleanTempFiles);
    document
        .getElementById("quarry-download-datasets")
        ?.addEventListener("click", () => openDownloadModal(loadSettings));
    document
        .getElementById("quarry-datasets")
        ?.addEventListener("click", datasetsClickHandler);
    document
        .getElementById(ADD_TO_EXISTING_TAG_ID)
        ?.addEventListener("change", (event) => {
            setAddToExistingTag((event.target as HTMLInputElement).checked);
        });
};

const installRequirements = (): void => {
    const button = document.getElementById(
        "quarry-install",
    ) as HTMLButtonElement | null;
    const status = document.getElementById("quarry-install-status");
    if (button) {
        button.disabled = true;
    }
    if (status) {
        status.textContent =
            "Installing… downloading the DuckDB lance extension (~235 MB). This can take a few minutes — please wait.";
    }
    genericRequest<InstallResponse>("QuarryInstallRequirements", {}, (data) => {
        if (data.success) {
            if (status) {
                status.textContent = "Installed! Loading…";
            }
            loadSettings();
        } else {
            if (button) {
                button.disabled = false;
            }
            if (status) {
                status.textContent = `Install failed: ${data.error ?? "unknown error"}`;
            }
        }
    });
};

const showInstallGate = (): void => {
    if (document.getElementById("quarry-install")) {
        return;
    }
    const host = document.getElementById(QUARRY_TAB_BODY_ID);
    if (!host) {
        return;
    }
    host.innerHTML = renderInstallGate();
    document
        .getElementById("quarry-install")
        ?.addEventListener("click", installRequirements);
};

const init = (): void => {
    const host = document.getElementById(QUARRY_TAB_BODY_ID);
    if (!host) {
        return;
    }
    host.innerHTML = `<div class="quarry-loading">Loading…</div>`;
    loadSettings();
    onReferences(applyTableHighlights);
};

export const quarry = {
    init,
};
