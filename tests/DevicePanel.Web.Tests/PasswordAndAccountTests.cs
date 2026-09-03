using DevicePanel.Web.Auth;
using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevicePanel.Web.Tests;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Theory]
    [InlineData("correct-horse-battery")]
    [InlineData("中文密码也不在话下")]
    [InlineData("p")]
    public void Verify_Returns_True_For_Correct_Password(string password)
    {
        var hash = _hasher.Hash(password);
        Assert.True(_hasher.Verify(password, hash));
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    public void Verify_Returns_False_For_Wrong_Password(string wrongPassword)
    {
        var hash = _hasher.Hash("actual-password");
        Assert.False(_hasher.Verify(wrongPassword, hash));
    }

    [Fact]
    public void Hash_Uses_Pbkdf2_Format_Without_Plaintext()
    {
        var password = "my-secret-密码";
        var hash = _hasher.Hash(password);

        Assert.StartsWith("pbkdf2-sha256:", hash);
        Assert.DoesNotContain(password, hash);
    }

    [Fact]
    public void Hash_Generates_Unique_Salt_Each_Time()
    {
        var hash1 = _hasher.Hash("same-password");
        var hash2 = _hasher.Hash("same-password");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Verify_Returns_False_For_Malformed_Hash()
    {
        Assert.False(_hasher.Verify("any", "not-a-valid-hash"));
        Assert.False(_hasher.Verify("any", "md5:10000:xx:yy"));
    }
}

public class AccountSeederTests
{
    [Fact]
    public void Seeds_User_With_Configured_Initial_Password()
    {
        using var db = new TempSqliteDatabase();
        var options = new AuthOptions { InitialUsername = "admin", InitialPassword = "init-pass-123" };
        var hasher = new PasswordHasher();

        Seed(db, options, hasher);

        var (username, hash) = QuerySingleUser(db);
        Assert.Equal("admin", username);
        Assert.StartsWith("pbkdf2-sha256:", hash);
        Assert.DoesNotContain("init-pass-123", hash);
        Assert.True(hasher.Verify("init-pass-123", hash));
    }

    [Fact]
    public void Seeds_User_With_Random_Password_When_Not_Configured()
    {
        using var db = new TempSqliteDatabase();
        var options = new AuthOptions { InitialUsername = "operator" };
        var hasher = new PasswordHasher();

        Seed(db, options, hasher);

        var (username, hash) = QuerySingleUser(db);
        Assert.Equal("operator", username);
        Assert.StartsWith("pbkdf2-sha256:", hash);
    }

    [Fact]
    public void Skips_When_User_Already_Exists()
    {
        using var db = new TempSqliteDatabase();
        var hasher = new PasswordHasher();
        Seed(db, new AuthOptions { InitialUsername = "admin", InitialPassword = "first" }, hasher);
        var firstHash = QuerySingleUser(db).Hash;

        Seed(db, new AuthOptions { InitialUsername = "admin", InitialPassword = "second" }, hasher);

        using var connection = db.CreateOpenConnection();
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM users";
        Assert.Equal(1L, Convert.ToInt64(countCommand.ExecuteScalar()));
        Assert.Equal(firstHash, QuerySingleUser(db).Hash);
    }

    private static void Seed(TempSqliteDatabase db, AuthOptions options, IPasswordHasher hasher)
    {
        var logger = new NullLogger<AccountSeeder>();
        var seeder = new AccountSeeder(db.Factory, hasher, options, logger);
        seeder.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static (string Username, string Hash) QuerySingleUser(TempSqliteDatabase db)
    {
        using var connection = db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT username, password_hash FROM users";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetString(1));
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}
