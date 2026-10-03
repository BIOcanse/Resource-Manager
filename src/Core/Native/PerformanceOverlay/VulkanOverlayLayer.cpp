#define WIN32_LEAN_AND_MEAN
#define VK_NO_PROTOTYPES
#define VK_USE_PLATFORM_WIN32_KHR
#include <windows.h>
#include <vulkan/vk_layer.h>
#include <vulkan/vulkan_win32.h>
#include "SharedSection.h"
#include "OverlayVulkanShaders.h"
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <vector>

#define LAYER_EXPORT extern "C" __declspec(dllexport)

namespace
{
constexpr char LayerName[] = "VK_LAYER_RESOURCE_MANAGER_performance_overlay";
struct InstanceState
{
    VkInstance instance;
    PFN_vkGetInstanceProcAddr next;
    PFN_GetPhysicalDeviceProcAddr nextPhysical;
};
struct DeviceState
{
    VkDevice device;
    PFN_vkGetDeviceProcAddr next;
    VkPhysicalDeviceMemoryProperties memory;
    std::vector<VkQueueFlags> queueFlags;
};
struct FrameResources
{
    VkImage image = VK_NULL_HANDLE;
    VkImageView view = VK_NULL_HANDLE;
    VkFramebuffer framebuffer = VK_NULL_HANDLE;
    VkCommandBuffer command = VK_NULL_HANDLE;
    VkFence fence = VK_NULL_HANDLE;
    VkSemaphore signal = VK_NULL_HANDLE;
};
struct SwapchainState
{
    bool ready = false;
    VkDevice device = VK_NULL_HANDLE;
    HWND window = nullptr;
    VkExtent2D extent{};
    VkFormat format = VK_FORMAT_UNDEFINED;
    VkImageUsageFlags usage = 0;
    uint32_t queueFamily = UINT32_MAX;
    VkCommandPool pool = VK_NULL_HANDLE;
    VkRenderPass pass = VK_NULL_HANDLE;
    VkDescriptorSetLayout descriptorLayout = VK_NULL_HANDLE;
    VkPipelineLayout pipelineLayout = VK_NULL_HANDLE;
    VkPipeline pipeline = VK_NULL_HANDLE;
    VkDescriptorPool descriptorPool = VK_NULL_HANDLE;
    VkDescriptorSet descriptor = VK_NULL_HANDLE;
    VkSampler sampler = VK_NULL_HANDLE;
    VkImage texture = VK_NULL_HANDLE;
    VkDeviceMemory textureMemory = VK_NULL_HANDLE;
    VkImageView textureView = VK_NULL_HANDLE;
    VkBuffer staging = VK_NULL_HANDLE;
    VkDeviceMemory stagingMemory = VK_NULL_HANDLE;
    VkDeviceSize stagingSize = 0;
    uint32_t textureWidth = 0, textureHeight = 0;
    uint64_t uploadedGeneration = 0;
    std::vector<FrameResources> frames;
};
std::mutex stateMutex;
std::unordered_map<void*, InstanceState> instances;
std::unordered_map<void*, DeviceState> devices;
std::unordered_map<VkSurfaceKHR, HWND> surfaces;
std::unordered_map<VkSwapchainKHR, SwapchainState> swapchains;
std::unordered_map<VkQueue, uint32_t> queueFamilies;
OverlaySection::Reader section;

template <typename T> void* Key(T handle) { return *reinterpret_cast<void**>(handle); }
template <typename T> InstanceState Instance(T handle)
{
    std::lock_guard<std::mutex> guard(stateMutex);
    const auto found = instances.find(Key(handle));
    return found == instances.end() ? InstanceState{} : found->second;
}
template <typename T> DeviceState Device(T handle)
{
    std::lock_guard<std::mutex> guard(stateMutex);
    const auto found = devices.find(Key(handle));
    return found == devices.end() ? DeviceState{} : found->second;
}

template <typename T> T Proc(const DeviceState& state, const char* name)
{
    const PFN_vkVoidFunction address = state.next(state.device, name);
    T function = nullptr;
    static_assert(sizeof(function) == sizeof(address));
    std::memcpy(&function, &address, sizeof(function));
    return function;
}
#define VK_FN(state, name) Proc<PFN_##name>(state, #name)

uint32_t MemoryType(const DeviceState& state, uint32_t bits, VkMemoryPropertyFlags required)
{
    for (uint32_t i = 0; i < state.memory.memoryTypeCount; ++i)
        if ((bits & (1u << i)) && (state.memory.memoryTypes[i].propertyFlags & required) == required) return i;
    return UINT32_MAX;
}

void DestroyBitmap(const DeviceState& device, SwapchainState& chain)
{
    if (chain.textureView) VK_FN(device, vkDestroyImageView)(device.device, chain.textureView, nullptr);
    if (chain.texture) VK_FN(device, vkDestroyImage)(device.device, chain.texture, nullptr);
    if (chain.textureMemory) VK_FN(device, vkFreeMemory)(device.device, chain.textureMemory, nullptr);
    if (chain.staging) VK_FN(device, vkDestroyBuffer)(device.device, chain.staging, nullptr);
    if (chain.stagingMemory) VK_FN(device, vkFreeMemory)(device.device, chain.stagingMemory, nullptr);
    chain.textureView = VK_NULL_HANDLE;
    chain.texture = VK_NULL_HANDLE;
    chain.textureMemory = VK_NULL_HANDLE;
    chain.staging = VK_NULL_HANDLE;
    chain.stagingMemory = VK_NULL_HANDLE;
    chain.stagingSize = 0;
    chain.textureWidth = chain.textureHeight = 0;
    chain.uploadedGeneration = 0;
}

void DestroyOverlay(const DeviceState& device, SwapchainState& chain)
{
    if (chain.pool || chain.pipeline || chain.texture) VK_FN(device, vkDeviceWaitIdle)(device.device);
    DestroyBitmap(device, chain);
    for (auto& frame : chain.frames)
    {
        if (frame.framebuffer) VK_FN(device, vkDestroyFramebuffer)(device.device, frame.framebuffer, nullptr);
        if (frame.view) VK_FN(device, vkDestroyImageView)(device.device, frame.view, nullptr);
        if (frame.fence) VK_FN(device, vkDestroyFence)(device.device, frame.fence, nullptr);
        if (frame.signal) VK_FN(device, vkDestroySemaphore)(device.device, frame.signal, nullptr);
    }
    chain.frames.clear();
    if (chain.pool) VK_FN(device, vkDestroyCommandPool)(device.device, chain.pool, nullptr);
    if (chain.pipeline) VK_FN(device, vkDestroyPipeline)(device.device, chain.pipeline, nullptr);
    if (chain.pipelineLayout) VK_FN(device, vkDestroyPipelineLayout)(device.device, chain.pipelineLayout, nullptr);
    if (chain.pass) VK_FN(device, vkDestroyRenderPass)(device.device, chain.pass, nullptr);
    if (chain.descriptorPool) VK_FN(device, vkDestroyDescriptorPool)(device.device, chain.descriptorPool, nullptr);
    if (chain.descriptorLayout) VK_FN(device, vkDestroyDescriptorSetLayout)(device.device, chain.descriptorLayout, nullptr);
    if (chain.sampler) VK_FN(device, vkDestroySampler)(device.device, chain.sampler, nullptr);
    chain.pool = VK_NULL_HANDLE;
    chain.pipeline = VK_NULL_HANDLE;
    chain.pipelineLayout = VK_NULL_HANDLE;
    chain.pass = VK_NULL_HANDLE;
    chain.descriptorPool = VK_NULL_HANDLE;
    chain.descriptorLayout = VK_NULL_HANDLE;
    chain.sampler = VK_NULL_HANDLE;
    chain.descriptor = VK_NULL_HANDLE;
    chain.ready = false;
}

bool CreateRenderer(const DeviceState& device, VkSwapchainKHR handle, SwapchainState& chain, uint32_t family)
{
    if (!(chain.usage & VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT)) return false;
    const auto getImages = VK_FN(device, vkGetSwapchainImagesKHR);
    uint32_t count = 0;
    if (!getImages || getImages(device.device, handle, &count, nullptr) != VK_SUCCESS || !count) return false;
    std::vector<VkImage> images(count);
    if (getImages(device.device, handle, &count, images.data()) != VK_SUCCESS) return false;
    chain.queueFamily = family;
    chain.frames.resize(count);
    for (uint32_t i = 0; i < count; ++i) chain.frames[i].image = images[i];

    VkAttachmentDescription attachment{};
    attachment.format = chain.format;
    attachment.samples = VK_SAMPLE_COUNT_1_BIT;
    attachment.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
    attachment.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    attachment.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    attachment.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    attachment.initialLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    attachment.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    VkAttachmentReference reference{ 0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL };
    VkSubpassDescription subpass{};
    subpass.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    subpass.colorAttachmentCount = 1;
    subpass.pColorAttachments = &reference;
    VkSubpassDependency dependencies[2]{};
    dependencies[0].srcSubpass = VK_SUBPASS_EXTERNAL;
    dependencies[0].dstSubpass = 0;
    dependencies[0].srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dependencies[0].dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dependencies[0].srcAccessMask = VK_ACCESS_MEMORY_READ_BIT;
    dependencies[0].dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    dependencies[1].srcSubpass = 0;
    dependencies[1].dstSubpass = VK_SUBPASS_EXTERNAL;
    dependencies[1].srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dependencies[1].dstStageMask = VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT;
    dependencies[1].srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    dependencies[1].dstAccessMask = VK_ACCESS_MEMORY_READ_BIT;
    VkRenderPassCreateInfo passInfo{ VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO };
    passInfo.attachmentCount = 1;
    passInfo.pAttachments = &attachment;
    passInfo.subpassCount = 1;
    passInfo.pSubpasses = &subpass;
    passInfo.dependencyCount = 2;
    passInfo.pDependencies = dependencies;
    if (VK_FN(device, vkCreateRenderPass)(device.device, &passInfo, nullptr, &chain.pass) != VK_SUCCESS) return false;

    VkDescriptorSetLayoutBinding binding{};
    binding.binding = 0;
    binding.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    binding.descriptorCount = 1;
    binding.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    VkDescriptorSetLayoutCreateInfo layoutInfo{ VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO };
    layoutInfo.bindingCount = 1;
    layoutInfo.pBindings = &binding;
    if (VK_FN(device, vkCreateDescriptorSetLayout)(device.device, &layoutInfo, nullptr, &chain.descriptorLayout) != VK_SUCCESS) return false;
    VkPushConstantRange push{ VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof(float) * 4 };
    VkPipelineLayoutCreateInfo pipelineLayoutInfo{ VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO };
    pipelineLayoutInfo.setLayoutCount = 1;
    pipelineLayoutInfo.pSetLayouts = &chain.descriptorLayout;
    pipelineLayoutInfo.pushConstantRangeCount = 1;
    pipelineLayoutInfo.pPushConstantRanges = &push;
    if (VK_FN(device, vkCreatePipelineLayout)(device.device, &pipelineLayoutInfo, nullptr, &chain.pipelineLayout) != VK_SUCCESS) return false;

    VkShaderModuleCreateInfo shaderInfo{ VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO };
    shaderInfo.codeSize = sizeof(OverlayVulkanShaders::Vertex);
    shaderInfo.pCode = OverlayVulkanShaders::Vertex;
    VkShaderModule vertex = VK_NULL_HANDLE, fragment = VK_NULL_HANDLE;
    if (VK_FN(device, vkCreateShaderModule)(device.device, &shaderInfo, nullptr, &vertex) != VK_SUCCESS) return false;
    shaderInfo.codeSize = sizeof(OverlayVulkanShaders::Fragment);
    shaderInfo.pCode = OverlayVulkanShaders::Fragment;
    if (VK_FN(device, vkCreateShaderModule)(device.device, &shaderInfo, nullptr, &fragment) != VK_SUCCESS)
    {
        VK_FN(device, vkDestroyShaderModule)(device.device, vertex, nullptr);
        return false;
    }
    VkPipelineShaderStageCreateInfo stages[2]{};
    stages[0].sType = stages[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
    stages[0].module = vertex;
    stages[0].pName = "main";
    stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
    stages[1].module = fragment;
    stages[1].pName = "main";
    VkPipelineVertexInputStateCreateInfo vertexInput{ VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO };
    VkPipelineInputAssemblyStateCreateInfo assembly{ VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO };
    assembly.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    VkViewport viewport{ 0, 0, float(chain.extent.width), float(chain.extent.height), 0, 1 };
    VkRect2D scissor{ {0, 0}, chain.extent };
    VkPipelineViewportStateCreateInfo viewportState{ VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO };
    viewportState.viewportCount = 1; viewportState.pViewports = &viewport;
    viewportState.scissorCount = 1; viewportState.pScissors = &scissor;
    VkPipelineRasterizationStateCreateInfo raster{ VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO };
    raster.polygonMode = VK_POLYGON_MODE_FILL;
    raster.cullMode = VK_CULL_MODE_NONE;
    raster.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE;
    raster.lineWidth = 1;
    VkPipelineMultisampleStateCreateInfo multisample{ VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO };
    multisample.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
    VkPipelineColorBlendAttachmentState color{};
    color.blendEnable = VK_TRUE;
    color.srcColorBlendFactor = VK_BLEND_FACTOR_ONE;
    color.dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    color.colorBlendOp = VK_BLEND_OP_ADD;
    color.srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
    color.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    color.alphaBlendOp = VK_BLEND_OP_ADD;
    color.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT
        | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
    VkPipelineColorBlendStateCreateInfo blend{ VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO };
    blend.attachmentCount = 1; blend.pAttachments = &color;
    VkGraphicsPipelineCreateInfo pipelineInfo{ VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO };
    pipelineInfo.stageCount = 2; pipelineInfo.pStages = stages;
    pipelineInfo.pVertexInputState = &vertexInput;
    pipelineInfo.pInputAssemblyState = &assembly;
    pipelineInfo.pViewportState = &viewportState;
    pipelineInfo.pRasterizationState = &raster;
    pipelineInfo.pMultisampleState = &multisample;
    pipelineInfo.pColorBlendState = &blend;
    pipelineInfo.layout = chain.pipelineLayout;
    pipelineInfo.renderPass = chain.pass;
    const VkResult pipelineResult = VK_FN(device, vkCreateGraphicsPipelines)(device.device,
        VK_NULL_HANDLE, 1, &pipelineInfo, nullptr, &chain.pipeline);
    VK_FN(device, vkDestroyShaderModule)(device.device, vertex, nullptr);
    VK_FN(device, vkDestroyShaderModule)(device.device, fragment, nullptr);
    if (pipelineResult != VK_SUCCESS) return false;

    VkCommandPoolCreateInfo poolInfo{ VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    poolInfo.queueFamilyIndex = family;
    if (VK_FN(device, vkCreateCommandPool)(device.device, &poolInfo, nullptr, &chain.pool) != VK_SUCCESS) return false;
    std::vector<VkCommandBuffer> commands(count);
    VkCommandBufferAllocateInfo allocate{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    allocate.commandPool = chain.pool;
    allocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    allocate.commandBufferCount = count;
    if (VK_FN(device, vkAllocateCommandBuffers)(device.device, &allocate, commands.data()) != VK_SUCCESS) return false;
    VkFenceCreateInfo fenceInfo{ VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
    fenceInfo.flags = VK_FENCE_CREATE_SIGNALED_BIT;
    VkSemaphoreCreateInfo signalInfo{ VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
    for (uint32_t i = 0; i < count; ++i)
    {
        auto& frame = chain.frames[i];
        frame.command = commands[i];
        VkImageViewCreateInfo viewInfo{ VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
        viewInfo.image = frame.image;
        viewInfo.viewType = VK_IMAGE_VIEW_TYPE_2D;
        viewInfo.format = chain.format;
        viewInfo.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        viewInfo.subresourceRange.levelCount = viewInfo.subresourceRange.layerCount = 1;
        if (VK_FN(device, vkCreateImageView)(device.device, &viewInfo, nullptr, &frame.view) != VK_SUCCESS) return false;
        VkFramebufferCreateInfo framebufferInfo{ VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO };
        framebufferInfo.renderPass = chain.pass;
        framebufferInfo.attachmentCount = 1;
        framebufferInfo.pAttachments = &frame.view;
        framebufferInfo.width = chain.extent.width;
        framebufferInfo.height = chain.extent.height;
        framebufferInfo.layers = 1;
        if (VK_FN(device, vkCreateFramebuffer)(device.device, &framebufferInfo, nullptr, &frame.framebuffer) != VK_SUCCESS
            || VK_FN(device, vkCreateFence)(device.device, &fenceInfo, nullptr, &frame.fence) != VK_SUCCESS
            || VK_FN(device, vkCreateSemaphore)(device.device, &signalInfo, nullptr, &frame.signal) != VK_SUCCESS) return false;
    }
    VkDescriptorPoolSize poolSize{ VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 1 };
    VkDescriptorPoolCreateInfo descriptorPoolInfo{ VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO };
    descriptorPoolInfo.maxSets = 1;
    descriptorPoolInfo.poolSizeCount = 1;
    descriptorPoolInfo.pPoolSizes = &poolSize;
    if (VK_FN(device, vkCreateDescriptorPool)(device.device, &descriptorPoolInfo, nullptr, &chain.descriptorPool) != VK_SUCCESS) return false;
    VkDescriptorSetAllocateInfo descriptorInfo{ VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO };
    descriptorInfo.descriptorPool = chain.descriptorPool;
    descriptorInfo.descriptorSetCount = 1;
    descriptorInfo.pSetLayouts = &chain.descriptorLayout;
    if (VK_FN(device, vkAllocateDescriptorSets)(device.device, &descriptorInfo, &chain.descriptor) != VK_SUCCESS) return false;
    VkSamplerCreateInfo samplerInfo{ VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO };
    samplerInfo.magFilter = samplerInfo.minFilter = VK_FILTER_NEAREST;
    samplerInfo.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
    samplerInfo.addressModeU = samplerInfo.addressModeV = samplerInfo.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    samplerInfo.maxLod = 0;
    chain.ready = VK_FN(device, vkCreateSampler)(device.device, &samplerInfo, nullptr, &chain.sampler) == VK_SUCCESS;
    return chain.ready;
}

bool PrepareBitmap(const DeviceState& device, SwapchainState& chain, const OverlaySection::Bitmap& bitmap)
{
    if (chain.uploadedGeneration == bitmap.generation) return true;
    for (const auto& frame : chain.frames)
        if (VK_FN(device, vkWaitForFences)(device.device, 1, &frame.fence, VK_TRUE, UINT64_MAX) != VK_SUCCESS) return false;
    const VkDeviceSize bytes = VkDeviceSize(bitmap.stride) * bitmap.height;
    if (chain.texture && (chain.textureWidth != bitmap.width || chain.textureHeight != bitmap.height
        || chain.stagingSize < bytes)) DestroyBitmap(device, chain);
    if (!chain.texture)
    {
        VkImageCreateInfo imageInfo{ VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO };
        imageInfo.imageType = VK_IMAGE_TYPE_2D;
        imageInfo.format = VK_FORMAT_B8G8R8A8_UNORM;
        imageInfo.extent = { bitmap.width, bitmap.height, 1 };
        imageInfo.mipLevels = imageInfo.arrayLayers = 1;
        imageInfo.samples = VK_SAMPLE_COUNT_1_BIT;
        imageInfo.tiling = VK_IMAGE_TILING_OPTIMAL;
        imageInfo.usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
        imageInfo.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        if (VK_FN(device, vkCreateImage)(device.device, &imageInfo, nullptr, &chain.texture) != VK_SUCCESS) return false;
        VkMemoryRequirements imageMemory{};
        VK_FN(device, vkGetImageMemoryRequirements)(device.device, chain.texture, &imageMemory);
        const uint32_t imageType = MemoryType(device, imageMemory.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        if (imageType == UINT32_MAX) return false;
        VkMemoryAllocateInfo allocate{ VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
        allocate.allocationSize = imageMemory.size;
        allocate.memoryTypeIndex = imageType;
        if (VK_FN(device, vkAllocateMemory)(device.device, &allocate, nullptr, &chain.textureMemory) != VK_SUCCESS
            || VK_FN(device, vkBindImageMemory)(device.device, chain.texture, chain.textureMemory, 0) != VK_SUCCESS) return false;
        VkImageViewCreateInfo viewInfo{ VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
        viewInfo.image = chain.texture;
        viewInfo.viewType = VK_IMAGE_VIEW_TYPE_2D;
        viewInfo.format = VK_FORMAT_B8G8R8A8_UNORM;
        viewInfo.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        viewInfo.subresourceRange.levelCount = viewInfo.subresourceRange.layerCount = 1;
        if (VK_FN(device, vkCreateImageView)(device.device, &viewInfo, nullptr, &chain.textureView) != VK_SUCCESS) return false;

        VkBufferCreateInfo bufferInfo{ VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
        bufferInfo.size = bytes;
        bufferInfo.usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT;
        bufferInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
        if (VK_FN(device, vkCreateBuffer)(device.device, &bufferInfo, nullptr, &chain.staging) != VK_SUCCESS) return false;
        VkMemoryRequirements bufferMemory{};
        VK_FN(device, vkGetBufferMemoryRequirements)(device.device, chain.staging, &bufferMemory);
        const uint32_t bufferType = MemoryType(device, bufferMemory.memoryTypeBits,
            VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
        if (bufferType == UINT32_MAX) return false;
        allocate.allocationSize = bufferMemory.size;
        allocate.memoryTypeIndex = bufferType;
        if (VK_FN(device, vkAllocateMemory)(device.device, &allocate, nullptr, &chain.stagingMemory) != VK_SUCCESS
            || VK_FN(device, vkBindBufferMemory)(device.device, chain.staging, chain.stagingMemory, 0) != VK_SUCCESS) return false;
        chain.textureWidth = bitmap.width;
        chain.textureHeight = bitmap.height;
        chain.stagingSize = bytes;
        VkDescriptorImageInfo imageDescriptor{};
        imageDescriptor.sampler = chain.sampler;
        imageDescriptor.imageView = chain.textureView;
        imageDescriptor.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
        VkWriteDescriptorSet write{ VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET };
        write.dstSet = chain.descriptor;
        write.dstBinding = 0;
        write.descriptorCount = 1;
        write.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
        write.pImageInfo = &imageDescriptor;
        VK_FN(device, vkUpdateDescriptorSets)(device.device, 1, &write, 0, nullptr);
    }
    void* mapped = nullptr;
    if (VK_FN(device, vkMapMemory)(device.device, chain.stagingMemory, 0, bytes, 0, &mapped) != VK_SUCCESS) return false;
    std::memcpy(mapped, bitmap.pixels.data(), static_cast<size_t>(bytes));
    VK_FN(device, vkUnmapMemory)(device.device, chain.stagingMemory);
    return true;
}

bool SubmitOverlay(const DeviceState& device, VkQueue queue, SwapchainState& chain, uint32_t imageIndex,
    const OverlaySection::Bitmap& bitmap, const VkPresentInfoKHR& present, VkSemaphore& signal)
{
    if (imageIndex >= chain.frames.size() || !PrepareBitmap(device, chain, bitmap)) return false;
    auto& frame = chain.frames[imageIndex];
    if (VK_FN(device, vkWaitForFences)(device.device, 1, &frame.fence, VK_TRUE, UINT64_MAX) != VK_SUCCESS
        || VK_FN(device, vkResetCommandBuffer)(frame.command, 0) != VK_SUCCESS) return false;
    VkCommandBufferBeginInfo begin{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
    begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    if (VK_FN(device, vkBeginCommandBuffer)(frame.command, &begin) != VK_SUCCESS) return false;
    const bool upload = chain.uploadedGeneration != bitmap.generation;
    if (upload)
    {
        VkImageMemoryBarrier before{ VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
        before.srcAccessMask = chain.uploadedGeneration ? VK_ACCESS_SHADER_READ_BIT : 0;
        before.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        before.oldLayout = chain.uploadedGeneration ? VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL : VK_IMAGE_LAYOUT_UNDEFINED;
        before.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        before.srcQueueFamilyIndex = before.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        before.image = chain.texture;
        before.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        before.subresourceRange.levelCount = before.subresourceRange.layerCount = 1;
        VK_FN(device, vkCmdPipelineBarrier)(frame.command,
            chain.uploadedGeneration ? VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT : VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
            VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 1, &before);
        VkBufferImageCopy copy{};
        copy.bufferRowLength = bitmap.stride / 4;
        copy.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        copy.imageSubresource.layerCount = 1;
        copy.imageExtent = { bitmap.width, bitmap.height, 1 };
        VK_FN(device, vkCmdCopyBufferToImage)(frame.command, chain.staging, chain.texture,
            VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &copy);
        VkImageMemoryBarrier after = before;
        after.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        after.dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
        after.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        after.newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
        VK_FN(device, vkCmdPipelineBarrier)(frame.command, VK_PIPELINE_STAGE_TRANSFER_BIT,
            VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, 0, 0, nullptr, 0, nullptr, 1, &after);
    }
    VkRenderPassBeginInfo render{ VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO };
    render.renderPass = chain.pass;
    render.framebuffer = frame.framebuffer;
    render.renderArea.extent = chain.extent;
    VK_FN(device, vkCmdBeginRenderPass)(frame.command, &render, VK_SUBPASS_CONTENTS_INLINE);
    VK_FN(device, vkCmdBindPipeline)(frame.command, VK_PIPELINE_BIND_POINT_GRAPHICS, chain.pipeline);
    VK_FN(device, vkCmdBindDescriptorSets)(frame.command, VK_PIPELINE_BIND_POINT_GRAPHICS,
        chain.pipelineLayout, 0, 1, &chain.descriptor, 0, nullptr);
    const float sizes[4] = { float(bitmap.width), float(bitmap.height),
        float(chain.extent.width), float(chain.extent.height) };
    VK_FN(device, vkCmdPushConstants)(frame.command, chain.pipelineLayout,
        VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof(sizes), sizes);
    VK_FN(device, vkCmdDraw)(frame.command, 6, 1, 0, 0);
    VK_FN(device, vkCmdEndRenderPass)(frame.command);
    if (VK_FN(device, vkEndCommandBuffer)(frame.command) != VK_SUCCESS) return false;
    std::vector<VkPipelineStageFlags> waitStages(present.waitSemaphoreCount, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT);
    VkSubmitInfo submit{ VK_STRUCTURE_TYPE_SUBMIT_INFO };
    submit.waitSemaphoreCount = present.waitSemaphoreCount;
    submit.pWaitSemaphores = present.pWaitSemaphores;
    submit.pWaitDstStageMask = waitStages.data();
    submit.commandBufferCount = 1;
    submit.pCommandBuffers = &frame.command;
    submit.signalSemaphoreCount = 1;
    submit.pSignalSemaphores = &frame.signal;
    if (VK_FN(device, vkResetFences)(device.device, 1, &frame.fence) != VK_SUCCESS) return false;
    if (VK_FN(device, vkQueueSubmit)(queue, 1, &submit, frame.fence) != VK_SUCCESS)
    {
        VK_FN(device, vkDestroyFence)(device.device, frame.fence, nullptr);
        VkFenceCreateInfo fenceInfo{ VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
        fenceInfo.flags = VK_FENCE_CREATE_SIGNALED_BIT;
        VK_FN(device, vkCreateFence)(device.device, &fenceInfo, nullptr, &frame.fence);
        return false;
    }
    chain.uploadedGeneration = bitmap.generation;
    signal = frame.signal;
    section.Drew(bitmap.generation);
    return true;
}
}

LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(VkInstance, const char*);
LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL vkGetDeviceProcAddr(VkDevice, const char*);
LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL vk_layerGetPhysicalDeviceProcAddr(VkInstance, const char*);

LAYER_EXPORT VkResult VKAPI_CALL vkCreateInstance(const VkInstanceCreateInfo* info,
    const VkAllocationCallbacks* allocator, VkInstance* output)
{
    auto* link = reinterpret_cast<const VkLayerInstanceCreateInfo*>(info->pNext);
    while (link && (link->sType != VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO
        || link->function != VK_LAYER_LINK_INFO)) link = reinterpret_cast<const VkLayerInstanceCreateInfo*>(link->pNext);
    if (!link || !link->u.pLayerInfo) return VK_ERROR_INITIALIZATION_FAILED;
    const auto next = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    const auto nextPhysical = link->u.pLayerInfo->pfnNextGetPhysicalDeviceProcAddr;
    const auto create = reinterpret_cast<PFN_vkCreateInstance>(next(nullptr, "vkCreateInstance"));
    if (!create) return VK_ERROR_INITIALIZATION_FAILED;
    const_cast<VkLayerInstanceCreateInfo*>(link)->u.pLayerInfo = link->u.pLayerInfo->pNext;
    const VkResult result = create(info, allocator, output);
    if (result != VK_SUCCESS) return result;
    std::lock_guard<std::mutex> guard(stateMutex);
    instances[Key(*output)] = InstanceState{*output, next, nextPhysical};
    return result;
}

LAYER_EXPORT void VKAPI_CALL vkDestroyInstance(VkInstance instance, const VkAllocationCallbacks* allocator)
{
    if (!instance) return;
    const auto state = Instance(instance);
    if (!state.next) return;
    {
        std::lock_guard<std::mutex> guard(stateMutex);
        instances.erase(Key(instance));
    }
    reinterpret_cast<PFN_vkDestroyInstance>(state.next(instance, "vkDestroyInstance"))(instance, allocator);
}

LAYER_EXPORT VkResult VKAPI_CALL vkCreateDevice(VkPhysicalDevice physical, const VkDeviceCreateInfo* info,
    const VkAllocationCallbacks* allocator, VkDevice* output)
{
    const auto instance = Instance(physical);
    if (!instance.next) return VK_ERROR_INITIALIZATION_FAILED;
    auto* link = reinterpret_cast<const VkLayerDeviceCreateInfo*>(info->pNext);
    while (link && (link->sType != VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO
        || link->function != VK_LAYER_LINK_INFO)) link = reinterpret_cast<const VkLayerDeviceCreateInfo*>(link->pNext);
    if (!link || !link->u.pLayerInfo) return VK_ERROR_INITIALIZATION_FAILED;
    const auto next = link->u.pLayerInfo->pfnNextGetDeviceProcAddr;
    const auto create = reinterpret_cast<PFN_vkCreateDevice>(
        link->u.pLayerInfo->pfnNextGetInstanceProcAddr(instance.instance, "vkCreateDevice"));
    if (!create || !next) return VK_ERROR_INITIALIZATION_FAILED;
    const_cast<VkLayerDeviceCreateInfo*>(link)->u.pLayerInfo = link->u.pLayerInfo->pNext;
    const VkResult result = create(physical, info, allocator, output);
    if (result != VK_SUCCESS) return result;
    DeviceState state{};
    state.device = *output;
    state.next = next;
    auto memory = reinterpret_cast<PFN_vkGetPhysicalDeviceMemoryProperties>(
        instance.next(instance.instance, "vkGetPhysicalDeviceMemoryProperties"));
    auto families = reinterpret_cast<PFN_vkGetPhysicalDeviceQueueFamilyProperties>(
        instance.next(instance.instance, "vkGetPhysicalDeviceQueueFamilyProperties"));
    if (memory) memory(physical, &state.memory);
    if (families)
    {
        uint32_t count = 0;
        families(physical, &count, nullptr);
        std::vector<VkQueueFamilyProperties> properties(count);
        families(physical, &count, properties.data());
        for (const auto& property : properties) state.queueFlags.push_back(property.queueFlags);
    }
    std::lock_guard<std::mutex> guard(stateMutex);
    devices[Key(*output)] = std::move(state);
    return result;
}

LAYER_EXPORT void VKAPI_CALL vkDestroyDevice(VkDevice device, const VkAllocationCallbacks* allocator)
{
    if (!device) return;
    const auto state = Device(device);
    if (!state.next) return;
    {
        std::lock_guard<std::mutex> guard(stateMutex);
        for (auto found = swapchains.begin(); found != swapchains.end();)
        {
            if (found->second.device == device)
            {
                DestroyOverlay(state, found->second);
                found = swapchains.erase(found);
            }
            else ++found;
        }
        for (auto found = queueFamilies.begin(); found != queueFamilies.end();)
            if (Key(found->first) == Key(device)) found = queueFamilies.erase(found);
            else ++found;
        devices.erase(Key(device));
    }
    reinterpret_cast<PFN_vkDestroyDevice>(state.next(device, "vkDestroyDevice"))(device, allocator);
}

LAYER_EXPORT VkResult VKAPI_CALL vkCreateWin32SurfaceKHR(VkInstance instance,
    const VkWin32SurfaceCreateInfoKHR* info, const VkAllocationCallbacks* allocator, VkSurfaceKHR* output)
{
    const auto state = Instance(instance);
    if (!state.next) return VK_ERROR_INITIALIZATION_FAILED;
    const auto next = reinterpret_cast<PFN_vkCreateWin32SurfaceKHR>(state.next(instance, "vkCreateWin32SurfaceKHR"));
    if (!next) return VK_ERROR_EXTENSION_NOT_PRESENT;
    const VkResult result = next(instance, info, allocator, output);
    if (result == VK_SUCCESS)
    {
        std::lock_guard<std::mutex> guard(stateMutex);
        surfaces[*output] = info->hwnd;
    }
    return result;
}

LAYER_EXPORT void VKAPI_CALL vkDestroySurfaceKHR(VkInstance instance, VkSurfaceKHR surface,
    const VkAllocationCallbacks* allocator)
{
    const auto state = Instance(instance);
    if (!state.next) return;
    {
        std::lock_guard<std::mutex> guard(stateMutex);
        surfaces.erase(surface);
    }
    reinterpret_cast<PFN_vkDestroySurfaceKHR>(state.next(instance, "vkDestroySurfaceKHR"))(instance, surface, allocator);
}

LAYER_EXPORT VkResult VKAPI_CALL vkCreateSwapchainKHR(VkDevice device, const VkSwapchainCreateInfoKHR* info,
    const VkAllocationCallbacks* allocator, VkSwapchainKHR* output)
{
    const auto state = Device(device);
    if (!state.next) return VK_ERROR_INITIALIZATION_FAILED;
    const auto next = reinterpret_cast<PFN_vkCreateSwapchainKHR>(state.next(device, "vkCreateSwapchainKHR"));
    if (!next) return VK_ERROR_EXTENSION_NOT_PRESENT;
    const VkResult result = next(device, info, allocator, output);
    if (result == VK_SUCCESS)
    {
        std::lock_guard<std::mutex> guard(stateMutex);
        const auto surface = surfaces.find(info->surface);
        SwapchainState chain{};
        chain.device = device;
        chain.window = surface == surfaces.end() ? nullptr : surface->second;
        chain.extent = info->imageExtent;
        chain.format = info->imageFormat;
        chain.usage = info->imageUsage;
        swapchains[*output] = std::move(chain);
    }
    return result;
}

LAYER_EXPORT void VKAPI_CALL vkDestroySwapchainKHR(VkDevice device, VkSwapchainKHR swapchain,
    const VkAllocationCallbacks* allocator)
{
    const auto state = Device(device);
    if (!state.next) return;
    {
        std::lock_guard<std::mutex> guard(stateMutex);
        const auto found = swapchains.find(swapchain);
        if (found != swapchains.end())
        {
            DestroyOverlay(state, found->second);
            swapchains.erase(found);
        }
    }
    reinterpret_cast<PFN_vkDestroySwapchainKHR>(state.next(device, "vkDestroySwapchainKHR"))(device, swapchain, allocator);
}

LAYER_EXPORT void VKAPI_CALL vkGetDeviceQueue(VkDevice device, uint32_t family, uint32_t index, VkQueue* queue)
{
    const auto state = Device(device);
    if (!state.next) return;
    VK_FN(state, vkGetDeviceQueue)(device, family, index, queue);
    if (queue && *queue)
    {
        std::lock_guard<std::mutex> guard(stateMutex);
        queueFamilies[*queue] = family;
    }
}

LAYER_EXPORT void VKAPI_CALL vkGetDeviceQueue2(VkDevice device, const VkDeviceQueueInfo2* info, VkQueue* queue)
{
    const auto state = Device(device);
    if (!state.next) return;
    VK_FN(state, vkGetDeviceQueue2)(device, info, queue);
    if (info && queue && *queue)
    {
        std::lock_guard<std::mutex> guard(stateMutex);
        queueFamilies[*queue] = info->queueFamilyIndex;
    }
}

LAYER_EXPORT VkResult VKAPI_CALL vkQueuePresentKHR(VkQueue queue, const VkPresentInfoKHR* info)
{
    const auto state = Device(queue);
    if (!state.next) return VK_ERROR_INITIALIZATION_FAILED;
    VkSemaphore signal = VK_NULL_HANDLE;
    if (info && info->swapchainCount == 1 && info->pSwapchains && info->pImageIndices)
    {
        std::lock_guard<std::mutex> guard(stateMutex);
        const auto found = swapchains.find(info->pSwapchains[0]);
        if (found != swapchains.end())
        {
            auto& chain = found->second;
            auto bitmap = section.Frame(chain.window, chain.extent.width, chain.extent.height);
            const auto family = queueFamilies.find(queue);
            if (bitmap && family != queueFamilies.end() && family->second < state.queueFlags.size()
                && (state.queueFlags[family->second] & VK_QUEUE_GRAPHICS_BIT))
            {
                if (!chain.ready)
                {
                    DestroyOverlay(state, chain);
                    CreateRenderer(state, info->pSwapchains[0], chain, family->second);
                }
                if (chain.ready && chain.queueFamily == family->second)
                    SubmitOverlay(state, queue, chain, info->pImageIndices[0], *bitmap, *info, signal);
            }
        }
    }
    else if (info)
    {
        std::lock_guard<std::mutex> guard(stateMutex);
        for (uint32_t i = 0; i < info->swapchainCount; ++i)
        {
            const auto found = swapchains.find(info->pSwapchains[i]);
            if (found != swapchains.end())
                section.ObserveFrame(found->second.window, found->second.extent.width, found->second.extent.height);
        }
    }
    const auto next = VK_FN(state, vkQueuePresentKHR);
    if (!signal) return next(queue, info);
    VkPresentInfoKHR forwarded = *info;
    forwarded.waitSemaphoreCount = 1;
    forwarded.pWaitSemaphores = &signal;
    return next(queue, &forwarded);
}

LAYER_EXPORT VkResult VKAPI_CALL vkEnumerateInstanceLayerProperties(uint32_t* count, VkLayerProperties* output)
{
    if (!count) return VK_ERROR_INITIALIZATION_FAILED;
    if (!output) { *count = 1; return VK_SUCCESS; }
    if (!*count) return VK_INCOMPLETE;
    *count = 1;
    VkLayerProperties properties{};
    std::strcpy(properties.layerName, LayerName);
    std::strcpy(properties.description, "Resource Manager performance overlay");
    properties.specVersion = VK_API_VERSION_1_4;
    properties.implementationVersion = 1;
    *output = properties;
    return VK_SUCCESS;
}

LAYER_EXPORT VkResult VKAPI_CALL vkEnumerateInstanceExtensionProperties(const char* layer,
    uint32_t* count, VkExtensionProperties*)
{
    if (!layer || std::strcmp(layer, LayerName) != 0) return VK_ERROR_LAYER_NOT_PRESENT;
    if (!count) return VK_ERROR_INITIALIZATION_FAILED;
    *count = 0;
    return VK_SUCCESS;
}

LAYER_EXPORT VkResult VKAPI_CALL vkEnumerateDeviceExtensionProperties(VkPhysicalDevice physical,
    const char* layer, uint32_t* count, VkExtensionProperties* output)
{
    if (layer && std::strcmp(layer, LayerName) == 0) { if (!count) return VK_ERROR_INITIALIZATION_FAILED; *count = 0; return VK_SUCCESS; }
    const auto state = Instance(physical);
    if (!state.next) return VK_ERROR_INITIALIZATION_FAILED;
    return reinterpret_cast<PFN_vkEnumerateDeviceExtensionProperties>(
        state.next(state.instance, "vkEnumerateDeviceExtensionProperties"))(physical, layer, count, output);
}

LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(VkInstance instance, const char* name)
{
    if (!name) return nullptr;
#define GLOBAL_PROC(fn) if (std::strcmp(name, #fn) == 0) return reinterpret_cast<PFN_vkVoidFunction>(fn)
    GLOBAL_PROC(vkGetInstanceProcAddr);
    GLOBAL_PROC(vkCreateInstance);
    GLOBAL_PROC(vkEnumerateInstanceLayerProperties);
    GLOBAL_PROC(vkEnumerateInstanceExtensionProperties);
    if (!instance) return nullptr;
    const auto state = Instance(instance);
    if (!state.next) return nullptr;
    const auto next = state.next(instance, name);
    if (!next) return nullptr;
#define INSTANCE_PROC(fn) if (std::strcmp(name, #fn) == 0) return reinterpret_cast<PFN_vkVoidFunction>(fn)
    INSTANCE_PROC(vkGetDeviceProcAddr);
    INSTANCE_PROC(vkDestroyInstance);
    INSTANCE_PROC(vkCreateDevice);
    INSTANCE_PROC(vkDestroyDevice);
    INSTANCE_PROC(vkGetDeviceQueue);
    INSTANCE_PROC(vkGetDeviceQueue2);
    INSTANCE_PROC(vkEnumerateDeviceExtensionProperties);
    INSTANCE_PROC(vkCreateWin32SurfaceKHR);
    INSTANCE_PROC(vkDestroySurfaceKHR);
    INSTANCE_PROC(vkCreateSwapchainKHR);
    INSTANCE_PROC(vkDestroySwapchainKHR);
    INSTANCE_PROC(vkQueuePresentKHR);
#undef INSTANCE_PROC
#undef GLOBAL_PROC
    return next;
}

LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL vkGetDeviceProcAddr(VkDevice device, const char* name)
{
    if (!device || !name) return nullptr;
    const auto state = Device(device);
    if (!state.next) return nullptr;
    const auto next = state.next(device, name);
    if (!next) return nullptr;
#define DEVICE_PROC(fn) if (std::strcmp(name, #fn) == 0) return reinterpret_cast<PFN_vkVoidFunction>(fn)
    DEVICE_PROC(vkGetDeviceProcAddr);
    DEVICE_PROC(vkDestroyDevice);
    DEVICE_PROC(vkGetDeviceQueue);
    DEVICE_PROC(vkGetDeviceQueue2);
    DEVICE_PROC(vkCreateSwapchainKHR);
    DEVICE_PROC(vkDestroySwapchainKHR);
    DEVICE_PROC(vkQueuePresentKHR);
#undef DEVICE_PROC
    return next;
}

LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL vk_layerGetPhysicalDeviceProcAddr(VkInstance instance, const char* name)
{
    if (!instance || !name) return nullptr;
    const auto state = Instance(instance);
    if (!state.nextPhysical) return nullptr;
    if (std::strcmp(name, "vkEnumerateDeviceExtensionProperties") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(vkEnumerateDeviceExtensionProperties);
    return state.nextPhysical(instance, name);
}

LAYER_EXPORT VkResult VKAPI_CALL vkNegotiateLoaderLayerInterfaceVersion(VkNegotiateLayerInterface* version)
{
    if (!version || version->sType != LAYER_NEGOTIATE_INTERFACE_STRUCT || version->loaderLayerInterfaceVersion < 2)
        return VK_ERROR_INITIALIZATION_FAILED;
    version->loaderLayerInterfaceVersion = 2;
    version->pfnGetInstanceProcAddr = vkGetInstanceProcAddr;
    version->pfnGetDeviceProcAddr = vkGetDeviceProcAddr;
    version->pfnGetPhysicalDeviceProcAddr = vk_layerGetPhysicalDeviceProcAddr;
    return VK_SUCCESS;
}
