namespace Knowz.MCP.Helpers;

/// <summary>
/// Generates the OAuth login page HTML.
/// </summary>
public static class OAuthPageGenerator
{
    // Microsoft 4-square logo SVG
    private const string MicrosoftSvgIcon = @"<svg viewBox=""0 0 21 21"" width=""20"" height=""20"">
        <rect x=""1"" y=""1"" width=""9"" height=""9"" fill=""#f25022""/>
        <rect x=""11"" y=""1"" width=""9"" height=""9"" fill=""#7fba00""/>
        <rect x=""1"" y=""11"" width=""9"" height=""9"" fill=""#00a4ef""/>
        <rect x=""11"" y=""11"" width=""9"" height=""9"" fill=""#ffb900""/>
    </svg>";

    // Google G logo SVG
    private const string GoogleSvgIcon = @"<svg viewBox=""0 0 24 24"" width=""20"" height=""20"">
        <path d=""M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92a5.06 5.06 0 0 1-2.2 3.32v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.1z"" fill=""#4285F4""/>
        <path d=""M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"" fill=""#34A853""/>
        <path d=""M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z"" fill=""#FBBC05""/>
        <path d=""M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z"" fill=""#EA4335""/>
    </svg>";

    public static string GenerateLoginPage(
        string requestId,
        string baseUrl,
        string scope,
        string? error = null,
        List<Services.McpSSOProvider>? ssoProviders = null,
        string? apiKeyHelpUrl = null,
        string? backendMode = null,
        Services.McpAuthPageOptions? authPageOptions = null)
    {
        var isSelfHosted = (backendMode ?? "proxy").Equals("selfhosted", StringComparison.OrdinalIgnoreCase);
        authPageOptions ??= new Services.McpAuthPageOptions
        {
            SsoProviders = ssoProviders ?? new List<Services.McpSSOProvider>()
        };
        if (authPageOptions.SsoProviders.Count == 0 && ssoProviders is { Count: > 0 })
            authPageOptions.SsoProviders = ssoProviders;

        var errorHtml = string.IsNullOrEmpty(error) ? "" : $@"
        <div class=""error"">
            <p>{System.Web.HttpUtility.HtmlEncode(error)}</p>
        </div>";

        var ssoButtonsHtml = "";
        if (authPageOptions.SsoProviders.Count > 0)
        {
            var buttons = string.Join("", authPageOptions.SsoProviders.Select(p =>
            {
                var icon = p.Provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
                    ? MicrosoftSvgIcon : GoogleSvgIcon;
                return $@"
                <a href=""{baseUrl}/oauth/sso/start?provider={Uri.EscapeDataString(p.Provider)}&requestId={Uri.EscapeDataString(requestId)}"" class=""sso-button sso-{p.Provider.ToLowerInvariant()}"">
                    {icon}
                    <span>{System.Web.HttpUtility.HtmlEncode(p.DisplayName)}</span>
                </a>";
            }));

            ssoButtonsHtml = $@"
            <div class=""sso-section"">
                {buttons}
            </div>
            <div class=""divider"">
                <span>{(authPageOptions.ShowPasswordLogin ? "or sign in below" : "or enter your API key")}</span>
            </div>";
        }
        else if (authPageOptions.ForcedSso &&
                 authPageOptions.ConfigurationStatus.Equals("misconfigured", StringComparison.OrdinalIgnoreCase))
        {
            ssoButtonsHtml = @"
            <div class=""notice"">
                <p>Organization sign-in is currently unavailable. Use an API key or contact your administrator.</p>
            </div>";
        }

        // Username/password login form (available in all modes)
        var credentialsFormHtml = authPageOptions.ShowPasswordLogin ? $@"
            <form method=""POST"" action=""{baseUrl}/oauth/authorize"">
                <input type=""hidden"" name=""request_id"" value=""{requestId}"">
                <div class=""form-group"">
                    <label for=""email"">Email or username</label>
                    <input type=""text"" id=""email"" name=""email"" placeholder=""Enter your email or username"" autocomplete=""email"">
                </div>
                <div class=""form-group"">
                    <label for=""password"">Password</label>
                    <input type=""password"" id=""password"" name=""password"" placeholder=""Enter your password"" autocomplete=""current-password"">
                </div>
                <button type=""submit"">Sign In</button>
            </form>
            <div class=""divider"">
                <span>or enter your API key</span>
            </div>" : "";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Authorize - Knowz</title>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 20px;
        }}
        .container {{
            background: white;
            border-radius: 16px;
            box-shadow: 0 20px 60px rgba(0,0,0,0.3);
            padding: 40px;
            max-width: 420px;
            width: 100%;
        }}
        .logo {{
            text-align: center;
            margin-bottom: 24px;
        }}
        .logo h1 {{
            font-size: 28px;
            color: #1a1a2e;
            margin-bottom: 8px;
        }}
        .logo p {{
            color: #666;
            font-size: 14px;
        }}
        .scope-info {{
            background: #f8f9fa;
            border-radius: 8px;
            padding: 16px;
            margin-bottom: 24px;
        }}
        .scope-info h3 {{
            font-size: 14px;
            color: #333;
            margin-bottom: 8px;
        }}
        .scope-info p {{
            font-size: 13px;
            color: #666;
        }}
        .scope-badge {{
            display: inline-block;
            background: #e8f4fd;
            color: #0066cc;
            padding: 4px 12px;
            border-radius: 12px;
            font-size: 12px;
            margin: 4px 4px 4px 0;
        }}
        .form-group {{
            margin-bottom: 20px;
        }}
        label {{
            display: block;
            font-size: 14px;
            font-weight: 500;
            color: #333;
            margin-bottom: 8px;
        }}
        input {{
            width: 100%;
            padding: 12px 16px;
            border: 2px solid #e0e0e0;
            border-radius: 8px;
            font-size: 14px;
            transition: border-color 0.2s;
        }}
        input:focus {{
            outline: none;
            border-color: #667eea;
        }}
        button {{
            width: 100%;
            padding: 14px;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            border: none;
            border-radius: 8px;
            font-size: 16px;
            font-weight: 600;
            cursor: pointer;
            transition: transform 0.2s, box-shadow 0.2s;
        }}
        button:hover {{
            transform: translateY(-2px);
            box-shadow: 0 4px 12px rgba(102, 126, 234, 0.4);
        }}
        .error {{
            background: #fee;
            border: 1px solid #fcc;
            color: #c00;
            padding: 12px;
            border-radius: 8px;
            margin-bottom: 20px;
            font-size: 14px;
        }}
        .notice {{
            background: #fff8e6;
            border: 1px solid #ffe0a3;
            color: #5f4200;
            padding: 12px;
            border-radius: 8px;
            margin-bottom: 20px;
            font-size: 14px;
        }}
        .help {{
            text-align: center;
            margin-top: 20px;
            font-size: 13px;
            color: #666;
        }}
        .help a {{
            color: #667eea;
            text-decoration: none;
        }}
        .sso-section {{
            display: flex;
            flex-direction: column;
            gap: 10px;
            margin-bottom: 0;
        }}
        .sso-button {{
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 10px;
            padding: 12px 16px;
            border: 2px solid #e0e0e0;
            border-radius: 8px;
            text-decoration: none;
            font-size: 14px;
            font-weight: 500;
            color: #333;
            background: white;
            transition: border-color 0.2s, background 0.2s;
            cursor: pointer;
        }}
        .sso-button:hover {{
            border-color: #667eea;
            background: #f8f9ff;
        }}
        .sso-button svg {{
            flex-shrink: 0;
        }}
        .divider {{
            display: flex;
            align-items: center;
            margin: 20px 0;
        }}
        .divider::before,
        .divider::after {{
            content: '';
            flex: 1;
            height: 1px;
            background: #e0e0e0;
        }}
        .divider span {{
            padding: 0 12px;
            font-size: 13px;
            color: #999;
            white-space: nowrap;
        }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""logo"">
            <h1>Knowz</h1>
            <p>Authorize access to your knowledge base</p>
        </div>
        {errorHtml}
        <div class=""scope-info"">
            <h3>This application is requesting access to:</h3>
            <p>
                <span class=""scope-badge"">Search knowledge</span>
                <span class=""scope-badge"">Read content</span>
                <span class=""scope-badge"">Ask questions</span>
            </p>
        </div>
        {ssoButtonsHtml}
        {credentialsFormHtml}
        <form method=""POST"" action=""{baseUrl}/oauth/authorize"">
            <input type=""hidden"" name=""request_id"" value=""{requestId}"">
            <div class=""form-group"">
                <label for=""api_key"">Your Knowz API Key</label>
                <input type=""password"" id=""api_key"" name=""api_key"" placeholder=""{(isSelfHosted ? "ksh_..." : "ukz_... or kz_...")}"" required autocomplete=""off"">
            </div>
            <button type=""submit"">Authorize</button>
        </form>
        {(string.IsNullOrEmpty(apiKeyHelpUrl) ? "" : $@"<p class=""help"">
            Don't have an API key? <a href=""{System.Web.HttpUtility.HtmlAttributeEncode(apiKeyHelpUrl)}"" target=""_blank"">Create one here</a>
        </p>")}
    </div>
</body>
</html>";
    }

    /// <summary>
    /// Generates an error page for SSO failures with a "Back to login" link.
    /// </summary>
    public static string GenerateErrorPage(string baseUrl, string errorMessage)
    {
        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>SSO Error - Knowz</title>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 20px;
        }}
        .container {{
            background: white;
            border-radius: 16px;
            box-shadow: 0 20px 60px rgba(0,0,0,0.3);
            padding: 40px;
            max-width: 420px;
            width: 100%;
            text-align: center;
        }}
        .logo h1 {{
            font-size: 28px;
            color: #1a1a2e;
            margin-bottom: 16px;
        }}
        .error-box {{
            background: #fee;
            border: 1px solid #fcc;
            color: #c00;
            padding: 16px;
            border-radius: 8px;
            margin-bottom: 24px;
            font-size: 14px;
        }}
        .back-link {{
            display: inline-block;
            padding: 12px 24px;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            border-radius: 8px;
            text-decoration: none;
            font-size: 14px;
            font-weight: 600;
            transition: transform 0.2s, box-shadow 0.2s;
        }}
        .back-link:hover {{
            transform: translateY(-2px);
            box-shadow: 0 4px 12px rgba(102, 126, 234, 0.4);
        }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""logo"">
            <h1>Knowz</h1>
        </div>
        <div class=""error-box"">
            <p>{System.Web.HttpUtility.HtmlEncode(errorMessage)}</p>
        </div>
        <a href=""{baseUrl}/oauth/authorize"" class=""back-link"">Back to login</a>
    </div>
</body>
</html>";
    }
}
