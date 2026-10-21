using System.Runtime.InteropServices;
using ResourceManager.Ledger.Identity;

namespace ResourceManager.Ledger.Interop;

internal static unsafe class NativeDescriptorValidator
{
    private const uint AbiVersion = 1;
    private const uint MaxSubscribersPerTable = 64;

    internal static NativeDescriptor QueryAndValidate<TUid>(NativeApi api, LedgerBuildIdentity expected)
        where TUid : unmanaged, ILedgerUid<TUid>
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(expected);

        NativeDescriptor descriptor = default;
        LedgerStatus status = (LedgerStatus)api.QueryDescriptor(&descriptor);
        if (status != LedgerStatus.Ok) throw new LedgerException(status, "query descriptor");

        if (IntPtr.Size != 8 || descriptor.DescriptorSize != sizeof(NativeDescriptor) ||
            descriptor.AbiVersion != AbiVersion || descriptor.UidWidthBits is not (128 or 256) ||
            descriptor.UidWidthBits != (uint)(TUid.ByteLength * 8) ||
            descriptor.CandidateId != (uint)expected.Candidate || descriptor.TableCount != expected.TableCount ||
            descriptor.FixedCount != expected.FixedCount || descriptor.ResourceCapacity != expected.ResourceCapacity ||
            descriptor.PartitionCapacity != expected.PartitionCapacity || descriptor.IndexNodeCount != expected.IndexNodeCount ||
            descriptor.IndexHomeCount != expected.IndexHomeCount || descriptor.MaxSubscribersPerTable != MaxSubscribersPerTable ||
            descriptor.Reserved != 0 || descriptor.NativePageSize == 0 || descriptor.InstanceSize == 0)
        {
            throw new InvalidOperationException("The Native descriptor does not match this ABI, UID type, or generated owner identity.");
        }

        if (!IsValidBufferLayout(descriptor.InstanceSize, descriptor.InstanceAlignment) ||
            !IsValidBufferLayout(descriptor.OperationWorkspaceSize, descriptor.OperationWorkspaceAlignment) ||
            !IsValidBufferLayout(descriptor.TickWorkspaceSize, descriptor.TickWorkspaceAlignment) ||
            !IsValidBufferLayout(descriptor.DestroyWorkspaceSize, descriptor.DestroyWorkspaceAlignment))
        {
            throw new InvalidOperationException("The Native descriptor contains an invalid state or workspace size/alignment pair.");
        }

        byte* actualStructure = descriptor.StructureHash;
        byte* actualPolicy = descriptor.PolicyHash;
        if (!new ReadOnlySpan<byte>(actualStructure, 32).SequenceEqual(expected.StructureHash) ||
            !new ReadOnlySpan<byte>(actualPolicy, 32).SequenceEqual(expected.PolicyHash))
        {
            throw new InvalidOperationException("The Native structure or policy fingerprint does not match the generated managed identity.");
        }

