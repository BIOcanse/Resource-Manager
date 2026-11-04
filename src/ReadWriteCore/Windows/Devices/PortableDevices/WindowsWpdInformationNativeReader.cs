using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.PortableDevices;

[SupportedOSPlatform("windows")]
internal sealed class WindowsWpdInformationNativeReader : IWindowsWpdInformationReader
{
    private const uint ClsctxInprocServer = 1;
    private const uint CoInitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int HResultErrorNotFound = unchecked((int)0x80070490);
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    internal const ushort VariantTypeLpwstr = 31;
    internal const int PropVariantStringPointerOffset = 8;
    private static readonly Guid PortableDeviceManagerClass = new("0af10cec-2ecd-4b92-9581-34f6ae0637f3");
    private static readonly Guid PortableDeviceClass = new("728a21c5-3d9e-48d7-9810-864848f0f404");
    private static readonly Guid PortableDeviceValuesClass = new("0c15d503-d017-47ce-9016-7b3f978721cc");
    private static readonly Guid PortableDeviceKeyCollectionClass = new("de2d022d-2480-43be-97f0-d1fa2cf98f4f");
    private static readonly Guid StorageFunctionalCategory = new("23f05bbc-15de-4c2a-a55b-a9af5ce412ef");

    private static readonly WpdPropertyKey ClientName = new(new("204d9f0c-2292-4080-9f42-40664e70f859"), 2);
    private static readonly WpdPropertyKey ClientDesiredAccess = new(new("204d9f0c-2292-4080-9f42-40664e70f859"), 9);
    private static readonly WpdPropertyKey ClientShareMode = new(new("204d9f0c-2292-4080-9f42-40664e70f859"), 10);
    private static readonly WpdPropertyKey DeviceFirmwareVersion = new(new("26d4979a-e643-4626-9e2b-736dc0c92fdc"), 3);
    private static readonly WpdPropertyKey DevicePowerLevel = new(new("26d4979a-e643-4626-9e2b-736dc0c92fdc"), 4);
    private static readonly WpdPropertyKey ObjectPersistentUniqueId = new(new("ef6b490d-5cd8-437a-affc-da8b60ee4a3c"), 5);
    private static readonly WpdPropertyKey StorageCapacity = new(new("01a3057a-74d6-4e80-bea7-dc4c212ce50a"), 4);

