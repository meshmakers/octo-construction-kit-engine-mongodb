using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;
using System.Security.Claims;
using Meshmakers.Octo.Backend.Persistence.SystemEntities;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Microsoft.AspNetCore.Identity;
using Persistence.IdentityCkModel.CkModelEntities;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public class OctoUserStore :
    IUserClaimStore<RtIdentityUser>,
    IUserStore<RtIdentityUser>,
    IDisposable,
    IUserLoginStore<RtIdentityUser>,
    IUserRoleStore<RtIdentityUser>,
    IUserPasswordStore<RtIdentityUser>,
    IUserSecurityStampStore<RtIdentityUser>,
    IUserEmailStore<RtIdentityUser>,
    IUserPhoneNumberStore<RtIdentityUser>,
    IQueryableUserStore<RtIdentityUser>,
    IUserTwoFactorStore<RtIdentityUser>,
    IUserLockoutStore<RtIdentityUser>,
    IUserAuthenticatorKeyStore<RtIdentityUser>,
    IUserAuthenticationTokenStore<RtIdentityUser>,
    IUserTwoFactorRecoveryCodeStore<RtIdentityUser>,
    IProtectedUserStore<RtIdentityUser>

{
    private readonly ITenantContext _tenantContext;
    private static readonly InsertOneOptions InsertOneOptions = new InsertOneOptions();
    private static readonly MongoDB.Driver.FindOptions<RtIdentityUser> FindOptions = new MongoDB.Driver.FindOptions<RtIdentityUser>();
    private static readonly ReplaceOptions ReplaceOptions = new ReplaceOptions();
    private const string InternalLoginProvider = "[AspNeRtIdentityUserStore]";
    private const string AuthenticatorKeyTokenName = "AuthenticatorKey";
    private const string RecoveryCodeTokenName = "RecoveryCodes";
    private bool _disposed;

    public IdentityErrorDescriber ErrorDescriber { get; set; }

    public OctoUserStore(
        ITenantContext tenantContext,
        IdentityErrorDescriber describer)
    {
        _tenantContext = tenantContext;
        ErrorDescriber = describer ?? new IdentityErrorDescriber();
    }

    public async Task SetTokenAsync(
        RtIdentityUser user,
        string loginProvider,
        string name,
        string value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        IdentityUserToken<string>? token = await FindTokenAsync(user, loginProvider, name, cancellationToken);
        if (token == null)
        {
            user.Tokens.Add(new IdentityUserToken<string>()
            {
                UserId = user.Id.ToString(),
                LoginProvider = loginProvider,
                Name = name,
                Value = value
            });
        }
        else
        {
            token.Value = value;
            user.Tokens[
                user.Tokens.FindIndex(
                    (Predicate<IdentityUserToken<string>>)(x => x.LoginProvider == token.LoginProvider && x.Name == token.Name))] = token;
        }
    }

    public async Task RemoveTokenAsync(
        RtIdentityUser user,
        string loginProvider,
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        IdentityUserToken<string> entry = await FindTokenAsync(user, loginProvider, name, cancellationToken);
        if (entry == null)
            ;
        else
            user.Tokens.RemoveAll(
                (Predicate<IdentityUserToken<string>>)(x => x.LoginProvider == entry.LoginProvider && x.Name == entry.Name));
    }

    public async Task<string> GetTokenAsync(
        RtIdentityUser user,
        string loginProvider,
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        return (await FindTokenAsync(user, loginProvider, name, cancellationToken))?.Value;
    }

    public Task<string> GetAuthenticatorKeyAsync(RtIdentityUser user, CancellationToken cancellationToken = default) =>
        GetTokenAsync(user, "[AspNeRtIdentityUserStore]", "AuthenticatorKey", cancellationToken);

    public Task SetAuthenticatorKeyAsync(
        RtIdentityUser user,
        string key,
        CancellationToken cancellationToken = default)
    {
        return SetTokenAsync(user, "[AspNeRtIdentityUserStore]", "AuthenticatorKey", key, cancellationToken);
    }

    public async Task<IdentityResult> CreateAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        await _userCollection.InsertOneAsync(user, UserStore<RtIdentityUser, TRole, TKey>.InsertOneOptions, cancellationToken)
            .ConfigureAwait(false);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        DeleteResult deleteResult = await _userCollection
            .DeleteOneAsync<RtIdentityUser>(
                (Expression<Func<RtIdentityUser, bool>>)(x => x.Id.Equals(user.Id) && x.ConcurrencyStamp.Equals(user.ConcurrencyStamp)),
                cancellationToken).ConfigureAwait(false);
        if (deleteResult.IsAcknowledged || deleteResult.DeletedCount != 0L)
            return IdentityResult.Success;
        return IdentityResult.Failed(ErrorDescriber.ConcurrencyFailure());
    }

    public Task<RtIdentityUser> FindByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return ByIdAsync(ConvertIdFromString(userId), cancellationToken);
    }

    public Task<RtIdentityUser> FindByNameAsync(
        string normalizedUserName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return _userCollection.FirstOrDefaultAsync<RtIdentityUser>(
            (Expression<Func<RtIdentityUser, bool>>)(x => x.NormalizedUserName == normalizedUserName), cancellationToken);
    }

    public async Task<IdentityResult> UpdateAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        string currentConcurrencyStamp = user != null ? user.ConcurrencyStamp : throw new ArgumentNullException(nameof(user));
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        ReplaceOneResult replaceOneResult = await _userCollection
            .ReplaceOneAsync<RtIdentityUser>(
                (Expression<Func<RtIdentityUser, bool>>)(x => x.Id.Equals(user.Id) && x.ConcurrencyStamp.Equals(currentConcurrencyStamp)),
                user, UserStore<RtIdentityUser, TRole, TKey>.ReplaceOptions, cancellationToken).ConfigureAwait(false);
        if (replaceOneResult.IsAcknowledged || replaceOneResult.ModifiedCount != 0L)
            return IdentityResult.Success;
        return IdentityResult.Failed(ErrorDescriber.ConcurrencyFailure());
    }

    public Task AddClaimsAsync(
        RtIdentityUser user,
        IEnumerable<Claim> claims,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        if (claims == null)
            throw new ArgumentNullException(nameof(claims));
        foreach (Claim claim in claims)
        {
            IdentityUserClaim<string> identityUserClaim = new IdentityUserClaim<string>()
            {
                ClaimType = claim.Type,
                ClaimValue = claim.Value
            };
            user.Claims.Add(identityUserClaim);
        }

        return Task.FromResult(false);
    }

    public Task ReplaceClaimAsync(
        RtIdentityUser user,
        Claim claim,
        Claim newClaim,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        if (claim == null)
            throw new ArgumentNullException(nameof(claim));
        if (newClaim == null)
            throw new ArgumentNullException(nameof(newClaim));
        foreach (IdentityUserClaim<string> identityUserClaim in user.Claims
                     .Where<IdentityUserClaim<string>>(
                         (Func<IdentityUserClaim<string>, bool>)(uc => uc.ClaimValue == claim.Value && uc.ClaimType == claim.Type))
                     .ToList<IdentityUserClaim<string>>())
        {
            identityUserClaim.ClaimValue = newClaim.Value;
            identityUserClaim.ClaimType = newClaim.Type;
        }

        return Task.CompletedTask;
    }

    public Task RemoveClaimsAsync(
        RtIdentityUser user,
        IEnumerable<Claim> claims,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        if (claims == null)
            throw new ArgumentNullException(nameof(claims));
        foreach (Claim claim1 in claims)
        {
            Claim claim = claim1;
            user.Claims.RemoveAll((Predicate<IdentityUserClaim<string>>)(x => x.ClaimType == claim.Type && x.ClaimValue == claim.Value));
        }

        return Task.CompletedTask;
    }

    public async Task<IList<RtIdentityUser>> GeRtIdentityUsersForClaimAsync(
        Claim claim,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (claim == null)
            throw new ArgumentNullException(nameof(claim));
        return (IList<RtIdentityUser>)(await _userCollection
            .WhereAsync<RtIdentityUser>(
                (Expression<Func<RtIdentityUser, bool>>)(u =>
                    u.Claims.Any<IdentityUserClaim<string>>(
                        (Func<IdentityUserClaim<string>, bool>)(c => c.ClaimType == claim.Type && c.ClaimValue == claim.Value))),
                cancellationToken).ConfigureAwait(false)).ToList<RtIdentityUser>();
    }

    public Task<string> GetNormalizedUserNameAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult<string>(user.NormalizedUserName) : throw new ArgumentNullException(nameof(user));
    }

    public Task<string> GeRtIdentityUserIdAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null
            ? Task.FromResult(ConvertIdToString(user.Id))
            : throw new ArgumentNullException(nameof(user));
    }

    public Task<string> GeRtIdentityUserNameAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult<string>(user.UserName) : throw new ArgumentNullException(nameof(user));
    }

    public async Task<IList<Claim>> GetClaimsAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        // ISSUE: variable of a boxed type
        __Boxed<RtIdentityUser> local = (object)await ByIdAsync(user.Id, cancellationToken).ConfigureAwait(true);
        List<Claim> claimsAsync;
        if (local == null)
        {
            claimsAsync = null;
        }
        else
        {
            List<IdentityUserClaim<string>> claims = local.Claims;
            if (claims == null)
            {
                claimsAsync = null;
            }
            else
            {
                IEnumerable<Claim> source =
                    claims.Select(
                        (Func<IdentityUserClaim<string>, Claim>)(x => new Claim(x.ClaimType, x.ClaimValue)));
                claimsAsync = source != null ? source.ToList<Claim>() : null;
            }
        }

        if (claimsAsync == null)
            claimsAsync = new List<Claim>();
        return claimsAsync;
    }

    public Task SetNormalizedUserNameAsync(
        RtIdentityUser user,
        string normalizedName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.NormalizedUserName = normalizedName;
        return Task.CompletedTask;
    }

    public Task SeRtIdentityUserNameAsync(RtIdentityUser user, string userName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.UserName = userName;
        return Task.CompletedTask;
    }

    public Task<string> GetEmailAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult<string>(user.Email) : throw new ArgumentNullException(nameof(user));
    }

    public Task<bool> GetEmailConfirmedAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult(user.EmailConfirmed) : throw new ArgumentNullException(nameof(user));
    }

    public Task<RtIdentityUser> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return _userCollection.FirstOrDefaultAsync<RtIdentityUser>(
            (Expression<Func<RtIdentityUser, bool>>)(u => u.NormalizedEmail == normalizedEmail), cancellationToken);
    }

    public Task<string> GetNormalizedEmailAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult<string>(user.NormalizedEmail) : throw new ArgumentNullException(nameof(user));
    }

    public Task SetEmailConfirmedAsync(
        RtIdentityUser user,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.EmailConfirmed = confirmed;
        return Task.CompletedTask;
    }

    public Task SetNormalizedEmailAsync(
        RtIdentityUser user,
        string normalizedEmail,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.NormalizedEmail = normalizedEmail;
        return Task.CompletedTask;
    }

    public Task SetEmailAsync(RtIdentityUser user, string email, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.Email = email;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult<int>(user.AccessFailedCount) : throw new ArgumentNullException(nameof(user));
    }

    public Task<bool> GetLockoutEnabledAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult(user.LockoutEnabled) : throw new ArgumentNullException(nameof(user));
    }

    public Task<int> IncrementAccessFailedCountAsync(
        RtIdentityUser user,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        ++user.AccessFailedCount;
        return Task.FromResult<int>(user.AccessFailedCount);
    }

    public Task ResetAccessFailedCountAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(
        RtIdentityUser user,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult(user.LockoutEnd) : throw new ArgumentNullException(nameof(user));
    }

    public Task SetLockoutEndDateAsync(
        RtIdentityUser user,
        DateTimeOffset? lockoutEnd,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task SetLockoutEnabledAsync(
        RtIdentityUser user,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.LockoutEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task AddLoginAsync(RtIdentityUser user, UserLoginInfo login, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        if (login == null)
            throw new ArgumentNullException(nameof(login));
        IdentityUserLogin<string> identityUserLogin = new IdentityUserLogin<string>()
        {
            UserId = ConvertIdToString(user.Id),
            LoginProvider = login.LoginProvider,
            ProviderDisplayName = login.ProviderDisplayName,
            ProviderKey = login.ProviderKey
        };
        user.Logins.Add(identityUserLogin);
        return Task.CompletedTask;
    }

    public Task RemoveLoginAsync(
        RtIdentityUser user,
        string loginProvider,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.Logins.RemoveAll(
            (Predicate<IdentityUserLogin<string>>)(x => x.LoginProvider == loginProvider && x.ProviderKey == providerKey));
        return Task.CompletedTask;
    }

    public async Task<RtIdentityUser> FindByLoginAsync(
        string loginProvider,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return await _userCollection
            .FirstOrDefaultAsync<RtIdentityUser>(
                (Expression<Func<RtIdentityUser, bool>>)(u =>
                    u.Logins.Any<IdentityUserLogin<string>>((Func<IdentityUserLogin<string>, bool>)(l =>
                        l.LoginProvider == loginProvider && l.ProviderKey == providerKey))), cancellationToken).ConfigureAwait(true);
    }

    public async Task<IList<UserLoginInfo>> GetLoginsAsync(
        RtIdentityUser user,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        // ISSUE: variable of a boxed type
        __Boxed<RtIdentityUser> local = (object)await ByIdAsync(user.Id, cancellationToken).ConfigureAwait(true);
        List<UserLoginInfo> loginsAsync;
        if (local == null)
        {
            loginsAsync = null;
        }
        else
        {
            List<IdentityUserLogin<string>> logins = local.Logins;
            if (logins == null)
            {
                loginsAsync = null;
            }
            else
            {
                IEnumerable<UserLoginInfo> source = logins.Select(
                    (Func<IdentityUserLogin<string>, UserLoginInfo>)(x =>
                        new UserLoginInfo(x.LoginProvider, x.ProviderKey, x.ProviderDisplayName)));
                loginsAsync = source != null ? source.ToList<UserLoginInfo>() : null;
            }
        }

        if (loginsAsync == null)
            loginsAsync = new List<UserLoginInfo>();
        return loginsAsync;
    }

    public Task<string> GetPasswordHashAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult<string>(user.PasswordHash) : throw new ArgumentNullException(nameof(user));
    }

    public Task<bool> HasPasswordAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult(user.PasswordHash != null) : throw new ArgumentNullException(nameof(user));
    }

    public Task SetPasswordHashAsync(
        RtIdentityUser user,
        string passwordHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string> GetPhoneNumberAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult<string>(user.PhoneNumber) : throw new ArgumentNullException(nameof(user));
    }

    public Task<bool> GetPhoneNumberConfirmedAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult(user.PhoneNumberConfirmed) : throw new ArgumentNullException(nameof(user));
    }

    public Task SetPhoneNumberAsync(
        RtIdentityUser user,
        string phoneNumber,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.PhoneNumber = phoneNumber;
        return Task.CompletedTask;
    }

    public Task SetPhoneNumberConfirmedAsync(
        RtIdentityUser user,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.PhoneNumberConfirmed = confirmed;
        return Task.CompletedTask;
    }

    public async Task AddToRoleAsync(
        RtIdentityUser user,
        string normalizedRoleName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        if (string.IsNullOrWhiteSpace(normalizedRoleName))
            throw new ArgumentNullException("Value cannot be null or empty.", nameof(normalizedRoleName));
        user.Roles.Add(ConvertIdToString((await FindRoleAsync(normalizedRoleName, cancellationToken) ??
                                          throw new InvalidOperationException(string.Format(
                                              CultureInfo.CurrentCulture, "Role {0} does not exist.",
                                              normalizedRoleName))).Id));
    }

    public async Task RemoveFromRoleAsync(
        RtIdentityUser user,
        string normalizedRoleName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        if (string.IsNullOrWhiteSpace(normalizedRoleName))
            throw new ArgumentNullException("Value cannot be null or empty.", nameof(normalizedRoleName));
        TRole roleAsync = await FindRoleAsync(normalizedRoleName, cancellationToken);
        if ((object)roleAsync == null)
            return;
        user.Roles.Remove(ConvertIdToString(roleAsync.Id));
    }

    public async Task<IList<RtIdentityUser>> GeRtIdentityUsersInRoleAsync(
        string normalizedRoleName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(normalizedRoleName))
            throw new ArgumentNullException(nameof(normalizedRoleName));
        TRole roleAsync = await FindRoleAsync(normalizedRoleName, cancellationToken);
        if ((object)roleAsync == null)
            return new List<RtIdentityUser>();
        return (IList<RtIdentityUser>)(await _userCollection.FindAsync<RtIdentityUser>(
            Builders<RtIdentityUser>.Filter.AnyEq<string>((Expression<Func<RtIdentityUser, IEnumerable<string>>>)(x => x.Roles),
                ConvertIdToString(roleAsync.Id)),
            (MongoDB.Driver.FindOptions<RtIdentityUser, RtIdentityUser>)UserStore<RtIdentityUser, TRole, TKey>.FindOptions,
            cancellationToken).ConfigureAwait(true)).ToList<RtIdentityUser>();
    }

    public async Task<IList<string>> GetRolesAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        UserStore<RtIdentityUser, TRole, TKey> userStore1 = this;
        cancellationToken.ThrowIfCancellationRequested();
        userStore1.ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        RtIdentityUser user1 = await userStore1.ByIdAsync(user.Id, cancellationToken).ConfigureAwait(true);
        if (user1 == null)
            return new List<string>();
        List<string> roles = new List<string>();
        foreach (string role1 in user1.Roles)
        {
            UserStore<RtIdentityUser, TRole, TKey> userStore = userStore1;
            string item = role1;
            TRole role2 = await userStore1._roleCollection
                .FirstOrDefaultAsync<TRole>((Expression<Func<TRole, bool>>)(r => r.Id.Equals(userStore1.ConvertIdFromString(item))),
                    cancellationToken).ConfigureAwait(true);
            if ((object)role2 != null)
                roles.Add(role2.Name);
        }

        return roles;
    }

    public async Task<bool> IsInRoleAsync(
        RtIdentityUser user,
        string normalizedRoleName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        RtIdentityUser dbUser = await ByIdAsync(user.Id, cancellationToken).ConfigureAwait(true);
        TRole role = await FindRoleAsync(normalizedRoleName, cancellationToken).ConfigureAwait(true);
        if ((object)role == null)
            return false;
        // ISSUE: variable of a boxed type
        __Boxed<RtIdentityUser> local = (object)dbUser;
        return local != null && local.Roles.Contains(ConvertIdToString(role.Id));
    }

    public Task<string> GetSecurityStampAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult<string>(user.SecurityStamp) : throw new ArgumentNullException(nameof(user));
    }

    public Task SetSecurityStampAsync(
        RtIdentityUser user,
        string stamp,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.SecurityStamp = stamp != null ? stamp : throw new ArgumentNullException(nameof(stamp));
        return Task.CompletedTask;
    }

    public Task ReplaceCodesAsync(
        RtIdentityUser user,
        IEnumerable<string> recoveryCodes,
        CancellationToken cancellationToken = default)
    {
        string str = string.Join(";", recoveryCodes);
        return SetTokenAsync(user, "[AspNeRtIdentityUserStore]", "RecoveryCodes", str, cancellationToken);
    }

    public async Task<bool> RedeemCodeAsync(
        RtIdentityUser user,
        string code,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        if (code == null)
            throw new ArgumentNullException(nameof(code));
        string[] source =
            (await GetTokenAsync(user, "[AspNeRtIdentityUserStore]", "RecoveryCodes", cancellationToken) ?? "").Split(';');
        if (!source.Contains(code))
            return false;
        List<string> recoveryCodes = new List<string>(source.Where<string>((Func<string, bool>)(s => s != code)));
        await ReplaceCodesAsync(user, recoveryCodes, cancellationToken);
        return true;
    }

    public async Task<int> CountCodesAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        string str = await GetTokenAsync(user, "[AspNeRtIdentityUserStore]", "RecoveryCodes", cancellationToken) ?? "";
        return str.Length <= 0 ? 0 : str.Split(';').Length;
    }

    public Task<bool> GetTwoFactorEnabledAsync(RtIdentityUser user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return user != null ? Task.FromResult(user.TwoFactorEnabled) : throw new ArgumentNullException(nameof(user));
    }

    public Task SetTwoFactorEnabledAsync(
        RtIdentityUser user,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    protected void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    public void Dispose() => _disposed = true;

    public virtual TKey ConvertIdFromString(string id) =>
        id == null ? default(TKey) : (TKey)TypeDescriptor.GetConverter(typeof(TKey)).ConvertFromInvariantString(id);

    public virtual string ConvertIdToString(TKey id) => Equals((object)id, (object)default(TKey)) ? (string)null : id.ToString();

    private Task<TRole> FindRoleAsync(
        string normalizedRoleName,
        CancellationToken cancellationToken = default)
    {
        return _roleCollection.FirstOrDefaultAsync<TRole>((Expression<Func<TRole, bool>>)(x => x.NormalizedName == normalizedRoleName),
            cancellationToken);
    }

    private async Task<IdentityUserToken<string>?> FindTokenAsync(
        IOctoSession session,
        RtIdentityUser user,
        string loginProvider,
        string name,
        CancellationToken cancellationToken = default)
    {
        var local = await _tenantContext.Repository.GetRtEntityAsync<RtIdentityUser>(session, user.RtId).ConfigureAwait(false);
        IdentityUserToken<string>? tokenAsync;
        if (local == null)
        {
            tokenAsync = null;
        }
        else
        {
            List<IdentityUserToken<string>> tokens = local.Tokens;
            tokenAsync = tokens != null
                ? tokens.FirstOrDefault(
                    (Func<IdentityUserToken<string>, bool>)(x => x.LoginProvider == loginProvider && x.Name == name))
                : null;
        }

        return tokenAsync;
    }

    private async Task UpdateAsync<TFieldValue>(
        RtIdentityUser user,
        Expression<Func<RtIdentityUser, TFieldValue>> expression,
        TFieldValue value,
        CancellationToken cancellationToken = default)
    {
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        UpdateResult updateResult = await _userCollection
            .UpdateOneAsync<RtIdentityUser>((Expression<Func<RtIdentityUser, bool>>)(x => x.Id.Equals(user.Id)),
                Builders<RtIdentityUser>.Update.Set<TFieldValue>(expression, value), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task AddAsync<TFieldValue>(
        RtIdentityUser user,
        Expression<Func<RtIdentityUser, IEnumerable<TFieldValue>>> expression,
        TFieldValue value,
        CancellationToken cancellationToken = default)
    {
        if (user == null)
            throw new ArgumentNullException(nameof(user));
        UpdateResult updateResult = await _userCollection
            .UpdateOneAsync<RtIdentityUser>((Expression<Func<RtIdentityUser, bool>>)(x => x.Id.Equals(user.Id)),
                Builders<RtIdentityUser>.Update.AddToSet<TFieldValue>(expression, value), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}