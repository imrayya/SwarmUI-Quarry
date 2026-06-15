// Shared DTOs mirroring the JObject shapes returned by the Quarry API endpoints (QuarryExtension.cs).

export type ColumnKind = "scalar" | "list";

export interface ColumnDto {
    name: string;
    kind: ColumnKind;
}

export interface DatasetDto {
    name: string;
    columns: ColumnDto[];
    resolvedPromptColumn: string | null;
    configuredPromptColumn: string | null;
    configuredTagColumns: string[];
    rowCount: number | null;
    error: string | null;
}

export interface SettingsResponse {
    success: boolean;
    enabled?: boolean;
    datasetsFolder?: string;
    active?: boolean;
    count?: number;
    datasets?: DatasetDto[];
    message?: string;
    error?: string;
}

export interface PreviewResponse {
    success: boolean;
    dataset?: string;
    columns?: string[];
    rows?: string[][];
    error?: string;
}

export interface ReferencesResponse {
    success: boolean;
    names?: string[];
}