    public WindowsWpdInformationReadResult Read(WindowsWpdInformationScope scope,
        WindowsWpdInformationCaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(scope, request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ReadCore(scope, request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsNativeReadFailure(exception))
        {
            return new(false, [], [], FailureCode(exception));
        }
    }

    private static WindowsWpdInformationReadResult ReadCore(WindowsWpdInformationScope scope,
        WindowsWpdInformationCaptureRequest request, CancellationToken cancellationToken)
    {
        using var apartment = ComApartmentLease.Enter();
        IPortableDeviceManager? manager = null;
        try
        {
            var classId = PortableDeviceManagerClass;
            var interfaceId = typeof(IPortableDeviceManager).GUID;
            Check(CoCreateDeviceManager(ref classId, IntPtr.Zero, ClsctxInprocServer, ref interfaceId, out manager),
                "CoCreateInstance(CLSID_PortableDeviceManager)");
            if (manager is null) throw new InvalidDataException("Portable-device manager activation returned no interface.");

            IReadOnlyList<string> connectionIds;
            if (request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices)
            {
                Check(manager.RefreshDeviceList(), "IPortableDeviceManager.RefreshDeviceList");
                connectionIds = ReadConnectedDeviceIds(manager, scope, cancellationToken);
            }
            else
            {
                connectionIds = request.Targets.Select(static target => target.DeviceConnectionId)
                    .Distinct(StringComparer.Ordinal).ToArray();
            }

            var devices = new List<DeviceTopologyWpdDeviceInformation>(connectionIds.Count);
            var failures = new List<WindowsWpdInformationReadFailure>();
            var storageObjectCount = 0;
            foreach (var connectionId in connectionIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deviceFields = SelectedForDevice(request, connectionId);
                var storageTargets = SelectedStorageTargets(request, connectionId);
                var read = ReadDevice(scope, request, connectionId, deviceFields, storageTargets,
                    ref storageObjectCount, cancellationToken);
                devices.Add(read.Device);
                failures.AddRange(read.Failures);
            }
            return new(true, devices, failures);
        }
        finally { Release(ref manager); }
    }

    private static IReadOnlyList<string> ReadConnectedDeviceIds(IPortableDeviceManager manager,
        WindowsWpdInformationScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint count = 0;
        Check(manager.GetDevices(IntPtr.Zero, ref count), "IPortableDeviceManager.GetDevices(size)");
        if (count > scope.MaximumDevices)
            throw new InvalidDataException("Connected WPD device count exceeds the caller's explicit bound.");
        if (count == 0) return [];

        var capacity = count;
        var arrayBytes = checked((int)((ulong)capacity * (uint)IntPtr.Size));
        var nativeIds = Marshal.AllocHGlobal(arrayBytes);
        try
        {
            for (var index = 0; index < capacity; index++)
                Marshal.WriteIntPtr(nativeIds, checked((int)index * IntPtr.Size), IntPtr.Zero);
            cancellationToken.ThrowIfCancellationRequested();
            var actual = capacity;
            var result = manager.GetDevices(nativeIds, ref actual);
            Check(result, "IPortableDeviceManager.GetDevices(values)");
            if (result != 0 || actual > capacity)
                throw new InvalidDataException("The connected WPD directory changed while its bounded IDs were read.");

            var ids = new List<string>(checked((int)actual));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < actual; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pointer = Marshal.ReadIntPtr(nativeIds, checked((int)index * IntPtr.Size));
                if (pointer == IntPtr.Zero) throw new InvalidDataException("WPD manager returned a null connection ID.");
                var id = ReadBoundedNativeString(pointer, scope.MaximumStringCharacters);
                ValidateSourceString(id, scope.MaximumStringCharacters, "connection ID");
                if (!seen.Add(id)) throw new InvalidDataException("WPD manager returned a duplicate connection ID.");
                ids.Add(id);
            }
            return ids;
        }
        finally
        {
            for (var index = 0; index < capacity; index++)
            {
                var pointer = Marshal.ReadIntPtr(nativeIds, checked((int)index * IntPtr.Size));
                if (pointer != IntPtr.Zero) CoTaskMemFree(pointer);
            }
            Marshal.FreeHGlobal(nativeIds);
        }
    }

