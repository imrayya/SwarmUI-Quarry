// Shared prompt plumbing for both the settings table and the Quarry browser tab:
//  - watches the positive/negative prompt boxes and resolves which datasets they reference,
//  - inserts / toggles a `<q:NAME>` tag at the cursor when a dataset is clicked.

import type { ReferencesResponse } from "./types";

// Debounce prompt edits before asking the backend which datasets are referenced.
const HIGHLIGHT_DEBOUNCE_MS = 250;
// SwarmUI core's positive + negative prompt textareas (live in the page even when the Quarry tab is shown).
const PROMPT_BOX_IDS = ["alt_prompt_textbox", "alt_negativeprompt_textbox"];
// Cheap guard: only hit the backend when the prompt actually contains a `<q:` or `<q[count]:` tag.
const Q_TAG_GUARD = /<q(?:\[|:)/i;

type ReferencesListener = (names: string[]) => void;

const listeners: ReferencesListener[] = [];
// The most recent set of referenced dataset names, replayed to every new subscriber.
let lastNames: string[] = [];

/// Subscribes to changes in which datasets the current prompt references. The listener is invoked immediately
/// with the last known set so a freshly-built view starts in sync.
export const onReferences = (listener: ReferencesListener): void => {
    listeners.push(listener);
    listener(lastNames);
};

const notify = (names: string[]): void => {
    lastNames = names;
    for (const listener of listeners) {
        listener(names);
    }
};

// The combined positive + negative prompt text, so a reference in either is detected.
const readPromptText = (): string =>
    PROMPT_BOX_IDS.map(
        (id) =>
            (document.getElementById(id) as HTMLTextAreaElement | null)
                ?.value ?? "",
    ).join("\n");

/// Re-resolves the datasets referenced by the current prompt and notifies subscribers. Skips the backend
/// round-trip (clearing everything) when the prompt has no `<q:` tag — the common case.
export const recomputeReferences = (): void => {
    const prompt = readPromptText();
    if (!Q_TAG_GUARD.test(prompt)) {
        notify([]);
        return;
    }
    genericRequest<ReferencesResponse>(
        "QuarryResolveReferences",
        { prompt },
        (data) => {
            if (data.success) {
                notify(data.names ?? []);
            }
        },
    );
};

let highlightTimer: ReturnType<typeof setTimeout> | null = null;
const schedule = (): void => {
    if (highlightTimer) {
        clearTimeout(highlightTimer);
    }
    highlightTimer = setTimeout(recomputeReferences, HIGHLIGHT_DEBOUNCE_MS);
};

let watching = false;
/// Starts watching the prompt boxes for edits (idempotent).
export const startPromptWatcher = (): void => {
    if (watching) {
        return;
    }
    watching = true;
    for (const id of PROMPT_BOX_IDS) {
        document.getElementById(id)?.addEventListener("input", schedule);
    }
};

/// Matches the bare `<q:NAME>` / `<q[count]:NAME>` tag (no filter) for a dataset — used to toggle a click off
/// when the cursor is right after one we just inserted. Mirrors core's wildcard matcher.
export const matchQuarryTag = (
    prompt: string,
    name: string,
): RegExpMatchArray | null => {
    const matcher = new RegExp(
        `<(q(?:\\[\\d+(?:-\\d+)?\\])?):${regexEscape(name)}>`,
        "g",
    );
    return prompt.match(matcher);
};

/// Inserts `<q:NAME>` at the cursor in whichever prompt box was last focused (positive by default). If the
/// cursor sits right after an existing bare reference to the same dataset, removes it instead (toggle).
export const insertQuarryTag = (name: string): void => {
    let [promptBox, cursorPos] = uiImprover.getLastSelectedTextbox();
    if (!promptBox) {
        promptBox = getRequiredElementById(
            "alt_prompt_textbox",
        ) as HTMLTextAreaElement;
        cursorPos = promptBox.value.length;
    }
    const prefix = promptBox.value.substring(0, cursorPos);
    const suffix = promptBox.value.substring(cursorPos);
    const trimmed = trimSpaces(prefix);
    const match = matchQuarryTag(trimmed, name);
    if (match && match.length > 0) {
        const last = match[match.length - 1];
        if (trimmed.endsWith(trimSpaces(last))) {
            promptBox.value =
                `${trimSpaces(trimmed.substring(0, trimmed.length - last.length))} ${suffix}`.trim();
            triggerChangeFor(promptBox);
            recomputeReferences();
            return;
        }
    }
    const tag = `<q:${name}>`;
    promptBox.value =
        `${trimSpaces(prefix)} ${tag} ${trimSpaces(suffix)}`.trim();
    promptBox.selectionStart = cursorPos + tag.length + 1;
    promptBox.selectionEnd = cursorPos + tag.length + 1;
    promptBox.focus();
    triggerChangeFor(promptBox);
    recomputeReferences();
};