        ValidateDescriptorFieldOffsets();
        if (api.GetDevelopmentStatistics != null)
        {
            string[] fields = [nameof(NativeDevelopmentStatistics.NewAllocations), nameof(NativeDevelopmentStatistics.SameEpochReleases),
                nameof(NativeDevelopmentStatistics.SameEpochAllocationBytes), nameof(NativeDevelopmentStatistics.SubpageAllocations),
                nameof(NativeDevelopmentStatistics.SubpageAllocationBytes)];
            if (sizeof(NativeDevelopmentStatistics) != 40)
                throw new InvalidOperationException("Development statistics must contain exactly five 64-bit counters.");
            for (int i = 0; i < fields.Length; i++)
                if (Marshal.OffsetOf<NativeDevelopmentStatistics>(fields[i]).ToInt64() != i * 8)
                    throw new InvalidOperationException("Development statistics fields do not match the Native layout.");
        }
        ValidateLayout(&descriptor.LeaseLayout, typeof(NativeLease), 8, nameof(NativeLease.Slot), nameof(NativeLease.Reserved),
            nameof(NativeLease.AllocationNumber), nameof(NativeLease.UseGeneration));
        ValidateLayout(&descriptor.BorrowLayout, typeof(NativeBorrow), 8, nameof(NativeBorrow.Lease), nameof(NativeBorrow.Data), nameof(NativeBorrow.Length));
        ValidateLayout(&descriptor.AttributesLayout, typeof(NativeAttributes), 4, nameof(NativeAttributes.Recoverability),
            nameof(NativeAttributes.RecoveryCostCoefficient), nameof(NativeAttributes.AccessLossImpact));
        ValidateLayout(&descriptor.UpdateLayout, typeof(NativeUpdate), 8, nameof(NativeUpdate.PresentMask), nameof(NativeUpdate.Recoverability),
            nameof(NativeUpdate.RecoveryCostCoefficient), nameof(NativeUpdate.AccessLossImpact), nameof(NativeUpdate.AllocationBytes));
        ValidateLayout(&descriptor.TargetLayout, typeof(NativeTarget), 8, nameof(NativeTarget.Kind), nameof(NativeTarget.Table),
            nameof(NativeTarget.ParentUid), nameof(NativeTarget.ParentUidLength));
        ValidateLayout(&descriptor.PartitionResultLayout, typeof(NativePartitionResult), 8, nameof(NativePartitionResult.AllocationNumber));
        ValidateLayout(&descriptor.SubscriptionLayout, typeof(NativeSubscriptionHandle), 8, nameof(NativeSubscriptionHandle.Table),
            nameof(NativeSubscriptionHandle.Slot), nameof(NativeSubscriptionHandle.Generation));
        ValidateLayout(&descriptor.TableViewLayout, typeof(NativeTableView), 8, nameof(NativeTableView.RegisteredBytes), nameof(NativeTableView.SystemPageBytes));
        ValidateLayout(&descriptor.TickResultLayout, typeof(NativeTickResult), 8, nameof(NativeTickResult.Released), nameof(NativeTickResult.Scanned));
        return descriptor;
    }

    private static bool IsValidBufferLayout(ulong size, ulong alignment) =>
        size <= (ulong)nuint.MaxValue && alignment != 0 && alignment <= (ulong)nuint.MaxValue &&
        (alignment & (alignment - 1)) == 0 && alignment >= (ulong)IntPtr.Size;

    private static void ValidateLayout(NativeTypeLayout* actual, Type managedType, uint expectedAlignment, params string[] fields)
    {
        int managedSize = Marshal.SizeOf(managedType);
        if (actual->Reserved != 0 || actual->Size != (uint)managedSize || actual->Alignment != expectedAlignment ||
            actual->FieldCount != (uint)fields.Length || fields.Length > 8)
        {
            throw new InvalidOperationException($"Native layout for {managedType.Name} does not match the managed mirror.");
        }

        for (int i = 0; i < fields.Length; i++)
        {
            uint managedOffset = checked((uint)Marshal.OffsetOf(managedType, fields[i]).ToInt64());
            if (actual->GetFieldOffset(i) != managedOffset)
                throw new InvalidOperationException($"Native field offset {i} for {managedType.Name} does not match the managed mirror.");
        }
    }

    private static void ValidateDescriptorFieldOffsets()
    {
        string[] fields =
        [
            nameof(NativeDescriptor.DescriptorSize), nameof(NativeDescriptor.AbiVersion), nameof(NativeDescriptor.UidWidthBits),
            nameof(NativeDescriptor.CandidateId), nameof(NativeDescriptor.TableCount), nameof(NativeDescriptor.FixedCount),
            nameof(NativeDescriptor.MaxSubscribersPerTable), nameof(NativeDescriptor.ResourceCapacity), nameof(NativeDescriptor.PartitionCapacity),
            nameof(NativeDescriptor.IndexNodeCount), nameof(NativeDescriptor.IndexHomeCount), nameof(NativeDescriptor.Reserved), nameof(NativeDescriptor.NativePageSize),
            nameof(NativeDescriptor.InstanceSize), nameof(NativeDescriptor.InstanceAlignment), nameof(NativeDescriptor.OperationWorkspaceSize),
            nameof(NativeDescriptor.OperationWorkspaceAlignment), nameof(NativeDescriptor.TickWorkspaceSize), nameof(NativeDescriptor.TickWorkspaceAlignment),
            nameof(NativeDescriptor.DestroyWorkspaceSize), nameof(NativeDescriptor.DestroyWorkspaceAlignment), nameof(NativeDescriptor.StructureHash),
            nameof(NativeDescriptor.PolicyHash), nameof(NativeDescriptor.LeaseLayout), nameof(NativeDescriptor.BorrowLayout),
            nameof(NativeDescriptor.AttributesLayout), nameof(NativeDescriptor.UpdateLayout), nameof(NativeDescriptor.TargetLayout),
            nameof(NativeDescriptor.PartitionResultLayout), nameof(NativeDescriptor.SubscriptionLayout), nameof(NativeDescriptor.TableViewLayout),
            nameof(NativeDescriptor.TickResultLayout),
        ];
        uint[] expectedOffsets = [0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120, 152, 184, 232, 280, 328, 376, 424, 472, 520, 568];

        if (Marshal.SizeOf<NativeDescriptor>() != 616 || fields.Length != expectedOffsets.Length)
            throw new InvalidOperationException("The managed descriptor mirror is not the current 616-byte ABI v1 draft layout.");
        for (int i = 0; i < fields.Length; i++)
        {
            uint actual = checked((uint)Marshal.OffsetOf<NativeDescriptor>(fields[i]).ToInt64());
            if (actual != expectedOffsets[i]) throw new InvalidOperationException($"Managed descriptor field '{fields[i]}' has an unexpected offset.");
        }
    }
}