    private static DeviceRead ReadDevice(WindowsWpdInformationScope scope,
        WindowsWpdInformationCaptureRequest request, string connectionId,
        WindowsWpdInformationFields deviceFields, IReadOnlyList<WindowsWpdInformationTarget> storageTargets,
        ref int totalStorageObjects, CancellationToken cancellationToken)
    {
        var failures = new List<WindowsWpdInformationReadFailure>();
        var storages = new List<DeviceTopologyWpdStorageInformation>();
        string? persistentId = null;
        string? firmware = null;
        uint? battery = null;
        var storageRequested = request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices
            ? (request.Fields & WindowsWpdInformationFields.StorageCapacity) != 0
            : storageTargets.Count > 0;
        bool? storageDirectorySucceeded = storageRequested ? false : null;
        IPortableDevice? device = null;
        var opened = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classId = PortableDeviceClass;
            var interfaceId = typeof(IPortableDevice).GUID;
            Check(CoCreatePortableDevice(ref classId, IntPtr.Zero, ClsctxInprocServer, ref interfaceId, out device),
                "CoCreateInstance(CLSID_PortableDevice)");
            if (device is null) throw new InvalidDataException("Portable-device activation returned no interface.");
            IPortableDeviceValues? clientInfo = null;
            try
            {
                var valuesClassId = PortableDeviceValuesClass;
                var valuesInterfaceId = typeof(IPortableDeviceValues).GUID;
                Check(CoCreateValues(ref valuesClassId, IntPtr.Zero, ClsctxInprocServer,
                    ref valuesInterfaceId, out clientInfo), "CoCreateInstance(CLSID_PortableDeviceValues)");
                if (clientInfo is null) throw new InvalidDataException("WPD client options activation returned no interface.");
                var clientName = ClientName;
                Check(clientInfo.SetStringValue(ref clientName, "Resource Manager read-only information"),
                    "IPortableDeviceValues.SetStringValue(WPD_CLIENT_NAME)");
                var desiredAccess = ClientDesiredAccess;
                Check(clientInfo.SetUnsignedIntegerValue(ref desiredAccess, GenericRead),
                    "IPortableDeviceValues.SetUnsignedIntegerValue(WPD_CLIENT_DESIRED_ACCESS)");
                var shareMode = ClientShareMode;
                Check(clientInfo.SetUnsignedIntegerValue(ref shareMode, FileShareRead | FileShareWrite),
                    "IPortableDeviceValues.SetUnsignedIntegerValue(WPD_CLIENT_SHARE_MODE)");
                cancellationToken.ThrowIfCancellationRequested();
                Check(device.Open(connectionId, clientInfo), "IPortableDevice.Open(read-only)");
                opened = true;
            }
            finally { Release(ref clientInfo); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Release(ref device);
            throw;
        }
        catch (Exception exception) when (IsNativeReadFailure(exception))
        {
            Release(ref device);
            var failureCode = FailureCode(exception);
            if (deviceFields != WindowsWpdInformationFields.None)
                failures.Add(new(connectionId, WindowsWpdInformationIdentity.DeviceObjectId, deviceFields, failureCode));
            if (storageRequested)
                failures.Add(new(connectionId, null, WindowsWpdInformationFields.StorageCapacity, failureCode));
            return new(new(connectionId, WindowsWpdInformationIdentity.DeviceObjectId, null, null, null,
                storageRequested ? false : null, storages), failures);
        }

        try
        {
            if (!opened || device is null)
                throw new InvalidDataException("The selected WPD connection was not opened.");
            if (deviceFields != WindowsWpdInformationFields.None)
            {
                try
                {
                    ReadDeviceProperties(device, scope, deviceFields, connectionId,
                        out persistentId, out firmware, out battery, failures, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsNativeReadFailure(exception))
                {
                    failures.Add(new(connectionId, WindowsWpdInformationIdentity.DeviceObjectId,
                        deviceFields, FailureCode(exception)));
                }
            }

            if (storageRequested)
            {
                try
                {
                    ReadStorageProperties(device, scope, request, connectionId, storageTargets,
                        ref totalStorageObjects, storages, failures, cancellationToken);
                    storageDirectorySucceeded = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsNativeReadFailure(exception))
                {
                    storages.Clear();
                    storageDirectorySucceeded = false;
                    failures.Add(new(connectionId, null, WindowsWpdInformationFields.StorageCapacity,
                        FailureCode(exception)));
                }
            }

            return new(new(connectionId, WindowsWpdInformationIdentity.DeviceObjectId,
                persistentId, firmware, battery, storageDirectorySucceeded, storages), failures);
        }
        finally
        {
            if (opened && device is not null) _ = device.Close();
            Release(ref device);
        }
    }

    private static void ReadDeviceProperties(IPortableDevice device, WindowsWpdInformationScope scope,
        WindowsWpdInformationFields selected, string connectionId,
        out string? persistentId, out string? firmware, out uint? battery,
        List<WindowsWpdInformationReadFailure> failures, CancellationToken cancellationToken)
    {
        IPortableDeviceContent? content = null;
        IPortableDeviceProperties? properties = null;
        IPortableDeviceKeyCollection? keys = null;
        IPortableDeviceValues? values = null;
        persistentId = null;
        firmware = null;
        battery = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(device.Content(out content), "IPortableDevice.Content");
            if (content is null) throw new InvalidDataException("WPD Content returned no interface.");
            Check(content.Properties(out properties), "IPortableDeviceContent.Properties");
            if (properties is null) throw new InvalidDataException("WPD Properties returned no interface.");
            keys = CreatePropertyKeys(selected);
            cancellationToken.ThrowIfCancellationRequested();
            var objectId = WindowsWpdInformationIdentity.DeviceObjectId;
            Check(properties.GetValues(objectId, keys, out values), "IPortableDeviceProperties.GetValues(DEVICE)");
            if (values is null) throw new InvalidDataException("WPD GetValues returned no property collection.");

            if ((selected & WindowsWpdInformationFields.DeviceId) != 0)
            {
                var read = ReadWpdString(values, ObjectPersistentUniqueId, scope.MaximumStringCharacters);
                persistentId = read.Value;
                AddPropertyFailure(failures, connectionId, objectId, WindowsWpdInformationFields.DeviceId, read.FailureCode);
            }
            if ((selected & WindowsWpdInformationFields.FirmwareVersion) != 0)
            {
                var read = ReadWpdString(values, DeviceFirmwareVersion, scope.MaximumStringCharacters);
                firmware = read.Value;
                AddPropertyFailure(failures, connectionId, objectId, WindowsWpdInformationFields.FirmwareVersion, read.FailureCode);
            }
            if ((selected & WindowsWpdInformationFields.BatteryPercent) != 0)
            {
                var read = ReadWpdUInt32(values, DevicePowerLevel);
                battery = read.Value;
                AddPropertyFailure(failures, connectionId, objectId, WindowsWpdInformationFields.BatteryPercent, read.FailureCode);
            }
        }
        finally
        {
            Release(ref values);
            Release(ref keys);
            Release(ref properties);
            Release(ref content);
        }
    }

