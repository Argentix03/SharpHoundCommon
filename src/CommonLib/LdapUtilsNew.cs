using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.DirectoryServices;
using System.DirectoryServices.ActiveDirectory;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpHoundCommonLib.Enums;
using SharpHoundCommonLib.Exceptions;
using SharpHoundCommonLib.LDAPQueries;
using SharpHoundCommonLib.OutputTypes;
using SharpHoundCommonLib.Processors;
using SharpHoundRPC.NetAPINative;
using Domain = System.DirectoryServices.ActiveDirectory.Domain;
using SearchScope = System.DirectoryServices.Protocols.SearchScope;
using SecurityMasks = System.DirectoryServices.Protocols.SecurityMasks;

namespace SharpHoundCommonLib;

public class LdapUtilsNew {
    //This cache is indexed by domain sid
    private readonly ConcurrentDictionary<string, NetAPIStructs.DomainControllerInfo?> _dcInfoCache = new();
    private static readonly ConcurrentDictionary<string, Domain> DomainCache = new();

    private static readonly ConcurrentDictionary<string, string> DomainToForestCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, ResolvedWellKnownPrincipal>
        SeenWellKnownPrincipals = new();

    private readonly ILogger _log;
    private readonly PortScanner _portScanner;
    private readonly NativeMethods _nativeMethods;
    private readonly string _nullCacheKey = Guid.NewGuid().ToString();
    private readonly Regex SidRegex = new Regex(@"^(S-\d+-\d+-\d+-\d+-\d+-\d+)-\d+$");

    private readonly string[] _translateNames = { "Administrator", "admin" };
    private LDAPConfig _ldapConfig = new();

    private ConnectionPoolManager _connectionPool;

    private static readonly TimeSpan MinBackoffDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoffDelay = TimeSpan.FromSeconds(20);
    private const int BackoffDelayMultiplier = 2;
    private const int MaxRetries = 3;

    private class ResolvedWellKnownPrincipal {
        public string DomainName { get; set; }
        public string WkpId { get; set; }
    }

    public LdapUtilsNew() {
        _nativeMethods = new NativeMethods();
        _portScanner = new PortScanner();
        _log = Logging.LogProvider.CreateLogger("LDAPUtils");
        _connectionPool = new ConnectionPoolManager(_ldapConfig);
    }

    public LdapUtilsNew(NativeMethods nativeMethods = null, PortScanner scanner = null, ILogger log = null) {
        _nativeMethods = nativeMethods ?? new NativeMethods();
        _portScanner = scanner ?? new PortScanner();
        _log = log ?? Logging.LogProvider.CreateLogger("LDAPUtils");
        _connectionPool = new ConnectionPoolManager(_ldapConfig, scanner: _portScanner);
    }

    public void SetLDAPConfig(LDAPConfig config) {
        _ldapConfig = config;
        _connectionPool.Dispose();
        _connectionPool = new ConnectionPoolManager(_ldapConfig, scanner: _portScanner);
    }

    public async IAsyncEnumerable<LdapResult<ISearchResultEntry>> Query(
        LdapQueryParameters queryParameters,
        [EnumeratorCancellation] CancellationToken cancellationToken = new()) {
        var setupResult = await SetupLdapQuery(queryParameters);
        if (!setupResult.Success) {
            _log.LogInformation("Query - Failure during query setup: {Reason}\n{Info}",
                setupResult.Message, queryParameters.GetQueryInfo());
            yield break;
        }

        var (searchRequest, connectionWrapper) = (setupResult.SearchRequest, setupResult.ConnectionWrapper);

        var queryResult = await ExecuteQuery(searchRequest, connectionWrapper, queryParameters, cancellationToken);
        if (!queryResult.Success) {
            yield return queryResult.Error;
            yield break;
        }

        //TODO: Fix this with a new wrapper object
        foreach (ISearchResultEntry entry in queryResult.Response.Entries) {
            yield return LdapResult<ISearchResultEntry>.Ok(entry);
        }
    }

