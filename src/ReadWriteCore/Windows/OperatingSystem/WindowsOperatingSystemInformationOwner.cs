using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Platform;

namespace ResourceManager.ReadWrite.Windows.SystemInformation;

/// <summary>Passive machine information owner. Explicit selected capture updates
/// only requested values in the same immutable current; subscriptions borrow it.</summary>
public sealed class WindowsOperatingSystemInformationOwner : IDisposable
{
    public const string MachineIdentity = "windows-operating-system|local-machine";
    public const string MachineObjectIdentity = "machine";
    public const string CollectionItemId = "information.operating-system";
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly Func<WindowsMachineUptime?> read;
    private readonly WindowsOperatingSystemDeclarationReader declarations;
    private readonly WindowsPlatformDeclarationReader platformDeclarations;
    private readonly IWindowsAcPowerStatusReader acPowerStatusReader;
    private readonly IWindowsKeyboardScanCodeRemapReader keyboardScanCodeRemapReader;
    private readonly WindowsOperatingSystemComponentInformationScope componentScope;
    private readonly IWindowsOperatingSystemComponentInformationReader componentReader;
    private readonly IDisposable? ownedComponentReader;
    private readonly TimeProvider clock;
    private readonly InformationReadWriteBackend backend;
    private DeviceInformationSnapshot current = DeviceInformationSnapshot.Empty;
    private DateTimeOffset? buildObservedAt;
    private DateTimeOffset? bootObservedAt;
    private int disposed;
    public SamplingPublicationSignal Publications { get; } = new();

    public WindowsOperatingSystemInformationOwner(TimeProvider? clock = null)
        : this(TimeSpan.FromSeconds(2), clock) { }
    public WindowsOperatingSystemInformationOwner(TimeSpan queryTimeout, TimeProvider? clock = null)
        : this(WindowsMachineUptimeReader.Read, new WindowsOperatingSystemDeclarationReader(queryTimeout),
            new WindowsPlatformDeclarationReader(queryTimeout: queryTimeout), clock, new WindowsAcPowerStatusReader(),
            new WindowsKeyboardScanCodeRemapReader(), componentReader: new WindowsOperatingSystemComponentInformationNativeReader(),
            ownsComponentReader: true) { }
    public WindowsOperatingSystemInformationOwner(IWindowsAcPowerStatusReader reader,
        TimeSpan queryTimeout, TimeProvider? clock = null)
        : this(WindowsMachineUptimeReader.Read, new WindowsOperatingSystemDeclarationReader(queryTimeout),
            new WindowsPlatformDeclarationReader(queryTimeout: queryTimeout), clock,
            reader ?? throw new ArgumentNullException(nameof(reader)), new WindowsKeyboardScanCodeRemapReader(),
            componentReader: new WindowsOperatingSystemComponentInformationNativeReader(), ownsComponentReader: true) { }
    public WindowsOperatingSystemInformationOwner(WindowsPlatformDeclarationScope scope, TimeSpan queryTimeout, TimeProvider? clock = null)
        : this(WindowsMachineUptimeReader.Read, new WindowsOperatingSystemDeclarationReader(queryTimeout),
            new WindowsPlatformDeclarationReader(scope, queryTimeout), clock, new WindowsAcPowerStatusReader(),
            new WindowsKeyboardScanCodeRemapReader(), componentReader: new WindowsOperatingSystemComponentInformationNativeReader(),
            ownsComponentReader: true) { }

    public WindowsOperatingSystemInformationOwner(WindowsOperatingSystemComponentInformationScope componentScope,
        TimeSpan queryTimeout, TimeProvider? clock = null)
        : this(WindowsMachineUptimeReader.Read, new WindowsOperatingSystemDeclarationReader(queryTimeout),
            new WindowsPlatformDeclarationReader(queryTimeout: queryTimeout), clock, new WindowsAcPowerStatusReader(),
            new WindowsKeyboardScanCodeRemapReader(), componentScope ?? throw new ArgumentNullException(nameof(componentScope)),
            componentReader: new WindowsOperatingSystemComponentInformationNativeReader(), ownsComponentReader: true) { }

