using Npgsql;

namespace AccountantApp.Api.Shared.Data;

public sealed class RequestConnection : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; }

    public RequestConnection(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");
        Connection = new NpgsqlConnection(connectionString);
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}