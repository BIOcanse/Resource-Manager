using System.Globalization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.SystemInformation;

internal static class OperatingSystemBuildBootConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = new WindowsOperatingSystemInformationOwner(TimeSpan.FromSeconds(2));
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        if (!core.Catalog.Fields.IsEmpty) throw new InvalidDataException("Passive OS constructor captured facts.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var catalog = await core.DiscoverAsync(timeout.Token);
        var uptime = catalog.Fields.Single(field => field.Definition.Name == "os_all_uptime_actual").Field;
        var build = catalog.Fields.Single(field => field.Definition.Name == "os_all_build_declared").Field;
        var boot = catalog.Fields.Single(field => field.Definition.Name == "os_all_boot_time_actual").Field;
        foreach (var field in new[] { build, boot })
        {
            var capability = core.Query(field);
            if (field.DeviceIdentity != WindowsOperatingSystemInformationOwner.MachineIdentity || capability.Definition.ValueKind != "string"
                || !capability.Read.Supported || !capability.Read.Dependencies.IsEmpty || capability.Write.Supported)
                throw new InvalidDataException("OS canonical declaration capability mismatch.");
        }
        var originalUptime = core.ReadCached(uptime);
        var built = (await core.CollectAsync([build], timeout.Token)).Single();
        if (core.ReadCached(uptime) != originalUptime || core.ReadCached(build) != built)
            throw new InvalidDataException("Build-only capture changed uptime or diverged from original current.");
        var booted = (await core.CollectAsync([boot], timeout.Token)).Single();
        if (core.ReadCached(uptime) != originalUptime || core.ReadCached(boot) != booted || core.ReadCached(build) != built)
            throw new InvalidDataException("Boot-only capture changed other values/timestamps or diverged from original current.");
        if (built.Status == FieldObservationStatus.Current)
        {
            if (string.IsNullOrEmpty(built.Value?.Text) || !built.Value.Text.All(static c => c >= '0' && c <= '9'))
                throw new InvalidDataException("Complete BuildNumber decimal string missing.");
        }
        else if (built.Value is not null) throw new InvalidDataException("Unavailable build was fabricated.");
        if (booted.Status == FieldObservationStatus.Current)
        {
            if (!DateTimeOffset.TryParseExact(booted.Value?.Text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
                || instant.Offset != TimeSpan.Zero || instant.Ticks % 10 != 0)
                throw new InvalidDataException("Reported boot datetime is not lossless UTC DMTF microseconds.");
        }
        else if (booted.Value is not null) throw new InvalidDataException("Unavailable boot was fabricated from uptime.");
        Console.WriteLine($"Actual OS BuildNumber: text={built.Value?.Text}; status={built.Status}; source={built.SourceId}; no-UBR/marketing-version claim.");
        Console.WriteLine($"Actual OS LastBootUpTime: utc={booted.Value?.Text}; status={booted.Status}; source={booted.SourceId}; same-current=yes; provider-restart-not-estimate/resume/login.");
    }
}
