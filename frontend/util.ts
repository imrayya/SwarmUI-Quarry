export const escapeHtml = (text: string): string => {
    const div = document.createElement("div");
    div.textContent = text;
    return div.innerHTML.replace(/"/g, "&quot;").replace(/'/g, "&#39;");
};

export const formatBytes = (bytes: number | null | undefined): string => {
    if (bytes == null || bytes < 0) {
        return "—";
    }
    const units = ["B", "KB", "MB", "GB", "TB"];
    let value = bytes;
    let unit = 0;
    while (value >= 1000 && unit < units.length - 1) {
        value /= 1000;
        unit++;
    }
    const decimals = unit === 0 || value >= 100 ? 0 : 1;
    return `${value.toFixed(decimals)} ${units[unit]}`;
};

export const datasetFolder = (name: string): string | null => {
    const slash = name.lastIndexOf("/");
    return slash > 0 ? name.slice(0, slash) : null;
};

export const datasetLeafName = (name: string): string =>
    name.slice(name.lastIndexOf("/") + 1);

export interface NameFolderGroup<T> {
    folder: string;
    items: T[];
}

export const groupByFolder = <T extends { name: string }>(
    items: T[],
): { loose: T[]; groups: NameFolderGroup<T>[] } => {
    const loose: T[] = [];
    const byFolder = new Map<string, T[]>();
    for (const item of items) {
        const folder = datasetFolder(item.name);
        if (folder === null) {
            loose.push(item);
            continue;
        }
        const existing = byFolder.get(folder);
        if (existing) {
            existing.push(item);
        } else {
            byFolder.set(folder, [item]);
        }
    }
    const groups = Array.from(byFolder, ([folder, grouped]) => ({
        folder,
        items: grouped,
    })).sort((a, b) => a.folder.localeCompare(b.folder));
    return { loose, groups };
};