    private static void ReadStorageProperties(IPortableDevice device, WindowsWpdInformationScope scope,
        WindowsWpdInformationCaptureRequest request, string connectionId,
        IReadOnlyList<WindowsWpdInformationTarget> selectedTargets, ref int totalStorageObjects,
        List<DeviceTopologyWpdStorageInformation> storages,
        List<WindowsWpdInformationReadFailure> failures, CancellationToken cancellationToken)
    {
        IPortableDeviceCapabilities? capabilities = null;
        IPortableDevicePropVariantCollection? objectIds = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(device.Capabilities(out capabilities), "IPortableDevice.Capabilities");
            if (capabilities is null) throw new InvalidDataException("WPD Capabilities returned no interface.");
            var category = StorageFunctionalCategory;
            Check(capabilities.GetFunctionalObjects(ref category, out objectIds),
                "IPortableDeviceCapabilities.GetFunctionalObjects(WPD_FUNCTIONAL_CATEGORY_STORAGE)");
            if (objectIds is null) throw new InvalidDataException("WPD storage enumeration returned no object list.");
            Check(objectIds.GetCount(out var count), "IPortableDevicePropVariantCollection.GetCount(storage)");
            if (count > scope.MaximumStorageObjectsPerDevice
                || count > scope.MaximumStorageObjects - totalStorageObjects)
                throw new InvalidDataException("WPD storage object count exceeds the caller's explicit bound.");
            totalStorageObjects = checked(totalStorageObjects + (int)count);

            var ids = new List<string>(checked((int)count));
            var identities = new HashSet<string>(StringComparer.Ordinal);
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var objectId = ReadStorageObjectId(
                    variant => objectIds.GetAt(index, variant),
                    scope.MaximumStringCharacters,
                    static variant => PropVariantClear(variant));
                if (!identities.Add(objectId))
                    throw new InvalidDataException("WPD storage enumeration returned a duplicate object ID.");
                ids.Add(objectId);
            }

