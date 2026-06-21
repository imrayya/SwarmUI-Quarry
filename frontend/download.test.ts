import { describe, expect, it } from "@jest/globals";
import {
    progressPercent,
    renderProgressInfo,
    renderRemoteDatasetName,
    renderRemoteDatasetRow,
    renderRemoteDatasets,
    renderRemoteFolderHeaderRow,
    sourceRepoUrl,
} from "./download";
import type { RemoteDatasetDto } from "./types";
import { type FolderNode, formatBytes } from "./util";

const makeRemote = (name: string, installed = false): RemoteDatasetDto => ({
    name,
    repoPath: `${name}.lance`,
    sizeBytes: 100,
    fileCount: 1,
    installed,
});

describe("renderRemoteDatasetRow", () => {
    it("renders a not-installed dataset with an unchecked selection checkbox", () => {
        const html = renderRemoteDatasetRow({
            name: "Gustavosta.Stable-Diffusion-Prompts",
            repoPath: "Gustavosta.Stable-Diffusion-Prompts.lance",
            sizeBytes: 9763643,
            fileCount: 4,
            installed: false,
        });
        expect(html).toContain(
            'data-dataset="Gustavosta.Stable-Diffusion-Prompts"',
        );
        // The name links to the source HuggingFace repo it was built from.
        expect(html).toContain(
            'href="https://huggingface.co/datasets/Gustavosta/Stable-Diffusion-Prompts"',
        );
        // A selection checkbox, flagged not-installed, with no per-row download button.
        expect(html).toContain("quarry-remote-select");
        expect(html).toContain('data-installed="false"');
        expect(html).not.toContain(">Download<");
        expect(html).not.toContain(">Redownload<");
        expect(html).toContain("9.8 MB");
        // The file count is intentionally not shown.
        expect(html).not.toContain("file");
        expect(html).not.toContain("quarry-remote-installed");
        expect(html).not.toContain("✓");
    });

    it("renders an installed dataset with a checkmark and a pre-set redownload flag", () => {
        const html = renderRemoteDatasetRow({
            name: "DamarJati.SD-Prompts",
            repoPath: "DamarJati.SD-Prompts.lance",
            sizeBytes: 22208,
            fileCount: 1,
            installed: true,
        });
        expect(html).toContain("quarry-remote-installed");
        expect(html).toContain("quarry-remote-check");
        expect(html).toContain("✓");
        expect(html).toContain('data-installed="true"');
        expect(html).not.toContain(">Redownload<");
        expect(html).toContain("22.2 KB");
    });

    it("escapes the dataset name", () => {
        const html = renderRemoteDatasetRow({
            name: "<evil>",
            repoPath: "<evil>.lance",
            sizeBytes: 0,
            fileCount: 2,
            installed: false,
        });
        expect(html).toContain("&lt;evil&gt;");
        expect(html).not.toContain("<evil>");
    });
});

describe("renderRemoteDatasets", () => {
    it("shows a hint when empty", () => {
        expect(renderRemoteDatasets([])).toContain("No datasets available");
    });

    it("renders a table with one row per dataset", () => {
        const html = renderRemoteDatasets([
            {
                name: "a",
                repoPath: "a.lance",
                sizeBytes: 100,
                fileCount: 1,
                installed: false,
            },
            {
                name: "b",
                repoPath: "b.lance",
                sizeBytes: 200,
                fileCount: 2,
                installed: true,
            },
        ]);
        expect(html).toContain("quarry-remote-table");
        expect(html).toContain('data-dataset="a"');
        expect(html).toContain('data-dataset="b"');
    });

    it("groups nested datasets under a collapsible folder header (collapsed by default)", () => {
        const html = renderRemoteDatasets([
            makeRemote("loose"),
            makeRemote("X779.Danbooruwildcards/DTR2024_1boy"),
            makeRemote("X779.Danbooruwildcards/DTR2024_1girl"),
        ]);
        // A collapsible folder header row, collapsed by default, with a 3-column-wide header and a count of 2.
        expect(html).toContain('class="quarry-folder-row quarry-collapsed"');
        expect(html).toContain('data-folder="X779.Danbooruwildcards"');
        expect(html).toContain("quarry-folder-toggle");
        expect(html).toContain('aria-expanded="false"');
        expect(html).toContain('colspan="3"');
        // Member rows keep the full name in data-dataset but display only the leaf.
        expect(html).toContain(
            'data-dataset="X779.Danbooruwildcards/DTR2024_1boy"',
        );
        expect(html).toContain(">DTR2024_1boy</a>");
        expect(html).not.toContain(">X779.Danbooruwildcards/DTR2024_1boy</a>");
        // The top-level dataset stays loose (not wrapped in a folder group's name link).
        expect(html).toContain('data-dataset="loose"');
    });

    it("renders a folder expanded when it is named in the expanded set", () => {
        const html = renderRemoteDatasets(
            [makeRemote("anime/1girl")],
            new Set(["anime"]),
        );
        expect(html).toContain('class="quarry-folder-row"');
        expect(html).not.toContain("quarry-collapsed");
        expect(html).toContain('aria-expanded="true"');
    });

    it("nests a sub-folder inside its parent rather than beside it", () => {
        const html = renderRemoteDatasets([
            makeRemote("tags/X779.Danbooruwildcards/DTR2024_1girl"),
        ]);
        expect(html).toContain('data-folder="tags"');
        expect(html).toContain(
            'data-folder="tags/X779.Danbooruwildcards" data-parent="tags"',
        );
        expect(html).toContain(
            'data-dataset="tags/X779.Danbooruwildcards/DTR2024_1girl" data-parent="tags/X779.Danbooruwildcards"',
        );
        expect(html).toContain(">DTR2024_1girl</a>");
    });
});

