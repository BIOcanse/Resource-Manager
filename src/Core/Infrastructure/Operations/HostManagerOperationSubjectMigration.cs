using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

internal static class HostManagerOperationSubjectMigration
{
    // Remove this reader when supported installations can no longer contain SubjectVersion=1.
    internal static HostManagerOperationCanonicalEnvelope Convert(
        HostManagerOperationCanonicalEnvelope source, out bool changed)
    {
        changed = false;
        var payloads = source.Payloads.Clone();
        var records = source.NativeImage.Records.ToArray();
        for (var index = 0; index < records.Length; index++)
        {
            ref var record = ref records[index];
            if ((record.Flags & (ulong)NativeOperationFlags.SubjectValid) == 0) continue;
            var entry = payloads.Require(record.SubjectHandle);
            if (entry.SchemaId != HostManagerOperationRequestSchemas.Subject
                || entry.Role != HostManagerOperationPayloadRole.Subject
                || entry.OperationId != record.OperationId || entry.SessionInstanceId != source.SessionInstanceId
                || !entry.AttemptToken.IsZero)
                throw new InvalidDataException("The operation subject binding is invalid.");
            if (entry.SchemaVersion == HostManagerOperationRequestSchemas.SubjectVersion)
            {
                _ = HostManagerOperationRequestCodec.DecodeSubject(entry.Payload);
                continue;
            }
            if (entry.SchemaVersion != 1)
                throw new InvalidDataException("The operation subject schema version is unsupported.");
            var kindEntry = payloads.Require(record.KindHandle);
            if (kindEntry.Role != HostManagerOperationPayloadRole.Kind
                || kindEntry.SchemaId != HostManagerOperationRequestSchemas.KindName
                || kindEntry.SchemaVersion != HostManagerOperationRequestSchemas.Version
                || kindEntry.OperationId != record.OperationId || kindEntry.SessionInstanceId != source.SessionInstanceId)
                throw new InvalidDataException("The operation kind binding is invalid.");
            var kind = HostManagerOperationRequestCodec.DecodeText(kindEntry.Payload);
            var subject = HostManagerOperationRequestCodec.DecodeLegacySubject(entry.Payload, kind);
            record.SubjectHandle = payloads.Add(HostManagerOperationRequestSchemas.Subject,
                HostManagerOperationRequestSchemas.SubjectVersion, HostManagerOperationPayloadRole.Subject,
                source.SessionInstanceId, record.OperationId, default,
                HostManagerOperationRequestCodec.EncodeSubject(subject)).Handle;
            changed = true;
        }
        if (!changed) return source;
        var roots = HostManagerOperationPayloadCatalog.CollectNativeRoots(records);
        source.Receipts.AddPayloadRoots(roots);
        payloads.PruneTo(roots);
        return source with
        {
            NativeImage = NativeOperationCanonicalChecksum.Rewrite(source.NativeImage.Header, records, NativeOperationCoordinatorAbi.Version),
            Payloads = payloads
        };
    }
}
