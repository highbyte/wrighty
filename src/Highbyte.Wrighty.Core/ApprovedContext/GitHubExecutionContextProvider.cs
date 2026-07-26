using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;
using Highbyte.Wrighty.Time;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// Resolves whether a GitHub actor may decide what an agent sees.
///
/// Implementations answer two related questions: whether an actor's reaction carries an
/// include/exclude decision, and whether an actor's comment may be recognised as one of Wrighty's
/// own protocol comments and therefore hidden from the agent.
/// </summary>
public interface IContextApproverPolicy
{
    /// <summary>Whether this actor's decision reactions count.</summary>
    bool IsApprover(string? actor);

    /// <summary>Whether this actor's marker-bearing comments may be excluded from task context.</summary>
    bool CanExcludeContent(string? actor);
}

/// <summary>
/// The policy in force until actor authorization is implemented.
///
/// It authorises nobody, which makes every reaction inert and leaves comments needing an explicit
/// decision pending. That is deliberately useless rather than permissive: resolving an actor
/// against repository permission, exact role, explicit user, and team membership needs live
/// verification that has not been completed, and a policy that guessed "yes" would let any
/// passer-by decide what an unattended agent reads.
///
/// Configuration that asks for approver sources must be rejected while this is in force, rather
/// than silently accepted and ignored.
/// </summary>
public sealed class UnavailableApproverPolicy : IContextApproverPolicy
{
    public static UnavailableApproverPolicy Instance { get; } = new();

    public bool IsApprover(string? actor) => false;

    public bool CanExcludeContent(string? actor) => false;
}

/// <summary>
/// Assembles an approved context from a GitHub issue: reads the whole conversation, reads the
/// approval field, and resolves the two into a snapshot.
/// </summary>
public sealed class GitHubExecutionContextProvider(
    GitHubConversationReader conversations,
    GitHubContextApprovalReader approvals,
    GitHubWorkItemAddressResolver addresses,
    IContextApproverPolicy? approverPolicy = null,
    DecisionPolicy? decisionPolicy = null,
    IClock? clock = null) : IExecutionContextProvider
{
    private readonly IContextApproverPolicy approverPolicy =
        approverPolicy ?? UnavailableApproverPolicy.Instance;
    private readonly IClock clock = clock ?? new SystemClock();

    public async Task<ExecutionContextResult> GetAsync(
        TrackerConfig config,
        WorkItemId id,
        ContextReadPurpose purpose,
        ContextLimits limits,
        CancellationToken cancellationToken)
    {
        GitHubWorkItemAddress address;
        try
        {
            address = addresses.Decode(id, config);
        }
        catch (TrackerException exception)
        {
            return ExecutionContextResult.Refused(
                ExecutionContextResult.Codes.ReadFailed,
                $"'{id}' could not be resolved to a GitHub issue ({exception.Code}).");
        }

        try
        {
            // The approval is read FIRST and the conversation second, so a comment arriving between
            // the two reads is seen by the comment read and lands after the cutoff — pending, and
            // blocking. Reading them the other way round would let a comment slip in behind an
            // approval that never covered it.
            var approval = await approvals.ReadAsync(
                config, address.Owner, address.Repository, address.IssueNumber, cancellationToken);

            var conversation = await conversations.ReadAsync(
                address.Host, address.Owner, address.Repository, address.IssueNumber,
                cancellationToken);

            var resolver = new ApprovedContextResolver(
                approverPolicy.IsApprover,
                approverPolicy.CanExcludeContent,
                decisionPolicy);

            return resolver.Resolve(id, conversation, approval, limits, clock.UtcNow);
        }
        catch (TrackerException exception)
        {
            // A partial read is never turned into a partial context. Whatever went wrong, the
            // answer is that this item's approved content could not be established.
            return ExecutionContextResult.Refused(
                ExecutionContextResult.Codes.ReadFailed,
                $"The conversation for '{id}' could not be read completely ({exception.Code}).");
        }
    }
}
