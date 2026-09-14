using Microsoft.Data.Sqlite;
using ResourceManager.App.Application.PublicServices.Sqlite;
using ResourceManager.App.Domain.PublicServices.Sqlite;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapPublicSqliteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/public/v1/sqlite/databases", async (
            IPublicSqliteDatabaseService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken))).AllowAnonymous();

        app.MapPut("/api/public/v1/sqlite/databases/{databaseId}", async (
            string databaseId,
            IPublicSqliteDatabaseService service,
            CancellationToken cancellationToken) =>
            await RunAsync(() => service.CreateAsync(databaseId, cancellationToken)));

        app.MapGet("/api/public/v1/sqlite/databases/{databaseId}", async (
            string databaseId,
            IPublicSqliteDatabaseService service,
            CancellationToken cancellationToken) =>
        {
            var database = await service.GetAsync(databaseId, cancellationToken);
            return database is null ? Results.NotFound() : Results.Ok(database);
        }).AllowAnonymous();

        app.MapPost("/api/public/v1/sqlite/databases/{databaseId}/query", async (
            string databaseId,
            PublicSqliteCommandRequest request,
            IPublicSqliteDatabaseService service,
            CancellationToken cancellationToken) =>
            await RunAsync(() => service.QueryAsync(databaseId, request, cancellationToken)));

        app.MapPost("/api/public/v1/sqlite/databases/{databaseId}/execute", async (
            string databaseId,
            PublicSqliteCommandRequest request,
            IPublicSqliteDatabaseService service,
            CancellationToken cancellationToken) =>
            await RunAsync(() => service.ExecuteAsync(databaseId, request, cancellationToken)));

        app.MapPost("/api/public/v1/sqlite/databases/{databaseId}/batch", async (
            string databaseId,
            PublicSqliteBatchRequest request,
            IPublicSqliteDatabaseService service,
            CancellationToken cancellationToken) =>
            await RunAsync(() => service.BatchAsync(databaseId, request, cancellationToken)));

        app.MapPost("/api/public/v1/sqlite/databases/{databaseId}/checkpoint", async (
            string databaseId,
            IPublicSqliteDatabaseService service,
            CancellationToken cancellationToken) =>
            await RunAsync(() => service.CheckpointAsync(databaseId, cancellationToken)));

        return app;
    }

    private static async Task<IResult> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid-sqlite-request", message = exception.Message });
        }
        catch (SqliteException exception)
        {
            return Results.BadRequest(new
            {
                error = "sqlite-error",
                code = exception.SqliteErrorCode,
                extendedCode = exception.SqliteExtendedErrorCode,
                message = exception.Message
            });
        }
    }
}
