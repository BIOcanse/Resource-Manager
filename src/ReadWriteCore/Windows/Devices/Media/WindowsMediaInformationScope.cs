using System.Collections.Immutable;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Media;

[Flags]
public enum WindowsMediaInformationFields
{
    None = 0, AudioEndpointId = 1, AudioDefaultRoles = 2, AudioMixFormat = 4, AudioDriverVersion = 8,
    CodecMediaTypes = 16, CodecVersion = 32,
    Audio = AudioEndpointId | AudioDefaultRoles | AudioMixFormat | AudioDriverVersion,
    Codec = CodecMediaTypes | CodecVersion, All = Audio | Codec
}
public enum WindowsAudioDataFlow { Render = 0, Capture = 1 }
public enum WindowsAudioDefaultRole { Console = 0, Multimedia = 1, Communications = 2 }
public enum WindowsMftCategory { AudioDecoder, AudioEncoder, VideoDecoder, VideoEncoder }
public enum WindowsDirectShowCategory
{
    RegisteredFilters,
    AudioCompressors,
    VideoCompressors,
    AudioCaptureSources,
    VideoCaptureSources
}
public enum WindowsCodecRegistryView { Registry32, Registry64 }

/// <summary>Explicit local caller context, direction/state mask, MFT and DirectShow
/// categories, and COM registry view. Does not discover or activate anything on construction.</summary>
public sealed record WindowsMediaInformationScope
{
    public WindowsMediaInformationFields Fields { get; }
    public ImmutableArray<WindowsAudioDataFlow> AudioDirections { get; }
    public uint AudioStateMask { get; }
    public ImmutableArray<WindowsMftCategory> CodecCategories { get; }
    public ImmutableArray<WindowsDirectShowCategory> DirectShowCategories { get; }
    public WindowsCodecRegistryView CodecRegistryView { get; }
    public int MaximumObjectsPerCategory { get; }
    public WindowsMediaInformationScope(WindowsMediaInformationFields fields,
        IEnumerable<WindowsAudioDataFlow> audioDirections, uint audioStateMask,
        IEnumerable<WindowsMftCategory> codecCategories, int maximumObjectsPerCategory = 512,
        IEnumerable<WindowsDirectShowCategory>? directShowCategories = null,
        WindowsCodecRegistryView codecRegistryView = WindowsCodecRegistryView.Registry64)
    {
        if (fields == WindowsMediaInformationFields.None || (fields & ~WindowsMediaInformationFields.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));
        ArgumentNullException.ThrowIfNull(audioDirections); ArgumentNullException.ThrowIfNull(codecCategories);
        AudioDirections = [.. audioDirections.Distinct().Order()]; CodecCategories = [.. codecCategories.Distinct().Order()];
        DirectShowCategories = [.. (directShowCategories ?? []).Distinct().Order()];
        if ((fields & WindowsMediaInformationFields.Audio) != 0 && AudioDirections.IsEmpty
            || AudioDirections.Any(static direction => direction is not (WindowsAudioDataFlow.Render or WindowsAudioDataFlow.Capture)))
            throw new ArgumentException("Select explicit audio directions.", nameof(audioDirections));
        if ((fields & WindowsMediaInformationFields.Audio) != 0
            && (audioStateMask == 0 || (audioStateMask & ~15U) != 0))
            throw new ArgumentOutOfRangeException(nameof(audioStateMask));
        if ((fields & WindowsMediaInformationFields.Codec) != 0 && CodecCategories.IsEmpty && DirectShowCategories.IsEmpty
            || CodecCategories.Any(static category => !Enum.IsDefined(category)))
            throw new ArgumentException("Select explicit Media Foundation or DirectShow codec categories.", nameof(codecCategories));
        if (DirectShowCategories.Any(static category => !Enum.IsDefined(category)))
            throw new ArgumentException("Select explicit DirectShow categories.", nameof(directShowCategories));
        if (!Enum.IsDefined(codecRegistryView)) throw new ArgumentOutOfRangeException(nameof(codecRegistryView));
        if (maximumObjectsPerCategory is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumObjectsPerCategory));
        Fields = fields; AudioStateMask = audioStateMask; MaximumObjectsPerCategory = maximumObjectsPerCategory;
        CodecRegistryView = codecRegistryView;
    }
    internal WindowsMediaInformationFields Select(ImmutableArray<InformationFieldSelection> selection)
    {
        var fields = WindowsMediaInformationFields.None;
        foreach (var item in selection)
        {
            var field = DefinitionField(item.Definition);
            if (item.Field.FieldName != item.Definition.Name || !ReferenceEquals(item.Definition, DefinitionFor(field)))
                throw new ArgumentException("Media selection must use the exact static field definition.", nameof(selection));
            if ((Fields & field) == 0) throw new ArgumentException("The selected media field is outside this scope.", nameof(selection));
            fields |= field;
        }
        return fields;
    }

    internal ImmutableArray<DeviceInformationDefinition> Definitions
    {
        get
        {
            var definitions = ImmutableArray.CreateBuilder<DeviceInformationDefinition>();
            foreach (var selectedField in new[] {
                WindowsMediaInformationFields.AudioEndpointId,
                WindowsMediaInformationFields.AudioDefaultRoles,
                WindowsMediaInformationFields.AudioMixFormat,
                WindowsMediaInformationFields.AudioDriverVersion,
                WindowsMediaInformationFields.CodecMediaTypes,
                WindowsMediaInformationFields.CodecVersion })
                if ((Fields & selectedField) != 0) definitions.Add(DefinitionFor(selectedField));
            return definitions.ToImmutable();
        }
    }

    internal bool HasAudio => (Fields & WindowsMediaInformationFields.Audio) != 0;
    internal bool HasCodec => (Fields & WindowsMediaInformationFields.Codec) != 0;
    internal const WindowsMediaInformationFields AudioEndpointFields =
        WindowsMediaInformationFields.AudioEndpointId | WindowsMediaInformationFields.AudioDefaultRoles
        | WindowsMediaInformationFields.AudioMixFormat;

    internal static string DirectionCode(WindowsAudioDataFlow direction) => direction switch
    {
        WindowsAudioDataFlow.Render => "render",
        WindowsAudioDataFlow.Capture => "capture",
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    internal static string RoleCode(WindowsAudioDefaultRole role) => role switch
    {
        WindowsAudioDefaultRole.Console => "console",
        WindowsAudioDefaultRole.Multimedia => "multimedia",
        WindowsAudioDefaultRole.Communications => "communications",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    internal static string CategoryCode(WindowsMftCategory category) => category switch
    {
        WindowsMftCategory.AudioDecoder => "audio-decoder",
        WindowsMftCategory.AudioEncoder => "audio-encoder",
        WindowsMftCategory.VideoDecoder => "video-decoder",
        WindowsMftCategory.VideoEncoder => "video-encoder",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    internal static string DirectShowCategoryCode(WindowsDirectShowCategory category) => category switch
    {
        WindowsDirectShowCategory.RegisteredFilters => "registered-filters",
        WindowsDirectShowCategory.AudioCompressors => "audio-compressors",
        WindowsDirectShowCategory.VideoCompressors => "video-compressors",
        WindowsDirectShowCategory.AudioCaptureSources => "audio-capture-sources",
        WindowsDirectShowCategory.VideoCaptureSources => "video-capture-sources",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    internal static string RegistryViewCode(WindowsCodecRegistryView view) => view switch
    {
        WindowsCodecRegistryView.Registry32 => "registry32",
        WindowsCodecRegistryView.Registry64 => "registry64",
        _ => throw new ArgumentOutOfRangeException(nameof(view))
    };

    private static WindowsMediaInformationFields DefinitionField(DeviceInformationDefinition definition)
    {
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.AudioAllEndpointIdDeclared)) return WindowsMediaInformationFields.AudioEndpointId;
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.AudioAllDefaultRoleActual)) return WindowsMediaInformationFields.AudioDefaultRoles;
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.AudioAllMixFormatConfigured)) return WindowsMediaInformationFields.AudioMixFormat;
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.AudioAllDriverVersionDeclared)) return WindowsMediaInformationFields.AudioDriverVersion;
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.CodecAllMediaTypeCapability)) return WindowsMediaInformationFields.CodecMediaTypes;
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.CodecAllVersionDeclared)) return WindowsMediaInformationFields.CodecVersion;
        throw new ArgumentException("Unknown media information definition.", nameof(definition));
    }

    private static DeviceInformationDefinition DefinitionFor(WindowsMediaInformationFields field) => field switch
    {
        WindowsMediaInformationFields.AudioEndpointId => DeviceInformationFieldRegistry.AudioAllEndpointIdDeclared,
        WindowsMediaInformationFields.AudioDefaultRoles => DeviceInformationFieldRegistry.AudioAllDefaultRoleActual,
        WindowsMediaInformationFields.AudioMixFormat => DeviceInformationFieldRegistry.AudioAllMixFormatConfigured,
        WindowsMediaInformationFields.AudioDriverVersion => DeviceInformationFieldRegistry.AudioAllDriverVersionDeclared,
        WindowsMediaInformationFields.CodecMediaTypes => DeviceInformationFieldRegistry.CodecAllMediaTypeCapability,
        WindowsMediaInformationFields.CodecVersion => DeviceInformationFieldRegistry.CodecAllVersionDeclared,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    internal static Guid CategoryId(WindowsMftCategory category) => category switch
    {
        WindowsMftCategory.AudioDecoder => new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7"),
        WindowsMftCategory.AudioEncoder => new("91c64bd0-f91e-4d8c-9276-db248279d975"),
        WindowsMftCategory.VideoDecoder => new("d6c02d4b-6833-45b4-971a-05a4b04bab91"),
        WindowsMftCategory.VideoEncoder => new("f79eac7d-e545-4387-bdee-d647d7bde42a"),
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    internal static Guid DirectShowCategoryId(WindowsDirectShowCategory category) => category switch
    {
        WindowsDirectShowCategory.RegisteredFilters => new("083863f1-70de-11d0-bd40-00a0c911ce86"),
        WindowsDirectShowCategory.AudioCompressors => new("33d9a761-90c8-11d0-bd43-00a0c911ce86"),
        WindowsDirectShowCategory.VideoCompressors => new("33d9a760-90c8-11d0-bd43-00a0c911ce86"),
        WindowsDirectShowCategory.AudioCaptureSources => new("33d9a762-90c8-11d0-bd43-00a0c911ce86"),
        WindowsDirectShowCategory.VideoCaptureSources => new("860bb310-5d01-11d0-bd3b-00a0c911ce86"),
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
}
