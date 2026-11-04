#include "IdleReleaseTest.h"
#include "SharedSection.h"
#include "ReaderTimingProtocolV1.h"
#include <algorithm>
#include <numeric>
#include <type_traits>

using namespace IdleReleaseTest;

namespace
{
template <typename Header> struct TimingPublisher
{
    HANDLE mapping = nullptr;
    Header* header = nullptr;

    TimingPublisher(uint32_t version, bool enabled)
    {
        wchar_t name[96]{};
        Check(OverlayProtocol::SectionName(name), "Timing SectionName failed");
        mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
            sizeof(Header) + OverlayProtocol::MaximumPixelBytes, name);
        const DWORD error = GetLastError();
        Check(mapping && error != ERROR_ALREADY_EXISTS, "Timing publisher found a retained section");
        header = static_cast<Header*>(MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0));
        Check(header != nullptr, "Timing publisher map failed");
        std::memset(header, 0, sizeof(*header));
        header->magic = OverlayProtocol::Magic;
        header->version = version;
        header->headerBytes = sizeof(Header);
        header->pixelCapacity = OverlayProtocol::MaximumPixelBytes;
        if constexpr (std::is_same_v<Header, OverlayProtocol::Header>)
            header->publisherProcessId = GetCurrentProcessId();
        if (enabled)
        {
            InterlockedExchange64(&header->generation, 1);
            header->width = Width;
            header->height = Height;
            header->stride = Width * 4;
            auto* pixels = reinterpret_cast<uint32_t*>(reinterpret_cast<uint8_t*>(header) + sizeof(Header));
            for (size_t i = 0; i < size_t(Width) * Height; ++i) pixels[i] = 0xff34ab12u;
            InterlockedExchange64(&header->generation, 2);
            InterlockedExchange(&header->enabled, 1);
        }
    }

    ~TimingPublisher()
    {
        if (header)
        {
            if constexpr (std::is_same_v<Header, OverlayProtocol::Header>)
                InterlockedExchange(&header->enabled, OverlayProtocol::Disabled);
            else InterlockedExchange(&header->enabled, 0);
            UnmapViewOfFile(header);
        }
        if (mapping) CloseHandle(mapping);
    }
};

enum class Operation { Frame, Observe, Drew, FrameAndDrew };

template <typename Reader> void Feedback(Reader& reader, uint64_t generation, uint64_t uploadId)
{
    if constexpr (std::is_same_v<Reader, OverlaySection::Reader>) reader.Drew(generation, uploadId);
    else reader.Drew(generation);
}

template <Operation Op, typename Reader> void Step(Reader& reader)
{
    if constexpr (Op == Operation::Frame) reader.Frame(nullptr, Width, Height);
    if constexpr (Op == Operation::Observe) reader.ObserveFrame(nullptr, Width, Height);
    if constexpr (Op == Operation::Drew) Feedback(reader, 2, 1);
    if constexpr (Op == Operation::FrameAndDrew)
    {
        const auto bitmap = reader.Frame(nullptr, Width, Height);
        if (bitmap)
        {
            if constexpr (std::is_same_v<Reader, OverlaySection::Reader>)
                Feedback(reader, bitmap->generation, bitmap->uploadId);
            else Feedback(reader, bitmap->generation, 0);
        }
    }
}

// RuntimeRead: Sampled
template <Operation Op, typename Reader> void Measure(const char* implementation, const char* scenario,
    const char* operation, LONGLONG frequency, bool enabled)
{
    Reader reader;
    LARGE_INTEGER before{}, after{};
    QueryPerformanceCounter(&before);
    Step<Op>(reader);
    QueryPerformanceCounter(&after);
    const double cold = double(after.QuadPart - before.QuadPart) * 1000000.0 / double(frequency);
    if constexpr (Op == Operation::Drew) reader.Frame(nullptr, Width, Height);
    for (int i = 0; i < 16; ++i) Step<Op>(reader);
    constexpr int samples = 101;
    const int calls = 64;
    std::vector<double> elapsed;
    elapsed.reserve(samples);
    for (int sample = 0; sample < samples; ++sample)
    {
        QueryPerformanceCounter(&before);
        for (int i = 0; i < calls; ++i) Step<Op>(reader);
        QueryPerformanceCounter(&after);
        elapsed.push_back(double(after.QuadPart - before.QuadPart) * 1000000.0 / double(frequency) / calls);
    }
    std::sort(elapsed.begin(), elapsed.end());
    const double mean = std::accumulate(elapsed.begin(), elapsed.end(), 0.0) / samples;
    double probe = -1;
    if (!enabled)
    {
        Sleep(270);
        QueryPerformanceCounter(&before);
        Step<Op>(reader);
        QueryPerformanceCounter(&after);
        probe = double(after.QuadPart - before.QuadPart) * 1000000.0 / double(frequency);
    }
    std::printf("%s,%s,%s,%d,%d,%.6f,%.6f,%.6f,%.6f,%.6f,%.6f,%.6f,%.6f\n", implementation, scenario,
        operation, samples, calls, cold, mean, elapsed[50], elapsed[95], elapsed[99], elapsed.front(), elapsed.back(), probe);
    std::fflush(stdout);
}