    public WindowsOperatingSystemInformationOwner(WindowsOperatingSystemComponentInformationScope componentScope,
        IWindowsOperatingSystemComponentInformationReader componentReader, TimeSpan queryTimeout, TimeProvider? clock = null)
        : this(WindowsMachineUptimeReader.Read, new WindowsOperatingSystemDeclarationReader(queryTimeout),
            new WindowsPlatformDeclarationReader(queryTimeout: queryTimeout), clock, new WindowsAcPowerStatusReader(),
            new WindowsKeyboardScanCodeRemapReader(),
            componentScope ?? throw new ArgumentNullException(nameof(componentScope)),
            componentReader ?? throw new ArgumentNullException(nameof(componentReader))) { }

    /// <summary>Creates the existing owner with an injected keyboard configuration source.</summary>
    public static WindowsOperatingSystemInformationOwner CreateWithKeyboardScanCodeRemapReader(
        IWindowsKeyboardScanCodeRemapReader reader, TimeSpan queryTimeout, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new(WindowsMachineUptimeReader.Read, new WindowsOperatingSystemDeclarationReader(queryTimeout),
            new WindowsPlatformDeclarationReader(queryTimeout: queryTimeout), clock, new WindowsAcPowerStatusReader(),
            reader, componentReader: new WindowsOperatingSystemComponentInformationNativeReader(), ownsComponentReader: true);
    }
    // Original recorded uptime boundary stays internal and never falls through to real WMI.
    internal WindowsOperatingSystemInformationOwner(Func<WindowsMachineUptime?> read, TimeProvider? clock = null)
        : this(read, new WindowsOperatingSystemDeclarationReader(TimeSpan.FromSeconds(2),
            static (_, _, _) => new QueryResult([], "Recorded uptime boundary has no WMI facts.")), clock) { }
    internal WindowsOperatingSystemInformationOwner(Func<WindowsMachineUptime?> read,
        WindowsOperatingSystemDeclarationReader declarations, TimeProvider? clock = null)
        : this(read, declarations, new WindowsPlatformDeclarationReader(static (_, _) => new(null, null, null, null)), clock) { }
    internal WindowsOperatingSystemInformationOwner(Func<WindowsMachineUptime?> read,
        WindowsOperatingSystemDeclarationReader declarations, WindowsPlatformDeclarationReader platformDeclarations, TimeProvider? clock = null,
        IWindowsAcPowerStatusReader? acPowerStatusReader = null,
        IWindowsKeyboardScanCodeRemapReader? keyboardScanCodeRemapReader = null,
        WindowsOperatingSystemComponentInformationScope? componentScope = null,
        IWindowsOperatingSystemComponentInformationReader? componentReader = null,
        bool ownsComponentReader = false)
    {
        this.read = read; this.declarations = declarations;
        this.platformDeclarations = platformDeclarations;
        this.acPowerStatusReader = acPowerStatusReader ?? new UnavailableAcPowerStatusReader();
        this.keyboardScanCodeRemapReader = keyboardScanCodeRemapReader ?? new UnavailableKeyboardScanCodeRemapReader();
        this.componentScope = componentScope ?? new WindowsOperatingSystemComponentInformationScope();
        if (ownsComponentReader && componentReader is not IDisposable)
            throw new ArgumentException("An owned component reader must implement IDisposable.", nameof(componentReader));
        this.componentReader = componentReader ?? UnavailableComponentInformationReader.Instance;
        ownedComponentReader = ownsComponentReader ? (IDisposable)componentReader! : null;
        this.clock = clock ?? TimeProvider.System;
        backend = new(ReadInformation, token => CaptureInformationAsync(token), static _ => new(true, null, []),
            (selection, token) => CaptureSelectedInformationAsync(SelectFields(selection), token));
    }
    public InformationReadWriteBackend CreateBackend() => backend;
    public DeviceInformationSnapshot ReadInformation() => Volatile.Read(ref current);
    public static WindowsOperatingSystemInformationFields SelectFields(ImmutableArray<InformationFieldSelection> selection)
    {
        var fields = WindowsOperatingSystemInformationFields.None;
        foreach (var field in selection)
        {
            if (field.Field.FieldName != field.Definition.Name)
                throw new ArgumentException("Unknown OS field definition identity.", nameof(selection));
            var category = WindowsPlatformDeclarationProjection.Category(field.Definition);
            if (category != WindowsOperatingSystemInformationFields.None) { fields |= category; continue; }
            var componentCategory = WindowsOperatingSystemComponentInformationProjection.Category(field.Definition);
            if (componentCategory != WindowsOperatingSystemInformationFields.None)
            {
                if (!WindowsOperatingSystemComponentInformationProjection.HasCanonicalIdentity(field.Field.DeviceIdentity, componentCategory))
                    throw new ArgumentException("Unknown component information object identity.", nameof(selection));
                fields |= componentCategory;
                continue;
            }
            if (field.Definition == DeviceInformationFieldRegistry.MachineAllAcPowerActual)
            {
                if (field.Field.DeviceIdentity != MachineObjectIdentity)
                    throw new ArgumentException("Unknown AC power machine field identity.", nameof(selection));
                fields |= WindowsOperatingSystemInformationFields.AcPower;
                continue;
            }
            if (field.Definition == DeviceInformationFieldRegistry.KeyboardAllScanCodeRemapConfigured)
            {
                if (field.Field.DeviceIdentity != MachineObjectIdentity)
                    throw new ArgumentException("Unknown keyboard remap machine field identity.", nameof(selection));
                fields |= WindowsOperatingSystemInformationFields.KeyboardRemap;
                continue;
            }
            if (field.Field.DeviceIdentity != MachineIdentity)
                throw new ArgumentException("Unknown OS machine field identity.", nameof(selection));
            fields |= field.Definition == DeviceInformationFieldRegistry.OsAllUptimeActual ? WindowsOperatingSystemInformationFields.Uptime
                : field.Definition == DeviceInformationFieldRegistry.OsAllBuildDeclared ? WindowsOperatingSystemInformationFields.Build
                : field.Definition == DeviceInformationFieldRegistry.OsAllBootTimeActual ? WindowsOperatingSystemInformationFields.BootTime
                : throw new ArgumentException("Unknown OS information definition.", nameof(selection));
        }
        return fields;
    }
    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return new InformationCollectionBorrow(backend, Publications, CollectionItemId,
            async (selection, permit, token) =>
            {
                var committed = false;
                var queried = WindowsOperatingSystemInformationFields.None;
                var snapshot = await CaptureSelectedInformationAsync(SelectFields(selection), token, commit =>
                {
                    committed = permit.TryPublish(commit); return committed;
                }, sourceQueriesCompleted: mask => queried |= mask);
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    [.. selection.Where(item => (SelectFields([item]) & queried) != WindowsOperatingSystemInformationFields.None)
                        .Select(static item => item.Field)]);
            }, FieldCollectionSourceKind.SourceQuery,
            field => IsFixedDeclaration(SelectFields(backend.Select([field])))
                ? FieldCollectionSourceKind.CurrentValue : FieldCollectionSourceKind.SourceQuery);
    }

    public static bool IsFixedDeclaration(WindowsOperatingSystemInformationFields fields)
        => fields != WindowsOperatingSystemInformationFields.None
            && (fields & ~(WindowsOperatingSystemInformationFields.Build | WindowsOperatingSystemInformationFields.BootTime)) == 0;
    public ValueTask<DeviceInformationSnapshot> CaptureInformationAsync(CancellationToken cancellationToken = default,
        Func<Action, bool>? tryPublish = null)
        => CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.All, cancellationToken, tryPublish);

    public async ValueTask<DeviceInformationSnapshot> CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields selected,
        CancellationToken cancellationToken = default, Func<Action, bool>? tryPublish = null,
        Action<WindowsOperatingSystemInformationFields>? sourceQueriesCompleted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if ((selected & ~WindowsOperatingSystemInformationFields.All) != 0) throw new ArgumentOutOfRangeException(nameof(selected));
        cancellationToken.ThrowIfCancellationRequested();
        if (selected == WindowsOperatingSystemInformationFields.None)
        {
            sourceQueriesCompleted?.Invoke(WindowsOperatingSystemInformationFields.None);
            return ReadInformation();
        }
        await captureGate.WaitAsync(cancellationToken);
        var queried = WindowsOperatingSystemInformationFields.None;
        var queryEvidenceReported = false;
        void RecordSourceQuery(WindowsOperatingSystemInformationFields fields)
        {
            queried |= fields;
            if ((fields & WindowsOperatingSystemInformationFields.Build) != 0) buildObservedAt = clock.GetUtcNow();
            if ((fields & WindowsOperatingSystemInformationFields.BootTime) != 0) bootObservedAt = clock.GetUtcNow();
        }
        void ReportSourceQueries()
        {
            if (queryEvidenceReported) return;
            queryEvidenceReported = true;
            sourceQueriesCompleted?.Invoke(queried);
        }
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if ((selected & WindowsOperatingSystemInformationFields.Uptime) != 0)
                RecordSourceQuery(WindowsOperatingSystemInformationFields.Uptime);
            var uptime = (selected & WindowsOperatingSystemInformationFields.Uptime) != 0 ? read() : null;
            if ((selected & WindowsOperatingSystemInformationFields.AcPower) != 0)
                RecordSourceQuery(WindowsOperatingSystemInformationFields.AcPower);
            var acPower = (selected & WindowsOperatingSystemInformationFields.AcPower) != 0 ? acPowerStatusReader.Read() : null;
            if ((selected & WindowsOperatingSystemInformationFields.KeyboardRemap) != 0)
                RecordSourceQuery(WindowsOperatingSystemInformationFields.KeyboardRemap);
            var keyboardRemap = (selected & WindowsOperatingSystemInformationFields.KeyboardRemap) != 0
                ? keyboardScanCodeRemapReader.Read() : null;
            var facts = declarations.Read(selected, RecordSourceQuery); // Fixed cache hits and uptime-only perform no WMI query.
            var platform = platformDeclarations.Read(selected, cancellationToken, RecordSourceQuery);
            const WindowsOperatingSystemInformationFields componentFields = WindowsOperatingSystemInformationFields.Features
                | WindowsOperatingSystemInformationFields.ComponentPackages | WindowsOperatingSystemInformationFields.DriverPackages;
            var componentSelected = selected & componentFields;
            var componentReadSelected = componentSelected & componentScope.Fields;
            RecordSourceQuery(componentReadSelected);
            var components = componentReadSelected == WindowsOperatingSystemInformationFields.None
                ? new WindowsOperatingSystemComponentInformationReadResult(null, null, null)
                : componentReader.Read(componentScope, componentReadSelected, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReportSourceQueries();
            var observedAt = clock.GetUtcNow();
            var previous = ReadInformation();
            DeviceInformationField Update(DeviceInformationDefinition definition, WindowsOperatingSystemInformationFields flag, string? value)
                => (selected & flag) == 0
                    ? previous.FindField(new(MachineIdentity, definition.Name)) ?? new(definition, null)
                    : new(definition, value is null ? null : JsonSerializer.SerializeToElement(value))
                    {
                        ObservedAt = value is null ? null : flag == WindowsOperatingSystemInformationFields.Build ? buildObservedAt
                            : flag == WindowsOperatingSystemInformationFields.BootTime ? bootObservedAt : observedAt
                    };
            DeviceInformationField[] fields = [
                Update(DeviceInformationFieldRegistry.OsAllUptimeActual, WindowsOperatingSystemInformationFields.Uptime,
                    uptime?.Seconds.ToString(CultureInfo.InvariantCulture)),
                Update(DeviceInformationFieldRegistry.OsAllBuildDeclared, WindowsOperatingSystemInformationFields.Build, facts.BuildDeclared),
                Update(DeviceInformationFieldRegistry.OsAllBootTimeActual, WindowsOperatingSystemInformationFields.BootTime,
                    facts.BootTimeActual?.ToString("O", CultureInfo.InvariantCulture))];
            var objects = new List<DeviceInformationObject> { new(MachineIdentity, "local_operating_system", fields) };
            var priorMachine = previous.Devices.FirstOrDefault(device => device.DeviceIdentity == MachineObjectIdentity);
            var acPowerSelected = (selected & WindowsOperatingSystemInformationFields.AcPower) != 0;
            var keyboardRemapSelected = (selected & WindowsOperatingSystemInformationFields.KeyboardRemap) != 0;
            if (acPowerSelected || keyboardRemapSelected)
            {
                var machineFields = priorMachine?.Fields.ToList() ?? [];
                if (acPowerSelected)
                {
                    bool? acPowerState = acPower is { } reading ? reading.AcLineStatus switch
                    {
                        0 => false,
                        1 => true,
                        _ => null
                    } : null;
                    var acPowerDefinition = DeviceInformationFieldRegistry.MachineAllAcPowerActual;
                    machineFields.RemoveAll(field => field.Definition == acPowerDefinition);
                    machineFields.Add(new(acPowerDefinition, acPowerState is null ? null : JsonSerializer.SerializeToElement(acPowerState.Value))
                        { ObservedAt = acPowerState is null ? null : observedAt });
                }
                if (keyboardRemapSelected)
                {
                    var keyboardDefinition = DeviceInformationFieldRegistry.KeyboardAllScanCodeRemapConfigured;
                    machineFields.RemoveAll(field => field.Definition == keyboardDefinition);
                    machineFields.Add(new(keyboardDefinition, keyboardRemap is null ? null : SerializeKeyboardRemap(keyboardRemap))
                        { ObservedAt = keyboardRemap is null ? null : observedAt });
                }
                objects.Add(new(MachineObjectIdentity, "machine", machineFields));
            }
            else if (priorMachine is not null) objects.Add(priorMachine);
            foreach (var category in new[] { WindowsOperatingSystemInformationFields.Activation, WindowsOperatingSystemInformationFields.Environment,
                WindowsOperatingSystemInformationFields.Startup, WindowsOperatingSystemInformationFields.Packages, WindowsOperatingSystemInformationFields.SessionId })
            {
                var prior = previous.Devices.Where(device => device.Fields.Any(field => WindowsPlatformDeclarationProjection.Category(field.Definition) == category));
                if ((selected & category) == 0) objects.AddRange(prior);
                else if (WindowsPlatformDeclarationProjection.Succeeded(platform, category))
                    objects.AddRange(WindowsPlatformDeclarationProjection.Project(platform, category, observedAt));
                else objects.AddRange(prior.Select(device => device with
                    { Fields = device.Fields.Select(field => field with { Value = null, ObservedAt = null }).ToArray() }));
            }
            foreach (var category in new[] { WindowsOperatingSystemInformationFields.Features,
                WindowsOperatingSystemInformationFields.ComponentPackages, WindowsOperatingSystemInformationFields.DriverPackages })
            {
                var prior = previous.Devices.Where(device => device.Fields.Any(field =>
                    WindowsOperatingSystemComponentInformationProjection.Category(field.Definition) == category));
                if ((componentSelected & category) == 0) objects.AddRange(prior);
                else if (WindowsOperatingSystemComponentInformationProjection.Succeeded(components, category))
                    objects.AddRange(WindowsOperatingSystemComponentInformationProjection.Project(components, category, componentScope, observedAt));
                else objects.AddRange(prior.Select(device => device with
                    { Fields = device.Fields.Select(field => field with { Value = null, ObservedAt = null }).ToArray() }));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var next = new DeviceInformationSnapshot(objects.Any(static device => device.Fields.Any(static field => field.Value is not null)) ? observedAt : null, objects.ToArray());
            void Commit()
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                Volatile.Write(ref current, next); Publications.Publish([CollectionItemId]);
            }
            if (tryPublish is null) Commit(); else _ = tryPublish(Commit);
            return ReadInformation();
        }
        catch
        {
            ReportSourceQueries();
            throw;
        }
        finally { captureGate.Release(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        captureGate.Wait();
        try { ownedComponentReader?.Dispose(); }
        finally { captureGate.Release(); }
        // No AvailableWaitHandle is created. Late queued callers reject and release.
    }

    private sealed class UnavailableAcPowerStatusReader : IWindowsAcPowerStatusReader
    {
        public WindowsAcPowerStatusReading? Read() => null;
    }

    private sealed class UnavailableKeyboardScanCodeRemapReader : IWindowsKeyboardScanCodeRemapReader
    {
        public WindowsKeyboardScanCodeRemapReading? Read() => null;
    }

    private sealed class UnavailableComponentInformationReader : IWindowsOperatingSystemComponentInformationReader
    {
        internal static readonly UnavailableComponentInformationReader Instance = new();
        public WindowsOperatingSystemComponentInformationReadResult Read(
            WindowsOperatingSystemComponentInformationScope scope,
            WindowsOperatingSystemInformationFields selected,
            CancellationToken cancellationToken = default)
            => new(null, null, null);
    }

    private static System.Text.Json.JsonElement SerializeKeyboardRemap(WindowsKeyboardScanCodeRemapReading reading)
        => JsonSerializer.SerializeToElement(new
        {
            configured = reading.Configured,
            entries = reading.Entries.Select(static entry => new
            {
                sourceScanCode = "0x" + entry.SourceScanCode.ToString("X4", CultureInfo.InvariantCulture),
                targetScanCode = "0x" + entry.TargetScanCode.ToString("X4", CultureInfo.InvariantCulture)
            }).ToArray()
        });
}
