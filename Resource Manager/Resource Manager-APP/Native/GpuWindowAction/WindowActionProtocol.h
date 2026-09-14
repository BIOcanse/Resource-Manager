#pragma once
#include <cstdint>

namespace window_action {
constexpr uint32_t version = 2;
enum class Kind : uint32_t { prepare = 1, prepared, execute, rejected, completed, parent };
enum class Method : uint32_t { redraw = 1, resize };
enum class Reason : uint32_t { target_unavailable = 1, window_mismatch, not_eligible, state_changed, invalid_request, context_unavailable };
enum class CallState : uint32_t { not_attempted, accepted, rejected, target_unavailable };
enum class ReadState : uint32_t { not_read, available, unavailable };

#pragma pack(push, 1)
struct Header { uint32_t version; Kind kind; };
struct Parent { Header header; uint64_t handle; };
struct Target { uint32_t pid; uint64_t creation; uint64_t window; Method method; };
struct State { int32_t left, top, width, height; uint32_t flags; };
struct Request { Header header; Target target; };
struct Prepared { Header header; Target target; uint32_t thread_id; State before; };
struct Rejected { Header header; Reason reason; uint32_t error; };
struct Call { CallState state; uint32_t error; };
struct Read { ReadState state; uint32_t error; State value; };
struct Completed { Header header; Call change; Read after_change; Call restore; Read after_restore; };
#pragma pack(pop)

static_assert(sizeof(Header) == 8);
static_assert(sizeof(Parent) == 16);
static_assert(sizeof(Request) == 32);
static_assert(sizeof(Prepared) == 56);
static_assert(sizeof(Rejected) == 16);
static_assert(sizeof(Completed) == 80);
constexpr uint32_t maximum_message_bytes = sizeof(Completed);
}