template <typename Reader, typename Header> void MeasureReader(const char* implementation, uint32_t version, LONGLONG frequency)
{
    {
        TimingPublisher<Header> publisher(version, true);
        Measure<Operation::Frame, Reader>(implementation, "steady_enabled_1920x1080", "Frame", frequency, true);
        Measure<Operation::Observe, Reader>(implementation, "steady_enabled_1920x1080", "ObserveFrame_alternative", frequency, true);
        Measure<Operation::Drew, Reader>(implementation, "steady_enabled_1920x1080", "Drew", frequency, true);
        Measure<Operation::FrameAndDrew, Reader>(implementation, "steady_enabled_1920x1080", "Frame+Drew_normal_present", frequency, true);
    }
    Measure<Operation::Frame, Reader>(implementation, "missing_section", "Frame_normal_present", frequency, false);
    Measure<Operation::Observe, Reader>(implementation, "missing_section", "ObserveFrame_alternative", frequency, false);
    {
        TimingPublisher<Header> publisher(version, false);
        Measure<Operation::Frame, Reader>(implementation, "initial_handshake_generation0", "Frame_normal_present", frequency, false);
        Measure<Operation::Observe, Reader>(implementation, "initial_handshake_generation0", "ObserveFrame_alternative", frequency, false);
    }
    if constexpr (std::is_same_v<Header, OverlayProtocol::Header>)
    {
        TimingPublisher<Header> publisher(version, false);
        InterlockedExchange(&publisher.header->enabled, OverlayProtocol::Disabled);
        Measure<Operation::Frame, Reader>(implementation, "terminal_disabled_minus1", "Frame_normal_present", frequency, false);
        Measure<Operation::Observe, Reader>(implementation, "terminal_disabled_minus1", "ObserveFrame_alternative", frequency, false);
    }
}

void VerifyVersionRefusal()
{
    TimingPublisher<TimingProtocolV1::Header> oldPublisher(TimingProtocolV1::Version, true);
    OverlaySection::Reader reader;
    Check(!reader.Frame(nullptr, Width, Height), "New reader accepted a v1 section");
    reader.ObserveFrame(nullptr, Width, Height);
    reader.Drew(2, 1);
    Check(InterlockedCompareExchange64(&oldPublisher.header->frameCounter, 0, 0) == 0
        && InterlockedCompareExchange64(&oldPublisher.header->drawSuccessCounter, 0, 0) == 0,
        "New reader wrote feedback into an incompatible v1 section");
    std::puts("ASSERT v1_mismatch_refused=yes no_v1_feedback=yes");
}

