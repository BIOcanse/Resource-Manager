namespace ResourceManager.App.Infrastructure.NativeCore;

internal enum NativeCoreResultCode
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    Unavailable = 3,
    NoData = 4,
    BufferTooSmall = 5,
    StaleFrame = 6,
    OutOfMemory = 7,
    PdhError = 8
}