    private async Task<(bool Success, SearchResponse Response, LdapResult<ISearchResultEntry> Error)> ExecuteQuery(
        SearchRequest searchRequest,
        LdapConnectionWrapperNew connectionWrapper,
        LdapQueryParameters queryParameters,
        CancellationToken cancellationToken) {
        int queryRetryCount = 0, busyRetryCount = 0;

        while (!cancellationToken.IsCancellationRequested) {
            try {
                _log.LogTrace("Sending ldap request - {Info}", queryParameters.GetQueryInfo());
                var response = (SearchResponse)connectionWrapper.Connection.SendRequest(searchRequest);

                if (response != null) {
                    return (true, response, null);
                }

                if (queryRetryCount == MaxRetries) {
                    return (false, null, LdapResult<ISearchResultEntry>.Fail(
                        $"Failed to get a response after {MaxRetries} attempts", queryParameters));
                }

                queryRetryCount++;
            }
            catch (LdapException le) when (le.ErrorCode == (int)LdapErrorCodes.ServerDown &&
                                           queryRetryCount < MaxRetries) {
                /*
                 * A ServerDown exception indicates that our connection is no longer valid for one of many reasons.
                 * We'll want to release our connection back to the pool, but dispose it. We need a new connection,
                 * and because this is not a paged query, we can get this connection from anywhere.
                 */

                var newConnection = await HandleServerDown(queryParameters, connectionWrapper, cancellationToken);
                if (newConnection.Success) {
                    connectionWrapper = newConnection.Wrapper;
                    queryRetryCount++;
                }
                else {
                    return (false, null, newConnection.Error);
                }
            }
            catch (LdapException le) when (le.ErrorCode == (int)ResultCode.Busy && busyRetryCount < MaxRetries) {
                /*
                 * If we get a busy error, we want to do an exponential backoff, but maintain the current connection
                 * The expectation is that given enough time, the server should stop being busy and service our query appropriately
                 */
                await HandleBusyServer(busyRetryCount++, cancellationToken);
            }
            catch (LdapException le) {
                return (false, null, LdapResult<ISearchResultEntry>.Fail(
                    $"Query - Caught unrecoverable ldap exception: {le.Message} (ServerMessage: {le.ServerErrorMessage}) (ErrorCode: {le.ErrorCode})",
                    queryParameters));
            }
            catch (Exception e) {
                return (false, null, LdapResult<ISearchResultEntry>.Fail(
                    $"PagedQuery - Caught unrecoverable exception: {e.Message}",
                    queryParameters));
            }
        }

        return (false, null, LdapResult<ISearchResultEntry>.Fail("Operation cancelled", queryParameters));
    }

    private async Task<(bool Success, LdapConnectionWrapperNew Wrapper, LdapResult<ISearchResultEntry> Error)>
        HandleServerDown(
            LdapQueryParameters queryParameters,
            LdapConnectionWrapperNew connectionWrapper,
            CancellationToken cancellationToken) {
        _connectionPool.ReleaseConnection(connectionWrapper, true);

        for (var retryCount = 0; retryCount < MaxRetries; retryCount++) {
            await Task.Delay(GetNextBackoff(retryCount), cancellationToken);
            var (success, newConnectionWrapper, message) =
                await _connectionPool.GetLdapConnection(queryParameters.DomainName, queryParameters.GlobalCatalog);

            if (success) {
                _log.LogDebug("Query - Recovered from ServerDown successfully, connection made to {NewServer}",
                    newConnectionWrapper.GetServer());
                return (true, newConnectionWrapper, null);
            }
        }

        _log.LogError("Query - Failed to get a new connection after ServerDown.\n{Info}",
            queryParameters.GetQueryInfo());
        return (false, null,
            LdapResult<ISearchResultEntry>.Fail("Query - Failed to get a new connection after ServerDown.",
                queryParameters));
    }

    private async Task HandleBusyServer(int busyRetryCount, CancellationToken cancellationToken) {
        var backoffDelay = GetNextBackoff(busyRetryCount);
        await Task.Delay(backoffDelay, cancellationToken);
    }

