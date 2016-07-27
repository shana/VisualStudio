﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Akavache;
using GitHub.Api;
using GitHub.Authentication;
using GitHub.Caches;
using GitHub.Models;
using GitHub.Primitives;
using GitHub.Services;
using NSubstitute;
using Octokit;
using UnitTests.Helpers;
using Xunit;

public class RepositoryHostTests
{
    public class TheLoginMethod : TestBaseClass
    {
        [Fact]
        public async Task LogsTheUserInSuccessfullyAndCachesRelevantInfo()
        {
            var apiClient = Substitute.For<IApiClient>();
            apiClient.HostAddress.Returns(HostAddress.GitHubDotComHostAddress);
            apiClient.GetOrCreateApplicationAuthenticationCode(
                Args.TwoFactorChallengCallback, Args.String, Args.Boolean)
                .Returns(Observable.Return(new ApplicationAuthorization("S3CR3TS")));
            apiClient.GetUser().Returns(Observable.Return(CreateUserAndScopes("baymax")));
            var hostCache = new InMemoryBlobCache();
            var modelService = new ModelService(apiClient, hostCache, Substitute.For<IAvatarProvider>());
            var loginCache = new TestLoginCache();
            var usage = Substitute.For<IUsageTracker>();
            var host = new RepositoryHost(apiClient, modelService, loginCache, Substitute.For<ITwoFactorChallengeHandler>(), usage);

            var result = await host.LogIn("baymax", "aPassword");

            Assert.Equal(AuthenticationResult.Success, result);
            var user = await hostCache.GetObject<AccountCacheItem>("user");
            Assert.NotNull(user);
            Assert.Equal("baymax", user.Login);
            var loginInfo = await loginCache.GetLoginAsync(HostAddress.GitHubDotComHostAddress);
            Assert.Equal("baymax", loginInfo.UserName);
            Assert.Equal("S3CR3TS", loginInfo.Password);
            Assert.True(host.IsLoggedIn);
        }

        [Fact]
        public async Task DoesNotLogInWhenRetrievingOauthTokenFails()
        {
            var apiClient = Substitute.For<IApiClient>();
            apiClient.HostAddress.Returns(HostAddress.GitHubDotComHostAddress);
            apiClient.GetOrCreateApplicationAuthenticationCode(
                Args.TwoFactorChallengCallback, Args.String, Args.Boolean)
                .Returns(Observable.Throw<ApplicationAuthorization>(new NotFoundException("", HttpStatusCode.BadGateway)));
            apiClient.GetUser().Returns(Observable.Return(CreateUserAndScopes("jiminy")));
            var hostCache = new InMemoryBlobCache();
            var modelService = new ModelService(apiClient, hostCache, Substitute.For<IAvatarProvider>());
            var loginCache = new TestLoginCache();
            var usage = Substitute.For<IUsageTracker>();
            var host = new RepositoryHost(apiClient, modelService, loginCache, Substitute.For<ITwoFactorChallengeHandler>(), usage);

            await Assert.ThrowsAsync<NotFoundException>(async () => await host.LogIn("jiminy", "cricket"));

            await Assert.ThrowsAsync<KeyNotFoundException>(
                async () => await hostCache.GetObject<AccountCacheItem>("user"));
            var loginInfo = await loginCache.GetLoginAsync(HostAddress.GitHubDotComHostAddress);
            Assert.Equal("jiminy", loginInfo.UserName);
            Assert.Equal("cricket", loginInfo.Password);
            Assert.False(host.IsLoggedIn);
        }

