"use strict";
(() => {
  // frontend/prompt.ts
  var HIGHLIGHT_DEBOUNCE_MS = 250;
  var PROMPT_BOX_IDS = ["alt_prompt_textbox", "alt_negativeprompt_textbox"];
  var Q_TAG_GUARD = /<q(?:\[|:)/i;
  var listeners = [];
  var lastNames = [];
  var onReferences = (listener) => {
    listeners.push(listener);
    listener(lastNames);
  };
  var notify = (names) => {
    lastNames = names;
    for (const listener of listeners) {
      listener(names);
    }
  };
  var readPromptText = () => PROMPT_BOX_IDS.map(
    (id) => document.getElementById(id)?.value ?? ""
  ).join("\n");
  var recomputeReferences = () => {
    const prompt = readPromptText();
    if (!Q_TAG_GUARD.test(prompt)) {
      notify([]);
      return;
    }
    genericRequest(
      "QuarryResolveReferences",
      { prompt },
      (data) => {
        if (data.success) {
          notify(data.names ?? []);
        }
      }
    );
  };
  var highlightTimer = null;
  var schedule = () => {
    if (highlightTimer) {
      clearTimeout(highlightTimer);
    }
    highlightTimer = setTimeout(recomputeReferences, HIGHLIGHT_DEBOUNCE_MS);
  };
  var watching = false;
  var startPromptWatcher = () => {
    if (watching) {
      return;
    }
    watching = true;
    for (const id of PROMPT_BOX_IDS) {
      document.getElementById(id)?.addEventListener("input", schedule);
    }
  };
  var matchQuarryTag = (prompt, name) => {
    const matcher = new RegExp(
      `<(q(?:\\[\\d+(?:-\\d+)?\\])?):${regexEscape(name)}>`,
      "g"
    );
    return prompt.match(matcher);
  };
  var insertQuarryTag = (name) => {
    let [promptBox, cursorPos] = uiImprover.getLastSelectedTextbox();
    if (!promptBox) {
      promptBox = getRequiredElementById(
        "alt_prompt_textbox"
      );
      cursorPos = promptBox.value.length;
    }
    const prefix = promptBox.value.substring(0, cursorPos);
    const suffix = promptBox.value.substring(cursorPos);
    const trimmed = trimSpaces(prefix);
    const match = matchQuarryTag(trimmed, name);
    if (match && match.length > 0) {
      const last = match[match.length - 1];
      if (trimmed.endsWith(trimSpaces(last))) {
        promptBox.value = `${trimSpaces(trimmed.substring(0, trimmed.length - last.length))} ${suffix}`.trim();
        triggerChangeFor(promptBox);
        recomputeReferences();
        return;
      }
    }
    const tag = `<q:${name}>`;
    promptBox.value = `${trimSpaces(prefix)} ${tag} ${trimSpaces(suffix)}`.trim();
    promptBox.selectionStart = cursorPos + tag.length + 1;
    promptBox.selectionEnd = cursorPos + tag.length + 1;
    promptBox.focus();
    triggerChangeFor(promptBox);
    recomputeReferences();
  };

  // frontend/settings.ts
  var MESSAGE_TIMEOUT_MS = 5e3;
  var PREVIEW_ROW_LIMIT = 100;
  var PREVIEW_MODAL_ID = "quarry-preview-modal";
  var PREVIEW_TITLE_ID = "quarry-preview-title";
  var PREVIEW_BODY_ID = "quarry-preview-body";
  var escapeHtml = (text) => {
    const div = document.createElement("div");
    div.textContent = text;
    return div.innerHTML;
  };
  var renderStatus = (active, count) => active ? `<span class="quarry-status-active">✓ Active — ${count} dataset(s)</span>` : `<span class="quarry-status-inactive">○ Inactive — enable and set a folder to activate</span>`;
  var renderDatasetOptions = (dataset) => dataset.columns.map((col) => {
    const selected = col.name === dataset.resolvedPromptColumn ? " selected" : "";
    const badge = col.kind === "list" ? " [list]" : "";
    return `<option value="${escapeHtml(col.name)}"${selected}>${escapeHtml(col.name)}${badge}</option>`;
  }).join("");
  var renderTagCheckboxes = (dataset) => dataset.columns.map((col) => {
    const checked = (dataset.configuredTagColumns ?? []).includes(
      col.name
    ) ? " checked" : "";
    const badge = col.kind === "list" ? " [list]" : "";
    return `<label class="quarry-tag-option"><input type="checkbox" class="quarry-dataset-tag" data-dataset="${escapeHtml(dataset.name)}" value="${escapeHtml(col.name)}"${checked}> ${escapeHtml(col.name)}${badge}</label>`;
  }).join("");
  var formatRowCount = (count) => count == null ? "—" : count.toLocaleString();
  var applyInPromptHighlights = (container, names) => {
    const wanted = new Set(names.map((n) => n.toLowerCase()));
    container.querySelectorAll("tr.quarry-dataset-row").forEach((row) => {
      const name = (row.getAttribute("data-dataset") ?? "").toLowerCase();
      row.classList.toggle("quarry-dataset-in-prompt", wanted.has(name));
    });
  };
  var renderDatasetRow = (dataset) => {
    const name = escapeHtml(dataset.name);
    if (dataset.error) {
      return `<tr class="quarry-dataset-row quarry-dataset-error" data-dataset="${name}">
            <td><code class="quarry-dataset-name">${name}</code></td>
            <td colspan="4"><span class="quarry-dataset-error-msg">⚠️ ${escapeHtml(dataset.error)}</span></td>
        </tr>`;
    }
    return `<tr class="quarry-dataset-row" data-dataset="${name}">
        <td><code class="quarry-dataset-name">${name}</code></td>
        <td><select class="quarry-dataset-column" data-dataset="${name}">${renderDatasetOptions(dataset)}</select></td>
        <td class="quarry-dataset-tags" title="Columns the 'tags' keyword searches across">${renderTagCheckboxes(dataset)}</td>
        <td class="quarry-dataset-rows" title="${formatRowCount(dataset.rowCount)} rows with a non-empty prompt (usable picks)">${formatRowCount(dataset.rowCount)}</td>
        <td><button type="button" class="basic-button quarry-preview-button" data-dataset="${name}" title="Preview the first ${PREVIEW_ROW_LIMIT} rows">👁 Preview</button></td>
    </tr>`;
  };
  var renderDatasets = (datasets2) => {
    if (!datasets2 || datasets2.length === 0) {
      return `<div class="quarry-datasets-empty">No datasets found. Set a folder containing CSV / JSON / JSONL / Parquet / Lance files, then Refresh.</div>`;
    }
    return `<table class="quarry-datasets-table">
        <thead>
            <tr><th>Dataset</th><th>Prompt column</th><th>Tag columns</th><th>Rows</th><th>Preview</th></tr>
        </thead>
        <tbody>${datasets2.map(renderDatasetRow).join("")}</tbody>
    </table>`;
  };
  var renderPreviewTable = (columns, rows) => {
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
    const body = rows.map((row) => {
      const cells = columns.map((_, i) => `<td>${escapeHtml(row[i] ?? "")}</td>`).join("");
      return `<tr>${cells}</tr>`;
    }).join("");
    return `<table class="quarry-preview-table simple-table">
        <thead><tr>${head}</tr></thead>
        <tbody>${body}</tbody>
    </table>`;
  };
  var renderForm = (enabled, folder) => `
    <div class="quarry-settings">
        <form id="quarry-form">
            <div class="input-group input-group-open">
                <span class="input-group-header input-group-noshrink">
                    <span class="header-label-wrap"><span class="header-label">🦆 Quarry</span></span>
                </span>
                <div class="input-group-content">
                    <div class="auto-input auto-input-flex">
                        <span class="auto-input-name">Enable</span>
                        <label class="auto-checkbox">
                            <input type="checkbox" id="quarry-enabled" ${enabled ? "checked" : ""}>
                            <span class="auto-checkbox-label">Enable</span>
                        </label>
                    </div>
                    <div class="auto-input auto-input-flex">
                        <label for="quarry-folder"><span class="auto-input-name">Datasets folder</span></label>
                        <input class="auto-text" type="text" id="quarry-folder" value="${escapeHtml(folder)}" placeholder="/path/to/datasets" autocomplete="off">
                    </div>
                    <div id="quarry-status" class="quarry-status-line"></div>
                    <div class="quarry-actions">
                        <button type="button" id="quarry-refresh" class="basic-button">🔄 Refresh</button>
                    </div>
                    <div id="quarry-datasets" class="quarry-datasets"></div>
                </div>
            </div>
            <div id="quarry-message" class="quarry-message"></div>
            <div class="quarry-actions">
                <button type="submit" class="basic-button">Save Settings</button>
            </div>
        </form>
    </div>`;
  var collectPromptColumns = (container) => {
    const result = {};
    const selects = container.querySelectorAll(
      "select.quarry-dataset-column"
    );
    selects.forEach((select) => {
      const name = select.getAttribute("data-dataset");
      if (name) {
        result[name] = select.value;
      }
    });
    return result;
  };
  var collectTagColumns = (container) => {
    const result = {};
    const boxes = container.querySelectorAll(
      "input.quarry-dataset-tag"
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
  var messageTimer = null;
  var applyTableHighlights = (names) => {
    const container = document.getElementById("quarry-datasets");
    if (container) {
      applyInPromptHighlights(container, names);
    }
  };
  var notifyDatasetsChanged = () => {
    document.dispatchEvent(new CustomEvent("quarry:datasets-changed"));
  };
  var applyResponse = (data) => {
    const enabledEl = document.getElementById(
      "quarry-enabled"
    );
    const folderEl = document.getElementById(
      "quarry-folder"
    );
    if (enabledEl) {
      enabledEl.checked = data.enabled ?? false;
    }
    if (folderEl) {
      folderEl.value = data.datasetsFolder ?? "";
    }
    const statusEl = document.getElementById("quarry-status");
    if (statusEl) {
      statusEl.innerHTML = renderStatus(
        data.active ?? false,
        data.count ?? 0
      );
    }
    const datasetsEl = document.getElementById("quarry-datasets");
    if (datasetsEl) {
      datasetsEl.innerHTML = renderDatasets(data.datasets ?? []);
      recomputeReferences();
    }
  };
  var showMessage = (message, type) => {
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
  var loadSettings = () => {
    genericRequest("QuarryGetSettings", {}, (data) => {
      if (data.success) {
        applyResponse(data);
      }
    });
  };
  var saveSettings = () => {
    const enabled = document.getElementById("quarry-enabled").checked;
    const folder = document.getElementById("quarry-folder").value.trim();
    const container = document.getElementById("quarry-datasets");
    const promptColumns = container ? collectPromptColumns(container) : {};
    const tagColumns = container ? collectTagColumns(container) : {};
    genericRequest(
      "QuarrySaveSettings",
      {
        enabled,
        datasetsFolder: folder,
        promptColumnsJson: JSON.stringify(promptColumns),
        tagColumnsJson: JSON.stringify(tagColumns)
      },
      (data) => {
        if (data.success) {
          applyResponse(data);
          notifyDatasetsChanged();
          showMessage("Settings saved.", "success");
        } else {
          showMessage(
            `Failed to save: ${data.error ?? "unknown error"}`,
            "error"
          );
        }
      }
    );
  };
  var refresh = () => {
    const button = document.getElementById(
      "quarry-refresh"
    );
    if (button) {
      button.disabled = true;
    }
    genericRequest("QuarryRefresh", {}, (data) => {
      if (button) {
        button.disabled = false;
      }
      if (data.success) {
        applyResponse(data);
        notifyDatasetsChanged();
        showMessage(data.message ?? "Refreshed.", "success");
      } else {
        showMessage(
          `Refresh failed: ${data.error ?? "unknown error"}`,
          "error"
        );
      }
    });
  };
  var ensurePreviewModal = () => {
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
                <div class="modal-footer">
                    <button type="button" class="btn btn-secondary basic-button" data-bs-dismiss="modal">Close</button>
                </div>
            </div>
        </div>`;
    document.body.appendChild(modal);
    modal.querySelector('[data-bs-dismiss="modal"]')?.addEventListener("click", hidePreviewModal);
  };
  var showPreviewModal = () => {
    if (typeof $ === "function") {
      $(`#${PREVIEW_MODAL_ID}`).modal("show");
    }
  };
  var hidePreviewModal = () => {
    if (typeof $ === "function") {
      $(`#${PREVIEW_MODAL_ID}`).modal("hide");
    }
  };
  var openPreview = (dataset) => {
    ensurePreviewModal();
    const titleEl = document.getElementById(PREVIEW_TITLE_ID);
    const bodyEl = document.getElementById(PREVIEW_BODY_ID);
    if (titleEl) {
      titleEl.textContent = `Preview — ${dataset} (first ${PREVIEW_ROW_LIMIT} rows)`;
    }
    if (bodyEl) {
      bodyEl.innerHTML = `<div class="quarry-preview-loading">Loading…</div>`;
    }
    showPreviewModal();
    genericRequest(
      "QuarryPreviewDataset",
      { dataset, limit: PREVIEW_ROW_LIMIT },
      (data) => {
        if (!bodyEl) {
          return;
        }
        if (data.success) {
          bodyEl.innerHTML = renderPreviewTable(
            data.columns ?? [],
            data.rows ?? []
          );
        } else {
          bodyEl.innerHTML = `<div class="quarry-preview-error">${escapeHtml(data.error ?? "Failed to load preview.")}</div>`;
        }
      }
    );
  };
  var init = () => {
    const tool = registerNewTool("quarry", "Quarry");
    tool.innerHTML = renderForm(false, "");
    loadSettings();
    document.getElementById("quarry-form")?.addEventListener("submit", (event) => {
      event.preventDefault();
      saveSettings();
    });
    document.getElementById("quarry-refresh")?.addEventListener("click", refresh);
    document.getElementById("quarry-datasets")?.addEventListener("click", (event) => {
      const target = event.target;
      const button = target?.closest(
        ".quarry-preview-button"
      );
      const dataset = button?.getAttribute("data-dataset");
      if (dataset) {
        openPreview(dataset);
      }
    });
    onReferences(applyTableHighlights);
  };
  var quarry = {
    init
  };

  // frontend/browser.ts
  var TAB_ID = "Quarry-Tab";
  var LIST_ID = "quarry_list";
  var BROWSER_ID = "quarrybrowser";
  var PLACEHOLDER_IMAGE = "/imgs/model_placeholder.jpg";
  var datasets = [];
  var datasetByName = {};
  var browser = null;
  var referencedNames = /* @__PURE__ */ new Set();
  var setDatasets = (list) => {
    datasets = list;
    datasetByName = {};
    for (const dataset of list) {
      datasetByName[dataset.name] = dataset;
    }
  };
  var fetchDatasets = (done) => {
    genericRequest("QuarryGetSettings", {}, (data) => {
      if (data.success) {
        setDatasets(data.datasets ?? []);
      }
      done();
    });
  };
  var computeFoldersAndFiles = (allNames, path, depth) => {
    const clampedDepth = Math.max(1, Math.min(20, Math.round(depth)));
    const prefix = path === "" ? "" : `${path.replace(/\/+$/, "")}/`;
    const folders = /* @__PURE__ */ new Set();
    const files = [];
    const seen = /* @__PURE__ */ new Set();
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
  var listQuarryFoldersAndFiles = (path, _isRefresh, callback, depth) => {
    const run = () => {
      const { folders, files } = computeFoldersAndFiles(
        datasets.map((dataset) => dataset.name),
        path,
        depth
      );
      const prefix = path === "" ? "" : path.endsWith("/") ? path : `${path}/`;
      const fileObjs = files.map((name) => ({
        name,
        data: {
          ...datasetByName[name],
          display: name.substring(prefix.length),
          image: PLACEHOLDER_IMAGE,
          src: ""
        }
      }));
      callback(
        folders.sort((a, b) => a.localeCompare(b)),
        fileObjs
      );
    };
    if (datasets.length === 0) {
      fetchDatasets(run);
    } else {
      run();
    }
  };
  var describeQuarry = (file) => {
    const name = file.name;
    const dataset = datasetByName[name];
    const display = name.replaceAll("/", " / ");
    const className = referencedNames.has(name.toLowerCase()) ? "model-selected" : "";
    if (!dataset) {
      return {
        name,
        description: escapeHtml(name),
        buttons: [],
        className,
        searchable: name,
        image: PLACEHOLDER_IMAGE,
        display
      };
    }
    const cols = (dataset.columns ?? []).map((col) => `${col.name}${col.kind === "list" ? " [list]" : ""}`).join(", ");
    const buttons = [];
    let description;
    if (dataset.error) {
      description = `<span class="quarry-card-title">${escapeHtml(name)}</span><br><span class="quarry-card-error">⚠️ ${escapeHtml(dataset.error)}</span>`;
    } else {
      const meta = [];
      if (dataset.rowCount != null) {
        meta.push(`${formatRowCount(dataset.rowCount)} rows`);
      }
      if (dataset.resolvedPromptColumn) {
        meta.push(`prompt: ${escapeHtml(dataset.resolvedPromptColumn)}`);
      }
      const lines = [
        `<span class="quarry-card-title">${escapeHtml(name)}</span>`,
        `<span class="quarry-card-meta">${meta.join(" · ")}</span>`
      ];
      if (cols) {
        lines.push(
          `<span class="quarry-card-cols">${escapeHtml(cols)}</span>`
        );
      }
      description = lines.join("<br>");
      buttons.push({ label: "Preview", onclick: () => openPreview(name) });
    }
    buttons.push({
      label: "Copy reference",
      onclick: () => copyText(`<q:${name}>`)
    });
    return {
      name,
      description,
      buttons,
      className,
      searchable: `${name}, ${cols}`,
      image: PLACEHOLDER_IMAGE,
      display,
      detail_list: [escapeHtml(display), escapeHtml(cols)]
    };
  };
  var selectQuarry = (file) => {
    insertQuarryTag(file.name);
  };
  var refreshCardHighlights = () => {
    if (!browser?.contentDiv) {
      return;
    }
    for (const child of Array.from(browser.contentDiv.children)) {
      const cardName = child.dataset?.name;
      if (cardName) {
        child.classList.toggle(
          "model-selected",
          referencedNames.has(cardName.toLowerCase())
        );
      }
    }
  };
  var createBrowser = () => {
    browser = new GenPageBrowserClass(
      LIST_ID,
      listQuarryFoldersAndFiles,
      BROWSER_ID,
      "Small Cards",
      describeQuarry,
      selectQuarry,
      ""
    );
    browser.refreshHandler = (callback) => {
      genericRequest("QuarryRefresh", {}, (data) => {
        if (data.success) {
          setDatasets(data.datasets ?? []);
          callback();
        } else {
          fetchDatasets(callback);
        }
      });
    };
    browser.builtEvent = () => recomputeReferences();
    onReferences((names) => {
      referencedNames = new Set(names.map((name) => name.toLowerCase()));
      refreshCardHighlights();
    });
    document.addEventListener(
      "quarry:datasets-changed",
      () => browser?.refresh()
    );
    if (typeof swarmHasLoaded !== "undefined" && swarmHasLoaded) {
      browser.navigate("");
    } else {
      sessionReadyCallbacks.push(() => browser?.navigate(""));
    }
  };
  var registerTabWithLayout = (navLink) => {
    if (typeof genTabLayout === "undefined" || !genTabLayout) {
      return;
    }
    const tab = new MovableGenTab(navLink, genTabLayout);
    genTabLayout.managedTabs.push(tab);
    if (genTabLayout.managedTabContainers.length > 0) {
      tab.contentElem.style.height = "100%";
      tab.contentElem.style.width = "100%";
      if (!genTabLayout.managedTabContainers.includes(
        tab.contentElem.parentElement
      )) {
        genTabLayout.managedTabContainers.push(
          tab.contentElem.parentElement
        );
      }
      tab.update();
      tab.navElem.addEventListener(
        "click",
        () => browserUtil.makeVisible(tab.contentElem)
      );
      genTabLayout.reapplyPositions();
    }
  };
  var injectTab = () => {
    const nav = document.getElementById("bottombartabcollection");
    const content = document.getElementById("t2i_bottom_bar_content");
    if (!nav || !content || document.getElementById(TAB_ID)) {
      return;
    }
    const li = document.createElement("li");
    li.className = "nav-item";
    li.setAttribute("role", "presentation");
    li.innerHTML = `<a class="nav-link translate" data-bs-toggle="tab" href="#${TAB_ID}" aria-selected="false" tabindex="-1" role="tab">Quarry</a>`;
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
  var quarryBrowser = {
    injectTab
  };

  // frontend/main.ts
  quarryBrowser.injectTab();
  var boot = () => {
    quarry.init();
    startPromptWatcher();
  };
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
//# sourceMappingURL=quarry.js.map
