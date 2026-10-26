using System.Text.Json;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

internal sealed class HostManagerOperationMigrationDeferredException(Exception inner)
    : IOException("Operation migration was deferred before changing its source bank or manifest.", inner);

internal static class HostManagerOperationLegacyMigration
{
    // Delete this compatibility reader only when no supported installation can own an envelope-v1/ABI1 bank.
    // Keep the byte-exact adjacent backup until the user/maintenance owner explicitly retires its recovery evidence.
    internal static HostManagerOperationCanonicalEnvelope Convert(HostManagerOperationCanonicalEnvelope legacy)
    {
        NativeOperationCanonicalChecksum.Validate(legacy.NativeImage);
        var payloads = legacy.Payloads.Clone();
        var records = legacy.NativeImage.Records.ToArray();
        for (var index = 0; index < records.Length; index++)
        {
            ref var record = ref records[index];
            var kindEntry = payloads.Require(record.KindHandle);
            if (kindEntry.Role != HostManagerOperationPayloadRole.Kind || kindEntry.SchemaId != HostManagerOperationRequestSchemas.KindName
                || kindEntry.SchemaVersion != HostManagerOperationRequestSchemas.Version || kindEntry.OperationId != record.OperationId
                || kindEntry.SessionInstanceId != legacy.SessionInstanceId)
                throw new InvalidDataException($"Legacy operation {record.OperationId.High:x16}{record.OperationId.Low:x16} is missing an exact operation-kind binding.");
            var kind = HostManagerOperationRequestCodec.DecodeText(kindEntry.Payload);
            var request = payloads.Require(record.RequestHandle);
            if (request.Role != HostManagerOperationPayloadRole.Request || request.OperationId != record.OperationId
                || request.SessionInstanceId != legacy.SessionInstanceId || request.SchemaVersion != HostManagerOperationRequestSchemas.Version)
                throw new InvalidDataException($"Legacy operation {record.OperationId.High:x16}{record.OperationId.Low:x16} is missing an exact canonical request binding.");
            HostManagerOperationSubject subject;
            try { subject = DecodeSubject(kind, request); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or EndOfStreamException or OverflowException)
            {
                throw new InvalidDataException($"Legacy operation {record.OperationId.High:x16}{record.OperationId.Low:x16} cannot recover target/caller facts for kind {kind}, request schema {request.SchemaId:x}: {error.Message}", error);
            }
            record.SubjectHandle = payloads.Add(HostManagerOperationRequestSchemas.Subject, HostManagerOperationRequestSchemas.SubjectVersion,
                HostManagerOperationPayloadRole.Subject, legacy.SessionInstanceId, record.OperationId, default,
                HostManagerOperationRequestCodec.EncodeSubject(subject)).Handle;
            record.Flags |= (ulong)NativeOperationFlags.SubjectValid;
            if ((record.ProgressValidMask & (ulong)NativeOperationProgressValidity.Stage) != 0)
            {
                var stage = TryReadText(payloads.Require(record.StageFactHandle).Payload);
                if (stage == "download")
                    record.StageFactHandle = payloads.Add(HostManagerOperationRequestSchemas.ProgressStage, HostManagerOperationRequestSchemas.Version,
                        HostManagerOperationPayloadRole.ProgressStage, legacy.SessionInstanceId, record.OperationId, record.AttemptToken,
                        HostManagerOperationRequestCodec.EncodeStage(HostManagerOperationStage.Download)).Handle;
                else
                {
                    record.StageFactHandle = default;
                    record.ProgressValidMask &= ~(ulong)NativeOperationProgressValidity.Stage;
                }
            }
            record.DetailFactHandle = default;
            record.ProgressValidMask &= ~(ulong)NativeOperationProgressValidity.DetailFact;
            if ((record.Flags & (ulong)NativeOperationFlags.ResultValid) != 0)
            {
                var value = ConvertFeedback(payloads.Require(record.ResultHandle), payloads, isError: false);
                record.ResultHandle = value;
                if (value.IsZero) record.Flags &= ~(ulong)NativeOperationFlags.ResultValid;
            }
            if ((record.Flags & (ulong)NativeOperationFlags.ErrorValid) != 0)
                record.ErrorHandle = ConvertFeedback(payloads.Require(record.ErrorHandle), payloads, isError: true);
        }
        var receipts = new HostManagerOperationEffectReceiptTable();
        foreach (var receipt in legacy.Receipts.ExportCanonical())
        {
            var observation = receipt.ObservationHandle;
            if (!observation.IsZero)
                observation = payloads.Add(HostManagerOperationRequestSchemas.EffectObservation, HostManagerOperationRequestSchemas.Version,
                    HostManagerOperationPayloadRole.EffectObservation, receipt.SessionInstanceId, receipt.OperationId, receipt.AttemptToken,
                    [(byte)receipt.Outcome]).Handle;
            var error = receipt.ErrorHandle.IsZero ? default
                : ConvertFeedback(payloads.Require(receipt.ErrorHandle), payloads, isError: true);
            receipts.Import(receipt with { ObservationHandle = observation, ErrorHandle = error });
        }
        var roots = HostManagerOperationPayloadCatalog.CollectNativeRoots(records);
        receipts.AddPayloadRoots(roots);
        payloads.PruneTo(roots);
        return legacy with
        {
            NativeImage = NativeOperationCanonicalChecksum.Rewrite(legacy.NativeImage.Header, records, NativeOperationCoordinatorAbi.Version),
            Payloads = payloads,
            Receipts = receipts,
            PublicationRevision = checked(legacy.PublicationRevision + 1),
            PriorEnvelopeSha256 = legacy.FullEnvelopeSha256.ToArray(),
            FullEnvelopeSha256 = HostManagerOperationCanonicalEnvelopeCodec.ZeroDigest()
        };
    }

