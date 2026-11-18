using Xunit;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// Test classes that park a writer inside <c>UnitOfWork</c>'s process-wide
/// write lock, or block a thread on a barrier, while asserting how a second
/// operation behaves within a fixed wait. The lock is static, so a test class
/// running in parallel that holds it behind its own barrier can delay this
/// class's writer past its wait on a busy runner. The collection runs alone,
/// after the parallel classes, so its waits measure only its own operations.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWriteLockCollection
{
    public const string Name = "Process write lock";
}
