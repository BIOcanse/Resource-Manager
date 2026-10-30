using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ResourceManager.App.Domain.Settings;
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
                return Results.Ok(await recordings.BeginAsync(request.SoftwareId, request.ProfileId, request.Items,
                    request.MaximumDurationSeconds ?? TargetedRecordingService.DefaultMaximumDurationSeconds,
                    cancellationToken));
            }
            catch (TargetedRecordingStartException error)
            {
                return Results.Json(new EndpointErrorPayload(error.Code), statusCode: error.StatusCode);
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

        app.MapGet("/api/targeted-recordings/targets", async (
            TargetedRecordingService recordings, CancellationToken cancellationToken) =>
            Results.Ok(await recordings.ListTargetsAsync(cancellationToken)));

        app.MapGet("/api/targeted-recordings/items/catalog", async (
            TargetedRecordingService recordings, CancellationToken cancellationToken) =>
            Results.Ok(await recordings.ReadItemCatalogAsync(cancellationToken)));

        app.MapGet("/api/targeted-recordings/profiles", async (
            [FromServices] TargetedRecordingProfileService profiles, CancellationToken cancellationToken) =>
            Results.Ok(await profiles.ReadProfilesAsync(cancellationToken)));

        app.MapPost("/api/targeted-recordings/profiles", async (
            TargetedRecordingProfileCreateRequest request, [FromServices] TargetedRecordingProfileService profiles,
            CancellationToken cancellationToken) =>
            ProfileResult(await profiles.CreateAsync(request, cancellationToken)));

        app.MapPatch("/api/targeted-recordings/profiles/{id}", async (
            string id, TargetedRecordingProfileUpdateRequest request, [FromServices] TargetedRecordingProfileService profiles,
            CancellationToken cancellationToken) =>
            ProfileResult(await profiles.UpdateAsync(id, request, cancellationToken)));

        app.MapDelete("/api/targeted-recordings/profiles/{id}", async (
            string id, [FromServices] TargetedRecordingProfileService profiles, CancellationToken cancellationToken) =>
            ProfileResult(await profiles.DeleteAsync(id, cancellationToken)));

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

        app.MapPost("/api/targeted-recordings/history/clear", async (
            TargetedRecordingService recordings, CancellationToken cancellationToken) =>
            Results.Ok(new ClearReportHistoryResponse(await recordings.ClearHistoryAsync(cancellationToken))));

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
                var export = new TargetedRecordingJsonExport(TargetedRecordingJsonExport.CurrentSchemaVersion,
                    report.Recording, report.Summary, report.FrameIntervals, report.FpsDistribution, report.ResourceSamples);
                return Results.File(JsonSerializer.SerializeToUtf8Bytes(export,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)), "application/json", name + ".json");
            }
            if (string.Equals(format, "long-csv", StringComparison.OrdinalIgnoreCase))
            {
                return Results.File(TargetedRecordingCsv.WriteLong(report), "text/csv; charset=utf-8", name + "-long.csv");
            }
            if (string.Equals(format, "frames-csv", StringComparison.OrdinalIgnoreCase))
            {
                return Results.File(TargetedRecordingCsv.WriteFrames(report), "text/csv; charset=utf-8", name + "-frames.csv");
            }
            if (string.Equals(format, "presentmon", StringComparison.OrdinalIgnoreCase))
            {
                return Results.File(TargetedRecordingCsv.WritePresentMon(report), "text/csv; charset=utf-8",
                    name + "-presentmon-" + TargetedRecordingCsv.PresentMonFormatVersion + ".csv");
            }
            return Results.BadRequest();
        });
        return app;
    }

    private static IResult ProfileResult(TargetedRecordingProfileResult result)
        => result.Error is { } error ? Results.Json(new EndpointErrorPayload(error), statusCode: result.StatusCode)
            : result.StatusCode == StatusCodes.Status204NoContent ? Results.NoContent()
            : result.StatusCode == StatusCodes.Status201Created
                ? Results.Json(result.Profile, statusCode: StatusCodes.Status201Created)
                : Results.Ok(result.Profile);

    /// <summary>ProfileId names a saved profile; Items is the request's own list. With neither the
    /// default profile is recorded; with both, Items wins and ProfileId is ignored.</summary>
    public sealed record StartTargetedRecordingRequest(string SoftwareId, int? MaximumDurationSeconds,
        string? ProfileId = null, IReadOnlyList<TargetedRecordingItem>? Items = null);
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

    /// <summary>The PresentMon CSV column layout this export follows (PresentMon 1.x capture files, which
    /// CapFrameX imports). Only the measured columns are written.</summary>
    public const string PresentMonFormatVersion = "1.6";

    /// <summary>Long format: one value per row. item is kind:id; process is "all" for a software item (the
    /// sum over the target's processes), the process id for frames, and empty for a system metric.</summary>
    public static byte[] WriteLong(TargetedRecordingReport report)
    {
        var csv = new StringBuilder("time_ms,item,process,value,unit\r\n");
        var started = report.Recording.StartedAt;
        var units = report.Recording.Items
            .GroupBy(static item => (item.Kind, item.Id.ToLowerInvariant()))
            .ToDictionary(static group => group.Key, static group => group.First().Unit);
        foreach (var interval in report.FrameIntervals)
        {
            Row(csv, interval.EndedAt - started, TargetedRecordingItemKinds.Frames + ":present",
                interval.ProcessId.ToString(CultureInfo.InvariantCulture), interval.DurationMs,
                TargetedRecordingItemCatalogRules.FramesUnit);
        }
        foreach (var sample in report.ResourceSamples)
        {
            foreach (var (kind, process, values) in new[]
            {
                (TargetedRecordingItemKinds.Software, "all", sample.Software),
                (TargetedRecordingItemKinds.Metric, "", sample.System)
            })
            {
                foreach (var (id, value) in values.OrderBy(static item => item.Key, StringComparer.Ordinal))
                {
                    Row(csv, sample.CapturedAt - started, kind + ":" + id, process, value,
                        units.GetValueOrDefault((kind, id.ToLowerInvariant())) ?? "");
                }
            }
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    public static byte[] WriteFrames(TargetedRecordingReport report)
    {
        var csv = new StringBuilder("time_ms,process_id,process_start_key,source,swap_chain,frame_time_ms\r\n");
        foreach (var interval in report.FrameIntervals)
        {
            csv.Append(Milliseconds(interval.EndedAt - report.Recording.StartedAt)).Append(',')
                .Append(interval.ProcessId).Append(',').Append(interval.ProcessStartKey).Append(',')
                .Append(interval.Source).Append(',').Append(interval.SwapChain).Append(',')
                .Append(interval.DurationMs.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    /// <summary>PresentMon 1.x columns. TimeInSeconds is the present end from the recording start;
    /// MsBetweenPresents is the frame time. SyncInterval, PresentFlags and Dropped are not observed and are
    /// written as 0.</summary>
    public static byte[] WritePresentMon(TargetedRecordingReport report)
    {
        var csv = new StringBuilder(
            "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,MsBetweenPresents\r\n");
        var application = Escape(report.Recording.SoftwareName);
        foreach (var interval in report.FrameIntervals)
        {
            csv.Append(application).Append(',').Append(interval.ProcessId).Append(",0x")
                .Append(interval.SwapChain.ToString("X16", CultureInfo.InvariantCulture)).Append(',')
                .Append(interval.Source).Append(",0,0,0,")
                .Append((interval.EndedAt - report.Recording.StartedAt).TotalSeconds.ToString("F6", CultureInfo.InvariantCulture))
                .Append(',').Append(interval.DurationMs.ToString("F4", CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static void Row(StringBuilder csv, TimeSpan offset, string item, string process, double? value, string unit)
    {
        csv.Append(Milliseconds(offset)).Append(',').Append(Escape(item)).Append(',').Append(process).Append(',');
        if (value is { } number) csv.Append(number.ToString("R", CultureInfo.InvariantCulture));
        csv.Append(',').Append(Escape(unit)).Append("\r\n");
    }

    private static string Milliseconds(TimeSpan offset)
        => Math.Round(offset.TotalMilliseconds, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value)
        => value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : '"' + value.Replace("\"", "\"\"") + '"';
}
