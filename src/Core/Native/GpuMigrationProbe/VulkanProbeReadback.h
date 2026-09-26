#pragma once

inline void RepeatReadback(OwnedDevice& owned, uint64_t originalLuid)
{
    const auto device = owned.device;
    const auto gdpa = owned.gdpa;
    std::memset(owned.mapped, 0, 4096);
    VkMappedMemoryRange range{};
    range.sType = VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE;
    range.memory = owned.memory;
    range.size = VK_WHOLE_SIZE;
    Check(DP(vkFlushMappedMemoryRanges)(device, 1, &range) == VK_SUCCESS, "old-device-zero-flushed");
    Check(DP(vkResetFences)(device, 1, &owned.fence) == VK_SUCCESS, "old-device-fence-reset");
    VkSubmitInfo submit{};
    submit.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    submit.commandBufferCount = 1;
    submit.pCommandBuffers = &owned.command;
    Check(DP(vkQueueSubmit)(owned.queue, 1, &submit, owned.fence) == VK_SUCCESS, "old-device-resubmitted");
    const auto result = DP(vkWaitForFences)(device, 1, &owned.fence, VK_TRUE, 3000000000ull);
    if (result != VK_SUCCESS)
    {
        std::printf("{\"passed\":false,\"fenceResult\":%d,\"cleanup\":\"failed-probe-process-exit\"}\n", result);
        std::fflush(stdout);
        ExitProcess(1);
    }
    Check(DP(vkInvalidateMappedMemoryRanges)(device, 1, &range) == VK_SUCCESS, "old-device-invalidated");
    const auto data = static_cast<const uint32_t*>(owned.mapped);
    Check(std::all_of(data, data + 1024, [](uint32_t value) { return value == 0x52AFC37Du; }),
        "old-device-all-1024-words-match");
    std::printf("{\"retainedDeviceReadback\":true,\"originalLuid\":\"%016llx\",\"words\":1024}\n",
        static_cast<unsigned long long>(originalLuid));
}
