using System.Globalization;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapTargetedRecordingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/targeted-recordings", async (
            StartTargetedRecordingRequest request, TargetedRecordingService recordings,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await recordings.BeginAsync(request.SoftwareId,
                    request.MaximumDurationSeconds ?? TargetedRecordingService.DefaultMaximumDurationSeconds,
                    cancellationToken));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict();
            }
        });

        app.MapPost("/api/targeted-recordings/{id}/stop", async (
            string id, TargetedRecordingService recordings, CancellationToken cancellationToken) =>
        {
            var stopped = await recordings.FinishAsync(id, cancellationToken);
            return stopped is null ? Results.NotFound() : Results.Ok(stopped);
        });

        app.MapGet("/api/targeted-recordings", async (
            TargetedRecordingService recordings, CancellationToken cancellationToken) =>
            Results.Ok(await recordings.ListAsync(cancellationToken)));

        app.MapGet("/api/targeted-recordings/{id}", async (
            string id, TargetedRecordingService recordings, CancellationToken cancellationToken) =>
        {
            var report = await recordings.GetAsync(id, cancellationToken);
            return report is null ? Results.NotFound() : Results.Ok(report);
        });

        app.MapDelete("/api/targeted-recordings/{id}", async (
            string id, TargetedRecordingService recordings, CancellationToken cancellationToken) =>
            await recordings.DeleteAsync(id, cancellationToken)
                ? Results.NoContent() : Results.NotFound());

        app.MapGet("/api/targeted-recordings/{id}/export", async (
            string id, string? format, TargetedRecordingService recordings,
            CancellationToken cancellationToken) =>
        {
            var report = await recordings.GetAsync(id, cancellationToken);
            if (report is null)
            {
                return Results.NotFound();
            }
            var name = $"targeted-recording-{id}";
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                return Results.File(TargetedRecordingCsv.Write(report), "text/csv; charset=utf-8", name + ".csv");
            }
            if (format is null || string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                return Results.File(JsonSerializer.SerializeToUtf8Bytes(report,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)), "application/json", name + ".json");
            }
            return Results.BadRequest();
        });
        return app;
    }

    public sealed record StartTargetedRecordingRequest(string SoftwareId, int? MaximumDurationSeconds);
}

internal static class TargetedRecordingCsv
{
    public static byte[] Write(TargetedRecordingReport report)
    {
        var csv = new StringBuilder("kind,utc,processId,processStartKey,source,swapChain,frameTimeMs,scope,metric,value\r\n");
        foreach (var (name, value) in new (string Name, string Value)[]
        {
            ("id", report.Recording.Id),
            ("softwareId", report.Recording.SoftwareId),
            ("softwareName", report.Recording.SoftwareName),
            ("startedAt", report.Recording.StartedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("endedAt", report.Recording.EndedAt?.ToString("O", CultureInfo.InvariantCulture) ?? ""),
            ("maximumDurationSeconds", report.Recording.MaximumDurationSeconds.ToString(CultureInfo.InvariantCulture)),
            ("status", report.Recording.Status),
            ("stopReason", report.Recording.StopReason ?? ""),
            ("incomplete", report.Recording.Incomplete ? "true" : "false")
        })
        {
            csv.Append("recording,,,,,,,,").Append(name).Append(',').Append(Escape(value)).Append("\r\n");
        }
        if (report.Summary is { } summary)
        {
            foreach (var metric in new (string Name, double Value)[]
            {
                ("averageFps", summary.AverageFps),
                ("onePercentLowFps", summary.OnePercentLowFps),
                ("pointOnePercentLowFps", summary.PointOnePercentLowFps),
                ("p50FrameTimeMs", summary.P50FrameTimeMs),
                ("p95FrameTimeMs", summary.P95FrameTimeMs),
                ("p99FrameTimeMs", summary.P99FrameTimeMs),
                ("maxFrameTimeMs", summary.MaxFrameTimeMs)
            })
            {
                csv.Append("summary,,,,,,,,").Append(metric.Name).Append(',')
                    .Append(metric.Value.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
            }
        }
        foreach (var interval in report.FrameIntervals)
        {
            csv.Append("frame,").Append(interval.EndedAt.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(interval.ProcessId).Append(',').Append(interval.ProcessStartKey).Append(',')
                .Append(interval.Source).Append(',').Append(interval.SwapChain).Append(',')
                .Append(interval.DurationMs.ToString("R", CultureInfo.InvariantCulture))
                .Append(",,,\r\n");
        }
        foreach (var sample in report.ResourceSamples)
        {
            foreach (var (scope, values) in new[] { ("software", sample.Software), ("system", sample.System) })
            {
                foreach (var (metric, value) in values.OrderBy(static item => item.Key, StringComparer.Ordinal))
                {
                    csv.Append("resource,").Append(sample.CapturedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Append(",,,,,,").Append(scope).Append(',').Append(metric).Append(',');
                    if (value is { } number)
                    {
                        csv.Append(number.ToString("R", CultureInfo.InvariantCulture));
                    }
                    csv.Append("\r\n");
                }
            }
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static string Escape(string value)
        => value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : '"' + value.Replace("\"", "\"\"") + '"';
}