void VerifyOwnerAndUploadIdentity()
{
    Publisher publisher;
    OverlaySection::Reader reader;
    publisher.Open(nullptr, 0xff112233u);
    auto bitmap = reader.Frame(nullptr, Width, Height);
    Check(bitmap && bitmap->generation == 2, "New reader did not acquire its first publisher");
    const uint64_t firstUpload = bitmap->uploadId;
    const uint8_t* firstPixels = bitmap->pixels.data();
    bitmap.reset();
    for (int i = 0; i < 100; ++i)
    {
        const auto steady = reader.Frame(nullptr, Width, Height);
        Check(steady && steady->uploadId == firstUpload && steady->pixels.data() == firstPixels,
            "Steady reader did not reuse its bitmap/upload identity");
        reader.ObserveFrame(nullptr, Width, Height);
        reader.Drew(2, firstUpload);
    }
    publisher.Disable();
    Check(!reader.Frame(nullptr, Width, Height), "New reader retained a disabled frame");
    publisher.Close();
    publisher.CheckGone();
    Sleep(270);
    publisher.Open(nullptr, 0xffaabbccu);
    bitmap = reader.Frame(nullptr, Width, Height);
    Check(bitmap && bitmap->generation == 2 && bitmap->uploadId != firstUpload,
        "New publisher reused the previous view's upload identity");
    Check(*reinterpret_cast<const uint32_t*>(bitmap->pixels.data()) == 0xffaabbccu,
        "New publisher reused stale pixels");
    const uint64_t secondUpload = bitmap->uploadId;
    bitmap.reset();
    publisher.Close(false);
    Check(!reader.Frame(nullptr, Width, Height), "Reader did not detect the real publisher process exit");
    publisher.CheckGone();
    std::printf("ASSERT steady_bitmap_reused=yes new_view_upload_id_changed=yes first_upload=%llu second_upload=%llu real_publisher_exit_released=yes\n",
        static_cast<unsigned long long>(firstUpload), static_cast<unsigned long long>(secondUpload));
}

void VerifyLivePublisherHandoff()
{
    Publisher publisher;
    PublisherWitness replacement;
    OverlaySection::Reader reader;
    publisher.Open(nullptr, 0xff123456u);
    auto bitmap = reader.Frame(nullptr, Width, Height);
    Check(bitmap != nullptr, "Reader did not acquire owner A before live handoff");
    const uint64_t firstUpload = bitmap->uploadId;
    bitmap.reset();
    replacement.Open();
    publisher.header->publisherProcessId = replacement.processId;
    Check(!reader.Frame(nullptr, Width, Height), "Reader did not close the previous live owner's view on handoff");
    Check(WaitForSingleObject(publisher.witness.process, 0) == WAIT_TIMEOUT,
        "Original publisher witness exited during live handoff");
    Sleep(270);
    bitmap = reader.Frame(nullptr, Width, Height);
    Check(bitmap && bitmap->generation == 2 && bitmap->uploadId != firstUpload,
        "Live handoff did not acquire a new upload identity for the same generation");
    const uint64_t replacementUpload = bitmap->uploadId;
    bitmap.reset();
    const LONG64 beforeDraws = OverlayProtocol::Read64(&publisher.header->drawSuccessCounter);
    const LONG64 beforeGeneration = OverlayProtocol::Read64(&publisher.header->consumedGeneration);
    reader.Drew(2, firstUpload);
    Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) == beforeDraws
        && OverlayProtocol::Read64(&publisher.header->consumedGeneration) == beforeGeneration,
        "Stale in-flight owner A feedback changed owner B's counters");
    reader.Drew(2, replacementUpload);
    Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) == beforeDraws + 1
        && OverlayProtocol::Read64(&publisher.header->consumedGeneration) == 2,
        "Matching owner B feedback did not update its counters");
    replacement.Close();
    Check(!reader.Frame(nullptr, Width, Height), "Reader did not release the exited replacement owner");
    Check(WaitForSingleObject(publisher.witness.process, 0) == WAIT_TIMEOUT,
        "Original owner must remain alive when replacement death is observed");
    publisher.Close(false);
    publisher.CheckGone();
    std::printf("ASSERT same_live_section_owner_handoff=yes first_upload=%llu replacement_upload=%llu stale_feedback_ignored=yes matching_feedback_recorded=yes old_owner_still_alive_when_replacement_exited=yes replacement_exit_released=yes\n",
        static_cast<unsigned long long>(firstUpload), static_cast<unsigned long long>(replacementUpload));
}