describe("renderRemoteFolderHeaderRow", () => {
    const node: FolderNode<RemoteDatasetDto> = {
        path: "anime",
        name: "anime",
        folders: [],
        items: [makeRemote("anime/1girl"), makeRemote("anime/2girls")],
    };

    it("renders a 3-column header row with a recursive dataset count", () => {
        const html = renderRemoteFolderHeaderRow(node, 0, new Set(["anime"]));
        expect(html).toContain('class="quarry-folder-row"');
        expect(html).toContain('colspan="3"');
        expect(html).toContain('aria-expanded="true"');
        expect(html).toContain('<span class="quarry-folder-name">anime</span>');
        expect(html).toContain('title="2 dataset(s)"');
    });

    it("marks the header collapsed when not expanded", () => {
        const html = renderRemoteFolderHeaderRow(node, 0, new Set());
        expect(html).toContain("quarry-collapsed");
        expect(html).toContain('aria-expanded="false"');
    });
});

describe("sourceRepoUrl", () => {
    it("maps a dataset name to its source repo by replacing the first dot with a slash", () => {
        expect(sourceRepoUrl("Gustavosta.Stable-Diffusion-Prompts")).toBe(
            "https://huggingface.co/datasets/Gustavosta/Stable-Diffusion-Prompts",
        );
        expect(sourceRepoUrl("succinctly.midjourney-prompts")).toBe(
            "https://huggingface.co/datasets/succinctly/midjourney-prompts",
        );
    });

    it("splits on the first dot only, leaving later dots in the repo name", () => {
        // HuggingFace org/user names never contain a dot, so the first dot is always the `/` separator;
        // any further dots belong to the repo name and are preserved.
        expect(sourceRepoUrl("org.repo.v2")).toBe(
            "https://huggingface.co/datasets/org/repo.v2",
        );
    });

    it("derives the source repo from the top-level folder for a nested dataset", () => {
        // Nested datasets carry a "parent/leaf" name; the source repo is named by the parent folder only.
        expect(sourceRepoUrl("X779.Danbooruwildcards/DTR2024_1boy")).toBe(
            "https://huggingface.co/datasets/X779/Danbooruwildcards",
        );
        expect(sourceRepoUrl("org.repo.v2/sub/leaf")).toBe(
            "https://huggingface.co/datasets/org/repo.v2",
        );
    });

    it("links a dot-less folder to its HuggingFace org/user page", () => {
        // A bare folder name (no org.repo dot) is a HuggingFace org/user; link to its page, preserving case.
        expect(sourceRepoUrl("CyberHarem")).toBe(
            "https://huggingface.co/CyberHarem",
        );
        // The org is taken from the top-level folder, even when the leaf carries a dot.
        expect(sourceRepoUrl("noseparator/leaf.v2")).toBe(
            "https://huggingface.co/noseparator",
        );
    });

    it("returns null when the top-level folder can't name an org", () => {
        expect(sourceRepoUrl("")).toBeNull();
        expect(sourceRepoUrl(".leading")).toBeNull();
        expect(sourceRepoUrl("trailing.")).toBeNull();
    });
});

