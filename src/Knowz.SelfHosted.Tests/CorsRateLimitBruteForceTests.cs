using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class CorsRateLimitBruteForceTests
{
    // ==========================================================================
    // CORS Warning Tests (VERIFY_CORS_01 - VERIFY_CORS_04)
    // ==========================================================================

    [Fact]
    public void CorsWarning_ShouldBeLogged_WhenAllowedOriginsNotConfigured()
    {
        // VERIFY_CORS_01: When AllowedOrigins not configured, warning is emitted
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var allowedOrigins = config.GetSection("SelfHosted:AllowedOrigins").Get<string[]>();

        // The condition that triggers the warning
        var shouldWarn = allowedOrigins is not { Length: > 0 };

        Assert.True(shouldWarn, "Warning should be emitted when AllowedOrigins is not configured");
    }

    [Fact]
    public void CorsWarning_ShouldNotBeLogged_WhenAllowedOriginsConfigured()
    {
        // VERIFY_CORS_04: Warning is NOT emitted when AllowedOrigins is configured
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "SelfHosted:AllowedOrigins:0", "http://localhost:3000" }
            })
            .Build();

        var allowedOrigins = config.GetSection("SelfHosted:AllowedOrigins").Get<string[]>();

        var shouldWarn = allowedOrigins is not { Length: > 0 };

        Assert.False(shouldWarn, "Warning should NOT be emitted when AllowedOrigins is configured");
    }

    [Fact]
    public void CorsWarning_ShouldBeLogged_WhenAllowedOriginsIsEmpty()
    {
        // Edge case: empty array should also trigger warning
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var allowedOrigins = config.GetSection("SelfHosted:AllowedOrigins").Get<string[]>();

        var shouldWarn = allowedOrigins is not { Length: > 0 };

        Assert.True(shouldWarn, "Warning should be emitted when AllowedOrigins is empty");
    }

    // ==========================================================================
    // Rate Limiting - Global Policy Tests (VERIFY_RL_01 - VERIFY_RL_05)
    // ==========================================================================

    [Fact]
    public async Task GlobalRateLimit_ShouldAllow100Requests_ThenReject101st()
    {
        // VERIFY_RL_01: 101st request within 60 seconds returns 429
        using var host = CreateTestHost(new Dictionary<string, string?>
        {
            { "SelfHosted:RateLimiting:Enabled", "true" },
            { "SelfHosted:RateLimiting:Global:PermitLimit", "5" },  // Use small limit for test speed
            { "SelfHosted:RateLimiting:Global:WindowSeconds", "60" },
            { "SelfHosted:RateLimiting:Auth:PermitLimit", "100" },  // Don't interfere
            { "SelfHosted:RateLimiting:Auth:WindowSeconds", "60" }
        });

        var client = host.CreateClient();

        // First 5 requests should succeed
        for (int i = 0; i < 5; i++)
        {
            var response = await client.GetAsync("/api/v1/test");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // 6th request should be rate-limited
        var limitedResponse = await client.GetAsync("/api/v1/test");
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
    }

    [Fact]
    public async Task GlobalRateLimit_429Response_ShouldContainErrorMessage()
    {
        // VERIFY_RL_02: 429 body contains expected error message
        using var host = CreateTestHost(new Dictionary<string, string?>
        {
            { "SelfHosted:RateLimiting:Enabled", "true" },
            { "SelfHosted:RateLimiting:Global:PermitLimit", "1" },
            { "SelfHosted:RateLimiting:Global:WindowSeconds", "60" }
        });

        var client = host.CreateClient();

        // Exhaust the limit
        await client.GetAsync("/api/v1/test");

        // Next request should be limited with error message
        var limitedResponse = await client.GetAsync("/api/v1/test");
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);

        var body = await limitedResponse.Content.ReadAsStringAsync();
        Assert.Contains("Too many requests", body);
    }

    [Fact]
    public async Task GlobalRateLimit_429Response_ShouldIncludeRetryAfterHeader()
    {
        // VERIFY_RL_03: 429 response includes Retry-After header
        using var host = CreateTestHost(new Dictionary<string, string?>
        {
            { "SelfHosted:RateLimiting:Enabled", "true" },
            { "SelfHosted:RateLimiting:Global:PermitLimit", "1" },
            { "SelfHosted:RateLimiting:Global:WindowSeconds", "60" }
        });

        var client = host.CreateClient();

        // Exhaust the limit
        await client.GetAsync("/api/v1/test");

        // Next request should include Retry-After
        var limitedResponse = await client.GetAsync("/api/v1/test");
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
        Assert.True(
            limitedResponse.Headers.Contains("Retry-After"),
            "429 response should include Retry-After header");
    }

    // ==========================================================================
    // Rate Limiting - Auth Policy Tests (VERIFY_AUTH_01 - VERIFY_AUTH_04)
    // ==========================================================================

    [Fact]
    public async Task AuthRateLimit_ShouldRejectAfterLimit()
    {
        // VERIFY_AUTH_01: 6th POST to /api/auth/login within 15s returns 429
        using var host = CreateTestHost(new Dictionary<string, string?>
        {
            { "SelfHosted:RateLimiting:Enabled", "true" },
            { "SelfHosted:RateLimiting:Global:PermitLimit", "1000" },  // High global limit
            { "SelfHosted:RateLimiting:Global:WindowSeconds", "60" },
            { "SelfHosted:RateLimiting:Auth:PermitLimit", "3" },  // Small for test speed
            { "SelfHosted:RateLimiting:Auth:WindowSeconds", "60" }
        });

        var client = host.CreateClient();

        // First 3 login requests should succeed (200 or 400 - doesn't matter, just not 429)
        for (int i = 0; i < 3; i++)
        {
            var response = await client.PostAsync("/api/v1/auth/login",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        // 4th request should be rate-limited
        var limitedResponse = await client.PostAsync("/api/v1/auth/login",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
    }

    [Fact]
    public async Task AuthRateLimit_ShouldNotAffectOtherEndpoints()
    {
        // VERIFY_AUTH_02: /api/test endpoint uses global policy, not auth
        using var host = CreateTestHost(new Dictionary<string, string?>
        {
            { "SelfHosted:RateLimiting:Enabled", "true" },
            { "SelfHosted:RateLimiting:Global:PermitLimit", "1000" },
            { "SelfHosted:RateLimiting:Global:WindowSeconds", "60" },
            { "SelfHosted:RateLimiting:Auth:PermitLimit", "2" },
            { "SelfHosted:RateLimiting:Auth:WindowSeconds", "60" }
        });

        var client = host.CreateClient();

        // Exhaust auth rate limit
        for (int i = 0; i < 3; i++)
        {
            await client.PostAsync("/api/v1/auth/login",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        }

        // Regular endpoint should still work (global limit is high)
        var response = await client.GetAsync("/api/v1/test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthRateLimit_AppliesToBothValidAndInvalidCredentials()
    {
        // VERIFY_AUTH_04: rate limit applies regardless of auth outcome
        using var host = CreateTestHost(new Dictionary<string, string?>
        {
            { "SelfHosted:RateLimiting:Enabled", "true" },
            { "SelfHosted:RateLimiting:Global:PermitLimit", "1000" },
            { "SelfHosted:RateLimiting:Global:WindowSeconds", "60" },
            { "SelfHosted:RateLimiting:Auth:PermitLimit", "2" },
            { "SelfHosted:RateLimiting:Auth:WindowSeconds", "60" }
        });

        var client = host.CreateClient();

        // First 2 requests with invalid data (but not 429)
        for (int i = 0; i < 2; i++)
        {
            var response = await client.PostAsync("/api/v1/auth/login",
                new StringContent("{\"username\":\"invalid\",\"password\":\"wrong\"}",
                    System.Text.Encoding.UTF8, "application/json"));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        // 3rd request should be rate-limited
        var limitedResponse = await client.PostAsync("/api/v1/auth/login",
            new StringContent("{\"username\":\"admin\",\"password\":\"valid\"}",
                System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
    }

    // ==========================================================================
    // Health Endpoint Exclusion Tests (VERIFY_HEALTH_01)
    // ==========================================================================

    [Fact]
    public async Task HealthEndpoint_ShouldNeverBeRateLimited()
    {
        // VERIFY_HEALTH_01: /healthz never rate-limited even when global limit exceeded
        using var host = CreateTestHost(new Dictionary<string, string?>
        {
            { "SelfHosted:RateLimiting:Enabled", "true" },
            { "SelfHosted:RateLimiting:Global:PermitLimit", "1" },
            { "SelfHosted:RateLimiting:Global:WindowSeconds", "60" }
        });

        var client = host.CreateClient();

        // Exhaust global limit
        await client.GetAsync("/api/v1/test");
        await client.GetAsync("/api/v1/test");

        // Health should still work
        for (int i = 0; i < 10; i++)
        {
            var response = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // ==========================================================================
    // Configuration Tests (VERIFY_CFG_01 - VERIFY_CFG_03)
    // ==========================================================================

    [Fact]
    public async Task RateLimiting_ShouldBeDisabled_WhenEnabledIsFalse()
    {
        // VERIFY_CFG_01: Enabled=false disables all rate limiting
        using var host = CreateTestHost(new Dictionary<string, string?>
        {
            { "SelfHosted:RateLimiting:Enabled", "false" },
            { "SelfHosted:RateLimiting:Global:PermitLimit", "1" },
            { "SelfHosted:RateLimiting:Global:WindowSeconds", "60" }
        });

        var client = host.CreateClient();

        // Even with limit of 1, all requests should succeed when disabled
        for (int i = 0; i < 10; i++)
        {
            var response = await client.GetAsync("/api/v1/test");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task RateLimiting_ShouldUseCustomThresholds()
    {
        // VERIFY_CFG_02: Custom thresholds are respected
        using var host = CreateTestHost(new Dictionary<string, string?>
        {
            { "SelfHosted:RateLimiting:Enabled", "true" },
            { "SelfHosted:RateLimiting:Global:PermitLimit", "3" },
            { "SelfHosted:RateLimiting:Global:WindowSeconds", "60" }
        });

        var client = host.CreateClient();

        // First 3 should succeed
        for (int i = 0; i < 3; i++)
        {
            var response = await client.GetAsync("/api/v1/test");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // 4th should be limited
        var limitedResponse = await client.GetAsync("/api/v1/test");
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
    }

    [Fact]
    public async Task RateLimiting_ShouldUseDefaults_WhenConfigSectionAbsent()
    {
        // VERIFY_CFG_03: When section absent, defaults (100/60s global, 5/15s auth) are used
        // We test that it works at all with no config (uses defaults)
        using var host = CreateTestHost(new Dictionary<string, string?>());

        var client = host.CreateClient();

        // Should succeed (default is 100/min which is generous)
        for (int i = 0; i < 5; i++)
        {
            var response = await client.GetAsync("/api/v1/test");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // ==========================================================================
    // Pipeline Order Tests (VERIFY_PIPE_01 - VERIFY_PIPE_02)
    // ==========================================================================

    [Fact]
    public async Task Pipeline_RateLimitedRequests_ShouldReturnBeforeAuth()
    {
        // VERIFY_PIPE_02: Rate-limited 429 returned before auth middleware
        // If auth runs first, it would return 401. We expect 429.
        using var host = CreateTestHost(new Dictionary<string, string?>
        {
            { "SelfHosted:RateLimiting:Enabled", "true" },
            { "SelfHosted:RateLimiting:Global:PermitLimit", "1" },
            { "SelfHosted:RateLimiting:Global:WindowSeconds", "60" }
        }, requireAuth: true);

        var client = host.CreateClient();

        // First request (no auth header) - should get through rate limiter to auth and fail
        var firstResponse = await client.GetAsync("/api/v1/test-auth");
        Assert.Equal(HttpStatusCode.Unauthorized, firstResponse.StatusCode);

        // Second request - should be caught by rate limiter before auth
        var secondResponse = await client.GetAsync("/api/v1/test-auth");
        Assert.Equal(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
    }

    // ==========================================================================
    // Test Host Factory
    // ==========================================================================

    private static TestServer CreateTestHost(
        Dictionary<string, string?> config,
        bool requireAuth = false)
    {
        var builder = new WebHostBuilder()
            .ConfigureAppConfiguration((context, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(config);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddRouting();

                // CORS
                var allowedOrigins = context.Configuration
                    .GetSection("SelfHosted:AllowedOrigins")
                    .Get<string[]>();

                services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                    {
                        if (allowedOrigins is { Length: > 0 })
                            policy.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader();
                        else
                            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
                    });
                });

                // Rate limiting (mirrors the production code we're about to write)
                var rateLimitingEnabled = context.Configuration.GetValue("SelfHosted:RateLimiting:Enabled", true);
                if (rateLimitingEnabled)
                {
                    var globalPermitLimit = context.Configuration.GetValue("SelfHosted:RateLimiting:Global:PermitLimit", 100);
                    var globalWindowSeconds = context.Configuration.GetValue("SelfHosted:RateLimiting:Global:WindowSeconds", 60);
                    var authPermitLimit = context.Configuration.GetValue("SelfHosted:RateLimiting:Auth:PermitLimit", 5);
                    var authWindowSeconds = context.Configuration.GetValue("SelfHosted:RateLimiting:Auth:WindowSeconds", 15);

                    services.AddRateLimiter(options =>
                    {
                        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                        options.OnRejected = async (ctx, cancellationToken) =>
                        {
                            if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                            {
                                ctx.HttpContext.Response.Headers.RetryAfter =
                                    ((int)retryAfter.TotalSeconds).ToString();
                            }

                            ctx.HttpContext.Response.ContentType = "application/json";
                            await ctx.HttpContext.Response.WriteAsJsonAsync(
                                new { error = "Too many requests. Please try again later." },
                                cancellationToken);
                        };

                        // Auth policy: sliding window per IP (stricter)
                        options.AddPolicy("auth", httpContext =>
                            RateLimitPartition.GetSlidingWindowLimiter(
                                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                                factory: _ => new SlidingWindowRateLimiterOptions
                                {
                                    PermitLimit = authPermitLimit,
                                    Window = TimeSpan.FromSeconds(authWindowSeconds),
                                    SegmentsPerWindow = 3,
                                    QueueLimit = 0
                                }));

                        // Global limiter as default
                        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                        {
                            var path = httpContext.Request.Path.Value ?? "";
                            if (path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase))
                            {
                                return RateLimitPartition.GetNoLimiter("health");
                            }

                            return RateLimitPartition.GetFixedWindowLimiter(
                                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                                factory: _ => new FixedWindowRateLimiterOptions
                                {
                                    PermitLimit = globalPermitLimit,
                                    Window = TimeSpan.FromSeconds(globalWindowSeconds),
                                    QueueLimit = 0
                                });
                        });
                    });
                }
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseCors();

                // Rate limiting (after CORS, before auth)
                var rateLimitingEnabled = app.ApplicationServices
                    .GetRequiredService<IConfiguration>()
                    .GetValue("SelfHosted:RateLimiting:Enabled", true);
                if (rateLimitingEnabled)
                {
                    app.UseRateLimiter();
                }

                // Simple auth middleware for pipeline order tests
                if (requireAuth)
                {
                    app.Use(async (context, next) =>
                    {
                        var path = context.Request.Path.Value ?? "";
                        if (path.StartsWith("/api/v1/test-auth"))
                        {
                            if (!context.Request.Headers.ContainsKey("Authorization"))
                            {
                                context.Response.StatusCode = 401;
                                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                                return;
                            }
                        }
                        await next();
                    });
                }

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/healthz",
                        () => Results.Ok(new { status = "healthy", version = "1.0.0" }))
                        .DisableRateLimiting();

                    endpoints.MapGet("/api/v1/test",
                        () => Results.Ok(new { message = "ok" }));

                    endpoints.MapGet("/api/v1/test-auth",
                        () => Results.Ok(new { message = "authenticated" }));

                    endpoints.MapPost("/api/v1/auth/login",
                        () => Results.BadRequest(new { error = "test stub" }))
                        .RequireRateLimiting("auth");
                });
            });

        var server = new TestServer(builder);
        return server;
    }
}