void VerifyActiveHandshake()
{
    {
        Publisher publisher;
        OverlaySection::Reader reader;
        publisher.Open(nullptr, 0xff112233u, false);
        for (int i = 0; i < 100; ++i)
        {
            const uint32_t width = Width + uint32_t(i & 1);
            Check(!reader.Frame(nullptr, width, Height), "Handshake returned a drawable bitmap");
            Check(OverlayProtocol::Read64(&publisher.header->frameCounter) == i + 1
                && OverlayProtocol::Read32(&publisher.header->swapChainWidth) == static_cast<LONG>(width),
                "Handshake Frame did not report every present");
        }
        for (int i = 0; i < 100; ++i)
        {
            const uint32_t height = Height + uint32_t(i & 1);
            reader.ObserveFrame(nullptr, Width, height);
            Check(OverlayProtocol::Read64(&publisher.header->frameCounter) == i + 101
                && OverlayProtocol::Read32(&publisher.header->swapChainHeight) == static_cast<LONG>(height),
                "Handshake ObserveFrame did not report every present");
        }
        Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) == 0,
            "Handshake reported a successful draw");
        publisher.Disable();
        publisher.CloseView(false);
        Check(WaitForSingleObject(publisher.witness.process, 0) == WAIT_TIMEOUT,
            "Publisher witness must stay alive during terminal disable proof");
        Check(!reader.Frame(nullptr, Width, Height), "Terminal disable returned a bitmap");
        publisher.CheckGone();
        Check(WaitForSingleObject(publisher.witness.process, 0) == WAIT_TIMEOUT,
            "Terminal disable proof relied on publisher process exit");
        publisher.witness.Close();
    }
    {
        Publisher publisher;
        OverlaySection::Reader reader;
        publisher.Open(nullptr, 0xff445566u);
        auto bitmap = reader.Frame(nullptr, Width, Height);
        Check(bitmap != nullptr, "Reader did not draw before adopted handshake");
        const uint64_t firstUpload = bitmap->uploadId;
        std::weak_ptr<const OverlaySection::Bitmap> oldBitmap = bitmap;
        bitmap.reset();
        const SIZE_T fullMapped = MappedCommit();
        InterlockedExchange(&publisher.header->enabled, OverlayProtocol::Handshake);
        Check(!reader.Frame(nullptr, Width, Height), "Adopted handshake returned the old bitmap");
        Check(oldBitmap.expired(), "Adopted handshake retained the old bitmap allocation");
        const SIZE_T headerMapped = MappedCommit();
        Check(fullMapped >= headerMapped + OverlayProtocol::MaximumPixelBytes,
            "Adopted handshake did not downgrade its full view to header bytes");
        InterlockedExchange(&publisher.header->enabled, OverlayProtocol::Enabled);
        bitmap = reader.Frame(nullptr, Width, Height);
        Check(bitmap && bitmap->generation == 2 && bitmap->uploadId != firstUpload,
            "Adopted handshake did not recreate its bitmap/upload identity when enabled");
        bitmap.reset();
        publisher.Disable();
        publisher.CloseView(false);
        Check(!reader.Frame(nullptr, Width, Height), "Disabled adopted publisher returned a bitmap");
        publisher.CheckGone();
        Check(WaitForSingleObject(publisher.witness.process, 0) == WAIT_TIMEOUT,
            "Adopted publisher terminal disable relied on process exit");
        publisher.witness.Close();
        std::printf("ASSERT adopted_same_owner_handshake_released_bitmap=yes full_to_header_mapped_drop=%llu same_generation_resume_new_upload=yes\n",
            static_cast<unsigned long long>(fullMapped - headerMapped));
    }
    std::puts("ASSERT handshake_each_Frame_100=yes handshake_each_ObserveFrame_100=yes handshake_no_draw=yes disabled_section_gone_while_publisher_alive=yes");
}
}

// RuntimeRead: Fixed
int main()
{
    try
    {
        LARGE_INTEGER frequency{};
        Check(QueryPerformanceFrequency(&frequency), "QueryPerformanceFrequency failed");
        std::printf("# QPC_frequency=%lld; normal enabled present=Frame+Drew; ObserveFrame is an alternative; units=microseconds; no GPU work\n", frequency.QuadPart);
        std::puts("# cold is the first requested operation; steady uses batched QPC after 16 warmup calls; Drew is primed by Frame; after_270ms may cross a missing/terminal-disabled probe gate, while v2 handshake keeps its active header; -1 means not applicable");
        std::puts("reader,scenario,operation,samples,calls_per_sample,cold_us,mean_us,median_us,p95_us,p99_us,min_us,max_us,after_270ms_us");
        MeasureReader<OverlaySection::Reader, OverlayProtocol::Header>("new_v2", OverlayProtocol::Version, frequency.QuadPart);
        VerifyVersionRefusal();
        VerifyOwnerAndUploadIdentity();
        VerifyLivePublisherHandoff();
        VerifyActiveHandshake();
        std::puts("Reader timing and protocol/lifetime checks PASS");
        return 0;
    }
    catch (const std::exception& error)
    {
        std::fprintf(stderr, "Reader timing FAIL: %s (Win32 error %lu)\n", error.what(), GetLastError());
        return 1;
    }
}