    public async IAsyncEnumerable<LdapResult<ISearchResultEntry>> PagedQuery(
        LdapQueryParameters queryParameters,
        [EnumeratorCancellation] CancellationToken cancellationToken = new()) {
        var setupResult = await SetupLdapQuery(queryParameters);
        if (!setupResult.Success) {
            _log.LogInformation("PagedQuery - Failure during query setup: {Reason}\n{Info}",
                setupResult.Message, queryParameters.GetQueryInfo());
            yield break;
        }

        var (searchRequest, connectionWrapper, serverName) =
            (setupResult.SearchRequest, setupResult.ConnectionWrapper, setupResult.Server);

        if (serverName == null) {
            _log.LogWarning("PagedQuery - Failed to get a server name for connection, retry not possible");
        }

        var pageControl = new PageResultRequestControl(500);
        searchRequest.Controls.Add(pageControl);

        while (!cancellationToken.IsCancellationRequested) {
            var queryResult = await ExecutePagedQuery(searchRequest, connectionWrapper, queryParameters, serverName,
                cancellationToken);
            if (!queryResult.Success) {
                if (queryResult.Error != null) {
                    yield return queryResult.Error;
                }

                yield break;
            }

            connectionWrapper = queryResult.ConnectionWrapper; // Update connection wrapper if it changed

            foreach (ISearchResultEntry entry in queryResult.Response.Entries) {
                if (cancellationToken.IsCancellationRequested) {
                    yield break;
                }

                yield return LdapResult<ISearchResultEntry>.Ok(entry);
            }

            var pageResponse = (PageResultResponseControl)queryResult.Response.Controls
                .FirstOrDefault(x => x is PageResultResponseControl);

            if (pageResponse == null || pageResponse.Cookie.Length == 0 || queryResult.Response.Entries.Count == 0) {
                yield break;
            }

            pageControl.Cookie = pageResponse.Cookie;
        }
    }

    private async
        Task<(bool Success, SearchResponse Response, LdapConnectionWrapperNew ConnectionWrapper,
            LdapResult<ISearchResultEntry> Error)> ExecutePagedQuery(
            SearchRequest searchRequest,
            LdapConnectionWrapperNew connectionWrapper,
            LdapQueryParameters queryParameters,
            string serverName,
            CancellationToken cancellationToken) {
        int queryRetryCount = 0, busyRetryCount = 0;

        while (!cancellationToken.IsCancellationRequested) {
            try {
                _log.LogTrace("Sending paged ldap request - {Info}", queryParameters.GetQueryInfo());
                var response = (SearchResponse)connectionWrapper.Connection.SendRequest(searchRequest);

                if (response != null) {
                    return (true, response, connectionWrapper, null);
                }

                if (queryRetryCount == MaxRetries) {
                    return (false, null, connectionWrapper, LdapResult<ISearchResultEntry>.Fail(
                        $"PagedQuery - Failed to get a response after {MaxRetries} attempts", queryParameters));
                }

                queryRetryCount++;
            }
            catch (LdapException le) when (le.ErrorCode == (int)LdapErrorCodes.ServerDown) {
                /*
                 * If we dont have a servername, we're not going to be able to re-establish a connection here. Page cookies are only valid for the server they were generated on. Bail out.
                 */
                if (serverName == null) {
                    _log.LogError(
                        "PagedQuery - Received server down exception without a known servername. Unable to generate new connection\n{Info}",
                        queryParameters.GetQueryInfo());
                    return (false, null, connectionWrapper, null);
                }

                var newConnection =
                    await HandlePagedServerDown(queryParameters, connectionWrapper, serverName, cancellationToken);
                if (newConnection.Success) {
                    connectionWrapper = newConnection.Wrapper;
                }
                else {
                    return (false, null, connectionWrapper, newConnection.Error);
                }
            }
            catch (LdapException le) when (le.ErrorCode == (int)ResultCode.Busy && busyRetryCount < MaxRetries) {
                /*
                 * If we get a busy error, we want to do an exponential backoff, but maintain the current connection
                 * The expectation is that given enough time, the server should stop being busy and service our query appropriately
                 */
                await HandleBusyServer(busyRetryCount++, cancellationToken);
            }
            catch (LdapException le) {
                return (false, null, connectionWrapper, LdapResult<ISearchResultEntry>.Fail(
                    $"PagedQuery - Caught unrecoverable ldap exception: {le.Message} (ServerMessage: {le.ServerErrorMessage}) (ErrorCode: {le.ErrorCode})",
                    queryParameters));
            }
            catch (Exception e) {
                return (false, null, connectionWrapper, LdapResult<ISearchResultEntry>.Fail(
                    $"PagedQuery - Caught unrecoverable exception: {e.Message}",
                    queryParameters));
            }
        }

        return (false, null, connectionWrapper,
            LdapResult<ISearchResultEntry>.Fail("Operation cancelled", queryParameters));
    }