describe("renderRemoteDatasetName", () => {
    it("links the name to its source HuggingFace repo, opening in a new tab", () => {
        const html = renderRemoteDatasetName("succinctly.midjourney-prompts");
        expect(html).toContain(
            'href="https://huggingface.co/datasets/succinctly/midjourney-prompts"',
        );
        expect(html).toContain('target="_blank"');
        expect(html).toContain(">succinctly.midjourney-prompts</a>");
    });

    it("links a nested dataset to its top-level source repo while showing the full name", () => {
        const html = renderRemoteDatasetName(
            "X779.Danbooruwildcards/DTR2024_1boy",
        );
        expect(html).toContain(
            'href="https://huggingface.co/datasets/X779/Danbooruwildcards"',
        );
        expect(html).toContain(">X779.Danbooruwildcards/DTR2024_1boy</a>");
    });

    it("links a dot-less name to its HuggingFace org/user page", () => {
        const html = renderRemoteDatasetName("CyberHarem");
        expect(html).toContain('href="https://huggingface.co/CyberHarem"');
        expect(html).toContain(">CyberHarem</a>");
    });

    it("escapes the name in both the link and its title", () => {
        const evil = renderRemoteDatasetName("<evil>");
        expect(evil).toContain("&lt;evil&gt;");
        expect(evil).not.toContain("<evil>");
    });

    it("falls back to plain escaped text when no source repo can be derived", () => {
        expect(renderRemoteDatasetName("trailing.")).toBe("trailing.");
        const evil = renderRemoteDatasetName(".<evil>");
        expect(evil).toBe(".&lt;evil&gt;");
        expect(evil).not.toContain("<a");
    });
});

describe("progressPercent", () => {
    it("returns 0 when the total is unknown", () => {
        expect(
            progressPercent({ success: true, bytesTotal: 0, bytesDone: 0 }),
        ).toBe(0);
    });

    it("rounds the ratio and clamps to 100", () => {
        expect(
            progressPercent({
                success: true,
                bytesDone: 1400,
                bytesTotal: 3400,
            }),
        ).toBe(41);
        expect(
            progressPercent({
                success: true,
                bytesDone: 9999,
                bytesTotal: 1000,
            }),
        ).toBe(100);
    });
});

describe("renderProgressInfo", () => {
    it("shows a starting/finalizing label for those phases", () => {
        expect(renderProgressInfo({ success: true, state: "starting" })).toBe(
            "Starting…",
        );
        expect(renderProgressInfo({ success: true, state: "finalizing" })).toBe(
            "Finalizing…",
        );
    });

    it("shows percent, sizes, speed, and file count while downloading", () => {
        const info = renderProgressInfo({
            success: true,
            state: "downloading",
            bytesDone: 1_400_000_000,
            bytesTotal: 3_400_000_000,
            perSecond: 12_000_000,
            filesDone: 3,
            filesTotal: 21,
        });
        expect(info).toContain("41%");
        expect(info).toContain("1.4 GB");
        expect(info).toContain("3.4 GB");
        expect(info).toContain("12.0 MB/s");
        expect(info).toContain("file 3/21");
    });

    it("omits the speed when it is zero", () => {
        const info = renderProgressInfo({
            success: true,
            state: "downloading",
            bytesDone: 100,
            bytesTotal: 200,
            perSecond: 0,
            filesTotal: 0,
        });
        expect(info).not.toContain("/s");
        expect(info).not.toContain("file ");
    });
});

describe("formatBytes", () => {
    it("formats across units", () => {
        expect(formatBytes(0)).toBe("0 B");
        expect(formatBytes(153)).toBe("153 B");
        expect(formatBytes(22208)).toBe("22.2 KB");
        expect(formatBytes(9763643)).toBe("9.8 MB");
        expect(formatBytes(340010000)).toBe("340 MB");
        expect(formatBytes(3417600000)).toBe("3.4 GB");
    });

    it("returns an em-dash for null/undefined/negative", () => {
        expect(formatBytes(null)).toBe("—");
        expect(formatBytes(undefined)).toBe("—");
        expect(formatBytes(-1)).toBe("—");
    });
});
