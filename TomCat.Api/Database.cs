using Microsoft.Data.Sqlite;

namespace TomCat.Api;

// Connections are short lived. Every transaction owns one connection; SQLite serializes writers.
public sealed class Database(string directory)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(directory, "tomcat.db"),
        ForeignKeys = true,
        DefaultTimeout = 10,
    }.ToString();

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using (var wal = Command(connection, "PRAGMA journal_mode = WAL;")) wal.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        using var version = Command(connection, "PRAGMA user_version;", transaction);
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current > 1) throw new InvalidOperationException("Database schema is newer than this API.");
        if (current == 0)
        {
            var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "001_initial.sql"));
            using var migration = Command(connection, sql, transaction);
            migration.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static SqliteCommand Command(SqliteConnection connection, string sql,
        SqliteTransaction? transaction = null, params (string Key, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }

    public UserRow? FindUser(string username)
    {
        using var connection = Open();
        using var command = Command(connection,
            "SELECT id, username, password_hash FROM users WHERE username = $name;", null, ("$name", username));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public void CreateUser(UserRow user)
    {
        using var connection = Open();
        using var command = Command(connection,
            "INSERT INTO users VALUES ($id, $name, $hash, $now);", null,
            ("$id", user.Id), ("$name", user.Username), ("$hash", user.PasswordHash), ("$now", Now()));
        command.ExecuteNonQuery();
    }

    public static string Now() => DateTimeOffset.UtcNow.ToString("O");
    public static string Id() => Guid.NewGuid().ToString("N");

    private static ProjectRow ReadProject(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6));

    public List<ProjectRow> ListProjects(string owner)
    {
        using var connection = Open();
        using var command = Command(connection,
            "SELECT id, name, description, template, current_revision_id, created_at, updated_at FROM projects WHERE owner_id = $owner ORDER BY updated_at DESC;",
            null, ("$owner", owner));
        using var reader = command.ExecuteReader();
        var projects = new List<ProjectRow>();
        while (reader.Read()) projects.Add(ReadProject(reader));
        return projects;
    }

    public static ProjectRow? GetProject(SqliteConnection connection, string owner, string id, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection,
            "SELECT id, name, description, template, current_revision_id, created_at, updated_at FROM projects WHERE id = $id AND owner_id = $owner;",
            transaction, ("$id", id), ("$owner", owner));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProject(reader) : null;
    }

    public ProjectRow CreateProject(string owner, ProjectInput input)
    {
        var now = Now();
        var project = new ProjectRow(Id(), input.Name!.Trim(), input.Description?.Trim() ?? "", input.Template ?? "2D", null, now, now);
        using var connection = Open();
        using var command = Command(connection,
            "INSERT INTO projects VALUES ($id, $owner, $name, $description, $template, NULL, $now, $now);", null,
            ("$id", project.Id), ("$owner", owner), ("$name", project.Name),
            ("$description", project.Description), ("$template", project.Template), ("$now", now));
        command.ExecuteNonQuery();
        return project;
    }
}