    private async Task<(bool Success, LdapConnectionWrapperNew Wrapper, LdapResult<ISearchResultEntry> Error)>
        HandlePagedServerDown(
            LdapQueryParameters queryParameters,
            LdapConnectionWrapperNew connectionWrapper,
            string serverName,
            CancellationToken cancellationToken) {
        /*
         * Paged queries will not use the cached ldap connections, as the intention is to only have 1 or a couple of these queries running at once.
         * The connection logic here is simplified accordingly
         */
        _connectionPool.ReleaseConnection(connectionWrapper, true);

        for (var retryCount = 0; retryCount < MaxRetries; retryCount++) {
            await Task.Delay(GetNextBackoff(retryCount), cancellationToken);
            var (success, newConnectionWrapper, message) = await _connectionPool.GetLdapConnectionForServer(
                queryParameters.DomainName, serverName, queryParameters.GlobalCatalog);

            if (success) {
                _log.LogDebug("PagedQuery - Recovered from ServerDown successfully");
                return (true, newConnectionWrapper, null);
            }
        }

        _log.LogError("PagedQuery - Failed to get a new connection after ServerDown.\n{Info}",
            queryParameters.GetQueryInfo());
        return (false, null, null);
    }

    public bool ResolveIDAndType(SecurityIdentifier securityIdentifier, string objectDomain,
        out TypedPrincipal resolvedPrincipal) {
        return ResolveIDAndType(securityIdentifier.Value, objectDomain, out resolvedPrincipal);
    }

    public async Task<(bool Success, TypedPrincipal Principal)>
        ResolveIDAndType(string identifier, string objectDomain) {
        if (identifier.Contains("0ACNF")) {
            return (false, null);
        }

        if (await GetWellKnownPrincipal(identifier, objectDomain) is (true, var principal)) {
            return (true, principal);
        }

        var type = identifier.StartsWith("S-") ? LookupSidType(id, fallbackDomain) : LookupGuidType(id, fallbackDomain);
        return new TypedPrincipal(id, type);
    }

    private async Task<(bool Success, Label type)> LookupSidType(string sid, string domain) {
        if (Cache.GetIDType(sid, out var type)) {
            return (true, type);
        }

        if (await GetDomainSidFromDomainName(domain) is (true, var domainSid)) {
        }
    }

    public async Task<(bool Success, TypedPrincipal wellKnownPrincipal)> GetWellKnownPrincipal(
        string securityIdentifier, string objectDomain) {
        if (!WellKnownPrincipal.GetWellKnownPrincipal(securityIdentifier, out var wellKnownPrincipal)) {
            return (false, null);
        }

        var (newIdentifier, newDomain) = await GetWellKnownPrincipalObjectIdentifier(securityIdentifier, objectDomain);

        wellKnownPrincipal.ObjectIdentifier = newIdentifier;
        SeenWellKnownPrincipals.TryAdd(wellKnownPrincipal.ObjectIdentifier, new ResolvedWellKnownPrincipal {
            DomainName = newDomain,
            WkpId = securityIdentifier
        });

        return (true, wellKnownPrincipal);
    }

