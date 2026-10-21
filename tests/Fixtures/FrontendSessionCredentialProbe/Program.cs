using System.Reflection;
using System.Diagnostics;
using System.Net;
using System.IO.Pipes;
using System.Text.Json;
using ResourceManager.Shared.Runtime;

if (args.Length is not (5 or 6 or 7)) return 2;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
// WTS does not inherit redirected handles. The WinExe fixture uses this owned
// diagnostic pipe instead, without creating a console or touching product IPC.
using var diagnostics = args.Length == 7 ? new NamedPipeClientStream(".", args[6],
    PipeDirection.InOut, PipeOptions.Asynchronous) : null;
using var diagnosticReader = diagnostics is null ? null : new StreamReader(diagnostics, leaveOpen: true);
using var diagnosticWriter = diagnostics is null ? null : new StreamWriter(diagnostics, leaveOpen: true);
if (diagnostics is not null)
{
    await diagnostics.ConnectAsync(deadline.Token);
    diagnosticWriter!.AutoFlush = true;
    Console.SetIn(diagnosticReader!);
    Console.SetOut(diagnosticWriter!);
    args = args[..6];
}
// Invoke the compiled product consumer without initializing its desktop application.
var client = Assembly.Load("ResourceManager.NativeUi").GetType("ResourceManager.NativeUi.FrontendSessionCredentialClient", true)!;
var acquire = client.GetMethod("AcquireSessionAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
var reply = await (Task<FrontendSessionCredentialReply?>)acquire.Invoke(null, [args[1], deadline.Token, args[0]])!;
if (reply is null)
{
    if (diagnostics is not null)
    {
        var pidArguments = new object[] { diagnostics.SafePipeHandle, 0U };
        var pidRead = (bool)client.GetMethod("GetNamedPipeServerProcessId", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, pidArguments)!;
        using var server = (Microsoft.Win32.SafeHandles.SafeProcessHandle)client.GetMethod("OpenProcess", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [0x1000U, false, (uint)pidArguments[1]])!;
        var openError = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        var timeArguments = new object[] { server, 0L, 0L, 0L, 0L };
        var timesRead = !server.IsInvalid && (bool)client.GetMethod("GetProcessTimes", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, timeArguments)!;
        var imageName = new System.Text.StringBuilder(32768);
        var pathArguments = new object[] { server, 0U, imageName, 32768U };
        var imageRead = !server.IsInvalid && (bool)client.GetMethod("QueryFullProcessImageNameW", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, pathArguments)!;
        Console.WriteLine($"rejected; server PID read={pidRead}; server={pidArguments[1]}; open={!server.IsInvalid}; "
            + $"open error={openError}; times={timesRead}; image={imageRead}; image matches="
            + (imageRead && StringComparer.OrdinalIgnoreCase.Equals(System.IO.Path.GetFullPath(imageName.ToString()), System.IO.Path.GetFullPath(args[1]))));
    }
    else Console.WriteLine("rejected");
    return 0;
}
if (args.Length == 6)
{
    object? backendRights = null;
    if (diagnostics is not null)
    {
        var pidArguments = new object[] { diagnostics.SafePipeHandle, 0U };
        if (!(bool)client.GetMethod("GetNamedPipeServerProcessId", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, pidArguments)!) return 3;
        bool Denied(uint access)
        {
            using var handle = (Microsoft.Win32.SafeHandles.SafeProcessHandle)client.GetMethod("OpenProcess", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [access, false, (uint)pidArguments[1]])!;
            return handle.IsInvalid;
        }
        using var ownIdentity = System.Security.Principal.WindowsIdentity.GetCurrent();
        backendRights = new { FrontendIsSystem = ownIdentity.IsSystem,
            FrontendIsAdministrator = new System.Security.Principal.WindowsPrincipal(ownIdentity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
            TerminationDenied = Denied(0x0001), VmReadDenied = Denied(0x0010), VmWriteDenied = Denied(0x0020),
            DaclWriteDenied = Denied(0x00040000) };
    }
    var assembly = client.Assembly;
    var publisher = assembly.GetType("ResourceManager.NativeUi.SystemIntegration.InteractiveDesktopObservationPublisher", true)!;
    var timing = Stopwatch.StartNew();
    var sample = (InteractiveDesktopObservation)publisher.GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
    var captureMilliseconds = timing.Elapsed.TotalMilliseconds;
    if (args[5] == "desktop-creation")
        sample = sample with { PublisherCreationFileTimeUtc = sample.PublisherCreationFileTimeUtc + 1 };
    else if (args[5] is "desktop-limit" or "desktop-count" or "desktop-oversize")
        sample = sample with { ForegroundKnown = false, Foreground = null, Complete = false,
            Windows = Enumerable.Range(0, args[5] switch
                {
                    "desktop-count" => InteractiveDesktopObservation.MaximumWindows + 1,
                    "desktop-oversize" => InteractiveDesktopObservation.MaximumWindows * 2,
                    _ => InteractiveDesktopObservation.MaximumWindows
                })
                .Select(index => new DesktopProcessWindowObservation(int.MaxValue - index,
                    sample.PublisherCreationFileTimeUtc, false, false, false)).ToArray() };
    else if (args[5] != "desktop") return 2;
    var sessionType = assembly.GetType("ResourceManager.NativeUi.BackendServiceSession", true)!;
    using var session = (IDisposable)sessionType.GetMethod("CreateDefault", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [args[2]])!;
    // Set only the owned test backend path and the reply actually obtained above.
    // The compiled request method still verifies the real server socket identity.
    sessionType.GetField("<BackendPath>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, args[1]);
    sessionType.GetField("backendCredential", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, reply);
    var authorization = sessionType.GetNestedType("BackendRequestAuthorization", BindingFlags.NonPublic)!;
    var tokenAuthorization = authorization.GetMethod("ForToken", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [reply.AccessToken]);
    sessionType.GetField("requestAuthorization", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, tokenAuthorization);
    var published = true;
    var statusCode = (int)HttpStatusCode.OK;
    var requestBytes = JsonSerializer.SerializeToUtf8Bytes(sample, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
    var sendAgeMilliseconds = (DateTime.UtcNow.Ticks - sample.ObservedAtUtcTicks) / (double)TimeSpan.TicksPerMillisecond;
    try
    {
        await (Task)sessionType.GetMethod("PublishDesktopAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, [sample, deadline.Token])!;
    }
    catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
    { published = false; statusCode = (int)error.StatusCode; }
    Console.WriteLine(JsonSerializer.Serialize(new { Published = published, Sample = sample, BackendRights = backendRights, CaptureMilliseconds = captureMilliseconds,
        RequestBytes = requestBytes, StatusCode = statusCode, SendAgeMilliseconds = sendAgeMilliseconds,
        FinishedAgeMilliseconds = (DateTime.UtcNow.Ticks - sample.ObservedAtUtcTicks) / (double)TimeSpan.TicksPerMillisecond }));
    await Console.In.ReadLineAsync(deadline.Token);
    return 0;
}
using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
using var request = new HttpRequestMessage(HttpMethod.Get, args[2] + args[3]);
request.Headers.Add(args[4], reply.AccessToken);
using var response = await http.SendAsync(request, deadline.Token);
Console.WriteLine((int)response.StatusCode);
await Console.In.ReadLineAsync(deadline.Token); // Retain the credential owner until the test releases it.
return 0;
