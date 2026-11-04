using System.Buffers.Binary;
using System.ComponentModel;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Drivers;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Media;

[SupportedOSPlatform("windows")]
internal sealed class WindowsMediaInformationNativeReader : IWindowsMediaInformationReader
{
    private const uint ClsctxAll = 0x17;
    private const uint CoInitMultithreaded = 0x0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int MmDeviceNotFound = unchecked((int)0x80070490);
    private const int MaxTopologyConnectors = 64;
    private const int MaxRegisteredTypesPerTransform = 4096;
    private const int MaxWaveFormatExtraBytes = 1024;
    private const ushort WaveFormatExtensibleTag = 0xFFFE;
    private const uint MfVersion = 0x00020070;
    private const uint MfStartupFull = 0;
    internal const uint MftEnumReservedFlags = 0;
    private const uint CrSuccess = 0;
    private const int HResultFalse = 1;
    private const uint ClsctxInprocServer = 0x1;

    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid ClsidSystemDeviceEnum = new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
    private static readonly Guid IidMmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidCreateDevEnum = new("29840822-5B84-11D0-BD3B-00A0C911CE86");
    private static readonly Guid IidPropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidDeviceTopology = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    public WindowsMediaInformationReadResult Read(WindowsMediaInformationScope scope,
        WindowsMediaInformationFields selectedFields, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directories = new List<WindowsMediaDirectoryRead>();
        if (selectedFields == WindowsMediaInformationFields.None)
            return new(directories);

        ComApartmentLease apartment;
        try { apartment = ComApartmentLease.Enter(); }
        catch (Exception exception) when (IsNativeReadFailure(exception))
        {
            return FailedDirectories(scope, selectedFields);
        }

        using (apartment)
        {
            if ((selectedFields & WindowsMediaInformationFields.Audio) != 0)
            {
                try { ReadAudio(scope, selectedFields, cancellationToken, directories); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (IsNativeReadFailure(exception))
                {
                    directories.RemoveAll(static item => item.IdentityPrefix.StartsWith("windows-audio-endpoint|", StringComparison.Ordinal)
                        || item.IdentityPrefix == WindowsInformationFieldProjection.AudioDeviceIdentityPrefix);
                    AddAudioFailures(scope, selectedFields, directories);
                }
            }
            if ((selectedFields & WindowsMediaInformationFields.Codec) != 0)
                ReadCodecs(scope, selectedFields, cancellationToken, directories);
        }
        return new(directories);
    }

    internal static DeviceTopologyWaveFormat? ParseWaveFormat(ReadOnlySpan<byte> bytes)
    {
        const int waveFormatExSize = 18;
        const int waveFormatExtensibleMinimum = 22;
        if (bytes.Length < waveFormatExSize) return null;
        var extraSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..18]);
        if (extraSize > MaxWaveFormatExtraBytes || bytes.Length != waveFormatExSize + extraSize) return null;

