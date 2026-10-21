using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

// Operation-local immutable selection only. The original tracker owns the leases and demand.
internal static class NvidiaDrsInformationBatch
{
    internal static ImmutableArray<NvidiaDrsInformationRequest> Create(IReadOnlyList<NvidiaDrsInformationRequest> requests)
    {
        var groups = new List<List<NvidiaDrsInformationRequest>>();
        foreach (var request in requests)
        {
            if (request.Fields.IsEmpty) continue;
            var group = groups.FirstOrDefault(items => items[0].Limits == request.Limits);
            if (group is null) { group = []; groups.Add(group); }
            if (!group.Any(prior => prior.Fields.ToHashSet().SetEquals(request.Fields))) group.Add(request);
        }
        var result = ImmutableArray.CreateBuilder<NvidiaDrsInformationRequest>();
        foreach (var group in groups)
        {
            if (TryUnion(group, out var combined)) result.Add(combined!);
            else result.AddRange(group); // Keep the original requests; no partial union or budget changes.
        }
        return result.ToImmutable();
    }

    private static bool TryUnion(List<NvidiaDrsInformationRequest> group, out NvidiaDrsInformationRequest? combined)
    {
        combined = null;
        var limits = group[0].Limits;
        var profiles = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
        ulong total = 0;
        foreach (var request in group)
            foreach (var profile in request.Profiles)
            {
                if (!profiles.TryGetValue(profile.ProfileName, out var ids))
                {
                    if ((ulong)profiles.Count >= limits.MaximumProfileCount) return false;
                    ids = []; profiles.Add(profile.ProfileName, ids);
                }
                foreach (var id in profile.SettingIds)
                {
                    if (!ids.Add(id)) continue;
                    total++;
                    if ((ulong)ids.Count > limits.MaximumSettingsPerProfile || total > limits.MaximumTotalSettingCount
                        || (ulong)profiles.Count * 4116UL + total * 12320UL > limits.MaximumPayloadBytes) return false;
                }
            }
        combined = new([.. profiles.Select(profile => new NvidiaDrsProfileRequest(profile.Key, profile.Value.Order().ToImmutableArray()))], limits);
        return true;
    }
}
