using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;

namespace PluginSidecar.Business;

public sealed record PluginAccountEligibility(bool Allowed, bool CheckSession);
public sealed record PluginAccountEffects(
    Func<int, long, CancellationToken, ValueTask<PluginAccountEligibility>> Eligibility,
    Func<int, long, CancellationToken, ValueTask<Nexa.Xsr.XsrResult>> Refresh);

public sealed partial class PluginBusinessRoutes
{
    private PluginAccountEffects? _accountEffects;
    public void BindAccountLaunch(PluginAccountEffects effects) => _accountEffects = effects;
    private sealed record GrantedLaunch(PluginIdentity Owner, LaunchGrant Public, EntityId Instance, long RosterRevision, int AccountIndex, DateTimeOffset Expires);
    private readonly Dictionary<Guid, GrantedLaunch> _launchGrants = [];
    private async ValueTask<LaunchEligibility> LaunchEligibility(HostInvocation c, EntityId id, CancellationToken token)
    {
        var account = FindAccount(c, id);
        var effects = _accountEffects ?? throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        var eligibility = await effects.Eligibility(account.Profile.Index, account.Revision, token).ConfigureAwait(false);
        bool allowed = eligibility.Allowed;
        ImmutableArray<ValidationIssue> reasons = !allowed ? [Issue("SIGN_IN_REQUIRED", "This account needs a supported sign-in before launch.", id.Value)]
            : !eligibility.CheckSession ? [] : [Issue("SESSION_CHECK_AT_LAUNCH", "The account provider will validate or refresh this session during launch.", id.Value, false)];
        return new(id, allowed, reasons);
    }
    private GrantedLaunch FindGrant(HostInvocation c, LaunchGrant reference, EntityId instance)
    {
        lock (_planGate)
        {
            if (!_launchGrants.TryGetValue(reference.Token, out var grant) || grant.Owner != c.Identity || grant.Public != reference || grant.Instance != instance)
                throw new AbiHostException(AbiErrors.HANDLE_INVALID);
            if (grant.Expires <= DateTimeOffset.UtcNow || FindAccount(c, reference.Account).Revision != grant.RosterRevision) throw new AbiHostException(AbiErrors.CONFLICT);
            FindInstance(c, instance); return grant;
        }
    }
    private IEnumerable<HostCapabilityRoute> AccountLaunchRoutes => [
        HostRoutes.Typed<EntityRequest, LaunchEligibility>("Accounts", "ReadLaunchEligibility", (c, v) => LaunchEligibility(c, v.Id, c.Options.Cancellation)),
        HostRoutes.Typed<GrantRequest, LaunchGrant>("Accounts", "RequestLaunchGrant", async (c, v) =>
        {
            FindInstance(c, v.Instance);
            if (!(await LaunchEligibility(c, v.Account, c.Options.Cancellation).ConfigureAwait(false)).Allowed) throw new AbiHostException(AbiErrors.PERMISSION_DENIED);
            var account = FindAccount(c, v.Account);
            lock (_planGate)
            {
                foreach (var key in _launchGrants.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) _launchGrants.Remove(key);
                if (_launchGrants.Count >= 256) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
                var reference = new LaunchGrant(Guid.NewGuid(), c.Identity.LoadInstanceId, v.Account);
                _launchGrants.Add(reference.Token, new(c.Identity, reference, v.Instance, account.Revision, account.Profile.Index, DateTimeOffset.UtcNow.AddMinutes(10)));
                return reference;
            }
        }),
        HostRoutes.Typed<EntityRequest, OperationRef>("Accounts", "Refresh", async (c, v) =>
        {
            var account = FindAccount(c, v.Id);
            var effects = _accountEffects ?? throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
            return Services.StartHostOperation(c, "nexa.accounts.write", Abi100Schemas.Empty, async token =>
            {
                await Confirm(c, "accounts/refresh", "Refresh sign-in for " + account.Profile.Username, EntityRequest.Write(v), token).ConfigureAwait(false);
                FindAccount(c, v.Id);
                var result = await effects.Refresh(account.Profile.Index, account.Revision, token).ConfigureAwait(false);
                if (!result.IsSuccess) throw new AbiHostException(ErrorCode(result.Error));
                return AbiReply<AbiValue>.Ok(Empty.Write(new()));
            });
        })
    ];
}
