using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace Emanager.Core.Metadata;

public sealed record FileIdentity(string Identity, string Path, long Size, DateTimeOffset Modified, string? ContentFingerprint);
public sealed record FileTag(string Tag, string? Color);
public interface IFileMetadataService { Task<FileIdentity?> GetIdentityAsync(string path, CancellationToken cancellationToken = default); Task ReconcileMoveAsync(string oldPath, string newPath, CancellationToken cancellationToken = default); Task CleanupMissingAsync(CancellationToken cancellationToken = default); }
public interface IFileTagService { Task SetTagsAsync(FileIdentity identity, IReadOnlyList<FileTag> tags, CancellationToken cancellationToken = default); Task<IReadOnlyList<FileTag>> GetTagsAsync(FileIdentity identity, CancellationToken cancellationToken = default); Task<IReadOnlyList<FileIdentity>> FindByTagAsync(string tag, CancellationToken cancellationToken = default); }

public sealed class SqliteMetadataStore : IFileMetadataService, IFileTagService
{
    private readonly string _connectionString;
    public SqliteMetadataStore(string databasePath) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!); _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString(); Initialize(); }
    private void Initialize() { using var db = new SqliteConnection(_connectionString); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS files(identity TEXT PRIMARY KEY,path TEXT NOT NULL,size INTEGER NOT NULL,modified TEXT NOT NULL,fingerprint TEXT); CREATE TABLE IF NOT EXISTS tags(identity TEXT NOT NULL,tag TEXT NOT NULL,color TEXT,PRIMARY KEY(identity,tag));"; cmd.ExecuteNonQuery(); }
    public async Task<FileIdentity?> GetIdentityAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path); if (!info.Exists) return null; var identity = await ComputeIdentityAsync(info, cancellationToken); using var db = new SqliteConnection(_connectionString); await db.OpenAsync(cancellationToken); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO files(identity,path,size,modified,fingerprint) VALUES($i,$p,$s,$m,$f) ON CONFLICT(identity) DO UPDATE SET path=$p,size=$s,modified=$m,fingerprint=$f;"; cmd.Parameters.AddWithValue("$i", identity.Identity); cmd.Parameters.AddWithValue("$p", identity.Path); cmd.Parameters.AddWithValue("$s", identity.Size); cmd.Parameters.AddWithValue("$m", identity.Modified); cmd.Parameters.AddWithValue("$f", identity.ContentFingerprint ?? (object)DBNull.Value); await cmd.ExecuteNonQueryAsync(cancellationToken); return identity;
    }
    public async Task ReconcileMoveAsync(string oldPath, string newPath, CancellationToken cancellationToken = default) { using var db = new SqliteConnection(_connectionString); await db.OpenAsync(cancellationToken); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE files SET path=$new WHERE path=$old"; cmd.Parameters.AddWithValue("$new", newPath); cmd.Parameters.AddWithValue("$old", oldPath); await cmd.ExecuteNonQueryAsync(cancellationToken); }
    public async Task CleanupMissingAsync(CancellationToken cancellationToken = default) { using var db = new SqliteConnection(_connectionString); await db.OpenAsync(cancellationToken); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT identity,path FROM files"; using var reader = await cmd.ExecuteReaderAsync(cancellationToken); var missing = new List<string>(); while (await reader.ReadAsync(cancellationToken)) if (!File.Exists(reader.GetString(1))) missing.Add(reader.GetString(0)); await reader.DisposeAsync(); foreach (var id in missing) { using var delete = db.CreateCommand(); delete.CommandText = "DELETE FROM files WHERE identity=$id; DELETE FROM tags WHERE identity=$id"; delete.Parameters.AddWithValue("$id", id); await delete.ExecuteNonQueryAsync(cancellationToken); } }
    public async Task SetTagsAsync(FileIdentity identity, IReadOnlyList<FileTag> tags, CancellationToken cancellationToken = default) { using var db = new SqliteConnection(_connectionString); await db.OpenAsync(cancellationToken); using var tx = db.BeginTransaction(); using var clear = db.CreateCommand(); clear.Transaction = tx; clear.CommandText = "DELETE FROM tags WHERE identity=$id"; clear.Parameters.AddWithValue("$id", identity.Identity); await clear.ExecuteNonQueryAsync(cancellationToken); foreach (var tag in tags) { using var add = db.CreateCommand(); add.Transaction = tx; add.CommandText = "INSERT INTO tags(identity,tag,color) VALUES($id,$tag,$color)"; add.Parameters.AddWithValue("$id", identity.Identity); add.Parameters.AddWithValue("$tag", tag.Tag); add.Parameters.AddWithValue("$color", tag.Color ?? (object)DBNull.Value); await add.ExecuteNonQueryAsync(cancellationToken); } tx.Commit(); }
    public async Task<IReadOnlyList<FileTag>> GetTagsAsync(FileIdentity identity, CancellationToken cancellationToken = default) { using var db = new SqliteConnection(_connectionString); await db.OpenAsync(cancellationToken); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT tag,color FROM tags WHERE identity=$id"; cmd.Parameters.AddWithValue("$id", identity.Identity); using var reader = await cmd.ExecuteReaderAsync(cancellationToken); var result = new List<FileTag>(); while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))); return result; }
    public async Task<IReadOnlyList<FileIdentity>> FindByTagAsync(string tag, CancellationToken cancellationToken = default) { using var db = new SqliteConnection(_connectionString); await db.OpenAsync(cancellationToken); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT f.identity,f.path,f.size,f.modified,f.fingerprint FROM files f JOIN tags t ON t.identity=f.identity WHERE t.tag=$tag"; cmd.Parameters.AddWithValue("$tag", tag); using var reader = await cmd.ExecuteReaderAsync(cancellationToken); var result = new List<FileIdentity>(); while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), DateTimeOffset.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4))); return result; }
    public async Task BackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        using var db = new SqliteConnection(_connectionString); await db.OpenAsync(cancellationToken); using var checkpoint = db.CreateCommand(); checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        var source = new SqliteConnectionStringBuilder(_connectionString).DataSource; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!); await Task.Run(() => File.Copy(source, destinationPath, overwrite: true), cancellationToken);
    }
    private static async Task<FileIdentity> ComputeIdentityAsync(FileInfo info, CancellationToken token)
    {
        var identity = $"path:{info.FullName}";
        if (OperatingSystem.IsLinux() && (File.Exists("/usr/bin/stat") || File.Exists("/bin/stat")))
        {
            var psi = new ProcessStartInfo("stat", $"-c %d:%i {Quote(info.FullName)}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(psi);
            if (process is not null) { var value = await process.StandardOutput.ReadToEndAsync(token); await process.WaitForExitAsync(token); if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(value)) identity = "inode:" + value.Trim(); }
        }
        return new FileIdentity(identity, info.FullName, info.Length, info.LastWriteTimeUtc, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
