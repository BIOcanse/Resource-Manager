namespace ResourceManager.Ledger;

/// <summary>
/// Declares an owning lease scope on an adapter or its disposing interface contract.
/// The analyzer propagates this contract through base classes and implemented/inherited interfaces.
/// The adapter must implement IDisposable or IAsyncDisposable and return its owned leases on disposal.
/// Only private underlying lease fields/captures are permitted; the adapter itself cannot escape into
/// application storage. This assertion does not prove disposal correctness or authorize non-owning views.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, AllowMultiple = false, Inherited = true)]
public sealed class LedgerLeaseScopeAttribute : Attribute { }