        [Fact]
        public async Task UsesUsernameAndPasswordInsteadOfAuthorizationTokenWhenEnterpriseAndAPIReturns404()
        {
            var enterpriseHostAddress = HostAddress.Create("https://enterprise.example.com/");
            var apiClient = Substitute.For<IApiClient>();
            apiClient.HostAddress.Returns(enterpriseHostAddress);
            // Throw a 404 on the first try with the new scopes
            apiClient.GetOrCreateApplicationAuthenticationCode(Args.TwoFactorChallengCallback, null, false, true)
                .Returns(Observable.Throw<ApplicationAuthorization>(new NotFoundException("Not there", HttpStatusCode.NotFound)));
            // Throw a 404 on the retry with the old scopes:
            apiClient.GetOrCreateApplicationAuthenticationCode(Args.TwoFactorChallengCallback, null, true, false)
                .Returns(Observable.Throw<ApplicationAuthorization>(new NotFoundException("Also not there", HttpStatusCode.NotFound)));
            apiClient.GetUser().Returns(Observable.Return(CreateUserAndScopes("Cthulu")));
            var hostCache = new InMemoryBlobCache();
            var modelService = new ModelService(apiClient, hostCache, Substitute.For<IAvatarProvider>());
            var loginCache = new TestLoginCache();
            var usage = Substitute.For<IUsageTracker>();
            var host = new RepositoryHost(apiClient, modelService, loginCache, Substitute.For<ITwoFactorChallengeHandler>(), usage);

            var result = await host.LogIn("Cthulu", "aPassword");

            Assert.Equal(AuthenticationResult.Success, result);
            // Only username and password were saved, never an authorization token:
            var loginInfo = await loginCache.GetLoginAsync(enterpriseHostAddress);
            Assert.Equal("Cthulu", loginInfo.UserName);
            Assert.Equal("aPassword", loginInfo.Password);
            Assert.True(host.IsLoggedIn);
        }

        // Since we're now catching a 401 to detect a bad authoriation token, we need a test to make sure 
        // real 2FA failures (like putting in a bad code) still make it through (they're also a 401, but
        // shouldn't be caught):
        [Fact]
        public async Task DoesNotFallBackToOldScopesWhenGitHubAndTwoFactorAuthFailsAndErasesLogin()
        {
            var apiClient = Substitute.For<IApiClient>();
            apiClient.HostAddress.Returns(HostAddress.GitHubDotComHostAddress);
            bool received1 = false, received2 = false;
            apiClient.GetOrCreateApplicationAuthenticationCode(Args.TwoFactorChallengCallback, null, false, true)
                .Returns(_ =>
                {
                    received1 = true;
                    return Observable.Throw<ApplicationAuthorization>(new TwoFactorChallengeFailedException());
                });

            apiClient.GetOrCreateApplicationAuthenticationCode(Args.TwoFactorChallengCallback,
                Args.String,
                true,
                Args.Boolean)
                .Returns(_ =>
                {
                    received2 = true;
                    return Observable.Throw<ApplicationAuthorization>(new TwoFactorChallengeFailedException());
                });
            apiClient.GetUser().Returns(Observable.Return(CreateUserAndScopes("jiminy")));
            var hostCache = new InMemoryBlobCache();
            var modelService = new ModelService(apiClient, hostCache, Substitute.For<IAvatarProvider>());
            var loginCache = new TestLoginCache();
            var usage = Substitute.For<IUsageTracker>();
            var host = new RepositoryHost(apiClient, modelService, loginCache, Substitute.For<ITwoFactorChallengeHandler>(), usage);

            await host.LogIn("aUsername", "aPassowrd");

            Assert.True(received1);
            Assert.False(received2);
            Assert.False(host.IsLoggedIn);
            var loginInfo = await loginCache.GetLoginAsync(HostAddress.GitHubDotComHostAddress);
            Assert.Equal("", loginInfo.UserName);
            Assert.Equal("", loginInfo.Password);
        }

