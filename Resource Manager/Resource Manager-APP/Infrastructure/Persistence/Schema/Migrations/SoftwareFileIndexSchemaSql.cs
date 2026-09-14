namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal static class SoftwareFileIndexSchemaSql
{
    public const string Current = """
        CREATE TABLE IF NOT EXISTS software_index_roots (
            id INTEGER PRIMARY KEY,
            software_id TEXT NOT NULL,
            software_name TEXT NOT NULL,
            root_path TEXT NOT NULL,
            root_path_key TEXT NOT NULL,
            root_kind TEXT NOT NULL,
            total_bytes INTEGER NOT NULL DEFAULT 0 CHECK(total_bytes >= 0),
            file_count INTEGER NOT NULL DEFAULT 0 CHECK(file_count >= 0),
            last_indexed_utc_ticks INTEGER,
            UNIQUE(software_id, root_path_key)
        ) STRICT;

        CREATE INDEX IF NOT EXISTS ix_software_index_roots_software
            ON software_index_roots(software_id);

        CREATE TABLE IF NOT EXISTS software_index_entries (
            id INTEGER PRIMARY KEY,
            root_id INTEGER NOT NULL REFERENCES software_index_roots(id) ON DELETE CASCADE,
            relative_path TEXT NOT NULL,
            relative_path_key TEXT NOT NULL,
            file_name TEXT NOT NULL,
            extension TEXT NOT NULL,
            size_bytes INTEGER NOT NULL CHECK(size_bytes >= 0),
            last_write_utc_ticks INTEGER NOT NULL,
            attributes INTEGER NOT NULL,
            UNIQUE(root_id, relative_path_key)
        ) STRICT;

        CREATE INDEX IF NOT EXISTS ix_software_index_entries_root
            ON software_index_entries(root_id);
        CREATE INDEX IF NOT EXISTS ix_software_index_entries_extension
            ON software_index_entries(extension);

        CREATE VIRTUAL TABLE IF NOT EXISTS software_file_name_fts USING fts5(
            file_name,
            content='software_index_entries',
            content_rowid='id',
            tokenize='trigram',
            detail='none',
            columnsize=0
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS software_file_path_fts USING fts5(
            relative_path,
            content='software_index_entries',
            content_rowid='id',
            tokenize='unicode61 remove_diacritics 2',
            detail='none',
            columnsize=0
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS software_root_search_fts USING fts5(
            software_name,
            content='software_index_roots',
            content_rowid='id',
            tokenize='trigram',
            detail='none',
            columnsize=0
        );

        CREATE TRIGGER IF NOT EXISTS software_index_entries_ai AFTER INSERT ON software_index_entries BEGIN
            INSERT INTO software_file_name_fts(rowid, file_name) VALUES (new.id, new.file_name);
            INSERT INTO software_file_path_fts(rowid, relative_path) VALUES (new.id, new.relative_path);
        END;

        CREATE TRIGGER IF NOT EXISTS software_index_entries_ad AFTER DELETE ON software_index_entries BEGIN
            INSERT INTO software_file_name_fts(software_file_name_fts, rowid, file_name)
            VALUES ('delete', old.id, old.file_name);
            INSERT INTO software_file_path_fts(software_file_path_fts, rowid, relative_path)
            VALUES ('delete', old.id, old.relative_path);
        END;

        CREATE TRIGGER IF NOT EXISTS software_index_entries_au
        AFTER UPDATE OF file_name, relative_path ON software_index_entries
        WHEN old.file_name <> new.file_name OR old.relative_path <> new.relative_path
        BEGIN
            INSERT INTO software_file_name_fts(software_file_name_fts, rowid, file_name)
            VALUES ('delete', old.id, old.file_name);
            INSERT INTO software_file_path_fts(software_file_path_fts, rowid, relative_path)
            VALUES ('delete', old.id, old.relative_path);
            INSERT INTO software_file_name_fts(rowid, file_name) VALUES (new.id, new.file_name);
            INSERT INTO software_file_path_fts(rowid, relative_path) VALUES (new.id, new.relative_path);
        END;

        CREATE TRIGGER IF NOT EXISTS software_index_roots_ai AFTER INSERT ON software_index_roots BEGIN
            INSERT INTO software_root_search_fts(rowid, software_name) VALUES (new.id, new.software_name);
        END;

        CREATE TRIGGER IF NOT EXISTS software_index_roots_ad AFTER DELETE ON software_index_roots BEGIN
            INSERT INTO software_root_search_fts(software_root_search_fts, rowid, software_name)
            VALUES ('delete', old.id, old.software_name);
        END;

        CREATE TRIGGER IF NOT EXISTS software_index_roots_au
        AFTER UPDATE OF software_name ON software_index_roots
        WHEN old.software_name <> new.software_name
        BEGIN
            INSERT INTO software_root_search_fts(software_root_search_fts, rowid, software_name)
            VALUES ('delete', old.id, old.software_name);
            INSERT INTO software_root_search_fts(rowid, software_name) VALUES (new.id, new.software_name);
        END;
        """;
}