    private async Task<(string ObjectID, string Domain)> GetWellKnownPrincipalObjectIdentifier(
        string securityIdentifier, string domain) {
        if (!WellKnownPrincipal.GetWellKnownPrincipal(securityIdentifier, out _))
            return (securityIdentifier, string.Empty);

        if (!securityIdentifier.Equals("S-1-5-9", StringComparison.OrdinalIgnoreCase)) {
            var tempDomain = domain;
            if (GetDomain(tempDomain, out var domainObject) && domainObject.Name != null) {
                tempDomain = domainObject.Name;
            }

            return ($"{tempDomain}-{securityIdentifier}".ToUpper(), tempDomain);
        }

        if (await GetForest(domain) is (true, var forest)) {
            return ($"{forest}-{securityIdentifier}".ToUpper(), forest);
        }

        _log.LogWarning("Failed to get a forest name for domain {Domain}, unable to resolve enterprise DC sid", domain);
        return ($"UNKNOWN-{securityIdentifier}", "UNKNOWN");
    }

    private async Task<(bool Success, string ForestName)> GetForest(string domain) {
        if (DomainToForestCache.TryGetValue(domain, out var cachedForest)) {
            return (true, cachedForest);
        }

        if (GetDomain(domain, out var domainObject)) {
            var forestName = domainObject.Forest.Name.ToUpper();
            DomainToForestCache.TryAdd(domain, forestName);
            return (true, forestName);
        }

        var (success, forest) = await GetForestFromLdap(domain);
        if (success) {
            DomainToForestCache.TryAdd(domain, forest);
            return (true, forest);
        }

        return (false, null);
    }

    private async Task<(bool Success, string ForestName)> GetForestFromLdap(string domain) {
        var queryParameters = new LdapQueryParameters {
            Attributes = new[] { LDAPProperties.RootDomainNamingContext },
            SearchScope = SearchScope.Base,
            DomainName = domain,
            LDAPFilter = new LDAPFilter().AddAllObjects().GetFilter(),
        };

        var result = await Query(queryParameters).FirstAsync();
        if (result.IsSuccess) {
            var rdn = result.Value.GetProperty(LDAPProperties.RootDomainNamingContext);
            if (!string.IsNullOrEmpty(rdn)) {
                return (true, Helpers.DistinguishedNameToDomain(rdn).ToUpper());
            }
        }

        return (false, null);
    }

    private static TimeSpan GetNextBackoff(int retryCount) {
        return TimeSpan.FromSeconds(Math.Min(
            MinBackoffDelay.TotalSeconds * Math.Pow(BackoffDelayMultiplier, retryCount),
            MaxBackoffDelay.TotalSeconds));
    }

    private bool CreateSearchRequest(LdapQueryParameters queryParameters,
        ref LdapConnectionWrapperNew connectionWrapper, out SearchRequest searchRequest) {
        string basePath;
        if (!string.IsNullOrWhiteSpace(queryParameters.SearchBase)) {
            basePath = queryParameters.SearchBase;
        }
        else if (!connectionWrapper.GetSearchBase(queryParameters.NamingContext, out basePath)) {
            string tempPath;
            if (CallDsGetDcName(queryParameters.DomainName, out var info) && info != null) {
                tempPath = Helpers.DomainNameToDistinguishedName(info.Value.DomainName);
                connectionWrapper.SaveContext(queryParameters.NamingContext, basePath);
            }
            else if (GetDomain(queryParameters.DomainName, out var domainObject)) {
                tempPath = Helpers.DomainNameToDistinguishedName(domainObject.Name);
            }
            else {
                searchRequest = null;
                return false;
            }

            basePath = queryParameters.NamingContext switch {
                NamingContext.Configuration => $"CN=Configuration,{tempPath}",
                NamingContext.Schema => $"CN=Schema,CN=Configuration,{tempPath}",
                NamingContext.Default => tempPath,
                _ => throw new ArgumentOutOfRangeException()
            };

            connectionWrapper.SaveContext(queryParameters.NamingContext, basePath);
        }

        searchRequest = new SearchRequest(basePath, queryParameters.LDAPFilter, queryParameters.SearchScope,
            queryParameters.Attributes);
        searchRequest.Controls.Add(new SearchOptionsControl(SearchOption.DomainScope));
        if (queryParameters.IncludeDeleted) {
            searchRequest.Controls.Add(new ShowDeletedControl());
        }

        if (queryParameters.IncludeSecurityDescriptor) {
            searchRequest.Controls.Add(new SecurityDescriptorFlagControl {
                SecurityMasks = SecurityMasks.Dacl | SecurityMasks.Owner
            });
        }

        return true;
    }