        var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(bytes[0..2]);
        ushort? validBits = null;
        uint? channelMask = null;
        Guid? subFormat = null;
        if (formatTag == WaveFormatExtensibleTag)
        {
            if (extraSize < waveFormatExtensibleMinimum) return null;
            validBits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..20]);
            channelMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..24]);
            subFormat = new Guid(bytes[24..40]);
        }

        return new DeviceTopologyWaveFormat(formatTag,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..14]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..16]),
            extraSize, validBits, channelMask, subFormat,
            Convert.ToBase64String(bytes[waveFormatExSize..]));
    }

    private static void ReadAudio(WindowsMediaInformationScope scope, WindowsMediaInformationFields selected,
        CancellationToken cancellationToken, List<WindowsMediaDirectoryRead> directories)
    {
        var endpointFields = selected & WindowsMediaInformationScope.AudioEndpointFields;
        var driverSelected = (selected & WindowsMediaInformationFields.AudioDriverVersion) != 0;
        IMMDeviceEnumerator? enumerator = null;
        var classId = ClsidMmDeviceEnumerator;
        var interfaceId = IidMmDeviceEnumerator;
        IMMDeviceEnumerator audioEnumerator;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(CoCreateInstance(ref classId, IntPtr.Zero, ClsctxAll, ref interfaceId, out enumerator), "CoCreateInstance(MMDeviceEnumerator)");
            audioEnumerator = enumerator ?? throw new InvalidDataException("CoCreateInstance returned no MMDevice enumerator.");
        }
        catch (Exception exception) when (IsNativeReadFailure(exception))
        {
            foreach (var direction in scope.AudioDirections)
                if (endpointFields != WindowsMediaInformationFields.None)
                    directories.Add(new(WindowsInformationFieldProjection.AudioIdentityPrefix(direction), false, []));
            if (driverSelected)
                directories.Add(new(WindowsInformationFieldProjection.AudioDeviceIdentityPrefix, false, []));
            Release(ref enumerator);
            return;
        }

        try
        {
            var connectedDevices = new Dictionary<string, DeviceTopologyAudioDeviceInformation>(StringComparer.OrdinalIgnoreCase);
            var driverDirectorySucceeded = true;
            foreach (var direction in scope.AudioDirections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = ReadAudioDirection(audioEnumerator, scope, direction, selected, cancellationToken);
                    if (endpointFields != WindowsMediaInformationFields.None)
                        directories.Add(result.EndpointDirectory);
                    if (driverSelected)
                    {
                        driverDirectorySucceeded &= result.DriverDirectorySucceeded;
                        foreach (var device in result.AudioDevices)
                        {
                            if (connectedDevices.TryGetValue(device.DeviceInstanceId, out var previous))
                            {
                                if (!string.Equals(previous.DriverVersion, device.DriverVersion, StringComparison.Ordinal))
                                    driverDirectorySucceeded = false;
                            }
                            else connectedDevices.Add(device.DeviceInstanceId, device);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (IsNativeReadFailure(exception))
                {
                    if (endpointFields != WindowsMediaInformationFields.None)
                        directories.Add(new(WindowsInformationFieldProjection.AudioIdentityPrefix(direction), false, []));
                    if (driverSelected) driverDirectorySucceeded = false;
                }
            }

            if (driverSelected)
            {
                DeviceInformationObject[] objects = driverDirectorySucceeded
                    ? connectedDevices.Values.OrderBy(static device => device.DeviceInstanceId, StringComparer.OrdinalIgnoreCase)
                        .Select(device => WindowsInformationFieldProjection.ProjectAudioDevice(device, selected)).ToArray()
                    : [];
                directories.Add(new(WindowsInformationFieldProjection.AudioDeviceIdentityPrefix,
                    driverDirectorySucceeded, objects));
            }
        }
        finally { Release(ref enumerator); }
    }

    private static AudioDirectionReadResult ReadAudioDirection(IMMDeviceEnumerator enumerator,
        WindowsMediaInformationScope scope, WindowsAudioDataFlow direction,
        WindowsMediaInformationFields selected, CancellationToken cancellationToken)
    {
        IMMDeviceCollection? collection = null;
        cancellationToken.ThrowIfCancellationRequested();
        var hr = enumerator.EnumAudioEndpoints(direction, scope.AudioStateMask, out collection);
        try
        {
            Check(hr, "IMMDeviceEnumerator.EnumAudioEndpoints");
            cancellationToken.ThrowIfCancellationRequested();
            var nativeCollection = collection ?? throw new InvalidDataException("EnumAudioEndpoints returned no collection.");
            Check(nativeCollection.GetCount(out var count), "IMMDeviceCollection.GetCount");
            if (count > scope.MaximumObjectsPerCategory)
                throw new InvalidDataException("Audio endpoint count exceeds the caller's explicit scope bound.");

            var endpointIds = new List<string>(checked((int)count));
            var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IMMDevice? endpoint = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Check(nativeCollection.Item(index, out endpoint), "IMMDeviceCollection.Item");
                    var endpointDevice = endpoint ?? throw new InvalidDataException("IMMDeviceCollection.Item returned no endpoint.");
                    var endpointId = ReadEndpointId(endpointDevice, cancellationToken);
                    if (!uniqueIds.Add(endpointId))
                        throw new InvalidDataException("The endpoint directory returned a duplicate endpoint identity.");
                    endpointIds.Add(endpointId);
                }
                finally { Release(ref endpoint); }
            }
            endpointIds.Sort(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, HashSet<WindowsAudioDefaultRole>>? defaultRoles = null;
            var rolesSucceeded = true;
            if ((selected & WindowsMediaInformationFields.AudioDefaultRoles) != 0)
            {
                defaultRoles = endpointIds.ToDictionary(static id => id,
                    static _ => new HashSet<WindowsAudioDefaultRole>(), StringComparer.OrdinalIgnoreCase);
                foreach (var role in Enum.GetValues<WindowsAudioDefaultRole>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var defaultId = ReadDefaultEndpointId(enumerator, direction, role, cancellationToken);
                        if (defaultId is not null && defaultRoles.TryGetValue(defaultId, out var roles)) roles.Add(role);
                    }
                    catch (COMException exception) when (exception.HResult == MmDeviceNotFound)
                    {
                        // No default endpoint for this role is a successful empty relationship.
                    }
                    catch (Exception exception) when (IsNativeReadFailure(exception))
                    {
                        rolesSucceeded = false;
                        break;
                    }
                }
            }

            var endpointFields = selected & WindowsMediaInformationScope.AudioEndpointFields;
            var objects = new List<DeviceInformationObject>(endpointFields == WindowsMediaInformationFields.None ? 0 : endpointIds.Count);
            var audioDevices = new List<DeviceTopologyAudioDeviceInformation>();
            var driverDirectorySucceeded = true;
            foreach (var endpointId in endpointIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var endpoint = new DeviceTopologyAudioEndpointInformation(endpointId, direction,
                    (selected & WindowsMediaInformationFields.AudioDefaultRoles) != 0 && rolesSucceeded
                        ? ImmutableArray.CreateRange(defaultRoles![endpointId].Order()) : null,
                    MixFormat: (selected & WindowsMediaInformationFields.AudioMixFormat) != 0
                        ? TryReadMixFormat(enumerator, endpointId, cancellationToken) : null);
                if (endpointFields != WindowsMediaInformationFields.None)
                    objects.Add(WindowsInformationFieldProjection.ProjectAudio(endpoint, selected));
                if ((selected & WindowsMediaInformationFields.AudioDriverVersion) != 0)
                {
                    var connectedDevice = ReadConnectedAudioDevice(enumerator, endpointId, cancellationToken);
                    driverDirectorySucceeded &= connectedDevice.Succeeded;
                    if (connectedDevice.Device is not null) audioDevices.Add(connectedDevice.Device);
                }
            }
            return new(new(WindowsInformationFieldProjection.AudioIdentityPrefix(direction), true, objects),
                driverDirectorySucceeded, audioDevices);
        }
        finally { Release(ref collection); }
    }

    private static string? ReadDefaultEndpointId(IMMDeviceEnumerator enumerator,
        WindowsAudioDataFlow direction, WindowsAudioDefaultRole role, CancellationToken cancellationToken)
    {
        IMMDevice? endpoint = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hr = enumerator.GetDefaultAudioEndpoint(direction, role, out endpoint);
            if (hr == MmDeviceNotFound) return null;
            Check(hr, "IMMDeviceEnumerator.GetDefaultAudioEndpoint");
            return ReadEndpointId(endpoint ?? throw new InvalidDataException("GetDefaultAudioEndpoint returned no endpoint."), cancellationToken);
        }
        finally { Release(ref endpoint); }
    }

    private static string ReadEndpointId(IMMDevice endpoint, CancellationToken cancellationToken)
    {
        IntPtr value = IntPtr.Zero;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(endpoint.GetId(out value), "IMMDevice.GetId");
            cancellationToken.ThrowIfCancellationRequested();
            var id = value == IntPtr.Zero ? null : Marshal.PtrToStringUni(value);
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("IMMDevice.GetId returned no endpoint identity.");
            return id;
        }
        finally { if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value); }
    }

    private static DeviceTopologyWaveFormat? TryReadMixFormat(IMMDeviceEnumerator enumerator, string endpointId,
        CancellationToken cancellationToken)
    {
        IMMDevice? endpoint = null;
        object? activated = null;
        IntPtr format = IntPtr.Zero;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(enumerator.GetDevice(endpointId, out endpoint), "IMMDeviceEnumerator.GetDevice");
            cancellationToken.ThrowIfCancellationRequested();
            var iid = IidAudioClient;
            var endpointDevice = endpoint ?? throw new InvalidDataException("GetDevice returned no endpoint.");
            Check(endpointDevice.Activate(ref iid, ClsctxAll, IntPtr.Zero, out activated), "IMMDevice.Activate(IAudioClient)");
            cancellationToken.ThrowIfCancellationRequested();
            var audioClient = activated as IAudioClient ?? throw new InvalidCastException("Audio endpoint did not expose IAudioClient.");
            Check(audioClient.GetMixFormat(out format), "IAudioClient.GetMixFormat");
            cancellationToken.ThrowIfCancellationRequested();
            if (format == IntPtr.Zero) return null;
            const int waveFormatExSize = 18;
            var extraSize = unchecked((ushort)Marshal.ReadInt16(format, 16));
            if (extraSize > MaxWaveFormatExtraBytes) return null;
            var bytes = new byte[waveFormatExSize + extraSize];
            Marshal.Copy(format, bytes, 0, bytes.Length);
            return ParseWaveFormat(bytes);
        }
        catch (Exception exception) when (IsNativeReadFailure(exception)) { return null; }
        finally
        {
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            Release(ref activated);
            Release(ref endpoint);
        }
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows PnP device properties changed")]
    private static ConnectedAudioDeviceRead ReadConnectedAudioDevice(IMMDeviceEnumerator enumerator, string endpointId,
        CancellationToken cancellationToken)
    {
        IMMDevice? endpoint = null;
        object? activated = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(enumerator.GetDevice(endpointId, out endpoint), "IMMDeviceEnumerator.GetDevice");
            cancellationToken.ThrowIfCancellationRequested();
            var iid = IidDeviceTopology;
            var endpointDevice = endpoint ?? throw new InvalidDataException("GetDevice returned no endpoint.");
            Check(endpointDevice.Activate(ref iid, ClsctxAll, IntPtr.Zero, out activated), "IMMDevice.Activate(IDeviceTopology)");
            cancellationToken.ThrowIfCancellationRequested();
            var topology = activated as IDeviceTopology ?? throw new InvalidCastException("Audio endpoint did not expose IDeviceTopology.");
            Check(topology.GetConnectorCount(out var connectorCount), "IDeviceTopology.GetConnectorCount");
            cancellationToken.ThrowIfCancellationRequested();
            if (connectorCount > MaxTopologyConnectors) return new(false, null);
            if (connectorCount == 0) return new(true, null);

            var connectedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var incomplete = false;
            for (uint index = 0; index < connectorCount; index++)
            {
                IConnector? connector = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Check(topology.GetConnector(index, out connector), "IDeviceTopology.GetConnector");
                    cancellationToken.ThrowIfCancellationRequested();
                    var nativeConnector = connector ?? throw new InvalidDataException("GetConnector returned no connector.");
                    Check(nativeConnector.IsConnected(out var isConnected), "IConnector.IsConnected");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!isConnected) continue;
                    IntPtr connectedId = IntPtr.Zero;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Check(nativeConnector.GetDeviceIdConnectedTo(out connectedId), "IConnector.GetDeviceIdConnectedTo");
                        cancellationToken.ThrowIfCancellationRequested();
                        var id = connectedId == IntPtr.Zero ? null : Marshal.PtrToStringUni(connectedId);
                        if (string.IsNullOrWhiteSpace(id)) incomplete = true;
                        else connectedDeviceIds.Add(id);
                    }
                    finally { if (connectedId != IntPtr.Zero) Marshal.FreeCoTaskMem(connectedId); }
                }
                catch (Exception exception) when (IsNativeReadFailure(exception)) { incomplete = true; }
                finally { Release(ref connector); }
            }
            if (incomplete) return new(false, null);
            if (connectedDeviceIds.Count == 0) return new(true, null);
            if (connectedDeviceIds.Count != 1) return new(false, null);

            var deviceInstanceId = connectedDeviceIds.Single();
            cancellationToken.ThrowIfCancellationRequested();
            var status = CmLocateDevNodeW(out var devInst, deviceInstanceId, 0);
            cancellationToken.ThrowIfCancellationRequested();
            if (status != CrSuccess) return new(false, null);
            var driverVersion = WindowsDeviceDriverPropertyReader.ReadDriverVersion(devInst);
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, new(deviceInstanceId, driverVersion));
        }
        catch (Exception exception) when (IsNativeReadFailure(exception)) { return new(false, null); }
        finally
        {
            Release(ref activated);
            Release(ref endpoint);
        }
    }

    private static void ReadCodecs(WindowsMediaInformationScope scope, WindowsMediaInformationFields selected,
        CancellationToken cancellationToken, List<WindowsMediaDirectoryRead> directories)
    {
        var processView = Environment.Is64BitProcess
            ? WindowsCodecRegistryView.Registry64 : WindowsCodecRegistryView.Registry32;
        if (scope.CodecRegistryView != processView)
        {
            foreach (var category in scope.CodecCategories)
                directories.Add(new(WindowsInformationFieldProjection.MftIdentityPrefix(category), false, []));
            foreach (var category in scope.DirectShowCategories)
                directories.Add(new(WindowsInformationFieldProjection.DirectShowIdentityPrefix(category), false, []));
            return;
        }

        if (!scope.CodecCategories.IsEmpty)
        {
            MediaFoundationLease? mediaFoundation = null;
            try { mediaFoundation = MediaFoundationLease.Start(); }
            catch (Exception exception) when (IsNativeReadFailure(exception))
            {
                foreach (var category in scope.CodecCategories)
                    directories.Add(new(WindowsInformationFieldProjection.MftIdentityPrefix(category), false, []));
            }
            if (mediaFoundation is not null)
            {
                using (mediaFoundation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var category in scope.CodecCategories)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            directories.Add(ReadMftCategory(category, scope.MaximumObjectsPerCategory,
                                scope.CodecRegistryView, selected, cancellationToken));
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception exception) when (IsNativeReadFailure(exception))
                        {
                            directories.Add(new(WindowsInformationFieldProjection.MftIdentityPrefix(category), false, []));
                        }
                    }
                }
            }
        }

        if (!scope.DirectShowCategories.IsEmpty)
            ReadDirectShow(scope, selected, cancellationToken, directories);
    }

    private static WindowsMediaDirectoryRead ReadMftCategory(WindowsMftCategory category, int maximumObjects,
        WindowsCodecRegistryView registryView,
        WindowsMediaInformationFields selected, CancellationToken cancellationToken)
    {
        IntPtr classIds = IntPtr.Zero;
        var categoryId = WindowsMediaInformationScope.CategoryId(category);
        uint count = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(MftEnum(categoryId, MftEnumReservedFlags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                out classIds, out count), "MFTEnum");
            cancellationToken.ThrowIfCancellationRequested();
            if (count > maximumObjects) throw new InvalidDataException("Registered MFT count exceeds the caller's explicit category bound.");
            if (count > 0 && classIds == IntPtr.Zero) throw new InvalidDataException("MFTEnum returned a missing CLSID array.");

            var objects = new List<DeviceInformationObject>(checked((int)count));
            var uniqueClassIds = new HashSet<Guid>();
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = checked((int)((long)index * 16));
                var classId = Marshal.PtrToStructure<Guid>(IntPtr.Add(classIds, offset));
                if (!uniqueClassIds.Add(classId)) continue;

                ImmutableArray<DeviceTopologyRegisteredMediaType>? inputTypes = null;
                ImmutableArray<DeviceTopologyRegisteredMediaType>? outputTypes = null;
                if ((selected & WindowsMediaInformationFields.CodecMediaTypes) != 0)
                {
                    try
                    {
                        var registered = ReadRegisteredTypes(classId, cancellationToken);
                        inputTypes = registered.Input;
                        outputTypes = registered.Output;
                    }
                    catch (Exception exception) when (IsNativeReadFailure(exception))
                    {
                        inputTypes = null;
                        outputTypes = null;
                    }
                }

                var version = (selected & WindowsMediaInformationFields.CodecVersion) != 0
                    ? TryReadCodecModuleVersion(classId, WindowsCodecRegistrationSource.MediaFoundationMft,
                        registryView, cancellationToken)
                    : null;
                var transform = new DeviceTopologyRegisteredMftInformation(category, classId,
                    inputTypes, outputTypes, version);
                objects.Add(WindowsInformationFieldProjection.ProjectMft(transform, selected, registryView));
            }
            return new(WindowsInformationFieldProjection.MftIdentityPrefix(category), true, objects);
        }
        finally { if (classIds != IntPtr.Zero) Marshal.FreeCoTaskMem(classIds); }
    }

    private static void ReadDirectShow(WindowsMediaInformationScope scope, WindowsMediaInformationFields selected,
        CancellationToken cancellationToken, List<WindowsMediaDirectoryRead> directories)
    {
        IDirectShowDeviceEnumerator? enumerator = null;
        var classId = ClsidSystemDeviceEnum;
        var interfaceId = IidCreateDevEnum;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(CoCreateDirectShowEnumerator(ref classId, IntPtr.Zero, ClsctxInprocServer,
                ref interfaceId, out enumerator), "CoCreateInstance(SystemDeviceEnum)");
            var deviceEnumerator = enumerator
                ?? throw new InvalidDataException("CoCreateInstance returned no DirectShow device enumerator.");
            foreach (var category in scope.DirectShowCategories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    directories.Add(ReadDirectShowCategory(deviceEnumerator, category, scope,
                        selected, cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (IsNativeReadFailure(exception))
                {
                    directories.Add(new(WindowsInformationFieldProjection.DirectShowIdentityPrefix(category), false, []));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsNativeReadFailure(exception))
        {
            foreach (var category in scope.DirectShowCategories)
                if (!directories.Any(directory => directory.IdentityPrefix == WindowsInformationFieldProjection.DirectShowIdentityPrefix(category)))
                    directories.Add(new(WindowsInformationFieldProjection.DirectShowIdentityPrefix(category), false, []));
        }
        finally { Release(ref enumerator); }
    }

    private static WindowsMediaDirectoryRead ReadDirectShowCategory(IDirectShowDeviceEnumerator enumerator,
        WindowsDirectShowCategory category, WindowsMediaInformationScope scope,
        WindowsMediaInformationFields selected, CancellationToken cancellationToken)
    {
        var categoryId = WindowsMediaInformationScope.DirectShowCategoryId(category);
        IDirectShowEnumMoniker? monikers = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = enumerator.CreateClassEnumerator(ref categoryId, out monikers, 0);
            if (result == HResultFalse)
                return new(WindowsInformationFieldProjection.DirectShowIdentityPrefix(category), true, []);
            Check(result, "ICreateDevEnum.CreateClassEnumerator");
            var categoryMonikers = monikers
                ?? throw new InvalidDataException("CreateClassEnumerator returned no moniker enumerator for S_OK.");
            var objects = new List<DeviceInformationObject>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IDirectShowMoniker? moniker = null;
                try
                {
                    var nextResult = categoryMonikers.Next(1, out moniker, out var fetched);
                    if (nextResult == HResultFalse && fetched == 0) break;
                    Check(nextResult, "IEnumMoniker.Next");
                    if (fetched != 1 || moniker is null)
                        throw new InvalidDataException("IEnumMoniker.Next returned an invalid moniker count.");
                    if (objects.Count >= scope.MaximumObjectsPerCategory)
                        throw new InvalidDataException("DirectShow category count exceeds the caller's explicit category bound.");

                    var displayName = ReadMonikerDisplayName(moniker, cancellationToken);
                    if (string.IsNullOrWhiteSpace(displayName))
                        throw new InvalidDataException("A DirectShow moniker returned no exact display-name identity.");
                    var properties = ReadMonikerProperties(moniker, cancellationToken);
                    var classId = properties.ClassId;
                    var version = (selected & WindowsMediaInformationFields.CodecVersion) != 0 && classId is { } registeredClassId
                        ? TryReadCodecModuleVersion(registeredClassId, WindowsCodecRegistrationSource.DirectShowCategoryMoniker,
                            scope.CodecRegistryView, cancellationToken)
                        : null;
                    var source = new DeviceTopologyRegisteredDirectShowInformation(category,
                        displayName, classId, properties.FriendlyName, properties.DevicePath,
                        InputTypes: null, OutputTypes: null, Version: version);
                    var projected = WindowsInformationFieldProjection.ProjectDirectShow(source, selected, scope.CodecRegistryView);
                    if (!identities.Add(projected.DeviceIdentity))
                        throw new InvalidDataException("A DirectShow category returned a duplicate moniker identity.");
                    objects.Add(projected);
                }
                finally { Release(ref moniker); }
            }
            return new(WindowsInformationFieldProjection.DirectShowIdentityPrefix(category), true, objects);
        }
        finally { Release(ref monikers); }
    }

    private static string? ReadMonikerDisplayName(IDirectShowMoniker moniker, CancellationToken cancellationToken)
    {
        IntPtr displayName = IntPtr.Zero;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(moniker.GetDisplayName(IntPtr.Zero, IntPtr.Zero, out displayName), "IMoniker.GetDisplayName");
            cancellationToken.ThrowIfCancellationRequested();
            return displayName == IntPtr.Zero ? null : Marshal.PtrToStringUni(displayName);
        }
        finally { if (displayName != IntPtr.Zero) Marshal.FreeCoTaskMem(displayName); }
    }

    private static DirectShowMonikerProperties ReadMonikerProperties(IDirectShowMoniker moniker,
        CancellationToken cancellationToken)
    {
        object? propertyBagObject = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var interfaceId = IidPropertyBag;
            var result = moniker.BindToStorage(IntPtr.Zero, IntPtr.Zero, ref interfaceId, out propertyBagObject);
            if (result < 0 || propertyBagObject is not IDirectShowPropertyBag propertyBag)
                return new(null, null, null);
            return new(
                TryReadPropertyBagString(propertyBag, "CLSID", cancellationToken) is { } clsidText
                    && Guid.TryParse(clsidText, out var classId) ? classId : null,
                TryReadPropertyBagString(propertyBag, "FriendlyName", cancellationToken),
                TryReadPropertyBagString(propertyBag, "DevicePath", cancellationToken));
        }
        finally { Release(ref propertyBagObject); }
    }

    private static string? TryReadPropertyBagString(IDirectShowPropertyBag propertyBag, string propertyName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = propertyBag.Read(propertyName, out var value, IntPtr.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        if (result < 0) return null;
        return value as string;
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows device-bound driver package changed")]

    private static DeviceTopologyCodecModuleVersion? TryReadCodecModuleVersion(Guid classId,
        WindowsCodecRegistrationSource source, WindowsCodecRegistryView registryView,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var view = registryView switch
            {
                WindowsCodecRegistryView.Registry32 => RegistryView.Registry32,
                WindowsCodecRegistryView.Registry64 => RegistryView.Registry64,
                _ => throw new ArgumentOutOfRangeException(nameof(registryView))
            };
            using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using var server = classes.OpenSubKey($@"CLSID\{classId:B}\InprocServer32");
            var registeredPath = server?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (string.IsNullOrWhiteSpace(registeredPath)) return null;
            var expandedPath = Environment.ExpandEnvironmentVariables(registeredPath.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(expandedPath)) return null;
            var resolvedPath = Path.GetFullPath(expandedPath);
            if (!File.Exists(resolvedPath)) return null;
            cancellationToken.ThrowIfCancellationRequested();
            var file = FileVersionInfo.GetVersionInfo(resolvedPath);
            if (string.IsNullOrWhiteSpace(file.FileVersion)) return null;
            cancellationToken.ThrowIfCancellationRequested();
            return new(source, classId, registryView, registeredPath, resolvedPath,
                file.FileVersion, EmptyToNull(file.CompanyName), EmptyToNull(file.ProductName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SecurityException or ArgumentException or NotSupportedException or Win32Exception)
        {
            return null;
        }
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static (ImmutableArray<DeviceTopologyRegisteredMediaType> Input,
        ImmutableArray<DeviceTopologyRegisteredMediaType> Output) ReadRegisteredTypes(Guid classId,
        CancellationToken cancellationToken)
    {
        IntPtr inputPointer = IntPtr.Zero;
        IntPtr outputPointer = IntPtr.Zero;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(MftGetInfo(classId, IntPtr.Zero, out inputPointer, out var inputCount,
                out outputPointer, out var outputCount, IntPtr.Zero), "MFTGetInfo");
            cancellationToken.ThrowIfCancellationRequested();
            return (ReadTypeArray(inputPointer, inputCount), ReadTypeArray(outputPointer, outputCount));
        }
        finally
        {
            if (inputPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(inputPointer);
            if (outputPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(outputPointer);
        }
    }

    private static ImmutableArray<DeviceTopologyRegisteredMediaType> ReadTypeArray(IntPtr pointer, uint count)
    {
        if (count > MaxRegisteredTypesPerTransform || (count > 0 && pointer == IntPtr.Zero))
            throw new InvalidDataException("MFTGetInfo returned an invalid registered media-type array.");
        var elementSize = Marshal.SizeOf<NativeMftRegisterTypeInfo>();
        var result = ImmutableArray.CreateBuilder<DeviceTopologyRegisteredMediaType>(checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            var offset = checked((int)((long)index * elementSize));
            var item = Marshal.PtrToStructure<NativeMftRegisterTypeInfo>(IntPtr.Add(pointer, offset));
            result.Add(new(item.MajorType, item.SubType));
        }
        return [.. result.Distinct().OrderBy(static item => item.MajorType).ThenBy(static item => item.SubType)];
    }

    private static WindowsMediaInformationReadResult FailedDirectories(WindowsMediaInformationScope scope,
        WindowsMediaInformationFields selected)
    {
        var directories = new List<WindowsMediaDirectoryRead>();
        AddAudioFailures(scope, selected, directories);
        if ((selected & WindowsMediaInformationFields.Codec) != 0)
        {
            foreach (var category in scope.CodecCategories)
                directories.Add(new(WindowsInformationFieldProjection.MftIdentityPrefix(category), false, []));
            foreach (var category in scope.DirectShowCategories)
                directories.Add(new(WindowsInformationFieldProjection.DirectShowIdentityPrefix(category), false, []));
        }
        return new(directories);
    }

    private static void AddAudioFailures(WindowsMediaInformationScope scope,
        WindowsMediaInformationFields selected, List<WindowsMediaDirectoryRead> directories)
    {
        if ((selected & WindowsMediaInformationScope.AudioEndpointFields) != 0)
            foreach (var direction in scope.AudioDirections)
                directories.Add(new(WindowsInformationFieldProjection.AudioIdentityPrefix(direction), false, []));
        if ((selected & WindowsMediaInformationFields.AudioDriverVersion) != 0)
            directories.Add(new(WindowsInformationFieldProjection.AudioDeviceIdentityPrefix, false, []));
    }

    private static bool IsNativeReadFailure(Exception exception)
        => exception is COMException or Win32Exception or InvalidDataException or OverflowException
            or InvalidCastException or InvalidOperationException or ArgumentException
            or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException
            or TypeLoadException or NotSupportedException or MarshalDirectiveException;

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new COMException($"{operation} failed.", result);
    }

    private static void Release<T>(ref T? instance) where T : class
    {
        var owned = instance;
        instance = null;
        if (owned is not null && Marshal.IsComObject(owned)) _ = Marshal.ReleaseComObject(owned);
    }

    private readonly struct ComApartmentLease(bool uninitialize) : IDisposable
    {
        public static ComApartmentLease Enter()
        {
            var result = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
            if (result == RpcEChangedMode) return new(false);
            Check(result, "CoInitializeEx");
            return new(true);
        }

        public void Dispose()
        {
            if (uninitialize) CoUninitialize();
        }
    }

    private readonly struct MediaFoundationLease : IDisposable
    {
        public static MediaFoundationLease Start()
        {
            Check(MfStartup(MfVersion, MfStartupFull), "MFStartup");
            return new();
        }

        public void Dispose() => Check(MfShutdown(), "MFShutdown");
    }

    private sealed record AudioDirectionReadResult(
        WindowsMediaDirectoryRead EndpointDirectory,
        bool DriverDirectorySucceeded,
        IReadOnlyList<DeviceTopologyAudioDeviceInformation> AudioDevices);

    private sealed record ConnectedAudioDeviceRead(
        bool Succeeded,
        DeviceTopologyAudioDeviceInformation? Device);

    private sealed record DirectShowMonikerProperties(
        Guid? ClassId,
        string? FriendlyName,
        string? DevicePath);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMftRegisterTypeInfo
    {
        public Guid MajorType;
        public Guid SubType;
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirectShowDeviceEnumerator
    {
        [PreserveSig] int CreateClassEnumerator(ref Guid deviceClass, out IDirectShowEnumMoniker? monikers, uint flags);
    }

    [ComImport, Guid("00000102-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirectShowEnumMoniker
    {
        [PreserveSig] int Next(uint count, out IDirectShowMoniker? moniker, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IDirectShowEnumMoniker? enumerator);
    }

    [ComImport, Guid("0000000F-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirectShowMoniker
    {
        [PreserveSig] int GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load(IntPtr stream);
        [PreserveSig] int Save(IntPtr stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);
        [PreserveSig] int GetSizeMax(out ulong size);
        [PreserveSig] int BindToObject(IntPtr bindContext, IntPtr monikerToLeft, ref Guid interfaceId,
            [MarshalAs(UnmanagedType.IUnknown)] out object boundObject);
        [PreserveSig] int BindToStorage(IntPtr bindContext, IntPtr monikerToLeft, ref Guid interfaceId,
            [MarshalAs(UnmanagedType.IUnknown)] out object storage);
        [PreserveSig] int Reduce(IntPtr bindContext, uint reduceHowFar, IntPtr monikerToLeft,
            out IDirectShowMoniker? reducedMoniker);
        [PreserveSig] int ComposeWith(IDirectShowMoniker rightMoniker,
            [MarshalAs(UnmanagedType.Bool)] bool onlyIfNotGeneric, out IDirectShowMoniker? compositeMoniker);
        [PreserveSig] int Enum([MarshalAs(UnmanagedType.Bool)] bool onlyIfNotGeneric,
            out IDirectShowEnumMoniker? enumerator);
        [PreserveSig] int IsEqual(IDirectShowMoniker otherMoniker);
        [PreserveSig] int Hash(out uint hash);
        [PreserveSig] int IsRunning(IntPtr bindContext, IntPtr monikerToLeft, IntPtr newlyRunning);
        [PreserveSig] int GetTimeOfLastChange(IntPtr bindContext, IntPtr monikerToLeft,
            out System.Runtime.InteropServices.ComTypes.FILETIME fileTime);
        [PreserveSig] int Inverse(out IDirectShowMoniker? inverseMoniker);
        [PreserveSig] int CommonPrefixWith(IDirectShowMoniker otherMoniker,
            out IDirectShowMoniker? commonPrefixMoniker);
        [PreserveSig] int RelativePathTo(IDirectShowMoniker otherMoniker,
            out IDirectShowMoniker? relativePathMoniker);
        [PreserveSig] int GetDisplayName(IntPtr bindContext, IntPtr monikerToLeft, out IntPtr displayName);
        [PreserveSig] int ParseDisplayName(IntPtr bindContext, IntPtr monikerToLeft,
            [MarshalAs(UnmanagedType.LPWStr)] string displayName, out uint eaten, out IDirectShowMoniker? parsedMoniker);
        [PreserveSig] int IsSystemMoniker(out uint systemMonikerType);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirectShowPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [MarshalAs(UnmanagedType.Struct)] out object? value, IntPtr errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(WindowsAudioDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(WindowsAudioDataFlow dataFlow, WindowsAudioDefaultRole role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string endpointId, out IMMDevice endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        [PreserveSig] int OpenPropertyStore(uint storageMode, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr endpointId);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
            IntPtr format, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int connectorType);
        [PreserveSig] int GetDataFlow(out int dataFlow);
        [PreserveSig] int ConnectTo(IConnector connector);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector connector);
        [PreserveSig] int GetConnectorIdConnectedTo(out IntPtr connectorId);
        [PreserveSig] int GetDeviceIdConnectedTo(out IntPtr deviceId);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint classContext,
        ref Guid interfaceId, out IMMDeviceEnumerator instance);
    [DllImport("ole32.dll", EntryPoint = "CoCreateInstance", ExactSpelling = true)]
    private static extern int CoCreateDirectShowEnumerator(ref Guid classId, IntPtr outer, uint classContext,
        ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IDirectShowDeviceEnumerator? instance);

    [DllImport("mfplat.dll", EntryPoint = "MFStartup", ExactSpelling = true)]
    private static extern int MfStartup(uint version, uint flags);
    [DllImport("mfplat.dll", EntryPoint = "MFShutdown", ExactSpelling = true)]
    private static extern int MfShutdown();
    [DllImport("mfplat.dll", EntryPoint = "MFTEnum", ExactSpelling = true)]
    private static extern int MftEnum(Guid category, uint flags, IntPtr inputType, IntPtr outputType,
        IntPtr attributes, out IntPtr classIds, out uint count);
    [DllImport("mfplat.dll", EntryPoint = "MFTGetInfo", ExactSpelling = true)]
    private static extern int MftGetInfo(Guid classId, IntPtr name, out IntPtr inputTypes,
        out uint inputTypeCount, out IntPtr outputTypes, out uint outputTypeCount, IntPtr attributes);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CmLocateDevNodeW(out uint devInst,
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInstanceId, uint flags);
}
