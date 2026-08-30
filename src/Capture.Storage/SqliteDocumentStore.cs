using System.Globalization;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Store;
using Microsoft.Data.Sqlite;

namespace Capture.Storage;

public sealed class SqliteDocumentStore : IDocumentStore
{
    private readonly IAppPaths _paths;
    private readonly string _connectionString;

    public SqliteDocumentStore(IAppPaths paths)
    {
        _paths = paths;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var commands = new[]
        {
            "PRAGMA journal_mode=WAL;",
            """
            CREATE TABLE IF NOT EXISTS documents (
              id TEXT PRIMARY KEY,
              original_file_name TEXT NOT NULL,
              stored_path TEXT NOT NULL,
              source INTEGER NOT NULL,
              profile_id TEXT,
              status INTEGER NOT NULL,
              page_count INTEGER NOT NULL,
              created_utc TEXT NOT NULL,
              error_message TEXT
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS pages (
              id TEXT PRIMARY KEY,
              document_id TEXT NOT NULL,
              page_number INTEGER NOT NULL,
              image_path TEXT NOT NULL,
              width INTEGER NOT NULL,
              height INTEGER NOT NULL,
              dpi INTEGER NOT NULL,
              FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE
            );
            """,
            "CREATE INDEX IF NOT EXISTS ix_pages_document ON pages(document_id, page_number);",
            """
            CREATE TABLE IF NOT EXISTS batches (
              id TEXT PRIMARY KEY,
              created_utc TEXT NOT NULL
            );
            """
        };

        foreach (var sql in commands)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await TryAddColumnAsync(connection, "documents", "batch_id", "TEXT", cancellationToken)
            .ConfigureAwait(false);
        await TryAddColumnAsync(connection, "batches", "number", "INTEGER NOT NULL DEFAULT 0", cancellationToken)
            .ConfigureAwait(false);
        await TryAddColumnAsync(connection, "batches", "watch_folder_entry_id", "TEXT", cancellationToken)
            .ConfigureAwait(false);
        await TryAddColumnAsync(connection, "pages", "source_page_number", "INTEGER", cancellationToken)
            .ConfigureAwait(false);
        await TryAddColumnAsync(connection, "documents", "redaction_status", "INTEGER NOT NULL DEFAULT 0", cancellationToken)
            .ConfigureAwait(false);
        await TryAddColumnAsync(connection, "documents", "redacted_path", "TEXT", cancellationToken)
            .ConfigureAwait(false);
        await TryAddColumnAsync(connection, "documents", "redaction_error", "TEXT", cancellationToken)
            .ConfigureAwait(false);
        await BackfillBatchesAsync(connection, cancellationToken).ConfigureAwait(false);
        await BackfillBatchNumbersAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    // Checks the schema before altering it rather than attempting the ALTER and swallowing whatever
    // SqliteException comes back — that used to hide real failures (locking, corruption, a malformed
    // migration) behind the same "column already exists" assumption.
    private static async Task TryAddColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(connection, table, column, cancellationToken).ConfigureAwait(false))
            return;

        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var nameOrdinal = -1;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (nameOrdinal < 0)
                nameOrdinal = reader.GetOrdinal("name");
            if (string.Equals(reader.GetString(nameOrdinal), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static async Task BackfillBatchesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO batches (id, created_utc)
                SELECT id, created_utc FROM documents WHERE batch_id IS NULL;
                """;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE documents SET batch_id = id WHERE batch_id IS NULL;";
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task BackfillBatchNumbersAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT id FROM batches WHERE IFNULL(number, 0) = 0 ORDER BY created_utc, id;";
        var ids = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(reader.GetString(0));
        }

        var number = 1;
        await using (var max = connection.CreateCommand())
        {
            max.CommandText = "SELECT IFNULL(MAX(number), 0) FROM batches;";
            number = Convert.ToInt32(await max.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) + 1;
        }

        foreach (var id in ids)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE batches SET number = $number WHERE id = $id;";
            update.Parameters.AddWithValue("$number", number++);
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveAsync(
        CaptureDocument document,
        IReadOnlyList<DocumentPage> pages,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var deletePages = connection.CreateCommand())
        {
            deletePages.Transaction = (SqliteTransaction)transaction;
            deletePages.CommandText = "DELETE FROM pages WHERE document_id = $id;";
            deletePages.Parameters.AddWithValue("$id", document.Id.ToString("D"));
            await deletePages.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = (SqliteTransaction)transaction;
            upsert.CommandText = """
                INSERT INTO documents (id, original_file_name, stored_path, source, profile_id, batch_id, status, page_count, created_utc, error_message, redaction_status, redacted_path, redaction_error)
                VALUES ($id, $original, $stored, $source, $profile, $batch, $status, $pages, $created, $error, $redactionStatus, $redactedPath, $redactionError)
                ON CONFLICT(id) DO UPDATE SET
                  original_file_name = excluded.original_file_name,
                  stored_path = excluded.stored_path,
                  source = excluded.source,
                  profile_id = excluded.profile_id,
                  batch_id = excluded.batch_id,
                  status = excluded.status,
                  page_count = excluded.page_count,
                  error_message = excluded.error_message,
                  redaction_status = excluded.redaction_status,
                  redacted_path = excluded.redacted_path,
                  redaction_error = excluded.redaction_error;
                """;
            AddDocumentParameters(upsert, document);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var page in pages)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO pages (id, document_id, page_number, image_path, width, height, dpi, source_page_number)
                VALUES ($id, $document, $number, $path, $width, $height, $dpi, $sourceNumber);
                """;
            insert.Parameters.AddWithValue("$id", page.Id.ToString("D"));
            insert.Parameters.AddWithValue("$document", page.DocumentId.ToString("D"));
            insert.Parameters.AddWithValue("$number", page.PageNumber);
            insert.Parameters.AddWithValue("$path", page.ImagePath);
            insert.Parameters.AddWithValue("$width", page.Width);
            insert.Parameters.AddWithValue("$height", page.Height);
            insert.Parameters.AddWithValue("$dpi", page.Dpi);
            insert.Parameters.AddWithValue("$sourceNumber", page.SourcePageNumber);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(CaptureDocument document, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE documents SET
              stored_path = $stored,
              profile_id = $profile,
              batch_id = $batch,
              status = $status,
              page_count = $pages,
              error_message = $error,
              redaction_status = $redactionStatus,
              redacted_path = $redactedPath,
              redaction_error = $redactionError
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", document.Id.ToString("D"));
        command.Parameters.AddWithValue("$stored", document.StoredPath);
        command.Parameters.AddWithValue("$profile", (object?)document.ProfileId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$batch", (object?)document.BatchId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)document.Status);
        command.Parameters.AddWithValue("$pages", document.PageCount);
        command.Parameters.AddWithValue("$error", (object?)document.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$redactionStatus", (int)document.RedactionStatus);
        command.Parameters.AddWithValue("$redactedPath", (object?)document.RedactedPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$redactionError", (object?)document.RedactionError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CaptureDocument>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.original_file_name, d.stored_path, d.source, d.profile_id, d.status, d.page_count, d.created_utc, d.error_message, d.batch_id, d.redaction_status, d.redacted_path, d.redaction_error
            FROM documents d
            LEFT JOIN batches b ON b.id = d.batch_id
            ORDER BY IFNULL(b.created_utc, d.created_utc), d.created_utc, d.id;
            """;

        var results = new List<CaptureDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadDocument(reader));

        return results;
    }

    public async Task<IReadOnlyList<DocumentPage>> GetPagesAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, document_id, page_number, image_path, width, height, dpi, source_page_number
            FROM pages
            WHERE document_id = $id
            ORDER BY page_number;
            """;
        command.Parameters.AddWithValue("$id", documentId.ToString("D"));

        var results = new List<DocumentPage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var pageNumber = reader.GetInt32(2);
            results.Add(new DocumentPage
            {
                Id = Guid.Parse(reader.GetString(0)),
                DocumentId = Guid.Parse(reader.GetString(1)),
                PageNumber = pageNumber,
                ImagePath = reader.GetString(3),
                Width = reader.GetInt32(4),
                Height = reader.GetInt32(5),
                Dpi = reader.GetInt32(6),
                // Rows written before this column existed have no source page recorded — falling back to
                // PageNumber preserves their existing (already-established) behavior rather than guessing.
                SourcePageNumber = reader.IsDBNull(7) ? pageNumber : reader.GetInt32(7)
            });
        }

        return results;
    }

    public async Task<CaptureBatch> CreateBatchAsync(
        Guid? watchFolderEntryId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var number = 1;
        await using (var max = connection.CreateCommand())
        {
            max.CommandText = "SELECT IFNULL(MAX(number), 0) FROM batches;";
            number = Convert.ToInt32(await max.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) + 1;
        }

        var batch = new CaptureBatch { Number = number, WatchFolderEntryId = watchFolderEntryId };
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO batches (id, created_utc, number, watch_folder_entry_id)
            VALUES ($id, $created, $number, $folder);
            """;
        command.Parameters.AddWithValue("$id", batch.Id.ToString("D"));
        command.Parameters.AddWithValue("$created", batch.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$number", batch.Number);
        command.Parameters.AddWithValue("$folder", (object?)watchFolderEntryId?.ToString("D") ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return batch;
    }

    public async Task<CaptureBatch?> GetLatestBatchForFolderAsync(
        Guid watchFolderEntryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, created_utc, number
            FROM batches
            WHERE watch_folder_entry_id = $folder
            ORDER BY created_utc DESC, rowid DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$folder", watchFolderEntryId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new CaptureBatch
        {
            Id = Guid.Parse(reader.GetString(0)),
            CreatedUtc = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Number = reader.GetInt32(2),
            WatchFolderEntryId = watchFolderEntryId
        };
    }

    public async Task<int> GetBatchNumberAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IFNULL(number, 1) FROM batches WHERE id = $id;";
        command.Parameters.AddWithValue("$id", batchId.ToString("D"));
        var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is null or DBNull ? 1 : Convert.ToInt32(raw);
    }

    public async Task<int> GetDocumentNumberInBatchAsync(
        Guid batchId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM documents
            WHERE batch_id = $batch
              AND (created_utc < (SELECT created_utc FROM documents WHERE id = $id)
                   OR (created_utc = (SELECT created_utc FROM documents WHERE id = $id)
                       AND rowid <= (SELECT rowid FROM documents WHERE id = $id)));
            """;
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$id", documentId.ToString("D"));
        var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var count = raw is null or DBNull ? 1 : Convert.ToInt32(raw);
        return Math.Max(1, count);
    }

    public async Task DeleteAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        Guid? batchId = null;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT batch_id FROM documents WHERE id = $id;";
            lookup.Parameters.AddWithValue("$id", documentId.ToString("D"));
            var raw = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (raw is string text && Guid.TryParse(text, out var parsed))
                batchId = parsed;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var deletePages = connection.CreateCommand())
        {
            deletePages.Transaction = (SqliteTransaction)transaction;
            deletePages.CommandText = "DELETE FROM pages WHERE document_id = $id;";
            deletePages.Parameters.AddWithValue("$id", documentId.ToString("D"));
            await deletePages.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteDocument = connection.CreateCommand())
        {
            deleteDocument.Transaction = (SqliteTransaction)transaction;
            deleteDocument.CommandText = "DELETE FROM documents WHERE id = $id;";
            deleteDocument.Parameters.AddWithValue("$id", documentId.ToString("D"));
            await deleteDocument.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var directory = _paths.DocumentDirectory(documentId);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        if (batchId is { } id)
            await DeleteEmptyBatchAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteEmptyBatchAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await DeleteBatchIfEmptyAsync(connection, batchId, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteBatchIfEmptyAsync(
        SqliteConnection connection,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM documents WHERE batch_id = $id;";
        count.Parameters.AddWithValue("$id", batchId.ToString("D"));
        var remaining = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (remaining > 0)
            return;

        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM batches WHERE id = $id;";
        delete.Parameters.AddWithValue("$id", batchId.ToString("D"));
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var path = _paths.BatchIndexesPath(batchId);
        var folder = Path.GetDirectoryName(path);
        if (folder is not null && Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    private static void AddDocumentParameters(SqliteCommand command, CaptureDocument document)
    {
        command.Parameters.AddWithValue("$id", document.Id.ToString("D"));
        command.Parameters.AddWithValue("$original", document.OriginalFileName);
        command.Parameters.AddWithValue("$stored", document.StoredPath);
        command.Parameters.AddWithValue("$source", (int)document.Source);
        command.Parameters.AddWithValue("$profile", (object?)document.ProfileId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$batch", (object?)document.BatchId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)document.Status);
        command.Parameters.AddWithValue("$pages", document.PageCount);
        command.Parameters.AddWithValue("$created", document.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$error", (object?)document.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$redactionStatus", (int)document.RedactionStatus);
        command.Parameters.AddWithValue("$redactedPath", (object?)document.RedactedPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$redactionError", (object?)document.RedactionError ?? DBNull.Value);
    }

    private static CaptureDocument ReadDocument(SqliteDataReader reader)
    {
        var profile = reader.IsDBNull(4) ? (Guid?)null : Guid.Parse(reader.GetString(4));
        return new CaptureDocument
        {
            Id = Guid.Parse(reader.GetString(0)),
            OriginalFileName = reader.GetString(1),
            StoredPath = reader.GetString(2),
            Source = (DocumentSource)reader.GetInt32(3),
            ProfileId = profile,
            Status = (DocumentStatus)reader.GetInt32(5),
            PageCount = reader.GetInt32(6),
            CreatedUtc = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
            BatchId = reader.FieldCount > 9 && !reader.IsDBNull(9) ? Guid.Parse(reader.GetString(9)) : null,
            RedactionStatus = reader.FieldCount > 10 && !reader.IsDBNull(10)
                ? (RedactionStatus)reader.GetInt32(10)
                : RedactionStatus.None,
            RedactedPath = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null,
            RedactionError = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : null
        };
    }
}
