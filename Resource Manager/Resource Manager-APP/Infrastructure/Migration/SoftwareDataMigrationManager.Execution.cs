using System.Diagnostics;
using ResourceManager.App.Domain.Migration;

namespace ResourceManager.App.Infrastructure.Migration;

public sealed partial class SoftwareDataMigrationManager
{
    public async Task<SoftwareDataMigrationResult> ExecuteAsync(
        SoftwareDataMigrationRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmExecution)
        {
            throw new InvalidOperationException("执行迁移前需要显式确认。");
        }

        var plan = Preview(request);
        if (!plan.CanExecute)
        {
            throw new InvalidOperationException("迁移计划包含不可执行项目。");
        }

        Directory.CreateDirectory(plan.TargetRoot);

        var results = new List<SoftwareDataMigrationActionResult>();
        var records = new List<SoftwareDataMigrationRecord>();
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExecuteItemAsync(item, cancellationToken);
            results.Add(result);
            if (result.State == "Migrated")
            {
                records.Add(new SoftwareDataMigrationRecord(
                    item.Id,
                    plan.SoftwareName,
                    plan.TargetCategory,
                    plan.MigrationKind,
                    item.SourcePath,
                    item.DestinationPath,
                    item.BackupPath,
                    "Migrated",
                    DateTimeOffset.Now,
                    null));
            }
        }

        if (records.Count > 0)
        {
            await AppendRecordsAsync(records, cancellationToken);
        }

        return new SoftwareDataMigrationResult(plan, results);
    }

    public async Task<SoftwareDataRestoreResult> RestoreAsync(
        SoftwareDataRestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmExecution)
        {
            throw new InvalidOperationException("恢复前需要显式确认。");
        }

        await recordGate.WaitAsync(cancellationToken);
        try
        {
            var records = (await LoadRecordsCoreAsync(cancellationToken)).ToList();
            var index = records.FindIndex(record => record.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException("找不到迁移记录。");
            }

            var record = records[index];
            if (record.State == "Restored")
            {
                return new SoftwareDataRestoreResult(record, "Restored", "该迁移记录已经恢复。");
            }

            RestoreRecord(record);

            var restored = record with
            {
                State = "Restored",
                RestoredAt = DateTimeOffset.Now
            };
            records[index] = restored;
            await SaveRecordsCoreAsync(records, cancellationToken);

            return new SoftwareDataRestoreResult(
                restored,
                "Restored",
                "已移除 junction，并把当前托管数据复制回原路径。托管目标和备份保留以便人工核对。");
        }
        finally
        {
            recordGate.Release();
        }
    }

    private async Task<SoftwareDataMigrationActionResult> ExecuteItemAsync(
        SoftwareDataMigrationItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
            CopyDirectory(item.SourcePath, item.DestinationPath, overwrite: false);
            Directory.Move(item.SourcePath, item.BackupPath);

            try
            {
                await CreateDirectoryJunctionAsync(item.SourcePath, item.DestinationPath, cancellationToken);
            }
            catch
            {
                if (!Directory.Exists(item.SourcePath) && Directory.Exists(item.BackupPath))
                {
                    Directory.Move(item.BackupPath, item.SourcePath);
                }

                throw;
            }

            return new SoftwareDataMigrationActionResult(
                item.SourcePath,
                item.DestinationPath,
                item.BackupPath,
                "Migrated",
                "已复制、备份原目录并创建 junction。");
        }
        catch (Exception ex)
        {
            return new SoftwareDataMigrationActionResult(
                item.SourcePath,
                item.DestinationPath,
                item.BackupPath,
                "Failed",
                ex.Message);
        }
    }

    private void RestoreRecord(SoftwareDataMigrationRecord record)
    {
        if (!Directory.Exists(record.DestinationPath))
        {
            throw new InvalidOperationException("托管目标目录不存在，不能恢复。");
        }

        if (Directory.Exists(record.SourcePath))
        {
            var attributes = File.GetAttributes(record.SourcePath);
            if (!attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("原路径不是 Resource Manager 创建的 junction，已停止恢复以避免覆盖真实目录。");
            }

            Directory.Delete(record.SourcePath);
        }

        Directory.CreateDirectory(record.SourcePath);
        CopyDirectory(record.DestinationPath, record.SourcePath, overwrite: true);
    }

    private async Task CreateDirectoryJunctionAsync(
        string linkPath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var arguments = $"/c mklink /J {QuoteForCmd(linkPath)} {QuoteForCmd(targetPath)}";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("无法启动 mklink。");

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory, bool overwrite)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, destination, overwrite);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            CopyDirectory(directory, destination, overwrite);
        }
    }

    private static string QuoteForCmd(string value)
    {
        if (value.Contains('"'))
        {
            throw new InvalidOperationException("路径不能包含双引号。");
        }

        return $"\"{value}\"";
    }
}