    private bool CallDsGetDcName(string domainName, out NetAPIStructs.DomainControllerInfo? info) {
        if (_dcInfoCache.TryGetValue(domainName.ToUpper().Trim(), out info)) return info != null;

        var apiResult = _nativeMethods.CallDsGetDcName(null, domainName,
            (uint)(NetAPIEnums.DSGETDCNAME_FLAGS.DS_FORCE_REDISCOVERY |
                   NetAPIEnums.DSGETDCNAME_FLAGS.DS_RETURN_DNS_NAME |
                   NetAPIEnums.DSGETDCNAME_FLAGS.DS_DIRECTORY_SERVICE_REQUIRED));

        if (apiResult.IsFailed) {
            _dcInfoCache.TryAdd(domainName.ToUpper().Trim(), null);
            return false;
        }

        info = apiResult.Value;
        return true;
    }

    private async Task<LdapQuerySetupResult> SetupLdapQuery(LdapQueryParameters queryParameters) {
        var result = new LdapQuerySetupResult();
        var (success, connectionWrapper, message) =
            await _connectionPool.GetLdapConnection(queryParameters.DomainName, queryParameters.GlobalCatalog);
        if (!success) {
            result.Success = false;
            result.Message = $"Unable to create a connection: {message}";
            return result;
        }

        //This should never happen as far as I know, so just checking for safety
        if (connectionWrapper.Connection == null) {
            result.Success = false;
            result.Message = $"Connection object is null";
            return result;
        }

        if (!CreateSearchRequest(queryParameters, ref connectionWrapper, out var searchRequest)) {
            result.Success = false;
            result.Message = "Failed to create search request";
            return result;
        }

        result.Server = connectionWrapper.GetServer();
        result.Success = true;
        result.SearchRequest = searchRequest;
        result.ConnectionWrapper = connectionWrapper;
        return result;
    }

    public static SearchRequest CreateSearchRequest(string distinguishedName, string ldapFilter,
        SearchScope searchScope,
        string[] attributes) {
        var searchRequest = new SearchRequest(distinguishedName, ldapFilter,
            searchScope, attributes);
        searchRequest.Controls.Add(new SearchOptionsControl(SearchOption.DomainScope));
        return searchRequest;
    }

    public async Task<(bool Success, string DomainName)> GetDomainNameFromSid(string sid) {
        string domainSid;
        try {
            domainSid = new SecurityIdentifier(sid).AccountDomainSid?.Value.ToUpper();
        }
        catch {
            var match = SidRegex.Match(sid);
            domainSid = match.Success ? match.Groups[1].Value : null;
        }

        if (domainSid == null) {
            return (false, "");
        }

        if (Cache.GetDomainSidMapping(domainSid, out var domain)) {
            return (true, domain);
        }

        try {
            var entry = new DirectoryEntry($"LDAP://<SID={domainSid}>");
            entry.RefreshCache(new[] { LDAPProperties.DistinguishedName });
            var dn = entry.GetProperty(LDAPProperties.DistinguishedName);
            if (!string.IsNullOrEmpty(dn)) {
                Cache.AddDomainSidMapping(domainSid, Helpers.DistinguishedNameToDomain(dn));
                return (true, Helpers.DistinguishedNameToDomain(dn));
            }
        }
        catch {
            //pass
        }

        if (await ConvertDomainSidToDomainNameFromLdap(sid) is (true, var domainName)) {
            Cache.AddDomainSidMapping(domainSid, domainName);
            return (true, domainName);
        }

        return (false, string.Empty);
    }

