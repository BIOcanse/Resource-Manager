using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace ResourceManager.App.Application.ProcessAttribution;

// Part of the existing per-instance attribution entry, independent of its
// catalog match. Catalog/service changes do not repeat fixed process reads.
internal sealed record RuntimeProcessAttributionFixedInputs(
    IRuntimePackageIdentityResolver PackageSource,
    object PackageVersion,
    ProcessInstanceKey Instance,
    RuntimeAttributionObservation Package,
    IRuntimeRootIdentityResolver RootSource,
    object RootVersion,
    RuntimeAttributionObservation Root,
    IRuntimeSystemProcessClassifier SystemSource,
    object SystemVersion,
    RuntimeAttributionObservation System);

