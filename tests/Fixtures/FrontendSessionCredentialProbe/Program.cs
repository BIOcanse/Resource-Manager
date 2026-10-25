using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Shared.Runtime;
using ResourceManager.Shared.Runtime.TrustedAccess;

if (args.Length is not (6 or 7)) return 2;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
// WTS does not inherit redirected handles. The WinExe fixture uses this owned
// diagnostic pipe for output and the test's release signal, never for a key.
using var diagnostics = args.Length == 7
    ? new NamedPipeClientStream(".", args[6], PipeDirection.InOut, PipeOptions.Asynchronous)
    : null;
using var diagnosticReader = diagnostics is null ? null : new StreamReader(diagnostics, leaveOpen: true);
using var diagnosticWriter = diagnostics is null ? null : new StreamWriter(diagnostics, leaveOpen: true);
if (diagnostics is not null)
{
    await diagnostics.ConnectAsync(deadline.Token);
    diagnosticWriter!.AutoFlush = true;
    Console.SetIn(diagnosticReader!);
    Console.SetOut(diagnosticWriter!);
}

var packageRoot = Path.GetFullPath(args[0]);
Environment.SetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT", packageRoot);
var backendProcessId = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
var backendCreatedUtcTicks = long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
var backendPath = Path.GetFullPath(args[3]);
var baseAddress = args[4];
var mode = args[5];
if (mode is not ("desktop" or "desktop-creation")) return 2;

var apiKey = TrustedAccessKeyFile.Read(packageRoot);
if (string.IsNullOrWhiteSpace(apiKey)) return 4;
if (diagnostics is not null && !MatchesDiagnosticServer(
        diagnostics.SafePipeHandle, backendProcessId, backendCreatedUtcTicks, backendPath))
{
    return 5;
}

var nativeAssembly = Assembly.Load("ResourceManager.NativeUi");
var backendSessionType = nativeAssembly.GetType("ResourceManager.NativeUi.BackendServiceSession", throwOnError: true)!;
using var session = (IDisposable)backendSessionType
    .GetMethod("CreateDefault", BindingFlags.Public | BindingFlags.Static)!
    .Invoke(null, [baseAddress])!;
var identityType = nativeAssembly.GetType("ResourceManager.NativeUi.BackendProcessIdentity", throwOnError: true)!;
var backendIdentity = Activator.CreateInstance(identityType,
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    args: [backendProcessId, backendCreatedUtcTicks, backendPath],
    culture: null)!;
const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
backendSessionType.GetField("<BackendPath>k__BackingField", instancePrivate)!.SetValue(session, backendPath);
backendSessionType.GetField("backendProcessIdentity", instancePrivate)!.SetValue(session, backendIdentity);
var authorizationType = backendSessionType.GetNestedType("BackendRequestAuthorization", BindingFlags.NonPublic)!;
var authorization = authorizationType.GetMethod("ForKey", BindingFlags.Public | BindingFlags.Static)!
    .Invoke(null, [apiKey]);
backendSessionType.GetField("requestAuthorization", instancePrivate)!.SetValue(session, authorization);

var publisherType = nativeAssembly.GetType(
    "ResourceManager.NativeUi.SystemIntegration.InteractiveDesktopObservationPublisher", throwOnError: true)!;
var captureTimer = Stopwatch.StartNew();
var sample = (InteractiveDesktopObservation)publisherType
    .GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, null)!;
var captureMilliseconds = captureTimer.Elapsed.TotalMilliseconds;
if (mode == "desktop-creation")
{
    sample = sample with { PublisherCreationFileTimeUtc = sample.PublisherCreationFileTimeUtc + 1 };
}

var rights = ReadBackendRights(diagnostics is null ? 0 : checked((uint)backendProcessId));
var requestBytes = JsonSerializer.SerializeToUtf8Bytes(sample,
    new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
var sendAgeMilliseconds = (DateTime.UtcNow.Ticks - sample.ObservedAtUtcTicks)
    / (double)TimeSpan.TicksPerMillisecond;
var published = true;
var statusCode = (int)HttpStatusCode.OK;
try
{
    await (Task)backendSessionType.GetMethod("PublishDesktopAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(session, [sample, deadline.Token])!;
}
catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
{
    published = false;
    statusCode = (int)error.StatusCode;
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    Published = published,
    Sample = sample,
    BackendRights = rights,
    CaptureMilliseconds = captureMilliseconds,
    RequestBytes = requestBytes,
    StatusCode = statusCode,
    SendAgeMilliseconds = sendAgeMilliseconds,
    FinishedAgeMilliseconds = (DateTime.UtcNow.Ticks - sample.ObservedAtUtcTicks)
        / (double)TimeSpan.TicksPerMillisecond
}));
await Console.In.ReadLineAsync(deadline.Token);
return 0;

static bool MatchesDiagnosticServer(SafePipeHandle pipe, int expectedProcessId,
    long expectedCreatedUtcTicks, string expectedPath)
{
    if (!ProbeNativeMethods.GetNamedPipeServerProcessId(pipe, out var processId) || processId != expectedProcessId)
    {
        return false;
    }
    using var process = ProbeNativeMethods.OpenProcess(0x1000, false, processId);
    if (process.IsInvalid || !ProbeNativeMethods.GetProcessTimes(process, out var creation, out _, out _, out _)
        || DateTime.FromFileTimeUtc(creation).Ticks != expectedCreatedUtcTicks)
    {
        return false;
    }
    var image = new StringBuilder(32_768);
    var length = checked((uint)image.Capacity);
    return ProbeNativeMethods.QueryFullProcessImageNameW(process, 0, image, ref length)
        && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(image.ToString()), expectedPath);
}

static object? ReadBackendRights(uint processId)
{
    if (processId == 0)
    {
        return null;
    }
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return new
    {
        FrontendIsSystem = identity.IsSystem,
        FrontendIsAdministrator = principal.IsInRole(WindowsBuiltInRole.Administrator),
        TerminationDenied = Denied(0x0001, processId),
        VmReadDenied = Denied(0x0010, processId),
        VmWriteDenied = Denied(0x0020, processId),
        DaclWriteDenied = Denied(0x00040000, processId)
    };
}

static bool Denied(uint access, uint processId)
{
    using var process = ProbeNativeMethods.OpenProcess(access, false, processId);
    return process.IsInvalid;
}

internal static class ProbeNativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited,
        out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags,
        StringBuilder name, ref uint size);
}
