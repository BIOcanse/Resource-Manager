using System.Diagnostics.CodeAnalysis;

// Ledger ships RML001 (a lease may live only in one code scope) as an error since 0.1.0-dev.5.
// These three adapters inside the product Ledger owner keep an SDK lease beyond one code
// scope exactly as before the dev.5 pin; the Ledger pins leave their behavior unchanged.
// Delete a line when its type moves to the scoped-lease template, and the file when all
// three have. RML001 still rejects new lease storage everywhere else in the assembly.
[assembly: SuppressMessage("Lifetime", "RML001", Scope = "type", Target = "~T:ResourceManager.App.Infrastructure.Adaptation.ResourceManagerLedgerOwner.Region", Justification = "Pending scoped-lease migration of the product Ledger owner; unchanged by the Ledger pin.")]
[assembly: SuppressMessage("Lifetime", "RML001", Scope = "type", Target = "~T:ResourceManager.App.Infrastructure.Adaptation.ResourceManagerLedgerOwner.Reader", Justification = "Pending scoped-lease migration of the product Ledger owner; unchanged by the Ledger pin.")]
[assembly: SuppressMessage("Lifetime", "RML001", Scope = "type", Target = "~T:ResourceManager.App.Infrastructure.Adaptation.ResourceManagerLedgerOwner.ScratchLease", Justification = "Pending scoped-lease migration of the product Ledger owner; unchanged by the Ledger pin.")]