            var capacityTargets = request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices
                ? ids.ToHashSet(StringComparer.Ordinal)
                : selectedTargets.Select(static target => target.ObjectId).ToHashSet(StringComparer.Ordinal);
            foreach (var objectId in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!capacityTargets.Contains(objectId)) continue;
                var read = ReadStorageCapacity(device, objectId);
                if (read.FailureCode is not null)
                    failures.Add(new(connectionId, objectId, WindowsWpdInformationFields.StorageCapacity, read.FailureCode));
                storages.Add(new(connectionId, objectId, read.Value));
            }
        }
        finally
        {
            Release(ref objectIds);
            Release(ref capabilities);
        }
    }

    private static (ulong? Value, string? FailureCode) ReadStorageCapacity(IPortableDevice device, string objectId)
    {
        IPortableDeviceContent? content = null;
        IPortableDeviceProperties? properties = null;
        IPortableDeviceKeyCollection? keys = null;
        IPortableDeviceValues? values = null;
        try
        {
            Check(device.Content(out content), "IPortableDevice.Content(storage)");
            if (content is null) throw new InvalidDataException("WPD Content returned no interface.");
            Check(content.Properties(out properties), "IPortableDeviceContent.Properties(storage)");
            if (properties is null) throw new InvalidDataException("WPD Properties returned no interface.");
            keys = CreatePropertyKeys(WindowsWpdInformationFields.StorageCapacity);
            Check(properties.GetValues(objectId, keys, out values), "IPortableDeviceProperties.GetValues(storage)");
            if (values is null) throw new InvalidDataException("WPD GetValues returned no storage property collection.");
            var key = StorageCapacity;
            var hr = values.GetUnsignedLargeIntegerValue(ref key, out var rawCapacity);
            return hr < 0 ? (null, PropertyFailureCode(values, ref key, hr)) : (rawCapacity, null);
        }
        catch (Exception exception) when (IsNativeReadFailure(exception))
        {
            return (null, FailureCode(exception));
        }
        finally
        {
            Release(ref values);
            Release(ref keys);
            Release(ref properties);
            Release(ref content);
        }
    }

    private static (string? Value, string? FailureCode) ReadWpdString(IPortableDeviceValues values,
        WpdPropertyKey key, int maximumStringCharacters)
    {
        var hr = values.GetStringValue(ref key, out var pointer);
        if (hr < 0) return (null, PropertyFailureCode(values, ref key, hr));
        if (pointer == IntPtr.Zero) return (null, null);
        try
        {
            var value = ReadBoundedNativeString(pointer, maximumStringCharacters);
            return (value.Length == 0 ? null : value, null);
        }
        finally { CoTaskMemFree(pointer); }
    }

    private static (uint? Value, string? FailureCode) ReadWpdUInt32(IPortableDeviceValues values,
        WpdPropertyKey key)
    {
        var hr = values.GetUnsignedIntegerValue(ref key, out var value);
        return hr < 0
            ? (null, PropertyFailureCode(values, ref key, hr))
            : (value, null);
    }

    private static string? PropertyFailureCode(IPortableDeviceValues values, ref WpdPropertyKey key, int valueHr)
    {
        var propertyErrorResult = values.GetErrorValue(ref key, out var propertyHr);
        var hr = propertyErrorResult >= 0 ? propertyHr : valueHr;
        return hr == HResultErrorNotFound ? null : FailureCode(hr);
    }

    private static void AddPropertyFailure(List<WindowsWpdInformationReadFailure> failures,
        string connectionId, string objectId, WindowsWpdInformationFields fields, string? code)
    {
        if (code is not null) failures.Add(new(connectionId, objectId, fields, code));
    }

    private static IPortableDeviceKeyCollection CreatePropertyKeys(WindowsWpdInformationFields fields)
    {
        IPortableDeviceKeyCollection? keys = null;
        try
        {
            var classId = PortableDeviceKeyCollectionClass;
            var interfaceId = typeof(IPortableDeviceKeyCollection).GUID;
            Check(CoCreateKeyCollection(ref classId, IntPtr.Zero, ClsctxInprocServer, ref interfaceId, out keys),
                "CoCreateInstance(CLSID_PortableDeviceKeyCollection)");
            if (keys is null) throw new InvalidDataException("WPD key collection activation returned no interface.");
            if ((fields & WindowsWpdInformationFields.DeviceId) != 0)
            {
                var key = ObjectPersistentUniqueId;
                Check(keys.Add(ref key), "IPortableDeviceKeyCollection.Add(WPD_OBJECT_PERSISTENT_UNIQUE_ID)");
            }
            if ((fields & WindowsWpdInformationFields.FirmwareVersion) != 0)
            {
                var key = DeviceFirmwareVersion;
                Check(keys.Add(ref key), "IPortableDeviceKeyCollection.Add(WPD_DEVICE_FIRMWARE_VERSION)");
            }
            if ((fields & WindowsWpdInformationFields.BatteryPercent) != 0)
            {
                var key = DevicePowerLevel;
                Check(keys.Add(ref key), "IPortableDeviceKeyCollection.Add(WPD_DEVICE_POWER_LEVEL)");
            }
            if ((fields & WindowsWpdInformationFields.StorageCapacity) != 0)
            {
                var key = StorageCapacity;
                Check(keys.Add(ref key), "IPortableDeviceKeyCollection.Add(WPD_STORAGE_CAPACITY)");
            }
            return keys;
        }
        catch
        {
            Release(ref keys);
            throw;
        }
    }

    private static WindowsWpdInformationFields SelectedForDevice(WindowsWpdInformationCaptureRequest request,
        string connectionId)
    {
        if (request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices)
            return request.Fields & WindowsWpdInformationFields.Device;
        var selected = WindowsWpdInformationFields.None;
        foreach (var target in request.Targets)
            if (target.DeviceConnectionId == connectionId && target.ObjectKind == WindowsWpdInformationObjectKind.Device)
                selected |= target.Fields;
        return selected;
    }

    private static IReadOnlyList<WindowsWpdInformationTarget> SelectedStorageTargets(
        WindowsWpdInformationCaptureRequest request, string connectionId)
        => request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices
            ? []
            : request.Targets.Where(target => target.DeviceConnectionId == connectionId
                && target.ObjectKind == WindowsWpdInformationObjectKind.Storage).ToArray();

    private static void ValidateRequest(WindowsWpdInformationScope scope, WindowsWpdInformationCaptureRequest request)
    {
        if ((request.Fields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices)
        {
            if (request.Fields != scope.Fields || !request.Targets.IsEmpty)
                throw new ArgumentException("Full WPD discovery must use the exact configured field scope.", nameof(request));
            return;
        }

        var connections = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<(string ConnectionId, string ObjectId, WindowsWpdInformationObjectKind Kind)>();
        var union = WindowsWpdInformationFields.None;
        var storageTargets = 0;
        foreach (var target in request.Targets)
        {
            ValidateSourceString(target.DeviceConnectionId, scope.MaximumStringCharacters, "connection ID");
            ValidateSourceString(target.ObjectId, scope.MaximumStringCharacters, "object ID");
            connections.Add(target.DeviceConnectionId);
            if (!targets.Add((target.DeviceConnectionId, target.ObjectId, target.ObjectKind)))
                throw new ArgumentException("A selected WPD object appears more than once.", nameof(request));
            union |= target.Fields;
            if (target.ObjectKind == WindowsWpdInformationObjectKind.Storage) storageTargets++;
        }
        if (connections.Count > scope.MaximumDevices || storageTargets > scope.MaximumStorageObjects
            || union != request.Fields)
            throw new ArgumentException("The exact WPD target selection exceeds or differs from the configured scope.", nameof(request));
    }

    /// <summary>The Windows SDK PROPVARIANT has an 8-byte header/union offset and
    /// an outer DECIMAL overlay: 16 bytes on x86 and 24 bytes on x64.</summary>
    internal static int PropVariantSizeForPointerSize(int pointerSize) => pointerSize switch
    {
        4 => 16,
        8 => 24,
        _ => throw new ArgumentOutOfRangeException(nameof(pointerSize))
    };

    /// <summary>Reads one PROPVARIANT returned by GetAt. Both callbacks are injectable
    /// so recorded tests exercise the exact ABI/cleanup path without COM or native calls.</summary>
    internal static string ReadStorageObjectId(Func<IntPtr, int> getAt, int maximumCharacters,
        Func<IntPtr, int> clearPropVariant)
    {
        ArgumentNullException.ThrowIfNull(getAt);
        ArgumentNullException.ThrowIfNull(clearPropVariant);
        if (maximumCharacters is < 1 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

        var variant = Marshal.AllocCoTaskMem(PropVariantSizeForPointerSize(IntPtr.Size));
        try
        {
            var size = PropVariantSizeForPointerSize(IntPtr.Size);
            for (var index = 0; index < size; index++) Marshal.WriteByte(variant, index, 0);

            var readFailed = false;
            try
            {
                Check(getAt(variant), "IPortableDevicePropVariantCollection.GetAt(storage)");
                var variantType = unchecked((ushort)Marshal.ReadInt16(variant));
                if (variantType != VariantTypeLpwstr)
                    throw new InvalidDataException($"WPD storage object ID has unexpected PROPVARIANT type {variantType}.");
                var value = Marshal.ReadIntPtr(variant, PropVariantStringPointerOffset);
                if (value == IntPtr.Zero)
                    throw new InvalidDataException("WPD storage enumeration returned a null object ID.");
                var objectId = ReadBoundedNativeString(value, maximumCharacters);
                if (objectId.Length == 0)
                    throw new InvalidDataException("WPD storage enumeration returned an empty object ID.");
                return objectId;
            }
            catch
            {
                readFailed = true;
                throw;
            }
            finally
            {
                var clearResult = clearPropVariant(variant);
                if (!readFailed) Check(clearResult, "PropVariantClear(storage object ID)");
            }
        }
        finally { Marshal.FreeCoTaskMem(variant); }
    }

    private static string ReadBoundedNativeString(IntPtr pointer, int maximumCharacters)
    {
        var characters = new char[maximumCharacters];
        for (var index = 0; index <= maximumCharacters; index++)
        {
            var character = (char)Marshal.ReadInt16(pointer, checked(index * sizeof(char)));
            if (character == '\0') return new string(characters, 0, index);
            if (index == maximumCharacters)
                throw new InvalidDataException("A WPD string exceeds the caller's explicit character bound.");
            characters[index] = character;
        }
        throw new InvalidDataException("A WPD string was not terminated within the caller's explicit bound.");
    }

    private static void ValidateSourceString(string value, int maximumCharacters, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumCharacters)
            throw new InvalidDataException($"WPD returned an invalid {name}.");
    }

    private static bool IsNativeReadFailure(Exception exception)
        => exception is COMException or Win32Exception or InvalidDataException or OverflowException
            or InvalidCastException or InvalidOperationException or ArgumentException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException
            or TypeLoadException or NotSupportedException or MarshalDirectiveException;

    private static string FailureCode(Exception exception) => exception switch
    {
        COMException com => FailureCode(com.HResult),
        Win32Exception win32 => $"win32-0x{win32.NativeErrorCode:X8}",
        _ => exception.GetType().Name
    };

    private static string FailureCode(int hresult) => $"hresult-0x{unchecked((uint)hresult):X8}";

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

    private readonly record struct DeviceRead(DeviceTopologyWpdDeviceInformation Device,
        IReadOnlyList<WindowsWpdInformationReadFailure> Failures);

    [StructLayout(LayoutKind.Sequential)]
    private struct WpdPropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceManager
    {
        [PreserveSig] int GetDevices(IntPtr deviceIdArray, ref uint deviceCount);
        [PreserveSig] int RefreshDeviceList();
    }

    [ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDevice
    {
        [PreserveSig] int Open([MarshalAs(UnmanagedType.LPWStr)] string deviceConnectionId,
            [MarshalAs(UnmanagedType.Interface)] IPortableDeviceValues clientInfo);
        [PreserveSig] int SendCommand(uint flags, [MarshalAs(UnmanagedType.Interface)] IPortableDeviceValues parameters,
            [MarshalAs(UnmanagedType.Interface)] out IPortableDeviceValues? results);
        [PreserveSig] int Content([MarshalAs(UnmanagedType.Interface)] out IPortableDeviceContent? content);
        [PreserveSig] int Capabilities([MarshalAs(UnmanagedType.Interface)] out IPortableDeviceCapabilities? capabilities);
        [PreserveSig] int Cancel();
        [PreserveSig] int Close();
    }

    [ComImport, Guid("6a96ed84-7c73-4480-9938-bf5af477d426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceContent
    {
        [PreserveSig] int EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parentObjectId,
            [MarshalAs(UnmanagedType.Interface)] IPortableDeviceValues? filter,
            [MarshalAs(UnmanagedType.Interface)] out object? objectEnumerator);
        [PreserveSig] int Properties([MarshalAs(UnmanagedType.Interface)] out IPortableDeviceProperties? properties);
    }

    [ComImport, Guid("2c8c6dbf-e3dc-4061-becc-8542e810d126"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceCapabilities
    {
        [PreserveSig] int GetSupportedCommands([MarshalAs(UnmanagedType.Interface)] out IPortableDeviceKeyCollection? commands);
        [PreserveSig] int GetCommandOptions(ref WpdPropertyKey command,
            [MarshalAs(UnmanagedType.Interface)] out IPortableDeviceValues? options);
        [PreserveSig] int GetFunctionalCategories([MarshalAs(UnmanagedType.Interface)] out IPortableDevicePropVariantCollection? categories);
        [PreserveSig] int GetFunctionalObjects(ref Guid category,
            [MarshalAs(UnmanagedType.Interface)] out IPortableDevicePropVariantCollection? objectIds);
    }

    [ComImport, Guid("7f6d695c-03df-4439-a809-59266beee3a6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceProperties
    {
        [PreserveSig] int GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string objectId,
            [MarshalAs(UnmanagedType.Interface)] out IPortableDeviceKeyCollection? keys);
        [PreserveSig] int GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId,
            ref WpdPropertyKey key, [MarshalAs(UnmanagedType.Interface)] out IPortableDeviceValues? attributes);
        [PreserveSig] int GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId,
            [MarshalAs(UnmanagedType.Interface)] IPortableDeviceKeyCollection keys,
            [MarshalAs(UnmanagedType.Interface)] out IPortableDeviceValues? values);
    }

    [ComImport, Guid("dada2357-e0ad-492e-98db-dd61c53ba353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceKeyCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref WpdPropertyKey key);
        [PreserveSig] int Add(ref WpdPropertyKey key);
    }

    [ComImport, Guid("89b2e422-4f1b-4316-bcef-a44afea83eb3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDevicePropVariantCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, IntPtr propVariant);
    }

    [ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceValues
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref WpdPropertyKey key, IntPtr propVariant);
        [PreserveSig] int SetValue(ref WpdPropertyKey key, IntPtr propVariant);
        [PreserveSig] int GetValue(ref WpdPropertyKey key, IntPtr propVariant);
        [PreserveSig] int SetStringValue(ref WpdPropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetStringValue(ref WpdPropertyKey key, out IntPtr value);
        [PreserveSig] int SetUnsignedIntegerValue(ref WpdPropertyKey key, uint value);
        [PreserveSig] int GetUnsignedIntegerValue(ref WpdPropertyKey key, out uint value);
        [PreserveSig] int SetSignedIntegerValue(ref WpdPropertyKey key, int value);
        [PreserveSig] int GetSignedIntegerValue(ref WpdPropertyKey key, out int value);
        [PreserveSig] int SetUnsignedLargeIntegerValue(ref WpdPropertyKey key, ulong value);
        [PreserveSig] int GetUnsignedLargeIntegerValue(ref WpdPropertyKey key, out ulong value);
        [PreserveSig] int SetSignedLargeIntegerValue(ref WpdPropertyKey key, long value);
        [PreserveSig] int GetSignedLargeIntegerValue(ref WpdPropertyKey key, out long value);
        [PreserveSig] int SetFloatValue(ref WpdPropertyKey key, float value);
        [PreserveSig] int GetFloatValue(ref WpdPropertyKey key, out float value);
        [PreserveSig] int SetErrorValue(ref WpdPropertyKey key, int value);
        [PreserveSig] int GetErrorValue(ref WpdPropertyKey key, out int value);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr value);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(IntPtr value);
    [DllImport("ole32.dll", EntryPoint = "CoCreateInstance", ExactSpelling = true)]
    private static extern int CoCreateDeviceManager(ref Guid classId, IntPtr outer, uint classContext,
        ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IPortableDeviceManager? instance);
    [DllImport("ole32.dll", EntryPoint = "CoCreateInstance", ExactSpelling = true)]
    private static extern int CoCreatePortableDevice(ref Guid classId, IntPtr outer, uint classContext,
        ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IPortableDevice? instance);
    [DllImport("ole32.dll", EntryPoint = "CoCreateInstance", ExactSpelling = true)]
    private static extern int CoCreateValues(ref Guid classId, IntPtr outer, uint classContext,
        ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IPortableDeviceValues? instance);
    [DllImport("ole32.dll", EntryPoint = "CoCreateInstance", ExactSpelling = true)]
    private static extern int CoCreateKeyCollection(ref Guid classId, IntPtr outer, uint classContext,
        ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IPortableDeviceKeyCollection? instance);
}