    private async Task<(bool Success, string DomainName)> ConvertDomainSidToDomainNameFromLdap(string domainSid) {
        if (!GetDomain(out var domain) || domain?.Name == null) {
            return (false, string.Empty);
        }

        var result = await Query(new LdapQueryParameters {
            DomainName = domain.Name,
            Attributes = new[] { LDAPProperties.DistinguishedName },
            GlobalCatalog = true,
            LDAPFilter = new LDAPFilter().AddDomains(CommonFilters.SpecificSID(domainSid)).GetFilter()
        }).FirstAsync();

        if (result.IsSuccess) {
            return (true, Helpers.DistinguishedNameToDomain(result.Value.DistinguishedName));
        }

        result = await Query(new LdapQueryParameters {
            DomainName = domain.Name,
            Attributes = new[] { LDAPProperties.DistinguishedName },
            GlobalCatalog = true,
            LDAPFilter = new LDAPFilter().AddFilter("(objectclass=trusteddomain)", true)
                .AddFilter($"(securityidentifier={Helpers.ConvertSidToHexSid(domainSid)})", true).GetFilter()
        }).FirstAsync();

        if (result.IsSuccess) {
            return (true, Helpers.DistinguishedNameToDomain(result.Value.DistinguishedName));
        }

        result = await Query(new LdapQueryParameters {
            DomainName = domain.Name,
            Attributes = new[] { LDAPProperties.DistinguishedName },
            LDAPFilter = new LDAPFilter().AddFilter("(objectclass=domaindns)", true)
                .AddFilter(CommonFilters.SpecificSID(domainSid), true).GetFilter()
        }).FirstAsync();

        if (result.IsSuccess) {
            return (true, Helpers.DistinguishedNameToDomain(result.Value.DistinguishedName));
        }

        return (false, string.Empty);
    }

    public async Task<(bool Success, string DomainSid)> GetDomainSidFromDomainName(string domainName) {
        if (Cache.GetDomainSidMapping(domainName, out var cachedSid))
            return (true, cachedSid);

        var (success, sid) = await TryGetSid(domainName);

        if (success) {
            Cache.AddDomainSidMapping(domainName, sid);
            return (true, sid);
        }

        return (false, string.Empty);
    }

    private async Task<(bool Success, string Sid)> TryGetSid(string domainName) {
        if (TryGetSidFromDirectoryEntry(domainName) is (true, var sid))
            return Task.FromResult(true, sid);
        else if (TryGetSidFromDomainObject(domainName) is (true, var sid))
            return Task.FromResult(true, sid);
        else if (TryGetSidFromNTAccount(domainName) is (true, var sid))
            return Task.FromResult(true, sid);
        else if (await TryGetSidFromLdapQuery(domainName) is (true, var sid))
            return Task.FromResult(true, sid);

        return (false, string.Empty);
    }

    private (bool, string) TryGetSidFromDirectoryEntry(string domainName) {
        try {
            var entry = new DirectoryEntry($"LDAP://{domainName}");
            entry.RefreshCache(new[] { "objectSid" });
            var sid = entry.GetSid();
            return (sid != null, sid);
        }
        catch {
            return (false, (string)null);
        }
    }

    private (bool, string) TryGetSidFromDomainObject(string domainName) {
        if (!GetDomain(domainName, out var domainObject))
            return (false, (string)null);

        try {
            var sid = domainObject.GetDirectoryEntry().GetSid();
            return (sid != null, sid);
        }
        catch {
            return (false, (string)null);
        }
    }

    private (bool, string) TryGetSidFromNTAccount(string domainName) {
        foreach (var name in _translateNames) {
            try {
                var account = new NTAccount(domainName, name);
                var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
                return (true, sid.AccountDomainSid.ToString());
            }
            catch {
                // Continue to next name if this one fails
            }
        }

        return (false, (string)null);
    }