        [Fact]
        public async Task RetriesUsingOldScopeWhenAuthenticationFailsAndIsEnterprise()
        {
            var enterpriseHostAddress = HostAddress.Create("https://enterprise.example.com/");
            var apiClient = Substitute.For<IApiClient>();
            apiClient.HostAddress.Returns(enterpriseHostAddress);
            apiClient.GetOrCreateApplicationAuthenticationCode(Args.TwoFactorChallengCallback, null, false, true)
                .Returns(Observable.Throw<ApplicationAuthorization>(new ApiException("Bad scopes", (HttpStatusCode)422)));
            apiClient.GetOrCreateApplicationAuthenticationCode(Args.TwoFactorChallengCallback, null, true, false)
                .Returns(Observable.Return(new ApplicationAuthorization("T0k3n")));
            apiClient.GetUser().Returns(Observable.Return(CreateUserAndScopes("jiminy")));
            var hostCache = new InMemoryBlobCache();
            var modelService = new ModelService(apiClient, hostCache, Substitute.For<IAvatarProvider>());
            var loginCache = new TestLoginCache();
            var usage = Substitute.For<IUsageTracker>();
            var host = new RepositoryHost(apiClient, modelService, loginCache, Substitute.For<ITwoFactorChallengeHandler>(), usage);

            await host.LogIn("jiminy", "aPassowrd");

            Assert.True(host.IsLoggedIn);
            var loginInfo = await loginCache.GetLoginAsync(enterpriseHostAddress);
            Assert.Equal("jiminy", loginInfo.UserName);
            Assert.Equal("T0k3n", loginInfo.Password);
        }

        [Fact]
        public async Task SupportsGistIsTrueWhenGistScopeIsPresent()
        {
            var apiClient = Substitute.For<IApiClient>();
            apiClient.HostAddress.Returns(HostAddress.GitHubDotComHostAddress);
            apiClient.GetUser().Returns(Observable.Return(CreateUserAndScopes("baymax", new[] { "gist" })));
            var hostCache = new InMemoryBlobCache();
            var modelService = new ModelService(apiClient, hostCache, Substitute.For<IAvatarProvider>());
            var loginCache = new TestLoginCache();
            var usage = Substitute.For<IUsageTracker>();
            var host = new RepositoryHost(apiClient, modelService, loginCache, Substitute.For<ITwoFactorChallengeHandler>(), usage);

            var result = await host.LogIn("baymax", "aPassword");

            Assert.Equal(AuthenticationResult.Success, result);
            Assert.True(host.SupportsGist);
        }

        [Fact]
        public async Task SupportsGistIsFalseWhenGistScopeIsNotPresent()
        {
            var apiClient = Substitute.For<IApiClient>();
            apiClient.HostAddress.Returns(HostAddress.GitHubDotComHostAddress);
            apiClient.GetUser().Returns(Observable.Return(CreateUserAndScopes("baymax", new[] { "foo" })));
            var hostCache = new InMemoryBlobCache();
            var modelService = new ModelService(apiClient, hostCache, Substitute.For<IAvatarProvider>());
            var loginCache = new TestLoginCache();
            var usage = Substitute.For<IUsageTracker>();
            var host = new RepositoryHost(apiClient, modelService, loginCache, Substitute.For<ITwoFactorChallengeHandler>(), usage);

            var result = await host.LogIn("baymax", "aPassword");

            Assert.Equal(AuthenticationResult.Success, result);
            Assert.False(host.SupportsGist);
        }

        [Fact]
        public async Task SupportsGistIsTrueWhenScopesAreNull()
        {
            // TODO: Check assumptions here. From my conversation with @shana it seems that the first login
            // will be done with basic auth and from then on a token will be used. So if it's the first login,
            // it's from this version and so gists will be supported. However I've been unable to repro this
            // behavior.
            var apiClient = Substitute.For<IApiClient>();
            apiClient.HostAddress.Returns(HostAddress.GitHubDotComHostAddress);
            apiClient.GetUser().Returns(Observable.Return(CreateUserAndScopes("baymax")));
            var hostCache = new InMemoryBlobCache();
            var modelService = new ModelService(apiClient, hostCache, Substitute.For<IAvatarProvider>());
            var loginCache = new TestLoginCache();
            var usage = Substitute.For<IUsageTracker>();
            var host = new RepositoryHost(apiClient, modelService, loginCache, Substitute.For<ITwoFactorChallengeHandler>(), usage);

            var result = await host.LogIn("baymax", "aPassword");

            Assert.Equal(AuthenticationResult.Success, result);
            Assert.True(host.SupportsGist);
        }
    }
}
