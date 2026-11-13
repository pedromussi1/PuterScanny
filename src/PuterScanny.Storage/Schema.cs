namespace PuterScanny.Storage;

internal static class Schema
{
    public const string Create = """
        CREATE TABLE meta (
            key   TEXT PRIMARY KEY,
            value TEXT
        );

        CREATE TABLE session (
            id                 INTEGER PRIMARY KEY CHECK (id = 1),
            root_path          TEXT    NOT NULL,
            mode               INTEGER NOT NULL,
            is_elevated        INTEGER NOT NULL,
            started_utc        INTEGER NOT NULL,
            completed_utc      INTEGER,
            status             INTEGER NOT NULL,
            file_count         INTEGER NOT NULL DEFAULT 0,
            dir_count          INTEGER NOT NULL DEFAULT 0,
            total_size         INTEGER NOT NULL DEFAULT 0,
            total_size_on_disk INTEGER NOT NULL DEFAULT 0,
            issue_count        INTEGER NOT NULL DEFAULT 0,
            stream_count       INTEGER NOT NULL DEFAULT 0,
            stream_bytes       INTEGER NOT NULL DEFAULT 0,
            cluster_size       INTEGER NOT NULL DEFAULT 0,
            machine_name       TEXT,
            app_version        TEXT,
            error_message      TEXT,
            method             TEXT
        );

        CREATE TABLE entry (
            id                 INTEGER PRIMARY KEY,
            parent_id          INTEGER,
            name               TEXT    NOT NULL,
            path               TEXT    NOT NULL,
            is_dir             INTEGER NOT NULL,
            depth              INTEGER NOT NULL,
            size               INTEGER NOT NULL,
            size_on_disk       INTEGER NOT NULL,
            attributes         INTEGER NOT NULL,
            created_utc        INTEGER NOT NULL,
            modified_utc       INTEGER NOT NULL,
            accessed_utc       INTEGER NOT NULL,
            reparse_tag        INTEGER NOT NULL,
            flags              INTEGER NOT NULL,
            stream_bytes       INTEGER NOT NULL,
            link_target        TEXT,
            total_size         INTEGER NOT NULL,
            total_size_on_disk INTEGER NOT NULL,
            file_count         INTEGER NOT NULL,
            dir_count          INTEGER NOT NULL
        );

        CREATE TABLE stream (
            entry_id INTEGER NOT NULL,
            name     TEXT    NOT NULL,
            size     INTEGER NOT NULL
        );

        CREATE TABLE issue (
            id          INTEGER PRIMARY KEY,
            path        TEXT    NOT NULL,
            kind        INTEGER NOT NULL,
            win32_error INTEGER NOT NULL,
            message     TEXT    NOT NULL
        );

        -- Programs found on the PC (Apps & features, Store, Steam, Epic, GOG, Chocolatey, Scoop).
        CREATE TABLE app (
            id                  INTEGER PRIMARY KEY,
            name                TEXT    NOT NULL,
            publisher           TEXT,
            version             TEXT,
            install_location    TEXT,
            install_date        INTEGER,
            estimated_size      INTEGER,
            source              INTEGER NOT NULL,
            uninstall_command   TEXT,
            is_system_component INTEGER NOT NULL,
            is_framework        INTEGER NOT NULL,
            source_key          TEXT,
            extra_folders       TEXT,
            owned_file_count    INTEGER NOT NULL DEFAULT 0,
            measured_bytes      INTEGER NOT NULL DEFAULT 0,
            measured_files      INTEGER NOT NULL DEFAULT 0,
            last_used           INTEGER,
            use_count           INTEGER NOT NULL DEFAULT 0,
            active_seconds      INTEGER NOT NULL DEFAULT 0,
            uninstall_confidence INTEGER NOT NULL DEFAULT 0,
            uninstall_reason    TEXT
        );

        -- What each entry is and who it belongs to.
        CREATE TABLE analysis (
            entry_id      INTEGER PRIMARY KEY,
            owner_app_id  INTEGER,
            owner_exact   INTEGER NOT NULL DEFAULT 0,
            location_rule TEXT,
            category      TEXT,
            type_label    TEXT,
            format_id     TEXT,
            mismatch      INTEGER NOT NULL DEFAULT 0,
            read_status   INTEGER NOT NULL DEFAULT 0,
            protection    INTEGER NOT NULL DEFAULT 0,
            contained_protection INTEGER NOT NULL DEFAULT 0,
            last_used     INTEGER,
            use_count     INTEGER NOT NULL DEFAULT 0,
            active_seconds INTEGER NOT NULL DEFAULT 0,
            contained_last_used INTEGER,
            contained_last_modified INTEGER,
            verdict       INTEGER,
            confidence    INTEGER NOT NULL DEFAULT 0,
            action        INTEGER NOT NULL DEFAULT 0,
            headline      TEXT,
            fact_flags    INTEGER NOT NULL DEFAULT 0,
            reclaim_bytes INTEGER NOT NULL DEFAULT 0,
            review_bytes  INTEGER NOT NULL DEFAULT 0,
            tool_bytes    INTEGER NOT NULL DEFAULT 0,
            top_reasons   TEXT
        );

        -- Items the user cleaned up (moved to the Recycle Bin or quarantine) since the scan.
        CREATE TABLE removed (
            entry_id INTEGER PRIMARY KEY,
            batch_id TEXT NOT NULL,
            method   INTEGER NOT NULL,
            removed_utc INTEGER NOT NULL
        );

        -- Identical files (byte for byte). One copy per group is the one to keep.
        CREATE TABLE duplicate (
            entry_id  INTEGER PRIMARY KEY,
            group_id  INTEGER NOT NULL,
            is_keeper INTEGER NOT NULL,
            copies    INTEGER NOT NULL,
            keeper_path TEXT NOT NULL
        );

        -- Individual usage records (which source said what).
        CREATE TABLE usage (
            entry_id  INTEGER NOT NULL,
            source    INTEGER NOT NULL,
            last_used INTEGER,
            count     INTEGER,
            active_seconds INTEGER,
            detail    TEXT NOT NULL
        );

        -- Usage records for whole programs (Steam playtime, Store apps).
        CREATE TABLE app_usage (
            app_id    INTEGER NOT NULL,
            source    INTEGER NOT NULL,
            last_used INTEGER,
            count     INTEGER,
            active_seconds INTEGER,
            detail    TEXT NOT NULL
        );

        -- Which usage sources could be read on this PC.
        CREATE TABLE usage_source (
            source    INTEGER PRIMARY KEY,
            available INTEGER NOT NULL,
            items     INTEGER NOT NULL,
            note      TEXT NOT NULL
        );

        -- Concrete things that depend on an item (services, tasks, running programs, …).
        CREATE TABLE reference (
            entry_id INTEGER NOT NULL,
            kind     INTEGER NOT NULL,
            level    INTEGER NOT NULL,
            detail   TEXT    NOT NULL
        );

        -- Details read from program files (EXE, DLL, SYS, MSI).
        CREATE TABLE pe_info (
            entry_id          INTEGER PRIMARY KEY,
            machine           TEXT,
            subsystem         TEXT,
            is_dll            INTEGER NOT NULL,
            is_driver         INTEGER NOT NULL,
            is_dotnet         INTEGER NOT NULL,
            link_time         INTEGER,
            company           TEXT,
            product           TEXT,
            description       TEXT,
            file_version      TEXT,
            product_version   TEXT,
            original_filename TEXT,
            internal_name     TEXT,
            copyright         TEXT,
            has_embedded_sig  INTEGER NOT NULL,
            sig_state         INTEGER NOT NULL,
            sig_kind          INTEGER NOT NULL,
            signed_by         TEXT,
            sig_error         INTEGER NOT NULL
        );

        CREATE TABLE extension_info (
            ext               TEXT PRIMARY KEY,
            files             INTEGER NOT NULL,
            bytes             INTEGER NOT NULL,
            label             TEXT,
            category          TEXT,
            known             INTEGER NOT NULL,
            windows_type_name TEXT,
            opens_with        TEXT
        );

        CREATE TABLE inventory_problem (
            message TEXT NOT NULL
        );
        """;

    /// <summary>Indexes are built once after the bulk load, which is much faster than maintaining them per insert.</summary>
    public const string CreateIndexes = """
        CREATE INDEX IF NOT EXISTS ix_entry_parent ON entry (parent_id, total_size DESC);
        CREATE INDEX IF NOT EXISTS ix_entry_files_by_size ON entry (is_dir, size DESC);
        CREATE INDEX IF NOT EXISTS ix_stream_entry ON stream (entry_id);
        CREATE INDEX IF NOT EXISTS ix_reference_entry ON reference (entry_id);
        CREATE INDEX IF NOT EXISTS ix_usage_entry ON usage (entry_id);
        CREATE INDEX IF NOT EXISTS ix_duplicate_group ON duplicate (group_id);
        ANALYZE;
        """;
}