    private async Task<(bool, string)> TryGetSidFromLdapQuery(string domainName) {
        var result = await Query(new LdapQueryParameters {
            DomainName = domainName,
            Attributes = new[] { LDAPProperties.ObjectSID },
            LDAPFilter = new LDAPFilter().AddFilter(CommonFilters.DomainControllers, true).GetFilter()
        }).FirstAsync();

        if (result.Success) {
            var sid = result.Value.GetSid();
            if (!string.IsNullOrEmpty(sid)) {
                var domainSid = new SecurityIdentifier(sid).AccountDomainSid.Value;
                return (true, domainSid);
            }
        }

        return (false, string.Empty);
    }

    /// <summary>
    ///     Attempts to get the Domain object representing the target domain. If null is specified for the domain name, gets
    ///     the user's current domain
    /// </summary>
    /// <param name="domain"></param>
    /// <param name="domainName"></param>
    /// <returns></returns>
    public bool GetDomain(string domainName, out Domain domain) {
        var cacheKey = domainName ?? _nullCacheKey;
        if (DomainCache.TryGetValue(cacheKey, out domain)) return true;

        try {
            DirectoryContext context;
            if (_ldapConfig.Username != null)
                context = domainName != null
                    ? new DirectoryContext(DirectoryContextType.Domain, domainName, _ldapConfig.Username,
                        _ldapConfig.Password)
                    : new DirectoryContext(DirectoryContextType.Domain, _ldapConfig.Username,
                        _ldapConfig.Password);
            else
                context = domainName != null
                    ? new DirectoryContext(DirectoryContextType.Domain, domainName)
                    : new DirectoryContext(DirectoryContextType.Domain);

            domain = Domain.GetDomain(context);
            if (domain == null) return false;
            DomainCache.TryAdd(cacheKey, domain);
            return true;
        }
        catch (Exception e) {
            _log.LogDebug(e, "GetDomain call failed for domain name {Name}", domainName);
            return false;
        }
    }

    public static bool GetDomain(string domainName, LDAPConfig ldapConfig, out Domain domain) {
        if (DomainCache.TryGetValue(domainName, out domain)) return true;

        try {
            DirectoryContext context;
            if (ldapConfig.Username != null)
                context = domainName != null
                    ? new DirectoryContext(DirectoryContextType.Domain, domainName, ldapConfig.Username,
                        ldapConfig.Password)
                    : new DirectoryContext(DirectoryContextType.Domain, ldapConfig.Username,
                        ldapConfig.Password);
            else
                context = domainName != null
                    ? new DirectoryContext(DirectoryContextType.Domain, domainName)
                    : new DirectoryContext(DirectoryContextType.Domain);

            domain = Domain.GetDomain(context);
            if (domain == null) return false;
            DomainCache.TryAdd(domainName, domain);
            return true;
        }
        catch (Exception e) {
            return false;
        }
    }

    /// <summary>
    ///     Attempts to get the Domain object representing the target domain. If null is specified for the domain name, gets
    ///     the user's current domain
    /// </summary>
    /// <param name="domain"></param>
    /// <param name="domainName"></param>
    /// <returns></returns>
    public bool GetDomain(out Domain domain) {
        var cacheKey = _nullCacheKey;
        if (DomainCache.TryGetValue(cacheKey, out domain)) return true;

        try {
            var context = _ldapConfig.Username != null
                ? new DirectoryContext(DirectoryContextType.Domain, _ldapConfig.Username,
                    _ldapConfig.Password)
                : new DirectoryContext(DirectoryContextType.Domain);

            domain = Domain.GetDomain(context);
            DomainCache.TryAdd(cacheKey, domain);
            return true;
        }
        catch (Exception e) {
            _log.LogDebug(e, "GetDomain call failed for blank domain");
            return false;
        }
    }

    private struct LdapFailure {
        public LdapFailureReason FailureReason { get; set; }
        public string Message { get; set; }
    }
}