    private static HostManagerOperationSubject DecodeSubject(string kind, HostManagerOperationPayloadEntry request)
    {
        if (kind == HostManagerOperationKinds.DiskUsageScan && request.SchemaId == HostManagerOperationRequestSchemas.DiskUsageScan)
        {
            var (scan, _) = HostManagerOperationRequestCodec.DecodeDiskUsageScan(request.Payload);
            return new(HostManagerOperationRequestCodec.TargetKindFor(kind, scan.Scope), Scope: scan.Scope, Mode: scan.Mode, TargetPath: string.IsNullOrEmpty(scan.Target) ? null : scan.Target);
        }
        if (kind == HostManagerOperationKinds.SoftwareUninstall)
        {
            if (request.SchemaId == HostManagerOperationRequestSchemas.UserSoftwareUninstall)
                return new(HostManagerOperationRequestCodec.TargetKindFor(kind), TargetId: HostManagerOperationRequestCodec.DecodeUserSoftwareUninstall(request.Payload).Request.Id);
            if (request.SchemaId == HostManagerOperationRequestSchemas.SoftwareUninstall)
                return new(HostManagerOperationRequestCodec.TargetKindFor(kind), TargetId: HostManagerOperationRequestCodec.DecodeBooleanRequest(request.Payload).Id);
        }
        if ((kind == HostManagerOperationKinds.ComponentInstall && request.SchemaId == HostManagerOperationRequestSchemas.UserComponentInstall)
            || (kind == HostManagerOperationKinds.DependencyLaunchInstaller && request.SchemaId == HostManagerOperationRequestSchemas.UserDependencyInstaller))
            return new(HostManagerOperationRequestCodec.TargetKindFor(kind), TargetId: HostManagerOperationRequestCodec.DecodeUserAcquisitionRequest(request.Payload).Id);
        if ((kind == HostManagerOperationKinds.ComponentDownload && request.SchemaId == HostManagerOperationRequestSchemas.ComponentDownload)
            || (kind == HostManagerOperationKinds.ComponentInstall && request.SchemaId == HostManagerOperationRequestSchemas.ComponentInstall)
            || (kind == HostManagerOperationKinds.DependencyDownload && request.SchemaId == HostManagerOperationRequestSchemas.DependencyDownload)
            || (kind == HostManagerOperationKinds.DependencyLaunchInstaller && request.SchemaId == HostManagerOperationRequestSchemas.DependencyInstaller))
            return new(HostManagerOperationRequestCodec.TargetKindFor(kind), TargetId: HostManagerOperationRequestCodec.DecodeAcquisitionRequest(request.Payload).Id);
        throw new InvalidDataException("The operation kind and canonical request schema do not identify supported recovery facts.");
    }

    private static NativeOperationHandle128 ConvertFeedback(HostManagerOperationPayloadEntry entry,
        HostManagerOperationPayloadCatalog payloads, bool isError)
    {
        var text = TryReadText(entry.Payload);
        HostManagerOperationFeedback? feedback = null;
        const string prefix = "operation-feedback.v1:";
        if (text?.StartsWith(prefix, StringComparison.Ordinal) == true)
        {
            try { feedback = JsonSerializer.Deserialize(text[prefix.Length..], OperationFeedbackJsonContext.Default.HostManagerOperationFeedback); }
            catch (JsonException) { }
        }
        if (feedback is null && !isError) return default; // A historical sentence is not a business result code.
        var current = feedback is null
            ? HostManagerOperationFeedback.Reason(BackendMessageCodes.Operation.UnexpectedFailure)
            : feedback.Encode();
        return payloads.Add(entry.SchemaId, entry.SchemaVersion, entry.Role, entry.SessionInstanceId, entry.OperationId, entry.AttemptToken,
            HostManagerOperationRequestCodec.EncodeText(current)).Handle;
    }

    private static string? TryReadText(byte[] payload)
    {
        try { return HostManagerOperationRequestCodec.DecodeText(payload); }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or ArgumentException) { return null; }
    }
}
